/**
 * fb.filesStore({ workspace }) — the kit's store contract (kit/js/lib/fs.js,
 * "THE STORE CONTRACT") over the Files API, so <sac-file-browser> and
 * sac.files.virtual() browse a workspace's real files.
 *
 *   pane.store = fb.filesStore({ workspace: "personal" });   // or "space:<slug>"
 *
 * One level per request: entries() maps GET /files/list, and every stat it
 * returns is cached, so the browser's per-file stat() costs nothing. A
 * folder marker "<folder>/.folder" written by the browser's New folder
 * becomes POST /folders; removing one trashes the folder. write() creates,
 * or replaces what is there (the view asks the user before it gets here).
 *
 * Extras for the Files view (not part of the kit contract):
 *   workspace   "personal" | "space:<slug>"
 *   api         the bound fb.api.files.in(workspace)
 *   filter      a name filter applied to entries() (the view's mod+F);
 *               folders stay so you can still walk down
 *   cached(p)   the last stat seen for a path, or null
 */
(function () {
    const MARKER = ".folder";
    const baseName = (p) => p.slice(p.lastIndexOf("/") + 1);
    const parentOf = (p) => (p.includes("/") ? p.slice(0, p.lastIndexOf("/")) : "");
    const clean = (p) => String(p || "").replace(/^\/+|\/+$/g, "");

    function toStat(e) {
        return {
            path: e.path,
            name: e.name,
            type: e.mime || "",
            size: e.size ?? 0,
            modified: e.mtime ? Date.parse(e.mtime) : null,
            binary: true,
            etag: e.etag || null,
            sha256: e.sha256 || null,
            createdBy: e.createdBy || null,
            link: e.kind === "link",
        };
    }

    fb.filesStore = function ({ workspace = "personal" } = {}) {
        const api = fb.api.files.in(workspace);
        const stats = new Map();

        const store = {
            workspace,
            api,
            filter: "",
            cached: (path) => stats.get(clean(path)) || null,

            async entries(prefix = "") {
                const folder = clean(prefix);
                const { entries } = await api.list(folder);
                const needle = store.filter.trim().toLowerCase();
                const folders = [];
                const files = [];
                for (const e of entries) {
                    if (e.kind === "folder") { folders.push(e.path); continue; }
                    const s = toStat(e);
                    stats.set(s.path, s);
                    if (needle && !s.name.toLowerCase().includes(needle)) continue;
                    files.push(s);
                }
                return { folders, files };
            },

            // Every path at all depths, folders as markers — only the kit's
            // fallbacks call this; the view never lists a whole tree.
            async list(prefix = "") {
                const out = [];
                const walk = async (folder) => {
                    const { entries } = await api.list(folder);
                    if (folder && !entries.length) out.push(`${folder}/${MARKER}`);
                    for (const e of entries) {
                        if (e.kind === "folder") await walk(e.path);
                        else out.push(e.path);
                    }
                };
                await walk(clean(prefix));
                return out.sort();
            },

            async stat(path) {
                const p = clean(path);
                if (stats.has(p)) return stats.get(p);
                try {
                    const e = await api.stat(p);
                    if (e.kind === "folder") return null;
                    const s = toStat(e);
                    stats.set(p, s);
                    return s;
                } catch (err) {
                    if (err.status === 404) return null;
                    throw err;
                }
            },

            async read(path, fallback = null) {
                const p = clean(path);
                try {
                    const blob = await api.read(p);
                    const s = stats.get(p);
                    return new File([blob], baseName(p), {
                        type: s?.type || blob.type,
                        lastModified: s?.modified || Date.now(),
                    });
                } catch (err) {
                    if (err.status === 404) return fallback;
                    throw err;
                }
            },

            async write(path, value, opts = {}) {
                const p = clean(path);
                if (baseName(p) === MARKER) {
                    await api.createFolder(parentOf(p));
                    return;
                }
                const blob = value instanceof Blob ? value
                    : new Blob([typeof value === "string" ? value : JSON.stringify(value)],
                        { type: typeof value === "string" ? "text/plain" : "application/json" });
                const progress = opts.onProgress;
                let entry;
                try {
                    entry = await api.upload(p, blob, { mode: "create", onProgress: progress });
                } catch (err) {
                    if (err.status !== 412) throw err;
                    entry = await api.upload(p, blob, { mode: "replace", onProgress: progress });
                }
                if (entry) stats.set(p, toStat(entry));
            },

            async remove(path) {
                const p = clean(path);
                const target = baseName(p) === MARKER ? parentOf(p) : p;
                await api.trash(target);
                stats.delete(target);
            },

            url: (path) => api.contentUrl(clean(path)),

            async move(from, to) {
                await api.move(clean(from), clean(to));
                stats.delete(clean(from));
            },

            async copy(from, to) {
                await api.copy(clean(from), clean(to));
            },

            async rename(path, name) {
                const p = clean(path);
                const dir = parentOf(p);
                await store.move(p, dir ? `${dir}/${name}` : name);
            },
        };
        return store;
    };
})();
