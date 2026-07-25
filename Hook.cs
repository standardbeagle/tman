using System.Text.Json.Nodes;

namespace Tman;

public enum HookAction
{
    /// <summary>Leave the command exactly as written.</summary>
    PassThrough,
    /// <summary>Run the same command under supervision.</summary>
    Rewrite,
    /// <summary>Tell the agent it is unsupervised, but do not touch the command.</summary>
    Warn,
}

public sealed record HookDecision(HookAction Action, string? Command = null, string? Reason = null);

/// <summary>
/// Claude Code <c>PreToolUse</c> hook for the Bash tool: bare test and build invocations are
/// re-issued through <c>tman run</c>, which PATH shims cannot do — they only cover shell lookups
/// on machines where install ran.
/// <para>
/// Everything here fails open. A command this cannot classify, a project it cannot locate, a
/// request it cannot parse: all leave the command untouched. Losing supervision costs one
/// unsupervised build; blocking costs the user their work, which is how the previous plugin hook
/// (<c>stop-guardian.js</c>, MODULE_NOT_FOUND on Windows) did real damage.
/// </para>
/// </summary>
public static class Hook
{
    /// <summary>
    /// npm scripts that are long-running test/build work — the ones worth an alias and worth
    /// supervising. One list, because a scaffold and a classifier that disagree about what
    /// counts as a test is exactly how defaults drift apart.
    /// </summary>
    public static readonly IReadOnlySet<string> SupervisedNpmScripts =
        new HashSet<string>(StringComparer.Ordinal)
        { "test", "e2e", "lint", "integration", "build", "typecheck" };

    /// <summary>
    /// Renders a PreToolUse response for Claude Code, or an empty string to say nothing at all.
    /// Throws on malformed input; the caller decides what a failure means (it means "allow").
    /// </summary>
    public static string Render(string requestJson, string? parentRunId)
    {
        if (JsonNode.Parse(requestJson) is not JsonObject root) return "";
        if ((string?)root["tool_name"] != "Bash") return "";
        if (root["tool_input"] is not JsonObject toolInput) return "";
        var command = (string?)toolInput["command"];
        if (string.IsNullOrWhiteSpace(command)) return "";

        var decision = Decide(command, (string?)root["cwd"], parentRunId);
        switch (decision.Action)
        {
            case HookAction.Rewrite:
                // every other field of tool_input is carried through: Claude Code validates the
                // replacement against the Bash tool's schema and drops it wholesale if it fails
                var updated = toolInput.DeepClone().AsObject();
                updated["command"] = decision.Command;
                return new JsonObject
                {
                    ["hookSpecificOutput"] = new JsonObject
                    {
                        ["hookEventName"] = "PreToolUse",
                        ["updatedInput"] = updated,
                    },
                    // a silent rewrite makes the transcript disagree with what ran, so announce it
                    ["systemMessage"] = decision.Reason,
                }.ToJsonString();

            case HookAction.Warn:
                // addressed to the model, which is the only party that can re-issue the command
                return new JsonObject
                {
                    ["hookSpecificOutput"] = new JsonObject
                    {
                        ["hookEventName"] = "PreToolUse",
                        ["additionalContext"] = decision.Reason,
                    },
                }.ToJsonString();

            default:
                return "";
        }
    }

    public static HookDecision Decide(string command, string? cwd, string? parentRunId)
    {
        // already inside a supervised tree: a second claim would queue against its own parent
        if (!string.IsNullOrEmpty(parentRunId)) return new(HookAction.PassThrough);

        var trimmed = command.Trim();

        // Rewriting means prefixing a string this did not parse, which only preserves meaning for
        // one plain command: `cd app && npm test` would supervise the `cd`, and `CI=1 npm test`
        // would exec a program named "CI=1". Anything more compound gets named, never touched.
        if (!IsSingleCommand(trimmed) || StartsWithEnvAssignment(trimmed))
            return SplitSegments(trimmed).Any(IsTestOrBuild)
                ? new(HookAction.Warn,
                    Reason: $"tman: '{trimmed}' runs test or build work unsupervised, and it is too " +
                            "compound to rewrite safely. Run that part on its own as " +
                            "`tman run -- <command>` so it gets a stall backstop and orphan reaping.")
                : new(HookAction.PassThrough);

        if (!IsTestOrBuild(trimmed)) return new(HookAction.PassThrough);

        // no .tman.kdl means no project caps to apply, and rewriting would impose this machine's
        // built-ins on a project that never opted in
        if (Config.FindConfigDir(string.IsNullOrEmpty(cwd) ? null : cwd) is null)
            return new(HookAction.Warn,
                Reason: $"tman: '{trimmed}' is running unsupervised — no .tman.kdl in this project. " +
                        "Run `tman init` to adopt supervision, or `tman run -- " + trimmed +
                        "` for this one command.");

        var supervised = "tman run -- " + trimmed;
        return new(HookAction.Rewrite, supervised,
            Reason: $"tman: supervising `{trimmed}` as `{supervised}`");
    }

    /// <summary>
    /// Deliberately narrow: only the invocations the fleet audit actually found running bare.
    /// A command that is not on this list is left alone rather than guessed at. tman invoking
    /// itself is never a match, so nothing is supervised twice.
    /// </summary>
    static bool IsTestOrBuild(string segment)
    {
        var tokens = segment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var i = 0;
        while (i < tokens.Length && IsEnvAssignment(tokens[i])) i++;
        var words = tokens[i..];
        if (words.Length == 0) return false;

        var sub = words.Length > 1 ? words[1] : "";
        return Basename(words[0]) switch
        {
            "tman" => false,
            "npm" => sub == "test"
                     || (sub == "run" && words.Length > 2 && SupervisedNpmScripts.Contains(words[2])),
            "go" or "dotnet" => sub is "test" or "build",
            "cargo" or "make" => sub == "test",
            "pytest" => true,
            _ => false,
        };
    }

    /// <summary>
    /// True when the whole string is one command, so prefixing it preserves its meaning. Operators
    /// that split or chain (including the <c>&amp;</c> in <c>2&gt;&amp;1</c>) disqualify it.
    /// </summary>
    static bool IsSingleCommand(string command) =>
        command.IndexOfAny(SegmentSeparators) < 0 && !command.Contains("$(");

    static readonly char[] SegmentSeparators = { '&', '|', ';', '\n', '\r', '`' };

    /// <summary>The pieces a chained command runs, so a buried test/build can still be named.</summary>
    static IEnumerable<string> SplitSegments(string command) =>
        command.Split(SegmentSeparators, StringSplitOptions.RemoveEmptyEntries);

    static bool StartsWithEnvAssignment(string command) =>
        command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            is [var first, ..] && IsEnvAssignment(first);

    static bool IsEnvAssignment(string token)
    {
        var eq = token.IndexOf('=');
        return eq > 0 && token[..eq].All(c => char.IsLetterOrDigit(c) || c == '_');
    }

    static string Basename(string path)
    {
        var cut = path.LastIndexOfAny(new[] { '/', '\\' });
        return cut < 0 ? path : path[(cut + 1)..];
    }
}
