using Tman;
using Xunit;

namespace Tman.Tests;

[Collection("cwd")]
public class RunnerTests : IDisposable
{
    readonly TempDir _home = new();
    readonly string? _prevHome = Environment.GetEnvironmentVariable("TMAN_HOME");
    readonly string? _prevParent = Environment.GetEnvironmentVariable(Runner.ParentIdEnvVar);

    // The suite itself runs under tman, so the host inherits a TMAN_RUN_ID; left in place it makes
    // every run here nested, and the record's parent is the suite's own run.
    public RunnerTests()
    {
        Environment.SetEnvironmentVariable("TMAN_HOME", _home.Path);
        Environment.SetEnvironmentVariable(Runner.ParentIdEnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TMAN_HOME", _prevHome);
        Environment.SetEnvironmentVariable(Runner.ParentIdEnvVar, _prevParent);
        _home.Dispose();
    }

    static Caps StallOnly(int seconds) => new() { Stall = TimeSpan.FromSeconds(seconds) };

    [UnixFact("supervises the sleep binary and reads the POSIX state it parks in")]
    public async Task IdleProcess_InInterruptibleSleep_IsStillStalled()
    {
        // The true positive the whole guard exists for. `sleep` parks in S with no io, which is
        // exactly the state a blocked socket read shows — so widening the progress signal to S
        // would silently retire stall detection. The stderr assertion keeps this non-vacuous:
        // it fails loudly if the kill was decided against some other state.
        var err = new StringWriter();
        var prevErr = Console.Error;
        Console.SetError(err);
        int exit;
        try
        {
            exit = await Runner.RunAsync("sleep", new[] { "30" }, StallOnly(1), null, null);
        }
        finally
        {
            Console.SetError(prevErr);
        }

        Assert.Equal(Runner.ExitStalled, exit);
        // process states are read on Linux only
        if (OperatingSystem.IsLinux())
            Assert.Contains("[S]", err.ToString());
    }

    [UnixFact("the busy work is a `yes` under sh")]
    public async Task SilentBusyChild_IsNotStalled()
    {
        // The busy work happens in a descendant, so seeing it requires walking the tree; the root
        // itself only sleeps, and a root-only sample would read the run as stalled.
        var err = new StringWriter();
        var prevErr = Console.Error;
        Console.SetError(err);
        int exit;
        try
        {
            exit = await Runner.RunAsync("sh",
                new[] { "-c", "yes > /dev/null & p=$!; sleep 3; kill $p; wait $p 2>/dev/null; exit 0" },
                StallOnly(1), null, null);
        }
        finally
        {
            Console.SetError(prevErr);
        }

        Assert.True(exit == 0, $"exit={exit} stderr: {err}");
    }

    [WindowsFact("the busy work is a powershell loop under cmd")]
    public async Task SilentBusyChild_IsNotStalled_OnWindows()
    {
        // the same case as SilentBusyChild_IsNotStalled: cmd waits, its powershell child burns cpu
        var took = System.Diagnostics.Stopwatch.StartNew();
        var exit = await Runner.RunAsync("cmd.exe",
            ["/c", WindowsCommand.PowerShell("$end=(Get-Date).AddSeconds(4); while((Get-Date) -lt $end){}")],
            StallOnly(1), null, null);

        Assert.Equal(0, exit);
        // the loop really ran: a run over in under its 4s never gave the stall window a chance
        Assert.True(took.Elapsed >= TimeSpan.FromSeconds(3.5), $"took {took.Elapsed}");
    }

    /// <summary>A sampler that reports the same frozen counters every tick, differing only in state.</summary>
    static Func<int, TreeSample?> Frozen(string states) =>
        _ => new TreeSample(CpuJiffies: 100, IoBytes: 100, RssMb: 1, Procs: 1, States: states);

    [UnixFact("needs a real sleep child to hold the stall window open")]
    public async Task ChildBlockedInUninterruptibleSleep_SurvivesTheStallWindow()
    {
        // A real child, a real 1s stall window, real silence — only the sample is injected, because
        // an on-demand uninterruptible io wait cannot be manufactured. Counters are frozen, so `D`
        // is the sole reason this run may live: if the runner stops consulting the state, it dies.
        var exit = await Runner.RunAsync("sleep", new[] { "3" }, StallOnly(1), null, null,
            null, default, Frozen("D"));

        Assert.Equal(0, exit);
    }

    [UnixFact("needs a real sleep child to hold the stall window open")]
    public async Task ChildInInterruptibleSleep_IsKilledUnderTheSameFrozenCounters()
    {
        // Same child, same window, same frozen counters — only the state differs. The pair is what
        // makes the D test load-bearing: it is the state, not the injection, that spares the run.
        var exit = await Runner.RunAsync("sleep", new[] { "3" }, StallOnly(1), null, null,
            null, default, Frozen("S"));

        Assert.Equal(Runner.ExitStalled, exit);
    }

    [UnixFact("needs a real, idle sleep child as the root of the sampled tree")]
    public async Task MaxCpu_CullsOnTreeCpu_WhenTheRootIsIdle()
    {
        // The root is `sleep`, which burns nothing; every jiffy the sampler reports belongs to
        // descendants it never spawned. If the cull only consulted the root's own processor time
        // this run would end 0 after the sleep. Ten thousand jiffies a tick is 100s of cpu per
        // second, well over --max-cpu 10 on any core count this test will meet.
        long jiffies = 0;
        Func<int, TreeSample?> climbing = _ =>
            new TreeSample(CpuJiffies: jiffies += 10_000, IoBytes: 0, RssMb: 1, Procs: 4, States: "SR");

        var exit = await Runner.RunAsync("sleep", new[] { "15" }, new Caps { MaxCpuPct = 10 }, null, null,
            null, default, climbing);

        Assert.Equal(Runner.ExitCulled, exit);
        var r = Assert.Single(Store.LoadAll());
        Assert.Equal(RunState.Culled, r.State);
        Assert.Contains("max-cpu", r.KillReason);
    }

    [UnixFact("the trickle of output comes from an sh loop")]
    public async Task PartialLineOutput_CountsAsActivity()
    {
        var exit = await Runner.RunAsync("sh",
            new[] { "-c", "for i in 1 2 3 4 5 6; do printf x; sleep 0.5; done" },
            StallOnly(1), null, null);

        Assert.Equal(0, exit);
    }

    [UnixFact("supervises the sleep binary")]
    public async Task MaxTime_StillKills()
    {
        var exit = await Runner.RunAsync("sleep", new[] { "30" },
            new Caps { MaxTime = TimeSpan.FromSeconds(2) }, null, null);

        Assert.Equal(Runner.ExitTimeout, exit);
    }

    [UnixFact("the exit code comes from `sh -c`")]
    public async Task ExitCode_PassesThrough()
    {
        var exit = await Runner.RunAsync("sh", new[] { "-c", "exit 42" },
            new Caps(), null, null);

        Assert.Equal(42, exit);
    }

    [UnixFact("the recorded run is an `sh -c` child")]
    public async Task Record_CapturesCanonicalContextAndEffectiveCaps()
    {
        var caps = new Caps { Stall = TimeSpan.FromSeconds(30), MaxMemMb = 4096 };
        await Runner.RunAsync("sh", new[] { "-c", "exit 0" }, caps, "unit", null, "unit@/repo");

        var r = Assert.Single(Store.LoadAll());
        Assert.Equal(RunRecord.CurrentSchema, r.Schema);
        Assert.Equal("unit@/repo", r.Group);
        Assert.Equal(Canon.Dir(Directory.GetCurrentDirectory()), r.Cwd);
        Assert.Equal(TimeSpan.FromSeconds(30), r.Caps.Stall);
        Assert.Equal(4096, r.Caps.MaxMemMb);
        Assert.Null(r.ParentId);
        Assert.False(r.IsNested);
        Assert.True(r.IsFinished);
    }

    [UnixFact("reads the env var back out of the child through sh printf")]
    public async Task Child_IsToldWhichRunLaunchedIt()
    {
        var outw = new StringWriter();
        var prevOut = Console.Out;
        Console.SetOut(outw);
        try
        {
            await Runner.RunAsync("sh", new[] { "-c", $"printf %s \"${Runner.ParentIdEnvVar}\"" },
                new Caps(), null, null);
        }
        finally
        {
            Console.SetOut(prevOut);
        }

        var r = Assert.Single(Store.LoadAll());
        Assert.Equal(r.Id, outw.ToString());
    }

    [UnixFact("the nested run is an `sh -c` child")]
    public async Task NestedRun_RecordsItsParent()
    {
        Environment.SetEnvironmentVariable(Runner.ParentIdEnvVar, "outerrun1234");
        try
        {
            await Runner.RunAsync("sh", new[] { "-c", "exit 0" }, new Caps(), null, null);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Runner.ParentIdEnvVar, null);
        }

        var r = Assert.Single(Store.LoadAll());
        Assert.Equal("outerrun1234", r.ParentId);
        Assert.True(r.IsNested);
    }

