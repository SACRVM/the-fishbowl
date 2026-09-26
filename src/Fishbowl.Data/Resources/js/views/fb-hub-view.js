/**
 * <fb-hub-view>  (mounted at #/) — the desktop.
 *
 * The same desktop as SACRVM Desktop, built the same way: the kit's hub
 * recipe (.orb, .hub-container, .grid of <a class="tile">), one tile per app
 * from fb.desktop's registry (js/lib/desktop.js), space-aware hrefs included
 * — never a tile for something that doesn't work. Fishbowl adds no styling
 * over the kit's tiles; the only rules here are SACRVM Desktop's own (the
 * tile menu, the wide / large footprints).
 *
 * Each tile has SACRVM Desktop's "⋯" menu: Medium / Wide / Large tile (✓ on
 * the current one), the colour row (kit palette slots, "Default" = none),
 * and Hide. The arrangement is the SERVER's, per workspace (fb.api.desktop):
 * a menu pick writes that one tile. There is no reordering UI; tiles keep
 * their stored position, unarranged ones sort by registry index. Hidden
 * tiles come back from the toolbar ("Show hidden tiles"). A space member
 * who may not arrange gets no menus and no toolbar item.
 *
 * Badges are the host's, from existing APIs, as the kit's .tile-badge:
 * open / due todos, the next event, unread messages. Refreshed on entry, on
 * a workspace switch and when messages change.
 */
