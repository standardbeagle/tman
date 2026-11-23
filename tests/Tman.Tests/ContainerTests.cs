using Tman;
using Xunit;

namespace Tman.Tests;

/// <summary>Docker as a test drives it: a list of labelled containers it holds, and a count of every time it was asked.</summary>
sealed class FakeEngine : IContainerEngine
{
    public List<LabelledContainer> Present { get; } = [];
    public int Lists { get; private set; }
    public List<string> RemovedIds { get; } = [];
    public bool Unreachable { get; set; }
    /// <summary>Ids docker refuses to remove.</summary>
    public HashSet<string> Stuck { get; } = [];

    /// <summary>When set, what docker holds is worked out at each listing rather than kept in <see cref="Present"/>.</summary>
    public Func<List<LabelledContainer>>? ListsFor { get; set; }

    public IReadOnlyList<LabelledContainer>? ListLabelled()
    {
        Lists++;
        if (Unreachable) return null;
        return ListsFor is null ? Present.ToList() : ListsFor().Where(c => !RemovedIds.Contains(c.Id)).ToList();
    }

    public IReadOnlySet<string> Remove(IReadOnlyList<LabelledContainer> containers)
    {
        var gone = new HashSet<string>();
        foreach (var c in containers.Where(c => !Stuck.Contains(c.Id)))
        {
            Present.RemoveAll(p => p.Id == c.Id);
            RemovedIds.Add(c.Id);
            gone.Add(c.Id);
        }
        return gone;
    }
}

/// <summary>
/// Which labelled containers belong to a run that is over. The rule is the whole of what keeps tman
/// from removing a fixture something is still using, so each case is pinned against a store holding
/// exactly the records it names.
/// </summary>
[Collection("cwd")]
public class ContainerOwnershipTests : IDisposable
{
    readonly TempDir _home = new();
    readonly TempDir _otherHome = new();
    readonly string? _prevHome = Environment.GetEnvironmentVariable("TMAN_HOME");

