using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Tman;
using Xunit;

namespace Tman.Tests;

/// <summary>
/// Two rules every command keeps, enforced over the dispatch table rather than a hand-kept list:
/// every command `Main` dispatches answers `--help`, and asking for help never touches a file.
/// <see cref="HelpCommandTests"/> proves the second in an empty directory with no tman home; this
/// proves it where it can actually go wrong — a real project with a .tman.kdl, a .gitignore, shims
/// and a populated run store, where `init` could rewrite, `clean` could prune and `kill` could kill.
/// `tman init --help` once scaffolded a .tman.kdl into a repo that had none.
/// </summary>
[Collection("cwd")]
public class HelpIsHarmlessTests : IDisposable
{
    readonly TempDir _root = new();
    readonly string _home;
    readonly string _cwd;
    readonly string? _prevHome = Environment.GetEnvironmentVariable("TMAN_HOME");
    readonly string? _prevParent = Environment.GetEnvironmentVariable(Runner.ParentIdEnvVar);
    readonly string _prevCwd = Directory.GetCurrentDirectory();

    public HelpIsHarmlessTests()
    {
        _home = _root.Mkdir("home");
        _cwd = _root.Mkdir("project");
        Environment.SetEnvironmentVariable("TMAN_HOME", _home);
        Environment.SetEnvironmentVariable(Runner.ParentIdEnvVar, null);
        Directory.SetCurrentDirectory(_cwd);

        File.WriteAllText(Path.Combine(_cwd, Config.FileName), """
            alias "test" {
                command "dotnet"
                args "test"
            }
            """);
        File.WriteAllText(Path.Combine(_cwd, ".gitignore"), "bin/\n");
        Directory.CreateDirectory(Path.Combine(_cwd, ".tman"));
        File.WriteAllText(Path.Combine(_cwd, ".tman", "test.fail.log"), "a failure digest\n");
        // a finished record past retention, for a sweep or `clean` to prune, and a live-looking
        // record in this project, for `kill all` to kill
        Store.Save(new RunRecord
        {
            Id = "oldfinished1", Command = "/bin/true", Args = [], State = RunState.Exited, ExitCode = 0,
            HeartbeatUtc = DateTime.UtcNow.AddDays(-30), StartedUtc = DateTime.UtcNow.AddDays(-30),
        });
        Store.Save(new RunRecord
        {
            Id = "liverecord1", Command = "/bin/sleep", Args = ["600"], State = RunState.Running,
            Pid = Environment.ProcessId, RunnerPid = Environment.ProcessId, Cwd = _cwd, ProjectRoot = _cwd,
            HeartbeatUtc = DateTime.UtcNow, StartedUtc = DateTime.UtcNow,
        });
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_prevCwd);
        Environment.SetEnvironmentVariable("TMAN_HOME", _prevHome);
        Environment.SetEnvironmentVariable(Runner.ParentIdEnvVar, _prevParent);
        _root.Dispose();
    }

    static async Task<(int Exit, string Out, string Err)> Tman(params string[] argv)
    {
        var (outw, errw) = (new StringWriter(), new StringWriter());
        var (prevOut, prevErr) = (Console.Out, Console.Error);
        Console.SetOut(outw);
        Console.SetError(errw);
        try { return (await Program.Main(argv), outw.ToString(), errw.ToString()); }
        finally
        {
            Console.SetOut(prevOut);
            Console.SetError(prevErr);
        }
    }

    /// <summary>Every file and directory under <paramref name="dir"/>: path, bytes, and write time.</summary>
    static SortedDictionary<string, string> Snapshot(string dir)
    {
        var snap = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var d in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories))
            snap[Path.GetRelativePath(dir, d) + "/"] = "dir";
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)));
            snap[Path.GetRelativePath(dir, f)] = $"{hash} {File.GetLastWriteTimeUtc(f):O}";
        }
        return snap;
    }

    /// <summary>The command names `Main`'s dispatch switch handles, read from Program.cs itself.</summary>
    static string[] DispatchedCommands([CallerFilePath] string self = "")
    {
        var program = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(self)!, "..", "..", "Program.cs"));
        var source = File.ReadAllText(program);
        var main = source[source.IndexOf("Main(string[] argv)", StringComparison.Ordinal)..];
        var body = main[..main.IndexOf("default:", StringComparison.Ordinal)];
        var names = Regex.Matches(body, "case \"([a-z][a-z-]*)\"(?: or \"([a-z][a-z-]*)\")*")
            .SelectMany(m => m.Groups.Cast<Group>().Skip(1).SelectMany(g => g.Captures.Select(c => c.Value)))
            .Distinct()
            .ToArray();
        Assert.NotEmpty(names);
        return names;
    }

    [Fact]
    public void EveryDispatchedCommand_HasAnEntryInTheHelpTable()
    {
        var missing = DispatchedCommands().Where(n => Help.Find(n) is null).ToArray();
        Assert.True(missing.Length == 0,
            $"Main dispatches {string.Join(", ", missing)} with no Help.Commands entry, so its --help would run it");
    }

    public static TheoryData<string[]> HelpRequests()
    {
        var data = new TheoryData<string[]>();
        var names = DispatchedCommands();
        foreach (var name in names)
        {
            data.Add([name, "--help"]);
            data.Add([name, "-h"]);
            data.Add(["help", name]);
        }
        // the flag counts after a command's own flags and positionals too
        data.Add(["init", "--shims", "--gitignore", "--help"]);
        data.Add(["kill", "all", "--help"]);
        data.Add(["kill", "all", "--everywhere", "-h"]);
        data.Add(["kill", "liverecord1", "--help"]);
        data.Add(["status", "liverecord1", "--help"]);
        data.Add(["hook", "pretooluse", "--help"]);
        data.Add(["run", "--alias", "test", "--help"]);
        data.Add(["probe", "--pid", "1", "--help"]);
        return data;
    }

    [Theory]
    [MemberData(nameof(HelpRequests))]
    public async Task Help_InAPopulatedProject_PrintsUsage_AndTouchesNoFile(string[] argv)
    {
        var (projectBefore, homeBefore) = (Snapshot(_cwd), Snapshot(_home));

        var (exit, printed, err) = await Tman(argv);

        var asked = $"`tman {string.Join(' ', argv)}`";
        Assert.True(exit == 0, $"{asked} exited {exit}: {err}");
        Assert.Contains("usage: tman", printed);
        Assert.Equal(projectBefore, Snapshot(_cwd));
        Assert.Equal(homeBefore, Snapshot(_home));
        Assert.NotNull(Store.Load("oldfinished1"));
        Assert.Equal(RunState.Running, Store.Load("liverecord1")!.State);
    }
}
