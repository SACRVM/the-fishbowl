/**
 * SACRVM APPKIT — sac.fs: the storage capability behind context.fs.
 *
 * An app gets a handle scoped to itself and nothing else:
 *
 *   await context.fs.write("notes/2026-08", { title: "…", body: "…" });
 *   const note = await context.fs.read("notes/2026-08", null);
 *   const paths = await context.fs.list("notes/");
 *
 * Three properties make this worth having over raw localStorage:
 *
 *   1. SCOPED. Every path is stored under "sac.fs/<appId>/", so two apps
 *      cannot collide on "settings" — and neither has to invent a prefix.
 *   2. ASYNC. Every method returns a Promise, even though the default backend
 *      answers immediately. An app written against this today keeps working
 *      when a host swaps in IndexedDB, the File System Access API or a server.
 *   3. SWAPPABLE. The namespacing, JSON and notification layers are the kit's;
 *      the actual bytes go through a four-method backend a host can replace:
 *
 *        sac.fs.backend = {
 *            get(key),          // → string | null   (may return a Promise)
 *            set(key, value),   // string
 *            del(key),
 *            keys(prefix),      // → string[]
 *        };
 *
 * Values are JSON — objects, arrays, strings, numbers, booleans, null. Not
 * Blobs: binary needs a backend that can hold it, and that is the point at
 * which a host supplies its own.
 *
 * The handle an app receives is deliberately small. It cannot read another
 * app's data, and it cannot enumerate what else is stored — not as a security
 * boundary (same origin, same JS realm) but so the shape stays honest when a
 * backend where that IS enforced arrives.
 *
 * API (all async except watch):
 *   read(path, fallback = null)   the stored value, or fallback if absent
 *   write(path, value)            store it; rejects if there is no room
 *   remove(path)                  delete one path
 *   list(prefix = "")             app-relative paths, sorted
 *   clear()                       delete everything this app stored
 *   usage()                       { bytes, count } — what this app keeps
 *   watch(cb)                     cb(path, value) on change, incl. other tabs;
 *                                 value is null for a delete. Returns an
 *                                 unsubscribe function.
 */
