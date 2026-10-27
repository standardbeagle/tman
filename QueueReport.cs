using System.Text;
using System.Text.Json;

namespace Tman;

/// <summary>
/// `tman queue`: each machine queue as admission sees it — its budget, what is admitted and what
/// that books, the host load no admitted run accounts for, who waits and in what order, and whether
/// a run of a given cost would be admitted now. The verdict is <see cref="Admission.Admits"/>
/// itself, asked about a run that has not arrived, so a scheduler reading it never re-derives the
/// queue's rules from the numbers and drifts from them.
/// </summary>
public static class QueueReport
{
    /// <summary>One queue at one instant.</summary>
    /// <param name="Admitted">Every live admission, in admission order; the totals are over these.</param>
    /// <param name="Waiting">Every live waiter, in line order.</param>
    /// <param name="Unattributed">Host load no admitted run accounts for; null where it cannot be measured.</param>
    /// <param name="Free">
    /// Budget less what is booked — admissions, and the unattributed load while anything is admitted,
    /// as admission books it. Negative when the queue is oversubscribed.
    /// </param>
    /// <param name="Probe">The cost the verdict was asked for.</param>
    /// <param name="AdmitsProbe">Whether a finite run costing <paramref name="Probe"/>, arriving now, is admitted at once.</param>
    public sealed record Snapshot(
        NamedQueue Queue,
        List<RunRecord> Admitted,
        List<RunRecord> Waiting,
        Admission.AdmissionCost Booked,
        Admission.AdmissionCost? Unattributed,
        Admission.AdmissionCost Free,
        bool ExclusiveHeld,
        Admission.AdmissionCost Probe,
        bool AdmitsProbe);

    /// <param name="probeCores">Cores to ask the verdict for; null for the queue's default cost.</param>
    public static Snapshot Take(NamedQueue queue, List<RunRecord> all, double? probeCores, long? probeMemMb, DateTime now)
    {
        var admitted = Admission.AdmittedRuns(queue.Name, all).OrderBy(r => r.AdmittedUtc).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
        // the line's own order (Admission.Position): join time, then id; a waiter whose tman died holds no place
        var waiting = all
            .Where(r => r is { State: RunState.Queued, AdmittedUtc: null, QueuedUtc: not null } && r.Queue == queue.Name
                        && Reaper.RunnerAlive(r))
            .OrderBy(r => r.QueuedUtc).ThenBy(r => r.Id, StringComparer.Ordinal)
            .ToList();
        var booked = new Admission.AdmissionCost(
            admitted.Sum(r => r.AdmittedCores ?? 0), admitted.Sum(r => r.AdmittedMemMb ?? 0));
        var unattributed = Admission.UnattributedLoad(admitted);
        var outside = admitted.Count > 0 ? unattributed : null;
        var free = new Admission.AdmissionCost(
            queue.Cores - booked.Cores - (outside?.Cores ?? 0), queue.MemMb - booked.MemMb - (outside?.MemMb ?? 0));

        var probe = new Admission.AdmissionCost(probeCores ?? queue.DefaultCores, probeMemMb ?? queue.DefaultMemMb);
        // a run that has not arrived: it joins the line now, behind every waiter, as an ordinary
        // finite run — the "~" id sorts after every real id, so an equal join time puts it last too
        var arrival = new RunRecord
        {
            Id = "~arrival", Command = "", Args = [], State = RunState.Queued, Queue = queue.Name, QueuedUtc = now,
        };
        var admits = Admission.Admits(arrival, queue, probe, exclusive: false, all, admitted, outside);
        return new Snapshot(queue, admitted, waiting, booked, unattributed, free,
            Admission.ExclusiveHeld(queue.Name, all), probe, admits);
    }

