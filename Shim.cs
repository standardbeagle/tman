namespace Tman;

public static class Shim
{
    public static List<string> Generate(string dir, IEnumerable<string> aliasNames)
    {
        var written = new List<string>();
        foreach (var name in aliasNames)
        {
            var shPath = Path.Combine(dir, name);
            File.WriteAllText(shPath, $"#!/usr/bin/env sh\nexec tman run --alias {name} -- \"$@\"\n");
            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(shPath);
                File.SetUnixFileMode(shPath, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
            written.Add(shPath);

            if (OperatingSystem.IsWindows())
            {
                var cmdPath = Path.Combine(dir, name + ".cmd");
                File.WriteAllText(cmdPath, $"@tman run --alias {name} -- %*\r\n");
                written.Add(cmdPath);
            }
        }
        return written;
    }

    public static bool AppendGitignore(string dir, IEnumerable<string> aliasNames)
    {
        var path = Path.Combine(dir, ".gitignore");
        var existing = File.Exists(path) ? File.ReadAllLines(path) : Array.Empty<string>();
        var toAdd = new List<string> { "", "# tman shims" };
        foreach (var name in aliasNames)
        {
            var entry = "/" + name;
            if (!existing.Contains(entry)) toAdd.Add(entry);
            var cmdEntry = "/" + name + ".cmd";
            if (!existing.Contains(cmdEntry)) toAdd.Add(cmdEntry);
        }
        if (toAdd.Count <= 2) return false;
        File.AppendAllLines(path, toAdd);
        return true;
    }
}
