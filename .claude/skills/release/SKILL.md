---
name: release
description: Cut a tman release — pick the version from what actually changed, update CHANGELOG and every doc surface that drifted, review as an outside consumer would, verify the real binary, then tag and push. Use when asked to release, cut a version, publish, or bump tman.
---

# Releasing tman

A tag push is irreversible: `v*` triggers a GitHub release plus publishes to npm, PyPI, and
PSGallery. Everything below happens **before** the tag exists.

Work through the phases in order. Do not skip a phase because a change looks small — the
documentation surfaces here drift silently, and nothing in CI checks them.

## Phase 1 — establish what is actually in the release

```sh
git fetch --tags
git tag -l | sort -V | tail -3          # the last RELEASED version
grep '<Version>' tman.csproj             # may be AHEAD of the last tag and never published
git log --oneline "$(git tag -l | sort -V | tail -1)"..HEAD
git status --short                       # must be clean before you start
```

The csproj version and the newest tag can disagree — a bump commit that was never tagged means
that version was never published. Trust `git tag`, not the csproj, for "what shipped last".

Read the actual commits. The release notes are written from the diff, not from the commit
subjects alone.

## Phase 2 — choose the version honestly

tman is 0.x, so the leading zero absorbs the "breaking" slot. Judge by what a user upgrading
would notice:

| Change | Bump |
| --- | --- |
| Bug fix, docs, internal refactor with no observable difference | patch |
| New flag, new config key, new detected alias — old configs behave identically | patch |
| A default changes, a limit's scope or meaning changes, on-disk state is invalidated, exit-code or output contract changes | **minor** |

A removed or relaxed default is a behavior change even though nothing errors. If an existing
`.tman.kdl` or an existing `~/.tman` store behaves differently after upgrading, it is a minor.

Confirm the number with the user before writing it anywhere.

## Phase 3 — CHANGELOG

`CHANGELOG.md` is [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) format, newest first.

- Write for someone who runs tman, not someone who wrote it. Lead with the observable change,
  then why it changed.
- Every entry that changes behavior goes under a **Changed** heading, and anything that
  invalidates existing state or configuration gets called out explicitly so it is visible
  without reading the diff.
- Link the version heading to the compare view: `[0.2.0]: https://github.com/standardbeagle/tman/compare/v0.1.4...v0.2.0`.
- Do not paste commit subjects. Group by what the user experiences.

## Phase 4 — sync every documentation surface

These drift independently. Grep for the thing you changed across all of them; a defaults change
touches all four.

| Surface | What lives there |
| --- | --- |
| `README.md` | feature bullets, run-flag table with defaults, `.tman.kdl` example, exit codes |
| `site/src/pages/docs.astro` | commands table, run-flag table with defaults, `.tman.kdl` example, exit codes |
| `site/src/pages/index.astro` | pitch copy, feature cards, quick-start comment line |
| `Program.cs` `PrintUsage()` | `tman --help` text |
| `Detect.cs` `RenderConfig()` | what `tman init` scaffolds |

Checks that catch the usual misses:

```sh
# every default documented as a number must still be a default in Caps.SaneDefaults
grep -n "SaneDefaults" -A 10 Caps.cs
grep -rn "max-mem\|max-cpu\|max-parallel\|stall\|retain" README.md site/src/pages/ | grep -v node_modules

# the help text and the README flag table must agree
./bin/Release/net10.0/linux-x64/publish/tman --help
```

Also bump the in-repo installer manifests so a reader is not misled — CI overwrites them from the
tag at publish time, but a stale number in the repo is a documentation bug:

- `installers/npm/package.json`
- `installers/python/pyproject.toml`
- `installers/psgallery/tman.psd1`

## Phase 5 — review as an outside consumer

Open-source releases are read by people with no context. Check:

- **README first screen** — does the pitch still describe what the tool now does? A changed
  default can invalidate the opening paragraph.
- **Quick start actually works** — run it, verbatim, in a scratch directory, using the built
  binary. Not a paraphrase of it.
