# Space apps — a space is a database with its own design

Status: **decided in brainstorming (2026-09-28), not yet built.** Replaces the
per-app database of `2026-05-13-apps-platform-design.md` and the private-repo
install idea. Builds on the Files backend (`2026-09-26-files-app-design.md`),
the sandboxed desktop apps (`2026-09-26-desktop-apps-design.md`) and the
admin model (`2026-09-26-admin-design.md`). Reverses one line of
`docs/slimming-plan.md`: server-side scripting comes back, as table triggers.

## Problem

People want small custom apps — a room booking for the club, an inventory, a
customer list — without a developer, a deployment pipeline or an expensive AI
plan. Lotus Notes and MS Access got this right: **data and design live
together in one database, and you change it live.** No build, no install, no
repo.

Fishbowl already has the database (a space: members, roles, keys, export,
archive), a sandbox for app code, a UI kit, hybrid search and an MCP server.
What's missing: a space that holds its own **tables**, **app code** and
**triggers**, changed live by an agent — and a way in that a non-developer
understands.

## Goal

With a normal Claude plan, someone connects Claude to a space and says "build
me a room booking for the club". Claude creates tables, triggers and an app
in that space, live; the tile appears on the space's desktop; members use it.
We cover the 95 %, not the egg-laying woolly milk-sow.

## Decisions

### Where things live

1. **A space is the database.** Tables, app code and triggers exist only in
   spaces. The **personal workspace** keeps Fishbowl's own apps and sandboxed
   third-party apps — no tables, no `.apps/`, no `design` scope there
   (requests for them are refused with a clear error).
2. **App code is files in the space, in a hidden folder:**
   `files/.apps/<app>/` (`app.json`, JS, CSS, images, trigger scripts). The
   Files API is the upload/list/hash/delete interface (the shape Neocities
   proved simple), so agents, a GitHub Action and curl all use it. Files
   export, space archive/restore and `snapshot-data` carry apps along.
   - Dot-folders are **hidden** in the Files app unless "Show hidden" is on
     (a kit file-browser feature — file upstream); never in search/palette.
   - **The server** enforces: writing, deleting, moving or copying into or out
     of `.apps/` (transfer, trash restore included) needs **Designer** or
     above (or a key with `design`); every member may read it; `.apps` itself
     can't be renamed or deleted.
3. **What an app makes for people** (images, exports) goes to the visible
   `Apps/<app>/`. Structured data goes to tables.
4. **An app is a folder with `app.json`** — it appears as a tile on the space
   desktop. No install step, no review dialog.

### Roles (space)

| Role | Read | Write data | Schema, app code, triggers | Invite | Delete / export space |
| --- | --- | --- | --- | --- | --- |
| Reader | ✓ | – | – | – | – |
| Member | ✓ | ✓ | – | – | – |
| Designer (new) | ✓ | ✓ | ✓ | – | – |
| Admin | ✓ | ✓ | ✓ | ✓ | – |
| Owner | ✓ | ✓ | ✓ | ✓ | ✓ |

