# MCP Manual Test Plan

Hand-execute after shipping Phase 5. Run top-to-bottom; each section stands alone so you can rerun a single area after a targeted fix. Every check has:

- **Action** — what to do.
- **Expected** — what should happen.
- **If not** — what to flag so we can fix.

---

## 0. Prerequisites

- Fishbowl host running on `https://localhost:7180` (`dotnet run --project src/Fishbowl.Host`).
- MiniLM-L6-v2 model present under `{dataRoot}/models/MiniLmL6V2/` (first run downloads it; subsequent runs log `MiniLM-L6-v2 already present`).
- An API key minted with `read:notes` + `write:notes` for the personal context (see `tools/mint-dev-key` or UI → Settings → API Keys).
- Claude Code configured with Fishbowl in its MCP config (`.mcp.json` at repo root is the easiest path).
- Browser window logged into Fishbowl for side-by-side verification.

---

## 1. MCP surface — every tool reachable

**1.1 `tools/list` returns every registered tool.**
- Action: in Claude Code, ask "what fishbowl tools do you have?"
- Expected: `search_memory`, `remember`, `get_memory`, `update_memory`, `list_pending`, `list_contacts`, `find_contact`. `OpenApiTests.OpenApi_IncludesContactsEndpoint_Test` covers the HTTP side; `Mcp_ToolsList_ReturnsAllRegisteredTools` covers this MCP list.
- If not: `ToolRegistry` DI binding; check `Program.cs` scoped registration order.

**1.2 `initialize` succeeds.**
- Action: Claude Code should transparently handshake. If it refuses to use the server, check Claude Code's MCP log.
- Expected: `protocolVersion: 2025-03-26`, `serverInfo.name: fishbowl`.

**1.3 Unauthenticated `/mcp` → 401.**
- Action: `curl -k -X POST https://localhost:7180/mcp -H "Content-Type: application/json" -d '{"jsonrpc":"2.0","id":1,"method":"initialize"}'` (no `Authorization`).
- Expected: HTTP 401.

---

## 2. Happy path — remember, search, get, update

**2.1 `remember` creates a note.**
- Action: "remember: the DatabaseFactory uses file-level boundaries per user".
- Expected: tool returns `{ id: <ulid>, note: { … } }`.
- Verify in UI: the note appears in the notes view. Tags include `source:mcp` and `review:pending`.
- If not: `RememberTool` wiring, `NoteRepository.ApplySourceTags` with `NoteSource.Mcp`.

**2.2 `search_memory` finds the note by keyword.**
- Action: "search my memory for DatabaseFactory".
- Expected: the note from 2.1 appears at or near the top; `degraded: false`.

**2.3 `search_memory` finds a note semantically.**
- Action: `remember` a note titled "Lazy migration pattern" with a body about on-demand schema upgrades. Then search for "how do migrations work".
- Expected: the "Lazy migration pattern" note ranks in the top few hits despite zero keyword overlap.
- If not: embeddings aren't landing in `vec_notes` (check `EmbeddingService` logs) or `HybridSearchService` merge logic regressed.

**2.4 `get_memory` returns full content.**
- Action: "get the memory with id <id from 2.1>".
- Expected: full note including title, content, tags.

**2.5 `update_memory` only changes fields passed.**
- Action: "update memory <id> — change the title to 'DatabaseFactory scopes'".
- Expected: title changes, content intact, tags re-add `review:pending` (MCP update is still MCP-origin).
- Verify in UI: title updated, `review:pending` present.

---

## 3. Secret-strip invariant (non-negotiable)

**3.1 Secret in content never leaves via MCP.**
- Action: via UI, create a note with:
  ```
  public preamble
  :::secret
  supersecret-xyz-token
  :::end
  public tail
  ```
- Via Claude Code: "search my memory for preamble".
- Expected: the note surfaces. Response text contains `[secret content hidden]` and `public preamble` + `public tail`. **Never** contains `supersecret-xyz-token`.
- If not: immediate regression — this is the non-negotiable invariant.

**3.2 `get_memory` strips secrets.**
- Action: "get memory <id of 3.1 note>".
- Expected: same shape — content has `[secret content hidden]`, no raw secret marker anywhere in response.

**3.3 Automated invariant tests still pass.**
- Action: `dotnet test --filter "FullyQualifiedName~SecretStripInvariantTests"`.
- Expected: green.

---

## 4. Review workflow

**4.1 `list_pending` returns only review:pending notes.**
- Action: Claude Code: "list my pending memories".
- Expected: every note from the MCP session so far appears; your own UI-created notes without `review:pending` are absent.

**4.2 Approval strips `review:pending`.**
- Action: in UI, hit the approve (green check) action on a pending note.
- Expected: note stays; `review:pending` tag removed; `source:mcp` remains (provenance is locked).
- Re-run `list_pending` via MCP: approved note is gone from the list.

