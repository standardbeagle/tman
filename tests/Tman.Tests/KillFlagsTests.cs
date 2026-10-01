using Tman;
using Xunit;

namespace Tman.Tests;

/// <summary>
/// `tman kill --stale-only` never matched anything: every command sweeps first, so a run whose
/// runner is dead has been reaped before the kill looks, and the flag then compared a possibly
/// recycled pid with IsAlive. It is gone. What must not happen in its place is the flag being
/// dropped on the floor — `kill all --stale-only` read as `kill all` kills the runs the caller
/// asked to keep. An unknown flag refuses the command, as it does for `run`.
/// </summary>
[Collection("cwd")]
public class KillFlagsTests : IDisposable
{
    readonly TempDir _home = new();
    readonly string? _prevHome = Environment.GetEnvironmentVariable("TMAN_HOME");
    readonly string? _prevParent = Environment.GetEnvironmentVariable(Runner.ParentIdEnvVar);

    public KillFlagsTests()
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

    /// <summary>The record of the one run in the store once its child is up, waiting at most 10s.</summary>
    static async Task<RunRecord> Started()
    {
        for (var i = 0; i < 200; i++)
        {
            if (Store.LoadAll().SingleOrDefault() is { ChildStartUtc: not null } r) return r;
            await Task.Delay(50);
        }
        throw new TimeoutException("the run never recorded a started child");
    }

    [UnixFact("supervises the sleep binary")]
    public async Task KillingALiveRun_EndsItKilledWithExit130_NotWithItsSignalStatus()
    {
        // The runner and `tman kill` both wrote the record; the runner's final save of its own stale
        // copy landed last, so the run returned 137 and read as exited with no kill reason.
        var run = Runner.RunAsync("sleep", ["30"], new Caps(), "victim", null);
        var r = await Started();

        Assert.Equal(0, await Program.Main(["kill", r.Id]));
        var exit = await run.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(Runner.ExitKilled, exit);
        var saved = Store.Load(r.Id)!;
        Assert.Equal(RunState.Killed, saved.State);
        Assert.Equal("killed via tman kill", saved.KillReason);
        Assert.Null(saved.ExitCode);
    }

    [Fact]
    public async Task KillWithAnUnknownFlag_RefusesRatherThanKillingEverythingElseNamed()
    {
        var err = new StringWriter();
        var prevErr = Console.Error;
        Console.SetError(err);
        int exit;
        try
        {
            exit = await Program.Main(new[] { "kill", "all", "--stale-only" });
        }
        finally
        {
            Console.SetError(prevErr);
        }

        Assert.Equal(Runner.ExitNotFound, exit);
        Assert.Contains("unknown flag --stale-only", err.ToString());
    }

    [Fact]
    public async Task Help_NoLongerOffersStaleOnly()
    {
        var prevOut = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            Assert.Equal(0, await Program.Main(new[] { "--help" }));
        }
        finally
        {
            Console.SetOut(prevOut);
        }

        Assert.DoesNotContain("--stale-only", captured.ToString());
    }
}
