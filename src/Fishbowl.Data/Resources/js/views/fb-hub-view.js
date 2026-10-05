/**
 * <fb-hub-view>  (mounted at #/) — the desktop.
 *
 * The same desktop as SACRVM Desktop, built the same way: the kit's hub
 * recipe (.orb, .hub-container, .grid of <a class="tile">), one tile per app
 * from fb.desktop's registry (js/lib/desktop.js), space-aware hrefs included
 * — never a tile for something that doesn't work. Fishbowl adds no styling
 * over the kit's tile frame, hover, menu and sizes; the rules here are
 * SACRVM Desktop's own (the tile menu, the wide / large footprints) plus ONE
 * layer that is Fishbowl's: the live content of medium / wide / large tiles
 * (.tile-live, below) — a candidate for the kit.
 *
 * Live tiles (js/lib/desktop-live.js, fb.desktopLive): Calendar, Todos, Notes,
 * Contacts, Files and Messages show their content instead of the description
 * from medium up, like a phone widget: a two-line status beside the icon (the
 * Calendar's icon shows today's day, its status the live time), then the list
 * running down to the tile's bottom edge and fading out there, the app name a
 * small uppercase label bottom right (the h2 stays the link's name); no big
 * numbers. A one-column card shows one row (`_fit`, re-marked on resize). The
 * rest (and every small tile) stay as they are. All
 * providers load in parallel for the workspace on show — when the desktop
 * renders (the first paint waits up to 400 ms for content it doesn't have
 * yet, so no tile flashes its description), on a workspace switch, every 5 minutes and when the page becomes
 * visible again (Messages also on fb:messages-changed) — and the Calendar's
 * clock repaints at every minute boundary without a request. A provider that
 * fails leaves its tile plain. The tile's "⋯" menu has a per-browser "Show
 * content" switch (localStorage, fb.desk.quiet); off, the provider isn't asked.
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
const LIVE_REFRESH_MS = 5 * 60 * 1000;

class FbHubView extends HTMLElement {
    connectedCallback() {
        this._entries = [];
        this._canArrange = false;
        // What the desktop of this workspace loaded last (kept by fb.desktopLive
        // across visits), so the tiles come back live at once.
        this._liveData = fb.desktopLive.cached(fb.api.workspace());
        this.render();
        this.refresh();
        this._onContext = () => { this._liveData = new Map(); this.refresh(); };
        this._onApps = () => this.refresh();
        this._onUpdates = () => this._renderTiles();
        this._onVisible = () => { if (document.visibilityState === "visible") this._loadLive(); };
        this._onMessages = () => this._loadLive(["builtin:messages"]);
        window.addEventListener("sac:scope-changed", this._onContext);
        window.addEventListener("fb:apps-changed", this._onApps);
        window.addEventListener("fb:app-updates", this._onUpdates);
        window.addEventListener("fb:messages-changed", this._onMessages);
        document.addEventListener("visibilitychange", this._onVisible);
        this._poll = setInterval(() => this._loadLive(), LIVE_REFRESH_MS);
        this._armClock();
        // The window crossing the one-column width turns the tiles between
        // "fill and fade" and "card": mark them again.
        this._ro = new ResizeObserver(() => {
            cancelAnimationFrame(this._fitFrame);
            this._fitFrame = requestAnimationFrame(() => this._fitAll());
        });
        this._ro.observe(this._grid);
        this._loadVersion();
    }

    disconnectedCallback() {
        window.removeEventListener("sac:scope-changed", this._onContext);
        window.removeEventListener("fb:apps-changed", this._onApps);
        window.removeEventListener("fb:app-updates", this._onUpdates);
        window.removeEventListener("fb:messages-changed", this._onMessages);
        document.removeEventListener("visibilitychange", this._onVisible);
        clearInterval(this._poll);
        clearTimeout(this._clock);
        cancelAnimationFrame(this._fitFrame);
        this._ro?.disconnect();
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

                /* SACRVM Desktop's origin line (.tile-meta), desktop.css. */
                fb-hub-view .tile-meta {
                    color: var(--text-dim);
                    font-size: 0.75rem;
                    font-family: var(--font-mono);
                    margin-top: 0.4rem;
                    overflow-wrap: anywhere;
                }

                /* SACRVM Desktop's footprints, collapsed on narrow screens. */
                fb-hub-view .grid .tile.size-wide  { grid-column: span 2; }
                /* --grid-rows is what makes the kit's tile 2 cells tall (its
                   .tile.large sets it); the span alone only reserved the rows. */
                fb-hub-view .grid .tile.size-large { grid-column: span 2; grid-row: span 2; --grid-rows: 2; }
                @media (max-width: 768px) {
                    fb-hub-view .grid .tile.size-wide,
                    fb-hub-view .grid .tile.size-large { grid-column: auto; grid-row: auto; --grid-rows: 1; }
                }

                /* Live tiles (js/lib/desktop-live.js) — Fishbowl's own layer,
                   a candidate for the kit. The kit's tile keeps its frame,
                   padding, icon and hover; the content takes the place of the
                   <p>, like a phone widget: the status beside the icon
                   (vertically on its centre line), the list running down to the
                   tile's bottom edge and fading out there, the name (the h2,
                   still the link's name) as a small label bottom right over the
                   faded part. Kit tokens and radii only. */
                fb-hub-view .tile-body { min-width: 0; }
                fb-hub-view .tile-live {
                    /* The tile's own accent, solved for text like the kit's *-text tokens. */
                    --tl-accent: color-mix(in oklab, var(--accent) var(--accent-text-pct, 90%), var(--text-solve, var(--lift)));
                    min-width: 0;
                    color: var(--text);
                }
                fb-hub-view .tl-status { min-width: 0; }
                fb-hub-view .tl-main {
                    font-size: 1rem;
                    font-weight: 600;
                    line-height: 1.3;
                    white-space: nowrap;
                    overflow: hidden;
                    text-overflow: ellipsis;
                }
                fb-hub-view .tl-main.clock {
                    font-size: 1.6rem;
                    font-weight: 700;
                    line-height: 1.1;
                    letter-spacing: -0.01em;
                    font-variant-numeric: tabular-nums;
                }
                fb-hub-view .tl-sub {
                    margin-top: 0.1rem;
                    color: var(--text-muted);
                    font-size: 0.85rem;
                    line-height: 1.3;
                    white-space: nowrap;
                    overflow: hidden;
                    text-overflow: ellipsis;
                }
                /* The list takes what is left; rows share three columns
                   (lead · text · tail) through a subgrid, so the leads line up
                   and the tails sit at one edge. */
                fb-hub-view .tl-items {
                    list-style: none;
                    margin: 0;
                    padding: 0;
                    display: grid;
                    grid-template-columns: max-content minmax(0, 1fr) max-content;
                    align-content: start;
                    flex: 1 1 auto;
                    min-height: 0;
                    overflow: hidden;
                    font-size: 0.9rem;
                    line-height: 1.4;
                }
                fb-hub-view .tl-items > li {
                    grid-column: 1 / -1;
                    display: grid;
                    grid-template-columns: max-content minmax(0, 1fr) max-content;
                    grid-template-columns: subgrid;
                    align-items: baseline;
                    padding: 0.2rem 0;
                }
                fb-hub-view .tl-items > li[hidden] { display: none; }
                fb-hub-view .tl-lead { color: var(--tl-accent); font-variant-numeric: tabular-nums; white-space: nowrap; }
                fb-hub-view .tl-lead:not(:empty) { padding-right: 0.8rem; }
                fb-hub-view .tl-text { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
                fb-hub-view .tl-tail { color: var(--text-muted); font-variant-numeric: tabular-nums; white-space: nowrap; text-align: right; }
                fb-hub-view .tl-tail:not(:empty) { padding-left: 0.8rem; }
                fb-hub-view .tl-empty { color: var(--text-muted); font-size: 0.9rem; line-height: 1.4; padding: 0.2rem 0; }

                /* Medium (and a footprint collapsed to one cell): a grid —
                   icon | status on the first row, the bar, the list on the
                   rest, the name last. The wrappers vanish so every part is
                   a grid item of the tile. */
                @media (min-width: 481px) {
                    fb-hub-view .tile.has-live {
                        display: grid;
                        grid-template-columns: auto minmax(0, 1fr);
                        grid-template-rows: auto auto minmax(0, 1fr);
                        column-gap: 1rem;
                        --tl-pad: 2rem;               /* the kit's tile padding */
                        justify-content: stretch;
                        align-content: stretch;
                    }
                    fb-hub-view .tile.has-live .tile-body.is-live,
                    fb-hub-view .tile.has-live .tile-live { display: contents; }
                    fb-hub-view .tile.has-live > sac-icon { grid-column: 1; grid-row: 1; margin: 0; align-self: center; }
                    fb-hub-view .tile.has-live .tl-status { grid-column: 2; grid-row: 1; align-self: center; }
                    fb-hub-view .tile.has-live .tl-bar { grid-column: 1 / -1; grid-row: 2; margin-top: 0.9rem; }
                    fb-hub-view .tile.has-live .tl-items,
                    fb-hub-view .tile.has-live .tl-empty { grid-column: 1 / -1; grid-row: 3; margin-top: 0.9rem; min-height: 0; overflow: hidden; }
                    fb-hub-view .tile.has-live .tl-empty { align-self: start; }
                    /* The name: a small muted label, right edge on the tails' edge;
                       it takes no row. A tile that fills sets it in as far from the
                       bottom as from the right (below); a card keeps it in the padding. */
                    fb-hub-view .tile.has-live .tile-body.is-live > h2 {
                        position: absolute;
                        grid-area: auto;
                        right: var(--tl-pad);
                        bottom: 0.7rem;
                        margin: 0;
                        font-size: 0.7rem;
                        font-weight: 600;
                        line-height: 1;
                        letter-spacing: 0.08em;
                        text-transform: uppercase;
                        color: var(--text-muted);
                        white-space: nowrap;
                    }
                    fb-hub-view .tile.has-live[data-fill] .tile-body.is-live > h2 { bottom: var(--tl-pad); }
                    /* Large: the list under a hairline. */
                    fb-hub-view .tile.has-live.live-large .tl-items,
                    fb-hub-view .tile.has-live.live-large .tl-empty { padding-top: 0.75rem; border-top: 1px solid var(--border); }
                }
                /* Wide (two cells): icon and status on the left, the list on the
                   right from the top down to the bottom edge, the name bottom left. */
                @media (min-width: 769px) {
                    fb-hub-view .tile.has-live.live-wide:not(.no-items) { grid-template-columns: auto minmax(0, 0.8fr) minmax(0, 1.3fr); }
                    fb-hub-view .tile.has-live.live-wide:not(.no-items) .tl-bar { grid-column: 1 / 3; }
                    fb-hub-view .tile.has-live.live-wide:not(.no-items) .tl-items,
                    fb-hub-view .tile.has-live.live-wide:not(.no-items) .tl-empty { grid-column: 3; grid-row: 1 / -1; margin: 0 0 0 1rem; }
                }
                @media (max-width: 768px) {
                    fb-hub-view .tile.has-live { --tl-pad: 1.5rem; }
                }
                /* A tile with a height to fill (the kit's square tiles — fb-hub-view
                   marks them [data-fill]): the list runs on into the bottom
                   padding, down to the tile's edge, and fades out — over about
                   three rows, never more than 60% of the list — fully clear from
                   just above the name label (inset like the right edge), so there
                   is no cut and the label sits on a clear band. */
                @media (min-width: 481px) {
                    fb-hub-view .tile.has-live[data-fill]:not(.no-items) .tl-items {
                        margin-bottom: calc(-1 * var(--tl-pad));
                        -webkit-mask-image: linear-gradient(to bottom, #000 calc(100% - min(var(--tl-pad) + 5.5rem, 60%)), transparent calc(100% - min(var(--tl-pad) + 1.2rem, 30%)));
                        mask-image: linear-gradient(to bottom, #000 calc(100% - min(var(--tl-pad) + 5.5rem, 60%)), transparent calc(100% - min(var(--tl-pad) + 1.2rem, 30%)));
                    }
                }
                @media (max-width: 480px) {
                    /* The kit turns a tile into a row: icon left, text right —
                       the name first, then the status and one row. */
                    fb-hub-view .tile-body.is-live { display: flex; flex-direction: column; flex: 1 1 auto; min-width: 0; }
                    fb-hub-view .tile-body.is-live > h2 { margin: 0 0 0.5rem; order: -1; }
                    fb-hub-view .tile-live { display: flex; flex-direction: column; gap: 0.7rem; }
                    fb-hub-view .tl-items { flex: none; }
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
        const seq = this._refreshSeq = (this._refreshSeq || 0) + 1;
        let loaded;
        try { loaded = await fb.desktop.load(); }
        catch (err) {
            console.warn("[fb-hub-view] desktop load failed:", err?.message || err);
            return;
        }
        if (!this.isConnected || seq !== this._refreshSeq) return;
        this._entries = loaded.entries;
        this._canArrange = loaded.canArrange;
        // Content for tiles that have none yet is given a moment (400 ms at
        // most) before the first paint, so a live tile never flashes its
        // description and then turns live; a slow provider paints later.
        const loading = this._loadLive();
        if (this._entries.some((e) => this._wantsLive(e) && !this._liveData.has(e.key))) {
            await Promise.race([loading, new Promise((resolve) => setTimeout(resolve, 400))]);
            if (!this.isConnected || seq !== this._refreshSeq) return;
        }
        this._renderTiles();
    }

    /* ---------------------------------------------------- live content -- */

    /** Does this tile show its content? Medium and up, an app with a
     *  provider, not hidden, and not switched off in this browser. */
    _wantsLive(e) {
        return !e.hidden && e.size !== "small" && fb.desktopLive.has(e.key)
            && !fb.desktopLive.isQuiet(fb.api.workspace(), e.key);
    }

    /** The model a live tile paints now, or null → the plain tile. */
    _liveModel(e) {
        if (!this._wantsLive(e) || !this._liveData.has(e.key)) return null;
        try { return fb.desktopLive.present(e.key, this._liveData.get(e.key), new Date()); }
        catch (err) {
            console.warn("[fb-hub-view] live tile failed:", e.key, err?.message || err);
            return null;
        }
    }

    /** Load the providers of the tiles that show content — all of them, or
     *  just `keys` — for the workspace on show, in parallel, and paint. */
    async _loadLive(keys = null) {
        const ws = fb.api.workspace();
        const wanted = this._entries.filter((e) => this._wantsLive(e)).map((e) => e.key);
        const toLoad = keys ? wanted.filter((k) => keys.includes(k)) : wanted;
        const results = toLoad.length ? await fb.desktopLive.load(ws, toLoad) : new Map();
        // Gone, or another workspace on show by now: not for this desktop.
        if (!this.isConnected || fb.api.workspace() !== ws) return;
        for (const k of toLoad) {
            if (results.has(k)) this._liveData.set(k, results.get(k));
            else this._liveData.delete(k);          // failed → the plain tile
        }
        for (const k of [...this._liveData.keys()]) if (!wanted.includes(k)) this._liveData.delete(k);
        this._applyLive();
    }

    /** Paint fresh data: in place while every tile keeps its kind (an open
     *  menu stays open), a full render when one flipped plain ⇄ live. */
    _applyLive() {
        const flipped = this._entries.some((e) => {
            if (e.hidden) return false;
            const tile = this._grid?.querySelector(`a.tile[data-key="${e.key}"]`);
            return !!tile && !!tile.querySelector(".tile-live") !== !!this._liveModel(e);
        });
        if (flipped) { this._renderTiles(); return; }
        for (const e of this._entries) this._paintLive(e);
    }

    /** Replace one tile's live block with a fresh one. */
    _paintLive(e) {
        const old = this._grid?.querySelector(`a.tile[data-key="${e.key}"] .tile-live`);
        const model = old && this._liveModel(e);
        if (!model) return;
        const tile = old.closest("a.tile");
        old.replaceWith(fb.desktopLive.render(model, e.size));
        const icon = tile.querySelector(":scope > sac-icon");
        const name = model.icon || e.icon || "cube";
        if (icon && icon.getAttribute("name") !== name) icon.setAttribute("name", name);
        this._fit(tile);
    }

    /** Which kind of tile this is: the kit's square tiles (two columns and
     *  up) have a height to fill — the list runs down to the bottom edge and
     *  fades ([data-fill]); a one-column card is as tall as its content and
     *  shows one row. */
    _fit(tile) {
        if (!tile?.querySelector(".tile-live")) return;
        const fill = parseInt(getComputedStyle(tile).getPropertyValue("--grid-cols"), 10) >= 2;
        // Also without rows (nothing yet): the name label sits where it does
        // on every square tile.
        tile.toggleAttribute("data-fill", fill);
        const list = tile.querySelector(".tl-items");
        if (list) [...list.children].forEach((row, i) => { row.hidden = !fill && i > 0; });
    }

    _fitAll() {
        for (const tile of this._grid?.querySelectorAll("a.tile.has-live") || []) this._fit(tile);
    }

    /** The clock: wake at every minute boundary, repaint the tiles that tell
     *  the time (Calendar) from the data they already have, and arm again. */
    _armClock() {
        clearTimeout(this._clock);
        this._clock = setTimeout(() => {
            for (const e of this._entries) if (fb.desktopLive.clock(e.key)) this._paintLive(e);
            this._armClock();
        }, 60000 - (Date.now() % 60000) + 25);
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
            } else if (e.open) {
                // A built-in window app (System): a window, not an address.
                tile.href = "#";
                tile.classList.add("tile-window");
                tile.addEventListener("click", (ev) => {
                    ev.preventDefault();
                    e.open();
                });
            } else {
                tile.href = e.href;
            }

            // Medium and up, a tile with a provider shows its content instead
            // of the description (the name stays: it is the link's name; the
            // icon may change too — Calendar's is today's day number).
            const live = this._liveModel(e);
            const icon = document.createElement("sac-icon");
            icon.setAttribute("name", (live && live.icon) || e.icon || "cube");

            const body = document.createElement("div");
            const h2 = document.createElement("h2");
            h2.textContent = e.name;
            if (live) {
                body.className = "tile-body is-live";
                body.append(h2, fb.desktopLive.render(live, e.size));
            } else {
                const desc = document.createElement("p");
                desc.textContent = e.desc || "";
                body.append(h2, desc);
            }
            if (e.app) {
                const meta = document.createElement("p");
                meta.className = "tile-meta";
                const version = e.app.version ? ` · v${e.app.version}` : "";
                meta.textContent = e.app.mode === "space"
                    ? `${e.app.folder}${version}`
                    : `${hostOf(e.app.origin)}${version} · ${e.app.mode}`;
                body.appendChild(meta);
            }

            tile.append(icon, body);
            if (live) {
                tile.classList.add("has-live", `live-${e.size}`);
                if (!Array.isArray(live.items)) tile.classList.add("no-items");
            }
            if (this._canArrange) tile.appendChild(this._menu(e));
            if (e.size === "wide" || e.size === "large") tile.classList.add("size-" + e.size);
            if (e.size === "small") {
                tile.classList.add("small");   // the kit's .small: icon only
                tile.title = e.name;
            }
            // Tile colour = the app's highlight, the SACRVM Desktop move.
            if (e.color) tile.style.setProperty("--accent", fb.accents.cssVar(e.color));
            return tile;
        });
        this._grid.replaceChildren(...this._packSmall(tiles));
        this._toolbar();
        this._fitAll();
    }

    /** The kit's .tile-pack: consecutive small tiles share one medium cell,
     *  four at most; any other tile between them starts a new pack. */
    _packSmall(tiles) {
        const out = [];
        let pack = null;
        for (const t of tiles) {
            if (!t.classList.contains("small")) { pack = null; out.push(t); continue; }
            if (!pack || pack.childElementCount === 4) {
                pack = document.createElement("div");
                pack.className = "tile-pack";
                out.push(pack);
            }
            pack.appendChild(t);
        }
        return out;
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

        // The per-browser privacy switch of a tile that can show content
        // (medium and up): ✓ while it does.
        const content = [];
        if (e.size !== "small" && fb.desktopLive.has(e.key)) {
            const b = document.createElement("button");
            b.dataset.action = "live:toggle";
            const on = !fb.desktopLive.isQuiet(fb.api.workspace(), e.key);
            b.textContent = (on ? "✓ " : "") + fb.t("fb.desk.live.show-content", "Show content");
            content.push(document.createElement("hr"), b);
        }

        menu.append(
            item("size:small", fb.t("fb.desk.size-small", "Small tile")),
            item("size:medium", fb.t("fb.desk.size-medium", "Medium tile")),
            item("size:wide", fb.t("fb.desk.size-wide", "Wide tile")),
            item("size:large", fb.t("fb.desk.size-large", "Large tile")),
            ...content,
            document.createElement("hr"),
            tint,
            document.createElement("hr"),
            item("hide", fb.t("fb.desk.hide", "Hide from this desktop")),
        );
        if (e.app && e.app.mode !== "space") {
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
            else if (action === "live:toggle") this._toggleContent(e);
            else if (action === "app:update") fb.desktopApps.update(e.app.id);
            else if (action === "app:perms") fb.desktopApps.permissions(e.app.id);
            else if (action === "app:remove") fb.desktopApps.remove(e.app.id);
        });

        // The tile is a link: a click inside its menu must not follow it.
        menu.addEventListener("click", (ev) => { ev.preventDefault(); ev.stopPropagation(); });
        return menu;
    }

    /** "Show content" — this browser's switch for one tile in this workspace. */
    _toggleContent(e) {
        const ws = fb.api.workspace();
        fb.desktopLive.setQuiet(ws, e.key, !fb.desktopLive.isQuiet(ws, e.key));
        this._renderTiles();
        this._loadLive([e.key]);
    }

    /** Change one tile, repaint, store it; a failed save reloads the server's state. */
    async _update(e, patch) {
        Object.assign(e, patch);
        this._renderTiles();
        // A tile that just became medium (or came back from hidden) has no
        // content yet.
        if ("size" in patch || "hidden" in patch) this._loadLive([e.key]);
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
