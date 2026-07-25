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
    public void Sweep_PrunesExpiredRecordsAndStaleLocks_WithoutAnExplicitClean()
    {
        Store.Save(Finished("expired00001", TimeSpan.FromHours(48)));
        Store.Save(Finished("recent000001", TimeSpan.FromMinutes(5)));
        var deadLock = Store.LockPathFor("gone@/repo");
        File.WriteAllText(deadLock, $"2147483646 {DateTime.UtcNow:O}\n");

        var (reaped, pruned, locksFreed) = Reaper.Sweep(TimeSpan.FromHours(24), quiet: true);

        Assert.Empty(reaped);
        Assert.Equal(1, pruned);
        Assert.Equal(1, locksFreed);
        Assert.Null(Store.Load("expired00001"));
        Assert.NotNull(Store.Load("recent000001"));
        Assert.False(File.Exists(deadLock));
    }

    [Fact]
    public void Sweep_LeavesALockHeldByALiveOwnerAlone()
    {
        Store.EnsureDirs();
        var path = Store.LockPathFor("busy@/repo");
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        Store.StampLockOwner(fs);

        // the sweep runs at the start of every command, including the one holding this lock
        Assert.Equal(0, Reaper.Sweep(TimeSpan.FromHours(24), quiet: true).LocksFreed);
        Assert.True(File.Exists(path));
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
