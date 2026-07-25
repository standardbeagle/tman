---
written_at: 2026-07-25T21:44:03Z
source_event: task:01KYD8STXXTKKN5RZ6G7YCVF69
module: tman
category: concurrency-and-workflow-issues
confidence: high
form: constraint
sources:
  - task:01KYD8STXXTKKN5RZ6G7YCVF69#review-round-1-blocker    # 43/400 double claims
  - task:01KYD8STXXTKKN5RZ6G7YCVF69#task_annotation_v1-round-2
  - task:01KYD8STXXTKKN5RZ6G7YCVF69#review_annotation_v1-round-2  # independent probe, 52/400
  - git:f47bdb9
  - git:e26b8f2
  - git:81c46fc
  - task:01KYD8VQ8RZE581VG3VZQTMMM3#task_annotation_v1-attempt-1        # §3, §4, §5 recurred
  - task:01KYD8VQ8RZE581VG3VZQTMMM3#review_annotation_v1-attempt-1     # §3, §4, §5 recurred
tags: [check-then-act, atomic-claim, file-lock, race-test, barrier, false-green-gate, path-resolution, reviewer-verification, missed-reuse]
status: steering
recurrence: 2
recurrence_note: >
  §3 (verify the gate can go red), §4 (relative argv[0] resolves through PATH) and §5 (reviewer
  re-derives independently) all recurred on task:01KYD8VQ8RZE581VG3VZQTMMM3, 2026-07-25.
  §3 and §4 were promoted to .claude/rules/honest-failure-and-verification.md on that recurrence
  under the explicitly-project-wide branch of the promotion gate.
---

# Atomic slot claim, and gates that can actually go red

Task: `max-parallel slot gate is check-then-act and fails 100% under simultaneous launch`.
Three workflow instances, one reviewer rewind, three RED/GREEN commit pairs.

## Lesson (one line)

A race is not fixed by adding a check before the racy step — it is fixed by **deleting the
step**, and none of that is provable until the test gate itself can go red.

---

## 1. Delete the check-then-act sequence; do not guard it  · form: constraint · confidence: high

