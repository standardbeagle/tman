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
/// <item><c>limit-mem</c>: on Linux a memory cgroup. An unprivileged process may set memory.max
/// only on a cgroup delegated to it, so the cgroup comes from whoever delegates one: by default the
/// systemd user manager, through <c>systemd-run --user --scope</c> with <c>OOMPolicy=kill</c>; or,
/// when the machine config names a <c>cgroup</c>, a leaf tman makes in that delegated directory
/// itself with <c>memory.oom.group</c> set — a container's own cgroup, or one an admin handed over.
/// Either way the kernel's OOM kill ends the whole tree rather than one worker. On Windows a Job
/// Object's job memory limit, which fails the allocation that would cross it; tman then ends the
/// tree.</item>
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
    /// <summary>The run's systemd scope unit (Linux limit-mem, by default), or null when it has none.</summary>
    readonly string? _memScope;
    /// <summary>The run's own cgroup directory (Linux limit-mem in a configured cgroup), or null.</summary>
    readonly string? _memLeaf;

    Confinement(Caps caps, int[]? cpus, string? memScope = null, string? memLeaf = null)
    {
        _caps = caps;
        _cpus = cpus;
        _memScope = memScope;
        _memLeaf = memLeaf;
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

        string? memScope = null, memLeaf = null;
        if (caps.LimitMemMb is { } mb && OperatingSystem.IsLinux())
        {
            if (Environment.GetEnvironmentVariable(MemScopeEnvVar) is { Length: > 0 } parentScope)
                Console.Error.WriteLine(
                    $"tman: nested run stays in its parent's memory scope {parentScope}; " +
                    $"its own limit-mem {Canon.Mem(mb)} is not applied separately");
            else if (Config.MemCgroup() is { } root)
                memLeaf = MakeMemLeaf(root, $"tman-{record.Id}", mb);
            else
                memScope = $"tman-{record.Id}.scope";
        }

        var confinement = new Confinement(caps, cpus, memScope, memLeaf);
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
        // a nested run makes no cgroup (see MemScopeEnvVar), so needs nothing to make one with
        if (Environment.GetEnvironmentVariable(MemScopeEnvVar) is { Length: > 0 }) return null;
        return Config.MemCgroup() is { } root
            ? MemCgroupRefusal(root, OwnCgroupDir())
            : SystemdScopeRefusal();
    }

    /// <summary>What stops a systemd user scope being made for limit-mem on this machine, or null.</summary>
    internal static string? SystemdScopeRefusal() =>
        LinuxMemScopeRefusal(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"), getuid(), FindOnPath("systemd-run"));

    /// <summary>
    /// What stops tman making a run's cgroup inside the configured <paramref name="root"/>, given
    /// that tman itself runs in <paramref name="ownCgroup"/>. Besides the directory being there,
    /// carrying the memory controller and being tman's to write, the kernel moves a process between
    /// cgroups only for a writer of cgroup.procs in the nearest cgroup holding both — so a root
    /// delegated to this user is still no use to a tman running outside it, as from a login session
    /// or WSL's /non-systemd, where that nearest cgroup is the machine's own.
    /// </summary>
    internal static string? MemCgroupRefusal(string root, string ownCgroup)
    {
        var needs = $"limit-mem makes its cgroups in {root}, from the machine config {Config.MachineConfigPath}";
        if (!Directory.Exists(root)) return $"{needs}, and it does not exist";
        string controllers;
        try { controllers = File.ReadAllText(Path.Combine(root, "cgroup.controllers")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"{needs}, and it is not a cgroup v2 directory ({e.Message})";
        }
        if (!controllers.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains("memory"))
            return $"{needs}, and the memory controller is not enabled for it — enable it in its parent's cgroup.subtree_control";
        foreach (var file in new[] { root, Path.Combine(root, "cgroup.subtree_control") })
            if (!Writable(file)) return $"{needs}, and {file} is not writable by this user";
        var ancestor = NearestCommonDir(root, ownCgroup);
        var procs = Path.Combine(ancestor, "cgroup.procs");
        return Writable(procs)
            ? null
            : $"{needs}, and tman runs in {ownCgroup}: moving a process from there into {root} needs " +
              $"write access to {procs}. Run tman inside the delegated cgroup's subtree, as from a container's own cgroup";
    }

    /// <summary>The deepest directory that contains both paths.</summary>
    internal static string NearestCommonDir(string a, string b)
    {
        var x = a.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var y = b.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var shared = x.Zip(y).TakeWhile(p => p.First == p.Second).Count();
        return "/" + string.Join('/', x.Take(shared));
    }

    /// <summary>tman's own cgroup directory, from /proc/self/cgroup's v2 line.</summary>
    static string OwnCgroupDir() =>
        "/sys/fs/cgroup" + File.ReadLines("/proc/self/cgroup").First(l => l.StartsWith("0::"))[3..].TrimEnd('/');

    static bool Writable(string path) => access(path, WriteOk) == 0;

    /// <summary>
    /// The run's cgroup in <paramref name="root"/>: memory.max at the limit, no swap to spill into,
    /// and memory.oom.group so the kernel's OOM kill takes the whole tree. The root's
    /// subtree_control is given the memory controller if it lacks it — the root being delegated to
    /// this user is what the machine config says, and a leaf without the controller has no
    /// memory.max to set. A cgroup half made is removed before the error that says why.
    /// </summary>
    static string MakeMemLeaf(string root, string name, long limitMemMb)
    {
        var leaf = Path.Combine(root, name);
        try
        {
            var subtree = Path.Combine(root, "cgroup.subtree_control");
            if (!File.ReadAllText(subtree).Split(' ', StringSplitOptions.TrimEntries).Contains("memory"))
                File.WriteAllText(subtree, "+memory");
            Directory.CreateDirectory(leaf);
            File.WriteAllText(Path.Combine(leaf, "memory.max"), (limitMemMb * 1024 * 1024).ToString());
            // a ceiling the tree can swap past is not a ceiling on what it takes from the machine
            File.WriteAllText(Path.Combine(leaf, "memory.swap.max"), "0");
            File.WriteAllText(Path.Combine(leaf, "memory.oom.group"), "1");
            return leaf;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            RemoveMemLeaf(leaf);
            throw new FormatException($"cannot make the run's cgroup {leaf} for limit-mem: {e.Message}");
        }
    }

    /// <summary>
    /// Removes the run's cgroup, ending first anything the run left in it. The run is over when its
    /// root is, and a leftover inside the cgroup is the run's by construction — as the scope's would
    /// be, which systemd ends with it.
    /// </summary>
    static void RemoveMemLeaf(string leaf)
    {
        if (!Directory.Exists(leaf)) return;
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2;
        while (true)
        {
            try { Directory.Delete(leaf); return; }
            catch (IOException) when (Stopwatch.GetTimestamp() < deadline)
            {
                try { File.WriteAllText(Path.Combine(leaf, "cgroup.kill"), "1"); }
                catch (IOException) { }
                Thread.Sleep(20);
            }
            catch (IOException e)
            {
                Console.Error.WriteLine($"tman: cannot remove the run's cgroup {leaf}: {e.Message}");
                return;
            }
        }
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
        if (_memLeaf is not null) WrapInMemLeaf(psi, _memLeaf);
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
    /// Rewrites <paramref name="psi"/> to join <paramref name="leaf"/> before the command runs: a
    /// shell writes its own pid into the leaf's cgroup.procs and then execs the command, so the pid
    /// tman watches is the command's own, and nothing the command starts can start outside the leaf.
    /// Moving the child in after Process.Start would leave a window for that; moving tman in would
    /// put tman under the limit, where the OOM kill that ends the tree would end its record too. A
    /// write that fails fails the shell, loudly, before the command runs.
    /// </summary>
    internal static void WrapInMemLeaf(ProcessStartInfo psi, string leaf)
    {
        var command = psi.FileName;
        var args = psi.ArgumentList.ToArray();
        psi.FileName = "/bin/sh";
        psi.ArgumentList.Clear();
        foreach (var a in (string[])["-c", "echo $$ > \"$0\" && exec \"$@\"", Path.Combine(leaf, "cgroup.procs"), command])
            psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment[MemScopeEnvVar] = leaf;
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
        if (_memLeaf is not null) return LeafOomKilled(_memLeaf) ? OomVerdict() : null;
        if (_memScope is null) return null;
        var result = Systemctl("show", _memScope, "-p", "Result", "--value");
        // a scope that ended cleanly is unloaded at once; one that failed stays until it is reset
        if (result is { Length: > 0 } && result != "success") Systemctl("reset-failed", _memScope);
        // unknown leaves the child's own status standing — after an OOM kill that is 137, a failure,
        // so not knowing who killed it can cost the label but never turn into a pass
        if (result is null)
            Console.Error.WriteLine(
                $"tman: cannot tell whether limit-mem ended the run: `systemctl --user show {_memScope}` failed");
        return result == "oom-kill" ? OomVerdict() : null;
    }

    string OomVerdict() => $"tree memory reached limit-mem {Canon.Mem(_caps.LimitMemMb!.Value)}; the kernel ended the tree";

    /// <summary>Whether the kernel OOM-killed anything in the leaf, from its memory.events.</summary>
    static bool LeafOomKilled(string leaf)
    {
        var events = Path.Combine(leaf, "memory.events");
        try
        {
            foreach (var line in File.ReadLines(events))
                if (line.Split(' ') is ["oom_kill", var count] && count != "0") return true;
        }
        // as with the scope: not knowing leaves the child's own status, a SIGKILL's 137, standing
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"tman: cannot tell whether limit-mem ended the run: {events}: {e.Message}");
        }
        return false;
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
        if (_memLeaf is not null) RemoveMemLeaf(_memLeaf);
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

    const int WriteOk = 2;

    [DllImport("libc", SetLastError = true)]
    static extern int access(string path, int mode);
}
