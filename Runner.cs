using System.Diagnostics;

namespace Tman;

public static class Runner
{
    public const int ExitTimeout = 124;
    public const int ExitStalled = 125;
    public const int ExitCulled = 126;
    public const int ExitNotFound = 127;
    public const int ExitKilled = 130;

    public static async Task<int> RunAsync(
        string command,
        string[] args,
        Caps caps,
        string? name,
        string? alias,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

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
            Id = Guid.NewGuid().ToString("N")[..12],
            Name = name ?? alias,
            Pid = proc.Id,
            RunnerPid = Environment.ProcessId,
            Command = command,
            Args = args,
            StartedUtc = DateTime.UtcNow,
            ChildStartUtc = ProcUtil.StartTimeUtc(proc.Id) ?? DateTime.UtcNow,
            HeartbeatUtc = DateTime.UtcNow,
            LastOutputUtc = DateTime.UtcNow,
            MaxTimeSec = (long?)(caps.MaxTime?.TotalSeconds),
            StallSec = (long?)(caps.Stall?.TotalSeconds),
            MaxMemMb = caps.MaxMemMb,
            MaxCpuPct = caps.MaxCpuPct,
        };
        Store.Save(record);

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            ProcUtil.KillTree(record.Pid);
        };

        var lastOutput = record.LastOutputUtc;
        var outPump = PumpAsync(proc.StandardOutput, Console.Out, () => lastOutput = DateTime.UtcNow, ct);
        var errPump = PumpAsync(proc.StandardError, Console.Error, () => lastOutput = DateTime.UtcNow, ct);

        string? killReason = null;
        RunState killState = RunState.Killed;
        var prevCpu = TimeSpan.Zero;
        var prevTick = DateTime.UtcNow;
        var cpuBreaches = 0;
        try { prevCpu = proc.TotalProcessorTime; } catch { }

        try
        {
            while (!proc.HasExited)
            {
                try { await Task.Delay(1000, ct); }
                catch (OperationCanceledException) { killReason = "cancelled"; killState = RunState.Killed; break; }

                var now = DateTime.UtcNow;
                record.HeartbeatUtc = now;

                long memMb = 0;
                if (ProcUtil.TryRefresh(proc.Id, out var live) && live is not null)
                {
                    memMb = live.WorkingSet64 / (1024 * 1024);
                    if (memMb > record.PeakMemMb) record.PeakMemMb = memMb;
                }

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
                else if (caps.Stall is { } st && now - lastOutput > st)
                { killReason = $"no output for {st}"; killState = RunState.Stalled; }
                else if (caps.MaxMemMb is { } mm && memMb > mm)
                { killReason = $"memory {memMb}MB > max-mem {mm}MB"; killState = RunState.Culled; }
                else if (caps.MaxCpuPct is { } mc)
                {
                    cpuBreaches = cpuPct > mc ? cpuBreaches + 1 : 0;
                    if (cpuBreaches >= 3)
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

    static async Task PumpAsync(StreamReader reader, TextWriter sink, Action onData, CancellationToken ct)
    {
        try
        {
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                onData();
                sink.WriteLine(line);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }
}
