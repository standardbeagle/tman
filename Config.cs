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

    public static TmanConfig? Load(string? startDir = null)
    {
        var dir = FindConfigDir(startDir);
        if (dir is null) return null;
        var path = Path.Combine(dir, FileName);
        var nodes = Kdl.Parse(File.ReadAllText(path));

        var defaults = Caps.FromNode(nodes.Find(n => n.Name == "defaults"));
        var aliases = new Dictionary<string, AliasDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in nodes.Where(n => n.Name == "alias"))
        {
            var name = n.Arg(0);
            if (name is null) continue;
            var command = n.Child("command")?.Arg(0);
            if (command is null)
                throw new FormatException($"alias \"{name}\" in {path} is missing a command");
            var argsNode = n.Child("args");
            var args = argsNode is null
                ? Array.Empty<string>()
                : argsNode.Args.Select(a => a.AsString() ?? "").ToArray();
            aliases[name] = new AliasDef(name, command, args, Caps.FromNode(n));
        }
        return new TmanConfig(path, dir, defaults, aliases);
    }

    public static Caps EffectiveCaps(AliasDef? alias, Caps cliCaps, TmanConfig? config) =>
        cliCaps
            .MergeOver(alias?.Caps)
            .MergeOver(config?.Defaults)
            .MergeOver(Caps.SaneDefaults);
}
