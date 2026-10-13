namespace Tman.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "tman-tests-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    /// <summary>
    /// The physical path, as getcwd reports it and as tman records a cwd or bucket. The temp root
    /// may sit behind a symlink — macOS's /var is a link to /private/var — and a test comparing a
    /// path it built against one tman read back would otherwise compare two spellings of one place.
    /// </summary>
    public static string RealPath(string path)
    {
        var dir = new DirectoryInfo(path);
        while (dir is not null)
        {
            if (dir.LinkTarget is not null)
                return System.IO.Path.Combine(dir.ResolveLinkTarget(true)!.FullName, System.IO.Path.GetRelativePath(dir.FullName, path));
            dir = dir.Parent;
        }
        return path;
    }

    public string WriteFile(string relative, string content)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public string Mkdir(string relative) =>
        Directory.CreateDirectory(System.IO.Path.Combine(Path, relative)).FullName;

    public void Dispose()
    {
        // A test that made this directory the process cwd must not leave the process standing in a
        // deleted directory: every test running in parallel resolves its child's path through getcwd
        // (Process.Start), which then throws FileNotFoundException. The cwd collection serialises only
        // the cwd-changing tests, not the process-starting ones they run beside.
        if (IsInside(Directory.GetCurrentDirectory())) Directory.SetCurrentDirectory(System.IO.Path.GetTempPath());
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    bool IsInside(string dir)
    {
        var root = RealPath(Path).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        var cwd = RealPath(dir).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        return cwd == root || cwd.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
