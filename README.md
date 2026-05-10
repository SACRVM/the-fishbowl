# The Fishbowl

> *Your memory lives here. You don't.*

Self-hosted, personal memory and assistant application. Open source, extendable, and entirely yours — your data is a single SQLite file you can take anywhere, at any time.

See [`CONCEPT.md`](CONCEPT.md) for the product vision and full architecture.
See [`CONTRIBUTING.md`](CONTRIBUTING.md) for how to work on the code.

## Running locally

Requires the .NET 10 SDK.

```bash
dotnet run --project src/Fishbowl.Host
```

The app starts at `https://localhost:7180`. First run opens a setup page to configure Google OAuth credentials; after that `/login` works and the API becomes available under `/api/v1/`.

```bash
dotnet test                                          # all tests
dotnet test --filter "FullyQualifiedName~TestName"   # one test
```

## Status

Early development. The foundation is hardened (CI, OpenAPI, plugin contracts, structured logging, typed Dapper mapping). Feature work on search, Discord bot, calendar sync, reminders, teams, and apps follows — see `docs/superpowers/specs/` for active design work.

## Wire up a Firepit project

[Firepit](https://github.com/chloe-dream/firepit-ai) hosts Claude Code (and other agent CLIs) in tabs, one per project. To use Fishbowl as the per-project memory backend, run:

```bash
dotnet run --project tools/init-project -- <project-slug>
```

This creates a Fishbowl team with that slug (the underlying file-boundary mechanism; "team" is the internal name, "project" is what the user sees), mints a team-scoped API key, and prints a ready-to-paste `cmdkey` line plus a Firepit `mcpOverrides` snippet. The project then has its own URL at `https://localhost:7180/p/<slug>`. See [`tools/init-project/README.md`](tools/init-project/README.md) for flags and idempotency notes.

## Deploy on a server

The host ships under [Husky](https://github.com/chloe-dream/husky) — drop this as `run.ps1` into an empty folder on a Windows server and execute it. First run pulls the latest Husky launcher from GitHub; every run Husky checks `chloe-dream/the-fishbowl`'s GitHub Releases and pulls the latest build. To redeploy after a new release tag: just run the script again.

```powershell
$ErrorActionPreference = 'Stop'
$dir = $PSScriptRoot
$exe = Join-Path $dir 'Husky.exe'

if (-not (Test-Path $exe)) {
    $zip = Join-Path $env:TEMP 'husky.zip'
    Invoke-WebRequest 'https://github.com/chloe-dream/husky/releases/latest/download/husky-win-x64.zip' -OutFile $zip -UseBasicParsing
    Expand-Archive $zip $dir -Force
    Remove-Item $zip
}

& $exe --repo 'chloe-dream/the-fishbowl' --asset 'Fishbowl-{version}-win-x64.zip' --dir $dir
```

The user-data folder (`fishbowl-data/`, holds `system.db` + per-user/team SQLite files + the embeddings model) lives next to the binary inside `app/` and is preserved across updates because Husky overlays new files without deleting. Linux/macOS deployments use the matching RID asset and override `executable` in a local `husky.config.json` (the repo-root config is Windows-pathed by default).

## Licence

AGPL-3.0
