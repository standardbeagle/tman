using System.Text.Json;

namespace Tman;

public static partial class Program
{
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
}
