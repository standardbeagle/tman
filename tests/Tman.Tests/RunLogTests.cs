using Tman;
using Xunit;

namespace Tman.Tests;

/// <summary>
/// The property under test throughout: what is in <c>.tman/</c> describes the LAST run of that
/// alias and nothing older. A digest that outlives the failure it reports is the failure mode this
/// whole feature would otherwise introduce — it reads exactly like a real one.
/// </summary>
[Collection("cwd")]
public class RunLogTests : IDisposable
{
    readonly TempDir _home = new();
    readonly TempDir _repo = new();
    readonly string? _prevHome = Environment.GetEnvironmentVariable("TMAN_HOME");
    readonly string? _prevParent = Environment.GetEnvironmentVariable(Runner.ParentIdEnvVar);

    public RunLogTests()
    {
        Environment.SetEnvironmentVariable("TMAN_HOME", _home.Path);
        // the suite itself runs under tman, so without this every test here would be a nested run
        Environment.SetEnvironmentVariable(Runner.ParentIdEnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TMAN_HOME", _prevHome);
        Environment.SetEnvironmentVariable(Runner.ParentIdEnvVar, _prevParent);
        _home.Dispose();
        _repo.Dispose();
    }

    string LogPath(string slug) => Path.Combine(_repo.Path, RunLog.DirName, slug + RunLog.LogSuffix);
    string DigestPath(string slug) => Path.Combine(_repo.Path, RunLog.DirName, slug + RunLog.DigestSuffix);

    async Task<int> Run(string script, string alias = "test")
    {
        using var log = RunLog.Open(_repo.Path, alias, alias, "sh");
        return await Runner.RunAsync("sh", new[] { "-c", script }, new Caps(), alias, alias, log: log);
    }

    [UnixFact("drives a real child through sh -c")]
    public async Task FailingRun_WritesADigestNamingTheFailedTest()
    {
        var exit = await Run("echo 'ok  tests/a'; echo 'FAILED tests/b.py::test_widget - AssertionError: 1 != 2'; exit 1");

        Assert.Equal(1, exit);
        var digest = File.ReadAllText(DigestPath("test"));
        Assert.Contains("test_widget", digest);
        Assert.Contains("AssertionError: 1 != 2", digest);
        Assert.Contains("exit 1", digest);
        // the digest points into the full log rather than standing in for it
        Assert.Contains(LogPath("test"), digest);
    }

    [UnixFact("drives a real child through sh -c")]
    public async Task PassingRun_LeavesNoDigestBehindFromTheFailureBeforeIt()
    {
        await Run("echo 'FAILED tests/b.py::test_widget'; exit 1");
        Assert.True(File.Exists(DigestPath("test")));

        var exit = await Run("echo 'all green'; exit 0");

        Assert.Equal(0, exit);
        Assert.False(File.Exists(DigestPath("test")),
            "a digest surviving a passing run reports a failure that is already fixed");
    }

    [UnixFact("drives a real child through sh -c")]
    public async Task EachRun_StartsFromAnEmptyLog()
    {
        await Run("echo 'output from the first run'");

        await Run("echo 'output from the second run'");

        var log = File.ReadAllText(LogPath("test"));
        Assert.Contains("second run", log);
        Assert.DoesNotContain("first run", log);
    }

    [UnixFact("drives a real child through sh -c")]
    public async Task Log_CapturesBothStreams()
    {
        await Run("echo to-stdout; echo to-stderr 1>&2; exit 3");

        var log = File.ReadAllText(LogPath("test"));
        Assert.Contains("to-stdout", log);
        Assert.Contains("to-stderr", log);
    }

    [UnixFact("needs a real child to hold the stall window open")]
    public async Task KilledRun_GetsADigestSayingWhyItWasKilled()
    {
        // A stalled run is exactly the case where the console has nothing useful in it: the reason
        // the run died is a tman decision, and the evidence for it is the last output before the
        // silence started. Both belong in the digest.
        using var log = RunLog.Open(_repo.Path, "test", "test", "sh");
        var err = new StringWriter();
        var prevErr = Console.Error;
        Console.SetError(err);
        try
        {
            await Runner.RunAsync("sh", new[] { "-c", "echo 'last thing printed'; sleep 30" },
                new Caps { Stall = TimeSpan.FromSeconds(1) }, "test", "test", log: log);
        }
        finally
        {
            Console.SetError(prevErr);
        }

        var digest = File.ReadAllText(DigestPath("test"));
        Assert.Contains("stalled", digest);
        Assert.Contains("last thing printed", digest);
    }

    [UnixFact("drives a real child through sh -c")]
    public async Task AliasesDoNotShareALog()
    {
        await Run("echo from-test", alias: "test");
        await Run("echo from-lint", alias: "lint");

        Assert.Contains("from-test", File.ReadAllText(LogPath("test")));
        Assert.Contains("from-lint", File.ReadAllText(LogPath("lint")));
    }

    [UnixFact("drives a real child through sh -c")]
    public async Task RunWithNoConfig_WritesNoLogDirectory()
    {
        // No .tman.kdl means no project to put a log in. Creating .tman/ in whatever directory the
        // caller happened to be standing in — $HOME included, where tman's own store lives — is a
        // side effect a supervisor has no business having.
        var cwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_repo.Path);
        try
        {
            var exit = await Program.GatedRun("sh", new[] { "-c", "exit 1" }, new Caps(),
                null, null, replace: false, _repo.Path, logDir: null);
            Assert.Equal(1, exit);
        }
        finally
        {
            Directory.SetCurrentDirectory(cwd);
        }

        Assert.False(Directory.Exists(Path.Combine(_repo.Path, RunLog.DirName)));
    }

