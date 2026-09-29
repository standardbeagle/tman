using System.ComponentModel;
using System.Diagnostics;

namespace Tman;

/// <summary>What a recorded pid names now, measured against the process tman recorded under it.</summary>
public enum ProcessIdentity
{
    /// <summary>The pid still names the process tman recorded.</summary>
    Mine,
    /// <summary>No process holds the pid any more.</summary>
    Gone,
    /// <summary>
    /// Another process holds the pid: the OS handed it out again, or it names a process this user
    /// may not open. tman records only processes it spawned as this user, so one it may not open is
    /// someone else's.
    /// </summary>
    NotMine,
}

public static class ProcUtil
{
    /// <summary>
    /// How far a re-read wall-clock start may sit from the recorded one and still name the same
    /// process. Linux recomputes a start from boot time on every read, so it moves with the clock.
    /// </summary>
    static readonly TimeSpan WallClockTolerance = TimeSpan.FromSeconds(2);

    /// <summary>Linux process state: a zombie has exited and waits only for its parent to reap it.</summary>
    const char Zombie = 'Z';

    /// <summary>
    /// The one question every sweep asks of a recorded pid: is it still the process tman recorded?
    /// Liveness and identity are one check, because a pid the OS reused is alive and still not ours.
    /// </summary>
    /// <param name="recordedStartUtc">Null when the process exited before its start could be read.</param>
    /// <param name="recordedStartTicks">
    /// Linux: the recorded start in clock ticks after boot, compared exactly — the wall-clock start is
    /// recomputed from boot time on every read, so a clock step moves it. Null off Linux, and on
    /// records written before tman recorded ticks, which fall back to the wall clock.
    /// </param>
    public static ProcessIdentity Identify(int pid, DateTime? recordedStartUtc, long? recordedStartTicks)
    {
        using var mine = OpenIfMine(pid, recordedStartUtc, recordedStartTicks, out var identity);
        return identity;
    }

    /// <summary>
    /// The identity question for a caller that recorded a pid of its own, not a tman run record: it
    /// has no wall-clock start to offer, so liveness is read straight from the process. With
    /// <paramref name="startTicks"/> (Linux only) a pid the OS reused reads as
    /// <see cref="ProcessIdentity.NotMine"/>; without them a live pid is taken to be the caller's.
    /// Read-only — it never signals or kills.
    /// </summary>
    public static ProcessIdentity Probe(int pid, long? startTicks)
    {
        if (pid <= 0) return ProcessIdentity.Gone;
        try
        {
            if (OperatingSystem.IsLinux())
                return VerdictFromStat(ReadStat(pid), startTicks) ?? ProcessIdentity.Mine;
            using var p = Process.GetProcessById(pid);
            if (OperatingSystem.IsWindows() && !Pin(p)) return ProcessIdentity.Gone;
            return p.HasExited ? ProcessIdentity.Gone : ProcessIdentity.Mine;
        }
        catch (Exception e) when (VerdictFor(e) is { } verdict) { return verdict; }
    }

    /// <summary>
    /// Opens the pid and identifies it through that same object, which is returned only when it is
    /// <see cref="ProcessIdentity.Mine"/>, for the caller to act on and dispose. On Windows the object
    /// holds a handle taken before the check, and Windows never hands a pid out again while a handle
    /// to it is open, so the process checked is the process acted on. Linux and macOS signal by pid,
    /// so there the gap between check and kill is only as safe as the kernel's reluctance to reuse.
    /// </summary>
    static Process? OpenIfMine(int pid, DateTime? recordedStartUtc, long? recordedStartTicks, out ProcessIdentity identity)
    {
        identity = ProcessIdentity.Gone;
        if (pid <= 0 || recordedStartUtc is null) return null;
        Process? p = null;
        try
        {
            var byStat = OperatingSystem.IsLinux() ? VerdictFromStat(ReadStat(pid), recordedStartTicks) : null;
            if (byStat is { } settled && settled != ProcessIdentity.Mine) { identity = settled; return null; }
            p = Process.GetProcessById(pid);
            if (OperatingSystem.IsWindows() && !Pin(p)) return null;
            identity = byStat ?? ByWallClock(p, recordedStartUtc.Value);
            if (identity != ProcessIdentity.Mine) return null;
            var mine = p;
            p = null;
            return mine;
        }
        catch (Exception e) when (VerdictFor(e) is { } verdict) { identity = verdict; return null; }
        finally { p?.Dispose(); }
    }

