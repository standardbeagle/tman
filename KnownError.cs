namespace Tman;

/// <summary>
/// A failure tman can name and tell the user how to clear. <see cref="Program.Main"/> prints the
/// message and each instruction and exits with <see cref="ExitCode"/> — never a stack trace, which
/// is kept for defects. Raised only where the cause is known; anything else propagates as it is.
/// </summary>
public sealed class KnownError(string message, IReadOnlyList<string> instructions, int exitCode, Exception? cause = null)
    : Exception(message, cause)
{
    public IReadOnlyList<string> Instructions { get; } = instructions;
    public int ExitCode { get; } = exitCode;
}
