namespace Tman;

public sealed record AliasDef(
    string Name,
    string Command,
    string[] Args,
    Caps Caps);

public sealed record TmanConfig(
    string FilePath,
    string Dir,
    Caps Defaults,
    IReadOnlyDictionary<string, AliasDef> Aliases);

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
        var nodes = Kdl.Parse(File.ReadAllText(path));

        Caps? defaults = null;
        var aliases = new Dictionary<string, AliasDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in nodes)
        {
            switch (n.Name)
            {
                case "defaults":
                    if (defaults is not null) throw new FormatException($"{path}: defaults is declared twice");
                    defaults = ReadCaps(n, $"{path}, defaults", _ => false);
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
        return new TmanConfig(path, dir, defaults ?? new Caps(), aliases);
    }

    static AliasDef ReadAlias(KdlNode n, string path)
    {
        if (n.Args.Count != 1 || n.Arg(0) is not { Length: > 0 } name)
            throw new FormatException($"{path}: alias takes exactly one name, as in alias \"test\" {{ ... }}");
        var where = $"{path}, alias \"{name}\"";
        var caps = ReadCaps(n, where, child => child.Name is "command" or "args");

        var commandNode = n.Child("command")
            ?? throw new FormatException($"alias \"{name}\" in {path} is missing a command");
        if (commandNode.Args.Count != 1 || commandNode.Arg(0) is not { Length: > 0 } command)
            throw new FormatException($"{where}: command takes exactly one program; put its arguments under args");
        var args = n.Child("args")?.Args.Select(a => a.AsString() ?? "").ToArray() ?? [];
        return new AliasDef(name, command, args, caps);
    }

    /// <param name="ownedElsewhere">Children the caller reads itself, such as an alias's command.</param>
    static Caps ReadCaps(KdlNode block, string where, Func<KdlNode, bool> ownedElsewhere)
    {
        var caps = new Caps();
        var seen = new HashSet<string>();
        foreach (var child in block.Children)
        {
            if (!seen.Add(child.Name)) throw new FormatException($"{where}: {child.Name} is set twice");
            if (child.Children.Count > 0) throw new FormatException($"{where}: {child.Name} takes no block");
            if (ownedElsewhere(child)) continue;
            if (!Caps.IsKey(child.Name))
                throw new FormatException(
                    $"{where}: unknown setting \"{child.Name}\" (expected one of {string.Join(", ", Caps.Keys)})");
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