The first fix replaced run-counting (`Reaper.LiveInGroup(group).Count < maxPar`) with
`FileMode.CreateNew` — right direction, but its stale-reclaim branch was `BreakLock` (unlink)
then `TryClaimLock` (create): **a second check-then-act**. Two threads each deleted the file the
other had created; both "held" one slot. Measured 43/400 (reviewer), 34/38/28/37 over four runs
(implementer), 52/400 (reviewer's independent re-derivation).

The durable fix removed unlinking from acquisition entirely — the **OS handle is the claim**:

```csharp
new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
```

`ReleaseLock` is `Dispose()` only. Kernel release on process death became the staleness signal
that the pid/start stamp had been approximating.

**Apply when** any fix adds a verification step around a racy sequence. Break-then-create is two
steps and cannot be made atomic on POSIX or Windows. The reviewer's own `fix_hint` here
(read back the stamp and confirm own pid) **would have moved the window rather than closed it** —
a fix_hint is a hypothesis, not a spec. Reviewers should phrase hints as outcome constraints.

**Prevention:** state one invariant every actor must obey, then audit each actor against it.
Here: *"a lock file is only ever unlinked by something holding it exclusively."* That audit forced
the `PruneStaleLocks` change (now `FileOptions.DeleteOnClose` under an exclusive hold), which was
in no acceptance criterion and was found unprompted.

## 2. Concurrency tests in this repo need a Barrier  · form: procedure · confidence: high

`GatedRun` runs synchronously into `Runner.RunAsync`, which starts the process and **saves the
record before its first `await`**. So `Task.WhenAll` over it serializes callers: caller 1 has
already written its record before caller 2 reads. **The first race test passed against the buggy
code.**

Recipe: `new Barrier(n)` + `Task.Run(() => { barrier.SignalAndWait(); return start(); })`, and
assert on **peak overlap of recorded `StartedUtc`/`HeartbeatUtc` intervals**, not wall-clock —
machine-speed independent. For reclaim races use raw `Thread`s behind `Barrier(2)` × 400 rounds.

Probabilistic-red tests must be **run repeatedly to demonstrate reliably red** (never accidentally
green), then repeatedly green after the fix.

**Load-bearing:** `Runner.RunAsync` saving the record before its first await is depended on by both
the dedup and slot paths. A refactor moving that save later silently reopens the window.

## 3. Verify the gate can go red before trusting its green  · form: constraint · confidence: high

Two independent false-greens in one task:

- Workflow template `slice-default` ran bare `dotnet test` at repo root → resolved `tman.csproj`
  (not a test project) → **exit 0 in ~3s having run zero tests**. Confirmed: `.tman.kdl` alias
  `test` points at `tests/Tman.Tests/Tman.Tests.csproj`; the root project is not it.
  Superseded by template `tman-slice-v2`.
- `scope-check` passed with `checkedFiles: []` because the implementer had already committed —
  diff vs HEAD was empty, so it **passed vacuously**. Real scope enforcement comes from
  review-context, not that step.

**Prevention:** a gate that has never been observed failing is not a gate. Revert the production
change and watch it fail before believing the pass.

> **Recurred 2026-07-25** on `task:01KYD8VQ8RZE581VG3VZQTMMM3`. Both false-greens repeated: the
> `scope-check` step again recorded `checkedFiles: []` and passed vacuously (the implementer commits
> before the gate runs, so the gate diffs against an empty HEAD delta) — confirming this is a
> structural defect of the `tman-slice-v2` template, not a one-off. And bare `dotnet test` at repo
> root was re-confirmed to exit 0 in ~3s having run zero tests, now filed as
> `01KYDMEXP3GH88T4RBNEJEKEV4`. **Promoted to
> `.claude/rules/honest-failure-and-verification.md`.**

## 4. A relative argv[0] resolves through PATH, not cwd  · form: constraint · confidence: high

A command step's `./test` resolved to `/home/beagle/.local/bin/test` (a uv-installed hass-mcp
pytest symlink), not the repo shim. Use `["sh", "-c", "exec ./test"]`. Any bare relative argv[0]
in a command step carries this exposure.

> **Recurred 2026-07-25** on `task:01KYD8VQ8RZE581VG3VZQTMMM3` — the implementer recorded
> `sh -c "exec ./test"` as a `patternsUsed` entry confirmed necessary for every suite run of that
> task. **Promoted to `.claude/rules/honest-failure-and-verification.md`.**

## 5. Reviewers must independently re-derive, not accept reported numbers  · form: procedure · confidence: high

Both review rounds rebuilt the evidence in a throwaway worktree with only the production file
reverted (`Store.cs` → `f47bdb9`; `Program.cs`/`Reaper.cs` byte-identical, so the revert was
clean) and observed the red themselves: PeakOverlap 3 vs expected 2; 52 double-claims / 400.
Numbers differed from the implementer's — the shape held. Round 2 also **corrected** an
implementer claim (see §7). This is the workspace norm, not an extra.

> **Recurred 2026-07-25** on `task:01KYD8VQ8RZE581VG3VZQTMMM3`, in a cheaper single-file form worth
> naming as the standard recipe: the reviewer reverted **only** `Detect.cs` to `81c46fc` in a
> **throwaway git worktree** with the new tests at HEAD, and observed `Failed: 6, Passed: 137` —
> matching the implementer's claim exactly. Cost is one worktree and one suite run; it converts a
> reported red into an observed one. Held at steering (recurrence 2, gate is 3) but this is the
> shape to reuse.

## 6. Probe the platform primitive in a throwaway project before building on it  · form: procedure · confidence: high

