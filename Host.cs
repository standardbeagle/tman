using System.Globalization;

namespace Tman;

/// <summary>
/// The resources a machine queue may budget. A queue declares its budget as an absolute figure or
/// a fraction of the host (for example "80%"), and the fraction has to be resolved against the
/// machine it runs on; this is the one place that knows how big the host is.
/// </summary>
public static class Host
{
    /// <summary>CPU and memory in use on the whole host, by every process, tman's or not.</summary>
    public readonly record struct Usage(double Cores, long MemMb);

    /// <summary>
    /// What the host is using now: busy cores, and memory in use (MemTotal − MemAvailable). Null
    /// where tman cannot measure it — anywhere but Linux today — which admission reads as no load
    /// outside its runs. Read only when someone asks (an admission attempt, `tman status`); no
    /// thread samples the host in the background.
    /// </summary>
    public static Usage? MeasureUsage()
    {
        if (!OperatingSystem.IsLinux()) return null;
        if (LinuxBusyCores() is not { } cores
            || LinuxMeminfoMb("MemTotal:") is not { } total
            || LinuxMeminfoMb("MemAvailable:") is not { } available) return null;
        return new Usage(cores, Math.Max(0, total - available));
    }

    /// <summary>
    /// CPUs the host offers. <see cref="Environment.ProcessorCount"/> is the affinity-narrowed count,
    /// which is the right denominator: a tman confined to four CPUs budgets four.
    /// </summary>
    public static double Cores => Environment.ProcessorCount;

    /// <summary>
    /// Physical memory in MiB, read from the OS. A budget is sized against the whole machine, so
    /// this is total memory, not available: a queue that budgeted what is free would grow and shrink
    /// with every other process. Falls back to the runtime's view when the OS cannot be read.
    /// </summary>
    public static long MemMb
    {
        get
        {
            if (OperatingSystem.IsLinux() && LinuxMeminfoMb("MemTotal:") is { } linux) return linux;
            return Math.Max(1, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024));
        }
    }

    static long? LinuxMeminfoMb(string key)
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/meminfo"))
                if (line.StartsWith(key, StringComparison.Ordinal))
                {
                    var kb = line.AsSpan(key.Length).Trim();
                    var space = kb.IndexOf(' ');
                    if (space > 0) kb = kb[..space];
                    return long.TryParse(kb, out var value) ? value / 1024 : null;
                }
        }
        catch (IOException) { }
        return null;
    }

    /// <summary>A reading shorter than this is noise; the last reading stands for it.</summary>
    static readonly TimeSpan MinWindow = TimeSpan.FromSeconds(1);

    /// <summary>A sample older than this no longer says what the host is doing now.</summary>
    static readonly TimeSpan MaxWindow = TimeSpan.FromSeconds(30);

    /// <summary>The window measured on the spot when no recent sample spans one.</summary>
    static readonly TimeSpan FreshWindow = TimeSpan.FromMilliseconds(250);

    readonly record struct CpuJiffies(long Busy, long Total, int Cpus);

    /// <summary>
    /// Busy cores over the window since the previous reading any tman on this machine took, kept in
    /// one sample file beside the run store, so the span between two admission attempts is the
    /// measuring window and nobody has to sample in between. Each reading is averaged with the one
    /// before it while that one is recent, so one burst does not swing admission. With no sample in
    /// the last 30 s, a 250 ms window is measured on the spot.
    /// </summary>
    static double? LinuxBusyCores()
    {
        if (ReadCpuJiffies() is not { } now) return null;
        var path = Path.Combine(Store.Root, "host-cpu");
        var at = DateTime.UtcNow;
        var prev = ReadCpuSample(path);
        var age = prev is { } p ? at - p.At : TimeSpan.MaxValue;
        if (age >= TimeSpan.Zero && age < MinWindow) return prev!.Value.Cores;

        CpuJiffies from;
        double? before = null;
        if (age >= TimeSpan.Zero && age <= MaxWindow) (from, before) = (prev!.Value.Jiffies, prev.Value.Cores);
        else
        {
            from = now;
            Thread.Sleep(FreshWindow);
            if (ReadCpuJiffies() is not { } again) return null;
            (now, at) = (again, DateTime.UtcNow);
        }
        var total = now.Total - from.Total;
        // a counter that went backwards is a reboot since the sample: read it fresh next time
        if (total <= 0) return before ?? 0;
        var cores = (double)(now.Busy - from.Busy) / total * now.Cpus;
        if (before is { } b) cores = (cores + b) / 2;
        try
        {
            // rename on write: a reader never sees half a sample
            var tmp = path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(tmp, string.Create(CultureInfo.InvariantCulture,
                $"{at.Ticks} {now.Busy} {now.Total} {now.Cpus} {cores}"));
            File.Move(tmp, path, overwrite: true);
        }
        // an unwritable store costs the next reading its window, not this one
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return cores;
    }

    static (DateTime At, CpuJiffies Jiffies, double Cores)? ReadCpuSample(string path)
    {
        try
        {
            var f = File.ReadAllText(path).Split(' ');
            var inv = CultureInfo.InvariantCulture;
            if (f.Length == 5 && long.TryParse(f[0], inv, out var ticks) && long.TryParse(f[1], inv, out var busy)
                && long.TryParse(f[2], inv, out var total) && int.TryParse(f[3], inv, out var cpus)
                && double.TryParse(f[4], inv, out var cores))
                return (new DateTime(ticks, DateTimeKind.Utc), new CpuJiffies(busy, total, cpus), cores);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>
    /// The host's aggregate `cpu` line of /proc/stat — user nice system idle iowait irq softirq
    /// steal; guest time is already inside user — and how many CPUs it sums. Idle and iowait are
    /// not busy.
    /// </summary>
    static CpuJiffies? ReadCpuJiffies()
    {
        try
        {
            long busy = 0, total = 0;
            var cpus = 0;
            var seen = false;
            foreach (var line in File.ReadLines("/proc/stat"))
            {
                if (line.StartsWith("cpu ", StringComparison.Ordinal))
                {
                    var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    for (var i = 1; i <= 8 && i < f.Length; i++)
                    {
                        var v = long.Parse(f[i], CultureInfo.InvariantCulture);
                        total += v;
                        if (i is not (4 or 5)) busy += v;
                    }
                    seen = true;
                }
                else if (line.StartsWith("cpu", StringComparison.Ordinal)) cpus++;
                else if (seen) break;
            }
            return seen && cpus > 0 ? new CpuJiffies(busy, total, cpus) : null;
        }
        catch (IOException) { return null; }
    }
}
