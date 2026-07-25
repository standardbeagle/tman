---
written_at: 2026-07-25T21:58:22Z
source_event: task:01KYD8VQ8RZE581VG3VZQTMMM3
module: tman
category: defaults-and-scaffolding
confidence: high
form: constraint
sources:
  - task:01KYD8VQ8RZE581VG3VZQTMMM3#task_annotation_v1-attempt-1
  - task:01KYD8VQ8RZE581VG3VZQTMMM3#review_annotation_v1-attempt-1   # independent red re-derivation
  - task:01KYD8VQ8RZE581VG3VZQTMMM3#worktrack_completion_v1-review-context
  - git:fe36e4d   # RED
  - git:e00e216   # GREEN
  - git:14976d5   # docs
tags: [fake-green, scaffold, placeholder, default-values, timeout-classification, legacy-test-encodes-bug, generator-testing, duplicated-constant]
status: steering
recurrence: 1
---

# A scaffold that runs is a scaffold that lies

Task: `tman init re-seeds both hazards: a 60s stall and a stub alias that fakes a green suite`.
One workflow instance, five steps, no rewinds. Fleet evidence: the `echo "replace me"` stub was
found **byte-identical in 20 repos**; wiring real commands into them turned **5 of 11 suites red**.
Separately, 32 of 38 fleet supervisor interventions were 60s stall kills, **none an actual hang**.

## Lesson (one line)

A default that silently **succeeds** is worse than one that silently fails — so scaffold the shape
that **cannot be mistaken for success**, and delete the placeholder rather than improve it.

---

## 1. Prefer absence + an existing loud failure over a runnable placeholder · form: constraint · confidence: high

`Detect.cs RenderConfig` emitted, when nothing was detected:

```kdl
alias "test" { command "echo"; args "replace me" }
```

Exit 0. Forever. In 20 repos. It survived precisely **because nothing ever went red** — there was
no signal to act on. The fix wrote *no new error-reporting code*: it emits a commented-out template
and **no alias node at all**, so an already-correct path carries the failure —
`Program.cs:125` throws `FormatException "alias test not defined"`, caught at `Program.cs:51`,
stderr + `Runner.ExitNotFound` (127), distinct from the 124/125/126 supervisor kill codes.

Rejected alternative: a stub that exits nonzero (`false`, `exit 1`). **A stub that fails looks like
a failing test suite** and gets debugged as one. Absence names the fix; a red stub misnames it.

**Apply when** any scaffold, template, migration, or default would emit a runnable-but-meaningless
artifact. Net structural simplification (a deleted branch) beats an added error branch.

**Prevention:** ask of every generated artifact — *if the user never edits this, what does CI say?*
If the answer is "green", the artifact is a lie. See the suggested guard test in §7.

## 2. Legacy tests can encode the bug as a contract · form: procedure · confidence: high

Three **green** tests here asserted the hazard as expected behavior: `command "echo"` and
`Stall == 60s`. A behavior change that looks untested may in fact be *anti-*tested.

Recipe: before changing a default or a generated artifact, **grep the suite for the hazard's
literal values** (`"echo"`, `"60s"`), not just for the function name. Then flip the legacy
assertions **in the same RED commit** as the new ones (`fe36e4d`), so the RED run is a single
coherent statement of target behavior rather than two contradictory suites.

Rename the tests in that same commit when the fix inverts what they pin —
`BareRepo_WritesPlaceholderConfig` → `BareRepo_LeavesTestAliasUndefined`,
`NoDetection_RendersPlaceholderAlias` → `NoDetection_EmitsNoRunnableAlias`.
**A stale test name is documentation that lies.**

## 3. Classify a timeout-shaped default before picking its number · form: constraint · confidence: high

Two distinct questions hide behind one integer:

| Question | Knob | Sane magnitude |
|---|---|---|
| **Liveness backstop** — is this hung? | `--stall` | minutes to tens of minutes (`30m`) |
| **Runtime budget** — has this taken too long? | `--max-time` | task-specific, often seconds |

