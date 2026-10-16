using System.Diagnostics;
using System.Runtime.InteropServices;
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
        var tmanPath = AppHost.Path;
        Assert.True(File.Exists(tmanPath), $"no tman apphost beside the test assembly at {tmanPath}");

        var psi = new ProcessStartInfo
        {
            FileName = "perl",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // Not the test assembly's own directory: that resolves tman's .tman.kdl upward and the
            // run would write its log into this repo's working tree.
            WorkingDirectory = _home.Path,
        };
        psi.Environment["TMAN_HOME"] = _home.Path;
        InOwnProcessGroup(psi, tmanPath, "run", "--", "sh", "-c", "trap 'kill $!; exit 0' INT; sleep 30 & wait");

        using var tman = AppHost.Start(psi);
        var stdout = tman.StandardOutput.ReadToEndAsync();
        var stderr = tman.StandardError.ReadToEndAsync();
        try
        {
            await AwaitLiveChildPid(tman);

            // the child exits 0 on the same Ctrl+C that interrupts tman, the case in which an honest 0
            // would be the wrong answer
            InterruptLikeATerminal(tman);

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

    /// <summary>
    /// The recorded gate failure made deterministic: the test host ignores SIGINT, as a background
    /// job does, and the apphost must still answer Ctrl+C. The apphost never sees this host's
    /// disposition — the helper starts it with SIGINT at its default and unblocked — so the interrupt
    /// depends on the apphost and not on how the suite was started.
    /// </summary>
    [UnixFact("delivers SIGINT with `kill` to an apphost started while this host ignored SIGINT")]
    public async Task Interrupt_ReportsKilled_WhenTheTestHostInheritedAnIgnoredSigint()
    {
        var previous = AppHost.IgnoreSigint();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "perl",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = _home.Path,
            };
            psi.Environment["TMAN_HOME"] = _home.Path;
            InOwnProcessGroup(psi, AppHost.Path, "run", "--", "sleep", "30");

            using var tman = AppHost.Start(psi);
            var stdout = tman.StandardOutput.ReadToEndAsync();
            var stderr = tman.StandardError.ReadToEndAsync();
            try
            {
                await AwaitLiveChildPid(tman);
                InterruptLikeATerminal(tman);

                Assert.True(tman.WaitForExit(15_000),
                    "the apphost did not answer SIGINT while its host had SIGINT ignored");
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
        finally
        {
            AppHost.RestoreSigint(previous);
        }
    }

    /// <summary>
    /// Pins the one platform-dependent value AppHost.Start passes to pthread_sigmask. Linux numbers
    /// SIG_UNBLOCK=1, SIG_SETMASK=2; macOS numbers SIG_UNBLOCK=2, SIG_SETMASK=3. Handing macOS the
    /// Linux pair would block SIGINT on the forking thread instead of unblocking it, the apphost would
    /// inherit the block and never answer Ctrl+C, and every SIGINT test would die at its 15 s limit.
    /// </summary>
    [UnixFact("pthread_sigmask's `how` values differ between Linux and macOS")]
    public void TheSigHowValues_MatchTheRunningOperatingSystem()
    {
        // Both branches, so a future platform that falls into the wrong one is caught here rather
        // than at a 15 s wait on the leg that runs it.
        Assert.Equal((Unblock: 1, Setmask: 2), AppHost.Hows(isMacOS: false));
        Assert.Equal((Unblock: 2, Setmask: 3), AppHost.Hows(isMacOS: true));
        var expected = OperatingSystem.IsMacOS() ? (Unblock: 2, Setmask: 3) : (Unblock: 1, Setmask: 2);
        Assert.Equal(expected, AppHost.Hows(OperatingSystem.IsMacOS()));
    }

    /// <summary>
    /// .NET dispatches a SIGINT on a thread of its own, with the handlers registered when the signal
    /// arrived, and a dispatch that no handler cancels ends the process by the signal's default:
    /// exit 130, nothing on stderr. A run's own handler leaves when the run ends, so a Ctrl+C that
    /// lands as it ends — or whose dispatch thread runs late on a loaded host — used to kill tman
    /// that way (the gate failure behind this test, 2026-10-15). Here the order is fixed rather than
    /// raced: the run is over before the signal is sent, and the dispatch thread is joined before
    /// anything is asserted. This host keeps a cancelling registration of its own, so it survives
    /// whatever tman does; what is pinned is that tman still answers.
    /// </summary>
    [UnixFact("delivers SIGINT to this test host, which holds a cancelling registration")]
    public async Task ACtrlCAfterTheRunEnded_IsAnsweredByTman_NotLeftToTheRuntimeDefault()
    {
        using var hostGuard = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => ctx.Cancel = true);
        using var proj = new TempDir();
        var prevCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(proj.Path);
        var err = new StringWriter();
        var prevErr = Console.Error;
        Console.SetError(err);
        try
        {
            Assert.Equal(0, await Program.Main(["run", "--no-queue", "--", "true"]));

            // registered after the run, so it is handed the dispatch first and names its thread;
            // every handler of that one dispatch runs on that thread, so joining it is the barrier
            var dispatch = new TaskCompletionSource<Thread>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var barrier = PosixSignalRegistration.Create(PosixSignal.SIGINT, _ => dispatch.TrySetResult(Thread.CurrentThread));
            using (var kill = Process.Start("kill", new[] { "-INT", Environment.ProcessId.ToString() }))
            {
                kill!.WaitForExit();
                Assert.Equal(0, kill.ExitCode);
            }
            var thread = await dispatch.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "the SIGINT dispatch did not finish");

            Assert.Contains("interrupted after the run ended", err.ToString());
        }
        finally
        {
            Console.SetError(prevErr);
            Directory.SetCurrentDirectory(prevCwd);
        }
    }

    /// <summary>
    /// Runs tman as the leader of a process group of its own (perl's setpgrp, then exec, so the pid is
    /// tman's), which its child joins, as a shell's foreground job would.
    /// </summary>
    static void InOwnProcessGroup(ProcessStartInfo psi, string tmanPath, params string[] args)
    {
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add("setpgrp(0, 0); exec { $ARGV[0] } @ARGV or die \"exec: $!\"");
        psi.ArgumentList.Add(tmanPath);
        foreach (var a in args) psi.ArgumentList.Add(a);
    }

    /// <summary>
    /// SIGINT to tman's whole process group in one kill(2), as a terminal sends Ctrl+C. Two pids in
    /// one `kill` are two syscalls, and a `kill` descheduled between them let the child exit and tman
    /// finish its run before tman was signalled at all — a run that ended before Ctrl+C, honestly 0.
    /// </summary>
    static void InterruptLikeATerminal(Process tman)
    {
        using var kill = Process.Start("kill", new[] { "-INT", "--", "-" + tman.Id })!;
        kill.WaitForExit();
        Assert.Equal(0, kill.ExitCode);
    }

    /// <summary>Waits for tman to record its child, so the group is signalled with the child in it.</summary>
    static async Task<int> AwaitLiveChildPid(Process tman)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            Assert.False(tman.HasExited, "tman exited before it recorded a child run");
            // a running child only: a queued run has pid 0, and `kill -INT 0` is the whole process group
            var live = Store.LoadAll().FirstOrDefault(r => r is { State: RunState.Running, Pid: > 0 });
            if (live is not null) return live.Pid;
            await Task.Delay(50);
        }
        throw new TimeoutException("tman recorded no live run within 10s");
    }
}
