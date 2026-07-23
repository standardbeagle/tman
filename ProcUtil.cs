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
