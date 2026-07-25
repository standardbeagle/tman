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


    static void Delete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void PruneCompleted(TimeSpan olderThan)
    {
        var cutoff = DateTime.UtcNow - olderThan;
        foreach (var r in LoadAll())
            if (r.IsFinished && r.HeartbeatUtc < cutoff)
                Remove(r.Id);
    }
}
