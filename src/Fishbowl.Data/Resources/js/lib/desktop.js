/**
 * Fishbowl — the desktop's app registry, the Ctrl/⌘K palette's commands,
 * and the small "open this view and do that" hand-off between them.
 *
 * One registry of built-in apps drives the desktop's tiles (#/, fb-hub-view),
 * the palette's Apps group and — through the routes the views register —
 * the burger. Admin apps exist only for admins, and only in the personal
 * workspace; nothing is listed that doesn't work.
 *
 * The arrangement (size, colour, order, hidden) is per workspace and lives
 * on the server (fb.api.desktop), so it follows the user across devices.
 * Tiles keep their stored position; a tile never arranged sorts by its
 * registry index, a workspace's own and installed apps right after the
 * full-screen apps. Only Notes, Todos,
 * Calendar and Contacts start medium; every other tile — built-in or installed,
 * in any workspace — starts small, and they come after those four, so they share
 * medium cells four at a time (the kit's .tile-pack).
 *
 * Two kinds of app: pages (Notes, Todos, Calendar, Files, Tables — a route,
 * in the burger) and window apps (`open`: Messages, Trash, Apps, Spaces,
 * API keys, Secrets, Users, System — fb.windowApps, no route).
 *
 * Palette groups (sac.commands). Routes still register with palette: false
 * — not because the palette would leave a space (it scopes route hashes
 * since kit 2.18) but because Apps already lists every app with the
 * workspace's rules: hidden tiles, admin apps only in the personal
 * workspace, Secrets never in a space.
 *   Apps      every visible tile, in desktop order (a window app opens)
 *   Create    new note / todo / event / contact — fb.desktop.go() into the
 *             view (the live tiles' "+" does the same)
 *   Go        the desktop, and the Your data window
 *   Workspace Personal and every space
 *   Notes     full-text search in the active workspace's notes (a
 *             sac.commands source over the hybrid search), title + the
 *             matching words; picking one opens the note
 */
