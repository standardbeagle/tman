using System.Diagnostics;

namespace Tman;

public readonly record struct TreeSample(long CpuJiffies, long IoBytes, long RssMb, int Procs, string States);

internal readonly record struct ProcStat(int Ppid, char State, long CpuJiffies, long RssPages);

public static class TreeStats
{
    /// <summary>
    /// True when a sample covers the whole process tree. Only Linux exposes parent pids and
    /// per-process io counters cheaply (/proc); elsewhere a sample sees the root process alone,
    /// so work happening in a descendant is invisible and must not be read as idleness.
    /// </summary>
    public static bool CoversTree => OperatingSystem.IsLinux();

    /// <summary>
    /// Whether this tick's sample shows the tree doing work, given the previous tick's sample.
    /// This is the whole "is it alive?" question for a run that is printing nothing.
    /// </summary>
    public static bool ShowsProgress(TreeSample prev, TreeSample now) =>
        now.CpuJiffies > prev.CpuJiffies ||
        now.IoBytes > prev.IoBytes;

    public static bool TrySample(int rootPid, out TreeSample sample) =>
        CoversTree ? TrySampleLinux(rootPid, out sample) : TrySampleRootOnly(rootPid, out sample);

    static bool TrySampleRootOnly(int rootPid, out TreeSample sample)
    {
        sample = default;
        if (!ProcUtil.TryRefresh(rootPid, out var p) || p is null) return false;
        using (p)
        {
            long cpu = 0;
            try { cpu = (long)(p.TotalProcessorTime.TotalSeconds * 100); } catch { }
            long rssMb = 0;
            try { rssMb = p.WorkingSet64 / (1024 * 1024); } catch { }
            sample = new TreeSample(cpu, 0, rssMb, 1, "");
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

        long cpu = 0, io = 0, rssPages = 0;
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
            rssPages += st.RssPages;
            if (!states.Contains(st.State)) states.Add(st.State);
            io += ReadIoBytes(pid);
            if (byPpid.TryGetValue(pid, out var children))
                foreach (var c in children) stack.Push(c);
        }

        states.Sort();
        var rssMb = rssPages * Environment.SystemPageSize / (1024 * 1024);
        sample = new TreeSample(cpu, io, rssMb, count, new string(states.ToArray()));
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
        // field 24 (rss, in pages) — absent on truncated /proc reads, so treat as zero rather than fail
        long rssPages = 0;
        if (rest.Length > 21) long.TryParse(rest[21], out rssPages);
        var state = rest[0].Length > 0 ? rest[0][0] : '?';
        stat = new ProcStat(ppid, state, utime + stime + cutime + cstime, rssPages);
        return true;
    }
}
