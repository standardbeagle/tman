using System.Diagnostics;
using Tman;
using Xunit;

namespace Tman.Tests;

/// <summary>The machine config's queue declarations, read whole or refused like `.tman.kdl`.</summary>
[Collection("cwd")]
public class NamedQueueConfigTests : IDisposable
{
    readonly TempDir _home = new();
    readonly string? _prevHome = Environment.GetEnvironmentVariable("TMAN_HOME");

    public NamedQueueConfigTests() => Environment.SetEnvironmentVariable("TMAN_HOME", _home.Path);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TMAN_HOME", _prevHome);
        _home.Dispose();
    }

    [Fact]
    public void ADeclaredQueue_IsReadFromBesideTheRunStore()
    {
        _home.WriteFile("tman.kdl", """
            queue "compile" {
                max-parallel 1
                queue-timeout "2h"
            }
            queue "gpu" {
                max-parallel 2
            }
            """);

        Assert.Equal(Path.Combine(Store.Root, "tman.kdl"), Config.MachineConfigPath);
        Assert.Equal(new NamedQueue("compile", 1, TimeSpan.FromHours(2)), Config.Queue("compile"));
        Assert.Equal(new NamedQueue("gpu", 2, NamedQueue.DefaultTimeout), Config.Queue("gpu"));
        Assert.Equal(TimeSpan.FromHours(8), NamedQueue.DefaultTimeout);
    }

    [Theory]
    [InlineData(null, "queue \"compile\" is not declared in")]
    [InlineData("queue \"compile\" {\n}", "max-parallel is required and must be at least 1")]
    [InlineData("queue \"compile\" {\n    max-parallel 0\n}", "max-parallel is required and must be at least 1")]
    [InlineData("queue \"compile\" {\n    max-parallel 1\n    stall \"1m\"\n}", "unknown setting \"stall\"")]
    [InlineData("queue \"compile\" {\n    max-parallel 1\n}\nqueue \"compile\" {\n    max-parallel 2\n}", "queue \"compile\" is declared twice")]
    [InlineData("defaults {\n}", "unknown node \"defaults\"")]
    [InlineData("queue {\n    max-parallel 1\n}", "queue takes exactly one name")]
    public void AQueueTmanCannotTrust_IsRefused(string? machineConfig, string expected)
    {
        if (machineConfig is not null) _home.WriteFile("tman.kdl", machineConfig);
        var ex = Assert.Throws<FormatException>(() => Config.Queue("compile"));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void AnAlias_NamesAQueueToJoin()
    {
        using var proj = new TempDir();
        proj.WriteFile(Config.FileName, """
            alias "build" {
                command "cargo"
                queue "compile"
            }
            """);
        Assert.Equal("compile", Config.Load(proj.Path)!.Aliases["build"].Queue);

        proj.WriteFile(Config.FileName, "alias \"build\" {\n    command \"cargo\"\n    queue \"a\" \"b\"\n}\n");
        Assert.Contains("queue takes exactly one queue name",
            Assert.Throws<FormatException>(() => Config.Load(proj.Path)).Message);
    }
}

/// <summary>
/// Runs from different projects that name one queue share its slots and are admitted in the order
/// they arrived. Driven through real tman processes where the claim crosses process boundaries,
/// because that is the whole point of a machine-wide queue.
/// </summary>
[Collection("cwd")]
public class NamedQueueRunTests : IDisposable
{
    readonly TempDir _home = new();
    readonly string? _prevHome = Environment.GetEnvironmentVariable("TMAN_HOME");
    readonly string? _prevParent = Environment.GetEnvironmentVariable(Runner.ParentIdEnvVar);
    readonly string _prevCwd = Directory.GetCurrentDirectory();
    readonly List<Process> _started = [];

    public NamedQueueRunTests()
    {
        Environment.SetEnvironmentVariable("TMAN_HOME", _home.Path);
        Environment.SetEnvironmentVariable(Runner.ParentIdEnvVar, null);
        _home.WriteFile("tman.kdl", """
            queue "compile" {
                max-parallel 1
            }
            """);
    }

