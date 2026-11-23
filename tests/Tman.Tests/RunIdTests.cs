using Tman;
using Xunit;

namespace Tman.Tests;

/// <summary>
/// A run id names the store that issued it. Every tman on a machine talks to the same docker
/// daemon, but each keeps its own store (a user's, another user's, a Windows tman beside the one in
/// WSL, every test's temporary TMAN_HOME), so a store that finds a container labelled with an id it
/// has no record of must be able to tell an id it issued and has since pruned from an id that
/// another, possibly live, store issued.
/// </summary>
[Collection("cwd")]
public class RunIdTests : IDisposable
{
    readonly TempDir _home = new();
    readonly TempDir _otherHome = new();
    readonly string? _prevHome = Environment.GetEnvironmentVariable("TMAN_HOME");

    public RunIdTests() => Environment.SetEnvironmentVariable("TMAN_HOME", _home.Path);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TMAN_HOME", _prevHome);
        _home.Dispose();
        _otherHome.Dispose();
    }

    [Fact]
    public void ANewId_IsLowercaseHex_EndingInItsStoresTag()
    {
        var id = RunRecord.NewId();

        Assert.Matches("^[0-9a-f]{16}$", id);
        Assert.EndsWith(Store.Tag, id);
    }

    [Fact]
    public void NewIds_DoNotRepeat()
    {
        var ids = Enumerable.Range(0, 1000).Select(_ => RunRecord.NewId()).ToHashSet();
        Assert.Equal(1000, ids.Count);
    }

    [Fact]
    public void TwoStores_IssueUnderDifferentTags()
    {
        var mine = Store.Tag;
        Environment.SetEnvironmentVariable("TMAN_HOME", _otherHome.Path);

        Assert.NotEqual(mine, Store.Tag);
    }

    [Fact]
    public void AStore_ClaimsTheIdsItIssued_AndNoOtherStores()
    {
        var mine = RunRecord.NewId();
        Environment.SetEnvironmentVariable("TMAN_HOME", _otherHome.Path);
        var theirs = RunRecord.NewId();

        Assert.True(Store.Issued(theirs));
        Assert.False(Store.Issued(mine));
    }

    [Theory]
    // the 12-hex ids tman issued before ids carried a tag: no store can say whose they were
    [InlineData("0123456789ab")]
    [InlineData("")]
    [InlineData("not-a-run-id")]
    public void AnIdNoTagCanBeReadFrom_IsNeverClaimed(string id) =>
        Assert.False(Store.Issued(id));

    [Fact]
    public void ARunsRecord_CarriesAnIdItsStoreIssued()
    {
        var r = Runner.NewRecord("echo", [], new Caps(), null, null, null, _home.Path);
        Assert.True(Store.Issued(r.Id));
    }
}

/// <summary>`tman list` and `tman queue` lay an id out in a column, which has to hold a whole one.</summary>
public class RunIdColumnTests : IDisposable
{
    readonly TempDir _home = new();
    readonly TempDir _cwd = new();

    public void Dispose()
    {
        _home.Dispose();
        _cwd.Dispose();
    }

    [UnixFact("runs true through the built tman")]
    public async Task TheListsIdColumn_HoldsAWholeId_WithAGapBeforeTheName()
    {
        Assert.Equal(0, (await TmanProcess.Run(_home.Path, _cwd.Path, null,
            "run", "--no-queue", "--name", "probe", "--", "true")).Exit);

        var list = await TmanProcess.Run(_home.Path, _cwd.Path, null, "list", "--all");

        Assert.Equal(0, list.Exit);
        var row = list.Out.Split('\n').Single(l => l.Contains("probe"));
        Assert.Matches("^[0-9a-f]{16} +probe ", row);
    }
}
