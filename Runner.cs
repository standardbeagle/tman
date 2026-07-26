using System.Diagnostics;

namespace Tman;

public static class Runner
{
    public const int ExitTimeout = 124;
    public const int ExitStalled = 125;
    public const int ExitCulled = 126;
    public const int ExitNotFound = 127;
    public const int ExitKilled = 130;

    /// <summary>Set on supervised children so a nested tman knows which run launched it.</summary>
    public const string ParentIdEnvVar = "TMAN_RUN_ID";

    const int MonitorTickMs = 1000;
    const int CpuBreachLimit = 3;
    const int SampleFailLimit = 5;

    public static Task<int> RunAsync(
        string command,
        string[] args,
        Caps caps,
        string? name,
        string? alias,
        string? group = null,
        CancellationToken ct = default)
        => RunAsync(command, args, caps, name, alias, group, ct, sampler: null);

    /// <summary>
    /// Test seam. <paramref name="sampler"/> stands in for the real <see cref="TreeStats.TrySample"/>
    /// walk so a test can put a real child through a real stall window while deciding exactly what
    /// the monitor sees — frozen counters plus a chosen process state. Reproducing a genuine
    /// uninterruptible io wait on demand is not possible; deciding on one is what needs pinning.
    /// </summary>
    internal static async Task<int> RunAsync(
        string command,
        string[] args,
        Caps caps,
        string? name,
        string? alias,
        string? group,
        CancellationToken ct,
        Func<int, TreeSample?>? sampler)
    {
        var id = Guid.NewGuid().ToString("N")[..12];

        var psi = new ProcessStartInfo
        {
            FileName = command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment[ParentIdEnvVar] = id;

        Process proc;
        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to start process");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"tman: cannot start '{command}': {e.Message}");
            return ExitNotFound;
        }

        var record = new RunRecord
        {
            Id = id,
            Name = name ?? alias,
            Pid = proc.Id,
            RunnerPid = Environment.ProcessId,
            RunnerStartUtc = ProcUtil.StartTimeUtc(Environment.ProcessId) ?? DateTime.UtcNow,
            Command = command,
            Args = args,
            Cwd = Canon.Dir(Directory.GetCurrentDirectory()),
            Group = group,
            ParentId = Environment.GetEnvironmentVariable(ParentIdEnvVar),
            StartedUtc = DateTime.UtcNow,
            ChildStartUtc = ProcUtil.StartTimeUtc(proc.Id) ?? DateTime.UtcNow,
            HeartbeatUtc = DateTime.UtcNow,
            LastOutputUtc = DateTime.UtcNow,
            Caps = caps,
        };
        Store.Save(record);

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            ProcUtil.KillTree(record.Pid);
        };

        long outputBytes = 0;
        var outPump = PumpAsync(proc.StandardOutput, Console.Out, n => Interlocked.Add(ref outputBytes, n), ct);
        var errPump = PumpAsync(proc.StandardError, Console.Error, n => Interlocked.Add(ref outputBytes, n), ct);

        string? killReason = null;
        RunState killState = RunState.Killed;
        var prevCpu = TimeSpan.Zero;
        var prevTick = DateTime.UtcNow;
        var cpuBreaches = 0;
        try { prevCpu = proc.TotalProcessorTime; } catch { }

        var lastOutput = record.LastOutputUtc;
        var lastProgress = record.StartedUtc;
        var prevOutputBytes = 0L;
        var haveSample = TrySample(sampler, record.Pid, out var prevSample);
        var sampleFailures = 0;
        var treeDiag = "unknown";

        try
        {
            while (!proc.HasExited)
            {
                try { await Task.Delay(MonitorTickMs, ct); }
                catch (OperationCanceledException) { killReason = "cancelled"; killState = RunState.Killed; break; }

                var now = DateTime.UtcNow;
                record.HeartbeatUtc = now;

                var outNow = Interlocked.Read(ref outputBytes);
                var progressed = outNow != prevOutputBytes;
                prevOutputBytes = outNow;
                if (progressed) lastOutput = now;
                record.LastOutputUtc = lastOutput;

                long memMb = 0;
                var sampleOk = TrySample(sampler, record.Pid, out var sample);
                if (sampleOk)
                {
                    memMb = sample.RssMb;
                    if (haveSample && TreeStats.ShowsProgress(prevSample, sample))
                        progressed = true;
                    treeDiag = $"{sample.Procs} proc [{sample.States}]";
                    prevSample = sample;
                    haveSample = true;
                    sampleFailures = 0;
                }
                else
                {
                    sampleFailures++;
                }
                if (progressed) lastProgress = now;

                if (!sampleOk && ProcUtil.TryRefresh(proc.Id, out var live) && live is not null)
                    memMb = live.WorkingSet64 / (1024 * 1024);
                if (memMb > record.PeakMemMb) record.PeakMemMb = memMb;

                double cpuPct = 0;
                try
                {
                    var curCpu = proc.TotalProcessorTime;
                    var elapsed = (now - prevTick).TotalSeconds;
                    if (elapsed > 0)
                        cpuPct = (curCpu - prevCpu).TotalSeconds / (elapsed * Environment.ProcessorCount) * 100.0;
                    prevCpu = curCpu;
                }
                catch { }
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
                Store.Save(record);
            }
        }
        finally
        {
            if (killReason is not null)
            {
                Console.Error.WriteLine($"tman: killing pid {record.Pid}: {killReason}");
                ProcUtil.KillTree(record.Pid);
            }

            try { await Task.WhenAll(outPump, errPump); } catch { }
            try { if (!proc.HasExited) await proc.WaitForExitAsync(); } catch { }

            record.HeartbeatUtc = DateTime.UtcNow;
            if (killReason is not null)
            {
                record.State = killState;
                record.KillReason = killReason;
            }
            else if (proc.HasExited)
            {
                record.State = RunState.Exited;
                try { record.ExitCode = proc.ExitCode; } catch { }
            }
            else
            {
                record.State = RunState.Killed;
                record.KillReason = "runner terminated";
            }
            Store.Save(record);
            proc.Dispose();
        }

        if (killReason is not null)
            return killState switch
            {
                RunState.TimedOut => ExitTimeout,
                RunState.Stalled => ExitStalled,
                RunState.Culled => ExitCulled,
                _ => ExitKilled,
            };
        return record.ExitCode ?? 0;
    }

    static bool TrySample(Func<int, TreeSample?>? sampler, int pid, out TreeSample sample)
    {
        if (sampler is null) return TreeStats.TrySample(pid, out sample);
        var injected = sampler(pid);
        sample = injected ?? default;
        return injected is not null;
    }

    static async Task PumpAsync(StreamReader reader, TextWriter sink, Action<int> onData, CancellationToken ct)
    {
        var buf = new char[4096];
        try
        {
            int n;
            while ((n = await reader.ReadAsync(buf.AsMemory(), ct)) > 0)
            {
                onData(n);
                sink.Write(buf.AsSpan(0, n));
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }
}
