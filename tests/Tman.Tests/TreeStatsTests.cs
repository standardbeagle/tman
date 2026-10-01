using Tman;
using Xunit;

namespace Tman.Tests;

public class TreeStatsTests
{
    [Fact]
    public void TryParseStat_HandlesCommWithSpacesAndParens()
    {
        var text = "1234 (weird ) name) S 1 2 3 4 5 6 7 8 9 10 100 50 7 3 18 19 20 0 21 22 23 24";

        var ok = TreeStats.TryParseStat(text, out var st);

        Assert.True(ok);
        Assert.Equal(1, st.Ppid);
        Assert.Equal('S', st.State);
        Assert.Equal(160, st.CpuJiffies);
        Assert.Equal(23, st.RssPages);
        Assert.Equal(21, st.StartTicks);
    }

    [Fact]
    public void TryParseStat_TruncatedLine_TreatsRssAsZero()
    {
        // /proc/<pid>/stat can come back short under fork churn; cpu/ppid still usable
        Assert.True(TreeStats.TryParseStat("1234 (sh) S 1 2 3 4 5 6 7 8 9 10 100 50 7 3", out var st));
        Assert.Equal(0, st.RssPages);
        Assert.Equal(160, st.CpuJiffies);
        Assert.Null(st.StartTicks);
    }

    [Fact]
    public void TryParseStat_RejectsGarbage()
    {
        Assert.False(TreeStats.TryParseStat("", out _));
        Assert.False(TreeStats.TryParseStat("not a stat line", out _));
        Assert.False(TreeStats.TryParseStat("1234 (comm) S notanumber", out _));
    }

    [Fact]
    public void TrySample_CurrentProcess_Succeeds()
    {
        var ok = TreeStats.TrySample(Environment.ProcessId, out var s);

        Assert.True(ok);
        Assert.True(s.Procs >= 1);
        Assert.True(s.RssMb > 0, "expected a live process tree to report nonzero rss");
    }

    [Fact]
    public void TrySample_CpuBurn_AdvancesJiffies()
    {
        Assert.True(TreeStats.TrySample(Environment.ProcessId, out var before));

        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(400);
        long x = 0;
        while (DateTime.UtcNow < deadline) x++;

        Assert.True(TreeStats.TrySample(Environment.ProcessId, out var after));
        Assert.True(after.CpuJiffies > before.CpuJiffies,
            $"expected cpu jiffies to advance ({before.CpuJiffies} -> {after.CpuJiffies})");
    }

    // A tick's progress verdict is pure, so it is exercised with synthetic samples rather than by
    // manufacturing kernel states. TrySample_PopulatesStates_ForTheProgressVerdict keeps these
    // honest by pinning that a real sample actually carries the States they read.
    static TreeSample Tick(string states, long cpu = 100, long io = 500) =>
        new(cpu, io, 8, 1, states);

    [Fact]
    public void ShowsProgress_UninterruptibleSleep_IsProgress()
    {
        // D = the kernel is servicing an io request for this process, so a quiet tick is not
        // evidence of a hang and --stall must not kill for it. It is not evidence of health
        // either: a process wedged in D forever (dead NFS server, failing disk) is a real hang
        // that only --max-time bounds. See TreeStats.ShowsProgress for why that is accepted.
        Assert.True(TreeStats.ShowsProgress(Tick("S"), Tick("D")));
    }

    [Fact]
    public void ShowsProgress_UninterruptibleSleepAnywhereInTheTree_IsProgress()
    {
        Assert.True(TreeStats.ShowsProgress(Tick("SS"), Tick("DS")));
    }

    [Fact]
    public void ShowsProgress_InterruptibleSleepAlone_IsNotProgress()
    {
        // `sleep 120` and a socket parked in recv() are both S. Reading S as activity would
        // disable stall detection outright, so it stays a non-signal.
        Assert.False(TreeStats.ShowsProgress(Tick("S"), Tick("S")));
    }

