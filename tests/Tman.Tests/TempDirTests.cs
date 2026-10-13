using System.Diagnostics;
using Xunit;

namespace Tman.Tests;

/// <summary>
/// A disposed <see cref="TempDir"/> must not leave the process standing in a directory it just
/// deleted. The cwd collection serialises only the cwd-changing tests, not the process-starting
/// ones they run beside: at efaf080 a using-scope deleted the cwd out from under a parallel
/// <c>Process.Start</c>, which resolves its child through getcwd and threw FileNotFoundException
/// (Process.cs ResolvePath). 5e1ca3b made <see cref="TempDir.Dispose"/> step the process out before
/// deleting; these pin that.
/// </summary>
[Collection("cwd")]
public class TempDirTests : IDisposable
{
    readonly string _origCwd = Directory.GetCurrentDirectory();

    // Restore the cwd unconditionally, not behind a Directory.GetCurrentDirectory() read: the read
    // throws when the cwd is already a deleted directory, which is exactly the state the mutation
    // check (no TempDir.cs:44) leaves behind, and it would hide which cases caught the mutation.
    public void Dispose() => Directory.SetCurrentDirectory(_origCwd);

    static int RunBareName()
    {
        var isWindows = OperatingSystem.IsWindows();
        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "sh",
            Arguments = isWindows ? "/c exit 0" : "-c true",
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }

    [Fact]
    public void DisposingWhileCwdIsTheTempDir_LeavesCwdOutsideTheDeletedPath()
    {
        var dir = new TempDir();
        Directory.SetCurrentDirectory(dir.Path);

        dir.Dispose();

        var cwd = Directory.GetCurrentDirectory();
        Assert.True(Directory.Exists(cwd), $"cwd '{cwd}' names no existing directory");
        Assert.False(IsInside(cwd, dir.Path), $"cwd '{cwd}' is still inside the disposed '{dir.Path}'");
    }

    [Fact]
    public void DisposingWhileCwdIsAMkdirSubdirectory_LeavesCwdOutsideTheDeletedPath()
    {
        var dir = new TempDir();
        var sub = dir.Mkdir("sub");
        Directory.SetCurrentDirectory(sub);

        dir.Dispose();

        var cwd = Directory.GetCurrentDirectory();
        Assert.True(Directory.Exists(cwd), $"cwd '{cwd}' names no existing directory");
        Assert.False(IsInside(cwd, dir.Path), $"cwd '{cwd}' is still inside the disposed '{dir.Path}'");
    }

    [Fact]
    public void DisposingWhileCwdIsAnotherDirectory_LeavesCwdUnchanged()
    {
        var dir = new TempDir();
        using var elsewhere = new TempDir();
        Directory.SetCurrentDirectory(elsewhere.Path);
        // getcwd returns the physical path; TempDir.Path may spell it through the /var -> /private/var
        // symlink on macOS (TempDir.RealPath's doc comment). Capture what getcwd reports so the
        // comparison is physical vs physical, not two spellings of one place.
        var before = Directory.GetCurrentDirectory();

        dir.Dispose();

        Assert.Equal(before, Directory.GetCurrentDirectory());
    }

    [Fact]
    public void AfterDisposingFromTheTempDir_BareNameProcessStartSucceeds()
    {
        var dir = new TempDir();
        Directory.SetCurrentDirectory(dir.Path);

        dir.Dispose();

        Assert.Equal(0, RunBareName());
    }

    static bool IsInside(string dir, string root)
    {
        root = TempDir.RealPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var cwd = TempDir.RealPath(dir).TrimEnd(Path.DirectorySeparatorChar);
        return cwd == root || cwd.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
