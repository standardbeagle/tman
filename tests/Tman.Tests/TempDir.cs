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
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
