using System.Diagnostics;
using System.Runtime.InteropServices;
using Tman;
using Xunit;

namespace Tman.Tests;

/// <summary>
/// A fact whose subject only exists on Unix. Off Unix the run reports it as skipped carrying
/// <paramref name="because"/>, rather than entering a body that returns before asserting anything —
/// a green that exercised nothing is indistinguishable from a green that passed.
/// </summary>
/// <remarks>
/// xUnit 2.9.2 has no dynamic skip (<c>Assert.Skip</c> is v3), but v2 discovery reads
/// <see cref="FactAttribute.Skip"/> off the attribute instance, and the platform is already known
/// there. So the conditional fact needs no runner package and no new dependency.
/// </remarks>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute(string because)
        : this(because, OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) { }

    internal UnixFactAttribute(string because, bool onUnix)
    {
        if (!onUnix) Skip = because;
    }
}

/// <summary>The theory form of <see cref="UnixFactAttribute"/>, skipped off Unix for the same reason.</summary>
public sealed class UnixTheoryAttribute : TheoryAttribute
{
    public UnixTheoryAttribute(string because)
    {
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())) Skip = because;
    }
}

/// <summary>
/// A fact whose subject only exists on Linux — /proc — skipped elsewhere carrying
/// <paramref name="because"/>, for the same reason as <see cref="UnixFactAttribute"/>.
/// </summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute(string because)
    {
        if (!OperatingSystem.IsLinux()) Skip = because;
    }
}

/// <summary>
/// A fact whose subject only exists on macOS; skipped elsewhere carrying <paramref name="because"/>,
/// for the same reason as <see cref="UnixFactAttribute"/>.
/// </summary>
public sealed class MacFactAttribute : FactAttribute
{
    public MacFactAttribute(string because)
    {
        if (!OperatingSystem.IsMacOS()) Skip = because;
    }
}

/// <summary>
/// A fact whose subject only exists on Windows; skipped elsewhere carrying <paramref name="because"/>,
/// for the same reason as <see cref="UnixFactAttribute"/>.
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute(string because)
    {
        if (!OperatingSystem.IsWindows()) Skip = because;
    }
}

internal static class WindowsCommand
{
    /// <summary>
    /// A `powershell` command line for `cmd /c` that runs <paramref name="script"/>. Encoded rather
    /// than quoted: .NET escapes an inner quote as \", cmd passes it through, and PowerShell then
    /// reads `-Command "..."` as a string literal to print — the script never runs, and a test built
    /// on it passes having done nothing.
    /// </summary>
    public static string PowerShell(string script) =>
        "powershell -NoProfile -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
}

/// <summary>
/// The built tman apphost beside the test assembly, started with SIGINT at the default disposition
/// and unblocked — the state a process must be in to answer Ctrl+C, and the one it does not
/// necessarily get: a child keeps an ignored SIGINT from its parent and a blocked one through the
/// inherited signal mask. A test that delivers SIGINT to an apphost starts it here, so it depends on
/// the apphost rather than on how the suite was started.
/// </summary>
/// <remarks>
/// The disposition and the mask are process-wide (the mask, per the thread that forks the child),
/// so one lock covers set, start and restore: two tests doing this at once would restore each
/// other's state. Off Unix this is a plain start; the callers are Unix facts.
/// </remarks>
internal static class AppHost
{
    internal const int Sigint = 2;
    internal const nint SigDfl = 0;
    internal const nint SigIgn = 1;

    /// <summary>
    /// A `struct sigaction`, held as bytes: 152 on Linux (glibc), 16 on macOS. Both layouts put the
    /// handler first, so a zeroed buffer is SIG_DFL with an empty mask and no flags, and one whose
    /// first word is 1 is SIG_IGN. The disposition is saved and restored whole — `signal()` cannot
    /// restore one, since it reinstalls only the handler pointer: the runtime's SA_SIGINFO handler
    /// came back called as a one-argument handler, and the next SIGINT to this host crashed it.
    /// </summary>
    const int SigactionBytes = 256;

    const int SigSetBytes = 128;

    // The `how` argument is the one platform-dependent value here: Linux numbers SIG_BLOCK=0,
    // SIG_UNBLOCK=1, SIG_SETMASK=2; macOS numbers SIG_BLOCK=1, SIG_UNBLOCK=2, SIG_SETMASK=3 (its
    // SIG_BLOCK and SIG_UNBLOCK are the other way round). Passing Linux's 1 for SIG_UNBLOCK on macOS
    // would block SIGINT on the forking thread instead, and the apphost would inherit a blocked SIGINT.
    static readonly int SigUnblock = Hows(OperatingSystem.IsMacOS()).Unblock;
    static readonly int SigSetmask = Hows(OperatingSystem.IsMacOS()).Setmask;