A staircase: each role has everything below plus one thing. "Admin" always
means a space's admin. The instance admin is renamed **Global Admin** in the
UI; it manages accounts and settings and **never sees content** (no notes,
files, tables or other people's spaces — the privacy rule stays).

### Apps: three kinds

| Kind | Sees | Runs |
| --- | --- | --- |
| Sandboxed (third-party, installed from a URL) | its own folder `Apps/<name>`, plus what the person picks | isolated frame |
| **Spaceboxed** (the space's own apps) | **everything of its space**: notes, events, todos, contacts, files, tables, search, members | isolated frame, broad grant |
| Fishbowl's own | what the user may | in the page |

- **Spaceboxed** apps may do anything inside their space without asking —
  that is what spaces are for (a booking app puts its events in the space
  calendar). They act **with the viewer's role** (a Reader's session can't
  write through an app) and must never break out of the space; that is why
  they still run in the opaque-origin frame and the host page answers their
  calls, limited to the space.
- **Sandboxed** apps: storage stays jailed to `Apps/<name>`; the **file picker
  opens up again** to the whole workspace — the app gets only the file the
  person picks (undoes the picker jail of `d867ac6`). Plus **"Open with"**
  from the Files app for the types an app declares (`opens`).
- The current **trusted** mode (third-party code in the page, personal only)
  is an open question — spaceboxed may make it unnecessary.

### Tables

5. Tables live in `space.db`, re-homed from `app.db`. Base columns stay (id,
   title, author, created/modified, row_version, additional_data); the
   soft-delete columns go (see Trash).
6. **Types that say what is meant:** text, long text (markdown), number
   (integer/decimal), yes/no, date, date-time, choice (single/multiple, fixed
   options), member (a person of the space), file (a path in the space's
   files), **link**.
7. **Links** are foreign keys: 1:n by default, "multiple allowed" for n:m (a
   hidden join table). Targets: rows of the space's tables and the space's
   built-ins — notes, events, todos, contacts. **Nothing that is linked to
   can be deleted** (the error says what still points at it); delete in the
   right order. Cascading delete: stage 2.
8. Per column: description, required, default, unique, **searchable** (rows
   join the space's hybrid search, embedded in the same transaction by the
   in-process model). Per table: description, **"members edit only their own
   rows"** (by `author`).
9. **Simple aggregates:** count, sum, min, max grouped by one column.
10. Not built: formulas, lookups/rollups, saved views, column permissions,
    schema editor UI (Claude edits the schema).
11. **Deleting a column** (Designer and above) is final — no trash.

### Triggers

12. **Scripts from the start**, JavaScript in **Jint** inside the Fishbowl
    process: before/after × insert/update/delete per table, in
    `.apps/…` beside the app code. `beforeX(row, ctx)` may change the row or
    refuse (`throw` → the writer gets the message) — e.g. set a customer
    number, or turn a delete into `archived = true`; `afterX(row, old, ctx)`
    acts on the space. `ctx` is the same space API apps get, with the
    writer's role. Bounded: time (~1 s), statements, memory, no .NET, no file
    system, no network (for now), a trigger chain depth limit. Every write
    goes through the API, so triggers fire for apps, agents, the tables app
    and curl alike. Scheduled scripts: later, same engine.

### Trash — one for everything

13. Deleting anything — file, note, todo, event, contact, table row, whole
    table, app code — moves it to **the workspace's trash**: a JSON snapshot
    (bytes for files) with kind, title, who, when. The row is really gone
    from its table, so unique values just work. **Restore works or doesn't:**
    a unique value taken since, or a link target gone, is a clear refusal —
    otherwise PP, or a backup. Retention like files today (30 days,
    configurable). A Fishbowl app **"Trash"** per workspace shows it (tile +
    menu), not only the Files app.
14. **No versions, no draft/publish.** Changes are live for everyone.
    Backups are the host's job, or the user's agent's (the API has
    everything — e.g. push `.apps/` to a private repo).

### Contacts (a Fishbowl app, personal and space)

15. Two kinds: **person** and **organisation**. A person belongs to at most
    one organisation; the role is a text field of the person. No n:m.
16. One fixed, generous vCard-based field set: names (salutation, title,
    first, last / name, legal form), several emails, phones and addresses
    with labels, website, customer number, VAT ID, tax number, trade
    register, bank (IBAN, BIC, holder), birthday, language, industry, notes,
    tags, photo/logo. No custom fields per space — a space needing more
    builds a table that links to contacts. Bank details are an ordinary
    field (Fishbowl isn't built for large enterprises).
17. **History on the contact** from links: notes, events, todos, table rows
    pointing at it. Import/export vCard and CSV, duplicate merge.

### Messages, errors, invitations

18. **Apps may message members of their own space** (never other users):
    the server sets the origin (space · app), users can mute a space/app, a
    rate limit per app. Chat fan-out sends "New message from <space>" by
    default; sending the text is an owner setting.
19. **Error store per space:** errors from app frames, refused calls and
    trigger errors, the last few hundred, readable over MCP and the API (so
    the agent can see what broke).
20. **Invitation links** (spaces in general): one-time, expiring, with a
    role; shared over any channel. **An invitation is an invitation:** it
    activates the account even when sign-up is on approval or closed; the
    Global Admin gets an info message; the account gets the default quota.
    Admins and owners invite.

### The way in for a non-developer

21. The **API keys** page opens with "Let your agent manage your Fishbowl
    data or even build new apps — here's how", also in space settings (space
    preselected). The guide page: pick a space → pick the agent's role → pick
    the client, then exactly its steps: **claude.ai** (connector URL + OAuth
    sign-in with a consent screen — custom connectors sign in with OAuth on
    every plan), **Claude Code / Desktop** (key + config snippet), **other**
    (key + API URL), plus a first sentence to try.
