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
        // Sampled at a dedicated busy process, not at the test host: the host's tree holds every
        // child the parallel suite spawns, and one of them exiting between samples made the
        // host's cpu go backwards on Windows (706 -> 484) with nothing wrong in the sampler.
        var (shell, busy) = ShellWithAnnouncedBusyChild();
        using (shell)
        using (busy)
        {
            try
            {
                Assert.True(TreeStats.TrySample(busy.Id, out var before), "the busy child vanished");
                Thread.Sleep(400);
                Assert.True(TreeStats.TrySample(busy.Id, out var after), "the busy child vanished");
                Assert.True(after.CpuJiffies > before.CpuJiffies,
                    $"expected cpu jiffies to advance ({before.CpuJiffies} -> {after.CpuJiffies})");
            }
            finally
            {
                shell.Kill(entireProcessTree: true);
            }
        }
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
            ? new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                ArgumentList = { "/c", WindowsCommand.PowerShell("while($true){}") }, WorkingDirectory = StableCwd,
            }
            : new System.Diagnostics.ProcessStartInfo("sh") { ArgumentList = { "-c", "yes > /dev/null & wait" }, WorkingDirectory = StableCwd })!;

    /// <summary>
    /// Where a spawned shell starts. This class is outside the "cwd" collection, so it runs beside
    /// tests that change the process cwd to a temp dir and delete it; a shell inheriting that cwd
    /// can find it gone, and on Windows its powershell child then dies at startup.
    /// </summary>
    static readonly string StableCwd = AppContext.BaseDirectory;

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

    /// <summary>
    /// A shell whose busy child announces its own pid on stdout, so a test can read the child's cpu
    /// straight from the OS and compare the tree sample against it.
    /// </summary>
    static (System.Diagnostics.Process Shell, System.Diagnostics.Process Busy) ShellWithAnnouncedBusyChild()
    {
        var start = OperatingSystem.IsWindows()
            ? new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                ArgumentList = { "/c", WindowsCommand.PowerShell("[Console]::Out.WriteLine($PID); [Console]::Out.Flush(); while($true){}") },
            }
            : new System.Diagnostics.ProcessStartInfo("sh") { ArgumentList = { "-c", "yes > /dev/null & echo $!; wait" } };
        start.WorkingDirectory = StableCwd;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        var shell = System.Diagnostics.Process.Start(start)!;
        var stderr = shell.StandardError.ReadToEndAsync();
        var line = shell.StandardOutput.ReadLine();
        if (!int.TryParse(line, out var busyPid))
        {
            shell.Kill(entireProcessTree: true);
            shell.WaitForExit(5000);
            throw new InvalidOperationException(
                $"the busy child announced no pid (got {line ?? "EOF"}; shell exit {shell.ExitCode}; stderr: {stderr.Result.Trim()})");
        }
        return (shell, System.Diagnostics.Process.GetProcessById(busyPid));
    }

    static long CpuJiffiesOf(System.Diagnostics.Process p)
    {
        p.Refresh();
        return p.TotalProcessorTime.Ticks / TimeSpan.TicksPerMillisecond / 10;
    }

    [Fact]
    public void TrySample_MetersADescendantsCpuInHundredthsOfASecond()
    {
        // The tree's cpu is judged against the busy descendant's own cpu as the OS reports it, not
        // against the wall clock: on a 2-vCPU CI runner running the suite in parallel, a busy loop
        // gets a fraction of a core, and a wall-clock floor failed there (38 jiffies against 60)
        // with nothing wrong in the sampler. The descendant's own figure moves with the starvation;
        // a unit slip -- macOS rusage in mach ticks on Apple silicon (41x), Windows 100ns ticks
        // left unconverted (100000x) -- still lands far outside the band.
        var (shell, busy) = ShellWithAnnouncedBusyChild();
        using (shell)
        using (busy)
        {
            try
            {
                SampleOnceTheTreeHas(shell.Id, procs: 2);
                Assert.True(TreeStats.TrySample(shell.Id, out var before),
                    $"the root vanished before the window (exited: {shell.HasExited})");
                var busyBefore = CpuJiffiesOf(busy);
                Thread.Sleep(2000);
                var busyAfter = CpuJiffiesOf(busy);
                Assert.True(TreeStats.TrySample(shell.Id, out var after),
                    $"the root vanished in the window (exited: {shell.HasExited})");

                var busyJiffies = busyAfter - busyBefore;
                Assert.True(busyJiffies >= 10, $"the busy child barely ran ({busyJiffies} jiffies in 2s)");
                // the tree window encloses the child's, and the idle shell adds next to nothing
                var tree = after.CpuJiffies - before.CpuJiffies;
                Assert.InRange(tree, (long)(busyJiffies * 0.9) - 3, (long)(busyJiffies * 1.25) + 25);
            }
            finally
            {
                shell.Kill(entireProcessTree: true);
            }
        }
    }
}
