using System.Text;

namespace Tman;

/// <summary>
/// Per-run output capture, written into <c>.tman/</c> beside the project's <c>.tman.kdl</c>.
///
/// Exists because the console used to be the only place a suite's output went. An agent that ran
/// the tests, then compacted or handed off its context, had no way back to WHICH test failed
/// except re-running the whole suite — the single most repeated waste in a supervised loop.
///
/// Three files per alias:
///   <c>&lt;slug&gt;.log</c>       full combined stdout+stderr of the last run
///   <c>&lt;slug&gt;.fail.log</c>  the failure digest, present only when that run failed
///   <c>&lt;slug&gt;.lock</c>      held while the log is open; stamped with the holder's pid
///
/// Both are cleared at the START of every run, and the digest is deleted when a run passes. That
/// is the load-bearing property: a digest on disk always describes the most recent run of that
/// alias. A stale one reporting an already-fixed failure would be worse than no digest at all,
/// because it reads exactly like a real one.
///
/// Named per alias, not per run id: an agent must be able to name the path without first looking
/// up which run it wants. Two concurrent runs of the same key in the same directory would then
/// share a file, so they are excluded: a named run by the dedup name lock, and an unnamed run —
/// which has no name lock, and which `max-parallel` admits beside another of the same command —
/// by the lock file held here for as long as the log is open. The second opener gets no log and
/// says so on stderr; the alternative was a run that truncated the other's capture mid-run.
///
/// The log itself cannot be that claim. On Unix .NET maps every share mode but
/// <see cref="FileShare.None"/> to a shared flock, so <see cref="FileShare.Read"/> excludes
/// nothing, and <see cref="FileShare.None"/> also turns away every .NET reader for the life of
/// the run — the agent tailing the log is exactly who that would lock out.
/// </summary>
public sealed class RunLog : IDisposable
{
    public const string DirName = ".tman";
    public const string LogSuffix = ".log";
    public const string DigestSuffix = ".fail.log";
    /// <summary>Held exclusively for the life of the log; never unlinked, see <see cref="Store"/>.</summary>
    public const string LockSuffix = ".lock";

    /// <summary>Ceiling on the full log. Past it output goes to a tail ring, flushed at close.</summary>
    public const long MaxBytes = 64L * 1024 * 1024;
    const int TailChars = 512 * 1024;

    readonly object _gate = new();
    readonly StreamWriter _writer;
    readonly FileStream _hold;
    readonly char[] _tail = new char[TailChars];
    int _tailStart, _tailLen;
    long _written;
    bool _capped;
    bool _closed;

    public string LogPath { get; }
    public string DigestPath { get; }

    RunLog(StreamWriter writer, FileStream hold, string logPath, string digestPath)
    {
        _writer = writer;
        _hold = hold;
        LogPath = logPath;
        DigestPath = digestPath;
    }

    /// <summary>
    /// Opens the log pair for a run, truncating both. Returns null when the directory cannot be
    /// written — a read-only checkout, a permission-denied mount — or when another run of the same
    /// key holds the log, which is reported on stderr. Capturing output is a convenience; failing a
    /// run because the convenience is unavailable is not acceptable, and neither is two runs
    /// writing one file.
    /// </summary>
    public static RunLog? Open(string scopeDir, string? name, string? alias, string command)
    {
        try
        {
            var dir = Path.Combine(scopeDir, DirName);
            Directory.CreateDirectory(dir);
            var slug = Slug(alias ?? name ?? Canon.CommandLabel(command));
            var logPath = Path.Combine(dir, slug + LogSuffix);
            var digestPath = Path.Combine(dir, slug + DigestSuffix);

            var hold = Store.TryClaimLock(Path.Combine(dir, slug + LockSuffix));
            if (hold is null)
            {
                Console.Error.WriteLine(
                    $"tman: {Path.Combine(DirName, slug + LogSuffix)} is held by a concurrent run; this run's output is not captured");
                return null;
            }

            try
            {
                // Deleted, not left to be overwritten: between here and the run finishing, the
                // honest state is "this run has produced no verdict yet", not the previous run's.
                try { File.Delete(digestPath); } catch (IOException) { }

                var writer = new StreamWriter(
                    new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite));
                return new RunLog(writer, hold, logPath, digestPath);
            }
            catch
            {
                hold.Dispose();
                throw;
            }
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (ArgumentException) { return null; }
    }

