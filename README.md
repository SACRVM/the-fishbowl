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

## Team-scoped memory via MCP

Fishbowl exposes its memory API as an MCP server at `/mcp` (JSON-RPC 2.0 over Streamable HTTP, Bearer auth). Any agent CLI that speaks MCP can attach. The isolation primitive is a **team** — each team is a separate SQLite file with its own membership and scopes.

To carve out a team for an agent:

```bash
dotnet run --project tools/init-team -- <slug>
```

This creates the team (if absent), mints a team-scoped API key, and prints the bearer on stdout. The integration handle is *(MCP endpoint, Bearer token)*; how an agent CLI stores and presents that token is the agent's concern and lives in its docs, not here. See [`tools/init-team/README.md`](tools/init-team/README.md) for flags and idempotency notes.

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

See [`docs/deploy.md`](docs/deploy.md) for the long-form runbook: the recommended ACME-staged sequence, post-lockout config edits via `/api/v1/admin/config`, scheduled backups with `tools/snapshot-data`, and a troubleshooting section for the failure modes the launcher can land in.

## Licence

AGPL-3.0
