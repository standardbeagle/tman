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

    /// <summary>
    /// The physical path. `pwd -P` resolves symlinks, and so does getcwd, which the recorded cwd comes
    /// from; the temp root may be one — macOS's /var is a link to /private/var.
    /// </summary>
    static string RealPath(string path)
    {
        var dir = new DirectoryInfo(path);
        while (dir is not null)
        {
            if (dir.LinkTarget is not null) return Path.Combine(dir.ResolveLinkTarget(true)!.FullName, Path.GetRelativePath(dir.FullName, path));
            dir = dir.Parent;
        }
        return path;
    }
}
