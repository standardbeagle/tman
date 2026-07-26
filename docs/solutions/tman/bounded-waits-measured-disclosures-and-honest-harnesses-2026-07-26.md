---
written_at: 2026-07-26T17:05:00Z
source_event: task:01KYE1DKM052DJZ33AFDJ338W6
module: tman
category: failure-paths-and-measurement-honesty
confidence: high
form: constraint
sources:
  - task:01KYE1DKM052DJZ33AFDJ338W6#workflow-implement-attempt-1    # host destroyed, nothing landed
  - task:01KYE1DKM052DJZ33AFDJ338W6#task_annotation_v1-attempt-2
  - task:01KYE1DKM052DJZ33AFDJ338W6#review_annotation_v1-attempt-1  # 2 blockers, rewind
  - task:01KYE1DKM052DJZ33AFDJ338W6#task_annotation_v1-attempt-3
  - task:01KYE1DKM052DJZ33AFDJ338W6#review_annotation_v1-attempt-2  # PASS
  - task:01KYEFGACRYVBP1WJ0PGEHRPYM#task_annotation_v1-attempt-1
  - task:01KYEFGACRYVBP1WJ0PGEHRPYM#review_annotation_v1-attempt-1  # 2 blockers, rewind
  - task:01KYEFGACRYVBP1WJ0PGEHRPYM#task_annotation_v1-attempt-2
  - task:01KYEFGACRYVBP1WJ0PGEHRPYM#review_annotation_v1-attempt-2  # PASS
  - git:ac50fae..814ede7   # full range, both slices
  - git:cb908d8            # refactor: exactly-one race, count only real deletes
  - git:8cd32db            # GREEN — reclaim lock files nothing can reach
  - git:71b6fef            # RED — what a claim that cannot take the gate owes the user
  - git:2fda627            # GREEN — claim-gate wait fails with a diagnostic, not a core dump
  - git:410dd78            # SegmentedWriter write-boundary capture bound
  - git:428620a            # ordered proximity pattern, sweep invariants 3+4 merged
  - git:814ede7            # merge landing the worktree-isolated slice on main
tags: [bounded-wait, entry-point-catch, exit-code-table, in-process-probe, flock-exec-holder, disclosure-overstatement, comment-as-claim, fix-hint-rebuttal, presence-vs-absence, capture-bounding, harness-integrity, stale-worktree, mtime-rebuild-skip, supervisor-shares-subject-store, surviving-mutant-declined, doc-surfaces, concurrent-slice-scope, worktree-landing]
status: steering
recurrence: 1
recurrence_note: >
  §6.1 (isolated worktree seeded at a stale head) is at its THIRD occurrence — twice during
  task:01KYD9AE3TWX4VM4F1R6ZCTJMY's review, once here on task:01KYEFGACRYVBP1WJ0PGEHRPYM
  implement attempt 1 — and carries a project-wide flag. Flagged for operator decision under
  "Promotion candidates flagged, not taken" rather than promoted, because the reviewer's own
  recommendation is to fix the worktree-seeding defect as its own task. §2 (a checkable sentence
  in a code comment overstating what was measured) is at three consecutive review ROUNDS but
  within one task lineage; promote on the next independent recurrence.
---

# A wait that stops waiting, a comment that overstates, and a harness that measures the wrong tree

> **Superseded in part, same day.** The claim gate and `Store.ReclaimUnusedLocks` this document
> analyses were removed as out of scope: the accumulation they addressed is a CI-host disk figure
> (~20 MB a year), and making reclamation safe cost every `tman run` a store-wide lock. See rule 0
> in `.claude/rules/honest-failure-and-verification.md`. The lessons below stand on their own — a
> bounded wait must fail with a diagnostic, a comment's guarantee is a claim, a harness must be
> proven to measure what it claims — but the code they cite is no longer in the tree, and the
> follow-ups about per-file gating and the unpinned wall-clock bound are moot.

Tasks: **`Lock files now accumulate forever`** (`01KYE1DKM052DJZ33AFDJ338W6` — implement attempts
1–3, the budget ceiling; review attempts 1–2, one rewind) and **`HelpTextTests guards known-bad
wording`** (`01KYEFGACRYVBP1WJ0PGEHRPYM` — implement attempts 1–2, review attempts 1–2, one
rewind). Both on template `tman-slice-v3`, closed together on 2026-07-26. Range `ac50fae..814ede7`.

