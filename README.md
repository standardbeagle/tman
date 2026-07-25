# tman

**Runaway tests, meet the reaper.** A single NativeAOT binary that supervises every process it launches — capping time, memory, and CPU, killing stalled runs, and automatically reaping the orphans your LLM agents leave behind when they hang, get distracted, or your machine suspends.

![demo](assets/demo.gif)

## Why

LLM agents start test suites and then hang, get distracted, or survive a machine suspend — leaving processes that drain your system for hours. `tman` wraps every run with hard limits and a reaper, so nothing outlives its welcome.

- **wall-time + stall kills** — `--max-time 10m`, `--stall 60s` (silent *and* idle = hung; quiet-but-busy work like `go test` keeps running)
- **resource culling** — opt-in `--max-mem 2g`, `--max-cpu 95` (sustained) kill the whole process tree
- **orphan reaping** — every `tman` command kills children whose runner died
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
./test        # now supervised: 60s stall, dedup + parallel gating
```

## Commands

| command | what it does |
| --- | --- |
| `tman run [flags] -- <cmd> [args]` | run a process under supervision |
| `tman run --alias <name> [args]` / `tman <alias>` | run a `.tman.kdl` alias |
| `tman list [--all]` | list live runs (or all records) |
| `tman kill <id\|name\|all> [--stale-only]` | kill run(s) |
| `tman clean` | reap orphans, prune records older than 24h |
| `tman status [id\|name\|id-prefix] [--json]` | summary counts, or one run's detail |
| `tman init [--shims] [--gitignore]` | scaffold `.tman.kdl` + shims |

## Run flags

| flag | default | what it does |
| --- | --- | --- |
| `--name N` | — | dedup lock; refuses if a live run has the same name **in this directory** |
| `--replace` | off | with `--name`: kill the existing run first |
| `--max-time T` | — | wall-clock limit → kill, exit 124 |
| `--stall T` | 60s | no output **and** no cpu/io activity in the process tree for T → kill, exit 125 |
| `--max-mem M` | — | ceiling on the process tree's RSS (MB or `2g`) → cull, exit 126 |
| `--max-cpu P` | — | sustained CPU% → cull, exit 126 |
| `--max-parallel N` | 2 | queue while N live runs share this run's bucket |
| `--queue-timeout T` | 5m | give up waiting for a slot |

Cap precedence: CLI flags > alias block > `defaults` block > built-ins.

Run records are canonical on disk: the command resolved to an absolute path, an absolute cwd, one
nested `Caps` object holding the caps the run actually ran under, and a schema version — so a record
written by a different tman version is discarded rather than half-read. A supervised process that
re-enters tman (a PATH shim calling `tman` again) records its parent's id and is marked `└` in
`tman list` instead of looking like a second, unrelated run.

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
    stall "60s"
    max-parallel 2
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

Full docs: **https://standardbeagle.github.io/tman/** · regenerate the demo gif with `vhs assets/demo.tape`

## License

[MIT](LICENSE)
