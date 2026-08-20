---
description: Cut a Fishbowl release — pre-flight, tag, push. release.yml then builds the 4 RID zips and creates the GitHub Release.
argument-hint: [test|patch|minor|major|<x.y.z>]
---

# /release — cut a Fishbowl release

You are cutting a Fishbowl release. Follow these steps **exactly**, in order. **Never skip the confirmation step.** Speak to the owner in German; tag names and commit messages stay English.

## What makes a Fishbowl release different

Unlike a library release (e.g. retro-crt → NuGet), there is **no version in any csproj** and **no PublicAPI to promote**. The version is derived from the git tag at publish time inside `release.yml` (`-p:Version=${tag#v}`). A release therefore consists of **a single git tag** — no release commit, no file edits, no `<Version>` bump. The tag push triggers `release.yml`, which builds 4 RID zips (`win-x64`, `linux-x64`, `osx-x64`, `osx-arm64`) and publishes a GitHub Release with auto-generated notes. Husky-supervised installs pick up the new release on next launcher poll.

This makes the flow short. Don't pad it with steps that don't apply.

## Argument

`$ARGUMENTS` is a **single token**:

- (empty) — auto-detect bump from commits since the last `v*` tag
- `test` — dry-run: print the plan, do nothing
- `patch` / `minor` / `major` — force that bump
- `0.2.0` (or any `X.Y.Z` / `X.Y.Z-suffix`) — set this exact version

Examples: `/release`, `/release test`, `/release patch`, `/release 0.2.0`, `/release 1.0.0-rc1`.

## Step 1 — Sanity checks

Run all of these. If any fails, stop and report to the owner — do not proceed.

```bash
git rev-parse --abbrev-ref HEAD                                    # must be 'master'
test -z "$(git status --porcelain)" && echo clean || echo dirty    # must be 'clean'
git fetch origin master --quiet
test "$(git rev-parse HEAD)" = "$(git rev-parse origin/master)" && echo synced || echo behind  # must be 'synced'
dotnet format --verify-no-changes Fishbowl.sln
dotnet build Fishbowl.sln -c Release --nologo
```

`dotnet build` of a `.Tests` project auto-runs `dotnet test` via `src/Directory.Build.targets`, so the build step also covers tests in this checkout. Don't run `dotnet test` separately — it would double-execute and waste 60-90s.

If format or build fail: **stop**. Leave the working tree alone so the owner can inspect. Do not auto-fix and re-run.

## Step 2 — Determine current state

```bash
git describe --tags --match 'v*' --abbrev=0                    # last release tag (e.g. v0.1.1)
git log $(git describe --tags --match 'v*' --abbrev=0)..HEAD --format='%s'  # commits since
```

If no `v*` tag exists yet (only the case before the very first release), treat the last tag as `v0.0.0` and analyze all commits.

## Step 3 — Decide the bump

Parse `$ARGUMENTS`:

| Arg | Action |
|---|---|
| empty | classify commits by Conventional-Commits prefix. Any `feat:` / `feat(scope):` → **minor**. Only `fix:` → **patch**. Only `docs:` / `chore:` / `refactor:` / `test:` / `build:` / `ci:` → **nothing to release** — stop and tell the owner |
| `test` | same classification as empty, but stop after the plan in Step 4 |
| `patch` | force Z+1 |
| `minor` | force Y+1, Z=0 |
| `major` | force X+1, Y=0, Z=0 (pre-1.0 — confirm twice before proceeding past Step 4) |
| `X.Y.Z` (or `X.Y.Z-suffix`) | use literally — must be greater than the current last tag (semver compare); reject if equal or older |

Compute `new_version` from the **last tag**, not from the csproj (there is none).

## Step 4 — Show the plan, ask for confirmation

Print to the owner in German, exactly like:

```
Letzter Tag:       v0.1.1
Commits seit Tag:  6 (2 feat, 1 fix, 3 docs/chore/refactor)
Vorgeschlagener Bump: minor (weil 2 feats drin)
Neue Version:      v0.2.0
Was passiert beim Push:
  - release.yml triggert
  - 4 RID-Zips bauen (win-x64, linux-x64, osx-x64, osx-arm64) — ~6-8 min
  - GitHub Release wird mit auto-generierten Notes erstellt
  - Husky-Installs ziehen beim nächsten Poll automatisch nach
```

Then list the commits since the last tag, grouped by prefix (feat / fix / chore / docs / refactor / test / build / ci / other), as a sanity check for the owner.

**If `$ARGUMENTS` is `test`: stop here. Tell the owner „Trockenlauf — nichts geändert."**

Otherwise: ask **„OK so? [j/n]"** and wait for the answer.
- `j` / `ja` / `y` / `yes` → continue to Step 5
- anything else → abort, change nothing, push nothing

## Step 5 — Tag and push

```bash
git tag v<NEW>
git push origin v<NEW>
```

That's it. No release commit. No csproj edit. No file changes. The tag *is* the release artefact.

**Never push tags without Step 4 confirmation.** **Never use `--force` on tag pushes** — if a tag with that name already exists somewhere, stop and ask the owner.

## Step 6 — Hand off

Tell the owner in German, exactly like:

```
Release v0.2.0 ist getaggt und gepusht.
- release.yml läuft → https://github.com/SACRVM/the-fishbowl/actions
- 4 RID-Zips in ~6-8 min auf https://github.com/SACRVM/the-fishbowl/releases/tag/v0.2.0
- GitHub Release Notes werden automatisch generiert
- Husky-Server: nächster Launcher-Poll zieht das Update; auf Wunsch run.ps1 erneut ausführen
```

Then **stop**. Do not poll the CI run. Do not `gh release view` (it crashes on Windows with the go-keyring bug). To verify asset upload later, hit the releases page directly or use `curl` against `https://api.github.com/repos/SACRVM/the-fishbowl/releases/tags/v<NEW>`.

## Hard rules

- Tag scheme is `v*.*.*` only — never `release-*`, never `fishbowl-v*`, never package-prefixed (this is a single-product repo).
- Branch is `master`, not `main`. A `/release` from any other branch is rejected at Step 1.
- Never commit a `<Version>` element into any csproj as part of a release — `release.yml` injects the version from the tag at publish time. A hard-coded version in the csproj would shadow that and ship the wrong version number in the banner.
- Never use `--no-verify`, `--force`, or `--force-with-lease` on any git command in this flow.
- If the plan in Step 4 shows zero `feat:` and zero `fix:` commits, that's „nichts zu releasen" — stop, tell the owner, do not invent a release.
- If `git describe` returns weird output (multiple tags on HEAD, partial match, etc.), stop and ask the owner instead of guessing.
- Cross-repo rule applies: this command never touches anything outside `the-fishbowl`. The Husky repo is the launcher's home; if release.yml needs launcher-side changes, raise it in the Husky repo's issue tracker.
