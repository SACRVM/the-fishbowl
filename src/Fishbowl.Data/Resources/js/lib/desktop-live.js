/**
 * Fishbowl — live tiles (fb.desktopLive): what a desktop tile shows instead
 * of its description once it is medium, wide or large. A small tile stays
 * icon-only; an app without a provider (Trash, Apps, Spaces, API keys,
 * Secrets, Users, System, installed apps) stays a plain tile.
 *
 * One model for every provider, one renderer for all of them, so the live
 * tiles look alike — no big numbers. The tile's header (a darker band, drawn
 * by fb-hub-view) holds the icon, the app's name as the heading and the
 * status under it; the list follows on the tile's lighter ground:
 *
 *   { icon, main, sub, large, items: [{ lead, text, tail }], empty }
 *
 *   icon    a different icon name for the tile (Calendar: today's day number)
 *   main    status line 1, muted — "12 open", "3 notes"
 *   sub     status line 2, muted: a string, or a list of strings joined by
 *           " · ". Optional — and what tells the list apart when line 1
 *           doesn't ("1 birthday in 30 days"). (No colour appears on one
 *           tile only: every status and tail is muted, the leads are the accent.)
 *   large   line 1 is the clock (Calendar): tabular numbers
 *   items   rows (up to 30) — lead (accent, aligned), text (one line,
 *           ellipsised), tail (muted). Absent = the app has no list. The
 *           list runs to the tile's bottom edge and fades out there.
 *   empty   the line shown when `items` is an empty list
 *
 * A provider is { load(ws) → data, present(data, now) → model, clock? }.
 * `load` is the only part that talks to the server (always the workspace the
 * desktop shows — fb.api.workspace(), through the .in(ws) calls); `present`
 * is pure over the loaded data, so the Calendar's clock and its "today" /
 * "tomorrow" tails repaint every minute without another request (`clock`).
 * Dates are compared as date strings (YYYY-MM-DD) — a birthday or an all-day
 * event is the same day in every zone, never shifted through UTC.
 *
 * Per-browser privacy switch: a tile's "Show content" (its "⋯" menu) is a list
 * of "<workspace>|<tile key>" in localStorage `fb.desk.quiet`. Switched off,
 * the tile is the plain tile and its provider isn't even asked.
 *
 * The desktop view (fb-hub-view) owns refreshing and painting; this file
 * owns what the content is.
 */
