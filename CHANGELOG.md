# Changelog

All notable changes to this project are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). While the version is
below 1.0, behavior changes land in minor releases.

## [Unreleased]

### Fixed

- **`--max-parallel` did not bound runs that started at the same instant.** The gate counted the
  bucket's live runs and admitted the run when the count was under the limit. Every member of a
  fan-out read that count before any of them had a record to be counted, so all of them started:
  under a barrier-synchronised launch with `max-parallel 2`, three runs were observed overlapping.
  A run is now admitted by **holding** one of its bucket's slot files open exclusively, which only
  one runner can do, so simultaneous launches queue as configured. A slot is given up when the
  handle closes, which includes the runner dying and the kernel closing it — there is no stale-slot
  reclaim to lose a race in. Runs already inside a supervised tree (`TMAN_RUN_ID` set) still claim
  no slot.
- **Two runs of one `--name` could start at once.** The dedup gate broke a stale lock and created
  a fresh one — the sequence the parallel-slot gate above was fixed for, on the path that was left
  behind. Two runners each removed the file the other had just created and each held one of the
  two files answering to the name. A name is now claimed by holding its lock file open
  exclusively, as slots already were.
- **A record could throw `FileNotFoundException` out of the middle of a live run.** A run record's
  runner and another command's housekeeping sweep wrote it through the same temp path, so
  whichever renamed second found the file already gone.

### Changed

- **The built-in `--stall` default is now `30m`, up from `60s`.** `stall` is a hang backstop, not a
  runtime budget, and 60s was only ever defensible as the latter. Across 959 supervised runs the
  60s guard fired 32 times and caught no actual hang; it killed `go build ./...` at 60s in
  directories with no `.tman.kdl` while the same command succeeded 14 times elsewhere, once taking
  75s. 0.2.0 scaffolded `30m` into new configs but left the built-in at `60s`, so the hazard
  survived on the two paths the scaffold does not reach.
  **A `.tman.kdl` that omits `stall` now resolves to `30m` as well** — omitting it meant no
  opinion, not a request for 60s. An explicit `stall`, an alias-level `stall`, and `--stall` are
  all unaffected, and widening a backstop cannot turn a passing run into a failing one. Use
  `--max-time` if you want a runtime bound.
- **The sweep no longer removes lock files, and `tman clean` no longer reports freeing them.** No
  unlink of a lock is safe, including one made while holding it exclusively: a run that opened the
  name a moment earlier takes the lock as the unlinker drops it and is then holding a file with no
  name, while the next arrival creates a fresh one. It also bought nothing — a lock whose owner is
  gone is taken over in place, because the kernel drops the hold when the process dies.
  `~/.tman/runs` now keeps one small `.lock` file per bucket it has ever seen.
- **`tman run --replace` waits for the run it killed to release the name** instead of taking the
  name from it, and reports that it is not replacing anything if the lock is still held when the
  queue timeout runs out.

## [0.2.0] - 2026-07-25

Limits used to be counted across your whole machine and enforced by defaults that killed healthy
builds. Both are fixed, and the two together mean `tman run -- vite build` now works in a fresh
project with no configuration at all.

### Changed

- **`max-parallel` and dedup locks are now scoped per bucket, not machine-wide.** A run belongs to
  `<name>@<dir>` when named and `<command>@<dir>` otherwise, where `<dir>` is the `.tman.kdl`
  directory governing it. Previously two `dotnet test` runs in one checkout could starve an
  unrelated build in another until it hit the queue timeout, and `~/.tman/runs/test.lock` was a
  single global name, so running the `test` alias in one repo made every other repo's `test`
  refuse to start.
- **`max-cpu` and `max-mem` are no longer applied by default.** A build is supposed to saturate
  cores, and the old built-in `max-cpu 95` culled it with exit 126 after three seconds; the
  `max-mem 2048` default did the same to any bundler large enough to matter. Both still work as
  flags and config keys, and `tman init` now scaffolds them commented out. The built-in floor is
  now only stall detection plus bucket gating.
- **`--stall` requires silence *and* idleness.** The stall check samples the process tree's CPU and
  I/O, so quiet-but-working runs like `go test` and `dotnet build` are no longer killed for not
  printing anything. The kill message reports the tree's process count and states. **Linux only** —
  see the platform note below.
- **`--max-mem` measures the whole process tree** on Linux. It read the direct child's working set,
  which is the wrong process for any wrapper command — `npm run build` reported the npm shell's
  ~15 MB while node grew underneath, so the cap never fired on the run it existed to catch.
