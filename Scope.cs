namespace Tman;

/// <summary>Whose runs a caller may kill in bulk. See <see cref="Reaper"/>.</summary>
public static class Scope
{
    /// <summary>
    /// Test seam: whether the caller has a controlling terminal. Null means ask the OS. An agent
    /// has none, which is the property <c>kill all --everywhere</c> relies on.
    /// </summary>
    internal static Func<bool>? TerminalProbe;
}
