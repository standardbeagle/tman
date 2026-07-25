using System.Diagnostics;

namespace Tman;

public readonly record struct TreeSample(long CpuJiffies, long IoBytes, int Procs, string States);

internal readonly record struct ProcStat(int Ppid, char State, long CpuJiffies);

public static class TreeStats
{
    public static bool TrySample(int rootPid, out TreeSample sample) =>
        OperatingSystem.IsLinux() ? TrySampleLinux(rootPid, out sample) : TrySampleRootOnly(rootPid, out sample);

    static bool TrySampleRootOnly(int rootPid, out TreeSample sample)
    {
        sample = default;
        if (!ProcUtil.TryRefresh(rootPid, out var p) || p is null) return false;
        using (p)
        {
            long cpu = 0;
            try { cpu = (long)(p.TotalProcessorTime.TotalSeconds * 100); } catch { }
            sample = new TreeSample(cpu, 0, 1, "");
            return true;
        }
    }

    static bool TrySampleLinux(int rootPid, out TreeSample sample)
    {
        sample = default;
        var procs = new Dictionary<int, ProcStat>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
            string? text;
            try { text = File.ReadAllText(Path.Combine(dir, "stat")); }
            catch { continue; }
            if (!TryParseStat(text, out var st)) continue;
            procs[pid] = st;
        }
        if (!procs.ContainsKey(rootPid))
        {
            // /proc scans can transiently miss a live pid under fork churn; retry directly
            try
            {
                var text = File.ReadAllText($"/proc/{rootPid}/stat");
                if (!TryParseStat(text, out var st)) return false;
                procs[rootPid] = st;
            }
            catch { return false; }
        }

        var byPpid = new Dictionary<int, List<int>>();
        foreach (var (pid, st) in procs)
        {
            if (!byPpid.TryGetValue(st.Ppid, out var list)) byPpid[st.Ppid] = list = new List<int>();
            list.Add(pid);
        }

        long cpu = 0, io = 0;
        var count = 0;
        var states = new List<char>();
        var stack = new Stack<int>();
        stack.Push(rootPid);
        while (stack.Count > 0)
        {
            var pid = stack.Pop();
            if (!procs.TryGetValue(pid, out var st)) continue;
            count++;
            cpu += st.CpuJiffies;
            if (!states.Contains(st.State)) states.Add(st.State);
            io += ReadIoBytes(pid);
            if (byPpid.TryGetValue(pid, out var children))
                foreach (var c in children) stack.Push(c);
        }

        states.Sort();
        sample = new TreeSample(cpu, io, count, new string(states.ToArray()));
        return true;
    }

    static long ReadIoBytes(int pid)
    {
        try
        {
            long total = 0;
            foreach (var line in File.ReadLines($"/proc/{pid}/io"))
                if (line.StartsWith("rchar:", StringComparison.Ordinal) ||
                    line.StartsWith("wchar:", StringComparison.Ordinal))
                    total += long.Parse(line.AsSpan(6));
            return total;
        }
        catch { return 0; }
    }

    internal static bool TryParseStat(string text, out ProcStat stat)
    {
        stat = default;
        var close = text.LastIndexOf(')');
        if (close < 0) return false;
        var rest = text[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (rest.Length < 15) return false;
        if (!int.TryParse(rest[1], out var ppid)) return false;
        if (!long.TryParse(rest[11], out var utime)) return false;
        if (!long.TryParse(rest[12], out var stime)) return false;
        long.TryParse(rest[13], out var cutime);
        long.TryParse(rest[14], out var cstime);
        var state = rest[0].Length > 0 ? rest[0][0] : '?';
        stat = new ProcStat(ppid, state, utime + stime + cutime + cstime);
        return true;
    }
}