    [Fact]
    public void ShowsProgress_RunnableAlone_IsNotProgress()
    {
        // R is already accounted for by the cpu counter; without counter movement it adds nothing.
        Assert.False(TreeStats.ShowsProgress(Tick("S"), Tick("R")));
    }

    [Fact]
    public void ShowsProgress_CountersAdvancing_IsProgress()
    {
        Assert.True(TreeStats.ShowsProgress(Tick("S", cpu: 100), Tick("S", cpu: 101)));
        Assert.True(TreeStats.ShowsProgress(Tick("S", io: 500), Tick("S", io: 501)));
    }

    [Fact]
    public void ShowsProgress_WithoutStates_FallsBackToCountersAlone()
    {
        // Off Linux a sample carries no states at all; the D signal must simply be unavailable
        // there rather than silently reading as progress.
        Assert.False(TreeStats.ShowsProgress(Tick(""), Tick("")));
        Assert.True(TreeStats.ShowsProgress(Tick("", cpu: 100), Tick("", cpu: 101)));
    }

    [LinuxFact("process states are read out of /proc, and only there")]
    public void TrySample_PopulatesStates_ForTheProgressVerdict()
    {
        Assert.True(TreeStats.TrySample(Environment.ProcessId, out var s));
        Assert.NotEqual("", s.States);
        foreach (var c in s.States)
            Assert.True("RSDZTtWXxKPI".Contains(c), $"unexpected proc state '{c}' in \"{s.States}\"");
    }

    /// <summary>
    /// A shell whose only work is a descendant: the shell itself waits, so everything the sample
    /// sees beyond one idle process it found by walking the tree.
    /// </summary>
    static System.Diagnostics.Process ShellWithBusyChild() => System.Diagnostics.Process.Start(
        OperatingSystem.IsWindows()
            ? new System.Diagnostics.ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "powershell -NoProfile -Command \"while($true){}\"" } }
            : new System.Diagnostics.ProcessStartInfo("sh") { ArgumentList = { "-c", "yes > /dev/null & wait" } })!;

    static TreeSample SampleOnceTheTreeHas(int rootPid, int procs)
    {
        for (var i = 0; i < 100; i++)
        {
            Assert.True(TreeStats.TrySample(rootPid, out var s), "the root vanished");
            if (s.Procs >= procs) return s;
            Thread.Sleep(50);
        }
        throw new TimeoutException($"the tree under {rootPid} never reached {procs} processes");
    }

    [Fact]
    public void TrySample_IncludesTheRootsDescendants()
    {
        using var shell = ShellWithBusyChild();
        try
        {
            var s = SampleOnceTheTreeHas(shell.Id, procs: 2);
            Assert.True(s.RssMb > 0, "tree rss should be reported alongside the child");
            if (OperatingSystem.IsLinux()) Assert.Contains('S', s.States);
            else Assert.Equal("", s.States);
        }
        finally
        {
            shell.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void TrySample_MetersADescendantsCpuInHundredthsOfASecond()
    {
        // Bounded on both sides: a busy descendant shows real cpu, and no more than the machine has.
        // A unit slip — macOS rusage times are mach ticks, not nanoseconds, on Apple silicon — lands
        // well outside the window either way.
        using var shell = ShellWithBusyChild();
        try
        {
            var before = SampleOnceTheTreeHas(shell.Id, procs: 2);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Thread.Sleep(1500);
            Assert.True(TreeStats.TrySample(shell.Id, out var after));
            var seconds = clock.Elapsed.TotalSeconds;

            var jiffies = after.CpuJiffies - before.CpuJiffies;
            Assert.InRange(jiffies, (long)(seconds * 100 * 0.3), (long)(seconds * 100 * Environment.ProcessorCount * 1.2) + 10);
        }
        finally
        {
            shell.Kill(entireProcessTree: true);
        }
    }
}
