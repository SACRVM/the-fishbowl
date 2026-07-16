# mint-dev-key

Dev utility. Mints an API key against the real `ApiKeyRepository` and prints the raw token to stdout.

## Usage

```bash
# default: first user in system.db, personal context, read+write scopes
dotnet run --project tools/mint-dev-key -- --data src/Fishbowl.Host/fishbowl-data

# read-only key (for scope-enforcement testing — section 5 of the test plan)
dotnet run --project tools/mint-dev-key -- \
  --data src/Fishbowl.Host/fishbowl-data \
  --name read-only-probe \
  --scopes read:notes

# space-scoped key (for context-isolation testing — section 8)
dotnet run --project tools/mint-dev-key -- \
  --data src/Fishbowl.Host/fishbowl-data \
  --name space-probe \
  --context space \
  --context-id fishbowl-dev

# pipe into an env var
TOKEN=$(dotnet run --project tools/mint-dev-key -- --data src/Fishbowl.Host/fishbowl-data 2>/dev/null)
```

## Flags

| Flag | Default | Notes |
|---|---|---|
| `--data` | `fishbowl-data` | Path to the Fishbowl data directory. The tool reads `<data>/system.db`. |
| `--user` | first user in `system.db` | Override to mint for a specific user id. |
| `--name` | `claude-code-local` | Human-readable label stored on the key row. |
| `--scopes` | `read:notes,write:notes` | Comma-separated. Valid scopes today: `read:notes`, `write:notes`. |
| `--context` | `user` | `user` or `space`. |
| `--context-id` | — | Required when `--context space`. Accepts the space slug or space id. |

## Output

First line of stdout = the raw token (`fb_live_…`). Everything else (id, user id, resolved context, scopes) goes to stderr so the output stays pipe-friendly:

```bash
TOKEN=$(dotnet run --project tools/mint-dev-key -- --data src/Fishbowl.Host/fishbowl-data 2>/dev/null)
```

## Exit codes

| Code | Condition |
|---|---|
| 0 | success |
| 2 | data dir or system.db missing — start the host once first |
| 3 | no users in system.db — log in via the web UI first |
| 4 | `--scopes` parsed to zero entries |
| 5 | `--context space` without `--context-id` |
| 6 | space slug/id not found |
| 7 | user isn't a member of the specified space |
| 8 | unknown `--context` value |

## Not a production path

Production keys get minted through the web UI — users see the token exactly once, then it's SHA-256 hashed at rest. This tool bypasses the UI for local development convenience only; the underlying storage + hashing go through the real `ApiKeyRepository`, so tokens minted here are indistinguishable at runtime from UI-minted ones.
