using Tman;
using Xunit;

namespace Tman.Tests;

/// <summary>
/// `tman kill --stale-only` never matched anything: every command sweeps first, so a run whose
/// runner is dead has been reaped before the kill looks, and the flag then compared a possibly
/// recycled pid with IsAlive. It is gone. What must not happen in its place is the flag being
/// dropped on the floor — `kill all --stale-only` read as `kill all` kills the runs the caller
/// asked to keep. An unknown flag refuses the command, as it does for `run`.
/// </summary>
[Collection("cwd")]
public class KillFlagsTests : IDisposable
{
    readonly TempDir _home = new();
    readonly string? _prevHome = Environment.GetEnvironmentVariable("TMAN_HOME");

    public KillFlagsTests() => Environment.SetEnvironmentVariable("TMAN_HOME", _home.Path);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TMAN_HOME", _prevHome);
        _home.Dispose();
    }

    [Fact]
    public async Task KillWithAnUnknownFlag_RefusesRatherThanKillingEverythingElseNamed()
    {
        var err = new StringWriter();
        var prevErr = Console.Error;
        Console.SetError(err);
        int exit;
        try
        {
            exit = await Program.Main(new[] { "kill", "all", "--stale-only" });
        }
        finally
        {
            Console.SetError(prevErr);
        }

        Assert.Equal(Runner.ExitNotFound, exit);
        Assert.Contains("unknown flag --stale-only", err.ToString());
    }

    [Fact]
    public async Task Help_NoLongerOffersStaleOnly()
    {
        var prevOut = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            Assert.Equal(0, await Program.Main(new[] { "--help" }));
        }
        finally
        {
            Console.SetOut(prevOut);
        }

        Assert.DoesNotContain("--stale-only", captured.ToString());
    }
}
