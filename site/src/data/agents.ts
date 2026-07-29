import { type Block, type Faq, type Source, h, p, note, code, list, table } from "../lib/blocks";
import { href } from "../lib/site";

/**
 * How much control a given agent's hook surface actually gives you over a shell command.
 *
 * This is the axis the whole setup section is organised around, because it decides which of tman's
 * three integration mechanisms you get. It is a property of the agent, not of tman, and it is the
 * first thing anyone arriving from a search for "<agent> + tman" needs to know.
 */
export type Tier = "rewrite" | "gate" | "shim";

export const TIERS: Record<Tier, { label: string; blurb: string }> = {
  rewrite: {
    label: "rewrites",
    blurb:
      "The agent's pre-tool hook can replace the command before it runs, so a bare <code>npm test</code> becomes a supervised one with no cooperation from the model.",
  },
  gate: {
    label: "gates",
    blurb:
      "The hook can allow, ask, or deny — but not rewrite. tman still decides <em>whether</em> the command runs; getting it supervised takes a shim or a denial the model can act on.",
  },
  shim: {
    label: "shims only",
    blurb:
      "No documented pre-shell hook. Supervision comes from PATH shims plus a line in the project's instruction file, which is enough for anything invoked through a shell.",
  },
};

export interface Agent {
  slug: string;
  name: string;
  vendor: string;
  tier: Tier;
  /** One line for the hub table. */
  tagline: string;
  /** Head title. Front-loads the agent name, which is what the query actually contains. */
  title: string;
  description: string;
  /** Where the integration is configured, shown in the hub table. */
  configPath: string;
  /** The lifecycle event tman attaches to, or null when there is none to attach to. */
  hookEvent: string | null;
  blocks: Block[];
  faqs: Faq[];
  sources: Source[];
}

/** Shared opener — every guide starts from the same two commands, so they are written once. */
const adopt = (): Block[] => [
  h("1. Install tman and adopt the project"),
  p(
    "Everything below assumes the binary is on your PATH and the repository has a <code>.tman.kdl</code>. " +
      "That file is what scopes caps to this project, and it is also the signal several of the integrations " +
      "test for before they do anything.",
  ),
  code(
    `npm install -g @standardbeagle/tman   # or the install.sh one-liner

cd your-project
tman init --shims --gitignore`,
    "shell",
  ),
  p(
    "<code>tman init</code> writes a <code>defaults</code> block and an alias for each test or build " +
      "command it can detect. Commands it cannot detect are left <em>commented out</em> rather than stubbed, " +
      "so an alias you never filled in fails loudly instead of exiting 0 having run nothing.",
  ),
];

/** Shared closer — the smoke test that proves supervision is actually happening. */
const verify = (extra: Block[] = []): Block[] => [
  h("Verify it is actually on"),
  p(
    "A supervision integration that silently does nothing is worse than none, because it stops you " +
      "looking. Two checks, both of which fail visibly:",
  ),
  code(
    `# 1. the classifier agrees this command is worth supervising
echo '{"tool_name":"Bash","cwd":"'"$PWD"'","tool_input":{"command":"npm test"}}' \\
  | tman hook pretooluse

# 2. a real run shows up while it is in flight
tman run --name smoke -- sleep 30 &
tman list`,
    "shell",
  ),
  p(
    "The first prints a JSON object naming the rewritten command. Empty output means tman decided to " +
      "stay out of the way — no <code>.tman.kdl</code> above <code>cwd</code>, a command it does not classify " +
      "as test or build work, or a shell string it refused to parse. The second must list the run; " +
      "if <code>tman list</code> is empty, nothing is being supervised.",
  ),
  ...extra,
];

const caps = (): Block[] => [
  h("Caps worth setting for agent-driven work"),
  p(
    "An agent re-runs the same command far more often than a human does, and it does it while you are " +
      "not watching. That changes which caps earn their keep:",
  ),
  table(
    ["cap", "why it matters more under an agent"],
    [
      [
        "<code>--name</code>",
        "The agent will start the suite again before the last one finished. A dedup lock refuses the duplicate instead of running two copies against the same port or database.",
      ],
      [
        "<code>--max-parallel 2</code>",
        "The default, and the right one for most repos. Excess runs queue on a slot file rather than stampeding your cores.",
      ],
      [
        "<code>--max-time</code>",
        "The only cap that bounds a run which is genuinely working but will never finish — a wait on a peer that never answers looks identical to healthy IO.",
      ],
      [
        "<code>--stall 30m</code>",
        "The built-in. A hang backstop, not a runtime budget: it should sit well above the longest quiet stretch your slowest command legitimately has.",
      ],
      [
        "<code>--max-mem</code>",
        "Opt-in, and worth it where a leak mode exists — worker pools, browser fleets, long-lived daemons. Off by default because builds are supposed to be greedy.",
      ],
    ],
  ),
  p(
    `Per-command starting points live in the <a href="${href("/tuning")}">tuning guides</a> — one page ` +
      "per test and lint tool, with the reasoning behind each number rather than just the number.",
  ),
];

const instructionFile = (file: string): Block[] => [
  h(`Tell the model, in ${file}`),
  p(
    "Shims and hooks catch the mechanical cases. The remaining gap is the model deciding to do something " +
      "clever — running the suite through a subshell, or with an inline environment assignment, both of which " +
      "tman deliberately refuses to rewrite because prefixing a string it did not parse changes what runs. " +
      `A standing instruction in <code>${file}</code> closes that gap:`,
  ),
  code(
    `## Running tests and builds

Run every test, build, lint, and typecheck command through tman — never bare.
Prefer the repo-root shims (\`./test\`, \`./lint\`) when they exist; they carry
this project's \`.tman.kdl\` caps. Otherwise prefix explicitly:

    tman run -- <command>

Never widen the caps from the command line. Exit 124 is a timeout, 125 a stall,
126 a resource cull — report those, do not retry with looser limits.`,
    file,
    "markdown",
  ),
];

