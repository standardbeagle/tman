# Changelog

All notable changes to this project are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). While the version is
below 1.0, behavior changes land in minor releases.

## [Unreleased]

### Added
- **Kernel-enforced limits: `limit-cpus` and `limit-mem`.** Unlike `max-mem` and `max-cpu`,
  which sample the tree once a second and cull it, these are held by the kernel and cannot be
  outrun between samples. `limit-cpus N` runs the tree on N CPUs through an affinity mask, so
  `nproc` and runners' worker pools see N; tman spreads concurrent runs over the least-held CPUs.
  `limit-mem M` is a hard ceiling for the whole tree: on Linux a systemd user scope (`MemoryMax`,
  no swap, `OOMPolicy=kill`), on Windows a Job Object; crossing it ends the tree and the run exits
  126. Without systemd, `cgroup "/sys/fs/cgroup/..."` in `~/.tman/tman.kdl` names a delegated
  cgroup — a container's own, or one an admin handed over — and tman makes each run's cgroup there
  itself; tman must run inside that subtree. Both limits are refused on macOS, and `limit-mem` is
  refused on a Linux machine with neither a systemd user manager nor a usable configured cgroup,
  with exit 127 before the run queues. Available as flags, in `defaults`, and in aliases.
- **`defaults` can name a queue.** `queue "compile"` in a `.tman.kdl` `defaults` block puts every
  run in the project in that machine-wide queue, including runs that no alias names: the PATH
  shims' `tman run -- go test`, the Claude Code hook's rewrite, and bare `tman run`. An alias's
  `queue` and `--queue` still take precedence. `--no-queue` keeps a long-lived run, such as a dev
  server, out of the queue.

## [0.6.1] - 2026-10-02

The first release of this work. `v0.6.0` was tagged, but its release build failed on a Windows
test race (fixed here), so nothing was published under that version.

### Changed
- **`tman kill all` kills only your own runs.** It used to kill every live run in the shared
  store, so an agent stopping its own CI run also killed sibling agents' runs on the same machine.
  `all` now means the same project root (nearest `.tman.kdl` above the cwd, else the cwd) and, when
  both sides carry one, the same agent session (`CLAUDE_CODE_SESSION_ID`). Runs left alone are
  counted in the output. `tman kill all --everywhere` restores the machine-wide kill but needs a
  controlling terminal (`/dev/tty`); without one it refuses with exit 77 and names the command a
  person runs. `tman kill <id|name>` is unchanged and prints the owner when it is outside your
  scope. `tman list` gains a PROJECT column. Runs recorded before this change are scoped by the
  directory they ran in.

### Added
- **Per-command help.** `tman <command> --help`, `-h`, and `tman help <command>` print that
  command's usage, flags, and exit codes. `tman help <alias>` shows what an alias runs, from which
  `.tman.kdl`, and under which caps. The overview is built from the same table, so the two cannot
  disagree. Help is answered before anything else runs: it creates no `~/.tman`, runs no sweep, and
  works with a read-only store.
- **Help is enforced harmless.** A test reads `Main`'s dispatch switch and requires every command it
  handles to have a help entry, then asks each for help in a populated project and checks no file
  changes. This is the guard on the 0.5.1 bug where `tman init --help` wrote a `.tman.kdl`.
- **macOS and Windows meter the whole process tree.** Off Linux a sample read the root process
  alone. A runner that forked its workers, or a shell wrapping the real job, could pin every core
  or fill memory unseen by `--max-cpu` and `--max-mem`, and `--stall` read the busy descendants as
  silence. macOS now walks the tree through libproc and reads each process's rusage. Its cpu
  times are converted from mach time, which is not nanoseconds on Apple silicon. Windows walks
  parent pids from a Toolhelp snapshot, counting a child only if it started after its parent, which
  guards against pid reuse. Process states, and with them the kernel-io-wait signal, remain
  Linux-only.
