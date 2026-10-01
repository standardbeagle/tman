using System.Diagnostics;
using Tman;
using Xunit;

namespace Tman.Tests;

/// <summary>
/// A run waiting for a slot is a run: it has a record, `tman list` shows it, `tman kill` and Ctrl+C
/// end it with an outcome, and it never starts a child once told to stop. Before, a waiter had no
/// record at all — invisible, unkillable by name, and gone without trace on Ctrl+C.
/// </summary>
[Collection("cwd")]
public class QueuedRunTests : IDisposable
{
    readonly TempDir _home = new();
    readonly string _scope;
    readonly string? _prevHome = Environment.GetEnvironmentVariable("TMAN_HOME");
    readonly string? _prevParent = Environment.GetEnvironmentVariable(Runner.ParentIdEnvVar);

    public QueuedRunTests()
    {
        Environment.SetEnvironmentVariable("TMAN_HOME", _home.Path);
        Environment.SetEnvironmentVariable(Runner.ParentIdEnvVar, null);
        _scope = _home.Mkdir("proj");
    }

    public void Dispose()
    {
        foreach (var r in Store.LoadAll().Where(r => !r.IsFinished)) Reaper.KillRun(r, "test cleanup");
        Environment.SetEnvironmentVariable("TMAN_HOME", _prevHome);
        Environment.SetEnvironmentVariable(Runner.ParentIdEnvVar, _prevParent);
        _home.Dispose();
    }

    static readonly Caps OneSlot = new() { MaxParallel = 1, QueueTimeout = TimeSpan.FromMinutes(1) };

    Task<int> Sleep(Caps caps) =>
        Task.Run(() => Program.GatedRun("sleep", ["30"], caps, null, null, replace: false, _scope));

    /// <summary>The first record matching <paramref name="match"/>, waiting at most 10s for it.</summary>
    static async Task<RunRecord> Await(Func<RunRecord, bool> match)
    {
        for (var i = 0; i < 200; i++)
        {
            if (Store.LoadAll().FirstOrDefault(match) is { } r) return r;
            await Task.Delay(50);
        }
        throw new TimeoutException("no matching run record within 10s");
    }

    static async Task<string> Captured(Func<Task<int>> command)
    {
        var outw = new StringWriter();
        var prevOut = Console.Out;
        Console.SetOut(outw);
        try { Assert.Equal(0, await command()); }
        finally { Console.SetOut(prevOut); }
        return outw.ToString();
    }

    [UnixFact("queues behind a sleep child")]
    public async Task AWaiter_IsListedAsQueued_AndKillEndsItWithoutStartingIt()
    {
        var holder = Sleep(OneSlot);
        var running = await Await(r => r is { State: RunState.Running, ChildStartUtc: not null });
        var waiter = Sleep(OneSlot);
        var queued = await Await(r => r.State == RunState.Queued);

        Assert.Equal(0, queued.Pid);
        Assert.Equal(running.Group, queued.Group);
        var listed = (await Captured(() => Program.Main(["list"]))).Split('\n');
        var row = Assert.Single(listed, l => l.StartsWith(queued.Id));
        Assert.Matches(@"\s-\s+queued\s", row);

        Assert.Equal(0, await Program.Main(["kill", queued.Id]));

        Assert.Equal(Runner.ExitKilled, await waiter.WaitAsync(TimeSpan.FromSeconds(10)));
        var ended = Store.Load(queued.Id)!;
        Assert.Equal(RunState.Killed, ended.State);
        Assert.Equal("killed via tman kill", ended.KillReason);
        Assert.Null(ended.ChildStartUtc);
        // the holder was never touched
        Assert.False(holder.IsCompleted);
        Assert.Equal(RunState.Running, Store.Load(running.Id)!.State);
    }

    [UnixFact("queues behind a sleep child")]
    public async Task AWaiterThatRunsOutOfQueueTime_IsRecordedAsKilledWithTheReason()
    {
        _ = Sleep(OneSlot);
        await Await(r => r is { State: RunState.Running, ChildStartUtc: not null });

        var exit = await Sleep(OneSlot with { QueueTimeout = TimeSpan.FromSeconds(1) }).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(Runner.ExitKilled, exit);
        var ended = Assert.Single(Store.LoadAll(), r => r.State == RunState.Killed);
        Assert.StartsWith("queue timeout waiting for a 'sleep@", ended.KillReason);
        Assert.Null(ended.ChildStartUtc);
    }

    [Fact]
    public void AWaiterWhoseRunnerDied_IsEndedByTheSweep()
    {
        Store.Save(new RunRecord
        {
            Id = "deadwaiter01", Command = "/bin/sleep", Args = [], State = RunState.Queued,
            RunnerPid = 2147483645, RunnerStartUtc = DateTime.UtcNow,
            StartedUtc = DateTime.UtcNow, HeartbeatUtc = DateTime.UtcNow,
        });
        Assert.Contains(Store.LoadAll(), r => r.Id == "deadwaiter01" && !r.IsFinished);

        Reaper.ReapOrphans(quiet: true);

        var ended = Store.Load("deadwaiter01")!;
        Assert.Equal(RunState.Killed, ended.State);
        Assert.Equal("runner died while queued", ended.KillReason);
        Assert.DoesNotContain(Reaper.LiveRuns(), r => r.Id == "deadwaiter01");
    }

    [UnixFact("delivers SIGINT with `kill` to a queued tman apphost")]
    public async Task CtrlCWhileQueued_ExitsKilledAndNeverStartsTheChild()
    {
        // out of process: the queued path's interrupt is a console signal handler
        _ = Sleep(OneSlot);
        await Await(r => r is { State: RunState.Running, ChildStartUtc: not null });

        var psi = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "tman"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _scope,
        };
        psi.Environment["TMAN_HOME"] = _home.Path;
        // the suite runs under tman: inherited, this would make the apphost a nested run, which never queues
        psi.Environment.Remove(Runner.ParentIdEnvVar);
        foreach (var a in new[] { "run", "--max-parallel", "1", "--", "sleep", "30" }) psi.ArgumentList.Add(a);
        using var tman = Process.Start(psi)!;
        var stderr = tman.StandardError.ReadToEndAsync();
        try
        {
            var queued = await Await(r => r.State == RunState.Queued);
            Assert.Equal(tman.Id, queued.RunnerPid);
            using (var kill = Process.Start("kill", ["-INT", tman.Id.ToString()])!) kill.WaitForExit();

            Assert.True(tman.WaitForExit(15_000), "tman did not end its wait on SIGINT");
            Assert.Equal(Runner.ExitKilled, tman.ExitCode);
            Assert.Contains(Admission.CancelledReason, await stderr);
            var ended = Store.Load(queued.Id)!;
            Assert.Equal(RunState.Killed, ended.State);
            Assert.Equal(Admission.CancelledReason, ended.KillReason);
            Assert.Null(ended.ChildStartUtc);
        }
        finally
        {
            if (!tman.HasExited) tman.Kill(entireProcessTree: true);
        }
    }
}