    /// <summary>
    /// Makes the object hold its own handle, which every later call on it then goes through. False
    /// when the process has exited: OpenProcess found it gone, or found its exit code already set.
    /// A handle this user may not have throws <see cref="Win32Exception"/>, which is not ours.
    /// </summary>
    static bool Pin(Process p)
    {
        try { _ = p.SafeHandle; return true; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>Windows and macOS, and Linux records from before tman recorded start ticks.</summary>
    static ProcessIdentity ByWallClock(Process p, DateTime recordedStartUtc)
    {
        if (p.HasExited) return ProcessIdentity.Gone;
        return MatchesWallClock(p.StartTime.ToUniversalTime(), recordedStartUtc)
            ? ProcessIdentity.Mine
            : ProcessIdentity.NotMine;
    }

    /// <summary>
    /// Kills the recorded process's tree, if the pid still names it. False when part of the tree
    /// could not be killed; each such failure is written to stderr. A pid that no longer names the
    /// recorded process is left alone, and said so when it now names someone else's.
    /// </summary>
    public static bool KillTree(int pid, DateTime? recordedStartUtc, long? recordedStartTicks)
    {
        using var mine = OpenIfMine(pid, recordedStartUtc, recordedStartTicks, out var identity);
        if (mine is not null) return KillTree(mine);
        if (identity == ProcessIdentity.NotMine)
            Console.Error.WriteLine($"tman: pid {pid} no longer names the recorded run; not killing it");
        return true;
    }

    /// <summary>
    /// Kills <paramref name="p"/> and its descendants through the object the caller already holds.
    /// A tree already gone is not a failure. A caller inside the tree is a tman defect, so the
    /// runtime's refusal of that propagates.
    /// </summary>
    public static bool KillTree(Process p)
    {
        try
        {
            p.Kill(entireProcessTree: true);
            return true;
        }
        catch (AggregateException e)
        {
            foreach (var inner in e.InnerExceptions)
                Console.Error.WriteLine($"tman: could not kill part of pid {p.Id}'s tree: {inner.Message}");
            return false;
        }
    }

    /// <summary>
    /// What a Linux stat line settles on its own: a zombie is gone, and recorded ticks either match
    /// or name another process. Null when there are no recorded ticks — a legacy record, left to the
    /// wall clock.
    /// </summary>
    internal static ProcessIdentity? VerdictFromStat(ProcStat stat, long? recordedStartTicks)
    {
        if (stat.State == Zombie) return ProcessIdentity.Gone;
        if (recordedStartTicks is not { } recorded) return null;
        var actual = stat.StartTicks ?? throw new InvalidDataException("/proc stat line has no starttime field");
        return actual == recorded ? ProcessIdentity.Mine : ProcessIdentity.NotMine;
    }

    internal static bool MatchesWallClock(DateTime actualUtc, DateTime recordedUtc) =>
        (actualUtc - recordedUtc).Duration() < WallClockTolerance;

    /// <summary>ESRCH: the process went away while its /proc entry was being read.</summary>
    const int NoSuchProcess = 3;

    /// <summary>
    /// The verdict an exception raised while inspecting a pid carries, or null when it carries none
    /// and must propagate. Only failures the runtime documents for a missing or foreign process map.
    /// </summary>
    internal static ProcessIdentity? VerdictFor(Exception e) => e switch
    {
        // GetProcessById: no process holds the pid
        ArgumentException => ProcessIdentity.Gone,
        // Windows: OpenProcess refused (access denied). Linux/macOS: the start time is unreadable —
        // another user's process, hidepid, or another pid namespace
        Win32Exception => ProcessIdentity.NotMine,
        // /proc/<pid>: the entry is gone, or went while it was read
        FileNotFoundException or DirectoryNotFoundException => ProcessIdentity.Gone,
        IOException { HResult: NoSuchProcess } when OperatingSystem.IsLinux() => ProcessIdentity.Gone,
        // /proc/<pid>: readable only by its owner
        UnauthorizedAccessException => ProcessIdentity.NotMine,
        _ => null,
    };

    /// <summary>
    /// The start tman records for a process it spawned: the wall clock everywhere, plus on Linux the
    /// boot-relative ticks <see cref="Identify"/> compares there. Throws when the process is gone.
    /// </summary>
    public static (DateTime Utc, long? Ticks) StartStamp(Process p)
    {
        long? ticks = OperatingSystem.IsLinux()
            ? ReadStat(p.Id).StartTicks ?? throw new InvalidDataException($"/proc/{p.Id}/stat has no starttime field")
            : null;
        return (p.StartTime.ToUniversalTime(), ticks);
    }

    /// <summary>This tman's own start stamp. Its own process is always readable, so this never falls back.</summary>
    public static (DateTime Utc, long? Ticks) OwnStart()
    {
        using var self = Process.GetCurrentProcess();
        return StartStamp(self);
    }

    static ProcStat ReadStat(int pid) =>
        TreeStats.TryParseStat(File.ReadAllText($"/proc/{pid}/stat"), out var stat)
            ? stat
            : throw new InvalidDataException($"/proc/{pid}/stat is not a stat line");

    public static bool TryRefresh(int pid, out Process? proc)
    {
        proc = null;
        try
        {
            var p = Process.GetProcessById(pid);
            if (p.HasExited) { p.Dispose(); return false; }
            p.Refresh();
            proc = p;
            return true;
        }
        catch (Exception e) when (VerdictFor(e) is not null) { return false; }
    }
}
