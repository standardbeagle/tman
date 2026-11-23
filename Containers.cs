using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Tman;

/// <summary>A container a test fixture labelled with the run that started it (<see cref="Containers.Label"/>).</summary>
/// <param name="RunId">The label's value; empty when the label was set to nothing.</param>
/// <param name="CreatedUtc">When docker created it; null when docker did not say in a form tman reads.</param>
public sealed record LabelledContainer(string Id, string Name, string RunId, DateTime? CreatedUtc = null);

/// <summary>What tman asks of docker: one listing and one removal.</summary>
public interface IContainerEngine
{
    /// <summary>
    /// Every container carrying the <see cref="Containers.Label"/> label, running or stopped. Null
    /// when docker could not answer — the daemon is down, or did not answer in time.
    /// </summary>
    IReadOnlyList<LabelledContainer>? ListLabelled();

    /// <summary>Removes the containers and their anonymous volumes (`docker rm -f -v`); returns the ids that went.</summary>
    IReadOnlySet<string> Remove(IReadOnlyList<LabelledContainer> containers);
}

/// <summary>
/// The docker containers a run's fixtures started. dockerd owns them, so they sit outside the
/// process tree tman supervises and outlive the run that started them: a test Postgres left up for
/// the afternoon, holding its port. A fixture labels its container with the run that started it —
/// `docker run --label tman.run=$TMAN_RUN_ID` — and tman removes it once that run is over.
///
/// THE OWNERSHIP RULE. A container labelled <c>tman.run=X</c> is removed when X is a run this
/// store can account for and nothing above it is still running:
///   - X has a record here, and X and every run above it — its parent, the parent's parent, through
///     <see cref="RunRecord.ParentId"/> — have ended. A nested tman hands its children its own id,
///     so a fixture started inside a nested run is labelled with the nested run; it belongs to the
///     outermost run still going, and stays up until that one ends.
///   - Or X has no record here, X carries this store's tag (<see cref="Store.Issued"/>), and the
///     container is older than <see cref="OrphanAge"/>: a run whose record was pruned before docker
///     could be reached, or a store that was wiped.
/// Everything else is left alone: an empty label, an id from another store (another user's, a
/// Windows tman beside WSL's, a test's throwaway TMAN_HOME — all on the same daemon), an id from
/// before ids carried a tag, and any run whose record exists but cannot be read.
/// </summary>
public static class Containers
{
    public const string Label = "tman.run";

    /// <summary>
    /// Names the docker CLI to use instead of the `docker` on PATH (podman, a wrapper); `off` or
    /// empty turns container cleanup off.
    /// </summary>
    public const string EngineEnvVar = "TMAN_DOCKER";

    /// <summary>How often the sweep asks docker for labelled containers when no ended run is waiting on it.</summary>
    public static readonly TimeSpan OrphanInterval = TimeSpan.FromHours(1);

    /// <summary>How long docker is left alone after it failed to answer or refused a removal.</summary>
    public static readonly TimeSpan Backoff = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The youngest a container with no record behind it may be and still be removed. A run's record
    /// exists before its child starts, so a young container with no record is one whose record was
    /// missed — a read that lost a race with a writer — not one whose record was pruned.
    /// </summary>
    public static readonly TimeSpan OrphanAge = TimeSpan.FromHours(1);

    /// <summary>The engine a pass talks to; null when there is none to ask. A test seam, like <see cref="Admission.MeasureHost"/>.</summary>
    internal static Func<IContainerEngine?> FindEngine = DockerCli.Find;

    internal static string CheckedStampPath => Store.MarkerPath("containers.checked");
    internal static string BackoffStampPath => Store.MarkerPath("containers.backoff");

    // ---- ownership -------------------------------------------------------------------------

