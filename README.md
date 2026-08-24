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

Early development. The foundation is hardened (CI, OpenAPI, structured logging, typed Dapper mapping). The project is currently being deliberately slimmed — see `docs/slimming-plan.md` for what is being dropped and why, and `docs/superpowers/specs/` for active design work.

## Space-scoped memory via MCP

Fishbowl exposes its memory API as an MCP server at `/mcp` (JSON-RPC 2.0 over Streamable HTTP, Bearer auth). Any agent CLI that speaks MCP can attach. The isolation primitive is a **space** — each space is a separate SQLite file with its own membership and scopes.

To carve out a space for an agent:

```bash
dotnet run --project tools/init-space -- <slug>
```

This creates the space (if absent), mints a space-scoped API key, and prints the bearer on stdout. The integration handle is *(MCP endpoint, Bearer token)*; how an agent CLI stores and presents that token is the agent's concern and lives in its docs, not here. See [`tools/init-space/README.md`](tools/init-space/README.md) for flags and idempotency notes.

## Deploy on a server

The host ships under [Husky](https://github.com/SACRVM/husky) — drop this as `run.ps1` into an empty folder on a Windows server and execute it. First run pulls the latest Husky launcher from GitHub; every run Husky checks `SACRVM/the-fishbowl`'s GitHub Releases and pulls the latest build. To redeploy after a new release tag: just run the script again.

```powershell
$ErrorActionPreference = 'Stop'
$dir = $PSScriptRoot
$exe = Join-Path $dir 'Husky.exe'

if (-not (Test-Path $exe)) {
    $zip = Join-Path $env:TEMP 'husky.zip'
    Invoke-WebRequest 'https://github.com/SACRVM/husky/releases/latest/download/husky-win-x64.zip' -OutFile $zip -UseBasicParsing
    Expand-Archive $zip $dir -Force
    Remove-Item $zip
}

# Detached so this PowerShell session can close without killing the launcher.
$proc = Start-Process -FilePath $exe -PassThru `
    -ArgumentList @('--repo','SACRVM/the-fishbowl','--asset','Fishbowl-{version}-win-x64.zip','--dir',$dir)
Write-Host "Husky started (PID $($proc.Id)). You can close this window." -ForegroundColor Green
```

For a permanent install that survives user logoff and restarts at boot, register Husky as a Windows service via NSSM — see [`docs/deploy.md`](docs/deploy.md) §5.

The user-data folder (`fishbowl-data/`, holds `system.db` + per-user/space SQLite files + the embeddings model) lives next to the binary inside `app/` and is preserved across updates because Husky overlays new files without deleting. Linux/macOS deployments use the matching RID asset and override `executable` in a local `husky.config.json` (the repo-root config is Windows-pathed by default).

See [`docs/deploy.md`](docs/deploy.md) for the long-form runbook: the recommended ACME-staged sequence, post-lockout config edits via `/api/v1/admin/config`, scheduled backups with `tools/snapshot-data`, and a troubleshooting section for the failure modes the launcher can land in.

## Licence

AGPL-3.0 — full text in [`LICENSE`](LICENSE).

Copyright (c) 2026 Fishbowl contributors

Fishbowl is a network-facing server application, which is what the AGPL is for:
if you run a modified version and let others interact with it over a network,
§13 requires you to offer those users the corresponding source of your version.