    public ContainerOwnershipTests() => Environment.SetEnvironmentVariable("TMAN_HOME", _home.Path);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TMAN_HOME", _prevHome);
        _home.Dispose();
        _otherHome.Dispose();
    }

    internal static RunRecord Run(string id, RunState state, string? parent = null) => new()
    {
        Id = id,
        Command = "/bin/true",
        Args = [],
        State = state,
        ParentId = parent,
        Pid = 4242,
        StartedUtc = DateTime.UtcNow,
        HeartbeatUtc = DateTime.UtcNow,
    };

    internal static LabelledContainer Box(string runId, string name = "fixture-pg", TimeSpan? age = null) =>
        new("id-" + name, name, runId, DateTime.UtcNow - (age ?? TimeSpan.FromMinutes(1)));

    static List<LabelledContainer> Doomed(LabelledContainer c, params RunRecord[] records) =>
        Containers.Doomed([c], records, DateTime.UtcNow);

    [Fact]
    public void AFinishedRunsContainer_IsRemoved()
    {
        var run = Run(RunRecord.NewId(), RunState.Exited);
        Assert.Single(Doomed(Box(run.Id), run));
    }

    [Theory]
    [InlineData(RunState.Running)]
    [InlineData(RunState.Queued)]
    public void ALiveRunsContainer_IsKept(RunState state)
    {
        var run = Run(RunRecord.NewId(), state);
        Assert.Empty(Doomed(Box(run.Id), run));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void AContainerWithAnEmptyLabel_IsNotTmans(string label) =>
        Assert.Empty(Doomed(Box(label, age: TimeSpan.FromDays(3))));

    [Fact]
    public void ANestedRunsContainer_IsKept_WhileItsOuterRunLives()
    {
        // a nested tman hands its children its own id, so the fixture carries the nested run's
        var outer = Run(RunRecord.NewId(), RunState.Running);
        var nested = Run(RunRecord.NewId(), RunState.Exited, parent: outer.Id);

        Assert.Empty(Doomed(Box(nested.Id), outer, nested));
    }

    [Fact]
    public void ANestedRunsContainer_IsRemoved_OnceEveryRunAboveItHasEnded()
    {
        var top = Run(RunRecord.NewId(), RunState.Running);
        var middle = Run(RunRecord.NewId(), RunState.Exited, parent: top.Id);
        var inner = Run(RunRecord.NewId(), RunState.Exited, parent: middle.Id);

        Assert.Empty(Doomed(Box(inner.Id), top, middle, inner));
        top.State = RunState.Exited;
        Assert.Single(Doomed(Box(inner.Id), top, middle, inner));
    }

    [Fact]
    public void AnOuterRunsContainer_IsKept_WhileARunAboveItLives_EvenOnceItEnded()
    {
        var outer = Run(RunRecord.NewId(), RunState.Running);
        var nested = Run(RunRecord.NewId(), RunState.Running, parent: outer.Id);

        Assert.Empty(Doomed(Box(outer.Id), outer, nested));
    }

    [Fact]
    public void AParentInAnotherStore_DoesNotHoldANestedRunsContainer()
    {
        // the suite's own TMAN_RUN_ID reaches a test's tman, whose store has no record of it
        var nested = Run(RunRecord.NewId(), RunState.Exited, parent: "a-run-of-another-store");
        Assert.Single(Doomed(Box(nested.Id), nested));
    }

    [Fact]
    public void AParentWhoseRecordCannotBeRead_HoldsItsChildsContainer()
    {
        var parent = RunRecord.NewId();
        UnreadableRecord(parent);
        var nested = Run(RunRecord.NewId(), RunState.Exited, parent: parent);

        Assert.Empty(Doomed(Box(nested.Id), nested));
    }

    [Fact]
    public void AnOldContainerOfAnIdThisStoreIssued_WithNoRecord_IsRemoved()
    {
        // its record was pruned before docker could be reached, or the store was wiped
        Assert.Single(Doomed(Box(RunRecord.NewId(), age: TimeSpan.FromHours(2))));
    }

    [Fact]
    public void AYoungContainerWithNoRecord_IsKept()
    {
        // a run's record exists before its child does: a young container without one is a record missed
        Assert.Empty(Doomed(Box(RunRecord.NewId(), age: TimeSpan.FromMinutes(5))));
    }

    [Fact]
    public void AContainerWithNoRecordAndNoCreationTime_IsKept()
    {
        var c = new LabelledContainer("id-x", "x", RunRecord.NewId(), CreatedUtc: null);
        Assert.Empty(Containers.Doomed([c], [], DateTime.UtcNow));
    }

    [Fact]
    public void AContainerOfAnotherStoresRun_IsKept()
    {
        Environment.SetEnvironmentVariable("TMAN_HOME", _otherHome.Path);
        var theirs = RunRecord.NewId();
        Environment.SetEnvironmentVariable("TMAN_HOME", _home.Path);

        Assert.Empty(Doomed(Box(theirs, age: TimeSpan.FromDays(3))));
    }

    [Fact]
    public void AContainerOfAnIdFromBeforeTags_WithNoRecord_IsKept() =>
        Assert.Empty(Doomed(Box("0123456789ab", age: TimeSpan.FromDays(3))));

    [Fact]
    public void AnIdFromBeforeTags_WithARecordHere_IsStillThisStoresToRemove()
    {
        // a run recorded by the tman before this one: its record is the proof it is this store's
        var run = Run("0123456789ab", RunState.Exited);
        Assert.Single(Doomed(Box(run.Id), run));
    }

    [Fact]
    public void ARecordThatCannotBeRead_IsNotTakenForAMissingOne()
    {
        var id = RunRecord.NewId();
        UnreadableRecord(id);
        Assert.Empty(Doomed(Box(id, age: TimeSpan.FromDays(3))));
    }

    void UnreadableRecord(string id)
    {
        Store.EnsureDirs();
        File.WriteAllText(Path.Combine(_home.Path, "runs", id + ".json"), "{ written by a newer tman");
    }

    [Fact]
    public void AnEndedRun_IsDue_UntilItsContainersAreSwept()
    {
        var run = Run(RunRecord.NewId(), RunState.Exited);
        Assert.True(Containers.Due(run, [run]));
        run.ContainersSwept = true;
        Assert.False(Containers.Due(run, [run]));
    }

    [Fact]
    public void ALiveRun_IsNotDue() =>
        Assert.False(Containers.Due(Run(RunRecord.NewId(), RunState.Running), []));

    [Fact]
    public void ANestedRun_IsNotDue_WhileItsOuterRunLives()
    {
        var outer = Run(RunRecord.NewId(), RunState.Running);
        var nested = Run(RunRecord.NewId(), RunState.Exited, parent: outer.Id);
        Assert.False(Containers.Due(nested, [outer, nested]));
    }

    [Fact]
    public void ARunThatNeverStartedAChild_IsNeverDue()
    {
        var waiter = Run(RunRecord.NewId(), RunState.Killed);
        waiter.Pid = 0;
        var hold = Run(RunRecord.NewId(), RunState.Exited);
        hold.Hold = true;

        Assert.False(Containers.Due(waiter, [waiter]));
        Assert.False(Containers.Due(hold, [hold]));
    }
}