- **Named queues: one line shared by every project on the machine.** Buckets are per project, so
  three checkouts each running `cargo build` never saw each other. Declare a queue once in
  `~/.tman/tman.kdl` (`queue "compile" { max-parallel 1 }`) and join it from any project with
  `tman run --queue compile` or an alias's `queue "compile"`. Runs are admitted in arrival order,
  after their own bucket admits them. `tman list` shows each waiter's place (`queued #2`). Waiters
  report their position once a minute and give up after the queue's `queue-timeout` (8h by
  default). Ctrl+C or `tman kill` ends a waiter and moves the next one up. An undeclared queue is
  refused with exit 127, and nested runs join no queue.
- **A run waiting for a slot is visible and cancellable.** A run queued behind `max-parallel` had
  no record while it waited: `tman list` could not show it, `tman kill` could not reach it, and
  Ctrl+C ended it with no trace. A waiter whose first claim fails is now recorded as `queued`,
  under the id it will run with. `tman list` shows it with pid `-`, and `tman status` shows how
  long it has waited. `tman kill` and Ctrl+C end it as `killed` with exit 130, and its child is
  never started. A queue timeout is recorded the same way, with the reason. A waiter whose tman
  died is closed out by the next sweep. A run admitted at once writes no queued record, so the
  uncontended path costs nothing extra.

### Fixed
- **`tman init --help` no longer scaffolds a project.** `init`, `clean`, `list`, and `status`
  skipped any argument they did not know and then did their job. `init --help` wrote `.tman.kdl`
  (and shims with `--shims`), `clean --help` ran the sweep, and `clean --dry-run` would have too.
  An argument a command does not take is now refused with exit 127, pointing at that command's
  help, as `run` and `kill` already did.
- **A run store tman cannot write is explained, not a crash.** When `~/.tman` (or `TMAN_HOME`) was
  read-only, full, or not a directory — the everyday case is an agent sandbox such as Codex's
  workspace sandbox, which mounts `$HOME` read-only — every supervised command aborted with an
  unhandled-exception stack trace and exit 134 before the command ran. tman now prints what it
  could not write, why it needs to, and how to fix it (make the directory writable in the sandbox,
  or point `TMAN_HOME` at a shared writable directory), and exits 74. A child already started when
  its record could not be written, or whose store went away mid-run, is killed rather than left
  running where nothing can list, reap, or cap it.
- **Upgrading the Python package runs the new binary.** The launcher cached the downloaded binary at
  one unversioned path and returned it whenever it existed, so after `pip install -U tman` it kept
  exec'ing the version first downloaded. The cache is now `~/.cache/tman/<version>/`. The binary
  is published by an atomic rename, so concurrent first launches can no longer exec a
  half-written file. Only the binary is read out of the archive. A leftover
  `~/.cache/tman/tman` from older versions is unused and can be deleted.
- **A run that finishes inside its deadline is never reported timed out.** The monitor checked
  for exit only before each 1s tick, then judged the caps after the tick. A root that exited during
  that second was measured as if still running, so a run ending just before `--max-time` could be
  recorded `timedout` with exit 124. The same applied to stall and resource kills. The runner now
  re-checks for exit after every tick.
- **A run whose root exits no longer hangs on a leftover holding its output.** Monitoring stopped
  when the root exited, and tman then waited without limit for stdout and stderr to close. A
  background process that kept them open (`sleep 60 &`, `server &`) held the run, its slot, and
  its caller indefinitely, ignoring `--max-time`. Output now drains for at most 2s after the root
  exits. On Linux the processes still holding the run's pipes are then found through
  `/proc/*/fd` and killed. On macOS and Windows tman says the output is still held and stops
  waiting. Descendants that let go of the output are left alone.
- **Deadlines no longer move with the wall clock.** `--max-time`, `--stall`, the CPU interval, the
  queue timeout, and the `--replace` wait all subtracted `DateTime.UtcNow` readings. An NTP step,
  a WSL clock resync after sleep, or a changed date could kill a run early or push its deadline
  out indefinitely. They are now measured on the monotonic clock. Wall-clock time only stamps the
  record for display.
