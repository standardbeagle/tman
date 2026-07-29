import { type Block, type Faq, type Source, h, p, note, code, table } from "../lib/blocks";
import { href } from "../lib/site";

export type Category = "test" | "e2e" | "lint" | "build";

export const CATEGORIES: Record<Category, string> = {
  test: "Unit and integration test runners",
  e2e: "Browser and end-to-end suites",
  lint: "Linters, formatters, and typecheckers",
  build: "Compilers and bundlers",
};

/**
 * The caps recommended for one tool, as they would be written in `.tman.kdl`.
 *
 * These are **starting points derived from how the tool behaves**, not measurements of your
 * machine. Every guide says so in its own words, because a table of numbers presented without that
 * qualifier is the kind of thing people paste into a config and then debug for an afternoon.
 */
export interface Caps {
  stall?: string;
  maxTime?: string;
  maxMem?: string;
  maxCpu?: string;
  maxParallel?: number;
  /** Set when the command must never be given a wall-clock limit — watchers, dev servers. */
  neverMaxTime?: boolean;
}

export interface Tool {
  slug: string;
  name: string;
  category: Category;
  ecosystem: string;
  tagline: string;
  title: string;
  description: string;
  caps: Caps;
  /** The `.tman.kdl` alias, ready to paste. */
  kdl: string;
  blocks: Block[];
  faqs: Faq[];
  sources: Source[];
}

const capRow = (t: Tool): string[] => [
  `<a href="${href(`/tuning/${t.slug}`)}">${t.name}</a>`,
  t.caps.stall ?? "—",
  t.caps.neverMaxTime ? "<em>never</em>" : (t.caps.maxTime ?? "—"),
  t.caps.maxMem ?? "—",
  t.caps.maxParallel !== undefined ? String(t.caps.maxParallel) : "—",
];

export const capsTable = (tools: Tool[]): Block =>
  table(
    ["tool", "<code>stall</code>", "<code>max-time</code>", "<code>max-mem</code>", "<code>max-parallel</code>"],
    tools.map(capRow),
  );

/** Shared closer: how to turn a starting point into a number that means something. */
const calibrate = (): Block[] => [
  h("Turn the starting point into a real number"),
  p(
    "Everything above is derived from how this tool behaves, not from your machine. The numbers that " +
      "matter come from your own runs, and getting them takes one command:",
  ),
  code(
    `tman status --json | jq '.runs[] | {name, exit, duration_ms}'`,
    "shell",
  ),
  p(
    "Set <code>max-time</code> from the slowest <em>successful</em> run you are willing to wait for, with " +
      "real headroom — a cap that fires on healthy work teaches everyone to raise the cap, and after a " +
      "few rounds of that it bounds nothing. Set <code>stall</code> from the longest stretch the command " +
      "legitimately produces no output and no CPU, then double it.",
  ),
  note(
    "<strong>Exit 124, 125, and 126 are findings, not flakes.</strong> 124 is a wall-clock timeout, 125 a " +
      "stall, 126 a resource cull. Widening the cap that fired is the right response only after you know " +
      "why it fired — the first two times a suite trips <code>max-mem</code>, the interesting question is " +
      "what is holding the memory.",
  ),
];

/** Shared: the rule that catches the single most common misconfiguration. */
const watchWarning = (command: string): Block =>
  note(
    `<strong>Never put a wall-clock cap on <code>${command}</code>.</strong> It is designed not to exit, ` +
      "so <code>max-time</code> guarantees a kill and an exit 124 that means nothing. Use " +
      "<code>--name</code> instead: the dedup lock is what you actually want, because it stops an agent " +
      "starting a second copy on top of the first.",
  );

