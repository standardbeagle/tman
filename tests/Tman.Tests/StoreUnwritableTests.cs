using Tman;
using Xunit;

namespace Tman.Tests;

/// <summary>
/// A run store tman cannot write is a known condition — an agent sandbox that mounts $HOME
/// read-only is the everyday case — so it ends in one explained line and exit 74, never a stack
/// trace. Every case is driven through <see cref="Program.Main"/>: an exception escaping it is
/// exactly what the published binary prints as an unhandled-exception abort (exit 134).
/// </summary>
[Collection("cwd")]
public class StoreUnwritableTests : IDisposable
{
    readonly TempDir _home = new();
    readonly string? _prevHome = Environment.GetEnvironmentVariable("TMAN_HOME");
    readonly string? _prevParent = Environment.GetEnvironmentVariable(Runner.ParentIdEnvVar);
    readonly string _prevCwd = Directory.GetCurrentDirectory();

    // The suite runs under tman from inside this repo: a run that inherits its run id is nested and
    // claims no slot, and the repo's .tman.kdl would cap an otherwise uncapped run. Both are cleared
    // so each case reaches the store write it names, and the second is asserted.
    public StoreUnwritableTests()
    {
        Environment.SetEnvironmentVariable("TMAN_HOME", _home.Path);
        Environment.SetEnvironmentVariable(Runner.ParentIdEnvVar, null);
        Directory.SetCurrentDirectory(_home.Path);
        Assert.Null(Config.Load());
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TMAN_HOME", _prevHome);
        Environment.SetEnvironmentVariable(Runner.ParentIdEnvVar, _prevParent);
        Directory.SetCurrentDirectory(_prevCwd);
        var runs = Path.Combine(_home.Path, "runs");
        if (Directory.Exists(runs) && !OperatingSystem.IsWindows())
            File.SetUnixFileMode(runs, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _home.Dispose();
    }

    static async Task<(int Exit, string Err)> Run(params string[] argv)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var prevOut = Console.Out;
        var prevErr = Console.Error;
        Console.SetOut(o);
        Console.SetError(e);
        try { return (await Program.Main(argv), e.ToString()); }
        finally { Console.SetOut(prevOut); Console.SetError(prevErr); }
    }

    /// <summary>
    /// Makes the runs directory refuse writes, and proves it does: a test user who can write it
    /// anyway (root) would otherwise pass every case below without reaching the failure at all.
    /// </summary>
    string LockRunsDir()
    {
        var runs = Path.Combine(_home.Path, "runs");
        Directory.CreateDirectory(runs);
        File.SetUnixFileMode(runs, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(Path.Combine(runs, "probe"), ""));
        return runs;
    }

    static void AssertExplained(string err, string storeDir)
    {
        Assert.Contains($"tman: cannot write its run store {storeDir}", err);
        Assert.Contains("TMAN_HOME", err);
        Assert.Contains("writable", err);
        Assert.DoesNotContain("   at ", err);
    }

    [UnixFact("unix file modes")]
    public async Task SlotClaim_InAnUnwritableStore_ExplainsAndExits74()
    {
        var runs = LockRunsDir();

        var (exit, err) = await Run("run", "--max-parallel", "1", "--", "true");

        Assert.Equal(Runner.ExitStoreUnwritable, exit);
        AssertExplained(err, runs);
        // the refusal named is the slot's, so the claim — not a later record write — was what failed
        Assert.Contains("-slot0.lock", err);
    }

    [LinuxFact("finds the child through /proc")]
    public async Task UngatedRun_InAnUnwritableStore_KillsTheChildItCannotRecord()
    {
        LockRunsDir();
        // a duration no other process on the host is sleeping, so /proc names only this child
        var marker = $"37.{Random.Shared.Next(100_000, 999_999)}";

        // no slot to claim, so the child is already running when its record is first written
        var (exit, err) = await Run("run", "--max-parallel", "0", "--", "sleep", marker);

        Assert.Equal(Runner.ExitStoreUnwritable, exit);
        AssertExplained(err, Path.Combine(_home.Path, "runs"));
        Assert.False(ChildAlive(marker));
    }

    static bool ChildAlive(string marker) => Directory.EnumerateDirectories("/proc").Any(d =>
    {
        try { return File.ReadAllText(Path.Combine(d, "cmdline")).Contains(marker); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    });

    [LinuxFact("finds the child through /proc")]
    public async Task AStoreLostMidRun_KillsTheChildRatherThanRunItUnrecorded()
    {
        var runs = Path.Combine(_home.Path, "runs");
        var marker = $"37.{Random.Shared.Next(100_000, 999_999)}";
        // the child takes the store away itself, once its record is there — so the first write
        // succeeded and it is a later heartbeat that finds the store gone
        var script = $"until ls '{runs}'/*.json >/dev/null 2>&1; do sleep 0.05; done; " +
                     $"chmod 555 '{runs}'; exec sleep {marker}";

        var started = DateTime.UtcNow;
        var (exit, err) = await Run("run", "--max-parallel", "0", "--", "sh", "-c", script);

        Assert.Equal(Runner.ExitStoreUnwritable, exit);
        AssertExplained(err, runs);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20), "tman waited out the child");
        Assert.False(ChildAlive(marker));
    }

    [Fact]
    public async Task AStoreThatCannotBeCreated_ExplainsAndExits74()
    {
        var notADir = Path.Combine(_home.Path, "home-is-a-file");
        File.WriteAllText(notADir, "");
        Environment.SetEnvironmentVariable("TMAN_HOME", notADir);

        var (exit, err) = await Run("list");

        Assert.Equal(Runner.ExitStoreUnwritable, exit);
        AssertExplained(err, Path.Combine(notADir, "runs"));
    }
}
