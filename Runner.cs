using System.ComponentModel;
using System.Diagnostics;

namespace Tman;

public static class Runner
{
    public const int ExitTimeout = 124;
    public const int ExitStalled = 125;
    public const int ExitCulled = 126;
    public const int ExitNotFound = 127;
    public const int ExitKilled = 130;
    /// <summary>sysexits EX_IOERR: tman's run store cannot be written, so no run can be recorded.</summary>
    public const int ExitStoreUnwritable = 74;

    // How long a failed start-stamp read waits to confirm the child really exited. A zombie reaps
    // at once; the bound only matters for a child that is somehow still running, which re-throws.
    const int ExitConfirmMs = 2000;

    /// <summary>Set on supervised children so a nested tman knows which run launched it.</summary>
    public const string ParentIdEnvVar = "TMAN_RUN_ID";

    /// <summary>
    /// KillReason for a child that is gone without anyone reading its exit status. The one outcome
    /// tman may never paper over with a 0: a run it cannot vouch for is reported killed, whether the
    /// Runner lost the status or the Reaper found the child dead after the fact.
    /// </summary>
    public const string ExitStatusUnknownReason = "child exit status unknown";

    const int MonitorTickMs = 1000;
    const int CpuBreachLimit = 3;
    const int SampleFailLimit = 5;

    /// <summary>
    /// Clock ticks per second behind <see cref="TreeSample.CpuJiffies"/>. Linux /proc reports
    /// utime+stime in USER_HZ, fixed at 100 on every supported arch; the root-only sampler off
    /// Linux manufactures its jiffies from TotalProcessorTime at the same rate, so one constant
    /// serves both.
    /// </summary>
    const double JiffiesPerSecond = 100;

    public static Task<int> RunAsync(
        string command,
        string[] args,
        Caps caps,
        string? name,
        string? alias,
        string? group = null,
        CancellationToken ct = default,
        RunLog? log = null,
        string? cwd = null)
        => RunAsync(command, args, caps, name, alias, group, ct, sampler: null, log: log, cwd: cwd);

    /// <summary>
    /// Test seam. <paramref name="sampler"/> stands in for the real <see cref="TreeStats.TrySample"/>
    /// walk so a test can put a real child through a real stall window while deciding exactly what
    /// the monitor sees — frozen counters plus a chosen process state. Reproducing a genuine
    /// uninterruptible io wait on demand is not possible; deciding on one is what needs pinning.
    /// </summary>
    /// <param name="cwd">Directory the child runs in; null means where tman itself is standing.</param>
    internal static async Task<int> RunAsync(
        string command,
        string[] args,
        Caps caps,
        string? name,
        string? alias,
        string? group,
        CancellationToken ct,
        Func<int, TreeSample?>? sampler,
        RunLog? log = null,
        string? cwd = null)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        cwd = Canon.Dir(cwd ?? Directory.GetCurrentDirectory());

        var psi = new ProcessStartInfo
        {
            FileName = command,
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment[ParentIdEnvVar] = id;

        // Ctrl+C reaches the child through the terminal at the same moment it reaches tman. Killing
        // the tree from here and letting the loop read whatever exit code the child chose would let
        // a runner that traps SIGINT and shuts down cleanly report 0 for work that never finished.
        // An interrupt is a cancellation: the loop ends on it and the run is reported killed.
        // Subscribed before the child exists: once the record is saved anyone watching the store can
        // signal, and a SIGINT that lands before the handler is installed takes the .NET default —
        // tman dies on the signal with nothing on stderr and no final record.
        using var interrupt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var interrupted = false;
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            interrupted = true;
            interrupt.Cancel();
        };
        Console.CancelKeyPress += onCancel;

