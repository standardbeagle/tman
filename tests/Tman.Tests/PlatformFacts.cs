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