/// <summary>
/// When tman asks docker. Every command sweeps, `tman list` included, so docker is asked only when an
/// ended run is waiting on it or the hourly look is due — and never inside the backoff after docker
/// failed to answer.
/// </summary>
[Collection("cwd")]
public class ContainerSweepTests : IDisposable
{
    readonly TempDir _home = new();
    readonly string? _prevHome = Environment.GetEnvironmentVariable("TMAN_HOME");
    readonly string? _prevParent = Environment.GetEnvironmentVariable(Runner.ParentIdEnvVar);
    readonly FakeEngine _docker = new();
    static readonly TimeSpan Retain = TimeSpan.FromHours(24);

    public ContainerSweepTests()
    {
        Environment.SetEnvironmentVariable("TMAN_HOME", _home.Path);
        Environment.SetEnvironmentVariable(Runner.ParentIdEnvVar, null);
        Containers.FindEngine = () => _docker;
    }

    public void Dispose()
    {
        Containers.FindEngine = () => null;
        Environment.SetEnvironmentVariable("TMAN_HOME", _prevHome);
        Environment.SetEnvironmentVariable(Runner.ParentIdEnvVar, _prevParent);
        _home.Dispose();
    }

    static RunRecord Ended(string? parent = null, TimeSpan? age = null)
    {
        var r = ContainerOwnershipTests.Run(RunRecord.NewId(), RunState.Exited, parent);
        r.HeartbeatUtc = r.StartedUtc = DateTime.UtcNow - (age ?? TimeSpan.FromMinutes(1));
        Store.Save(r);
        return r;
    }

    /// <summary>A run whose runner is this test process, which the reaper leaves to it as live.</summary>
    static RunRecord Live()
    {
        var r = ContainerOwnershipTests.Run(RunRecord.NewId(), RunState.Running);
        r.RunnerPid = Environment.ProcessId;
        Store.Save(r);
        return r;
    }

    [Fact]
    public void ASweep_WithNoEndedRunWaiting_AsksDockerOnlyOnceAnHour()
    {
        Reaper.Sweep(Retain, quiet: true);
        Reaper.Sweep(Retain, quiet: true);
        Reaper.Sweep(Retain, quiet: true);

        Assert.Equal(1, _docker.Lists);
    }

    [Fact]
    public void TheHourlyLook_ComesRoundOnceItsIntervalIsUp()
    {
        Reaper.Sweep(Retain, quiet: true);
        File.SetLastWriteTimeUtc(Containers.CheckedStampPath, DateTime.UtcNow - TimeSpan.FromHours(2));

        Reaper.Sweep(Retain, quiet: true);

        Assert.Equal(2, _docker.Lists);
    }

