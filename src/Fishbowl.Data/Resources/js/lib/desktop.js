/**
 * Fishbowl — the desktop's app registry, the Ctrl/⌘K palette's commands,
 * and the small "open this view and do that" hand-off between them.
 *
 * One registry of built-in apps drives the desktop grid (#/, fb-hub-view),
 * the palette's Apps group and — through the routes the views register —
 * the burger. Admin apps exist only for admins, and only in the personal
 * workspace; nothing is listed that doesn't work.
 *
 * The arrangement (size, colour, order, hidden) is per workspace and lives
 * on the server (fb.api.desktop), so it follows the user across devices.
 * Positions are REAL like todos: a drag writes one tile, the midpoint of
 * its new neighbours; a tile never arranged sorts by its registry index.
 *
 * Palette groups (sac.commands; routes register with palette: false, since
 * the palette would navigate them out of a space):
 *   Apps      every visible tile, in desktop order
 *   Create    new note / todo / event — fb.desktop.go() into the view
 *   Go        the settings pages
 *   Workspace Personal and every space
 *   Notes     note titles of the active workspace (the palette filters
 *             labels; it has no async provider for full-text search)
 */
(function () {
    const BUILTINS = [
        { key: "builtin:notes",    hash: "#/notes",       name: "Notes",    icon: "note",     desc: "Write freely. Find anything." },
        { key: "builtin:todos",    hash: "#/todos",       name: "Todos",    icon: "check",    desc: "Fast to-dos, always at hand." },
        { key: "builtin:calendar", hash: "#/calendar",    name: "Calendar", icon: "calendar", desc: "Events and reminders, yours." },
        { key: "builtin:files",    hash: "#/files",       name: "Files",    icon: "folder",   desc: "Real files, two panes, any workspace." },
        { key: "builtin:messages", hash: "#/messages",    name: "Messages", icon: "mail",     desc: "What Fishbowl has to tell you." },
        { key: "builtin:users",    hash: "#/admin/users", name: "Users",    icon: "users",    desc: "Approve and manage accounts.", admin: true, personal: true },
    ];

    const SETTINGS = [
        { hash: "#/",        label: "Desktop",  icon: "home" },
        { hash: "#/spaces",  label: "Spaces",   icon: "users" },
        { hash: "#/keys",    label: "API keys", icon: "key" },
        { hash: "#/secrets", label: "Secrets",  icon: "lock", personal: true },
    ];

    const SIZES = ["medium", "wide", "large"];

    let mePromise = null;
    const me = () => (mePromise ||= fb.api.me.get().catch(() => null));

    const inSpace = () => sac.scope.get().type === "scoped";

    /** The built-ins this user sees in the active workspace, registry order. */
    async function builtins() {
        const user = await me();
        return BUILTINS.filter((a) => (!a.admin || user?.isAdmin) && (!a.personal || !inSpace()));
    }

    /** Where an app lives from the active workspace. */
    function hrefOf(app) {
        return app.personal ? app.hash : sac.scope.hashFor(app.hash);
    }

    /**
     * The workspace's desktop: every built-in merged with its stored tile,
     * sorted by position. Hidden tiles are included (flagged) so the
     * desktop can offer them back. { entries, canArrange }.
     */
    async function load() {
        const [apps, stored] = await Promise.all([
            builtins(),
            fb.api.desktop.get().catch(() => ({ tiles: [], canArrange: false })),
        ]);
        const byKey = new Map((stored.tiles || []).map((t) => [t.key, t]));
        const entries = apps.map((app, i) => {
            const t = byKey.get(app.key) || {};
            return {
                ...app,
                href: hrefOf(app),
                position: typeof t.position === "number" ? t.position : i + 1,
                size: SIZES.includes(t.size) ? t.size : "medium",
                color: t.color && fb.tags.SLOTS.includes(t.color) ? t.color : null,
                hidden: !!t.hidden,
            };
        });
        entries.sort((a, b) => a.position - b.position);
        return { entries, canArrange: !!stored.canArrange };
    }

    /** Store one tile's arrangement (the whole row) and resync the palette. */
    async function save(entry) {
        await fb.api.desktop.putTile(entry.key, entry);
        syncCommands();
    }

    /* ------------------------------------------------------ hand-off -- */

    // "Open the notes view and create one" — the view may not be mounted
    // yet. The intent waits here until the view takes it: on connect
    // (after its first load) or, when it is already on screen, on the
    // fb:intent event.
    let pending = null;

    function go(view, action, id) {
        pending = { view, action, id: id ?? null };
        const hash = sac.scope.hashFor(`#/${view}`);
        if (sac.router.currentResource() === `#/${view}`) {
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
    let noteTitles = [];

    function register(id, cmd) { wanted.set(PREFIX + id, { id: PREFIX + id, ...cmd }); }

    async function syncCommands() {
        if (!window.sac?.commands) return;
        wanted = new Map();
        const space = inSpace() ? sac.scope.get().slug : null;

        let entries = [];
        try { entries = (await load()).entries; } catch { entries = []; }
        for (const e of entries.filter((x) => !x.hidden)) {
            register(`app:${e.key}`, {
                label: e.name, icon: e.icon, group: "Apps",
                run: () => sac.router.navigate(e.href),
            });
        }
        if (entries.some((e) => e.key === "builtin:notes")) {
            register("create:note", { label: "New note", icon: "plus", group: "Create", run: () => go("notes", "create") });
        }
        if (entries.some((e) => e.key === "builtin:todos")) {
            register("create:todo", { label: "New todo", icon: "plus", group: "Create", run: () => go("todos", "create") });
        }
        if (entries.some((e) => e.key === "builtin:calendar")) {
            register("create:event", { label: "New event", icon: "plus", group: "Create", run: () => go("calendar", "create") });
        }
        for (const s of SETTINGS.filter((x) => !x.personal || !space)) {
            register(`go:${s.hash}`, {
                label: s.label, icon: s.icon, group: "Go",
                run: () => sac.router.navigate(sac.scope.hashFor(s.hash)),
            });
        }
        if (space) {
            register("ws:personal", {
                label: "Switch to Personal", icon: "user", group: "Workspace",
                run: () => sac.scope.set({ type: "root" }),
            });
        }
        for (const s of spaces.filter((x) => x.slug !== space)) {
            register(`ws:${s.slug}`, {
                label: `Switch to ${s.name}`, icon: "users", group: "Workspace",
                run: () => sac.scope.set({ type: "scoped", slug: s.slug }),
            });
        }
        noteTitles.forEach((n) => register(`note:${n.id}`, {
            label: n.title, icon: "note", group: "Notes",
            run: () => go("notes", "open", n.id),
        }));

        for (const c of sac.commands.list()) {
            if (c.id.startsWith(PREFIX) && !wanted.has(c.id)) sac.commands.unregister(c.id);
        }
        wanted.forEach((c) => sac.commands.register(c));
    }

    // Note titles of the active workspace, newest first. Refreshed on a
    // workspace switch and — throttled — on navigation, so a note written a
    // minute ago is findable by the time the palette opens again.
    let notesAt = 0;
    async function refreshNotes(force) {
        if (!force && Date.now() - notesAt < 15_000) return;
        notesAt = Date.now();
        try {
            const notes = await fb.api.notes.list();
            noteTitles = (notes || [])
                .slice()
                .sort((a, b) => String(b.updatedAt || "").localeCompare(String(a.updatedAt || "")))
                .slice(0, 300)
                .map((n) => ({ id: n.id, title: (n.title || "").trim() || "Untitled note" }));
        } catch {
            noteTitles = [];
        }
        syncCommands();
    }

    async function loadSpaces() {
        try { spaces = (await fb.api.spaces.list()) || []; } catch { spaces = []; }
        syncCommands();
    }

    window.addEventListener("sac:scope-changed", () => { refreshNotes(true); });
    window.addEventListener("fb:spaces-changed", loadSpaces);
    window.addEventListener("hashchange", () => refreshNotes(false));
    window.addEventListener("DOMContentLoaded", () => {
        loadSpaces();
        refreshNotes(true);
    });

    fb.desktop = { SIZES, builtins, load, save, go, takeIntent, syncCommands, refreshNotes };
})();