/** The adapter every gate-tier agent needs: turn tman's rewrite into a denial the model can act on. */
const denyAdapter = (payloadHint: string): Block[] => [
  h("Turn the rewrite into an actionable denial"),
  p(
    "Because this hook cannot replace the command, the useful move is to <em>refuse</em> the unsupervised " +
      "one and hand the model the supervised string to re-issue. tman already knows which commands deserve " +
      "that and what the replacement should be, so the adapter is a few lines around " +
      "<code>tman hook pretooluse</code> rather than a second copy of the classifier.",
  ),
  code(
    `#!/usr/bin/env bash
# tman-gate.sh — deny an unsupervised test/build command, naming its supervised form.
set -euo pipefail
payload=$(cat)

# ${payloadHint}
command=$(jq -r '${payloadHint}' <<<"$payload")
[ -n "$command" ] && [ "$command" != "null" ] || exit 0

verdict=$(jq -nc --arg c "$command" --arg d "$PWD" \\
  '{tool_name:"Bash", cwd:$d, tool_input:{command:$c}}' | tman hook pretooluse)

# tman stays silent on anything it will not supervise. So does this.
[ -n "$verdict" ] || exit 0
supervised=$(jq -r '.hookSpecificOutput.updatedInput.command // empty' <<<"$verdict")
[ -n "$supervised" ] || exit 0

jq -nc --arg s "$supervised" '{
  permissionDecision: "deny",
  permissionDecisionReason: ("Run it supervised instead: " + $s)
}'`,
    "~/.config/tman-gate.sh — chmod +x",
    "bash",
  ),
  note(
    "<strong>Confirm the payload field before you trust it.</strong> Hook payload shapes move between " +
      "versions, and a <code>jq</code> path that no longer matches yields an empty command and a script " +
      "that silently allows everything — a false green. Replace the script with " +
      "<code>cat &gt; /tmp/hook-payload.json</code> for one run, read the file, then wire the real adapter " +
      "against the field name you actually saw.",
  ),
];

