# seed-dev-data

Populates a Fishbowl context (personal or space) with a small, opinionated set
of sample notes, todos, and contacts so the UI and MCP tools have something
to work against during development.

## Usage

```bash
# Personal workspace of the first user in system.db
dotnet run --project tools/seed-dev-data

# Specific user
dotnet run --project tools/seed-dev-data -- --user 01HZ123...

# Space workspace by slug
dotnet run --project tools/seed-dev-data -- --context space --context-id my-space

# Custom data directory
dotnet run --project tools/seed-dev-data -- --data ./fishbowl-data-alt

# Reseed on top of an already-seeded context (adds a fresh batch)
dotnet run --project tools/seed-dev-data -- --force
```

## What gets seeded

| Kind       | Count | Markers                                                       |
|------------|-------|---------------------------------------------------------------|
| Notes      | 60    | `seed:dev` tag + `[seed-dev] ` title prefix                   |
| Todos      | 4     | `[seed-dev] ` title prefix, one completed                     |
| Contacts   | 4     | `[seed-dev] ` name prefix, one archived                       |
| Events     | 3     | `[seed-dev] ` title prefix, one recurring, one all-day offsite |

The note corpus is grouped by topic (programming, databases, git, cooking,
music, travel, books, health, personal, work, admin) with a handful of
deliberately identifier-heavy entries and two `review:pending` notes.
One note includes a `::secret` block so you can verify the secret-strip path.

## Testing hybrid search

The corpus is structured so each kind of ranking probe has a matching note.
After seeding, **always run the reindex** — `seed-dev-data` writes notes
without embeddings, so vec_notes stays empty and search runs in
degraded (FTS-only) mode until you backfill:

```bash
dotnet run --project tools/reindex-dev
```

Then hit `GET /api/v1/search?q=…` (or the `search_memory` MCP tool).
Suggested probe queries:

| Query                                 | Expected top hit(s)                           | Why it's interesting                        |
|---------------------------------------|-----------------------------------------------|---------------------------------------------|
| `how do servers agree on a value`     | "Raft consensus — leader election basics"     | Semantic win — no keyword overlap           |
| `memory safety without a GC`          | "Rust ownership…", "Memory safety without a GC" | Paraphrase twins should both rank         |
| `CrashLoopBackOff`                    | "Kubernetes CrashLoopBackOff triage"          | Lexical win — identifier-exact             |
| `ENOBUFS`                             | "Linux ENOBUFS — socket buffer exhaustion"    | Lexical win — error code                    |
| `avoid thundering herd`               | "Exponential backoff with jitter"             | Semantic only — no matching word            |
| `sound check` vs `pre-show audio setup` | Both live-audio notes                       | Paraphrase twins across short and long form |
| `umami stock`                         | "Ramen broth — dashi base"                    | Semantic stretch (dashi ≠ umami ≠ stock)    |
| `PRAGMA journal_mode=WAL`             | "SQLite WAL mode in practice"                 | Lexical win — fenced-code token             |
| `fix my body clock`                   | "Jet lag recovery — light timing"             | Semantic win — different vocabulary         |
| `TK-2024-8843`                        | "Insurance policy reference"                  | Lexical win — policy number                 |

Flip `includePending=true` to surface the two `review:pending` notes; the
default filters them out. A `degraded: true` flag in the response means
the embedding model wasn't ready and ranking fell back to FTS.

## Idempotency

The tool refuses to reseed if any row already carries the sentinel — otherwise
every rerun would double the sample set. `--force` overrides that check.

Exit codes match the other dev tools:

| Code | Meaning                                |
|------|----------------------------------------|
| 0    | Seeded (or already seeded — see stdout) |
| 2    | `system.db` missing                    |
| 3    | No users in `system.db`                |
| 5    | `--context space` without `--context-id` |
| 6    | Space slug not found                   |
| 8    | Unknown `--context` value              |

stdout is a single-line JSON summary:
`{"notes":N,"contacts":N,"todos":N,"events":N,"reseeded":true|false}`.
