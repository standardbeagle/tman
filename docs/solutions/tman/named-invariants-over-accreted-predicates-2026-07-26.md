---
written_at: 2026-07-26T00:34:00Z
source_event: task:01KYD9AE3TWX4VM4F1R6ZCTJMY
module: tman
category: invariants-and-test-honesty
confidence: high
form: constraint
sources:
  - task:01KYD9AE3TWX4VM4F1R6ZCTJMY#reviewer_verdict_v1-attempt-1   # bare `tman`, exit 127
  - task:01KYD9AE3TWX4VM4F1R6ZCTJMY#reviewer_verdict_v1-attempt-2   # dotnet-host command substitution, mutation MC
  - task:01KYD9AE3TWX4VM4F1R6ZCTJMY#reviewer_verdict_v1-attempt-3   # PASS, 8 reviewer mutations, 7 killed
  - task:01KYD9AE3TWX4VM4F1R6ZCTJMY#task_annotation_v1-attempt-1
  - task:01KYD9AE3TWX4VM4F1R6ZCTJMY#task_annotation_v1-attempt-2
  - task:01KYD9AE3TWX4VM4F1R6ZCTJMY#task_annotation_v1-attempt-3
  - git:d1a6b32   # refactor: inject argv[0] into Render
  - git:7a7a490   # RED
  - git:97d5c2d   # GREEN — IsProvenTmanExecutable
  - git:783ce9c   # docs
  - git:cdd3ea2   # case-insensitivity
  - git:df32bad   # two mutation survivors closed
tags: [named-invariant, predicate-accretion, blacklist-vs-positive-id, self-certifying-test, environment-accident, reviewer-originated-mutation, surviving-mutant, fix-hint-half-implemented, attempt-budget, file-scope-under-specification, equivalent-mutant]
status: steering
recurrence: 1
---

# Name the invariant, or keep adding `&&` until the next host arrives

Task: `PreToolUse Bash hook that routes unsupervised test and build commands through tman`.
**Three implement rounds and three review rounds — attempt 3 of 3, the budget ceiling.** Two
reviewer rewinds, both on the *same defect wearing a different costume*. The most expensive task
this workspace has run.

## Lesson (one line)

If a fix adds another `&&` to a predicate to block the symptom you just found, you are building a
blacklist: **stop, name the invariant the value must satisfy, and prove it positively** — and check
that the test pinning it is not being satisfied by an accident of the test environment.

---

## 1. Predicate accretion is a blacklist in disguise · form: constraint · confidence: high

One defect, three costumes:

| Attempt | Emitted argv[0] | What was actually proven | Failure |
|---|---|---|---|
| 1 | bare `tman` | nothing | Bash tool resolves via PATH → **exit 127**, the build never runs |
| 2 | `Environment.ProcessPath`, guarded by `IsResolvableExecutable` (non-empty **&&** fully-qualified **&&** exists) | "this is *an* absolute path that exists" | under a dotnet host: `/usr/lib/dotnet/dotnet run -- go test ./...` — **silently runs a different program** |
| 3 | same value, guarded by `IsProvenTmanExecutable` | "this argv[0] *is tman*" | none; unprovable input degrades to an announced `Warn` |

