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

    /// <summary>
    /// How long output may keep flowing once the root has exited. What the root wrote is already in
    /// the pipe and drains at once; this only runs out when something else still holds the pipe open.
    /// </summary>
    static readonly TimeSpan OutputDrainGrace = TimeSpan.FromSeconds(2);
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
    /// <param name="clock">
    /// Null means the system clock. Every cap is enforced on its monotonic timestamps; its wall clock
    /// only stamps the record, so a test can step the wall clock and see that no deadline moves.
    /// </param>
    internal static Task<int> RunAsync(
        string command,
        string[] args,
        Caps caps,
        string? name,
        string? alias,
        string? group,
        CancellationToken ct,
        Func<int, TreeSample?>? sampler,
        RunLog? log = null,
        string? cwd = null,
        TimeProvider? clock = null)
        => Supervise(NewRecord(command, args, caps, name, alias, group, cwd), ct, sampler, log, clock);

    /// <summary>
    /// A run's identity, before anything has happened to it: who it is, what it runs, where, and
    /// under which caps. Made by whoever admits the run, so a run that waits for a slot and the run
    /// it then becomes are one record under one id.
    /// </summary>
    /// <param name="cwd">Directory the child runs in; null means where tman itself is standing.</param>
    public static RunRecord NewRecord(
        string command, string[] args, Caps caps, string? name, string? alias, string? group, string? cwd)
    {
        var runnerStart = ProcUtil.OwnStart();
        return new RunRecord
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Name = name ?? alias,
            RunnerPid = Environment.ProcessId,
            RunnerStartUtc = runnerStart.Utc,
            RunnerStartTicks = runnerStart.Ticks,
            Command = command,
            Args = args,
            Cwd = Canon.Dir(cwd ?? Directory.GetCurrentDirectory()),
            Group = group,
            ParentId = Environment.GetEnvironmentVariable(ParentIdEnvVar),
            Caps = caps,
        };
    }

    /// <summary>Starts <paramref name="record"/>'s command and supervises it to an outcome, which it records.</summary>
    /// <param name="clock">See the RunAsync test seam.</param>
    internal static async Task<int> Supervise(
        RunRecord record,
        CancellationToken ct = default,
        Func<int, TreeSample?>? sampler = null,
        RunLog? log = null,
        TimeProvider? clock = null)
    {
        // An NTP step, a WSL resync after sleep, or a user setting the date moves the wall clock by
        // any amount in either direction; a deadline measured on it fires early or never.
        clock ??= TimeProvider.System;
        DateTime Utc() => clock.GetUtcNow().UtcDateTime;
        var (command, caps) = (record.Command, record.Caps);

        var psi = new ProcessStartInfo
        {
            FileName = command,
            WorkingDirectory = record.Cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in record.Args) psi.ArgumentList.Add(a);
        psi.Environment[ParentIdEnvVar] = record.Id;

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

        record.State = RunState.Running;
        record.StartedUtc = record.HeartbeatUtc = record.LastOutputUtc = Utc();

        Process proc;
        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to start process");
        }
        catch (Exception e)
        {
            Console.CancelKeyPress -= onCancel;
            return RecordStartFailure(record, $"cannot start '{command}': {e.Message}", log);
        }
        var startedAt = clock.GetTimestamp();
        // read now, while both ends are certainly open: after the root exits these are how the
        // processes still holding its output are told apart from every other process
        var outputPipes = OperatingSystem.IsLinux()
            ? ProcUtil.PipeIds(proc.StandardOutput.BaseStream, proc.StandardError.BaseStream)
            : [];

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
        record.Pid = proc.Id;
        record.ChildStartUtc = childStart?.Utc;
        record.ChildStartTicks = childStart?.Ticks;

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
        var prevTick = clock.GetTimestamp();
        var cpuBreaches = 0;
        try { prevCpu = proc.TotalProcessorTime; }
        catch (Exception e) when (ExitedMeanwhile(e, proc)) { }

        var lastOutput = record.LastOutputUtc;
        var lastProgress = startedAt;
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
                // a root that exited during the delay finished inside every cap it is about to be
                // measured against; judging it at this tick reported a run that ended in time as timed out
                if (proc.HasExited) break;

                var now = clock.GetTimestamp();
                var nowUtc = Utc();
                record.HeartbeatUtc = nowUtc;

                var outNow = Interlocked.Read(ref outputBytes);
                var progressed = outNow != prevOutputBytes;
                prevOutputBytes = outNow;
                if (progressed) lastOutput = nowUtc;
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
                        var sinceSample = clock.GetElapsedTime(prevSampleTick, now).TotalSeconds;
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
                    var elapsed = clock.GetElapsedTime(prevTick, now).TotalSeconds;
                    if (!sampleOk && elapsed > 0)
                        cpuPct = (curCpu - prevCpu).TotalSeconds / (elapsed * Environment.ProcessorCount) * 100.0;
                    prevCpu = curCpu;
                }
                catch (Exception e) when (ExitedMeanwhile(e, proc)) { }
                prevTick = now;

                if (Store.ReadKillRequest(record.Id) is { } requested)
                { killReason = requested; killState = RunState.Killed; }
                else if (caps.MaxTime is { } mt && clock.GetElapsedTime(startedAt, now) > mt)
                { killReason = $"exceeded max-time {mt}"; killState = RunState.TimedOut; }
                else if (caps.Stall is { } st && clock.GetElapsedTime(lastProgress, now) > st &&
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
            // and here, after the loop, because the usual way a requested kill shows up is the child
            // dying of it between two ticks: the request was left before the kill, so it is on disk
            // by the time the exit is
            if (killReason is null && Store.ReadKillRequest(record.Id) is { } requested)
            {
                killReason = requested;
                killState = RunState.Killed;
            }

            if (killReason is not null)
            {
                Console.Error.WriteLine($"tman: killing pid {record.Pid}: {killReason}");
                // through the object that started it: its pid cannot have been handed on while it is held
                ProcUtil.KillTree(proc);
            }

            await proc.WaitForExitAsync();
            await DrainOutput(Task.WhenAll(outPump, errPump), outputPipes);

            record.HeartbeatUtc = Utc();
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

        return ExitCodeFor(record);
    }

    /// <summary>
    /// A program that cannot be started is a run with an outcome like any other: recorded, so
    /// `tman status` can answer for it, and completed in its log, so the digest that was cleared when
    /// the log opened is replaced by one saying why — not left absent, which reads as a pass.
    /// </summary>
    static int RecordStartFailure(RunRecord record, string reason, RunLog? log)
    {
        Console.Error.WriteLine($"tman: {reason}");
        record.State = RunState.StartFailed;
        record.KillReason = reason;
        log?.Write($"tman: {reason}{Environment.NewLine}");
        try { Store.Save(record); }
        finally { log?.Complete(record); }
        return ExitCodeFor(record);
    }

    static int ExitCodeFor(RunRecord record) =>
        record.State switch
        {
            RunState.Exited => record.ExitCode!.Value,
            RunState.StartFailed => ExitNotFound,
            RunState.TimedOut => ExitTimeout,
            RunState.Stalled => ExitStalled,
            RunState.Culled => ExitCulled,
            _ => ExitKilled,
        };

    /// <summary>
    /// Waits for the run's output to end, after its root has exited. Output ends when every holder of
    /// the pipes has closed them, so a descendant the root left running — `server &amp;`, a daemon that
    /// kept stdout — holds the run open for as long as it lives, and with it the run's slot and
    /// every deadline: the monitor has already stopped. The run is over when its root is, so after
    /// <see cref="OutputDrainGrace"/> the holders are the run's leftovers. On Linux they are found
    /// by the pipes themselves and killed; elsewhere tman cannot name them, says so, and stops
    /// waiting — exiting closes its end, and the next write they make fails.
    /// </summary>
    static async Task DrainOutput(Task pumps, string[] outputPipes)
    {
        if (await Ends(pumps)) return;
        if (outputPipes.Length == 0)
        {
            Console.Error.WriteLine(
                "tman: a process the run left behind still holds its output; not waiting for it");
            return;
        }
        foreach (var pid in ProcUtil.PipeHolders(outputPipes))
        {
            try
            {
                using var holder = Process.GetProcessById(pid);
                Console.Error.WriteLine(
                    $"tman: killing pid {pid} ({holder.ProcessName}): still holds the run's output after it exited");
                ProcUtil.KillTree(holder);
            }
            catch (Exception e) when (ProcUtil.VerdictFor(e) is not null || e is InvalidOperationException) { }
        }
        if (!await Ends(pumps))
            Console.Error.WriteLine("tman: the run's output is still held open after its leftovers were killed; not waiting for it");

        static async Task<bool> Ends(Task pumps)
        {
            try { await pumps.WaitAsync(OutputDrainGrace); return true; }
            catch (TimeoutException) { return false; }
        }
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
        // the run stopped waiting for this pump and disposed the pipe under it
        catch (ObjectDisposedException) { }
    }
}
