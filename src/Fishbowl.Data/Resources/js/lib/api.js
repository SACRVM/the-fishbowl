/**
 * Fishbowl — fetch wrapper for /api/v1/*.
 * 401 responses redirect to /login. Non-OK responses throw ApiError.
 *
 * Context-aware: every CRUD wrapper routes through `fb.context.endpoint(path)`
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
        return window.fb?.context?.endpoint ? fb.context.endpoint(path) : path;
    }

    const crud = (resource) => ({
        list:   ()        => request(ctx(`/${resource}`)),
        get:    (id)      => request(ctx(`/${resource}/${encodeURIComponent(id)}`)),
        create: (body)    => request(ctx(`/${resource}`),                      { method: "POST",   body: JSON.stringify(body) }),
        update: (id, body) => request(ctx(`/${resource}/${encodeURIComponent(id)}`), { method: "PUT",    body: JSON.stringify(body) }),
        delete: (id)      => request(ctx(`/${resource}/${encodeURIComponent(id)}`), { method: "DELETE" })
    });

    // Notes list accepts an optional filter: { tags?: string[], match?: 'any'|'all' }.
    // Repeated `tag` query params follow the server's IReadOnlyCollection<string>
    // binding; absent params leave the server defaults (no filter, match=any).
    function listNotes(opts) {
        if (!opts || !opts.tags || opts.tags.length === 0) return request(ctx("/notes"));
        const qs = new URLSearchParams();
        for (const t of opts.tags) qs.append("tag", t);
        if (opts.match === "all") qs.set("match", "all");
        return request(`${ctx("/notes")}?${qs.toString()}`);
    }

    // ── Secret block transforms (Phase 3 encryption) ───────────────────────
    //
    // Outbound: extract ::secret\n…\n::end bodies from note.content, encrypt
    //           each via fb.vault, write them to contentSecret as a JSON
    //           envelope, and replace the inline body with ::secret#N::end
    //           marker. The server never sees plaintext secrets.
    // Inbound:  inverse — decrypt each entry in contentSecret and splice the
    //           body back between ::secret and ::end so the editor sees the
    //           normal inline form.
    //
    // Legacy notes with inline bodies and no contentSecret round-trip through
    // here unchanged until they're saved; that save triggers lazy migration.
    // A failure to unlock (user cancels) leaves markers + ciphertext in
    // place; the editor renders them as "[decryption failed]" inside each
    // ::secret block so the user sees something's encrypted rather than
    // silently losing data.

    // ::secret (optional label) \n <body> \n ::end (rest of line). `m` for
    // per-line anchors; non-greedy body so adjacent blocks don't collapse.
    // IMPORTANT: the optional label uses `[ \t]` (horizontal whitespace
    // only), NOT `\s`. With `\s`, the engine greedily consumes the `\n`
    // after `::secret` into the optional group AND then `[^\n]*` eats the
    // first body line — so the capture group loses the first body line.
    // Bug squashed: always keep the label-matcher confined to the boundary
    // line by using [ \t] to forbid newlines.
    const INLINE_SECRET_RE = /^::secret(?:[ \t][^\n]*)?\n([\s\S]*?)\n::end[^\n]*$/gm;
    const MARKER_SECRET_RE = /::secret#(\d+)::end/g;

    async function transformNoteOutbound(note) {
        const content = note?.content || "";
        const bodies = [];
        const rewritten = content.replace(INLINE_SECRET_RE, (_m, body) => {
            const i = bodies.length;
            bodies.push(body);
            return `::secret#${i}::end`;
        });
        if (bodies.length === 0) {
            // User removed every secret block. Null contentSecret to avoid
            // orphaned ciphertext sitting on the row. Leftover markers
            // without matching inline bodies is a weird state we don't
            // auto-clean — preserve whatever's there so the user can fix it.
            const hasMarkers = /::secret#\d+::end/.test(content);
            if (!hasMarkers && note?.contentSecret) {
                return { ...note, contentSecret: null };
            }
            return note;
        }
        if (!window.fb?.vault) throw new Error("fb.vault unavailable — secret encryption disabled");
        await fb.vault.ensureUnlocked();
        const ciphertexts = [];
        for (const body of bodies) ciphertexts.push(await fb.vault.encryptBlock(body));
        const payload = JSON.stringify({ v: 1, blocks: ciphertexts });
        // Base64 of UTF-8 JSON bytes — what the server deserialises into
        // the byte[] ContentSecret column.
        const bytes = new TextEncoder().encode(payload);
        const contentSecret = btoa(String.fromCharCode(...bytes));
        return { ...note, content: rewritten, contentSecret };
    }

    async function transformNoteInbound(note) {
        if (!note) return note;
        const content = note.content || "";
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
        if (payload.v !== 1 || !Array.isArray(payload.blocks)) return note;

        try { if (window.fb?.vault) await fb.vault.ensureUnlocked(); }
        catch { return note; } // user cancelled unlock — leave markers visible

        const decrypted = {};
        for (const [, idx] of content.matchAll(MARKER_SECRET_RE)) {
            if (decrypted[idx] !== undefined) continue;
            const n = Number(idx);
            if (n < 0 || n >= payload.blocks.length) {
                decrypted[idx] = `[no ciphertext #${idx}]`;
                continue;
            }
            try { decrypted[idx] = await fb.vault.decryptBlock(payload.blocks[n]); }
            catch (e) {
                console.warn(`fb.api.notes: decryptBlock #${n} failed:`, e);
                decrypted[idx] = "[decryption failed]";
            }
        }
        const restored = content.replace(MARKER_SECRET_RE,
            (_m, idx) => `::secret\n${decrypted[idx]}\n::end`);
        return { ...note, content: restored };
    }

    const rawNotes = crud("notes");
    rawNotes.list = listNotes;

    const notes = {
        list:   async (opts)     => {
            const arr = await rawNotes.list(opts);
            if (!Array.isArray(arr)) return arr;
            return Promise.all(arr.map(transformNoteInbound));
        },
        get:    async (id)       => transformNoteInbound(await rawNotes.get(id)),
        create: async (body)     => transformNoteInbound(await rawNotes.create(await transformNoteOutbound(body))),
        update: async (id, body) => rawNotes.update(id, await transformNoteOutbound(body)),
        delete: rawNotes.delete,
    };

    // Contacts list accepts an optional filter: { includeArchived?: boolean }.
    function listContacts(opts) {
        if (!opts || !opts.includeArchived) return request(ctx("/contacts"));
        return request(`${ctx("/contacts")}?includeArchived=true`);
    }
    const contacts = crud("contacts");
    contacts.list   = listContacts;
    // Resource-scoped search — contacts_fts backed, different ranker from
    // the hybrid notes search so it stays on its own path.
    contacts.search = (query, { limit = 50 } = {}) => {
        const qs = new URLSearchParams({ q: query, limit: String(limit) });
        return request(`${ctx("/contacts/search")}?${qs.toString()}`);
    };

    // Events list accepts { from, to } as a chronological range — maps to
    // the server's half-open [from, to) query. Both must be Date-ish
    // (Date or ISO-8601 string).
    function listEvents(opts) {
        if (!opts || !opts.from || !opts.to) return request(ctx("/events"));
        const from = opts.from instanceof Date ? opts.from.toISOString() : opts.from;
        const to   = opts.to   instanceof Date ? opts.to.toISOString()   : opts.to;
        const qs = new URLSearchParams({ from, to });
        return request(`${ctx("/events")}?${qs.toString()}`);
    }
    const events = crud("events");
    events.list = listEvents;

    // Common shortcuts for the calendar view. All resolve to the same
    // /events?from=&to= range query server-side.
    events.upcoming = (days = 7) => {
        const from = new Date();
        const to   = new Date(from.getTime() + days * 86400_000);
        return listEvents({ from, to });
    };
    events.today = () => {
        const from = new Date(); from.setHours(0, 0, 0, 0);
        const to   = new Date(from.getTime() + 86400_000);
        return listEvents({ from, to });
    };

    fb.api = {
        notes,
        todos: crud("todos"),
        contacts,
        events,
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
        // Export the current context's SQLite DB file. Returns a Blob the
        // caller can turn into a download (e.g. via URL.createObjectURL).
        // Cookie-only — Bearer gets 403.
        exportDb: () => fetch(base + ctx("/export/db"), {
            headers: { "Accept": "application/vnd.sqlite3" },
        }).then(async (res) => {
            if (res.status === 401) { window.location.href = "/login"; throw new ApiError(401, "Unauthenticated"); }
            if (!res.ok) throw new ApiError(res.status, await res.text().catch(() => ""));
            return res.blob();
        }),
        spaces: {
            list:   ()       => request("/spaces"),
            get:    (slug)   => request(`/spaces/${encodeURIComponent(slug)}`),
            create: ({ name }) => request("/spaces", { method: "POST", body: JSON.stringify({ name }) }),
            delete: (slug)   => request(`/spaces/${encodeURIComponent(slug)}`, { method: "DELETE" }),
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
            get: () => request("/me")
        },
        auth: {
            logout: () => request("/auth/logout", { method: "POST" })
        }
    };
    fb.ApiError = ApiError;
})();