- **`tman kill` and `--replace` no longer lose the killed outcome.** Both saved the record as
  killed while its runner was still alive, and the runner then saved its own stale copy over it, so
  the killed run returned 137 and read as `exited` with no reason instead of `killed` and 130. A
  live runner is now the only writer of its record: the killer leaves the reason in
  `~/.tman/runs/<id>.kill` before it kills, and the runner finishes the run from it. The reaper
  follows the same rule and leaves a live runner's record alone. A run whose runner is already
  dead is still finished by whoever finds it, using the requested reason.
- **A program that cannot be started leaves a digest and a record.** Opening the run log clears the
  previous digest, and a start that failed after that only closed the log. A run that exited 127
  therefore left no `.fail.log`, which is how a pass looks, and no record for `tman status` to show.
  It is now recorded in a new `startfailed` state with the reason, and its digest says why it did
  not start.
- **A failed write to the run log no longer skips the digest and leaks the log's lock.** The first
  write error marked the log closed, so finishing the run returned early: the digest was never
  written, and the writer and the `.lock` were released only when tman exited. Capture now stops
  with one stderr line, and the run still finishes normally. The digest is written with a
  `capture: failed` line, and the lock is released.
- **`tman init --shims` shims the aliases the config defines.** With a `.tman.kdl` already in
  place it still named the shims after what project detection would scaffold, so a config with a
  `custom` alias got a `test` shim that could only fail with "alias not defined", and no `custom`
  shim. Shims and their `.gitignore` entries now come from the config on disk.
- **An alias program written as a relative path runs from the config's directory.** An alias
  already runs there, and its args are read from there, but `command "./tool"` was resolved
  against the caller's directory first — so `tman tool` from a subdirectory looked for
  `sub/tool` and exited 127. The program is now resolved against the directory the child runs in.
- **A cap that cannot be read is refused, not dropped.** `max-time "bogus"` in `.tman.kdl` parsed
  as no deadline, so the command ran unbounded, exited 0, and printed nothing. A setting tman did
  not know (`max_time`), one given twice or with no value, a second `defaults` block, and a
  duplicate alias were skipped the same way. Every one is now a config error naming the file,
  block, key, and value, with exit 127. The flags and the config now share one parser per cap, so
  `max-parallel -1` and `max-cpu NaN` are refused in the file as they already were on the command
  line, and a figure past the type's range (`--max-time 99999999999h`) is refused instead of
  crashing.
- **A truncated `.tman.kdl` is refused.** A file cut off inside a block parsed as if its closing
  brace were there, and an unterminated `/* ...` comment silently swallowed everything after it, so
  an alias could lose its caps — or its existence — to a bad save and still run. Both are now parse
  errors naming the offset where the block or comment opened, and tman exits 127. Every parse error
  in `.tman.kdl` or `~/.tman/tman.kdl` now names the file too, not just the offset.

## [0.5.1] - 2026-09-28

### Added
- **`tman probe --pid <pid> [--start-ticks <ticks>]`.** Answers, for a caller that recorded a pid of
  its own, whether that pid is still the process it recorded. It uses the identity check the
  reaper applies to run records: on Linux a zombie is gone and a start-tick mismatch means the OS
  reused the pid. The verdict is the exit code — 0 mine, 1 gone, 3 not mine, 127 bad arguments —
  and is printed too. A caller that keeps step records can now discount a dead or pid-reused step
  instead of blocking a restart on it. Read-only: no signal, no kill, no housekeeping sweep. Without
  `--start-ticks` a live pid counts as yours, and off Linux `--start-ticks` is refused because no
  ticks are recorded there.

## [0.5.0] - 2026-09-24