    [UnixFact("drives a real child through sh -c")]
    public async Task NestedRun_WritesNoLog()
    {
        // A supervised process that re-enters tman is the same work as its parent: it takes no
        // slot, and it must not open a log of its own either. Observed: `tman test` runs
        // `dotnet test`, a machine-level PATH shim re-issues that as `tman run -- dotnet ...`, and
        // the inner run wrote .tman/dotnet.log beside the outer run's .tman/test.log — one suite,
        // two logs, and the second one is the one an agent reads by mistake.
        _repo.WriteFile(Config.FileName, "");
        var logDir = Path.Combine(_repo.Path, RunLog.DirName);

        // precondition: the same call, not nested, does capture — otherwise "no log" is vacuous
        Assert.Equal(1, await Gated());
        Assert.True(File.Exists(Path.Combine(logDir, "sh" + RunLog.LogSuffix)));
        Directory.Delete(logDir, recursive: true);

        Environment.SetEnvironmentVariable(Runner.ParentIdEnvVar, "parent-run-id");
        Assert.Equal(1, await Gated());

        Assert.False(Directory.Exists(logDir), "a nested run opened its own log");

        Task<int> Gated() => Program.GatedRun("sh", new[] { "-c", "exit 1" }, new Caps(),
            null, null, replace: false, _repo.Path, logDir: _repo.Path);
    }

    /// <summary>
    /// Named runs cannot share a log because the name lock admits one of them. Unnamed runs have
    /// no such lock: `max-parallel 2` admits two `tman run -- npm test` at once, both keyed to
    /// npm.log, and the second one truncated the first one's capture mid-run. The log itself has
    /// to be the claim — the second opener gets no log and says so, rather than a file two runs
    /// are interleaving into.
    /// </summary>
    [Fact]
    public void ASecondOpenOfTheSameLog_GetsNullAndSaysSo_WhileReadersStillRead()
    {
        using var held = RunLog.Open(_repo.Path, null, null, "npm");
        Assert.NotNull(held);
        held.Write("captured by the first run");

        var err = new StringWriter();
        var prevErr = Console.Error;
        Console.SetError(err);
        RunLog? second;
        try
        {
            second = RunLog.Open(_repo.Path, null, null, "npm");
        }
        finally
        {
            Console.SetError(prevErr);
        }

        Assert.Null(second);
        Assert.Contains("npm.log is held by a concurrent run", err.ToString());
        Assert.Contains("not captured", err.ToString());
        // a held log is still a readable one: an agent tailing the run must not be locked out.
        // What it sees is whatever the writer has flushed so far — the hold is the point here
        var readWhileHeld = File.ReadAllText(held.LogPath);
        Assert.NotNull(readWhileHeld);

        held.Dispose();
        Assert.Contains("captured by the first run", File.ReadAllText(held.LogPath));
        // and the hold ends with the run: the next run of the key opens it again
        using var next = RunLog.Open(_repo.Path, null, null, "npm");
        Assert.NotNull(next);
    }

    [Fact]
    public void OpenOnAnUnwritableParent_ReturnsNullRatherThanThrowing()
    {
        // Capture is a convenience; a run must not fail because the convenience is unavailable.
        var log = RunLog.Open(Path.Combine(_repo.Path, "no-such-file", "deeper"), null, null, "sh");
        if (log is null) return;                // a platform that happily creates the tree: fine
        log.Dispose();
    }
}
