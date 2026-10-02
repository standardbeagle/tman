using Tman;
using Xunit;

namespace Tman.Tests;

/// <summary>
/// An agent fixing one repo's CI ran `tman kill all` and killed every sibling agent's supervised
/// run on the box. `kill all` now means "all of mine": the same project root and, when both sides
/// know one, the same agent session. Reaching beyond that takes `--everywhere` and a terminal.
/// </summary>
[Collection("cwd")]
public class KillScopeTests : IDisposable
{
    const string SessionVar = "CLAUDE_CODE_SESSION_ID";

    readonly TempDir _home = new();
    readonly TempDir _work = new();
    readonly string? _prevHome = Environment.GetEnvironmentVariable("TMAN_HOME");
    readonly string? _prevParent = Environment.GetEnvironmentVariable(Runner.ParentIdEnvVar);
    readonly string? _prevSession = Environment.GetEnvironmentVariable(SessionVar);
    readonly string _prevCwd = Directory.GetCurrentDirectory();
    readonly List<Task<int>> _runs = [];

    public KillScopeTests()
    {
        Environment.SetEnvironmentVariable("TMAN_HOME", _home.Path);
        Environment.SetEnvironmentVariable(Runner.ParentIdEnvVar, null);
        Environment.SetEnvironmentVariable(SessionVar, null);
    }

    public void Dispose()
    {
        // only this test's own runs, by id
        foreach (var r in Reaper.LiveRuns()) Reaper.KillRun(r, "test cleanup");
        Task.WaitAll(_runs.ToArray(), TimeSpan.FromSeconds(15));
        Directory.SetCurrentDirectory(_prevCwd);
        Environment.SetEnvironmentVariable("TMAN_HOME", _prevHome);
        Environment.SetEnvironmentVariable(Runner.ParentIdEnvVar, _prevParent);
        Environment.SetEnvironmentVariable(SessionVar, _prevSession);
        Scope.TerminalProbe = null;
        _home.Dispose();
        _work.Dispose();
    }

    /// <summary>Starts a supervised sleep as if tman were invoked from <paramref name="dir"/> by <paramref name="session"/>.</summary>
    async Task<RunRecord> Start(string name, string dir, string? session)
    {
        Directory.SetCurrentDirectory(dir);
        Environment.SetEnvironmentVariable(SessionVar, session);
        _runs.Add(Runner.RunAsync("sleep", ["30"], new Caps(), name, null));
        for (var i = 0; i < 200; i++)
        {
            if (Store.LoadAll().FirstOrDefault(r => r.Name == name) is { ChildStartUtc: not null } r) return r;
            await Task.Delay(50);
        }
        throw new TimeoutException($"run {name} never started its child");
    }

    static bool Live(RunRecord r) => Reaper.LiveRuns().Any(l => l.Id == r.Id);

    /// <summary>A killed child is reaped by its own runner a moment later, so death is waited for.</summary>
    static async Task<bool> Dies(RunRecord r)
    {
        for (var i = 0; i < 100; i++)
        {
            if (!Live(r)) return true;
            await Task.Delay(50);
        }
        return false;
    }

    static async Task<(int Exit, string Out, string Err)> Tman(params string[] argv)
    {
        var (o, e) = (new StringWriter(), new StringWriter());
        var (po, pe) = (Console.Out, Console.Error);
        Console.SetOut(o);
        Console.SetError(e);
        try { return (await Program.Main(argv), o.ToString(), e.ToString()); }
        finally { Console.SetOut(po); Console.SetError(pe); }
    }

    [UnixFact("supervises the sleep binary")]
    public async Task KillAll_SparesRunsOfAnotherProject()
    {
        var mine = await Start("mine", _work.Mkdir("a"), "s1");
        var theirs = await Start("theirs", _work.Mkdir("b"), "s1");
        Directory.SetCurrentDirectory(_work.Path + "/a");

        var (exit, _, _) = await Tman("kill", "all");

        Assert.Equal(0, exit);
        Assert.True(await Dies(mine));
        Assert.True(Live(theirs));
    }

