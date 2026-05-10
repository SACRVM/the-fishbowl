# CLAUDE.md

Guidance for Claude Code in this repository.

## What this project is

Self-hosted personal memory + assistant. **`CONCEPT.md` is the target spec** — much of it (Discord bot, search, sync, scripting, teams, triggers) is future design. Today only `Fishbowl.Core`, `.Data`, `.Api`, and `.Host` have real implementations; other projects are empty shells. Align with CONCEPT.md — don't invent a different architecture. See `CONTRIBUTING.md` and `docs/superpowers/` for active work.

## Working style — adaptive programming

Find the closest existing solution and **extend it**, don't parallel it. Almost-fits → add overload/option/partial. Genuinely wrong → say so and migrate callers. **Never silently diverge.**

- **HTTP endpoints**: `MapXxxApi()` extensions on `IEndpointRouteBuilder`; resolve the request's scope via `McpContextClaims.Resolve(user)` → `ContextRef` (User or Team), call a repository. Gate with `.RequireScope("read:notes")` etc. — cookie principals bypass scope; Bearer principals must match. Routes under `/api/v1/`.
- **Repositories**: interface in `Fishbowl.Core.Repositories`, impl in `Fishbowl.Data.Repositories`, `AddScoped`. Access via `DatabaseFactory.CreateContextConnection(ctx)` or `CreateSystemConnection()`. The legacy `CreateConnection(userId)` is a `ContextRef.User`-alias kept for cookie callers.
- **Multi-step writes**: `_dbFactory.WithContextTransactionAsync(ctx, async (db, tx, ct) => …)` — see `NoteRepository` for `notes` + `notes_fts` syncing.
- **Schema changes**: add `ApplyVN` in `DatabaseFactory`, bump `PRAGMA user_version`. No EF Core, no migration runner.
- **UI resources/scripts/templates**: `IResourceProvider.GetAsync(path)` only (disk → DB → embedded). Never direct file I/O.
- **Plugins**: implement `IFishbowlPlugin`, register via `IFishbowlApi.AddBotClient/AddSyncProvider/AddScheduledJob` (`Fishbowl.Core.Plugins`).
- **Configuration**: in `system.db` via `ISystemRepository.Get/SetConfigAsync` — never `appsettings.json`, never env vars for runtime config.
- **Frontend backend calls**: `fb.api.notes.list()` etc. — 401s auto-redirect to `/login`. Never `fetch` directly from views/components.

## Commands

Target framework is **`net10.0`** across every project.

```bash
dotnet build Fishbowl.sln
dotnet run --project src/Fishbowl.Host           # https://localhost:7180
dotnet watch run --project src/Fishbowl.Host     # hot reload
dotnet test
dotnet test --filter "FullyQualifiedName~TestName"
```

`src/Directory.Build.targets` auto-runs `dotnet test --no-build` after any `*.Tests` compile (skip via `ContinuousIntegrationBuild=true`). Test stack is **xUnit v3**; cancellation tokens come from `TestContext.Current.CancellationToken`, not an ambient source.

## Architecture

**Dependency direction:** `Host → Api/Bot.Discord/Sync/Scheduler/Scripting → Data/Search → Core`. `Fishbowl.Host` is the **only publish target**; everything else is a library. No cycles, no upward refs.

**One `DatabaseFactory` (singleton), three DB locations:**
- `CreateSystemConnection()` → `fishbowl-data/system.db` — `users`, `user_mappings`, `teams`, `team_members`, `api_keys`, `system_config`.
- `CreateContextConnection(ContextRef.User(userId))` → `fishbowl-data/users/{userId}/personal.db` — notes, events, todos, tags, notes_fts, vec_notes. Folder-per-context: dropping the whole `users/{userId}/` folder onto another instance moves the user's data atomically (cold import).
- `CreateContextConnection(ContextRef.Team(slug))` → `fishbowl-data/teams/{teamId}/team.db` — identical schema to user DBs. Folder is keyed by the team's ULID, not its slug — slug can change (rename) while the folder stays put. `ResolveTeamAsync` translates slug→id before opening.
- Legacy flat layout (`users/{id}.db`, `teams/{id}.db`) auto-migrates on first open — `MigrateLegacyLayoutIfPresent` moves the file into its new folder. Idempotent; never overwrites a pre-existing new-path file.

