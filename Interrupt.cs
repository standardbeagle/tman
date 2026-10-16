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
    }

    /// <summary>Unsubscribes a handler <see cref="Listen"/> subscribed; Ctrl+C stays tman's.</summary>
    public static void Unlisten(ConsoleCancelEventHandler handler)
    {
        Console.CancelKeyPress -= handler;
        Interlocked.Decrement(ref s_listening);
    }

    static void Absorb(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        if (Volatile.Read(ref s_listening) == 0)
            Console.Error.WriteLine("tman: interrupted after the run ended; its outcome stands");
    }
}
