using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tman;

public enum RunState { Running, Exited, Killed, Reaped, TimedOut, Stalled, Culled }

public sealed class RunRecord
{
    /// <summary>
    /// Bumped whenever the on-disk shape changes incompatibly. Records from a newer tman are
    /// left alone rather than half-read; records from an older one are dropped by the reaper.
    /// </summary>
    public const int CurrentSchema = 2;

    public int Schema { get; set; } = CurrentSchema;
    public required string Id { get; set; }
    public string? Name { get; set; }
    public int Pid { get; set; }
    public int RunnerPid { get; set; }
    public DateTime RunnerStartUtc { get; set; }
    /// <summary>Absolute path to the executable, resolved through PATH so one binary reads one way.</summary>
    public required string Command { get; set; }
    public required string[] Args { get; set; }
    public string? Cwd { get; set; }
    /// <summary>Dedup/slot bucket, see <see cref="RunKey"/>.</summary>
    public string? Group { get; set; }
    /// <summary>Id of the tman run that launched this one, when a supervised process re-enters tman.</summary>
    public string? ParentId { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime ChildStartUtc { get; set; }
    public DateTime HeartbeatUtc { get; set; }
    public DateTime LastOutputUtc { get; set; }
    public RunState State { get; set; } = RunState.Running;
    public int? ExitCode { get; set; }
    /// <summary>The effective caps this run was started under — the same shape the config parses into.</summary>
    public Caps Caps { get; set; } = new();
    public long PeakMemMb { get; set; }
    public string? KillReason { get; set; }

    public bool IsNested => ParentId is not null;

    /// <summary>True once the run reached a terminal state and can be pruned.</summary>
    public bool IsFinished => State != RunState.Running;

    /// <summary>Minimum id characters accepted as a prefix, short enough to type and long enough to be unique.</summary>
    public const int MinIdPrefix = 4;

    public bool Matches(string nameOrId) =>
        string.Equals(Name, nameOrId, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Id, nameOrId, StringComparison.OrdinalIgnoreCase) ||
        (nameOrId.Length >= MinIdPrefix && Id.StartsWith(nameOrId, StringComparison.OrdinalIgnoreCase));
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(RunRecord))]
internal partial class RunRecordJsonContext : JsonSerializerContext;

/// <summary>
/// Reporting view of a record. The default encoder escapes quotes and angle brackets to \uXXXX,
/// which is safe for HTML but turns a shell command line into something nobody can read; this is
/// console output, so it uses the relaxed encoder.
/// </summary>
internal static class RunRecordReport
{
    static readonly RunRecordJsonContext Context = new(new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    public static string ToJson(RunRecord r) => JsonSerializer.Serialize(r, Context.RunRecord);
}

public static class Store
{
    public static string Root =>
        Environment.GetEnvironmentVariable("TMAN_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tman");

    static string RunsDir => Path.Combine(Root, "runs");

    public static void EnsureDirs() => Directory.CreateDirectory(RunsDir);

    static string PathFor(string id) => Path.Combine(RunsDir, id + ".json");

    public static string LockPathFor(string runKey) =>
        Path.Combine(RunsDir, RunKey.LockStem(runKey) + ".lock");

    /// <summary>One of a bucket's parallel slots. Named `.lock` so the stale sweep covers it too.</summary>
    public static string SlotPathFor(string runKey, int slot) =>
        Path.Combine(RunsDir, $"{RunKey.LockStem(runKey)}-slot{slot}.lock");

    public static void Save(RunRecord r)
    {
        EnsureDirs();
        var tmp = PathFor(r.Id) + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(r, RunRecordJsonContext.Default.RunRecord));
        File.Move(tmp, PathFor(r.Id), overwrite: true);
    }

    public static RunRecord? Load(string id) => ReadFile(PathFor(id));

    public static List<RunRecord> LoadAll()
    {
        EnsureDirs();
        var list = new List<RunRecord>();
        foreach (var f in Directory.EnumerateFiles(RunsDir, "*.json"))
        {
            var r = ReadFile(f);
            if (r is not null) list.Add(r);
        }
        return list;
    }