Lazy migrations keyed on `PRAGMA user_version`. Dapper raw SQL; IDs are ULIDs (`Ulid.NewUlid().ToString()`).

**sqlite-vec loaded on every context open.** `SqliteVecLoader.LoadInto(conn)` enables extension loading, calls `LoadExtension("vec0")`, then flips extensions back off. Binaries ship via the `sqlite-vec` NuGet package (v0.1.7-alpha.2 by roji + jeffhandley) — MSBuild drops them under `runtimes/{rid}/native/` automatically. System DB skips this (no vec tables there).

**Two auth schemes, one principal shape.** Cookie (Google OAuth; default) and ApiKey (Bearer `fb_live_…`). On first login, `Program.cs` creates a Fishbowl user, maps `(provider, providerId) → internalUserId`, adds a `fishbowl_user_id` claim. Bearer keys additionally carry `fishbowl_context_type` + `fishbowl_context_id` + one `scope` claim each — `McpContextClaims.Resolve(user)` collapses all of that to a single `ContextRef`. The cookie scheme forwards Bearer requests to ApiKey via `ForwardDefaultSelector`, so `.RequireAuthorization()` is scheme-agnostic. Data isolation is by file boundary, not row filtering.

**OAuth creds in `system.db`, not `appsettings.json`.** `GoogleOptions` binds against `ConfigurationCache` populated at startup by `ConfigurationInitializer : IHostedService`. `POST /api/setup` writes both DB and cache so changes propagate without a restart. Unconfigured options use sentinel `"placeholder"`.

**API/MCP paths return 401, not 302.** `OnRedirectToLogin` is overridden so `/api` and `/mcp` requests get `401` instead of redirect to Google — browsers following the redirect would ruin Bearer/MCP flows. OpenAPI doc at `/api/openapi.json`.

**`/setup` locked after config.** Once `Google:ClientId` is set, `GET /setup` and `POST /api/setup` return `404` (not 302 — harder to bypass). Validates: ClientId ends `.apps.googleusercontent.com`, ClientSecret ≥ 20 chars. No antiforgery (only responds when unconfigured).

