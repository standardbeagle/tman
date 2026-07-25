namespace Tman;

public static class Reaper
{
    public static readonly TimeSpan DefaultRetain = TimeSpan.FromHours(24);

    /// <summary>
    /// The full housekeeping pass every tman command performs: kill orphans, drop expired and
    /// unreadable records, release locks whose owner died. Old data is never left for a `tman clean`
    /// that may never be run.
    /// </summary>
    public static (List<RunRecord> Reaped, int Pruned, int LocksFreed) Sweep(TimeSpan retain, bool quiet = false)
    {
        var reaped = ReapOrphans(quiet);
        var pruned = Store.Prune(retain);
        var locksFreed = Store.PruneStaleLocks();
        return (reaped, pruned, locksFreed);
    }

    public static List<RunRecord> ReapOrphans(bool quiet = false)
    {
        var reaped = new List<RunRecord>();
        foreach (var r in Store.LoadAll())
        {
            if (r.State != RunState.Running) continue;

            var childAlive = ProcUtil.IsAlive(r.Pid) && ProcUtil.IsSameProcess(r.Pid, r.ChildStartUtc);
            var runnerAlive = r.RunnerPid == Environment.ProcessId
                || (ProcUtil.IsAlive(r.RunnerPid) && ProcUtil.IsSameProcess(r.RunnerPid, r.RunnerStartUtc));

            if (!childAlive)
            {
                r.State = RunState.Exited;
                r.HeartbeatUtc = DateTime.UtcNow;
                Store.Save(r);
                continue;
            }

            if (!runnerAlive)
            {
                if (!quiet)
                    Console.Error.WriteLine($"tman: reaping orphan pid {r.Pid} ({r.Command}, id {r.Id})");
                ProcUtil.KillTree(r.Pid);
                r.State = RunState.Reaped;
                r.KillReason = "runner died; orphan reaped";
                r.HeartbeatUtc = DateTime.UtcNow;
                Store.Save(r);
                reaped.Add(r);
            }
        }
        return reaped;
    }

    public static List<RunRecord> LiveRuns()
    {
        var live = new List<RunRecord>();
        foreach (var r in Store.LoadAll())
        {
            if (r.State != RunState.Running) continue;
            if (ProcUtil.IsAlive(r.Pid) && ProcUtil.IsSameProcess(r.Pid, r.ChildStartUtc))
                live.Add(r);
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
            .OrderByDescending(r => r.State == RunState.Running)
            .ThenByDescending(r => r.StartedUtc)
            .FirstOrDefault();

    /// <summary>Live run holding a dedup/slot bucket. See <see cref="RunKey"/>.</summary>
    public static RunRecord? FindLiveInGroup(string group) =>
        LiveRuns().FirstOrDefault(r => r.Group == group);
}
