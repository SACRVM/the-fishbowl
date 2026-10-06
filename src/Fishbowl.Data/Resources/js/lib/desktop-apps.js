/**
 * fb.desktopApps — the apps installed on a workspace's desktop, run by the
 * kit's app runtime (sac.apps), plus the install / review / remove flow
 * (docs/superpowers/specs/2026-09-26-desktop-apps-design.md).
 *
 * The same flow as SACRVM Desktop: paste a URL, sac.apps.inspect() reads the
 * app's app.json (this browser reads it — the server never fetches a foreign
 * URL), a review dialog says what the app is and what it asks for, and the
 * install posts that manifest and its entry hash to the server
 * (fb.api.desktop.install), which validates both against the admin's policy.
 *
 * Every installed app runs sandboxed (the server enforces it): the kit's
 * isolated runtime — an opaque-origin frame (sac.apps.frameUrl →
 * /apps/frame/…, a real CSP header), the entry pinned by its SRI hash, only
 * what was granted. A new version waits for the owner to review and re-pin
 * it. (The trusted, same-realm mode is gone; an app installed that way came
 * back unpinned and asks for that review.)
 *
 * Grants, as the owner chose them at install (never more than the manifest
 * asked for):
 *   files      its storage — the app's own folder in this workspace's
 *              Files, Apps/<name> (context.fs, backed here by the Files API)
 *              — and the picker (context.files): the kit's dialog over the
 *              whole workspace, where the person picks; the isolated app
 *              holds only opaque handles to what was picked (space-apps
 *              spec phase 8 — the picker is open again, the storage stays
 *              jailed). Without it the app runs, but nothing it writes is
 *              kept and it can't open your files.
 *   identity   nothing (the default, whatever the manifest asks) or your
 *              name and picture ("identity"). A stable anonymous id per app
 *              ("identity:pseudonymous") waits for the kit: its pseudonymous
 *              mode (2.25) swaps only the id and passes the name and picture
 *              through, so it isn't offered and such a grant hands nothing
 *              (ANON below). The salt for its ids is already per account,
 *              from the server (sac.apps.identitySalt, set in sync()).
 *   connect    the servers the manifest declared — shown, not choosable;
 *              the frame's CSP allows exactly those.
 *
 * A space's own apps (mode "space", id "space.<folder>") come with the
 * desktop too: the code in the space's files/.apps/<folder>/, served by the
 * server under a signed URL (the frame has no cookie). They run sandboxed
 * and unpinned — a Designer changes the code, the next open runs it — and
 * aren't installed, so there is nothing to update, grant or remove here.
 * They get the space's data as context.space (grant.api, kit 2.25):
 * fb.api.spaceData(<slug>) — tables and rows, read-only notes, todos,
 * events, contacts, members — as the signed-in user, so the server's role
 * checks hold (a Reader's app can't write); a refusal reaches the app as
 * { code, message } with the server's code (underscores as dashes).
 * What breaks goes to the space's error store (fb.api.appErrors) for its
 * Designers: an entry that doesn't start (load), a refused call (call),
 * context.space.error(message, detail) (reported) — the server adds an
 * app.json it can't read itself. context.space.notify(to, text) sends
 * members of the space a message from the app (to: member ids or "all");
 * the server sets the sender, skips who muted it and limits the rate.
 *
 * Every installed app opens as a kit window above the desktop (the host's
 * choice, whatever `kind` the manifest says): a "view" app would take over
 * the hash, and the hash is Fishbowl's router's.
 *
 * Updates: when the desktop opens, each sandboxed app's manifest is read
 * again (once per session); a changed entry hash or version shows as
 * "Update to v…" in the tile menu and on Settings → Apps — never as a
 * message.
 */