    /// <summary>
    /// The system clock, except that its wall-clock reading jumps by <paramref name="step"/> after the
    /// first read — an NTP step or a WSL resync landing mid-run. Its monotonic timestamps are real.
    /// </summary>
    sealed class SteppedWallClock(TimeSpan step) : TimeProvider
    {
        int _reads;
        public override DateTimeOffset GetUtcNow() =>
            Interlocked.Increment(ref _reads) == 1 ? base.GetUtcNow() : base.GetUtcNow() + step;
    }

    [UnixFact("supervises the sleep binary")]
    public async Task AWallClockSteppedForward_DoesNotKillARunInsideItsDeadline()
    {
        // measured on the wall clock, a day's step made a 2s run look 24h old at its first tick
        var exit = await Runner.RunAsync("sleep", ["2"],
            new Caps { MaxTime = TimeSpan.FromSeconds(30), Stall = TimeSpan.FromSeconds(30) },
            null, null, null, default, sampler: null, clock: new SteppedWallClock(TimeSpan.FromDays(1)));

        Assert.Equal(0, exit);
    }

    [UnixFact("supervises the sleep binary")]
    public async Task AWallClockSteppedBack_StillEndsTheRunAtItsDeadline()
    {
        // measured on the wall clock, a day's step back put the deadline a day further away
        var run = Runner.RunAsync("sleep", ["30"], new Caps { MaxTime = TimeSpan.FromSeconds(1) },
            null, null, null, default, sampler: null, clock: new SteppedWallClock(TimeSpan.FromDays(-1)));

        Assert.Equal(Runner.ExitTimeout, await run.WaitAsync(TimeSpan.FromSeconds(15)));
    }

