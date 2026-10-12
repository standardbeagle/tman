using System.ComponentModel;
using System.Diagnostics;
using Tman;
using Xunit;

namespace Tman.Tests;

public class ProcUtilTests
{
    /// <summary>A pid no OS hands out in practice: int.MaxValue - 1.</summary>
    const int UnusedPid = 2147483646;

    // PID 4 is the Windows System process: it always exists and a normal user can never open a
    // handle to it, so Process.HasExited throws Win32Exception "Access is denied". That is the shape
    // of a recorded pid that Windows has since handed to a process tman may not open, and it took
    // down every command that sweeps run records (`tman ls` included) instead of reading as gone.
    [WindowsFact("PID 4 is the protected System process only on Windows")]
    public void Identify_AProcessTmanMayNotOpen_IsNotOneOfItsRuns()
    {
        Assert.Equal(ProcessIdentity.NotMine, ProcUtil.Identify(4, DateTime.UtcNow, null));
    }

    [Fact]
    public void Identify_ThisProcessAtItsRecordedStart_IsMine()
    {
        using var self = Process.GetCurrentProcess();
        var start = self.StartTime.ToUniversalTime();

        Assert.Equal(ProcessIdentity.Mine, ProcUtil.Identify(self.Id, start, ProcUtil.OwnStart().Ticks));
    }

    [Fact]
    public void Identify_ALivePidStartedAtAnotherTime_IsNotMine()
    {
        using var self = Process.GetCurrentProcess();
        var someoneElsesStart = self.StartTime.ToUniversalTime() - TimeSpan.FromHours(1);

        Assert.Equal(ProcessIdentity.NotMine, ProcUtil.Identify(self.Id, someoneElsesStart, null));
    }

    [Theory]
    [InlineData(UnusedPid)]
    // a record whose pid defaulted to 0 names the Windows idle process, and every process on POSIX
    // to kill(2); neither is a run
    [InlineData(0)]
    [InlineData(-1)]
    public void Identify_APidNoRunHolds_IsGone(int pid)
    {
        Assert.Equal(ProcessIdentity.Gone, ProcUtil.Identify(pid, DateTime.UtcNow, null));
    }

    [Fact]
    public void VerdictFor_MapsOnlyTheFailuresTheRuntimeDocumentsForAMissingOrForeignPid()
    {
        Assert.Equal(ProcessIdentity.Gone, ProcUtil.VerdictFor(new ArgumentException("no such pid")));
        Assert.Equal(ProcessIdentity.NotMine, ProcUtil.VerdictFor(new Win32Exception(5)));
        Assert.Equal(ProcessIdentity.Gone, ProcUtil.VerdictFor(new FileNotFoundException()));
        Assert.Equal(ProcessIdentity.Gone, ProcUtil.VerdictFor(new DirectoryNotFoundException()));
        Assert.Equal(ProcessIdentity.NotMine, ProcUtil.VerdictFor(new UnauthorizedAccessException()));
        Assert.Null(ProcUtil.VerdictFor(new IOException("disk on fire")));
        Assert.Null(ProcUtil.VerdictFor(new InvalidDataException()));
        // anything else is a defect in tman or the runtime, and must surface rather than read as a verdict
        Assert.Null(ProcUtil.VerdictFor(new InvalidOperationException()));
        Assert.Null(ProcUtil.VerdictFor(new NullReferenceException()));
    }

    [LinuxFact("ESRCH surfaces as an IOException carrying the raw errno on Linux")]
    public void VerdictFor_AProcEntryThatWentWhileItWasRead_IsGone()
    {
        Assert.Equal(ProcessIdentity.Gone, ProcUtil.VerdictFor(new IOException("No such process", 3)));
    }

    static ProcStat Stat(char state, long? startTicks) => new(1, state, 0, 0, startTicks);

    [Fact]
    public void VerdictFromStat_ComparesTicksExactly()
    {
        Assert.Equal(ProcessIdentity.Mine, ProcUtil.VerdictFromStat(Stat('S', 4242), 4242));
        Assert.Equal(ProcessIdentity.NotMine, ProcUtil.VerdictFromStat(Stat('S', 4243), 4242));
    }

    [Fact]
    public void VerdictFromStat_AZombieIsGone_EvenAtItsRecordedStart()
    {
        // it exited; only its parent's wait is left, and nothing tman does can reach it
        Assert.Equal(ProcessIdentity.Gone, ProcUtil.VerdictFromStat(Stat('Z', 4242), 4242));
    }

    [Fact]
    public void VerdictFromStat_ALegacyRecordWithoutTicks_IsLeftToTheWallClock()
    {
        Assert.Null(ProcUtil.VerdictFromStat(Stat('S', 4242), null));
    }

    [Fact]
    public void VerdictFromStat_AStatLineCutShortOfItsStart_IsAnErrorNotAVerdict()
    {
        Assert.Throws<InvalidDataException>(() => ProcUtil.VerdictFromStat(Stat('S', null), 4242));
    }

