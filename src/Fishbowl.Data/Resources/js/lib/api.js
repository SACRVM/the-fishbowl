/**
 * Fishbowl — fetch wrapper for /api/v1/*.
 * 401 responses redirect to /login. Non-OK responses throw ApiError.
 *
 * Context-aware: every CRUD wrapper routes through `ctx(path)`, built from
 * `sac.scope.get()`
 * so a request for "/notes" becomes "/api/v1/notes" when personal is active
 * or "/api/v1/spaces/SLUG/notes" when a space is active. Context-agnostic
 * endpoints (spaces CRUD, API keys, auth, /me) stay on the personal path.
 */
(function () {
    const base = "/api/v1";

    class ApiError extends Error {
        constructor(status, body) {
            super(`API error ${status}: ${body}`);
            this.status = status;
            this.body = body;
        }
    }

    async function request(path, options = {}) {
        const res = await fetch(base + path, {
            headers: { "Content-Type": "application/json", ...(options.headers || {}) },
            ...options
        });
        if (res.status === 401) {
            window.location.href = "/login";
            // Throw so callers' await chains don't continue as if success.
            throw new ApiError(401, "Unauthenticated");
        }
        if (!res.ok) {
            const body = await res.text().catch(() => "");
            throw new ApiError(res.status, body);
        }
        if (res.status === 204) return undefined;
        const contentType = res.headers.get("content-type") || "";
        if (contentType.includes("application/json")) return res.json();
        return res.text();
    }

    // `ctx(path)` prefixes `path` with the current space slug when a space
    // context is active. Called lazily (per request) so switching context
    // doesn't require rebuilding the fb.api object.
    function ctx(path) {
        // Not sac.scope.endpoint(): the kit reuses its hash prefix ("space",
        // singular) for data paths, but the REST nesting is plural.
        const s = window.sac?.scope?.get?.();
        return s?.type === "scoped" ? `/spaces/${encodeURIComponent(s.slug)}${path}` : path;
    }

    // A path in a named workspace ("personal" | "space:<slug>"), else ctx().
    function wsPath(ws, path) {
        if (ws === "personal") return path;
        if (typeof ws === "string" && ws.startsWith("space:")) return `/spaces/${encodeURIComponent(ws.slice(6))}${path}`;
        return ctx(path);
    }

    function messagesChanged(result) {
        window.dispatchEvent(new CustomEvent("fb:messages-changed"));
        return result;
    }

    function spacesChanged(result) {
        window.dispatchEvent(new CustomEvent("fb:spaces-changed"));
        return result;
    }

    // `ws` pins a workspace ("personal" | "space:<slug>"); without it the
    // active one at request time.
    const crud = (resource, ws) => ({
        list:   ()        => request(wsPath(ws, `/${resource}`)),
        get:    (id)      => request(wsPath(ws, `/${resource}/${encodeURIComponent(id)}`)),
        create: (body)    => request(wsPath(ws, `/${resource}`),                      { method: "POST",   body: JSON.stringify(body) }),
        update: (id, body) => request(wsPath(ws, `/${resource}/${encodeURIComponent(id)}`), { method: "PUT",    body: JSON.stringify(body) }),
        delete: (id)      => request(wsPath(ws, `/${resource}/${encodeURIComponent(id)}`), { method: "DELETE" })
    });

    // Notes list accepts an optional filter: { tags?: string[], match?: 'any'|'all' }.
    // Repeated `tag` query params follow the server's IReadOnlyCollection<string>
    // binding; absent params leave the server defaults (no filter, match=any).
    function listNotes(opts, ws) {
        if (!opts || !opts.tags || opts.tags.length === 0) return request(wsPath(ws, "/notes"));
        const qs = new URLSearchParams();
        for (const t of opts.tags) qs.append("tag", t);
        if (opts.match === "all") qs.set("match", "all");
        return request(`${wsPath(ws, "/notes")}?${qs.toString()}`);
    }

    // ── Secret block transforms (vault v2) ─────────────────────────────────
    //
    // Outbound: take each :::secret … :::end block out of note.content — the
    //           whole block as written, its label and any text after
    //           `:::end` included — encrypt it via fb.vault, write the
    //           ciphertexts to contentSecret as a JSON envelope
    //           { v: 2, whole: true, blocks: [...] }, and put a
    //           :::secret#N:::end marker line where the block was. The
    //           server never sees plaintext secrets.
    // Inbound:  inverse — decrypt each entry in contentSecret and put it back
    //           in place of its marker, so the editor sees the block exactly
    //           as it was written. Envelopes without `whole` (saved before)
    //           hold only the lines between opener and closer; those come
    //           back between a bare :::secret and :::end.
    //
    // Each block is encrypted with AAD "<note id>|<index>", so ciphertext
    // can't be moved to another note or reordered within one. A note gets
    // its id before its first secret (the notes view creates notes empty),
    // so an id-less note with a secret block is refused, not guessed at.
    //
    // Spaces have no vault: a note with a secret block is refused there
    // before any vault prompt (the server refuses it too).
    //
    // Delimiters read as `:{2,3}` and are always written as three. A failure
    // to unlock (user cancels) leaves markers + ciphertext in place; so does
    // a block that can't be decrypted — the whole note stays as stored
    // (flagged `secretsUnreadable`), so it opens read-only and no save can
    // put anything in place of that ciphertext.

    // One grammar with the editor (vendored sac-md-secret.js) and the server
    // (SecretStripper): a block OPENS on a line `:::secret` (or `::secret`,
    // optional label) and CLOSES on a line `:::end` / `::end`, optionally
    // followed by whitespace and text; without a closer it runs to the end
    // of the note. Opening is generous (any case, indented), closing strict
    // (never `::endpoint=`, never an indented `:::end`): everything the
    // editor shows as secret gets encrypted, never less. Whitespace is JS's
    // \s, as in the editor: a non-breaking space (Option+Space) after
    // `:::secret` opens a block there, so it does here. Lines are split on
    // "\n" only, like the editor's.
    const SECRET_OPEN = /^\s*:{2,3}secret(?:\s|$)/i;
    const SECRET_CLOSE = /^:{2,3}end(?:\s|$)/;

    // The secret blocks of a text as line ranges { open, close } (close =
    // the last line when the block isn't closed).
    function secretRanges(content) {
        const lines = String(content ?? "").split("\n");
        const ranges = [];
        for (let i = 0; i < lines.length; i++) {
            if (!SECRET_OPEN.test(lines[i])) continue;
            let end = i + 1;
            while (end < lines.length && !SECRET_CLOSE.test(lines[end])) end++;
            ranges.push({ open: i, close: Math.min(end, lines.length - 1), closed: end < lines.length });
            i = Math.min(end, lines.length - 1);
        }
        return ranges;
    }

    // Replaces each secret block (its lines) with fn(body, block) — body =
    // the lines between opener and closer, block = all of its lines as
    // written; returns the new text.
    function replaceSecretBlocks(content, fn) {
        const lines = String(content ?? "").split("\n");
        const out = [];
        let at = 0;
        for (const r of secretRanges(content)) {
            for (; at < r.open; at++) out.push(lines[at]);
            const body = lines.slice(r.open + 1, r.closed ? r.close : r.close + 1).join("\n").replace(/\r$/, "");
            out.push(fn(body, lines.slice(r.open, r.close + 1).join("\n")));
            at = r.close + 1;
        }
        for (; at < lines.length; at++) out.push(lines[at]);
        return out.join("\n");
    }
    const MARKER_SECRET_RE = /:{2,3}secret#(\d+):{2,3}end/g;
    const ENVELOPE_VERSION = 2;

    // Thrown for a save the user has to change, with a message the view can
    // show as is.
    class SecretSaveError extends Error {
        constructor(message) { super(message); this.userFacing = true; }
    }

    const blockAad = (noteId, index) => `${noteId}|${index}`;

    // `ws` is the workspace the note is saved to (else the active one): a
    // space holds no secrets.
    async function transformNoteOutbound(note, ws) {
        const content = note?.content || "";
        const bodies = [];
        // The whole block is encrypted, not only the lines inside: a label
        // (`:::secret AWS root`) and text after `:::end` come back as written.
        const rewritten = replaceSecretBlocks(content, (_body, block) => {
            const i = bodies.length;
            bodies.push(block);
            return `:::secret#${i}:::end`;
        });
        if (bodies.length === 0) {
            // User removed every secret block. Null contentSecret to avoid
            // orphaned ciphertext sitting on the row. Leftover markers
            // without matching inline bodies is a weird state we don't
            // auto-clean — preserve whatever's there so the user can fix it.
            const hasMarkers = /:{2,3}secret#\d+:{2,3}end/.test(content);
            if (!hasMarkers && note?.contentSecret) {
                return { ...note, contentSecret: null };
            }
            return note;
        }
        if (ws ? ws.startsWith("space:") : window.sac?.scope?.get?.().type === "scoped")
            throw new SecretSaveError("Secrets are personal for now — remove the secret block to save this note in a space.");
        if (!note?.id) throw new SecretSaveError("Save the note once before adding a secret to it.");
        if (!window.fb?.vault) throw new SecretSaveError("Secrets are unavailable in this browser.");
        try { await fb.vault.ensureUnlocked({ setup: true }); }
        catch (e) { throw new SecretSaveError(e?.message || "Secrets are locked — the note was not saved."); }
        const ciphertexts = [];
        for (let i = 0; i < bodies.length; i++)
            ciphertexts.push(await fb.vault.encryptBlock(bodies[i], blockAad(note.id, i)));
        const payload = JSON.stringify({ v: ENVELOPE_VERSION, whole: true, blocks: ciphertexts });
        // Base64 of UTF-8 JSON bytes — what the server deserialises into
        // the byte[] ContentSecret column.
        const bytes = new TextEncoder().encode(payload);
        const contentSecret = btoa(String.fromCharCode(...bytes));
        return { ...note, content: rewritten, contentSecret };
    }

    // `prompt: false` — decrypt only if the vault is already unlocked, never
    // ask. Used to repaint a view right after lock(): its secrets go back to
    // markers instead of popping the unlock dialog straight away.
    async function transformNoteInbound(note, { prompt = true } = {}) {
        if (!note) return note;
        const content = note.content || "";
        // Cheap pre-filter before the JSON/base64/crypto work below. Two
        // colons on purpose: ":::secret#" contains "::secret#", so this one
        // test covers both delimiter forms. MARKER_SECRET_RE does the real
        // matching.
        if (!content.includes("::secret#")) return note;
        if (!note.contentSecret) return note;

        let payload;
        try {
            const raw = Uint8Array.from(atob(note.contentSecret), c => c.charCodeAt(0));
            payload = JSON.parse(new TextDecoder().decode(raw));
        } catch (e) {
            console.warn("fb.api.notes: invalid content_secret JSON:", e);
            return note;
        }
        if (!Array.isArray(payload?.blocks)) return note;

        // Each marker is replaced by text(idx): the whole block as written
        // (`whole` envelopes), or the lines inside it with a bare opener and
        // closer around them (envelopes saved before).
        const wrap = (body) => `:::secret\n${body}\n:::end`;
        const restore = (text) => ({
            ...note,
            content: content.replace(MARKER_SECRET_RE, (_m, idx) => text(idx)),
        });

        // v1 was encrypted with a per-browser key that no longer exists.
        if (payload.v !== ENVELOPE_VERSION) return restore(() => wrap("[unreadable secret from the old vault — type it again]"));

        if (!prompt && !window.fb?.vault?.isUnlocked()) return note;
        try { if (window.fb?.vault) await fb.vault.ensureUnlocked(); }
        catch { return note; } // cancelled or unavailable — leave markers visible

        const decrypted = {};
        for (const [, idx] of content.matchAll(MARKER_SECRET_RE)) {
            if (decrypted[idx] !== undefined) continue;
            const n = Number(idx);
            if (n < 0 || n >= payload.blocks.length) {
                // Nothing stored to lose: the notice may be edited away.
                decrypted[idx] = wrap(`[no ciphertext #${idx}]`);
                continue;
            }
            try {
                const text = await fb.vault.decryptBlock(payload.blocks[n], blockAad(note.id, n));
                decrypted[idx] = payload.whole === true ? text : wrap(text);
            } catch (e) {
                console.warn(`fb.api.notes: decryptBlock #${n} failed:`, e);
                // All or nothing: the note stays as stored — markers and
                // ciphertext — so it opens read-only like a locked one. A
                // notice spliced in as text would be encrypted over the
                // ciphertext on the next save.
                const kept = { ...note };
                Object.defineProperty(kept, "secretsUnreadable", { value: true });
                return kept;
            }
        }
        return restore((idx) => decrypted[idx]);
    }

    // Notes come pinned to a workspace too (`fb.api.notes.in(ws)`, like the
    // records below): the notes view writes where it loaded.
    function notesIn(ws) {
        const raw = crud("notes", ws);
        return {
            list:   async (opts, { prompt = true } = {}) => {
                const arr = await listNotes(opts, ws);
                if (!Array.isArray(arr)) return arr;
                return Promise.all(arr.map(n => transformNoteInbound(n, { prompt })));
            },
            // `prompt: false` — decrypt only if the vault is already unlocked.
            get:    async (id, { prompt = true } = {}) => transformNoteInbound(await raw.get(id), { prompt }),
            create: async (body)     => transformNoteInbound(await raw.create(await transformNoteOutbound(body, ws))),
            update: async (id, body) => raw.update(id, await transformNoteOutbound(body, ws)),
            delete: raw.delete,
        };
    }
    const notes = { ...notesIn(), in: (ws) => notesIn(ws) };

    // Contacts, events, todos and the record trash also come pinned to a
    // workspace — `fb.api.contacts.in("personal" | "space:<slug>")`, like
    // files.in(): a view writes where it loaded, even when the hash has
    // already moved on (a save flushed on leaving).

    // Contacts list accepts an optional filter: { includeArchived?: boolean }.
    function contactsIn(ws) {
        const p = (path) => wsPath(ws, path);
        const c = crud("contacts", ws);
        c.list = (opts) => request(opts?.includeArchived ? `${p("/contacts")}?includeArchived=true` : p("/contacts"));
        // Resource-scoped search — contacts_fts backed, different ranker from
        // the hybrid notes search so it stays on its own path.
        c.search = (query, { limit = 50 } = {}) => {
            const qs = new URLSearchParams({ q: query, limit: String(limit) });
            return request(`${p("/contacts/search")}?${qs.toString()}`);
        };
        // The Contacts app's tools (ContactToolsApi): what links here, merging a
        // duplicate in, finding duplicates, vCard / CSV out and in.
        c.links      = (id)          => request(p(`/contacts/${encodeURIComponent(id)}/links`));
        c.merge      = (id, from)    => request(p(`/contacts/${encodeURIComponent(id)}/merge`), { method: "POST", body: JSON.stringify({ from }) });
        c.duplicates = ()            => request(p("/contacts/duplicates"));
        c.exportUrl  = (format)      => `${base}${p("/contacts/export")}?format=${encodeURIComponent(format)}`;
        c.import     = (text, format) => request(`${p("/contacts/import")}?format=${encodeURIComponent(format)}`,
            { method: "POST", body: text, headers: { "Content-Type": "text/plain; charset=utf-8" } });
        return c;
    }
    const contacts = contactsIn(null);
    contacts.in = contactsIn;

    // Mail (MailApi): the workspace's accounts and the synced conversations.
    // Adding / changing / removing an account is cookie-only and, in a
    // space, for its Admins.
    function mailIn(ws) {
        const p = (path) => wsPath(ws, path);
        const id = encodeURIComponent;
        return {
            accounts:      ()          => request(p("/mail/accounts")),
            addAccount:    (body)      => request(p("/mail/accounts"), { method: "POST", body: JSON.stringify(body) }),
            updateAccount: (aid, body) => request(p(`/mail/accounts/${id(aid)}`), { method: "PATCH", body: JSON.stringify(body) }),
            removeAccount: (aid)       => request(p(`/mail/accounts/${id(aid)}`), { method: "DELETE" }),
            sync:          (aid)       => request(p(`/mail/accounts/${id(aid)}/sync`), { method: "POST" }),
            // { q, account, tag, unread, archived, address, before, limit }
            threads: (opts = {}) => {
                const qs = new URLSearchParams();
                for (const [k, v] of Object.entries(opts)) if (v != null && v !== "" && v !== false) qs.set(k, String(v));
                const s = qs.toString();
                return request(p("/mail/threads") + (s ? `?${s}` : ""));
            },
            thread:  (tid)         => request(p(`/mail/threads/${id(tid)}`)),
            setTags: (tid, tags)   => request(p(`/mail/threads/${id(tid)}/tags`), { method: "PUT", body: JSON.stringify({ tags }) }),
            setSeen: (tid, seen)   => request(p(`/mail/threads/${id(tid)}/seen`), { method: "POST", body: JSON.stringify({ seen }) }),
            unreadCount: ()        => request(p("/mail/unread-count")),
            attachmentUrl: (mid, index) => `${base}${p(`/mail/messages/${id(mid)}/attachments/${index}`)}`,
            // The HTML body as a sandboxed page of its own (MailApi): no script,
            // nothing remote unless `images`.
            htmlUrl: (mid, images) => `${base}${p(`/mail/messages/${id(mid)}/html`)}${images ? "?images=true" : ""}`,
        };
    }
    const mail = { ...mailIn(null), in: mailIn };

    // Events list accepts { from, to } as a chronological range — maps to
    // the server's half-open [from, to) query. Both must be Date-ish
    // (Date or ISO-8601 string).
    function eventsIn(ws) {
        const e = crud("events", ws);
        const list = (opts) => {
            if (!opts || !opts.from || !opts.to) return request(wsPath(ws, "/events"));
            const from = opts.from instanceof Date ? opts.from.toISOString() : opts.from;
            const to   = opts.to   instanceof Date ? opts.to.toISOString()   : opts.to;
            const qs = new URLSearchParams({ from, to });
            return request(`${wsPath(ws, "/events")}?${qs.toString()}`);
        };
        e.list = list;
        // Common shortcuts for the calendar view. All resolve to the same
        // /events?from=&to= range query server-side.
        e.upcoming = (days = 7) => {
            const from = new Date();
            const to   = new Date(from.getTime() + days * 86400_000);
            return list({ from, to });
        };
        e.today = () => {
            const from = new Date(); from.setHours(0, 0, 0, 0);
            const to   = new Date(from.getTime() + 86400_000);
            return list({ from, to });
        };
        // The calendar's tools (EventToolsApi): .ics out and in (tz = the zone
        // for floating times), the contacts' birthdays between two date keys
        // (to exclusive), and the caller's subscription links.
        e.exportUrl = () => `${base}${wsPath(ws, "/events/export")}`;
        e.import = (text, tz) => request(`${wsPath(ws, "/events/import")}?tz=${encodeURIComponent(tz || "")}`,
            { method: "POST", body: text, headers: { "Content-Type": "text/calendar; charset=utf-8" } });
        e.birthdays = (from, to) => request(`${wsPath(ws, "/events/birthdays")}?${new URLSearchParams({ from, to })}`);
        e.feeds = {
            list:   ()   => request(wsPath(ws, "/events/feeds")),
            create: ()   => request(wsPath(ws, "/events/feeds"), { method: "POST" }),
            remove: (id) => request(wsPath(ws, `/events/feeds/${encodeURIComponent(id)}`), { method: "DELETE" }),
        };
        return e;
    }
    const events = eventsIn(null);
    events.in = eventsIn;

    // ── Files ──────────────────────────────────────────────────────────────
    //
    // Unlike the other resources, a Files pane names its workspace itself
    // (two panes, two workspaces): `files.in("personal" | "space:<slug>")`
    // binds one; `files.*` without it follows sac.scope like everything else.

    function currentWorkspace() {
        const s = window.sac?.scope?.get?.();
        return s?.type === "scoped" ? `space:${s.slug}` : "personal";
    }

    function filesBase(ws) {
        return ws === "personal" ? "/files" : `/spaces/${encodeURIComponent(ws.slice("space:".length))}/files`;
    }

    // Raw-body upload through XHR — fetch has no upload progress. `mode`
    // "create" sends If-None-Match: * (412 when the name is taken),
    // "replace" If-Match: * (the old one is overwritten).
    function upload(path, blob, { mode = "create", onProgress, signal } = {}) {
        return new Promise((resolve, reject) => {
            const xhr = new XMLHttpRequest();
            xhr.open("PUT", `${base}${path}`);
            xhr.setRequestHeader("X-Fishbowl-Upload", "1");
            if (mode === "replace") xhr.setRequestHeader("If-Match", "*");
            else xhr.setRequestHeader("If-None-Match", "*");
            if (blob.type) xhr.setRequestHeader("Content-Type", blob.type);
            if (onProgress) xhr.upload.onprogress = (e) => onProgress(e.loaded, e.lengthComputable ? e.total : blob.size);
            xhr.onload = () => {
                if (xhr.status === 401) { window.location.href = "/login"; reject(new ApiError(401, "Unauthenticated")); return; }
                if (xhr.status < 200 || xhr.status >= 300) { reject(new ApiError(xhr.status, xhr.responseText)); return; }
                try { resolve(JSON.parse(xhr.responseText)); } catch { resolve(null); }
            };
            xhr.onerror = () => reject(new ApiError(0, "Network error"));
            xhr.onabort = () => reject(new ApiError(0, "Aborted"));
            if (signal) signal.addEventListener("abort", () => xhr.abort(), { once: true });
            xhr.send(blob);
        });
    }

    function filesIn(workspace) {
        const b = () => filesBase(workspace ?? currentWorkspace());
        const q = (p) => `?path=${encodeURIComponent(p ?? "")}`;
        const json = (body) => ({ method: "POST", body: JSON.stringify(body) });
        return {
            get workspace() { return workspace ?? currentWorkspace(); },
            capabilities: () => request(`${b()}/capabilities`),
            // Every page of one folder: { path, entries }.
            list: async (path) => {
                const entries = [];
                let cursor = null;
                do {
                    const page = await request(`${b()}/list${q(path)}${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ""}`);
                    entries.push(...(page.entries || []));
                    cursor = page.next;
                } while (cursor);
                return { path: path || "", entries };
            },
            stat:    (path) => request(`${b()}/stat${q(path)}`),
            // A URL an <img>/<audio>/<a download> can stream from; `inline`
            // opens a PDF (or a safe type) in its own tab.
            contentUrl: (path, { inline = false } = {}) => `${base}${b()}/content${q(path)}${inline ? "&inline=1" : ""}`,
            read: async (path) => {
                const res = await fetch(`${base}${b()}/content${q(path)}`);
                if (res.status === 401) { window.location.href = "/login"; throw new ApiError(401, "Unauthenticated"); }
                if (!res.ok) throw new ApiError(res.status, await res.text().catch(() => ""));
                return res.blob();
            },
            upload:  (path, blob, opts) => upload(`${b()}/content${q(path)}&parents=1`, blob, opts),
            createFolder: (path) => request(`${b()}/folders`, json({ path })),
            move:    (from, to) => request(`${b()}/move`, json({ from, to })),
            copy:    (from, to) => request(`${b()}/copy`, json({ from, to })),
            trash:   (path) => request(`${b()}${q(path)}`, { method: "DELETE" }),
            destroy: (path) => request(`${b()}${q(path)}&permanent=1`, { method: "DELETE" }),
            trashList: () => request(`${b()}/trash`),
            restore: (id, onConflict) => request(`${b()}/trash/${encodeURIComponent(id)}/restore`,
                onConflict ? json({ onConflict }) : { method: "POST" }),
            purge:   (id) => request(`${b()}/trash/${encodeURIComponent(id)}`, { method: "DELETE" }),
            emptyTrash: () => request(`${b()}/trash`, { method: "DELETE" }),
            usage:   () => request(`${b()}/usage`),
            // The change feed (the journal): no cursor → { changes: [], cursor: "<epoch>:<head>" }; a
            // cursor → what happened after it, oldest first; 410 cursor_expired when it can't be served.
            changes: (cursor, limit) => {
                const qs = new URLSearchParams();
                if (cursor) qs.set("cursor", cursor);
                if (limit) qs.set("limit", String(limit));
                const s = qs.toString();
                return request(`${b()}/changes${s ? `?${s}` : ""}`);
            },
        };
    }

    const files = filesIn(null);
    files.in = filesIn;
    // Copy or move between (or within) workspaces. `from`/`to` workspaces
    // are "personal" | "space:<slug>"; onConflict fail | rename | replace.
    // Resolves { results: [{ from, to, error, message }] } — per item.
    files.transfer = ({ op, from, paths, to, folder, onConflict = "fail" }) => request("/files/transfer", {
        method: "POST",
        body: JSON.stringify({ op, from: { workspace: from, paths }, to: { workspace: to, folder: folder || "" }, onConflict }),
    });

    // Todos list: open ones only, unless { includeCompleted: true } — the
    // server's default leaves completed todos out.
    // The workspace's record trash (deleted notes, todos, events, contacts);
    // files keep theirs under fb.api.files (trashList/restore/purge).
    const trashIn = (ws) => ({
        list:    ()   => request(wsPath(ws, "/trash")),
        restore: (id) => request(wsPath(ws, `/trash/${encodeURIComponent(id)}/restore`), { method: "POST" }),
        remove:  (id) => request(wsPath(ws, `/trash/${encodeURIComponent(id)}`), { method: "DELETE" }),
        empty:   ()   => request(wsPath(ws, "/trash"), { method: "DELETE" }),
    });
    const trash = trashIn(null);
    trash.in = trashIn;

    // A space's own tables (spaces only: the routes live under /spaces/<slug>/).
    const t = (name) => ctx(`/tables/${encodeURIComponent(name)}`);
    const tables = {
        list:     ()             => request(ctx("/tables")),
        describe: (name)         => request(t(name)),
        query:    (name, spec)   => request(`${t(name)}/query`, { method: "POST", body: JSON.stringify(spec || {}) }),
        count:    (name, where)  => request(`${t(name)}/count`, { method: "POST", body: JSON.stringify({ where }) }),
        insert:   (name, values) => request(`${t(name)}/rows`, { method: "POST", body: JSON.stringify(values) }),
        update:   (name, id, values) => request(`${t(name)}/rows/${encodeURIComponent(id)}`, { method: "PATCH", body: JSON.stringify(values) }),
        remove:   (name, id)     => request(`${t(name)}/rows/${encodeURIComponent(id)}`, { method: "DELETE" }),
    };

    // One space's data, pinned to it whatever workspace is active — what a
    // space app gets over the bridge (fb.desktopApps, grant.api "space").
    // The cookie decides: every call runs with the user's role there.
    function spaceData(slug) {
        const p = (path) => wsPath(`space:${slug}`, path);
        // A table name, row id or note id the app passes is one plain path
        // segment. The URL parser resolves "." and ".." (encodeURIComponent
        // keeps them), so remove("..", "..") would be DELETE /spaces/<slug>/
        // with the viewer's cookie — refused here, before any request.
        const seg = (value, field) => {
            const s = typeof value === "number" && Number.isFinite(value) ? String(value) : value;
            if (typeof s !== "string" || s === "" || s === "." || s === ".." || s.length > 200) {
                throw new ApiError(400, JSON.stringify({ error: "invalid_value", field,
                    message: `${field} must be a plain name or id — not empty, "." or "..".` }));
            }
            return encodeURIComponent(s);
        };
        const tb = (name) => p(`/tables/${seg(name, "table")}`);
        const row = (name, id) => `${tb(name)}/rows/${seg(id, "id")}`;
        const post = (path, body) => request(path, { method: "POST", body: JSON.stringify(body ?? {}) });
        const range = (from, to) => {
            const q = new URLSearchParams();
            if (from) q.set("from", new Date(from).toISOString());
            if (to) q.set("to", new Date(to).toISOString());
            const s = q.toString();
            return p("/events") + (s ? `?${s}` : "");
        };
        // async, so a refused segment is a rejected promise like any refusal.
        return {
            info:      async ()            => request(`/spaces/${encodeURIComponent(slug)}`),
            members:   async ()            => request(p("/members")),
            tables:    async ()            => request(p("/tables")),
            describe:  async (name)        => request(tb(name)),
            query:     async (name, spec)  => post(`${tb(name)}/query`, spec),
            count:     async (name, where) => post(`${tb(name)}/count`, { where }),
            aggregate: async (name, spec)  => post(`${tb(name)}/aggregate`, spec),
            search:    async (q, table)    => request(`${p("/tables/search")}?${new URLSearchParams(table ? { q, table } : { q })}`),
            get:       async (name, id)    => request(row(name, id)),
            insert:    async (name, values) => post(`${tb(name)}/rows`, values),
            update:    async (name, id, values, rowVersion) => request(row(name, id) + (rowVersion != null ? `?rowVersion=${encodeURIComponent(rowVersion)}` : ""),
                                                  { method: "PATCH", body: JSON.stringify(values ?? {}) }),
            remove:    async (name, id)    => request(row(name, id), { method: "DELETE" }),
            notes:     async ()            => request(p("/notes")),
            note:      async (id)          => request(p(`/notes/${seg(id, "id")}`)),
            todos:     async (includeCompleted) => request(p(includeCompleted ? "/todos?includeCompleted=true" : "/todos")),
            events:    async (from, to)    => request(range(from, to)),
            contacts:  async ()            => request(p("/contacts")),
        };
    }

    // A space app's messages to members of its space (AppMessagesApi).
    const appMessage = (slug, folder, to, text) =>
        request(`/spaces/${encodeURIComponent(slug)}/apps/${encodeURIComponent(folder)}/notify`,
            { method: "POST", body: JSON.stringify({ to, text }) }).then(messagesChanged);

    // A space's app error store (AppErrors): Designers list and clear, any
    // member's browser reports what broke in an app's frame.
    function appErrors(slug) {
        const p = `/spaces/${encodeURIComponent(slug)}/apps/errors`;
        const q = (o) => { const s = new URLSearchParams(Object.entries(o).filter(([, v]) => v != null)).toString(); return s ? `?${s}` : ""; };
        return {
            list:   ({ app, limit } = {}) => request(p + q({ app, limit })),
            report: ({ app, kind, message, detail }) => request(p, { method: "POST", body: JSON.stringify({ app, kind, message, detail }) }),
            clear:  (app)                 => request(p + q({ app }), { method: "DELETE" }),
        };
    }

    // What a space's own apps show on their desktop tiles — each app's
    // tile.js, run on the server as the caller: { tiles: { <folder>: model } }.
    function appTiles(slug) {
        return request(`/spaces/${encodeURIComponent(slug)}/apps/tiles`);
    }

    function todosIn(ws) {
        const td = crud("todos", ws);
        td.list = (opts) => request(opts?.includeCompleted
            ? `${wsPath(ws, "/todos")}?includeCompleted=true`
            : wsPath(ws, "/todos"));
        return td;
    }
    const todos = todosIn(null);
    todos.in = todosIn;

    // The block grammar, for tests: list of block bodies in a text.
    const secretBodies = (text) => { const b = []; replaceSecretBlocks(text || "", (x) => { b.push(x); return ""; }); return b; };
    // The same grammar for views (the notes view's title, list preview and
    // lock): one definition of what a secret block is, never a copy.
    const secrets = { ranges: secretRanges, replace: replaceSecretBlocks, bodies: secretBodies };

    fb.api = {
        secretBodies, secrets,
        files,
        notes,
        todos,
        trash,
        tables,
        spaceData,
        appErrors,
        appTiles,
        appMessage,
        contacts,
        mail,
        events,
        // The active workspace as the .in() calls name it: "personal" | "space:<slug>".
        workspace: currentWorkspace,
        tags: {
            list:        ()                  => request(ctx("/tags")),
            upsertColor: (name, color)       => request(ctx(`/tags/${encodeURIComponent(name)}`),
                                                        { method: "PUT", body: JSON.stringify({ color }) }),
            rename:      (name, newName)     => request(ctx(`/tags/${encodeURIComponent(name)}/rename`),
                                                        { method: "POST", body: JSON.stringify({ newName }) }),
            delete:      (name)              => request(ctx(`/tags/${encodeURIComponent(name)}`),
                                                        { method: "DELETE" })
        },
        // Hybrid notes search (CONCEPT.md "one search bar for everything").
        // `query` returns { notes: [{…, score}], degraded: boolean }. Reindex
        // is cookie-only server-side (Bearer 403s).
        search: {
            query:   (q, { limit = 20, includePending = true } = {}) => {
                const qs = new URLSearchParams({
                    q, limit: String(limit), includePending: String(includePending),
                });
                return request(`${ctx("/search")}/?${qs.toString()}`);
            },
            reindex: () => request(ctx("/search/reindex"), { method: "POST" })
        },
        // ── Data lifecycle (Files phase 3) ──
        // Export of the current workspace as streamed ZIPs: "db", "files" or
        // "all". `info()` gives the sizes (and whether "all" is offered);
        // `url(kind)` is a same-origin link the browser downloads directly —
        // never buffered into a Blob. Cookie-only; a space is owner-only.
        // `ws` names the workspace like fb.api.files.in(): "personal" or
        // "space:<slug>"; without it the active one.
        export: {
            info: (ws) => request(wsPath(ws, "/export/info")),
            url:  (kind, ws) => base + wsPath(ws, `/export/${encodeURIComponent(kind)}`),
        },
        // Archived spaces — what "Archive before deleting" left behind.
        archive: {
            list:        ()   => request("/archive/spaces"),
            downloadUrl: (id) => `${base}/archive/spaces/${encodeURIComponent(id)}/download`,
            restore:     (id) => request(`/archive/spaces/${encodeURIComponent(id)}/restore`, { method: "POST" })
                .then(spacesChanged),
            remove:      (id) => request(`/archive/spaces/${encodeURIComponent(id)}`, { method: "DELETE" }),
        },
        // create/update/delete fire `fb:spaces-changed` so the shell's workspace
        // switcher reloads its list — whoever made the change.
        spaces: {
            list:   ()       => request("/spaces"),
            get:    (slug)   => request(`/spaces/${encodeURIComponent(slug)}`),
            create: ({ name }) => request("/spaces", { method: "POST", body: JSON.stringify({ name }) })
                .then(spacesChanged),
            // `archive` (default true) keeps a restorable ZIP; resolves to the
            // archive entry, or undefined when deleted outright.
            delete: (slug, { archive = true } = {}) =>
                request(`/spaces/${encodeURIComponent(slug)}?archive=${archive ? "true" : "false"}`, { method: "DELETE" })
                .then(spacesChanged),
            // Owner-only. `color` is a palette slot name or null (default).
            // Partial: only the fields given change.
            update: (slug, fields) => request(`/spaces/${encodeURIComponent(slug)}`, { method: "PATCH", body: JSON.stringify(fields) })
                .then(spacesChanged),
            // Who is in a space: { role, canManage, items: [{ userId, name, avatarUrl, role, joinedAt, you }] }.
            members: (slug) => request(`/spaces/${encodeURIComponent(slug)}/members`),
            setRole: (slug, userId, role) =>
                request(`/spaces/${encodeURIComponent(slug)}/members/${encodeURIComponent(userId)}`, { method: "PATCH", body: JSON.stringify({ role }) }),
            // Removing yourself = leaving the space.
            removeMember: (slug, userId) =>
                request(`/spaces/${encodeURIComponent(slug)}/members/${encodeURIComponent(userId)}`, { method: "DELETE" }),
            leave: (slug, userId) =>
                request(`/spaces/${encodeURIComponent(slug)}/members/${encodeURIComponent(userId)}`, { method: "DELETE" })
                .then(spacesChanged),
            // Invitation links. create() resolves to { id, role, expiresAt, url } —
            // the only moment the link exists; list() never has it.
            invites: {
                list:   (slug) => request(`/spaces/${encodeURIComponent(slug)}/invites`),
                create: (slug, { role, days }) =>
                    request(`/spaces/${encodeURIComponent(slug)}/invites`, { method: "POST", body: JSON.stringify({ role, days }) }),
                revoke: (slug, id) =>
                    request(`/spaces/${encodeURIComponent(slug)}/invites/${encodeURIComponent(id)}`, { method: "DELETE" }),
            },
        },
        // API keys — the create() response is the ONLY moment the raw token
        // exists on the client. Store nothing; surface it to the user with a
        // one-time-view dialog and let them copy it themselves.
        keys: {
            list:   ()       => request("/keys"),
            create: ({ name, contextType, contextId, scopes }) =>
                request("/keys", {
                    method: "POST",
                    body: JSON.stringify({ name, contextType, contextId, scopes }),
                }),
            delete: (id)     => request(`/keys/${encodeURIComponent(id)}`, { method: "DELETE" }),
        },
        version: () => request("/version"),
        providers: () => fetch("/api/auth/providers").then(r => r.json()),
        me: {
            get: () => request("/me"),
            // Personal settings, partial: send only what changes —
            // { accent } (palette slot or null) and/or { dateFormat }.
            update: (settings) => request("/me", { method: "PATCH", body: JSON.stringify(settings) }),
            // Which workspaces the personal Calendar and Contacts show:
            // { calendar: [...], contacts: [...] } — "personal" and space ids.
            // setSource switches one and resolves to the lists after it.
            sources:   ()                  => request("/me/sources"),
            setSource: (app, source, on)   => request("/me/sources", { method: "PUT", body: JSON.stringify({ app, source, on }) }),
        },
        // Secret vault key slots — personal only, never context-prefixed
        // (spaces have no vault). Cookie-only server-side. Used by fb.vault.
        vault: {
            get:     ()     => request("/vault/"),
            addSlot: (slot) => request("/vault/slots", { method: "POST", body: JSON.stringify(slot) }),
            touch:   (id)   => request(`/vault/slots/${encodeURIComponent(id)}/used`, { method: "POST" }),
            // { label } to rename; { kdf, wrappedKey } together to re-wrap.
            updateSlot: (id, patch) => request(`/vault/slots/${encodeURIComponent(id)}`, { method: "PATCH", body: JSON.stringify(patch) }),
            // 409 { error: "last-recoverable-slot" } keeps the last passphrase/recovery slot.
            deleteSlot: (id) => request(`/vault/slots/${encodeURIComponent(id)}`, { method: "DELETE" }),
        },
        auth: {
            logout: () => request("/auth/logout", { method: "POST" })
        },
        // The workspace's desktop: its tile arrangement and its installed
        // apps. Scope-aware; cookie-only; in a space only the owner writes
        // (the GET says so in canArrange / canInstall). A PUT replaces the
        // whole tile row — send every field. The server never fetches an
        // app: install/update post the manifest and entry hash this browser
        // read (sac.apps.inspect).
        desktop: {
            get:     ()          => request(ctx("/desktop")),
            // { manifestUrl, manifest, integrity, mode, granted: ["files", "identity" | "identity:pseudonymous"] }
            install: (body)      => request(ctx("/desktop/apps"), { method: "POST", body: JSON.stringify(body) }),
            // Partial: { manifestUrl?, manifest?, integrity?, mode?, granted? } — new code needs its new hash.
            updateApp: (id, body) => request(ctx(`/desktop/apps/${encodeURIComponent(id)}`), { method: "PATCH", body: JSON.stringify(body) }),
            // purgeData moves the app's folder (Apps/<name>) to the trash; kept otherwise.
            removeApp: (id, { purgeData = false } = {}) =>
                request(ctx(`/desktop/apps/${encodeURIComponent(id)}${purgeData ? "?purgeData=true" : ""}`), { method: "DELETE" }),
            putTile: (key, tile) => request(ctx(`/desktop/tiles/${encodeURIComponent(key)}`), {
                method: "PUT",
                body: JSON.stringify({
                    position: tile.position ?? null,
                    size: tile.size ?? null,
                    color: tile.color ?? null,
                    hidden: !!tile.hidden,
                }),
            }),
        },
        // System messages — personal, never context-prefixed, cookie-only.
        // Every change fires `fb:messages-changed` so the nav badge follows.
        messages: {
            // Spaces and space apps whose messages I muted (AppMessagesApi).
            muted:   ()                  => request("/messages/muted"),
            mute:    (space, app, muted) => request("/messages/muted", { method: "PUT", body: JSON.stringify({ space, app: app || null, muted }) })
                .then(messagesChanged),
            list:        (unread) => request(`/messages${unread ? "?unread=true" : ""}`),
            unreadCount: ()       => request("/messages/unread-count"),
            read:        (id)     => request(`/messages/${encodeURIComponent(id)}/read`, { method: "POST" })
                .then(messagesChanged),
        },
        // Instance administration — admins only (403 otherwise), cookie-only.
        admin: {
            users:   ()   => request("/admin/users"),
            // { quotaBytes } null = the instance default, 0 = unlimited.
            approve: (id, { quotaBytes = null, makeAdmin = false } = {}) =>
                request(`/admin/users/${encodeURIComponent(id)}/approve`,
                    { method: "POST", body: JSON.stringify({ quotaBytes, makeAdmin }) }).then(messagesChanged),
            reject:  (id) => request(`/admin/users/${encodeURIComponent(id)}/reject`,  { method: "POST" }).then(messagesChanged),
            block:   (id) => request(`/admin/users/${encodeURIComponent(id)}/block`,   { method: "POST" }).then(messagesChanged),
            unblock: (id) => request(`/admin/users/${encodeURIComponent(id)}/unblock`, { method: "POST" }).then(messagesChanged),
            // { username, displayName?, quotaBytes? } → { id, username, tempPassword, … } — the password once.
            createUser:    (body)     => request("/admin/users", { method: "POST", body: JSON.stringify(body) }),
            // Partial: { quotaBytes?: number|null, isAdmin?: bool, disabled?: bool }.
            updateUser:    (id, body) => request(`/admin/users/${encodeURIComponent(id)}`,
                { method: "PATCH", body: JSON.stringify(body) }),
            resetPassword: (id)       => request(`/admin/users/${encodeURIComponent(id)}/reset-password`, { method: "POST" }),
            // A3: { self, lastAdmin, hasData, ownedSpaces: [{ slug, name }] } before the dialog…
            deleteCheck:   (id)       => request(`/admin/users/${encodeURIComponent(id)}/delete-check`),
            // …then the delete; archive defaults to true on the server too.
            deleteUser:    (id, { archive = true } = {}) =>
                request(`/admin/users/${encodeURIComponent(id)}?archive=${archive ? "true" : "false"}`, { method: "DELETE" })
                    .then(messagesChanged),
            // The System page: version, data sizes, archives, embedding, scheduler.
            system:        ()         => request("/admin/system"),
            // System settings: [{ key, value (secrets redacted), isSet, secret, restartRequired, description }].
            config:      ()           => request("/admin/config"),
            setConfig:   (key, value) => request(`/admin/config/${encodeURIComponent(key)}`,
                { method: "PUT", body: JSON.stringify({ value }) }),
            clearConfig: (key)        => request(`/admin/config/${encodeURIComponent(key)}`, { method: "DELETE" }),
        }
    };
    fb.ApiError = ApiError;
    fb.SecretSaveError = SecretSaveError;
})();