    public void Dispose()
    {
        foreach (var p in _started)
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
            p.Dispose();
        }
        Directory.SetCurrentDirectory(_prevCwd);
        Environment.SetEnvironmentVariable("TMAN_HOME", _prevHome);
        Environment.SetEnvironmentVariable(Runner.ParentIdEnvVar, _prevParent);
        _home.Dispose();
    }

    /// <summary>A real tman, in a project directory of its own, running `sh -c script` in queue compile.</summary>
    Process Tman(string project, string script)
    {
        var psi = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "tman"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _home.Mkdir(project),
        };
        psi.Environment["TMAN_HOME"] = _home.Path;
        // the suite runs under tman: inherited, this would make each one a nested run, which never queues
        psi.Environment.Remove(Runner.ParentIdEnvVar);
        foreach (var a in new[] { "run", "--queue", "compile", "--", "sh", "-c", script }) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        _started.Add(p);
        return p;
    }

    static async Task<List<RunRecord>> AwaitRecords(Func<List<RunRecord>, bool> ready)
    {
        for (var i = 0; i < 300; i++)
        {
            var all = Store.LoadAll();
            if (ready(all)) return all;
            await Task.Delay(50);
        }
        throw new TimeoutException("the store never reached the expected state within 15s");
    }

    static async Task<string> ListOutput()
    {
        var outw = new StringWriter();
        var prev = Console.Out;
        Console.SetOut(outw);
        try { Assert.Equal(0, await Program.Main(["list"])); }
        finally { Console.SetOut(prev); }
        return outw.ToString();
    }

    [UnixFact("drives tman apphosts running sh children")]
    public async Task ThreeProjectsInOneQueue_RunOneAtATimeInArrivalOrder()
    {
        // Each one launched once the last has a record, so the order they arrive in is the order
        // they were started in: an apphost's cold start can outlast a fixed gap, and then the
        // second launched is the first to arrive.
        var runs = new List<Process>();
        for (var n = 1; n <= 3; n++)
        {
            var run = Tman($"project{n}", $"echo {n}; sleep 2");
            runs.Add(run);
            await AwaitRecords(all => all.Any(r => r.RunnerPid == run.Id));
        }

        var waiting = await AwaitRecords(all => all.Count(r => r.State == RunState.Queued) == 2);
        var listed = await ListOutput();
        foreach (var (pid, position) in new[] { (runs[1].Id, 1), (runs[2].Id, 2) })
        {
            var waiter = waiting.Single(r => r.RunnerPid == pid);
            Assert.Equal("compile", waiter.Queue);
            Assert.True(System.Text.RegularExpressions.Regex.IsMatch(listed, $@"(?m)^{waiter.Id}\s.*\squeued #{position}\s"),
                $"no row for {waiter.Id} at position {position} in:\n{listed}");
        }

        foreach (var p in runs)
        {
            Assert.True(p.WaitForExit(30_000), "a queued run never finished");
            Assert.Equal(0, p.ExitCode);
        }
        var ran = Store.LoadAll().OrderBy(r => r.StartedUtc).ToList();
        Assert.Equal(runs.Select(p => p.Id), ran.Select(r => r.RunnerPid));
        Assert.All(ran, r => Assert.Equal(RunState.Exited, r.State));
        for (var i = 1; i < ran.Count; i++)
            Assert.True(ran[i].StartedUtc >= ran[i - 1].HeartbeatUtc,
                $"run {i + 1} started at {ran[i].StartedUtc:O} before run {i} ended at {ran[i - 1].HeartbeatUtc:O}");
        // each in its own project: the buckets differ, so only the queue can have serialized them
        Assert.Equal(3, ran.Select(r => r.Group).Distinct().Count());
    }

    [UnixFact("drives a tman apphost running an sh child")]
    public async Task AFreeSlot_GoesToTheEarliestWaiter_NotToTheRunThatAskedFirst()
    {
        // an earlier waiter, alive (its runner is this process, stamped as itself so every other
        // tman's sweep agrees it is alive) but not claiming: a slot is free, and arrival order still
        // says it is not the newcomer's
        var me = ProcUtil.OwnStart();
        Store.Save(new RunRecord
        {
            Id = "earlierwait1", Command = "/bin/sh", Args = [], State = RunState.Queued, Queue = "compile",
            QueuedUtc = DateTime.UtcNow.AddMinutes(-1), StartedUtc = DateTime.UtcNow, HeartbeatUtc = DateTime.UtcNow,
            RunnerPid = Environment.ProcessId, RunnerStartUtc = me.Utc, RunnerStartTicks = me.Ticks,
        });
        var newcomer = Tman("late", "exit 0");
        var waiting = (await AwaitRecords(all => all.Any(r => r.RunnerPid == newcomer.Id && r.State == RunState.Queued)))
            .Single(r => r.RunnerPid == newcomer.Id);
        Assert.Equal(2, Admission.Position(waiting, Store.LoadAll()));
        Assert.False(newcomer.WaitForExit(2_500), "the newcomer took the free slot ahead of an earlier waiter");

        var earlier = Store.Load("earlierwait1")!;
        earlier.State = RunState.Killed;
        Store.Save(earlier);
        Assert.True(newcomer.WaitForExit(15_000), "the newcomer was not admitted once it was first in line");
        Assert.Equal(0, newcomer.ExitCode);
    }

    [UnixFact("delivers SIGINT with `kill` to a queued tman apphost")]
    public async Task AnInterruptedWaiter_GivesUpItsPlace_AndTheNextIsAdmitted()
    {
        // the one slot is held here, so both apphosts must wait for it
        using var held = Store.TryAcquireSlot(Admission.QueueKey("compile"), 1)!;
        var first = Tman("a", "exit 0");
        await AwaitRecords(all => all.Any(r => r.RunnerPid == first.Id && r.State == RunState.Queued));
        var second = Tman("b", "exit 0");
        await AwaitRecords(all => all.Any(r => r.RunnerPid == second.Id && r.State == RunState.Queued));

        using (var kill = Process.Start("kill", ["-INT", first.Id.ToString()])!) kill.WaitForExit();
        Assert.True(first.WaitForExit(15_000), "the interrupted waiter did not end");
        Assert.Equal(Runner.ExitKilled, first.ExitCode);
        var interrupted = Store.LoadAll().Single(r => r.RunnerPid == first.Id);
        Assert.Equal(Admission.CancelledReason, interrupted.KillReason);
        Assert.Null(interrupted.ChildStartUtc);

        var next = Store.LoadAll().Single(r => r.RunnerPid == second.Id);
        Assert.Equal(1, Admission.Position(next, Store.LoadAll()));
        Store.ReleaseLock(held);
        Assert.True(second.WaitForExit(15_000), "the next waiter was not admitted once the slot was free");
        Assert.Equal(0, second.ExitCode);
    }

    [UnixFact("runs the true binary")]
    public async Task ANestedRun_ClaimsNoQueueSlot()
    {
        // the parent already holds whatever slot this work needs; the child claiming another would
        // deadlock a queue of one against itself
        using var held = Store.TryAcquireSlot(Admission.QueueKey("compile"), 1)!;
        Environment.SetEnvironmentVariable(Runner.ParentIdEnvVar, "outerrun1234");
        using var scope = new TempDir();

        var exit = await Program.GatedRun("true", [], new Caps(), null, null, replace: false, scope.Path,
            queue: Config.Queue("compile")).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, exit);
        Assert.Null(Assert.Single(Store.LoadAll()).Queue);
    }

    [Fact]
    public async Task AnUndeclaredQueue_RefusesTheRunNamingTheMachineConfig()
    {
        var err = new StringWriter();
        var prev = Console.Error;
        Console.SetError(err);
        int exit;
        try { exit = await Program.Main(["run", "--queue", "nope", "--", "true"]); }
        finally { Console.SetError(prev); }

        Assert.Equal(Runner.ExitNotFound, exit);
        Assert.Contains($"queue \"nope\" is not declared in {Config.MachineConfigPath}", err.ToString());
        Assert.Empty(Store.LoadAll());
    }

    [UnixFact("queues an alias whose program is the sleep binary")]
    public async Task AnAliasThatNamesAQueue_WaitsInIt()
    {
        using var held = Store.TryAcquireSlot(Admission.QueueKey("compile"), 1)!;
        using var proj = new TempDir();
        proj.WriteFile(Config.FileName, """
            alias "build" {
                command "sleep"
                args "30"
                queue "compile"
            }
            """);
        Directory.SetCurrentDirectory(proj.Path);
        var run = Task.Run(() => Program.Main(["build"]));

        var queued = (await AwaitRecords(all => all.Any(r => r.State == RunState.Queued))).Single();
        Assert.Equal("compile", queued.Queue);
        Assert.Equal(0, await Program.Main(["kill", queued.Id]));
        Assert.Equal(Runner.ExitKilled, await run.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