Attempt 2 was **worse than attempt 1**. Exit 127 is loud and honest; command substitution is
neither — in any directory holding a `.csproj`, `dotnet run` builds and launches an unrelated
application while the transcript claims the user's test suite ran. The implementer had already
refused exactly this substitution for aliases ("the transcript says one thing and the machine ran
another") and then shipped it in the guard.

The structural difference the reviewer ruled on, verbatim:

> the predicate names three facts a path must HAVE (fully qualified, a name tman ships under,
> present on disk) and consults no fourth kind of evidence; a blacklist would need a new entry per
> host never yet seen, which is precisely the accretion that failed in attempts 1 and 2.
> **It grows no entries.**

And the collapse that made a third disguise unavailable:

> The two failure shapes that were two guards added a round apart — 'no path' and 'someone else's
> path' — are now **one violation of one invariant in one branch**.

`Hook.cs` now carries the invariant as a named, commented contract (`THE SUPERVISOR IDENTITY
INVARIANT`), not as an anonymous boolean chain.

**Apply when** you catch yourself appending a conjunct to a guard to exclude a case you just
observed. Ask: *what must be true of this value?* — then assert that, positively. A guard that
enumerates what the value must not be is waiting for the next host.

**Corollary — the honest residual is stated, not hidden.** A file *named* `tman` that is not tman
still passes. Ruled acceptable and written down: the threat model is misconfiguration, not an
adversary, and the only value reaching the predicate in production is the running process's own
path. Hashing and build metadata were rejected as unverifiable here; an `AppContext.BaseDirectory`
sibling lookup was rejected as a **second evidence path, i.e. a fallback** — forbidden by project
standard. A named invariant makes its own weak edge nameable; an accreted predicate cannot state
what it fails to cover.

**Rejected alternative worth recording**: comparing the path against the *entry assembly name* — it
**would self-certify**, because under `dotnet test` the entry assembly and `ProcessPath` are *both*
`testhost`. A proof that consults evidence the impostor also supplies is not a proof. (See §3: this
is the same failure the old test had.)

The implementer's own statement of the arc:

> Two attempts each appended a condition to exclude the symptom just observed, and the next disguise
> walked through.

## 2. Check every clause of a `fix_hint`; half a fix reports as a whole one · form: constraint · confidence: high

Attempt 1's `fix_hint` named the eventual attempt-2 defect **verbatim**:

> If ProcessPath is unavailable **or is a dotnet host rather than a tman apphost**, do NOT emit an
> unresolvable program — degrade to Warn or PassThrough.

Only the `unavailable` half was implemented — and the completion comment **reported the blocker as
fixed**. The easy clause was taken; the clause that required a new idea was dropped, and the
reporting did not distinguish them.

**Apply when** closing out any review round: enumerate the `fix_hint`'s clauses as a checklist and
report per clause. "Blocker fixed" is only true when every clause is addressed; otherwise say which
clause is not, and why. A partially-honored hint costs a full rewind.

**The reviewer's half of the same lesson — write criteria as principles, not instances.** From the
attempt-2 `systemicObservations`:

> A fix aimed at a named blocker satisfies the LETTER of the added acceptance criterion while the
> defect's FAMILY survives one step over. […] when writing `new_acceptance_criteria`, encode the
> PRINCIPLE, not the instance.

Attempt 1's criterion said *"assert the rewritten argv[0] is an existing absolute path."* Attempt 2
implemented precisely that and shipped a strictly worse instance of the same family. The criterion
should have said *"assert the rewritten argv[0] is proven to be tman."* Compare `.claude/rules`
rule 1, which the reviewer noted "pins the instance (`./test`) rather than the principle — whenever
code names a program for someone else to execute, it must name the resolved path it has already
proven exists."

**Corollary — a safety property proven at one layer is re-violated at the next.** Attempt 1 proved
fail-open rigorously *for the hook process* (exit 2 blocks, every other nonzero is
surfaced-but-non-blocking; verified against real Claude Code probes) — while the **rewritten
command it emitted received no fail-open analysis at all**. When a requirement is absolute, trace it
across every layer the value crosses, not just the layer you are editing.

## 3. A test can be satisfied by an accident of its own environment · form: constraint · confidence: high

`Rewrite_EmitsTheRunningBinarysAbsolutePath_NotABareProgramName` — a name that states exactly the
right property, pinning nothing. It ran under `dotnet test`, where `Environment.ProcessPath` **is**
`/usr/lib/dotnet/dotnet` (entry assembly `testhost`). So the single production-entry assertion in
the suite was *being satisfied by a non-tman host binary*: the assertion was true because the
defect was present. The reviewer's mutation MC — hardcoding `"/usr/lib/dotnet/dotnet"` into the
3-arg production entry — left the suite **fully green at 181/181**.

The reviewer's ruling: *"per rule 2 a gate that certifies the defective case is not a gate."*

The fix was injection, not more assertions: a 3-arg `Render` overload mirroring the pre-existing
2-arg/4-arg `Decide` pair, so a test can drive a **chosen** argv[0] instead of inheriting the
host's. The production-entry tests then guard against their own vacuity —
`TheProductionEntryPoint...` asserts `Path.GetFileNameWithoutExtension(ProcessPath) != "tman"` as a
**precondition**, so the test cannot quietly become meaningless if the suite ever runs under a tman
host.

**Apply when** any assertion concerns a property of the execution environment itself — process
path, cwd, PATH, user, clock, hostname, TTY, env vars, platform. Ask: *could this assertion be
satisfied by an accident of the test environment rather than by the code?* If yes, either drive it
through an injected overload or run it out-of-process. And where the environment must hold a
particular shape for the test to mean anything, **assert that shape as a precondition** so the test
fails loudly rather than passing vacuously.

## 4. Bring your own mutations; aim at what the tests never mention · form: procedure · confidence: high

Across three rounds the reviewer originated every mutation and never re-ran the implementer's
(attempt 3: *"The implementer's 17 mutations were NOT re-run; these eight are mine and aimed at
predicates their table does not name"*). It found a blind spot every single round:

- **Attempt 1** — replacing `Basename(words[0]) switch` with `(words[0]) switch` left all 28 hook
  tests green while changing real behavior: `/usr/local/bin/npm test` silently stops being
  supervised.
- **Attempt 2** — MC, the production-entry substitution above: green at 181/181.
- **Attempt 3** — eight mutations, seven killed: widening the name set to `StartsWith("tman")`
  (accepts `tman.dll`, `tmanager`), swapping `Path.GetFileName` for the sibling `Basename` helper,
  dropping `Canon.Quote` on argv[0], nulling *both* production entry points, emptying the `Warn`
  branch's `additionalContext`, and hardcoding `/bin/sh`.

The asymmetry is structural: an implementer's mutations cluster on **the guard they are currently
thinking about**. The reviewer's value comes from attacking **functions and branches the test names
never mention** — helper choice, quoting, wiring at the entry point, the message on the safety
path. The reviewer's diagnosis, which is sharper than "the implementer chose badly":

> Mutation coverage inherits the author's blind spots exactly as test coverage does. […] The blind
> spot is not the heuristic, **it is recency**.

The implementer converged on the same conclusion by attempt 3, having run 17 mutations and still
left three survivors on code they believed covered:

> Mutating ONLY the code under change is not enough; mutating functions the tests never mention
> (`Basename`, `IsEnvAssignment`, `IsSingleCommand`, the `Render` delegation) is where both prior
> rounds' gaps actually were.

Recipe: throwaway **detached git worktree** at the head commit, full suite per mutation, source
restored between runs, worktree removed. Baseline green recorded first. For fast iteration inside a
round, `dotnet test tests/Tman.Tests/Tman.Tests.csproj --filter …` cuts the loop to ~5s — but score
nothing on a filtered run.

**Mutation testing also finds dead code, immediately.** Attempt 1's very first mutation deleted a
`"tman" => false` case from the allow-list switch and **nothing went red** — because the allow-list
never contained `tman` in the first place. It was decoration implying a guard, and was deleted in
`ba4b7fa` rather than kept.

## 5. Disclose surviving mutants — and verify the stated cause, which is often another false green · form: constraint · confidence: high

The implementer self-reported a surviving mutant in **two** of three rounds. The reviewer verified
the *explanation* both times, and the two outcomes are the whole lesson:

- **Attempt 2's M5 — a false green hiding inside the theory data.** Dropping
  `Path.IsPathFullyQualified` (keeping `File.Exists`) survived at 180/180. The real cause, found by
  looking rather than reasoning: *"every relative case in the theory named a file absent from the
  test process cwd, so `File.Exists` rejected them for the wrong reason; the fully-qualified
  requirement was never independently exercised."* Closed in `503861a` by
  `ARelativeSupervisorPath_IsRefusedEvenWhenThatFileExistsInCwd`, which chdirs into a temp dir where
  `bin/tman` genuinely exists. **The mutant survived because the test data made two predicates
  indistinguishable** — a shape to look for whenever a theory's negative cases all fail for the same
  incidental reason.
- **Attempt 3's survivor — the explanation was true, and verified by measurement.** The reviewer ran
  a probe fact inside the suite confirming `Environment.ProcessPath == "/usr/lib/dotnet/dotnet"`,
  then confirmed R8 survives at 194/194 while *every neighbouring mutation dies* — including a
  different non-tman path (R7) and a tman-named path. Uniquely the value the environment happens to
  hold: an **equivalent mutant within the test host**, not a coverage gap.

But the reviewer also **corrected the framing**: the implementer said only manual out-of-process
runs could cover it. Out-of-process is required; *manual* is not. See §6.

**Apply when** reporting a surviving mutant. The report is mandatory — and the explanation for why
it cannot be killed is itself a claim requiring independent verification, because "this can't be
tested" is one of the most common shapes a false green takes. Verify by measurement (probe the
value) and by neighbourhood (do adjacent mutations die?).

The reviewer named the disclosure as the reason the review was cheap enough to be deep:

> The self-disclosure of the survivor, for the third round running, is what made this review
> efficient enough to spend its budget on the predicates nobody had doubted.

## 6. At the attempt ceiling, pass and file the residue · form: constraint · confidence: high

Attempt 3 of 3. The reviewer had a genuine, non-hypothetical coverage gap in hand and chose
**pass + file** over burning the last attempt:

- the unpinned thing is **one wiring line**;
- its breakage degrades **fail-safe** (to `Warn` — supervision lost, never the wrong program run);
- the shipped behavior was verified by hand in both invocation forms;
- the acceptance criteria as written are met.

Filed as `01KYDW1NJ24RSBC6T57EFXANNH` with the demonstration required, linked `blocks` from this
task.

**Apply when** the attempt budget is nearly spent. Neither extreme is the answer: do not exhaust the
budget on perfectionism, and do not wave through a real blocker to close the loop. Test the residue
against those four questions — size, failure direction, independent verification, criteria-as-written
— and if it passes, **pass and file it with the demonstration attached**.

## 7. `new_scope` must cover everything its own new criteria compel · form: constraint · confidence: high

Scope was misjudged in all three rounds, and the second misjudgement was **the reviewer's own
doing**. Attempt 1's `new_scope` listed only the blocker file, `["Hook.cs"]` — while the criterion
it added in the same breath demanded *"pin the behavior with a test"*. The test file was therefore
out of scope **by construction**. Attempt 2 corrected it to
`["Hook.cs", "tests/Tman.Tests/HookTests.cs", "README.md"]` and attempt 3's scope ruling was clean.

Also ruled, in all three rounds: under-specified `fileScope` is a **planning failure, not
implementer overreach**. This task shipped with an *empty* declared `fileScope` in round 1.

**Apply when** writing `new_scope` or a task's initial `fileScope`: derive it from the acceptance
criteria, and include every path the criteria *force* — test files, README, docs. This is the third
recurrence of the open item at
`honest-scaffold-defaults-2026-07-25.md` §7 item 6.

## 8. Verified environment facts (reference) · form: fact · confidence: high

Traps that cost real rounds here — all re-verified in tree on 2026-07-26:

- **`test` is gitignored** (`.gitignore:5: /test`). A fresh `git worktree` therefore has **no
  `./test` shim**, and `sh -c "exec ./test"` fails with *"not found"*. In a mutation run this reads
  as **a surviving mutant** and is not one. In a throwaway worktree, invoke
  `dotnet test tests/Tman.Tests/Tman.Tests.csproj` explicitly.
- **Under `dotnet test`, `Environment.ProcessPath` is `/usr/lib/dotnet/dotnet`** (entry assembly
  `testhost`), not a tman binary. Any test reading it asserts against the host, not the product.
- **The tman apphost is copied into the test output directory** —
  `tests/Tman.Tests/bin/Debug/net10.0/tman` (79 KB, executable, present today).
- **The suite already spawns processes**: `TreeStatsTests.TrySample_IncludesChildProcesses` uses
  `System.Diagnostics.Process.Start`. Out-of-process coverage needs **no new infrastructure** — one
  wiring line, which is exactly why the residual gap in §5 was ruled fileable rather than blocking.
- **The tman suite runs under tman, so `TMAN_RUN_ID` is ambient inside the test process.** The hook
  correctly treats every command as already-nested, and *"the ambient value made a correct
  implementation look broken for one debugging cycle"* (`JsonReaderException: input does not contain
  any JSON tokens`). `HookCommandTests` save/clear/restore it per test and join `[Collection("cwd")]`.
- **Claude Code hook contracts are discoverable, not guesswork.** The embedded docs in the `claude`
  binary carry the full hook JSON output schema for that exact version
  (`strings -n 8 <binary> | grep -B25 -A10 updatedInput`). Verified semantics: **exit 2 blocks a tool
  call; every other nonzero is surfaced but non-blocking** — this is the fail-open mechanism.
  `hookSpecificOutput.updatedInput` alone rewrites the command; emitting `permissionDecision: allow`
  alongside it *"would have bypassed the user's permission prompts as a side effect"* and was
  deliberately not done.
- `Canon.Quote` was promoted private → public for reuse by the hook (the reuse rung of the minimal-code
  ladder). A second POSIX quoter would have drifted — the duplicated-literal hazard of
  `honest-scaffold-defaults-2026-07-25.md` §6.
- **Standing trip hazard, by design:** `Hook.cs` now holds *two* name-extraction helpers —
  `Path.GetFileName` for filesystem paths, and the local `Basename` for program names inside
  agent-written command lines. They are not interchangeable (`Basename` would accept a Linux file
  literally named `sub\tman`). Reviewer mutation R2, swapping one for the other, goes RED, so the
  distinction is pinned rather than merely commented — but two similarly-named helpers in one file
  will invite the swap again.

## 9. What went right, and should be repeated · form: procedure · confidence: high

Pass patterns the reviewer named explicitly — worth copying, not just the failures:

- **Commit discipline, ruled "exemplary"**: `d1a6b32` refactor → `7a7a490` RED → `97d5c2d` GREEN →
  `783ce9c` docs → `cdd3ea2` + `df32bad` two separate hardening commits. Each individually testable;
  the preparatory refactor (injecting argv[0] into `Render`) landed **before** the test that needed
  it.
- **The refactor was compelled, not decorative.** The new 3-arg `Render` overload mirrors the
  pre-existing 2-arg/4-arg `Decide` pair — *it adds a shape the file already had* rather than a new
  one — and its XML doc states why in the terms that matter: *"the test host this suite runs under is
  one of those other hosts, so a test that let this default would be asserting against an accident of
  its own environment."*
- **Documentation as a load-bearing claim.** Attempt 1's README asserted a rewrite form that the
  GREEN commit made false. Attempt 3's README states the proof obligation, names the dotnet-host case
  concretely, and says what the hook does when it cannot prove — matching the observed binary word
  for word. Ruled "honest and load-bearing, not decoration".
- **The implementer rejected the task's own sketch, with a reason.** The task proposed rewriting to
  `tman run --alias test -- npm test`. Refused: *"A project's alias named 'test' can point at an
  entirely different command… the transcript says one thing and the machine ran another."* The same
  principle that later decided the blocker — and it was applied to the spec, not just the code. A
  task's suggested mechanism is a hypothesis; the criteria are the contract.
- **Volunteering a meaningless green.** The reviewer's own words on why this dominated the review's
  economics: *"Self-report the mutation that SURVIVED, then close it in a named commit… Volunteering
  a green that means nothing is the single strongest signal that the rest of the evidence was not
  manufactured."*
- **Carried advisories get filed, not ridden.** Two honesty gaps (quote-blind `SplitSegments`;
  `CmdHook`'s catch-all returning 0 with empty stdout) surfaced in attempt 1, were re-verified
  unchanged in attempt 2, and were **filed as `01KYDSS5HPY0K5YZ61K2D6YF8R`** rather than carried a
  third round. Out-of-scope truth gets a task, not a rewind.

---

## Open follow-ups

1. `01KYDW1NJ24RSBC6T57EFXANNH` — automated out-of-process test proving the production entry passes
   the real tman apphost path (blocks-linked from this task).
2. `01KYDSS5HPY0K5YZ61K2D6YF8R` — quote-blind `SplitSegments` false-positive warning; silent
   `CmdHook` catch-all.
3. `fileScope` derived from acceptance criteria at planning time — third recurrence, still not a
   task. Candidate for a planning-time gate rather than another steering line.