`60s` was defensible **only under the budget reading**, which was never `--stall`'s job. Cold builds
and test suites legitimately run for many quiet minutes; killing those costs far more than noticing
a real hang late. README.md:81-85 now states the distinction verbatim.

**Apply when** picking or reviewing any timeout, TTL, retry window, or heartbeat interval: name
the question first, then the number. A number chosen before the question is a coin flip.

## 4. Test a generator through its real consumer, not its output text · form: procedure · confidence: high

`NoDetection_EmitsNoRunnableAlias` / `BareRepo_LeavesTestAliasUndefined` feed `RenderConfig` output
through **`Config.Load`** and assert on parsed `Aliases`.
`BareRepo_TestAliasFailsLoudlyInsteadOfExitingZero` drives **`Program.Main(["run","--alias","test"])`**
after a real `CmdInit` and asserts a nonzero exit.

Why not string-contains: *the hazard is an exit code, not a substring.* A KDL-shape assertion would
pass against any stub that merely lacks the word `echo`, and would also pass if the emitted KDL
stopped parsing entirely.

**Apply when** testing any code generator, scaffolder, config emitter, or serializer:
**round-trip the artifact through the component that will actually read it**, and assert on the
behavior a user observes. Cost paid here: cwd manipulation forces `[Collection("cwd")]`
serialization, losing a little suite parallelism. Worth it.

## 5. Commit messages carry field evidence, not the diff · form: procedure · confidence: high

`e00e216` records that the stub was byte-identical in 20 repos and that wiring real commands turned
5 of 11 suites red. **The diff already shows what changed; the fleet numbers are unrecoverable from
it** — and they are exactly what a future reader needs when tempted to reintroduce a convenience
stub. Applies to any change motivated by production or fleet observation.

## 6. Duplicated default literals are how defaults drift · form: constraint · confidence: high

**Open in tree.** `Detect.cs` now scaffolds `stall "30m"` while `Caps.cs:24` keeps the config-less
built-in at `60s`. The value is a bare literal in **both** sites with no shared constant — which is
precisely how they came to disagree. `README.md:73` papers over it: `60s (tman init scaffolds 30m)`.

Deliberate, not accidental: the acceptance criteria required existing configs be unaffected with no
migration, and the built-in applies to every config that omits `stall`. But the original hazard
therefore survives on two paths — `tman run -- <cmd>` with no `.tman.kdl`, and any `.tman.kdl` that
omits `stall`. Tracked as **`01KYDM3B8543G5HNAJZMQSQ36V`**.

**Prevention:** a default that exists in both a scaffold emitter and a runtime fallback must be
**one named constant**. Otherwise every future default change is a two-site edit that will be done
once.

## 7. Follow-ups surfaced

1. Extract the scaffolded + built-in stall to a single named constant (`01KYDM3B8543G5HNAJZMQSQ36V`).
2. Root `dotnet test` runs zero tests and exits 0 — tman reproduces the hazard it treats
   (`01KYDMEXP3GH88T4RBNEJEKEV4`).
3. **Guard test:** fail if any scaffolded alias resolves to a command that exits 0 without doing
   work, so the fake-green stub cannot be reintroduced by a future detector. *(not yet a task)*
4. Run the xUnit analyzers as errors in the test project — an analyzer cleanup (xUnit2029) straddled
   the RED/GREEN commit pair here. *(not yet a task)*
5. `file_scope` gate is structurally blind under `tman-slice-v2`: the implementer commits before the
   gate runs, so it diffs against HEAD, sees nothing, records `checkedFiles: []` and passes
   vacuously. Real scope enforcement rests entirely on the reviewer. **Workflow-template defect, not
   a tman defect** — belongs to the worktrack template, not this repo. *(not yet a task)*
6. Declared `fileScope` routinely under-specifies what the criteria require: this task declared
   `[Detect.cs]` while its own fourth criterion mandates a README change and TDD mandates test files.
   `fileScope` should be **derived from the acceptance criteria at planning time**, including docs
   and test paths. *(not yet a task)*
