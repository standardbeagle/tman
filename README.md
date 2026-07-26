# tman

**Runaway tests, meet the reaper.** A single NativeAOT binary that supervises every process it launches — killing runs that have genuinely hung, capping time, memory, and CPU when you ask it to, and automatically reaping the orphans your LLM agents leave behind when they hang, get distracted, or your machine suspends.

![demo](assets/demo.gif)

## Why

LLM agents start test suites and then hang, get distracted, or survive a machine suspend — leaving processes that drain your system for hours. `tman` wraps every run with hard limits and a reaper, so nothing outlives its welcome.

- **wall-time + stall kills** — `--max-time 10m`, `--stall 30m` (on Linux: silent *and* idle = hung, so quiet-but-busy work like `go test` keeps running)
- **resource culling** — opt-in `--max-mem 2g`, `--max-cpu 95` (sustained) kill the whole process tree
- **orphan reaping** — every `tman` command kills children whose runner died and prunes expired records; a lock whose runner died is taken over in place by the next run of that name
- **dedup locks** — `--name test` refuses duplicates; `--replace` kills the old run
- **resource gating** — `--max-parallel 2` queues excess runs instead of stampeding cores
- **per-project scoping** — locks and slots bucket by name (or command) *and* directory, so one repo's runs never block another's
- **folder aliases** — `.tman.kdl` per project, with repo-root shims so `./test` is supervised transparently
- **~3.8 MB native binary**, zero runtime deps, cross-platform (linux/mac/windows, x64/arm64)

## Install

**npm**
```sh
npm install -g @standardbeagle/tman
```

**shell one-liner**
```sh
curl -fsSL https://raw.githubusercontent.com/standardbeagle/tman/main/install.sh | sh
```

