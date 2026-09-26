/**
 * <fb-hub-view>  (mounted at #/) — the desktop.
 *
 * The workspace's apps as SQUARE tiles on the kit's <sac-launcher>: medium is
 * one square, wide two side by side, large two by two (Windows-Phone style),
 * packed densely so mixed sizes leave no holes. The column count follows the
 * desktop's own width (a container query), and the row height is computed
 * from it, so a medium tile is always exactly square — two columns on a
 * phone, up to six on a wide screen. Never a tile for something that doesn't
 * work: the tiles come from fb.desktop's registry (js/lib/desktop.js) as
 * launcher link tiles (setLinks), space-aware hrefs included.
 *
 * The layout is the SERVER's (sacrvm-appkit #25): the launcher runs with
 * persist="none" — it never touches localStorage — and is fed the stored
 * arrangement through its `layout` setter on every refresh (entry, workspace
 * switch), so nothing leaks between workspaces. A drag, the Edit mode's
 * move / hide / show controls, and our own tile menu (size, colour, hide —
 * the kit leaves size and colour to the host) all end in _persist(), which
 * writes the tiles that changed through fb.api.desktop. Hidden tiles come
 * back through the launcher's Edit mode, where they show grayed with a Show
 * control. A space member who may not arrange gets `readonly`: the same
 * arrangement, no Edit, no drag, no menus.
 *
 * Badges are the host's, from existing APIs, through setBadge(): open / due
 * todos, the next event, unread messages. Refreshed on entry, on a workspace
 * switch and when messages change. `no-add`: no kit "Add app" tile —
 * installing apps goes through the desktop's own (sandboxed) install flow.
 */
class FbHubView extends HTMLElement {
    connectedCallback() {
        this._entries = [];
        this._canArrange = false;
        this.render();
        this.refresh();
        this._onContext = () => this.refresh();
        this._onMessages = () => this._messageBadge();
        window.addEventListener("sac:scope-changed", this._onContext);
        window.addEventListener("fb:messages-changed", this._onMessages);
        this._loadVersion();
    }

    disconnectedCallback() {
        window.removeEventListener("sac:scope-changed", this._onContext);
        window.removeEventListener("fb:messages-changed", this._onMessages);
    }

    async _loadVersion() {
        try {
            const v = await fb.api.version();
            fb.version = v?.version ?? null;
        } catch {
            fb.version = null;
        }
        const footer = this.querySelector("sac-footer");
        if (footer && fb.version) footer.setAttribute("version", fb.version);
    }

