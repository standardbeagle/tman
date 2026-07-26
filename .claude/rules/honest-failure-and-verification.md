# Honest failure and verification

Promoted to project tier 2026-07-25 from module steering:

- `docs/solutions/tman/atomic-slot-claim-and-honest-gates-2026-07-25.md` §3, §4
  (`source_event: task:01KYD8STXXTKKN5RZ6G7YCVF69`)
- `docs/solutions/tman/honest-scaffold-defaults-2026-07-25.md` §1
  (`source_event: task:01KYD8VQ8RZE581VG3VZQTMMM3`)

Both lessons recurred across two independent tasks with independent reviewer re-derivation, and both
were flagged **project-wide** by the reviewer (`appliesTo: any agent or CI job verifying this repo`).

Section 3 promoted 2026-07-26 on the **third** recurrence (gate N=3) from:

- `docs/solutions/tman/atomic-slot-claim-and-honest-gates-2026-07-25.md` §3, §5
  (`source_event: task:01KYD8STXXTKKN5RZ6G7YCVF69`)
- `docs/solutions/tman/honest-scaffold-defaults-2026-07-25.md` §1, §7
  (`source_event: task:01KYD8VQ8RZE581VG3VZQTMMM3`)
- `docs/solutions/tman/named-invariants-over-accreted-predicates-2026-07-26.md` §3, §4, §5
  (`source_event: task:01KYD9AE3TWX4VM4F1R6ZCTJMY`)

---

## 0. What tman is for, and the scale at which a feature is out of scope.

Set by the project owner 2026-07-26, after a release-blocker slice shipped machine-wide
serialization on the common claim path to reclaim disk that only accumulates on a CI runner.

**tman keeps a few rogue processes on one machine from doing harm**: a test runner orphaned in a
loop eating battery, a test server holding a port for hours after its last use. That machine is a
developer's laptop or a **department-sized CI host** — a handful of runners, builds you could
count in a day. Both are in scope; neither is a fleet.

**Reject any feature whose motivating scenario requires more than a handful of concurrent
processes on one machine.** Agent farms, thousands of runners, and anything measured in tens of
thousands of buckets or dozens of simultaneous runs are **not** the target. A measurement taken at
that scale does not justify a feature; it disqualifies one. When a defect report's evidence is "at
20,000 X it costs Y", re-derive the number at department scale before deciding anything — the
honest answer is usually to document the bound and move on, not to build the mechanism that
removes it.

Scale down the evidence, don't just discount it. A CI host that seeds one bucket per build is a
real user, but 20 builds a day for a year is a few thousand small files — tens of megabytes, which
a line in a runner's cleanup script removes. That is a documentation problem. The same report at
20,000 buckets is a fleet problem, and not ours.

Two corollaries, because this is where the cost actually lands:

- **The common path pays first.** A guard added for a rare scenario that sits inside the ordinary
  claim/run path taxes every invocation and adds a failure mode to the case that always happens.
  Weigh it against the case that never does.
- **Acceptance criteria offering "or document the bound instead" mean it.** Choosing that branch is
  not an argument from difficulty when the scenario the mechanism serves is out of scope. Re-ask
  whether the primary route is worth its price once its price is visible, rather than treating the
  criteria as settled at planning time.

## 1. Verify this repo with `sh -c "exec ./test"`. Nothing else counts.

**Bare `dotnet test` from the repo root now refuses.** It resolves `tman.csproj` — the application
project, not a test project — and used to **exit 0 in about 3 seconds having run zero tests**. As of
`tman.csproj`'s `RefuseTestRunsAgainstTheApplicationProject` target it exits 1 with `error TMAN0001`
naming the commands below. The real suite lives under `tests/Tman.Tests/`.

Do not read that refusal as permission to reach for the command: it is a backstop for people and
tools that have not read this file, and it fires *instead of* a test run, never alongside one. The
guard binds `BeforeTargets="VSTest"`, the one target `dotnet test` invokes, so `dotnet build`,
`dotnet run`, `dotnet publish`, and building the project as a reference of `Tman.Tests` are all
unaffected — a change that broke any of those would be a defect in the guard, and
`RootDotnetTestGuardTests` covers both the refusal and that boundary.

**Never pass a bare relative `argv[0]`** such as `./test` to a workflow command step or any exec
that does not go through a shell. A relative `argv[0]` is resolved through **PATH, not cwd**; on
this fleet `./test` lands on `/home/beagle/.local/bin/test`, an unrelated uv-installed pytest
symlink.

