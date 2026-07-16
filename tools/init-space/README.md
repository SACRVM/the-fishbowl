# init-space

Dev utility. Creates a Fishbowl space (if absent) and mints a space-scoped API key with full read/write scopes (or a narrower set via `--scopes`). The bearer goes to stdout; a status banner goes to stderr. Idempotent on the slug.

## What it solves

A space in Fishbowl is the file-boundary isolation primitive — each space has its own SQLite file under `spaces/{spaceId}/`, its own roles, and its own MCP scope. To carve out a memory namespace for an agent CLI, you need a space, a key bound to that space, and the bearer in the hands of the agent. This tool produces the first two and prints the third on stdout for the caller to route.

How the bearer is stored and consumed (credential vault, environment variable, agent-CLI config) is the consumer's concern and lives in the consumer's docs — Fishbowl just hands out tokens.

## Usage

```bash
# create-or-reuse space 'lighthouse' for the first user in system.db
dotnet run --project tools/init-space -- lighthouse \
  --data src/Fishbowl.Host/fishbowl-data
```

```bash
# pretty display name + scoped-down key (search-only agent)
dotnet run --project tools/init-space -- lighthouse \
  --data src/Fishbowl.Host/fishbowl-data \
  --name "Lighthouse" \
  --scopes read:notes,read:tags,read:contacts,read:events
```

## Flags

| Flag | Default | Notes |
|---|---|---|
| `<slug>` | — | Positional, required. Lowercase letters/digits/hyphens, 1-60 chars. The space slug is used as the URL fragment (`/#/space/<slug>/notes`) and as the default key/display name. |
| `--data` | `fishbowl-data` | Path to the Fishbowl data directory. |
| `--user` | first user in `system.db` | Owns the space if it gets created. Re-running for an existing space requires this user to already be a member. |
| `--name` | same as `<slug>` | Display name stored on the space row. |
| `--scopes` | every `read:*` + `write:*` scope | Comma-separated. See `ScopeCatalog.All` for the canonical set. |
| `--key-name` | `space-<slug>` | Human-readable label stored on the API key row. |

## Output

stdout: the raw bearer token on its own line (pipe-friendly).
stderr: status banner — space id, key id, scopes, space URL, MCP endpoint.

```bash
TOKEN=$(dotnet run --project tools/init-space -- lighthouse 2>/dev/null)
```

## Idempotency

Re-running with the same `<slug>` reuses the existing space and just mints a fresh API key (old keys are not revoked — do that from the web UI or via a future revoke tool). Safe to wire into a `setup.ps1` you re-run after pulling.

## Exit codes

| Code | Condition |
|---|---|
| 0 | success |
| 2 | missing positional `<slug>` |
| 3 | slug fails the `^[a-z0-9]([a-z0-9-]{0,58}[a-z0-9])?$` check |
| 4 | data dir or `system.db` missing — start the host once first |
| 5 | `--scopes` contains an unknown entry |
| 6 | no users in `system.db` — log in via the web UI first |
| 7 | space with that slug exists but the chosen user isn't a member |
| 8 | slug raced (a space with the requested slug appeared mid-create) |
