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
    public void Prune_RemovesOldCompleted_KeepsRunningAndRecent()
    {
        var old = NewRecord("old", RunState.Exited);
        old.HeartbeatUtc = DateTime.UtcNow - TimeSpan.FromHours(48);
        var recent = NewRecord("recent", RunState.Exited);
        var running = NewRecord("running");
        running.HeartbeatUtc = DateTime.UtcNow - TimeSpan.FromHours(48);
        Store.Save(old);
        Store.Save(recent);
        Store.Save(running);

        var pruned = Store.Prune(TimeSpan.FromHours(24));

        Assert.Equal(1, pruned);
        Assert.Null(Store.Load("old"));
        Assert.NotNull(Store.Load("recent"));
        Assert.NotNull(Store.Load("running"));
    }

    [Fact]
    public void Prune_RemovesUnreadableRecordsOnceTheyAreOld()
    {
        Store.EnsureDirs();
        var runsDir = System.IO.Path.Combine(_home.Path, "runs");
        var corrupt = System.IO.Path.Combine(runsDir, "corrupt.json");
        var fresh = System.IO.Path.Combine(runsDir, "fresh.json");
        File.WriteAllText(corrupt, "{ this is not json");
        File.WriteAllText(fresh, "{ also not json");
        File.SetLastWriteTimeUtc(corrupt, DateTime.UtcNow - TimeSpan.FromHours(48));

        var pruned = Store.Prune(TimeSpan.FromHours(24));

        Assert.Equal(1, pruned);
        Assert.False(File.Exists(corrupt));
        // a file being written right now must survive; it is not evidence of rot
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void Prune_RemovesRecordsFromAnotherSchema()
    {
        var r = NewRecord("future", RunState.Exited);
        r.HeartbeatUtc = DateTime.UtcNow - TimeSpan.FromHours(48);
        Store.Save(r);
        var path = System.IO.Path.Combine(_home.Path, "runs", "future.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace(
            $"\"Schema\":{RunRecord.CurrentSchema}", $"\"Schema\":{RunRecord.CurrentSchema + 1}"));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromHours(48));

        Assert.Null(Store.Load("future"));
        Assert.Equal(1, Store.Prune(TimeSpan.FromHours(24)));
    }

    [Fact]
    public void AReleasedNameLock_StaysOnDiskAndNamesItsLastHolder()
    {
        var claimed = Store.TryAcquireNameLock("test@/repo");
        Assert.NotNull(claimed);
        var path = Store.LockPathFor("test@/repo");
        Assert.Null(Store.TryAcquireNameLock("test@/repo"));

        Store.ReleaseLock(claimed);

        // removing it is what let two runs share a name, and it buys nothing: the next claim opens
        // this same file. The stamp stays readable so a busy name can be traced to a process.
        Assert.True(File.Exists(path));
        Assert.StartsWith($"{Environment.ProcessId} ", File.ReadAllText(path));
        var again = Store.TryAcquireNameLock("test@/repo");
        Assert.NotNull(again);
        Store.ReleaseLock(again);
    }

    [Fact]
    public void LockPathFor_SanitizesKeyAndStaysReadable()
    {
        var p = Store.LockPathFor("my/test:name@/repo");
        Assert.EndsWith(".lock", p);
        Assert.StartsWith("my_test_name-", Path.GetFileName(p));
    }

    [Fact]
    public void LockPathFor_DiffersPerScopeDir()
    {
        Assert.NotEqual(Store.LockPathFor("test@/repo-a"), Store.LockPathFor("test@/repo-b"));
    }

    [Fact]
    public void Matches_ByNameOrId_CaseInsensitive()
    {
        var r = NewRecord("AbC");
        Assert.True(r.Matches("abc"));
        Assert.True(r.Matches("NAME-abc"));
        Assert.False(r.Matches("other"));
    }

    [Fact]
    public void Matches_ByIdPrefix_OnceLongEnoughToBeUnambiguous()
    {
        var r = NewRecord("351dd67eae5b");
        Assert.True(r.Matches("351d"));
        Assert.True(r.Matches("351DD67"));
        // a one- or two-character prefix would collide across runs
        Assert.False(r.Matches("35"));
        Assert.False(r.Matches("999d"));
    }
}