(function () {
    if (!window.sac) { console.warn("[sac.fs] globals.js must load first — storage unavailable."); return; }
    if (sac.fs) return;   // idempotent

    const ROOT = "sac.fs/";

    /** localStorage, wrapped to the backend's four methods. */
    const localBackend = {
        get(key) {
            try { return localStorage.getItem(key); }
            catch (err) { return null; }          // private mode, blocked storage
        },
        set(key, value) { localStorage.setItem(key, value); },
        del(key) {
            try { localStorage.removeItem(key); } catch (err) { /* nothing to undo */ }
        },
        keys(prefix) {
            const out = [];
            try {
                for (let i = 0; i < localStorage.length; i++) {
                    const key = localStorage.key(i);
                    if (key && key.startsWith(prefix)) out.push(key);
                }
            } catch (err) { /* no storage, no keys */ }
            return out;
        },
    };

    /** Path hygiene: no leading/trailing slashes, no empty segments, no "..". */
    function cleanPath(path) {
        const clean = String(path == null ? "" : path)
            .split("/").filter((seg) => seg && seg !== "." && seg !== "..").join("/");
        if (!clean) throw new Error("[sac.fs] a path is required");
        return clean;
    }

    const cleanId = (appId) =>
        String(appId || "app").replace(/[^a-z0-9._-]+/gi, "-").toLowerCase();

    /* Local listeners, so a write is heard in THIS tab too — the storage event
       fires only in the others. Keyed by app root. */
    const watchers = new Map();          // root → Set<cb>

    function notify(root, path, value) {
        const set = watchers.get(root);
        if (!set) return;
        for (const cb of set) {
            try { cb(path, value); }
            catch (err) { console.error("[sac.fs] a watcher threw:", err); }
        }
    }

    // Other tabs of the same origin. One document-level listener, however many
    // handles exist; storage events carry the new value already.
    let storageBound = false;
    function bindStorage() {
        if (storageBound) return;
        storageBound = true;
        window.addEventListener("storage", (e) => {
            if (!e.key || !e.key.startsWith(ROOT)) return;
            for (const root of watchers.keys()) {
                if (!e.key.startsWith(root)) continue;
                let value = null;
                if (e.newValue != null) {
                    try { value = JSON.parse(e.newValue); } catch (err) { value = null; }
                }
                notify(root, e.key.slice(root.length), value);
            }
        });
    }

    /** The handle one app gets. Everything it can reach lives under its root. */
    function handleFor(appId) {
        const root = ROOT + cleanId(appId) + "/";
        const backend = () => sac.fs.backend || localBackend;

        return {
            async read(path, fallback = null) {
                const raw = await backend().get(root + cleanPath(path));
                if (raw == null) return fallback;
                try {
                    return JSON.parse(raw);
                } catch (err) {
                    // Corrupt data is not worth crashing an app over: say so
                    // loudly, hand back the fallback, let the app move on.
                    console.warn(`[sac.fs] ${appId}: "${path}" is not readable JSON`, err);
                    return fallback;
                }
            },

            async write(path, value) {
                const key = root + cleanPath(path);
                let json;
                try {
                    json = JSON.stringify(value);
                } catch (err) {
                    throw new Error(`[sac.fs] ${appId}: "${path}" is not JSON-serializable`);
                }
                if (json === undefined) {
                    // JSON.stringify answers undefined for functions, symbols
                    // and undefined itself — no throw to catch, so check it.
                    throw new Error(
                        `[sac.fs] ${appId}: "${path}" — a function, a symbol or undefined cannot be stored`);
                }
                try {
                    await backend().set(key, json);
                } catch (err) {
                    // Quota is the one failure an app can act on (prune, warn
                    // the user), so it must arrive as a rejection, not a
                    // swallowed console line.
                    throw new Error(
                        `[sac.fs] ${appId}: could not store "${path}" — ${err && err.name === "QuotaExceededError"
                            ? "no room left in this browser's storage" : (err && err.message) || err}`);
                }
                notify(root, cleanPath(path), value);
            },

            async remove(path) {
                await backend().del(root + cleanPath(path));
                notify(root, cleanPath(path), null);
            },

            async list(prefix = "") {
                const want = root + String(prefix || "").replace(/^\/+/, "");
                const keys = await backend().keys(want);
                return keys.map((k) => k.slice(root.length)).sort();
            },

            async clear() {
                const keys = await backend().keys(root);
                for (const key of keys) {
                    await backend().del(key);
                    notify(root, key.slice(root.length), null);
                }
            },

            async usage() {
                const keys = await backend().keys(root);
                let bytes = 0;
                for (const key of keys) {
                    const raw = await backend().get(key);
                    // UTF-16 in every engine that ships localStorage: two bytes
                    // per code unit, key included — it is stored too.
                    if (raw != null) bytes += (raw.length + key.length) * 2;
                }
                return { bytes, count: keys.length };
            },

            watch(cb) {
                if (typeof cb !== "function") return () => {};
                bindStorage();
                if (!watchers.has(root)) watchers.set(root, new Set());
                watchers.get(root).add(cb);
                return () => {
                    const set = watchers.get(root);
                    if (!set) return;
                    set.delete(cb);
                    if (set.size === 0) watchers.delete(root);
                };
            },
        };
    }

    sac.fs = {
        /**
         * The handle for one app — what lands in context.fs.
         *
         * A host calls this too, with somebody else's id: that is how a desktop
         * shows what an app is keeping (`usage()`) or offers to delete it
         * (`clear()`) without being that app.
         */
        for: handleFor,

        /**
         * Host-side: the ids that have stored something. An app cannot ask this
         * — it only ever sees its own drawer — but a host must be able to, or
         * the data of an app somebody uninstalled a year ago is unreachable
         * and unaccountable. Pair it with for(id).usage() and for(id).clear().
         */
        async apps() {
            const backend = sac.fs.backend || localBackend;
            const keys = await backend.keys(ROOT);
            const ids = new Set();
            for (const key of keys) {
                const rest = key.slice(ROOT.length);
                const cut = rest.indexOf("/");
                if (cut > 0) ids.add(rest.slice(0, cut));
            }
            return Array.from(ids).sort();
        },

        /** Swap the bytes layer; null restores the localStorage default. */
        backend: null,
    };
})();
