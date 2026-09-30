using System.Diagnostics;
using Tman;
using Xunit;

namespace Tman.Tests;

/// <summary>
/// `tman probe` answers one question for a caller that holds a pid it recorded earlier: is that
/// still the process I recorded? It is the same identity check the reaper uses on run records, so a
/// caller keeping its own step record can discount a dead or pid-reused step instead of blocking on
/// it. The verdict is the exit code, so a shell caller needs no parsing: 0 mine, 1 gone, 3 not mine.
/// </summary>
[Collection("cwd")]
public class ProbeTests : IDisposable
{
    const int UnusedPid = 2147483646;

    readonly TempDir _home = new();
    readonly string? _prevHome = Environment.GetEnvironmentVariable("TMAN_HOME");

    public ProbeTests() => Environment.SetEnvironmentVariable("TMAN_HOME", _home.Path);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TMAN_HOME", _prevHome);
        _home.Dispose();
    }

    static async Task<(int Exit, string Out, string Err)> Run(params string[] argv)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var prevOut = Console.Out;
        var prevErr = Console.Error;
        Console.SetOut(o);
        Console.SetError(e);
        try { return (await Program.Main(argv), o.ToString().Trim(), e.ToString()); }
        finally { Console.SetOut(prevOut); Console.SetError(prevErr); }
    }

    static string Pid => Environment.ProcessId.ToString();

    [Fact]
    public void Probe_ThisProcessWithNoTicks_IsMine() =>
        Assert.Equal(ProcessIdentity.Mine, ProcUtil.Probe(Environment.ProcessId, null));

    [LinuxFact("start ticks are recorded on Linux only")]
    public void Probe_ThisProcessAtItsRecordedTicks_IsMine() =>
        Assert.Equal(ProcessIdentity.Mine, ProcUtil.Probe(Environment.ProcessId, ProcUtil.OwnStart().Ticks));

    [LinuxFact("start ticks are recorded on Linux only")]
    public void Probe_ALivePidAtOtherTicks_IsNotMine() =>
        Assert.Equal(ProcessIdentity.NotMine, ProcUtil.Probe(Environment.ProcessId, ProcUtil.OwnStart().Ticks!.Value + 1));

    [Theory]
    [InlineData(UnusedPid)]
    [InlineData(0)]
    [InlineData(-1)]
    public void Probe_APidNoProcessHolds_IsGone(int pid) =>
        Assert.Equal(ProcessIdentity.Gone, ProcUtil.Probe(pid, null));

    [Fact]
    public async Task Command_LiveProcess_ExitsZeroAndSaysMine()
    {
        var r = await Run("probe", "--pid", Pid);
        Assert.Equal(0, r.Exit);
        Assert.Equal("mine", r.Out);
    }

    [Fact]
    public async Task Command_DeadPid_ExitsOneAndSaysGone()
    {
        var r = await Run("probe", "--pid", UnusedPid.ToString());
        Assert.Equal(1, r.Exit);
        Assert.Equal("gone", r.Out);
    }

    [LinuxFact("start ticks are recorded on Linux only")]
    public async Task Command_ReusedPid_ExitsThreeAndSaysNotMine()
    {
        var wrong = (ProcUtil.OwnStart().Ticks!.Value + 1).ToString();
        var r = await Run("probe", "--pid", Pid, "--start-ticks", wrong);
        Assert.Equal(3, r.Exit);
        Assert.Equal("not-mine", r.Out);
    }

    [Fact]
    public async Task Command_ExitedChildAtItsRecordedStart_IsGone()
    {
        // alive until its start is read: a child that exits on its own can be reaped, and its /proc
        // entry gone, before StartStamp gets there
        using var child = Process.Start(OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd", "/c exit 0") { CreateNoWindow = true }
            : new ProcessStartInfo("sleep", "30"))!;
        var ticks = OperatingSystem.IsLinux() ? ProcUtil.StartStamp(child).Ticks : null;
        var pid = child.Id;
        if (!OperatingSystem.IsWindows()) child.Kill();
        child.WaitForExit();
        var argv = ticks is { } t
            ? new[] { "probe", "--pid", pid.ToString(), "--start-ticks", t.ToString() }
            : new[] { "probe", "--pid", pid.ToString() };
        var r = await Run(argv);
        Assert.Equal(1, r.Exit);
        Assert.Equal("gone", r.Out);
    }

    [Theory]
    [InlineData]                                           // no pid
    [InlineData("--pid", "abc")]                           // not a number
    [InlineData("--pid", "1", "--start-ticks", "x")]       // ticks not a number
    [InlineData("--pid", "1", "--bogus")]                  // unknown flag refuses, never skipped
    public async Task Command_BadArguments_RefuseWithUsage(params string[] args)
    {
        var r = await Run(new[] { "probe" }.Concat(args).ToArray());
        Assert.Equal(Runner.ExitNotFound, r.Exit);
        Assert.Contains("usage: tman probe --pid <pid> [--start-ticks <ticks>]", r.Err);
    }

    [Fact]
    public async Task Help_ListsProbe()
    {
        var r = await Run("--help");
        Assert.Contains("tman probe --pid", r.Out);
    }
}