        Process proc;
        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to start process");
        }
        catch (Exception e)
        {
            Console.CancelKeyPress -= onCancel;
            Console.Error.WriteLine($"tman: cannot start '{command}': {e.Message}");
            log?.Dispose();
            return ExitNotFound;
        }

        (DateTime Utc, long? Ticks)? childStart;
        try { childStart = ProcUtil.StartStamp(proc); }
        // the child exited before its start could be read: the record says so rather than inventing a
        // start, and a record with no start never identifies as a live run. Once the runtime has seen
        // the exit, StartTime refuses outright; before that, /proc is already gone (Linux) or
        // proc_pidinfo refuses the unreaped zombie (macOS) while HasExited, which only asks
        // kill(pid, 0), still calls it alive. It is this user's own child, so the read cannot have
        // been refused for privilege: the exit is confirmed by reaping it, which returns at once for
        // a zombie. A child that is still running after that re-throws.
        catch (Exception e) when ((e is InvalidOperationException || ProcUtil.VerdictFor(e) is not null)
                                  && proc.WaitForExit(ExitConfirmMs)) { childStart = null; }
        var runnerStart = ProcUtil.OwnStart();

        var record = new RunRecord
        {
            Id = id,
            Name = name ?? alias,
            Pid = proc.Id,
            RunnerPid = Environment.ProcessId,
            RunnerStartUtc = runnerStart.Utc,
            RunnerStartTicks = runnerStart.Ticks,
            Command = command,
            Args = args,
            Cwd = cwd,
            Group = group,
            ParentId = Environment.GetEnvironmentVariable(ParentIdEnvVar),
            StartedUtc = DateTime.UtcNow,
            ChildStartUtc = childStart?.Utc,
            ChildStartTicks = childStart?.Ticks,
            HeartbeatUtc = DateTime.UtcNow,
            LastOutputUtc = DateTime.UtcNow,
            Caps = caps,
        };
        try { Store.Save(record); }
        // a run tman cannot record is one nothing can list, reap, or kill by name, so it is not left
        // running behind the error that reports it
        catch (KnownError)
        {
            Console.CancelKeyPress -= onCancel;
            ProcUtil.KillTree(proc);
            proc.WaitForExit();
            proc.Dispose();
            log?.Dispose();
            throw;
        }

        long outputBytes = 0;
        var outPump = PumpAsync(proc.StandardOutput, Console.Out, log, n => Interlocked.Add(ref outputBytes, n), ct);
        var errPump = PumpAsync(proc.StandardError, Console.Error, log, n => Interlocked.Add(ref outputBytes, n), ct);

        string? killReason = null;
        RunState killState = RunState.Killed;
        var prevCpu = TimeSpan.Zero;
        var prevTick = DateTime.UtcNow;
        var cpuBreaches = 0;
        try { prevCpu = proc.TotalProcessorTime; }
        catch (Exception e) when (ExitedMeanwhile(e, proc)) { }

        var lastOutput = record.LastOutputUtc;
        var lastProgress = record.StartedUtc;
        var prevOutputBytes = 0L;
        var haveSample = TrySample(sampler, record.Pid, out var prevSample);
        var prevSampleTick = prevTick;
        var sampleFailures = 0;
        var treeDiag = "unknown";
        KnownError? unrecorded = null;

        try
        {
            while (!proc.HasExited)
            {
                try { await Task.Delay(MonitorTickMs, interrupt.Token); }
                catch (OperationCanceledException) { break; }

                var now = DateTime.UtcNow;
                record.HeartbeatUtc = now;

                var outNow = Interlocked.Read(ref outputBytes);
                var progressed = outNow != prevOutputBytes;
                prevOutputBytes = outNow;
                if (progressed) lastOutput = now;
                record.LastOutputUtc = lastOutput;

                long memMb = 0;
                double cpuPct = 0;
                var sampleOk = TrySample(sampler, record.Pid, out var sample);
                if (sampleOk)
                {
                    memMb = sample.RssMb;
                    if (haveSample)
                    {
                        if (TreeStats.ShowsProgress(prevSample, sample)) progressed = true;
                        // cpu of the whole tree, over the interval the two samples actually span —
                        // a missed tick in between is spread over its true elapsed, not the last tick
                        var sinceSample = (now - prevSampleTick).TotalSeconds;
                        if (sinceSample > 0)
                            cpuPct = (sample.CpuJiffies - prevSample.CpuJiffies)
                                / (sinceSample * JiffiesPerSecond * Environment.ProcessorCount) * 100.0;
                    }
                    treeDiag = $"{sample.Procs} proc [{sample.States}]";
                    prevSample = sample;
                    prevSampleTick = now;
                    haveSample = true;
                    sampleFailures = 0;
                }
                else
                {
                    sampleFailures++;
                }
                if (progressed) lastProgress = now;

                // the sample fails mostly because the child is exiting, so this read must survive that
                if (!sampleOk)
                {
                    try
                    {
                        proc.Refresh();
                        memMb = proc.WorkingSet64 / (1024 * 1024);
                    }
                    catch (Exception e) when (ExitedMeanwhile(e, proc)) { }
                }
                if (memMb > record.PeakMemMb) record.PeakMemMb = memMb;

                // Only when there is no tree sample to read: the root's own processor time. Off Linux
                // the sample is root-only too (see TreeStats.CoversTree), so there --max-cpu never
                // sees a descendant on either path — the README's platform note carries that limit.
                try
                {
                    var curCpu = proc.TotalProcessorTime;
                    var elapsed = (now - prevTick).TotalSeconds;
                    if (!sampleOk && elapsed > 0)
                        cpuPct = (curCpu - prevCpu).TotalSeconds / (elapsed * Environment.ProcessorCount) * 100.0;
                    prevCpu = curCpu;
                }
                catch (Exception e) when (ExitedMeanwhile(e, proc)) { }
                prevTick = now;

                if (caps.MaxTime is { } mt && now - record.StartedUtc > mt)
                { killReason = $"exceeded max-time {mt}"; killState = RunState.TimedOut; }
                else if (caps.Stall is { } st && now - lastProgress > st &&
                         (sampleOk || sampleFailures >= SampleFailLimit))
                { killReason = $"no output or activity for {st} (tree: {treeDiag})"; killState = RunState.Stalled; }
                else if (caps.MaxMemMb is { } mm && memMb > mm)
                { killReason = $"tree memory {memMb}MB > max-mem {mm}MB"; killState = RunState.Culled; }
                else if (caps.MaxCpuPct is { } mc)
                {
                    cpuBreaches = cpuPct > mc ? cpuBreaches + 1 : 0;
                    if (cpuBreaches >= CpuBreachLimit)
                    { killReason = $"cpu {cpuPct:F0}% > max-cpu {mc:F0}% sustained"; killState = RunState.Culled; }
                }

                if (killReason is not null) break;
                try { Store.Save(record); }
                catch (KnownError e)
                {
                    unrecorded = e;
                    killReason = "tman can no longer record this run";
                    break;
                }
            }
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;

            // checked here and not only where the delay was cut short: the loop can also end because
            // the child exited on the same signal, and that exit is still not a finished run
            if (killReason is null && interrupt.IsCancellationRequested)
            {
                killReason = interrupted ? "interrupted" : "cancelled";
                killState = RunState.Killed;
            }

            if (killReason is not null)
            {
                Console.Error.WriteLine($"tman: killing pid {record.Pid}: {killReason}");
                // through the object that started it: its pid cannot have been handed on while it is held
                ProcUtil.KillTree(proc);
            }

            try { await Task.WhenAll(outPump, errPump); } catch { }
            await proc.WaitForExitAsync();

            record.HeartbeatUtc = DateTime.UtcNow;
            if (killReason is not null)
            {
                record.State = killState;
                record.KillReason = killReason;
            }
            else if (TryReadExitCode(proc) is { } exitCode)
            {
                record.State = RunState.Exited;
                record.ExitCode = exitCode;
            }
            else
            {
                record.State = RunState.Killed;
                record.KillReason = ExitStatusUnknownReason;
                Console.Error.WriteLine($"tman: exit status of pid {record.Pid} is unknown; reporting {ExitKilled}");
            }
            Store.Save(record);
            // after the record is final: the digest reports the outcome, so it cannot be written
            // until the outcome is decided, and a killed run needs a digest as much as a failed one
            log?.Complete(record);
            proc.Dispose();
        }
        if (unrecorded is not null) throw unrecorded;

        return record.State switch
        {
            RunState.Exited => record.ExitCode!.Value,
            RunState.TimedOut => ExitTimeout,
            RunState.Stalled => ExitStalled,
            RunState.Culled => ExitCulled,
            _ => ExitKilled,
        };
    }

    /// <summary>
    /// A read of the child's counters failed because the child exited under it: the runtime raises
    /// InvalidOperationException once it knows, Win32Exception when the OS entry went first. Any
    /// other failure, or either one from a child still running, is a defect and propagates.
    /// </summary>
    static bool ExitedMeanwhile(Exception e, Process proc) =>
        e is InvalidOperationException or Win32Exception && proc.HasExited;

    static int? TryReadExitCode(Process proc)
    {
        try { return proc.HasExited ? proc.ExitCode : null; }
        catch (InvalidOperationException) { return null; }
    }

    static bool TrySample(Func<int, TreeSample?>? sampler, int pid, out TreeSample sample)
    {
        if (sampler is null) return TreeStats.TrySample(pid, out sample);
        var injected = sampler(pid);
        sample = injected ?? default;
        return injected is not null;
    }

    static async Task PumpAsync(
        StreamReader reader, TextWriter sink, RunLog? log, Action<int> onData, CancellationToken ct)
    {
        var buf = new char[4096];
        try
        {
            int n;
            while ((n = await reader.ReadAsync(buf.AsMemory(), ct)) > 0)
            {
                onData(n);
                sink.Write(buf.AsSpan(0, n));
                log?.Write(buf.AsSpan(0, n));
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }
}