### Changed
- **`--max-cpu` meters the whole process tree on Linux.** The cull read the root process's own
  CPU time, so a test runner that forks workers, or a shell wrapping the real job, could pin every
  core while the root slept at 0% and the cap never fired. It now uses the same tree sample the
  stall check already takes, over the interval the two samples span. Off Linux the sample is
  root-only, as the platform note states, so nothing changes there.
- **An alias runs in its `.tman.kdl` directory.** `tman test` from a subdirectory found the root
  config by walking up and then ran the alias where the caller stood, so an alias written relative
  to the config — `dotnet test tests/X.csproj` — failed with a path error from anywhere but the
  root. `tman <alias>` and `tman run --alias` now set the child's working directory to the config's
  directory and record it as the run's cwd. A bare `tman run -- cmd` keeps the caller's directory.
- **`tman kill --stale-only` is removed, and `kill` refuses a flag it does not know.** The flag
  could never match: every command sweeps before it acts, so a run whose runner is dead is reaped
  before `kill` sees the live set. Removing it alone was not safe, because `kill` skipped any `--`
  argument it did not understand — `kill all --stale-only` read as `kill all` and killed the runs
  the caller was trying to keep. An unknown flag now refuses the command with exit 127, as `run`
  already does.
- **A queued run says once that it is waiting, and once when it got through.** It printed
  `slots busy, waiting...` on every 2s poll — 150 identical lines over a full five-minute queue,
  burying the child's own output once it started. It now prints one line when it first has to wait,
  carrying the queue timeout, and `slot acquired after Xs` when a slot is taken. A run that never
  waits prints nothing.
- **The reaper records a vanished child as killed, not exited.** A `running` record whose child was
  gone by the time the sweep found it was marked `exited` with no exit code, which the digest and
  `tman status` read as a normal finish. It is now `killed` with `child exit status unknown`, the
  same state and reason the runner writes for a child it cannot vouch for.

### Fixed
- **A recycled pid tman may not open no longer crashes every command on Windows.** When Windows
  handed a recorded pid to a process this user may not open, `Process.HasExited` threw
  `Access is denied` out of the sweep, so every command, `tman ls` included, died. Liveness and
  identity are now one check that reads such a pid as another process's run and moves on. Only the
  failures the runtime documents for a gone or foreign process count as a verdict; anything else
  surfaces instead of reading as a dead run.
- **A clock step no longer turns a live Linux run into an orphan.** Linux recomputes a process's
  wall-clock start from boot time on every read, so an NTP step or a WSL clock resync after sleep
  moved it past the 2s tolerance and the next sweep treated the live run as a reused pid. Runs now
  record the start in clock ticks after boot (`/proc/<pid>/stat` field 22) and compare those.
  Records written by an older tman have no ticks and still use the wall clock until they are
  pruned. A zombie now reads as gone rather than running.
- **A kill hits only the process that was checked.** The kill reopened the pid after the identity
  check. On Windows the check and the kill now share one handle, and Windows never reuses a pid
  while a handle to it is open. When part of a tree survives a kill, each failure goes to stderr:
  the reaper keeps the record running for the next sweep, `--replace` refuses to start beside the
  survivor, and `tman kill` exits 1.
- **A child exiting under the monitor no longer crashes it.** A memory read taken just as the child
  exited threw out of the monitor loop. A child that exits before its start time can be read is
  now recorded without one, instead of with the current time, which could have matched a later
  process.
- **Two unnamed runs of one command no longer write one log.** A named run is alone by its name
  lock, but `max-parallel 2` admits two `tman run -- npm test` at once, both keyed to
  `.tman/npm.log`, and the second open truncated the first run's capture while it was still being
  written. The log is now claimed through a sidecar `.tman/<alias>.lock`, held the same way as the
  store's locks and never removed for the same reason. The second claimant gets no log and says so
  on stderr: `tman: .tman/npm.log is held by a concurrent run; this run's output is not captured`.
  The log file itself could not be the claim: .NET maps every share mode except `FileShare.None`
  to a shared lock on Unix, and `FileShare.None` would shut out the agent tailing the log.
  The lock is held until the digest is written, so a run claiming the slug as another finishes
  cannot end up with that run's digest beside its own fresh log.
