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

        // said once: a line per poll was 150 lines over a full queue, burying the child's own
        // output once it started
        Console.Error.WriteLine(
            $"tman: all {maxParallel} '{group}' slots busy, waiting (queue-timeout {Canon.Duration(timeout)})...");
        var since = Stopwatch.GetTimestamp();
        slot = await Wait(record, timeout, ct, $"a '{group}' slot (all {maxParallel} busy)", () =>
        {
            // a slot may be held by a live child whose runner died; free it before waiting on it
            Reaper.ReapOrphans(quiet: true);
            return Store.TryAcquireSlot(group, maxParallel);
        });
        if (slot is not null)
            Console.Error.WriteLine($"tman: slot acquired after {Canon.Duration(Stopwatch.GetElapsedTime(since))}");
        return slot;
    }

    /// <summary>
    /// Saves <paramref name="record"/> as queued and retries <paramref name="tryClaim"/> until it
    /// returns a slot, or the run is killed, interrupted, or out of <paramref name="timeout"/> —
    /// which end it as killed with the reason, and return null.
    /// </summary>
    static async Task<FileStream?> Wait(
        RunRecord record, TimeSpan timeout, CancellationToken ct, string waitingFor, Func<FileStream?> tryClaim)
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
            if (Stopwatch.GetElapsedTime(since) >= timeout)
                return EndQueued(record, $"queue timeout waiting for {waitingFor}");

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
