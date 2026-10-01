using Tman;
using Xunit;

namespace Tman.Tests;

[Collection("cwd")]
public class ReaperTests : IDisposable
{
    readonly TempDir _home = new();
    readonly string? _prevHome = Environment.GetEnvironmentVariable("TMAN_HOME");

    public ReaperTests() => Environment.SetEnvironmentVariable("TMAN_HOME", _home.Path);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TMAN_HOME", _prevHome);
        _home.Dispose();
    }

    static RunRecord Finished(string id, TimeSpan age) => new()
    {
        Id = id,
        Command = "/bin/echo",
        Args = Array.Empty<string>(),
        State = RunState.Exited,
        StartedUtc = DateTime.UtcNow - age,
        HeartbeatUtc = DateTime.UtcNow - age,
    };

    [Fact]
    public void Sweep_PrunesExpiredRecords_WithoutAnExplicitClean()
    {
        Store.Save(Finished("expired00001", TimeSpan.FromHours(48)));
        Store.Save(Finished("recent000001", TimeSpan.FromMinutes(5)));

        var (reaped, pruned) = Reaper.Sweep(TimeSpan.FromHours(24), quiet: true);

        Assert.Empty(reaped);
        Assert.Equal(1, pruned);
        Assert.Null(Store.Load("expired00001"));
        Assert.NotNull(Store.Load("recent000001"));
    }

    [Fact]
    public void Sweep_LeavesEveryLockFileWhereItIs()
    {
        Store.EnsureDirs();
        var held = Store.LockPathFor("busy@/repo");
        using (var stamp = new FileStream(held, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            Store.StampLockOwner(stamp);
        var abandoned = Store.LockPathFor("gone@/repo");
        File.WriteAllText(abandoned, $"2147483646 {DateTime.UtcNow:O}\n");
        var slot = Store.SlotPathFor("busy@/repo", 0);
        File.WriteAllText(slot, $"2147483646 {DateTime.UtcNow:O}\n");

        // age every lock past the retention the sweep is given: a fresh lock survives an
        // age-gated prune whether or not that prune looks at locks at all, so only an aged one
        // can tell "the sweep leaves locks alone" apart from "the sweep has not got to them yet"
        var ancient = DateTime.UtcNow - TimeSpan.FromHours(48);
        foreach (var f in new[] { held, abandoned, slot }) File.SetLastWriteTimeUtc(f, ancient);

        // reopening does not touch mtime — the lock stays held, and stays older than the cutoff
        using var fs = new FileStream(held, FileMode.Open, FileAccess.Write, FileShare.None);

        Reaper.Sweep(TimeSpan.FromHours(24), quiet: true);

        // the sweep runs at the start of every command, including the one holding a lock, and a
        // name it removed could be taken by a run that had already opened it — so it removes none
        Assert.True(File.Exists(held));
        Assert.True(File.Exists(abandoned));
        Assert.True(File.Exists(slot));
    }

    [Fact]
    public void ReapOrphans_RecordsADeadChildAsKilled_NotAsAZeroExit()
    {
        // A Running record whose child is gone but whose runner never wrote the outcome. Nobody
        // saw that child's exit status, so the record may not read as a finished run: Exited with
        // no code is what the digest and `tman show` print as a pass. The Runner names this case
        // "child exit status unknown"; the Reaper must say the same thing.
        var r = DeadChildOfADeadRunner("deadchild001");
        Store.Save(r);

        var reaped = Reaper.ReapOrphans(quiet: true);

        Assert.Empty(reaped);
        var saved = Store.Load("deadchild001")!;
        Assert.Equal(RunState.Killed, saved.State);
        Assert.Equal(Runner.ExitStatusUnknownReason, saved.KillReason);
        Assert.Null(saved.ExitCode);
    }

    /// <summary>A Running record whose child and runner are both gone: no one is left to finish it.</summary>
    RunRecord DeadChildOfADeadRunner(string id)
    {
        var r = Finished(id, TimeSpan.FromMinutes(1));
        r.State = RunState.Running;
        r.Pid = 2147483646;
        r.ChildStartUtc = DateTime.UtcNow;
        r.RunnerPid = 2147483645;
        r.RunnerStartUtc = DateTime.UtcNow;
        return r;
    }

    [Fact]
    public void ReapOrphans_ADeadChildSomeoneKilled_IsRecordedWithTheirReason()
    {
        // `tman kill` leaves its reason before it kills; if the runner died before writing the
        // outcome, the sweep that finishes the record says what actually happened
        Store.Save(DeadChildOfADeadRunner("killedchild1"));
        Store.RequestKill("killedchild1", "killed via tman kill");

        Reaper.ReapOrphans(quiet: true);

        Assert.Equal("killed via tman kill", Store.Load("killedchild1")!.KillReason);
    }

    [Fact]
    public void ReapOrphans_LeavesALiveRunnersRecordToItsRunner()
    {
        // the runner saves the copy it holds every tick; an outcome written beside it here is
        // overwritten by that save, which is how a killed run came to read as exited
        var r = DeadChildOfADeadRunner("liverunner01");
        r.RunnerPid = Environment.ProcessId;
        Store.Save(r);

        Reaper.ReapOrphans(quiet: true);

        Assert.Equal(RunState.Running, Store.Load("liverunner01")!.State);
    }

    /// <summary>
    /// Linux derives a process's wall-clock start from boot time, recomputed on every read, so an
    /// NTP step or a WSL clock resync after sleep moves it. A run recorded before such a step is the
    /// same process afterwards, and must still be live — not reaped as an orphan whose pid was reused.
    /// </summary>
    [LinuxFact("the recomputed wall-clock start and /proc starttime ticks are Linux's")]
    public void LiveRuns_ARunWhoseStartMovedWithTheWallClock_IsStillLive()
    {
        using var child = System.Diagnostics.Process.Start("sleep", "30")
            ?? throw new IOException("could not start sleep");
        try
        {
            var r = Finished("clockstep001", TimeSpan.FromMinutes(1));
            r.State = RunState.Running;
            r.Pid = child.Id;
            r.RunnerPid = Environment.ProcessId;
            // recorded before the clock stepped 10s back: today's re-read is 10s off the record
            r.ChildStartUtc = child.StartTime.ToUniversalTime() + TimeSpan.FromSeconds(10);
            r.ChildStartTicks = StartTicksOf(child.Id);
            Store.Save(r);

            Assert.Contains(Reaper.LiveRuns(), live => live.Id == r.Id);
        }
        finally
        {
            child.Kill();
            child.WaitForExit();
        }
    }

    /// <summary>Field 22 of /proc/&lt;pid&gt;/stat: start time in clock ticks after boot, which no clock step moves.</summary>
    static long StartTicksOf(int pid)
    {
        var stat = File.ReadAllText($"/proc/{pid}/stat");
        var afterComm = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
        return long.Parse(afterComm[19]);
    }

    [Fact]
    public void Resolve_FindsFinishedRunsByName()
    {
        var r = Finished("aaaabbbbcccc", TimeSpan.FromMinutes(1));
        r.Name = "build";
        Store.Save(r);

        // FindLiveByNameOrId only sees running work; detail is usually wanted after a failure
        Assert.Null(Reaper.FindLiveByNameOrId("build"));
        Assert.Equal("aaaabbbbcccc", Reaper.Resolve("build")?.Id);
        Assert.Equal("aaaabbbbcccc", Reaper.Resolve("aaaa")?.Id);
        Assert.Null(Reaper.Resolve("nosuchrun"));
    }

    [Fact]
    public void Resolve_PrefersTheMostRecentRunOfAReusedName()
    {
        var older = Finished("older0000001", TimeSpan.FromHours(2));
        older.Name = "test";
        var newer = Finished("newer0000001", TimeSpan.FromMinutes(2));
        newer.Name = "test";
        Store.Save(older);
        Store.Save(newer);

        Assert.Equal("newer0000001", Reaper.Resolve("test")?.Id);
    }
}