    /// <summary>The (SIG_UNBLOCK, SIG_SETMASK) `how` pair for the running OS, named so a test pins it.</summary>
    internal static (int Unblock, int Setmask) Hows(bool isMacOS) => isMacOS ? (2, 3) : (1, 2);

    static readonly object Gate = new();

    /// <summary>Path of the apphost the test assembly was built beside.</summary>
    internal static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "tman");

    /// <summary>
    /// Starts <paramref name="psi"/> with SIGINT at its default disposition and unblocked on the
    /// calling thread, whatever this host inherited, then restores both. The default disposition is
    /// set before the fork because a SIG_IGN survives exec; the mask is cleared on the forking thread
    /// because that is the one the child inherits. A plain start off Unix, where the callers are
    /// skipped. One lock covers set, start and restore: the disposition and the mask are process-wide.
    /// </summary>
    internal static Process Start(ProcessStartInfo psi)
    {
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
            return Process.Start(psi) ?? throw new InvalidOperationException("could not start apphost");

        lock (Gate)
        {
            var prevMask = new byte[SigSetBytes];
            Check("pthread_sigmask(SIG_SETMASK, NULL, oldset) reading the current mask",
                pthread_sigmask(SigSetmask, null, prevMask));
            InitRuntimeSignalHandling();
            var prevAction = SetSigint(SigDfl);
            try
            {
                var unblock = new byte[SigSetBytes];
                unblock[0] = (byte)(1 << (Sigint - 1));
                Check("pthread_sigmask(SIG_UNBLOCK, {SIGINT}, NULL) unblocking SIGINT",
                    pthread_sigmask(SigUnblock, unblock, null));

                // Read the mask back: a `how` value wrong for this OS blocks SIGINT instead of
                // unblocking it, and the apphost would inherit that and never answer Ctrl+C. Fail here
                // with its own message rather than at a test's 15 s limit.
                var afterUnblock = new byte[SigSetBytes];
                Check("pthread_sigmask(SIG_SETMASK, NULL, oldset) reading the mask back",
                    pthread_sigmask(SigSetmask, null, afterUnblock));
                if ((afterUnblock[0] & (1 << (Sigint - 1))) != 0)
                    throw new InvalidOperationException(
                        "pthread_sigmask did not unblock SIGINT on the forking thread (is SigUnblock correct for this OS?)");

                return Process.Start(psi) ?? throw new InvalidOperationException("could not start apphost");
            }
            finally
            {
                Check("pthread_sigmask(SIG_SETMASK, oldset, NULL) restoring the signal mask",
                    pthread_sigmask(SigSetmask, prevMask, null));
                RestoreSigint(prevAction);
            }
        }
    }

    /// <summary>
    /// Sets this process's SIGINT to ignored, returning the handler to restore. For a test that pins
    /// what an apphost does when its host inherited an ignored SIGINT.
    /// </summary>
    internal static byte[] IgnoreSigint()
    {
        InitRuntimeSignalHandling();
        return SetSigint(SigIgn);
    }

    /// <summary>
    /// Makes the runtime install its own signal handlers before a disposition is saved. It installs
    /// them on first use — the first Process.Start, Console or PosixSignalRegistration — and if
    /// that first use is the Process.Start inside <see cref="Start"/>'s SIG_DFL window, the runtime
    /// records SIG_DFL as the original and the restore then removes its handler: every later
    /// SIGINT to this host takes the default and kills it. Registering any signal is a first use.
    /// </summary>
    static void InitRuntimeSignalHandling() => PosixSignalRegistration.Create(PosixSignal.SIGCONT, _ => { }).Dispose();

    /// <summary>Restores the SIGINT handler <paramref name="previous"/> returned by <see cref="IgnoreSigint"/>.</summary>
    internal static void RestoreSigint(byte[] previous) =>
        Check("sigaction(SIGINT, previous, NULL) restoring SIGINT", sigaction(Sigint, previous, null));

    /// <summary>Sets SIGINT to <paramref name="handler"/> (SIG_DFL or SIG_IGN), returning the whole disposition it replaced.</summary>
    static byte[] SetSigint(nint handler)
    {
        var action = new byte[SigactionBytes];
        BitConverter.TryWriteBytes(action.AsSpan(0, IntPtr.Size), (long)handler);
        var previous = new byte[SigactionBytes];
        Check("sigaction(SIGINT, action, previous) setting SIGINT", sigaction(Sigint, action, previous));
        return previous;
    }

    /// <summary>Throws naming <paramref name="what"/> when a pthread_sigmask call returned non-zero.</summary>
    static void Check(string what, int rc)
    {
        if (rc != 0)
            throw new InvalidOperationException($"{what} failed with {rc} (errno {Marshal.GetLastPInvokeError()})");
    }

    [DllImport("libc", SetLastError = true)]
    static extern int sigaction(int signum, byte[]? act, byte[]? oldact);

    [DllImport("libc", SetLastError = true)]
    static extern int pthread_sigmask(int how, byte[]? set, byte[]? oldset);
}
