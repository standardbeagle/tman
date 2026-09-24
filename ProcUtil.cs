using System.Diagnostics;

namespace Tman;

public static class ProcUtil
{
    public static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        // The pid exists but belongs to a process we may not open (recycled by an elevated or
        // protected process). It is alive; IsSameProcess cannot read its start time and so
        // reports it is not ours, which is what keeps the reaper from ever killing it. Letting
        // this throw took down every tman command, since each one sweeps first.
        catch (System.ComponentModel.Win32Exception) { return true; }
    }

    public static DateTime? StartTimeUtc(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.StartTime.ToUniversalTime();
        }
        catch { return null; }
    }

    public static bool IsSameProcess(int pid, DateTime expectedStartUtc)
    {
        var st = StartTimeUtc(pid);
        if (st is null) return false;
        return Math.Abs((st.Value - expectedStartUtc).TotalSeconds) < 2;
    }

    public static bool TryRefresh(int pid, out Process? proc)
    {
        proc = null;
        try
        {
            var p = Process.GetProcessById(pid);
            if (p.HasExited) { p.Dispose(); return false; }
            p.Refresh();
            proc = p;
            return true;
        }
        catch { return false; }
    }

    public static void KillTree(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch { }
    }
}