Correct invocations, and only these:

```sh
sh -c "exec ./test"                              # preferred
dotnet test tests/Tman.Tests/Tman.Tests.csproj   # explicit path
```

Worktrack tasks in this repo must attach `templateName="tman-slice-v3"`.
**Do not use `slice-default`** — its test gate is wired to bare `dotnet test`. Against this repo that
gate can now only go red, since the guard above refuses the command outright; before the guard it
could only go green, and never on evidence. Neither is a gate.

`tman-slice-v2` is archived. It carried a `file_scope` gate that diffed against `HEAD`, but commit
discipline requires implementers to commit *during* implementation, so by the time the gate ran the
diff was always empty — it passed with `checkedFiles: []` on every task it ever gated. By rule 2
below, a gate that has never been observed failing is not a gate, so v3 drops it rather than
carrying a green that means nothing. Scope is enforced by `review-context`, which reads the task's
declared `fileScope` against the real commit range and has ruled on scope questions substantively.

## 2. Nothing may report success without doing work.

A default that silently **succeeds** is worse than one that silently fails: it produces no signal to
act on, so it survives indefinitely. The `echo "replace me"` test alias scaffolded by `tman init`
was found byte-identical in **20 repos**; wiring real commands into them turned **5 of 11 suites
red**.

- **Scaffolds, templates, and defaults emit absence, not a runnable placeholder.** When no test
  command is detected, emit a commented-out template and no alias node, and let the existing
  undefined-alias path fail (`FormatException` → stderr → exit 127). Do not write a new error
  branch, and do not emit a stub that exits nonzero — **a failing stub is debugged as a failing test
  suite**, while absence names its own fix.
- **A gate that has never been observed failing is not a gate.** Before trusting a green, revert the
  production change in a throwaway git worktree with the tests at HEAD and watch it go red.
- **Test a generator through its real consumer.** Round-trip generated config through `Config.Load`
  and assert on parsed state, or drive `Program.Main` and assert on the exit code. A
  string-contains assertion on generated text passes even when the artifact no longer parses.

Ask of every generated artifact: *if the user never edits this, what does CI say?* If the answer is
"green", the artifact is a lie.

## 3. A test may not be satisfied by an accident of its own environment, and a reviewer brings their own mutations.

Two failure shapes that have now each cost three tasks. Both are rule 2 applied to the *test* rather
than to the product.

**A test that asserts a property of the execution environment can be satisfied by that
environment.** `Rewrite_EmitsTheRunningBinarysAbsolutePath_NotABareProgramName` named exactly the
right property and pinned nothing: under `dotnet test`, `Environment.ProcessPath` **is**
`/usr/lib/dotnet/dotnet`, so the suite's only production-entry assertion was true *because the
defect was present*. Hardcoding that literal into production left the suite green at 181/181.

- Whenever an assertion touches process path, cwd, PATH, user, clock, hostname, TTY, env vars, or
  platform, ask: **could this pass by accident of the test host rather than by the code?**
- If yes, drive it through an **injected overload** (take the value as a parameter; keep the
  production entry a thin delegate) or run it **out-of-process**. Do not add more assertions to the
  same in-process path.
- Where the test only means something if the environment has a particular shape, **assert that shape
  as a precondition** — e.g. `Assert.NotEqual("tman", Path.GetFileNameWithoutExtension(ProcessPath))`
  — so the test fails loudly instead of going vacuous when the environment moves.

**A reviewer originates their own mutations and never re-runs the implementer's.** An implementer's
mutations cluster on the guard they are currently thinking about; the reviewer's value is in
attacking **functions and branches the test names never mention** — helper choice, quoting, wiring at
the production entry point, the text on the safety path. This has caught a real blind spot in every
round it has been applied.

Recipe: throwaway **detached git worktree** at the head commit, record the baseline green, one full
suite run per mutation, restore the source between runs, remove the worktree. Note that `/test` is
gitignored, so a fresh worktree has **no `./test` shim** — `sh -c "exec ./test"` fails with "not
found" and reads as a surviving mutant when it is nothing of the kind. Use
`tman run -- dotnet test tests/Tman.Tests/Tman.Tests.csproj` there — see rule 5 for why the bare
form is not an option even in a throwaway tree.