    render() {
        this.innerHTML = `
            <style>
                /* The desktop measures itself: the columns follow its width,
                   the rows are computed from the columns — squares. */
                fb-hub-view .fb-desk { container: fb-desk / inline-size; }
                fb-hub-view sac-launcher > .grid {
                    --desk-cols: 6;
                    --desk-gap: 12px;
                    grid-template-columns: repeat(var(--desk-cols), minmax(0, 1fr));
                    grid-auto-rows: calc((100cqi - (var(--desk-cols) - 1) * var(--desk-gap)) / var(--desk-cols));
                    grid-auto-flow: dense;
                    gap: var(--desk-gap);
                }
                @container fb-desk (max-width: 1079px) { fb-hub-view sac-launcher > .grid { --desk-cols: 5; } }
                @container fb-desk (max-width: 859px)  { fb-hub-view sac-launcher > .grid { --desk-cols: 4; } }
                @container fb-desk (max-width: 639px)  { fb-hub-view sac-launcher > .grid { --desk-cols: 3; } }
                @container fb-desk (max-width: 439px)  { fb-hub-view sac-launcher > .grid { --desk-cols: 2; --desk-gap: 10px; } }

                /* Footprints never collapse: with two columns at least, a
                   wide tile is two squares and a large one two by two — on
                   a phone too (the kit collapses them to medium there). */
                fb-hub-view sac-launcher .sac-launcher-cell.size-wide  { grid-column: span 2; grid-row: span 1; }
                fb-hub-view sac-launcher .sac-launcher-cell.size-large { grid-column: span 2; grid-row: span 2; }

                /* Quiet tiles: flat, no lift, no glow, no spinning icon —
                   the border takes the accent on hover, that's all. Icon top
                   left, title and badge at the bottom; a column even on a
                   narrow phone (ui.css turns tiles into rows there). */
                fb-hub-view sac-launcher .tile {
                    flex-direction: column;
                    align-items: stretch;
                    justify-content: flex-end;
                    gap: 0;
                    padding: 14px;
                    border-color: var(--border);
                    backdrop-filter: none;
                    -webkit-backdrop-filter: none;
                    transition: border-color 0.15s, background 0.15s;
                }
                fb-hub-view sac-launcher .tile:hover {
                    transform: translateZ(0);
                    box-shadow: none;
                    border-color: var(--accent);
                }
                fb-hub-view sac-launcher .tile > sac-icon { --icon-size: 28px; margin-bottom: auto; }
                fb-hub-view sac-launcher .tile:hover > sac-icon { transform: none; }
                fb-hub-view sac-launcher .sac-launcher-tile-body { min-width: 0; }
                fb-hub-view sac-launcher .tile h2 {
                    font-size: 1rem;
                    margin: 0;
                    white-space: nowrap;
                    overflow: hidden;
                    text-overflow: ellipsis;
                }
                fb-hub-view sac-launcher .tile p {
                    margin-top: 2px;
                    white-space: nowrap;
                    overflow: hidden;
                    text-overflow: ellipsis;
                }
                /* A medium square has no room for a sentence. */
                fb-hub-view sac-launcher .sac-launcher-cell:not(.size-wide):not(.size-large) .sac-launcher-tile-body p { display: none; }

                /* The badge sits under the title, a quiet accent pill. */
                fb-hub-view sac-launcher .tile-badge {
                    position: static;
                    order: 3;
                    align-self: flex-start;
                    max-width: calc(100% - 32px);
                    margin-top: 6px;
                    padding: 1px 6px;
                    border: 0;
                    border-radius: var(--radius-m);
                    background: var(--accent-tint);
                    color: var(--accent-text);
                    font-size: 0.75rem;
                    font-weight: 600;
                    letter-spacing: 0;
                    text-transform: none;
                    white-space: nowrap;
                    overflow: hidden;
                    text-overflow: ellipsis;
                }

                /* The tile menu stays in the bottom-right corner, never
                   rides a lift; on a pointer it shows on hover or focus. */
                fb-hub-view sac-launcher .sac-launcher-menu { top: auto; bottom: 8px; right: 8px; transform: none; }
                fb-hub-view sac-launcher:not([edit]) .sac-launcher-cell:has(> .sac-launcher-tile:hover) > .sac-launcher-menu { transform: none; }
                fb-hub-view sac-launcher .sac-launcher-menu-btn { border-color: transparent; background: none; }
                @media (hover: hover) {
                    fb-hub-view sac-launcher .sac-launcher-menu-btn { opacity: 0; transition: opacity 0.15s; }
                    fb-hub-view sac-launcher .sac-launcher-cell:hover .sac-launcher-menu-btn,
                    fb-hub-view sac-launcher .sac-launcher-menu-btn:focus-visible,
                    fb-hub-view sac-launcher .sac-launcher-menu[open] .sac-launcher-menu-btn { opacity: 1; }
                }
                fb-hub-view sac-launcher .sac-launcher-controls { top: 8px; right: 8px; }

            </style>
            <div class="orb"></div>
            <div class="hub-container">
                <header>
                    <h1>THE FISHBOWL</h1>
                    <p class="intro-text">Your memory lives here. You don't.</p>
                </header>
                <div class="fb-desk">
                    <sac-launcher persist="none" no-add drag="always" aria-label="Apps"></sac-launcher>
                </div>
            </div>
            <sac-footer brand="THE FISHBOWL"></sac-footer>
        `;
        if (fb.version) this.querySelector("sac-footer").setAttribute("version", fb.version);
        this._launcher = this.querySelector("sac-launcher");
        // A drag, a move button, hide / show in Edit mode — the launcher
        // reports the whole layout; what changed goes to the server.
        this._launcher.addEventListener("sac:layout", (e) => this._persist(e.detail.layout));
    }

    async refresh() {
        let loaded;
        try { loaded = await fb.desktop.load(); }
        catch (err) {
            console.warn("[fb-hub-view] desktop load failed:", err?.message || err);
            return;
        }
        if (!this.isConnected) return;
        this._entries = loaded.entries;
        this._canArrange = loaded.canArrange;

        const l = this._launcher;
        l.toggleAttribute("readonly", !this._canArrange);
        if (!this._canArrange) l.removeAttribute("edit");
        l.setLinks(this._entries.map((e) => ({
            id: e.key, name: e.name, icon: e.icon, description: e.desc, href: e.href,
        })));
        l.layout = this._layoutOf(this._entries);
        for (const e of this._entries) l.setMenu(e.key, this._canArrange ? this._menu(e) : null);
        this._badges();
    }

    /** Our stored tiles as the launcher's layout object. */
    _layoutOf(entries) {
        const sizes = {}, colors = {};
        for (const e of entries) {
            if (e.size && e.size !== "medium") sizes[e.key] = e.size;
            if (e.color) colors[e.key] = e.color;
        }
        return {
            order: entries.map((e) => e.key),
            hidden: entries.filter((e) => e.hidden).map((e) => e.key),
            sizes, colors, custom: [],
        };
    }

