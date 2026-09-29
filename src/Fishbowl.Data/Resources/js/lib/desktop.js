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
 * Tiles keep their stored position (the desktop has no reordering UI);
 * a tile never arranged sorts by its registry index.
 *
 * Palette groups (sac.commands). Routes still register with palette: false
 * — not because the palette would leave a space (it scopes route hashes
 * since kit 2.18) but because Apps and Go below already list them with the
 * workspace's rules: hidden tiles, admin apps only in the personal
 * workspace, Secrets never in a space. A "Views" group would repeat them
 * and offer pages that don't work where you are.
 *   Apps      every visible tile, in desktop order
 *   Create    new note / todo / event — fb.desktop.go() into the view
 *   Go        the settings pages
 *   Workspace Personal and every space
 *   Notes     full-text search in the active workspace's notes (a
 *             sac.commands source over the hybrid search), title + the
 *             matching words; picking one opens the note
 */
(function () {
    const BUILTINS = [
        { key: "builtin:notes",    hash: "#/notes",       name: "Notes",    icon: "note",     desc: "Write freely. Find anything." },
        { key: "builtin:todos",    hash: "#/todos",       name: "Todos",    icon: "check",    desc: "Fast to-dos, always at hand." },
        { key: "builtin:calendar", hash: "#/calendar",    name: "Calendar", icon: "calendar", desc: "Events and reminders, yours." },
        { key: "builtin:files",    hash: "#/files",       name: "Files",    icon: "folder",   desc: "Real files, two panes, any workspace." },
        { key: "builtin:messages", hash: "#/messages",    name: "Messages", icon: "mail",     desc: "What Fishbowl has to tell you." },
        { key: "builtin:trash",    hash: "#/trash",       name: "Trash",    icon: "trash",    desc: "Deleted things, back with one click." },
        { key: "builtin:users",    hash: "#/admin/users", name: "Users",    icon: "users",    desc: "Approve and manage accounts.", admin: true, personal: true },
        { key: "builtin:settings", hash: "#/admin/settings", name: "System settings", icon: "settings", desc: "How this Fishbowl runs.", admin: true, personal: true },
        { key: "builtin:system",   hash: "#/admin/system", name: "System", icon: "info", desc: "Version, data sizes, archives.", admin: true, personal: true },
    ];

    const SETTINGS = [
        { hash: "#/",        label: "Desktop",  icon: "home" },
        { hash: "#/spaces",  label: "Spaces",   icon: "users" },
        { hash: "#/apps",    label: "Apps",     icon: "grid" },
        { hash: "#/keys",    label: "API keys", icon: "key" },
        { hash: "#/secrets", label: "Secrets",  icon: "lock", personal: true },
    ];

    const SIZES = ["medium", "wide", "large"];

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
        return BUILTINS.filter((a) => (!a.admin || user?.isAdmin) && (!a.personal || !inSpace()))
            .map((a) => ({ ...a, name: nameOf(a), desc: descOf(a) }));
    }

    /** Where an app lives from the active workspace. */
    function hrefOf(app) {
        return app.personal ? app.hash : sac.scope.hashFor(app.hash);
    }

    /**
     * The workspace's desktop: every built-in and every installed app
     * (fb.desktopApps; key "app:<id>", `app` = the install record) merged
     * with its stored tile, sorted by position. Hidden tiles are included
     * (flagged) so the desktop can offer them back.
     * { entries, canArrange, canInstall, canTrust }.
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
                size: SIZES.includes(t.size) ? t.size : "medium",
                color: t.color && fb.tags.SLOTS.includes(t.color) ? t.color : null,
                hidden: !!t.hidden,
            };
        };
        const entries = apps.map((app, i) => arranged({ ...app, href: hrefOf(app) }, i));
        // Installed apps follow the built-ins until arranged.
        fb.desktopApps?.sync(stored);
        (stored.apps || []).forEach((a, j) => entries.push(arranged({
            key: `app:${a.id}`,
            app: a,
            name: a.manifest?.name || a.id,
            icon: a.manifest?.icon || "cube",
            desc: a.manifest?.description || "",
            href: null,
        }, apps.length + j)));
        entries.sort((a, b) => a.position - b.position);
        return {
            entries,
            canArrange: !!stored.canArrange,
            canInstall: !!stored.canInstall,
            canTrust: !!stored.canTrust,
        };
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

    function register(id, cmd) { wanted.set(PREFIX + id, { id: PREFIX + id, ...cmd }); }

    async function syncCommands() {
        if (!window.sac?.commands) return;
        wanted = new Map();
        const space = inSpace() ? sac.scope.get().slug : null;

        let entries = [];
        try { entries = (await load()).entries; } catch { entries = []; }
        for (const e of entries.filter((x) => !x.hidden)) {
            register(`app:${e.key}`, {
                label: e.name, icon: e.icon, group: fb.t("fb.palette.apps", "Apps"),
                run: e.app ? () => fb.desktopApps.open(e.app.id) : () => sac.router.navigate(e.href),
            });
        }
        if (entries.some((e) => e.key === "builtin:notes")) {
            register("create:note", { label: fb.t("fb.palette.new-note", "New note"), icon: "plus", group: fb.t("fb.palette.create", "Create"), run: () => go("notes", "create") });
        }
        if (entries.some((e) => e.key === "builtin:todos")) {
            register("create:todo", { label: fb.t("fb.palette.new-todo", "New todo"), icon: "plus", group: fb.t("fb.palette.create", "Create"), run: () => go("todos", "create") });
        }
        if (entries.some((e) => e.key === "builtin:calendar")) {
            register("create:event", { label: fb.t("fb.palette.new-event", "New event"), icon: "plus", group: fb.t("fb.palette.create", "Create"), run: () => go("calendar", "create") });
        }
        for (const s of SETTINGS.filter((x) => !x.personal || !space)) {
            register(`go:${s.hash}`, {
                label: goLabel(s), icon: s.icon, group: fb.t("fb.palette.go", "Go"),
                run: () => sac.router.navigate(sac.scope.hashFor(s.hash)),
            });
        }
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

    fb.desktop = { SIZES, builtins, load, save, go, takeIntent, syncCommands };
})();