    /// <summary>
    /// True while <paramref name="r"/> or any run above it is still going. A parent whose record is
    /// on disk but could not be read counts as going: whether it ended is not known, and removing a
    /// live fixture costs a run where keeping a dead one costs a few minutes.
    /// </summary>
    internal static bool ChainLive(RunRecord r, IReadOnlyDictionary<string, RunRecord> byId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var run = r; seen.Add(run.Id);)
        {
            if (!run.IsFinished) return true;
            if (run.ParentId is not { Length: > 0 } parent) return false;
            if (!byId.TryGetValue(parent, out var above)) return Store.RecordExists(parent);
            run = above;
        }
        return false;
    }

    /// <summary>A run that started a child; a waiter that never ran, and a hold, cannot have labelled anything.</summary>
    static bool HadAChild(RunRecord r) => r.Pid != 0 && !r.Hold;

    /// <summary>An ended run whose containers have not yet been looked for.</summary>
    static bool Unswept(RunRecord r) => r.IsFinished && !r.ContainersSwept && HadAChild(r);

    /// <summary>
    /// True when <paramref name="r"/> has ended, nothing above it is still going, and its containers
    /// have not yet been looked for — the one thing that sends the sweep to docker before the
    /// <see cref="OrphanInterval"/> is up.
    /// </summary>
    public static bool Due(RunRecord r, IReadOnlyList<RunRecord> records) => Due(r, ById(records));

    static bool Due(RunRecord r, IReadOnlyDictionary<string, RunRecord> byId) => Unswept(r) && !ChainLive(r, byId);

    /// <summary>
    /// True for an ended nested run that may still have containers up for a run above it. Its
    /// record is kept past the retention window: pruned, its containers would read as an orphan's
    /// and go while the outer run still uses them.
    /// </summary>
    internal static bool HeldForAnOuterRun(RunRecord r, IReadOnlyDictionary<string, RunRecord> byId) =>
        Unswept(r) && ChainLive(r, byId);

    /// <summary>The containers in <paramref name="found"/> the ownership rule (see the class summary) removes.</summary>
    public static List<LabelledContainer> Doomed(
        IEnumerable<LabelledContainer> found, IReadOnlyList<RunRecord> records, DateTime nowUtc) =>
        Doomed(found, ById(records), nowUtc);

    static List<LabelledContainer> Doomed(
        IEnumerable<LabelledContainer> found, IReadOnlyDictionary<string, RunRecord> byId, DateTime nowUtc)
    {
        var doomed = new List<LabelledContainer>();
        foreach (var c in found)
        {
            if (string.IsNullOrWhiteSpace(c.RunId)) continue;
            if (byId.TryGetValue(c.RunId, out var run))
            {
                if (!ChainLive(run, byId)) doomed.Add(c);
            }
            else if (Store.Issued(c.RunId) && !Store.RecordExists(c.RunId)
                     && c.CreatedUtc is { } created && nowUtc - created >= OrphanAge)
            {
                doomed.Add(c);
            }
        }
        return doomed;
    }

    static Dictionary<string, RunRecord> ById(IEnumerable<RunRecord> records)
    {
        var byId = new Dictionary<string, RunRecord>(StringComparer.Ordinal);
        foreach (var r in records) byId[r.Id] = r;
        return byId;
    }

    // ---- passes ----------------------------------------------------------------------------

    /// <summary>
    /// The housekeeping sweep's part: asks docker only when an ended run is waiting on it, or the
    /// <see cref="OrphanInterval"/> since the last look is up, and never inside the
    /// <see cref="Backoff"/> — so `tman list` costs a docker round-trip once an hour at most, not
    /// every time. <paramref name="now"/> (`tman clean`) asks whatever the gates say.
    /// </summary>
    /// <returns>The containers removed.</returns>
    public static IReadOnlyList<LabelledContainer> Sweep(IReadOnlyList<RunRecord> records, bool now, bool quiet)
    {
        var byId = ById(records);
        if (!now)
        {
            if (!records.Any(r => Due(r, byId)) && !Stale(CheckedStampPath, OrphanInterval)) return [];
            if (!Stale(BackoffStampPath, Backoff)) return [];
        }
        return Pass(records, byId, quiet)?.Removed ?? [];
    }

    /// <summary>
    /// A run's own part, once its outcome is recorded: removes the containers labelled with it, and
    /// says so on stderr. A nested run's are left up for the run above it, which it also says.
    /// Bounded by docker's timeouts and skipped inside the <see cref="Backoff"/>; nothing here can
    /// change the run's outcome or its exit.
    /// </summary>
    public static void AfterRun(RunRecord record)
    {
        try
        {
            if (!HadAChild(record)) return;
            var records = Store.LoadAll();
            var own = records.FirstOrDefault(r => r.Id == record.Id);
            // another tman's sweep got here first, and has said what it removed
            if (own is null || !Unswept(own)) return;
            if (!Stale(BackoffStampPath, Backoff)) return;
            var byId = ById(records);
            var pass = Pass(records, byId, quiet: false);
            if (pass is null) return;
            var kept = pass.Remaining.Where(c => c.RunId == own.Id).ToList();
            if (kept.Count > 0 && ChainLive(own, byId) && OutermostLive(own, byId) is { } outer)
                Console.Error.WriteLine(
                    $"tman: {Count(kept.Count)} labelled {Label}={own.Id} {(kept.Count == 1 ? "stays" : "stay")} up until outer run {outer.Id} ends: " +
                    string.Join(", ", kept.Select(c => c.Name)));
        }
        catch (KnownError) { }
    }

    static RunRecord? OutermostLive(RunRecord r, IReadOnlyDictionary<string, RunRecord> byId)
    {
        RunRecord? outermost = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var run = r; seen.Add(run.Id);)
        {
            if (!run.IsFinished) outermost = run;
            if (run.ParentId is not { Length: > 0 } parent || !byId.TryGetValue(parent, out var above)) break;
            run = above;
        }
        return outermost;
    }

    sealed record PassResult(IReadOnlyList<LabelledContainer> Removed, IReadOnlyList<LabelledContainer> Remaining);

    /// <summary>
    /// One look at docker: list the labelled containers, remove the doomed, and mark each ended run
    /// with none left up as swept. Null when docker could not answer, which starts the backoff.
    /// </summary>
    static PassResult? Pass(IReadOnlyList<RunRecord> records, IReadOnlyDictionary<string, RunRecord> byId, bool quiet)
    {
        var engine = FindEngine();
        if (engine is null)
        {
            // no docker to ask: nothing can be removed, now or on the next command, so the records
            // stop asking. Should docker appear later, the hourly look still finds their containers
            // by their records.
            foreach (var r in records.Where(Unswept)) MarkSwept(r, []);
            Touch(CheckedStampPath);
            return new PassResult([], []);
        }

        var found = engine.ListLabelled();
        if (found is null)
        {
            Touch(BackoffStampPath);
            return null;
        }
        var doomed = Doomed(found, byId, DateTime.UtcNow);
        var gone = doomed.Count == 0 ? new HashSet<string>() : engine.Remove(doomed);
        var removed = doomed.Where(c => gone.Contains(c.Id)).ToList();
        // a container docker would not remove is retried after the backoff, not on every command
        if (removed.Count < doomed.Count) Touch(BackoffStampPath);
        var remaining = found.Where(c => !gone.Contains(c.Id)).ToList();

        foreach (var r in records.Where(Unswept))
        {
            if (remaining.Any(c => c.RunId == r.Id)) continue;
            MarkSwept(r, removed.Where(c => c.RunId == r.Id).Select(c => c.Name));
        }
        Touch(CheckedStampPath);

        if (!quiet)
            foreach (var run in removed.GroupBy(c => c.RunId))
                Console.Error.WriteLine(
                    $"tman: removed {Count(run.Count())} labelled {Label}={run.Key}: " +
                    string.Join(", ", run.Select(c => c.Name)));
        return new PassResult(removed, remaining);
    }

    static string Count(int n) => n == 1 ? "1 container" : $"{n} containers";

    static void MarkSwept(RunRecord r, IEnumerable<string> removed)
    {
        r.ContainersSwept = true;
        var names = (r.RemovedContainers ?? []).Concat(removed).Distinct().ToArray();
        r.RemovedContainers = names.Length == 0 ? null : names;
        Store.Save(r);
    }

    /// <summary>
    /// True when the stamp at <paramref name="path"/> is missing or older than <paramref name="window"/>.
    /// A stamp from the future — the wall clock stepped back — is stale too, so a clock step can
    /// neither silence docker nor hold the backoff for longer than it says.
    /// </summary>
    static bool Stale(string path, TimeSpan window)
    {
        try
        {
            if (!File.Exists(path)) return true;
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
            return age < TimeSpan.Zero || age >= window;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    }

    static void Touch(string path)
    {
        try
        {
            Store.EnsureDirs();
            File.WriteAllText(path, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or KnownError) { }
    }
}

/// <summary>
/// The docker CLI, run as a child process: whatever `docker` resolves to on PATH — the Linux CLI,
/// Docker Desktop's, or a wrapper into WSL on Windows — or the command <see cref="Containers.EngineEnvVar"/>
/// names. Every call is bounded, and every way it can fail reads as "docker did not answer".
/// </summary>
sealed class DockerCli(string path) : IContainerEngine
{
    static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(5);
    static readonly TimeSpan RemoveTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The CLI to use, or null when cleanup is off or there is none to run.</summary>
    internal static IContainerEngine? Find()
    {
        var named = Environment.GetEnvironmentVariable(Containers.EngineEnvVar);
        if (named is not null && (named.Trim().Length == 0 || named.Trim().Equals("off", StringComparison.OrdinalIgnoreCase)))
            return null;
        var resolved = Resolve(named?.Trim() ?? "docker", Environment.GetEnvironmentVariable("PATH"), OperatingSystem.IsWindows());
        return resolved is null ? null : new DockerCli(resolved);
    }

    /// <summary>
    /// Where <paramref name="command"/> is on <paramref name="pathVar"/>, or null. On Windows only a
    /// file Windows can start counts — .exe, .cmd, .bat, .com — so a bash wrapper named plain
    /// `docker` that Git Bash would run is passed over rather than failing to start. Elsewhere only
    /// an executable file counts.
    /// </summary>
    internal static string? Resolve(string command, string? pathVar, bool windows)
    {
        if (command.Length == 0) return null;
        string[] extensions = [".exe", ".cmd", ".bat", ".com"];
        IEnumerable<string> Candidates(string stem)
        {
            if (!windows) { yield return stem; yield break; }
            if (extensions.Contains(Path.GetExtension(stem), StringComparer.OrdinalIgnoreCase)) yield return stem;
            else foreach (var ext in extensions) yield return stem + ext;
        }

        IEnumerable<string> stems;
        if (command.Contains('/') || command.Contains('\\')) stems = [command];
        else if (string.IsNullOrEmpty(pathVar)) return null;
        else stems = pathVar.Split(windows ? ';' : ':', StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => SafeCombine(dir, command)).OfType<string>();

        foreach (var candidate in stems.SelectMany(Candidates))
            if (IsRunnable(candidate, windows)) return candidate;
        return null;

        static string? SafeCombine(string dir, string command)
        {
            try { return Path.Combine(dir.Trim('"'), command); }
            catch (ArgumentException) { return null; }
        }
    }

    static bool IsRunnable(string file, bool windows)
    {
        try
        {
            if (!File.Exists(file)) return false;
            if (windows || OperatingSystem.IsWindows()) return true;
            const UnixFileMode anyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return (File.GetUnixFileMode(file) & anyExecute) != 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    // Containers come and go while tman looks. The daemon is the host's, shared by every agent's
    // fixtures, and a fixture removes its own as its run ends: Testcontainers' dispose and its Ryuk
    // reaper, a `--rm` container exiting, a compose `down`. One that went between two docker calls
    // is not docker failing and not docker refusing; read as either, it started the backoff, which
    // skips every run's cleanup in the store for its whole window (build3, 2026-11-23). So a call
    // that failed over containers is checked against a fresh listing before it counts as a failure.

    public IReadOnlyList<LabelledContainer>? ListLabelled()
    {
        var listed = Listed();
        if (listed is null) return null;
        if (listed.Count == 0) return [];
        // a container removed between the two calls fails inspect for that one, and the rest still print
        var inspected = Run(["inspect", "--type", "container", .. listed.Keys], ListTimeout);
        if (inspected is null) return null;
        var parsed = ParseInspect(inspected.Out);
        if (parsed is null || inspected.Exit == 0 || parsed.Count > 0) return parsed;
        // a failure that printed none of them: every one went between the calls, or docker is failing
        return AllGone(listed.Keys) ? parsed : null;
    }

    public IReadOnlySet<string> Remove(IReadOnlyList<LabelledContainer> containers)
    {
        var gone = new HashSet<string>(StringComparer.Ordinal);
        if (containers.Count == 0) return gone;
        // docker prints each container it removed, as it was named; one already gone, or already
        // being removed by something else, is an error for that one alone
        var removed = Run(["rm", "-f", "-v", .. containers.Select(c => c.Id)], RemoveTimeout);
        if (removed is null) return gone;
        var printed = removed.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        foreach (var c in containers)
            if (printed.Contains(c.Id)) gone.Add(c.Id);
        if (gone.Count == containers.Count) return gone;
        // only a container docker still holds is one it would not remove
        var still = Listed();
        if (still is null) return gone;
        foreach (var c in containers)
            if (Going(still, c.Id)) gone.Add(c.Id);
        return gone;
    }

    /// <summary>
    /// Every labelled container docker holds, by full id, with its state (`running`, `exited`,
    /// `removing`, …); null when docker could not answer.
    /// </summary>
    Dictionary<string, string>? Listed()
    {
        var ps = Run(["ps", "-a", "--no-trunc", "--filter", "label=" + Containers.Label, "--format", "{{.ID}} {{.State}}"], ListTimeout);
        if (ps is not { Exit: 0 }) return null;
        var listed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in ps.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            listed[fields[0]] = fields.Length > 1 ? fields[1] : "";
        }
        return listed;
    }

    /// <summary>True when docker, asked again, holds none of <paramref name="ids"/> but to remove it.</summary>
    bool AllGone(IEnumerable<string> ids) => Listed() is { } still && ids.All(id => Going(still, id));

    /// <summary>Gone from docker, or on its way: docker is already removing it.</summary>
    static bool Going(IReadOnlyDictionary<string, string> listed, string id) =>
        !listed.TryGetValue(id, out var state) || state.Equals("removing", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// `docker inspect` output as labelled containers; null when it is not the array docker prints.
    /// </summary>
    internal static List<LabelledContainer>? ParseInspect(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var list = new List<LabelledContainer>();
            foreach (var c in doc.RootElement.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.Object || !c.TryGetProperty("Id", out var id) || id.GetString() is not { Length: > 0 } cid)
                    continue;
                var name = c.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString()!.TrimStart('/')
                    : cid[..Math.Min(12, cid.Length)];
                var runId = c.TryGetProperty("Config", out var config) && config.ValueKind == JsonValueKind.Object
                            && config.TryGetProperty("Labels", out var labels) && labels.ValueKind == JsonValueKind.Object
                            && labels.TryGetProperty(Containers.Label, out var label) && label.ValueKind == JsonValueKind.String
                    ? label.GetString()!.Trim()
                    : "";
                var created = c.TryGetProperty("Created", out var cr) && cr.ValueKind == JsonValueKind.String
                    ? ParseCreated(cr.GetString()!)
                    : null;
                list.Add(new LabelledContainer(cid, name, runId, created));
            }
            return list;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// docker's RFC 3339 time, which carries nanoseconds — two digits more than .NET reads — and is
    /// cut to the hundred-nanosecond tick .NET keeps.
    /// </summary>
    internal static DateTime? ParseCreated(string text)
    {
        var dot = text.IndexOf('.');
        if (dot > 0)
        {
            var end = dot + 1;
            while (end < text.Length && char.IsAsciiDigit(text[end])) end++;
            if (end - dot - 1 > 7) text = text[..(dot + 8)] + text[end..];
        }
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at.UtcDateTime
            : null;
    }

    sealed record Output(int Exit, string Out);

    /// <summary>
    /// Runs the CLI with <paramref name="args"/>, giving up — and killing what it started — after
    /// <paramref name="timeout"/>. Null when it could not be started or did not finish in time.
    /// </summary>
    Output? Run(string[] args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process? proc;
        try { proc = Process.Start(psi); }
        catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException or PlatformNotSupportedException)
        {
            return null;
        }
        if (proc is null) return null;
        using (proc)
        {
            try { proc.StandardInput.Close(); }
            catch (IOException) { }
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(timeout))
            {
                try { proc.Kill(entireProcessTree: true); }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException) { }
                return null;
            }
            // the CLI has exited; what it wrote is in the pipe, unless something it started still holds it
            if (!stdout.Wait(TimeSpan.FromSeconds(2))) return null;
            _ = stderr;
            return new Output(proc.ExitCode, stdout.Result);
        }
    }
}