    [UnixFact("supervises the sleep binary")]
    public async Task KillAll_SparesRunsOfAnotherSessionInTheSameProject()
    {
        var dir = _work.Mkdir("a");
        var mine = await Start("mine", dir, "s1");
        var theirs = await Start("theirs", dir, "s2");
        Environment.SetEnvironmentVariable(SessionVar, "s1");

        await Tman("kill", "all");

        Assert.True(await Dies(mine));
        Assert.True(Live(theirs));
    }

    [UnixFact("supervises the sleep binary")]
    public async Task KillAll_ProjectRootIsTheNearestTmanKdlAboveTheCwd()
    {
        var root = _work.Mkdir("repo");
        File.WriteAllText(Path.Combine(root, ".tman.kdl"), "// scope marker\n");
        var sub = _work.Mkdir("repo/src/deep");
        var other = _work.Mkdir("repo2");
        var fromSub = await Start("fromsub", sub, null);
        var elsewhere = await Start("elsewhere", other, null);
        Directory.SetCurrentDirectory(root);

        await Tman("kill", "all");

        Assert.True(await Dies(fromSub));
        Assert.True(Live(elsewhere));
    }

    [UnixFact("supervises the sleep binary")]
    public async Task KillAllEverywhere_WithoutATerminal_RefusesAndNamesTheUserCommand()
    {
        var theirs = await Start("theirs", _work.Mkdir("b"), "s2");
        Directory.SetCurrentDirectory(_work.Mkdir("a"));
        Scope.TerminalProbe = () => false;

        var (exit, _, err) = await Tman("kill", "all", "--everywhere");

        Assert.NotEqual(0, exit);
        Assert.Contains("tman kill all --everywhere", err);
        Assert.True(Live(theirs));
    }

    [UnixFact("supervises the sleep binary")]
    public async Task KillAllEverywhere_WithATerminal_KillsRunsOfOtherProjectsAndSessions()
    {
        var theirs = await Start("theirs", _work.Mkdir("b"), "s2");
        Directory.SetCurrentDirectory(_work.Mkdir("a"));
        Scope.TerminalProbe = () => true;

        var (exit, _, _) = await Tman("kill", "all", "--everywhere");

        Assert.Equal(0, exit);
        Assert.True(await Dies(theirs));
    }

    [UnixFact("supervises the sleep binary")]
    public async Task KillById_OutsideTheScope_StillKillsAndSaysWhoseItWas()
    {
        var theirs = await Start("theirs", _work.Mkdir("b"), "s2");
        Directory.SetCurrentDirectory(_work.Mkdir("a"));
        Environment.SetEnvironmentVariable(SessionVar, "s1");

        var (exit, stdout, _) = await Tman("kill", theirs.Id);

        Assert.Equal(0, exit);
        Assert.True(await Dies(theirs));
        Assert.Contains("outside this scope", stdout);
        Assert.Contains("/b", stdout);
        Assert.Contains("s2", stdout);
    }

    [UnixFact("supervises the sleep binary")]
    public async Task Clean_LeavesLiveRunsOfOtherScopesAlone()
    {
        var theirs = await Start("theirs", _work.Mkdir("b"), "s2");
        Directory.SetCurrentDirectory(_work.Mkdir("a"));

        Assert.Equal(0, (await Tman("clean")).Exit);

        Assert.True(Live(theirs));
    }

    [UnixFact("supervises the sleep binary")]
    public async Task List_ShowsEachRunsProjectRoot()
    {
        await Start("proj", _work.Mkdir("someproject"), null);

        var (_, stdout, _) = await Tman("list");

        Assert.Contains("PROJECT", stdout);
        Assert.Contains("someproject", stdout);
    }
}