**A surviving mutant must be disclosed, and the stated reason for its survival must be independently
verified** — "this cannot be tested" is one of the most common shapes a false green takes. Verify by
measurement (probe the actual value) and by neighbourhood (do adjacent mutations die?). Of the two
survivors disclosed under this rule so far, one explanation was true (an equivalent mutant inside the
test host) and one was false (a case file that merely happened not to exist).

## 4. Name the invariant; do not accrete conjuncts.

When a fix appends another `&&` to a guard to exclude the case you just observed, you are building a
blacklist and it is waiting for the next host. Stop and state what must be **true** of the value,
then prove that positively.

The hook's supervisor path went `bare "tman"` → `non-empty && fully-qualified && exists` →
`IsProvenTmanExecutable`. The middle step proved "some absolute path that exists" and so rewrote
`go test ./...` into `/usr/lib/dotnet/dotnet run -- go test ./...` — **worse than the exit 127 it
replaced**, because command substitution is silent where 127 is loud. The durable form names three
facts a path must have and grows no entry per host, and it collapses two separately-added guards
("no path", "someone else's path") into one violation of one invariant in one branch.

A named invariant can also state its own weak edge honestly; an accreted predicate cannot describe
what it fails to cover. And a second source of evidence for the same invariant is a **fallback** —
forbidden by project standard.

**Check every clause of a reviewer's `fix_hint` as a checklist and report per clause.** "Blocker
fixed" is only true when all of them are addressed. A hint whose hard clause was silently dropped
while the easy one shipped cost a full rewind here.

## 5. A throwaway probe is a supervised run, and a self-respawning process dispatches on a sentinel it sets itself.

Promoted 2026-07-26 from an incident, not from a task. At 01:52 CDT a `task-context` subagent on the
lock-reclamation blocker wrote a scratchpad probe (`…/scratchpad/probe/gate/Program.cs`) for
`FileShare` gate semantics and ran it as `timeout 300 dotnet run`. It fork-bombed the box:
**28,061 SIGABRT crashes** (17.5k in the 02:00 hour alone), a high PID of **3,739,031** against a
`pid_max` of 4,194,304, `libc++abi: thread constructor failed: Resource temporarily unavailable`,
~439,000 journal lines in two hours, and journald flushing caches under memory pressure. No OOM
kill — this was **PID, log, and IO exhaustion**. Two independent guards each would have stopped it.

**Every process you start goes through `tman`, including the ones you intend to delete.** `timeout`
sends one signal to one PID; it does not know the tree exists, so an exponentially branching child
set outlives it silently. `tman run --` caps and reaps the whole tree and reports the kill as exit
124/125/126. A scratchpad directory has no `.tman.kdl`, so the PATH shims do not fire there and the
prefix must be explicit: `tman run -- dotnet run`. "It's a five-line probe I'm about to throw away"
is the exact reasoning that removed the only supervisor in the loop.

**Never infer "am I the child?" from `Environment.ProcessPath`.** The probe re-invoked itself with:

```csharp
new ProcessStartInfo(Environment.ProcessPath!)                 // under `dotnet run`: the apphost ./gate
psi.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);   // muxer-style "gate.dll" as args[0]
psi.ArgumentList.Add("hold");
```

Under `dotnet run` the running process is the **apphost**, not the `dotnet` muxer, so the child's
`args[0]` was `…/gate.dll` and the `if (args[0] == "hold")` branch never fired. Every child re-ran
the whole probe, spawn block included — 2 children per generation, unbounded. The dispatch key was
a property of the **host mode**, which differs between `dotnet run`, `dotnet test`, a published
apphost, and `dotnet gate.dll`; the parent had no way to know which shape its own child would see.

- Dispatch on **a sentinel you set and the child cannot avoid seeing** — an environment variable
  (`TMAN_PROBE_ROLE=hold`), read identically in every host mode. Argument position is host-shaped;
  the environment is not.
- **Invert the default: the child branch is what runs unless the parent branch is explicitly opted
  into.** Then a mis-parse yields a process that holds a file and exits, not one that spawns two
  more. Fail-safe means the failure mode is inert, not fertile.
- **Bound the recursion in the code as well**, with a generation counter or a hard child cap, so a
  dispatch bug costs one wasted process instead of the PID space.

Ask of any process that starts a copy of itself: *if the dispatch is wrong, does this terminate?*