(function () {
    const t = (key, fallback, vars) => fb.t(key, fallback, vars);
    const count = (n, one, many, key) => (n === 1 ? t(key + "-1", one) : t(key, many, { n }));

    const QUIET_KEY = "fb.desk.quiet";
    // Rows a provider hands over: enough for a large tile to fill. How many a
    // tile shows is its room (the list is clipped and fades out at the bottom).
    const MAX_ITEMS = 30;
    const DAY = 86400000;

    // The Calendar tile's icon is the kit's calendar frame with today's day
    // number in its body (a tear-off calendar). The tile's own <sac-icon>
    // keeps the kit's size, accent and hover; only its name changes (the
    // clock switches it when the day does).
    const dayIcon = (n) => `fb-calendar-day-${n}`;
    if (window.sac?.icons) {
        for (let n = 1; n <= 31; n++) {
            sac.icons.register(dayIcon(n),
                '<rect x="3" y="4" width="18" height="18" rx="2"/><line x1="16" y1="2" x2="16" y2="6"/>' +
                '<line x1="8" y1="2" x2="8" y2="6"/><line x1="3" y1="10" x2="21" y2="10"/>' +
                `<text x="12" y="19.4" text-anchor="middle" font-size="9.5" font-weight="700" fill="currentColor" stroke="none">${n}</text>`);
        }
    }

    // ---------------------------------------------------------------- dates

    const pad = (n) => String(n).padStart(2, "0");
    /** The local calendar day of a Date as "YYYY-MM-DD". */
    const dayKey = (d) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
    const parts = (key) => key.split("-").map(Number);
    /** A key moved by n days — pure date math, never through the local clock. */
    const addDays = (key, n) => {
        const [y, m, d] = parts(key);
        return new Date(Date.UTC(y, m - 1, d + n)).toISOString().slice(0, 10);
    };
    /** Whole days from key a to key b. */
    const daysBetween = (a, b) => {
        const [ay, am, ad] = parts(a), [by, bm, bd] = parts(b);
        return Math.round((Date.UTC(by, bm - 1, bd) - Date.UTC(ay, am - 1, ad)) / DAY);
    };
    /** A key as a local midnight — only to hand it to fb.format's day names. */
    const localDate = (key) => { const [y, m, d] = parts(key); return new Date(y, m - 1, d); };

    /** "today" / "tomorrow" / a short weekday + day-month, for a day key. */
    function dayTail(key, today) {
        const diff = daysBetween(today, key);
        if (diff === 0) return t("fb.desk.live.today", "today");
        if (diff === 1) return t("fb.desk.live.tomorrow", "tomorrow");
        const d = localDate(key);
        return `${fb.format.weekday(d)} ${fb.format.dayMonth(d)}`;
    }

    /** "just now" · "5 min" · "2 h" · "yesterday" · the day-month. */
    function ago(iso, now) {
        const d = new Date(iso);
        if (isNaN(d)) return "";
        const ms = now - d;
        if (ms < 60000) return t("fb.desk.live.just-now", "just now");
        if (ms < 3600000) return t("fb.desk.live.minutes-ago", "{n} min", { n: Math.floor(ms / 60000) });
        const today = dayKey(now), then = dayKey(d);
        if (then === today) return t("fb.desk.live.hours-ago", "{n} h", { n: Math.floor(ms / 3600000) });
        if (then === addDays(today, -1)) return t("fb.desk.live.yesterday", "yesterday");
        return d.getFullYear() === now.getFullYear() ? fb.format.dayMonth(d) : fb.format.date(d);
    }

    // ------------------------------------------------------------ providers

    /** "today" · "yesterday" · the day-month (with the year in another year). */
    function dayAgo(iso, now) {
        const d = new Date(iso);
        if (isNaN(d)) return "";
        const today = dayKey(now), then = dayKey(d);
        if (then === today) return t("fb.desk.live.today", "today");
        if (then === addDays(today, -1)) return t("fb.desk.live.yesterday", "yesterday");
        return d.getFullYear() === now.getFullYear() ? fb.format.dayMonth(d) : fb.format.date(d);
    }

    /**
     * The files that changed last, newest first — from the end of the files
     * journal (the same feed the Files app syncs from): ask for the head
     * ("<epoch>:<seq>"), then for what came after a cursor a little before it,
     * and fold those entries into the files that exist now. A cursor the
     * server can't serve (410 cursor_expired — the journal was compacted past
     * it) falls back to a shorter look back, and in the end to nothing: a
     * tile never fails over it. Hidden entries (a dot in any segment: .apps,
     * .trash, the apps' storage files) are never listed; a file deleted or
     * trashed later is gone.
     */
    async function recentFiles(api) {
        let head;
        try { head = await api.changes(); } catch { return []; }
        const cursor = String(head?.cursor || "");
        const sep = cursor.lastIndexOf(":");
        const top = Number(cursor.slice(sep + 1));
        if (sep <= 0 || !Number.isFinite(top) || top <= 0) return [];
        for (const back of [200, 60, 20, 5]) {
            let page;
            try { page = await api.changes(`${cursor.slice(0, sep)}:${Math.max(0, top - back)}`, 500); }
            catch (err) {
                if (err?.status === 410) continue;       // expired: look back less
                return [];
            }
            const files = new Map();                     // path → { name, at }
            for (const c of page?.changes || []) {
                if (c.kind !== "file") continue;
                if (c.op === "delete" || c.trashed) { files.delete(c.path); continue; }
                if (c.op === "move" && c.oldPath) files.delete(c.oldPath);
                files.set(c.path, { path: c.path, at: c.at });
            }
            return [...files.values()]
                .filter((f) => !f.path.split("/").some((seg) => seg.startsWith(".")))
                .sort((a, b) => new Date(b.at) - new Date(a.at))
                .map((f) => ({ name: f.path.slice(f.path.lastIndexOf("/") + 1), at: f.at }));
        }
        return [];
    }

    // A note's title is stored in plain text and never holds a secret (the
    // notes view derives it around the blocks); a stored marker or an open
    // block in it would be a lock all the same — the same grammar as the
    // notes view's list.
    const SECRET_MARKER = /:{2,3}secret#\d+:{2,3}end/gi;
    const noteTitle = (note) => {
        const raw = String(note.title || "").trim();
        const safe = fb.api.secrets.replace(raw, () => "🔒").replace(SECRET_MARKER, "🔒").trim();
        return safe || t("fb.notes.untitled", "Untitled");
    };

    const providers = {
        "builtin:calendar": {
            clock: true,
            // From this morning, so an event that is still ahead at load time
            // but over by the next tick is simply dropped by `present`; eight
            // days, because all-day rows are matched with a day of slack.
            async load(ws) {
                const n = new Date();
                const from = new Date(n.getFullYear(), n.getMonth(), n.getDate());
                const to = new Date(n.getFullYear(), n.getMonth(), n.getDate() + 8);
                const list = await fb.api.events.in(ws).list({ from, to });
                return Array.isArray(list) ? list : [];
            },
            present(events, now) {
                const today = dayKey(now);
                const end7 = addDays(today, 7);            // exclusive
                const rows = [];
                for (const e of events) {
                    let day, lead, order;
                    if (e.allDay && e.startDate) {
                        // Placed by its date strings; end is exclusive.
                        const end = e.endDate || addDays(e.startDate, 1);
                        if (end <= today || e.startDate >= end7) continue;
                        day = e.startDate > today ? e.startDate : today;
                        lead = t("fb.desk.live.all-day", "All day");
                        order = [0, 0];
                    } else {
                        const s = new Date(e.startAt);
                        if (isNaN(s)) continue;
                        const end = e.endAt ? new Date(e.endAt) : s;
                        if (end < now) continue;           // over
                        const sd = dayKey(s);
                        if (sd >= end7) continue;
                        day = sd > today ? sd : today;     // still running from an earlier day
                        lead = e.allDay ? t("fb.desk.live.all-day", "All day") : fb.format.time(s);
                        order = [e.allDay ? 0 : 1, s.getTime()];
                    }
                    rows.push({ day, order, id: e.id, lead, text: (e.title || "").trim() || t("fb.calendar.untitled", "Untitled") });
                }
                rows.sort((a, b) => (a.day < b.day ? -1 : a.day > b.day ? 1 : 0)
                    || a.order[0] - b.order[0] || a.order[1] - b.order[1]);
                return {
                    icon: dayIcon(now.getDate()),
                    main: fb.format.time(now),
                    large: true,
                    sub: fb.format.longDate(now),
                    items: rows.slice(0, MAX_ITEMS).map((r) => ({ lead: r.lead, text: r.text, tail: dayTail(r.day, today) })),
                    empty: t("fb.desk.live.nothing-planned", "Nothing planned"),
                };
            },
        },

        "builtin:todos": {
            // The server's default is the open ones.
            load: (ws) => fb.api.todos.in(ws).list().then((l) => (Array.isArray(l) ? l : [])),
            present(todos, now) {
                const today = dayKey(now);
                const open = todos.filter((x) => !x.completedAt);
                // The list's own order: position, then creation.
                const order = (a, b) => (a.position ?? Infinity) - (b.position ?? Infinity)
                    || new Date(a.createdAt || 0) - new Date(b.createdAt || 0)
                    || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0);
                const late = (x) => !!x.dueAt && new Date(x.dueAt) < now;
                const rank = (x) => (late(x) ? 0 : x.dueAt && dayKey(new Date(x.dueAt)) === today ? 1 : 2);
                const sorted = [...open].sort((a, b) => rank(a) - rank(b) || order(a, b));
                const overdue = open.filter(late).length;
                const dueToday = open.filter((x) => rank(x) === 1).length;
                const sub = [];
                if (overdue) sub.push(t("fb.desk.live.overdue", "{n} overdue", { n: overdue }));
                if (dueToday) sub.push(t("fb.desk.live.due-today", "{n} today", { n: dueToday }));
                return {
                    main: t("fb.desk.live.n-open", "{n} open", { n: open.length }),
                    sub,
                    items: sorted.slice(0, MAX_ITEMS).map((x) => {
                        let tail = "";
                        if (x.dueAt) {
                            const due = new Date(x.dueAt);
                            const diff = daysBetween(today, dayKey(due));
                            tail = diff === 0 ? t("fb.desk.live.today", "today")
                                : diff === 1 ? t("fb.desk.live.tomorrow", "tomorrow")
                                : fb.format.dayMonth(due);
                        }
                        return { text: (x.title || "").trim() || t("fb.todos.untitled", "Untitled"), tail };
                    }),
                    empty: t("fb.desk.live.all-done", "All done"),
                };
            },
        },

        "builtin:notes": {
            // `prompt: false`: never asks for the vault; only the title is used.
            load: (ws) => fb.api.notes.in(ws).list(undefined, { prompt: false }).then((l) => (Array.isArray(l) ? l : [])),
            present(notes, now) {
                const live = notes.filter((n) => !n.archived);
                const review = live.filter((n) => (n.tags || []).includes("review:pending")).length;
                const recent = [...live].sort((a, b) => new Date(b.updatedAt || 0) - new Date(a.updatedAt || 0));
                return {
                    main: count(live.length, "1 note", "{n} notes", "fb.desk.live.n-notes"),
                    sub: review ? t("fb.desk.live.to-review", "{n} to review", { n: review }) : "",
                    items: recent.slice(0, MAX_ITEMS).map((n) => ({ text: noteTitle(n), tail: ago(n.updatedAt, now) })),
                    // No empty line: the status already says "0 notes".
                };
            },
        },

        "builtin:contacts": {
            load: (ws) => fb.api.contacts.in(ws).list().then((l) => (Array.isArray(l) ? l : [])),
            present(contacts, now) {
                const today = dayKey(now);
                const [ty, tm, td] = parts(today);
                const next = [];
                for (const c of contacts) {
                    if (!/^\d{4}-\d{2}-\d{2}$/.test(c.birthday || "")) continue;
                    const [, m, d] = parts(c.birthday);
                    // The next time that day comes round (a 29 February in a
                    // common year is 1 March) — compared as dates.
                    let at = new Date(Date.UTC(ty, m - 1, d));
                    if (at < Date.UTC(ty, tm - 1, td)) at = new Date(Date.UTC(ty + 1, m - 1, d));
                    const key = at.toISOString().slice(0, 10);
                    const diff = daysBetween(today, key);
                    if (diff <= 30) next.push({ key, diff, name: (c.name || "").trim() });
                }
                next.sort((a, b) => a.diff - b.diff || a.name.localeCompare(b.name));
                return {
                    main: count(contacts.length, "1 contact", "{n} contacts", "fb.desk.live.n-contacts"),
                    // Line 2 says what the list is; no contacts, no line (and
                    // no empty line: "0 contacts" says it).
                    sub: !contacts.length ? ""
                        : next.length ? count(next.length, "1 birthday in 30 days", "{n} birthdays in 30 days", "fb.desk.live.n-birthdays")
                        : t("fb.desk.live.no-birthdays", "No birthdays in 30 days"),
                    items: next.slice(0, MAX_ITEMS).map((b) => ({
                        lead: fb.format.dayMonth(localDate(b.key)),
                        text: b.name,
                        tail: b.diff === 0 ? t("fb.desk.live.today", "today") : count(b.diff, "in 1 day", "in {n} days", "fb.desk.live.in-days"),
                    })),
                };
            },
        },

        "builtin:files": {
            async load(ws) {
                const api = fb.api.files.in(ws);
                const [usage, recent] = await Promise.all([api.usage(), recentFiles(api).catch(() => [])]);
                return { usage, recent };
            },
            present({ usage, recent }, now) {
                const used = Number(usage?.bytes) || 0;
                const quota = Number(usage?.quotaBytes) || 0;
                const size = fb.format.bytes(used);
                return {
                    main: quota > 0 ? `${size} ${t("fb.desk.live.of-quota", "of {quota}", { quota: fb.format.bytes(quota) })}` : size,
                    // The lead stays empty: the name and when, nothing else.
                    items: recent.slice(0, MAX_ITEMS).map((f) => ({ text: f.name, tail: dayAgo(f.at, now) })),
                };
            },
        },

        "builtin:messages": {
            async load() {
                const [count, list] = await Promise.all([fb.api.messages.unreadCount(), fb.api.messages.list()]);
                return { unread: Number(count?.unread) || 0, messages: Array.isArray(list?.items) ? list.items : [] };
            },
            present({ unread, messages }, now) {
                // Unread first, then newest; the line is the one the Messages window shows.
                const sorted = [...messages].sort((a, b) => (a.readAt ? 1 : 0) - (b.readAt ? 1 : 0)
                    || new Date(b.createdAt) - new Date(a.createdAt));
                return {
                    main: unread > 0 ? t("fb.desk.live.n-unread", "{n} unread", { n: unread }) : t("fb.desk.live.no-messages", "No new messages"),
                    items: sorted.map((m) => ({ text: fb.accounts.messageText(m).text, tail: ago(m.createdAt, now) }))
                        .filter((row) => row.text).slice(0, MAX_ITEMS),
                };
            },
        },
    };

    // ------------------------------------------------------------- renderer

    const el = (tag, cls, text) => {
        const node = document.createElement(tag);
        if (cls) node.className = cls;
        if (text) node.textContent = text;
        return node;
    };

    /** The `.tile-live` block for a model at a size ("medium" | "wide" | "large"):
     *  the header beside the tile's icon (the app's name as the heading, the
     *  status under it), then the list. */
    function render(model, size, name) {
        const root = el("div", `tile-live live-${size}`);

        const status = el("div", "tl-status");
        status.append(el("h2", "", name), el("div", model.large ? "tl-main clock" : "tl-main", model.main));
        const sub = (Array.isArray(model.sub) ? model.sub : [model.sub]).filter(Boolean);
        if (sub.length) status.append(el("div", "tl-sub", sub.join(" · ")));
        root.append(status);

        if (Array.isArray(model.items)) {
            if (model.items.length) {
                const list = el("ul", "tl-items");
                for (const item of model.items) {
                    const row = el("li");
                    const text = el("span", "tl-text", item.text);
                    text.title = item.text;
                    row.append(el("span", "tl-lead", item.lead || ""), text, el("span", "tl-tail", item.tail || ""));
                    list.append(row);
                }
                root.append(list);
            } else if (model.empty) {
                root.append(el("div", "tl-empty", model.empty));
            }
        }
        return root;
    }

    // ------------------------------------------------- the privacy switch

    let memoryQuiet = null;   // when localStorage is blocked, this session only
    const quietList = () => {
        if (memoryQuiet) return memoryQuiet;
        try {
            const v = JSON.parse(localStorage.getItem(QUIET_KEY) || "[]");
            return Array.isArray(v) ? v.filter((x) => typeof x === "string") : [];
        } catch { return []; }
    };
    const quietId = (ws, key) => `${ws}|${key}`;
    const isQuiet = (ws, key) => quietList().includes(quietId(ws, key));
    function setQuiet(ws, key, quiet) {
        const id = quietId(ws, key);
        const list = quietList().filter((x) => x !== id);
        if (quiet) list.push(id);
        try {
            if (list.length) localStorage.setItem(QUIET_KEY, JSON.stringify(list));
            else localStorage.removeItem(QUIET_KEY);
            memoryQuiet = null;
        } catch { memoryQuiet = list; }
    }

    // ---------------------------------------------------------------- load

    // What the last desktop of each workspace loaded, so coming back to the
    // desktop paints the tiles at once (then refreshes) instead of flashing
    // the plain tile first. In memory only; never used for a switched-off tile.
    const cache = new Map();

    /** Ask the providers of `keys` for workspace `ws`, in parallel. Resolves a
     *  Map of the ones that answered — a provider that fails is simply absent. */
    async function load(ws, keys) {
        const out = new Map();
        await Promise.all(keys.filter((k) => providers[k]).map(async (k) => {
            try { out.set(k, await providers[k].load(ws)); }
            catch (err) { console.warn(`[fb.desktopLive] ${k} failed:`, err?.status || err?.message || err); }
        }));
        const kept = cache.get(ws) || new Map();
        for (const k of keys) { if (out.has(k)) kept.set(k, out.get(k)); else kept.delete(k); }
        cache.set(ws, kept);
        return out;
    }

    fb.desktopLive = {
        has: (key) => !!providers[key],
        /** Does this tile repaint every minute without a request? */
        clock: (key) => !!providers[key]?.clock,
        load,
        cached: (ws) => new Map(cache.get(ws) || []),
        present: (key, data, now = new Date()) => providers[key].present(data, now),
        render,
        isQuiet,
        setQuiet,
    };
})();
