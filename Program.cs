using System.Text.Json;

namespace Tman;

public static class Program
{
    static string Version
    {
        get
        {
            var v = System.Reflection.CustomAttributeExtensions
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
                    System.Reflection.Assembly.GetExecutingAssembly())
                ?.InformationalVersion ?? "dev";
            var plus = v.IndexOf('+');
            return plus > 0 ? v[..plus] : v;
        }
    }

    public static async Task<int> Main(string[] argv)
    {
        Store.EnsureDirs();
        if (argv.Length == 0) { PrintUsage(); return 0; }

        var cmd = argv[0];
        var rest = argv[1..];

        try
        {
            switch (cmd)
            {
                case "run": return await CmdRun(rest, null);
                case "list" or "ls": return CmdList(rest);
                case "kill": return CmdKill(rest);
                case "clean": return CmdClean();
                case "status": return CmdStatus(rest);
                case "init": return CmdInit(rest);
                case "--help" or "-h" or "help": PrintUsage(); return 0;
                case "--version" or "-v": Console.WriteLine($"tman {Version}"); return 0;
                default:
                    {
                        var config = Config.Load();
                        if (config is not null && config.Aliases.TryGetValue(cmd, out var alias))
                            return await RunAlias(alias, config, rest);
                        Console.Error.WriteLine($"tman: unknown command or alias '{cmd}'");
                        PrintUsage();
                        return Runner.ExitNotFound;
                    }
            }
        }
        catch (FormatException e)
        {
            Console.Error.WriteLine($"tman: {e.Message}");
            return Runner.ExitNotFound;
        }
    }

    static async Task<int> RunAlias(AliasDef alias, TmanConfig config, string[] extraArgs)
    {
        Reaper.ReapOrphans();
        var args = alias.Args.Concat(extraArgs).ToArray();
        var caps = Config.EffectiveCaps(alias, new Caps(), config);
        return await GatedRun(alias.Command, args, caps, alias.Name, alias.Name, replace: false);
    }

    static async Task<int> CmdRun(string[] argv, string? _)
    {
        Reaper.ReapOrphans();

        string? name = null, aliasName = null;
        var replace = false;
        var cliCaps = new CapsBuilder();
        var cmdArgs = new List<string>();
        var i = 0;
        var sawDashDash = false;

        for (; i < argv.Length; i++)
        {
            var a = argv[i];
            if (!sawDashDash && a == "--") { sawDashDash = true; continue; }
            if (!sawDashDash && a.StartsWith("--"))
            {
                string Next() => i + 1 < argv.Length ? argv[++i] : throw new FormatException($"flag {a} requires a value");
                switch (a)
                {
                    case "--name": name = Next(); break;
                    case "--alias": aliasName = Next(); break;
                    case "--replace": replace = true; break;
                    case "--max-time": cliCaps.MaxTime = Caps.ParseDuration(Next()) ?? throw new FormatException("bad --max-time"); break;
                    case "--stall": cliCaps.Stall = Caps.ParseDuration(Next()) ?? throw new FormatException("bad --stall"); break;
                    case "--max-mem": cliCaps.MaxMemMb = Caps.ParseMemMb(Next()) ?? throw new FormatException("bad --max-mem"); break;
                    case "--max-cpu": cliCaps.MaxCpuPct = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--max-parallel": cliCaps.MaxParallel = int.Parse(Next()); break;
                    case "--queue-timeout": cliCaps.QueueTimeout = Caps.ParseDuration(Next()) ?? throw new FormatException("bad --queue-timeout"); break;
                    default: throw new FormatException($"unknown flag {a}");
                }
                continue;
            }
            cmdArgs.Add(a);
        }

        if (aliasName is not null)
        {
            var config = Config.Load()
                ?? throw new FormatException("no .tman.kdl found (run 'tman init')");
            if (!config.Aliases.TryGetValue(aliasName, out var alias))
                throw new FormatException($"alias '{aliasName}' not defined in {config.FilePath}");
            var args = alias.Args.Concat(cmdArgs).ToArray();
            var caps = Config.EffectiveCaps(alias, cliCaps.Build(), config);
            return await GatedRun(alias.Command, args, caps, name ?? alias.Name, alias.Name, replace);
        }

        if (cmdArgs.Count == 0) throw new FormatException("run requires a command");
        {
            var config = Config.Load();
            var caps = Config.EffectiveCaps(null, cliCaps.Build(), config);
            return await GatedRun(cmdArgs[0], cmdArgs[1..].ToArray(), caps, name, null, replace);
        }
    }

    sealed class CapsBuilder
    {
        public TimeSpan? MaxTime, Stall, QueueTimeout;
        public long? MaxMemMb;
        public double? MaxCpuPct;
        public int? MaxParallel;
        public Caps Build() => new()
        {
            MaxTime = MaxTime, Stall = Stall, QueueTimeout = QueueTimeout,
            MaxMemMb = MaxMemMb, MaxCpuPct = MaxCpuPct, MaxParallel = MaxParallel,
        };
    }

    static async Task<int> GatedRun(string command, string[] args, Caps caps, string? name, string? alias, bool replace)
    {
        FileStream? lockFile = null;
        string? lockPath = null;
        if (name is not null)
        {
            Store.EnsureDirs();
            lockPath = Store.LockPathFor(name);
            while (lockFile is null)
            {
                try
                {
                    lockFile = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                }
                catch (IOException)
                {
                    var holder = Reaper.FindLiveByNameOrId(name);
                    if (holder is null)
                    {
                        File.Delete(lockPath);
                        continue;
                    }
                    if (!replace)
                    {
                        Console.Error.WriteLine($"tman: run '{name}' already active (pid {holder.Pid}, id {holder.Id}); use --replace to kill it");
                        return Runner.ExitKilled;
                    }
                    Console.Error.WriteLine($"tman: replacing run '{name}' (pid {holder.Pid})");
                    ProcUtil.KillTree(holder.Pid);
                    holder.State = RunState.Killed;
                    holder.KillReason = "replaced by newer run";
                    Store.Save(holder);
                    File.Delete(lockPath);
                }
            }

            var existing = Reaper.FindLiveByNameOrId(name);
            if (existing is not null)
            {
                if (!replace)
                {
                    Console.Error.WriteLine($"tman: run '{name}' already active (pid {existing.Pid}, id {existing.Id}); use --replace to kill it");
                    lockFile.Dispose();
                    File.Delete(lockPath);
                    return Runner.ExitKilled;
                }
                Console.Error.WriteLine($"tman: replacing run '{name}' (pid {existing.Pid})");
                ProcUtil.KillTree(existing.Pid);
                existing.State = RunState.Killed;
                existing.KillReason = "replaced by newer run";
                Store.Save(existing);
            }
        }

        try
        {
            if (caps.MaxParallel is { } maxPar && maxPar > 0)
            {
                var deadline = DateTime.UtcNow + (caps.QueueTimeout ?? TimeSpan.FromMinutes(5));
                while (true)
                {
                    Reaper.ReapOrphans(quiet: true);
                    var live = Reaper.LiveRuns();
                    if (live.Count < maxPar) break;
                    if (DateTime.UtcNow >= deadline)
                    {
                        Console.Error.WriteLine($"tman: queue timeout waiting for slot ({live.Count}/{maxPar} running)");
                        return Runner.ExitKilled;
                    }
                    Console.Error.WriteLine($"tman: {live.Count}/{maxPar} slots busy, waiting...");
                    await Task.Delay(2000);
                }
            }

            return await Runner.RunAsync(command, args, caps, name, alias);
        }
        finally
        {
            lockFile?.Dispose();
            if (lockPath is not null)
            {
                try { File.Delete(lockPath); } catch (IOException) { }
            }
        }
    }

    static int CmdList(string[] argv)
    {
        Reaper.ReapOrphans(quiet: true);
        var all = argv.Contains("--all");
        var runs = Store.LoadAll()
            .Where(r => all || r.State == RunState.Running)
            .OrderByDescending(r => r.StartedUtc)
            .ToList();
        if (runs.Count == 0) { Console.WriteLine("no runs"); return 0; }

        Console.WriteLine($"{"ID",-14}{"NAME",-16}{"PID",-8}{"STATE",-10}{"AGE",-10}{"PEAKMEM",-9}COMMAND");
        foreach (var r in runs)
        {
            var age = (DateTime.UtcNow - r.StartedUtc).ToString(@"mm\:ss");
            var cmdline = r.Args.Length > 0 ? $"{r.Command} {string.Join(' ', r.Args)}" : r.Command;
            Console.WriteLine($"{r.Id,-14}{r.Name ?? "-",-16}{r.Pid,-8}{r.State,-10}{age,-10}{$"{r.PeakMemMb}MB",-9}{cmdline}");
        }
        return 0;
    }

    static int CmdKill(string[] argv)
    {
        Reaper.ReapOrphans(quiet: true);
        var staleOnly = argv.Contains("--stale-only");
        var targets = argv.Where(a => !a.StartsWith("--")).ToList();
        if (targets.Count == 0) throw new FormatException("kill requires <id|name|all>");

        var killed = 0;
        foreach (var target in targets)
        {
            IEnumerable<RunRecord> matches = target == "all"
                ? Reaper.LiveRuns()
                : Reaper.LiveRuns().Where(r =>
                    string.Equals(r.Name, target, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(r.Id, target, StringComparison.OrdinalIgnoreCase));

            foreach (var r in matches)
            {
                if (staleOnly && r.RunnerPid > 0 && ProcUtil.IsAlive(r.RunnerPid))
                    continue;
                Console.WriteLine($"tman: killing {r.Name ?? r.Id} (pid {r.Pid})");
                ProcUtil.KillTree(r.Pid);
                r.State = RunState.Killed;
                r.KillReason = "killed via tman kill";
                r.HeartbeatUtc = DateTime.UtcNow;
                Store.Save(r);
                killed++;
            }
        }
        if (killed == 0) Console.WriteLine("no matching live runs");
        return 0;
    }

    static int CmdClean()
    {
        var reaped = Reaper.ReapOrphans();
        Store.PruneCompleted(TimeSpan.FromHours(24));
        Console.WriteLine($"tman: reaped {reaped.Count} orphan(s), pruned records older than 24h");
        return 0;
    }

    static int CmdStatus(string[] argv)
    {
        Reaper.ReapOrphans(quiet: true);
        var target = argv.FirstOrDefault(a => !a.StartsWith("--"));
        var live = Reaper.LiveRuns();

        if (target is null)
        {
            var counts = Store.LoadAll().GroupBy(r => r.State).ToDictionary(g => g.Key, g => g.Count());
            Console.WriteLine($"live: {live.Count}");
            foreach (var (state, count) in counts.OrderBy(kv => kv.Key.ToString()))
                Console.WriteLine($"{state.ToString().ToLowerInvariant()}: {count}");
            return 0;
        }

        var r = Store.Load(target) ?? Reaper.FindLiveByNameOrId(target);
        if (r is null) { Console.Error.WriteLine($"tman: no run '{target}'"); return Runner.ExitNotFound; }
        Console.WriteLine(JsonSerializer.Serialize(r, RunRecordJsonContext.Default.RunRecord));
        return 0;
    }

    internal static int CmdInit(string[] argv)
    {
        var dir = Directory.GetCurrentDirectory();
        var path = Path.Combine(dir, Config.FileName);
        var withShims = argv.Contains("--shims");
        var withGitignore = argv.Contains("--gitignore");

        var detected = DetectAliases(dir);
        if (File.Exists(path))
        {
            Console.WriteLine($"tman: {Config.FileName} already exists");
        }
        else
        {
            File.WriteAllText(path, RenderConfig(detected));
            Console.WriteLine($"tman: wrote {path}");
        }

        var names = detected.Count > 0
            ? detected.Select(a => a.Name).ToList()
            : new List<string> { "test" };
        if (withShims)
        {
            var (written, skipped) = Shim.Generate(dir, names);
            foreach (var p in written)
                Console.WriteLine($"tman: wrote shim {p}");
            foreach (var p in skipped)
                Console.WriteLine($"tman: skipped shim {p} (path already exists; use 'tman run' instead)");
        }
        if (withGitignore && Shim.AppendGitignore(dir, names))
            Console.WriteLine("tman: updated .gitignore");
        return 0;
    }

    internal sealed record DetectedAlias(string Name, string Command, string[] Args);

    internal static List<DetectedAlias> DetectAliases(string dir)
    {
        var found = new List<DetectedAlias>();

        var pkgPath = Path.Combine(dir, "package.json");
        if (File.Exists(pkgPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(pkgPath));
                if (doc.RootElement.TryGetProperty("scripts", out var scripts))
                {
                    foreach (var s in scripts.EnumerateObject())
                    {
                        if (s.Name is "test" or "e2e" or "lint" or "integration")
                            found.Add(new DetectedAlias(s.Name, "npm", new[] { "run", s.Name }));
                    }
                }
            }
            catch (JsonException) { }
        }

        if (File.Exists(Path.Combine(dir, "pyproject.toml")) || File.Exists(Path.Combine(dir, "pytest.ini")))
            found.Add(new DetectedAlias("test", "pytest", Array.Empty<string>()));

        if (File.Exists(Path.Combine(dir, "go.mod")))
            found.Add(new DetectedAlias("test", "go", new[] { "test", "./..." }));

        var makefile = Path.Combine(dir, "Makefile");
        if (File.Exists(makefile) && File.ReadAllText(makefile).Contains("test:"))
            found.Add(new DetectedAlias("test", "make", new[] { "test" }));

        return found
            .GroupBy(a => a.Name)
            .Select(g => g.First())
            .ToList();
    }

    internal static string RenderConfig(List<DetectedAlias> detected)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("defaults {");
        sb.AppendLine("    max-time \"10m\"");
        sb.AppendLine("    stall \"60s\"");
        sb.AppendLine("    max-mem 2048");
        sb.AppendLine("    max-cpu 95");
        sb.AppendLine("    max-parallel 2");
        sb.AppendLine("}");
        sb.AppendLine();

        if (detected.Count == 0)
        {
            sb.AppendLine("alias \"test\" {");
            sb.AppendLine("    command \"echo\"");
            sb.AppendLine("    args \"replace me\"");
            sb.AppendLine("}");
        }
        foreach (var a in detected)
        {
            sb.AppendLine($"alias \"{a.Name}\" {{");
            sb.AppendLine($"    command \"{a.Command}\"");
            if (a.Args.Length > 0)
                sb.AppendLine($"    args {string.Join(' ', a.Args.Select(x => $"\"{x}\""))}");
            sb.AppendLine("}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    static void PrintUsage() => Console.WriteLine("""
        tman - AOT process/test runner manager

        usage:
          tman run [flags] -- <cmd> [args...]     run a process under tman supervision
          tman run --alias <name> [args...]       run a .tman.kdl alias
          tman <alias> [args...]                  shorthand for an alias
          tman list|ls [--all]                    list live (or all) runs
          tman kill <id|name|all> [--stale-only]  kill run(s)
          tman clean                              reap orphans, prune old records
          tman status [id|name]                   summary or run detail
          tman init [--shims] [--gitignore]       scaffold .tman.kdl (+ shim scripts)

        run flags:
          --name N            dedup lock name (fail if already running)
          --replace           kill existing run with same name first
          --max-time T        wall-clock limit (30s, 10m, 2h)
          --stall T           kill if no output for T
          --max-mem M         kill above memory (2048, 2g)
          --max-cpu P         kill above P% sustained CPU
          --max-parallel N    queue when N runs already active
          --queue-timeout T   give up queueing after T

        orphans (dead runner, live child) are reaped automatically on every command.
        """);
}