    [Fact]
    public void ASweep_RemovesAnEndedRunsContainers_AndRecordsWhatWent()
    {
        Reaper.Sweep(Retain, quiet: true);
        var run = Ended();
        _docker.Present.Add(ContainerOwnershipTests.Box(run.Id, "worktrack2-testpg"));

        var swept = Reaper.Sweep(Retain, quiet: true);

        Assert.Equal(2, _docker.Lists);
        Assert.Equal(["id-worktrack2-testpg"], _docker.RemovedIds);
        Assert.Equal("worktrack2-testpg", Assert.Single(swept.RemovedContainers).Name);
        var saved = Store.Load(run.Id)!;
        Assert.True(saved.ContainersSwept);
        Assert.Equal(["worktrack2-testpg"], saved.RemovedContainers);

        // swept: the next command has nothing waiting on docker
        Reaper.Sweep(Retain, quiet: true);
        Assert.Equal(2, _docker.Lists);
    }

    [Fact]
    public void ARunWhoseRunnerDied_HasItsContainersRemovedInTheSweepThatFindsIt()
    {
        // suspend or SIGKILL took the runner and its child; no runner is left to clean up after it
        var r = ContainerOwnershipTests.Run(RunRecord.NewId(), RunState.Running);
        r.Pid = 2147483646;
        r.ChildStartUtc = DateTime.UtcNow;
        r.RunnerPid = 2147483645;
        r.RunnerStartUtc = DateTime.UtcNow;
        Store.Save(r);
        _docker.Present.Add(ContainerOwnershipTests.Box(r.Id, "testsvc-harvest-kc"));

        Reaper.Sweep(Retain, quiet: true);

        Assert.Equal(["id-testsvc-harvest-kc"], _docker.RemovedIds);
    }

    [Fact]
    public void ADockerThatCannotAnswer_IsLeftAloneForTheBackoff()
    {
        _docker.Unreachable = true;
        var run = Ended();

        Reaper.Sweep(Retain, quiet: true);
        Reaper.Sweep(Retain, quiet: true);

        Assert.Equal(1, _docker.Lists);
        Assert.False(Store.Load(run.Id)!.ContainersSwept);
    }

    [Fact]
    public void TheBackoff_EndsWhenItsWindowIsUp()
    {
        _docker.Unreachable = true;
        var run = Ended();
        Reaper.Sweep(Retain, quiet: true);
        File.SetLastWriteTimeUtc(Containers.BackoffStampPath, DateTime.UtcNow - TimeSpan.FromMinutes(11));
        _docker.Unreachable = false;
        _docker.Present.Add(ContainerOwnershipTests.Box(run.Id));

        Reaper.Sweep(Retain, quiet: true);

        Assert.Equal(2, _docker.Lists);
        Assert.Single(_docker.RemovedIds);
    }

    [Fact]
    public void ABackoffStampFromTheFuture_DoesNotSilenceDocker()
    {
        // the wall clock stepped back after docker failed: the stamp is not a window still running
        _docker.Unreachable = true;
        Ended();
        Reaper.Sweep(Retain, quiet: true);
        File.SetLastWriteTimeUtc(Containers.BackoffStampPath, DateTime.UtcNow + TimeSpan.FromHours(5));

        Reaper.Sweep(Retain, quiet: true);

        Assert.Equal(2, _docker.Lists);
    }

    [Fact]
    public void AContainerDockerWillNotRemove_StartsTheBackoff_AndKeepsItsRunWaiting()
    {
        var run = Ended();
        var stuck = ContainerOwnershipTests.Box(run.Id);
        _docker.Present.Add(stuck);
        _docker.Stuck.Add(stuck.Id);

        Reaper.Sweep(Retain, quiet: true);
        Reaper.Sweep(Retain, quiet: true);

        Assert.Equal(1, _docker.Lists);
        Assert.False(Store.Load(run.Id)!.ContainersSwept);
    }

    [Fact]
    public void Clean_AsksDockerWhateverTheGatesSay()
    {
        _docker.Unreachable = true;
        Ended();
        Reaper.Sweep(Retain, quiet: true);
        _docker.Unreachable = false;

        Reaper.Sweep(Retain, quiet: true, containersNow: true);

        Assert.Equal(2, _docker.Lists);
    }

    [Fact]
    public void AHostWithNoDocker_SettlesItsEndedRuns_RatherThanLookingForDockerEveryCommand()
    {
        Containers.FindEngine = () => null;
        var run = Ended();

        Reaper.Sweep(Retain, quiet: true);

        Assert.True(Store.Load(run.Id)!.ContainersSwept);
    }

