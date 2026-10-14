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

    /// <summary>What the host is using now; null where tman cannot measure it.</summary>
    public static Usage? MeasureUsage() => null;

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
            if (OperatingSystem.IsLinux() && LinuxMemMb() is { } linux) return linux;
            return Math.Max(1, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024));
        }
    }

    static long? LinuxMemMb()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/meminfo"))
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                {
                    var kb = line.AsSpan("MemTotal:".Length).Trim();
                    var space = kb.IndexOf(' ');
                    if (space > 0) kb = kb[..space];
                    return long.TryParse(kb, out var value) ? value / 1024 : null;
                }
        }
        catch (IOException) { }
        return null;
    }
}