    /// <summary>Filesystem-safe, readable, and stable for the same alias across runs.</summary>
    static string Slug(string label)
    {
        var s = new string(label
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')
            .Take(48)
            .ToArray())
            .Trim('-');
        return s.Length == 0 ? "run" : s;
    }

    /// <summary>Tees one chunk of child output. Called from both pumps, hence the lock.</summary>
    public void Write(ReadOnlySpan<char> chunk)
    {
        lock (_gate)
        {
            if (_closed) return;
            try
            {
                if (!_capped)
                {
                    _writer.Write(chunk);
                    _written += chunk.Length;
                    if (_written >= MaxBytes) _capped = true;
                    return;
                }
                AppendTail(chunk);
            }
            catch (IOException) { _closed = true; }
        }
    }

    // Once capped, the most recent output is the part worth keeping: every runner prints its
    // failure summary last, so a log truncated at the head still answers the question.
    void AppendTail(ReadOnlySpan<char> chunk)
    {
        if (chunk.Length >= TailChars)
        {
            chunk[^TailChars..].CopyTo(_tail);
            _tailStart = 0;
            _tailLen = TailChars;
            return;
        }
        foreach (var c in chunk)
        {
            var end = (_tailStart + _tailLen) % TailChars;
            _tail[end] = c;
            if (_tailLen < TailChars) _tailLen++;
            else _tailStart = (_tailStart + 1) % TailChars;
        }
    }

    /// <summary>
    /// Closes the log and, when the run did not pass, writes the digest beside it. Announces the
    /// digest path on stderr: an agent that only reads the tail of a run's console output still
    /// learns where the detail is.
    /// </summary>
    public void Complete(RunRecord record)
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            try
            {
                if (_capped)
                {
                    _writer.Write($"{Environment.NewLine}── tman: log capped at {MaxBytes / (1024 * 1024)}MB; tail follows ──{Environment.NewLine}");
                    for (var i = 0; i < _tailLen; i++) _writer.Write(_tail[(_tailStart + i) % TailChars]);
                }
                _writer.Flush();
            }
            catch (IOException) { }
            try { _writer.Dispose(); } catch (IOException) { }
            _hold.Dispose();
        }

        var passed = record.State == RunState.Exited && record.ExitCode == 0;
        if (passed) return;

        try
        {
            File.WriteAllLines(DigestPath, Header(record).Concat(Failures.Digest(LogPath)));
            Console.Error.WriteLine($"tman: failures logged to {DigestPath}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    IEnumerable<string> Header(RunRecord record)
    {
        var outcome = record.State == RunState.Exited
            ? $"exit {record.ExitCode}"
            : $"{record.State.ToString().ToLowerInvariant()} — {record.KillReason ?? "no reason recorded"}";
        var duration = record.HeartbeatUtc - record.StartedUtc;
        yield return $"tman failure digest — {record.Name ?? Canon.CommandLabel(record.Command)}";
        yield return $"  outcome:  {outcome}";
        yield return $"  command:  {record.Command} {string.Join(' ', record.Args)}";
        yield return $"  cwd:      {record.Cwd}";
        yield return $"  started:  {record.StartedUtc:u}  ({duration.TotalSeconds:F1}s)";
        yield return $"  full log: {LogPath}";
        yield return "";
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            try { _writer.Dispose(); } catch (IOException) { }
            _hold.Dispose();
        }
    }
}