export const TOOLS: Tool[] = [
  {
    slug: "vitest",
    name: "Vitest",
    category: "test",
    ecosystem: "JavaScript / TypeScript",
    tagline: "Watch mode never exits; the thread pool is where memory goes.",
    title: "Tuning tman for Vitest: stall, timeout, memory",
    description:
      "Recommended tman caps for Vitest: why watch mode must never get a wall-clock limit, where the thread pool leaks, and how to size the stall backstop.",
    caps: { stall: "10m", maxTime: "15m", maxMem: "4096", maxParallel: 2 },
    kdl: `alias "test" {
    command "npx"
    args "vitest" "run"
    max-time "15m"
    max-mem 4096
}`,
    blocks: [
      p(
        "Vitest streams results as they land, so output-based stall detection sees it constantly and the " +
          "backstop rarely has to reason about anything. The two things worth configuring are the mode and " +
          "the memory ceiling.",
      ),
      h("Always `vitest run`, never bare `vitest`"),
      p(
        "Bare <code>vitest</code> enters watch mode and stays resident. Under an agent that is a durable " +
          "problem: the agent gets no exit code, moves on, and leaves a file watcher and a worker pool alive " +
          "for the rest of the session. The alias should pin <code>run</code> so the mode cannot be inherited " +
          "from a config file or a habit.",
      ),
      watchWarning("vitest --watch"),
      h("Where the memory goes"),
      p(
        "The default pool forks worker threads, and a suite that mounts a DOM per test — <code>jsdom</code> " +
          "or <code>happy-dom</code> — accumulates across a run when teardown is incomplete. A " +
          "<code>max-mem</code> of 4 GB is high enough that an ordinary suite never sees it and low enough " +
          "that a leak is caught before the machine starts swapping. On Linux the ceiling is summed across " +
          "the whole process tree, so it covers the workers, not just the parent.",
      ),
      p(
        "If you hit 126 repeatedly, cap the pool before you raise the ceiling: " +
          "<code>--pool=threads --poolOptions.threads.maxThreads=4</code> bounds the multiplier that turns a " +
          "small per-worker leak into an OOM.",
      ),
      h("Sizing the stall backstop"),
      p(
        "Vitest's quietest stretch is the first transform of a cold dependency graph — a large monorepo can " +
          "sit for a minute or two before the first test result appears. <code>10m</code> leaves an order of " +
          "magnitude of headroom over that while still catching a wedged worker in a coffee break rather " +
          "than a working day.",
      ),
      ...calibrate(),
    ],
    faqs: [
      {
        q: "What tman caps should I use for Vitest?",
        a: "Start with max-time 15m, stall 10m, max-mem 4096, max-parallel 2, and pin `vitest run` in the alias so watch mode can never be inherited. Adjust max-time from your own slowest successful run.",
      },
      {
        q: "Why does my Vitest run get killed with exit 126?",
        a: "126 is a resource cull — the process tree crossed max-mem or sustained max-cpu. With Vitest that usually means a worker pool accumulating DOM instances across tests. Cap the pool size before raising the ceiling; a ceiling raised to fit a leak stops being a ceiling.",
      },
    ],
    sources: [{ label: "Vitest CLI reference", url: "https://vitest.dev/guide/cli" }],
  },

  {
    slug: "jest",
    name: "Jest",
    category: "test",
    ecosystem: "JavaScript / TypeScript",
    tagline: "Open handles keep the process alive forever — the exact shape stall detection catches.",
    title: "Tuning tman for Jest: open handles and workers",
    description:
      "Recommended tman caps for Jest, and why the classic 'Jest did not exit' hang is exactly the shape the stall backstop catches at exit 125.",
    caps: { stall: "10m", maxTime: "20m", maxMem: "4096", maxParallel: 2 },
    kdl: `alias "test" {
    command "npx"
    args "jest" "--ci"
    max-time "20m"
    max-mem 4096
}`,
    blocks: [
      p(
        "Jest is the clearest illustration of why a stall backstop is not the same thing as a timeout. Its " +
          "signature failure is a suite that <em>passes</em>, prints its summary, and then never exits, " +
          "because an open socket or timer is holding the event loop. The process is silent and idle — " +
          "no output, no CPU, no IO — which is exactly the signature tman kills on, at exit 125.",
      ),
      note(
        "A <code>max-time</code> would also end that run, but it ends every long run the same way and tells " +
          "you nothing. Exit 125 says <em>this process was doing nothing</em>, which points straight at the " +
          "handle. Run <code>jest --detectOpenHandles</code> once after a 125 and it usually names the culprit.",
      ),
      h("Workers are the memory multiplier"),
      p(
        "Jest spawns a worker per core by default, each a full Node process with its own module registry. " +
          "On a 16-core machine that is 16 heaps, and a module-level cache that grows across test files grows " +
          "in all of them at once. <code>max-mem 4096</code> catches that; <code>--maxWorkers=50%</code> in " +
          "the alias args stops it happening.",
      ),
      p(
        "The ceiling is summed across the process tree on Linux, which is what makes it useful here — " +
          "measuring only the parent would miss every worker.",
      ),
      h("`--ci` belongs in the alias"),
      p(
        "Under an agent there is no one to answer an interactive prompt, and a snapshot written rather than " +
          "compared is a test that passed by rewriting its own expectation. <code>--ci</code> makes an " +
          "unmatched snapshot fail instead of being silently updated. That is the same principle as tman's " +
          "own scaffolding: nothing should report success without doing the work.",
      ),
      ...calibrate(),
    ],
    faqs: [
      {
        q: "What does exit 125 mean for a Jest run?",
        a: "A stall: no output and no CPU, IO, or kernel IO-wait activity for the stall window. For Jest that most often means the suite finished but an open handle — a socket, a timer, a database pool — is keeping the event loop alive. `jest --detectOpenHandles` will usually name it.",
      },
      {
        q: "Should I set max-mem for Jest?",
        a: "Yes, around 4096 MB to start. Jest runs one worker process per core by default, so a per-file leak is multiplied by the worker count. Pair it with `--maxWorkers=50%`, which reduces the multiplier rather than just catching the result.",
      },
    ],
    sources: [{ label: "Jest CLI options", url: "https://jestjs.io/docs/cli" }],
  },

  {
    slug: "pytest",
    name: "pytest",
    category: "test",
    ecosystem: "Python",
    tagline: "Chatty by default, quiet during collection; xdist workers orphan on a hard kill.",
    title: "Tuning tman for pytest: collection and xdist",
    description:
      "Recommended tman caps for pytest: sizing the stall backstop around collection, and how xdist workers interact with the orphan reaper.",
    caps: { stall: "10m", maxTime: "20m", maxMem: "4096", maxParallel: 2 },
    kdl: `alias "test" {
    command "pytest"
    args "-q" "--tb=short"
    max-time "20m"
}

alias "e2e" {
    command "pytest"
    args "tests/e2e" "--tb=short"
    max-time "30m"
    max-mem 4096
    max-parallel 1
}`,
    blocks: [
      p(
        "pytest prints a character per test, so a running suite is never quiet for long and the stall " +
          "backstop mostly sits idle. The interesting windows are before the first character and after the " +
          "last one.",
      ),
      h("Collection is the quiet stretch"),
      p(
        "Before any test runs, pytest imports every test module. On a large suite with heavy imports — a " +
          "framework, a machine-learning stack, anything that touches native libraries at import time — that " +
          "is minutes of silence. It is not idle: the CPU is busy and files are being read, so on Linux " +
          "tman sees activity and the backstop holds regardless. On macOS and Windows the tree is not walked, " +
          "so give collection-heavy suites a wider <code>stall</code> there.",
      ),
      note(
        "A session-scoped fixture that waits on a service — a container starting, a migration running — is " +
          "the case that <em>does</em> look like a hang: zero output, zero CPU, waiting on a peer that has " +
          "sent nothing. That is what <code>max-time</code> is for. Do not tighten <code>stall</code> to " +
          "bound it; <code>stall</code> cannot tell that wait apart from a dead process, and a value low " +
          "enough to catch one kills the other.",
      ),
      h("xdist and the reaper"),
      p(
        "<code>pytest -n auto</code> spawns worker processes. When the run is culled or killed, tman kills " +
          "the whole tree, so the workers go with it — and any that survive a machine suspend are reaped by " +
          "the next tman command rather than sitting on your CPU overnight. That is the case the reaper " +
          "exists for.",
      ),
      h("Anything that binds a port gets `max-parallel 1`"),
      p(
        "An end-to-end alias that starts a server, a database, or a container should hold one slot, not two. " +
          "The second copy will not fail cleanly — it will bind-conflict, or worse, share the database and " +
          "produce a failure that looks like a flaky test. Buckets are scoped per name and directory, so " +
          "<code>max-parallel 1</code> on <code>e2e</code> does not slow down <code>test</code> at all.",
      ),
      ...calibrate(),
    ],
    faqs: [
      {
        q: "What tman caps should I use for pytest?",
        a: "max-time 20m, stall 10m, max-parallel 2 for the unit suite; for an end-to-end alias that binds a port or a database, max-parallel 1 and a longer max-time. Buckets are per name and directory, so the two aliases queue independently.",
      },
      {
        q: "My pytest run is killed during collection. What do I change?",
        a: "On Linux this should not happen — collection burns CPU and reads files, both of which count as activity. On macOS and Windows tman sees only the root process, so raise `stall` for collection-heavy suites there. If it happens on Linux, the process really was idle, and the cause is a fixture waiting on something that never arrived.",
      },
    ],
    sources: [{ label: "pytest usage", url: "https://docs.pytest.org/en/stable/how-to/usage.html" }],
  },

  {
    slug: "go-test",
    name: "go test",
    category: "test",
    ecosystem: "Go",
    tagline: "Silent for minutes while compiling; keep Go's own -timeout below tman's.",
    title: "Tuning tman for go test: silence and timeouts",
    description:
      "Recommended tman caps for go test: why a cold build looks like a hang, and why Go's own -timeout should fire before tman's max-time.",
    caps: { stall: "15m", maxTime: "20m", maxParallel: 2 },
    kdl: `alias "test" {
    command "go"
    args "test" "-timeout" "10m" "./..."
    max-time "20m"
}`,
    blocks: [
      p(
        "<code>go test ./...</code> compiles every package before it runs anything, and a cold build cache " +
          "means minutes of complete silence. This is the case that motivated tman's activity-aware stall " +
          "detection: across 959 supervised runs, a 60-second stall backstop fired 32 times and caught no " +
          "actual hang — it killed <code>go build ./...</code> at 60s on work that succeeded 14 other times, " +
          "once taking 75 seconds.",
      ),
      note(
        "That is why the built-in <code>stall</code> is <strong>30m</strong> and why the guidance is to size " +
          "it well above the longest quiet stretch you ever expect. On Linux the compile is protected " +
          "regardless — CPU jiffies advance, so tman sees a busy tree even with no output. On macOS and " +
          "Windows only the root process is sampled, so the wide default is doing the work.",
      ),
      h("Let Go's timeout fire first"),
      p(
        "<code>go test -timeout</code> and tman's <code>max-time</code> answer the same question, but they " +
          "answer it very differently. Go's panics the test binary and prints a full goroutine dump naming " +
          "exactly what was blocked. tman's kills the tree and returns 124. The dump is worth far more, so " +
          "set Go's below tman's and let it win:",
      ),
      table(
        ["layer", "value", "what you get"],
        [
          ["<code>go test -timeout</code>", "10m", "goroutine dump naming the blocked test — diagnose from this"],
          ["<code>max-time</code>", "20m", "backstop for a binary too wedged to panic, or a hang before the tests start"],
          ["<code>stall</code>", "15m", "catches an idle tree; the compile is covered by activity detection on Linux"],
        ],
      ),
      p(
        "The same layering applies to <code>gotestsum</code> and to <code>-race</code>, which roughly doubles " +
          "runtime and memory — give a race build its own alias with its own <code>max-time</code> rather " +
          "than widening the ordinary one to fit it.",
      ),
      ...calibrate(),
    ],
    faqs: [
      {
        q: "Why did tman kill my go build with exit 125?",
        a: "It did not, at the current defaults. The stall backstop is 30m and on Linux a compile counts as activity because CPU jiffies advance even with no output. If you see 125 on a Go build, you are on a config carrying the old 60s stall, or on macOS or Windows where only the root process is sampled — widen `stall`.",
      },
      {
        q: "Should I set go test -timeout if tman already has max-time?",
        a: "Yes, and lower. Go's timeout panics the test binary and prints a goroutine dump naming what was blocked; tman's kills the tree and reports 124. Setting Go's below tman's means you get the diagnosis in the normal case and keep the backstop for a binary too wedged to panic.",
      },
    ],
    sources: [{ label: "go test flags", url: "https://pkg.go.dev/cmd/go#hdr-Testing_flags" }],
  },

  {
    slug: "dotnet-test",
    name: "dotnet test",
    category: "test",
    ecosystem: ".NET",
    tagline: "Restore and build are quiet; testhost processes are famous orphans.",
    title: "Tuning tman for dotnet test: testhost orphans",
    description:
      "Recommended tman caps for dotnet test: sizing around quiet restores and builds, and why lingering testhost processes are the reaper's headline case.",
    caps: { stall: "15m", maxTime: "20m", maxParallel: 2 },
    kdl: `alias "test" {
    command "dotnet"
    args "test" "tests/YourProject.Tests/YourProject.Tests.csproj"
    max-time "20m"
}`,
    blocks: [
      p(
        "A <code>dotnet test</code> invocation is three phases wearing one command: restore, build, then the " +
          "actual run. The first two are near-silent and can dominate the wall clock on a cold cache or a " +
          "large solution, so the stall backstop has to be sized for the build, not for the tests.",
      ),
      h("testhost is the orphan"),
      p(
        "The test run happens in a separate <code>testhost</code> process. When the parent dies badly — an " +
          "agent that gave up, a terminal that closed, a machine that suspended — that child routinely " +
          "survives, holding a lock on the build output and, if the suite starts one, a port. This is the " +
          "textbook case for the reaper: every tman command, including <code>tman list</code>, kills live " +
          "children whose runner has died.",
      ),
      code(
        `tman list          # the sweep runs first, then you see what is left
tman clean         # same sweep, on demand, printing the counts`,
        "shell",
      ),
      note(
        "<strong>Point the alias at the test project, not at the solution root.</strong> Run from a directory " +
          "holding an application <code>.csproj</code>, <code>dotnet test</code> resolves that project, finds " +
          "no tests, and can exit 0 in a few seconds having run nothing — a green that means nothing at all. " +
          "An explicit path to the test project is the fix, and a build-time guard that refuses the ambiguous " +
          "invocation is worth adding if the mistake has been made once.",
      ),
      h("Separate the build from the test"),
      p(
        "If restore and build dominate, split them into their own alias with their own caps. " +
          "<code>dotnet build</code> is supposed to saturate cores and can want several GB, so it wants a " +
          "wide budget and no memory ceiling; the test run afterwards wants a tighter one. " +
          "<code>dotnet test --no-build</code> then measures what you actually care about.",
      ),
      ...calibrate(),
    ],
    faqs: [
      {
        q: "How do I clean up leftover testhost processes?",
        a: "Any tman command does it. Every invocation, including `tman list`, runs a housekeeping sweep first that kills live children whose runner has died and prunes expired records. `tman clean` runs the same sweep on demand and prints what it did.",
      },
      {
        q: "Why does dotnet test exit 0 without running tests?",
        a: "It resolved the wrong project. Run from a directory holding an application .csproj rather than a test project, it finds no tests and reports success in a few seconds. Always give the alias an explicit path to the test project — and consider a build target that refuses the ambiguous invocation outright.",
      },
    ],
    sources: [{ label: "dotnet test reference", url: "https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-test" }],
  },

  {
    slug: "cargo-test",
    name: "cargo test",
    category: "test",
    ecosystem: "Rust",
    tagline: "The longest quiet stretch of any common command; linking is the memory spike.",
    title: "Tuning tman for cargo test: cold build silence",
    description:
      "Recommended tman caps for cargo test: why a cold Rust build is the longest silence tman sees, and why the memory ceiling must clear the linker.",
    caps: { stall: "30m", maxTime: "45m", maxParallel: 1 },
    kdl: `alias "test" {
    command "cargo"
    args "test" "--all-features"
    max-time "45m"
    max-parallel 1
}`,
    blocks: [
      p(
        "A cold <code>cargo test</code> on a dependency-heavy crate is the longest legitimately quiet stretch " +
          "you are likely to hand tman. Without <code>--verbose</code>, cargo prints a compile line per crate " +
          "and then nothing while <code>rustc</code> works, and a single large crate can take many minutes on " +
          "its own.",
      ),
      note(
        "Leave <code>stall</code> at the <strong>30m</strong> built-in here, or raise it. This is the command " +
          "the default was chosen for. If you want the run bounded, that is <code>max-time</code> — the two " +
          "answer different questions, and only <code>max-time</code> answers \"has this taken too long?\".",
      ),
      h("Memory peaks at link time, not compile time"),
      p(
        "The spike is at the end: linking a large binary with debug info, and LTO if it is enabled, can " +
          "briefly want several gigabytes in a single process. That is why this guide recommends no " +
          "<code>max-mem</code> by default — a ceiling sized for the compile phase will cull a perfectly " +
          "healthy link. If you do set one, set it above your observed link peak and treat a 126 as a " +
          "question about the link, not as a number to raise.",
      ),
      h("`max-parallel 1` on a shared target directory"),
      p(
        "Two cargo invocations against the same <code>target/</code> serialise on its lock anyway, so a " +
          "second slot buys nothing and costs you two processes holding memory instead of one. Since buckets " +
          "are scoped per name and directory, a different crate in a different checkout is unaffected.",
      ),
      p(
        "<code>cargo nextest</code> changes the picture on the test side — it streams per-test results, so " +
          "the run phase is never quiet — but the build phase in front of it is unchanged, and that is the " +
          "phase the stall backstop has to clear.",
      ),
      ...calibrate(),
    ],
    faqs: [
      {
        q: "What stall value should I use for cargo test?",
        a: "The 30m built-in, or higher. A cold Rust build is the longest silence tman commonly sees, and stall is a hang backstop rather than a runtime budget — if you want the run bounded, use max-time, which is the cap that answers that question.",
      },
      {
        q: "Should I set max-mem for cargo test?",
        a: "Not by default. The peak is at link time, not compile time, and a ceiling sized for compilation will cull a healthy link of a large binary with debug info or LTO. If you set one, put it above your observed link peak.",
      },
    ],
    sources: [{ label: "cargo test", url: "https://doc.rust-lang.org/cargo/commands/cargo-test.html" }],
  },

  {
    slug: "playwright",
    name: "Playwright",
    category: "e2e",
    ecosystem: "JavaScript / TypeScript",
    tagline: "Browser downloads look like a hang; browsers and web servers are the orphans.",
    title: "Tuning tman for Playwright: browsers and ports",
    description:
      "Recommended tman caps for Playwright: why browser downloads look like a stall, why max-parallel must be 1, and how orphaned browsers get reaped.",
    caps: { stall: "15m", maxTime: "30m", maxMem: "8192", maxParallel: 1 },
    kdl: `alias "e2e" {
    command "npx"
    args "playwright" "test"
    max-time "30m"
    max-mem 8192
    max-parallel 1
}`,
    blocks: [
      p(
        "Playwright is the tool that exercises every part of tman at once: a long quiet prologue, a fleet of " +
          "memory-hungry child processes, a server bound to a port, and a shutdown path that leaves things " +
          "behind when it is interrupted.",
      ),
      h("The first run downloads browsers"),
      p(
        "On a cold machine or a fresh CI image, <code>playwright test</code> fetches browser bundles before " +
          "it runs anything — hundreds of megabytes, quiet, and slow on a bad network. On Linux that traffic " +
          "moves the tree's IO counters and the backstop sees a working process. On macOS and Windows it does " +
          "not, so either widen <code>stall</code> there or, better, make the download its own step: " +
          "<code>npx playwright install --with-deps</code> in setup, so the test alias never has to cover it.",
      ),
      h("`max-parallel 1`, always"),
      p(
        "Playwright's <code>webServer</code> binds a port. Two runs of the same suite in the same directory " +
          "means two servers wanting the same port, and the failure is not clean — the second either " +
          "bind-conflicts or quietly talks to the first server's database and produces failures that read as " +
          "flakes. One slot per bucket removes the whole category.",
      ),
      h("Browsers are where the memory is"),
      p(
        "Each worker drives a browser process with its own renderer children, and a trace or video artifact " +
          "per test adds to it. <code>max-mem 8192</code> is a deliberately generous ceiling: it is not there " +
          "to tune throughput, it is there so a suite that leaks contexts does not take the machine down. " +
          "The ceiling is summed across the tree on Linux, which is the only way it could cover a browser " +
          "fleet.",
      ),
      note(
        "<strong>Orphaned browsers are the reaper's other headline case.</strong> Interrupt a Playwright run " +
          "and you can be left with chrome processes and a <code>webServer</code> holding a port for the rest " +
          "of the afternoon. Any tman command sweeps them: children whose runner has died are killed before " +
          "anything else happens.",
      ),
      ...calibrate(),
    ],
    faqs: [
      {
        q: "Why should Playwright run with max-parallel 1?",
        a: "Because its webServer binds a port. Two concurrent runs in the same directory either bind-conflict or, worse, share one server and one database and produce failures that look like flaky tests. A single slot per bucket removes that class entirely, and buckets are per directory, so other projects are unaffected.",
      },
      {
        q: "My first Playwright run gets killed. Why?",
        a: "It is probably downloading browsers — a long, quiet prologue. On Linux the IO counters cover it; on macOS and Windows tman samples only the root process, so widen stall there. The better fix is to move `npx playwright install --with-deps` into a setup step so the test alias never covers a download.",
      },
    ],
    sources: [{ label: "Playwright CLI", url: "https://playwright.dev/docs/test-cli" }],
  },

  {
    slug: "rspec",
    name: "RSpec",
    category: "test",
    ecosystem: "Ruby",
    tagline: "Chatty during the run; preloaders like Spring leave daemons behind.",
    title: "Tuning tman for RSpec: Spring daemons and boot",
    description:
      "Recommended tman caps for RSpec: sizing around Rails boot, and why preloaders such as Spring defeat process supervision unless you disable them.",
    caps: { stall: "10m", maxTime: "20m", maxMem: "4096", maxParallel: 1 },
    kdl: `alias "test" {
    command "bundle"
    args "exec" "rspec"
    max-time "20m"
    max-mem 4096
    max-parallel 1
}`,
    blocks: [
      p(
        "RSpec prints a character per example, so the run itself is never quiet. The quiet part is in front " +
          "of it: booting a Rails application, which on a large app means loading the whole framework, the " +
          "gem set, and the schema before the first dot appears.",
      ),
      h("Disable preloaders under supervision"),
      p(
        "Spring and its relatives work by keeping a resident daemon that survives your command. That is " +
          "directly at odds with supervision: the work you want capped happens in a process outside the tree " +
          "tman is watching, so caps do not apply to it — and the daemon is exactly the kind of long-lived " +
          "leftover the reaper exists to clean up.",
      ),
      code(
        `alias "test" {
    command "bundle"
    args "exec" "rspec"
    max-time "20m"
    max-parallel 1
}

// and in the project, so the alias means what it says:
//   DISABLE_SPRING=1   (or remove spring from the test group)`,
        ".tman.kdl",
        "kdl",
      ),
      h("One slot, because of the database"),
      p(
        "A Rails test suite owns its test database. Two concurrent runs in the same checkout truncate each " +
          "other's tables mid-example, and the resulting failures look like flakes rather than like a " +
          "collision. <code>max-parallel 1</code> is the honest setting unless you have per-process database " +
          "isolation configured, in which case raise it deliberately.",
      ),
      ...calibrate(),
    ],
    faqs: [
      {
        q: "Why does tman recommend disabling Spring for RSpec?",
        a: "Spring keeps a resident daemon that outlives your command, so the work you meant to cap runs in a process outside the supervised tree and the caps do not apply to it. The daemon is also precisely the kind of leftover the orphan reaper is for. Disable it in the supervised alias and the caps mean what they say.",
      },
    ],
    sources: [{ label: "RSpec command line", url: "https://rspec.info/features/3-13/rspec-core/command-line/" }],
  },

  {
    slug: "gradle-maven",
    name: "Gradle & Maven",
    category: "test",
    ecosystem: "JVM",
    tagline: "The Gradle daemon deliberately outlives your run — supervise around it, not through it.",
    title: "Tuning tman for Gradle and Maven: the daemon",
    description:
      "Recommended tman caps for JVM builds: why the Gradle daemon defeats process supervision, when --no-daemon is right, and how to size memory vs -Xmx.",
    caps: { stall: "15m", maxTime: "30m", maxMem: "8192", maxParallel: 1 },
    kdl: `alias "test" {
    command "./gradlew"
    args "test" "--no-daemon"
    max-time "30m"
    max-mem 8192
    max-parallel 1
}`,
    blocks: [
      p(
        "Gradle and Maven share a shape: a long, quiet dependency-resolution phase in front, a compile phase " +
          "that saturates cores, and a JVM whose memory is governed by its own flags rather than by the " +
          "operating system. Gradle adds one complication the others do not have.",
      ),
      h("The daemon is a process outside your tree"),
      p(
        "Gradle's default is to hand the work to a long-lived background daemon and have the client wait for " +
          "it. Under supervision that inverts what you asked for: the process tman is watching is a thin " +
          "client, the actual compile and test work happens in a process it never sees, and killing the " +
          "client on a timeout leaves the daemon running exactly as before.",
      ),
      table(
        ["what you want", "how to get it"],
        [
          [
            "caps that actually bound the build",
            "<code>--no-daemon</code> in the supervised alias, so the work happens inside the tree",
          ],
          [
            "daemon speed for interactive use",
            "leave your own shell alone; supervise the agent's and CI's alias only",
          ],
          [
            "a daemon that does not accumulate",
            "<code>./gradlew --stop</code> in cleanup — tman will not reap it, because it is not a child of any run",
          ],
        ],
      ),
      note(
        "<strong>The reaper cannot help you here.</strong> It kills children whose runner died. A Gradle " +
          "daemon is deliberately not a child of your run — it is designed to outlive it — so no amount of " +
          "supervision reaches it. That is a good reason to prefer <code>--no-daemon</code> for automated " +
          "runs and to keep the daemon for the shell you are sitting in front of.",
      ),
      h("`max-mem` versus `-Xmx`"),
      p(
        "The JVM's own heap limit fails with an <code>OutOfMemoryError</code> and a stack trace naming what " +
          "was allocating; tman's <code>max-mem</code> kills the tree and returns 126. As with Go's " +
          "<code>-timeout</code>, set the tool's own limit below tman's and let the informative one fire " +
          "first. tman's ceiling has to clear the heap plus metaspace plus the compiler's own overhead — " +
          "8 GB against a 4 GB heap is a reasonable starting ratio.",
      ),
      ...calibrate(),
    ],
    faqs: [
      {
        q: "Why does tman recommend --no-daemon for supervised Gradle builds?",
        a: "Because with the daemon, the supervised process is a thin client and the real compile and test work happens in a background process outside the tree. Caps then bound the client rather than the build, and killing the client on a timeout leaves the daemon running. --no-daemon puts the work back inside the tree tman is watching.",
      },
      {
        q: "Will tman's reaper clean up a leftover Gradle daemon?",
        a: "No. The reaper kills children whose runner has died; the Gradle daemon is deliberately not a child of your run. Use `./gradlew --stop` in your cleanup step, and prefer --no-daemon for automated runs so the situation does not arise.",
      },
    ],
    sources: [
      { label: "Gradle daemon", url: "https://docs.gradle.org/current/userguide/gradle_daemon.html" },
      { label: "Maven Surefire", url: "https://maven.apache.org/surefire/maven-surefire-plugin/" },
    ],
  },

  {
    slug: "eslint",
    name: "ESLint",
    category: "lint",
    ecosystem: "JavaScript / TypeScript",
    tagline: "Cheap unless type-aware rules are on — then it is a typecheck wearing a linter's name.",
    title: "Tuning tman for ESLint: type-aware rule costs",
    description:
      "Recommended tman caps for ESLint: why type-aware rules change the cost profile entirely, and why lint aliases can carry a higher max-parallel.",
    caps: { stall: "5m", maxTime: "10m", maxParallel: 4 },
    kdl: `alias "lint" {
    command "npx"
    args "eslint" "." "--max-warnings" "0"
    max-time "10m"
    max-parallel 4
}`,
    blocks: [
      p(
        "Plain ESLint is fast, bounded, and holds little memory — it barely needs supervising. Turn on " +
          "type-aware rules and the picture changes completely: the linter now builds a TypeScript program, " +
          "and the cost profile becomes <code>tsc</code>'s rather than a linter's, both in time and in heap.",
      ),
      table(
        ["configuration", "what to expect", "caps"],
        [
          ["syntactic rules only", "seconds, small heap", "<code>max-time 10m</code> is generous already"],
          [
            "type-aware (<code>projectService</code>, <code>parserOptions.project</code>)",
            "a full program build; minutes on a large repo, gigabytes of heap",
            `treat it like the <a href="${href("/tuning/typescript")}">typecheck</a> guide`,
          ],
        ],
      ),
      h("Lint can take a higher `max-parallel`"),
      p(
        "The default of 2 exists because test runs contend for ports, databases, and cores. A syntactic lint " +
          "contends for none of those, so a lint bucket can comfortably hold 4 slots — and because buckets " +
          "are scoped per name and directory, raising it on <code>lint</code> does not affect the queue for " +
          "<code>test</code> at all.",
      ),
      h("`--max-warnings 0` in the alias"),
      p(
        "Under an agent, a warning nobody reads is a warning that does not exist. Making the alias fail on " +
          "any warning is the same principle as pinning <code>--ci</code> for Jest: a command that reports " +
          "success without doing its job produces no signal to act on, so it survives indefinitely.",
      ),
      note(
        "<code>--cache</code> is worth adding for interactive use and worth <em>omitting</em> for the " +
          "agent-facing alias. A stale cache is one more way for a lint to report success it did not earn, " +
          "and the seconds it saves are not the constraint here.",
      ),
      ...calibrate(),
    ],
    faqs: [
      {
        q: "Can lint aliases use a higher max-parallel than test aliases?",
        a: "Yes. The default of 2 is sized for runs that contend for ports, databases, and cores; a syntactic lint contends for none of them, so 4 is comfortable. Buckets are scoped per name and directory, so raising it on the lint alias leaves the test queue untouched.",
      },
      {
        q: "Why is my ESLint run suddenly taking minutes and gigabytes?",
        a: "Almost certainly type-aware rules. With `projectService` or `parserOptions.project` set, ESLint builds a full TypeScript program, and the cost profile becomes tsc's rather than a linter's. Size the caps from the typecheck guide instead.",
      },
    ],
    sources: [{ label: "ESLint CLI", url: "https://eslint.org/docs/latest/use/command-line-interface" }],
  },

  {
    slug: "ruff",
    name: "Ruff",
    category: "lint",
    ecosystem: "Python",
    tagline: "Fast enough that the caps are only there to catch a pathological case.",
    title: "Tuning tman for Ruff: minimal caps, fast linter",
    description:
      "Recommended tman caps for Ruff and other fast native linters: what to set when supervision is a backstop against a wedged filesystem, not a control.",
    caps: { stall: "5m", maxTime: "5m", maxParallel: 4 },
    kdl: `alias "lint" {
    command "ruff"
    args "check" "."
    max-time "5m"
    max-parallel 4
}`,
    blocks: [
      p(
        "Ruff lints a large repository in under a second. Supervising it is not about controlling resource " +
          "use — there is none to control. It is about two other things.",
      ),
      h("A cap that fires is a signal"),
      p(
        "A five-minute <code>max-time</code> on a command that normally takes 800 milliseconds will never " +
          "fire on healthy work. If it does fire, something is genuinely wrong — a filesystem that has " +
          "stopped answering, a symlink loop, a network mount — and you find out through a clear exit 124 " +
          "rather than through an agent that appears to have gone quiet.",
      ),
      h("Consistency is worth more than the cap"),
      p(
        "The other reason is uniformity: if every command in the project goes through an alias, then " +
          "<code>tman list</code> shows the whole picture, <code>tman status --json</code> gives you " +
          "durations for everything, and there is no category of command that quietly runs unsupervised " +
          "because it seemed too small to bother with. The same reasoning applies to " +
          "<a href=\"" + href("/tuning/biome") + "\">Biome</a> and to formatters.",
      ),
      note(
        "<code>ruff format --check</code> deserves its own alias rather than being folded into " +
          "<code>lint</code>. A single alias that does both gives you one exit code for two questions, and " +
          "the answer an agent needs — reformat, or fix a rule — is different in each case.",
      ),
      ...calibrate(),
    ],
    faqs: [
      {
        q: "Is it worth supervising a linter as fast as Ruff?",
        a: "Yes, for two reasons that are not about resource control. A cap that never fires on healthy work turns a genuinely wedged filesystem into a clear exit 124 instead of a silent pause. And if every command goes through an alias, `tman list` and `tman status --json` show the whole project rather than the parts that seemed big enough to bother with.",
      },
    ],
    sources: [{ label: "Ruff CLI", url: "https://docs.astral.sh/ruff/linter/" }],
  },

  {
    slug: "biome",
    name: "Biome",
    category: "lint",
    ecosystem: "JavaScript / TypeScript",
    tagline: "Native speed; supervise it for uniformity, and pin --error-on-warnings.",
    title: "Tuning tman for Biome: caps and non-mutating CI",
    description:
      "Recommended tman caps for Biome: minimal limits for a fast native linter, plus the flags that stop a lint alias reporting success it did not earn.",
    caps: { stall: "5m", maxTime: "5m", maxParallel: 4 },
    kdl: `alias "lint" {
    command "npx"
    args "biome" "ci" "."
    max-time "5m"
    max-parallel 4
}`,
    blocks: [
      p(
        "Biome is in the same class as <a href=\"" + href("/tuning/ruff") + "\">Ruff</a>: fast enough that the caps are " +
          "a backstop against a pathological environment rather than a control on resource use. The " +
          "interesting choices are about which subcommand the alias pins.",
      ),
      h("`biome ci`, not `biome check`"),
      p(
        "<code>biome ci</code> is the non-mutating, fail-on-any-diagnostic mode. <code>biome check " +
          "--write</code> fixes files in place, which under an agent means the lint alias silently rewrites " +
          "the working tree and then reports success — you get a green from a command that changed your code " +
          "rather than one that judged it.",
      ),
      note(
        "That is the same failure shape as a snapshot test that writes instead of comparing, and as a " +
          "scaffolded alias that echoes instead of running. In each case the command reports success without " +
          "doing the work it was asked to do, which produces no signal to act on — so it survives " +
          "indefinitely.",
      ),
      h("Give formatting its own alias"),
      p(
        "One command answering two questions gives you one exit code for two different fixes. A separate " +
          "<code>format</code> alias running <code>biome format --check</code> keeps \"this file is badly " +
          "formatted\" distinguishable from \"this code violates a rule\", which is exactly the distinction " +
          "an agent needs to act correctly.",
      ),
      ...calibrate(),
    ],
    faqs: [
      {
        q: "Should the tman lint alias run `biome check --write`?",
        a: "No. A writing lint rewrites the working tree and then reports success, so you get a green from a command that changed your code rather than one that judged it. Pin `biome ci`, which is non-mutating and fails on any diagnostic.",
      },
    ],
    sources: [{ label: "Biome CLI", url: "https://biomejs.dev/reference/cli/" }],
  },

  {
    slug: "golangci-lint",
    name: "golangci-lint",
    category: "lint",
    ecosystem: "Go",
    tagline: "The lint that actually needs a memory ceiling — whole-program analysis is greedy.",
    title: "Tuning tman for golangci-lint: memory ceilings",
    description:
      "Recommended tman caps for golangci-lint: why this is the one linter that genuinely needs max-mem, and how --timeout should relate to max-time.",
    caps: { stall: "10m", maxTime: "15m", maxMem: "6144", maxParallel: 2 },
    kdl: `alias "lint" {
    command "golangci-lint"
    args "run" "--timeout" "10m"
    max-time "15m"
    max-mem 6144
}`,
    blocks: [
      p(
        "golangci-lint is the exception to the rule that linters are cheap. Several of its analysers need " +
          "whole-program type information, which means loading and type-checking every package in the module " +
          "at once. On a large repository that is gigabytes of resident memory and minutes of near-silence, " +
          "which puts it closer to <a href=\"" + href("/tuning/go-test") + "\">go test</a> than to a linter.",
      ),
      h("This is where `max-mem` earns its keep"),
      p(
        "tman does not set a memory ceiling by default, because a build is supposed to be greedy and culling " +
          "one out of the box would break ordinary work. golangci-lint is the case where opting in is " +
          "clearly right: the failure mode without a ceiling is not a slow lint, it is the machine swapping " +
          "and everything else on it becoming unusable. A 6 GB ceiling turns that into a clean exit 126.",
      ),
      p(
        "When 126 does fire, the useful responses are to narrow the enabled analysers, to run against " +
          "packages rather than <code>./...</code>, or to raise the ceiling deliberately — in that order. " +
          "Raising it first turns the ceiling into a rubber stamp.",
      ),
      h("Let the tool's own timeout fire first"),
      p(
        "<code>golangci-lint --timeout</code> exits with a message naming which linter was still running. " +
          "tman's <code>max-time</code> kills the tree and reports 124. Set the tool's below tman's, exactly " +
          "as with <code>go test -timeout</code>, so the diagnosis comes from the tool and the backstop only " +
          "covers the case where the tool itself is wedged.",
      ),
      ...calibrate(),
    ],
    faqs: [
      {
        q: "Why does golangci-lint need max-mem when other linters do not?",
        a: "Because several of its analysers need whole-program type information, so it loads and type-checks every package in the module at once — gigabytes on a large repository. Without a ceiling the failure mode is the machine swapping rather than a slow lint. A 6 GB ceiling turns that into a clean exit 126.",
      },
      {
        q: "Should I set --timeout as well as max-time?",
        a: "Yes, and lower. golangci-lint's own timeout exits with a message naming the linter that was still running; tman's max-time kills the tree and reports 124. The tool's message is the more useful of the two, so let it fire first and keep tman's as the backstop for a wedged process.",
      },
    ],
    sources: [{ label: "golangci-lint", url: "https://golangci-lint.run/usage/configuration/" }],
  },

  {
    slug: "typescript",
    name: "TypeScript (tsc)",
    category: "lint",
    ecosystem: "JavaScript / TypeScript",
    tagline: "Minutes of total silence on a large project, and --watch must never get a timeout.",
    title: "Tuning tman for tsc: silence, watch, and heap",
    description:
      "Recommended tman caps for TypeScript typechecking: sizing the stall backstop around a silent tsc, and why --watch must never carry a max-time.",
    caps: { stall: "15m", maxTime: "15m", maxMem: "8192", maxParallel: 2 },
    kdl: `alias "typecheck" {
    command "npx"
    args "tsc" "--noEmit"
    max-time "15m"
    max-mem 8192
}`,
    blocks: [
      p(
        "<code>tsc --noEmit</code> produces no output at all until it produces all of it. On a large project " +
          "that is minutes of complete silence followed by either nothing or a wall of errors — the exact " +
          "profile that makes a stall value sized like a runtime budget so destructive.",
      ),
      note(
        "On Linux this is safe at any reasonable <code>stall</code>, because the type checker burns CPU " +
          "continuously and tman counts that as activity. On macOS and Windows only the root process is " +
          "sampled — which is the whole of <code>tsc</code>, so its CPU is still visible, but any work it " +
          "delegates is not. Keep <code>stall</code> wide there.",
      ),
      h("`--watch` must never carry a wall-clock cap"),
      p(
        "A watching typechecker is designed never to exit. Giving it <code>max-time</code> guarantees a kill " +
          "and an exit 124 that carries no information. What you actually want from supervision here is the " +
          "dedup lock: an agent that starts a watcher, forgets, and starts another leaves you with two " +
          "processes racing on the same output.",
      ),
      code(
        `alias "typecheck" {
    command "npx"
    args "tsc" "--noEmit"
    max-time "15m"
}

alias "typecheck-watch" {
    command "npx"
    args "tsc" "--noEmit" "--watch"
    max-parallel 1        // one watcher, and --name refuses the second
}`,
        ".tman.kdl",
        "kdl",
      ),
      h("Heap, and project references"),
      p(
        "The type checker holds the whole program graph in memory, and on a large monorepo that reaches " +
          "several gigabytes before Node's own heap limit stops it with an unhelpful abort. A " +
          "<code>max-mem</code> of 8 GB sits above ordinary work and below the point where the machine " +
          "suffers. If you are hitting it regularly, project references and " +
          "<code>tsc --build</code> reduce the working set rather than just accommodating it.",
      ),
      ...calibrate(),
    ],
    faqs: [
      {
        q: "Can I put max-time on tsc --watch?",
        a: "No — a watching typechecker is designed never to exit, so max-time guarantees a kill and an exit 124 that means nothing. Give the watch alias max-parallel 1 and a --name instead; the dedup lock is the supervision you actually want, because it stops a second watcher racing the first.",
      },
      {
        q: "How long can tsc be silent before tman kills it?",
        a: "At the 30m built-in stall, half an hour of silence *and* idleness. On Linux the check never looks idle because it burns CPU continuously and tman counts that as activity, so the practical answer is that a working tsc is not at risk. On macOS and Windows keep the value wide.",
      },
    ],
    sources: [{ label: "tsc CLI options", url: "https://www.typescriptlang.org/docs/handbook/compiler-options.html" }],
  },

  {
    slug: "vite-webpack",
    name: "Vite & webpack",
    category: "build",
    ecosystem: "JavaScript / TypeScript",
    tagline: "Builds want the cores and the RAM; dev servers must never get a timeout.",
    title: "Tuning tman for Vite and webpack builds",
    description:
      "Recommended tman caps for bundlers: why a production build should get no memory ceiling by default, and why a dev server needs dedup, not a deadline.",
    caps: { stall: "10m", maxTime: "20m", maxParallel: 2 },
    kdl: `alias "build" {
    command "npx"
    args "vite" "build"
    max-time "20m"
}

alias "dev" {
    command "npx"
    args "vite"
    max-parallel 1
}`,
    blocks: [
      p(
        "Bundlers split cleanly into two commands with opposite supervision needs, and conflating them is " +
          "the most common configuration mistake in this whole section.",
      ),
      h("The build: no memory ceiling by default"),
      p(
        "A production build is <em>supposed</em> to saturate your cores and can legitimately want several " +
          "gigabytes — minification and source maps over a large graph are not frugal operations. This is " +
          "exactly why tman ships no default <code>max-mem</code> or <code>max-cpu</code>: culling a build " +
          "out of the box would break <code>tman run -- vite build</code> in a project with no config at all.",
      ),
      p(
        "Set one only after a build has actually hurt you, and set it above the observed peak. What the " +
          "build does want is <code>max-time</code>, because a bundler wedged on a circular dependency or a " +
          "pathological plugin will otherwise sit there indefinitely.",
      ),
      h("The dev server: dedup, not deadline"),
      watchWarning("vite / webpack serve"),
      p(
        "The real hazard with a dev server under an agent is duplication. The agent starts one, the tool " +
          "call returns, the agent forgets, and twenty minutes later it starts another — now two servers " +
          "want port 5173, and the one that lost is either dead or, worse, silently serving stale output on " +
          "a different port. A dedup lock refuses the second outright:",
      ),
      code(
        `tman run --name dev -- npx vite            # the second call refuses
tman run --name dev --replace -- npx vite   # or kills the first and waits for the name`,
        "shell",
      ),
      note(
        "<code>--replace</code> does not just kill and start — it waits for the old runner to hand the name " +
          "back, up to <code>--queue-timeout</code>, and refuses to start if it is still held. That is the " +
          "difference between replacing a server and briefly running two.",
      ),
      h("And when the agent's machine suspends"),
      p(
        "A dev server is the process most likely to still be running tomorrow. Every tman command sweeps " +
          "children whose runner has died, so a laptop that suspended overnight comes back to a machine that " +
          "cleans itself up on the next <code>tman list</code> rather than to a fan running at full speed.",
      ),
      ...calibrate(),
    ],
    faqs: [
      {
        q: "Should I set max-mem on a Vite or webpack production build?",
        a: "Not by default. A production build is supposed to saturate cores and can legitimately want several gigabytes — which is why tman ships no default memory ceiling at all. Add one only after a build has actually caused a problem, and set it above the peak you measured.",
      },
      {
        q: "How do I stop an agent starting a second dev server?",
        a: "Give the run a --name. A same-name run in the same directory refuses by default; with --replace it kills the existing one and waits for the runner to hand the name back before starting, so you never briefly have two servers on the same port.",
      },
    ],
    sources: [
      { label: "Vite CLI", url: "https://vite.dev/guide/cli.html" },
      { label: "webpack CLI", url: "https://webpack.js.org/api/cli/" },
    ],
  },
];

export const toolBySlug = (slug: string) => TOOLS.find((t) => t.slug === slug);
export const toolsByCategory = (c: Category) => TOOLS.filter((t) => t.category === c);
