using System.ComponentModel;
using System.Diagnostics;

namespace Tman;

public readonly record struct TreeSample(long CpuJiffies, long IoBytes, long RssMb, int Procs, string States);

/// <param name="StartTicks">
/// Field 22: start time in clock ticks after boot. Null only on a line cut short before it.
/// </param>
internal readonly record struct ProcStat(int Ppid, char State, long CpuJiffies, long RssPages, long? StartTicks);

/// <summary>
/// One sample of a run's whole process tree — the root and every descendant still parented under
/// it — on each supported platform: /proc on Linux, libproc on macOS, a Toolhelp snapshot on
/// Windows. A descendant that has left the tree (reparented to init or launchd, or orphaned on
/// Windows) is no longer counted; that is the edge of "tree" on every platform alike.
/// </summary>
public static partial class TreeStats
{
    /// <summary>Linux process state: uninterruptible sleep, i.e. blocked inside a kernel io wait.</summary>
    const char UninterruptibleSleep = 'D';

    /// <summary>
    /// Whether this tick's sample shows the tree doing work, given the previous tick's sample.
    /// This is the whole "is it alive?" question for a run that is printing nothing. Three
    /// positive facts, any one of which is read as progress:
    /// cpu jiffies advanced; io bytes advanced; or some process sits in <c>D</c>, where the
    /// kernel is servicing an io request on its behalf.
    /// <para>
    /// The two counter clauses are edge-triggered — they need movement between ticks — but the
    /// <c>D</c> clause is level-triggered: the state alone suffices, every tick, forever. So a
    /// process wedged permanently in <c>D</c> (a dead NFS server, a failing disk) is a genuine
    /// hang that <c>--stall</c> can never kill, and <c>--max-time</c> is the only bound on it.
    /// </para>
    /// <para>
    /// <c>S</c> is deliberately not a signal: `sleep 120` and a socket parked in recv() are both
    /// <c>S</c>, so reading it as activity would retire stall detection rather than sharpen it.
    /// <c>R</c> needs no clause of its own — a runnable process moves the cpu counter.
    /// Off Linux <see cref="TrySample"/> reports no states at all, so only the counters apply.
    /// </para>
    /// </summary>
    public static bool ShowsProgress(TreeSample prev, TreeSample now) =>
        now.CpuJiffies > prev.CpuJiffies ||
        now.IoBytes > prev.IoBytes ||
        now.States.Contains(UninterruptibleSleep);

    /// <summary>
    /// Samples <paramref name="rootPid"/>'s tree. False when the root itself is gone. Process states,
    /// and with them the <c>D</c> clause of <see cref="ShowsProgress"/>, are read on Linux only;
    /// elsewhere <see cref="TreeSample.States"/> is empty and the counters carry the verdict.
    /// </summary>
    public static bool TrySample(int rootPid, out TreeSample sample)
    {
        if (OperatingSystem.IsLinux()) return TrySampleLinux(rootPid, out sample);
        if (OperatingSystem.IsMacOS()) return TrySampleMacOS(rootPid, out sample);
        if (OperatingSystem.IsWindows()) return TrySampleWindows(rootPid, out sample);
        throw new PlatformNotSupportedException("tman samples process trees on Linux, macOS and Windows only");
    }

    /// <summary>
    /// <see cref="TreeSample.CpuJiffies"/> are hundredths of a second on every platform, as Linux
    /// USER_HZ is; the other samplers convert into them.
    /// </summary>
    const long TimeSpanTicksPerJiffy = TimeSpan.TicksPerSecond / 100;

    static bool TrySampleLinux(int rootPid, out TreeSample sample)
    {
        sample = default;
        var procs = new Dictionary<int, ProcStat>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
            string? text;
            try { text = File.ReadAllText(Path.Combine(dir, "stat")); }
            // gone since the listing, or not ours to read: either way not part of this tree's sample
            catch (Exception e) when (ProcUtil.VerdictFor(e) is not null) { continue; }
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
            catch (Exception e) when (ProcUtil.VerdictFor(e) is not null) { return false; }
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
        // gone mid-sample, or io counters readable only by the process's owner
        catch (Exception e) when (ProcUtil.VerdictFor(e) is not null) { return 0; }
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
        long? startTicks = rest.Length > 19 && long.TryParse(rest[19], out var st) ? st : null;
        var state = rest[0].Length > 0 ? rest[0][0] : '?';
        stat = new ProcStat(ppid, state, utime + stime + cutime + cstime, rssPages, startTicks);
        return true;
    }
}