    /// <summary>Runs `sh -c script` and returns its exit, what it printed, and how long the run took.</summary>
    static async Task<(int Exit, string Out, string Err, TimeSpan Took)> Timed(string script, Caps caps)
    {
        var outw = new StringWriter();
        var errw = new StringWriter();
        var (prevOut, prevErr) = (Console.Out, Console.Error);
        Console.SetOut(outw);
        Console.SetError(errw);
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var exit = await Runner.RunAsync("sh", ["-c", script], caps, null, null).WaitAsync(TimeSpan.FromSeconds(30));
            return (exit, outw.ToString(), errw.ToString(), started.Elapsed);
        }
        finally
        {
            Console.SetOut(prevOut);
            Console.SetError(prevErr);
        }
    }

    [UnixFact("backgrounds a sleep from sh")]
    public async Task ARootThatExitsLeavingItsOutputHeld_StillFinishesTheRun()
    {
        // The review's reproduction: the root exits at once, a background sleep keeps its stdout,
        // and the runner waited on that pipe forever — past max-time, holding its slot.
        var (exit, _, _, took) = await Timed("sleep 60 & echo started", new Caps { MaxTime = TimeSpan.FromSeconds(1) });

        Assert.Equal(0, exit);
        Assert.True(took < TimeSpan.FromSeconds(15), $"took {took}");
        Assert.Equal(RunState.Exited, Assert.Single(Store.LoadAll()).State);
    }

    [LinuxFact("finds the holder through /proc/<pid>/fd")]
    public async Task ALeftoverHoldingTheRunsOutput_IsKilled()
    {
        var (_, printed, err, _) = await Timed("sleep 60 & echo $!", new Caps());
        var leftover = int.Parse(printed.Trim());

        Assert.Contains($"killing pid {leftover} (sleep)", err);
        Assert.Equal(ProcessIdentity.Gone, ProcUtil.Probe(leftover, null));
    }

    [LinuxFact("finds the holder through /proc/<pid>/fd")]
    public async Task ALeftoverThatLetGoOfTheOutput_IsLeftRunning()
    {
        // a descendant that outlives the root on purpose and does not hold its output — a compiler
        // server, a build daemon — is not what kept the run open, and is not tman's to kill
        var (exit, printed, _, took) = await Timed("sleep 60 >/dev/null 2>&1 & echo $!", new Caps());
        var leftover = int.Parse(printed.Trim());
        try
        {
            Assert.Equal(0, exit);
            Assert.True(took < TimeSpan.FromSeconds(1.5), $"took {took}: waited out the drain grace");
            Assert.Equal(ProcessIdentity.Mine, ProcUtil.Probe(leftover, null));
        }
        finally
        {
            using var p = System.Diagnostics.Process.GetProcessById(leftover);
            p.Kill();
        }
    }

    [UnixFact("supervises the sleep binary")]
    public async Task ARootThatExitsDuringATick_IsNotJudgedAtThatTick()
    {
        // the child outlives the start of the monitor loop and exits half way into its first tick;
        // that tick lands past max-time, and used to report the finished run as timed out
        var exit = await Runner.RunAsync("sleep", ["0.5"], new Caps { MaxTime = TimeSpan.FromSeconds(1) }, null, null);

        Assert.Equal(0, exit);
        Assert.Equal(RunState.Exited, Assert.Single(Store.LoadAll()).State);
    }
}