Done by the implementer and independently re-run by the reviewer (6 probes). This is what caught
that `File.Exists` returns **true for a dangling symlink** on .NET/Linux — so the reviewer's
suggested `when (File.Exists(path))` guard alone still misreports a broken path as busy. Final
classifier excludes `FileNotFoundException`/`DirectoryNotFoundException` **and** checks
`File.Exists` **and** retries once (a holder can release between the failed open and the check —
otherwise a swallowed error is traded for an invented one).

Narrowing a swallowed exception can introduce a spurious failure if the classifier races the thing
it classifies. Everything fails closed: `IsLockStale` returns not-stale on unparseable/truncated
input; the retry rethrows rather than degrading to "busy".

## 7. Verified platform semantics (reference)  · form: fact · confidence: high

Probed twice independently on this platform (Linux/.NET):

- `FileShare.None` is **flock-backed**, enforced **cross-process and same-process** (it conflicts
  between two open file descriptions even within one process).
- The kernel releases the exclusive hold on holder death.
- A **refused** `FileShare.None` + `DeleteOnClose` open does **not** unlink the file.
- `DeleteOnClose` unlinks by **path, not inode** — it will delete an inode recreated at that path.
- `File.Exists(dangling symlink)` → `true`.
- Lock-conflict `HResult` is a raw errno (Linux 11 / macOS 35) — residual bare EIO on an existing
  lock file still reads as busy.

**Correction to a widely-repeated claim:** "in-process xUnit cannot prove a cross-process lock" is
**more pessimistic than this platform requires** — flock conflicts across open file descriptions,
so the in-process suite does exercise the real semantics. flock is still advisory, so the
4-process smoke (`peak record overlap 2`, two waves, no leftover `.lock` files after sweep) stays
worthwhile. Earlier steering asserting the strong form is superseded by this entry.

## 8. Fix the primitive once, then migrate every caller  · form: constraint · confidence: high

**Still open in tree.** The dedup name lock in `Program.cs` `GatedRun` (~line 158/169) was never
migrated: it still does `FileMode.CreateNew` → `IsLockStale` → `Store.BreakLock` → retry, and
still unlinks on release. That is exactly the sequence `e26b8f2` proved racy — *the last
unlink-then-create sequence left in the codebase*.

Worse in combination with §7: `PruneStaleLocks` unlinks by path under `DeleteOnClose`, while
`BreakLock` plain-unlinks without holding. Sweep takes a stale dedup lock → a second runner
`BreakLock`s and `CreateNew`s it → the sweep closes and unlinks the **second runner's live lock** →
a third same-name run is admitted. Pre-existing; ruled advisory, not a blocker.

Blocker to reuse: `Store.TryClaimLock` is private/static, so the fixed primitive cannot be shared
without widening it. `Store.BreakLock` survives **solely** to serve the legacy path.

**Prevention:** when a race fix converges on a primitive, enumerate every other caller of the old
shape in the same commit range and either migrate or file them. Leaving one behind keeps the class
of bug alive and keeps the superseded helper alive with it.

## 9. Delete the broken helper, do not keep it as a fallback  · form: constraint · confidence: high

`Reaper.LiveInGroup` was deleted with the counting gate rather than retained as a diagnostic — its
only caller was the broken gate, and *keeping a counting helper invites re-gating on it*. Matches
the project-wide no-fallbacks rule; worth restating because the pressure to keep it was real.

---

## Open follow-ups surfaced (not yet tasks)

1. Migrate `Program.cs` `GatedRun` dedup loop onto `Store.TryClaimLock`; drop `BreakLock` entirely (§8).
2. `TryAcquireSlot_ReclaimsTheSlotOfADeadRunner` is weak — it writes a dead-pid stamp the new claim
   path never reads, so it only proves a released file can be reopened. Assert instead that a slot
   held by a **live foreign holder** is not handed out.
3. Promote the platform probe into a skipped-by-default xUnit theory.
4. Ban bare relative `argv[0]` in workflow command-step templates.
5. `StampLockOwner` now writes a stamp on every claim that only the sweep reads.
6. README.md:76 and CHANGELOG.md:17,:83 still describe the gate as "counting live runs".
