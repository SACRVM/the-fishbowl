# Files app — design

Status: **decided** · 2026-09-26 (decisions at the end; no open questions)

## Problem

Fishbowl is meant to grow into a desktop: notes, calendar, todos today, and
apps with their own tables and their own files tomorrow. There is no place for
*files*. A photo of a receipt, a PDF contract, a voice memo, the MP3 a friend
sent — none of it can live in Fishbowl, so it lives in iCloud, Google Drive or a
Downloads folder, and the promise "your memory, on your hardware" stops at the
first binary.

Three things are missing, in order of dependency:

1. **A file store in the spine.** Real files in a real directory tree inside
   the context folder, streamed in and out, quota-bound, safe to serve, with a
   change journal a sync client can follow.
2. **A face for it.** A two-pane Files view that is pleasant to organise with:
   upload, move, copy, rename, delete, download — keyboard-first, with a
   preview of what a file *is* before opening it.
3. **A capability other apps build on.** The kit already defines the seam —
   `sac.files` ("Open…" / "Save as…" on the *user's* files, wherever the host
   keeps them) and `<sac-file-browser>` over any store. Fishbowl should be the
   host that answers it with a real server, so every future app gets a shared
   file store for free.

The look borrows the spirit of Norton Commander (and of retro-crt's
`apps/commander` demo): two panes, a function-key bar, a highlight bar you drive
with the arrow keys, marks for batch operations, F3 to look inside, Alt+F1/F2
to change a pane's "drive". In spirit only — kit tokens, kit components, no raw
colours, no ASCII-art cosplay.

This is an **addition**, which `docs/slimming-plan.md` treats with suspicion.
It is a product decision the user has taken (2026-09-26); it should be recorded
there as an accepted addition so the next sweep doesn't read it as drift.

## Goals

1. Upload (drag-and-drop, picker, phone camera roll), organise in folders,
   move, copy, rename, delete to trash, restore, download — in personal and
   space workspaces, and **across** them (each pane its own workspace).
2. **The folder on disk is the truth.** Files keep their real names in a real
   tree under the context folder; files placed or edited there outside
   Fishbowl show up in the UI and in the change feed.
3. **"We follow the host."** Name rules, case sensitivity and path limits are
   the server OS's, reported to clients, enforced with a clear 400 per rule.
4. Preview for what the browser can show safely: raster images (zoom/pan),
   plain text and code, Markdown (rendered, sanitised), audio and video
   (streamed, seekable). PDFs open in a new browser tab. Everything else gets
   an info card and a download button.
5. Bytes never pass through memory whole: streaming upload into a temp file
   with hashing on the fly, streaming download with HTTP Range.
6. The folder-per-context invariant holds: dropping `users/<id>/` or
   `spaces/<id>/` onto another instance moves the files with everything else.
7. Serving user files can never run code in Fishbowl's origin.
8. **Bearer access and a change feed from the first release** (`read:files` /
   `write:files`), as the foundation for a future Fishbowl file-sync client.
9. Keyboard-first on desktop, fully usable on a phone.
10. A store adapter that plugs into the kit's seams (`sac-file-browser`
    `store`, `sac.files.virtual({ store })`), so the Files view and every
    future app's Open/Save dialog are the same code path.

## Non-goals

- **WebDAV, SMB, mounts — never.** Desktop integration comes from a dedicated
  Fishbowl sync client built on the change feed (a separate, later project;
  this spec only designs the server side it needs).
- Public share links. Nothing in this design is reachable without auth.
- Thumbnails, a gallery or thumbnail grid, server-side image processing.
- Versioning / file history. Replace overwrites.
- Deduplication. Copy is a real copy.
- Encryption at rest, or vault-encrypted files. Files are **not secret
  storage** (see "Secrets and PII").
- Indexing file contents into FTS or embeddings; OCR (CONCEPT.md § Documents &
  Attachments stays the long-term direction, not this spec).
- Note/contact/event attachments (built on this store later; not designed
  here).
- Real-time collaboration, locking, co-editing.

## Disk layout

```
fishbowl-data/
  users/<userId>/
    personal.db                 ← gains file_index, file_changes, file_trash, file_state (user-schema v9)
    files/                      ← the user's tree, real names, real folders
      Photos/2026/IMG_0412.jpg
      Documents/lease.pdf
      Photos/.~fb-<ulid>.part   ← in-flight upload, hidden, in the target folder
      .trash/<trashId>/lease.pdf ← one folder per trashed item
    apps/<appId>/app.db         ← (existing)
  spaces/<spaceId>/
    space.db                    ← same v9 migration
    files/…                     ← same shape
  archive/spaces/<spaceId>-<yyyyMMddHHmmss>.zip   ← archived spaces (see Space delete)
```

- `files/` is created lazily on first write. It sits under the context's
  ULID/user-id folder like the DB, so a space rename moves nothing and a cold
  import moves the files with the DB, journal included.
- **Reserved names**, invisible in every listing, snapshot and feed, and
  refused as user names: `.trash` at the root of `files/`, and any name
  starting `.~fb-` anywhere. On Windows temp files additionally get
  `FileAttributes.Hidden`. Reserved names are compared with the host's case
  rule, so `.TRASH` is reserved on Windows.
- `DatabaseFactory` gets one helper, `ResolveContextFolder(ctx)` → `users/<id>/`
  or `spaces/<id>/`. **Only `DiskFileStore` touches `files/`.** (CLAUDE.md's
  "`IResourceProvider` only, never direct file I/O" rule is about UI
  resources; user data goes through this one class.)
- Code shape: `IFileStore` (Core) / `Fishbowl.Data.Files.DiskFileStore` owns
  bytes and paths; `IFileRepository` (Core) / `FileRepository` (Data,
  `AddScoped`) owns the v9 tables; `FileService` (Data) orchestrates the two
  under a per-context lock and is what `FilesApi` calls.

## Path safety and names

Every request addresses a file by a **relative, `/`-separated path** from the
context's `files/` root. No ids, no disk paths. `FilePathResolver` turns it
into a disk path or a 400; nothing else combines paths.

### Resolution

