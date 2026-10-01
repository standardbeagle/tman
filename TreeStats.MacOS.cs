using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Tman;

public static partial class TreeStats
{
    /// <summary>
    /// macOS: the tree walked through libproc — the kernel's live parent links, so a reused pid is
    /// never mistaken for a child — and each process's counters read from its rusage. CPU includes
    /// the time of children it has already reaped, as Linux's does, so a finished worker's cpu does
    /// not vanish from the tree it ran in.
    /// </summary>
    [SupportedOSPlatform("macos")]
    static bool TrySampleMacOS(int rootPid, out TreeSample sample)
    {
        sample = default;
        if (!TryMeasureMacOS(rootPid, out var root)) return false;
        var (cpu, io, rss) = (root.CpuMachTime, root.IoBytes, root.RssBytes);
        var procs = 1;
        var walk = new Stack<int>();
        walk.Push(rootPid);
        while (walk.Count > 0)
        {
            foreach (var child in ChildPidsMacOS(walk.Pop()))
            {
                if (!TryMeasureMacOS(child, out var m)) continue;
                cpu += m.CpuMachTime;
                io += m.IoBytes;
                rss += m.RssBytes;
                procs++;
                walk.Push(child);
            }
        }
        var cpuNs = (double)cpu * MachTimebase.Value.Numer / MachTimebase.Value.Denom;
        sample = new TreeSample((long)(cpuNs / 100 / TimeSpanTicksPerJiffy), (long)io, (long)(rss / (1024 * 1024)), procs, "");
        return true;
    }

    readonly record struct MacOSMeasure(ulong CpuMachTime, ulong IoBytes, ulong RssBytes);

    /// <summary>
    /// rusage_info_v2's fields tman reads, by offset: 16-byte uuid, then 64-bit counters. The
    /// times are mach absolute time, which is nanoseconds on Intel and not on Apple silicon — hence
    /// <see cref="MachTimebase"/>.
    /// </summary>
    const int RusageInfoV2 = 2, RusageInfoV2Size = 160;
    const int RiUserTime = 16, RiSystemTime = 24, RiResidentSize = 64,
        RiChildUserTime = 96, RiChildSystemTime = 104, RiDiskioBytesRead = 144, RiDiskioBytesWritten = 152;

    /// <summary>One process's counters, or false when it is gone or not this user's to read.</summary>
    [SupportedOSPlatform("macos")]
    static bool TryMeasureMacOS(int pid, out MacOSMeasure measure)
    {
        measure = default;
        var info = new byte[RusageInfoV2Size];
        if (proc_pid_rusage(pid, RusageInfoV2, info) != 0) return false;
        ulong At(int offset) => BinaryPrimitives.ReadUInt64LittleEndian(info.AsSpan(offset));
        measure = new MacOSMeasure(
            At(RiUserTime) + At(RiSystemTime) + At(RiChildUserTime) + At(RiChildSystemTime),
            At(RiDiskioBytesRead) + At(RiDiskioBytesWritten),
            At(RiResidentSize));
        return true;
    }

    const uint ProcPpidOnly = 6;

    /// <summary>The pids whose parent is <paramref name="ppid"/> right now.</summary>
    [SupportedOSPlatform("macos")]
    static ReadOnlySpan<int> ChildPidsMacOS(int ppid)
    {
        // asked for room first; a child forked between the two calls is caught on the next tick
        var bytes = proc_listpids(ProcPpidOnly, (uint)ppid, null, 0);
        if (bytes <= 0) return [];
        var pids = new int[bytes / sizeof(int) + 16];
        bytes = proc_listpids(ProcPpidOnly, (uint)ppid, pids, pids.Length * sizeof(int));
        return bytes <= 0 ? [] : pids.AsSpan(0, bytes / sizeof(int));
    }

    /// <summary>mach_timebase_info_data_t: mach time × Numer / Denom = nanoseconds.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Timebase
    {
        public uint Numer;
        public uint Denom;
    }

    [SupportedOSPlatform("macos")]
    static readonly Lazy<Timebase> MachTimebase = new(() =>
        mach_timebase_info(out var tb) == 0 && tb.Denom != 0
            ? tb
            : throw new InvalidOperationException("mach_timebase_info failed"));

    [DllImport("libproc")]
    static extern int proc_listpids(uint type, uint typeinfo, int[]? buffer, int bufferSize);

    [DllImport("libproc")]
    static extern int proc_pid_rusage(int pid, int flavor, byte[] buffer);

    [DllImport("/usr/lib/libSystem.dylib")]
    static extern int mach_timebase_info(out Timebase info);
}
