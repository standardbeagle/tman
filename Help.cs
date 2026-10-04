using System.Text;

namespace Tman;

/// <summary>
/// Every help text tman prints, from one table: the top-level `tman --help` is assembled from the
/// same entries `tman &lt;command&gt; --help` prints, so the two cannot describe a command differently.
/// Help is answered before anything else runs — no store directory, no sweep, no config written —
/// so asking a command how it works can never be the thing that does it.
/// </summary>
public static class Help
{
    /// <param name="Synopsis">The command's line in the top-level list; may wrap with indented lines.</param>
    /// <param name="Body">Everything `tman &lt;command&gt; --help` adds after the usage lines.</param>
    public sealed record Command(string Name, string[] Usage, string Synopsis, string Body, string[]? AlsoCalled = null);

    /// <summary>The flags `tman run` takes, in the top-level help and in `tman run --help` alike.</summary>
    public const string RunFlags = """
        run flags:
          --name N            dedup lock name (per directory; fail if already running)
          --replace           kill the run holding the name, then wait for its runner to release
                              the name (up to --queue-timeout; refuses to start if still held)
          --max-time T        wall-clock limit (30s, 10m, 2h)
          --stall T           kill if no output or cpu/io/io-wait activity for T
          --max-mem M         kill above process-tree memory (4096, 2g)
          --max-cpu P         kill above P% sustained CPU
          --limit-cpus N      run the tree on N CPUs, enforced by the kernel (affinity; nproc sees N)
          --limit-mem M       hard memory ceiling for the tree, enforced by the kernel; crossing it
                              ends the tree (Linux: a systemd user scope; Windows: a Job Object)
          --max-parallel N    queue until one of this bucket's N slot files can be held
                              (bucket: name-or-command @ dir)
          --queue-timeout T   give up queueing after T
          --queue Q           also wait in named queue Q, shared by every project on this machine
                              and declared in ~/.tman/tman.kdl; admitted in arrival order.
                              Without it, the alias's queue, else the .tman.kdl defaults' queue
          --no-queue          join no named queue, whatever .tman.kdl says (a dev server, a watch)
        """;

    const string Sweep = """
        every command sweeps: orphans (dead runner, live child) are killed, and finished records
        past the retention window are pruned. Set the window with `retain` in .tman.kdl defaults
        (24h by default). Lock files are not part of it — a bucket whose holder died is taken over
        in place by the next run that claims it.
        """;

    public static readonly IReadOnlyList<Command> Commands =
    [
        new("run",
            ["tman run [flags] -- <cmd> [args...]", "tman run [flags] --alias <name> [args...]"],
            "run a process (or a .tman.kdl alias) under supervision",
            $"""
            Runs <cmd> under supervision: its caps are enforced across the whole process tree, its
            output is logged to .tman/<alias>.log beside the governing .tman.kdl (with a
            .tman/<alias>.fail.log digest when it does not pass), and it is recorded so `tman list`,
            `tman kill` and the sweep can find it. Everything after `--` is the command, untouched.
            With --alias, the alias's command and args run in its .tman.kdl's directory, followed by
            any further args.

            {RunFlags}

            caps: flags > alias block > .tman.kdl defaults > built-ins (stall 30m, max-parallel 2,
            queue-timeout 5m). A cap value tman cannot read refuses the run.

            exit: the child's own code, or 124 max-time, 125 stall, 126 max-mem/max-cpu/limit-mem, 127 cannot
            start / bad flag or config, 130 killed, interrupted, queue timeout, or name already
            running, 74 the run store cannot be written.
            """),
        new("<alias>",
            ["tman <alias> [args...]"],
            "shorthand for tman run --alias <alias>",
            """
            Runs a .tman.kdl alias. Every arg goes to the alias's command, `--help` included; to read
            what an alias runs, use `tman help <alias>`.
            """),
        new("list",
            ["tman list [--all]"],
            "list live (or all) runs",
            """
            Lists live runs: running ones, and ones queued for a slot (pid `-`; a waiter in a named
            queue shows its place as `queued #N`). PROJECT is the run's project root — the scope
            `tman kill all` works within.

              --all               every record still within retention, finished runs included
            """,
            ["ls"]),
        new("kill",
            ["tman kill <id|name|id-prefix|all>...", "tman kill all --everywhere"],
            "kill run(s)",
            """
            Kills each matching live run's process tree, queued runs included — a queued run ends
            without ever starting its child. The run's own tman records it `killed: killed via tman
            kill` and exits 130. An id prefix needs at least 4 characters.

            `all` means your own runs: the same project root (the directory holding the nearest
            .tman.kdl above the cwd, else the cwd) and, when both sides have one, the same agent
            session (CLAUDE_CODE_SESSION_ID). Other projects' and sessions' runs are left alone and
            counted. A run named by id or name is killed wherever it is, and its owner is printed
            when that is outside your scope.

              --everywhere        with `all`: every live run on the machine. Needs a controlling
                                  terminal (/dev/tty); without one it refuses with exit 77

            exit: 0, or 1 when part of a run's tree could not be killed; 127 for an unknown flag;
            77 for `--everywhere` without a terminal.
            """),
        new("clean",
            ["tman clean"],
            "reap orphans, prune old records",
            """
            Runs the housekeeping sweep now and prints what it did. Every command already sweeps
            before it acts; this is the same sweep, on demand, with the counts.
            """),
        new("status",
            ["tman status [<id|name|id-prefix>] [--json]"],
            "summary or run detail",
            """
            With no run named: how many runs are live, and how many records are in each state.
            With a run named: the live run, or else the most recent record, that matches it.

              --json              the run's record as JSON

            exit: 0, or 127 when no record matches.
            """),
        new("init",
            ["tman init [--shims] [--gitignore]"],
            "scaffold .tman.kdl (+ shim scripts)",
            """
            Writes .tman.kdl in the current directory when there is none — never over one — with
            aliases for the test commands it detects (package.json scripts, pytest, go, make). One
            it cannot detect is left commented out, so `tman test` fails until it is filled in.

              --shims             a ./<alias> script for each alias in the .tman.kdl on disk, which
                                  runs it through tman; an existing different file is left alone
              --gitignore         ignore .tman/ and the shims in .gitignore
            """),
        new("probe",
            ["tman probe --pid <pid> [--start-ticks <ticks>]"],
            """
            is a recorded pid still that process? exit 0
            mine, 1 gone, 3 not mine (pid reused); read-only
            """,
            """
            For a caller that recorded a pid of its own. Prints and exits with the verdict: 0 mine,
            1 gone, 3 not mine (the OS reused the pid), 127 bad arguments. Never signals or kills,
            and does not sweep.

              --pid N             the pid to ask about
              --start-ticks T     Linux only: field 22 of /proc/<pid>/stat when the pid was
                                  recorded; without it a live pid counts as yours
            """),
        new("hook",
            ["tman hook pretooluse"],
            """
            Claude Code PreToolUse hook: reads the tool call
            on stdin, re-issues bare test/build commands
            through tman, never blocks
            """,
            """
            Reads a Claude Code PreToolUse request on stdin and answers on stdout, re-issuing a bare
            test or build command through tman. It never blocks the command: anything it cannot
            decide is let through unchanged, and it always exits 0.
            """),
        new("help",
            ["tman help [<command>|<alias>]", "tman <command> --help"],
            "this help, a command's, or what an alias runs",
            """
            `-h` works wherever `--help` does. Help changes nothing: it creates no files and runs
            no sweep.
            """),
    ];

