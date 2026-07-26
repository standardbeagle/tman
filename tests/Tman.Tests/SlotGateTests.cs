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

    /// <summary>
    /// Two runners reclaiming the same dead runner's slot at once. Breaking a stale lock and then
    /// creating a fresh one is two steps, and each racer can perform the break between the other's
    /// break and create — leaving both holding a file they created and believing they own the slot.
    /// The window is a handful of instructions wide, so the race is driven to it repeatedly rather
    /// than assumed to be hit once.
    /// </summary>
    [Fact]
    public void ConcurrentReclaimOfADeadRunnersSlot_AdmitsOnlyOne()
    {
        const string group = "test@/repo";
        Store.EnsureDirs();
        var path = Store.SlotPathFor(group, 0);
        var doubleClaims = 0;

        for (var i = 0; i < 400; i++)
        {
            // the slot of a runner that was killed while holding it
            File.WriteAllText(path, $"2147483646 {DateTime.UtcNow:O}\n");

            var atTheGate = new Barrier(2);
            var claims = new FileStream?[2];
            var racers = Enumerable.Range(0, 2)
                .Select(t => new Thread(() =>
                {
                    atTheGate.SignalAndWait();
                    claims[t] = Store.TryAcquireSlot(group, 1);
                }))
                .ToList();
            foreach (var r in racers) r.Start();
            foreach (var r in racers) r.Join();

            if (claims.Count(c => c is not null) > 1) doubleClaims++;
            foreach (var c in claims) c?.Dispose();
        }

        Assert.Equal(0, doubleClaims);
    }

    /// <summary>
    /// A slot that cannot be opened for a real reason — no space, no directory, bad IO — is not a
    /// busy slot. Reporting it as one buries the fault behind a full queue timeout and a "all N
    /// slots busy" message that names the wrong problem.
    /// </summary>
    [UnixFact("dangles the slot through a symlink, which needs POSIX symlink semantics without elevation")]
    public void SlotThatCannotBeOpenedAtAll_ReportsTheIoFaultRatherThanBusy()
    {
        const string group = "test@/repo";
        Store.EnsureDirs();
        File.CreateSymbolicLink(Store.SlotPathFor(group, 0), Path.Combine(_home.Path, "gone", "slot"));

        Assert.Throws<FileNotFoundException>(() => Store.TryAcquireSlot(group, 1));
    }

    [Fact]
    public void ASlotLeftByADeadRunner_IsReclaimedInPlaceRatherThanSwept()
    {
        var slot = Store.TryAcquireSlot("test@/repo", 1);
        Assert.NotNull(slot);
        var path = slot.Name;
        slot.Dispose();
        File.WriteAllText(path, $"2147483646 {DateTime.UtcNow:O}\n");

        var reclaimed = Store.TryAcquireSlot("test@/repo", 1);

        Assert.NotNull(reclaimed);
        Assert.Equal(path, reclaimed.Name);
        Assert.True(File.Exists(path));
        Store.ReleaseLock(reclaimed);
    }

    [UnixFact("gates real runs of the sleep binary")]
    public async Task NestedRun_TakesNoSlot()
    {
        var held = Store.TryAcquireSlot(Group("sleep"), 1);
        Assert.NotNull(held);
        var caps = new Caps { MaxParallel = 1, QueueTimeout = TimeSpan.Zero };

        Assert.Equal(Runner.ExitKilled, await Sleep("0", caps));
        // a supervised process re-entering tman would otherwise queue behind its own parent
        Environment.SetEnvironmentVariable(Runner.ParentIdEnvVar, "parent-run-id");
        Assert.Equal(0, await Sleep("0", caps));

        Store.ReleaseLock(held);
    }

    [UnixFact("races real runs of the sleep binary")]
    public async Task RunsStartedAtTheSameInstant_NeverExceedMaxParallel()
    {
        var caps = new Caps { MaxParallel = 2, QueueTimeout = TimeSpan.FromSeconds(30) };
        var exits = await RaceToTheGate(3, () => Sleep("1", caps));

        Assert.All(exits, e => Assert.Equal(0, e));
        var records = Store.LoadAll().Where(r => r.Group == Group("sleep")).ToList();
        Assert.Equal(3, records.Count);
        Assert.Equal(2, PeakOverlap(records));
    }

    [UnixFact("races real runs of the sleep binary")]
    public async Task ConcurrentRunsOfOneName_LeaveExactlyOneWinner()
    {
        var caps = new Caps { QueueTimeout = TimeSpan.FromSeconds(30) };
        var exits = await RaceToTheGate(5, () => Sleep("1", caps, name: "dedup"));

        Assert.Equal(1, exits.Count(e => e == 0));
        Assert.Equal(4, exits.Count(e => e == Runner.ExitKilled));
    }

    /// <summary>
    /// A child that outlived the runner which started it. The kernel handed the name back when
    /// that runner died, so the lock says the name is free while the work it stood for is still
    /// running — the one thing a lock cannot see. The run is refused, and the name it briefly held
    /// is left free for the sweep's reaping to hand on rather than pinned by the refusal.
    /// </summary>
    [UnixFact("needs a real orphaned sleep child and its /proc start time")]
    public async Task ARunWhoseChildOutlivedItsRunner_IsRefusedAndLeavesTheNameFree()
    {
        var group = RunKey.For("dedup", Canon.ResolveCommand("sleep"), _scope);
        using var orphan = System.Diagnostics.Process.Start("sleep", "30")
            ?? throw new IOException("could not start sleep");
        try
        {
            Store.Save(new RunRecord
            {
                Id = "orphanchild1",
                Name = "dedup",
                Pid = orphan.Id,
                ChildStartUtc = ProcUtil.StartTimeUtc(orphan.Id) ?? DateTime.UtcNow,
                Command = Canon.ResolveCommand("sleep"),
                Args = new[] { "30" },
                Group = group,
                StartedUtc = DateTime.UtcNow,
                HeartbeatUtc = DateTime.UtcNow,
            });

            Assert.Equal(Runner.ExitKilled, await Sleep("0", new Caps(), name: "dedup"));

            var free = Store.TryAcquireNameLock(group);
            Assert.NotNull(free);
            Store.ReleaseLock(free);
        }
        finally
        {
            orphan.Kill(entireProcessTree: true);
            orphan.WaitForExit();
        }
    }

    /// <summary>
    /// --replace kills the run holding a name and then waits for that run's runner to let the name
    /// go, because taking it any other way is the unlink this gate was fixed to stop making. When
    /// it is not given up in time, the replacement does not happen — running anyway would be two
    /// runs of the name, which is what --replace exists to prevent.
    /// </summary>
    [UnixFact("drives the production gate against the sleep binary")]
    public async Task AReplaceThatIsNeverGivenTheName_DoesNotRun()
    {
        var group = RunKey.For("dedup", Canon.ResolveCommand("sleep"), _scope);
        var heldByAnotherRunner = Store.TryAcquireNameLock(group);
        Assert.NotNull(heldByAnotherRunner);

        var exit = await Program.GatedRun(
            "sleep", new[] { "0" }, new Caps { QueueTimeout = TimeSpan.Zero },
            "dedup", null, replace: true, _scope);

        Assert.Equal(Runner.ExitKilled, exit);
        Assert.Empty(Store.LoadAll());
        Store.ReleaseLock(heldByAnotherRunner);
    }

    /// <summary>
    /// The deterministic half of the race below. A lock name is only exclusive while every runner
    /// reaching it opens the same file: unlink the name and create it again, and two runners hold
    /// two different files that answer to it. The probe is a hard link, which follows the inode
    /// rather than the name, so it says which of the two happened without depending on a window
    /// being hit — on POSIX an unlink goes through whether or not somebody holds the file, so no
    /// exclusive hold makes break-then-create safe.
    /// </summary>
    [UnixFact("the inode probe is a hard link made with ln, and the unlink it rules out is POSIX")]
    public async Task ReclaimingADeadRunnersName_TakesOverItsLockFileRatherThanReplacingIt()
    {
        Store.EnsureDirs();
        var lockPath = Store.LockPathFor(RunKey.For("dedup", Canon.ResolveCommand("sleep"), _scope));
        // the dedup lock of a runner that was killed while holding it
        File.WriteAllText(lockPath, $"2147483646 {DateTime.UtcNow:O}\n");
        var sameInode = Path.Combine(_home.Path, "reclaimed-lock-inode");
        HardLink(lockPath, sameInode);

        Assert.Equal(0, await Sleep("0", new Caps(), name: "dedup"));

        // a claim that unlinked the name and created a fresh file stamped that other file instead
        Assert.StartsWith($"{Environment.ProcessId} ", File.ReadAllText(sameInode));
    }

    /// <summary>A second name for one inode. .NET can create symlinks but not hard links.</summary>
    static void HardLink(string target, string link)
    {
        using var ln = System.Diagnostics.Process.Start("ln", new[] { target, link })
            ?? throw new IOException("could not start ln");
        ln.WaitForExit();
        if (ln.ExitCode != 0) throw new IOException($"ln exited {ln.ExitCode}");
    }

    /// <summary>
    /// The dedup gate's counterpart to <see cref="ConcurrentReclaimOfADeadRunnersSlot_AdmitsOnlyOne"/>,
    /// driven through the production entry point because that is where the name lock is taken. Two
    /// runs and a stale sweep arrive at one dead runner's name at the same instant: whoever unlinks
    /// that name without holding it strips it from under whoever just claimed it, and the next
    /// arrival creates it again — two runs of one name. The window is a few instructions wide, so
    /// the race is driven to it repeatedly rather than assumed to be hit once.
    /// </summary>
    [UnixFact("races real runs of the sleep binary through the production gate")]
    public void ConcurrentRunsOfOneName_ReclaimingADeadRunnersLockWhileSwept_AdmitOnlyOne()
    {
        // enough arrivals and enough rounds that a window this narrow shows up on every run
        const int rounds = 400;
        const int racerCount = 4;

        Store.EnsureDirs();
        var caps = new Caps { QueueTimeout = TimeSpan.FromSeconds(30) };
        var lockPath = Store.LockPathFor(RunKey.For("dedup", Canon.ResolveCommand("sleep"), _scope));
        var runsDir = Path.Combine(_home.Path, "runs");
        var doubleClaims = 0;

        for (var i = 0; i < rounds; i++)
        {
            // the dedup lock of a runner that was killed while holding it
            File.WriteAllText(lockPath, $"2147483646 {DateTime.UtcNow:O}\n");

            var atTheGate = new Barrier(racerCount + 1);
            var racers = Enumerable.Range(0, racerCount)
                .Select(_ => new Thread(() =>
                {
                    atTheGate.SignalAndWait();
                    Sleep("0", caps, name: "dedup").GetAwaiter().GetResult();
                }))
                .ToList();
            // every tman command sweeps before it does anything else, so a sweep is one of the
            // arrivals at this name
            var sweeper = new Thread(() =>
            {
                atTheGate.SignalAndWait();
                Reaper.Sweep(TimeSpan.FromHours(24), quiet: true);
            });
            sweeper.Start();
            foreach (var r in racers) r.Start();
            foreach (var r in racers) r.Join();
            sweeper.Join();

            // two runs of one name that overlap in time are the violation — two that merely both
            // finished are not, since the name is free again the moment the first one releases it
            if (PeakOverlap(Store.LoadAll()) > 1) doubleClaims++;
            // records are per-round evidence; keeping them all would make every round scan the last
            foreach (var f in Directory.EnumerateFiles(runsDir, "*.json")) File.Delete(f);
        }

        Assert.Equal(0, doubleClaims);
    }
}