    [Fact]
    public void ANestedRunsRecord_OutlivesTheRetention_WhileItsOuterRunLives()
    {
        // pruned, its container would read as an orphan's and go while the outer run still uses it
        var outer = Live();
        var nested = Ended(parent: outer.Id, age: TimeSpan.FromHours(48));
        _docker.Present.Add(ContainerOwnershipTests.Box(nested.Id, age: TimeSpan.FromHours(48)));

        var swept = Reaper.Sweep(Retain, quiet: true);

        Assert.Equal(0, swept.Pruned);
        Assert.NotNull(Store.Load(nested.Id));
        Assert.Empty(_docker.RemovedIds);
    }

    [Fact]
    public void ARunsEnd_RemovesItsContainers_AndSaysSoOnStderr()
    {
        var run = Ended();
        _docker.Present.Add(ContainerOwnershipTests.Box(run.Id, "beagleterm-ssh-1"));

        var err = CaptureStderr(() => Containers.AfterRun(run));

        Assert.Equal(["id-beagleterm-ssh-1"], _docker.RemovedIds);
        Assert.Contains("removed 1 container", err);
        Assert.Contains("beagleterm-ssh-1", err);
        Assert.Contains(run.Id, err);
    }

    [Fact]
    public void ANestedRunsEnd_LeavesItsContainersToItsOuterRun_WhichRemovesThemAtItsOwnEnd()
    {
        var outer = Live();
        var nested = Ended(parent: outer.Id);
        _docker.Present.Add(ContainerOwnershipTests.Box(nested.Id, "fixture-pg"));

        var err = CaptureStderr(() => Containers.AfterRun(nested));

        Assert.Empty(_docker.RemovedIds);
        Assert.Contains(outer.Id, err);
        Assert.False(Store.Load(nested.Id)!.ContainersSwept);

        outer.State = RunState.Exited;
        Store.Save(outer);
        Containers.AfterRun(outer);

        Assert.Equal(["id-fixture-pg"], _docker.RemovedIds);
        Assert.Equal(["fixture-pg"], Store.Load(nested.Id)!.RemovedContainers);
    }

    [Fact]
    public void ANestedRunWithNoContainers_IsSettledAtItsEnd_SoItsRecordPrunesAsUsual()
    {
        var outer = Live();
        var nested = Ended(parent: outer.Id);

        Containers.AfterRun(nested);

        Assert.True(Store.Load(nested.Id)!.ContainersSwept);
    }

    [Fact]
    public void ARunsEnd_InsideTheBackoff_DoesNotAskDocker()
    {
        _docker.Unreachable = true;
        Ended();
        Reaper.Sweep(Retain, quiet: true);
        var run = Ended();

        Containers.AfterRun(run);

        Assert.Equal(1, _docker.Lists);
    }

    [UnixFact("supervises true")]
    public async Task ARunsSupervisor_RemovesItsContainersOnceItsOutcomeIsRecorded()
    {
        // the fixture's container is "started" the moment the run is recorded: docker then lists it
        // labelled with the run, as a fixture's own `docker run --label` would
        _docker.ListsFor = () => Store.LoadAll().Select(r => ContainerOwnershipTests.Box(r.Id, "fixture-" + r.Id)).ToList();

        var exit = await Runner.RunAsync("true", [], new Caps(), null, null);

        Assert.Equal(0, exit);
        var run = Assert.Single(Store.LoadAll());
        Assert.Equal(["fixture-" + run.Id], run.RemovedContainers);
    }

    static string CaptureStderr(Action act)
    {
        var err = new StringWriter();
        var prev = Console.Error;
        Console.SetError(err);
        try { act(); }
        finally { Console.SetError(prev); }
        return err.ToString();
    }
}

/// <summary>Finding the docker CLI, and reading what it prints.</summary>
public class DockerCliTests : IDisposable
{
    readonly TempDir _bin = new();

    public void Dispose() => _bin.Dispose();

