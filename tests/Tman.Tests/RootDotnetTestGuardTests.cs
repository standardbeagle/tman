using System.Diagnostics;
using Xunit;

namespace Tman.Tests;

/// <summary>
/// `dotnet test` run from the repo root resolves <c>tman.csproj</c> — the application project, which
/// holds no tests. Left unguarded it restores, runs nothing, and exits 0 in about three seconds: the
/// fake-green hazard tman exists to name, reproduced in tman's own toolchain. It has already cost
/// real time, since a worktrack template wired its test gate to exactly this command and so could
/// never go red.
/// </summary>
/// <remarks>
/// The guard is an MSBuild target, so its only real consumer is a real <c>dotnet</c> process: asking
/// the project file whether it contains the right text would pass just as happily if the target no
/// longer bound to a target MSBuild actually runs. These tests therefore run the command and assert
/// on what came back.
/// </remarks>
public sealed class RootDotnetTestGuardTests
{
    [Fact]
    public void DotnetTest_AtTheRepoRoot_FailsAndNamesHowToRunTheRealSuite()
    {
        var repoRoot = FindRepoRoot();

        var (exitCode, output) = RunDotnet(repoRoot, "test");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("TMAN0001", output);
        Assert.Contains("./test", output);
        Assert.Contains("tests/Tman.Tests/Tman.Tests.csproj", output);
    }

    /// <summary>
    /// The refusal has to be narrow enough to leave every other entry point alone. <c>dotnet build</c>
    /// at the repo root resolves the same project file, so it is the closest neighbour the guard
    /// could take down with it, and nothing else in this suite would notice if it did.
    /// </summary>
    [Fact]
    public void DotnetBuild_AtTheRepoRoot_IsLeftAlone()
    {
        var repoRoot = FindRepoRoot();

        var (exitCode, output) = RunDotnet(repoRoot, "build");

        Assert.True(exitCode == 0, $"`dotnet build` at the repo root should still succeed, got {exitCode}:\n{output}");
        Assert.DoesNotContain("TMAN0001", output);
    }

    /// <summary>
    /// Walks up from the test binaries rather than trusting the working directory, which xUnit does
    /// not promise. Both markers are required so a partial match cannot silently pick a parent
    /// directory that merely happens to hold a file of the right name.
    /// </summary>
    static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "tman.csproj"))
                && File.Exists(Path.Combine(dir.FullName, "tests", "Tman.Tests", "Tman.Tests.csproj")))
                return dir.FullName;
        }

        throw new InvalidOperationException(
            $"no directory above {AppContext.BaseDirectory} holds both tman.csproj and "
            + "tests/Tman.Tests/Tman.Tests.csproj, so the repo root could not be located");
    }

    static (int ExitCode, string Output) RunDotnet(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var child = Process.Start(psi)
            ?? throw new InvalidOperationException($"could not start `dotnet {string.Join(' ', args)}`");

        // Read both pipes concurrently: a child that fills one while we block on the other deadlocks.
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();

        if (!child.WaitForExit(300_000))
        {
            try { child.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException(
                $"`dotnet {string.Join(' ', args)}` in {cwd} did not exit within 300s");
        }

        child.WaitForExit(); // settles the redirected streams now that the process is gone
        var output = stdout.Result + stderr.Result;
        Assert.False(string.IsNullOrWhiteSpace(output),
            $"`dotnet {string.Join(' ', args)}` produced no output at all, so nothing was observed");
        return (child.ExitCode, output);
    }
}
