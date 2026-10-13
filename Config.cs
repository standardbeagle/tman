namespace Tman;

public sealed record AliasDef(
    string Name,
    string Command,
    string[] Args,
    Caps Caps,
    string? Queue = null,
    /// <summary>True when the alias may never run beside another admitted run: a perf audit.</summary>
    bool Exclusive = false,
    /// <summary>The alias's declared expected cost, used when the bucket has no history yet.</summary>
    double? ExpectedCores = null,
    long? ExpectedMemMb = null);

/// <summary>
/// A queue declared once for the whole machine, which any project's runs can join by name. It
/// budgets the host in cores and memory rather than counting slots, so a run is admitted when the
/// cost it is expected to bear fits beside what is already admitted — a 5 GB suite and a 120 MB
/// single test are not the same claim on the machine.
/// </summary>
public sealed record NamedQueue(
    string Name,
    double Cores,
    long MemMb,
    TimeSpan Timeout,
    double DefaultCores,
    long DefaultMemMb)
{
    /// <summary>
    /// Long, because a named queue exists to serialize heavy work across projects and its line can
    /// legitimately be hours deep; bounded, because an unbounded wait hides a queue that is wedged.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromHours(8);

    /// <summary>
    /// The cost assumed for a run the queue has no history for: one core and 1 GiB. Small enough
    /// that several unknown runs share the budget, large enough that a stampede of them is still
    /// bounded — the history the first few runs leave replaces it with the truth.
    /// </summary>
    public static readonly double FallbackCores = 1;
    public static readonly long FallbackMemMb = 1024;
}

public sealed record TmanConfig(
    string FilePath,
    string Dir,
    Caps Defaults,
    IReadOnlyDictionary<string, AliasDef> Aliases,
    string? DefaultQueue = null,
    /// <summary>Expected cost the project declares for runs no alias names; null when it declares none.</summary>
    double? ExpectedCores = null,
    long? ExpectedMemMb = null);

public static class Config
{
    public const string FileName = ".tman.kdl";

