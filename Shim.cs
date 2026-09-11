namespace Tman;

public static class Shim
{
    public static (List<string> Written, List<string> Skipped) Generate(string dir, IEnumerable<string> aliasNames)
    {
        var written = new List<string>();
        var skipped = new List<string>();
        foreach (var name in aliasNames)
        {
            var shPath = Path.Combine(dir, name);
            if (Directory.Exists(shPath))
            {
                skipped.Add(shPath);
                continue;
            }
            var content = $"#!/usr/bin/env sh\nexec tman run --alias {name} -- \"$@\"\n";
            if (File.Exists(shPath) && File.ReadAllText(shPath) != content)
            {
                skipped.Add(shPath);
                continue;
            }
            File.WriteAllText(shPath, content);
            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(shPath);
                File.SetUnixFileMode(shPath, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
            written.Add(shPath);

            if (OperatingSystem.IsWindows())
            {
                var ps1Path = Path.Combine(dir, name + ".ps1");
                File.WriteAllText(ps1Path, $"tman run --alias {name} -- @args\r\nexit $LASTEXITCODE\r\n");
                written.Add(ps1Path);
                var cmdPath = Path.Combine(dir, name + ".cmd");
                File.WriteAllText(cmdPath, $"@tman run --alias {name} -- %*\r\n");
                written.Add(cmdPath);
            }
        }
        return (written, skipped);
    }

    public static bool AppendGitignore(string dir, IEnumerable<string> aliasNames)
    {
        var path = Path.Combine(dir, ".gitignore");
        var existing = File.Exists(path) ? File.ReadAllLines(path) : Array.Empty<string>();
        var toAdd = new List<string> { "", "# tman shims and run logs" };
        // .tman/ holds the last run's output and failure digest per alias. Rewritten every run and
        // meaningful only on the machine that produced it, so it is never a commit.
        if (!existing.Contains("/" + RunLog.DirName + "/")) toAdd.Add("/" + RunLog.DirName + "/");
        foreach (var name in aliasNames)
        {
            // Generate skips a shim whose name is already a directory, so ignoring "/name" here
            // would hide that directory's contents instead of a shim that never existed.
            if (Directory.Exists(Path.Combine(dir, name))) continue;
            var entry = "/" + name;
            if (!existing.Contains(entry)) toAdd.Add(entry);
            var ps1Entry = "/" + name + ".ps1";
            if (!existing.Contains(ps1Entry)) toAdd.Add(ps1Entry);
            var cmdEntry = "/" + name + ".cmd";
            if (!existing.Contains(cmdEntry)) toAdd.Add(cmdEntry);
        }
        if (toAdd.Count <= 2) return false;
        File.AppendAllLines(path, toAdd);
        return true;
    }
}