(function () {
    const BUILTINS = [
        { key: "builtin:notes",    hash: "#/notes",       name: "Notes",    icon: "note",     desc: "Write freely. Find anything.", size: "medium" },
        { key: "builtin:todos",    hash: "#/todos",       name: "Todos",    icon: "check",    desc: "Fast to-dos, always at hand.", size: "medium" },
        { key: "builtin:calendar", hash: "#/calendar",    name: "Calendar", icon: "calendar", desc: "Events and reminders, yours.", size: "medium" },
        { key: "builtin:contacts", hash: "#/contacts",    name: "Contacts", icon: "contacts", desc: "People and organisations.", size: "medium" },
        { key: "builtin:mail",     hash: "#/mail",        name: "Mail",     icon: "mail",     desc: "Every mailbox in one list.", size: "medium" },
        { key: "builtin:files",    hash: "#/files",       name: "Files",    icon: "folder",   desc: "Real files, two panes, any workspace." },
        { key: "builtin:tables",   hash: "#/tables",      name: "Tables",   icon: "grid",     desc: "This space's own tables, like a spreadsheet.", space: true },
        // Window apps (fb.windowApps): `open`, no address.
        { key: "builtin:messages", route: "messages", open: () => fb.windowApps.open("messages"), name: "Notifications", icon: "fb-bell",  desc: "What Fishbowl has to tell you." },
        { key: "builtin:trash",    route: "trash", open: () => fb.windowApps.open("trash"),    name: "Trash",    icon: "trash", desc: "Deleted things, back with one click." },
        { key: "builtin:apps",     route: "apps", open: () => fb.windowApps.open("apps"),     name: "Apps",     icon: "cube",  desc: "The apps installed on this desktop." },
        { key: "builtin:spaces",   route: "spaces", open: () => fb.windowApps.open("spaces"),   name: "Spaces",   icon: "layers", desc: "Shared workspaces and who is in them.", personal: true },
        { key: "builtin:keys",     route: "keys", open: () => fb.windowApps.open("keys"),     name: "API keys", icon: "key",   desc: "Tokens for agents and scripts.", personal: true },
        { key: "builtin:secrets",  route: "secrets", open: () => fb.windowApps.open("secrets"),  name: "Secrets",  icon: "lock",  desc: "The ways in to your secret vault.", personal: true },
        { key: "builtin:users",    route: "admin/users", open: () => fb.windowApps.open("users"),    name: "Users",    icon: "users", desc: "Approve and manage accounts.", admin: true, personal: true },
        { key: "builtin:system",   route: "admin/system", open: () => fb.windowApps.open("system"),   name: "System",   icon: "settings", desc: "How this Fishbowl is doing and how it runs.", admin: true, personal: true },
    ];

    const SETTINGS = [
        { hash: "#/",        label: "Desktop",  icon: "home" },
    ];

    const SIZES = ["small", "medium", "wide", "large"];

    // Names in the current language: "builtin:notes" → fb.app.notes.name /
    // .desc, a Go entry's hash → fb.go.<path>. English is the registry's.
    const short = (key) => key.replace(/^builtin:/, "");
    const nameOf = (a) => fb.t(`fb.app.${short(a.key)}.name`, a.name);
    const descOf = (a) => fb.t(`fb.app.${short(a.key)}.desc`, a.desc);
    const goLabel = (s) => fb.t(`fb.go.${s.hash === "#/" ? "desktop" : s.hash.replace(/^#\//, "")}`, s.label);

    let mePromise = null;
    const me = () => (mePromise ||= fb.api.me.get().catch(() => null));

    const inSpace = () => sac.scope.get().type === "scoped";

    /** The built-ins this user sees in the active workspace, registry order. */
    async function builtins() {
        const user = await me();
        return BUILTINS.filter((a) => (!a.admin || user?.isAdmin) && (!a.personal || !inSpace()) && (!a.space || inSpace()))
            .map((a) => ({ ...a, name: nameOf(a), desc: descOf(a) }));
    }

    /** Where an app lives from the active workspace. */
    function hrefOf(app) {
        if (!app.hash) return null;   // a window app (`open`)
        return app.personal ? app.hash : sac.scope.hashFor(app.hash);
    }

    /**
     * The workspace's desktop: every built-in and every installed app
     * (fb.desktopApps; key "app:<id>", `app` = the install record) merged
     * with its stored tile, sorted by position. Hidden tiles are included
     * (flagged) so the desktop can offer them back.
     * { entries, canArrange, canInstall }.
     */
    async function load() {
        const [apps, stored] = await Promise.all([
            builtins(),
            fb.api.desktop.get().catch(() => ({ tiles: [], apps: [], canArrange: false })),
        ]);
        const byKey = new Map((stored.tiles || []).map((t) => [t.key, t]));
        const arranged = (entry, i) => {
            const t = byKey.get(entry.key) || {};
            return {
                ...entry,
                position: typeof t.position === "number" ? t.position : i + 1,
                size: SIZES.includes(t.size) ? t.size : (entry.size || "small"),
                defaultSize: entry.size || "small",
                color: t.color && fb.tags.SLOTS.includes(t.color) ? t.color : null,
                hidden: !!t.hidden,
            };
        };
        const entries = apps.map((app, i) => arranged({ ...app, href: hrefOf(app) }, i));
        // Until arranged, a workspace's own and installed apps come right
        // after the full-screen apps (Notes … Files, Tables) and before the
        // window apps (Messages, Trash, Apps, …): positions between the two.
        fb.desktopApps?.sync(stored);
        const extra = stored.apps || [];
        const lastPage = apps.reduce((last, a, i) => (a.hash ? i + 1 : last), 0);
        extra.forEach((a, j) => entries.push(arranged({
            key: `app:${a.id}`,
            app: a,
            name: a.manifest?.name || a.id,
            // A kit icon name, or the default (the server drops anything else).
            icon: /^[a-z0-9-]{1,40}$/.test(a.manifest?.icon || "") ? a.manifest.icon : "cube",
            desc: a.manifest?.description || "",
            href: null,
            // A space app with a tile.js has something to show: medium.
            size: a.tile ? "medium" : undefined,
        }, lastPage - 1 + (j + 1) / (extra.length + 1))));
        entries.sort((a, b) => a.position - b.position);
        return {
            entries,
            canArrange: !!stored.canArrange,
            canInstall: !!stored.canInstall,
        };
    }

    /** Store one tile's arrangement (the whole row) and resync the palette. */
    async function save(entry) {
        // A size is stored only when it differs from the app's default, so
        // a later default (or a colour change today) never freezes it.
        const size = entry.size === (entry.defaultSize || "small") ? null : entry.size;
        await fb.api.desktop.putTile(entry.key, { ...entry, size });
        syncCommands();
    }

    /* ------------------------------------------------------ hand-off -- */

    // "Open the notes view and create one" — the view may not be mounted
    // yet. The intent waits here until the view takes it: on connect
    // (after its first load) or, when it is already on screen, on the
    // fb:intent event.
    let pending = null;

    // `ws` ("personal" | "space:<slug>") sends the hand-off to another
    // workspace — the personal Calendar opening a space's event there.
    function go(view, action, id, ws) {
        pending = { view, action, id: id ?? null };
        const elsewhere = ws && ws !== fb.api.workspace();
        const hash = !elsewhere ? sac.scope.hashFor(`#/${view}`)
            : ws === "personal" ? `#/${view}` : `#/space/${ws.slice(6)}/${view}`;
        if (!elsewhere && sac.router.currentResource() === `#/${view}`) {
            window.dispatchEvent(new CustomEvent("fb:intent", { detail: { view } }));
        } else {
            sac.router.navigate(hash);
        }
    }

    /** The pending intent for `view`, once — or null. */
    function takeIntent(view) {
        if (!pending || pending.view !== view) return null;
        const it = pending;
        pending = null;
        return it;
    }

    /* ------------------------------------------------------ palette ---- */

    const PREFIX = "fb:desk:";
    let wanted = new Map();
    let spaces = [];

    function register(id, cmd) { wanted.set(PREFIX + id, { id: PREFIX + id, ...cmd }); }

    async function syncCommands() {
        if (!window.sac?.commands) return;
        wanted = new Map();
        const space = inSpace() ? sac.scope.get().slug : null;

        let entries = [];
        try { entries = (await load()).entries; } catch { entries = []; }
        // The burger lists what the desktop shows (shell.js renders the
        // window apps; the pages are the kit's own route list).
        window.dispatchEvent(new CustomEvent("fb:desktop-entries", { detail: { entries: entries.filter((x) => !x.hidden) } }));
        for (const e of entries.filter((x) => !x.hidden)) {
            register(`app:${e.key}`, {
                label: e.name, icon: e.icon, group: fb.t("fb.palette.apps", "Apps"),
                run: e.app ? () => fb.desktopApps.open(e.app.id) : e.open ? () => e.open() : () => sac.router.navigate(e.href),
            });
        }
        // A space Reader can't create anything there.
        const writable = await (fb.access?.canWrite?.() ?? true);
        if (writable && entries.some((e) => e.key === "builtin:notes")) {
            register("create:note", { label: fb.t("fb.palette.new-note", "New note"), icon: "plus", group: fb.t("fb.palette.create", "Create"), run: () => go("notes", "create") });
        }
        if (writable && entries.some((e) => e.key === "builtin:todos")) {
            register("create:todo", { label: fb.t("fb.palette.new-todo", "New todo"), icon: "plus", group: fb.t("fb.palette.create", "Create"), run: () => go("todos", "create") });
        }
        if (writable && entries.some((e) => e.key === "builtin:calendar")) {
            register("create:event", { label: fb.t("fb.palette.new-event", "New event"), icon: "plus", group: fb.t("fb.palette.create", "Create"), run: () => go("calendar", "create") });
        }
        if (writable && entries.some((e) => e.key === "builtin:contacts")) {
            register("create:contact", { label: fb.t("fb.palette.new-contact", "New contact"), icon: "plus", group: fb.t("fb.palette.create", "Create"), run: () => go("contacts", "create") });
        }
        if (writable && entries.some((e) => e.key === "builtin:mail")) {
            register("create:mail", { label: fb.t("fb.palette.new-mail", "New mail"), icon: "plus", group: fb.t("fb.palette.create", "Create"), run: () => go("mail", "create") });
        }
        for (const s of SETTINGS.filter((x) => !x.personal || !space)) {
            register(`go:${s.hash}`, {
                label: goLabel(s), icon: s.icon, group: fb.t("fb.palette.go", "Go"),
                run: () => sac.router.navigate(sac.scope.hashFor(s.hash)),
            });
        }
        register("go:your-data", {
            label: fb.t("fb.shell.your-data", "Your data…"), icon: "download", group: fb.t("fb.palette.go", "Go"),
            run: () => fb.yourData.open(),
        });
        if (space) {
            register("ws:personal", {
                label: fb.t("fb.palette.switch-personal", "Switch to Personal"), icon: "user", group: fb.t("fb.palette.workspace", "Workspace"),
                run: () => sac.scope.set({ type: "root" }),
            });
        }
        for (const s of spaces.filter((x) => x.slug !== space)) {
            register(`ws:${s.slug}`, {
                label: fb.t("fb.palette.switch-to", "Switch to {name}", { name: s.name }), icon: "users", group: fb.t("fb.palette.workspace", "Workspace"),
                run: () => sac.scope.set({ type: "scoped", slug: s.slug }),
            });
        }
        for (const c of sac.commands.list()) {
            if (c.id.startsWith(PREFIX) && !wanted.has(c.id)) sac.commands.unregister(c.id);
        }
        wanted.forEach((c) => sac.commands.register(c));
    }

    // Notes: full-text over the active workspace's hybrid search (same
    // endpoint as the MCP search_memory tool), asked as you type. The
    // server never holds secret plaintext; markers become a lock here.
    function registerNoteSearch() {
        if (!window.sac?.commands?.registerSource) return;
        sac.commands.registerSource({
            id: "fb-notes",
            group: () => fb.t("fb.palette.notes", "Notes"),
            minLength: 2,
            debounce: 200,
            async search(q, { signal }) {
                const res = await fb.api.search.query(q, { limit: 8 });
                if (signal?.aborted) return [];
                return (res?.notes || []).map((n) => ({
                    id: `fb-note:${n.id}`,
                    label: (n.title || "").trim() || fb.t("fb.palette.untitled", "Untitled note"),
                    icon: n.archived ? "archive" : "note",
                    hint: hintFor(n, q),
                    run: () => go("notes", "open", n.id),
                }));
            },
        });
    }

    /** A few plain words from the note around the first query term —
     *  what makes a full-text hit recognisable. Never secret content:
     *  markers (the only form the server has) show as a lock. */
    function hintFor(note, q) {
        const title = (note.title || "").trim();
        let text = String(note.content || "")
            .replace(/:{2,3}secret#\d+:{2,3}end/gi, "🔒")
            .replace(/\[secret content hidden\]/g, "🔒")   // SecretStripper's placeholder
            .replace(/^[ \t]*(```|~~~)[^\n]*$/gm, " ")
            .replace(/^[ \t]{0,3}#{1,6}[ \t]+/gm, "")
            .replace(/^[ \t]*(?:>|[-*+]|\d+[.)])[ \t]+(?:\[[ xX]\][ \t]+)?/gm, "")
            .replace(/!?\[([^\]]*)\]\([^)]*\)/g, "$1")
            .replace(/[`*_~|]+/g, " ")
            .replace(/\s+/g, " ")
            .trim();
        if (title && text.startsWith(title)) text = text.slice(title.length).trim();
        if (!text) return "";
        const words = q.toLowerCase().split(/[^\p{L}\p{N}]+/u).filter(Boolean);
        const lower = text.toLowerCase();
        const at = words.map((w) => lower.indexOf(w)).filter((i) => i >= 0).sort((a, b) => a - b)[0] ?? 0;
        // Start a few words before the match, on a word boundary.
        let from = Math.max(0, at - 30);
        if (from > 0) from = text.indexOf(" ", from) + 1 || from;
        const cut = text.slice(from, from + 70);
        return (from > 0 ? "…" : "") + cut + (from + 70 < text.length ? "…" : "");
    }

    async function loadSpaces() {
        try { spaces = (await fb.api.spaces.list()) || []; } catch { spaces = []; }
        syncCommands();
    }

    window.addEventListener("sac:scope-changed", () => syncCommands());
    window.addEventListener("fb:spaces-changed", loadSpaces);
    window.addEventListener("DOMContentLoaded", () => {
        registerNoteSearch();
        loadSpaces();
    });

    // The burger lists what the desktop shows: the window apps are router
    // entries that open (kit 2.25), registered after the pages so they follow
    // them like on the desktop, each only where it exists (scope) and the
    // admin ones only for an admin. Hidden tiles stay listed: hiding is about
    // the desktop's room.
    me().then((user) => {
        for (const a of BUILTINS) {
            if (!a.open || (a.admin && !user?.isAdmin)) continue;
            sac.router.register(`#/${a.route}`, null, {
                label: a.name, icon: a.icon, palette: false,
                scope: a.personal ? "root" : a.space ? "scoped" : "any",
                open: () => a.open(),
            });
        }
    });

    fb.desktop = { SIZES, builtins, load, save, go, takeIntent, syncCommands };
})();
