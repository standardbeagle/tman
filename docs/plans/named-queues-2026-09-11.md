---
written_at: 2026-09-11
source_event: session_01SvcLJdbxneiDVKUPHKJj8c
status: planned, not yet persisted to worktrack (server unreachable at planning time)
template: tman-slice-v3
verify: sh -c "exec ./test"
---

# Named queues: ordered shared admission across projects

## Epic

GOAL: a machine-level config declares named queues once; any project's alias or `tman run --queue`
joins one by name, and runs in that queue are admitted in arrival order under the queue's own
max-parallel and timeout. Three Rust and two C++ checkouts naming `compile` build one at a time
without anyone watching.

BASELINE: 290 tests green at b685e77. Admission is per bucket (`name-or-command @ dir`) only; two
`cargo build` runs in two checkouts never see each other.

DONE-CONDITION: in a fresh TMAN_HOME with `queue "compile" { max-parallel 1 }`, three `tman run
--queue compile -- sh -c 'echo $N; sleep 2'` started 200ms apart from three different directories
print 1 2 3 in order with no overlap, `tman list` shows the two waiters as queued with positions
1 and 2, and Ctrl+C on a waiter exits 130 with no child record.

INTEGRATION-POINTS: Program.GatedRun (claim order), Store.TryAcquireSlot (slot key), RunRecord
(new state and fields), Runner.RunAsync (interrupt subscription lifted earlier), Config (machine
file), Canon/list rendering, README + CHANGELOG.

## Decisions

1. Queue config lives in `~/.tman/tman.kdl` under the store root that TMAN_HOME already relocates.
   Projects only name a queue; they never define one. A named queue with no declaration is a
   FormatException, exit 127.
2. Admission reuses the slot claim: a queue slot is `Store.TryAcquireSlot("queue:<name>", n)`.
   The never-unlink invariant and dead-holder takeover carry over unchanged.
3. FIFO comes from run records, not new files. A waiter saves its record as `Queued` with an
   arrival stamp before its first claim attempt and may claim only when no live earlier queued
   record exists for that queue (order by arrival, then id). Waiter liveness is the existing
   runner pid + start-time check. Records are already pruned by retention.
4. Claim order is fixed: bucket slot, then queue slot. Nested runs (TMAN_RUN_ID) skip both.
5. Queue timeout default is 8h, announced when the wait starts; `--queue-timeout` overrides.
   Unbounded is rejected because it hides a wedged queue.
6. Not in scope: load-aware admission, command matching, ticket files, cross-machine anything.

## Slices, in order

### 1. Queue declaration and admission

Flow: load `~/.tman/tman.kdl` (Kdl.Parse, same subset), `queue "<name>" { max-parallel N;
queue-timeout T }`. `queue "<name>"` node on an alias; `--queue <name>` on run. After the bucket
slot is held, claim a queue slot with the queue's timeout. First commit extracts GatedRun's wait
loop into one named admission function taking the key, count, timeout, and a description, then
the second call site is added.

Acceptance criteria:
- two runs naming one queue with max-parallel 1 serialize end to end (out-of-process, fresh
  TMAN_HOME, assert no overlap by timestamps the children print)
- a run in a second project directory joins the same queue
- a run naming no queue is unaffected; nested runs claim no queue slot
- `--queue undeclared` exits 127 with the message naming the config path
- README gains the machine config section; CHANGELOG Unreleased entry

Scope: Config.cs, Store.cs, Program.cs, Queue.cs (new), tests/Tman.Tests/, README.md, CHANGELOG.md

### 2. FIFO order and visibility

Flow: RunRecord gains `State = Queued`, `Queue`, `QueuedUtc`. The waiter saves before its first
claim; admission is refused while a live earlier queued record for that queue exists. Heartbeat
saved each tick while waiting; one stderr line per minute: queue name, position, elapsed.
`tman list` renders queued runs with a position column computed at list time.

Acceptance criteria:
- three waiters started in order are admitted in that order (Barrier rendezvous, per context.md)
- a waiter whose runner pid is dead or reused is skipped and reaped as before
- `tman list` output pins the queued state and position (HelpText/list test style)
- heartbeat advances while queued (record read from disk between ticks)

Scope: Store.cs, Program.cs, Queue.cs, Canon.cs, tests/Tman.Tests/, README.md

### 3. Cancellation while queued

Flow: Ctrl+C during the wait ends the run `Killed` with reason `cancelled while queued`, the
child never starts, no log and no digest. `tman kill <id|name>` on a queued run signals the
runner pid (there is no child pid) and the runner ends the same way. The CancelKeyPress
subscription moves from Runner.RunAsync into the admission function's caller so it covers the
wait; Runner keeps a thin delegate.

Acceptance criteria:
- out-of-process: SIGINT to a queued tman exits 130, record is Killed with the reason, no child
  record, no `.tman/*.log`
- `tman kill <name>` on a queued run: same outcome, and the slot it never held is untouched
- an interrupted waiter frees its position: the next waiter is admitted
- CHANGELOG Fixed/Added entry; README kill row mentions queued runs

Scope: Program.cs, Runner.cs, Queue.cs, tests/Tman.Tests/, README.md, CHANGELOG.md

## Adversarial notes

- Timeout: unbounded rejected (hides a wedged queue); 8h default with the wait announced.
- Ordering tie: same-instant arrivals order by id, which is stable across readers.
- Visibility race: a waiter that saves after another scanned is admitted behind it, which is
  arrival-by-visibility; acceptable and documented.
- Leanness: no ticket files, no load sensing, no per-command matching.
