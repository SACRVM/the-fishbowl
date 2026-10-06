/**
 * <fb-hub-view>  (mounted at #/) — the desktop.
 *
 * The same desktop as SACRVM Desktop, built the same way: the kit's hub
 * recipe (.orb, .hub-container, .grid of <a class="tile">), one tile per app
 * from fb.desktop's registry (js/lib/desktop.js), space-aware hrefs included
 * — never a tile for something that doesn't work. The rules here are SACRVM
 * Desktop's own (the tile menu, the wide / large footprints) plus a layer
 * that is Fishbowl's, tried here first and then handed to the kit as "how we
 * do it": the live content of medium / wide / large tiles (.tile-live,
 * below), the small tiles' names under their icons, and a hover that grows
 * the icon without turning it.
 *
 * Live tiles (js/lib/desktop-live.js, fb.desktopLive): Calendar, Todos, Notes,
 * Contacts, Files and Messages show their content instead of the description
 * from medium up, like a widget in two zones: a darker header band (the kit's
 * --field wash) with the icon, the app's name as the heading (the link's
 * name) and the status under it (the Calendar's icon shows today's day, its
 * status the live time), "+" for a new item (Notes, Todos, Calendar,
 * Contacts — for whoever may write) and "⋯" on the right; below it, on the
 * tile's own lighter ground, the list running down to the tile's bottom edge
 * and fading out there. No big numbers. A one-column card shows one row
 * (`_fit`, re-marked on resize). The rest (and every small tile) stay as
 * they are. All
 * providers load in parallel for the workspace on show — when the desktop
 * renders (the first paint waits up to 400 ms for content it doesn't have
 * yet, so no tile flashes its description), on a workspace switch, every 5 minutes and when the page becomes
 * visible again (Messages also on fb:messages-changed) — and the Calendar's
 * clock repaints at every minute boundary without a request. A provider that
 * fails leaves its tile plain. The tile's "⋯" menu has a per-browser "Show
 * content" switch (localStorage, fb.desk.quiet); off, the provider isn't asked.
 *
 * The cover: instead of a page heading, the grid's FIRST tile is a wide cover
 * (.fb-cover) — the kit's tile footprint, hover lift and accent border, but
 * not an app: no "⋯" menu, can't be hidden, not in fb.desktop's registry.
 * Like a record sleeve: "THE FISHBOWL" top left, the version as a catalogue
 * number ("FB · 0.5.0", once /version has answered) top right, the active
 * workspace's name (Personal or the space's) big at the bottom; behind it a
 * still-water scene — light rays, bubbles, sand, three pebbles — and the
 * goldfish of the logo swimming across and back. All its CSS is the cover's
 * own (.fc-*, below; the water colours are the art's, not theme tokens); with
 * prefers-reduced-motion everything stands still. A click, Enter or Space
 * opens the About window (js/lib/about.js, fb.about) — name, version,
 * tagline, the project's licence and the bundled parts'.
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
// The "+" in a live tile's header: the palette's Create hand-off
// (fb.desktop.go), only for whoever may write in the workspace.
const CREATE = {
    "builtin:notes":    ["notes",    "fb.palette.new-note",    "New note"],
    "builtin:todos":    ["todos",    "fb.palette.new-todo",    "New todo"],
    "builtin:calendar": ["calendar", "fb.palette.new-event",   "New event"],
    "builtin:contacts": ["contacts", "fb.palette.new-contact", "New contact"],
};

// The cover's scene (static markup; the text comes from _paintCover). The
// fish is the logo's (img/fishbowl.svg), as plain shapes so its tail can beat.
const COVER_HTML = `
    <span class="fc-art" aria-hidden="true">
        <svg viewBox="0 0 660 316" preserveAspectRatio="xMaxYMax slice" focusable="false">
            <defs>
                <linearGradient id="fbc-ray" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#fff" stop-opacity=".35"/><stop offset="1" stop-color="#fff" stop-opacity="0"/></linearGradient>
                <radialGradient id="fbc-glow" cx="70%" cy="0%" r="70%"><stop offset="0" stop-color="#e0f2fe" stop-opacity=".4"/><stop offset="1" stop-color="#e0f2fe" stop-opacity="0"/></radialGradient>
            </defs>
            <rect width="660" height="316" fill="url(#fbc-glow)"/>
            <g class="fc-rays">
                <polygon points="380,0 420,0 330,316 250,316" fill="url(#fbc-ray)"/>
                <polygon points="470,0 495,0 455,316 400,316" fill="url(#fbc-ray)" opacity=".7"/>
                <polygon points="560,0 610,0 600,316 520,316" fill="url(#fbc-ray)" opacity=".6"/>
            </g>
            <path d="M0 290 Q160 270 330 286 T660 280 V316 H0 Z" fill="#1e293b" opacity=".85"/>
            <ellipse cx="528" cy="298" rx="15" ry="7" fill="#64748b"/>
            <ellipse cx="556" cy="302" rx="10" ry="5" fill="#94a3b8"/>
            <ellipse cx="604" cy="299" rx="14" ry="7" fill="#475569"/>
            <g class="fc-bubbles" transform="translate(0 300)">
                <circle cx="612" r="3" fill="#fff" fill-opacity=".5"/>
                <circle cx="596" r="2" fill="#fff" fill-opacity=".45"/>
                <circle cx="474" r="3.5" fill="#fff" fill-opacity=".5"/>
                <circle cx="430" r="2.5" fill="#fff" fill-opacity=".45"/>
            </g>
        </svg>
    </span>
    <span class="fc-fish" aria-hidden="true"><span class="fc-bob"><svg class="fc-turn" viewBox="-17 -19 50 34" focusable="false">
        <defs><linearGradient id="fbc-fish" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#fb923c"/><stop offset="1" stop-color="#ea580c"/></linearGradient></defs>
        <path class="fc-tail" d="M13 0 C21 -12 32 -11 30 -4 C29 -1 26 0 30 3 C33 10 21 13 13 0 Z" fill="#fdba74" opacity=".95"/>
        <path d="M-6 -9 Q1 -18 8 -9 Z" fill="#fdba74"/>
        <ellipse cx="0" cy="0" rx="15" ry="10" fill="url(#fbc-fish)"/>
        <path d="M-1 5 Q3 13 9 7 Z" fill="#fdba74"/>
        <circle cx="-8" cy="-2" r="1.9" fill="#0b0f1a"/><circle cx="-8.6" cy="-2.6" r=".6" fill="#fff"/>
        <ellipse cx="-3" cy="-5" rx="6" ry="1.6" fill="#fff" opacity=".45"/>
    </svg></span></span>
    <span class="fc-text">
        <span class="fc-top"><span class="fc-brand">THE FISHBOWL</span><span class="fc-cat" hidden></span></span>
        <span class="fc-name"></span>
    </span>
`;

class FbHubView extends HTMLElement {
    connectedCallback() {
        this._entries = [];
        this._canArrange = false;
        // What the desktop of this workspace loaded last (kept by fb.desktopLive
        // across visits), so the tiles come back live at once.
        this._liveData = fb.desktopLive.cached(fb.api.workspace());
        this.render();
        this.refresh();
        this._onContext = () => { this._liveData = new Map(); this._paintCover(); this.refresh(); };
        // A rename, a new or a deleted space: ask again when the cover needs it.
        this._onSpaces = () => { this._spaces = null; this._paintCover(); };
        this._onApps = () => this.refresh();
        this._onUpdates = () => this._renderTiles();
        this._onVisible = () => { if (document.visibilityState === "visible") this._loadLive(); };
        this._onMessages = () => this._loadLive(["builtin:messages"]);
        window.addEventListener("sac:scope-changed", this._onContext);
        window.addEventListener("fb:apps-changed", this._onApps);
        window.addEventListener("fb:app-updates", this._onUpdates);
        window.addEventListener("fb:messages-changed", this._onMessages);
        window.addEventListener("fb:spaces-changed", this._onSpaces);
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
        window.removeEventListener("fb:spaces-changed", this._onSpaces);
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
        this._paintCover();
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

                /* No rotation: the kit turns a tile's icon 5° on hover; here it
                   only grows (reported to the kit as "how we do it"). */
                fb-hub-view .tile:hover > sac-icon { transform: scale(1.1); }

                /* Small tiles say what they are: the name under the icon (the
                   kit's .small hides it — an icon alone didn't tell layers,
                   cube or lock apart). */
                fb-hub-view .tile.small .tile-body { max-width: 100%; min-width: 0; }
                fb-hub-view .tile.small h2 {
                    position: static;
                    width: auto;
                    height: auto;
                    clip-path: none;
                    max-width: 100%;
                    margin: 0.5rem 0 0;
                    padding: 0 0.5rem;
                    font-size: 0.75rem;
                    font-weight: 600;
                    line-height: 1.2;
                    color: var(--text-muted);
                    text-align: center;
                    white-space: nowrap;
                    overflow: hidden;
                    text-overflow: ellipsis;
                }
                @media (max-width: 600px) {
                    fb-hub-view .tile.small > sac-icon { --icon-size: 32px; }
                    fb-hub-view .tile.small h2 { margin-top: 0.35rem; padding: 0 0.25rem; font-size: 0.68rem; }
                }

                /* Live tiles (js/lib/desktop-live.js) — Fishbowl's own layer,
                   a candidate for the kit. Two zones, like a widget: a header
                   band, darker (the kit's sunken --field wash), with the icon,
                   the app's name as the heading (still the link's name), the
                   status under it and, on the right, "+" (new item) and "⋯";
                   below it, on the tile's own lighter ground, the list running
                   down to the bottom edge and fading out there. Kit tokens and
                   radii only. */
                fb-hub-view .tile-body { min-width: 0; }
                fb-hub-view .tile-live {
                    /* The tile's own accent, solved for text like the kit's *-text tokens. */
                    --tl-accent: color-mix(in oklab, var(--accent) var(--accent-text-pct, 90%), var(--text-solve, var(--lift)));
                    min-width: 0;
                    color: var(--text);
                }
                fb-hub-view .tl-status { min-width: 0; }
                fb-hub-view .tl-status > h2 {
                    margin: 0 0 0.15rem;
                    font-size: 1.15rem;
                    font-weight: 700;
                    line-height: 1.25;
                    white-space: nowrap;
                    overflow: hidden;
                    text-overflow: ellipsis;
                }
                fb-hub-view .tl-main,
                fb-hub-view .tl-sub {
                    color: var(--text-muted);
                    font-size: 0.85rem;
                    line-height: 1.35;
                    white-space: nowrap;
                    overflow: hidden;
                    text-overflow: ellipsis;
                }
                fb-hub-view .tl-main.clock { font-variant-numeric: tabular-nums; }
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

                /* Every footprint from 481px: a grid — the header (the icon over
                   rows 1–3; name, "+" and "⋯" on row 1; the status lines on
                   rows 2–3, under the buttons too), then the list.
                   The wrappers vanish so every part is a grid item of the tile;
                   the band is the tile's ::before behind rows 1–3, out to the
                   tile's edges. */
                @media (min-width: 481px) {
                    fb-hub-view .tile.has-live {
                        display: grid;
                        grid-template-columns: auto minmax(0, 1fr) auto auto;
                        /* Row 3 is kept with one status line or two, so neighbouring
                           bands end on one line. */
                        grid-template-rows: auto auto minmax(calc(0.85rem * 1.35), auto) minmax(0, 1fr);
                        --tl-pad: 2rem;               /* the kit's tile padding */
                        --tl-head: 1.1rem;            /* the band's inset above and below */
                        padding-top: var(--tl-head);
                        justify-content: stretch;
                        align-content: stretch;
                    }
                    fb-hub-view .tile.has-live::before {
                        content: "";
                        grid-column: 1 / -1;
                        grid-row: 1 / 4;
                        margin: calc(-1 * var(--tl-head)) calc(-1 * var(--tl-pad));
                        background: var(--field);
                        border-bottom: 1px solid var(--border);
                        z-index: -1;
                        pointer-events: none;
                    }
                    fb-hub-view .tile.has-live .tile-body.is-live,
                    fb-hub-view .tile.has-live .tile-live,
                    fb-hub-view .tile.has-live .tl-status { display: contents; }
                    fb-hub-view .tile.has-live > sac-icon { grid-column: 1; grid-row: 1 / 4; margin: 0 1rem 0 0; align-self: center; }
                    fb-hub-view .tile.has-live .tl-status > h2 { grid-column: 2; grid-row: 1; align-self: center; }
                    fb-hub-view .tile.has-live .tl-main { grid-column: 2 / -1; grid-row: 2; }
                    fb-hub-view .tile.has-live .tl-sub { grid-column: 2 / -1; grid-row: 3; }
                    fb-hub-view .tile.has-live > .tl-new { grid-column: 3; grid-row: 1; align-self: center; margin-left: 0.5rem; color: var(--accent); }
                    fb-hub-view .tile.has-live > .tile-menu { position: static; grid-column: 4; grid-row: 1; align-self: center; margin-left: 0.15rem; }
                    fb-hub-view .tile.has-live .tl-items,
                    fb-hub-view .tile.has-live .tl-empty { grid-column: 1 / -1; grid-row: 4; margin-top: calc(var(--tl-head) + 0.9rem); min-height: 0; overflow: hidden; }
                    fb-hub-view .tile.has-live .tl-empty { align-self: start; }
                    /* One status line: the band keeps its three-line height (row 3
                       stays), so the name, the line and the buttons move down half
                       a line — the text sits on the icon's centre line too. */
                    fb-hub-view .tile.has-live:not(:has(.tl-sub)) .tl-status > h2,
                    fb-hub-view .tile.has-live:not(:has(.tl-sub)) .tl-main,
                    fb-hub-view .tile.has-live:not(:has(.tl-sub)) > .tl-new,
                    fb-hub-view .tile.has-live:not(:has(.tl-sub)) > .tile-menu {
                        position: relative;
                        top: calc(0.85rem * 1.35 / 2);
                    }
                }
                @media (max-width: 768px) {
                    fb-hub-view .tile.has-live { --tl-pad: 1.5rem; }
                }
                /* A tile with a height to fill (the kit's square tiles — fb-hub-view
                   marks them [data-fill]): the list runs on into the bottom
                   padding, down to the tile's edge, and fades out over about
                   three rows (never more than 60% of the list), so rows are
                   clipped, never cut. */
                @media (min-width: 481px) {
                    fb-hub-view .tile.has-live[data-fill]:not(.no-items) .tl-items {
                        margin-bottom: calc(-1 * var(--tl-pad));
                        -webkit-mask-image: linear-gradient(to bottom, #000 calc(100% - min(var(--tl-pad) + 4rem, 60%)), transparent calc(100% - var(--tl-pad) / 2));
                        mask-image: linear-gradient(to bottom, #000 calc(100% - min(var(--tl-pad) + 4rem, 60%)), transparent calc(100% - var(--tl-pad) / 2));
                    }
                }
                @media (max-width: 480px) {
                    /* The kit turns a tile into a row: icon left, text right —
                       the name first, then the status and one row; no band, no "+". */
                    fb-hub-view .tile-body.is-live { display: flex; flex-direction: column; flex: 1 1 auto; min-width: 0; }
                    fb-hub-view .tile-live { display: flex; flex-direction: column; gap: 0.7rem; }
                    fb-hub-view .tl-status > h2 { font-size: 1.25rem; margin-bottom: 0.3rem; }
                    fb-hub-view .tl-items { flex: none; }
                    fb-hub-view .tile.has-live > .tl-new { display: none; }
                }

                /* The cover — the desktop's first tile, Fishbowl's own (see the
                   header comment). The kit's tile keeps frame, hover lift and
                   accent border; inside it the art is absolutely placed and
                   the type sits on top. The cover is an inline-size container,
                   so type and fish size follow ITS width (cqw). The water,
                   sand and the type's white are the art's own colours. */
                fb-hub-view .grid > .fb-cover {
                    container-type: inline-size;
                    --fc-w: clamp(54px, 14cqw, 96px);     /* the fish */
                    min-height: 180px;                    /* a one-column card */
                    color: #fff;
                }
                fb-hub-view .fb-cover .fc-art {
                    position: absolute;
                    inset: 0;
                    z-index: 0;
                    overflow: hidden;
                    background: linear-gradient(180deg, #38bdf8 0%, #1d4ed8 45%, #172554 85%, #0b1324 100%);
                }
                fb-hub-view .fb-cover .fc-art svg { position: absolute; inset: 0; width: 100%; height: 100%; display: block; }
                fb-hub-view .fb-cover .fc-rays {
                    transform-origin: 50% 0;
                    animation: fc-rays 9s ease-in-out infinite alternate;
                }
                @keyframes fc-rays { from { transform: skewX(-6deg); opacity: .9; } to { transform: skewX(6deg); opacity: .6; } }
                fb-hub-view .fb-cover .fc-bubbles circle { animation: fc-rise 7s linear infinite; }
                fb-hub-view .fb-cover .fc-bubbles circle:nth-child(2) { animation-delay: -2.3s; animation-duration: 8s; }
                fb-hub-view .fb-cover .fc-bubbles circle:nth-child(3) { animation-delay: -4.6s; animation-duration: 6s; }
                fb-hub-view .fb-cover .fc-bubbles circle:nth-child(4) { animation-delay: -1.1s; animation-duration: 9s; }
                /* They rise two thirds of the water and are gone before the top row of type. */
                @keyframes fc-rise { from { transform: translateY(0); opacity: 0; } 12% { opacity: .7; } 80% { opacity: .4; } to { transform: translateY(-210px); opacity: 0; } }

                /* The goldfish: across and back (12 s a leg), turning as it
                   slows at each end, bobbing and beating its tail on the way.
                   It swims in the middle band — the type is top and bottom. */
                fb-hub-view .fb-cover .fc-fish {
                    position: absolute;
                    z-index: 1;
                    left: 0;
                    top: clamp(34px, 32%, 120px);
                    width: var(--fc-w);
                    animation: fc-across 12s ease-in-out infinite alternate;
                }
                @keyframes fc-across {
                    from { transform: translateX(calc(100cqw - var(--fc-w) - 4cqw)); }
                    to   { transform: translateX(4cqw); }
                }
                fb-hub-view .fb-cover .fc-bob { display: block; animation: fc-bob 3s ease-in-out infinite alternate; }
                @keyframes fc-bob { from { transform: translateY(calc(var(--fc-w) * -0.06)); } to { transform: translateY(calc(var(--fc-w) * 0.08)); } }
                fb-hub-view .fb-cover .fc-turn { display: block; width: 100%; height: auto; transform-origin: 50% 50%; animation: fc-turn 24s linear infinite; }
                /* One turn cycle is the two legs (24 s, not alternating): facing
                   left on the way left, right on the way back, the flip being the
                   last stretch of each leg — the fish slows, turns, swims off. */
                @keyframes fc-turn {
                    0%, 42% { transform: scaleX(1); animation-timing-function: ease-in-out; }
                    50%, 92% { transform: scaleX(-1); animation-timing-function: ease-in-out; }
                    100% { transform: scaleX(1); }
                }
                fb-hub-view .fb-cover .fc-tail { transform-box: view-box; transform-origin: 13px 0; animation: fc-tail .7s ease-in-out infinite alternate; }
                @keyframes fc-tail { from { transform: scaleY(1); } to { transform: scaleY(.8); } }

                /* Reduced motion: the whole scene stands still (the fish rests
                   mid-water, the bubbles are left out). */
                @media (prefers-reduced-motion: reduce) {
                    fb-hub-view .fb-cover .fc-rays,
                    fb-hub-view .fb-cover .fc-fish,
                    fb-hub-view .fb-cover .fc-bob,
                    fb-hub-view .fb-cover .fc-turn,
                    fb-hub-view .fb-cover .fc-tail { animation: none; }
                    fb-hub-view .fb-cover .fc-fish { transform: translateX(56cqw); }
                    fb-hub-view .fb-cover .fc-bubbles { display: none; }
                }

                /* The type, like a record sleeve. */
                fb-hub-view .fb-cover .fc-text {
                    position: absolute;
                    inset: 0;
                    z-index: 2;
                    display: flex;
                    flex-direction: column;
                    justify-content: space-between;
                    min-width: 0;
                    padding: 1.4rem 1.7rem 1.45rem;
                }
                fb-hub-view .fb-cover .fc-top {
                    display: flex;
                    justify-content: space-between;
                    align-items: baseline;
                    gap: 1rem;
                    font-size: 0.78rem;
                    line-height: 1;
                }
                fb-hub-view .fb-cover .fc-brand {
                    font-family: 'Outfit', sans-serif;
                    font-weight: 700;
                    letter-spacing: 0.22em;
                    text-transform: uppercase;
                    white-space: nowrap;
                    opacity: .9;
                }
                fb-hub-view .fb-cover .fc-cat {
                    font-family: var(--font-mono);
                    font-weight: 600;
                    letter-spacing: 0.08em;
                    white-space: nowrap;
                    opacity: .85;
                }
                /* The name keeps off the pebbles in the bottom right corner: a long
                   one ends in an ellipsis before them. */
                fb-hub-view .fb-cover .fc-name {
                    min-width: 0;
                    margin-right: 18cqw;
                    font-family: 'Outfit', sans-serif;
                    font-weight: 800;
                    font-size: clamp(1.8rem, 10cqw, 3.15rem);
                    line-height: 1.12;
                    letter-spacing: -0.01em;
                    white-space: nowrap;
                    overflow: hidden;
                    text-overflow: ellipsis;
                    text-shadow: 0 2px 18px rgba(0, 0, 0, .35);
                }
                @media (max-width: 480px) {
                    fb-hub-view .fb-cover .fc-text { padding: 1rem 1.2rem 1.1rem; }
                }
            </style>
            <div class="orb"></div>
            <div class="hub-container">
                <main class="grid" id="fb-tiles" aria-label="${fb.t("fb.desk.apps", "Apps")}"></main>
            </div>
            <sac-footer brand="THE FISHBOWL"></sac-footer>
        `;
        if (fb.version) this.querySelector("sac-footer").setAttribute("version", fb.version);
        this._grid = this.querySelector("#fb-tiles");
        // The cover is built once per mount and kept across tile renders (a
        // repaint must not restart the fish).
        this._cover = this._buildCover();
        this._grid.append(this._cover);
        this._paintCover();
    }

    /* -------------------------------------------------------- cover -- */

    _buildCover() {
        const cover = document.createElement("a");
        cover.className = "tile wide fb-cover";
        cover.href = "#";
        cover.setAttribute("role", "button");
        cover.setAttribute("aria-label", sac.t("about.title", "About {name}").replace("{name}", "The Fishbowl"));
        cover.innerHTML = COVER_HTML;
        cover.addEventListener("click", (ev) => { ev.preventDefault(); fb.about.open(); });
        // A button, not just a link: Space acts like Enter (Enter is the
        // link's own click).
        cover.addEventListener("keydown", (ev) => {
            if (ev.key !== " " || ev.repeat) return;
            ev.preventDefault();
            fb.about.open();
        });
        return cover;
    }

    /** The catalogue number (once the version is known) and the workspace's name. */
    _paintCover() {
        const cover = this._cover;
        if (!cover) return;
        const cat = cover.querySelector(".fc-cat");
        cat.textContent = fb.version ? `FB · ${fb.version}` : "";
        cat.hidden = !fb.version;

        const scope = sac.scope.get();
        let name;
        if (scope.type === "scoped") {
            if (!this._spaces && !this._spacesLoading) this._loadSpaces();
            const space = this._spaces?.find((s) => s.slug === scope.slug);
            // Until the list is in, no name rather than the slug and then the name.
            name = space ? (space.name || space.slug) : (this._spaces ? scope.slug : "");
        } else {
            name = fb.t("fb.shell.personal", "Personal");
        }
        cover.querySelector(".fc-name").textContent = name;
    }

    async _loadSpaces() {
        this._spacesLoading = true;
        try { this._spaces = (await fb.api.spaces.list()) || []; }
        catch { this._spaces = []; }
        this._spacesLoading = false;
        if (this.isConnected) this._paintCover();
    }

    async refresh() {
        const seq = this._refreshSeq = (this._refreshSeq || 0) + 1;
        let loaded, writable;
        try {
            [loaded, writable] = await Promise.all([
                fb.desktop.load(),
                Promise.resolve(fb.access?.canWrite?.() ?? true).catch(() => false),
            ]);
        } catch (err) {
            console.warn("[fb-hub-view] desktop load failed:", err?.message || err);
            return;
        }
        if (!this.isConnected || seq !== this._refreshSeq) return;
        this._entries = loaded.entries;
        this._canArrange = loaded.canArrange;
        this._writable = writable;
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
        old.replaceWith(fb.desktopLive.render(model, e.size, e.name));
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
        // Also without rows (nothing yet): every square tile is marked alike.
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
            // of the description: a header (the name as its heading — still
            // the link's name — and the status under it) over the list. The
            // icon may change too — Calendar's is today's day number.
            const live = this._liveModel(e);
            const icon = document.createElement("sac-icon");
            icon.setAttribute("name", (live && live.icon) || e.icon || "cube");

            const body = document.createElement("div");
            if (live) {
                body.className = "tile-body is-live";
                body.append(fb.desktopLive.render(live, e.size, e.name));
            } else {
                const h2 = document.createElement("h2");
                h2.textContent = e.name;
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
            if (live && this._writable && CREATE[e.key]) tile.appendChild(this._newButton(e));
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
        // The cover stays (its animation keeps running); everything else is replaced.
        for (const child of [...this._grid.children]) if (child !== this._cover) child.remove();
        this._grid.append(...this._packSmall(tiles));
        this._toolbar();
        this._fitAll();
    }

    /** The live tile's "+": the view opens and starts a new item. */
    _newButton(e) {
        const [view, key, fallback] = CREATE[e.key];
        const b = document.createElement("button");
        b.type = "button";
        b.className = "icon-btn tl-new";
        b.title = fb.t(key, fallback);
        b.setAttribute("aria-label", b.title);
        b.innerHTML = '<sac-icon name="plus"></sac-icon>';
        b.addEventListener("click", (ev) => {
            ev.preventDefault();      // inside the tile's link
            ev.stopPropagation();
            fb.desktop.go(view, "create");
        });
        return b;
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
