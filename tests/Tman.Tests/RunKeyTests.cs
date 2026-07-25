using Tman;
using Xunit;

namespace Tman.Tests;

public class RunKeyTests
{
    [Fact]
    public void NamedRuns_InDifferentDirs_AreDifferentBuckets()
    {
        Assert.NotEqual(
            RunKey.For("test", "dotnet", "/repo-a"),
            RunKey.For("test", "dotnet", "/repo-b"));
    }

    [Fact]
    public void NamedRuns_InSameDir_ShareABucket()
    {
        Assert.Equal(
            RunKey.For("test", "dotnet", "/repo"),
            RunKey.For("test", "npm", "/repo"));
    }

    [Fact]
    public void UnnamedRuns_BucketByCommandBasename()
    {
        Assert.Equal(
            RunKey.For(null, "npm", "/repo"),
            RunKey.For(null, "/usr/bin/npm", "/repo"));
        Assert.NotEqual(
            RunKey.For(null, "npm", "/repo"),
            RunKey.For(null, "vite", "/repo"));
    }

    [Fact]
    public void UnnamedRuns_OfSameCommand_InDifferentDirs_AreDifferentBuckets()
    {
        Assert.NotEqual(
            RunKey.For(null, "vite", "/repo-a"),
            RunKey.For(null, "vite", "/repo-b"));
    }

    [Fact]
    public void ScopeDir_IgnoresTrailingSeparator()
    {
        Assert.Equal(
            RunKey.For("test", "dotnet", "/repo"),
            RunKey.For("test", "dotnet", "/repo/"));
    }

    [Fact]
    public void ScopeDir_PrefersConfigDirOverCwd()
    {
        var config = new TmanConfig("/repo/.tman.kdl", "/repo", new Caps(),
            new Dictionary<string, AliasDef>());
        Assert.Equal("/repo", RunKey.ScopeDir(config, "/repo/sub/dir"));
        Assert.Equal("/repo/sub/dir", RunKey.ScopeDir(null, "/repo/sub/dir"));
    }

    [Fact]
    public void LockStem_KeysThatSanitizeAlike_StayDistinct()
    {
        Assert.NotEqual(RunKey.LockStem("a/b@/repo"), RunKey.LockStem("a:b@/repo"));
    }
}
