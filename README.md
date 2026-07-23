# tman

**Runaway tests, meet the reaper.** A single NativeAOT binary that supervises every process it launches — capping time, memory, and CPU, killing stalled runs, and automatically reaping the orphans your LLM agents leave behind when they hang, get distracted, or your machine suspends.

![demo](assets/demo.gif)

## Why

LLM agents start test suites and then hang, get distracted, or survive a machine suspend — leaving processes that drain your system for hours. `tman` wraps every run with hard limits and a reaper, so nothing outlives its welcome.

- **wall-time + stall kills** — `--max-time 10m`, `--stall 60s` (no output = hung)
- **resource culling** — `--max-mem 2g`, `--max-cpu 95` (sustained) kill the whole process tree
- **orphan reaping** — every `tman` command kills children whose runner died
- **dedup locks** — `--name test` refuses duplicates; `--replace` kills the old run
- **resource gating** — `--max-parallel 2` queues excess runs instead of stampeding cores
- **folder aliases** — `.tman.kdl` per project, with repo-root shims so `./test` is supervised transparently
- **~3.8 MB native binary**, zero runtime deps, cross-platform (linux/mac/windows, x64/arm64)

## Install

Requires the [.NET 10 SDK](https://dot.net) to build:

```sh
git clone https://github.com/standardbeagle/tman
cd tman
dotnet publish -c Release -r linux-x64   # or win-x64, osx-arm64, linux-arm64
cp bin/Release/net10.0/*/publish/tman ~/.local/bin/
```

## Quick start

```sh
# supervise anything
tman run --max-time 10m --max-mem 2g -- npm test

# adopt in a project (auto-detects npm / pytest / go / make)
cd your-project
tman init --shims --gitignore
./test        # now supervised: 10m wall, 60s stall, 2GB, 95% cpu
```

## Commands

| command | what it does |
| --- | --- |
| `tman run [flags] -- <cmd> [args]` | run a process under supervision |
| `tman run --alias <name> [args]` / `tman <alias>` | run a `.tman.kdl` alias |
| `tman list [--all]` | list live runs (or all records) |
| `tman kill <id\|name\|all> [--stale-only]` | kill run(s) |
| `tman clean` | reap orphans, prune records older than 24h |
| `tman status [id\|name]` | summary counts or a run's JSON record |
| `tman init [--shims] [--gitignore]` | scaffold `.tman.kdl` + shims |

## Run flags

| flag | default | what it does |
| --- | --- | --- |
| `--name N` | — | dedup lock; refuses if a live run has the same name |
| `--replace` | off | with `--name`: kill the existing run first |
| `--max-time T` | 10m | wall-clock limit → kill, exit 124 |
| `--stall T` | 60s | no output for T → kill, exit 125 |
| `--max-mem M` | 2048 | memory ceiling (MB or `2g`) → cull, exit 126 |
| `--max-cpu P` | 95 | sustained CPU% → cull, exit 126 |
| `--max-parallel N` | 2 | queue while N live runs are active |
| `--queue-timeout T` | 5m | give up waiting for a slot |

Cap precedence: CLI flags > alias block > `defaults` block > built-ins.

## .tman.kdl

Resolved from the current directory upward, like `.git`:

```kdl
defaults {
    max-time "10m"
    stall "60s"
    max-mem 2048      // MB
    max-cpu 95        // percent, sustained
    max-parallel 2
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
