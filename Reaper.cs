namespace Tman;

public static class Reaper
{
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
        LiveRuns().FirstOrDefault(r =>
            string.Equals(r.Name, nameOrId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(r.Id, nameOrId, StringComparison.OrdinalIgnoreCase));
}