1. Reject before touching the file system: empty or whitespace-only path
   segments, `.` and `..` segments, a leading `/` or `\`, anything
   `Path.IsPathRooted` accepts, drive letters (`C:`), UNC and device prefixes
   (`\\server`, `\\?\`, `\\.\`), `:` anywhere on Windows (drive syntax and
   alternate data streams `name:stream`), and a reserved name as any segment.
   Percent-decoding happens exactly once, in ASP.NET's query binding, so
   `%2e%2e` arrives as `..` and is refused.
2. Validate every segment with the name rules below.
3. `Path.GetFullPath(Path.Combine(root, rel))` and require the result to start
   with `root + separator` under the host's comparison. `root` is resolved
   once at startup to its final target, so an operator may symlink
   `fishbowl-data` or a context folder elsewhere — only links *below* `files/`
   are refused.
4. **Links are never followed.** Walk the resolved path from `root` segment by
   segment; any existing component with `FileAttributes.ReparsePoint`
   (symlink, junction, mount point, and on Windows any other reparse tag) is
   refused with 400 `link_not_followed` — whether it points inside or outside
   the root. Listings show links as `kind: "link"` entries that cannot be
   opened, so the user sees why. Enumeration uses
   `EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0 }`
   and never recurses into a reparse point.
5. **Trust boundary, stated:** a symlink swapped in between check and open, or
   a hard link to a file outside the root, needs local write access to the
   data directory. That party is already the operator; Fishbowl defends
   against remote callers, not against its own host account.

### Name rules — the host's

Detected at startup per data volume, not assumed per OS: **case sensitivity**
by probing (create `.~fb-probe-a`, test for `.~FB-PROBE-A`), which gets
case-sensitive NTFS folders, casefolded ext4 and default-insensitive APFS
right. The rule set is reported by `GET /files/capabilities` so API clients
can check names locally.

| Rule (`rule` code) | Windows | Linux | macOS |
|---|---|---|---|
| `empty` / `dot_name` — empty, `.`, `..` | all | all | all |
| `control_char` — NUL, C0, C1 | all | all | all |
| `bidi_override` — U+202A–202E, U+2066–2069 ("invoice‮fdp.exe" shows as "invoiceexe.pdf") | all | all | all |
| `separator` — `/` or `\` | all | all | all |
| `reserved_fishbowl` — `.trash` at root, `.~fb-*` | all | all | all |
| `forbidden_char` — `< > : " \| ? *` | yes | — | `:` |
| `reserved_device` — `CON PRN AUX NUL COM0-9 LPT0-9` incl. `¹²³` digits, `CONIN$ CONOUT$`, with or without an extension (`con.txt`) | yes | — | — |
| `trailing_dot_space` — Win32 silently strips them, so `a.` aliases `a` | yes | — | — |
| `short_name_alias` — `~<digit>` in the 8.3 shape (`TRASH~1`), which Windows resolves to another entry | yes | — | — |
| `segment_too_long` | 255 UTF-16 units | 255 UTF-8 bytes | 255 UTF-16 units |
| `path_too_long` (absolute) | 259, or 32,767 when `LongPathsEnabled` is set | 4,095 bytes | 1,023 bytes |
| `name_exists` — case-insensitively on an insensitive volume | per probe | per probe | per probe |

`\` is refused on Linux too, although the file system allows it: it is the
separator of every Windows client, including a sync client, and no Linux user
loses anything they rely on. The table is the only place Fishbowl adds to the
host's rules.

Every refusal is a 400 `{ error: "invalid_name", rule, segment, message }`
with a human sentence the UI shows verbatim — `"CON" is a reserved name on
this server (Windows).` The `segment` is echoed to the caller only, never
logged.

**Unicode:** names are NFC-normalised when Fishbowl *creates* them
(upload, mkdir, rename target). Existing on-disk names are addressed exactly as
listed, so an NFD name created out-of-band on Linux stays reachable.

**Case-only rename** (`report.pdf` → `Report.pdf`) on an insensitive volume
goes through a hidden temp name, so it works regardless of `File.Move`'s
behaviour.

**Downloads** send RFC 6266/5987 `filename*=UTF-8''…`
(`Results.File(fileDownloadName:)`).

## What lives in the DB — user/space schema v9

The file system holds names, the tree, bytes, sizes and mtimes. The DB holds
only what the file system cannot:

```sql
-- Reconcile baseline + metadata cache. Derived; rebuildable from disk.
CREATE TABLE file_index (
    path_key    TEXT PRIMARY KEY,   -- path, case-folded on an insensitive volume
    path        TEXT NOT NULL,      -- '/'-separated, relative to files/, as on disk
    parent_key  TEXT NOT NULL,      -- for per-folder reconcile
    kind        TEXT NOT NULL CHECK (kind IN ('file','folder','link')),
    size        INTEGER,
    mtime       TEXT,               -- UTC, full precision
    sha256      TEXT,               -- cached; valid only while size+mtime match
    mime        TEXT,               -- sniffed effective type; same validity
    created_by  TEXT                -- who wrote it through Fishbowl (spaces); NULL = on disk
);
CREATE INDEX ix_file_index_parent ON file_index (parent_key);

-- The change journal. seq is the cursor.
CREATE TABLE file_changes (
    seq         INTEGER PRIMARY KEY AUTOINCREMENT,
    op          TEXT NOT NULL CHECK (op IN ('create','modify','move','delete')),
    kind        TEXT NOT NULL CHECK (kind IN ('file','folder')),
    path        TEXT NOT NULL,      -- new path for move
    old_path    TEXT,               -- move only (rename = move)
    size        INTEGER,
    mtime       TEXT,
    etag        TEXT,
    sha256      TEXT,               -- when cached
    trashed     INTEGER NOT NULL DEFAULT 0,  -- delete by trash vs purge
    source      TEXT NOT NULL CHECK (source IN ('api','disk')),
    actor       TEXT,               -- user id; NULL for disk
    at          TEXT NOT NULL
);

-- Trash bookkeeping. The bytes sit in files/.trash/<id>/<name>.
CREATE TABLE file_trash (
    id            TEXT PRIMARY KEY,  -- ULID = folder name under .trash/
    original_path TEXT NOT NULL,
    kind          TEXT NOT NULL,
    size          INTEGER NOT NULL,  -- folder: total bytes
    deleted_at    TEXT NOT NULL,
    deleted_by    TEXT
);

