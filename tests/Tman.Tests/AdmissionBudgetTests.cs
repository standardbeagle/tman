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
    readonly string _prevCwd = Directory.GetCurrentDirectory();

    public AdmissionBudgetTests()
    {
        Environment.SetEnvironmentVariable("TMAN_HOME", _home.Path);
        Environment.SetEnvironmentVariable(Runner.ParentIdEnvVar, null);
    }

    public void Dispose()
    {
        // restore the cwd first: a temp project may already be gone, and a ctor that reads a deleted
        // cwd fails every later test in the collection
        if (Directory.Exists(_prevCwd)) Directory.SetCurrentDirectory(_prevCwd);
        // records this test wrote with its own pid stand in for runs tman did not start; killing
        // one would kill the test's own process tree
        foreach (var r in Store.LoadAll().Where(r => !r.IsFinished && r.Pid != Environment.ProcessId && r.RunnerPid != Environment.ProcessId))
            Reaper.KillRun(r, "test cleanup");
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
                cores "80%"
                mem "80%"
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
                cores "80%"
                mem "80%"
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

    // ---- root cause (2): admission is by measured cost, not slot count ----

    static NamedQueue Queue(double cores, long memMb) =>
        new("q", cores, memMb, NamedQueue.DefaultTimeout, NamedQueue.FallbackCores, NamedQueue.FallbackMemMb);

    [Fact]
    public void ASmallRun_FitsBesideABigOne_WhenTheBudgetAllows()
    {
        var q = Queue(cores: 6, memMb: 40 * 1024);
        Assert.True(Admission.Fits(new Admission.AdmissionCost(3, 20 * 1024),
            [new Admission.AdmissionCost(3, 20 * 1024)], q), "two 3-core suites should share a 6-core budget");
        // the same two, when the big one claims more memory than is left
        Assert.False(Admission.Fits(new Admission.AdmissionCost(3, 20 * 1024),
            [new Admission.AdmissionCost(3, 25 * 1024)], q), "the second suite should not fit past the memory budget");
    }

    [Fact]
    public void ALoneRunLargerThanTheBudget_IsStillAdmitted()
    {
        // a suite bigger than the declared budget must still be able to start, alone, or a queue
        // sized too small would deadlock every run
        var q = Queue(cores: 2, memMb: 2 * 1024);
        Assert.True(Admission.Fits(new Admission.AdmissionCost(8, 30 * 1024), [], q));
        Assert.False(Admission.Fits(new Admission.AdmissionCost(8, 30 * 1024),
            [new Admission.AdmissionCost(1, 512)], q));
    }

    [Fact]
    public void ExpectedCost_UsesTheHighPercentileOfTheBucketsHistory()
    {
        _home.WriteFile("tman.kdl", "queue \"q\" {\n    cores 8\n    mem 64g\n}\n");
        var q = Config.Queue("q");
        // a bucket's recent finished records: mostly small, one suite that peaked at 5 cores / 6 GiB
        for (var i = 0; i < 8; i++)
            Store.Save(Finished("hist" + i, "test@/a", cores: 1, memMb: 200));
        Store.Save(Finished("bighist00001", "test@/a", cores: 5, memMb: 6 * 1024));

        var cost = Admission.ExpectedCost(
            Runner.NewRecord("go", ["test"], new Caps(), "test", null, "test@/a", "/a"), q, null, null);

        // nearest-rank p90 of nine samples is the largest, so the suite that once cost 5 cores sizes it
        Assert.Equal(5, cost.Cores);
        Assert.Equal(6 * 1024, cost.MemMb);
    }

    [Fact]
    public void ExpectedCost_FallsBackToTheAliasThenTheQueue_WithNoHistory()
    {
        _home.WriteFile("tman.kdl", "queue \"q\" {\n    cores 8\n    mem 64g\n    cores-default 2\n    mem-default 3g\n}\n");
        var q = Config.Queue("q");
        var record = Runner.NewRecord("go", ["test"], new Caps(), "test", null, "test@/new", "/new");

        Assert.Equal(new Admission.AdmissionCost(5, 4 * 1024),
            Admission.ExpectedCost(record, q, aliasCores: 5, aliasMemMb: 4 * 1024));
        Assert.Equal(new Admission.AdmissionCost(2, 3 * 1024),
            Admission.ExpectedCost(record, q, null, null));
    }

    static RunRecord Finished(string id, string group, double cores, long memMb) => new()
    {
        Id = id, Command = "/bin/true", Args = [], Group = group, State = RunState.Exited,
        PeakCores = cores, PeakMemMb = memMb, StartedUtc = DateTime.UtcNow, HeartbeatUtc = DateTime.UtcNow,
        Cwd = "/tmp",
    };

    [Fact]
    public void MachineDefaultQueue_IsDeclaredOnlyWhenTheMachineNamesOne()
    {
        Assert.Null(Config.MachineDefaultQueue());
        _home.WriteFile("tman.kdl", "queue \"compile\" {\n    cores 4\n    mem 8g\n}\n");
        Assert.Null(Config.MachineDefaultQueue());
        _home.WriteFile("tman.kdl", "queue \"compile\" {\n    cores 4\n    mem 8g\n}\nqueue \"default\" {\n    cores \"80%\"\n    mem \"80%\"\n}\n");
        Assert.Equal("default", Config.MachineDefaultQueue());
    }

    [UnixFact("writes records this process's runner owns")]
    public async Task AnExclusiveRun_WaitsForAdmittedWork_ThenHoldsTheQueueAlone()
    {
        _home.WriteFile("tman.kdl", "queue \"q\" {\n    cores 4\n    mem 4g\n}\n");
        var q = Config.Queue("q") with { Timeout = TimeSpan.FromMilliseconds(700) };
        Occupy("normalocc001", cores: 1, memMb: 512);

        var audit = Runner.NewRecord("audit", [], new Caps(), "audit", null, "audit@/a", "/tmp");
        var exclusiveCost = new Admission.AdmissionCost(1, 512);
        // exclusive waits for admitted work to finish even though its cost fits
        Assert.False(await Admission.ClaimAdmission(audit, q, exclusiveCost, exclusive: true, null, null, default),
            "an exclusive run started beside admitted work");

        Clear("normalocc001");
        Assert.True(await Admission.ClaimAdmission(audit, q, exclusiveCost, exclusive: true, null, null, default));
        Assert.True(audit.Exclusive);
        Assert.NotNull(audit.AdmittedUtc);

        // while the exclusive run holds the queue, a normal run of any size waits
        var normal = Runner.NewRecord("test", [], new Caps(), "test", null, "test@/b", "/tmp");
        Assert.False(await Admission.ClaimAdmission(normal, q, new Admission.AdmissionCost(1, 512), false, null, null, default),
            "a normal run started while an exclusive run held the queue");
    }

    void Occupy(string id, double cores, long memMb, string queue = "q")
    {
        var me = ProcUtil.OwnStart();
        Store.Save(new RunRecord
        {
            Id = id, Command = "/bin/sh", Args = [], Group = "hold@" + _home.Path, State = RunState.Running,
            Queue = queue, AdmittedUtc = DateTime.UtcNow, AdmittedCores = cores, AdmittedMemMb = memMb,
            QueuedUtc = DateTime.UtcNow, StartedUtc = DateTime.UtcNow, HeartbeatUtc = DateTime.UtcNow,
            Cwd = "/tmp", Pid = Environment.ProcessId, ChildStartUtc = me.Utc, ChildStartTicks = me.Ticks,
            RunnerPid = Environment.ProcessId, RunnerStartUtc = me.Utc, RunnerStartTicks = me.Ticks,
        });
    }

    void Clear(string id)
    {
        var r = Store.Load(id)!;
        r.State = RunState.Killed;
        r.KillReason = "test";
        Store.Save(r);
    }

    [UnixFact("runs a real tman apphost")]
    public async Task AHold_ReservesAdmissionWithoutTmanKnowingTheWork()
    {
        _home.WriteFile("tman.kdl", "queue \"default\" {\n    cores 1\n    mem 1g\n}\n");
        using var proj = new TempDir();
        Directory.SetCurrentDirectory(proj.Path);

        var psi = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "tman"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = proj.Path,
        };
        psi.Environment["TMAN_HOME"] = _home.Path;
        psi.Environment.Remove(Runner.ParentIdEnvVar);
        foreach (var a in new[] { "hold", "--cores", "1", "--mem", "1g" }) psi.ArgumentList.Add(a);
        using var hold = Process.Start(psi)!;
        try
        {
            var rec = await Await(r => r.Queue == "default" && r.AdmittedUtc is not null);
            Assert.Equal(1, rec.AdmittedCores);
            Assert.Equal(1024, rec.AdmittedMemMb);
            Assert.Equal("hold", rec.Command);
        }
        finally
        {
            if (!hold.HasExited) hold.Kill(entireProcessTree: true);
        }
    }

    [UnixFact("runs a real tman apphost")]
    public async Task AHoldThatWaited_RecordsHowLongItQueued()
    {
        _home.WriteFile("tman.kdl", "queue \"default\" {\n    cores 1\n    mem 1g\n}\n");
        // the budget is held, so the hold below must wait before it is admitted
        Occupy("occupy000001", cores: 1, memMb: 1024, queue: "default");
        using var proj = new TempDir();
        Directory.SetCurrentDirectory(proj.Path);

        var psi = new ProcessStartInfo(AppHost.Path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = proj.Path,
        };
        psi.Environment["TMAN_HOME"] = _home.Path;
        psi.Environment.Remove(Runner.ParentIdEnvVar);
        foreach (var a in new[] { "hold", "--cores", "1", "--mem", "1g" }) psi.ArgumentList.Add(a);
        using var hold = AppHost.Start(psi);
        var waiting = await Await(r => r.RunnerPid == hold.Id && r.Queue == "default" && r.AdmittedUtc is null);
        Assert.Null(waiting.AdmittedUtc);
        await Task.Delay(1_100);

        Clear("occupy000001");
        var admitted = await AwaitRecord(all => all.FirstOrDefault(r =>
            r.RunnerPid == hold.Id && r.AdmittedUtc is not null));
        Assert.True(admitted.QueuedSeconds is > 0,
            $"a hold that waited should record its wait, was {admitted.QueuedSeconds}");
        if (!hold.HasExited) hold.Kill(entireProcessTree: true);
    }

    [UnixFact("runs a real tman apphost")]
    public async Task AHoldOnAPid_ReleasesItsAdmissionWhenThePidExits()
    {
        _home.WriteFile("tman.kdl", "queue \"default\" {\n    cores 4\n    mem 4g\n}\n");
        using var proj = new TempDir();
        Directory.SetCurrentDirectory(proj.Path);

        using var child = Process.Start(new ProcessStartInfo("sleep", "60"))!;
        var psi = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "tman"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = proj.Path,
        };
        psi.Environment["TMAN_HOME"] = _home.Path;
        psi.Environment.Remove(Runner.ParentIdEnvVar);
        foreach (var a in new[] { "hold", "--cores", "1", "--mem", "1g", "--pid", child.Id.ToString() })
            psi.ArgumentList.Add(a);
        using var hold = Process.Start(psi)!;
        try
        {
            var rec = await Await(r => r.Queue == "default" && r.AdmittedUtc is not null);
            Assert.True(hold.WaitForExit(15_000) == false, "the hold ended before its pid");
            child.Kill(entireProcessTree: true);
            // the hold lasts the pid's lifetime: once the pid is gone the admission is released
            var released = await AwaitFinished(rec.Id);
            Assert.True(released.IsFinished);
            Assert.True(hold.WaitForExit(15_000), "the hold did not release when its pid exited");
        }
        finally
        {
            if (!hold.HasExited) hold.Kill(entireProcessTree: true);
        }
    }

    static async Task<RunRecord> AwaitFinished(string id)
    {
        for (var i = 0; i < 300; i++)
        {
            if (Store.Load(id) is { IsFinished: true } r) return r;
            await Task.Delay(50);
        }
        throw new TimeoutException($"run {id} never finished within 15s");
    }

    // ---- (e) the record says what it was admitted with and how long it queued ----

    [UnixFact("runs a real tman apphost")]
    public async Task ARunThatWaited_RecordsWhatItWasAdmittedWithAndHowLongItQueued()
    {
        _home.WriteFile("tman.kdl", "queue \"compile\" {\n    cores 4\n    mem 4g\n}\n");
        // the whole budget is held, so the run must wait rather than be admitted at once
        Occupy("occupy000001", cores: 4, memMb: 4 * 1024, queue: "compile");
        using var proj = new TempDir();
        Directory.SetCurrentDirectory(proj.Path);

        var psi = new ProcessStartInfo(AppHost.Path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = proj.Path,
        };
        psi.Environment["TMAN_HOME"] = _home.Path;
        psi.Environment.Remove(Runner.ParentIdEnvVar);
        foreach (var a in new[] { "run", "--queue", "compile", "--", "sleep", "30" }) psi.ArgumentList.Add(a);
        using var run = AppHost.Start(psi);
        var waiting = await Await(r => r.RunnerPid == run.Id && r.Queue == "compile" && r.AdmittedUtc is null);
        Assert.Null(waiting.AdmittedUtc);
        await Task.Delay(1_100);

        Clear("occupy000001");
        var admitted = await AwaitRecord(all => all.FirstOrDefault(r =>
            r.RunnerPid == run.Id && r.AdmittedUtc is not null));
        Assert.Equal("compile", admitted.Queue);
        Assert.NotNull(admitted.AdmittedCores);
        Assert.NotNull(admitted.AdmittedMemMb);
        Assert.True(admitted.QueuedSeconds is > 0,
            $"QueuedSeconds should be the wait, was {admitted.QueuedSeconds}");

        Assert.Equal(0, await Program.Main(["kill", admitted.Id]));
        run.WaitForExit(15_000);
    }

    static async Task<RunRecord> AwaitRecord(Func<List<RunRecord>, RunRecord> match)
    {
        for (var i = 0; i < 300; i++)
        {
            var all = Store.LoadAll();
            if (match(all) is { } r) return r;
            await Task.Delay(50);
        }
        throw new TimeoutException("the store never reached the expected state within 15s");
    }

    // ---- (a)+(c) an exclusive run that names no queue joins the machine default alone ----

    [UnixFact("runs the sleep binary through Program.Main")]
    public async Task AnExclusiveRunThatNamesNoQueue_HoldsTheMachineDefaultQueueAlone()
    {
        _home.WriteFile("tman.kdl", "queue \"default\" {\n    cores 4\n    mem 4g\n}\n");
        using var proj = new TempDir();
        proj.WriteFile(Config.FileName, """
            alias "perf" {
                command "sleep"
                args "30"
                exclusive true
            }
            """);
        Directory.SetCurrentDirectory(proj.Path);

        var run = Task.Run(() => Program.Main(["perf"]));
        var record = await Await(r => r.Queue == "default" && r.Exclusive && r.AdmittedUtc is not null);
        Assert.Equal("default", record.Queue);
        Assert.True(record.Exclusive);

        Assert.Equal(0, await Program.Main(["kill", record.Id]));
        Assert.Equal(Runner.ExitKilled, await run.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void AnExclusiveRunThatNamesNoQueue_IsRefusedWhenNoMachineDefaultIsDeclared()
    {
        Assert.Throws<FormatException>(() =>
            Program.Join(null, null, exclusive: true));
    }

    // ---- (a) an alias's explicit opt-out exempts it even when the default queue is full ----

    [UnixFact("runs the sleep binary through Program.Main")]
    public async Task AnAliasThatOptsOut_StartsWhileTheMachineDefaultQueueIsFull()
    {
        _home.WriteFile("tman.kdl", "queue \"default\" {\n    cores 1\n    mem 1g\n}\n");
        // the whole default queue's budget is held, so a run that joined it would have to wait
        Occupy("occupy000001", cores: 1, memMb: 1024, queue: "default");
        using var proj = new TempDir();
        proj.WriteFile(Config.FileName, """
            alias "dev" {
                command "sleep"
                args "30"
                no-queue true
            }
            """);
        Directory.SetCurrentDirectory(proj.Path);

        // the alias and shim doors take no tman flags, so the opt-out must live in the config
        var run = Task.Run(() => Program.Main(["dev"]));
        var record = await Await(r => r is { State: RunState.Running } && r.Name == "dev");
        Assert.Null(record.Queue);
        Assert.Null(record.AdmittedUtc);

        Assert.Equal(0, await Program.Main(["kill", record.Id]));
        Assert.Equal(Runner.ExitKilled, await run.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void AnAliasOptOut_IsRefusedWithAQueueOrExclusive()
    {
        using var proj = new TempDir();
        proj.WriteFile(Config.FileName, """
            alias "dev" {
                command "sleep"
                no-queue true
                queue "compile"
            }
            """);
        Assert.Contains("no-queue and queue", Assert.Throws<FormatException>(() => Config.Load(proj.Path)).Message);

        proj.WriteFile(Config.FileName, """
            alias "audit" {
                command "sleep"
                no-queue true
                exclusive true
            }
            """);
        Assert.Contains("no-queue and exclusive", Assert.Throws<FormatException>(() => Config.Load(proj.Path)).Message);
    }

    // ---- (b) waiters do not wake each other ----

    [UnixFact("writes records this process's runner owns")]
    public async Task TwoWaitersBehindAFullBudget_DoNotWakeEachOther()
    {
        _home.WriteFile("tman.kdl", "queue \"q\" {\n    cores 2\n    mem 2g\n}\n");
        var q = Config.Queue("q") with { Timeout = TimeSpan.FromSeconds(30) };
        Occupy("occupy000001", cores: 2, memMb: 2 * 1024);
        Admission.ResetAttemptsForTest();

        // the holder heartbeats like a live runner: its runner saves its record every second
        // (Runner.MonitorTickMs), and every save of a running admitted run is a store event. A
        // waiter that watched every *.json would be woken by each of these and make an attempt —
        // about one per holder save, per waiter. A waiter that watches only its queue's admission
        // events is not woken by them, so its attempt count stays flat while the line does not move.
        using var beat = new CancellationTokenSource();
        var beating = Task.Run(async () =>
        {
            try
            {
                while (!beat.IsCancellationRequested)
                {
                    var r = Store.Load("occupy000001");
                    if (r is not null)
                    {
                        r.HeartbeatUtc = DateTime.UtcNow;
                        Store.Save(r);
                    }
                    await Task.Delay(100, beat.Token);
                }
            }
            catch (OperationCanceledException) { }
        });

        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var a = Runner.NewRecord("go", ["test"], new Caps(), "a", null, "a@/a", "/tmp");
        var b = Runner.NewRecord("go", ["test"], new Caps(), "b", null, "b@/b", "/tmp");
        var cost = new Admission.AdmissionCost(1, 512);
        var wa = Admission.ClaimAdmission(a, q, cost, false, null, null, cancel.Token);
        var wb = Admission.ClaimAdmission(b, q, cost, false, null, null, cancel.Token);
        await Task.WhenAll(wa, wb);
        beat.Cancel();
        await beating;

        // a waiter that saves its own record each pass wakes the other, which saves and wakes it
        // back — over four seconds that is a spin of hundreds; a waiter that parks costs an attempt
        // only when the line actually changes
        var attempts = Interlocked.Read(ref Admission.AttemptsForTest);
        Assert.True(attempts < 40, $"two waiters made {attempts} admission attempts in 4s");
    }

    [UnixFact("writes records this process's runner owns")]
    public async Task SeveralWaitersWokenByOneRelease_AdmitTheFirstInLineQuickly()
    {
        _home.WriteFile("tman.kdl", "queue \"q\" {\n    cores 1\n    mem 1g\n}\n");
        var q = Config.Queue("q") with { Timeout = TimeSpan.FromSeconds(30) };
        // a 1-core, 1 GiB run holds the whole budget
        Occupy("occupy000001", cores: 1, memMb: 1024);

        // five waiters line up behind it, each needing the whole budget, arriving in a known order
        var waiters = new List<RunRecord>();
        for (var i = 0; i < 5; i++)
            waiters.Add(Wait($"waiter{i:D8}", cost: (1, 1024), joinedAgo: TimeSpan.FromSeconds(5 - i)));

        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var claims = waiters.Select((w, i) =>
            Admission.ClaimAdmission(w, q, new Admission.AdmissionCost(1, 1024), false, null, null, cancel.Token)).ToList();

        // give them a moment to park, then release the budget while the gate lock is briefly held.
        // A release with the gate contended is what a real one looks like: one waiter wins it and
        // the rest must retry it, not park on the ten-second backstop while the first in line waits.
        await Task.Delay(500);
        var gate = Store.TryClaimLock(Store.GatePathFor(Admission.QueueKey("q")));
        Assert.NotNull(gate);
        var released = Stopwatch.GetTimestamp();
        Clear("occupy000001");
        await Task.Delay(300);
        Store.ReleaseLock(gate!);

        var admitted = await AwaitRecord(all => all.FirstOrDefault(r => r.Id == waiters[0].Id && r.AdmittedUtc is not null));
        Assert.NotNull(admitted.AdmittedUtc);
        // the first in line was admitted promptly after the release, not after the backstop
        Assert.True(Stopwatch.GetElapsedTime(released) < TimeSpan.FromSeconds(5),
            $"the first waiter took {Stopwatch.GetElapsedTime(released)} to be admitted after one release");
        // the rest are still held by the newly admitted run: only one fits
        Assert.Null(Store.Load(waiters[1].Id)!.AdmittedUtc);

        await Program.Main(["kill", waiters[0].Id]);
        cancel.Cancel();
        try { await Task.WhenAll(claims); } catch (OperationCanceledException) { }
    }

    // ---- (b) order and release ----

    [UnixFact("writes records this process's runner owns")]
    public async Task ASmallRun_StartsAheadOfALargerWaiter_WhenOnlyTheSmallOneFits()
    {
        _home.WriteFile("tman.kdl", "queue \"q\" {\n    cores 4\n    mem 4g\n}\n");
        var q = Config.Queue("q") with { Timeout = TimeSpan.FromSeconds(30) };
        // a 3-core, 3 GiB run already admitted leaves a 1-core, 1 GiB gap
        Occupy("occupy000001", cores: 3, memMb: 3 * 1024);
        // an earlier waiter that needs the whole budget cannot take that gap...
        Wait("largewaiting1", cost: (4, 4 * 1024), joinedAgo: TimeSpan.FromSeconds(1));

        // ...but a smaller newcomer fits, and the DONE-CONDITION's single test starts beside a suite
        var small = Runner.NewRecord("go", ["test"], new Caps(), "small", null, "small@/s", "/tmp");
        Assert.True(await Admission.ClaimAdmission(small, q, new Admission.AdmissionCost(1, 512), false, null, null, default));
        Assert.NotNull(small.AdmittedUtc);
    }

    [UnixFact("writes records this process's runner owns")]
    public async Task ARunWaitingPastTheStarvationBound_IsAdmittedBeforeSmallerArrivals()
    {
        _home.WriteFile("tman.kdl", "queue \"q\" {\n    cores 4\n    mem 4g\n}\n");
        var prev = Admission.StarvationBound;
        Admission.StarvationBound = TimeSpan.FromMilliseconds(100);
        try
        {
            var q = Config.Queue("q") with { Timeout = TimeSpan.FromMilliseconds(700) };
            Occupy("occupy000001", cores: 1, memMb: 1024);
            // an earlier waiter that fits nowhere beside the admitted run, past the bound: a smaller
            // newcomer must not be admitted ahead of it
            var large = Wait("largewaiting1", cost: (4, 4 * 1024), joinedAgo: TimeSpan.FromSeconds(1));

            var small = Runner.NewRecord("go", ["test"], new Caps(), "small", null, "small@/s", "/tmp");
            Assert.False(await Admission.ClaimAdmission(small, q, new Admission.AdmissionCost(1, 512), false, null, null, default),
                "a smaller run was admitted ahead of a waiter past the starvation bound");

            // once the earlier waiter can be admitted, it is
            Clear("occupy000001");
            Assert.True(await Admission.ClaimAdmission(large, q, new Admission.AdmissionCost(4, 4 * 1024), false, null, null, default));
            Assert.NotNull(large.AdmittedUtc);
        }
        finally { Admission.StarvationBound = prev; }
    }

    [UnixFact("writes records this process's runner owns")]
    public async Task ADeadRunnersAdmission_IsReleasedByTheOrphanSweep()
    {
        _home.WriteFile("tman.kdl", "queue \"q\" {\n    cores 4\n    mem 4g\n}\n");
        var q = Config.Queue("q") with { Timeout = TimeSpan.FromSeconds(10) };
        // a run whose runner is gone holds the whole budget: without a sweep it wedges the queue
        Dead("deadrunner01", cores: 4, memMb: 4 * 1024);

        var run = Runner.NewRecord("go", ["test"], new Caps(), "x", null, "x@/x", "/tmp");
        Assert.True(await Admission.ClaimAdmission(run, q, new Admission.AdmissionCost(1, 512), false, null, null, default));
        Assert.NotNull(run.AdmittedUtc);
    }

    RunRecord Wait(string id, (double Cores, long MemMb) cost, TimeSpan joinedAgo)
    {
        var me = ProcUtil.OwnStart();
        var r = new RunRecord
        {
            Id = id, Command = "/bin/sh", Args = [], Group = "wait@" + _home.Path, State = RunState.Queued,
            Queue = "q", AdmittedCores = cost.Cores, AdmittedMemMb = cost.MemMb,
            QueuedUtc = DateTime.UtcNow - joinedAgo, StartedUtc = DateTime.UtcNow, HeartbeatUtc = DateTime.UtcNow,
            Cwd = "/tmp", Pid = Environment.ProcessId,
            RunnerPid = Environment.ProcessId, RunnerStartUtc = me.Utc, RunnerStartTicks = me.Ticks,
        };
        Store.Save(r);
        return r;
    }

    void Dead(string id, double cores, long memMb)
    {
        var me = ProcUtil.OwnStart();
        Store.Save(new RunRecord
        {
            Id = id, Command = "/bin/sh", Args = [], Group = "dead@" + _home.Path, State = RunState.Running,
            Queue = "q", AdmittedUtc = DateTime.UtcNow, AdmittedCores = cores, AdmittedMemMb = memMb,
            QueuedUtc = DateTime.UtcNow, StartedUtc = DateTime.UtcNow, HeartbeatUtc = DateTime.UtcNow,
            Cwd = "/tmp", Pid = 2_000_000_000, RunnerPid = 2_000_000_000,
            RunnerStartUtc = me.Utc, RunnerStartTicks = me.Ticks,
        });
    }
}