    /// <summary>`tman queue --json`. Entries are filtered by <paramref name="filter"/>; the totals never are.</summary>
    public static string Json(IReadOnlyList<Snapshot> queues, IReadOnlyDictionary<string, string> filter, DateTime now) =>
        JsonReport.Document(w =>
        {
            w.WriteStartObject("host");
            w.WriteNumber("cores", Host.Cores);
            w.WriteNumber("memMb", Host.MemMb);
            w.WriteEndObject();
            w.WriteStartArray("queues");
            foreach (var q in queues)
            {
                w.WriteStartObject();
                w.WriteString("name", q.Queue.Name);
                Cost(w, "budget", q.Queue.Cores, q.Queue.MemMb);
                Cost(w, "defaultCost", q.Queue.DefaultCores, q.Queue.DefaultMemMb);
                w.WriteNumber("timeoutSeconds", q.Queue.Timeout.TotalSeconds);
                w.WriteNumber("starvationBoundSeconds", Admission.StarvationBound.TotalSeconds);
                Cost(w, "booked", q.Booked);
                if (q.Unattributed is { } u) Cost(w, "unattributed", u);
                else w.WriteNull("unattributed");
                Cost(w, "free", q.Free);
                w.WriteBoolean("exclusiveHeld", q.ExclusiveHeld);
                w.WriteNumber("admittedCount", q.Admitted.Count);
                w.WriteNumber("waitingCount", q.Waiting.Count);
                w.WriteStartObject("probe");
                w.WriteNumber("cores", q.Probe.Cores);
                w.WriteNumber("memMb", q.Probe.MemMb);
                w.WriteBoolean("admitsNow", q.AdmitsProbe);
                w.WriteEndObject();

                w.WriteStartArray("admitted");
                foreach (var r in q.Admitted.Where(r => Labels.Match(r.Labels, filter)))
                {
                    w.WriteStartObject();
                    Identity(w, r);
                    w.WriteNumber("cores", r.AdmittedCores ?? 0);
                    w.WriteNumber("memMb", r.AdmittedMemMb ?? 0);
                    JsonReport.Number(w, "currentCores", r.CurrentCores);
                    w.WriteNumber("peakMemMb", r.PeakMemMb);
                    JsonReport.Time(w, "admittedAt", r.AdmittedUtc);
                    JsonReport.Number(w, "queuedSeconds", r.QueuedSeconds);
                    Kind(w, r);
                    JsonReport.WriteLabels(w, r.Labels);
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WriteStartArray("waiting");
                foreach (var (r, position) in Line(q, filter))
                {
                    w.WriteStartObject();
                    Identity(w, r);
                    w.WriteNumber("position", position);
                    JsonReport.Time(w, "queuedAt", r.QueuedUtc);
                    w.WriteNumber("waitedSeconds", Math.Max(0, (now - r.QueuedUtc!.Value).TotalSeconds));
                    w.WriteNumber("cores", r.AdmittedCores ?? 0);
                    w.WriteNumber("memMb", r.AdmittedMemMb ?? 0);
                    Kind(w, r);
                    w.WriteBoolean("starving", now - r.QueuedUtc!.Value >= Admission.StarvationBound);
                    JsonReport.WriteLabels(w, r.Labels);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });

    /// <summary>`tman queue`: one block per queue, the totals line first.</summary>
    public static string Text(IReadOnlyList<Snapshot> queues, IReadOnlyDictionary<string, string> filter, DateTime now)
    {
        var sb = new StringBuilder();
        foreach (var q in queues)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine(
                $"queue \"{q.Queue.Name}\": budget {Describe(q.Queue.Cores, q.Queue.MemMb)}; booked {Describe(q.Booked)}; " +
                (q.Unattributed is { } u ? $"unattributed {Describe(u)}; " : "unattributed not measured; ") +
                $"free {Describe(q.Free)}" + (q.ExclusiveHeld ? "; held by an exclusive run" : ""));
            sb.AppendLine($"  a {Describe(q.Probe)} run arriving now: {(q.AdmitsProbe ? "admitted" : "waits")}");
            var admitted = q.Admitted.Where(r => Labels.Match(r.Labels, filter)).ToList();
            var waiting = Line(q, filter).ToList();
            if (admitted.Count == 0 && waiting.Count == 0) continue;
            sb.AppendLine($"  {"",-4}{"ID",-14}{"NAME",-16}{"CORES",-7}{"MEM",-9}{"AGE",-8}COMMAND");
            foreach (var r in admitted)
                Row(sb, "run", r, r.AdmittedUtc!.Value, now);
            foreach (var (r, position) in waiting)
                Row(sb, $"#{position}", r, r.QueuedUtc!.Value, now);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>The waiters <paramref name="filter"/> keeps, each with its place in the whole line.</summary>
    static IEnumerable<(RunRecord Run, int Position)> Line(Snapshot q, IReadOnlyDictionary<string, string> filter) =>
        q.Waiting.Select((r, i) => (r, i + 1)).Where(e => Labels.Match(e.r.Labels, filter));

    static void Row(StringBuilder sb, string place, RunRecord r, DateTime since, DateTime now) =>
        sb.AppendLine(
            $"  {place,-4}{r.Id,-14}{Canon.Ellipsize(r.Name ?? "-", 15),-16}{r.AdmittedCores ?? 0,-7:0.#}" +
            $"{Canon.Mem(r.AdmittedMemMb ?? 0),-9}{Canon.Duration(now - since),-8}{Canon.CommandLine(r.Command, r.Args)}");

    static string Describe(Admission.AdmissionCost c) => Describe(c.Cores, c.MemMb);

    static string Describe(double cores, long memMb) =>
        $"{cores:0.#} cores, " + (memMb < 0 ? "-" + Canon.Mem(-memMb) : Canon.Mem(memMb));

    static void Identity(Utf8JsonWriter w, RunRecord r)
    {
        w.WriteString("id", r.Id);
        JsonReport.String(w, "name", r.Name);
        JsonReport.String(w, "alias", r.Alias);
        w.WriteString("command", Canon.CommandLine(r.Command, r.Args, full: true));
        w.WriteNumber("pid", r.Pid);
        w.WriteNumber("runnerPid", r.RunnerPid);
    }

    static void Kind(Utf8JsonWriter w, RunRecord r)
    {
        w.WriteBoolean("exclusive", r.Exclusive);
        w.WriteBoolean("hold", r.Hold);
        w.WriteBoolean("service", r.Service);
    }

    static void Cost(Utf8JsonWriter w, string name, Admission.AdmissionCost c) => Cost(w, name, c.Cores, c.MemMb);

    static void Cost(Utf8JsonWriter w, string name, double cores, long memMb)
    {
        w.WriteStartObject(name);
        w.WriteNumber("cores", cores);
        w.WriteNumber("memMb", memMb);
        w.WriteEndObject();
    }
}