export const AGENTS: Agent[] = [
  {
    slug: "claude-code",
    name: "Claude Code",
    vendor: "Anthropic",
    tier: "rewrite",
    tagline: "Native PreToolUse rewrite — tman ships the hook, no adapter needed.",
    title: "tman + Claude Code: supervise every agent test run",
    description:
      "Wire tman into Claude Code's PreToolUse hook so bare npm test and go test commands run supervised — stall backstop, memory cull, orphan reaping.",
    configPath: ".claude/settings.json",
    hookEvent: "PreToolUse",
    blocks: [
      p(
        "Claude Code is the integration tman was built against, and the only one that needs no adapter: " +
          "<code>tman hook pretooluse</code> speaks its PreToolUse contract directly. Register it once and " +
          "every bare <code>npm test</code> the agent issues comes back rewritten as a supervised run.",
      ),
      ...adopt(),
      h("2. Register the PreToolUse hook"),
      p(
        "Project scope is <code>.claude/settings.json</code>; user scope is " +
          "<code>~/.claude/settings.json</code> and covers every repository at once. The matcher is the " +
          "Bash tool, because that is the only tool that can start a process.",
      ),
      code(
        `{
  "hooks": {
    "PreToolUse": [
      {
        "matcher": "Bash",
        "hooks": [{ "type": "command", "command": "tman hook pretooluse" }]
      }
    ]
  }
}`,
        ".claude/settings.json",
        "json",
      ),
      h("What the hook does to each command"),
      table(
        ["the agent runs", "what happens"],
        [
          [
            "<code>go test ./...</code> in a project with <code>.tman.kdl</code>",
            "rewritten to <code>/abs/path/to/tman run -- go test ./...</code>, and the rewrite is announced in the transcript",
          ],
          [
            "<code>go test ./...</code> with no <code>.tman.kdl</code>",
            "runs unchanged; the model is told it was unsupervised and how to adopt",
          ],
          [
            "<code>tman test</code>, or anything inside a supervised tree",
            "untouched — <code>TMAN_RUN_ID</code> is set, so this is the same work as its parent and gets no second supervisor",
          ],
          [
            "<code>cd app &amp;&amp; npm test</code>, <code>CI=1 npm test</code>",
            "runs unchanged, with a note. The hook does not parse shell and will not prefix a string it did not parse",
          ],
          ["<code>git status</code>, <code>npm run dev</code>", "untouched"],
          [
            "any of the above, when tman cannot prove which binary it is running as",
            "runs unchanged, with a note naming the path it refused to use",
          ],
        ],
      ),
      note(
        "<strong>It cannot block you.</strong> Exit code 2 is the only status Claude Code treats as a block, " +
          "and this hook never returns it. A missing binary, a malformed request, an unreadable project: every " +
          "failure path leaves the command exactly as written. Losing supervision costs you a backstop; " +
          "it never costs you the build.",
      ),
      h("Why the rewrite names an absolute path"),
      p(
        "The rewritten command starts with the running binary's full path, not the word <code>tman</code>. " +
          "The Bash tool resolves a program name against its own PATH, which frequently does not include " +
          "<code>~/.local/bin</code> or the npm bin directory tman installs into — so a bare name would turn " +
          "a working build into <code>command not found</code>.",
      ),
      p(
        "That path has to be <em>proven</em> to be tman before it is emitted: fully qualified, named as tman " +
          "ships, and present on disk. Launched as <code>dotnet tman.dll</code>, the running process is the " +
          "shared dotnet host — rewriting to it would hand the agent <code>dotnet run -- go test ./...</code>, " +
          "which in a directory holding a <code>.csproj</code> builds and launches an unrelated application. " +
          "When tman cannot prove what it is about to name, it warns and changes nothing.",
      ),
      ...instructionFile("CLAUDE.md"),
      ...verify([
        p(
          "Inside Claude Code, <code>/hooks</code> lists what is registered, and a supervised run announces " +
            "itself in the transcript — if you see the command run with no such line, the hook is not firing.",
        ),
      ]),
      ...caps(),
    ],
    faqs: [
      {
        q: "Does the tman hook ever block Claude Code from running a command?",
        a: "No. Exit code 2 is the only status Claude Code treats as a block, and the hook never returns it. Every failure path — missing binary, malformed request, unreadable project — leaves the command exactly as written, so an uninstalled tman costs supervision but never the build.",
      },
      {
        q: "Why does the hook skip commands like `cd app && npm test`?",
        a: "The hook does not parse shell. Prefixing a chained or environment-prefixed string would change what runs — `tman run -- cd app && npm test` supervises the `cd`, not the test. Rather than guess, tman leaves the command untouched and tells the model it was unsupervised.",
      },
      {
        q: "Do I still need PATH shims if the hook is registered?",
        a: "They cover different paths. The hook catches commands the agent issues through its Bash tool; the shims catch commands that go through a shell lookup, including ones you type yourself and ones a script invokes. Running both costs nothing: a command already inside a supervised tree sets TMAN_RUN_ID and is never supervised twice.",
      },
    ],
    sources: [
      { label: "Claude Code hooks reference", url: "https://docs.claude.com/en/docs/claude-code/hooks" },
      { label: "tman README — Claude Code hook", url: "https://github.com/standardbeagle/tman#claude-code-hook" },
    ],
  },

  {
    slug: "codex-cli",
    name: "Codex CLI",
    vendor: "OpenAI",
    tier: "rewrite",
    tagline: "Same PreToolUse payload as Claude Code — the shipped hook drops straight in.",
    title: "tman + Codex CLI: hooks for supervised test runs",
    description:
      "Wire tman into OpenAI Codex CLI's PreToolUse hook. The shipped hook drops straight in — config, verification, and the one field to check first.",
    configPath: "~/.codex/hooks.json or [hooks] in config.toml",
    hookEvent: "PreToolUse",
    blocks: [
      p(
        "Codex CLI's hook system uses the same event taxonomy and the same payload field names as Claude " +
          "Code: <code>PreToolUse</code>, a <code>tool_name</code> of <code>Bash</code> for the shell tool, and " +
          "the command at <code>tool_input.command</code>. That is exactly what <code>tman hook pretooluse</code> " +
          "reads, so the shipped hook works here with no adapter.",
      ),
      note(
        "Codex hooks are a comparatively recent, opt-in surface and are <strong>not available on Windows</strong>. " +
          "On Windows, and on any version where hooks are disabled, fall back to the PATH shims from step 1 — " +
          "they are the mechanism that does not depend on a hook surface at all.",
      ),
      ...adopt(),
      h("2. Register the PreToolUse hook"),
      p(
        "Hooks load from a <code>hooks.json</code> beside an active config layer, or from an inline " +
          "<code>[hooks]</code> table in <code>config.toml</code>. Either form works; the JSON one is easier " +
          "to keep identical to your Claude Code setup.",
      ),
      code(
        `{
  "hooks": {
    "PreToolUse": [
      {
        "matcher": "Bash",
        "hooks": [
          {
            "type": "command",
            "command": "tman hook pretooluse",
            "statusMessage": "routing test commands through tman",
            "timeout": 30
          }
        ]
      }
    ]
  }
}`,
        "~/.codex/hooks.json",
        "json",
      ),
      code(
        `[[hooks.PreToolUse]]
matcher = "Bash"

  [[hooks.PreToolUse.hooks]]
  type = "command"
  command = "tman hook pretooluse"
  timeout = 30`,
        "~/.codex/config.toml — the equivalent inline form",
        "toml",
      ),
      p(
        "Set this at user scope. Codex treats provider, notification, and telemetry keys in a project-local " +
          "<code>.codex/config.toml</code> as untrusted and warns at startup; keeping the hook in " +
          "<code>~/.codex/</code> avoids that class of surprise entirely. Non-managed hooks also require an " +
          "explicit trust review the first time they run, so expect one prompt.",
      ),
      h("The one field to check on your version"),
      p(
        "Codex documents a rewrite as <code>permissionDecision: \"allow\"</code> alongside " +
          "<code>updatedInput</code>. tman emits <code>updatedInput</code> without the accompanying " +
          "<code>permissionDecision</code>, because that is what the Claude Code contract asks for. Whether " +
          "your Codex build applies the replacement anyway is the single thing worth confirming before you " +
          "rely on this — and it is a one-command check:",
      ),
      code(
        `cd your-project
codex exec 'run the test suite'   # then read the transcript

# the command it ran should start with an absolute path to tman.
# if it ran bare, the shims from step 1 are still supervising shell lookups —
# and you can close the gap with the deny adapter from the Cursor guide.`,
        "shell",
      ),
      ...instructionFile("AGENTS.md"),
      ...verify(),
      ...caps(),
    ],
    faqs: [
      {
        q: "Does Codex CLI name its shell tool `Bash` or `shell`?",
        a: "`Bash`. Codex's PreToolUse hook intercepts the shell tool under the tool name `Bash`, matching Claude Code's naming, which is why tman's shipped hook — which requires exactly that name — works unmodified.",
      },
      {
        q: "Codex hooks are not available on Windows. What supervises there?",
        a: "The PATH shims written by `tman init --shims`. They catch any command that goes through a shell lookup, which covers everything you type and everything a script invokes, and they do not depend on a hook surface existing.",
      },
    ],
    sources: [
      { label: "Codex hooks documentation", url: "https://developers.openai.com/codex/hooks" },
      { label: "Codex configuration reference", url: "https://github.com/openai/codex/blob/main/docs/config.md" },
    ],
  },

  {
    slug: "gemini-cli",
    name: "Gemini CLI",
    vendor: "Google",
    tier: "rewrite",
    tagline: "BeforeTool hook merges a replacement command into the tool arguments.",
    title: "tman + Gemini CLI: BeforeTool supervised test runs",
    description:
      "Wire tman into Gemini CLI's BeforeTool hook so run_shell_command test calls run supervised, with a wrapper for the tool_input contract.",
    configPath: ".gemini/settings.json",
    hookEvent: "BeforeTool",
    blocks: [
      p(
        "Gemini CLI's pre-execution event is <code>BeforeTool</code>, and it <em>can</em> replace a command: " +
          "the object at <code>hookSpecificOutput.tool_input</code> merges with and overrides the model's " +
          "arguments before the tool runs. The names differ from tman's native output, so this integration " +
          "needs a short wrapper — but the capability is the same one Claude Code and Codex have.",
      ),
      ...adopt(),
      h("2. Add the BeforeTool wrapper"),
      p(
        "The wrapper does two translations: Gemini's tool name and argument shape on the way in, and " +
          "<code>updatedInput</code> to <code>tool_input</code> on the way out. It reuses tman's classifier " +
          "rather than reimplementing it, so which commands count as supervisable stays in one place.",
      ),
      code(
        `#!/usr/bin/env bash
# tman-gemini-hook.sh — adapt tman's PreToolUse output to Gemini's BeforeTool contract.
set -euo pipefail
payload=$(cat)

command=$(jq -r '.tool_input.command // empty' <<<"$payload")
cwd=$(jq -r '.cwd // empty' <<<"$payload")
[ -n "$command" ] || exit 0

verdict=$(jq -nc --arg c "$command" --arg d "\${cwd:-$PWD}" \\
  '{tool_name:"Bash", cwd:$d, tool_input:{command:$c}}' | tman hook pretooluse)
[ -n "$verdict" ] || exit 0

supervised=$(jq -r '.hookSpecificOutput.updatedInput.command // empty' <<<"$verdict")
if [ -n "$supervised" ]; then
  jq -nc --arg c "$supervised" --arg m "$(jq -r '.systemMessage // ""' <<<"$verdict")" \\
    '{systemMessage:$m, hookSpecificOutput:{tool_input:{command:$c}}}'
else
  # advice only — pass the note to the model, change nothing.
  jq -c '{systemMessage: (.hookSpecificOutput.additionalContext // "")}' <<<"$verdict"
fi`,
        "~/.gemini/tman-gemini-hook.sh — chmod +x",
        "bash",
      ),
      h("3. Register it against the shell tool"),
      p(
        "For <code>BeforeTool</code>, the matcher is compared against the name of the tool being executed, " +
          "and it is a regular expression. Gemini's shell tool is <code>run_shell_command</code>.",
      ),
      code(
        `{
  "hooks": {
    "BeforeTool": [
      {
        "matcher": "run_shell_command",
        "hooks": [
          {
            "type": "command",
            "name": "tman supervision",
            "command": "~/.gemini/tman-gemini-hook.sh",
            "timeout": 30000
          }
        ]
      }
    ]
  }
}`,
        ".gemini/settings.json (or ~/.gemini/settings.json for every project)",
        "json",
      ),
      note(
        "The timeout is in <strong>milliseconds</strong> here — the default is 60000. A value of " +
          "<code>30</code>, copied from a Codex or Claude config where the unit is seconds, gives the hook " +
          "30ms and it will appear to do nothing at all.",
      ),
      ...instructionFile("GEMINI.md"),
      ...verify([
        p(
          "Run the wrapper by hand first — it is a plain filter, so it needs no agent to test: " +
            "<code>echo '{\"tool_input\":{\"command\":\"npm test\"},\"cwd\":\"'\"$PWD\"'\"}' | ~/.gemini/tman-gemini-hook.sh</code>. " +
            "It should print an object containing <code>hookSpecificOutput.tool_input.command</code>.",
        ),
      ]),
      ...caps(),
    ],
    faqs: [
      {
        q: "Why does Gemini CLI need a wrapper when Codex does not?",
        a: "Codex uses the same field names as Claude Code — `updatedInput` under `hookSpecificOutput`. Gemini merges a `tool_input` object instead, and matches on `run_shell_command` rather than `Bash`. The capability is identical; only the names differ, so the wrapper is a translation and not a second classifier.",
      },
      {
        q: "Is the hook timeout in seconds or milliseconds?",
        a: "Milliseconds, with a default of 60000. This differs from Codex and Claude Code, where hook timeouts are seconds — a config copied across without changing the unit gives the hook 30ms and it silently never completes.",
      },
    ],
    sources: [
      {
        label: "Gemini CLI hooks reference",
        url: "https://github.com/google-gemini/gemini-cli/blob/main/docs/hooks/reference.md",
      },
      { label: "Gemini CLI settings", url: "https://geminicli.com/docs/cli/settings/" },
    ],
  },

  {
    slug: "opencode",
    name: "opencode",
    vendor: "open source",
    tier: "rewrite",
    tagline: "A plugin mutates the bash tool's arguments in tool.execute.before.",
    title: "tman + opencode: plugin hook for supervised tests",
    description:
      "Wire tman into opencode with a tool.execute.before plugin that rewrites bare test and build commands into supervised runs. Project or global.",
    configPath: ".opencode/plugin/ or ~/.config/opencode/plugin/",
    hookEvent: "tool.execute.before",
    blocks: [
      p(
        "opencode's extension point is a plugin rather than a config-declared shell hook. That is more " +
          "direct, not less: <code>tool.execute.before</code> receives the tool's arguments as a mutable " +
          "object, so assigning to <code>output.args.command</code> replaces what runs.",
      ),
      ...adopt(),
      h("2. Drop in the plugin"),
      p(
        "Project scope is <code>.opencode/plugin/</code>; global scope is " +
          "<code>~/.config/opencode/plugin/</code>. The plugin shells out to <code>tman hook pretooluse</code> " +
          "so the decision about which commands deserve supervision stays in tman.",
      ),
      code(
        `import { execFileSync } from "node:child_process";

/**
 * Route bare test and build commands through tman.
 *
 * tman owns the classification: which commands count, whether this project is adopted, and whether
 * the supervising binary can be named safely. Empty output means "leave it exactly as written",
 * which is also what every failure here degrades to.
 */
export const TmanSupervision = async () => ({
  "tool.execute.before": async (input, output) => {
    if (input.tool !== "bash") return;
    const command = output.args?.command;
    if (typeof command !== "string" || !command) return;

    let verdict;
    try {
      verdict = execFileSync("tman", ["hook", "pretooluse"], {
        input: JSON.stringify({
          tool_name: "Bash",
          cwd: process.cwd(),
          tool_input: { command },
        }),
        encoding: "utf8",
        timeout: 5000,
      });
    } catch {
      return; // tman missing or unhappy: never cost the user their build
    }

    const supervised = verdict && JSON.parse(verdict)?.hookSpecificOutput?.updatedInput?.command;
    if (supervised) output.args.command = supervised;
  },
});`,
        ".opencode/plugin/tman.js",
        "javascript",
      ),
      note(
        "<strong>Subagent tool calls have historically bypassed plugin hooks.</strong> If your workflow " +
          "delegates test runs to subagents, treat the plugin as a convenience and the PATH shims from step 1 " +
          "as the mechanism you actually depend on — the shims sit below the agent entirely and cannot be " +
          "routed around.",
      ),
      ...instructionFile("AGENTS.md"),
      ...verify(),
      ...caps(),
    ],
    faqs: [
      {
        q: "Where do opencode plugins live?",
        a: "`.opencode/plugin/` for a single project, `~/.config/opencode/plugin/` globally. Both are loaded, and all hooks from all plugins run in sequence, so a global tman plugin and a project-specific one coexist.",
      },
      {
        q: "Why shell out to tman instead of matching commands in the plugin?",
        a: "So there is one classifier. Which commands count as test or build work, whether the project has been adopted, and whether the supervising binary can be safely named are decisions tman already makes and tests; a second copy in JavaScript is a second thing to keep in sync and a second place for it to drift.",
      },
    ],
    sources: [
      { label: "opencode plugins documentation", url: "https://open-code.ai/en/docs/plugins" },
    ],
  },

  {
    slug: "cursor",
    name: "Cursor",
    vendor: "Anysphere",
    tier: "gate",
    tagline: "beforeShellExecution can allow, ask, or deny — but not rewrite.",
    title: "tman + Cursor: gating agent shell commands",
    description:
      "Cursor's beforeShellExecution hook blocks but cannot rewrite. Use tman's PATH shims plus a deny adapter naming the supervised command to re-issue.",
    configPath: "hooks.json",
    hookEvent: "beforeShellExecution",
    blocks: [
      p(
        "Cursor fires <code>beforeShellExecution</code> before the agent runs any shell command, and the " +
          "response controls permission — allow, ask, or deny, with messages for the user and for the model. " +
          "It does not carry a replacement command. So supervision here is two mechanisms working together: " +
          "shims that catch the command silently, and a denial that catches what the shims cannot.",
      ),
      ...adopt(),
      p(
        "For Cursor the shims are the load-bearing half. A shell command the agent issues resolves " +
          "<code>./test</code> through the repository root, and that file is already a supervised entry point — " +
          "no hook involved.",
      ),
      ...denyAdapter(".command // .tool_input.command // empty"),
      h("Register the hook"),
      code(
        `{
  "version": 1,
  "hooks": {
    "beforeShellExecution": [
      { "command": "~/.config/tman-gate.sh" }
    ]
  }
}`,
        "hooks.json",
        "json",
      ),
      note(
        "A deny is a real interruption, unlike a rewrite. Keep the adapter narrow — it only denies commands " +
          "tman itself classifies as supervisable test or build work in an adopted project — and prefer " +
          '<code>"ask"</code> over <code>"deny"</code> while you are still calibrating, so a false positive ' +
          "costs a keystroke rather than a stalled agent.",
      ),
      ...instructionFile("AGENTS.md"),
      ...verify(),
      ...caps(),
    ],
    faqs: [
      {
        q: "Can Cursor's hooks rewrite a shell command before it runs?",
        a: "No. `beforeShellExecution` returns a permission decision — allow, ask, or deny — plus optional messages for the user and the agent. Getting a command supervised therefore takes either a PATH shim, which the agent resolves without knowing, or a denial that names the supervised form for the model to re-issue.",
      },
      {
        q: "Will the deny adapter block ordinary commands?",
        a: "Only what tman classifies as test or build work in a project that has a .tman.kdl. Anything else — git, a dev server, a one-off script — produces no verdict from `tman hook pretooluse`, and the adapter exits without printing, which Cursor reads as allow.",
      },
    ],
    sources: [
      { label: "Cursor hooks", url: "https://cursor.com/docs/agent/hooks" },
      { label: "Cursor 1.7 hooks announcement", url: "https://www.infoq.com/news/2025/10/cursor-hooks/" },
    ],
  },

  {
    slug: "antigravity",
    name: "Antigravity",
    vendor: "Google",
    tier: "gate",
    tagline: "PreToolUse gates run_command; hooks receive the call but cannot modify it.",
    title: "tman + Google Antigravity: hooks, shims, rules",
    description:
      "Antigravity's PreToolUse hook gates run_command but cannot rewrite it. Set up tman with PATH shims, a deny adapter, and rules under .agents/.",
    configPath: ".agents/hooks.json or ~/.gemini/config/hooks.json",
    hookEvent: "PreToolUse",
    blocks: [
      p(
        "Antigravity's hook surface uses Claude-style event names — <code>PreToolUse</code>, " +
          "<code>PostToolUse</code>, <code>Stop</code> — but the pre-tool contract is a gate, not a rewrite. " +
          "The hook receives <code>toolCall.name</code> and <code>toolCall.args</code> and answers with a " +
          "<code>decision</code> of <code>allow</code>, <code>deny</code>, <code>ask</code>, or " +
          "<code>force_ask</code>. Tools like <code>run_command</code> can be permitted or blocked, not " +
          "rewritten.",
      ),
      ...adopt(),
      h("2. Wire the hook"),
      p(
        "<code>hooks.json</code> lives in your customization directory — <code>.agents/</code> in the " +
          "workspace, or <code>~/.gemini/config/</code> globally. Note the enable flag: a hook block ships " +
          "disabled and stays inert until you flip it, which is a common reason a correct config appears to " +
          "do nothing.",
      ),
      code(
        `{
  "tman-supervision": {
    "enabled": true,
    "PreToolUse": [
      {
        "matcher": "run_command",
        "hooks": [{ "type": "command", "command": "~/.config/tman-gate.sh" }]
      }
    ]
  }
}`,
        ".agents/hooks.json",
        "json",
      ),
      ...denyAdapter(".toolCall.args.command // empty"),
      p(
        "Antigravity's decision field is <code>decision</code> rather than " +
          "<code>permissionDecision</code>, so change the adapter's final <code>jq</code> to emit " +
          "<code>{decision:\"deny\", reason: …}</code>. Everything above it — reading the command, asking " +
          "tman what the supervised form is — is unchanged.",
      ),
      h("3. Standing rules under .agents/"),
      p(
        "Antigravity reads <code>AGENTS.md</code> at the workspace root, with workspace rules under " +
          "<code>.agents/rules/</code> and global ones in <code>~/.gemini/GEMINI.md</code>. A supervision rule " +
          "belongs in the workspace copy, since the caps it references are the ones in this repository's " +
          "<code>.tman.kdl</code>.",
      ),
      ...instructionFile("AGENTS.md"),
      ...verify(),
      ...caps(),
    ],
    faqs: [
      {
        q: "Can an Antigravity PreToolUse hook change the command that runs?",
        a: "No. The hook receives toolCall.name and toolCall.args and returns a decision of allow, deny, ask, or force_ask. run_command can be permitted or blocked but not rewritten, so supervision comes from PATH shims plus a denial the model can act on.",
      },
      {
        q: "My hooks.json looks right but nothing happens.",
        a: "Check the `enabled` flag on the hook block — it defaults to false, so a correctly-shaped config stays inert until you set it true. Then confirm the file is in a directory Antigravity actually reads: `.agents/` in the workspace, or `~/.gemini/config/` globally.",
      },
    ],
    sources: [
      { label: "Antigravity hooks", url: "https://antigravity.google/docs/hooks" },
      { label: "Antigravity plugins and skills", url: "https://antigravity.google/docs/cli/plugins" },
    ],
  },

  {
    slug: "kimi",
    name: "Kimi Code CLI",
    vendor: "Moonshot AI",
    tier: "gate",
    tagline: "PreToolUse hooks in config.toml; rewriting is undocumented, so shim first.",
    title: "tman + Kimi Code CLI: hooks and PATH shims",
    description:
      "Set up tman under Moonshot's Kimi Code CLI: PATH shims for reliable supervision, a PreToolUse hook in config.toml, and an AGENTS.md rule.",
    configPath: "~/.kimi-code/config.toml",
    hookEvent: "PreToolUse",
    blocks: [
      p(
        "Kimi Code CLI supports lifecycle hooks that run local commands to gate risky tool calls, declared " +
          "as <code>[[hooks]]</code> entries in <code>config.toml</code>. What is <em>not</em> documented is " +
          "whether a <code>PreToolUse</code> hook can rewrite the tool's arguments — so this guide treats it " +
          "as a gate, and puts the weight on the shims, which do not depend on the answer.",
      ),
      ...adopt(),
      h("2. Register the hook"),
      p(
        "Config lives under <code>~/.kimi-code/</code> by default, relocatable with " +
          "<code>KIMI_CODE_HOME</code>; a project-local <code>.kimi-code/local.toml</code> carries workspace " +
          "settings. The hook timeout here is in seconds.",
      ),
      code(
        `[[hooks]]
event = "PreToolUse"
matcher = "Bash"
command = "~/.config/tman-gate.sh"
timeout = 10`,
        "~/.kimi-code/config.toml",
        "toml",
      ),
      ...denyAdapter(".tool_input.command // .toolArgs.command // empty"),
      h("Find out whether your build rewrites"),
      p(
        "It is worth five minutes to learn which behaviour you have, because a rewrite is strictly better " +
          "than a denial. Log one payload, then try emitting a rewrite and see whether the command changes:",
      ),
      code(
        `# 1. capture a real payload
cat > /tmp/kimi-payload.json    # as the hook command, for one run
jq . /tmp/kimi-payload.json     # read the field names you actually got

# 2. if the field names match Claude Code's, try the native hook directly
#    (tman requires tool_name == "Bash" and tool_input.command)
tman hook pretooluse < /tmp/kimi-payload.json`,
        "shell",
      ),
      p(
        "If the native hook prints a rewrite object and Kimi honours it, drop the adapter and point the hook " +
          "straight at <code>tman hook pretooluse</code>. If it does not, the adapter and the shims are still " +
          "doing the work.",
      ),
      ...instructionFile("AGENTS.md"),
      ...verify(),
      ...caps(),
    ],
    faqs: [
      {
        q: "Where does Kimi Code CLI keep its configuration?",
        a: "`~/.kimi-code/` by default — `config.toml` for agent and runtime settings, `tui.toml` for the terminal UI — relocatable by setting KIMI_CODE_HOME. Workspace-specific settings go in `<project>/.kimi-code/local.toml`.",
      },
      {
        q: "Can a Kimi PreToolUse hook rewrite the command?",
        a: "The documentation describes hooks as gating risky tool calls and does not state that arguments can be modified. Treat it as a gate until you have confirmed otherwise on your build, and rely on the PATH shims — which sit below the agent — for supervision that does not depend on the answer.",
      },
    ],
    sources: [
      { label: "Kimi Code CLI configuration", url: "https://moonshotai.github.io/kimi-code/en/configuration/config-files" },
      { label: "Kimi Code CLI", url: "https://github.com/MoonshotAI/kimi-code" },
    ],
  },

  {
    slug: "copilot-cli",
    name: "GitHub Copilot CLI",
    vendor: "GitHub",
    tier: "gate",
    tagline: "preToolUse allows or denies only; policy file lives in .github/hooks/.",
    title: "tman + GitHub Copilot CLI: hooks policy setup",
    description:
      "Copilot CLI's preToolUse hook denies but cannot modify a command. Set up tman with PATH shims, a committed hooks policy, and AGENTS.md instructions.",
    configPath: ".github/hooks/copilot-cli-policy.json",
    hookEvent: "preToolUse",
    blocks: [
      p(
        "Copilot CLI exposes three hook events — <code>sessionStart</code>, " +
          "<code>userPromptSubmitted</code>, and <code>preToolUse</code> — and the pre-tool one is explicitly " +
          "a policy gate: it can allow execution by doing nothing, or deny it by returning a structured " +
          "response. Commands cannot be rewritten. The policy file lives in the repository, which makes this " +
          "the easiest of the gate-tier integrations to apply to a whole team at once.",
      ),
      ...adopt(),
      h("2. Commit the hooks policy"),
      p(
        "Because <code>.github/hooks/copilot-cli-policy.json</code> is a checked-in file, adopting tman " +
          "and committing this policy in the same change means every clone of the repository gets the same " +
          "supervision. Note the payload quirk: <code>toolArgs</code> arrives as a JSON <em>string</em> and " +
          "has to be parsed before you can read the command out of it.",
      ),
      code(
        `{
  "version": 1,
  "hooks": {
    "preToolUse": [
      {
        "type": "command",
        "bash": "~/.config/tman-gate.sh",
        "timeoutSec": 10
      }
    ]
  }
}`,
        ".github/hooks/copilot-cli-policy.json",
        "json",
      ),
      ...denyAdapter('(.toolArgs | if type == "string" then fromjson else . end).command // empty'),
      p(
        "Copilot's denial shape is <code>{\"permissionDecision\":\"deny\",\"permissionDecisionReason\":\"…\"}</code>, " +
          "which is exactly what the adapter above emits — no change needed to its final <code>jq</code>.",
      ),
      h("3. Custom instructions"),
      p(
        "Copilot CLI reads <code>AGENTS.md</code>, and also <code>.github/copilot-instructions.md</code>, " +
          "<code>.github/instructions/**.instructions.md</code>, <code>CLAUDE.md</code>, and " +
          "<code>GEMINI.md</code>. If you already keep a shared <code>AGENTS.md</code> for other agents, one " +
          "copy covers Copilot too.",
      ),
      ...instructionFile("AGENTS.md"),
      ...verify(),
      ...caps(),
    ],
    faqs: [
      {
        q: "Can a Copilot CLI hook modify the command it inspects?",
        a: "No. The preToolUse hook allows execution by doing nothing, or denies it by returning {\"permissionDecision\":\"deny\",\"permissionDecisionReason\":\"…\"}. There is no field for a replacement command, so supervision comes from PATH shims plus a denial naming the supervised form.",
      },
      {
        q: "Why does my hook see toolArgs as a string?",
        a: "That is the documented shape: `toolArgs` arrives JSON-encoded inside the payload and needs a second parse. In jq, `(.toolArgs | if type == \"string\" then fromjson else . end).command` handles both forms, which keeps the adapter working if the encoding changes.",
      },
    ],
    sources: [
      { label: "Copilot CLI hooks", url: "https://docs.github.com/en/copilot/tutorials/copilot-cli-hooks" },
      {
        label: "Copilot CLI custom instructions",
        url: "https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-custom-instructions",
      },
    ],
  },

  {
    slug: "other-agents",
    name: "Any other agent",
    vendor: "Aider, Amp, Windsurf, Zed, CI runners",
    tier: "shim",
    tagline: "PATH shims plus an AGENTS.md rule — no hook surface required.",
    title: "tman with any AI agent: shims, aliases, AGENTS.md",
    description:
      "The hook-free setup for Aider, Amp, Windsurf, Zed, and CI runners: PATH shims, .tman.kdl aliases, and one instruction-file rule.",
    configPath: ".tman.kdl + AGENTS.md",
    hookEvent: null,
    blocks: [
      p(
        "Hooks are a convenience. The mechanism underneath them — a supervised entry point that any shell " +
          "lookup resolves to — needs nothing from the agent at all, which is why it is the right default for " +
          "Aider, Amp, Windsurf, Zed, a CI runner, or a tool that shipped after this page was written.",
      ),
      ...adopt(),
      h("What the shims actually do"),
      p(
        "<code>tman init --shims</code> writes small executables at the repository root — <code>./test</code>, " +
          "<code>./lint</code>, and one per alias it detected. Each invokes tman with that alias, so the caps " +
          "in <code>.tman.kdl</code> apply without anyone having to remember them. Anything that runs " +
          "<code>./test</code> is supervised: you at a prompt, a Makefile, a CI step, or an agent that shells out.",
      ),
      note(
        "<strong>Never hand a bare relative <code>argv[0]</code> like <code>./test</code> to an exec that " +
          "does not go through a shell.</strong> A relative <code>argv[0]</code> is resolved through PATH, not " +
          "the working directory, and will silently find some unrelated program of the same name. From a " +
          "workflow step or a language-level <code>exec</code>, write <code>sh -c \"exec ./test\"</code>.",
      ),
      h("Aliases carry the caps"),
      p(
        "The point of an alias is that the caps live with the command instead of in whoever's memory. " +
          "A per-alias block overrides the <code>defaults</code>, and a CLI flag overrides both.",
      ),
      code(
        `defaults {
    stall "30m"       // hang backstop, not a runtime budget
    max-parallel 2
    retain "24h"
}

alias "test" {
    command "npm"
    args "run" "test"
    max-time "15m"
}

alias "e2e" {
    command "pytest"
    args "tests/e2e" "--tb=short"
    max-time "30m"
    max-mem 4096
    max-parallel 1     // binds a port; two copies fight
}`,
        ".tman.kdl",
        "kdl",
      ),
      ...instructionFile("AGENTS.md"),
      p(
        "<code>AGENTS.md</code> is read by Aider, Amp, Copilot CLI, Codex, Antigravity, and most things " +
          "shipped since — and Copilot CLI and Antigravity additionally read <code>CLAUDE.md</code> and " +
          "<code>GEMINI.md</code>. One file at the repository root usually covers your whole fleet.",
      ),
      h("If your agent gains a hook later"),
      p(
        "Check whether its pre-tool event can <em>replace</em> the command or only allow and deny it. If it " +
          "replaces, point it straight at <code>tman hook pretooluse</code> and see whether the field names " +
          `line up — the <a href="${href("/setup/codex-cli")}">Codex</a> and ` +
          `<a href="${href("/setup/gemini-cli")}">Gemini CLI</a> guides cover both outcomes. If it only ` +
          `gates, the adapter in the <a href="${href("/setup/cursor")}">Cursor</a> guide is the pattern.`,
      ),
      ...verify(),
      ...caps(),
    ],
    faqs: [
      {
        q: "Does tman work with an agent that has no hook system?",
        a: "Yes, and this is the primary mechanism rather than a fallback. `tman init --shims` writes supervised entry points at the repository root that any shell lookup resolves, so anything that shells out — an agent, a Makefile, a CI step, or you — runs supervised without the agent knowing tman exists.",
      },
      {
        q: "Why does `./test` fail from my CI step when it works in a terminal?",
        a: "A relative argv[0] is resolved through PATH, not the working directory. A workflow step or a language-level exec that is handed `./test` directly will look for it on PATH and often find an unrelated program of the same name. Write `sh -c \"exec ./test\"` so a shell does the resolution.",
      },
      {
        q: "Can one AGENTS.md cover several different agents?",
        a: "Usually. AGENTS.md is read by Aider, Amp, Codex, Copilot CLI, and Antigravity among others, and several of those also read CLAUDE.md and GEMINI.md — so a single file at the repository root, or one file plus symlinks, generally covers a mixed fleet.",
      },
    ],
    sources: [
      { label: "tman README", url: "https://github.com/standardbeagle/tman#readme" },
      { label: "AGENTS.md convention", url: "https://agents.md/" },
    ],
  },
];

export const agentBySlug = (slug: string) => AGENTS.find((a) => a.slug === slug);