- **Run ages and sizes are formatted at human scale.** Ages used `mm:ss`, which wraps: a 90-minute
  run displayed as `30:00`, i.e. shorter than a 31-minute one.
- **`tman status` prints readable detail** instead of a single line of JSON. Use `--json` for the
  record, now indented and without `\uXXXX` escaping of ordinary quotes.
- **`tman clean` reports what it did** — orphans reaped, records pruned, locks freed.

### Added

- **Automatic housekeeping on every command.** Every invocation, including `tman list`, reaps
  orphans, prunes finished records past the retention window, and releases locks whose owning
  runner died. Old data no longer waits for someone to remember `tman clean`.
- **`retain` config key** (default `24h`) sets how long finished run records are kept.
- **`tman init` detects `build` and `typecheck` npm scripts.** A fresh Vite project previously
  produced a config containing only `lint`, so `tman build` printed usage instead of running.
  `dev`, `start`, and `preview` stay excluded on purpose — an idle server produces no output and
  no CPU, which is what the stall cap kills on.
- **Nested runs are tracked as one logical run.** A supervised process that re-enters tman (a PATH
  shim calling `tman` again) records its parent's id, renders indented in `tman list`, and no
  longer claims a second parallel slot.
- **`tman status` and `tman kill` accept an id prefix** of four characters or more, and `status`
  now resolves finished runs — previously it failed the moment a run exited, which is exactly when
  its detail is wanted.
- **`CHANGELOG.md`** and a documented release process.

### Fixed

- A runner killed mid-run no longer wedges its bucket. Locks now record their owning process, so a
  lock is broken only once its owner is provably gone — previously "no live run matches" was used
  as the staleness test, which is also what a run looks like in the instant between taking its
  lock and registering.
- Unreadable and off-schema record files are cleaned up. They were skipped by every reader and so
  were never revisited by anything, accumulating indefinitely.

### Platform note

Tree-walking needs `/proc`, so it is Linux-only. On macOS and Windows a sample sees the supervised
process alone: `--stall` falls back to output-only detection (as in 0.1.x), and `--max-mem`
measures the root process rather than the tree. Give quiet-but-busy runs a longer `--stall` on
those platforms.

### Upgrading

- **Existing `~/.tman` run records are discarded.** Records now carry a schema version, and v1
  records are dropped rather than half-read — a record deserialized to defaults has `Pid` 0, which
  the reaper would act on. Live runs started by an older tman are not supervised by the new one;
  let them finish or `tman kill` them before upgrading.
- **If you relied on the built-in `max-cpu 95` or `max-mem 2048`**, set them explicitly in your
  `.tman.kdl` `defaults` block. Configs that already set them are unaffected.
- **If you relied on `max-parallel` throttling your whole machine**, it now throttles per bucket.
  There is no machine-wide equivalent.

## [0.1.4] - 2026-07-24

### Added

- PowerShell `.ps1` shim alongside the extensionless and `.cmd` shims on Windows.

### Changed

- `max-time` dropped from the built-in and `tman init` defaults; it is opt-in.

## [0.1.3] - 2026-07-23

### Fixed

- Sub-megabyte `--max-mem` values round up instead of truncating to 0.
- Dedup race closed with an atomic name lock.
- `LastOutputUtc` reports the real value; reaping survives runner PID reuse.

## [0.1.2] - 2026-07-23

### Fixed

- `tman init` skips shim paths already taken by directories or foreign files.
- `--version` reads the assembly informational version.

### Changed

- The release pipeline runs the test suite before publishing native binaries.

## [0.1.1] - 2026-07-23

### Fixed

- The npm installer self-heals its binary download on first run.

## [0.1.0] - 2026-07-23

First release. Supervised process runs with wall-time, stall, memory, and CPU limits; orphan
reaping; dedup locks; parallel gating; `.tman.kdl` folder aliases with repo-root shims; NativeAOT
binaries for linux-x64, linux-arm64, win-x64, osx-arm64, and osx-x64, distributed via npm, PyPI,
PSGallery, and a shell installer.

[0.2.0]: https://github.com/standardbeagle/tman/compare/v0.1.4...v0.2.0
[0.1.4]: https://github.com/standardbeagle/tman/compare/v0.1.3...v0.1.4
[0.1.3]: https://github.com/standardbeagle/tman/compare/v0.1.2...v0.1.3
[0.1.2]: https://github.com/standardbeagle/tman/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/standardbeagle/tman/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/standardbeagle/tman/releases/tag/v0.1.0
