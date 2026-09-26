/**
 * <fb-hub-view>  (mounted at #/) — the desktop.
 *
 * The workspace's apps as tiles, from fb.desktop's registry (js/lib/
 * desktop.js), built from the kit's hub recipe (.hub-container / .grid /
 * .tile) — on a narrow phone the tiles turn into rows on their own. Never a
 * tile for something that doesn't work.
 *
 * Arranging (only where the GET says canArrange — personal, or the space's
 * owner): each tile's "…" menu sets its size (medium / wide / large), its
 * colour (a kit palette slot, the same picker as the space colour) or hides
 * it; a drag reorders (sac.sortable, grid axis — a long-press on touch).
 * Hidden tiles come back from the "N hidden" link under the grid, which
 * only exists while something is hidden. Everything is stored per
 * workspace on the server.
 *
 * Badges are the host's, from existing APIs: open / due todos, the next
 * event, unread messages. Refreshed on entry and on a workspace switch.
 *
 * <sac-launcher> isn't used: it keeps its layout in localStorage, has no
 * per-tile menu, no drag and no targeted badge update (sacrvm-appkit #16).
 */
class FbHubView extends HTMLElement {
    connectedCallback() {
        this._entries = [];
        this._canArrange = false;
        this.render();
        this.refresh();
        this._onContext = () => this.refresh();
        window.addEventListener("sac:scope-changed", this._onContext);
        window.addEventListener("fb:messages-changed", this._onContext);
        this._loadVersion();
    }

