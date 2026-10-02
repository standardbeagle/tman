namespace Tman;

/// <summary>
/// Whose runs a caller may kill in bulk. Several agents and sessions share one box, so
/// `tman kill all` means "all of mine", never "everything on the machine".
///
/// A run belongs to the project root it was started from — the directory holding the nearest
/// `.tman.kdl` above the cwd, else the cwd itself — and to the agent session that started it, when
/// one is identifiable. Both must match: subagents of one session share a session id but work in
/// different clones, so the root separates them; two sessions in one clone share a root, so the
/// session separates them. A side with no session id (a human shell, an older record) matches any.
/// </summary>
public static class Scope
{
    /// <summary>Set by Claude Code for every process of a session. The only agent id tman knows.</summary>
    public const string SessionEnvVar = "CLAUDE_CODE_SESSION_ID";

    /// <summary>
    /// Test seam: whether the caller has a controlling terminal. Null means ask the OS. An agent
    /// has none, which is the property <c>kill all --everywhere</c> relies on.
    /// </summary>
    internal static Func<bool>? TerminalProbe;

    public static string ProjectRootOf(string dir) =>
        Config.FindConfigDir(dir) ?? Canon.Dir(dir);

    /// <summary>Where tman was invoked from; the scope a new run is recorded under.</summary>
    public static string CurrentProjectRoot() => ProjectRootOf(Directory.GetCurrentDirectory());

    public static string? CurrentSession() =>
        Environment.GetEnvironmentVariable(SessionEnvVar) is { Length: > 0 } s ? s : null;

    /// <summary>A record written before scopes existed falls back to the cwd it ran in.</summary>
    public static string RootOf(RunRecord r) =>
        r.ProjectRoot ?? ProjectRootOf(r.Cwd ?? "");

    public static bool Contains(RunRecord r, string callerRoot, string? callerSession) =>
        string.Equals(RootOf(r), callerRoot, StringComparison.Ordinal)
        && (r.AgentSession is null || callerSession is null || r.AgentSession == callerSession);

    /// <summary>`~`-abbreviated, left-truncated so the tail that tells projects apart survives.</summary>
    public static string Short(string root, int width = 24)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 1 && root.StartsWith(home, StringComparison.Ordinal)
            && (root.Length == home.Length || root[home.Length] is '/' or '\\'))
            root = "~" + root[home.Length..];
        return root.Length <= width ? root : "…" + root[^(width - 1)..];
    }

    /// <summary>One run's owner, for a message: its project root and, when known, its session.</summary>
    public static string Describe(RunRecord r) =>
        r.AgentSession is null ? RootOf(r) : $"{RootOf(r)}, session {r.AgentSession}";

    /// <summary>
    /// Whether this process can reach a person: /dev/tty opens only with a controlling terminal,
    /// which an agent's shell does not have. Stdin is not a stand-in — an agent can feed it.
    /// Windows has no /dev/tty; there the console input handle being redirected is the test.
    /// </summary>
    public static bool HasControllingTerminal()
    {
        if (TerminalProbe is not null) return TerminalProbe();
        if (OperatingSystem.IsWindows()) return !Console.IsInputRedirected;
        try
        {
            using var tty = new FileStream("/dev/tty", FileMode.Open, FileAccess.ReadWrite);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
