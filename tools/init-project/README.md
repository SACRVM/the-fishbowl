# init-project

Dev utility. Bootstraps a Fishbowl team that backs a [Firepit](https://github.com/chloe-dream/firepit-ai) (or Claude-Code) project: creates the team if absent, mints a team-scoped API key with full read/write scopes, and prints a ready-to-paste `cmdkey` command + Firepit `settings.json` snippet.

## What it solves

Every project that wants Fishbowl as its memory backend needs four things wired up: a Fishbowl team, an API key bound to that team, the bearer stored in a credential vault, and a Firepit MCP entry referencing it. This tool makes it one command.

## Usage

```bash
# create-or-reuse team 'firepit' for the first user in system.db
dotnet run --project tools/init-project -- firepit \
  --data src/Fishbowl.Host/fishbowl-data
```

```bash
# pretty display name + scoped-down key (search-only agent)
dotnet run --project tools/init-project -- lighthouse \
  --data src/Fishbowl.Host/fishbowl-data \
  --name "Lighthouse" \
  --scopes read:notes,read:tags,read:contacts,read:events
```

## Flags

| Flag | Default | Notes |
|---|---|---|
| `<slug>` | — | Positional, required. Lowercase letters/digits/hyphens, 1-60 chars. Same string is used as the team slug, the credential-manager target (`firepit/fishbowl-<slug>`), and the project URL alias (`/p/<slug>`). |
| `--data` | `fishbowl-data` | Path to the Fishbowl data directory. |
| `--user` | first user in `system.db` | Owns the team if it gets created. Re-running for an existing team requires this user to already be a member. |
| `--name` | same as `<slug>` | Display name stored on the team row. |
| `--scopes` | every `read:*` + `write:*` scope | Comma-separated. See `ScopeCatalog.All` for the canonical set. |
| `--key-name` | `firepit-<slug>` | Human-readable label stored on the API key row. |

## Output

stdout: the raw bearer token on its own line (pipe-friendly).
stderr: status banner + the `cmdkey` line + the Firepit `settings.json` snippet + the project URL.

```bash
TOKEN=$(dotnet run --project tools/init-project -- firepit 2>/dev/null)
```

## Idempotency

Re-running with the same `<slug>` reuses the existing team and just mints a fresh API key (old keys are not revoked — do that from the web UI or via a future revoke tool). Safe to wire into a `setup.ps1` you re-run after pulling.

## Exit codes

| Code | Condition |
|---|---|
| 0 | success |
| 2 | missing positional `<slug>` |
| 3 | slug fails the `^[a-z0-9]([a-z0-9-]{0,58}[a-z0-9])?$` check |
| 4 | data dir or `system.db` missing — start the host once first |
| 5 | `--scopes` contains an unknown entry |
| 6 | no users in `system.db` — log in via the web UI first |
| 7 | team with that slug exists but the chosen user isn't a member |
| 8 | slug raced (a team with the requested slug appeared mid-create) |
