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
                        if (Hook.SupervisedNpmScripts.Contains(s.Name))
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
        // A hang backstop, not a runtime budget: cold builds and test suites legitimately
        // run for many quiet minutes, and killing those is far more costly than noticing a
        // real hang late.
        sb.AppendLine("    stall \"30m\"");
        sb.AppendLine("    max-parallel 2");
        sb.AppendLine("    retain \"24h\"");
        sb.AppendLine("    // opt-in ceilings; builds legitimately saturate cores and eat RAM");
        sb.AppendLine("    // max-mem 8192");
        sb.AppendLine("    // max-cpu 95");
        sb.AppendLine("    // max-time \"30m\"");
        sb.AppendLine("}");
        sb.AppendLine();

        if (detected.Count == 0)
        {
            // No runnable stub: an alias that exits 0 without running anything makes a repo
            // report green, and a suite that cannot fail looks exactly like one that passes.
            // Leaving 'test' undefined makes `tman test` fail loudly until it is filled in.
            sb.AppendLine("// no test command detected; uncomment and fill in:");
            sb.AppendLine("// alias \"test\" {");
            sb.AppendLine("//     command \"your-test-runner\"");
            sb.AppendLine("//     args \"--flag\"");
            sb.AppendLine("// }");
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
