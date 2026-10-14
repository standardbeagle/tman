using System.Diagnostics;

namespace Tman;

/// <summary>
/// Waiting for admission, as a state of the run rather than a pause before it. A run that cannot be
/// admitted at once saves itself as <see cref="RunState.Queued"/> under the id it will run with, so
/// `tman list` shows it, `tman kill` reaches it, and Ctrl+C ends it with an outcome — none of which
/// a waiter with no record could have. A queue waiter writes that record once, before its first
/// claim, so a run arriving after it sees the line; the write that admits it is the run's own
/// record, so a run that is admitted at once pays the queued save and the admitting one.
/// </summary>
public static class Admission
{
    /// <summary>How often a waiter refreshes its heartbeat and looks for a kill request.</summary>
    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    public const string CancelledReason = "cancelled while queued";

    /// <summary>
    /// How long a run may wait before it is admitted next regardless of smaller arrivals: without
    /// this, a stream of small runs that always fit would starve one large one forever. The bound
    /// reserves the budget only while the run would fit beside the admitted holds: holds end when
    /// their owner's work ends, not when the queue stops admitting, so no reservation can admit a
    /// run too large to sit beside them. Settable so
    /// a test can prove the bound without waiting minutes; production never changes it.
    /// </summary>
    internal static TimeSpan StarvationBound = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Reads what the whole host is using. Settable so a test can fake the host's load; production
    /// never changes it.
    /// </summary>
    internal static Func<Host.Usage?> MeasureHost = Host.MeasureUsage;

    /// <summary>Admission attempts made by this process, so a test can prove waiters do not spin.</summary>
    internal static long AttemptsForTest;

    internal static void ResetAttemptsForTest() => Interlocked.Exchange(ref AttemptsForTest, 0);

    /// <summary>The cost a run is admitted with: cores and memory it is expected to bear.</summary>
    public readonly record struct AdmissionCost(double Cores, long MemMb);

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

    /// <summary>The cost estimator's key: the run's bucket and what it runs, so like work sizes like.</summary>
    public const int HistoryDepth = 20;

    /// <summary>
    /// The cost a run is expected to bear, in cores and memory:
    /// <list type="number">
    /// <item>a high percentile of the run's bucket's recent records, once there are any;</item>
    /// <item>the alias's declared `cores`/`mem`, or the project's in `defaults`;</item>
    /// <item>the queue's own defaults.</item>
    /// </list>
    /// The high percentile, not the mean, because admission is a ceiling: a run that once took 5 GB
    /// must be sized at 5 GB, and one good sample must not halve a suite's claim.
    /// </summary>
    public static AdmissionCost ExpectedCost(RunRecord record, NamedQueue queue, double? aliasCores, long? aliasMemMb)
    {
        var history = Store.LoadAll()
            .Where(r => r.Group == record.Group && r.Id != record.Id && r.IsFinished)
            .OrderByDescending(r => r.StartedUtc)
            .Take(HistoryDepth)
            .ToList();
        var cores = Percentile(history.Select(r => r.PeakCores).Where(v => v > 0).ToList());
        var memMb = Percentile(history.Select(r => (double)r.PeakMemMb).Where(v => v > 0).ToList());
        return new AdmissionCost(
            cores > 0 ? cores : aliasCores ?? queue.DefaultCores,
            memMb > 0 ? (long)Math.Ceiling(memMb) : aliasMemMb ?? queue.DefaultMemMb);
    }

