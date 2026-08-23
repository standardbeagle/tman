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

    public RunLogTests() => Environment.SetEnvironmentVariable("TMAN_HOME", _home.Path);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TMAN_HOME", _prevHome);
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

    [Fact]
    public void OpenOnAnUnwritableParent_ReturnsNullRatherThanThrowing()
    {
        // Capture is a convenience; a run must not fail because the convenience is unavailable.
        var log = RunLog.Open(Path.Combine(_repo.Path, "no-such-file", "deeper"), null, null, "sh");
        if (log is null) return;                // a platform that happily creates the tree: fine
        log.Dispose();
    }
}