- **Upgrade path** — if existing state or config behaves differently, the CHANGELOG must say so
  plainly. Someone upgrading should not have to discover it from behavior.
- **`LICENSE` present, `--version` correct, install instructions unchanged or updated.**
- **Repo hygiene** — `CONTRIBUTING.md`, `SECURITY.md`, issue templates. If absent, say so; do not
  invent policy the maintainer has not agreed to.

## Phase 6 — verify the real artifact

Never release on the strength of a debug test run alone.

```sh
git status --short                       # clean
/usr/bin/dotnet test tests/Tman.Tests/Tman.Tests.csproj --nologo
/usr/bin/dotnet publish tman.csproj -c Release -r linux-x64 --nologo   # must be warning-free
./bin/Release/net10.0/linux-x64/publish/tman --version                 # matches the new version
```

Use `/usr/bin/dotnet`, not `dotnet`: a `dotnet` PATH shim re-enters tman and can queue the build
behind an unrelated supervised run.

AOT trim warnings are release blockers — they mean a code path works in tests and fails in the
shipped binary. Confirm zero:

```sh
/usr/bin/dotnet publish tman.csproj -c Release -r linux-x64 --nologo -v n 2>&1 \
  | grep -E "warning (IL|CS)[0-9]+" | sort -u
```

Then smoke-test the built binary against a real project, not a mock, with a scratch
`TMAN_HOME` so the check cannot pass by reusing existing state.

**A green local suite only proves Linux.** The `ci` workflow runs the tests, an AOT publish, and a
binary smoke test on linux-x64, linux-arm64, win-x64, and osx-arm64 for every push to main — so
confirm it is green on the release commit *before* tagging:

```sh
gh run watch "$(gh run list --workflow=ci.yml --branch main --limit 1 --json databaseId --jq '.[0].databaseId')" --exit-status
```

Tagging on an unverified commit is how a platform bug reaches a release. Before pushing, re-read
anything you added or changed for two specific traps:

- **Baked-in POSIX paths.** `/tmp` becomes `D:\tmp` on Windows. Assert on relationships
  (`Dir(x + sep) == Dir(x)`) or build paths with `Path.Combine` and `Path.GetTempPath()`, never
  on a literal absolute path.
- **Linux-only capabilities.** Process-tree walking needs `/proc`, so `TreeStats.CoversTree` is
  false on macOS and Windows and any test that depends on seeing a *descendant's* work must be
  gated on it. Gate on the capability flag, not on `IsLinux()` at the call site — the flag is what
  the product actually branches on.

## Phase 7 — release commit and tag

```sh
git commit -m "chore: release <version>"   # version bump + CHANGELOG + doc sync together
git push origin main
git tag v<version>
git push origin v<version>
```

Confirm with the user before pushing the tag. Then watch CI to completion — a publish job that
fails leaves the ecosystem half-released (a GitHub release with no npm package):

```sh
gh run watch "$(gh run list --workflow=release.yml --limit 1 --json databaseId --jq '.[0].databaseId')"
```

### When the release run fails

The `release` job is gated on `needs: build`, so a build or test failure on **any** platform stops
everything downstream — no GitHub release, no npm, no PyPI, no PSGallery. Check what actually
published before deciding how to recover:

```sh
gh run view <run-id> --json jobs --jq '.jobs[] | "\(.name): \(.conclusion // .status)"'
# per-step detail, and logs readable while the run is still going:
gh api repos/standardbeagle/tman/actions/jobs/<job-id>/logs | grep -A 10 '\[FAIL\]'
```

- **Nothing published** (build failed, so the publish jobs never ran) — the tag points at a commit
  no artifact exists for. Delete it, fix, and re-tag the same version. Skipping to a new number
  leaves a permanent hole that readers of the changelog cannot explain.
- **Anything published** — never move the tag. npm and PyPI reject republished versions, so a moved
  tag produces a release that can never be completed. Fix forward with a new patch version.

Deleting a tag is only safe under the first case, and only for a tag you pushed minutes ago.
Confirm with the user before doing it.
