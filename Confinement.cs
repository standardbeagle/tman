using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Tman;

/// <summary>
/// The limits the kernel enforces on a run's tree, as opposed to the caps tman samples once a
/// second and culls on. <c>max-mem</c> can be outrun: a runner that allocates 30 GB between two
/// ticks has already pushed the machine into swap by the time tman looks. These cannot.
/// <list type="bullet">
/// <item><c>limit-cpus</c>: an affinity mask, so the tree runs on N CPUs and `nproc`, .NET's
/// ProcessorCount, Go's GOMAXPROCS and pytest-xdist all size their worker pools to N. Linux sets it
/// on the starting thread around the fork, so the child inherits it before it runs a single
/// instruction; Windows sets it on the run's Job Object.</item>
/// <item><c>limit-mem</c>: on Linux a memory cgroup, made by starting the child through
/// <c>systemd-run --user --scope</c> — a user's own cgroup is the one place a user may set
/// memory.max, and the user manager is what delegates it. The scope is
/// <c>OOMPolicy=kill</c>, so the kernel's OOM kill ends the whole tree rather than one worker. On
/// Windows a Job Object's job memory limit, which fails the allocation that would cross it; tman
/// then ends the tree.</item>
/// </list>
/// macOS has neither, for an unprivileged process, and a run that asks for either is refused there.
/// A limit someone wrote down is enforced or refused, never quietly left out.
/// </summary>
public sealed partial class Confinement : IDisposable
{
    /// <summary>
    /// Set on the children of a run whose memory is in a scope of its own, naming that scope. A
    /// nested tman that made a scope of its own would move its child out of the parent's, and out
    /// from under the parent's limit — so it stays where it is, and says so.
    /// </summary>
    public const string MemScopeEnvVar = "TMAN_MEM_SCOPE";

    /// <summary>How long tman waits on systemctl when it asks what became of a run's scope.</summary>
    static readonly TimeSpan SystemctlTimeout = TimeSpan.FromSeconds(5);

    readonly Caps _caps;
    readonly int[]? _cpus;
    /// <summary>The run's systemd scope unit (Linux limit-mem), or null when it has none.</summary>
    readonly string? _memScope;

    Confinement(Caps caps, int[]? cpus, string? memScope)
    {
        _caps = caps;
        _cpus = cpus;
        _memScope = memScope;
    }

    /// <summary>
    /// The confinement <paramref name="record"/>'s caps ask for, with the CPUs it will run on chosen
    /// and stored on the record. Throws FormatException naming what this machine cannot enforce.
    /// </summary>
    public static Confinement For(RunRecord record)
    {
        var caps = record.Caps;
        if (caps.LimitCpus is null && caps.LimitMemMb is null) return new Confinement(caps, null, null);
        if (Refusal(caps) is { } refusal) throw new FormatException(refusal);

        int[]? cpus = null;
        if (caps.LimitCpus is { } count)
        {
            var claimed = Store.LoadAll()
                .Where(r => !r.IsFinished && r.Id != record.Id && r.Cpus is not null)
                .Select(r => r.Cpus!);
            cpus = ChooseCpus(AllowedCpus(), count, claimed);
            record.Cpus = cpus;
        }

        string? memScope = null;
        if (caps.LimitMemMb is { } mb && OperatingSystem.IsLinux())
        {
            if (Environment.GetEnvironmentVariable(MemScopeEnvVar) is { Length: > 0 } parentScope)
                Console.Error.WriteLine(
                    $"tman: nested run stays in its parent's memory scope {parentScope}; " +
                    $"its own limit-mem {Canon.Mem(mb)} is not applied separately");
            else
                memScope = $"tman-{record.Id}.scope";
        }

        var confinement = new Confinement(caps, cpus, memScope);
        if (OperatingSystem.IsWindows()) confinement.CreateJob();
        return confinement;
    }

