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
 * Installed apps (fb.desktopApps) are tiles like the built-ins, with
 * SACRVM Desktop's origin line under the description and three more menu
 * items: "Update to v…" (only when a newer version was found), "Permissions…"
 * (sandboxed apps) and "Remove from this desktop". A tile opens its app in
 * a kit window. The dashed "Install app" tile closes the grid, like SACRVM
 * Desktop's — only for whoever may install here (policy, space owner).
 */
class FbHubView extends HTMLElement {
    connectedCallback() {
        this._entries = [];
        this._canArrange = false;
        this.render();
        this.refresh();
        this._onContext = () => this.refresh();
        this._onApps = () => this.refresh();
        this._onUpdates = () => this._renderTiles();
        window.addEventListener("sac:scope-changed", this._onContext);
        window.addEventListener("fb:apps-changed", this._onApps);
        window.addEventListener("fb:app-updates", this._onUpdates);
        this._loadVersion();
    }

    disconnectedCallback() {
        window.removeEventListener("sac:scope-changed", this._onContext);
        window.removeEventListener("fb:apps-changed", this._onApps);
        window.removeEventListener("fb:app-updates", this._onUpdates);
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

                /* SACRVM Desktop's install tile (.tile-add) and origin line
                   (.tile-meta), desktop.css. */
                fb-hub-view .tile-add {
                    align-items: center;
                    justify-content: center;
                    gap: 0.6rem;
                    background: transparent;
                    border: 1px dashed var(--border-strong);
                    color: var(--text-muted);
                    font: inherit;
                    font-weight: 600;
                    font-size: 0.95rem;
                    cursor: pointer;
                }
                fb-hub-view .tile-add > sac-icon { --icon-size: 32px; margin-bottom: 0; color: var(--text-muted); }
                fb-hub-view .tile-add:hover { border-color: var(--accent); color: var(--text); }
                fb-hub-view .tile-meta {
                    color: var(--text-dim);
                    font-size: 0.75rem;
                    font-family: var(--font-mono);
                    margin-top: 0.4rem;
                    overflow-wrap: anywhere;
                }

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
                    <p class="intro-text">${fb.t("fb.desk.tagline", "Your memory lives here. You don't.")}</p>
                </header>
                <main class="grid" id="fb-tiles" aria-label="${fb.t("fb.desk.apps", "Apps")}"></main>
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
        this._canInstall = loaded.canInstall;
        this._renderTiles();
    }

    /* -------------------------------------------------------- tiles -- */

    _renderTiles() {
        const tiles = this._entries.filter((e) => !e.hidden).map((e) => {
            const tile = document.createElement("a");
            tile.className = "tile";
            tile.dataset.key = e.key;
            if (e.app) {
                // An installed app opens in a kit window, not by address.
                tile.href = "#";
                tile.classList.add("tile-window");
                tile.addEventListener("click", (ev) => {
                    ev.preventDefault();
                    fb.desktopApps.open(e.app.id);
                });
            } else {
                tile.href = e.href;
            }

            const icon = document.createElement("sac-icon");
            icon.setAttribute("name", e.icon || "cube");

            const body = document.createElement("div");
            const h2 = document.createElement("h2");
            h2.textContent = e.name;
            const desc = document.createElement("p");
            desc.textContent = e.desc || "";
            body.append(h2, desc);
            if (e.app) {
                const meta = document.createElement("p");
                meta.className = "tile-meta";
                const version = e.app.version ? ` · v${e.app.version}` : "";
                meta.textContent = `${hostOf(e.app.origin)}${version} · ${e.app.mode}`;
                body.appendChild(meta);
            }

            tile.append(icon, body);
            if (this._canArrange) tile.appendChild(this._menu(e));
            if (e.size === "wide" || e.size === "large") tile.classList.add("size-" + e.size);
            // Tile colour = the app's highlight, the SACRVM Desktop move.
            if (e.color) tile.style.setProperty("--accent", fb.accents.cssVar(e.color));
            return tile;
        });
        if (this._canInstall) tiles.push(this._installTile());
        this._grid.replaceChildren(...tiles);
        this._toolbar();
    }

    /** SACRVM Desktop's dashed tile that closes the grid. */
    _installTile() {
        const add = document.createElement("button");
        add.type = "button";
        add.className = "tile tile-add";
        add.id = "fb-install-tile";
        const icon = document.createElement("sac-icon");
        icon.setAttribute("name", "plus");
        const label = document.createElement("span");
        label.textContent = this._entries.some((e) => e.app)
            ? fb.t("fb.desk.install", "Install app")
            : fb.t("fb.desk.install-first", "Install your first app");
        add.append(icon, label);
        add.addEventListener("click", () => fb.desktopApps.install());
        return add;
    }

    /** "Show hidden tiles" — only when there are some and you may arrange. */
    _toolbar() {
        const hidden = this._entries.filter((e) => e.hidden);
        fb.toolbar.set(this._canArrange && hidden.length ? [{
            icon: "eye",
            title: fb.t("fb.desk.show-hidden", "Show hidden tiles ({n})", { n: hidden.length }),
            onClick: () => this._showHidden(),
        }] : []);
    }

    _showHidden() {
        const dlg = document.createElement("sac-dialog");
        dlg.id = "fb-hidden-tiles";
        dlg.setAttribute("title", fb.t("fb.desk.hidden-title", "Hidden tiles"));
        dlg.style.setProperty("--dialog-width", "340px");
        dlg.buttons = [{ action: "close", label: fb.t("fb.common.close", "Close"), kind: "default" }];
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
                show.textContent = fb.t("fb.desk.show", "Show");
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
        trigger.title = fb.t("fb.desk.options", "{name} options", { name: e.name });
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
            item("size:medium", fb.t("fb.desk.size-medium", "Medium tile")),
            item("size:wide", fb.t("fb.desk.size-wide", "Wide tile")),
            item("size:large", fb.t("fb.desk.size-large", "Large tile")),
            document.createElement("hr"),
            tint,
            document.createElement("hr"),
            item("hide", fb.t("fb.desk.hide", "Hide from this desktop")),
        );
        if (e.app) {
            // Items are added only when they apply — no dead entries.
            const fresh = fb.desktopApps.updateFor(e.app.id);
            const extra = [];
            if (fresh) extra.push(item("app:update", fb.t("fb.desk.update-to", "Update to v{version}…", { version: fresh.version || "?" })));
            if (e.app.mode === "sandboxed") extra.push(item("app:perms", fb.t("fb.desk.permissions", "Permissions…")));
            const remove = item("app:remove", fb.t("fb.desk.remove", "Remove from this desktop"));
            remove.dataset.danger = "";
            menu.append(document.createElement("hr"), ...extra, remove);
        }

        menu.addEventListener("sac:select", (ev) => {
            const action = ev.detail.action;
            if (action === "hide") this._update(e, { hidden: true });
            else if (action?.startsWith("size:")) this._update(e, { size: action.slice(5) });
            else if (action === "app:update") fb.desktopApps.update(e.app.id);
            else if (action === "app:perms") fb.desktopApps.permissions(e.app.id);
            else if (action === "app:remove") fb.desktopApps.remove(e.app.id);
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
            window.sac?.toast?.(fb.t("fb.desk.save-failed", "Couldn't save the desktop."), { kind: "error" });
            this.refresh();
        }
    }

}

function hostOf(url) {
    try { return new URL(url).host; } catch { return url || ""; }
}

customElements.define("fb-hub-view", FbHubView);
sac.router.register("#/", "fb-hub-view", { label: "Home", icon: "home", palette: false });
