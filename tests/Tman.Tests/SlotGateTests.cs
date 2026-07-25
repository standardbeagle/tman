using Tman;
using Xunit;

namespace Tman.Tests;

/// <summary>
/// The parallel-slot gate. These assert on recorded facts — which run records overlap in time,
/// which run won a name — rather than on how long a wave took, so they do not depend on the
/// machine being fast enough for a timing window to hold.
/// </summary>
[Collection("cwd")]
public class SlotGateTests : IDisposable
{
    static bool Unix => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    readonly TempDir _home = new();
    readonly string? _prevHome = Environment.GetEnvironmentVariable("TMAN_HOME");
    readonly string? _prevParent = Environment.GetEnvironmentVariable(Runner.ParentIdEnvVar);
    readonly string _scope;

    public SlotGateTests()
    {
        Environment.SetEnvironmentVariable("TMAN_HOME", _home.Path);
        Environment.SetEnvironmentVariable(Runner.ParentIdEnvVar, null);
        _scope = _home.Mkdir("proj");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TMAN_HOME", _prevHome);
        Environment.SetEnvironmentVariable(Runner.ParentIdEnvVar, _prevParent);
        _home.Dispose();
    }

    string Group(string command) => RunKey.For(null, Canon.ResolveCommand(command), _scope);

    Task<int> Sleep(string seconds, Caps caps, string? name = null) =>
        Program.GatedRun("sleep", new[] { seconds }, caps, name, null, replace: false, _scope);

    /// <summary>
    /// Starts n runs that all reach the gate at the same instant. Awaiting the calls directly would
    /// not do it: GatedRun runs synchronously into Runner.RunAsync, so the first caller has already
    /// written its record before the second one looks — exactly the interleaving the gate must not
    /// rely on.
    /// </summary>
    Task<int[]> RaceToTheGate(int n, Func<Task<int>> start)
    {
        var atTheGate = new Barrier(n);
        return Task.WhenAll(Enumerable.Range(0, n).Select(_ => Task.Run(() =>
        {
            atTheGate.SignalAndWait();
            return start();
        })));
    }

    /// <summary>Highest number of run records whose lifetimes overlapped at any instant.</summary>
    static int PeakOverlap(IEnumerable<RunRecord> runs)
    {
        var edges = runs
            .SelectMany(r => new[] { (At: r.StartedUtc, Delta: 1), (At: r.HeartbeatUtc, Delta: -1) })
            .OrderBy(e => e.At).ThenBy(e => e.Delta)
            .ToList();
        int current = 0, peak = 0;
        foreach (var e in edges) peak = Math.Max(peak, current += e.Delta);
        return peak;
    }

    [Fact]
    public void TryAcquireSlot_HandsOutExactlyMaxParallelSlots()
    {
        const string group = "test@/repo";

        var first = Store.TryAcquireSlot(group, 2);
        var second = Store.TryAcquireSlot(group, 2);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first.Name, second.Name);
        Assert.Null(Store.TryAcquireSlot(group, 2));

        Store.ReleaseLock(second);
        // a released slot goes straight back into circulation
        var reused = Store.TryAcquireSlot(group, 2);
        Assert.NotNull(reused);
        Store.ReleaseLock(first);
        Store.ReleaseLock(reused);
    }

    [Fact]
    public void TryAcquireSlot_ReclaimsTheSlotOfADeadRunner()
    {
        const string group = "test@/repo";
        var held = Store.TryAcquireSlot(group, 1);
        Assert.NotNull(held);
        var path = held.Name;
        held.Dispose();
        // the runner died holding its slot: the file outlives the process that stamped it
        File.WriteAllText(path, $"2147483646 {DateTime.UtcNow:O}\n");

        var reclaimed = Store.TryAcquireSlot(group, 1);

        Assert.NotNull(reclaimed);
        Store.ReleaseLock(reclaimed);
    }

    [Fact]
    public void StaleSlotLocks_AreSweptLikeDedupLocks()
    {
        var slot = Store.TryAcquireSlot("test@/repo", 1);
        Assert.NotNull(slot);
        var path = slot.Name;
        slot.Dispose();
        File.WriteAllText(path, $"2147483646 {DateTime.UtcNow:O}\n");

        Assert.Equal(1, Store.PruneStaleLocks());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task NestedRun_TakesNoSlot()
    {
        if (!Unix) return;

        var held = Store.TryAcquireSlot(Group("sleep"), 1);
        Assert.NotNull(held);
        var caps = new Caps { MaxParallel = 1, QueueTimeout = TimeSpan.Zero };

        Assert.Equal(Runner.ExitKilled, await Sleep("0", caps));
        // a supervised process re-entering tman would otherwise queue behind its own parent
        Environment.SetEnvironmentVariable(Runner.ParentIdEnvVar, "parent-run-id");
        Assert.Equal(0, await Sleep("0", caps));

        Store.ReleaseLock(held);
    }

    [Fact]
    public async Task RunsStartedAtTheSameInstant_NeverExceedMaxParallel()
    {
        if (!Unix) return;

        var caps = new Caps { MaxParallel = 2, QueueTimeout = TimeSpan.FromSeconds(30) };
        var exits = await RaceToTheGate(3, () => Sleep("1", caps));

        Assert.All(exits, e => Assert.Equal(0, e));
        var records = Store.LoadAll().Where(r => r.Group == Group("sleep")).ToList();
        Assert.Equal(3, records.Count);
        Assert.Equal(2, PeakOverlap(records));
    }

    [Fact]
    public async Task ConcurrentRunsOfOneName_LeaveExactlyOneWinner()
    {
        if (!Unix) return;

        var caps = new Caps { QueueTimeout = TimeSpan.FromSeconds(30) };
        var exits = await RaceToTheGate(5, () => Sleep("1", caps, name: "dedup"));

        Assert.Equal(1, exits.Count(e => e == 0));
        Assert.Equal(4, exits.Count(e => e == Runner.ExitKilled));
    }
}