- **A nested run opens no log.** A supervised process that re-enters tman already claims no slot
  because it is the same work as its parent, but it still opened its own log, so `tman test`
  running `dotnet test` through a machine-level PATH shim left `.tman/dotnet.log` beside
  `.tman/test.log` — the same output twice, and the second file is the one an agent reads by
  mistake.
- **The interrupt handler is installed before the child exists.** The run record was saved before
  `Ctrl+C` was subscribed, so a signal landing in that window took the .NET default: tman died with
  exit 130, an empty stderr, and a record left `running`. The 0.4.0 fix for interrupted runs
  covered every moment after that window; this closes the window itself.
- **A bad `--max-cpu` or `--max-parallel` is reported by name.** Every other cap flag answered a bad
  value with `bad --flag` and exit 127; these two went through bare parsing, so a typo got the
  framework's generic message and an oversized `--max-parallel` got an unhandled overflow with a
  stack trace. Both now say `bad --max-cpu` / `bad --max-parallel` and exit 127. `--max-cpu` must be
  a non-negative number and `--max-parallel` a non-negative integer; a negative count of slots is
  not a count of slots.
- **`tman init --gitignore` no longer ignores a directory that shares an alias name.** The shim
  generator already skips a shim when a directory of that name exists, but the gitignore step still
  wrote `/name` for every alias, so any repo with a `test/` or `build/` directory had that whole
  tree silently dropped from git. The entry (and its `.ps1` / `.cmd` companions) is now skipped
  whenever the alias name is an existing directory; `/.tman/` is unaffected.
- **The KDL parser honours `/-` slashdash and refuses `key=value` properties.** `/-node ...`
  became a node literally named `/-node`, so a slashdash-commented alias stayed live, and
  `max-parallel=4` became a bare string argument nothing read, so a cap in property form was
  ignored without a signal. Slashdash now discards the next node or value as the spec intends, and
  a bare `ident=value` token fails with a message naming the `key value` form to write instead.
- **The npm, PyPI, and PSGallery manifests say 0.4.0.** They still said 0.3.0 after the tag moved;
  the publish workflow overwrites the field at release time, so nothing shipped wrong, but the
  checked-in files should state the version actually released.

## [0.4.0] - 2026-08-23

### Added
- **Run logs and failure digests.** A run governed by a `.tman.kdl` now writes its combined output
  to `.tman/<alias>.log`, and a run that does not pass writes `.tman/<alias>.fail.log` — the
  outcome, the command, every failure line in the log with its following context, and the tail.
  The console was previously the only place a suite's output went, so an agent that ran the tests
  and then compacted or handed off its context had no way back to *which* test failed except
  re-running the whole suite. Both files are cleared at the start of every run and the digest is
  deleted when a run passes, so a digest on disk always describes the most recent run of that
  alias; a stale one would read exactly like a real one. Failure lines are matched by shape
  (`FAILED`, `--- FAIL:`, `[FAIL]`, `Failed!`, `panic:`, `not ok`, `: error `, `×`, `●`), and a
  runner nothing matches degrades to the tail rather than to an empty file. `tman init --gitignore`
  now ignores `.tman/`.

### Fixed
- **An interrupted run no longer reports 0.** Ctrl+C reaches the child through the terminal at the
  same moment it reaches tman, and a test runner that traps SIGINT and shuts down cleanly exits 0 —
  which tman then passed through as if the run had finished. An interrupt is now handled like any
  other cancellation: the tree is killed, the record says `interrupted`, and the exit code is 130.
  A child whose exit status tman cannot read is reported the same way instead of defaulting to 0.

## [0.3.0] - 2026-07-26