**4.3 Editing a pending note auto-approves it (approval-by-editing).**
- Action: remember via MCP → note has `review:pending`. In UI, edit title/content and save.
- Expected: `review:pending` stripped on save. `source:mcp` stays.
- If not: `NoteRepository.ApplySourceTags(Human)` isn't running on cookie writes.

**4.4 System tag immutability.**
- Action: in UI, try to rename `source:mcp` tag.
- Expected: 400 error, UI shows error toast.
- Action: try to delete `source:mcp`.
- Expected: 400 error.

---

## 5. Scope enforcement

**5.1 Read-only Bearer cannot write.**
- Action: mint a key with only `read:notes`. Claude Code config using that token.
- Verify: `search_memory` works, `remember` returns a JSON-RPC error (code `-32603`).
- If not: `IMcpTool.RequiredScope` gating in `McpEndpoint` dispatcher.

**5.2 Bearer context-bound to personal cannot read team notes.**
- Action: create a team in the UI. Personal-scoped Bearer → `GET /api/v1/notes` via curl.
- Expected: returns personal notes only. No access to team's `/api/v1/teams/{slug}/notes` (403).

**5.3 Revoked key denied.**
- Action: revoke the Claude Code key in UI → Settings → Keys. Claude Code tries any tool.
- Expected: 401. Mint a fresh one, put back in `.mcp.json`, restart Claude Code.

---

## 6. Degraded mode (model unavailable)

**6.1 Missing model → FTS-only results, service still works.**
- Action: stop host, delete `{dataRoot}/models/MiniLmL6V2/`, restart.
- During the re-download window, call `search_memory`.
- Expected: response has `degraded: true`, hits come from keyword match only.
- If not: `EmbeddingService.EmbedAsync` isn't throwing `EmbeddingUnavailableException`, or `HybridSearchService` isn't catching it.

**6.2 Once model lands, non-degraded ranking resumes.**
- Action: wait for download to complete (`Verified` log line), repeat 2.3.
- Expected: `degraded: false`, semantic hits reappear.

---

## 7. Re-index

**7.1 `POST /api/v1/search/reindex` (cookie) returns counts.**
- Action: browser (logged-in session cookie), devtools → Network, POST to `/api/v1/search/reindex`.
- Expected: 200, body `{ processed: N, failed: 0 }` where N = note count in your personal DB.
- Verify in DB: `sqlite3 .../users/<id>.db "SELECT COUNT(*) FROM vec_notes"` (if sqlite3 available) matches N.

**7.2 Re-index via Bearer → 403.**
- Action: `curl -H "Authorization: Bearer fb_live_..." -X POST https://localhost:7180/api/v1/search/reindex`.
- Expected: 403. Maintenance endpoints are cookie-only.

---

## 8. Context isolation (for when teams are in play)

**8.1 Personal Bearer on team URL → 403.**
- Only runs once you've created a team. Personal-scoped key trying `GET /api/v1/teams/{slug}/notes` must be rejected even if you're a member.
- Expected: 403. The token's context, not the user's membership, is authoritative.

---

## 8a. Contacts (list + find)

**8a.1 `list_contacts` returns the current context's address book.**
- Action: Claude Code: "list my fishbowl contacts".
- Expected: all non-archived contacts for the active context; each entry has `name`, `email`, `phone`, `notes`, `archived: false`, `updatedAt`. Archived rows default-hidden — pass `include_archived: true` to see them.
- If not: `IContactRepository` DI wiring, or `ListContactsTool` registration in `Program.cs`.

**8a.2 `find_contact` full-text search matches across fields.**
- Action: seed (`dotnet run --project tools/seed-dev-data`) then Claude Code: "find the contact called Venue Engineer" / "find the catering person".
- Expected: the seeded contact with matching `name`/`email`/`phone`/`notes` ranks top. Hyphenated queries work (tokenizer strips `-` before FTS sees it).
- If not: `ContactRepository.SearchAsync` tokenizer, or the FTS5 virtual table is out of sync with `contacts` (check migration v5).

**8a.3 `read:contacts` scope gating.**
- Action: mint a key without `read:contacts`. Claude Code's `find_contact` call.
- Expected: JSON-RPC error envelope (code from the dispatcher's scope-check path). Browser-side cookie path keeps working in parallel — scopes only apply to Bearer.

---

## 9. UI regressions from Phase 6

**9.1 "Needs review" filter chip.**
- Action: UI notes view → click the chip.
- Expected: only `review:pending` notes visible.

**9.2 Approve button per pending note.**
- Expected: green check icon, click → note disappears from pending filter, still in all-notes with `source:mcp` intact.

**9.3 Refresh button in toolbar.**
- Expected: clickable, re-fetches notes list.

---

## Reporting shape

For each issue, grab:
1. Section + step (e.g. "2.3").
2. What Claude Code said / what the UI showed.
3. Relevant log snippet (`src/Fishbowl.Host/bin/.../fishbowl.log` or stdout).
4. Whether it's a blocker, tuning, or "nice-to-fix".

File an ad-hoc list — I'll fold them into commits as we go.
