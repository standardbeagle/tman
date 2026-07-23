using Tman;
using Xunit;

namespace Tman.Tests;

public class DetectAliasesTests
{
    [Fact]
    public void EmptyDirectory_DetectsNothing()
    {
        using var dir = new TempDir();
        Assert.Empty(Program.DetectAliases(dir.Path));
    }

    [Fact]
    public void NpmProject_DetectsRecognizedScripts()
    {
        using var dir = new TempDir();
        dir.WriteFile("package.json", """
            { "scripts": { "test": "vitest", "e2e": "playwright test", "lint": "eslint .", "integration": "vitest run int", "build": "tsc", "start": "node ." } }
            """);

        var aliases = Program.DetectAliases(dir.Path);

        Assert.Equal(
            new[] { "e2e", "integration", "lint", "test" },
            aliases.Select(a => a.Name).OrderBy(n => n));
        Assert.All(aliases, a => Assert.Equal("npm", a.Command));
        var test = aliases.Single(a => a.Name == "test");
        Assert.Equal(new[] { "run", "test" }, test.Args);
    }

    [Fact]
    public void NpmProject_WithoutRecognizedScripts_DetectsNothing()
    {
        using var dir = new TempDir();
        dir.WriteFile("package.json", """{ "scripts": { "build": "tsc", "start": "node ." } }""");
        Assert.Empty(Program.DetectAliases(dir.Path));
    }

    [Fact]
    public void NpmProject_WithMalformedPackageJson_DoesNotThrow()
    {
        using var dir = new TempDir();
        dir.WriteFile("package.json", "{ not json");
        Assert.Empty(Program.DetectAliases(dir.Path));
    }

    [Fact]
    public void MonorepoRoot_WithWorkspaces_DetectsRootScripts()
    {
        using var dir = new TempDir();
        dir.WriteFile("package.json", """
            { "private": true, "workspaces": ["packages/*"], "scripts": { "test": "npm test --workspaces", "lint": "eslint ." } }
            """);
        dir.WriteFile("packages/app/package.json", """{ "name": "app", "scripts": { "test": "vitest" } }""");

        var root = Program.DetectAliases(dir.Path);
        Assert.Equal(new[] { "lint", "test" }, root.Select(a => a.Name).OrderBy(n => n));

        var sub = Program.DetectAliases(System.IO.Path.Combine(dir.Path, "packages", "app"));
        Assert.Single(sub);
        Assert.Equal("test", sub[0].Name);
    }

    [Theory]
    [InlineData("pyproject.toml")]
    [InlineData("pytest.ini")]
    public void PytestProject_DetectsTest(string marker)
    {
        using var dir = new TempDir();
        dir.WriteFile(marker, "[tool.pytest]");
        var alias = Assert.Single(Program.DetectAliases(dir.Path));
        Assert.Equal("test", alias.Name);
        Assert.Equal("pytest", alias.Command);
        Assert.Empty(alias.Args);
    }

    [Fact]
    public void GoProject_DetectsTest()
    {
        using var dir = new TempDir();
        dir.WriteFile("go.mod", "module example.com/x\n\ngo 1.22\n");
        var alias = Assert.Single(Program.DetectAliases(dir.Path));
        Assert.Equal("test", alias.Name);
        Assert.Equal("go", alias.Command);
        Assert.Equal(new[] { "test", "./..." }, alias.Args);
    }

    [Fact]
    public void MakeProject_WithTestTarget_DetectsTest()
    {
        using var dir = new TempDir();
        dir.WriteFile("Makefile", "test:\n\tgo test ./...\n");
        var alias = Assert.Single(Program.DetectAliases(dir.Path));
        Assert.Equal("test", alias.Name);
        Assert.Equal("make", alias.Command);
        Assert.Equal(new[] { "test" }, alias.Args);
    }

    [Fact]
    public void MakeProject_WithoutTestTarget_DetectsNothing()
    {
        using var dir = new TempDir();
        dir.WriteFile("Makefile", "build:\n\tgo build\n");
        Assert.Empty(Program.DetectAliases(dir.Path));
    }

    [Fact]
    public void PolyglotProject_NpmWinsTestAlias_OtherAliasesKept()
    {
        using var dir = new TempDir();
        dir.WriteFile("package.json", """{ "scripts": { "test": "vitest", "lint": "eslint ." } }""");
        dir.WriteFile("pyproject.toml", "[project]\nname = 'x'");
        dir.WriteFile("go.mod", "module example.com/x\n");
        dir.WriteFile("Makefile", "test:\n\tgo test ./...\n");

        var aliases = Program.DetectAliases(dir.Path);

        Assert.Equal(2, aliases.Count);
        var test = aliases.Single(a => a.Name == "test");
        Assert.Equal("npm", test.Command);
        Assert.Single(aliases, a => a.Name == "lint");
    }
}

public class RenderConfigTests
{
    [Fact]
    public void NoDetection_RendersPlaceholderAlias()
    {
        var kdl = Program.RenderConfig(new List<Program.DetectedAlias>());
        Assert.Contains("defaults {", kdl);
        Assert.Contains("alias \"test\" {", kdl);
        Assert.Contains("command \"echo\"", kdl);
    }

    [Fact]
    public void DetectedAliases_RenderWithQuotedArgs_AndRoundTrip()
    {
        var kdl = Program.RenderConfig(new List<Program.DetectedAlias>
        {
            new("test", "go", new[] { "test", "./..." }),
            new("lint", "npm", new[] { "run", "lint" }),
        });
        Assert.Contains("alias \"test\" {", kdl);
        Assert.Contains("command \"go\"", kdl);
        Assert.Contains("args \"test\" \"./...\"", kdl);

        var nodes = Kdl.Parse(kdl);
        Assert.Equal(2, nodes.Count(n => n.Name == "alias"));
    }
}