### Added
- **`tman hook pretooluse` — a Claude Code PreToolUse hook.** PATH shims only cover commands typed
  in a project that has them installed; an agent that runs `npm test`, `go test`, `pytest` or
  `dotnet test` directly bypasses supervision entirely. Registered as a hook, tman rewrites those
  bare commands to run under `tman run` in projects it governs, so an agent's test run gets the
  same caps and reaping as yours. It **never blocks a command**: if anything about the rewrite is
  uncertain — no `.tman.kdl`, an unrecognised command, or a tman binary it cannot positively
  identify — it passes the command through untouched and says why on stderr. The rewrite targets
  this binary's own absolute path, so a hook installed from one tman cannot silently route work
  through another.

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
  `~/.tman/runs` therefore keeps one `.lock` per bucket it has seen for the name dedup, plus one per
  parallel slot that bucket has ever handed out — see **Documented** below for what that costs and
  how to clear it.
- **`tman run --replace` waits for the run it killed to release the name** instead of taking the
  name from it, and reports that it is not replacing anything if the lock is still held when the
  queue timeout runs out.
- **`tman init` no longer scaffolds a test alias that passes without running anything.** It wrote
  `echo "replace me"`, which exits 0, so a project that adopted tman and never edited the alias got
  a green suite that ran nothing — found byte-identical in 20 repos. When no test command can be
  detected, `tman init` now emits a commented-out template and no alias, so `./test` fails loudly
  with exit 127 naming the undefined alias instead of reporting a pass.

### Fixed

- **`tman clean` could report removals that never happened.** The delete helper swallowed a refused
  or lost-race delete while every caller counted the attempt, so the printed count could name files
  still on disk. It now reports whether it acted, and callers count only real removals.

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
- **`--stall` killed runs that were blocked on the kernel.** A process in uninterruptible sleep —
  waiting on disk or a network filesystem — produces no output and burns no CPU, so it looked
  identical to a hang and was killed at the stall bound. Kernel io-wait now counts as activity.
  Note the honest limit, documented rather than papered over: a process wedged in that state
  *permanently* now looks alive forever, and only `--max-time` bounds it.
- **A slot that could not be opened at all was reported as a busy slot.** An IO fault — no space,
  a missing directory, a dangling symlink — was indistinguishable from contention, so the run
  waited out its whole `--queue-timeout` and then blamed a full bucket for a problem that was
  nothing of the kind. The fault is now surfaced as itself.

### Documented

- **Where tman stops.** It supervises one machine — a laptop, or a department-sized CI host with a
  handful of runners — and is not a fleet scheduler. Stated in the README and on the docs site so
  the boundary is checkable rather than folklore.
- **What lock files cost, and how to clear them.** Nothing in tman removes a `.lock`; each occupies
  one filesystem block, so budget ~4 KB per bucket. A development machine settles at a few dozen
  kilobytes. A CI host whose workspace path carries a build number seeds a new bucket per build and
  accumulates on the order of 20 MB a year at twenty builds a day — clear it with
  `rm ~/.tman/runs/*.lock` while no runs are live. tman does not reclaim them itself: making that
  safe against a concurrent claim costs every `tman run` a store-wide lock, which is a bad trade for
  a few megabytes a year.

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

[Unreleased]: https://github.com/standardbeagle/tman/compare/v0.6.1...HEAD
[0.6.1]: https://github.com/standardbeagle/tman/compare/v0.5.1...v0.6.1
[0.5.1]: https://github.com/standardbeagle/tman/compare/v0.5.0...v0.5.1
[0.5.0]: https://github.com/standardbeagle/tman/compare/v0.4.0...v0.5.0
[0.4.0]: https://github.com/standardbeagle/tman/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/standardbeagle/tman/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/standardbeagle/tman/compare/v0.1.4...v0.2.0
[0.1.4]: https://github.com/standardbeagle/tman/compare/v0.1.3...v0.1.4
[0.1.3]: https://github.com/standardbeagle/tman/compare/v0.1.2...v0.1.3
[0.1.2]: https://github.com/standardbeagle/tman/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/standardbeagle/tman/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/standardbeagle/tman/releases/tag/v0.1.0
