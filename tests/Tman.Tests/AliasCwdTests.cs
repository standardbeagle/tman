using Tman;
using Xunit;

namespace Tman.Tests;

/// <summary>
/// Where an alias runs. `.tman.kdl` is found by walking up from the caller's directory, and its
/// aliases are written relative to it — `dotnet test tests/X.csproj` — so an alias invoked from a
/// subdirectory has to run in the config's directory or its paths mean nothing. A bare
/// `tman run -- cmd` keeps the caller's directory: the user is standing where they mean to run.
/// </summary>
[Collection("cwd")]
public class AliasCwdTests : IDisposable
{
    readonly TempDir _home = new();
    readonly TempDir _repo = new();
    readonly string _sub;
    readonly string? _prevHome = Environment.GetEnvironmentVariable("TMAN_HOME");
    readonly string? _prevParent = Environment.GetEnvironmentVariable(Runner.ParentIdEnvVar);
    readonly string _prevCwd = Directory.GetCurrentDirectory();

    public AliasCwdTests()
    {
        Environment.SetEnvironmentVariable("TMAN_HOME", _home.Path);
        Environment.SetEnvironmentVariable(Runner.ParentIdEnvVar, null);
        _repo.WriteFile(Config.FileName, """
            alias "where" {
                command "sh"
                args "-c" "pwd -P"
            }
            """);
        _sub = _repo.Mkdir("src/deeper");
        Directory.SetCurrentDirectory(_sub);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_prevCwd);
        Environment.SetEnvironmentVariable("TMAN_HOME", _prevHome);
        Environment.SetEnvironmentVariable(Runner.ParentIdEnvVar, _prevParent);
        _home.Dispose();
        _repo.Dispose();
    }

    /// <summary>The child's own answer to `pwd`, and the directory the run record says it ran in.</summary>
    static async Task<(string Printed, string Recorded)> RanIn(params string[] argv)
    {
        var prevOut = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            Assert.Equal(0, await Program.Main(argv));
        }
        finally
        {
            Console.SetOut(prevOut);
        }
        var record = Assert.Single(Store.LoadAll());
        return (captured.ToString().Trim(), record.Cwd!);
    }

    [UnixFact("drives a real child through sh -c")]
    public async Task AnAliasInvokedFromASubdirectory_RunsInTheConfigsDirectory()
    {
        // precondition: the caller really is below the config, or the assertion is the cwd default
        Assert.NotEqual(Canon.Dir(_repo.Path), Canon.Dir(Directory.GetCurrentDirectory()));

        var (printed, recorded) = await RanIn("where");

        Assert.Equal(Canon.Dir(RealPath(_repo.Path)), printed);
        Assert.Equal(Canon.Dir(RealPath(_repo.Path)), recorded);
    }

    [UnixFact("drives a real child through sh -c")]
    public async Task RunDashDashAlias_RunsInTheConfigsDirectoryToo()
    {
        var (printed, recorded) = await RanIn("run", "--alias", "where");

        Assert.Equal(Canon.Dir(RealPath(_repo.Path)), printed);
        Assert.Equal(Canon.Dir(RealPath(_repo.Path)), recorded);
    }

    [UnixFact("drives a real child through sh -c")]
    public async Task ABareRun_StaysWhereTheCallerIsStanding()
    {
        var (printed, recorded) = await RanIn("run", "--", "sh", "-c", "pwd -P");

        Assert.Equal(Canon.Dir(RealPath(_sub)), printed);
        Assert.Equal(Canon.Dir(RealPath(_sub)), recorded);
    }

    [UnixTheory("the alias's program is a shell script made executable with chmod")]
    [InlineData("./tool")]
    [InlineData("scripts/tool")]
    public async Task AnAliasProgramWrittenRelativeToTheConfig_IsFoundFromASubdirectory(string program)
    {
        // An alias's args are relative to its config, and so is a program written as a path. It was
        // resolved against the caller's directory instead, so `tman tool` from src/deeper looked
        // for src/deeper/tool and exited 127.
        var script = _repo.WriteFile(program, "#!/bin/sh\npwd -P\n");
        File.SetUnixFileMode(script, File.GetUnixFileMode(script) | UnixFileMode.UserExecute);
        _repo.WriteFile(Config.FileName, $$"""
            alias "tool" {
                command "{{program}}"
            }
            """);
        // precondition: the same relative path from where the caller stands names nothing
        Assert.False(File.Exists(Path.Combine(_sub, program)));

        var (printed, _) = await RanIn("tool");

        Assert.Equal(Canon.Dir(RealPath(_repo.Path)), printed);
        Assert.Equal(Canon.Dir(RealPath(script)), Assert.Single(Store.LoadAll()).Command);
    }

    static string RealPath(string path) => TempDir.RealPath(path);
}
