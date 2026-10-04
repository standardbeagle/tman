using System.Diagnostics;
using Tman;
using Xunit;

namespace Tman.Tests;

/// <summary>Choosing which CPUs a limit-cpus run gets, and reading what stops a memory scope.</summary>
public class ConfinementChoiceTests
{
    static readonly int[] Sixteen = [.. Enumerable.Range(0, 16)];

    [Fact]
    public void ChooseCpus_PrefersTheCpusFewestLiveRunsHold()
    {
        Assert.Equal([4, 5, 6, 7], Confinement.ChooseCpus(Sixteen, 4, [[0, 1, 2, 3]]));
        // the second run of a pair lands beside the first, not on top of it
        Assert.Equal([8, 9, 10, 11], Confinement.ChooseCpus(Sixteen, 4, [[0, 1, 2, 3], [4, 5, 6, 7]]));
        // held twice is held more than held once
        Assert.Equal([1, 2], Confinement.ChooseCpus([0, 1, 2], 2, [[0, 1], [0]]));
    }

    [Fact]
    public void ChooseCpus_TiesGoToTheLowerCpu_AndOnlyAllowedCpusCount()
    {
        Assert.Equal([0, 1], Confinement.ChooseCpus(Sixteen, 2, []));
        // a run nested under limit-cpus narrows its parent's set, never widens it
        Assert.Equal([5, 9], Confinement.ChooseCpus([3, 5, 9], 2, [[3], [40, 41]]));
    }

    [Fact]
    public void ChooseCpus_AskingForAtLeastWhatIsAllowed_IsEveryAllowedCpu()
    {
        Assert.Equal([2, 3], Confinement.ChooseCpus([2, 3], 2, [[2]]));
        Assert.Equal([2, 3], Confinement.ChooseCpus([2, 3], 8, []));
    }

    [Fact]
    public void LinuxMemScopeRefusal_NamesTheMissingPiece()
    {
        using var runtime = new TempDir();
        Assert.Contains("systemd-run is not on PATH",
            Confinement.LinuxMemScopeRefusal(runtime.Path, 1000, systemdRun: null));
        Assert.Contains("no systemd user manager is running",
            Confinement.LinuxMemScopeRefusal(runtime.Path, 1000, "/usr/bin/systemd-run"));
        Assert.Contains("no systemd user manager is running",
            Confinement.LinuxMemScopeRefusal(null, 1000, "/usr/bin/systemd-run"));
    }

    [Theory]
    [InlineData("limit-cpus", "0")]
    [InlineData("limit-cpus", "-2")]
    [InlineData("limit-cpus", "half")]
    [InlineData("limit-mem", "0")]
    [InlineData("limit-mem", "lots")]
    public void ALimitTmanCannotRead_IsRefused(string key, string value)
    {
        var e = Assert.Throws<FormatException>(() => Caps.With(new Caps(), key, value, key));
        Assert.Contains($"bad {key}", e.Message);
    }

    [Fact]
    public void Limits_AreReadFromConfig_AndMergeLikeEveryOtherCap()
    {
        using var proj = new TempDir();
        proj.WriteFile(Config.FileName, """
            defaults {
                limit-cpus 4
                limit-mem "8g"
            }
            alias "t" {
                command "true"
                limit-mem 512
            }
            """);
        var config = Config.Load(proj.Path)!;
        var caps = Config.EffectiveCaps(config.Aliases["t"], new Caps { LimitCpus = 2 }, config);
        Assert.Equal(2, caps.LimitCpus);
        Assert.Equal(512, caps.LimitMemMb);
        Assert.Equal(8192, Config.EffectiveCaps(null, new Caps(), config).LimitMemMb);
    }
}

/// <summary>
/// A fact that needs a systemd user manager with the memory controller delegated — the one thing
/// limit-mem on Linux is made of. Skipped, naming what is missing, where there is none: a CI runner
/// with no user session has nothing to test it against, and a body that returned early would be a
/// green that exercised nothing.
/// </summary>
public sealed class MemScopeFactAttribute : FactAttribute
{
    public MemScopeFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "limit-mem's systemd scope only exists on Linux";
        else if (Confinement.Refusal(new Caps { LimitMemMb = 64 }) is { } refusal) Skip = refusal;
    }
}

/// <summary>
/// The limits through <see cref="Program.Main"/>, so the flag, the caps chain, and the start all run
/// as a user's `tman run --limit-* -- cmd` does. Each asserts what the child itself sees — its own
/// affinity, its own cgroup's memory.max — since that is the only proof the kernel holds the limit.
/// </summary>
[Collection("cwd")]
public class ConfinementRunTests : IDisposable
{
    readonly TempDir _home = new();
    readonly TempDir _work = new();
    readonly string? _prevHome = Environment.GetEnvironmentVariable("TMAN_HOME");
    readonly string? _prevParent = Environment.GetEnvironmentVariable(Runner.ParentIdEnvVar);
    readonly string? _prevScope = Environment.GetEnvironmentVariable(Confinement.MemScopeEnvVar);
    readonly string? _prevRuntime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
    readonly string _prevCwd = Directory.GetCurrentDirectory();