## Lesson (one line)

Every new give-up path, every explanatory sentence, and every verification harness is a claim that
must be measured — a fix that ends a wait, a comment that explains a limit, and a worktree that
reports a green are each capable of being confidently, silently wrong.

This document records only what these two rounds **added**. Where
`.claude/rules/honest-failure-and-verification.md` rules 1–5 or an existing module doc already
covers a fact, the section says so and records the new evidence or the new shape alone.

---

## 1. A bounded wait is not finished when it stops waiting · form: constraint · confidence: high

`Store.OpenClaimGate` waited up to `ClaimGateWait` (10s) by retrying inside
`catch (IOException) when (IsHeldByAnother(...) && DateTime.UtcNow < deadline)`. At the deadline
**the filter stopped matching**, so the raw `IOException` escaped — and `Program.Main` catches only
`FormatException`. Both `tman run --name probe` and `tman clean` died `Aborted (core dumped)`,
exit **134**, stack trace at `Store.cs:268`. A new crash path in tman's primary command, shipped by
a slice whose whole purpose was fixing a concurrency defect.

The 233-test suite was green over it. It was found because the **reviewer ran the real Release
binary with the gate held externally** (`flock -x <gate> sleep 30`) — something no test did. The
doc comment on the wait claimed *"failing with that in hand beats hanging on it"* while the code
failed with **nothing** in hand: the comment described the intended design, the catch filter
implemented a different one.

**Apply when** adding any bounded wait, retry loop, deadline, or backoff to a command.

**Prevention**

- **A new give-up path owes two answers: what does the user see, and what does the shell see.** A
  `tman:` line on stderr and a code already in the exit-code table. 124 was chosen over 130 because
  *nothing was killed — the command never started*.
- **Throw a type the entry point already maps; do not widen the entry point's catch.** Reviewer
  mutation M5 proved the narrowness is load-bearing and survived the suite: widening the second
  filter to a bare `catch (IOException)` would misreport a genuine filesystem fault as a gate
  timeout with the wrong exit code.
- **When a criterion offers "bound it by the existing flag OR document the new bound", check the
  two waits are on the same axis first.** `--queue-timeout` bounds queueing behind other runs of
  *your own bucket*; the gate is contention with a concurrent `tman clean`, which has no such flag.
  Binding only the claim side gives two commands different bounds for one wait, and turns a user's
  5m default into a 5m silent block. **Reusing a user-facing timeout for an unrelated wait is worse
  than a second documented bound, because the flag then means two things.**
- **The exit-code table is load-bearing documentation.** A new failure mode means picking from
  124/125/126/127/130 and updating the table's wording, not inventing a code.

