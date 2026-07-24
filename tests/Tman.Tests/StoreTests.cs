using Tman;
using Xunit;

namespace Tman.Tests;

[Collection("cwd")]
public class StoreTests : IDisposable
{
    readonly TempDir _home = new();
    readonly string? _prevHome = Environment.GetEnvironmentVariable("TMAN_HOME");

    public StoreTests() => Environment.SetEnvironmentVariable("TMAN_HOME", _home.Path);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TMAN_HOME", _prevHome);
        _home.Dispose();
    }

    static RunRecord NewRecord(string id, RunState state = RunState.Running) => new()
    {
        Id = id,
        Name = "name-" + id,
        Command = "echo",
        Args = new[] { "hi" },
        State = state,
        HeartbeatUtc = DateTime.UtcNow,
    };

    [Fact]
    public void SaveLoad_RoundTrips()
    {
        var r = NewRecord("abc123");
        r.RunnerStartUtc = DateTime.UtcNow;
        Store.Save(r);

        var loaded = Store.Load("abc123");

        Assert.NotNull(loaded);
        Assert.Equal("abc123", loaded.Id);
        Assert.Equal("name-abc123", loaded.Name);
        Assert.Equal(new[] { "hi" }, loaded.Args);
    }

    [Fact]
    public void Load_Missing_ReturnsNull()
    {
        Assert.Null(Store.Load("nope"));
    }

    [Fact]
    public void Load_CorruptJson_ReturnsNull()
    {
        Store.EnsureDirs();
        File.WriteAllText(System.IO.Path.Combine(_home.Path, "runs", "bad.json"), "{ corrupt");
        Assert.Null(Store.Load("bad"));
    }

    [Fact]
    public void LoadAll_SkipsCorruptFiles()
    {
        Store.Save(NewRecord("good1"));
        Store.EnsureDirs();
        File.WriteAllText(System.IO.Path.Combine(_home.Path, "runs", "bad.json"), "{ corrupt");

        var all = Store.LoadAll();

        Assert.Single(all);
        Assert.Equal("good1", all[0].Id);
    }

    [Fact]
    public void Remove_DeletesRecord()
    {
        Store.Save(NewRecord("gone"));
        Store.Remove("gone");
        Assert.Null(Store.Load("gone"));
    }

    [Fact]
    public void PruneCompleted_RemovesOldCompleted_KeepsRunningAndRecent()
    {
        var old = NewRecord("old", RunState.Exited);
        old.HeartbeatUtc = DateTime.UtcNow - TimeSpan.FromHours(48);
        var recent = NewRecord("recent", RunState.Exited);
        var running = NewRecord("running");
        running.HeartbeatUtc = DateTime.UtcNow - TimeSpan.FromHours(48);
        Store.Save(old);
        Store.Save(recent);
        Store.Save(running);

        Store.PruneCompleted(TimeSpan.FromHours(24));

        Assert.Null(Store.Load("old"));
        Assert.NotNull(Store.Load("recent"));
        Assert.NotNull(Store.Load("running"));
    }

    [Fact]
    public void LockPathFor_SanitizesName()
    {
        var p = Store.LockPathFor("my/test:name");
        Assert.EndsWith("my_test_name.lock", p);
    }

    [Fact]
    public void Matches_ByNameOrId_CaseInsensitive()
    {
        var r = NewRecord("AbC");
        Assert.True(r.Matches("abc"));
        Assert.True(r.Matches("NAME-abc"));
        Assert.False(r.Matches("other"));
    }
}
