using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Tman;

public sealed partial class Confinement
{
    IntPtr _job, _port;

    /// <summary>
    /// The run's Job Object: limit-cpus as its affinity, limit-mem as its job memory limit, and a
    /// completion port the job reports a crossed limit through. A job, unlike a per-process limit,
    /// holds for every process the child starts after it is assigned.
    /// </summary>
    [SupportedOSPlatform("windows")]
    void CreateJob()
    {
        _job = CreateJobObjectW(IntPtr.Zero, null);
        if (_job == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject");

        var limits = new JobObjectExtendedLimitInformation();
        if (_cpus is not null)
        {
            limits.BasicLimitInformation.LimitFlags |= JobObjectLimitAffinity;
            limits.BasicLimitInformation.Affinity = (nuint)_cpus.Aggregate(0UL, (mask, cpu) => mask | 1UL << cpu);
        }
        if (_caps.LimitMemMb is { } mb)
        {
            limits.BasicLimitInformation.LimitFlags |= JobObjectLimitJobMemory;
            limits.JobMemoryLimit = (nuint)(mb * 1024 * 1024);
        }
        if (!SetInformationJobObject(_job, JobObjectExtendedLimitInformationClass, ref limits, Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject(limits)");

        _port = CreateIoCompletionPort(InvalidHandle, IntPtr.Zero, UIntPtr.Zero, 1);
        if (_port == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateIoCompletionPort");
        var association = new JobObjectAssociateCompletionPort { CompletionKey = _job, CompletionPort = _port };
        if (!SetInformationJobObject(_job, JobObjectAssociateCompletionPortInformation, ref association, Marshal.SizeOf<JobObjectAssociateCompletionPort>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject(completion port)");
    }

    /// <summary>
    /// Starts the child and puts it in the job. Process.Start cannot create a process suspended, so
    /// the child runs for the instant between its creation and its assignment; a process it starts
    /// in that instant is outside the job. A runtime or interpreter is still loading then, before any
    /// code of its own could start another process, which is why this holds in practice — but it is
    /// a window, not a guarantee.
    /// </summary>
    [SupportedOSPlatform("windows")]
    Process StartInJob(ProcessStartInfo psi)
    {
        var proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to start process");
        if (_job != IntPtr.Zero && !AssignProcessToJobObject(_job, proc.Handle))
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject");
            // a child tman could not confine does not run unconfined behind the error that says so
            ProcUtil.KillTree(proc);
            proc.WaitForExit();
            proc.Dispose();
            throw error;
        }
        return proc;
    }

    [SupportedOSPlatform("windows")]
    static int[] AllowedCpusWindows()
    {
        if (!GetProcessAffinityMask(Process.GetCurrentProcess().Handle, out var mask, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetProcessAffinityMask");
        var cpus = new List<int>();
        for (var i = 0; i < 64; i++)
            if (((ulong)mask & (1UL << i)) != 0) cpus.Add(i);
        return [.. cpus];
    }

    /// <summary>The job memory limit's message, if the job has posted one since the last look.</summary>
    string? JobMemoryBreach()
    {
        if (_port == IntPtr.Zero || _caps.LimitMemMb is not { } mb) return null;
        string? breach = null;
        while (GetQueuedCompletionStatus(_port, out var message, out _, out _, 0))
            if (message == JobObjectMsgJobMemoryLimit)
                breach = $"tree memory reached limit-mem {Canon.Mem(mb)}; the job refused the allocation";
        return breach;
    }

    void CloseJob()
    {
        if (_port != IntPtr.Zero) CloseHandle(_port);
        if (_job != IntPtr.Zero) CloseHandle(_job);
        _port = _job = IntPtr.Zero;
    }

    const int JobObjectAssociateCompletionPortInformation = 7;
    const int JobObjectExtendedLimitInformationClass = 9;
    const uint JobObjectLimitAffinity = 0x10;
    const uint JobObjectLimitJobMemory = 0x200;
    const uint JobObjectMsgJobMemoryLimit = 10;
    static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JobIoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public JobIoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JobObjectAssociateCompletionPort
    {
        public IntPtr CompletionKey;
        public IntPtr CompletionPort;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobObjectExtendedLimitInformation info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobObjectAssociateCompletionPort info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateIoCompletionPort(IntPtr file, IntPtr existingPort, UIntPtr key, uint threads);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetQueuedCompletionStatus(IntPtr port, out uint bytes, out UIntPtr key, out IntPtr overlapped, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessAffinityMask(IntPtr process, out nuint processMask, out nuint systemMask);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);
}
