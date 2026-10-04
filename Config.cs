namespace Tman;

public sealed record AliasDef(
    string Name,
    string Command,
    string[] Args,
    Caps Caps,
    string? Queue = null);

/// <summary>A queue declared once for the whole machine, which any project's runs can join by name.</summary>
public sealed record NamedQueue(string Name, int MaxParallel, TimeSpan Timeout)
{
    /// <summary>
    /// Long, because a named queue exists to serialize heavy work across projects and its line can
    /// legitimately be hours deep; bounded, because an unbounded wait hides a queue that is wedged.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromHours(8);
}

public sealed record TmanConfig(
    string FilePath,
    string Dir,
    Caps Defaults,
    IReadOnlyDictionary<string, AliasDef> Aliases,
    string? DefaultQueue = null);

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
        var aliases = new Dictionary<string, AliasDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in nodes)
        {
            switch (n.Name)
            {
                case "defaults":
                    if (defaults is not null) throw new FormatException($"{path}: defaults is declared twice");
                    defaults = ReadCaps(n, $"{path}, defaults", child => child.Name == "queue");
                    defaultQueue = ReadQueueName(n, $"{path}, defaults");
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
        return new TmanConfig(path, dir, defaults ?? new Caps(), aliases, defaultQueue);
    }

    static AliasDef ReadAlias(KdlNode n, string path)
    {
        if (n.Args.Count != 1 || n.Arg(0) is not { Length: > 0 } name)
            throw new FormatException($"{path}: alias takes exactly one name, as in alias \"test\" {{ ... }}");
        var where = $"{path}, alias \"{name}\"";
        var caps = ReadCaps(n, where, child => child.Name is "command" or "args" or "queue");

        var commandNode = n.Child("command")
            ?? throw new FormatException($"alias \"{name}\" in {path} is missing a command");
        if (commandNode.Args.Count != 1 || commandNode.Arg(0) is not { Length: > 0 } command)
            throw new FormatException($"{where}: command takes exactly one program; put its arguments under args");
        var args = n.Child("args")?.Args.Select(a => a.AsString() ?? "").ToArray() ?? [];
        return new AliasDef(name, command, args, caps, ReadQueueName(n, where));
    }

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
                $"queue \"{name}\" is not declared in {MachineConfigPath}; declare it there, e.g. queue \"{name}\" {{ max-parallel 1 }}");
    }

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
                        throw new FormatException($"{path}: queue takes exactly one name, as in queue \"compile\" {{ max-parallel 1 }}");
                    var where = $"{path}, queue \"{declared}\"";
                    var caps = ReadCaps(n, where, _ => false, ["max-parallel", "queue-timeout"]);
                    if (caps.MaxParallel is not ({ } slots and > 0))
                        throw new FormatException($"{where}: max-parallel is required and must be at least 1");
                    if (!queues.TryAdd(declared, new NamedQueue(declared, slots, caps.QueueTimeout ?? NamedQueue.DefaultTimeout)))
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
