using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Tman;

/// <summary>
/// The documents tman prints for another program to read: `tman list --json` and, from the same
/// writers, `tman queue --json`. Written field by field rather than serialized from the record, so
/// the contract is this file and not the store's on-disk shape: a field renamed in the store keeps
/// its name here, and a field appears here because it was added here. Every document carries
/// <see cref="Schema"/>. A field may be added under the same number; one removed, renamed or changed
/// in meaning bumps it, so a reader that checks the number never misreads a field.
/// </summary>
public static class JsonReport
{
    public const int Schema = 1;

    /// <summary>Indented and relaxed, as `tman status --json` is: a command line stays readable.</summary>
    static readonly JsonWriterOptions Options = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>`tman list --json`: <paramref name="runs"/>, each placed in its queue's line by <paramref name="all"/>.</summary>
    public static string Runs(IEnumerable<RunRecord> runs, IReadOnlyCollection<RunRecord> all) =>
        Document(w =>
        {
            w.WriteStartArray("runs");
            foreach (var r in runs) WriteRun(w, r, all);
            w.WriteEndArray();
        });

    internal static string Document(Action<Utf8JsonWriter> body)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, Options))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", Schema);
            body(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    static void WriteRun(Utf8JsonWriter w, RunRecord r, IReadOnlyCollection<RunRecord> all)
    {
        w.WriteStartObject();
        w.WriteString("id", r.Id);
        String(w, "name", r.Name);
        String(w, "alias", r.Alias);
        String(w, "parentId", r.ParentId);
        String(w, "cwd", r.Cwd);
        w.WriteString("projectRoot", Scope.RootOf(r));
        String(w, "agentSession", r.AgentSession);
        w.WriteString("command", r.Command);
        w.WriteStartArray("args");
        foreach (var a in r.Args) w.WriteStringValue(a);
        w.WriteEndArray();
        // 0 while no child exists: queued, or a program that could not be started
        w.WriteNumber("pid", r.Pid);
        w.WriteNumber("runnerPid", r.RunnerPid);
        w.WriteString("state", StateName(r));
        String(w, "endReason", EndReason(r));
        Number(w, "exitCode", r.ExitCode);
        Number(w, "tmanExit", r.IsFinished ? Runner.ExitCodeFor(r) : null);
        String(w, "killReason", r.KillReason);
        w.WriteBoolean("killRequested", r.KillRequested);
        String(w, "culledBy", r.CulledBy);
        Time(w, "queuedAt", QueuedAt(r));
        Time(w, "admittedAt", r.AdmittedUtc);
        Time(w, "startedAt", StartedAt(r));
        Time(w, "endedAt", r.IsFinished ? r.HeartbeatUtc : null);
        Time(w, "heartbeatAt", r.HeartbeatUtc);
        String(w, "queue", r.Queue);
        Number(w, "position", Admission.Position(r, all));
        Number(w, "queuedSeconds", r.QueuedSeconds);
        Number(w, "admittedCores", r.AdmittedCores);
        Number(w, "admittedMemMb", r.AdmittedMemMb);
        w.WriteBoolean("exclusive", r.Exclusive);
        w.WriteBoolean("hold", r.Hold);
        w.WriteBoolean("service", r.Service);
        w.WriteNumber("peakMemMb", r.PeakMemMb);
        w.WriteNumber("peakCores", r.PeakCores);
        Number(w, "currentCores", r.CurrentCores);
        w.WriteNumber("cpuSeconds", r.CpuSeconds);
        WriteLabels(w, r.Labels);
        w.WriteEndObject();
    }

    /// <summary>Three states a reader acts on: waiting, under way, over. Why it ended is <see cref="EndReason"/>.</summary>
    static string StateName(RunRecord r) => r.State switch
    {
        RunState.Queued => "queued",
        RunState.Running => "running",
        _ => "exited",
    };

    /// <summary>
    /// Why a finished run ended, from the record's state and the fields set at the decision — never
    /// from <see cref="RunRecord.KillReason"/>'s wording. Null while the run is live. "killed" is
    /// every kill not named otherwise — `tman kill` (killRequested), Ctrl+C, an exit status nobody
    /// read; "culled" is a cap kill recorded before tman named the cap.
    /// </summary>
    internal static string? EndReason(RunRecord r) => r.State switch
    {
        RunState.Running or RunState.Queued => null,
        RunState.Exited => "exit",
        RunState.TimedOut => "max-time",
        RunState.Stalled => "stall",
        RunState.Culled => r.CulledBy switch
        {
            "max-mem" or "limit-mem" => "mem",
            "max-cpu" => "cpu",
            _ => "culled",
        },
        RunState.Killed when r.QueueTimedOut => "queue-timeout",
        RunState.Killed => "killed",
        RunState.Reaped => "reaped",
        RunState.StartFailed => "start-failed",
        _ => throw new ArgumentOutOfRangeException(nameof(r), r.State, "a run state the report does not name"),
    };

    /// <summary>
    /// When the run joined its named queue; for a run still waiting for a bucket slot, when that
    /// wait began. A run that waited for a slot and has since started keeps no record of the wait.
    /// </summary>
    static DateTime? QueuedAt(RunRecord r) =>
        r.QueuedUtc ?? (r.State == RunState.Queued ? r.StartedUtc : null);

    /// <summary>
    /// When the child started: null for a run that never had one — still queued, ended while
    /// queued, or unable to start. A hold starts nothing of its own; it is under way once admitted.
    /// </summary>
    static DateTime? StartedAt(RunRecord r) =>
        r.State == RunState.Queued || r.Pid == 0 ? null
        : r.Hold ? r.AdmittedUtc
        : r.StartedUtc;

    internal static void WriteLabels(Utf8JsonWriter w, IReadOnlyDictionary<string, string>? labels)
    {
        w.WriteStartObject("labels");
        if (labels is not null)
            foreach (var (k, v) in labels.OrderBy(kv => kv.Key, StringComparer.Ordinal)) w.WriteString(k, v);
        w.WriteEndObject();
    }

    internal static void String(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null) w.WriteNull(name);
        else w.WriteString(name, value);
    }

    internal static void Number(Utf8JsonWriter w, string name, double? value)
    {
        if (value is { } v) w.WriteNumber(name, v);
        else w.WriteNull(name);
    }

    internal static void Number(Utf8JsonWriter w, string name, long? value)
    {
        if (value is { } v) w.WriteNumber(name, v);
        else w.WriteNull(name);
    }

    /// <summary>UTC, ISO 8601 with its Z: a record's times are UTC whatever kind they read back as.</summary>
    internal static void Time(Utf8JsonWriter w, string name, DateTime? value)
    {
        if (value is { } v) w.WriteString(name, DateTime.SpecifyKind(v, DateTimeKind.Utc));
        else w.WriteNull(name);
    }
}
