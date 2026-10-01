using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Tman;

public static partial class TreeStats
{
    /// <summary>
    /// Windows: parent pids from one Toolhelp snapshot of every process, then cpu, working set and
    /// io for each process in the root's tree. Windows keeps a dead parent's pid in its children's
    /// entries and hands that pid out again, so a process counts as a child only if it started
    /// after the parent it names — otherwise it is some older process whose parent's pid was reused.
    /// </summary>
    [SupportedOSPlatform("windows")]
    static bool TrySampleWindows(int rootPid, out TreeSample sample)
    {
        sample = default;
        var childrenOf = new Dictionary<int, List<int>>();
        foreach (var (pid, ppid) in WindowsProcessTable())
        {
            if (pid == ppid) continue;
            if (!childrenOf.TryGetValue(ppid, out var list)) childrenOf[ppid] = list = [];
            list.Add(pid);
        }

        if (!TryMeasureWindows(rootPid, out var root)) return false;
        long cpuTicks = root.CpuTicks, io = root.IoBytes, rss = root.RssBytes;
        var procs = 1;
        var walk = new Stack<(int Pid, DateTime Started)>();
        walk.Push((rootPid, root.Started));
        while (walk.Count > 0)
        {
            var (parent, parentStarted) = walk.Pop();
            if (!childrenOf.TryGetValue(parent, out var children)) continue;
            foreach (var child in children)
            {
                if (!TryMeasureWindows(child, out var m) || m.Started < parentStarted) continue;
                cpuTicks += m.CpuTicks;
                io += m.IoBytes;
                rss += m.RssBytes;
                procs++;
                walk.Push((child, m.Started));
            }
        }
        sample = new TreeSample(cpuTicks / TimeSpanTicksPerJiffy, io, rss / (1024 * 1024), procs, "");
        return true;
    }

    readonly record struct WindowsMeasure(DateTime Started, long CpuTicks, long IoBytes, long RssBytes);

    /// <summary>
    /// One process's counters, or false when it is gone, exits under the read, or belongs to
    /// someone else — none of which is part of this run's tree.
    /// </summary>
    [SupportedOSPlatform("windows")]
    static bool TryMeasureWindows(int pid, out WindowsMeasure measure)
    {
        measure = default;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!GetProcessIoCounters(p.Handle, out var io)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            measure = new WindowsMeasure(
                p.StartTime.ToUniversalTime(),
                p.TotalProcessorTime.Ticks,
                (long)(io.ReadTransferCount + io.WriteTransferCount),
                p.WorkingSet64);
            return true;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { return false; }
    }

    /// <summary>Every process's pid and the parent pid it was created under, from one snapshot.</summary>
    [SupportedOSPlatform("windows")]
    static List<(int Pid, int Ppid)> WindowsProcessTable()
    {
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == InvalidHandle) throw new Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            var table = new List<(int, int)>();
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32FirstW(snapshot, ref entry)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            do table.Add(((int)entry.ProcessId, (int)entry.ParentProcessId));
            while (Process32NextW(snapshot, ref entry));
            var last = Marshal.GetLastPInvokeError();
            if (last != ErrorNoMoreFiles) throw new Win32Exception(last);
            return table;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    const uint SnapProcess = 0x2;
    const int ErrorNoMoreFiles = 18;
    static readonly IntPtr InvalidHandle = new(-1);

    /// <summary>
    /// PROCESSENTRY32W, up to the field tman reads; <c>Size</c> pads it to the 568 bytes the API
    /// checks on 64-bit Windows, the only Windows tman ships for. The trailing executable name is
    /// never read.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 568)]
    internal struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
}