(function () {
    const ID = "identity";
    const ID_ANON = "identity:pseudonymous";
    const INDEX = ".fishbowl-app.json";      // an app's non-file entries, in its folder
    // The kit's "pseudonymous" identity still hands over the name and the
    // picture (it replaces only the id); until it hands { id } alone, a
    // pseudonymous grant gets nothing and the choice isn't offered.
    const ANON = false;

    let user = null;
    let state = { ws: null, apps: [], canInstall: false, canArrange: false };
    const registered = new Map();           // app id → signature of what we handed the kit
    const adding = new Map();               // app id → the kit's add() in flight
    const updates = new Map();              // `${ws}|${id}` → inspected manifest
    const checked = new Set();              // `${ws}|${id}` read this session

    const workspace = () => {
        const s = sac.scope.get();
        return s.type === "scoped" ? `space:${s.slug}` : "personal";
    };
    const byId = (id) => state.apps.find((a) => a.id === id) || null;
    const nameOf = (app) => app?.manifest?.name || app?.id || fb.t("fb.apps.app", "App");
    const hostOf = (url) => { try { return new URL(url).host; } catch { return url; } };
    const has = (app, g) => (app.granted || []).includes(g);
    // What the app actually gets (see ANON).
    const identityOf = (app) => has(app, ID) ? "full" : ANON && has(app, ID_ANON) ? "pseudonymous" : "none";
    // The server's AppDataFolders.For: an app's folder in Files, by its name.
    function dataFolderFor(name, id) {
        let clean = String(name ?? "").replace(/[^A-Za-z0-9 ._-]+/g, " ").trim().replace(/\.+$/, "").trim();
        if (clean.length > 64) clean = clean.slice(0, 64).trimEnd();
        if (!clean || /^\.+$/.test(clean)) clean = id;
        return `Apps/${clean}`;
    }
    const asks = (manifest, p) => {
        const perms = manifest?.permissions;
        return Array.isArray(perms) ? perms.includes(p) : !!(perms && perms[p]);
    };

    /* ----------------------------------------------------------- kit -- */

    /** The frame document for an app of the ACTIVE workspace (kit hook). */
    function frameUrlOf(manifest) {
        const app = byId(manifest?.id);
        if (!app || !user) return null;
        const s = sac.scope.get();
        const [type, id] = s.type === "scoped" ? ["space", s.slug] : ["user", user.id];
        return `/apps/frame/${type}/${encodeURIComponent(id)}/${encodeURIComponent(app.id)}`;
    }

    /** What the kit gets: the stored manifest, as a window, with our pin. */
    function kitManifest(app) {
        return Object.assign({}, app.manifest, {
            id: app.id,
            kind: "window",
            src: app.entryUrl,
            origin: app.origin,
            manifestUrl: app.manifestUrl,
            icon: app.manifest?.icon || "cube",
            width: app.manifest?.width || "960px",
            height: app.manifest?.height || "680px",
        });
    }

    function kitOptions(app) {
        const connect = Array.isArray(app.manifest?.connect) ? app.manifest.connect : [];
        const identity = has(app, ID) ? true : ANON && has(app, ID_ANON) ? "pseudonymous" : false;
        // The files grant also gives the app the picker (a provider of its
        // own, kit 2.20): the kit's dialog over the workspace's whole Files —
        // the person picks, the app gets handles to just that.
        const ws = state.ws;
        const files = allowed(app) && window.sac?.files?.virtual
            ? { provider: () => sac.files.virtual({ store: fb.filesStore({ workspace: ws }), label: fb.t("fb.apps.picker-label", "Files") }) }
            : false;
        if (app.mode === "space") {
            return { isolated: true, integrity: false, grant: { files: false, identity: false, connect: [], api: { space: spaceApi(ws, app), tile: tileApi(ws, app) } } };
        }
        return { isolated: true, integrity: app.entryIntegrity, grant: { files, identity, connect, api: { tile: tileApi(ws, app) } } };
    }

    /**
     * context.tile for every app: what its desktop tile shows — the built-in
     * live tiles' model, { main, sub, items: [{ lead, text, tail }], empty }.
     * Kept per browser for the workspace the app runs in (fb.desktopLive
     * cleans it: text only, 30 rows); a space app's tile.js, when it has one,
     * wins over it. clear() takes the tile back to plain.
     */
    function tileApi(ws, app) {
        const key = `app:${app.id}`;
        const changed = () => window.dispatchEvent(new CustomEvent("fb:app-tile", { detail: { ws, key } }));
        return {
            set: async (model) => {
                if (!fb.desktopLive?.setAppTile(ws, key, model)) {
                    const e = new Error("A tile needs main, sub or items (text).");
                    e.code = "invalid-tile";
                    throw e;
                }
                changed();
            },
            clear: async () => { fb.desktopLive?.setAppTile(ws, key, null); changed(); },
        };
    }

    /** context.space for a space app: the space's data, refusals as { code, message }. */
    function spaceApi(ws, app) {
        const data = fb.api.spaceData(ws.slice("space:".length));
        const api = {};
        for (const [name, fn] of Object.entries(data)) {
            api[name] = async (...args) => {
                try { return await fn(...args); }
                catch (err) {
                    const e = spaceError(err);
                    report(app, "call", `${name}: ${e.code} — ${e.message}`);
                    throw e;
                }
            };
        }
        // A message to members of the space, from this app.
        api.notify = async (to, text) => {
            try { return await fb.api.appMessage(ws.slice("space:".length), app.folder.replace(/^\.apps\//, ""), to, text); }
            catch (err) {
                const e = spaceError(err);
                report(app, "call", `notify: ${e.code} — ${e.message}`);
                throw e;
            }
        };
        // The app's own report, for its Designers.
        api.error = async (message, detail) => {
            report(app, "reported", String(message ?? ""), detail == null ? null : String(detail));
        };
        return api;
    }

    /** Into the space's error store; never in the way of the app. */
    function report(app, kind, message, detail = null) {
        const slug = state.ws?.startsWith("space:") ? state.ws.slice(6) : null;
        if (!slug || app?.mode !== "space" || !message) return;
        fb.api.appErrors(slug).report({ app: app.folder.replace(/^\.apps\//, ""), kind, message: message.slice(0, 1000), detail: detail?.slice(0, 4000) ?? null })
            .catch(() => { /* the store is best effort */ });
    }

    // What goes wrong inside a running app's frame (kit ≥ 2.26, sac:app-error
    // on document): a mount that throws didn't start ("load"); an uncaught
    // error, a rejection or a failing unmount is "runtime". Only a space app's
    // reach the store (report() checks) — an installed app isn't the space's code.
    document.addEventListener("sac:app-error", (e) => {
        const d = e.detail || {};
        const app = byId(d.appId);
        if (!app) return;
        const at = d.source ? ` (${d.source}${d.line ? `:${d.line}${d.column ? `:${d.column}` : ""}` : ""})` : "";
        const what = d.kind === "rejection" ? "Unhandled rejection: " : d.kind === "unmount" ? "unmount: " : "";
        report(app, d.kind === "mount" ? "load" : "runtime", `${what}${d.message || "Script error."}${at}`,
            typeof d.stack === "string" ? d.stack : null);
    });

    function spaceError(err) {
        let body = null;
        try { body = typeof err?.body === "string" ? JSON.parse(err.body) : err?.body; } catch { /* plain text */ }
        const byStatus = { 400: "bad-request", 401: "denied", 403: "forbidden", 404: "not-found", 409: "conflict", 413: "too-large", 429: "rate-limited" };
        const code = typeof body?.error === "string" ? body.error.replace(/_/g, "-").toLowerCase()
            : byStatus[err?.status] || "internal";
        const e = new Error(typeof body?.message === "string" ? body.message : err?.message || "Request failed");
        e.code = code;
        return e;
    }

    /** Hand the active workspace's apps to the kit; drop what left. */
    function register() {
        if (!window.sac?.apps) return;
        const want = new Map(state.apps.map((a) => [a.id, JSON.stringify([state.ws, a.mode, a.entryIntegrity, a.granted, a.updatedAt])]));
        for (const [id, sig] of registered) {
            if (want.get(id) !== sig) {
                try { sac.apps.remove(id); } catch { /* already gone */ }
                registered.delete(id);
            }
        }
        for (const app of state.apps) {
            if (registered.has(app.id)) continue;
            registered.set(app.id, want.get(app.id));
            const p = sac.apps.add(kitManifest(app), kitOptions(app))
                .catch((err) => {
                    registered.delete(app.id);
                    console.warn("[fb.desktopApps] couldn't register", app.id, err?.message || err);
                })
                .finally(() => { if (adding.get(app.id) === p) adding.delete(app.id); });
            adding.set(app.id, p);
        }
    }

    /**
     * The desktop's GET, handed over by fb.desktop.load(): which apps this
     * workspace has and what the caller may do. Registers them with the kit
     * and starts the (once per session) update check.
     */
    function sync(stored) {
        // Pseudonymous ids per account (the server's), not per browser.
        if (window.sac?.apps && typeof stored?.identitySalt === "string" && stored.identitySalt) sac.apps.identitySalt = stored.identitySalt;
        state = {
            ws: workspace(),
            apps: Array.isArray(stored?.apps) ? stored.apps : [],
            canInstall: !!stored?.canInstall,
            canArrange: !!stored?.canArrange,
            installPolicy: stored?.installPolicy || "everyone",
        };
        register();
        if (state.canArrange) checkUpdates();
    }

    async function open(id) {
        if (!byId(id)) return;
        try {
            await adding.get(id);
            await sac.apps.open(id);
        }
        catch (err) {
            const code = err?.code;
            report(byId(id), "load", `${code || "error"}: ${err?.message || "couldn't start"}`);
            sac.toast?.(code === "integrity"
                ? fb.t("fb.apps.changed", "{name} changed since you installed it — review the update first.", { name: nameOf(byId(id)) })
                : fb.t("fb.apps.no-start", "{name} couldn't start.", { name: nameOf(byId(id)) }), { kind: "error" });
        }
    }

    /* ------------------------------------------------------- updates -- */

    function updateFor(id) { return updates.get(`${state.ws}|${id}`) || null; }

    async function checkUpdates() {
        const ws = state.ws;
        for (const app of state.apps) {
            const key = `${ws}|${app.id}`;
            if (app.mode === "space" || checked.has(key)) continue;
            checked.add(key);
            let fresh;
            try { fresh = await sac.apps.inspect(app.manifestUrl); }
            catch { continue; }                       // offline, moved: nothing to offer
            if (fresh.id !== app.id) continue;
            const changed = fresh.entryIntegrity && fresh.entryIntegrity !== app.entryIntegrity;
            if (changed || (fresh.version || null) !== (app.version || null)) {
                updates.set(key, fresh);
                window.dispatchEvent(new CustomEvent("fb:app-updates", { detail: { id: app.id } }));
            }
        }
    }

    /** The manifest as the author wrote it — without what inspect() added. */
    function clean(inspected) {
        const m = Object.assign({}, inspected);
        for (const k of ["src", "origin", "manifestUrl", "entryIntegrity", "integrity"]) delete m[k];
        return m;
    }

    /* ------------------------------------------------------ storage --- */
    // context.fs of an installed app is its folder in the workspace's Files
    // (Apps/<name>), when "files" was granted. Values the kit writes as JSON
    // live in one index file there; bytes (a Blob, a File) are real files at
    // their path — so an image an app keeps is a file you can see. Keys that
    // aren't an installed app's go to localStorage, like the kit's default.

    const ROOT = "sac.fs/";
    const stores = new Map();                // `${ws}|${id}` → app store

    const local = {
        get(key) { try { return localStorage.getItem(key); } catch { return null; } },
        set(key, value) { localStorage.setItem(key, value); },
        del(key) { try { localStorage.removeItem(key); } catch { /* nothing */ } },
        keys(prefix) {
            const out = [];
            try {
                for (let i = 0; i < localStorage.length; i++) {
                    const k = localStorage.key(i);
                    if (k && k.startsWith(prefix)) out.push(k);
                }
            } catch { /* no storage */ }
            return out;
        },
    };

    function refuse(app) {
        const e = new Error(fb.t("fb.apps.no-storage", "{name} wasn't given storage — allow \"Files\" under Settings → Apps.", { name: nameOf(app) }));
        e.code = "denied";
        return e;
    }

    function storeFor(app) {
        const key = `${state.ws}|${app.id}`;
        if (stores.has(key)) return stores.get(key);
        const files = fb.filesStore({ workspace: state.ws });
        const folder = app.dataFolder || `Apps/${nameOf(app)}`;
        const full = (p) => `${folder}/${p}`;
        let index = null;                    // { path: raw JSON }
        let paths = null;                    // Set of file paths (relative)
        let chain = Promise.resolve();       // index writes, one after another
        const serial = (fn) => (chain = chain.then(fn, fn));

        async function load() {
            if (index && paths) return;
            index = {};
            paths = new Set();
            try {
                const blob = await files.api.read(full(INDEX));
                const data = JSON.parse(await blob.text());
                if (data && typeof data.entries === "object") index = data.entries;
            } catch { /* none yet */ }
            const walk = async (rel) => {
                let listing;
                try { listing = await files.api.list(rel ? full(rel) : folder); }
                catch { return; }            // no folder yet
                for (const e of listing.entries) {
                    const r = e.path.slice(folder.length + 1);
                    if (e.kind === "folder") await walk(r);
                    else if (r !== INDEX) paths.add(r);
                }
            };
            await walk("");
        }
        const saveIndex = () => serial(() => files.write(full(INDEX),
            new Blob([JSON.stringify({ v: 1, entries: index })], { type: "application/json" })));

        const store = {
            async get(p) {
                await load();
                if (Object.prototype.hasOwnProperty.call(index, p)) return index[p];
                if (!paths.has(p)) return null;
                const s = await files.stat(full(p));
                if (!s) { paths.delete(p); return null; }
                return JSON.stringify({ "$sac.blob": { type: s.type || "application/octet-stream", size: s.size, modified: s.modified || Date.now() } });
            },
            async set(p, raw) {
                await load();
                let parsed = null;
                try { parsed = JSON.parse(raw); } catch { /* stored as is */ }
                // A blob's stub: the file written just before is the truth.
                if (parsed && typeof parsed === "object" && parsed["$sac.blob"] && typeof parsed["$sac.blob"].data !== "string") {
                    if (Object.prototype.hasOwnProperty.call(index, p)) { delete index[p]; await saveIndex(); }
                    return;
                }
                index[p] = raw;
                await saveIndex();
            },
            async del(p) {
                await load();
                if (Object.prototype.hasOwnProperty.call(index, p)) { delete index[p]; await saveIndex(); }
            },
            async keys(prefix) {
                await load();
                return [...new Set([...Object.keys(index), ...paths])].filter((p) => p.startsWith(prefix));
            },
            async getBlob(p) {
                try { return await files.api.read(full(p)); } catch { return null; }
            },
            async setBlob(p, blob) {
                await load();
                await files.write(full(p), blob);
                paths.add(p);
            },
            async delBlob(p) {
                await load();
                if (!paths.has(p)) return;
                try { await files.remove(full(p)); } catch { /* already gone */ }
                paths.delete(p);
            },
        };
        stores.set(key, store);
        return store;
    }

    /** "sac.fs/<app id>/<path>" of an installed app → [app, path], else null. */
    function route(key) {
        if (typeof key !== "string" || !key.startsWith(ROOT)) return null;
        const rest = key.slice(ROOT.length);
        const cut = rest.indexOf("/");
        const app = byId(cut < 0 ? rest : rest.slice(0, cut));
        return app ? [app, cut < 0 ? "" : rest.slice(cut + 1)] : null;
    }

    const allowed = (app) => has(app, "files");

    const backend = {
        async get(key) {
            const r = route(key);
            if (!r) return local.get(key);
            return allowed(r[0]) ? storeFor(r[0]).get(r[1]) : null;
        },
        async set(key, value) {
            const r = route(key);
            if (!r) return local.set(key, value);
            if (!allowed(r[0])) throw refuse(r[0]);
            return storeFor(r[0]).set(r[1], value);
        },
        async del(key) {
            const r = route(key);
            if (!r) return local.del(key);
            if (allowed(r[0])) return storeFor(r[0]).del(r[1]);
        },
        async keys(prefix) {
            const r = route(prefix);
            if (!r) return local.keys(prefix);
            if (!allowed(r[0])) return [];
            const base = prefix.slice(0, prefix.length - r[1].length);
            return (await storeFor(r[0]).keys(r[1])).map((p) => base + p);
        },
        async getBlob(key) {
            const r = route(key);
            return r && allowed(r[0]) ? storeFor(r[0]).getBlob(r[1]) : null;
        },
        async setBlob(key, blob) {
            const r = route(key);
            if (!r) throw new Error("[fb.desktopApps] bytes are kept for installed apps only");
            if (!allowed(r[0])) throw refuse(r[0]);
            return storeFor(r[0]).setBlob(r[1], blob);
        },
        async delBlob(key) {
            const r = route(key);
            if (r && allowed(r[0])) return storeFor(r[0]).delBlob(r[1]);
        },
    };

    /** Bytes an app keeps in its folder (for the remove dialog). */
    async function dataSize(app, ws = state.ws) {
        const api = fb.api.files.in(ws);
        const folder = app.dataFolder || `Apps/${nameOf(app)}`;
        let bytes = 0, count = 0;
        const walk = async (path) => {
            let listing;
            try { listing = await api.list(path); } catch { return; }
            for (const e of listing.entries) {
                if (e.kind === "folder") await walk(e.path);
                else { bytes += e.size || 0; count++; }
            }
        };
        await walk(folder);
        return { bytes, count, folder };
    }

    function formatBytes(n) {
        return fb.format.bytes(n);
    }

    /* ------------------------------------------------------- dialogs -- */

    const el = (tag, cls, text) => {
        const e = document.createElement(tag);
        if (cls) e.className = cls;
        if (text != null) e.textContent = text;
        return e;
    };

    function dialog(title, width, buttons) {
        const dlg = document.createElement("sac-dialog");
        dlg.setAttribute("title", title);
        dlg.style.setProperty("--dialog-width", width);
        dlg.buttons = buttons;
        dlg.addEventListener("sac:action", () => setTimeout(() => dlg.remove(), 120), { once: true });
        return dlg;
    }

    function show(dlg) {
        document.body.appendChild(dlg);
        setTimeout(() => dlg.open(), 0);
        return new Promise((resolve) => dlg.addEventListener("sac:action", (e) => resolve(e.detail.action), { once: true }));
    }

    // A refusal in the page's language (fb.errors), else the server's text.
    const errorText = (err) => fb.errors.text(err, err?.message || String(err));

    /** Step 1: the URL. Resolves with the inspected manifest, or null. */
    async function askUrl() {
        const dlg = dialog(fb.t("fb.apps.install-title", "Install an app"), "520px", [
            { action: "cancel", label: fb.t("fb.common.cancel", "Cancel"), kind: "default" },
            { action: "read", label: fb.t("fb.apps.read", "Read manifest"), kind: "primary" },
        ]);
        const panel = el("div", "fb-app-dialog");
        const input = el("input");
        input.type = "url";
        input.id = "fb-app-url";
        input.placeholder = "https://github.com/owner/repo";
        input.setAttribute("aria-label", fb.t("fb.apps.url", "App URL"));
        const error = el("p", "fb-app-error");
        error.hidden = true;
        panel.append(
            el("p", null, fb.t("fb.apps.url-intro", "Paste the app's repository URL — or its app.json, if it lives somewhere else.")),
            input,
            error,
            el("p", "fb-app-hint", fb.t("fb.apps.url-hint", "Nothing runs yet: Fishbowl reads the manifest and shows you what the app is and what it asks for.")),
        );
        dlg.appendChild(panel);
        let inspected = null;
        dlg.beforeAction = async (action) => {
            if (action !== "read") return true;
            error.hidden = true;
            try {
                inspected = await sac.apps.inspect(input.value);
            } catch (err) {
                error.textContent = String(err?.message || err).replace(/^\[sac\.apps\]\s*/, "");
                error.hidden = false;
                return false;
            }
            if (byId(inspected.id)) {
                error.textContent = fb.t("fb.apps.already", "{name} is already on this desktop.", { name: inspected.name });
                error.hidden = false;
                return false;
            }
            if (!inspected.entryIntegrity) {
                error.textContent = fb.t("fb.apps.no-pin", "Fishbowl couldn't read the app's code to pin it (its server must allow cross-origin reads).");
                error.hidden = false;
                return false;
            }
            return true;
        };
        input.addEventListener("keydown", (e) => { if (e.key === "Enter") { e.preventDefault(); dlg.trigger("read"); } });
        setTimeout(() => input.focus(), 50);
        const action = await show(dlg);
        return action === "read" ? inspected : null;
    }

    /**
     * Step 2 — review: install (a fresh manifest), update (the installed app
     * + its new manifest) or permissions (the installed app). Resolves with
     * { mode, granted } or null.
     */
    async function review({ kind, manifest, app = null }) {
        const inSpace = state.ws !== "personal";
        const name = manifest.name || app?.id;
        const title = kind === "update" ? fb.t("fb.apps.update-title", "Update {name} to v{version}?", { name, version: manifest.version || "?" })
            : kind === "perms" ? fb.t("fb.apps.perms-title", "{name} — permissions", { name })
            : fb.t("fb.apps.review-title", "Install {name}?", { name });
        const dlg = dialog(title, "560px", [
            { action: "cancel", label: fb.t("fb.common.cancel", "Cancel"), kind: "default" },
            { action: "ok", label: kind === "update" ? fb.t("fb.apps.update", "Update")
                : kind === "perms" ? fb.t("fb.common.save", "Save") : fb.t("fb.apps.install", "Install"), kind: "primary" },
        ]);
        dlg.id = "fb-app-review";
        const r = el("div", "fb-app-dialog");
        if (manifest.description) r.appendChild(el("p", null, manifest.description));

        const dl = el("dl");
        const row = (k, v) => {
            dl.append(el("dt", null, k));
            const dd = el("dd");
            if (v instanceof Node) dd.append(v); else dd.textContent = v;
            dl.append(dd);
        };
        row(fb.t("fb.apps.origin", "Origin"), manifest.origin || app?.origin || "");
        row(fb.t("fb.apps.version", "Version"), kind === "update" ? `${app?.version || "?"} → ${manifest.version || "?"}` : (manifest.version || app?.version || "—"));
        const pin = kind === "perms" ? app?.entryIntegrity : manifest.entryIntegrity;
        if (pin) row(fb.t("fb.apps.pin", "Code pin"), el("code", null, pin.length > 34 ? pin.slice(0, 32) + "…" : pin));
        row(fb.t("fb.apps.into", "Installs into"), inSpace
            ? fb.t("fb.apps.into-space", "This space — every member sees it")
            : fb.t("fb.apps.into-personal", "Your personal desktop"));
        r.appendChild(dl);

        const mode = "sandboxed";
        const grantsBox = el("div");
        r.appendChild(grantsBox);
        const current = app ? new Set(app.granted || []) : new Set();
        let files = kind === "install" ? asks(manifest, "files") : current.has("files");
        let identity = kind === "install" ? "none" : identityOf(app);   // never raised for you
        const oldConnect = new Set(Array.isArray(app?.manifest?.connect) ? app.manifest.connect : []);
        const connect = Array.isArray(manifest.connect) ? manifest.connect : [];
        // Where its data lives — by name, so an update that renames it moves on.
        const folder = kind === "perms" ? app?.dataFolder || dataFolderFor(name, app?.id)
            : dataFolderFor(manifest.name, manifest.id || app?.id);
        const oldFolder = kind === "update" ? app?.dataFolder || null : null;

        function paintGrants() {
            grantsBox.replaceChildren();
            grantsBox.append(el("h4", null, fb.t("fb.apps.asks", "It asks for")));
            const grid = el("div", "fb-app-grants");
            if (asks(manifest, "files")) {
                const lab = el("label", "fb-check");
                const box = el("input");
                box.type = "checkbox";
                box.id = "fb-app-files";
                box.checked = files;
                box.addEventListener("change", () => { files = box.checked; });
                lab.append(box, el("span", null, fb.t("fb.apps.files", "Files — its own folder, and what you pick")));
                grid.append(lab, el("code", null, oldFolder && oldFolder !== folder ? `${oldFolder} → ${folder}` : folder));
            }
            if (asks(manifest, ID)) {
                const sel = el("span", "select");
                const s = el("select");
                s.id = "fb-app-identity";
                s.setAttribute("aria-label", fb.t("fb.apps.who", "Who you are"));
                for (const [v, t] of [
                    ["none", fb.t("fb.apps.who-none", "Nothing")],
                    ...(ANON ? [["pseudonymous", fb.t("fb.apps.who-anon", "A stable anonymous id")]] : []),
                    ["full", fb.t("fb.apps.who-full", "Your name and picture")],
                ]) {
                    const o = el("option", null, t);
                    o.value = v;
                    o.selected = v === identity;
                    s.append(o);
                }
                s.addEventListener("change", () => { identity = s.value; });
                sel.append(s);
                grid.append(el("span", null, fb.t("fb.apps.who", "Who you are")), sel);
            }
            const talks = el("div", "fb-app-connect");
            if (connect.length) {
                for (const o of connect) {
                    const line = el("div");
                    line.append(el("code", null, hostOf(o)));
                    if (kind === "update" && !oldConnect.has(o)) line.append(" ", el("span", "fb-app-new", fb.t("fb.apps.new", "new")));
                    talks.append(line);
                }
            } else {
                talks.append(el("span", "fb-app-hint", fb.t("fb.apps.talks-none", "Nobody — no network at all")));
            }
            grid.append(el("span", null, fb.t("fb.apps.talks", "Talks to")), talks);
            grantsBox.append(grid, el("p", "fb-app-hint",
                kind === "update" ? fb.t("fb.apps.update-hint", "Nothing changes until you update; its data stays.")
                    : fb.t("fb.apps.blocked-hint", "Everything else is blocked: no other network, no other files, no notes. You can change this later under Settings → Apps.")));
        }
        paintGrants();
        dlg.appendChild(r);

        const action = await show(dlg);
        if (action !== "ok") return null;
        const granted = [];
        if (files && asks(manifest, "files")) granted.push("files");
        if (asks(manifest, ID) && identity === "full") granted.push(ID);
        if (ANON && asks(manifest, ID) && identity === "pseudonymous") granted.push(ID_ANON);
        return { mode, granted };
    }

    /* -------------------------------------------------------- actions -- */

    function changed() {
        window.dispatchEvent(new CustomEvent("fb:apps-changed"));
        fb.desktop?.syncCommands();
    }

    async function install() {
        if (!state.canInstall) return;
        const inspected = await askUrl();
        if (!inspected) return;
        const choice = await review({ kind: "install", manifest: inspected });
        if (!choice) return;
        try {
            await fb.api.desktop.install({
                manifestUrl: inspected.manifestUrl,
                manifest: clean(inspected),
                integrity: inspected.entryIntegrity,
                mode: "sandboxed",
                granted: choice.granted,
            });
            sac.toast?.(fb.t("fb.apps.installed", "{name} is on your desktop.", { name: inspected.name }), { kind: "success" });
            changed();
        } catch (err) {
            sac.dialog.info({ title: fb.t("fb.apps.install-failed", "Couldn't install {name}", { name: inspected.name }), message: errorText(err) });
        }
    }

    async function update(id) {
        const app = byId(id);
        const fresh = updateFor(id);
        if (!app || !fresh) return;
        const choice = await review({ kind: "update", manifest: fresh, app });
        if (!choice) return;
        try {
            await fb.api.desktop.updateApp(id, {
                manifestUrl: app.manifestUrl,
                manifest: clean(fresh),
                integrity: fresh.entryIntegrity,
                granted: choice.granted,
            });
            updates.delete(`${state.ws}|${id}`);
            sac.toast?.(fb.t("fb.apps.updated", "{name} is up to date.", { name: nameOf(app) }), { kind: "success" });
            changed();
        } catch (err) {
            sac.dialog.info({ title: fb.t("fb.apps.update-failed", "Couldn't update {name}", { name: nameOf(app) }), message: errorText(err) });
        }
    }

    async function permissions(id) {
        const app = byId(id);
        if (!app || app.mode !== "sandboxed") return;
        const choice = await review({ kind: "perms", manifest: Object.assign({ origin: app.origin }, app.manifest), app });
        if (!choice) return;
        try {
            await fb.api.desktop.updateApp(id, { granted: choice.granted });
            changed();
        } catch (err) {
            sac.dialog.info({ title: fb.t("fb.apps.perms-failed", "Couldn't change {name}", { name: nameOf(app) }), message: errorText(err) });
        }
    }

    async function remove(id) {
        const app = byId(id);
        if (!app) return;
        const size = await dataSize(app);
        const message = [fb.t("fb.apps.remove-leaves", "It leaves this desktop. The app itself stays at {host}; installing it again is one paste.", { host: hostOf(app.origin) })];
        if (size.count) {
            message.push(fb.t("fb.apps.remove-data", "It keeps {size} in Files → {folder}. Kept by default, so reinstalling brings it back — or delete it now, which moves it to the trash.",
                { size: formatBytes(size.bytes), folder: size.folder }));
        }
        const buttons = [{ action: "cancel", label: fb.t("fb.common.cancel", "Cancel"), kind: "default" }];
        if (size.count) {
            buttons.push({ action: "purge", label: fb.t("fb.apps.delete-data", "Delete data"), kind: "destructive" });
            buttons.push({ action: "keep", label: fb.t("fb.apps.keep-data", "Keep data"), kind: "primary" });
        } else {
            buttons.push({ action: "keep", label: fb.t("fb.apps.remove", "Remove"), kind: "destructive" });
        }
        const action = await sac.dialog.confirm({ title: fb.t("fb.apps.remove-title", "Remove {name}?", { name: nameOf(app) }), message, buttons });
        if (action !== "keep" && action !== "purge") return;
        try {
            // The server first: a refusal leaves the app as it was.
            await fb.api.desktop.removeApp(id, { purgeData: action === "purge" });
            try { sac.apps.remove(id); } catch { /* not open */ }
            registered.delete(id);
            changed();
        } catch (err) {
            sac.dialog.info({ title: fb.t("fb.apps.remove-failed", "Couldn't remove {name}", { name: nameOf(app) }), message: errorText(err) });
        }
    }

    /* ---------------------------------------------------------- boot --- */

    function boot() {
        if (!window.sac?.apps) return;
        sac.apps.frameUrl = (manifest) => frameUrlOf(manifest);
        if (window.sac.fs) sac.fs.backend = backend;
        fb.api.me.get().then((me) => {
            user = me;
            sac.identity?.use?.({
                get: () => (user ? { id: user.id, name: user.name || "", avatar: user.avatarUrl || null } : null),
            });
        }).catch(() => { user = null; });
    }

    const style = document.createElement("style");
    style.textContent = `
        .fb-app-dialog p { margin: 0 0 10px; white-space: normal; }
        .fb-app-dialog input[type=url] { width: 100%; box-sizing: border-box; margin: 0 0 10px; }
        .fb-app-dialog dl { display: grid; grid-template-columns: max-content 1fr; gap: 6px 14px; margin: 0 0 14px; font-size: 0.9rem; }
        .fb-app-dialog dt { color: var(--text-muted); }
        .fb-app-dialog dd { margin: 0; overflow-wrap: anywhere; }
        .fb-app-dialog code { font-family: var(--font-mono); font-size: 0.8rem; }
        .fb-app-dialog h4 { margin: 14px 0 6px; font-size: 0.75rem; letter-spacing: 0.05em; text-transform: uppercase; color: var(--text-muted); }
        .fb-app-modes { display: grid; gap: 8px; }
        .fb-app-grants { display: grid; grid-template-columns: 1fr auto; align-items: center; gap: 10px 12px; margin-bottom: 12px; }
        .fb-app-connect { display: grid; gap: 4px; justify-items: end; }
        .fb-app-new { color: var(--accent-warm); font-weight: 600; }
        .fb-app-hint { color: var(--text-muted); font-size: 0.8rem; }
        .fb-app-error { color: var(--danger); font-size: 0.85rem; }
    `;
    document.head.appendChild(style);

    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", boot);
    else boot();

    fb.desktopApps = {
        sync, open, install, update, permissions, remove,
        get state() { return state; },
        updateFor, frameUrlOf, dataSize, formatBytes, identityOf,
        get byId() { return byId; },
    };
})();
