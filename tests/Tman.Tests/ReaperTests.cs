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
        using var fs = new FileStream(held, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        Store.StampLockOwner(fs);
        var abandoned = Store.LockPathFor("gone@/repo");
        File.WriteAllText(abandoned, $"2147483646 {DateTime.UtcNow:O}\n");

        Reaper.Sweep(TimeSpan.FromHours(24), quiet: true);

        // the sweep runs at the start of every command, including the one holding a lock, and a
        // name it removed could be taken by a run that had already opened it — so it removes none
        Assert.True(File.Exists(held));
        Assert.True(File.Exists(abandoned));
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
