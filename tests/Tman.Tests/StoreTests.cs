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
    public void ARecordWrittenByTwoWritersAtOnce_SurvivesBothOfThem()
    {
        var r = NewRecord("shared00001");

        var atTheGate = new Barrier(4);
        var faults = new Exception?[4];
        var writers = Enumerable.Range(0, 4)
            .Select(t => new Thread(() =>
            {
                atTheGate.SignalAndWait();
                // a runner and any other command's housekeeping sweep both save the same record
                for (var i = 0; i < 50; i++)
                    try { Store.Save(r); } catch (Exception e) { faults[t] ??= e; return; }
            }))
            .ToList();
        foreach (var w in writers) w.Start();
        foreach (var w in writers) w.Join();

        Assert.All(faults, f => Assert.Null(f));
        Assert.NotNull(Store.Load("shared00001"));
    }

    /// <summary>
    /// The message a sharing violation carries; what MoveFileEx(MOVEFILE_REPLACE_EXISTING) raises
    /// while another writer holds the destination it is being asked to replace.
    /// </summary>
    static IOException TheDestinationIsHeldByAnotherWriter() =>
        new("The process cannot access the file because it is being used by another process.");

    [Fact]
    public void ASaveThatLosesTheRenameToAnotherWriter_WaitsForTheDestinationInsteadOfFailing()
    {
        var r = NewRecord("contended001");
        var renames = 0;

        Store.Save(r, (from, to) =>
        {
            if (++renames <= 3) throw TheDestinationIsHeldByAnotherWriter();
            File.Move(from, to, overwrite: true);
        });

        Assert.Equal(4, renames);
        Assert.NotNull(Store.Load("contended001"));
    }

    [Fact]
    public void ASaveThatNeverWinsTheRename_ThrowsRatherThanReportARecordItNeverPlaced()
    {
        var r = NewRecord("contended002");
        var renames = 0;

        Assert.Throws<IOException>(() => Store.Save(r, (_, _) =>
        {
            renames++;
            throw TheDestinationIsHeldByAnotherWriter();
        }));

        // bounded on both sides: a transient that is never waited out is the win-x64 defect, and a
        // wait with no end is a run that hangs instead of reporting a fault it cannot recover from
        Assert.InRange(renames, 2, 64);
        Assert.Null(Store.Load("contended002"));
    }

    [Fact]
    public void ASaveWhoseTempRecordIsGone_ReportsItAtOnceRatherThanWaitingOnIt()
    {
        var r = NewRecord("contended003");
        var renames = 0;

        // nothing about this settles by waiting — only a destination held by another writer does
        Assert.Throws<FileNotFoundException>(() => Store.Save(r, (from, _) =>
        {
            renames++;
            File.Delete(from);
            throw new FileNotFoundException("gone", from);
        }));

        Assert.Equal(1, renames);
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