    /// <summary>The built-in command <paramref name="name"/> names, under any of its names.</summary>
    public static Command? Find(string name) =>
        Commands.FirstOrDefault(c => !c.Name.StartsWith('<') && (c.Name == name || (c.AlsoCalled?.Contains(name) ?? false)));

    static bool IsHelpFlag(string a) => a is "--help" or "-h";

    /// <summary>
    /// The help <paramref name="argv"/> asks for: "" for the top-level text, a command or alias
    /// name for its own, or null when it asks for none. `--help` counts only before a `--`: after
    /// it, it belongs to the supervised command. An alias's args are all its command's, so
    /// `tman &lt;alias&gt; --help` is never read here.
    /// </summary>
    public static string? Requested(string[] argv)
    {
        if (argv.Length == 0) return "";
        if (IsHelpFlag(argv[0])) return "";
        if (argv[0] == "help") return argv.Length == 1 ? "" : IsHelpFlag(argv[1]) ? "help" : argv[1];
        if (Find(argv[0]) is not { } command) return null;
        return argv.Skip(1).TakeWhile(a => a != "--").Any(IsHelpFlag) ? command.Name : null;
    }

    /// <summary>The top-level help: every command's synopsis, the run flags, and the sweep.</summary>
    public static string Overview()
    {
        var sb = new StringBuilder();
        sb.AppendLine("tman - AOT process/test runner manager");
        sb.AppendLine();
        sb.AppendLine("usage:");
        foreach (var c in Commands)
        {
            var names = c.AlsoCalled is null ? c.Usage[0] : c.Usage[0].Replace(c.Name, string.Join('|', [c.Name, .. c.AlsoCalled]));
            var synopsis = c.Synopsis.ReplaceLineEndings("\n").Split('\n');
            if (names.Length <= 38)
                sb.AppendLine($"  {names,-38}  {synopsis[0]}");
            else
                sb.AppendLine($"  {names}").AppendLine($"  {"",-38}  {synopsis[0]}");
            foreach (var more in synopsis.Skip(1)) sb.AppendLine($"  {"",-38}  {more}");
        }
        sb.AppendLine();
        sb.AppendLine(RunFlags);
        sb.AppendLine();
        sb.AppendLine(Sweep);
        sb.AppendLine();
        sb.Append("tman <command> --help, or tman help <command>, describes one command.");
        return sb.ToString();
    }

    /// <summary>One command's help: its usage lines, then what it does and takes.</summary>
    public static string For(Command c)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < c.Usage.Length; i++) sb.AppendLine((i == 0 ? "usage: " : "       ") + c.Usage[i]);
        if (c.AlsoCalled is not null) sb.AppendLine($"(also: tman {string.Join(", tman ", c.AlsoCalled)})");
        sb.AppendLine();
        sb.Append(c.Body.TrimEnd());
        return sb.ToString();
    }

    /// <summary>What an alias runs, as its .tman.kdl defines it — the help an alias can have.</summary>
    public static string ForAlias(AliasDef alias, TmanConfig config)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"usage: tman {alias.Name} [args...]");
        sb.AppendLine();
        sb.AppendLine($"alias \"{alias.Name}\" from {config.FilePath}");
        sb.AppendLine($"  runs:   {Canon.CommandLine(alias.Command, alias.Args, full: true)} [args...]");
        sb.AppendLine($"  in:     {config.Dir}");
        sb.AppendLine($"  caps:   {Program.DescribeCaps(Config.EffectiveCaps(alias, new Caps(), config))}");
        if ((alias.Queue ?? config.DefaultQueue) is { } queue) sb.AppendLine($"  queue:  {queue}");
        sb.Append("Every arg, --help included, goes to the command.");
        return sb.ToString();
    }
}