    [Fact]
    public void OnWindows_AWrapperWindowsCanStart_IsTaken_NotABashScriptOfTheSameName()
    {
        _bin.WriteFile("docker", "#!/bin/sh\nexec wsl docker \"$@\"\n");
        var cmd = _bin.WriteFile("docker.cmd", "@wsl docker %*\r\n");

        Assert.Equal(cmd, DockerCli.Resolve("docker", _bin.Path, windows: true));
    }

    [Fact]
    public void OnWindows_DockerDesktopsExe_IsTakenBeforeAWrapper()
    {
        _bin.WriteFile("docker.cmd", "@wsl docker %*\r\n");
        var exe = _bin.WriteFile("docker.exe", "");

        Assert.Equal(exe, DockerCli.Resolve("docker", _bin.Path, windows: true));
    }

    [Fact]
    public void OnWindows_OnlyABashScript_IsNoDocker()
    {
        _bin.WriteFile("docker", "#!/bin/sh\n");
        Assert.Null(DockerCli.Resolve("docker", _bin.Path, windows: true));
    }

    [Fact]
    public void NoDockerOnThePath_IsNoDocker()
    {
        Assert.Null(DockerCli.Resolve("docker", _bin.Path, windows: OperatingSystem.IsWindows()));
        Assert.Null(DockerCli.Resolve("docker", null, windows: OperatingSystem.IsWindows()));
    }

    [UnixFact("reads the Unix execute bit")]
    public void OnUnix_OnlyAnExecutableFile_IsDocker()
    {
        var docker = _bin.WriteFile("docker", "#!/bin/sh\n");
        Assert.Null(DockerCli.Resolve("docker", _bin.Path, windows: false));

        File.SetUnixFileMode(docker, File.GetUnixFileMode(docker) | UnixFileMode.UserExecute);
        Assert.Equal(docker, DockerCli.Resolve("docker", _bin.Path, windows: false));
    }

    [Fact]
    public void Inspect_IsReadForIdNameLabelAndCreationTime()
    {
        const string json = """
            [{"Id":"5f1c9e0b2a7d","Created":"2026-11-23T09:15:42.123456789Z","Name":"/worktrack2-testpg",
              "Config":{"Labels":{"tman.run":"0a1b2c3d4e5f6789","com.docker.compose.project":"wt"}}},
             {"Id":"77aa","Created":"2026-11-23T09:15:42Z","Name":"/no-labels","Config":{"Labels":null}}]
            """;

        var parsed = DockerCli.ParseInspect(json)!;

        Assert.Equal(2, parsed.Count);
        Assert.Equal(new LabelledContainer("5f1c9e0b2a7d", "worktrack2-testpg", "0a1b2c3d4e5f6789",
            new DateTime(2026, 11, 23, 9, 15, 42, DateTimeKind.Utc).AddTicks(1234567)), parsed[0]);
        Assert.Equal("", parsed[1].RunId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Error response from daemon: No such container: 5f1c")]
    [InlineData("{\"Id\":\"x\"}")]
    public void InspectOutputThatIsNotDockersArray_IsNoAnswer(string output) =>
        Assert.Null(DockerCli.ParseInspect(output));
}

/// <summary>TMAN_DOCKER, read from the environment every tman inherits.</summary>
[Collection("cwd")]
public class DockerCliSwitchTests : IDisposable
{
    readonly string? _prev = Environment.GetEnvironmentVariable(Containers.EngineEnvVar);

    public void Dispose() => Environment.SetEnvironmentVariable(Containers.EngineEnvVar, _prev);

    [Theory]
    [InlineData("off")]
    [InlineData("OFF")]
    public void TmanDockerOff_TurnsCleanupOff(string value)
    {
        Environment.SetEnvironmentVariable(Containers.EngineEnvVar, value);
        Assert.Null(DockerCli.Find());
    }

    [Fact]
    public void TmanDocker_NamingNothingRunnable_IsNoDocker()
    {
        Environment.SetEnvironmentVariable(Containers.EngineEnvVar, "tman-no-such-docker-cli");
        Assert.Null(DockerCli.Find());
    }
}