**from source** — requires the [.NET 10 SDK](https://dot.net):
```sh
git clone https://github.com/standardbeagle/tman
cd tman
dotnet publish -c Release -r linux-x64   # or win-x64, osx-arm64, linux-arm64
cp bin/Release/net10.0/*/publish/tman ~/.local/bin/
```

Prebuilt binaries are attached to every [GitHub release](https://github.com/standardbeagle/tman/releases) for linux-x64, linux-arm64, win-x64, osx-arm64, and osx-x64.

## Quick start

```sh
# supervise anything
tman run --max-time 10m --max-mem 2g -- npm test

# adopt in a project (auto-detects npm / pytest / go / make)
cd your-project
tman init --shims --gitignore
./test        # now supervised: stall backstop, dedup + parallel gating
```

## Commands

| command | what it does |
| --- | --- |
| `tman run [flags] -- <cmd> [args]` | run a process under supervision |
| `tman run --alias <name> [args]` / `tman <alias>` | run a `.tman.kdl` alias |
| `tman list [--all]` | list live runs (or all records) |
| `tman kill <id\|name\|all> [--stale-only]` | kill run(s) |
| `tman clean` | run the housekeeping sweep now and report what it did |
| `tman status [id\|name\|id-prefix] [--json]` | summary counts, or one run's detail |
| `tman init [--shims] [--gitignore]` | scaffold `.tman.kdl` + shims (aliases it cannot detect are left commented out, so `./test` fails loudly instead of faking a pass) |
| `tman hook pretooluse` | [Claude Code hook](#claude-code-hook): routes bare test/build commands through tman, and never blocks |

## Run flags

| flag | default | what it does |
| --- | --- | --- |
| `--name N` | — | dedup lock; refuses if a live run has the same name **in this directory** |
| `--replace` | off | with `--name`: kill the existing run first |
| `--max-time T` | — | wall-clock limit → kill, exit 124 |
| `--stall T` | 60s (`tman init` scaffolds `30m`) | no output **and** no cpu/io activity for T → kill, exit 125 |
| `--max-mem M` | — | ceiling on the process tree's RSS (MB or `2g`) → cull, exit 126 |
| `--max-cpu P` | — | sustained CPU% → cull, exit 126 |
| `--max-parallel N` | 2 | queue while N live runs share this run's bucket |
| `--queue-timeout T` | 5m | give up waiting for a slot |

Cap precedence: CLI flags > alias block > `defaults` block > built-ins.

> **`--stall` is a hang backstop, not a runtime budget.** It answers "is this process dead?",
> not "is this taking too long?" — use `--max-time` for the latter. A cold `go build ./...`,
> `npm run typecheck` or `dotnet test` can legitimately run for many minutes while printing
> nothing, so a stall sized like an expected runtime kills healthy work. Set it well above the
> longest quiet stretch you ever expect; `tman init` scaffolds `30m`.

> **Platform note.** Activity-aware stall detection walks the whole process tree on **Linux**
> only, where `/proc` exposes parent pids and per-process io counters cheaply. On macOS and
> Windows a sample sees the supervised process alone, so work done by a descendant is invisible
> and `--stall` falls back to output-only detection. Give quiet-but-busy runs a longer `--stall`
> on those platforms. `--max-mem` has the same limit: it sums the tree on Linux and measures the
> root process elsewhere.

### Buckets

Dedup locks and parallel slots are counted per **bucket**, not machine-wide. A bucket is
`<name>@<dir>` for a named run and `<command>@<dir>` for an unnamed one, where `<dir>` is the
`.tman.kdl` directory governing the run (or the cwd when there is none):

```
/repo-a  tman test          -> bucket  test@/repo-a
/repo-b  tman test          -> bucket  test@/repo-b   # independent: does not queue behind repo-a
/repo-a  tman run -- vite   -> bucket  vite@/repo-a   # independent of test@/repo-a
```

So `max-parallel 2` means *two of this thing here*, and a long test run in one checkout never
starves a build in another.

## .tman.kdl

Resolved from the current directory upward, like `.git`:

```kdl
defaults {
    stall "30m"       // hang backstop, not a runtime budget — see --stall above
    max-parallel 2
    retain "24h"      // how long finished run records are kept
    // opt-in ceilings — a build is supposed to saturate cores and can want several GB
    // max-mem 8192      // MB, summed across the process tree
    // max-cpu 95        // percent, sustained
}

alias "test" {
    command "npm"
    args "run" "test"
}

alias "e2e" {
    command "pytest"
    args "tests/e2e" "--tb=short"
    max-time "30m"
    max-mem 4096
}
```

## Claude Code hook

Shims only catch commands that go through a shell lookup, on a machine where `tman init --shims`
ran. An agent calling a Bash tool with `npm test` walks straight past them. `tman hook pretooluse`
is the same policy applied one level up — it reads the tool call on stdin and re-issues bare test
and build commands through `tman`:

```json
{
  "hooks": {
    "PreToolUse": [
      { "matcher": "Bash", "hooks": [{ "type": "command", "command": "tman hook pretooluse" }] }
    ]
  }
}
```

| the agent runs | what happens |
| --- | --- |
| `go test ./...` in a project with `.tman.kdl` | rewritten to `/path/to/tman run -- go test ./...`, and the rewrite is announced |
| `go test ./...` with no `.tman.kdl` | runs unchanged; the agent is told it was unsupervised |
| `tman test`, or anything inside a supervised tree (`TMAN_RUN_ID` set) | untouched — no double supervision |
| `cd app && npm test`, `CI=1 npm test` | runs unchanged, with a note; this hook does not parse shell and will not prefix a string it did not parse |
| `git status`, `npm run dev`, anything else | untouched |
| any of the above, when tman cannot prove which binary it is running as | runs unchanged, with a note naming the path it refused to use |

It supervises exactly what was asked for (`tman run -- <command>`), never the project's alias of
the same name — an alias can point somewhere else, and silently running something other than the
command in the transcript is worse than running it unsupervised.

The rewrite names the **running binary by absolute path**, not `tman`, because the Bash tool
resolves a program name against its own `PATH` — which frequently does not include `~/.local/bin`
or the node bin directory tman installs into.

That path has to be *proven* to be tman before it is emitted: fully qualified, named as tman ships
(`tman` or `tman.exe`), and present on disk. Nothing else counts as proof. Launched as
`dotnet tman.dll` rather than as the `tman` executable, the running process is the shared dotnet
host, and rewriting to it would hand the agent `dotnet run -- go test ./...` — which, in a
directory holding a `.csproj`, builds and launches an unrelated application. So whenever the hook
cannot prove what it is about to name — no path, a bare or relative one, a path that is gone, or
one belonging to some other host — it warns, names the path it rejected, and leaves the command
exactly as written.

**It cannot block you.** A missing binary, a malformed request, an unreadable project: every
failure path leaves the command exactly as written. Exit code 2 is the only one Claude Code treats
as a block, and this hook never returns it — so `tman` being uninstalled costs supervision, never
the build.

## Housekeeping

There is no daemon and no cron entry. Every `tman` command — including `tman list` — performs the
same sweep before it does anything else:

- kills orphans (a live child whose runner died, e.g. after a machine suspend)
- deletes finished records older than `retain` (default 24h), along with unreadable or
  off-schema record files that nothing else would ever revisit

Lock files are not part of it. A lock is claimed by holding its file open exclusively, so the
kernel releases it when its runner dies and the next run of that bucket takes the same file over
in place. Removing one is never safe — a run that opened the name a moment earlier would take the
lock as the sweep dropped it and end up holding a file with no name, while the next run created a
fresh one — and it would gain nothing, so `~/.tman/runs` keeps one small `.lock` file per bucket
it has ever seen.

`tman clean` runs that sweep on demand and prints the counts. Records are canonical on disk:
absolute resolved command paths, absolute cwd, one nested `Caps` object, and a schema version, so a
record written by a different tman version is discarded rather than half-read.

## Exit codes

| code | meaning |
| --- | --- |
| 0–n | child's own exit code |
| 124 | timed out (`--max-time`) |
| 125 | stalled (`--stall`) |
| 126 | culled (`--max-mem` / `--max-cpu`) |
| 127 | command / config not found |
| 130 | killed (dedup refusal, queue timeout, `tman kill`) |

## Docs + demo

Full docs: **https://standardbeagle.github.io/tman/** · release history: [CHANGELOG.md](CHANGELOG.md) · regenerate the demo gif with `vhs assets/demo.tape`

## License

[MIT](LICENSE)
