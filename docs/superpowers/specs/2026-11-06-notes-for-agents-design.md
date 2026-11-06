# Notes as documents for agents

A space can hold a project's documents as tagged notes: plans, reports,
research, decisions, features. Its agents reach it only over MCP, with a space
key the owner issued, and keep a Git mirror of the notes that the orchestrating
agent refreshes before each session. The owner reads and approves in the Notes
app; `review:pending` on every agent write is her "new for me" and stays as it
is. No custom space code (triggers, tiles, pages) is needed for any of this.

These are the gaps such a space ran into (2026-11-06), all built the same day.
CLAUDE.md ("Notes as documents for agents", "MCP surface") has the reference.

## 1. `note_list`

MCP `note_list` (`read:notes`) lists every note of the workspace in change
order (`updated_at`, then id), from `INoteRepository.ListChangesAsync`.

- Arguments:
  - `tags` + `match` (`any` | `all`);
  - `updated_since`;
  - `limit` (1–500) + `cursor`, a keyset that a write between pages can't shift.
- Light by default: id, slug, title, tags, times (UTC), pinned, archived,
  locked. `include_content` adds the secret-stripped markdown.
- Deletions:
  - With `updated_since`, the first page lists `deleted`: the notes trashed
    since.
  - A note restored from the trash gets a new `updated_at`.
  - A full listing (no `updated_since`) is the complete set. Only that
    catches a delete that the trash lost before the mirror looked.

## 2–3. Todos over MCP, with tags

- Tools: `todo_list`, `todo_create`, `todo_update` (done / reopen, tags
  replaced or added/removed in place) and `todo_delete` (to the trash), on
  `read:tasks` / `write:tasks`.
- An agent's todo carries `source:mcp`.
- `todos.tags` (v31) follows the notes' rules. Renaming or deleting a tag
  rewrites todos too, and the tag counts them.
- In the Todos app: a tag line on each row, a chip input in the editor, and a
  filter strip.

## 4. Tags in place

`update_memory` and `todo_update` take `add_tags` / `remove_tags`, applied in
the write's transaction (`PatchAsync`). REST `PATCH …/notes/{id}` does the
same, for the board.

## 5. Tags over MCP

- `tag_list`: colour, description, system flag, and where each tag is used.
- `tag_update`: colour (makes a new tag), description, `rename_to`.
- Both on `read:tags` / `write:tags`.
- `tags.description` (v31) is also shown and edited in the tag manager.

## 6. Slug

- `notes.slug` (v31) is unique in any case: `[A-Za-z0-9._-]` segments joined
  by `/`, up to 200 characters.
- Set by `remember` / `update_memory` / REST. An update without a slug keeps
  it; `""` clears it.
- Found by `get_memory { slug }` and `update_memory { by_slug }`.

## 7. Links between notes

- The vendored editor renders none of these links and has no extension point
  for inline elements.
- So the Notes app resolves them itself (`fb.noteLinks`): `[[Title]]`,
  `[[Title|text]]`, `note:<id>`, and relative `.md` paths by slug.
- It shows "Links" and "Linked from" under the text. Ctrl/⌘+click in the text
  opens a link.
- An inline-link hook in `sac-md-editor` would let the text itself show them
  as links. That belongs upstream.

## 8. OR / NOT and saved filters

- Tag expressions (`fb.tagExpr`): AND / OR / NOT, a leading `-`, brackets,
  `ns:*`.
- Saved per workspace in `note_filters` (v31), so in a space they are
  everyone's. They are picked from the list head's funnel; the one in use is
  kept per browser.

## 9. Note history

- Every agent write keeps the note as it was (`note_versions`, the newest 50 a
  note).
- The Notes app's "N agent changes" opens a window showing each change's
  title, tags and a line diff, with Restore.

## 10. Locked notes

- A locked note's title and text don't change (409 `note_locked`). An append
  and tags still do.
- Any writer, or an agent, may lock a note.
- Only a person unlocks: the personal workspace's owner, or a space Admin+.

## 11. A board over notes

- A view of the Notes app: one column per value of a tag namespace, plus
  "No <ns>".
- Cards move by drag, by Alt+arrow or by "Move to …". Each move swaps the tag
  and keeps `review:pending`.
- The kit has no board, and its sortable doesn't cross lists. A board
  component would belong there.

## Left as they are

- Agent orchestration stays with the agents' host.
- The mirror and its Git repository belong to the space's project.
- Saved filters are not desktop tiles yet.