class FbHubView extends HTMLElement {
    connectedCallback() {
        this._entries = [];
        this._canArrange = false;
        this._badgeText = new Map();
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
                /* SACRVM Desktop's tile menu (desktop.css): hidden until the
                   tile is hovered or has focus, always keyboard-reachable. */
                fb-hub-view .tile-menu {
                    position: absolute;
                    top: 8px;
                    right: 10px;
                }
                fb-hub-view .tile-menu-btn {
                    width: 28px;
                    height: 28px;
                    padding: 0;
                    display: flex;
                    align-items: center;
                    justify-content: center;
                    font-size: 1.1rem;
                    line-height: 1;
                    color: var(--text-dim);
                    background: none;
                    border: none;
                    border-radius: var(--radius-m);
                    opacity: 0;
                    cursor: pointer;
                    transition: opacity 0.15s var(--ease-smooth),
                                color 0.15s var(--ease-smooth),
                                background 0.15s var(--ease-smooth);
                }
                fb-hub-view .tile:hover .tile-menu-btn,
                fb-hub-view .tile:focus-within .tile-menu-btn,
                fb-hub-view .tile-menu[open] .tile-menu-btn { opacity: 1; }
                fb-hub-view .tile-menu-btn:hover { color: var(--text); background: var(--hover); }
                fb-hub-view .tile-menu-btn:focus-visible {
                    opacity: 1;
                    outline: 2px solid var(--accent);
                    outline-offset: 1px;
                }
                fb-hub-view .tile-menu .tile-tint {
                    padding: 0.45rem 0.9rem 0.25rem;
                    width: 13rem;
                }
                /* The kit's badge owns the top-right corner; on a tile that
                   wears one, the menu takes the bottom-right (sac-launcher's
                   rule for the same pair). */
                fb-hub-view .tile:has(> .tile-badge) > .tile-menu { top: auto; bottom: 8px; }

                /* SACRVM Desktop's footprints, collapsed on narrow screens. */
                fb-hub-view .grid .tile.size-wide  { grid-column: span 2; }
                fb-hub-view .grid .tile.size-large { grid-column: span 2; grid-row: span 2; }
                @media (max-width: 768px) {
                    fb-hub-view .grid .tile.size-wide,
                    fb-hub-view .grid .tile.size-large { grid-column: auto; grid-row: auto; }
                }
            </style>
            <div class="orb"></div>
            <div class="hub-container">
                <header>
                    <h1>THE FISHBOWL</h1>
                    <p class="intro-text">Your memory lives here. You don't.</p>
                </header>
                <main class="grid" id="fb-tiles" aria-label="Apps"></main>
            </div>
            <sac-footer brand="THE FISHBOWL"></sac-footer>
        `;
        if (fb.version) this.querySelector("sac-footer").setAttribute("version", fb.version);
        this._grid = this.querySelector("#fb-tiles");
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
        this._renderTiles();
        this._badges();
    }

    /* -------------------------------------------------------- tiles -- */

    _renderTiles() {
        this._grid.replaceChildren(...this._entries.filter((e) => !e.hidden).map((e) => {
            const tile = document.createElement("a");
            tile.className = "tile";
            tile.href = e.href;
            tile.dataset.key = e.key;

            const icon = document.createElement("sac-icon");
            icon.setAttribute("name", e.icon || "cube");

            const body = document.createElement("div");
            const h2 = document.createElement("h2");
            h2.textContent = e.name;
            const desc = document.createElement("p");
            desc.textContent = e.desc || "";
            body.append(h2, desc);

            tile.append(icon, body);
            const badge = this._badgeText.get(e.key);
            if (badge) tile.appendChild(this._badgeEl(badge));
            if (this._canArrange) tile.appendChild(this._menu(e));
            if (e.size === "wide" || e.size === "large") tile.classList.add("size-" + e.size);
            // Tile colour = the app's highlight, the SACRVM Desktop move.
            if (e.color) tile.style.setProperty("--accent", fb.accents.cssVar(e.color));
            return tile;
        }));
        this._toolbar();
    }

    /** "Show hidden tiles" — only when there are some and you may arrange. */
    _toolbar() {
        const hidden = this._entries.filter((e) => e.hidden);
        fb.toolbar.set(this._canArrange && hidden.length ? [{
            icon: "eye",
            title: `Show hidden tiles (${hidden.length})`,
            onClick: () => this._showHidden(),
        }] : []);
    }

    _showHidden() {
        const dlg = document.createElement("sac-dialog");
        dlg.id = "fb-hidden-tiles";
        dlg.setAttribute("title", "Hidden tiles");
        dlg.style.setProperty("--dialog-width", "340px");
        dlg.buttons = [{ action: "close", label: "Close", kind: "default" }];
        const list = document.createElement("div");
        list.className = "stack";
        const fill = () => {
            const hidden = this._entries.filter((e) => e.hidden);
            if (!hidden.length) { dlg.close("done"); return; }
            list.replaceChildren(...hidden.map((e) => {
                const row = document.createElement("div");
                row.className = "row";
                row.style.justifyContent = "space-between";
                const name = document.createElement("span");
                name.textContent = e.name;
                const show = document.createElement("button");
                show.type = "button";
                show.className = "btn";
                show.textContent = "Show";
                show.dataset.key = e.key;
                show.addEventListener("click", () => { this._update(e, { hidden: false }); fill(); });
                row.append(name, show);
                return row;
            }));
        };
        fill();
        dlg.appendChild(list);
        dlg.addEventListener("sac:action", () => setTimeout(() => dlg.remove(), 120), { once: true });
        document.body.appendChild(dlg);
        setTimeout(() => dlg.open(), 0);
    }

    /** SACRVM Desktop's tile menu: size, colour, and the way out (Hide). */
    _menu(e) {
        const menu = document.createElement("sac-menu");
        menu.className = "tile-menu";

        const trigger = document.createElement("button");
        trigger.slot = "trigger";
        trigger.type = "button";
        trigger.className = "tile-menu-btn";
        trigger.title = `${e.name} options`;
        trigger.setAttribute("aria-label", trigger.title);
        trigger.textContent = "⋯";          // midline horizontal ellipsis
        menu.appendChild(trigger);

        const item = (action, label) => {
            const b = document.createElement("button");
            b.dataset.action = action;
            b.textContent = label;
            if (action.startsWith("size:") && (e.size || "medium") === action.slice(5)) {
                b.textContent = "✓ " + label;   // the current size, marked
            }
            return b;
        };

        // The colour row: "Default" (no colour) plus every kit palette slot.
        const tint = document.createElement("sac-swatch-grid");
        tint.setAttribute("columns", "8");
        tint.setAttribute("selectable", "");
        tint.className = "tile-tint";
        tint.colors = fb.accents.swatches(e.color);
        tint.addEventListener("sac:change", (ev) => {
            this._update(e, { color: fb.accents.slotOf(ev.detail.value) });
        });

        menu.append(
            item("size:medium", "Medium tile"),
            item("size:wide", "Wide tile"),
            item("size:large", "Large tile"),
            document.createElement("hr"),
            tint,
            document.createElement("hr"),
            item("hide", "Hide from this desktop"),
        );

        menu.addEventListener("sac:select", (ev) => {
            const action = ev.detail.action;
            if (action === "hide") this._update(e, { hidden: true });
            else if (action?.startsWith("size:")) this._update(e, { size: action.slice(5) });
        });

        // The tile is a link: a click inside its menu must not follow it.
        menu.addEventListener("click", (ev) => { ev.preventDefault(); ev.stopPropagation(); });
        return menu;
    }

    /** Change one tile, repaint, store it; a failed save reloads the server's state. */
    async _update(e, patch) {
        Object.assign(e, patch);
        this._renderTiles();
        try {
            await fb.desktop.save(e);
        } catch (err) {
            console.warn("[fb-hub-view] tile save failed:", err?.message || err);
            window.sac?.toast?.("Couldn't save the desktop.", { kind: "error" });
            this.refresh();
        }
    }

    _badgeEl(text) {
        const b = document.createElement("span");
        b.className = "tile-badge";
        b.textContent = text;
        return b;
    }

    /** Set (or clear, with null) one tile's badge, now and across repaints. */
    _setBadge(key, text) {
        if (text) this._badgeText.set(key, text);
        else this._badgeText.delete(key);
        const tile = this._grid?.querySelector(`a.tile[data-key="${key}"]`);
        if (!tile) return;
        tile.querySelector(":scope > .tile-badge")?.remove();
        if (text) tile.insertBefore(this._badgeEl(text), tile.querySelector(":scope > .tile-menu"));
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
        this._setBadge("builtin:todos",
            !open.length ? null : due.length ? `${open.length} · ${due.length} today` : `${open.length}`);
    }

    async _eventBadge() {
        let events;
        try { events = await fb.api.events.upcoming(30); } catch { return; }
        const now = Date.now();
        const next = (events || [])
            .filter((e) => new Date(e.endAt || e.startAt).getTime() >= now)
            .sort((a, b) => new Date(a.startAt) - new Date(b.startAt))[0];
        if (!next) { this._setBadge("builtin:calendar", null); return; }
        const d = new Date(next.startAt);
        const sameDay = d.toDateString() === new Date().toDateString();
        const day = sameDay ? "Today" : `${fb.format.weekday(d)} ${fb.format.dayMonth(d)}`;
        this._setBadge("builtin:calendar", next.allDay ? day : `${day} ${fb.format.time(d)}`);
    }

    async _messageBadge() {
        let n = 0;
        try { n = (await fb.api.messages.unreadCount())?.unread || 0; } catch { return; }
        this._setBadge("builtin:messages", n ? (n > 99 ? "99+" : String(n)) : null);
    }
}

customElements.define("fb-hub-view", FbHubView);
sac.router.register("#/", "fb-hub-view", { label: "Home", icon: "home", palette: false });
