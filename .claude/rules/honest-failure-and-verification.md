# Honest failure and verification

Promoted to project tier 2026-07-25 from module steering:

- `docs/solutions/tman/atomic-slot-claim-and-honest-gates-2026-07-25.md` §3, §4
  (`source_event: task:01KYD8STXXTKKN5RZ6G7YCVF69`)
- `docs/solutions/tman/honest-scaffold-defaults-2026-07-25.md` §1
  (`source_event: task:01KYD8VQ8RZE581VG3VZQTMMM3`)

Both lessons recurred across two independent tasks with independent reviewer re-derivation, and both
were flagged **project-wide** by the reviewer (`appliesTo: any agent or CI job verifying this repo`).

---

## 1. Verify this repo with `sh -c "exec ./test"`. Nothing else counts.

**Never run bare `dotnet test` from the repo root.** It resolves `tman.csproj` — the application
project, not a test project — and **exits 0 in about 3 seconds having run zero tests**. The real
suite is 143 tests under `tests/Tman.Tests/`.

**Never pass a bare relative `argv[0]`** such as `./test` to a workflow command step or any exec
that does not go through a shell. A relative `argv[0]` is resolved through **PATH, not cwd**; on
this fleet `./test` lands on `/home/beagle/.local/bin/test`, an unrelated uv-installed pytest
symlink.

Correct invocations, and only these:

```sh
sh -c "exec ./test"                              # preferred
dotnet test tests/Tman.Tests/Tman.Tests.csproj   # explicit path
```

Worktrack tasks in this repo must attach `templateName="tman-slice-v2"`.
**Do not use `slice-default`** — its test gate is wired to bare `dotnet test` and therefore cannot
go red.

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
