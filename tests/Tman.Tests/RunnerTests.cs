using Tman;
using Xunit;

namespace Tman.Tests;

[Collection("cwd")]
public class RunnerTests : IDisposable
{
    readonly TempDir _home = new();
    readonly string? _prevHome = Environment.GetEnvironmentVariable("TMAN_HOME");

    public RunnerTests() => Environment.SetEnvironmentVariable("TMAN_HOME", _home.Path);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TMAN_HOME", _prevHome);
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
        if (TreeStats.CoversTree)
            Assert.Contains("[S]", err.ToString());
    }

    [TreeSamplingFact("the busy work is a grandchild, so only tree-wide sampling can see it")]
    public async Task SilentBusyChild_IsNotStalled()
    {
        // The busy work happens in a grandchild, so seeing it requires walking the tree.
        // Where tman cannot do that, silence is all it has to go on and the kill is correct.
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
}
