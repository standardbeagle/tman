using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tman;

public enum RunState { Running, Exited, Killed, Reaped, TimedOut, Stalled, Culled }

public sealed class RunRecord
{
    public required string Id { get; set; }
    public string? Name { get; set; }
    public int Pid { get; set; }
    public int RunnerPid { get; set; }
    public DateTime RunnerStartUtc { get; set; }
    public required string Command { get; set; }
    public required string[] Args { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime ChildStartUtc { get; set; }
    public DateTime HeartbeatUtc { get; set; }
    public DateTime LastOutputUtc { get; set; }
    public RunState State { get; set; } = RunState.Running;
    public int? ExitCode { get; set; }
    public long? MaxTimeSec { get; set; }
    public long? StallSec { get; set; }
    public long? MaxMemMb { get; set; }
    public double? MaxCpuPct { get; set; }
    public long PeakMemMb { get; set; }
    public string? KillReason { get; set; }

    public bool Matches(string nameOrId) =>
        string.Equals(Name, nameOrId, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Id, nameOrId, StringComparison.OrdinalIgnoreCase);
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(RunRecord))]
internal partial class RunRecordJsonContext : JsonSerializerContext;

public static class Store
{
    public static string Root =>
        Environment.GetEnvironmentVariable("TMAN_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tman");

    static string RunsDir => Path.Combine(Root, "runs");

    public static void EnsureDirs() => Directory.CreateDirectory(RunsDir);

    static string PathFor(string id) => Path.Combine(RunsDir, id + ".json");

    public static string LockPathFor(string name)
    {
        var safe = new string(name.Select(c =>
            char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
        return Path.Combine(RunsDir, safe + ".lock");
    }

    public static void Save(RunRecord r)
    {
        EnsureDirs();
        var tmp = PathFor(r.Id) + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(r, RunRecordJsonContext.Default.RunRecord));
        File.Move(tmp, PathFor(r.Id), overwrite: true);
    }

    public static RunRecord? Load(string id)
    {
        var p = PathFor(id);
        if (!File.Exists(p)) return null;
        try { return JsonSerializer.Deserialize(File.ReadAllText(p), RunRecordJsonContext.Default.RunRecord); }
        catch (JsonException) { return null; }
    }

    public static List<RunRecord> LoadAll()
    {
        EnsureDirs();
        var list = new List<RunRecord>();
        foreach (var f in Directory.EnumerateFiles(RunsDir, "*.json"))
        {
            try
            {
                var r = JsonSerializer.Deserialize(File.ReadAllText(f), RunRecordJsonContext.Default.RunRecord);
                if (r is not null) list.Add(r);
            }
            catch (JsonException) { }
        }
        return list;
    }

    public static void Remove(string id)
    {
        var p = PathFor(id);
        if (File.Exists(p)) File.Delete(p);
    }

    public static void PruneCompleted(TimeSpan olderThan)
    {
        var cutoff = DateTime.UtcNow - olderThan;
        foreach (var r in LoadAll())
            if (r.State != RunState.Running && r.HeartbeatUtc < cutoff)
                Remove(r.Id);
    }
}
