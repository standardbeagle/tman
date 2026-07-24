using Tman;
using Xunit;

namespace Tman.Tests;

public class ShimGenerateTests
{
    [Fact]
    public void WritesExecutableShimPerAlias()
    {
        using var dir = new TempDir();
        var (written, skipped) = Shim.Generate(dir.Path, new[] { "test", "lint" });

        Assert.Empty(skipped);
        Assert.Equal(2, written.Count(p => !p.EndsWith(".ps1") && !p.EndsWith(".cmd")));

        var sh = File.ReadAllText(System.IO.Path.Combine(dir.Path, "test"));
        Assert.Contains("exec tman run --alias test", sh);

        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(System.IO.Path.Combine(dir.Path, "test"));
            Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
        }
        else
        {
            var ps1 = System.IO.Path.Combine(dir.Path, "test.ps1");
            Assert.True(File.Exists(ps1));
            Assert.Contains("@args", File.ReadAllText(ps1));
            Assert.True(File.Exists(System.IO.Path.Combine(dir.Path, "test.cmd")));
        }
    }

    [Fact]
    public void ExistingDirectory_SkipsWithoutThrowing()
    {
        using var dir = new TempDir();
        dir.Mkdir("test");

        var (written, skipped) = Shim.Generate(dir.Path, new[] { "test", "lint" });

        Assert.Single(skipped);
        Assert.True(Directory.Exists(System.IO.Path.Combine(dir.Path, "test")));
        Assert.True(File.Exists(System.IO.Path.Combine(dir.Path, "lint")));
    }

    [Fact]
    public void ExistingForeignFile_SkipsWithoutOverwriting()
    {
        using var dir = new TempDir();
        dir.WriteFile("test", "#!/usr/bin/env bash\n./vendor/bin/phpunit\n");

        var (written, skipped) = Shim.Generate(dir.Path, new[] { "test" });

        Assert.Single(skipped);
        Assert.Empty(written);
        Assert.Contains("phpunit", File.ReadAllText(System.IO.Path.Combine(dir.Path, "test")));
    }

    [Fact]
    public void ExistingTmanShim_IsRewritten_Idempotent()
    {
        using var dir = new TempDir();
        Shim.Generate(dir.Path, new[] { "test" });

        var (written, skipped) = Shim.Generate(dir.Path, new[] { "test" });

        Assert.Empty(skipped);
        Assert.NotEmpty(written);
    }
}

public class ShimGitignoreTests
{
    [Fact]
    public void AppendsEntries_AndIsIdempotent()
    {
        using var dir = new TempDir();
        dir.WriteFile(".gitignore", "/bin/\n");

        Assert.True(Shim.AppendGitignore(dir.Path, new[] { "test" }));
        Assert.False(Shim.AppendGitignore(dir.Path, new[] { "test" }));

        var lines = File.ReadAllLines(System.IO.Path.Combine(dir.Path, ".gitignore"));
        Assert.Equal(1, lines.Count(l => l == "/test"));
        Assert.Contains("/bin/", lines);
    }

    [Fact]
    public void CreatesGitignoreWhenMissing()
    {
        using var dir = new TempDir();
        Assert.True(Shim.AppendGitignore(dir.Path, new[] { "test", "lint" }));
        var text = File.ReadAllText(System.IO.Path.Combine(dir.Path, ".gitignore"));
        Assert.Contains("/test", text);
        Assert.Contains("/lint", text);
    }
}