Adjacent to `atomic-slot-claim-and-honest-gates-2026-07-25.md` §6 ("narrowing a swallowed exception
can invent a new failure"), but the shape here is the inverse and is new: the classifier did not
*misclassify*, it **stopped classifying at a boundary it defined itself**, and the fault fell
through into an entry point that had no branch for it.

## 2. A checkable sentence in a comment is a claim, and is measured last · form: constraint · confidence: high

`named-invariants-over-accreted-predicates-2026-07-26.md` §5 already establishes that a **surviving
mutant's stated cause** is a claim requiring independent verification, and §9 that a README
assertion is load-bearing. What is new: the same standard applies to **inline comments and
docstrings**, and that is exactly where it keeps failing.

Three consecutive review rounds, each with a **truthful headline disclosure** and a false
explanatory sentence beside it:

| Round | Sentence | Falsified by |
|---|---|---|
| HelpText attempt 1 | *"Nothing short of stating in-place reuse satisfies it."* | Mutant `A dead holder's lock file is **removed in place**…` passes — a bare `in place` unrelated to any removal verb. |
| HelpText attempt 1 | *"A counting gate says `while`, a slot gate says `until`."* | The connective can be supplied by any later clause, so it does not discriminate. |
| HelpText attempt 2 | *"No proximity form separates them; the only regex that excludes them would ban the word the wrong text happens to use."* | The reviewer derived an auxiliary-adjacency form naming no forbidden word that keeps the true text GREEN and turns M-A RED. A positive form **does** exist. |

In each case the *operative conclusion* held and the *quantified sentence* did not. The reviewer
flagged this `systemic: true` in both rounds.

**Apply when** writing any comment that explains a test's reach, a survivor's cause, or a limit.

**Prevention**

- **Audit every checkable sentence in comments and docstrings with the measurement you applied to
  the headline claim.** Prose written to *explain* a limit is habitually held to a lower
  evidentiary standard than the limit itself.
- **Prefer sentences that state what was measured over sentences that quantify over all possible
  fixes.** "No form exists", "nothing short of X satisfies it" — the universal claims are the ones
  that keep turning out false. The corrected third row reads: *proximity forms can be found that
  exclude any GIVEN counting-gate wording, but none captures the invariant, because the falsehood
  lives in a trailing clause the regex is not required to read* — and cites the reworded gate that
  defeats the candidate.
- **A comment that overstates a test's reach is the same artifact class as a gate that cannot go
  red** (rule 2). It survives because it produces no signal.

## 3. A `fix_hint` rebutted by measurement, and accepted · form: procedure · confidence: high

`atomic-slot-claim-and-honest-gates-2026-07-25.md` §1 already states that a `fix_hint` is a
hypothesis, not a spec. What was missing there — and what these rounds supply — is a **worked case
of an implementer rebutting a hint by measurement and the loop accepting it**, plus the mirror case
of a refusal upheld.

Review 1 of the HelpText slice offered two canaries for the polluted-capture blocker: pin the
capture's final non-blank line, or assert `run flags:` occurs exactly once. The implementer
substituted a `SegmentedWriter` keeping each `Write`/`WriteLine` its own segment (`410dd78`).
Review 2 measured **both suggestions were worse**:

- the final-non-blank-line literal **is** the sweep paragraph's own last line, so every sweep
  mutation (M-C, P0, TASK-2) would have failed as *"polluted capture"* rather than as a false
  claim — reporting the wrong cause;
- `"run flags:" occurs exactly once` **does not fire on the measured P1 case at all**, so it would
  not have closed the blocker.

The substitution is strictly stronger: it **excludes** foreign output rather than detecting it, so
neither a false pass nor a spurious failure occurs, and reviewer probe N9 confirmed it fails
*loudly* rather than vacuously if production splits the document across writes.

Mirror case, lock slice: the implementer **declined** the suggested blanket `IOException` catch in
`Main`, arguing it would replace an unexpected fault's stack trace — its only diagnostic — with a
terse line. Upheld, and review 2 measured the stronger reason: the reachable faults are
`UnauthorizedAccessException`, which the suggested catch **would not have caught**.

**Apply when** an implementer wants to substitute for, or decline, a reviewer's proposed remedy.

**Prevention**

- The diagnosis in a hint binds; the remedy does not.
- **A substitution or refusal ships with the measurement that the hint's own remedy was worse — not
  with an argument that it was harder.** Both here did, and the loop accepted both.
- The counterweight is unchanged (rule 4): **check every clause as a checklist and report per
  clause.** The lock slice reported its five-clause hint as `b1_i`…`b1_v`, which is what let the
  reviewer re-derive RED by removing either half of the fix.

## 4. Absence→presence conversion trades false-failure for false-pass · form: constraint · confidence: high

Attempt 1 of the HelpText slice rewrote `DoesNotMatch` assertions over an unbounded region into
presence assertions, and **wrote down as a durable lesson** that *"presence checks are monotone and
cannot be falsified by added text… the Console-serialization problem dissolved instead of needing a
fix."* That lesson was recorded before it was measured, and it is half wrong. Monotonicity removes
false *failures*; in the pass direction it is precisely the hazard. Measured at `cc242ee` with the
sweep paragraph mutated false:

| Probe | Foreign output on the captured writer | Result |
|---|---|---|
| P0 | none | RED (correct) |
| P2 | innocuous line | RED (correct) |
| **P1** | line carrying `reclaimed … in place` | **GREEN — false pass** |
| P3 | true text + foreign line | GREEN (correct) |

**Apply when** a test reads process-global state — `Console.SetOut`, cwd, environment variables —
or when rule 4 is applied to a *test* rather than to a guard.

**Prevention**

- **Bound the region structurally, then assert presence inside it.** Detection alone forces a
  choice between a spurious failure and a false pass; bounding produces neither.
- `Console.SetOut` wraps in a `SyncTextWriter` that forwards `WriteLine(string)` intact, so a
  segmenting `TextWriter` is a viable seam **without touching production code**.
- **Prove a newly added canary can fire, on more than one code path, before trusting it.** Probe C1
  (foreign write carrying the document token) and reviewer-originated N7 (foreign writer emitting
  char-at-a-time, exercising the accumulate-into-`_pending` path rather than `WriteLine`) each
  turned all four tests RED. A canary added without watching it fire reproduces the defect it was
  added to fix.
- **Where N invariants describe one sentence, N independent searches over the containing region can
  be satisfied by N unrelated clauses — including a clause stating the negation.** Both blockers
  and one advisory were that shape. The fix is *one ordered pattern over the sentence* (`428620a`),
  not a tighter alternation on each search.
- **An alternation is an implicit OR whose weakest branch defines the invariant.** `in place` OR
  `not removed` looked like two proofs of one fact and was one proof plus a keyword any sentence
  could carry.

## 5. Not every surviving mutant should be killed · form: constraint · confidence: high

A new use of rule 4 — as the reason **not** to act. Mutant M-A (`queue until one of this bucket's N
slot files can be held, admitted by tallying how many are live`) survives the shipped regex. The
reviewer constructed a positive auxiliary-adjacency form naming no forbidden word that keeps the
true text GREEN and turns M-A RED — then found it is defeated by a one-word rewording of the same
counting gate, which also survives the shipped regex.

**Apply when** a candidate fix would add a conjunct or a branch to exclude an observed-bad input.

**Prevention**

- **Test the candidate fix against a *rewording* of the same defect before adopting it.** If the
  rewording survives, the candidate is a blacklist wearing a whitelist's clothes: it excludes an
  instance and leaves the class.
- The honest move is then to **quote the survivor verbatim, prove by neighbourhood that the
  assertion still bites, and say what would actually close it.** Here: ORIG-A and TASK-1
  counting-gate mutants die, the natural reversion wording for the sweep dies, and only an
  adversarial `never` insertion defeats it.
- **Regex-over-prose is negation-insensitive and no ordered pattern fixes it.** Inserting `never`
  between the noun and the verb keeps every required structure intact. The honest guarantee a prose
  test can make is *"this fact is stated"*, never *"this sentence is true"*.
- **Reviewers should test the natural drift wording, not only adversarial insertions.** Here the
  natural reversion died, which is what makes the assertion worth keeping at all.

## 6. A harness must be proven to measure what it claims, before any result is read · form: constraint · confidence: high

Not covered anywhere in the existing rules or module docs. Three distinct ways a verification
harness silently measured the wrong thing, all in one day, **all presenting as a real defect**:

1. **Isolated worktree seeded at a stale head — third occurrence.** The worktree for
   `01KYEFGACRYVBP1WJ0PGEHRPYM` came up at `5a48d55` (132 tests) instead of the requested head; the
   same corruption spoiled the predecessor task's review twice. Caught by checking HEAD before
   reading any source; reset to `ac50fae`, baselined at 227. **A green at 132 or 221 tests when the
   tree is at 227 is a silent false negative.**
2. **`shutil.copy2` preserves mtime.** Restoring a pristine source file between mutation runs left
   MSBuild believing the project up to date, so the unmutated control **ran the previous mutant's
   binary** and reported RED for a clean tree — which reads exactly like a real defect. Fixed with
   an explicit `os.utime` after every restore; review 2 re-asserted the control GREEN across 24
   supervised runs.
3. **Supervisor sharing `TMAN_HOME` with the store under test.** A scale measurement run as
   `tman run --name x -- <new tman> clean` reported *"reclaimed 0"* while 20,000 files vanished: the
   supervising **installed** tman was an older release whose sweep still removed stale locks, and it
   cleaned the sandbox before the new binary ran.

**Apply when** spawning into a worktree, writing a mutation harness, or measuring a store with a
supervised run.

**Prevention**

- **Assert what the harness claims to measure — HEAD, baseline test count, control colour, and the
  identity of the store under test — before any result from it is read.** All three shapes are one
  failure class: *a run reporting a result for a tree it is not actually testing*. Every conclusion
  in both slices' final rounds is prefixed by a `headVerified` and a `baselineTests` field for this
  reason.
- A mutation harness that restores source **must touch the restored files' mtime**, or its controls
  measure the previous mutant's build.
- Keep the supervisor's `TMAN_HOME` separate from the store under test.

Two neighbouring vacuity shapes the same discipline caught:

- **Assert exactly-one, not at-most-one, in a barrier race test** (`cb908d8`). A 400-round race
  scored a round clean when no two runs overlapped — which a round where *nothing ran* also
  satisfies. Where the fixture guarantees exactly one winner, `at most` is the vacuous direction.
- **A helper that swallows failure while its callers count the attempt is a quiet lie.**
  `Store.Delete` swallowed refused deletes while every caller counted the attempt, so `pruned N`
  could name files still on disk. Make it return whether it acted (`cb908d8`).

## 7. Four user-facing descriptions of behaviour, not two · form: fact · confidence: high

Acceptance criteria in this repo habitually name `README.md` and `CHANGELOG.md`. There are four:
`README.md`, `CHANGELOG.md`, `site/src/pages/*.astro`, and **the usage text inside `Program.cs`**.

Surface 3 blocked review 1 of the lock slice: `docs.astro:101` still said *"Nothing ever removes a
lock file"* and *"`tman clean` runs the same sweep"* — the exact inverse of the shipped change. The
site is actively maintained (`d15cf32` updated README and site in one commit; `8ae8c9a` corrected
the site's lock-file counting specifically), so criteria naming only the first two systematically
leave the public site stating superseded behaviour. Surface 4 is the one a docs-only sweep of
`*.md` and `site/` **structurally cannot see**.

**Prevention**

- **Treat a criterion's enumeration of doc files as a floor; grep every surface for the sentence
  the diff falsifies.** `git log -S` on the falsified sentence shows which surfaces are kept in
  sync. Beyond the two paragraphs the reviewer cited, that grep caught the site's `tman clean`
  command-table row and its exit-code table row for 124 — both stale, neither named in any
  criterion.
- Only the usage text is machine-checked (`HelpTextTests`). README and the site carry the same
  figures with **no** machine check and can drift from code independently.

## 8. Scope disjointness decays, and closing is not landing · form: constraint · confidence: high

Scope *under-specification* is covered three times already (`honest-scaffold` §7, `named-invariants`
§7, `atomic-slot` open item 4). Neither **drift between concurrent slices** nor **worktree landing**
is covered anywhere. Both were real here, and both were caught by a human-readable note, not a gate.

- **Scope disjointness verified at claim time does not stay true.** Review 1 of the lock slice
  appended `site/src/pages/docs.astro` and four new criteria mid-flight; implement attempt 3 then
  edited `tests/Tman.Tests/HelpTextTests.cs` — the **declared scope of the other live task**. It
  merged cleanly only because the addition was a pure insertion reusing an existing helper, and
  because the implementer flagged it explicitly for the coordinator.
- **Declared `fileScope` covers production files only, so test-file collisions between concurrent
  tasks are invisible at planning time.** The lock slice's criterion 2 mandated a barrier-driven
  concurrent test while its `fileScope` listed no test file. **When a criterion mandates a test by
  name, declare the test files in `fileScope` at planning time** — that scope question was created
  by the plan, not by the implementer.
- **A worktree-isolated slice's commits are not on `main` when its task closes.**
  `01KYEFGACRYVBP1WJ0PGEHRPYM` reported `implementation_ready` with commits on branch
  `worktree-agent-adc518762f92e8ea7` at `ac50fae..be3d029`; they reached `main` only via merge
  `814ede7` (parents `2b8cffa`, `be3d029`) once the other slice released the main tree. **Closing
  and landing are separate steps**, and a green reported from a worktree is a green about that
  worktree.

## 9. Verified environment facts

- **The self-respawning probe that destroyed the host was never needed.** The incident itself is
  `.claude/rules/honest-failure-and-verification.md` §5 (promoted from the incident, committed
  `ac50fae`); this is its technical postscript, and the first module-tier record behind that rule.
  The probe existed to measure `FileShare` gate semantics — a property
  `atomic-slot-claim-and-honest-gates-2026-07-25.md` §7 had **already recorded** as testable
  in-process, because `flock` conflicts are per open file description, not per process. Pinned here
  by `StoreTests.TheSharingModesTheClaimGateIsBuiltOn_AdmitManyReadersAndOneWriter`. Reach for an
  out-of-process probe only when the property under test is genuinely process-scoped.
- **`FileShare.None` → `LOCK_EX`; any other share value → `LOCK_SH` regardless of `FileAccess`.**
  A `FileAccess.Write` open with `FileShare.Read` still conflicts with an exclusive holder. **Three
  of six reviewer-originated mutations survived for this single reason** — do not read a surviving
  `FileShare` mutation as a test gap without measuring the lock semantics first.
- **`flock -x FILE sleep 30` in its exec form** (not `-c`) is the cheap way to get a real external
  lock holder: the holder's PID is the `sleep` itself, so one `kill <pid>` ends it, and it lives
  inside the supervising `tman run` tree so it cannot outlive the measurement. The `-c` form leaves
  an orphanable shell child.
- **PATH traps hit and avoided by absolute paths during these runs:** `./test` resolves to a pytest
  symlink (rule 1), and `env` resolves to an unexecutable `~/.local/bin/env`.
- **`flock` has no fairness.** The claim gate is the first machine-wide serialization point tman has
  ever had; a sustained population of overlapping shared holders can starve the exclusive waiter,
  reproduced with four overlapping shared holders starving `tman clean` past its deadline.
- **Residue after reclaiming 20,000 lock files is ~936 KB of ext4 directory entry table that is
  never shrunk** (measured 1052672 bytes identical before and after). The figure is host-dependent;
  the *substance* — the filesystem does not shrink it — is exact. Worth stating in user docs, since
  a user who measures after a clean will otherwise think it did not work.

## Open follow-ups

- An unexpected store fault under `Program.Main` aborts with a stack trace, a core dump and exit
  134 across the **whole CLI**, not just at the claim gate. Measured at this HEAD: `chmod 000` on a
  bucket `.lock` (claim path) and on a record `.json` (`tman list`) both abort with
  `UnauthorizedAccessException` — **not** an `IOException`. Deserves one deliberate repo-wide
  decision, not a per-slice one.
- The claim gate's **wall-clock bound is unpinned**: mutation M4 (deadline arithmetic doubled) and
  M6 (message figure hardcoded) both survive, because `ClaimGateTests` shorten the wait to 200ms
  and assert only exit code and stderr substrings, never elapsed time. Structural blind spot for
  **any** timeout, deadline, retry interval or backoff in this repo.
- The narrowness of the second catch filter is load-bearing and unpinned (M5 survives). No test
  constructs a non-holder `IOException` on the gate.
- README's and the site's copies of the 10s bound have no machine check; only the usage text does.
  A docs-consistency test grepping both against the code constants would close it.
- Fix the **worktree-seeding defect as its own task**. Three occurrences; its failure mode is a
  silent green at the wrong test count.
- `HelpTextTests` would be better capturing help through an **injected `TextWriter`** than through
  `Console.SetOut` — removes the process-global hazard at the root rather than bounding it, at the
  cost of a small seam in `Program`. A simplification, not a fix; the current bound is sound.
- Consider **per-file gating** in `ReclaimUnusedLocks` (mutation M5 of review 1, which survived the
  suite): equally safe by the stated invariant, and it would shorten the machine-wide stall.

## Promotion candidates flagged, not taken

- **§6.1, stale worktree seeding.** Three occurrences (`task:01KYD9AE3TWX4VM4F1R6ZCTJMY` ×2 during
  review, `task:01KYEFGACRYVBP1WJ0PGEHRPYM#task_annotation_v1-attempt-1`), independently re-derived
  by that round's reviewer, and flagged project-wide (*"Every agent spawned into an isolated
  worktree in this repo, until worktree creation is fixed"*). **Nominally clears the N=3 gate.** Not
  promoted here, because the reviewer's own recommendation is to fix the defect as its own task, and
  promoting a workaround for a tooling bug freezes the bug. Operator decision.
- **§2, disclosure overstatement.** Three consecutive review *rounds*, but within one task lineage —
  not the two-or-more independent tasks the project rule's own gate was cleared on. Registered as
  steering; promote on the next independent recurrence.
