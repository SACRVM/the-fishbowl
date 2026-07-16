# reindex-dev

Dev utility. Re-runs the MiniLM embedding pass for every note in a context and prints `{ processed, failed }` — same behaviour as `POST /api/v1/search/reindex`, but driven directly against the disk so no cookie session is required.

The HTTP endpoint stays the production path (cookie-auth only); this tool exists so local iteration — running the host, minting Bearer tokens, sanity-checking hybrid ranking — doesn't force you into a browser every time.

## Usage

```bash
# default: first user in system.db, personal context
dotnet run --project tools/reindex-dev -- --data src/Fishbowl.Host/fishbowl-data

# space context
dotnet run --project tools/reindex-dev -- \
  --data src/Fishbowl.Host/fishbowl-data \
  --context space --context-id fishbowl-dev
```

## Flags

| Flag | Default | Notes |
|---|---|---|
| `--data` | `fishbowl-data` | Path to the Fishbowl data directory. |
| `--user` | first user in `system.db` | Override to pick a specific user id. |
| `--context` | `user` | `user` or `space`. |
| `--context-id` | — | Required when `--context space`. Accepts slug or id. |

## Output

stdout: a single JSON line, e.g. `{"processed":16,"failed":0}`. Stderr carries progress + context info.

## Exit codes

| Code | Condition |
|---|---|
| 0 | success; every note re-embedded cleanly |
| 2 | data dir or system.db missing |
| 3 | no users in system.db |
| 5 | `--context space` without `--context-id` |
| 6 | space slug/id not found |
| 8 | unknown `--context` value |
| 9 | MiniLM model isn't on disk yet — start the host once so it downloads |
| 10 | finished, but at least one note failed (inspect host logs for the `Re-embed failed for note {Id}` warning) |

## Concurrency

Safe to run while the host is live — SQLite handles concurrent access, and `ReEmbedAllAsync` uses the same transaction pattern as a regular note write. You may see brief pauses in the UI during the run at personal-memory scale; imperceptible at typical note counts.