    /// <summary>
    /// Why this machine cannot enforce <paramref name="caps"/>' limits, or null when it can (or
    /// they set none). Read before a run queues, so a run that can never start does not wait for a
    /// slot first, and again when it starts.
    /// </summary>
    public static string? Refusal(Caps caps)
    {
        if (caps.LimitCpus is null && caps.LimitMemMb is null) return null;
        if (OperatingSystem.IsMacOS())
            return "limit-cpus and limit-mem cannot be enforced on macOS, which offers an unprivileged " +
                   "process neither CPU affinity nor a memory ceiling for a process tree; " +
                   "max-mem and max-cpu cull on samples instead";
        if (!OperatingSystem.IsLinux() || caps.LimitMemMb is null) return null;
        // a nested run makes no scope (see MemScopeEnvVar), so needs no user manager
        if (Environment.GetEnvironmentVariable(MemScopeEnvVar) is { Length: > 0 }) return null;
        return LinuxMemScopeRefusal(
            Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"), getuid(), FindOnPath("systemd-run"));
    }

    /// <summary>
    /// What stops a memory scope being made, read from the three things one needs: systemd-run,
    /// a running user manager to ask, and the memory controller delegated to it. Every one is the
    /// difference between a run that is limited and one whose `systemd-run` fails with exit 1 —
    /// which would read as the run's own failure.
    /// </summary>
    internal static string? LinuxMemScopeRefusal(string? runtimeDir, uint uid, string? systemdRun)
    {
        const string needs = "limit-mem on Linux runs the command in a systemd user scope";
        if (systemdRun is null) return $"{needs}, and systemd-run is not on PATH";
        if (runtimeDir is not { Length: > 0 } || !File.Exists(Path.Combine(runtimeDir, "systemd", "private")))
            return $"{needs}, and no systemd user manager is running for this user " +
                   "(no $XDG_RUNTIME_DIR/systemd/private; `loginctl enable-linger` starts one)";
        var controllers = $"/sys/fs/cgroup/user.slice/user-{uid}.slice/user@{uid}.service/cgroup.subtree_control";
        string delegated;
        try { delegated = File.ReadAllText(controllers); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"{needs}, and {controllers} cannot be read ({e.Message}); is this cgroup v2?";
        }
        return delegated.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains("memory")
            ? null
            : $"{needs}, and the memory controller is not delegated to it ({controllers} has \"{delegated.Trim()}\")";
    }

    /// <summary>
    /// <paramref name="count"/> of the <paramref name="allowed"/> CPUs, preferring those the fewest
    /// live runs already hold: two runs of limit-cpus 4 on sixteen CPUs get eight between them, not
    /// the same four. Ties go to the lower CPU. A count at or above what is allowed is every allowed
    /// CPU — the ceiling holds already, and asking for more than exists is not an error.
    /// </summary>
    internal static int[] ChooseCpus(IReadOnlyList<int> allowed, int count, IEnumerable<int[]> claimed)
    {
        if (count >= allowed.Count) return [.. allowed];
        var load = allowed.ToDictionary(cpu => cpu, _ => 0);
        foreach (var cpus in claimed)
            foreach (var cpu in cpus)
                if (load.ContainsKey(cpu)) load[cpu]++;
        return [.. allowed.OrderBy(cpu => load[cpu]).ThenBy(cpu => cpu).Take(count).Order()];
    }

    /// <summary>The CPUs tman itself may run on — a nested run narrows its parent's set, never widens it.</summary>
    static int[] AllowedCpus()
    {
        if (OperatingSystem.IsWindows()) return AllowedCpusWindows();
        var mask = ThreadAffinity();
        var cpus = new List<int>();
        for (var i = 0; i < mask.Length * 64; i++)
            if ((mask[i / 64] & (1UL << (i % 64))) != 0) cpus.Add(i);
        return [.. cpus];
    }

    /// <summary>
    /// Starts <paramref name="psi"/> inside this confinement. On Linux the affinity is set on the
    /// calling thread and restored after: .NET forks from the thread that calls Process.Start, and a
    /// forked child inherits that thread's mask, so the child and everything it starts run on the
    /// chosen CPUs from the first instruction. Setting it on the child afterwards would let anything
    /// it forked in the meantime keep every CPU.
    /// </summary>
    public Process Start(ProcessStartInfo psi)
    {
        if (_memScope is not null) WrapInMemScope(psi, _memScope, _caps.LimitMemMb!.Value);
        if (OperatingSystem.IsWindows()) return StartInJob(psi);
        if (_cpus is null) return Process.Start(psi) ?? throw new InvalidOperationException("failed to start process");

        var saved = ThreadAffinity();
        SetThreadAffinity(MaskOf(_cpus));
        try { return Process.Start(psi) ?? throw new InvalidOperationException("failed to start process"); }
        finally { SetThreadAffinity(saved); }
    }

    /// <summary>
    /// Rewrites <paramref name="psi"/> to run its command through `systemd-run --scope`, which asks
    /// the user manager for the scope and then execs the command in place: the pid tman watches is
    /// the command's own. The scope is not collected on failure, so that after the run tman can still
    /// ask it whether it ended in an OOM kill — <see cref="Verdict"/> clears it once asked.
    /// </summary>
    internal static void WrapInMemScope(ProcessStartInfo psi, string unit, long limitMemMb)
    {
        var command = psi.FileName;
        var args = psi.ArgumentList.ToArray();
        psi.FileName = FindOnPath("systemd-run") ?? throw new FormatException("systemd-run is not on PATH");
        psi.ArgumentList.Clear();
        foreach (var a in (string[])[
                     "--user", "--scope", "--quiet", $"--unit={unit}",
                     "-p", $"MemoryMax={limitMemMb * 1024 * 1024}",
                     // a ceiling the tree can swap past is not a ceiling on what it takes from the machine
                     "-p", "MemorySwapMax=0",
                     // the default policy, DefaultOOMPolicy=stop, ends the tree too — but a host can
                     // set it to continue, which would leave the rest of the tree running
                     "-p", "OOMPolicy=kill",
                     "--", command])
            psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment[MemScopeEnvVar] = unit;
    }

    /// <summary>
    /// A limit the tree crossed while running, once per tick: Windows reports the job memory limit
    /// through the job's completion port. On Linux the kernel acts on its own and there is nothing
    /// to report until the run is over.
    /// </summary>
    public string? Breach() => OperatingSystem.IsWindows() ? JobMemoryBreach() : null;

    /// <summary>
    /// After the root has exited: why a limit ended the run, or null when none did. On Linux the
    /// scope's Result says whether the kernel's OOM kill ended it; the root's own status is only a
    /// SIGKILL, which says nothing about who sent it.
    /// </summary>
    public string? Verdict()
    {
        if (OperatingSystem.IsWindows()) return JobMemoryBreach();
        if (_memScope is null) return null;
        var result = Systemctl("show", _memScope, "-p", "Result", "--value");
        // a scope that ended cleanly is unloaded at once; one that failed stays until it is reset
        if (result is { Length: > 0 } && result != "success") Systemctl("reset-failed", _memScope);
        // unknown leaves the child's own status standing — after an OOM kill that is 137, a failure,
        // so not knowing who killed it can cost the label but never turn into a pass
        if (result is null)
            Console.Error.WriteLine(
                $"tman: cannot tell whether limit-mem ended the run: `systemctl --user show {_memScope}` failed");
        return result == "oom-kill"
            ? $"tree memory reached limit-mem {Canon.Mem(_caps.LimitMemMb!.Value)}; the kernel ended the tree"
            : null;
    }

    /// <summary>systemctl --user's trimmed stdout, or null when it could not be asked.</summary>
    static string? Systemctl(params string[] args)
    {
        var psi = new ProcessStartInfo(FindOnPath("systemctl") ?? "systemctl")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--user");
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(SystemctlTimeout)) { p.Kill(); return null; }
            return p.ExitCode == 0 ? stdout.Result.Trim() : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    static string? FindOnPath(string program) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, program))
            .FirstOrDefault(File.Exists);

    public void Dispose()
    {
        if (OperatingSystem.IsWindows()) CloseJob();
    }

    // 1024 CPUs, the kernel's CONFIG_NR_CPUS default; a mask smaller than the kernel's is EINVAL
    const int AffinityWords = 16;

    internal static ulong[] ThreadAffinity()
    {
        var mask = new ulong[AffinityWords];
        if (sched_getaffinity(0, AffinityWords * sizeof(ulong), mask) != 0)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "sched_getaffinity");
        return mask;
    }

    static void SetThreadAffinity(ulong[] mask)
    {
        if (sched_setaffinity(0, AffinityWords * sizeof(ulong), mask) != 0)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "sched_setaffinity");
    }

    static ulong[] MaskOf(int[] cpus)
    {
        var mask = new ulong[AffinityWords];
        foreach (var cpu in cpus) mask[cpu / 64] |= 1UL << (cpu % 64);
        return mask;
    }

    [DllImport("libc", SetLastError = true)]
    static extern int sched_getaffinity(int pid, nint cpusetsize, ulong[] mask);

    [DllImport("libc", SetLastError = true)]
    static extern int sched_setaffinity(int pid, nint cpusetsize, ulong[] mask);

    [DllImport("libc")]
    static extern uint getuid();
}