    public static string? FindConfigDir(string? startDir = null)
    {
        var dir = new DirectoryInfo(startDir ?? Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, FileName)))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// Reads the nearest `.tman.kdl` whole, or refuses it. Every node either means something here
    /// or is a FormatException naming it: a node tman skipped — a misspelt key, a second copy, a
    /// value it cannot read — would be a bound the user believes is in force and is not.
    /// </summary>
    public static TmanConfig? Load(string? startDir = null)
    {
        var dir = FindConfigDir(startDir);
        if (dir is null) return null;
        var path = Path.Combine(dir, FileName);
        var nodes = ParseFile(path);

        Caps? defaults = null;
        string? defaultQueue = null;
        double? defaultCores = null;
        long? defaultMemMb = null;
        var aliases = new Dictionary<string, AliasDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in nodes)
        {
            switch (n.Name)
            {
                case "defaults":
                    if (defaults is not null) throw new FormatException($"{path}: defaults is declared twice");
                    defaults = ReadCaps(n, $"{path}, defaults", child => child.Name is "queue" or "cores" or "mem");
                    defaultQueue = ReadQueueName(n, $"{path}, defaults");
                    defaultCores = ReadExpectedCores(n, $"{path}, defaults");
                    defaultMemMb = ReadExpectedMem(n, $"{path}, defaults");
                    break;
                case "alias":
                    var alias = ReadAlias(n, path);
                    if (!aliases.TryAdd(alias.Name, alias))
                        throw new FormatException($"{path}: alias \"{alias.Name}\" is declared twice");
                    break;
                default:
                    throw new FormatException($"{path}: unknown node \"{n.Name}\" (expected defaults or alias)");
            }
        }
        return new TmanConfig(path, dir, defaults ?? new Caps(), aliases, defaultQueue, defaultCores, defaultMemMb);
    }

    static AliasDef ReadAlias(KdlNode n, string path)
    {
        if (n.Args.Count != 1 || n.Arg(0) is not { Length: > 0 } name)
            throw new FormatException($"{path}: alias takes exactly one name, as in alias \"test\" {{ ... }}");
        var where = $"{path}, alias \"{name}\"";
        var caps = ReadCaps(n, where, child => child.Name is "command" or "args" or "queue" or "exclusive" or "cores" or "mem");

        var commandNode = n.Child("command")
            ?? throw new FormatException($"alias \"{name}\" in {path} is missing a command");
        if (commandNode.Args.Count != 1 || commandNode.Arg(0) is not { Length: > 0 } command)
            throw new FormatException($"{where}: command takes exactly one program; put its arguments under args");
        var args = n.Child("args")?.Args.Select(a => a.AsString() ?? "").ToArray() ?? [];
        var exclusive = n.Child("exclusive") is { } ex && ReadBool(ex, $"{where}: exclusive");
        return new AliasDef(
            name, command, args, caps, ReadQueueName(n, where), exclusive,
            ReadExpectedCores(n, where), ReadExpectedMem(n, where));
    }

    static bool ReadBool(KdlNode node, string where)
    {
        if (node.Children.Count > 0 || node.Args.Count != 1 || node.AsBool() is not { } value)
            throw new FormatException($"{where} takes exactly one value, true or false");
        return value;
    }

    /// <summary>An alias's declared expected core count, as in `cores 3`; null when it declares none.</summary>
    static double? ReadExpectedCores(KdlNode block, string where) =>
        block.Child("cores") is { } node ? ParseCores(node, where) : null;

    /// <summary>An alias's declared expected memory, as in `mem 2g`; null when it declares none.</summary>
    static long? ReadExpectedMem(KdlNode block, string where) =>
        block.Child("mem") is { } node ? ParseMem(node, where) : null;

    /// <summary>The named queue a `defaults` or alias block joins, or null when it names none.</summary>
    static string? ReadQueueName(KdlNode block, string where)
    {
        if (block.Child("queue") is not { } queueNode) return null;
        if (queueNode.Args.Count != 1 || queueNode.Arg(0) is not { Length: > 0 } queue)
            throw new FormatException($"{where}: queue takes exactly one queue name");
        return queue;
    }

    /// <summary>The machine config, beside the run store so TMAN_HOME relocates both together.</summary>
    public static string MachineConfigPath => Path.Combine(Store.Root, "tman.kdl");

    /// <summary>
    /// The named queue <paramref name="name"/>, as the machine config declares it. Read only when a
    /// run names a queue, so no other run pays for the file. A queue the file does not declare is an
    /// error: projects only join queues, and a name nobody declared is a typo or a missing setup.
    /// </summary>
    public static NamedQueue Queue(string name)
    {
        var machine = LoadMachine();
        return machine.Queues.TryGetValue(name, out var queue)
            ? queue
            : throw new FormatException(
                $"queue \"{name}\" is not declared in {MachineConfigPath}; declare it there, e.g. queue \"{name}\" {{ cores \"80%\" mem \"80%\" }}");
    }

    /// <summary>
    /// The queue a run joins when its alias, its `.tman.kdl` defaults and `--queue` all name none:
    /// the machine's "default" queue, when it declares one. Null when it declares none, which is the
    /// unqueued machine. This is what makes a machine-wide queue the limit every worktree shares,
    /// rather than a per-directory `max-parallel` each worktree gets its own copy of.
    /// </summary>
    public static string? MachineDefaultQueue() =>
        LoadMachine().Queues.ContainsKey("default") ? "default" : null;

    /// <summary>
    /// The delegated cgroup limit-mem makes its runs' cgroups in, or null for a systemd user scope —
    /// see <see cref="Confinement"/>. Read only when a run sets limit-mem.
    /// </summary>
    public static string? MemCgroup() => LoadMachine().Cgroup;

    /// <summary>The machine config, read whole or refused like `.tman.kdl`; empty when the file is absent.</summary>
    static MachineConfig LoadMachine()
    {
        var path = MachineConfigPath;
        var queues = new Dictionary<string, NamedQueue>(StringComparer.Ordinal);
        string? cgroup = null;
        if (!File.Exists(path)) return new MachineConfig(queues, null);
        foreach (var n in ParseFile(path))
        {
            switch (n.Name)
            {
                case "queue":
                    if (n.Args.Count != 1 || n.Arg(0) is not { Length: > 0 } declared)
                        throw new FormatException($"{path}: queue takes exactly one name, as in queue \"compile\" {{ cores \"80%\" mem \"80%\" }}");
                    var where = $"{path}, queue \"{declared}\"";
                    // the slot-counting form is replaced, not kept: a machine that still writes
                    // max-parallel would otherwise be read as a queue with no budget and refuse
                    if (n.Child("max-parallel") is { } slots)
                        throw new FormatException(
                            $"{where}: max-parallel {slots.Arg(0)} counts slots and is no longer read; " +
                            "a queue budgets the host instead, as in queue \"" + declared + "\" { cores \"80%\" mem \"80%\" }");
                    var caps = ReadCaps(n, where, child => child.Name is "cores" or "mem" or "cores-default" or "mem-default",
                        ["queue-timeout"]);
                    if (n.Child("cores") is not { } coresNode)
                        throw new FormatException($"{where}: cores is required (an absolute count or a fraction of the host, as in cores \"80%\")");
                    if (n.Child("mem") is not { } memNode)
                        throw new FormatException($"{where}: mem is required (an absolute size or a fraction of the host, as in mem \"80%\")");
                    var cores = ParseCores(coresNode, where);
                    var memMb = ParseMem(memNode, where);
                    var defaultCores = n.Child("cores-default") is { } dc ? ParseCores(dc, where) : NamedQueue.FallbackCores;
                    var defaultMemMb = n.Child("mem-default") is { } dm ? ParseMem(dm, where) : NamedQueue.FallbackMemMb;
                    if (!queues.TryAdd(declared, new NamedQueue(
                            declared, cores, memMb, caps.QueueTimeout ?? NamedQueue.DefaultTimeout, defaultCores, defaultMemMb)))
                        throw new FormatException($"{path}: queue \"{declared}\" is declared twice");
                    break;
                case "cgroup":
                    if (cgroup is not null) throw new FormatException($"{path}: cgroup is declared twice");
                    if (n.Children.Count > 0 || n.Args.Count != 1 || n.Arg(0) is not { Length: > 0 } dir || !Path.IsPathFullyQualified(dir))
                        throw new FormatException(
                            $"{path}: cgroup takes exactly one absolute directory, as in cgroup \"/sys/fs/cgroup/tman\"");
                    cgroup = Path.TrimEndingDirectorySeparator(dir);
                    break;
                default:
                    throw new FormatException($"{path}: unknown node \"{n.Name}\" (expected queue or cgroup)");
            }
        }
        return new MachineConfig(queues, cgroup);
    }

    sealed record MachineConfig(IReadOnlyDictionary<string, NamedQueue> Queues, string? Cgroup);

    /// <summary>
    /// A queue's cores, absolute (`cores 6`) or a fraction of the host (`cores "80%"`). A fraction
    /// is resolved once, here, against <see cref="Host"/>, so the queue carries a number every reader
    /// agrees on.
    /// </summary>
    static double ParseCores(KdlNode node, string where)
    {
        if (node.Children.Count > 0 || node.Args.Count != 1) throw new FormatException($"{where}: cores takes exactly one value");
        var raw = node.Arg(0) ?? "";
        var value = ParseFractionOrNumber(raw, Host.Cores)
            ?? throw new FormatException($"{where}: bad cores \"{raw}\": expected a count or a fraction such as \"80%\"");
        if (value <= 0) throw new FormatException($"{where}: cores must be greater than zero");
        return value;
    }

    /// <summary>
    /// A queue's memory, absolute in MiB or a size (`mem "36g"`), or a fraction of the host
    /// (`mem "80%"`). Resolved against <see cref="Host.MemMb"/> like cores.
    /// </summary>
    static long ParseMem(KdlNode node, string where)
    {
        if (node.Children.Count > 0 || node.Args.Count != 1) throw new FormatException($"{where}: mem takes exactly one value");
        var raw = node.Arg(0) ?? "";
        if (raw.EndsWith('%'))
        {
            var fraction = ParseFractionOrNumber(raw, Host.MemMb)
                ?? throw new FormatException($"{where}: bad mem \"{raw}\": expected a size such as 8g or a fraction such as \"80%\"");
            var mb = (long)Math.Ceiling(fraction);
            if (mb <= 0) throw new FormatException($"{where}: mem must be greater than zero");
            return mb;
        }
        var absolute = Caps.ParseMemMb(raw)
            ?? throw new FormatException($"{where}: bad mem \"{raw}\": expected a size such as 8g or a fraction such as \"80%\"");
        if (absolute <= 0) throw new FormatException($"{where}: mem must be greater than zero");
        return absolute;
    }

    /// <summary>
    /// A figure that is either a plain number or a percentage of <paramref name="whole"/>
    /// (`"80%"` → 80% of the host). Null when it is neither, or not finite and positive.
    /// </summary>
    static double? ParseFractionOrNumber(string raw, double whole)
    {
        raw = raw.Trim();
        if (raw.EndsWith('%'))
        {
            if (!double.TryParse(raw[..^1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var pct)) return null;
            var value = whole * pct / 100.0;
            return double.IsFinite(value) ? value : null;
        }
        return double.TryParse(raw, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var n) && double.IsFinite(n)
            ? n
            : null;
    }

    /// <summary>A config file's nodes; a parse error names the file, since the offset alone does not.</summary>
    static List<KdlNode> ParseFile(string path)
    {
        try { return Kdl.Parse(File.ReadAllText(path)); }
        catch (FormatException e) { throw new FormatException($"{path}: {e.Message}"); }
    }

    /// <param name="ownedElsewhere">Children the caller reads itself, such as an alias's command.</param>
    /// <param name="keys">The cap keys this block may set; every one by default.</param>
    static Caps ReadCaps(KdlNode block, string where, Func<KdlNode, bool> ownedElsewhere, IReadOnlyList<string>? keys = null)
    {
        keys ??= Caps.Keys;
        var caps = new Caps();
        var seen = new HashSet<string>();
        foreach (var child in block.Children)
        {
            if (!seen.Add(child.Name)) throw new FormatException($"{where}: {child.Name} is set twice");
            if (child.Children.Count > 0) throw new FormatException($"{where}: {child.Name} takes no block");
            if (ownedElsewhere(child)) continue;
            if (!keys.Contains(child.Name))
                throw new FormatException(
                    $"{where}: unknown setting \"{child.Name}\" (expected one of {string.Join(", ", keys)})");
            if (child.Args.Count != 1) throw new FormatException($"{where}: {child.Name} takes exactly one value");
            try { caps = Caps.With(caps, child.Name, child.Arg(0), child.Name); }
            catch (FormatException e) { throw new FormatException($"{where}: {e.Message}"); }
        }
        return caps;
    }

    public static Caps EffectiveCaps(AliasDef? alias, Caps cliCaps, TmanConfig? config) =>
        cliCaps
            .MergeOver(alias?.Caps)
            .MergeOver(config?.Defaults)
            .MergeOver(Caps.SaneDefaults);
}