    /**
     * Write the tiles a layout changed: position is the tile's index in the
     * effective order, size / colour / hidden as the layout says.
     */
    async _persist(layout) {
        const hidden = new Set(layout.hidden || []);
        const order = [...(layout.order || [])];
        // Keys the launcher's order doesn't list (none today) keep their place at the end.
        for (const e of this._entries) if (!order.includes(e.key)) order.push(e.key);
        const changed = [];
        order.forEach((key, i) => {
            const e = this._entries.find((x) => x.key === key);
            if (!e) return;
            const next = {
                position: i + 1,
                size: layout.sizes?.[key] || "medium",
                color: layout.colors?.[key] || null,
                hidden: hidden.has(key),
            };
            if (e.position !== next.position || e.size !== next.size || e.color !== next.color || e.hidden !== next.hidden) {
                Object.assign(e, next);
                changed.push(e);
            }
        });
        this._entries.sort((a, b) => a.position - b.position);
        if (!changed.length) return;
        try {
            await Promise.all(changed.map((e) => fb.desktop.save(e)));
        } catch (err) {
            console.warn("[fb-hub-view] tile save failed:", err?.message || err);
            window.sac?.toast?.("Couldn't save the desktop.", { kind: "error" });
            this.refresh();   // back to what the server holds
        }
    }

    /** Apply a change through the launcher's layout, then store it. */
    _change(mutate) {
        const layout = this._launcher.layout;
        mutate(layout);
        this._launcher.layout = layout;   // the setter never fires sac:layout
        this._persist(layout);
        for (const e of this._entries) this._launcher.setMenu(e.key, this._menu(e));
    }

    /** The tile's menu: size, colour, hide. A check marks the current size. */
    _menu(e) {
        const setSize = (size) => this._change((l) => {
            if (size === "medium") delete l.sizes[e.key];
            else l.sizes[e.key] = size;
        });
        const sizes = [["medium", "Medium"], ["wide", "Wide"], ["large", "Large"]];
        return [
            ...sizes.map(([id, label]) => ({
                id: `size:${id}`, label, icon: (e.size || "medium") === id ? "check" : "",
                onClick: () => setSize(id),
            })),
            "-",
            { id: "color", label: "Colour…", icon: "palette", onClick: () => this._pickColor(e) },
            { id: "hide", label: "Hide", icon: "eye-off", onClick: () => this._change((l) => {
                if (!l.hidden.includes(e.key)) l.hidden.push(e.key);
            }) },
        ];
    }

    _pickColor(e) {
        const dlg = document.createElement("sac-dialog");
        dlg.setAttribute("title", `Colour — ${e.name}`);
        dlg.style.setProperty("--dialog-width", "340px");
        dlg.buttons = [{ action: "close", label: "Close", kind: "default" }];
        const grid = document.createElement("sac-swatch-grid");
        grid.setAttribute("selectable", "");
        grid.setAttribute("columns", "6");
        dlg.appendChild(grid);
        grid.colors = fb.accents.swatches(e.color);
        dlg.addEventListener("sac:action", () => setTimeout(() => dlg.remove(), 120), { once: true });
        grid.addEventListener("sac:change", (ev) => {
            dlg.close("picked");
            const slot = fb.accents.slotOf(ev.detail.value);
            this._change((l) => {
                if (slot) l.colors[e.key] = slot;
                else delete l.colors[e.key];
            });
        });
        document.body.appendChild(dlg);
        setTimeout(() => dlg.open(), 0);
    }

    /* ------------------------------------------------------- badges -- */

    _badges() {
        const has = (key) => this._entries.some((e) => e.key === key);
        if (has("builtin:todos")) this._todoBadge();
        if (has("builtin:calendar")) this._eventBadge();
        if (has("builtin:messages")) this._messageBadge();
    }

    async _todoBadge() {
        let todos;
        try { todos = await fb.api.todos.list(); } catch { return; }
        const end = new Date();
        end.setHours(23, 59, 59, 999);
        const open = (todos || []).filter((t) => !t.completedAt);
        const due = open.filter((t) => t.dueAt && new Date(t.dueAt) <= end);
        this._launcher.setBadge("builtin:todos",
            !open.length ? null : due.length ? `${open.length} · ${due.length} today` : `${open.length}`);
    }

    async _eventBadge() {
        let events;
        try { events = await fb.api.events.upcoming(30); } catch { return; }
        const now = Date.now();
        const next = (events || [])
            .filter((e) => new Date(e.endAt || e.startAt).getTime() >= now)
            .sort((a, b) => new Date(a.startAt) - new Date(b.startAt))[0];
        if (!next) { this._launcher.setBadge("builtin:calendar", null); return; }
        const d = new Date(next.startAt);
        const sameDay = d.toDateString() === new Date().toDateString();
        const day = sameDay ? "Today" : `${fb.format.weekday(d)} ${fb.format.dayMonth(d)}`;
        this._launcher.setBadge("builtin:calendar", next.allDay ? day : `${day} ${fb.format.time(d)}`);
    }

    async _messageBadge() {
        let n = 0;
        try { n = (await fb.api.messages.unreadCount())?.unread || 0; } catch { return; }
        this._launcher?.setBadge("builtin:messages", n ? (n > 99 ? "99+" : String(n)) : null);
    }
}

customElements.define("fb-hub-view", FbHubView);
sac.router.register("#/", "fb-hub-view", { label: "Home", icon: "home", palette: false });
