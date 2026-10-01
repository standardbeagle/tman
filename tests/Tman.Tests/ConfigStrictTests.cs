using Tman;
using Xunit;

namespace Tman.Tests;

/// <summary>
/// A `.tman.kdl` is read whole or refused. Every case here used to load: the node was skipped or
/// its value read as absent, and the command then ran without the bound the file declared.
/// </summary>
public class ConfigStrictTests
{
    static FormatException Refused(string configText)
    {
        using var proj = new TempDir();
        proj.WriteFile(Config.FileName, configText);
        return Assert.Throws<FormatException>(() => Config.Load(proj.Path));
    }

    [Theory]
    [InlineData("max-time", "bogus")]
    [InlineData("stall", "")]
    [InlineData("queue-timeout", "5 minutes")]
    [InlineData("retain", "99999999999h")]
    [InlineData("max-mem", "lots")]
    [InlineData("max-cpu", "NaN")]
    [InlineData("max-cpu", "-5")]
    [InlineData("max-parallel", "-1")]
    [InlineData("max-parallel", "1.5")]
    public void AnUnreadableCapValue_IsRefusedNamingKeyAndValue(string key, string value)
    {
        var ex = Refused($$"""
            defaults {
                {{key}} "{{value}}"
            }
            """);
        Assert.Contains($"bad {key} \"{value}\"", ex.Message);
        Assert.Contains("defaults", ex.Message);
    }

    [Fact]
    public void AnUnreadableCapInAnAlias_IsRefusedNamingTheAlias()
    {
        var ex = Refused("""
            alias "test" {
                command "true"
                max-time "bogus"
            }
            """);
        Assert.Contains("alias \"test\"", ex.Message);
        Assert.Contains("bad max-time \"bogus\"", ex.Message);
    }

    [Theory]
    [InlineData("defaults {\n    max_time \"5m\"\n}", "unknown setting \"max_time\"")]
    [InlineData("alias \"t\" {\n    command \"true\"\n    maxtime \"5m\"\n}", "unknown setting \"maxtime\"")]
    [InlineData("alais \"t\" {\n    command \"true\"\n}", "unknown node \"alais\"")]
    [InlineData("defaults {\n    max-time \"5m\"\n    max-time \"9m\"\n}", "max-time is set twice")]
    [InlineData("defaults {\n    stall\n}", "stall takes exactly one value")]
    [InlineData("defaults {\n    stall \"1m\" \"2m\"\n}", "stall takes exactly one value")]
    [InlineData("defaults {\n    stall \"1m\" {\n        x 1\n    }\n}", "stall takes no block")]
    [InlineData("defaults {\n}\ndefaults {\n}", "defaults is declared twice")]
    [InlineData("alias \"t\" {\n    command \"a\"\n}\nalias \"T\" {\n    command \"b\"\n}", "alias \"T\" is declared twice")]
    [InlineData("alias {\n    command \"a\"\n}", "alias takes exactly one name")]
    [InlineData("alias \"t\" {\n    command \"npm\" \"test\"\n}", "command takes exactly one program")]
    public void ANodeTmanWouldSkip_IsRefused(string configText, string expected)
    {
        Assert.Contains(expected, Refused(configText).Message);
    }

    [Fact]
    public void EveryCapKey_IsReadFromABlock()
    {
        // the refusal above must not cost a valid key: each one, written as the docs write it
        using var proj = new TempDir();
        proj.WriteFile(Config.FileName, """
            defaults {
                max-time "30m"
                stall "10m"
                max-mem "2g"
                max-cpu 95
                max-parallel 0
                queue-timeout "1h"
                retain "2h"
            }
            alias "t" {
                command "true"
                args "a" "b"
                max-mem 512
            }
            """);
        var config = Config.Load(proj.Path)!;

        Assert.Equal(new Caps
        {
            MaxTime = TimeSpan.FromMinutes(30), Stall = TimeSpan.FromMinutes(10), MaxMemMb = 2048,
            MaxCpuPct = 95, MaxParallel = 0, QueueTimeout = TimeSpan.FromHours(1), Retain = TimeSpan.FromHours(2),
        }, config.Defaults);
        Assert.Equal(512, config.Aliases["t"].Caps.MaxMemMb);
        Assert.Equal(new[] { "a", "b" }, config.Aliases["t"].Args);
        Assert.Equal(Caps.Keys.Count, typeof(Caps).GetProperties().Length);
    }
}

/// <summary>The review's reproduction, end to end: an alias whose deadline cannot be read.</summary>
[Collection("cwd")]
public class InvalidCapRunTests : IDisposable
{
    readonly TempDir _home = new();
    readonly TempDir _proj = new();
    readonly string? _prevHome = Environment.GetEnvironmentVariable("TMAN_HOME");
    readonly string? _prevParent = Environment.GetEnvironmentVariable(Runner.ParentIdEnvVar);
    readonly string _prevCwd = Directory.GetCurrentDirectory();

    public InvalidCapRunTests()
    {
        Environment.SetEnvironmentVariable("TMAN_HOME", _home.Path);
        Environment.SetEnvironmentVariable(Runner.ParentIdEnvVar, null);
        Directory.SetCurrentDirectory(_proj.Path);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_prevCwd);
        Environment.SetEnvironmentVariable("TMAN_HOME", _prevHome);
        Environment.SetEnvironmentVariable(Runner.ParentIdEnvVar, _prevParent);
        _home.Dispose();
        _proj.Dispose();
    }

    [UnixFact("the alias runs `true`, which has no binary off Unix")]
    public async Task AnAliasWhoseDeadlineCannotBeRead_DoesNotRun()
    {
        _proj.WriteFile(Config.FileName, """
            alias "t" {
                command "true"
                max-time "bogus"
            }
            """);
        var err = new StringWriter();
        var prevErr = Console.Error;
        Console.SetError(err);
        int exit;
        try { exit = await Program.Main(["t"]); }
        finally { Console.SetError(prevErr); }

        Assert.Equal(Runner.ExitNotFound, exit);
        Assert.Contains("bad max-time \"bogus\"", err.ToString());
        Assert.Empty(Store.LoadAll());
    }

    [Theory]
    [InlineData("--max-cpu", "Infinity")]
    [InlineData("--max-time", "99999999999h")]
    [InlineData("--max-mem", "1e400")]
    public async Task ACapFlagOutsideItsRange_IsRefusedNotACrash(string flag, string value)
    {
        var err = new StringWriter();
        var prevErr = Console.Error;
        Console.SetError(err);
        int exit;
        try { exit = await Program.Main(["run", flag, value, "--", "true"]); }
        finally { Console.SetError(prevErr); }

        Assert.Equal(Runner.ExitNotFound, exit);
        Assert.Contains($"bad {flag}", err.ToString());
        Assert.Empty(Store.LoadAll());
    }
}
