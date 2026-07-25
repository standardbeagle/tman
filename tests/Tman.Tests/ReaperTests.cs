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
