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
                cores 6
                mem 24g
                queue-timeout "2h"
            }
            queue "gpu" {
                cores "80%"
                mem "80%"
            }
            """);

        Assert.Equal(Path.Combine(Store.Root, "tman.kdl"), Config.MachineConfigPath);
        var compile = Config.Queue("compile");
        Assert.Equal("compile", compile.Name);
        Assert.Equal(6, compile.Cores);
        Assert.Equal(24 * 1024, compile.MemMb);
        Assert.Equal(TimeSpan.FromHours(2), compile.Timeout);
        Assert.Equal(NamedQueue.FallbackCores, compile.DefaultCores);
        var gpu = Config.Queue("gpu");
        Assert.Equal(Host.Cores * 0.8, gpu.Cores, precision: 3);
        Assert.Equal((long)Math.Ceiling(Host.MemMb * 0.8), gpu.MemMb);
        Assert.Equal(NamedQueue.DefaultTimeout, gpu.Timeout);
        Assert.Equal(TimeSpan.FromHours(8), NamedQueue.DefaultTimeout);
    }

    [Theory]
    [InlineData(null, "queue \"compile\" is not declared in")]
    [InlineData("queue \"compile\" {\n}", "cores is required")]
    [InlineData("queue \"compile\" {\n    cores 1\n}", "mem is required")]
    [InlineData("queue \"compile\" {\n    cores 0\n    mem 1g\n}", "cores must be greater than zero")]
    [InlineData("queue \"compile\" {\n    cores \"abc\"\n    mem 1g\n}", "bad cores")]
    [InlineData("queue \"compile\" {\n    cores 1\n    mem \"abc\"\n}", "bad mem")]
    [InlineData("queue \"compile\" {\n    cores 1\n    mem 1g\n    stall \"1m\"\n}", "unknown setting \"stall\"")]
    [InlineData("queue \"compile\" {\n    cores 1\n    mem 1g\n}\nqueue \"compile\" {\n    cores 2\n    mem 1g\n}", "queue \"compile\" is declared twice")]
    [InlineData("defaults {\n}", "unknown node \"defaults\"")]
    [InlineData("queue {\n    cores 1\n    mem 1g\n}", "queue takes exactly one name")]
    public void AQueueTmanCannotTrust_IsRefused(string? machineConfig, string expected)
    {
        if (machineConfig is not null) _home.WriteFile("tman.kdl", machineConfig);
        var ex = Assert.Throws<FormatException>(() => Config.Queue("compile"));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void AQueueInTheSlotForm_IsRefusedNamingTheBudgetFormToWrite()
    {
        _home.WriteFile("tman.kdl", "queue \"compile\" {\n    max-parallel 1\n}\n");
        var ex = Assert.Throws<FormatException>(() => Config.Queue("compile"));
        Assert.Contains(Path.Combine(Store.Root, "tman.kdl"), ex.Message);
        Assert.Contains("queue \"compile\"", ex.Message);
        Assert.Contains("cores", ex.Message);
        Assert.Contains("mem", ex.Message);

        // the example the refusal names must itself parse and declare the queue: a message that
        // tells every host to write a form Config refuses is worse than no message
        var example = ex.Message[(ex.Message.IndexOf("queue \"compile\" {", StringComparison.Ordinal))..];
        _home.WriteFile("tman.kdl", example);
        var queue = Config.Queue("compile");
        Assert.Equal("compile", queue.Name);
        Assert.Equal(Host.Cores * 0.8, queue.Cores, precision: 3);
        Assert.Equal((long)Math.Ceiling(Host.MemMb * 0.8), queue.MemMb);
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

    [Fact]
    public void Defaults_NameAQueueEveryRunInTheProjectJoins()
    {
        using var proj = new TempDir();
        proj.WriteFile(Config.FileName, """
            defaults {
                max-parallel 2
                queue "compile"
            }
            """);
        var config = Config.Load(proj.Path)!;
        Assert.Equal("compile", config.DefaultQueue);
        Assert.Equal(2, config.Defaults.MaxParallel);

        proj.WriteFile(Config.FileName, "defaults {\n    queue\n}\n");
        Assert.Contains("defaults: queue takes exactly one queue name",
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
        Machine(cores: 1, mem: "4g");
    }

    /// <summary>Writes the machine config with one queue "compile" of the given budget.</summary>
    void Machine(double cores, string mem)
    {
        var n = cores.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _home.WriteFile("tman.kdl",
            "queue \"compile\" {\n" +
            $"    cores {n}\n" +
            $"    mem \"{mem}\"\n" +
            "}\n");
    }

    /// <summary>
    /// Occupies the queue by writing a live admitted record this process's runner owns, so other
    /// tman processes agree it is alive. The cost it holds is the whole budget, so nothing else fits
    /// beside it until it is cleared.
    /// </summary>
    RunRecord Occupy(string id, double cores, long memMb, bool exclusive = false)
    {
        var me = ProcUtil.OwnStart();
        var r = new RunRecord
        {
            Id = id, Command = "/bin/sh", Args = [], State = RunState.Running, Queue = "compile",
            AdmittedUtc = DateTime.UtcNow, AdmittedCores = cores, AdmittedMemMb = memMb, Exclusive = exclusive,
            QueuedUtc = DateTime.UtcNow, StartedUtc = DateTime.UtcNow, HeartbeatUtc = DateTime.UtcNow,
            Cwd = _home.Path,
            Pid = Environment.ProcessId, ChildStartUtc = me.Utc, ChildStartTicks = me.Ticks,
            RunnerPid = Environment.ProcessId, RunnerStartUtc = me.Utc, RunnerStartTicks = me.Ticks,
        };
        Store.Save(r);
        return r;
    }

    void Clear(string id)
    {
        var r = Store.Load(id)!;
        r.State = RunState.Killed;
        r.KillReason = "test";
        Store.Save(r);
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
        var psi = new ProcessStartInfo(AppHost.Path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _home.Mkdir(project),
        };
        psi.Environment["TMAN_HOME"] = _home.Path;
        // the suite runs under tman: inherited, this would make each one a nested run, which never queues
        psi.Environment.Remove(Runner.ParentIdEnvVar);
        foreach (var a in new[] { "run", "--queue", "compile", "--", "sh", "-c", script }) psi.ArgumentList.Add(a);
        var p = AppHost.Start(psi);
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
        // the whole budget is held here until all three are in line, so the line is observed whole
        // however slowly each apphost starts
        Occupy("occupy000001", cores: 1, memMb: 4 * 1024);
        var runs = new List<Process>();
        for (var n = 1; n <= 3; n++)
        {
            var run = Tman($"project{n}", $"echo {n}; sleep 1");
            runs.Add(run);
            await AwaitRecords(all => all.Any(r => r.RunnerPid == run.Id && r.State == RunState.Queued));
        }

        var waiting = Store.LoadAll();
        var listed = await ListOutput();
        for (var i = 0; i < runs.Count; i++)
        {
            var waiter = waiting.Single(r => r.RunnerPid == runs[i].Id);
            Assert.Equal("compile", waiter.Queue);
            Assert.True(System.Text.RegularExpressions.Regex.IsMatch(listed, $@"(?m)^{waiter.Id}\s.*\squeued #{i + 1}\s"),
                $"no row for {waiter.Id} at position {i + 1} in:\n{listed}");
        }
        Clear("occupy000001");

        foreach (var p in runs)
        {
            Assert.True(p.WaitForExit(30_000), "a queued run never finished");
            Assert.Equal(0, p.ExitCode);
        }
        var ran = Store.LoadAll().Where(r => r.Id != "occupy000001").OrderBy(r => r.StartedUtc).ToList();
        Assert.Equal(runs.Select(p => p.Id), ran.Select(r => r.RunnerPid));
        Assert.All(ran, r => Assert.Equal(RunState.Exited, r.State));
        for (var i = 1; i < ran.Count; i++)
            Assert.True(ran[i].StartedUtc >= ran[i - 1].HeartbeatUtc,
                $"run {i + 1} started at {ran[i].StartedUtc:O} before run {i} ended at {ran[i - 1].HeartbeatUtc:O}");
        // each in its own project: the buckets differ, so only the queue can have serialized them
        Assert.Equal(3, ran.Select(r => r.Group).Distinct().Count());
    }

    [UnixFact("drives a tman apphost running an sh child")]
    public async Task AFreeAdmission_GoesToTheEarliestWaiter_NotToTheRunThatAskedFirst()
    {
        // the budget is full, so the newcomer must wait; an earlier live waiter then joins the line
        Occupy("occupy000001", cores: 1, memMb: 4 * 1024);
        var newcomer = Tman("late", "exit 0");
        await AwaitRecords(all => all.Any(r => r.RunnerPid == newcomer.Id && r.State == RunState.Queued));
        var first = Store.LoadAll().Single(r => r.RunnerPid == newcomer.Id);

        var me = ProcUtil.OwnStart();
        Store.Save(new RunRecord
        {
            Id = "earlierwait1", Command = "/bin/sh", Args = [], State = RunState.Queued, Queue = "compile",
            QueuedUtc = first.QueuedUtc!.Value.AddMinutes(-1), StartedUtc = DateTime.UtcNow, HeartbeatUtc = DateTime.UtcNow,
            AdmittedCores = 1, AdmittedMemMb = 4 * 1024, Cwd = _home.Path,
            RunnerPid = Environment.ProcessId, RunnerStartUtc = me.Utc, RunnerStartTicks = me.Ticks,
        });
        Assert.Equal(2, Admission.Position(first, Store.LoadAll()));

        // the budget frees, but the earlier waiter fits, so the newcomer still may not go first
        Clear("occupy000001");
        Assert.False(newcomer.WaitForExit(2_500), "the newcomer was admitted ahead of an earlier waiter");

        var earlier = Store.Load("earlierwait1")!;
        earlier.State = RunState.Killed;
        Store.Save(earlier);
        Assert.True(newcomer.WaitForExit(15_000), "the newcomer was not admitted once it was first in line");
        Assert.Equal(0, newcomer.ExitCode);
    }

    [UnixFact("delivers SIGINT with `kill` to a queued tman apphost")]
    public async Task AFullBudget_HoldsWaiters_AndTheNextIsAdmittedWhenItFrees()
    {
        Occupy("occupy000001", cores: 1, memMb: 4 * 1024);
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
        Clear("occupy000001");
        Assert.True(second.WaitForExit(15_000), "the next waiter was not admitted once the budget was free");
        Assert.Equal(0, second.ExitCode);
    }

    [UnixFact("runs the true binary")]
    public async Task ANestedRun_ClaimsNoQueueAdmission()
    {
        // the parent already holds whatever admission this work needs; the child claiming another
        // would deadlock a queue of one against itself
        Occupy("occupy000001", cores: 1, memMb: 4 * 1024);
        Environment.SetEnvironmentVariable(Runner.ParentIdEnvVar, "outerrun1234");
        using var scope = new TempDir();

        var exit = await Program.GatedRun("true", [], new Caps(), null, null, replace: false, scope.Path,
            queue: new Program.QueueJoin(Config.Queue("compile"), false)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, exit);
        Assert.DoesNotContain(Store.LoadAll(), r => r.Id != "occupy000001" && r.Queue is not null);
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

    /// <summary>
    /// A project whose defaults name compile, the queue's budget held here, and the cwd moved into
    /// it — the setting every route into `tman run` without an alias meets: the PATH shims'
    /// `exec tman run -- "$real" "$@"` and the Claude Code hook's rewrite alike.
    /// </summary>
    TempDir ProjectWhoseDefaultsJoinCompile(string? aliases = null)
    {
        var proj = new TempDir();
        proj.WriteFile(Config.FileName, $$"""
            defaults {
                queue "compile"
            }
            {{aliases}}
            """);
        Directory.SetCurrentDirectory(proj.Path);
        return proj;
    }

    [UnixFact("queues the sleep binary")]
    public async Task ABareRun_InAProjectWhoseDefaultsNameAQueue_WaitsInIt()
    {
        Occupy("occupy000001", cores: 1, memMb: 4 * 1024);
        using var proj = ProjectWhoseDefaultsJoinCompile();
        var run = Task.Run(() => Program.Main(["run", "--", "sleep", "30"]));

        var queued = (await AwaitRecords(all => all.Any(r => r.State == RunState.Queued && r.RunnerPid == Environment.ProcessId)))
            .Single(r => r.State == RunState.Queued && r.RunnerPid == Environment.ProcessId);
        Assert.Equal("compile", queued.Queue);
        Assert.Equal(0, await Program.Main(["kill", queued.Id]));
        Assert.Equal(Runner.ExitKilled, await run.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [UnixFact("runs the true binary")]
    public async Task NoQueue_KeepsARunOutOfTheDefaultsQueue()
    {
        // the queue's whole budget is held for the test: a run that joined it could not finish
        Occupy("occupy000001", cores: 1, memMb: 4 * 1024);
        using var proj = ProjectWhoseDefaultsJoinCompile();

        var exit = await Program.Main(["run", "--no-queue", "--", "true"]).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, exit);
        Assert.Null(Store.LoadAll().Single(r => r.Id != "occupy000001").Queue);
    }

    [UnixFact("runs the true binary from an alias")]
    public async Task AnAliasQueue_TakesPrecedenceOverTheDefaultsQueue()
    {
        _home.WriteFile("tman.kdl", """
            queue "compile" {
                cores 1
                mem "4g"
            }
            queue "free" {
                cores 1
                mem "4g"
            }
            """);
        Occupy("occupy000001", cores: 1, memMb: 4 * 1024);
        using var proj = ProjectWhoseDefaultsJoinCompile("""
            alias "build" {
                command "true"
                queue "free"
            }
            """);

        var exit = await Program.Main(["build"]).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, exit);
        Assert.Equal("free", Store.LoadAll().Single(r => r.Id != "occupy000001").Queue);
    }

    [Fact]
    public async Task QueueAndNoQueueTogether_RefuseTheRun()
    {
        var err = new StringWriter();
        var prev = Console.Error;
        Console.SetError(err);
        int exit;
        try { exit = await Program.Main(["run", "--queue", "compile", "--no-queue", "--", "true"]); }
        finally { Console.SetError(prev); }

        Assert.Equal(Runner.ExitNotFound, exit);
        Assert.Contains("--queue and --no-queue cannot both be given", err.ToString());
        Assert.Empty(Store.LoadAll());
    }

    [UnixFact("queues an alias whose program is the sleep binary")]
    public async Task AnAliasThatNamesAQueue_WaitsInIt()
    {
        Occupy("occupy000001", cores: 1, memMb: 4 * 1024);
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

        var queued = (await AwaitRecords(all => all.Any(r => r.State == RunState.Queued && r.RunnerPid == Environment.ProcessId)))
            .Single(r => r.State == RunState.Queued && r.RunnerPid == Environment.ProcessId);
        Assert.Equal("compile", queued.Queue);
        Assert.Equal(0, await Program.Main(["kill", queued.Id]));
        Assert.Equal(Runner.ExitKilled, await run.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
