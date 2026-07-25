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
    }

    [Fact]
    public void TryParseStat_TruncatedLine_TreatsRssAsZero()
    {
        // /proc/<pid>/stat can come back short under fork churn; cpu/ppid still usable
        Assert.True(TreeStats.TryParseStat("1234 (sh) S 1 2 3 4 5 6 7 8 9 10 100 50 7 3", out var st));
        Assert.Equal(0, st.RssPages);
        Assert.Equal(160, st.CpuJiffies);
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

    [Fact]
    public void CoversTree_OnlyWhereParentPidsAreCheaplyAvailable()
    {
        Assert.Equal(OperatingSystem.IsLinux(), TreeStats.CoversTree);
    }

    [Fact]
    public void TrySample_IncludesChildProcesses()
    {
        if (!TreeStats.CoversTree) return;

        using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "sleep",
            ArgumentList = { "5" },
            UseShellExecute = false,
        })!;
        try
        {
            Assert.True(TreeStats.TrySample(Environment.ProcessId, out var s));
            Assert.True(s.Procs >= 2, $"expected tree to include sleep child, got {s.Procs} proc(s)");
            Assert.Contains('S', s.States);
            Assert.True(s.RssMb > 0, "tree rss should be reported alongside the child");
        }
        finally
        {
            try { child.Kill(); } catch { }
        }
    }
}
