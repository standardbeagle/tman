using Tman;
using Xunit;

namespace Tman.Tests;

[Collection("cwd")]
public class InitTests
{
    static int RunInitIn(string dir, params string[] args)
    {
        var prev = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(dir);
            return Program.CmdInit(args);
        }
        finally
        {
            Directory.SetCurrentDirectory(prev);
        }
    }

    [Fact]
    public void NpmRepo_WritesConfigShimsAndGitignore()
    {
        using var dir = new TempDir();
        dir.WriteFile("package.json", """{ "scripts": { "test": "vitest", "lint": "eslint ." } }""");

        var code = RunInitIn(dir.Path, "--shims", "--gitignore");

        Assert.Equal(0, code);
        var kdl = File.ReadAllText(System.IO.Path.Combine(dir.Path, ".tman.kdl"));
        Assert.Contains("alias \"test\"", kdl);
        Assert.Contains("alias \"lint\"", kdl);
        Assert.True(File.Exists(System.IO.Path.Combine(dir.Path, "test")));
        Assert.True(File.Exists(System.IO.Path.Combine(dir.Path, "lint")));
        Assert.Contains("/test", File.ReadAllText(System.IO.Path.Combine(dir.Path, ".gitignore")));
    }

    [Fact]
    public void RepoWithTestDirectory_SkipsShimGracefully()
    {
        using var dir = new TempDir();
        dir.WriteFile("package.json", """{ "scripts": { "test": "vitest", "lint": "eslint ." } }""");
        dir.Mkdir("test");

        var code = RunInitIn(dir.Path, "--shims", "--gitignore");

        Assert.Equal(0, code);
        Assert.True(File.Exists(System.IO.Path.Combine(dir.Path, ".tman.kdl")));
        Assert.True(Directory.Exists(System.IO.Path.Combine(dir.Path, "test")));
        Assert.True(File.Exists(System.IO.Path.Combine(dir.Path, "lint")));
    }

    [Fact]
    public void BareRepo_WritesPlaceholderConfig()
    {
        using var dir = new TempDir();

        var code = RunInitIn(dir.Path, "--shims");

        Assert.Equal(0, code);
        var kdl = File.ReadAllText(System.IO.Path.Combine(dir.Path, ".tman.kdl"));
        Assert.Contains("alias \"test\"", kdl);
        Assert.Contains("command \"echo\"", kdl);
        Assert.True(File.Exists(System.IO.Path.Combine(dir.Path, "test")));
    }

    [Fact]
    public void Rerun_KeepsExistingConfig()
    {
        using var dir = new TempDir();
        dir.WriteFile("package.json", """{ "scripts": { "test": "vitest" } }""");
        Assert.Equal(0, RunInitIn(dir.Path));

        var original = File.ReadAllText(System.IO.Path.Combine(dir.Path, ".tman.kdl"));
        Assert.Equal(0, RunInitIn(dir.Path, "--shims"));
        Assert.Equal(original, File.ReadAllText(System.IO.Path.Combine(dir.Path, ".tman.kdl")));
    }
}

public class ConfigLoadTests
{
    [Fact]
    public void Monorepo_NestedPackage_ResolvesRootConfig()
    {
        using var dir = new TempDir();
        dir.WriteFile(".tman.kdl", """
            defaults {
                max-time "5m"
            }
            alias "test" {
                command "npm"
                args "run" "test"
            }
            """);
        var nested = dir.Mkdir(System.IO.Path.Combine("packages", "app", "src"));

        var config = Config.Load(nested);

        Assert.NotNull(config);
        Assert.Equal(dir.Path, config.Dir);
        var alias = Assert.Contains("test", config.Aliases);
        Assert.Equal("npm", alias.Command);
        Assert.Equal(new[] { "run", "test" }, alias.Args);
    }

    [Fact]
    public void NearestConfigWins_WhenNestedConfigsExist()
    {
        using var dir = new TempDir();
        dir.WriteFile(".tman.kdl", "alias \"test\" {\n    command \"npm\"\n}\n");
        dir.WriteFile(System.IO.Path.Combine("packages", "app", ".tman.kdl"),
            "alias \"test\" {\n    command \"pytest\"\n}\n");

        var config = Config.Load(System.IO.Path.Combine(dir.Path, "packages", "app"));

        Assert.NotNull(config);
        Assert.Equal("pytest", config.Aliases["test"].Command);
    }

    [Fact]
    public void MissingConfig_ReturnsNull()
    {
        using var dir = new TempDir();
        Assert.Null(Config.Load(dir.Path));
    }

    [Fact]
    public void AliasWithoutCommand_Throws()
    {
        using var dir = new TempDir();
        dir.WriteFile(".tman.kdl", "alias \"test\" {\n}\n");
        Assert.Throws<FormatException>(() => Config.Load(dir.Path));
    }
}
