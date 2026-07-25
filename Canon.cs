using System.Text;

namespace Tman;

/// <summary>
/// One representation per fact. Commands, directories, durations, and sizes get canonicalized here
/// on the way into a run record and formatted here on the way back out, so `tman list`, `tman status`,
/// and the bucket key can never disagree about what a run is.
/// </summary>
public static class Canon
{
    /// <summary>
    /// Resolves a command to an absolute path the way the OS will, so `dotnet` and `/usr/bin/dotnet`
    /// record identically. Returns the input unchanged when nothing on PATH matches — the run is
    /// about to fail to start anyway, and the unresolved name is the more useful thing to report.
    /// </summary>
    public static string ResolveCommand(string command)
    {
        if (command.Length == 0) return command;
        if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
            return TryFullPath(command);

        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar)) return command;

        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var candidate in Candidates(dir, command))
            {
                try { if (File.Exists(candidate)) return TryFullPath(candidate); }
                catch (ArgumentException) { }
            }
        }
        return command;
    }

    static IEnumerable<string> Candidates(string dir, string command)
    {
        string combined;
        try { combined = Path.Combine(dir, command); }
        catch (ArgumentException) { yield break; }
        yield return combined;
        if (!OperatingSystem.IsWindows()) yield break;
        foreach (var ext in new[] { ".exe", ".cmd", ".bat", ".com" })
            yield return combined + ext;
    }

    static string TryFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (ArgumentException) { return path; }
        catch (NotSupportedException) { return path; }
        catch (PathTooLongException) { return path; }
    }

    /// <summary>Absolute, separator-normalized directory with no trailing slash.</summary>
    public static string Dir(string dir)
    {
        var full = TryFullPath(dir);
        return full.Length > 1
            ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : full;
    }

    /// <summary>Short name for a resolved command, for table columns where the full path is noise.</summary>
    public static string CommandLabel(string command)
    {
        var leaf = Path.GetFileName(command);
        return string.IsNullOrEmpty(leaf) ? command : leaf;
    }

    /// <summary>
    /// Re-renders a command and its args as a line you could paste back into a shell. Args are only
    /// quoted when they need it, so the common case stays readable.
    /// </summary>
    public static string CommandLine(string command, IReadOnlyList<string> args, bool full = false)
    {
        var sb = new StringBuilder(full ? command : CommandLabel(command));
        foreach (var a in args)
        {
            sb.Append(' ');
            sb.Append(Quote(a));
        }
        return sb.ToString();
    }

    static string Quote(string arg)
    {
        if (arg.Length == 0) return "''";
        var needsQuote = false;
        foreach (var c in arg)
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or '=' or ',' or ':' or '@' or '+' or '%')
                continue;
            needsQuote = true;
            break;
        }
        return needsQuote ? "'" + arg.Replace("'", "'\\''") + "'" : arg;
    }

    /// <summary>
    /// Age at human scale. TimeSpan's "mm:ss" silently wraps — a 90-minute run reads as "30:00" —
    /// so every magnitude gets its own unit pair instead.
    /// </summary>
    public static string Duration(TimeSpan d)
    {
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        if (d.TotalSeconds < 60) return $"{(int)d.TotalSeconds}s";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m{d.Seconds:00}s";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h{d.Minutes:00}m";
        return $"{(int)d.TotalDays}d{d.Hours:00}h";
    }

    /// <summary>Memory at human scale; MB below a gigabyte, one decimal of GB above it.</summary>
    public static string Mem(long mb) =>
        mb >= 1024 ? $"{mb / 1024.0:0.0}GB" : $"{mb}MB";

    /// <summary>Truncates to a column width, marking the cut so a clipped value is never mistaken for a whole one.</summary>
    public static string Ellipsize(string s, int width) =>
        s.Length <= width ? s : s[..Math.Max(0, width - 1)] + "…";
}