    /// <summary>
    /// The 90th percentile of a small sample, nearest-rank. Empty for no samples, so "no history"
    /// and "history of runs too small to measure" are one answer: fall back to the declared default.
    /// </summary>
    static double Percentile(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        var rank = (int)Math.Ceiling(0.9 * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }

    /// <summary>True when a run costing <paramref name="cost"/> fits beside what is admitted.</summary>
    public static bool Fits(AdmissionCost cost, IEnumerable<AdmissionCost> admitted, NamedQueue queue)
    {
        double cores = 0;
        long memMb = 0;
        foreach (var a in admitted)
        {
            cores += a.Cores;
            memMb += a.MemMb;
        }
        // a lone run is admitted even when it alone exceeds the budget, or a suite larger than the
        // declared budget could never start; the budget is a bound on what runs *beside* it
        if (cores == 0 && memMb == 0) return true;
        return cores + cost.Cores <= queue.Cores && memMb + cost.MemMb <= queue.MemMb;
    }

    /// <summary>
    /// The host's load that no run in <paramref name="admitted"/> accounts for: what the host is
    /// using, less each run's measured tree cores (its booked cores until it has a sample) and its
    /// booked memory, never below zero. Build servers reparented out of a run's tree, and work tman
    /// never started, show up here; what they are is not tman's to know. Null when the host's use
    /// cannot be measured.
    /// </summary>
    public static AdmissionCost? UnattributedLoad(IEnumerable<RunRecord> admitted)
    {
        if (MeasureHost() is not { } host) return null;
        var cores = host.Cores;
        var memMb = host.MemMb;
        foreach (var r in admitted)
        {
            cores -= r.CurrentCores ?? r.AdmittedCores ?? 0;
            memMb -= r.AdmittedMemMb ?? 0;
        }
        return new AdmissionCost(Math.Max(0, cores), Math.Max(0, memMb));
    }

    /// <summary>The live records holding an admission in <paramref name="queue"/>.</summary>
    public static List<RunRecord> AdmittedRuns(string queue, IEnumerable<RunRecord> all) =>
        all.Where(r => r.Queue == queue && r.AdmittedUtc is not null && !r.IsFinished).ToList();

    /// <summary>True when <paramref name="queue"/> has a live admitted exclusive run holding it.</summary>
    public static bool ExclusiveHeld(string queue, IEnumerable<RunRecord> all) =>
        AdmittedRuns(queue, all).Any(r => r.Exclusive && Reaper.RunnerAlive(r));

    /// <summary>
    /// Waits for admission to <paramref name="queue"/> by cost: saves the record as queued, then
    /// retries until the expected cost fits beside what is admitted, or the run is killed,
    /// interrupted, or out of the queue's timeout — which end it as killed and return null. An
    /// exclusive run waits for admitted work to finish and then holds new admissions until it exits;
    /// a non-exclusive run never starts while an exclusive one is admitted. Arrival order is kept,
    /// but a small run may start ahead of a larger waiting one when it fits, and a run waiting past
    /// the starvation bound is admitted next whatever its size — unless it cannot fit beside the
    /// admitted holds, which no amount of waiting frees.
    /// </summary>
    public static async Task<bool> ClaimAdmission(
        RunRecord record, NamedQueue queue, AdmissionCost cost, bool exclusive,
        double? aliasCores, long? aliasMemMb, CancellationToken ct)
    {
        record.Queue = queue.Name;
        record.QueuedUtc = DateTime.UtcNow;
        record.AdmittedCores = cost.Cores;
        record.AdmittedMemMb = cost.MemMb;
        record.Exclusive = exclusive;

        var gate = QueueKey(queue.Name);
        var said = false;
        long lastReport = Stopwatch.GetTimestamp();

        record.State = RunState.Queued;
        record.StartedUtc = record.HeartbeatUtc = DateTime.UtcNow;
        Store.Save(record);

        // the watcher is armed once, before the first attempt, and kept for the whole wait. A
        // release that lands between an attempt and parking is therefore not lost: the event file
        // already carries it, and the wait returns at once rather than sleeping the backstop.
        using var wake = Store.WatchQueue(gate);
        var since = Stopwatch.GetTimestamp();
        while (true)
        {
            if ((Store.ReadKillRequest(record.Id) ?? (ct.IsCancellationRequested ? CancelledReason : null)) is { } stop)
            { EndQueued(record, stop); return false; }
            var attempt = TryAdmit(record, queue, cost, exclusive, aliasCores, aliasMemMb, gate);
            if (attempt == AdmitOutcome.Admitted) return true;

            var elapsed = Stopwatch.GetElapsedTime(since);
            if (elapsed >= queue.Timeout)
            { EndQueued(record, $"queue timeout waiting for queue '{queue.Name}'"); return false; }

            if (!said)
            {
                Console.Error.WriteLine(
                    $"tman: queued for '{queue.Name}' (needs {cost.Cores:0.#} cores, {Canon.Mem(cost.MemMb)}; " +
                    $"budget {queue.Cores:0.#} cores, {Canon.Mem(queue.MemMb)}, queue-timeout {Canon.Duration(queue.Timeout)})");
                said = true;
                lastReport = Stopwatch.GetTimestamp();
            }
            else if (Stopwatch.GetElapsedTime(lastReport) >= QueueReportInterval)
            {
                Console.Error.WriteLine(
                    $"tman: still queued for '{queue.Name}': position {Position(record, Store.LoadAll())}, waited {Canon.Duration(elapsed)}");
                lastReport = Stopwatch.GetTimestamp();
                // the heartbeat rides the report, and a queued heartbeat is not an admission event,
                // so it wakes nobody: waiting costs nothing while the line does not move
                record.HeartbeatUtc = DateTime.UtcNow;
                Store.Save(record);
            }

            // never park past the remaining queue-timeout: a queue-timeout shorter than the
            // backstop would otherwise be honoured up to ten seconds late, and a timeout does not
            // touch the event file, so nothing else would wake the waiter to notice it. The park is
            // taken from the `elapsed` just checked, not a fresh clock read: the report branch above
            // can run for tens of milliseconds, and a negative timeout would throw and abort the run.
            var remaining = queue.Timeout - elapsed;
            if (attempt == AdmitOutcome.GateBusy)
            {
                // the gate is held across Reaper.ReapOrphans and Store.LoadAll — two reads of the
                // whole run store, tens of milliseconds with thousands of records — so a waiter
                // woken by one release can lose the gate to another that was woken by the same
                // event. It must retry the gate, paced, rather than park the backstop: no further
                // event follows when nobody was admitted, so a park here would stall the line for
                // ten seconds. The pace keeps the retry from becoming a spin when the host is full,
                // and the timeout check above still ends it at its queue-timeout if the gate stays
                // held (a suspended or I/O-hung holder).
                try { await Task.Delay(remaining < GateRetryPace ? remaining : GateRetryPace, ct); }
                catch (OperationCanceledException) { }
            }
            else
                await wake.Wait(remaining < IdleBackstop ? remaining : IdleBackstop, ct);
        }
    }

    /// <summary>The verdict of one admission attempt under the queue's gate.</summary>
    enum AdmitOutcome
    {
        /// <summary>The run's cost fit and it is now recorded as admitted.</summary>
        Admitted,
        /// <summary>The gate was held by another attempt; the caller should retry it.</summary>
        GateBusy,
        /// <summary>The gate was taken, but the run does not fit — wait for a real event.</summary>
        NoRoom,
    }

    /// <summary>
    /// How long a waiter waits between retries of a gate lock another attempt holds. Short enough
    /// that a waiter that lost the gate to the winner of one release gains it promptly, long enough
    /// that a full host does not turn the retry into a spin (or into a <see cref="Store"/> read per
    /// free CPU cycle).
    /// </summary>
    static readonly TimeSpan GateRetryPace = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How long a waiter parks on the store before re-checking anyway. Only a guard against a missed
    /// file event — long enough that it is not a poll, short enough to notice a starvation bound.
    /// </summary>
    static readonly TimeSpan IdleBackstop = TimeSpan.FromSeconds(10);

    /// <summary>
    /// One admission attempt under the queue's gate lock, so two runners cannot both read the same
    /// admitted set and both conclude they fit. Admission is ordering: only the run the line says is
    /// next may go ahead of a larger waiter, unless it is small enough to fit beside admitted work.
    /// The gate is not held for microseconds: the attempt reaps orphans and reads the whole run
    /// store under it, tens of milliseconds with thousands of records, so when one release wakes
    /// several waiters each must retry the gate rather than conclude its turn is over — the one the
    /// line admits would otherwise sleep to the backstop while the others did nothing. A
    /// <see cref="AdmitOutcome.GateBusy"/> says exactly that: retry, do not park.
    /// </summary>
    static AdmitOutcome TryAdmit(
        RunRecord record, NamedQueue queue, AdmissionCost cost, bool exclusive,
        double? aliasCores, long? aliasMemMb, string gate)
    {
        Interlocked.Increment(ref AttemptsForTest);
        using var held = ClaimGate(gate);
        if (held is null) return AdmitOutcome.GateBusy;
        Reaper.ReapOrphans(quiet: true);

        var all = Store.LoadAll();
        var admitted = AdmittedRuns(queue.Name, all);
        var costs = admitted.Select(r => new AdmissionCost(r.AdmittedCores ?? 0, r.AdmittedMemMb ?? 0)).ToList();
        // load outside the admitted runs is booked beside them, but only beside them: with nothing
        // admitted the head starts whatever the host is doing, or outside load could hold the
        // queue forever
        if (costs.Count > 0 && UnattributedLoad(admitted) is { } outside) costs.Add(outside);

        if (exclusive)
        {
            // an exclusive run starts alone: it waits for admitted work to finish, and no run
            // starts while it holds the queue
            if (costs.Count > 0) return AdmitOutcome.NoRoom;
        }
        else
        {
            // a run may not start while an exclusive one is admitted, whatever its cost
            if (ExclusiveHeld(queue.Name, all)) return AdmitOutcome.NoRoom;

            // arrival order, unless a small run can slip into a gap a larger earlier waiter cannot
            // take: the earlier waiter's own cost is what the gap is judged against, so an equal or
            // larger newcomer always queues behind it
            var position = Position(record, all);
            if (position is > 1)
            {
                var earlier = EarlierWaiters(record, all);
                if (earlier.Count > 0)
                {
                    // a starving waiter reserves the budget only when the admitted runs ending
                    // can admit it: one that does not fit beside the holds alone would reserve
                    // until a hold's owner gave up, and that owner's work may be this arrival
                    var holds = admitted.Where(r => r.Hold)
                        .Select(r => new AdmissionCost(r.AdmittedCores ?? 0, r.AdmittedMemMb ?? 0)).ToList();
                    var reserving = earlier.Any(o =>
                        o.QueuedUtc is { } at && DateTime.UtcNow - at >= StarvationBound
                        && Fits(new AdmissionCost(o.AdmittedCores ?? 0, o.AdmittedMemMb ?? 0), holds, queue));
                    if (reserving) return AdmitOutcome.NoRoom;
                    var first = earlier.OrderBy(o => o.QueuedUtc).ThenBy(o => o.Id, StringComparer.Ordinal).First();
                    var firstCost = new AdmissionCost(first.AdmittedCores ?? 0, first.AdmittedMemMb ?? 0);
                    if (Fits(firstCost, costs, queue) || !Fits(cost, costs, queue)) return AdmitOutcome.NoRoom;
                }
            }
        }

        if (!Fits(cost, costs, queue)) return AdmitOutcome.NoRoom;

        record.State = RunState.Queued;
        record.AdmittedUtc = DateTime.UtcNow;
        // the wait is stamped in the same save that admits the run, so every door that claims an
        // admission — a run, a hold — records it in the write that books the cost
        if (record.QueuedUtc is { } joined)
            record.QueuedSeconds = (record.AdmittedUtc.Value - joined).TotalSeconds;
        record.HeartbeatUtc = DateTime.UtcNow;
        Store.Save(record);
        // an admission changes the admitted set, which can free a gap or a turn for another waiter
        Store.TouchQueue(gate);
        return AdmitOutcome.Admitted;
    }

    /// <summary>
    /// Claims the queue gate, retrying briefly while another runner holds it. The holder's work is a
    /// reap and one store read — tens of milliseconds, not microseconds — so a bounded spin rarely
    /// wins it against a waiter already inside; a loss is reported as <see cref="AdmitOutcome.GateBusy"/>
    /// and the caller retries at its own pace rather than parking, because no event follows a lost
    /// gate when nobody was admitted.
    /// </summary>
    static FileStream? ClaimGate(string gate)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 0.5);
        for (;;)
        {
            var held = Store.TryClaimLock(Store.GatePathFor(gate));
            if (held is not null) return held;
            if (Stopwatch.GetTimestamp() >= deadline) return null;
            Thread.Sleep(2);
        }
    }

    static List<RunRecord> EarlierWaiters(RunRecord run, IEnumerable<RunRecord> all)
    {
        var joined = run.QueuedUtc!.Value;
        return all.Where(o =>
            o is { State: RunState.Queued, AdmittedUtc: null, QueuedUtc: { } at }
            && o.Queue == run.Queue
            && (at < joined || (at == joined && string.CompareOrdinal(o.Id, run.Id) < 0))
            && Reaper.RunnerAlive(o)).ToList();
    }

    /// <summary>The slot key a named queue's gate is claimed under, machine-wide.</summary>
    internal static string QueueKey(string queue) => "queue:" + queue;

    /// <summary>How often a named queue's waiter says where it stands.</summary>
    static readonly TimeSpan QueueReportInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Where <paramref name="run"/> stands in its named queue's line: 1 + the live waiters that
    /// joined before it, by join time and then id, so every reader agrees on the order. Null for a
    /// run not waiting in a named queue. A waiter whose tman died holds no place.
    /// </summary>
    public static int? Position(RunRecord run, IEnumerable<RunRecord> all)
    {
        if (run is not { State: RunState.Queued, AdmittedUtc: null, Queue: { } queue, QueuedUtc: { } joined }) return null;
        return 1 + all.Count(o =>
            o is { State: RunState.Queued, AdmittedUtc: null, QueuedUtc: { } at }
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
        // the save is a finished record, so Store.Save touches the queue event: a waiter leaving
        // the line frees a place for whoever was behind it
        Store.Save(record);
        return null;
    }
}
