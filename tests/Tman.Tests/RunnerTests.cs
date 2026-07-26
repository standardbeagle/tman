using Tman;
using Xunit;

namespace Tman.Tests;

[Collection("cwd")]
public class RunnerTests : IDisposable
{
    static bool Unix => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    readonly TempDir _home = new();
    readonly string? _prevHome = Environment.GetEnvironmentVariable("TMAN_HOME");

    public RunnerTests() => Environment.SetEnvironmentVariable("TMAN_HOME", _home.Path);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TMAN_HOME", _prevHome);
        _home.Dispose();
    }

    static Caps StallOnly(int seconds) => new() { Stall = TimeSpan.FromSeconds(seconds) };

    [Fact]
    public async Task IdleProcess_InInterruptibleSleep_IsStillStalled()
    {
        // The true positive the whole guard exists for. `sleep` parks in S with no io, which is
        // exactly the state a blocked socket read shows — so widening the progress signal to S
        // would silently retire stall detection. The stderr assertion keeps this non-vacuous:
        // it fails loudly if the kill was decided against some other state.
        if (!Unix) return;

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

    [Fact]
    public async Task SilentBusyChild_IsNotStalled()
    {
        // The busy work happens in a grandchild, so seeing it requires walking the tree.
        // Where tman cannot do that, silence is all it has to go on and the kill is correct.
        if (!Unix || !TreeStats.CoversTree) return;

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

    [Fact]
    public async Task PartialLineOutput_CountsAsActivity()
    {
        if (!Unix) return;

        var exit = await Runner.RunAsync("sh",
            new[] { "-c", "for i in 1 2 3 4 5 6; do printf x; sleep 0.5; done" },
            StallOnly(1), null, null);

        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task MaxTime_StillKills()
    {
        if (!Unix) return;

        var exit = await Runner.RunAsync("sleep", new[] { "30" },
            new Caps { MaxTime = TimeSpan.FromSeconds(2) }, null, null);

        Assert.Equal(Runner.ExitTimeout, exit);
    }

    [Fact]
    public async Task ExitCode_PassesThrough()
    {
        if (!Unix) return;

        var exit = await Runner.RunAsync("sh", new[] { "-c", "exit 42" },
            new Caps(), null, null);

        Assert.Equal(42, exit);
    }

    [Fact]
    public async Task Record_CapturesCanonicalContextAndEffectiveCaps()
    {
        if (!Unix) return;

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

    [Fact]
    public async Task Child_IsToldWhichRunLaunchedIt()
    {
        if (!Unix) return;

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

    [Fact]
    public async Task NestedRun_RecordsItsParent()
    {
        if (!Unix) return;

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