-- One row. Journal identity and floor; volume facts the index was built with.
CREATE TABLE file_state (
    id              INTEGER PRIMARY KEY CHECK (id = 1),
    epoch           TEXT NOT NULL,   -- ULID; rotated whenever seq history breaks
    floor_seq       INTEGER NOT NULL DEFAULT 0,  -- compacted through
    case_sensitive  INTEGER NOT NULL,
    last_scan_at    TEXT
);
```

- **Disk wins.** When `file_index` and disk disagree, disk is right and the
  difference becomes journal entries with `source = 'disk'`. Losing
  `file_index` costs a rescan and a new epoch, nothing else.
- **Cross-host cold import:** at first open `file_state.case_sensitive` is
  compared with the probe; on mismatch the index is rebuilt (new
  `path_key`s) and the epoch rotates.
- Sniffed `mime` and `sha256` are cache keyed by `size + mtime`: a miss
  re-sniffs 4 KB (cheap) and leaves `sha256` NULL until something needs it.
- Standard `ApplyV9` in `DatabaseFactory`, `PRAGMA user_version = 9`, same
  migration for user and space DBs.

## Change journal and reconcile

### Writing the journal

`FileService` serialises all mutations and scans of one context through a
per-context `SemaphoreSlim` (cross-workspace operations take both locks in a
fixed order — by context key — so two transfers can't deadlock). Each
mutation is: disk operation → then, in one `WithContextTransactionAsync`,
update `file_index` and append `file_changes`. A crash between the two leaves
disk ahead of the DB; the next reconcile records the difference as
`source = 'disk'`. **Reconcile is the repair path**, so no two-phase commit.

Entry granularity:

- Rename / move within a context: **one** `move` entry, even for a folder
  (the client applies it to the subtree).
- Trash / purge: one `delete` for the item (`trashed` says which).
- Folder copy, restore, and anything arriving from another workspace: a
  `create` for the folder **and each descendant** (≤ the 10,000-node copy cap),
  since the client has no local copy to move.
- Out-of-band renames can't be told from delete + create; the feed says so
  honestly (a sync client re-downloads).

### Reconcile — scan, not FileSystemWatcher

Files placed or edited on disk outside Fishbowl are picked up by **scanning**:

1. **On list:** listing reads the directory anyway; it diffs the entries
   against `file_index` rows with that `parent_key` and journals the
   differences before answering. The folder you are looking at is always
   current.
2. **Before a feed or snapshot request:** a full-tree scan if `last_scan_at`
   is older than `Files:ReconcileIntervalSeconds` (default 60). Folders are
   enumerated, every file stat'ed (size + mtime); nothing is hashed. A folder
   that vanished yields one `delete` and drops its descendants' index rows.
3. **Daily**, on the scheduler tick, for every context with a `files/`
   folder — so the journal doesn't wait for a client to ask.

Why not `FileSystemWatcher`: it drops events on buffer overflow, is recursive
only by emulation on Linux and eats inotify watches per folder (per context,
across all users), and doesn't work on many network mounts. A scan is needed
as the backstop anyway, and then the watcher only buys latency. It may come
later as a *hint* that triggers a scan, never as the source of truth. Cost:
out-of-band changes reach the feed within the reconcile interval.

### Cursor semantics

- A cursor is opaque to clients; the server encodes `<epoch>:<seq>`. `seq` is
  `AUTOINCREMENT`, so it is monotonic even across compaction.
- `GET /files/changes?cursor=…&limit=…` returns entries with `seq > cursor`
  in order, the new cursor, and `more`. Without a cursor it returns the head
  cursor and no changes.
- **410 Gone** `{ error: "cursor_expired" }` when the epoch differs, when
  `seq < floor_seq`, or when `seq` is *ahead* of the head (a DB restored from
  an older backup). The client then takes a snapshot. Restoring a space from
  an archive and rebuilding `file_index` rotate the epoch.
- **Compaction** on the daily tick: delete entries older than
  `Files:JournalRetentionDays` (default 90) and raise `floor_seq`.
- **Snapshot:** `GET /files/snapshot` reconciles, records the head cursor,
  then streams NDJSON — one line per entry (`path, kind, size, mtime, etag,
  sha256?`) and a final `{ cursor }`. Changes during the walk appear again in
  the feed after that cursor; entries are state descriptions (path + etag),
  so applying one twice is harmless.

This is the whole server side of a future Fishbowl sync client: snapshot once,
follow `changes`, upload with conditional `PUT`, and on 410 snapshot again.
A long-poll `?wait=` on `changes` is a later addition that doesn't change the
shape.

## Trash

Like an OS file explorer:

- **Delete** (Delete / F8) moves the item to trash — a rename into
  `files/.trash/<ulid>/<name>` (same volume, atomic, cheap) plus a
  `file_trash` row. No confirm; a `<sac-toast>` offers Undo.
- **Delete permanently** (Shift+Delete / Shift+F8) removes it after a kit
  confirm. Cookie-only (see API).
- **Restore** moves it back to `original_path`, recreating missing parent
  folders (as Windows does). If the name is taken: `onConflict=ask` (the UI's
  default — Replace / Keep both / Skip) or `rename` → `report (restored).pdf`,
  `(restored 2)` …
- **Auto-purge** on the daily tick after `Files:TrashRetentionDays` (default
  30; 0 = never).
- Trash counts towards the quota until purged.
- Drift is tolerated: a `.trash/<id>/` folder without a row (a partial cold
  import) is listed as "origin unknown", restorable to the root; a row without
  its folder is dropped by the sweeper.

## Limits and quota — configuration

All limits are `system_config` keys, set with `tools/set-config`, read through
`ISystemRepository` with a short cache, applied without a restart. The values
below are **defaults**:

| Key | Default | Where it bites |
|---|---|---|
| `Files:MaxFileBytes` | 2 GiB | upload/copy; checked against `Content-Length` up front and against the byte count while streaming |
| `Files:QuotaBytes` | 20 GiB per context (0 = none) | upload/copy/restore/transfer; 413 `{ field: "quota" }` |
| `Files:InstanceCapBytes` | 0 = off | sum of all contexts' usage + archives |
| `Files:MinFreeBytes` | 1 GiB | refuse a write when free space < this + write size; 507 |
| `Files:TrashRetentionDays` | 30 (0 = never) | daily purge |
| `Files:JournalRetentionDays` | 90 | daily compaction |
| `Files:ReconcileIntervalSeconds` | 60 | feed/snapshot scan throttle |
| `Export:CombinedMaxBytes` | 4 GiB | "DB + files in one ZIP" offered only below this |
| `Archive:RetentionDays` | 30 (0 = keep forever) | archived-space purge |

Fixed in code (`FileLimits`, Core): max depth 32, listing page 500
(cursor-paged), copy/transfer ≤ 10,000 nodes.

**Usage** is the byte total of `files/` including trash, walked once per
context on first need, cached in memory, adjusted by every Fishbowl write and
recomputed by each full reconcile. It is a **soft** limit against out-of-band
writes: an operator can copy past it on disk; Fishbowl then refuses further
uploads.

Kestrel's default 30 MB body limit stays for every other route; the upload
handler raises it for itself only, via `IHttpMaxRequestBodySizeFeature`, to
`Files:MaxFileBytes`. Size and quota violations throw
`ResourceValidationException` (`SizeLimit` → 413 via `ValidationResults.From`),
invalid names use `Invalid` → 400. Behind Caddy in prod: Caddy imposes no body
limit by default; the deployment note should say so.

## Streaming and conditional writes

- **Upload is a raw request body, not multipart:**
  `PUT …/files/content?path=a/b.jpg` with `Content-Type:
  application/octet-stream` and a required `X-Fishbowl-Upload: 1` header. The
  custom header forces a CORS preflight, so a cross-site form cannot post a
  file even if the cookie's `SameSite=Lax` default ever changes. The body is
  copied in 80 KB chunks to `a/.~fb-<ulid>.part` **in the target folder**
  through `IncrementalHash(SHA256)`, the first 4 KB kept for sniffing. Then,
  under the context lock, the precondition is re-checked and the temp file is
  renamed into place (`File.Move(overwrite: true)`), so readers see the old
  file or the new one, never half of it. Nothing is buffered in memory. Parent
  folders must exist unless `?parents=1`.
- **Conditions are required on `PUT`:** `If-None-Match: *` = create only
  (412 if it exists), `If-Match: "<etag>"` = replace only that version (412 if
  it changed). Neither → **428 Precondition Required**. The UI's
  Replace / Keep both / Skip dialog is built on exactly this: try create, on
  412 ask, then replace with the current ETag or retry create under the next
  free name. One mechanism for the UI and the sync client.
- **ETag = `"<size>-<mtime ticks UTC>"`.** Available for every file,
  including out-of-band ones, without hashing. `sha256` travels alongside
  (listing, feed, snapshot) when cached, for content comparison. Weakness
  under "Sharp edges".
- **Progress** comes from the client: `fetch` has no upload progress, so
  `fb.api.files.upload` uses `XMLHttpRequest` (`upload.onprogress`) — the one
  documented exception to "no raw transport in views", kept inside `api.js`.
- **Download:** `Results.File(stream, mime, downloadName, lastModified,
  entityTag, enableRangeProcessing: true)` over a `FileStream` opened with
  `FileShare.ReadWrite | FileShare.Delete` — so a replace or trash can rename
  over a file that is being streamed on Windows. 206 partial responses and
  `If-None-Match` → 304 come for free; Range is what makes `<audio>`/`<video>`
  seek and lets the text preview fetch only the first 256 KB.
- A file held open out-of-band without delete-sharing (an editor, an
  antivirus scan) makes the rename fail on Windows: 423 Locked, "the file is
  in use on the server", nothing journalled.
- **Copy** is a streamed real copy into a temp file in the target folder, then
  rename; folders recursively, quota checked up front from the summed size.
- **No resumable uploads.** A broken upload (client abort, dropped
  connection, limit hit mid-stream) is aborted: the handler deletes its temp
  file in a `finally`, nothing is renamed into place, nothing is journalled.
  The user retries the file. Files arrive a few at a time, not in bulk, so one
  simple `PUT` is the whole upload API.
- **Sweeper** (startup + daily tick) deletes `.~fb-*.part` older than an hour —
  the backstop for a process killed mid-upload.

## Security

**Serving user bytes from the app's own origin is the risk.** A user file
served inline as `text/html` or `image/svg+xml` would run script with the
user's session. Rules:

1. **Effective type is sniffed, not trusted.** The first 4 KB go through a
   small magic-number table (PNG, JPEG, GIF, WebP, AVIF/HEIC `ftyp`, BMP, ICO,
   PDF, ZIP family, MP3/ID3, OGG, FLAC, WAV, MP4/M4A `ftyp`, WebM/Matroska)
   plus a "valid UTF-8, no NUL" text test. The extension breaks ties only
   between text types (`.md`, `.csv`, `.json`, code). Anything unrecognised is
   `application/octet-stream`. A client-declared type is never used for
   serving.
2. **Attachment by default.** Every content response carries
   `Content-Disposition: attachment`, `X-Content-Type-Options: nosniff`, and
   `Content-Security-Policy: default-src 'none'; img-src 'self' data:;
   media-src 'self'; style-src 'unsafe-inline'; sandbox`. Even a direct
   navigation to the URL renders in an opaque, script-less sandbox.
3. **`?inline=1` only for a fixed allowlist** of effective types: raster
   images, `audio/*`, `video/*`, and `text/plain; charset=utf-8` (every text
   type is served as `text/plain` inline — Markdown is rendered by the client,
   never by the browser). HTML, SVG, XML and JavaScript are **never** inline;
   SVG previews render through `<img>` (which never runs script) from an
   attachment-served URL fetched into a blob.
4. **PDF: opened in a new tab, never embedded.** `?inline=1` on a file whose
   *sniffed* type is `application/pdf` (`%PDF-` magic in the first bytes)
   answers with `Content-Type: application/pdf`, `Content-Disposition:
   inline`, `X-Content-Type-Options: nosniff`. The browser's own PDF viewer
   renders it, isolated from the page. A renamed HTML/SVG called `x.pdf`
   sniffs as something else and stays attachment-only. The UI opens it with
   `window.open(url, "_blank", "noopener")`; no `<iframe>`, `<embed>` or
   `<object>` anywhere. **Verify during implementation:** Chrome's PDF viewer
   refuses to render under some headers (a CSP `sandbox` directive among
   them), so this one variant gets its own CSP — chosen so Chrome, Edge and
   Firefox all render it, pinned by a Playwright check — instead of the
   sandbox policy in rule 2.
5. **Paths, never disk paths.** Every route takes a relative path through
   `FilePathResolver` (see "Path safety"); no route accepts or returns an
   absolute path, and error messages never contain one.
6. **Names:** bidi override/isolate characters are refused (see the rule
   table), so a name can't disguise its extension.
7. **Markdown preview** runs through the kit's `marked` + `DOMPurify`, with
   remote `<img>`/`<iframe>`/`<object>` removed (a Markdown file with
   `![](https://tracker…)` must not phone home when previewed), links forced to
   `rel="noopener noreferrer" target="_blank"`.
8. **Bearer isolation** follows the existing model: a space-bound token only
   reaches its own space's files; a personal token on a space URL is 403 even
   for a member (`ResolveSpaceAsync`). A token is bound to one context, so
   cross-workspace transfer is cookie-only.
9. **Logs** carry context, sizes, effective types, operation and a short hash
   of the path for correlation — **never file names or paths** (PII per
   CLAUDE.md), never content.

A strict CSP for the SPA itself (slimming-plan open question 7) is independent
of this spec but becomes more valuable once user content is on the page.

### Secrets and PII

Files are opaque bytes; there is no `:::secret` layer and `SecretStripper` has
nothing to strip. That is acceptable only because:

- file contents are **never** indexed (FTS, embeddings) in this spec;
- Bearer access is opt-in per key (`read:files` / `write:files` are never added
  to existing keys by migration), and MCP tools come later, text-only and
  capped;
- the UI says it plainly: the empty state and the upload hint mention that
  files are stored unencrypted and that passwords belong in a note's secret
  block.

Real names on disk also mean file names are visible to whatever reads the data
directory — backups, OS search indexers, antivirus. The deployment note should
recommend excluding `fishbowl-data/` from content indexing.

## API surface

Personal under `/api/v1/files`, space mirror under
`/api/v1/spaces/{slug}/files`, sharing handler logic with a different
`ContextRef` (the space-nested pattern). Routes live in a new `FilesApi.cs`
with a `MapFilesApi()` that registers both groups, so `SpacesApi.cs` doesn't
grow by another 300 lines. Space writes reject `role == Readonly` via
`CanWrite()`.

New scopes in `ScopeCatalog`: **`read:files`**, **`write:files`**
(CONCEPT.md calls them `read:attachments`/`write:attachments`; "files" is the
name the product uses). Cookie principals bypass scope as everywhere else.

| Route | Scope | |
|---|---|---|
| `GET /files/capabilities` | read | `{ caseSensitive, nameRules: "windows"\|"posix"\|"macos", maxSegment, maxPath, maxFileBytes, quotaBytes }` |
| `GET /files/list?path=&cursor=&limit=` | read | reconciles the folder; `{ path, entries: [{ name, kind, size, mtime, etag, sha256?, mime, createdBy? }], next }`, folders first |
| `GET /files/stat?path=` | read | one entry |
| `GET /files/content?path=` | read | stream; Range, ETag; `?inline=1` per allowlist, PDF variant per security rule 4 |
| `PUT /files/content?path=&parents=` | write | raw-body upload, one request, no resume; `If-None-Match: *` or `If-Match` required → 201/200, 412, 428 |
| `POST /files/folders` `{ path }` | write | mkdir → 201 |
| `POST /files/move` `{ from, to }` | write | rename / move; 409 clash, 400 into own subtree |
| `POST /files/copy` `{ from, to }` | write | real copy, folders recursive |
| `DELETE /files?path=` | write | to trash |
| `DELETE /files?path=&permanent=1` | cookie-only | purge |
| `GET /files/trash` | read | trash entries |
| `POST /files/trash/{id}/restore` `{ onConflict: fail\|rename }` | write | |
| `DELETE /files/trash/{id}`, `DELETE /files/trash` | cookie-only | purge one / empty |
| `GET /files/usage` | read | `{ bytes, quotaBytes, files, folders, trashBytes }` |
| `GET /files/changes?cursor=&limit=` | read | change feed; 410 on expired cursor |
| `GET /files/snapshot` | read | NDJSON full tree + cursor |
| `POST /api/v1/files/transfer` | cookie-only | cross-workspace, below |

**Transfer** `{ op: copy|move, from: { workspace, paths[] }, to: { workspace,
folder }, onConflict: fail|rename|replace }`, `workspace` = `"personal"` or
`"space:<slug>"`. Each side is resolved like its own route (membership via
`ResolveSpaceAsync`). A readonly target is 403; `move` also needs write on the
source; `copy` from a readonly source is fine. Quota and size limits are the
target's. Same volume → rename; otherwise copy then delete. Journals: `delete`
in the source, `create`s in the target. Per-item results, so a partial
failure reports which items moved.

Permanent delete and empty-trash are **cookie-only** (Bearer 403): an agent
or sync client may tidy, but an irreversible wipe is a human action — same
reasoning as export. A sync client's local delete therefore lands in the
server's trash, which the auto-purge empties.

Later: `POST /files/batch` and `GET /files/archive?path=` (a folder or
selection as a streamed ZIP).

## Store adapter — the seam to the kit

The kit's `<sac-file-browser>` and `sac.files.virtual()` browse *any* object
with `list / stat / read / write / remove` (`sac.fs` handle shape, paths as
keys, folders derived from `/`) — the same path model the server now has.
Fishbowl ships one adapter in `js/lib/files-store.js`:

```js
const store = fb.filesStore({ workspace: "personal" });  // or "space:<slug>"
leftBrowser.store = store;
sac.files.use(sac.files.virtual({ store: fb.filesStore(), label: "Fishbowl" }));  // later: follows sac.scope
```

- Each pane gets its own adapter with an **explicit workspace**; `fb.api`
  grows a `files.in(workspace)` form next to its `sac.scope`-derived default,
  rather than a parallel client.
- `list(prefix)` calls `GET /files/list` for **one level** and returns the
  direct files plus a synthetic `"<sub>/.folder"` key per subfolder — the
  browser derives folders from exactly that. Per-entry stats are cached from
  the same response, so the browser's `stat()`-per-file loop costs no
  requests.
- `read(path)` returns a `File` (the kit contract); for media the view hands
  the element a content URL instead (kit gap 1b).
- `write(path, blob)` → conditional `PUT`; `remove(path)` → trash;
  `remove("<folder>/.folder")` → trash the folder.

Every future SPA app stays on the kit's own API — `context.files.open({ accept:
"image/*" })`, the user picks from Fishbowl's store in a kit dialog, the app
never learns a URL. For in-realm apps that is *consent-mediated* access, not
an enforced boundary (same origin, same realm); the enforced boundary is the
Bearer path.

## Apps and agents

- **Kit apps inside the SPA** (later): Fishbowl installs its provider with
  `sac.files.use(…)`, so `context.files.open/save` land in the user's store.
  `context.fs` (an app's private drawer) stays slimming-plan open question 4.
- **Apps-platform keys** (later): an app gets a real folder
  `Apps/<app name>/` in the owner's `files/`, created on first use. An
  app-scoped key with a new `app:files` scope is confined to that path prefix
  by `FilePathResolver`. App files count against the owner's quota and are
  visible in the Files view — nothing an app stores is hidden from its owner.
- **MCP** (later): `list_files`, `get_file_info`, `read_text_file` (effective
  type text, UTF-8, first 256 KB, `truncated` flag), `save_text_file`.
  Binary content never crosses the MCP wire.
- **Bearer REST** is in the MVP (above) — what a sync client or script uses.

## UI — the Files view

`#/files` (`js/views/fb-files-view.js`, light DOM), registered with
`sac.router.register("#/files", "fb-files-view", { label: "Files", icon: "folder" })`
and a hub tile. **Always the commander layout** — there is no plain-list mode.

### Layout (desktop)

```
┌ nav ribbon ───────────────────────────────────────── toolbar: ⬆ upload  ⌫ trash ┐
│ ╔═ Personal ▾ › Photos › 2026 ══════╗ ┌─ Family ▾ › Documents ──────────────┐ │
│ ║ ..                                ║ │ ..                                  │ │
│ ║ ▸ Summer                          ║ │ ▸ Taxes                             │ │
│ ║▐ IMG_0412.jpg   2.3 MB  Sep 21  ▌║ │   lease.pdf        412 KB  Mar 02   │ │
│ ║ ★IMG_0413.jpg   2.1 MB  Sep 21   ║ │   notes.md           3 KB  today    │ │
│ ╚═ 2 of 48 marked · 4.4 MB ═════════╝ └─ 12 files · 18.0 MB ────────────────┘ │
│ 1 Help 3 View 4 Rename 5 Copy 6 Move 7 MkDir 8 Delete ^Q Preview  ▓▓▓▓░ 38% │
└──────────────────────────────────────────────────────────────────────────────┘
```

- `<sac-split direction="horizontal" position="50%" collapse>` with one
  `<sac-file-browser multiple>` per slot. Each pane has its own **workspace
  and folder**; the **active pane** is the one with focus.
- **Workspace per pane:** the pane title starts with a `<sac-menu>` listing
  Personal and every space the user is a member of — NC's drive letter.
  Alt+F1 / Alt+F2 open it for the left / right pane. A space pane's frame is
  tinted `--accent-warm`, like the context switcher, so writes to shared data
  are unmissable. The left pane starts in the current `sac.scope` workspace.
  F5/F6 between panes in different workspaces use `POST /files/transfer`;
  a readonly space pane accepts no drops and offers no F5/F6 target.
- **The retro touch, tokens only:** the active pane gets a
  `border-style: double` frame in `--accent` and a caption-type title; the
  inactive pane a single `--border` hairline. The cursor bar is the kit's
  selected row (a solid NC-style bar is a kit ask, not a Fishbowl override).
  Marked entries use `--accent-warm-text`. Names and sizes in monospace (kit
  gap: `--font-mono`). The frame's bottom edge is a status line: counts,
  marked size, free quota. No scanlines, no CRT glow.
- **Function-key bar** at the bottom: one light-DOM row of kit buttons, each a
  `<kbd>` plus a label, clickable, wired to `sac.hotkeys` with
  `group: "Files"` so `sac-shortcut-sheet` (F1 / `?`) lists them. **No dead
  keys** (no-dead-links rule): in a readonly space pane F4–F8 are not
  rendered, not greyed. Holding Shift relabels 8 to "Delete permanently".
  Upload progress sits at the right end (`<sac-progress>`), with a
  `<sac-toast>` summary when done.
- Uploads: `<sac-drop-zone multiple>` as a full-pane overlay while a file drag
  hovers the view (and via the toolbar's upload button / Ctrl+U), targeting
  the active pane's folder.

### Preview pane

**Ctrl+Q** (and the `^Q Preview` key) switches the **right pane** into a
preview of the left pane's cursor file, following the cursor live; focus moves
to the left pane; Ctrl+Q again brings the browser back. What it shows:

| Type | Rendering |
|---|---|
| Raster images | `<img>` on a `.pz-layer` with `sac.setupPanZoom` (wheel, drag, pinch, double-click reset); dimensions in the footer |
| SVG | `<img>` from a fetched blob (never inline-served, never `<object>`) |
| Text, code, JSON, CSV, logs | first 256 KB via `Range`, `<pre>` with `textContent`, monospace, "showing 256 KB of 12 MB" note; JSON pretty-printed |
| Markdown | kit `marked` + `DOMPurify` (config under Security), toggle raw/rendered |
| Audio | `<audio controls preload="metadata">` from the content URL; moving the cursor off an audio file keeps it playing until another audio file is picked — a music folder becomes a playlist |
| Video | `<video controls>` for browser-playable codecs; otherwise the info card |
| PDF (sniffed) | info card with **Open in new tab** (the `?inline=1` PDF variant, security rule 4) and Download — never embedded |
| Anything else | info card: name, size, effective type, SHA-256 (short, when cached), modified via `fb.format`, created by (spaces), Download |

**F3** opens the same renderer in a maximised `<sac-window>` (full-screen on a
phone), with ←/→ walking the images in the folder. One image at a time — no
thumbnail grid.

### Keys

| Key | Action | Notes |
|---|---|---|
| ↑ ↓ PgUp PgDn Home End | move the cursor | `sac-file-browser` built-in |
| Enter / Backspace | open folder / up | built-in |
| Tab | switch active pane | |
| Alt+F1 / Alt+F2 | workspace of left / right pane | |
| Insert, Shift+↑/↓ | mark and advance | needs kit marks (gap) |
| F3 | viewer | |
| Ctrl+Q | preview pane on/off | |
| F4 / F2 | rename | inline; F2 is the modern alias |
| F5 | copy marked/cursor → other pane's folder | progress modal |
| F6 | move → other pane's folder | |
| F7 | new folder | `sac-file-browser.newFolder()` |
| F8 / Delete | to trash | no confirm, toast with Undo |
| Shift+F8 / Shift+Delete | delete permanently | kit confirm, `armAfterMs` |
| Ctrl+U | upload into active folder | |
| F1 / ? | shortcut sheet | |

F-keys collide with the browser (F3 find, F5 reload, F6 address bar, F1 help,
F7 caret browsing). They are captured only while the Files view has focus and
nothing editable is focused, with `preventDefault`; every action also has a
clickable key, a toolbar/menu path and a modifier alias, so a browser or OS
that eats the key (macOS without Fn) loses nothing.

Conflicts on copy/move/upload/restore open one kit confirm per clash with
**Replace / Keep both / Skip** and an "apply to all" checkbox — built on the
conditional `PUT` / `onConflict` above.

### Phone

- **Single column only.** The split collapses to one pane, `no-back`; the
  preview pane becomes the F3 viewer. Two-pane operations become a
  **destination picker**: Move/Copy open a kit dialog with a workspace menu and
  a `readonly` `<sac-file-browser>` to choose the target folder — so
  cross-workspace transfer works on a phone too.
- The F-key bar becomes the kit's toolbar items (`fb.toolbar.set`, overflow
  via "…"): upload, new folder, select, move, copy, delete, delete
  permanently.
- Marking: a "Select" toggle turns rows into checkboxes; long press as a
  shortcut.
- Upload: the drop-zone's touch mode is the tap target; `accept` unset so the
  OS offers camera and files.
- View heights and safe areas as in the other views
  (`calc(100dvh - 50px - env(safe-area-inset-top))`).

### State and URLs

The kit router matches exact hashes and remounts the view on every hash
change, so `#/files/Photos/2026` cannot address a folder today (kit gap). MVP:
`#/files` (and `#/space/<slug>/files`, which sets the left pane's workspace);
each pane's workspace and folder are per-viewer conveniences in
`sessionStorage`. Once the router can route a prefix without remounting, the
active pane's folder moves into the hash (URL-first routing, as for notes).

## Kit gaps — file upstream (sacrvm-appkit), don't build as `fb-*`

Per CLAUDE.md, what the kit lacks is either composed from kit parts in the view
or filed upstream. Most important first:

1. **`sac-file-browser` for server stores.**
   a) optional `store.entries(prefix)` → `{ folders, files: stat[] }` in one
      call, instead of a recursive `list()` plus one `stat()` per file;
   b) optional `store.url(path)` so media and previews use a streaming URL
      instead of `read()`-ing full bytes into a blob;
   c) **marks**: Insert / Shift+arrows toggle marks independently of the
      cursor, folders markable, `marked` property + `sac:mark` event;
   d) **rename** (F2, inline like `newFolder()`), `sac:rename` event;
   e) cancellable `sac:request-remove` carrying `{ permanent }` (Shift), so a
      host can trash by default instead of the hard-coded "permanently
      deleted" wording;
   f) drag between two browsers and drop of OS files onto a row/folder →
      `sac:drop { paths | files, target }`;
   g) virtualised rows for folders with thousands of entries;
   h) optional type column and sort by name/size/date;
   i) `cursor-style="bar"` (solid accent bar) for the commander look;
   j) a title slot, so the pane's workspace menu and breadcrumb sit in the
      browser's own header.
2. **`sac.fs` store contract for remote backends:** optional `move`, `copy`,
   `rename` (fallback read+write+remove), `write` progress callback, the
   `url()` hook; document that `list()` may be one level deep when `entries()`
   exists.
3. **Router prefix routes** — `register("#/files/*", …)` delivering the rest as
   a sub-route *without* remounting the view.
4. **`<sac-quick-look>`** — a preview surface for a `File`/URL: image with
   pan-zoom, text, Markdown (kit `marked`/`purify`), audio, video, info
   fallback; usable both as a pane and in a window. Fishbowl builds it in the
   view first; the ask is to lift it into the kit.
5. **`<sac-keybar>`** — a function-key bar bound to `sac.hotkeys` (label, key,
   enabled, Shift-layer labels), collapsing into `sac-nav`'s toolbar overflow
   on a phone.
6. **`sac.dialog.prompt()`** — a text-input dialog (rename on a phone, "Keep
   both" name edit).
7. **`sac-drop-zone` folder drops** — `webkitGetAsEntry()` traversal with
   relative paths in `sac:files`, and an overlay mode that covers its parent
   only while a drag hovers.
8. **`--font-mono` token** — the kit hard-codes a monospace stack on `kbd`.
9. **Upload progress in `sac.files.virtual`'s save** (follows from 2).

None block the backend; 1a/1c, 3 and 4 shape the UI MVP. If upstream is slower
than Fishbowl, the view composes around the gap and deletes the workaround when
the kit ships.

## Operational touch points

- **`tools/snapshot-data`** backs up `personal.db`/`space.db` via the online
  backup API but copies no other context files. It must copy `files/`
  (excluding `.~fb-*` temp files; `.trash` included) and, an existing gap,
  `apps/*/app.db`. Files are mutable now, so a snapshot is a best-effort copy:
  take the DB backup *first*, then copy files; any drift is what the next
  reconcile after a restore journals — and a restore rotates the journal
  epoch so clients re-snapshot.
- **Data export** — Settings offers two independent choices:
  - **Database** — the existing online-backup DB, now delivered as a ZIP
    (`GET /api/v1/export/db`);
  - **All files** — `files/` minus trash and temp (`GET /api/v1/export/files`);
  - **both in one ZIP** (`GET /api/v1/export/all`), offered only while files
    usage is below `Export:CombinedMaxBytes`; above it the option isn't shown
    and a one-line note says why (no-dead-links).

  ZIPs are **streamed** to the response (`ZipArchive` in `Create` mode on the
  non-seekable body, ZIP64 automatically) — never built in memory or in a temp
  ZIP. Already-compressed types are stored, text deflated. The UI shows the
  expected size first and warns above the same threshold. Cookie-only; the
  space variants are owner-only, like today's DB export.
- **Space delete** — see below.
- **Husky upgrades** preserve `app/fishbowl-data/`, files and archives
  included. The data drive needs headroom — the free-disk guard and
  `GET /files/usage` make that visible.

### Space delete and archived spaces

The delete dialog (owner-only, as today) gets a checkbox **"Archive before
deleting"**, checked by default.

- **Archive checked:** after a free-disk check (the archive needs about the
  space's size), stream a ZIP of the whole `spaces/<id>/` — `space.db` and
  every `apps/*/app.db` via the online backup API, `files/` including trash —
  plus a `manifest.json` (space id, slug, name, owner, members with roles,
  archived at/by, Fishbowl version, schema versions) to
  `fishbowl-data/archive/spaces/<spaceId>-<yyyyMMddHHmmss>.zip` (temp name,
  then rename). Only after the ZIP is re-opened and its entry count checked is
  the space deleted.
- **Unchecked:** the `system.db` rows **and** the `spaces/<id>/` folder are
  deleted — fixing today's leftover folder. A folder that can't be removed
  (a file locked on Windows) is renamed to `spaces/.deleted-<id>` and retried
  by the sweeper.
- Space API keys (including app keys) are deleted either way.

**What one does with an archive:** Settings → Spaces lists **Archived
spaces** to the owner who archived them — read straight from the ZIPs'
manifests, so no table is needed and the archive folder is the truth, like
`files/`. Three actions:

- **Download** — streams the ZIP; it is a complete, portable copy (unzip into
  another instance's `spaces/` for a cold import).
- **Restore** — extracts to `spaces/.restoring-<newId>/`, renames into place,
  recreates the space with a **new id**, the original slug if free (else
  `<slug>-2`…), the restoring user as its **only** member (owner).
  Former members are **not** restored — the manifest keeps them for the
  record only — and neither are API keys. The journal epoch rotates.
- **Delete** — removes the ZIP, after a confirm.

Archives count towards `Files:InstanceCapBytes` and the free-disk guard, not
towards any context quota. The daily tick purges archives older than
`Archive:RetentionDays` — the config decides keep-or-delete: **default 30
days**, `0` keeps them forever.

## Sharp edges

- **Case and name rules differ per host.** A case-insensitive server
  (Windows, default macOS) can't hold `A.txt` next to `a.txt`; a Linux sync
  client can — its second upload gets 412 and must surface a conflict. A
  Linux server can hold `CON`, `a:b` or case twins that a Windows/macOS client
  can't create — the client must skip and report them. `GET
  /files/capabilities` exists so clients know which world they talk to; the
  sync client needs an "unrepresentable name" policy.
- **Cross-host cold import** can bring names the new host can't store; the
  operator's copy fails or collides at the OS level before Fishbowl sees it.
  The index rebuild handles a changed case rule, not invalid names.
- **ETag = size + mtime** is weak on coarse-mtime file systems (FAT/exFAT:
  2 s, some SMB shares): a same-size edit inside one tick is invisible to
  `If-Match`. NTFS/ext4/APFS resolution makes it negligible there. If it bites,
  the ETag can switch to the cached sha256 without changing the API.
- **Out-of-band renames** are delete + create; out-of-band changes reach the
  feed only after a reconcile (≤ `Files:ReconcileIntervalSeconds` when a client
  is polling; daily otherwise). Full scans stat every file — fine at tens of
  thousands, worth measuring at millions.
- **Windows file locks** from editors, indexers or antivirus turn writes into
  423s; exclude the data directory from indexing and real-time scanning where
  possible.
- **Quota is soft** against out-of-band writes.

## Phases

0. **Upstream asks** filed against sacrvm-appkit (list above), in parallel
   with phase 1.
1. **Backend MVP** — v9 schema, `FilePathResolver` + host name rules + probe,
   `DiskFileStore`, `FileService` with per-context lock, reconcile (list +
   throttled full scan + daily), journal with `changes` / `snapshot` /
   compaction, trash with restore and auto-purge, sweeper, limits as config,
   `FilesApi` personal + space, **Bearer `read:files` / `write:files`**,
   conditional `PUT`, `transfer`, sniffing + security headers, snapshot-data
   copies `files/`.
2. **UI MVP** — `#/files` commander view with **per-pane workspaces** and
   cross-workspace F5/F6, store adapter, upload with progress and drop
   overlay, F-key bar incl. Shift+F8, rename, trash view with restore, Ctrl+Q
   preview pane and F3 viewer (image, text, Markdown, audio, video, info
   card), PDF "Open in new tab", Replace / Keep both / Skip, phone single column with destination
   picker, hub tile.
3. **Data lifecycle** — export choices (DB / files / both, streamed ZIPs),
   space delete with "archive before deleting", archived spaces (download,
   restore, delete, purge). Should land before Files is announced to users,
   since a space delete otherwise orphans their files.
4. **Polish** — batch endpoint, folder/selection ZIP download, folder upload,
   `sac.files` provider for SPA apps.
5. **Platform** — app subtrees + `app:files`, MCP tools, `changes?wait=`
   long-poll; the Fishbowl sync client as its own project on the diff API.
6. **Later** — attachments on top of the store, OCR and content search
   (opt-in, secret-free), vault-encrypted files, versions.

## Testing

CI runs Linux and Windows; host-dependent tests assert per OS
(`Assert.Skip` on the other), so both rule sets are exercised on every push.

- **Path safety (xUnit, table-driven):** `..`, `a/../../x`, `%2e%2e`, `..\x`,
  `/etc/passwd`, `C:\x`, `C:x`, `\\server\share`, `\\?\C:\x`, `a:stream`,
  empty and `.` segments, reserved `.trash` / `.TRASH` / `.~fb-x`; a symlink
  (Linux) and a junction (Windows) inside `files/` pointing **outside** and one
  pointing **inside** — both refused, neither followed, both listed as
  `link`; a root that is itself reached through a symlink still works.
- **Host name rules:** Windows — `CON`, `con.txt`, `COM¹`, `CONIN$`,
  `name.`, `name `, `a<b`, `TRASH~1`, 256-unit segment, path over 259 without
  `LongPathsEnabled`; Linux — the same names accepted except the Fishbowl
  floor (`\`, control chars, bidi), 256-byte segment refused; both — every
  refusal carries its `rule` code. Case: probe result, `a.txt` vs `A.txt`
  clash on an insensitive volume and coexistence on a sensitive one,
  case-only rename. NFC on create, NFD on-disk name still reachable.
- **Journal and diff:** every API operation appends the right entry
  (`move` once for a folder; `create` per descendant for copy/restore/transfer);
  a file created, modified and deleted out-of-band shows up as `source = disk`
  after list and after a full scan; a crash between disk op and journal insert
  is repaired by the next reconcile; cursor monotonic across compaction;
  expired, foreign-epoch and ahead-of-head cursors → 410; case-rule mismatch
  rebuilds the index and rotates the epoch; **convergence** — random
  operations (API and out-of-band), a simulated client applying snapshot +
  feed ends equal to the disk.
- **Storage:** temp file lives in the target folder and is hidden; a killed
  upload leaves only a `.part` the sweeper removes; an aborted request
  (client disconnect, over-limit mid-stream) leaves no temp file, no visible
  file and no journal entry; streaming hash equals
  `SHA256.HashData` on a multi-MB payload without buffering (memory high-water
  check); copy produces an independent file; trash → restore to original path
  (missing parents recreated, clash → rename) → purge; auto-purge respects 0;
  usage includes trash; sniffing table incl. HTML-disguised-as-PNG → not
  inline; Windows — replace and trash succeed while a download stream is open.
- **Host (`WebApplicationFactory`, `X-Test-User-Id`):** `PUT` → 201; missing
  `X-Fishbowl-Upload` → 400; no condition → 428; `If-None-Match: *` on an
  existing file and stale `If-Match` → 412; over `MaxFileBytes` by
  `Content-Length` and by a chunked body without it → 413; quota → 413; config
  change applies without restart; Range → 206 with correct `Content-Range`;
  `If-None-Match` → 304; `?inline=1` on HTML/SVG — and on HTML renamed to
  `.pdf` — still `attachment`; `?inline=1` on a real PDF → `application/pdf`,
  `inline`, `nosniff`; every other content response carries `nosniff` +
  sandbox CSP; Bearer without
  `read:files` → 403; permanent delete / empty trash / transfer via Bearer →
  403; space readonly write and readonly transfer target → 403; personal token
  on a space URL → 403; transfer quota is the target's; export ZIPs stream and
  open; combined export absent above the threshold; space delete with archive
  (ZIP + manifest, folder gone) and without (rows + folder gone); archive
  restore under a taken slug. Fresh data dir per fixture and
  `SqliteConnection.ClearAllPools()` before deleting it (Windows). No test
  asserts a file name in the log output.
- **UI (`Fishbowl.Ui.Tests`, Playwright on the shared host):** upload via the
  drop-zone's input (`SetInputFilesAsync`) → row appears with size; F7 creates
  a folder; Tab switches the active pane; Alt+F2 switches the right pane to a
  space and F5 copies across; F6 moves a marked file; F8 trashes without a
  confirm and Undo restores; Shift+F8 asks, then purges; Ctrl+Q shows a PNG
  as `<img>`, `.md` as a rendered heading with **no** `<script>` from a
  hostile fixture, `.txt` as text, `.mp3` as an `<audio>` whose `src` answers
  a Range request, a PDF as an info card with no `<iframe>`/`<embed>` whose
  "Open in new tab" loads a page the browser's PDF viewer renders (the CSP
  check from security rule 4 — Chromium in CI; Edge and Firefox verified by
  hand); readonly space pane shows no write keys; phone viewport
  shows one pane and the Move destination picker with a workspace menu; no
  request from the view bypasses `fb.api`.

## Decisions (2026-09-26)

1. **Real files, host semantics.** No content-addressed blobs: real names in a
   real tree under `users/<id>/files/` / `spaces/<id>/files/`; the folder is
   the truth. Name rules, case sensitivity and path limits follow the host;
   every path is resolved inside the root, links are never followed. The DB
   keeps only the reconcile index/metadata cache, the change journal and trash
   bookkeeping. Copy is a real copy; writes go temp-file-then-rename. Cold
   import still moves everything.
2. **Limits are configuration** (`system_config`, `tools/set-config`): max
   file size 2 GiB, quota 20 GiB per context, optional instance cap — defaults,
   not constants.
3. **Delete like an OS file explorer:** Delete/F8 → trash (restore to the
   original path, clash → ask or rename); Shift+Delete / Shift+F8 → permanent
   after a confirm. Trash auto-purges after 30 days by default, 0 = never.
4. **Commander layout always**, no plain list; the right pane toggles to a
   preview pane (Ctrl+Q). Phone: single column.
5. **Workspaces across panes in the MVP**; cross-workspace copy/move with role
   checks (readonly space is never a target), cookie-only.
6. **Bearer API from the start** (`read:files` / `write:files`) plus a change
   feed (cursor, 410 → snapshot, compaction) and conditional uploads
   (`If-Match` / `If-None-Match: *`, ETag = size + mtime) — the foundation of
   a future Fishbowl sync client.
7. **No thumbnails, no gallery.** A single image in the preview pane / viewer.
8. **Export as separate choices** — database, all files, or both in one
   streamed ZIP while below a size threshold.
9. **WebDAV: never.** A dedicated sync client instead.
10. **Space delete:** "Archive before deleting" (checked by default) writes a
    ZIP of the whole space folder to `archive/spaces/`, listed under Archived
    spaces with Download / Restore / Delete; unchecked deletes DB and files
    outright. Archives are purged after `Archive:RetentionDays` — **30 days by
    default**, `0` = keep forever. **Restore brings back no members**: the
    restoring user becomes the sole owner; API keys aren't restored either.
11. **PDF opens in a new browser tab** — only when sniffing confirms `%PDF-`,
    through the `?inline=1` variant (`application/pdf`, `inline`, `nosniff`,
    its own CSP verified against Chrome/Edge/Firefox); everything else stays
    attachment. The preview pane never embeds a PDF: info card with "Open in
    new tab".
12. **No resumable uploads.** A broken upload is aborted, its temp file
    removed, nothing half-written becomes visible; the user retries. One
    simple `PUT`.
