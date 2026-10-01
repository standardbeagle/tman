using System.Diagnostics;

namespace Tman;

/// <summary>
/// Waiting for a slot, as a state of the run rather than a pause before it. A run that cannot be
/// admitted at once saves itself as <see cref="RunState.Queued"/> under the id it will run with,
/// so `tman list` shows it, `tman kill` reaches it, and Ctrl+C ends it with an outcome — none of
/// which a waiter with no record could have. A run admitted at once never writes a queued record:
/// the uncontended path, which is nearly every run, pays nothing for this.
/// </summary>
public static class Admission
{
    /// <summary>How often a waiter retries, refreshes its heartbeat, and looks for a kill request.</summary>
    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    public const string CancelledReason = "cancelled while queued";

    /// <summary>
    /// One of the bucket's <paramref name="maxParallel"/> slots, or null when the run ended while
    /// queued — its record is then final and says why, and it has no child.
    /// </summary>
    public static async Task<FileStream?> ClaimBucketSlot(
        RunRecord record, int maxParallel, TimeSpan timeout, CancellationToken ct)
    {
        var group = record.Group!;
        // holding the slot file, rather than counting live runs, is what admits this run: every
        // racer would read the same count, but only one can create the same file
        var slot = Store.TryAcquireSlot(group, maxParallel);
        if (slot is not null) return slot;

        var said = false;
        var since = Stopwatch.GetTimestamp();
        slot = await Wait(record, timeout, ct, $"a '{group}' slot (all {maxParallel} busy)",
            tryClaim: () =>
            {
                // a slot may be held by a live child whose runner died; free it before waiting on it
                Reaper.ReapOrphans(quiet: true);
                return Store.TryAcquireSlot(group, maxParallel);
            },
            waiting: _ =>
            {
                // said once: a line per poll was 150 lines over a full queue, burying the child's
                // own output once it started
                if (said) return;
                said = true;
                Console.Error.WriteLine(
                    $"tman: all {maxParallel} '{group}' slots busy, waiting (queue-timeout {Canon.Duration(timeout)})...");
            });
        if (slot is not null)
            Console.Error.WriteLine($"tman: slot acquired after {Canon.Duration(Stopwatch.GetElapsedTime(since))}");
        return slot;
    }

    /// <summary>The slot key a named queue's slots are claimed under, machine-wide.</summary>
    internal static string QueueKey(string queue) => "queue:" + queue;

    /// <summary>How often a named queue's waiter says where it stands.</summary>
    static readonly TimeSpan QueueReportInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// One of <paramref name="queue"/>'s slots, taken in arrival order across every tman on the
    /// machine, or null when the run ended while queued. The record is saved in line before the
    /// first claim, so a run arriving after this one sees it; and a run claims only while nobody
    /// earlier is still waiting. A run that saves its record after another has looked is behind it —
    /// arrival is arrival as the store sees it.
    /// </summary>
    public static async Task<FileStream?> ClaimQueueSlot(RunRecord record, NamedQueue queue, CancellationToken ct)
    {
        record.Queue = queue.Name;
        record.QueuedUtc = DateTime.UtcNow;
        var key = QueueKey(queue.Name);
        var waited = false;
        var lastReport = TimeSpan.Zero;
        var since = Stopwatch.GetTimestamp();
        var slot = await Wait(record, queue.Timeout, ct, $"queue '{queue.Name}'",
            tryClaim: () =>
            {
                Reaper.ReapOrphans(quiet: true);
                return Position(record, Store.LoadAll()) == 1 ? Store.TryAcquireSlot(key, queue.MaxParallel) : null;
            },
            waiting: elapsed =>
            {
                if (waited && elapsed - lastReport < QueueReportInterval) return;
                var position = Position(record, Store.LoadAll());
                Console.Error.WriteLine(waited
                    ? $"tman: still queued for '{queue.Name}': position {position}, waited {Canon.Duration(elapsed)}"
                    : $"tman: queued for '{queue.Name}' at position {position} (max-parallel {queue.MaxParallel}, queue-timeout {Canon.Duration(queue.Timeout)})");
                waited = true;
                lastReport = elapsed;
            });
        if (slot is not null && waited)
            Console.Error.WriteLine($"tman: admitted from queue '{queue.Name}' after {Canon.Duration(Stopwatch.GetElapsedTime(since))}");
        return slot;
    }

    /// <summary>
    /// Where <paramref name="run"/> stands in its named queue's line: 1 + the live waiters that
    /// joined before it, by join time and then id, so every reader agrees on the order. Null for a
    /// run not waiting in a named queue. A waiter whose tman died holds no place.
    /// </summary>
    public static int? Position(RunRecord run, IEnumerable<RunRecord> all)
    {
        if (run is not { State: RunState.Queued, Queue: { } queue, QueuedUtc: { } joined }) return null;
        return 1 + all.Count(o =>
            o is { State: RunState.Queued, QueuedUtc: { } at }
            && o.Queue == queue
            && (at < joined || (at == joined && string.CompareOrdinal(o.Id, run.Id) < 0))
            && Reaper.RunnerAlive(o));
    }

    /// <summary>
    /// Saves <paramref name="record"/> as queued and retries <paramref name="tryClaim"/> until it
    /// returns a slot, or the run is killed, interrupted, or out of <paramref name="timeout"/> —
    /// which end it as killed with the reason, and return null. <paramref name="waiting"/> is told
    /// how long the run has waited after every claim that failed.
    /// </summary>
    static async Task<FileStream?> Wait(
        RunRecord record, TimeSpan timeout, CancellationToken ct, string waitingFor,
        Func<FileStream?> tryClaim, Action<TimeSpan> waiting)
    {
        record.State = RunState.Queued;
        record.StartedUtc = record.HeartbeatUtc = DateTime.UtcNow;
        Store.Save(record);
        var since = Stopwatch.GetTimestamp();
        while (true)
        {
            // before every claim: a run told to stop must not be admitted on the way out
            if ((Store.ReadKillRequest(record.Id) ?? (ct.IsCancellationRequested ? CancelledReason : null)) is { } stop)
                return EndQueued(record, stop);
            if (tryClaim() is { } slot) return slot;
            var elapsed = Stopwatch.GetElapsedTime(since);
            if (elapsed >= timeout)
                return EndQueued(record, $"queue timeout waiting for {waitingFor}");
            waiting(elapsed);

            try { await Task.Delay(PollInterval, ct); }
            catch (OperationCanceledException) { continue; }
            record.HeartbeatUtc = DateTime.UtcNow;
            Store.Save(record);
        }
    }

    static FileStream? EndQueued(RunRecord record, string reason)
    {
        Console.Error.WriteLine($"tman: {reason}");
        record.State = RunState.Killed;
        record.KillReason = reason;
        record.HeartbeatUtc = DateTime.UtcNow;
        Store.Save(record);
        return null;
    }
}