    [LinuxFact("start ticks are Linux's")]
    public void Identify_OnLinux_TicksDecide_NotTheWallClock()
    {
        using var self = Process.GetCurrentProcess();
        var (startUtc, ticks) = ProcUtil.OwnStart();
        Assert.NotNull(ticks);

        // the clock stepped since the record was taken: the ticks still name this process
        Assert.Equal(ProcessIdentity.Mine, ProcUtil.Identify(self.Id, startUtc.AddSeconds(10), ticks));
        // the wall clock agrees but the ticks do not: a pid reused within the tolerance is not ours
        Assert.Equal(ProcessIdentity.NotMine, ProcUtil.Identify(self.Id, startUtc, ticks + 1));
    }

    /// <summary>
    /// The migration path: a record written before tman recorded ticks is still identified by the
    /// wall clock, and so is still exposed to a clock step until it is pruned.
    /// </summary>
    [LinuxFact("start ticks are Linux's")]
    public void Identify_OnLinux_ALegacyRecordFallsBackToTheWallClock()
    {
        using var self = Process.GetCurrentProcess();
        var (startUtc, _) = ProcUtil.OwnStart();

        Assert.Equal(ProcessIdentity.Mine, ProcUtil.Identify(self.Id, startUtc, null));
        Assert.Equal(ProcessIdentity.NotMine, ProcUtil.Identify(self.Id, startUtc.AddSeconds(10), null));
    }

    /// <summary>
    /// A zombie still answers kill(pid, 0), so Process.HasExited reads it as running. It is a run
    /// that finished, not one to keep a name or a slot for.
    /// </summary>
    [LinuxFact("process state 'Z' is read from /proc")]
    public void Identify_OnLinux_AZombieChild_IsGone()
    {
        // sh backgrounds a child, then execs into a sleep that never reaps it. The child exits only
        // once its parent IS that sleep: a child that exits first can be reaped by sh before the exec,
        // leaving no zombie to read (it did under load, 2026-10-12).
        using var parent = Process.Start(new ProcessStartInfo("sh", new[] { "-c",
            "p=$$; (while [ \"$(cat /proc/$p/comm)\" != sleep ]; do :; done) & echo $!; exec sleep 30" })
        {
            RedirectStandardOutput = true,
        }) ?? throw new IOException("could not start sh");
        try
        {
            var zombie = int.Parse(parent.StandardOutput.ReadLine()!);
            ProcStat stat = default;
            for (var i = 0; i < 200 && stat.State != 'Z'; i++)
            {
                Thread.Sleep(10);
                Assert.True(TreeStats.TryParseStat(File.ReadAllText($"/proc/{zombie}/stat"), out stat));
            }
            Assert.Equal('Z', stat.State);

            Assert.Equal(ProcessIdentity.Gone, ProcUtil.Identify(zombie, DateTime.UtcNow, stat.StartTicks));
        }
        finally
        {
            parent.Kill();
            parent.WaitForExit();
        }
    }

    /// <summary>A child that outlives any test: a sleep on POSIX, a ping loop on Windows.</summary>
    static Process LongChild() =>
        (OperatingSystem.IsWindows()
            ? Process.Start(new ProcessStartInfo("ping", new[] { "-n", "60", "127.0.0.1" }) { RedirectStandardOutput = true })
            : Process.Start("sleep", "60"))
        ?? throw new IOException("could not start a long-running child");

    [Fact]
    public void KillTree_TheRecordedProcess_IsKilled()
    {
        using var child = LongChild();
        var (startUtc, ticks) = ProcUtil.StartStamp(child);

        Assert.True(ProcUtil.KillTree(child.Id, startUtc, ticks));

        Assert.True(child.WaitForExit(10_000));
    }

    [Fact]
    public void KillTree_APidNowNamingAnotherProcess_IsLeftAlone()
    {
        using var child = LongChild();
        try
        {
            // the record says the pid's process started an hour earlier: the OS has handed the pid on
            var (startUtc, ticks) = ProcUtil.StartStamp(child);

            Assert.True(ProcUtil.KillTree(child.Id, startUtc - TimeSpan.FromHours(1), ticks - 1));

            Assert.False(child.WaitForExit(200));
        }
        finally
        {
            child.Kill();
            child.WaitForExit();
        }
    }

    [WindowsFact("PID 4 is the protected System process only on Windows")]
    public void KillTree_AProcessTmanMayNotOpen_IsLeftAloneWithoutThrowing()
    {
        Assert.True(ProcUtil.KillTree(4, DateTime.UtcNow, null));
    }

    [Fact]
    public void KillTree_APidNoRunHolds_IsAlreadyDone()
    {
        Assert.True(ProcUtil.KillTree(UnusedPid, DateTime.UtcNow, null));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1.9, true)]
    [InlineData(-1.9, true)]
    [InlineData(2, false)]
    [InlineData(-10, false)]
    public void MatchesWallClock_ToleratesTheRecomputationJitterOnly(double offsetSeconds, bool same)
    {
        var recorded = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(same, ProcUtil.MatchesWallClock(recorded.AddSeconds(offsetSeconds), recorded));
    }
}