**`IResourceProvider` serves the web UI and mods.** Three-tier: disk (`fishbowl-mods/{path}`) → DB (not yet wired) → embedded in `Fishbowl.Data.dll`. `MapFallback` handles non-API paths; unmatched `/api/*` short-circuits to 404 (so it doesn't serve `index.html`). The private `TryOpenEmbeddedStream` helper tries three path forms because MSBuild's `RecursiveDir` produces Windows-style paths on Windows — keep it.

**Plugins in isolated ALCs.** `PluginLoadContext` uses `AssemblyDependencyResolver` in a collectible ALC so `fishbowl-mods/plugins/` DLLs ship their own deps. `PluginLoader.LoadPlugins` runs at startup (`Plugins:Path`, default `fishbowl-mods/plugins`); failures logged and skipped.

**UI is vanilla JS SPA — no framework, no build step.** `index.html` is the shell; views mount in `#app-root` via `fb.router.register("#/path", "tag-name", { label, icon })`. **Views use light DOM** (so `app.css` applies); **components use Shadow DOM** (style isolation). Theme via CSS custom props on `:root` (`--accent`, `--accent-warm`, `--danger`, `--glass`, `--border`, `--text-muted`). System components prefixed `fb-`; mods use `usr_` in `fishbowl-mods/components/`. `fb.icons.register(name, path)` extends icons. `/login` and `/setup` stay server-rendered. Component spec: `docs/superpowers/specs/2026-04-19-ui-foundation-design.md`.

**Context is URL-driven** (personal vs team workspace). `#/notes` = personal, `#/team/<slug>/notes` = team. `fb.context` in `/js/lib/context.js` parses the hash, exposes `get/endpoint/hashFor/set`, and emits `fb:context-changed` on every switch. `fb.api.{notes,tags,todos,search}` route through `fb.context.endpoint(path)` so the same wrapper hits `/api/v1/...` or `/api/v1/teams/<slug>/...` automatically — no mirror client code. The router strips the team prefix for view lookup (`fb.router.currentResource()`) so one view registration serves both workspaces. Hrefs that should be sticky across contexts (nav items, hub tiles, brand) wrap through `fb.context.hashFor()`. Switcher: `<fb-context-switcher slot="context">` inside `<fb-nav>`; the ribbon exposes a dedicated `.context` slot that `renderToolbar()` never touches (so the switcher survives per-view toolbar paints). Team context tints the switcher with `--accent-warm` so edits to shared data are unmissable. `fb.tags` cache invalidates on `fb:context-changed`.

**UI smoke test (`Fishbowl.Ui.Tests`)** launches `Fishbowl.Host` as a real subprocess on a free port for Playwright (not `TestServer`). A gated bypass (`FISHBOWL_PLAYWRIGHT_TEST` env + `Testing` environment) injects a test user. **Never set `FISHBOWL_PLAYWRIGHT_TEST` outside that fixture.**

**MCP surface** at `POST /mcp` — JSON-RPC 2.0 over Streamable HTTP, Bearer-auth only. Five tools in `Fishbowl.Mcp.Tools/*`: `search_memory`, `remember`, `get_memory`, `update_memory`, `list_pending`. Each implements `IMcpTool` with a `RequiredScope`; `ToolRegistry` lists them for `tools/list`. MCP writes pass `NoteSource.Mcp` → `NoteRepository.ApplySourceTags` auto-adds `source:mcp` + `review:pending`. Human edits (cookie path) strip `review:pending` (approval by editing). **Secret-strip invariant:** every note response runs through `SecretStripper.StripNote` — `::secret`…`::end` blocks and `content_secret` blobs must never cross the MCP wire. Enforced by `SecretStripInvariantTests`.

**Hybrid search** (`HybridSearchService` in `Fishbowl.Data.Search`) blends sqlite-vec cosine distance (70%) with FTS5 bm25 (30%), each min-max normalised over a 50-candidate pool. `search_memory` tool always calls it; a `degraded: true` flag surfaces on the response when the embedding model isn't ready yet and ranking falls back to FTS-only. FTS query tokens are split on non-alphanumeric then prefix-matched with `AND` — matches how FTS5's default tokenizer indexes hyphenated identifiers. Lives in `Fishbowl.Data.Search` (not `Fishbowl.Search`) because it needs `DatabaseFactory`, and the dep direction forbids `Fishbowl.Search → Fishbowl.Data`; same reason `SqliteVecLoader` is in `Fishbowl.Data`.

**Embeddings:** `MiniLmPipeline` (ONNX Runtime + `Microsoft.ML.Tokenizers.BertTokenizer`) produces 384-dim L2-normalised float vectors for `sentence-transformers/all-MiniLM-L6-v2`. `ModelDownloader` pulls `model.onnx` + `vocab.txt` from HuggingFace on first start, SHA-256-pinned for the LFS-stored model. `EmbeddingInitializer` (`IHostedService`) runs the download detached. Callers on the hot path catch `EmbeddingUnavailableException` and degrade — `NoteRepository.UpsertEmbeddingAsync` logs and continues, `HybridSearchService` flips to FTS-only. `IEmbeddingService` lives in `Fishbowl.Core.Search` so `NoteRepository` can depend on it without a reverse-dep into `Fishbowl.Search`.

**vec_notes sync.** User DB v4 adds `vec_notes` (sqlite-vec virtual table, `FLOAT[384]`). Every `NoteRepository` write embeds `title + stripped content + tags` and writes the blob in the same transaction as `notes` + `notes_fts`. sqlite-vec's vec0 doesn't honour `INSERT OR REPLACE` as a true replace — use **DELETE then INSERT** explicitly. On delete, remove from `vec_notes` BEFORE `notes` (same pattern as `notes_fts`). Text fed to the model is always secret-stripped — a vector built from secret content would let semantic search surface notes on hidden text, violating the same invariant as the MCP wire.

**`POST /api/v1/search/reindex`** (cookie-only) triggers `NoteRepository.ReEmbedAllAsync` — iterates every note in the resolved context, re-runs the embed+DELETE+INSERT in one transaction, returns `{ processed, failed }`. Bearer requests get 403. Mirrored at `/api/v1/teams/<slug>/search/reindex` for the team workspace. Used on model upgrade or the first time embeddings become available for a pre-existing personal DB.

**`GET /api/v1/search?q=…&limit=…&includePending=…`** is the CONCEPT.md hybrid search over notes: `HybridSearchService` blends sqlite-vec cosine distance with FTS5 bm25 and returns ranked hits + a `degraded: true` flag when the embedding model isn't ready yet. Response shape matches the MCP `search_memory` tool so clients can swap transports. `limit` is clamped to 1–100. Scope: `read:notes`. Team variant at `/api/v1/teams/<slug>/search`. Notes-only for now; contact-scoped search stays on `/api/v1/contacts/search` until a unified ranker exists.

**`GET /api/v1/export/db`** (cookie-only) streams the authenticated user's SQLite file as a download via SQLite's online backup API — safe under concurrent writes, no lock, no corruption. `Content-Type: application/vnd.sqlite3`, `Content-Disposition: attachment; filename="fishbowl-<userId>-<yyyyMMdd>.db"`. Bearer requests get 403 (wholesale DB export isn't an agent action). The team variant `/api/v1/teams/<slug>/export/db` is **owner-only** (`CanDeleteTeam`), not just any member: walking off with the whole team DB file is a terminal action, on par with team deletion. Implementation in `ExportApi.cs`; backup routes through a temp file and buffers into memory for the response (personal DBs stay small).

**Events** live at `/api/v1/events` (personal) and `/api/v1/teams/<slug>/events` (team). CONCEPT.md § Calendar — "A calendar that belongs to you." `Event` model carries `Title`/`Description`/`StartAt`/`EndAt`/`AllDay`/`RRule`/`Location`/`ReminderMinutes`/`ExternalId`/`ExternalSource` + ULID + created_by + timestamps; the schema is the original `events` table from user DB v1 so no migration was needed. Scopes: `read:events`, `write:events`. List takes optional `?from=…&to=…` as an ISO-8601 half-open range; the repo stores `start_at` as ISO-8601 so lexical string comparison gives the right temporal ordering. RRULE is stored verbatim — no recurrence expansion on read; the future scheduler fires expansions when firing reminders. `DeleteAsync` cascades the matching `reminders` rows (reminders FK events.id and are ephemeral). No MCP tool yet — events over MCP is a separate design call (agents scheduling on your calendar has sharper consequences than agents remembering a note). No UI wired.

**Contacts** live at `/api/v1/contacts` (personal) and `/api/v1/teams/<slug>/contacts` (team). CONCEPT.md § Contacts: "Not an address book. A living record of the people who matter." `Contact` model carries `Name`/`Email`/`Phone`/`Notes`/`Archived` + standard ULID + timestamps. Storage: user/team DB schema v5 adds `contacts` + `contacts_fts`. No secret-strip layer — there's no encrypted column on contacts, hence nothing to strip. Scopes: `read:contacts`, `write:contacts`. `ContactRepository` mirrors the `NoteRepository` pattern (FTS rowid mapped via `(SELECT rowid FROM contacts WHERE id = @Id)`; delete FTS before the row). `SearchAsync` uses the same prefix-match-AND tokenizer as `HybridSearchService` so hyphenated queries don't get parsed as FTS5 NOT operators; exposed at `/api/v1/contacts/search?q=…` and over MCP as `find_contact`. `list_contacts` MCP tool does the plain listing. No UI wired yet — backend-only until the editor shape lands.

**Team-nested API shape.** Every resource the SPA shows in a workspace has a cookie-scoped personal path **and** a team-scoped nested path, and they share the handler logic (just a different `ContextRef`): notes, tags (CRUD + color + rename), todos (CRUD), contacts (CRUD, reject readonly writes), search/reindex, export/db (owner-only). Team routes live in `TeamsApi.cs`, go through `ResolveTeamAsync` (validates membership + Bearer-context match), and reject `role == Readonly` on writes via `TeamRoleExtensions.CanWrite()`. Bearer tokens bound to a team can only hit their own team's paths; a personal-scope token on a team URL is 403 even when the human behind it is a member — the token's context, not the user's membership, is the authoritative scope.

**Dev ports.** In Development the host binds **both** `https://localhost:7180` **and** `http://localhost:7181`, and `UseHttpsRedirection` is skipped. The HTTP port is for programmatic clients (Claude Code's MCP over HTTP, `curl`) that don't want to deal with the self-signed dev cert; the HTTPS port stays the primary URL for browser + logged-in flows. A dev-only request-trace middleware logs `>> METHOD /path auth=… accept=…` and `<< … status scheme=…` on `/mcp` + `/api/*` so you can see what an MCP client is actually sending. Production: both the HTTP bind and the trace logger are skipped by `app.Environment.IsDevelopment()` gates.

**Prod ports.** Production binding is driven by what's in `system.db`. With ACME configured (`Acme:Domains`/`Acme:Email`/`Acme:AcceptTos` all set), `Program.cs` binds `0.0.0.0:80` + `0.0.0.0:443` and `UseHttpsRedirection` is active. Without ACME — fresh install or LAN-only deployment — Program.cs binds **`0.0.0.0:80` HTTP-only** so the operator can reach `/setup` on first boot, and `UseHttpsRedirection` is skipped (nothing's listening on 443 to redirect to). If `urls` is set in configuration — via `ASPNETCORE_URLS` env var or `--urls` CLI arg, both feed the same key — the fallback skips and Kestrel uses those URLs (operator escape hatch for a non-80 port; also how `PlaywrightFixture` pins its tests to a free loopback port without hitting the prod default). The "post-ACME" → "post-restart-binds-443" transition is why /api/setup returns `restartRequired: true` when ACME fields change.

**Dev tools under `tools/`.** `mint-dev-key` issues an API key directly against `ApiKeyRepository` without a browser session — supports `--scopes`, `--context user|team`, `--context-id <slug>`; token on stdout, metadata on stderr. `reindex-dev` calls `NoteRepository.ReEmbedAllAsync` against the disk (bypasses the cookie-only HTTP endpoint). `seed-dev-data` populates notes/todos/contacts in a context for quick UI + MCP iteration; sentinels (`seed:dev` tag on notes, `[seed-dev] ` prefix on contacts/todos) make it idempotent, `--force` overrides. `init-project <slug>` is the [Firepit](https://github.com/chloe-dream/firepit-ai)-integration shortcut: creates (or reuses) a team with the given slug, mints a team-scoped API key with all read/write scopes, and prints both a `cmdkey /generic:firepit/fishbowl-<slug>` line and a Firepit `mcpOverrides` snippet. All read `system.db` via the real `DatabaseFactory`, share exit codes, and `tools/` is outside the product solution. `.mcp.json` at repo root (gitignored) holds Claude Code's local MCP config pointing at `http://localhost:7181/mcp`; regenerate the token with `mint-dev-key`.

**Firepit / per-project integration.** Each Firepit project that uses Fishbowl as its memory backend maps 1:1 to a Fishbowl **team** (the file-boundary isolation, role-based membership, and team-scoped MCP routes were already in place — "team" is the internal name; "project" is what the user sees on the integration edges). `tools/init-project <slug>` is the bootstrap; the host exposes `/p/<slug>` as a 302 to `/#/team/<slug>/notes` so Firepit's quicklink template (`https://localhost:7180/p/{projectName}` per firepit-ai SPEC §"Quick-Links per Project") doesn't have to know the team-as-project naming choice. There is no `project_id` MCP tool argument and no `X-Project` header — the bearer token's `fishbowl_context_type=team` claim carries the scope. Issue #1 is resolved by this convention.

## Testing

- `WebApplicationFactory<Program>` requires `public partial class Program { }` at end of `Program.cs` — leave it.
- `TestAuthHandler` reads **`X-Test-User-Id`** and synthesizes both `NameIdentifier` and `fishbowl_user_id`. Use this for any auth'd endpoint test — never mock Google OAuth.
- Fresh temp data dir per fixture, override `DatabaseFactory` singleton, **call `SqliteConnection.ClearAllPools()` before deleting** (Windows holds the file open otherwise).
- `Testing` env suppresses `StartupBranding` — don't add console output that assumes a TTY.

## Conventions

- **Dapper snake_case via `DapperConventions.Install()`** in `DatabaseFactory` static ctor — `created_at` → `CreatedAt` works automatically. `JsonTagsHandler` handles `List<string>` ↔ JSON for `notes.tags`. Use `QueryAsync<T>`, never `QueryAsync<dynamic>` with manual mappers.
- **FTS5 + vec_notes must stay synced.** `notes_fts.rowid` maps to `notes.rowid` via `(SELECT rowid FROM notes WHERE id = @Id)`. On delete, remove from `vec_notes` AND `notes_fts` BEFORE `notes`. Tags are space-joined string (not JSON).
- **Logging via `ILogger<T>?` defaulting to `NullLogger<T>.Instance`** so tests can construct objects directly. **Never log PII** (email, name, content, secrets, tokens).
- SQLite `DateTime` stored ISO-8601 (`.ToString("o")`); booleans as `INTEGER 0/1`; IDs are ULIDs.
- Secrets use `::secret` markdown block + separate `content_secret BLOB` (client-encrypted). **Never include secret content in FTS, embeddings, or chat responses** — non-negotiable; enforced by `SecretStripper` on every MCP return path.
- **System tags** carry three flags (`is_system`, `user_assignable`, `user_removable`) — set in `Fishbowl.Core.Util.SystemTags.Seeds`. Current seeds: `review:pending` (removable by user), `source:mcp` (locked). Rename/delete of `is_system=1` rows rejected in `TagRepository`.
- Modding rule: "disk file wins, else default" — uniform across components/styles/scripts/templates/plugins. No registration manifests or whitelists.

## CI

`ci.yml` runs on push to master and PRs (Linux + Windows): `dotnet format --verify-no-changes`, `dotnet build`, `dotnet test`. `release.yml` on `v*` tag publishes per-RID `Fishbowl-{version}-{rid}.zip` artefacts (each containing an `app/` directory with the self-contained single-file binary) for `win-x64`, `linux-x64`, `osx-x64`, `osx-arm64`.

## Husky launcher

The host attaches to [Husky](https://github.com/chloe-dream/husky) when the launcher hosts it (`HuskyClient.AttachIfHostedAsync` returns `null` under `dotnet run` / debugger, so dev is unaffected). Husky-driven shutdown forwards to `app.StopAsync(ct)` so hosted services (Discord bot, embedding initializer, ACME renewal) drain cleanly before the binary swap. End users run `Husky --repo chloe-dream/the-fishbowl --asset 'Fishbowl-{version}-<rid>.zip'`; Husky reads `husky.config.json` from the repo root for `name` + `executable` + timing knobs (currently Windows-pathed; Linux/Mac users override `executable` in their local config). Husky preserves anything outside the update package, so `app/fishbowl-data/` survives upgrades.