    disconnectedCallback() {
        window.removeEventListener("sac:scope-changed", this._onContext);
        window.removeEventListener("fb:messages-changed", this._onContext);
        this._sortable?.destroy();
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
                fb-hub-view .fb-desk-grid {
                    grid-template-columns: repeat(auto-fill, minmax(240px, 1fr));
                    grid-auto-rows: 150px;
                    gap: 1rem;
                }
                fb-hub-view .fb-desk-cell { position: relative; min-width: 0; display: flex; }
                fb-hub-view .fb-desk-cell.size-wide  { grid-column: span 2; }
                fb-hub-view .fb-desk-cell.size-large { grid-column: span 2; grid-row: span 2; }
                fb-hub-view .fb-desk-cell[data-sortable-dragging] { z-index: 2; }
                /* Quiet tiles: no lift, no glow, no spinning icon — the
                   border takes the accent on hover, that's all. */
                fb-hub-view .fb-desk-cell .tile {
                    flex: 1;
                    min-width: 0;
                    padding: 1.25rem;
                    transition: border-color 0.15s, background 0.15s;
                }
                fb-hub-view .fb-desk-cell .tile:hover {
                    transform: translateZ(0);
                    box-shadow: none;
                }
                fb-hub-view .fb-desk-cell .tile > sac-icon { --icon-size: 32px; }
                fb-hub-view .fb-desk-cell .tile:hover > sac-icon { transform: none; }
                fb-hub-view .fb-desk-cell .tile h2 {
                    display: flex;
                    align-items: center;
                    gap: 8px;
                    font-size: 1.15rem;
                }
                fb-hub-view .fb-desk-count {
                    padding: 0 6px;
                    border-radius: var(--radius-m);
                    background: var(--accent-tint);
                    color: var(--accent-text);
                    font-size: 0.8rem;
                    font-weight: 600;
                    line-height: 1.5;
                }
                fb-hub-view .fb-desk-sub {
                    overflow: hidden;
                    text-overflow: ellipsis;
                    white-space: nowrap;
                }
                fb-hub-view .fb-desk-menu { position: absolute; top: 8px; right: 8px; }
                fb-hub-view .fb-desk-foot { margin-top: 1rem; min-height: 32px; }
                fb-hub-view .fb-desk-hidden-btn {
                    border: none;
                    background: none;
                    padding: 4px 0;
                    color: var(--text-muted);
                    font: inherit;
                    cursor: pointer;
                }
                fb-hub-view .fb-desk-hidden-btn:hover { color: var(--text); }
                @media (max-width: 768px) {
                    fb-hub-view .fb-desk-cell.size-wide,
                    fb-hub-view .fb-desk-cell.size-large { grid-column: span 1; grid-row: span 1; }
                }
                @media (max-width: 480px) {
                    fb-hub-view .fb-desk-grid { grid-auto-rows: auto; gap: 0.75rem; }
                    fb-hub-view .fb-desk-cell .tile { padding-right: 3rem; }
                }
            </style>
            <div class="orb"></div>
            <div class="hub-container">
                <header>
                    <h1>THE FISHBOWL</h1>
                    <p class="intro-text">Your memory lives here. You don't.</p>
                </header>
                <main class="grid fb-desk-grid" aria-label="Apps"></main>
                <div class="fb-desk-foot"></div>
            </div>
            <sac-footer brand="THE FISHBOWL"></sac-footer>
        `;
        if (fb.version) this.querySelector("sac-footer").setAttribute("version", fb.version);
        this._grid = this.querySelector(".fb-desk-grid");
        this._sortable = sac.sortable(this._grid, {
            items: ".fb-desk-cell",
            axis: "grid",
            disabled: () => !this._canArrange,
            onReorder: (from, to) => this._move(from, to),
        });
    }

    async refresh() {
        let layout;
        try { layout = await fb.desktop.load(); }
        catch (err) {
            console.warn("[fb-hub-view] desktop load failed:", err?.message || err);
            return;
        }
        if (!this.isConnected) return;
        this._entries = layout.entries;
        this._canArrange = layout.canArrange;
        this._paint();
        this._badges();
    }

    _visible() { return this._entries.filter((e) => !e.hidden); }

    _paint() {
        this._grid.replaceChildren(...this._visible().map((e) => this._cell(e)));
        this._paintHidden();
    }

    _cell(e) {
        const cell = document.createElement("div");
        cell.className = `fb-desk-cell reveal-on-hover size-${e.size}`;
        cell.dataset.key = e.key;
        if (e.color) cell.style.setProperty("--accent", fb.accents.cssVar(e.color));

        const tile = document.createElement("a");
        tile.className = "tile";
        tile.href = e.href;
        const icon = document.createElement("sac-icon");
        icon.setAttribute("name", e.icon);
        const body = document.createElement("div");
        const h2 = document.createElement("h2");
        const name = document.createElement("span");
        name.textContent = e.name;
        h2.appendChild(name);
        const sub = document.createElement("p");
        sub.className = "fb-desk-sub";
        sub.textContent = e.desc;
        body.append(h2, sub);
        tile.append(icon, body);
        cell.appendChild(tile);

        if (this._canArrange) cell.appendChild(this._menu(e));
        return cell;
    }

    _menu(e) {
        const menu = document.createElement("sac-menu");
        menu.className = "fb-desk-menu";
        const trigger = document.createElement("button");
        trigger.slot = "trigger";
        trigger.type = "button";
        trigger.className = "icon-btn hover-reveal";
        trigger.title = `Arrange ${e.name}`;
        trigger.setAttribute("aria-label", `Arrange ${e.name}`);
        trigger.setAttribute("data-sortable-ignore", "");
        const more = document.createElement("sac-icon");
        more.setAttribute("name", "more");
        trigger.appendChild(more);
        menu.appendChild(trigger);

        const item = (action, label, icon) => {
            const b = document.createElement("button");
            b.dataset.action = action;
            // The kit menu can't colour the active item — a check marks it.
            const i = document.createElement("sac-icon");
            i.setAttribute("name", icon || "");
            if (!icon) i.style.visibility = "hidden";
            b.append(i, document.createTextNode(" " + label));
            menu.appendChild(b);
        };
        const sizes = { medium: "Medium", wide: "Wide", large: "Large" };
        for (const [size, label] of Object.entries(sizes)) item(`size:${size}`, label, e.size === size ? "check" : null);
        menu.appendChild(document.createElement("hr"));
        item("color", "Colour…", "palette");
        item("hide", "Hide", "eye-off");

        menu.addEventListener("sac:select", (ev) => {
            const action = ev.detail.action || "";
            if (action.startsWith("size:")) this._update(e, { size: action.slice(5) });
            if (action === "color") this._pickColor(e);
            if (action === "hide") this._update(e, { hidden: true });
        });
        return menu;
    }

    async _update(e, patch) {
        Object.assign(e, patch);
        this._entries.sort((a, b) => a.position - b.position);
        this._paint();
        this._badges();
        try {
            await fb.desktop.save(e);
        } catch (err) {
            console.warn("[fb-hub-view] tile save failed:", err?.message || err);
            window.sac?.toast?.("Couldn't save the desktop.", { kind: "error" });
            this.refresh();   // back to what the server holds
        }
    }

    /** A drag: the moved tile takes the midpoint of its new neighbours. */
    _move(from, to) {
        const keys = [...this._grid.querySelectorAll(".fb-desk-cell")].map((c) => c.dataset.key);
        const vis = this._visible();
        const byKey = (k) => vis.find((x) => x.key === k);
        const moved = byKey(keys[to]);
        if (!moved) return;
        const prev = byKey(keys[to - 1]);
        const next = byKey(keys[to + 1]);
        const position = prev && next ? (prev.position + next.position) / 2
            : prev ? prev.position + 1
            : next ? next.position - 1
            : moved.position;
        this._update(moved, { position });
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
            this._update(e, { color: fb.accents.slotOf(ev.detail.value) });
        });
        document.body.appendChild(dlg);
        setTimeout(() => dlg.open(), 0);
    }

    /** "2 hidden" under the grid — present only while something is hidden. */
    _paintHidden() {
        const foot = this.querySelector(".fb-desk-foot");
        const hidden = this._entries.filter((e) => e.hidden);
        foot.replaceChildren();
        if (!this._canArrange || !hidden.length) return;
        const menu = document.createElement("sac-menu");
        menu.className = "fb-desk-hidden";
        const trigger = document.createElement("button");
        trigger.slot = "trigger";
        trigger.type = "button";
        trigger.className = "fb-desk-hidden-btn";
        trigger.textContent = hidden.length === 1 ? "1 hidden tile" : `${hidden.length} hidden tiles`;
        menu.appendChild(trigger);
        for (const e of hidden) {
            const b = document.createElement("button");
            b.dataset.action = e.key;
            const i = document.createElement("sac-icon");
            i.setAttribute("name", "eye");
            b.append(i, document.createTextNode(` Show ${e.name}`));
            menu.appendChild(b);
        }
        menu.addEventListener("sac:select", (ev) => {
            const e = this._entries.find((x) => x.key === ev.detail.action);
            if (e) this._update(e, { hidden: false });
        });
        foot.appendChild(menu);
    }

    /* ------------------------------------------------------- badges -- */

    _cellOf(key) { return this._grid.querySelector(`.fb-desk-cell[data-key="${key}"]`); }

    _setCount(key, n, title) {
        const h2 = this._cellOf(key)?.querySelector("h2");
        if (!h2) return;
        h2.querySelector(".fb-desk-count")?.remove();
        if (!n) return;
        const c = document.createElement("span");
        c.className = "fb-desk-count";
        c.textContent = n > 99 ? "99+" : String(n);
        c.title = title;
        h2.appendChild(c);
    }

    _setSub(key, text) {
        const sub = this._cellOf(key)?.querySelector(".fb-desk-sub");
        if (sub && text) sub.textContent = text;
    }

    _badges() {
        const has = (key) => this._visible().some((e) => e.key === key);
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
        this._setCount("builtin:todos", open.length, `${open.length} open`);
        if (open.length) {
            this._setSub("builtin:todos", due.length
                ? `${open.length} open · ${due.length} due today`
                : `${open.length} open`);
        }
    }

    async _eventBadge() {
        let events;
        try { events = await fb.api.events.upcoming(30); } catch { return; }
        const now = Date.now();
        const next = (events || [])
            .filter((e) => new Date(e.endAt || e.startAt).getTime() >= now)
            .sort((a, b) => new Date(a.startAt) - new Date(b.startAt))[0];
        if (!next) {
            this._setSub("builtin:calendar", "Nothing in the next 30 days");
            return;
        }
        const d = new Date(next.startAt);
        const today = new Date();
        const sameDay = d.toDateString() === today.toDateString();
        const day = sameDay ? "Today" : `${fb.format.weekday(d)} ${fb.format.dayMonth(d)}`;
        const when = next.allDay ? day : `${day} ${fb.format.time(d)}`;
        this._setSub("builtin:calendar", `Next: ${when} · ${next.title || "Untitled event"}`);
    }

    async _messageBadge() {
        let n = 0;
        try { n = (await fb.api.messages.unreadCount())?.unread || 0; } catch { return; }
        this._setCount("builtin:messages", n, `${n} unread`);
        if (n) this._setSub("builtin:messages", n === 1 ? "1 unread message" : `${n} unread messages`);
    }
}

customElements.define("fb-hub-view", FbHubView);
sac.router.register("#/", "fb-hub-view", { label: "Home", icon: "home", palette: false });
