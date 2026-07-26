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

    /// <summary>
    /// One of a bucket's parallel slots. Claimed, held, and given up exactly as the dedup name lock
    /// is — one <see cref="TryClaimLock"/> serves both — hence the shared `.lock` suffix.
    /// </summary>
    public static string SlotPathFor(string runKey, int slot) =>
        Path.Combine(RunsDir, $"{RunKey.LockStem(runKey)}-slot{slot}.lock");

    public static void Save(RunRecord r)
    {
        EnsureDirs();
        // one record has two writers — its own runner, and the housekeeping sweep any other tman
        // command runs — so the temp name is per writer: on a shared one, whichever renames second
        // finds the file already gone and throws in the middle of a run
        var tmp = $"{PathFor(r.Id)}.{Environment.ProcessId}-{Environment.CurrentManagedThreadId}.tmp";
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
    /// Stamps the owning runner over whatever the lock held before. This is no longer a staleness
    /// test — nothing judges staleness, because the kernel dropping a dead holder's handle is the
    /// whole signal — but the stamp did not become dead with it. It is what makes the ownership
    /// invariant below <em>observable</em>: a claim that took the existing file over in place and a
    /// claim that unlinked the name and created a fresh one are indistinguishable by name, and
    /// differ only in which inode ends up carrying the new owner's pid. That is what
    /// <c>ReclaimingADeadRunnersName_TakesOverItsLockFileRatherThanReplacingIt</c> reads through a
    /// hard link, and it is the only evidence the repo has that the dedup path obeys the invariant.
    /// It also answers "who holds this bucket" from `~/.tman/runs/*.lock` with no running tman.
    /// </summary>
    public static void StampLockOwner(FileStream lockFile)
    {
        var startUtc = ProcUtil.StartTimeUtc(Environment.ProcessId) ?? DateTime.UtcNow;
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            $"{Environment.ProcessId} {startUtc:O}\n");
        lockFile.SetLength(0);
        lockFile.Write(bytes);
        lockFile.Flush(flushToDisk: false);
    }

    // THE LOCK OWNERSHIP INVARIANT, which every claim and release below obeys:
    //
    //     a lock is claimed by opening its file exclusively, and a lock file is never unlinked.
    //
    // Both halves are load-bearing, and neither is a check that can be tightened. Creating a lock
    // only after removing someone else's is two steps that no ordering makes atomic — two
    // claimants each remove the file the other created, and each holds a different file answering
    // to one name. And no unlink is safe, not even one made while holding the file exclusively: a
    // claimant that opened the name a moment earlier takes the lock the instant the unlinker drops
    // it, and is then holding a file that no longer has a name, while the next arrival creates a
    // fresh one and holds that. Measured on this platform, driving runs and a stale-lock sweep at
    // one name: 2% of rounds ran two runs of that name at once, and none once the sweep stopped
    // unlinking. Nothing is gained by removing them either — a lock whose holder is gone is taken
    // over in place, because the kernel drops the hold when the process dies. So a lock file is
    // created once per bucket and then lives as long as the tman home does, and holding it is the
    // whole of the claim.

    /// <summary>
    /// Takes a bucket's dedup lock, or returns null when another runner holds the name.
    /// </summary>
    public static FileStream? TryAcquireNameLock(string runKey)
    {
        EnsureDirs();
        return TryClaimLock(LockPathFor(runKey));
    }

    /// <summary>
    /// Claims one of a bucket's <paramref name="maxParallel"/> slots, or returns null when every
    /// slot is taken. Holding the slot file exclusively is the claim, so two runners racing for the
    /// last slot cannot both win — counting live records could not offer that, because the count is
    /// read before any of the racers has a record to be counted.
    /// </summary>
    public static FileStream? TryAcquireSlot(string runKey, int maxParallel)
    {
        EnsureDirs();
        for (var slot = 0; slot < maxParallel; slot++)
        {
            var claimed = TryClaimLock(SlotPathFor(runKey, slot));
            if (claimed is not null) return claimed;
        }
        return null;
    }

    /// <summary>
    /// Takes a lock file exclusively, creating it when absent and taking it over in place when its
    /// previous holder is gone. The single claim both the dedup name and the parallel slots are
    /// taken with — see the ownership invariant above.
    /// </summary>
    static FileStream? TryClaimLock(string path)
    {
        FileStream file;
        // tman never removes a lock file, but a user or another tool can, and a claim that raced
        // that removal reads as a fault it is not; one retry settles it without hiding a real one
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
                break;
            }
            catch (IOException e) when (IsHeldByAnother(e, path)) { return null; }
            catch (IOException) when (attempt == 0) { }
        }

        StampLockOwner(file);
        return file;
    }

    /// <summary>
    /// True when the only thing that refused the open was another holder — the one reason
    /// <see cref="FileShare.None"/> turns away a lock file that is there. A path that is not there,
    /// a missing directory, or a create that fails outright is a fault, and a run that reads it as a
    /// busy slot waits out its whole queue timeout before blaming contention for it.
    /// </summary>
    static bool IsHeldByAnother(IOException e, string path) =>
        e is not (FileNotFoundException or DirectoryNotFoundException) && File.Exists(path);

    /// <summary>
    /// Gives up a held lock. The file stays: it is the handle, not the name, that admits a run.
    /// </summary>
    public static void ReleaseLock(FileStream lockFile) => lockFile.Dispose();

}
