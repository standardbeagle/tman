using Tman;
using Xunit;

namespace Tman.Tests;

/// <summary>
/// Per-command help, through <see cref="Program.Main"/>. The property that matters most is that
/// asking is harmless: `tman init --help` used to scaffold a project, because init skipped the
/// flag it did not know and then did its job. So every help request here runs in an empty
/// directory against a tman home that does not exist, and must leave both exactly so.
/// </summary>
[Collection("cwd")]
public class HelpCommandTests : IDisposable
{
    readonly TempDir _root = new();
    readonly string _home;
    readonly string _cwd;
    readonly string? _prevHome = Environment.GetEnvironmentVariable("TMAN_HOME");
    readonly string? _prevParent = Environment.GetEnvironmentVariable(Runner.ParentIdEnvVar);
    readonly string _prevCwd = Directory.GetCurrentDirectory();

    public HelpCommandTests()
    {
        _home = Path.Combine(_root.Path, "home-never-created");
        _cwd = _root.Mkdir("project");
        Environment.SetEnvironmentVariable("TMAN_HOME", _home);
        Environment.SetEnvironmentVariable(Runner.ParentIdEnvVar, null);
        Directory.SetCurrentDirectory(_cwd);
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

    public static TheoryData<string> BuiltInCommands()
    {
        var data = new TheoryData<string>();
        foreach (var c in Help.Commands.Where(c => !c.Name.StartsWith('<'))) data.Add(c.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(BuiltInCommands))]
    public async Task EveryCommandsHelp_IsTheSameUnderEverySpelling_AndChangesNothing(string command)
    {
        var asked = new[] { new[] { command, "--help" }, [command, "-h"], ["help", command] };
        var answers = new List<string>();
        foreach (var argv in asked)
        {
            var (exit, printed, err) = await Tman(argv);
            Assert.True(exit == 0, $"`tman {string.Join(' ', argv)}` exited {exit}: {err}");
            answers.Add(printed);
        }

        Assert.StartsWith($"usage: tman {command}", answers[0]);
        Assert.All(answers, a => Assert.Equal(answers[0], a));
        // nothing was created: no store, and no project scaffolding
        Assert.False(Directory.Exists(_home), "help created the tman home");
        Assert.Empty(Directory.EnumerateFileSystemEntries(_cwd));
    }

    [Fact]
    public async Task HelpAfterOtherFlags_IsStillHelp()
    {
        // the help flag counts wherever it sits among the command's own flags
        var (exit, printed, _) = await Tman("init", "--shims", "--help");

        Assert.Equal(0, exit);
        Assert.StartsWith("usage: tman init", printed);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_cwd));
    }

    [Fact]
    public async Task TheOverview_ListsEveryCommand_AndRunsHelpCarriesTheSameFlags()
    {
        var (_, overview, _) = await Tman("--help");
        var (_, run, _) = await Tman("run", "--help");

        foreach (var c in Help.Commands)
            Assert.Contains(c.Name.StartsWith('<') ? c.Usage[0] : $"tman {c.Name}", overview);
        Assert.Contains(Help.RunFlags, overview);
        Assert.Contains(Help.RunFlags, run);
    }

    [Theory]
    [InlineData("init", "--bogus")]
    [InlineData("init", "extra")]
    [InlineData("clean", "--dry-run")]
    [InlineData("list", "--running")]
    [InlineData("status", "--yaml")]
    [InlineData("status", "one", "two")]
    public async Task AnArgumentACommandDoesNotTake_IsRefused(params string[] argv)
    {
        // an old finished record: a sweep that ran anyway would prune it
        Store.Save(new RunRecord
        {
            Id = "oldfinished1", Command = "/bin/true", Args = [], State = RunState.Exited, ExitCode = 0,
            HeartbeatUtc = DateTime.UtcNow.AddDays(-30), StartedUtc = DateTime.UtcNow.AddDays(-30),
        });

        var (exit, _, err) = await Tman(argv);

        Assert.Equal(Runner.ExitNotFound, exit);
        Assert.Contains($"see tman {argv[0]} --help", err);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_cwd));
        Assert.NotNull(Store.Load("oldfinished1"));
    }

    [Fact]
    public async Task HelpForANameThatIsNothing_IsRefused()
    {
        var (exit, _, err) = await Tman("help", "nosuchthing");

        Assert.Equal(Runner.ExitNotFound, exit);
        Assert.Contains("no command or alias 'nosuchthing'", err);
    }

    [Fact]
    public async Task HelpForAnAlias_SaysWhatItRunsAndWhere()
    {
        File.WriteAllText(Path.Combine(_cwd, Config.FileName), """
            alias "test" {
                command "dotnet"
                args "test" "tests/X.csproj"
                max-time "10m"
            }
            """);

        var (exit, printed, _) = await Tman("help", "test");

        Assert.Equal(0, exit);
        Assert.Contains("dotnet test tests/X.csproj [args...]", printed);
        Assert.Contains(Path.Combine(_cwd, Config.FileName), printed);
        Assert.Contains("max-time 10m", printed);
    }

    [UnixFact("the alias's command is sh, echoing what it was given")]
    public async Task AnAliasesHelpFlag_GoesToItsCommand()
    {
        // aliases forward every arg: `tman test --help` asks the test runner, not tman
        File.WriteAllText(Path.Combine(_cwd, Config.FileName), """
            alias "echo" {
                command "sh"
                args "-c" "echo got:$1" "sh"
            }
            """);

        var (exit, printed, _) = await Tman("echo", "--help");

        Assert.Equal(0, exit);
        Assert.Contains("got:--help", printed);
    }

    [UnixFact("the supervised command is sh, echoing what it was given")]
    public async Task HelpAfterTheDoubleDash_GoesToTheSupervisedCommand()
    {
        var (exit, printed, _) = await Tman("run", "--", "sh", "-c", "echo got:$1", "sh", "--help");

        Assert.Equal(0, exit);
        Assert.Contains("got:--help", printed);
    }
}