    public ConfinementRunTests()
    {
        Environment.SetEnvironmentVariable("TMAN_HOME", _home.Path);
        Environment.SetEnvironmentVariable(Runner.ParentIdEnvVar, null);
        // the suite may itself run inside a tman memory scope; this test's runs are not nested in it
        Environment.SetEnvironmentVariable(Confinement.MemScopeEnvVar, null);
        Directory.SetCurrentDirectory(_work.Path);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_prevCwd);
        Environment.SetEnvironmentVariable("TMAN_HOME", _prevHome);
        Environment.SetEnvironmentVariable(Runner.ParentIdEnvVar, _prevParent);
        Environment.SetEnvironmentVariable(Confinement.MemScopeEnvVar, _prevScope);
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", _prevRuntime);
        _work.Dispose();
        _home.Dispose();
    }

    static async Task<(int Exit, string Stderr)> Tman(params string[] argv)
    {
        var err = new StringWriter();
        var prev = Console.Error;
        Console.SetError(err);
        try { return (await Program.Main(argv).WaitAsync(TimeSpan.FromSeconds(60)), err.ToString()); }
        finally { Console.SetError(prev); }
    }

    /// <summary>`Cpus_allowed_list` as the kernel reports it for a process, as a set of CPUs.</summary>
    static int[] ParseCpuList(string list) =>
        [.. list.Trim().Split(',').SelectMany(part =>
        {
            var ends = part.Split('-').Select(int.Parse).ToArray();
            return Enumerable.Range(ends[0], ends[^1] - ends[0] + 1);
        })];

    static int[] OwnAllowedCpus()
    {
        var line = File.ReadLines("/proc/thread-self/status").Single(l => l.StartsWith("Cpus_allowed_list:"));
        return ParseCpuList(line["Cpus_allowed_list:".Length..]);
    }

    [LinuxFact("reads affinity from /proc")]
    public async Task LimitCpus_ConfinesTheChildAndWhatItStarts_AndLeavesTmansThreadAsItWas()
    {
        var before = OwnAllowedCpus();
        // with one CPU there is nothing to narrow, and a one-CPU child would pass by default
        Assert.True(before.Length >= 2, $"needs at least 2 allowed CPUs to observe narrowing, has {before.Length}");

        var (exit, stderr) = await Tman("run", "--limit-cpus", "1", "--", "sh", "-c",
            "grep Cpus_allowed_list /proc/self/status > root; sh -c 'grep Cpus_allowed_list /proc/self/status' > grandchild");

        Assert.True(exit == 0, stderr);
        var record = Assert.Single(Store.LoadAll());
        var cpu = Assert.Single(record.Cpus!);
        Assert.Contains(cpu, before);
        foreach (var file in new[] { "root", "grandchild" })
            Assert.Equal([cpu], ParseCpuList(File.ReadAllText(Path.Combine(_work.Path, file)).Split(':')[1]));
        Assert.Equal(before, OwnAllowedCpus());
    }

    [LinuxFact("reads affinity from /proc")]
    public async Task LimitCpus_StaysOffACpuALiveRunHolds()
    {
        var allowed = OwnAllowedCpus();
        Assert.True(allowed.Length >= 2, $"needs at least 2 allowed CPUs to see a choice, has {allowed.Length}");
        // its runner is this process, stamped as itself, so the run's own sweep agrees it is alive
        var me = ProcUtil.OwnStart();
        Store.Save(new RunRecord
        {
            Id = "holdsfirstcpu", Command = "/bin/sh", Args = [], State = RunState.Running,
            StartedUtc = DateTime.UtcNow, HeartbeatUtc = DateTime.UtcNow, Cpus = [allowed[0]],
            RunnerPid = Environment.ProcessId, RunnerStartUtc = me.Utc, RunnerStartTicks = me.Ticks,
        });

        var (exit, stderr) = await Tman("run", "--limit-cpus", "1", "--", "true");

        Assert.True(exit == 0, stderr);
        Assert.Equal([allowed[1]], Store.LoadAll().Single(r => r.Id != "holdsfirstcpu").Cpus!);
    }

    [MemScopeFact]
    public async Task LimitMem_RunsTheChildInAScopeWhoseMemoryMaxIsTheLimit()
    {
        var (exit, stderr) = await Tman("run", "--limit-mem", "96m", "--", "sh", "-c",
            "d=/sys/fs/cgroup$(cut -d: -f3 /proc/self/cgroup); echo $d > cgroup; cat $d/memory.max $d/memory.swap.max > limits");

        Assert.True(exit == 0, stderr);
        var record = Assert.Single(Store.LoadAll());
        Assert.EndsWith($"/tman-{record.Id}.scope", File.ReadAllText(Path.Combine(_work.Path, "cgroup")).Trim());
        Assert.Equal(["100663296", "0"], File.ReadAllLines(Path.Combine(_work.Path, "limits")));
        // the pid tman watched is the command's own: systemd-run execs it in place
        Assert.Equal(RunState.Exited, record.State);
    }

    [MemScopeFact]
    public async Task LimitMem_TheKernelEndsTheWholeTree_AndTheRunIsCulled()
    {
        Assert.NotNull(Confinement_FindOnPath("python3"));
        var (exit, stderr) = await Tman("run", "--limit-mem", "64m", "--", "sh", "-c",
            "sleep 60 & echo $! > sibling; python3 -c 'a = bytearray(400 * 1024 * 1024)'");

        Assert.True(exit == Runner.ExitCulled, $"exit {exit}: {stderr}");
        var record = Assert.Single(Store.LoadAll());
        Assert.Equal(RunState.Culled, record.State);
        Assert.Contains("limit-mem 64MB", record.KillReason);
        var sibling = int.Parse(File.ReadAllText(Path.Combine(_work.Path, "sibling")));
        Assert.False(Directory.Exists($"/proc/{sibling}"), "the scope's OOM kill left the sleep sibling running");
        // asking the scope what happened is also what clears it
        Assert.Equal("not-found", Systemctl("show", $"tman-{record.Id}.scope", "-p", "LoadState", "--value"));
    }

    [MemScopeFact]
    public async Task ANestedRun_StaysInItsParentsScope()
    {
        Environment.SetEnvironmentVariable(Confinement.MemScopeEnvVar, "tman-outer.scope");
        // no user manager to ask: a nested run must not need one, since it makes no scope
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", _home.Path);

        var (exit, stderr) = await Tman("run", "--limit-mem", "96m", "--", "sh", "-c", "cut -d: -f3 /proc/self/cgroup > cgroup");

        Assert.True(exit == 0, stderr);
        Assert.Contains("stays in its parent's memory scope tman-outer.scope", stderr);
        Assert.DoesNotContain("/tman-", File.ReadAllText(Path.Combine(_work.Path, "cgroup")));
    }

    [LinuxFact("refuses limit-mem where no systemd user manager answers")]
    public async Task LimitMem_WithNoUserManager_IsRefusedBeforeTheRunExists()
    {
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", _home.Path);

        var (exit, stderr) = await Tman("run", "--limit-mem", "96m", "--", "true");

        Assert.Equal(Runner.ExitNotFound, exit);
        Assert.Contains("no systemd user manager is running", stderr);
        Assert.Empty(Store.LoadAll());
    }

    [WindowsFact("sets a Job Object's affinity")]
    public async Task LimitCpus_OnWindows_ConfinesTheChildsChildren()
    {
        Assert.True(Environment.ProcessorCount >= 2, "needs at least 2 CPUs to observe narrowing");
        var (exit, stderr) = await Tman("run", "--limit-cpus", "1", "--", "cmd", "/c",
            WindowsCommand.PowerShell("[Diagnostics.Process]::GetCurrentProcess().ProcessorAffinity.ToInt64() | Out-File -Encoding ascii affinity"));

        Assert.True(exit == 0, stderr);
        var cpu = Assert.Single(Assert.Single(Store.LoadAll()).Cpus!);
        Assert.Equal(1L << cpu, long.Parse(File.ReadAllText(Path.Combine(_work.Path, "affinity")).Trim()));
    }

    [WindowsFact("sets a Job Object's memory limit")]
    public async Task LimitMem_OnWindows_TheJobRefusesTheAllocation_AndTheRunIsCulled()
    {
        var (exit, stderr) = await Tman("run", "--limit-mem", "256m", "--", "cmd", "/c",
            WindowsCommand.PowerShell("$a = New-Object byte[] (1024MB); $a[0] = 1; Start-Sleep 5"));

        Assert.True(exit == Runner.ExitCulled, $"exit {exit}: {stderr}");
        var record = Assert.Single(Store.LoadAll());
        Assert.Equal(RunState.Culled, record.State);
        Assert.Contains("limit-mem 256MB", record.KillReason);
    }

    static string? Confinement_FindOnPath(string program) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, program)).FirstOrDefault(File.Exists);

    static string Systemctl(params string[] args)
    {
        var psi = new ProcessStartInfo("systemctl") { RedirectStandardOutput = true };
        psi.ArgumentList.Add("--user");
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return output;
    }
}
