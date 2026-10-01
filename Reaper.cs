namespace Tman;

public static class Reaper
{
    public static readonly TimeSpan DefaultRetain = TimeSpan.FromHours(24);

    /// <summary>
    /// The full housekeeping pass every tman command performs: kill orphans, drop expired and
    /// unreadable records. Old data is never left for a `tman clean` that may never be run. Lock
    /// files are not part of it — see the ownership invariant in <see cref="Store"/> for why one is
    /// never removed once created.
    /// </summary>
    public static (List<RunRecord> Reaped, int Pruned) Sweep(TimeSpan retain, bool quiet = false)
    {
        var reaped = ReapOrphans(quiet);
        var pruned = Store.Prune(retain);
        return (reaped, pruned);
    }

    public static List<RunRecord> ReapOrphans(bool quiet = false)
    {
        var reaped = new List<RunRecord>();
        foreach (var r in Store.LoadAll())
        {
            if (r.IsFinished) continue;
            // a live runner owns its record, outcome included: it saves the copy it holds every
            // tick, so an outcome written here would be lost to its next save
            if (RunnerAlive(r)) continue;

            if (r.State == RunState.Queued)
            {
                // a waiter has no child: nothing to kill, only an outcome nobody else will write
                r.State = RunState.Killed;
                r.KillReason = Store.ReadKillRequest(r.Id) ?? "runner died while queued";
                r.HeartbeatUtc = DateTime.UtcNow;
                Store.Save(r);
                continue;
            }

            if (ProcUtil.Identify(r.Pid, r.ChildStartUtc, r.ChildStartTicks) != ProcessIdentity.Mine)
            {
                // the child is gone and its runner never wrote the outcome: nobody read the exit
                // status, so this is the same unknown the Runner reports, not a finished run —
                // unless someone killed it on purpose and said why
                r.State = RunState.Killed;
                r.KillReason = Store.ReadKillRequest(r.Id) ?? Runner.ExitStatusUnknownReason;
                r.HeartbeatUtc = DateTime.UtcNow;
                Store.Save(r);
                continue;
            }

            if (!quiet)
                Console.Error.WriteLine($"tman: reaping orphan pid {r.Pid} ({r.Command}, id {r.Id})");
            // part of the tree survived and said why on stderr: the record stays running, so the
            // next sweep finds whatever is left rather than a run marked reaped that is not
            if (!ProcUtil.KillTree(r.Pid, r.ChildStartUtc, r.ChildStartTicks)) continue;
            r.State = RunState.Reaped;
            r.KillReason = "runner died; orphan reaped";
            r.HeartbeatUtc = DateTime.UtcNow;
            Store.Save(r);
            reaped.Add(r);
        }
        return reaped;
    }

    internal static bool RunnerAlive(RunRecord r) =>
        r.RunnerPid == Environment.ProcessId
        || ProcUtil.Identify(r.RunnerPid, r.RunnerStartUtc, r.RunnerStartTicks) == ProcessIdentity.Mine;

    /// <summary>
    /// Kills a run's tree and has it recorded as killed for <paramref name="reason"/>. The request is
    /// left before the kill, so a live runner that sees its child die already knows why and writes
    /// the outcome itself; only a run whose runner is gone is finished here. False when part of the
    /// tree could not be killed, which is said on stderr.
    /// </summary>
    public static bool KillRun(RunRecord r, string reason)
    {
        Store.RequestKill(r.Id, reason);
        if (!ProcUtil.KillTree(r.Pid, r.ChildStartUtc, r.ChildStartTicks)) return false;
        if (RunnerAlive(r)) return true;
        r.State = RunState.Killed;
        r.KillReason = reason;
        r.HeartbeatUtc = DateTime.UtcNow;
        Store.Save(r);
        return true;
    }

    /// <summary>Runs that are under way: a running child that is still the one recorded, or a waiter still waiting.</summary>
    public static List<RunRecord> LiveRuns()
    {
        var live = new List<RunRecord>();
        foreach (var r in Store.LoadAll())
        {
            var alive = r.State switch
            {
                RunState.Running => ProcUtil.Identify(r.Pid, r.ChildStartUtc, r.ChildStartTicks) == ProcessIdentity.Mine,
                RunState.Queued => RunnerAlive(r),
                _ => false,
            };
            if (alive) live.Add(r);
        }
        return live;
    }

    public static RunRecord? FindLiveByNameOrId(string nameOrId) =>
        LiveRuns().FirstOrDefault(r => r.Matches(nameOrId));

    /// <summary>
    /// Resolves a name, id, or id prefix against every record, not just live ones — a run's detail is
    /// most often wanted right after it failed, when it is no longer live. Live runs win, then the
    /// most recent, so a name reused across runs resolves to the one the user means.
    /// </summary>
    public static RunRecord? Resolve(string nameOrId) =>
        Store.LoadAll()
            .Where(r => r.Matches(nameOrId))
            .OrderByDescending(r => !r.IsFinished)
            .ThenByDescending(r => r.StartedUtc)
            .FirstOrDefault();

    /// <summary>Live run holding a dedup/slot bucket. See <see cref="RunKey"/>.</summary>
    public static RunRecord? FindLiveInGroup(string group) =>
        LiveRuns().FirstOrDefault(r => r.Group == group);
}
