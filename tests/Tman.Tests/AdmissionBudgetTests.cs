using System.Diagnostics;
using Tman;
using Xunit;

namespace Tman.Tests;

/// <summary>
/// The machine queue is the host's admission gate: a run joins the machine's "default" queue when
/// its project names no other, and admission is decided by the cost the run is expected to bear
/// (cores and memory), not by counting slots. History — the last records of the run's bucket —
/// sizes a run; an alias's declared limits, then the queue's defaults, stand in when there is none.
/// </summary>
[Collection("cwd")]
public class AdmissionBudgetTests : IDisposable
{
    readonly TempDir _home = new();
    readonly string? _prevHome = Environment.GetEnvironmentVariable("TMAN_HOME");
    readonly string? _prevParent = Environment.GetEnvironmentVariable(Runner.ParentIdEnvVar);

    public AdmissionBudgetTests()
    {
        Environment.SetEnvironmentVariable("TMAN_HOME", _home.Path);
        Environment.SetEnvironmentVariable(Runner.ParentIdEnvVar, null);
    }

    public void Dispose()
    {
        foreach (var r in Store.LoadAll().Where(r => !r.IsFinished)) Reaper.KillRun(r, "test cleanup");
        Environment.SetEnvironmentVariable("TMAN_HOME", _prevHome);
        Environment.SetEnvironmentVariable(Runner.ParentIdEnvVar, _prevParent);
        _home.Dispose();
    }

    // ---- root cause (1): a run with no queue named anywhere joins "default" ----

    [UnixFact("runs the sleep binary through Program.Main")]
    public async Task ARun_WhoseProjectNamesNoQueue_JoinsTheMachineDefaultQueue()
    {
        _home.WriteFile("tman.kdl", """
            queue "default" {
                max-parallel 1
            }
            """);
        using var proj = new TempDir();
        Directory.SetCurrentDirectory(proj.Path);

        var run = Task.Run(() => Program.Main(["run", "--", "sleep", "30"]));
        var record = await Await(r => r.Queue == "default");
        Assert.Equal("default", record.Queue);

        Assert.Equal(0, await Program.Main(["kill", record.Id]));
        Assert.Equal(Runner.ExitKilled, await run.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [UnixFact("runs the sleep binary through Program.Main")]
    public async Task ANoQueueRun_IsExemptFromTheMachineDefaultQueue()
    {
        _home.WriteFile("tman.kdl", """
            queue "default" {
                max-parallel 1
            }
            """);
        using var proj = new TempDir();
        Directory.SetCurrentDirectory(proj.Path);

        var run = Task.Run(() => Program.Main(["run", "--no-queue", "--", "sleep", "30"]));
        var record = await Await(r => r is { State: RunState.Running });
        Assert.Null(record.Queue);

        Assert.Equal(0, await Program.Main(["kill", record.Id]));
        Assert.Equal(Runner.ExitKilled, await run.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    static async Task<RunRecord> Await(Func<RunRecord, bool> match)
    {
        for (var i = 0; i < 200; i++)
        {
            if (Store.LoadAll().FirstOrDefault(match) is { } r) return r;
            await Task.Delay(50);
        }
        throw new TimeoutException("no matching run record within 10s");
    }
}
