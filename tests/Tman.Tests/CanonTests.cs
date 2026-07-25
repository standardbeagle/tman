using Tman;
using Xunit;

namespace Tman.Tests;

public class CanonTests
{
    [Fact]
    public void ResolveCommand_FindsBareNameOnPath()
    {
        if (OperatingSystem.IsWindows()) return;

        var resolved = Canon.ResolveCommand("sh");

        Assert.True(System.IO.Path.IsPathRooted(resolved), $"expected an absolute path, got '{resolved}'");
        Assert.True(File.Exists(resolved));
        Assert.Equal("sh", Canon.CommandLabel(resolved));
    }

    [Fact]
    public void ResolveCommand_IsIdempotent()
    {
        if (OperatingSystem.IsWindows()) return;

        var once = Canon.ResolveCommand("sh");
        Assert.Equal(once, Canon.ResolveCommand(once));
    }

    [Fact]
    public void ResolveCommand_UnknownName_IsReturnedUnchanged()
    {
        // the run is about to fail to start; the name the user typed is the useful thing to report
        Assert.Equal("definitely-not-a-real-command-xyz", Canon.ResolveCommand("definitely-not-a-real-command-xyz"));
    }

    [Fact]
    public void ResolveCommand_CollapsesRelativeSegments()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal("/bin/sh", Canon.ResolveCommand("/bin/./sh"));
    }

    [Fact]
    public void Dir_IsAbsoluteWithoutTrailingSeparator()
    {
        var sep = Path.DirectorySeparatorChar;
        var dir = Path.Combine(Path.GetTempPath(), "canon-dir-check");

        Assert.Equal(Canon.Dir(dir), Canon.Dir(dir + sep));
        Assert.False(Canon.Dir(dir + sep).EndsWith(sep), "trailing separator should be trimmed");
        Assert.True(Path.IsPathRooted(Canon.Dir("relative-thing")), "should absolutize against the cwd");
    }

    [Fact]
    public void Dir_KeepsTheRootItself()
    {
        // trimming a root turns "/" into "" and "D:\" into "D:", which means the drive's
        // current directory rather than the drive root
        var root = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))!;
        Assert.Equal(root, Canon.Dir(root));
        Assert.True(Path.IsPathRooted(Canon.Dir(root)));
    }

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(59, "59s")]
    [InlineData(60, "1m00s")]
    [InlineData(3599, "59m59s")]
    [InlineData(3600, "1h00m")]
    [InlineData(5400, "1h30m")]
    [InlineData(86399, "23h59m")]
    [InlineData(86400, "1d00h")]
    [InlineData(320000, "3d16h")]
    public void Duration_UsesUnitsThatDoNotWrap(int seconds, string expected)
    {
        Assert.Equal(expected, Canon.Duration(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Duration_NinetyMinutes_IsNotConfusedWithThirty()
    {
        // TimeSpan's "mm:ss" rendered 90 minutes as "30:00"; that was the bug
        Assert.NotEqual(
            Canon.Duration(TimeSpan.FromMinutes(30)),
            Canon.Duration(TimeSpan.FromMinutes(90)));
    }

    [Fact]
    public void Duration_ClampsNegativeClockSkew()
    {
        Assert.Equal("0s", Canon.Duration(TimeSpan.FromSeconds(-5)));
    }

    [Theory]
    [InlineData(0, "0MB")]
    [InlineData(512, "512MB")]
    [InlineData(1024, "1.0GB")]
    [InlineData(3277, "3.2GB")]
    public void Mem_ScalesToGigabytes(long mb, string expected) => Assert.Equal(expected, Canon.Mem(mb));

    [Fact]
    public void CommandLine_LeavesOrdinaryArgsUnquoted()
    {
        Assert.Equal("dotnet test ./tests/A.csproj --nologo",
            Canon.CommandLine("/usr/bin/dotnet", new[] { "test", "./tests/A.csproj", "--nologo" }));
    }

    [Fact]
    public void CommandLine_QuotesArgsThatWouldReparse()
    {
        Assert.Equal("dotnet --logger 'console;verbosity=normal'",
            Canon.CommandLine("/usr/bin/dotnet", new[] { "--logger", "console;verbosity=normal" }));
        Assert.Equal("sh -c 'echo hi'", Canon.CommandLine("/bin/sh", new[] { "-c", "echo hi" }));
        Assert.Equal("sh ''", Canon.CommandLine("/bin/sh", new[] { "" }));
    }

    [Fact]
    public void CommandLine_EscapesEmbeddedQuotes()
    {
        Assert.Equal("""sh -c 'echo '\''hi'\'''""",
            Canon.CommandLine("/bin/sh", new[] { "-c", "echo 'hi'" }));
    }

    [Fact]
    public void CommandLine_FullKeepsTheResolvedPath()
    {
        Assert.Equal("/usr/bin/dotnet test",
            Canon.CommandLine("/usr/bin/dotnet", new[] { "test" }, full: true));
    }

    [Fact]
    public void Ellipsize_MarksTruncation()
    {
        Assert.Equal("abc", Canon.Ellipsize("abc", 5));
        Assert.Equal("abcd…", Canon.Ellipsize("abcdefgh", 5));
    }
}