    /// <summary>
    /// Records written by a newer tman are skipped rather than half-read: an unknown shape would
    /// deserialize with defaults, and a record whose Pid defaulted to 0 is a reaping hazard.
    /// </summary>
    static RunRecord? ReadFile(string path)
    {
        try
        {
            var r = JsonSerializer.Deserialize(File.ReadAllText(path), RunRecordJsonContext.Default.RunRecord);
            return r?.Schema == RunRecord.CurrentSchema ? r : null;
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    public static void Remove(string id) => Delete(PathFor(id));

    public static void BreakLock(string lockPath) => Delete(lockPath);

    static void Delete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Deletes finished records past their retention, plus any file the current tman cannot read —
    /// unparsable JSON and off-schema records would otherwise accumulate forever, since nothing
    /// else ever revisits them.
    /// </summary>
    public static int Prune(TimeSpan retain)
    {
        EnsureDirs();
        var cutoff = DateTime.UtcNow - retain;
        var removed = 0;
        foreach (var f in Directory.EnumerateFiles(RunsDir, "*.json"))
        {
            var r = ReadFile(f);
            if (r is null)
            {
                // unreadable: only reap once it is old enough to not be a record being written now
                if (File.GetLastWriteTimeUtc(f) < cutoff) { Delete(f); removed++; }
                continue;
            }
            if (r.IsFinished && r.HeartbeatUtc < cutoff) { Delete(f); removed++; }
        }
        foreach (var f in Directory.EnumerateFiles(RunsDir, "*.tmp"))
            if (File.GetLastWriteTimeUtc(f) < cutoff) { Delete(f); removed++; }
        return removed;
    }

    /// <summary>
    /// Stamps the owning runner into a freshly created lock. A lock is taken before the run record
    /// exists, so "no matching record" cannot be used to judge staleness without a race — the owner
    /// pid can, and it is correct from the instant the lock is created.
    /// </summary>
    public static void StampLockOwner(FileStream lockFile)
    {
        var startUtc = ProcUtil.StartTimeUtc(Environment.ProcessId) ?? DateTime.UtcNow;
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            $"{Environment.ProcessId} {startUtc:O}\n");
        lockFile.Write(bytes);
        lockFile.Flush(flushToDisk: false);
    }

    /// <summary>
    /// Claims one of a bucket's <paramref name="maxParallel"/> slots, or returns null when every
    /// slot is taken. Creating the slot file is the claim, so two runners racing for the last slot
    /// cannot both win — counting live records could not offer that, because the count is read
    /// before any of the racers has a record to be counted.
    /// </summary>
    public static FileStream? TryAcquireSlot(string runKey, int maxParallel)
    {
        EnsureDirs();
        for (var slot = 0; slot < maxParallel; slot++)
        {
            var path = SlotPathFor(runKey, slot);
            var claimed = TryClaimLock(path);
            // a runner killed while holding a slot would otherwise take it out of circulation forever
            if (claimed is null && IsLockStale(path))
            {
                BreakLock(path);
                claimed = TryClaimLock(path);
            }
            if (claimed is not null) return claimed;
        }
        return null;
    }

    static FileStream? TryClaimLock(string path)
    {
        try
        {
            var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            StampLockOwner(file);
            return file;
        }
        catch (IOException) { return null; }
    }

    /// <summary>Gives up a held lock: drop the handle first, then the file it stands for.</summary>
    public static void ReleaseLock(FileStream lockFile)
    {
        var path = lockFile.Name;
        lockFile.Dispose();
        Delete(path);
    }

    /// <summary>True when a lock file's owning runner is gone, so the lock can be broken.</summary>
    public static bool IsLockStale(string lockPath)
    {
        string text;
        try { text = File.ReadAllText(lockPath); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }

        var parts = text.Split(' ', 2, StringSplitOptions.TrimEntries);
        // an unstamped lock predates owner tracking, or was caught mid-write; leave it to the holder check
        if (parts.Length < 2 || !int.TryParse(parts[0], out var pid)) return false;
        if (!DateTime.TryParse(parts[1], null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var startUtc)) return false;
        return !ProcUtil.IsAlive(pid) || !ProcUtil.IsSameProcess(pid, startUtc);
    }

    /// <summary>
    /// Releases locks whose owning runner died. A runner killed between taking its lock and
    /// finishing would otherwise refuse its bucket forever.
    /// </summary>
    public static int PruneStaleLocks()
    {
        EnsureDirs();
        var removed = 0;
        foreach (var f in Directory.EnumerateFiles(RunsDir, "*.lock"))
            if (IsLockStale(f)) { Delete(f); removed++; }
        return removed;
    }
}