22. A plain **"Tables" app** per space, like a SQLite manager, on the kit's
    `<sac-data-grid>` (in the making upstream — no Fishbowl grid): tables with
    descriptions, rows in a grid to sort, filter and edit (links show the
    target's title), the schema read-only.
23. For small agents: descriptions everywhere; progressive disclosure
    (`table_list` names + descriptions, `table_describe` one table as a short
    markdown table with sample values); a guide the server generates from the
    live space (MCP resource/prompt: tables, the space API, kit components,
    the rules above). No raw SQL ever — the JSON query DSL stays.

## The space API (apps, triggers, MCP)

One surface, three callers — spaceboxed apps (`context.*` over the kit
bridge), trigger scripts (`ctx.*`), agents (MCP tools / REST). To be detailed
in the implementation plan; the shape:

- `db` — tables: list, describe, query, get, insert, update, delete, count,
  aggregate, search; schema: create/alter/drop table, create index (Designer).
- `notes`, `events`, `todos`, `contacts` — the space's built-ins, CRUD + search.
- `files` — the space's files (the Files API).
- `search` — the space's hybrid search (notes + searchable rows).
- `members` — who is in the space (for member fields and messages).
- `notify(members, text)` — messages (decision 18).
- `errors` — the error store (read; apps write theirs via the bridge).

`context.db` & co. are proposed to the kit as generic capabilities (like
`context.fs`), so other hosts can offer them.

## What goes

- The **per-app database** (`app.db`, app keys, `ownerType` routes, the
  `app:*` scopes) — its table code moves to spaces.
- **Soft-delete columns** and `app_restore` — replaced by the trash.
- The **private-GitHub-repo install** (token in the install dialog, not
  committed). Its hosted-code serving becomes serving from `.apps/`.
- Public sandboxed apps installed from a URL stay.

## Phases

1. **Roles & invitations** — Designer role, staircase checks, Global Admin
   naming, invitation links. *Built 2026-09-28.*
2. **Trash for everything** — the trash app, snapshots for notes/todos/events/
   contacts, restore rules. *Built 2026-09-29* (table rows and app code join
   the same `trash` in phases 3 and 4).
3. **Tables in spaces** — re-home, types, links as foreign keys, aggregates,
   own-rows, searchable columns, descriptions, `table_*` MCP tools, the
   Tables app. *3a built 2026-09-29* (tables, types, links, own rows, trash,
   aggregates, REST, MCP; table names get a hidden `t_` prefix — open
   question settled). Still open: searchable columns, the Tables app (on the
   kit's `sac-data-grid`). The old per-app DB is removed (2026-09-30).
4. **Space apps** — `.apps/` protection + hidden folders, tiles from
   `app.json`, frame serving from `.apps/`, the space API over the bridge
   (`context.*`, kit issue), messages from apps, the error store, the
   server's guide.
5. **Triggers** — Jint, before/after scripts, limits.
6. **Contacts** — persons/organisations, the field set, history, vCard/CSV.
7. **The way in** — guide page, OAuth for MCP (claude.ai), GitHub Action
   `deploy-to-fishbowl`.
8. **Sandboxed apps** — reopen the picker, "Open with".

## Open questions

- Does the **trusted** mode for third-party apps stay?
- Table names: free, or a hidden internal prefix (`tbl_`) so they never
  collide with Fishbowl's tables?
- Contacts: one table per workspace for both kinds, or two?
