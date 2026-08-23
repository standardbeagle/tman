using System.Diagnostics;
using Tman;
using Xunit;

namespace Tman.Tests;

/// <summary>
/// An interrupted run is not a finished run, whatever the child says on its way out. Ctrl+C reaches
/// every process in the terminal's group at once, so a child that traps SIGINT and exits 0 (a test
/// runner shutting down cleanly) hands tman a zero for work that never completed. Pinned
/// out-of-process against the built apphost, because the interrupt path is a console signal handler
/// that an in-process test cannot reach without signalling its own host.
/// </summary>
[Collection("cwd")]
public class InterruptTests : IDisposable
{
    readonly TempDir _home = new();
    readonly string? _prevHome = Environment.GetEnvironmentVariable("TMAN_HOME");

    public InterruptTests() => Environment.SetEnvironmentVariable("TMAN_HOME", _home.Path);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TMAN_HOME", _prevHome);
        _home.Dispose();
    }

    [UnixFact("delivers SIGINT with `kill` to an `sh -c` child")]
    public async Task Interrupt_ReportsKilled_EvenWhenTheChildExitsZero()
    {
        var tmanPath = Path.Combine(AppContext.BaseDirectory, "tman");
        Assert.True(File.Exists(tmanPath), $"no tman apphost beside the test assembly at {tmanPath}");

        var psi = new ProcessStartInfo
        {
            FileName = tmanPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // Not the test assembly's own directory: that resolves tman's .tman.kdl upward and the
            // run would write its log into this repo's working tree.
            WorkingDirectory = _home.Path,
        };
        psi.Environment["TMAN_HOME"] = _home.Path;
        foreach (var a in new[] { "run", "--", "sh", "-c", "trap 'kill $!; exit 0' INT; sleep 30 & wait" })
            psi.ArgumentList.Add(a);

        using var tman = Process.Start(psi) ?? throw new InvalidOperationException("could not start tman");
        var stdout = tman.StandardOutput.ReadToEndAsync();
        var stderr = tman.StandardError.ReadToEndAsync();
        try
        {
            var child = await AwaitLiveChildPid(tman);

            // the child first, so it has already exited 0 by the time tman learns it was interrupted —
            // the ordering in which an honest 0 would be the wrong answer
            using (var kill = Process.Start("kill", new[] { "-INT", child.ToString(), tman.Id.ToString() }))
            {
                kill!.WaitForExit();
                Assert.Equal(0, kill.ExitCode);
            }

            Assert.True(tman.WaitForExit(15_000),
                "tman did not exit within 15s of SIGINT; either the signal never arrived (is SIGINT " +
                "ignored in this host?) or the interrupt did not stop the run");
            tman.WaitForExit();

            Assert.Equal(Runner.ExitKilled, tman.ExitCode);
            Assert.Contains("interrupted", await stderr);
            Assert.Equal("", await stdout);
        }
        finally
        {
            try { if (!tman.HasExited) tman.Kill(entireProcessTree: true); } catch { }
        }
    }

    /// <summary>Waits for tman to record its child, so the test signals the real sh and not a pid it guessed.</summary>
    static async Task<int> AwaitLiveChildPid(Process tman)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            Assert.False(tman.HasExited, "tman exited before it recorded a child run");
            var live = Store.LoadAll().FirstOrDefault(r => !r.IsFinished);
            if (live is not null) return live.Pid;
            await Task.Delay(50);
        }
        throw new TimeoutException("tman recorded no live run within 10s");
    }
}
