using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Tman;

/// <summary>
/// Ctrl+C, answered by tman for the rest of the process once a run has listened for it. .NET
/// dispatches a SIGINT on a thread of its own, with the handlers registered when the signal arrived,
/// and a dispatch that no handler cancels ends the process by the signal's default: exit 130,
/// nothing on stderr, and a run record that may never be made final. A run's handler leaves when the
/// run ends, but a dispatch can land after that — the signal arrived as the run ended, or its thread
/// ran late on a loaded host — so the first listener also subscribes <see cref="Absorb"/>, which
/// never leaves. A Ctrl+C no run is listening for is cancelled and said, and the run's recorded
/// outcome stands.
/// <para>
/// Because that thread can run late, a handler alone cannot say whether a SIGINT arrived before a
/// child's exit was read: a child that exits 0 on the same Ctrl+C read as a finished run. So every
/// listen also counts SIGINTs in signal context (<c>native/tman_sigint.c</c>), and
/// <see cref="Received"/> answers synchronously. The kernel delivers the SIGINT before the SIGCHLD of
/// a child that exits on it, so the count has moved by the time the exit is seen. The counter is C,
/// not an [UnmanagedCallersOnly] method: a signal lands on whatever thread is running, a reverse
/// P/Invoke entered on a thread already in managed code fails fast, and attaching an unknown thread
/// allocates — neither is safe in a signal handler.
/// </para>
/// </summary>
internal static class Interrupt
{
    static int s_absorbing;
    static int s_listening;

    /// <summary>Subscribes <paramref name="handler"/> to Ctrl+C until <see cref="Unlisten"/>.</summary>
    public static void Listen(ConsoleCancelEventHandler handler)
    {
        if (Interlocked.Exchange(ref s_absorbing, 1) == 0) Console.CancelKeyPress += Absorb;
        Interlocked.Increment(ref s_listening);
        Console.CancelKeyPress += handler;
        // after the subscription, which installs the runtime's handler: the counter chains in front
        // of it. Every listen re-checks, so a disposition replaced since is counted again
        if (!OperatingSystem.IsWindows() && tman_sigint_install() != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "tman: cannot count SIGINTs");
    }

    /// <summary>Unsubscribes a handler <see cref="Listen"/> subscribed; Ctrl+C stays tman's.</summary>
    public static void Unlisten(ConsoleCancelEventHandler handler)
    {
        Console.CancelKeyPress -= handler;
        Interlocked.Decrement(ref s_listening);
    }

    /// <summary>
    /// SIGINTs this process has received since it first listened, counted as each arrived. Windows
    /// has no SIGINT, and reads 0.
    /// </summary>
    public static long Received => OperatingSystem.IsWindows() ? 0 : tman_sigint_received();

    static void Absorb(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        if (Volatile.Read(ref s_listening) == 0)
            Console.Error.WriteLine("tman: interrupted after the run ended; its outcome stands");
    }

    [DllImport("tman_sigint", SetLastError = true)]
    static extern int tman_sigint_install();

    [DllImport("tman_sigint")]
    static extern long tman_sigint_received();
}
