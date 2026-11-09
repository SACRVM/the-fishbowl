/**
 * <fb-photos-view>  (mounted at #/photos, personal and space)
 *
 * The Photos app (spec docs/superpowers/specs/2026-10-30-photos-design.md):
 * every photo and video under the workspace's Photos/ folder as one
 * timeline, by month, newest first — on the kit's <sac-split>: the library,
 * the favourites and the albums on the left, the grid on the right.
 *
 * The grid is the app's own code (the kit has no grid that scrolls tens of
 * thousands of pictures): only the months near the view are fetched and
 * only the cells on screen exist; the scroll height comes from the months'
 * counts, so the scrollbar is the whole library from the first paint.
 *
 *   click            opens the viewer (a maximised window: the large
 *                    preview, ←/→ walk the timeline, i the info)
 *   Ctrl/⌘+click     marks; Shift+click marks a range; a long press opens
 *                    the cell's menu, whose Select starts marking
 *   keys             ←/→/↑/↓, PgUp/PgDn, Home/End move the cursor (Shift
 *                    marks), Enter or Space opens, F favourites, Delete asks
 *                    and sends the files to the trash, Ctrl/⌘+A marks the
 *                    loaded ones, Esc lets go, +/− zoom
 *
 * Marked photos get Favourite, Add to album, Remove from the album and
 * Delete in the bar. Upload: "+" or a drop on the grid (writers); the
 * server files each by the day it was taken. A space Reader looks only.
 * Light DOM: the kit's tokens and classes apply; CSS scoped with the tag.
 */
(function () {
    const GAP = 2;
    const HEAD = 48;                       // a month's heading
    const ZOOM = [84, 120, 172, 248];      // a cell's target edge, px
    const KEEP = 1.5;                      // viewports kept above and below
    const ACCEPT = "image/*,video/*,.heic,.heif,.avif,.dng,.cr2,.cr3,.nef,.arw,.orf,.rw2,.raf,.mov,.m4v";

    const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));
    const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

    /** The camera's own clock as a Date the browser formats as local time. */
    function wallClock(photo) {
        const utc = Date.parse(photo.takenAt);
        const wall = new Date(utc + (photo.offset ?? 0) * 60000);
        return new Date(wall.getTime() + wall.getTimezoneOffset() * 60000);
    }

    function duration(seconds) {
        if (!seconds && seconds !== 0) return "";
        const s = Math.round(seconds);
        const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), r = s % 60;
        return h ? `${h}:${String(m).padStart(2, "0")}:${String(r).padStart(2, "0")}` : `${m}:${String(r).padStart(2, "0")}`;
    }

    class FbPhotosView extends HTMLElement {
        constructor() {
            super();
            this.view = { kind: "library", album: null };
            this.months = [];            // [{ month, count }]
            this.data = new Map();       // month → photos[] (loaded)
            this.loading = new Map();    // month → Promise
            this.blocks = [];
            this.total = 0;
            this.albums = [];
            this.marks = new Set();      // photo ids
            this.cursor = -1;            // global index
            this.anchor = -1;
            this.nodes = new Map();      // key → element
            this._raf = 0;
        }

        t(key, fallback, vars) { return fb.t(`fb.photos.${key}`, fallback, vars); }

        async connectedCallback() {
            this.api = fb.api.photos.in(fb.api.workspace());
            this.files = fb.api.files.in(this.api.workspace);
            try { this.zoom = clamp(Number(localStorage.getItem("fb.photos.zoom") ?? 1), 0, ZOOM.length - 1); }
            catch { this.zoom = 1; }
            if (!Number.isFinite(this.zoom)) this.zoom = 1;
            this.render();
            this.writable = await fb.access.canWrite();
            if (!this.isConnected) return;
            this.toggleAttribute("readonly", !this.writable);
            this._menu = sac.contextMenu(this.canvas, {
                targets: ".pv-cell[data-id]",
                items: (cell) => this._cellItems(cell),
            });
            this._albumMenu = sac.contextMenu(this.querySelector("#pv-albums"), {
                targets: ".pv-row[data-album]",
                items: (row) => this.writable ? [
                    { id: "rename", label: this.t("album-rename", "Rename…"), icon: "pencil", onClick: () => this.renameAlbum(row.dataset.album) },
                    "-",
                    { id: "delete", label: this.t("album-delete", "Delete album…"), icon: "trash", danger: true, onClick: () => this.deleteAlbum(row.dataset.album) },
                ] : null,
            });
            this._resize = new ResizeObserver(() => this.relayout());
            this._resize.observe(this.scroller);
            await Promise.all([this.loadAlbums(), this.reload()]);
            if (!this.isConnected) return;
            this.paintToolbar();
            this._statusTimer = setInterval(() => this.checkStatus(), 10000);
            this.checkStatus();
            this._onIntent = () => {
                const it = fb.desktop?.takeIntent("photos");
                if (it?.action === "create" && this.writable) this.pick();
            };
            window.addEventListener("fb:intent", this._onIntent);
            this._onIntent();
        }

        disconnectedCallback() {
            if (this._onIntent) window.removeEventListener("fb:intent", this._onIntent);
            clearInterval(this._statusTimer);
            cancelAnimationFrame(this._raf);
            this._resize?.disconnect();
            this._menu?.destroy();
            this._albumMenu?.destroy();
            this._viewer?.close?.();
            if (window.fb?.toolbar) fb.toolbar.clear();
        }

        // ───────────────────────────── markup ─────────────────────────────

        render() {
            const t = (k, f) => this.t(k, f);
            this.innerHTML = `
                <style>
                    fb-photos-view {
                        display: block;
                        height: calc(100vh - 50px);
                        height: calc(100dvh - 50px - env(safe-area-inset-top, 0px));
                        background: var(--bg);
                    }
                    fb-photos-view [hidden] { display: none !important; }
                    fb-photos-view .pv-side { height: 100%; background: var(--panel); display: flex; flex-direction: column; }
                    fb-photos-view .pv-sections { padding: 12px 12px 4px; display: grid; gap: 2px; }
                    fb-photos-view .pv-head { display: flex; align-items: center; gap: 2px; padding: 14px 12px 6px 24px; }
                    fb-photos-view .pv-head-title {
                        flex: 1; font-family: 'Outfit', sans-serif; font-weight: 700; font-size: 11px;
                        text-transform: uppercase; letter-spacing: 0.1em; color: var(--text-muted);
                    }
                    fb-photos-view .pv-head .icon-btn { color: var(--text-muted); }
                    fb-photos-view .pv-albums { flex: 1; min-height: 0; overflow-y: auto; padding: 2px 12px 12px; display: grid; align-content: start; gap: 2px; }
                    fb-photos-view .pv-row {
                        display: flex; align-items: center; gap: 10px; width: 100%;
                        padding: 8px 12px; border: 1px solid transparent; border-radius: var(--radius-m);
                        background: none; color: var(--text); font: inherit; text-align: left; cursor: pointer;
                    }
                    fb-photos-view .pv-row:hover { background: var(--hover); }
                    fb-photos-view .pv-row.selected { background: var(--accent-tint); border-color: color-mix(in srgb, var(--accent) 28%, transparent); }
                    fb-photos-view .pv-row sac-icon { color: var(--text-muted); flex: none; }
                    fb-photos-view .pv-row.selected sac-icon { color: var(--accent); }
                    fb-photos-view .pv-row-name { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
                    fb-photos-view .pv-row-count { color: var(--text-muted); font-size: 0.8125rem; font-variant-numeric: tabular-nums; }
                    fb-photos-view .pv-albums-empty { color: var(--text-muted); font-size: 0.8125rem; padding: 4px 12px; }

                    fb-photos-view .pv-main { position: relative; height: 100%; display: flex; flex-direction: column; min-width: 0; }
                    fb-photos-view .pv-bar { display: flex; align-items: center; gap: 8px; padding: 12px 16px 8px 24px; min-height: 56px; box-sizing: border-box; }
                    fb-photos-view .pv-titles { flex: 1; min-width: 0; }
                    fb-photos-view .pv-title { margin: 0; font-size: 1.25rem; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
                    fb-photos-view .pv-sub { color: var(--text-muted); font-size: 0.8125rem; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
                    fb-photos-view .pv-tools, fb-photos-view .pv-marktools { display: flex; align-items: center; gap: 2px; flex: none; }
                    fb-photos-view .pv-marked-count { font-weight: 600; margin-right: 6px; white-space: nowrap; }
                    fb-photos-view sac-progress { margin: 0 24px 6px; }

                    fb-photos-view .pv-scroller { flex: 1; min-height: 0; overflow-y: auto; overflow-x: hidden; position: relative; }
                    fb-photos-view .pv-scroller:focus { outline: none; }
                    fb-photos-view .pv-scroller:focus-visible .pv-cell.cursor { outline: 2px solid var(--accent); outline-offset: -2px; }
                    fb-photos-view .pv-canvas { position: relative; width: 100%; }
                    fb-photos-view .pv-month {
                        position: absolute; left: 0; right: 0; height: ${HEAD}px; box-sizing: border-box;
                        padding: 18px 24px 0; font-weight: 700; font-size: 0.95rem;
                    }
                    fb-photos-view .pv-month-count { color: var(--text-muted); font-weight: 400; font-size: 0.8125rem; margin-left: 8px; }
                    fb-photos-view .pv-cell {
                        position: absolute; box-sizing: border-box; overflow: hidden; cursor: pointer;
                        background: var(--field); border-radius: var(--radius-s);
                        display: grid; place-items: center; color: var(--text-muted);
                    }
                    fb-photos-view .pv-cell img { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: cover; display: block; }
                    fb-photos-view .pv-cell.cursor { outline: 2px solid color-mix(in srgb, var(--accent) 70%, transparent); outline-offset: -2px; }
                    fb-photos-view .pv-cell.marked::after {
                        content: ""; position: absolute; inset: 0;
                        box-shadow: inset 0 0 0 3px var(--accent); background: color-mix(in srgb, var(--accent) 18%, transparent);
                    }
                    fb-photos-view .pv-badge {
                        position: absolute; z-index: 1; display: flex; align-items: center; gap: 4px;
                        color: #fff; font-size: 0.75rem; font-weight: 600; text-shadow: 0 1px 2px rgba(0,0,0,.6);
                        --icon-size: 14px;
                    }
                    fb-photos-view .pv-badge.br { right: 6px; bottom: 4px; }
                    fb-photos-view .pv-badge.bl { left: 6px; bottom: 4px; }
                    fb-photos-view .pv-badge.tl { left: 6px; top: 5px; }
                    fb-photos-view .pv-badge.tr { right: 5px; top: 5px; }
                    fb-photos-view .pv-check {
                        width: 20px; height: 20px; border-radius: 50%; display: grid; place-items: center;
                        background: var(--accent); color: #fff; --icon-size: 13px;
                    }
                    fb-photos-view .pv-empty { margin: auto; max-width: 460px; text-align: center; }
                    fb-photos-view[readonly] .pv-write { display: none !important; }
                    @media (max-width: 768px) {
                        fb-photos-view .pv-bar { padding: 10px 8px 6px 12px; }
                        fb-photos-view .pv-month { padding: 16px 12px 0; }
                    }
                </style>
                <sac-split id="pv-split" collapse show="end" position="${this._splitPosition()}" min-start="200px" min-end="360px"
                           aria-label="${t("resize", "Resize the albums list")}">
                    <aside class="pv-side" slot="start">
                        <nav class="pv-sections" aria-label="${t("sections", "Library")}">
                            <button type="button" class="pv-row" data-view="library">
                                <sac-icon name="image"></sac-icon><span class="pv-row-name">${t("library", "Library")}</span><span class="pv-row-count" id="pv-count-library"></span>
                            </button>
                            <button type="button" class="pv-row" data-view="favorites">
                                <sac-icon name="fb-heart"></sac-icon><span class="pv-row-name">${t("favorites", "Favourites")}</span>
                            </button>
                        </nav>
                        <div class="pv-head">
                            <span class="pv-head-title">${t("albums", "Albums")}</span>
                            <button type="button" class="icon-btn pv-write" id="pv-new-album" title="${t("new-album", "New album")}" aria-label="${t("new-album", "New album")}"><sac-icon name="plus"></sac-icon></button>
                        </div>
                        <div class="pv-albums fb-scroll-fade" id="pv-albums"></div>
                    </aside>
                    <main class="pv-main" slot="end">
                        <div class="pv-bar">
                            <div class="pv-titles">
                                <h2 class="pv-title" id="pv-title"></h2>
                                <div class="pv-sub" id="pv-sub"></div>
                            </div>
                            <div class="pv-marktools" id="pv-marktools" hidden>
                                <span class="pv-marked-count" id="pv-marked-count"></span>
                                <button type="button" class="icon-btn tool pv-write" id="pv-mark-fav" title="${t("favorite", "Favourite")}" aria-label="${t("favorite", "Favourite")}"><sac-icon name="fb-heart"></sac-icon></button>
                                <sac-menu id="pv-mark-album" class="pv-write">
                                    <button slot="trigger" type="button" class="icon-btn tool" title="${t("add-to-album", "Add to album")}" aria-label="${t("add-to-album", "Add to album")}"><sac-icon name="fb-album"></sac-icon></button>
                                </sac-menu>
                                <button type="button" class="icon-btn tool pv-write" id="pv-mark-unalbum" hidden title="${t("remove-from-album", "Remove from this album")}" aria-label="${t("remove-from-album", "Remove from this album")}"><sac-icon name="close"></sac-icon></button>
                                <button type="button" class="icon-btn tool danger pv-write" id="pv-mark-delete" title="${t("delete", "Delete")}" aria-label="${t("delete", "Delete")}"><sac-icon name="trash"></sac-icon></button>
                                <button type="button" class="icon-btn tool" id="pv-mark-clear" title="${t("clear-marks", "Clear selection")}" aria-label="${t("clear-marks", "Clear selection")}"><sac-icon name="check"></sac-icon></button>
                            </div>
                            <div class="pv-tools" id="pv-tools">
                                <button type="button" class="icon-btn tool" id="pv-zoom-out" title="${t("zoom-out", "Smaller")}" aria-label="${t("zoom-out", "Smaller")}"><sac-icon name="zoom-out"></sac-icon></button>
                                <button type="button" class="icon-btn tool" id="pv-zoom-in" title="${t("zoom-in", "Larger")}" aria-label="${t("zoom-in", "Larger")}"><sac-icon name="zoom-in"></sac-icon></button>
                                <button type="button" class="icon-btn tool pv-write" id="pv-add" title="${t("add", "Add photos")}" aria-label="${t("add", "Add photos")}"><sac-icon name="plus"></sac-icon></button>
                            </div>
                        </div>
                        <sac-progress id="pv-progress" hidden></sac-progress>
                        <div class="pv-scroller fb-scroll-fade" id="pv-scroller" tabindex="0" role="grid" aria-label="${t("grid", "Photos")}">
                            <div class="pv-canvas" id="pv-canvas"></div>
                        </div>
                        <div class="empty-state pv-empty" id="pv-empty" hidden></div>
                        <input type="file" id="pv-file" multiple accept="${ACCEPT}" hidden>
                        <sac-drop-zone overlay multiple accept="${ACCEPT}" id="pv-drop" class="pv-write"
                                       label="${t("drop", "Drop photos and videos to add them")}" hint=""></sac-drop-zone>
                    </main>
                </sac-split>`;

            this.scroller = this.querySelector("#pv-scroller");
            this.canvas = this.querySelector("#pv-canvas");
            this.split = this.querySelector("#pv-split");

            for (const row of this.querySelectorAll(".pv-sections [data-view]"))
                row.addEventListener("click", () => this.show({ kind: row.dataset.view, album: null }));
            this.querySelector("#pv-albums").addEventListener("click", (e) => {
                const row = e.target.closest(".pv-row[data-album]");
                if (row) this.show({ kind: "album", album: row.dataset.album });
            });
            this.querySelector("#pv-new-album").addEventListener("click", () => this.newAlbum());
            this.split.addEventListener("sac:resize", (e) => {
                try { localStorage.setItem("fb.photos.split", e.detail.position); } catch { /* storage off */ }
            });

            this.scroller.addEventListener("scroll", () => this.schedule(), { passive: true });
            this.scroller.addEventListener("keydown", (e) => this.onKey(e));
            this.canvas.addEventListener("click", (e) => this.onCellClick(e));

            this.querySelector("#pv-zoom-in").addEventListener("click", () => this.setZoom(this.zoom + 1));
            this.querySelector("#pv-zoom-out").addEventListener("click", () => this.setZoom(this.zoom - 1));
            this.querySelector("#pv-add").addEventListener("click", () => this.pick());
            const input = this.querySelector("#pv-file");
            input.addEventListener("change", () => { const f = [...input.files]; input.value = ""; this.upload(f); });
            this.querySelector("#pv-drop").addEventListener("sac:files", (e) => this.upload(e.detail?.files || e.detail || []));

            this.querySelector("#pv-mark-fav").addEventListener("click", () => this.favoriteMarked());
            this.querySelector("#pv-mark-delete").addEventListener("click", () => this.trash([...this.marks]));
            this.querySelector("#pv-mark-clear").addEventListener("click", () => this.clearMarks());
            this.querySelector("#pv-mark-unalbum").addEventListener("click", () => this.removeFromAlbum([...this.marks]));
            this.querySelector("#pv-mark-album").addEventListener("sac:select", (e) => this.onAlbumPick(e, [...this.marks]));
            this.paintSections();
            this.paintBar();
        }

        _splitPosition() {
            try { const v = localStorage.getItem("fb.photos.split"); return v && v.endsWith("%") ? v : "22%"; } catch { return "22%"; }
        }

        paintToolbar() {
            // The pane's own bar holds the actions; the nav toolbar stays free.
            fb.toolbar?.clear?.();
        }

        // ───────────────────────────── sections ─────────────────────────────

        async loadAlbums() {
            try { this.albums = (await this.api.albums.list()).albums || []; }
            catch (err) { console.error("[fb-photos-view] albums failed:", err); this.albums = []; }
            if (this.view.kind === "album" && !this.albums.some((a) => a.id === this.view.album))
                this.view = { kind: "library", album: null };
            this.paintSections();
            this.paintAlbumMenu();
        }

        paintSections() {
            for (const row of this.querySelectorAll(".pv-sections [data-view]")) {
                const on = this.view.kind === row.dataset.view;
                row.classList.toggle("selected", on);
                row.setAttribute("aria-current", on ? "page" : "false");
            }
            const list = this.querySelector("#pv-albums");
            if (!list) return;
            list.innerHTML = this.albums.length === 0
                ? `<div class="pv-albums-empty">${esc(this.t("no-albums", "No albums yet"))}</div>`
                : this.albums.map((a) => `
                    <button type="button" class="pv-row${this.view.album === a.id ? " selected" : ""}" data-album="${esc(a.id)}"
                            aria-current="${this.view.album === a.id ? "page" : "false"}">
                        <sac-icon name="fb-album"></sac-icon><span class="pv-row-name">${esc(a.name)}</span><span class="pv-row-count">${a.count}</span>
                    </button>`).join("");
        }

        paintAlbumMenu() {
            const menu = this.querySelector("#pv-mark-album");
            if (!menu) return;
            for (const item of [...menu.querySelectorAll(":scope > button:not([slot])")]) item.remove();
            const add = (action, icon, label) => {
                const b = document.createElement("button");
                b.type = "button";
                b.dataset.action = action;
                b.innerHTML = `<sac-icon name="${icon}"></sac-icon> ${esc(label)}`;
                menu.appendChild(b);
            };
            for (const a of this.albums) add(`album:${a.id}`, "fb-album", a.name);
            add("new", "plus", this.t("new-album-with", "New album…"));
        }

        filter() {
            return this.view.kind === "favorites" ? { favorites: true }
                : this.view.kind === "album" ? { album: this.view.album } : {};
        }

        currentTitle() {
            if (this.view.kind === "favorites") return this.t("favorites", "Favourites");
            if (this.view.kind === "album") return this.albums.find((a) => a.id === this.view.album)?.name || this.t("album", "Album");
            return this.t("library", "Library");
        }

        async show(view) {
            this.view = view;
            this.clearMarks(false);
            this.cursor = -1;
            this.scroller.scrollTop = 0;
            this.paintSections();
            if (this.split?.collapsed) this.split.show = "end";
            await this.reload();
            this.scroller.focus({ preventScroll: true });
        }

        // ───────────────────────────── data ─────────────────────────────

        async reload() {
            const f = this.filter();
            const key = JSON.stringify(f);
            this._key = key;
            let months = [];
            try { months = (await this.api.months(f)).months || []; }
            catch (err) { console.error("[fb-photos-view] months failed:", err); }
            if (this._key !== key) return;
            this.months = months;
            this.data.clear();
            this.loading.clear();
            for (const node of this.nodes.values()) node.remove();
            this.nodes.clear();
            if (this.view.kind === "library") {
                const el = this.querySelector("#pv-count-library");
                if (el) el.textContent = String(months.reduce((n, m) => n + m.count, 0));
            }
            this.relayout();
            this.paintBar();
        }

        loadMonth(month) {
            if (this.data.has(month)) return Promise.resolve(this.data.get(month));
            if (this.loading.has(month)) return this.loading.get(month);
            const key = this._key;
            const p = this.api.month(month, this.filter())
                .then((r) => {
                    if (this._key !== key) return [];
                    this.data.set(month, r.photos || []);
                    this.schedule();
                    return this.data.get(month);
                })
                .catch((err) => { console.error("[fb-photos-view] month failed:", err); return []; })
                .finally(() => this.loading.delete(month));
            this.loading.set(month, p);
            return p;
        }

        /** The photo at a global index, if its month is loaded. */
        at(index) {
            const b = this.blockOf(index);
            if (!b) return null;
            return this.data.get(b.month)?.[index - b.start] ?? null;
        }

        async fetchAt(index) {
            const b = this.blockOf(index);
            if (!b) return null;
            const list = await this.loadMonth(b.month);
            return list[index - b.start] ?? null;
        }

        indexOf(id) {
            for (const b of this.blocks) {
                const i = (this.data.get(b.month) || []).findIndex((p) => p.id === id);
                if (i >= 0) return b.start + i;
            }
            return -1;
        }

        blockOf(index) {
            if (index < 0 || index >= this.total) return null;
            let lo = 0, hi = this.blocks.length - 1;
            while (lo < hi) {
                const mid = (lo + hi + 1) >> 1;
                if (this.blocks[mid].start <= index) lo = mid; else hi = mid - 1;
            }
            return this.blocks[lo];
        }

        // ───────────────────────────── layout ─────────────────────────────

        relayout() {
            if (!this.scroller) return;
            const phone = window.matchMedia("(max-width: 768px)").matches;
            this.pad = phone ? 2 : 24;
            const width = Math.max(0, this.scroller.clientWidth - 2 * this.pad);
            const target = phone ? ZOOM[this.zoom] * 0.85 : ZOOM[this.zoom];
            this.cols = Math.max(1, Math.floor((width + GAP) / (target + GAP)));
            this.cell = this.cols > 0 ? (width - (this.cols - 1) * GAP) / this.cols : target;
            let y = 0, start = 0;
            this.blocks = this.months.map((m) => {
                const rows = Math.ceil(m.count / this.cols);
                const b = { month: m.month, count: m.count, top: y, start, rows };
                y += HEAD + rows * (this.cell + GAP);
                start += m.count;
                return b;
            });
            this.total = start;
            this.canvas.style.height = `${Math.ceil(y + 24)}px`;
            for (const node of this.nodes.values()) node.remove();
            this.nodes.clear();
            const empty = this.querySelector("#pv-empty");
            const none = this.total === 0;
            empty.hidden = !none;
            this.scroller.hidden = none;
            if (none) this.paintEmpty(empty);
            this.paint();
        }

        paintEmpty(el) {
            const writer = this.writable;
            if (this.view.kind === "library") {
                el.innerHTML = `
                    <sac-icon name="image"></sac-icon>
                    <h3>${esc(this.t("empty", "No photos yet"))}</h3>
                    <p>${esc(this.t("empty-hint", "Every photo and video in the Photos folder of Files shows here."))}</p>
                    ${writer ? `<button type="button" class="btn primary" id="pv-empty-add"><sac-icon name="plus"></sac-icon> ${esc(this.t("add", "Add photos"))}</button>` : ""}`;
                el.querySelector("#pv-empty-add")?.addEventListener("click", () => this.pick());
            } else {
                el.innerHTML = `<sac-icon name="${this.view.kind === "favorites" ? "fb-heart" : "fb-album"}"></sac-icon>
                    <h3>${esc(this.view.kind === "favorites" ? this.t("empty-favorites", "No favourites yet") : this.t("empty-album", "This album is empty"))}</h3>`;
            }
        }

        schedule() {
            if (this._raf) return;
            this._raf = requestAnimationFrame(() => { this._raf = 0; this.paint(); });
        }

        paint() {
            if (!this.blocks.length) return;
            const top = this.scroller.scrollTop;
            const h = this.scroller.clientHeight || 600;
            const lo = top - h * KEEP, hi = top + h * (1 + KEEP);
            const step = this.cell + GAP;
            const want = new Set();

            for (const b of this.blocks) {
                const bottom = b.top + HEAD + b.rows * step;
                if (bottom < lo || b.top > hi) continue;
                const list = this.data.get(b.month);
                if (!list) this.loadMonth(b.month);

                const hk = `h:${b.month}`;
                want.add(hk);
                if (!this.nodes.has(hk)) {
                    const el = document.createElement("div");
                    el.className = "pv-month";
                    el.style.top = `${b.top}px`;
                    const [y, m] = b.month.split("-").map(Number);
                    el.innerHTML = `${esc(fb.format.monthYear(new Date(y, m - 1, 1)))}<span class="pv-month-count">${b.count}</span>`;
                    this.canvas.appendChild(el);
                    this.nodes.set(hk, el);
                }

                const r0 = clamp(Math.floor((lo - b.top - HEAD) / step), 0, b.rows - 1);
                const r1 = clamp(Math.floor((hi - b.top - HEAD) / step), 0, b.rows - 1);
                for (let i = r0 * this.cols; i < Math.min(b.count, (r1 + 1) * this.cols); i++) {
                    const index = b.start + i;
                    const photo = list?.[i] ?? null;
                    const key = `c:${index}`;
                    want.add(key);
                    let el = this.nodes.get(key);
                    if (!el) {
                        el = document.createElement("div");
                        el.className = "pv-cell";
                        el.setAttribute("role", "gridcell");
                        el.dataset.index = String(index);
                        el.id = `pv-cell-${index}`;
                        this.canvas.appendChild(el);
                        this.nodes.set(key, el);
                    }
                    el.style.left = `${this.pad + (i % this.cols) * step}px`;
                    el.style.top = `${b.top + HEAD + Math.floor(i / this.cols) * step}px`;
                    el.style.width = el.style.height = `${this.cell}px`;
                    this.fill(el, photo);
                    el.classList.toggle("cursor", index === this.cursor);
                }
            }
            for (const [key, el] of this.nodes) if (!want.has(key)) { el.remove(); this.nodes.delete(key); }
            if (this.cursor >= 0) this.scroller.setAttribute("aria-activedescendant", `pv-cell-${this.cursor}`);
        }

        fill(el, photo) {
            const stamp = photo ? `${photo.id}|${photo.version}|${photo.favorite}|${this.marks.has(photo.id)}` : "";
            if (el.dataset.stamp === stamp) return;
            el.dataset.stamp = stamp;
            if (!photo) {
                el.removeAttribute("data-id");
                el.classList.remove("marked");
                el.innerHTML = "";
                el.removeAttribute("aria-label");
                return;
            }
            el.dataset.id = photo.id;
            const marked = this.marks.has(photo.id);
            el.classList.toggle("marked", marked);
            el.setAttribute("aria-selected", String(marked));
            const when = wallClock(photo);
            el.setAttribute("aria-label", `${photo.kind === "video" ? this.t("video", "Video") : this.t("photo", "Photo")}, ${fb.format.dateTime(when)}`);
            const img = photo.preview
                ? `<img alt="" loading="lazy" decoding="async" draggable="false" src="${esc(this.api.previewUrl(photo, "s"))}">`
                : `<sac-icon name="${photo.kind === "video" ? "film" : "image"}"></sac-icon>`;
            const badges = [
                photo.kind === "video" ? `<span class="pv-badge br"><sac-icon name="play"></sac-icon>${esc(duration(photo.duration))}</span>` : "",
                photo.kind === "live" ? `<span class="pv-badge tl" title="Live"><sac-icon name="fb-live"></sac-icon></span>` : "",
                photo.favorite ? `<span class="pv-badge bl"><sac-icon name="fb-heart-fill"></sac-icon></span>` : "",
                marked ? `<span class="pv-badge tr"><span class="pv-check"><sac-icon name="check"></sac-icon></span></span>` : "",
            ].join("");
            el.innerHTML = img + badges;
        }

        setZoom(z) {
            const next = clamp(z, 0, ZOOM.length - 1);
            if (next === this.zoom) return;
            // Keep the cursor's photo (else the top row) in view.
            const keep = this.cursor >= 0 ? this.cursor : this.firstVisible();
            this.zoom = next;
            try { localStorage.setItem("fb.photos.zoom", String(next)); } catch { /* storage off */ }
            this.relayout();
            if (keep >= 0) this.reveal(keep, "start");
            this.paintBar();
        }

        firstVisible() {
            const top = this.scroller.scrollTop;
            for (const b of this.blocks) {
                const bottom = b.top + HEAD + b.rows * (this.cell + GAP);
                if (bottom > top) return b.start + Math.max(0, Math.floor((top - b.top - HEAD) / (this.cell + GAP))) * this.cols;
            }
            return -1;
        }

        cellTop(index) {
            const b = this.blockOf(index);
            if (!b) return 0;
            return b.top + HEAD + Math.floor((index - b.start) / this.cols) * (this.cell + GAP);
        }

        reveal(index, where = "nearest") {
            const y = this.cellTop(index);
            const h = this.scroller.clientHeight;
            if (where === "start") this.scroller.scrollTop = Math.max(0, y - HEAD);
            else if (y < this.scroller.scrollTop + 4) this.scroller.scrollTop = Math.max(0, y - (this.blockOf(index)?.start === index - ((index - this.blockOf(index).start) % this.cols) ? HEAD : 8));
            else if (y + this.cell > this.scroller.scrollTop + h - 4) this.scroller.scrollTop = y + this.cell - h + 8;
            this.paint();
        }

        // ───────────────────────────── the bar ─────────────────────────────

        paintBar() {
            const title = this.querySelector("#pv-title");
            if (!title) return;
            title.textContent = this.currentTitle();
            const n = this.total;
            const sub = [n === 1 ? this.t("count-1", "1 item") : this.t("count", "{n} items", { n: fb.format.num(n) })];
            if (this._previewsToMake > 0) sub.push(this.t("previews", "making previews… {n} to go", { n: fb.format.num(this._previewsToMake) }));
            this.querySelector("#pv-sub").textContent = sub.join(" · ");
            const marking = this.marks.size > 0;
            this.querySelector("#pv-marktools").hidden = !marking;
            this.querySelector("#pv-tools").hidden = marking;
            if (marking) {
                this.querySelector("#pv-marked-count").textContent = this.marks.size === 1
                    ? this.t("marked-1", "1 selected") : this.t("marked", "{n} selected", { n: fb.format.num(this.marks.size) });
                this.querySelector("#pv-mark-unalbum").hidden = this.view.kind !== "album";
                const allFav = [...this.marks].every((id) => this.photoById(id)?.favorite);
                const fav = this.querySelector("#pv-mark-fav");
                fav.querySelector("sac-icon").setAttribute("name", allFav ? "fb-heart-fill" : "fb-heart");
                const label = allFav ? this.t("unfavorite", "Remove from favourites") : this.t("favorite", "Favourite");
                fav.title = label;
                fav.setAttribute("aria-label", label);
            }
            this.querySelector("#pv-zoom-in").disabled = this.zoom >= ZOOM.length - 1;
            this.querySelector("#pv-zoom-out").disabled = this.zoom <= 0;
        }

        async checkStatus() {
            if (document.hidden) return;
            let s;
            try { s = await this.api.status(); } catch { return; }
            const before = this._previewsToMake ?? 0;
            this._previewsToMake = s.previewsToMake || 0;
            // Previews came: the months on screen again, with their pictures.
            if (before !== this._previewsToMake) {
                for (const b of this.blocks) {
                    if (!this.data.has(b.month)) continue;
                    const node = this.nodes.get(`h:${b.month}`);
                    if (node) { this.data.delete(b.month); this.loadMonth(b.month); }
                }
            }
            this.paintBar();
        }

        photoById(id) {
            for (const list of this.data.values()) {
                const p = list.find((x) => x.id === id);
                if (p) return p;
            }
            return null;
        }

        // ───────────────────────────── marking ─────────────────────────────

        clearMarks(paint = true) {
            this.marks.clear();
            this.anchor = -1;
            if (paint) { this.paint(); this.paintBar(); }
        }

        toggleMark(index) {
            const p = this.at(index);
            if (!p) return;
            if (this.marks.has(p.id)) this.marks.delete(p.id); else this.marks.add(p.id);
            this.anchor = index;
        }

        async markRange(from, to) {
            const [a, b] = from <= to ? [from, to] : [to, from];
            for (let i = a; i <= b; i++) {
                const p = this.at(i) ?? await this.fetchAt(i);
                if (p) this.marks.add(p.id);
            }
            this.paint();
            this.paintBar();
        }

        async onCellClick(e) {
            const cell = e.target.closest(".pv-cell");
            if (!cell) return;
            const index = Number(cell.dataset.index);
            if (e.shiftKey && this.anchor >= 0) {
                this.cursor = index;
                await this.markRange(this.anchor, index);
                return;
            }
            if (e.ctrlKey || e.metaKey || this.marks.size > 0) {
                this.cursor = index;
                this.toggleMark(index);
                this.paint();
                this.paintBar();
                return;
            }
            this.cursor = index;
            this.anchor = index;
            this.paint();
            this.openViewer(index);
        }

        _cellItems(cell) {
            const index = Number(cell.dataset.index);
            const p = this.at(index);
            if (!p) return null;
            const several = this.marks.size > 1 && this.marks.has(p.id);
            const ids = several ? [...this.marks] : [p.id];
            const items = [{ id: "open", label: this.t("open", "Open"), icon: "eye", onClick: () => this.openViewer(index) }];
            if (!this.writable) return items;
            items.push({
                id: "select", label: this.marks.has(p.id) ? this.t("unselect", "Deselect") : this.t("select", "Select"), icon: "check",
                onClick: () => { this.toggleMark(index); this.cursor = index; this.paint(); this.paintBar(); },
            });
            items.push("-");
            const allFav = ids.every((id) => this.photoById(id)?.favorite);
            items.push({
                id: "fav", label: allFav ? this.t("unfavorite", "Remove from favourites") : this.t("favorite", "Favourite"),
                icon: allFav ? "fb-heart-fill" : "fb-heart", onClick: () => this.setFavorite(ids, !allFav),
            });
            for (const a of this.albums.filter((x) => x.id !== this.view.album).slice(0, 12))
                items.push({ id: `album:${a.id}`, label: this.t("add-to", "Add to “{name}”", { name: a.name }), icon: "fb-album", onClick: () => this.addToAlbum(a.id, ids) });
            items.push({ id: "new-album", label: this.t("add-to-new", "Add to a new album…"), icon: "plus", onClick: () => this.addToNewAlbum(ids) });
            if (this.view.kind === "album")
                items.push({ id: "unalbum", label: this.t("remove-from-album", "Remove from this album"), icon: "close", onClick: () => this.removeFromAlbum(ids) });
            items.push("-");
            items.push({
                id: "delete", label: several ? this.t("delete-n", "Delete {n}…", { n: ids.length }) : this.t("delete-one", "Delete…"),
                icon: "trash", danger: true, onClick: () => this.trash(ids),
            });
            return items;
        }

        // ───────────────────────────── keys ─────────────────────────────

        async onKey(e) {
            if (e.target !== this.scroller) return;
            const mod = e.ctrlKey || e.metaKey;
            const rows = Math.max(1, Math.floor(this.scroller.clientHeight / (this.cell + GAP)));
            let next = null;
            switch (e.key) {
                case "ArrowRight": next = this.cursor + 1; break;
                case "ArrowLeft":  next = this.cursor - 1; break;
                case "ArrowDown":  next = this.moveRows(1); break;
                case "ArrowUp":    next = this.moveRows(-1); break;
                case "PageDown":   next = this.moveRows(rows); break;
                case "PageUp":     next = this.moveRows(-rows); break;
                case "Home":       next = 0; break;
                case "End":        next = this.total - 1; break;
                case "Enter":
                case " ":
                    if (this.cursor >= 0) { e.preventDefault(); this.openViewer(this.cursor); }
                    return;
                case "Escape":
                    if (this.marks.size) { e.preventDefault(); this.clearMarks(); }
                    return;
                case "Delete":
                case "Backspace":
                    if (e.key === "Backspace" && !mod) return;
                    if (!this.writable) return;
                    e.preventDefault();
                    if (this.marks.size) this.trash([...this.marks]);
                    else if (this.at(this.cursor)) this.trash([this.at(this.cursor).id]);
                    return;
                case "+": case "=": if (!mod) { e.preventDefault(); this.setZoom(this.zoom + 1); } return;
                case "-": if (!mod) { e.preventDefault(); this.setZoom(this.zoom - 1); } return;
                default:
                    if (mod && e.key.toLowerCase() === "a" && this.writable) {
                        e.preventDefault();
                        for (const list of this.data.values()) for (const p of list) this.marks.add(p.id);
                        this.paint();
                        this.paintBar();
                        return;
                    }
                    if (!mod && e.key.toLowerCase() === "f" && this.writable) {
                        const ids = this.marks.size ? [...this.marks] : this.at(this.cursor) ? [this.at(this.cursor).id] : [];
                        if (ids.length) {
                            e.preventDefault();
                            this.setFavorite(ids, !ids.every((id) => this.photoById(id)?.favorite));
                        }
                    }
                    return;
            }
            if (next === null || this.total === 0) return;
            e.preventDefault();
            if (this.cursor < 0) next = Math.max(0, this.firstVisible());
            next = clamp(next, 0, this.total - 1);
            const from = this.anchor >= 0 ? this.anchor : this.cursor >= 0 ? this.cursor : next;
            this.cursor = next;
            if (e.shiftKey && this.writable) {
                this.anchor = from;
                this.marks.clear();
                await this.markRange(from, next);
            } else this.anchor = next;
            this.reveal(next);
            this.paintBar();
        }

        // Up/down within a month keeps the column; across months it lands on
        // the same column of the neighbouring month's nearest row.
        moveRows(n) {
            if (this.cursor < 0) return 0;
            let index = this.cursor;
            for (let k = 0; k < Math.abs(n); k++) {
                const b = this.blockOf(index);
                const i = index - b.start;
                const col = i % this.cols;
                if (n > 0) {
                    if (i + this.cols < b.count) index += this.cols;
                    else {
                        const nb = this.blocks[this.blocks.indexOf(b) + 1];
                        if (!nb) { index = this.total - 1; break; }
                        index = nb.start + Math.min(col, nb.count - 1);
                    }
                } else {
                    if (i - this.cols >= 0) index -= this.cols;
                    else {
                        const pb = this.blocks[this.blocks.indexOf(b) - 1];
                        if (!pb) { index = 0; break; }
                        const lastRow = Math.floor((pb.count - 1) / this.cols) * this.cols;
                        index = pb.start + Math.min(lastRow + col, pb.count - 1);
                    }
                }
            }
            return index;
        }

        // ───────────────────────────── changes ─────────────────────────────

        async setFavorite(ids, favorite) {
            try { await this.api.favorite(ids, favorite); }
            catch (err) { sac.toast(fb.errors.text(err, this.t("err-favorite", "Couldn't change the favourites.")), { kind: "error" }); return; }
            for (const id of ids) { const p = this.photoById(id); if (p) p.favorite = favorite; }
            if (this.view.kind === "favorites") await this.reload();
            else { this.paint(); this.paintBar(); }
            this._viewerSync?.();
        }

        favoriteMarked() {
            const ids = [...this.marks];
            this.setFavorite(ids, !ids.every((id) => this.photoById(id)?.favorite));
        }

        async trash(ids) {
            if (!this.writable || ids.length === 0) return false;
            const answer = await sac.dialog.confirm({
                title: ids.length === 1 ? this.t("delete-title-1", "Delete this photo?") : this.t("delete-title", "Delete {n} photos?", { n: ids.length }),
                message: this.t("delete-msg", "Its files move to the trash of Files — restored there, a photo comes back with its albums."),
                buttons: [
                    { action: "cancel", label: fb.t("fb.common.cancel", "Cancel"), kind: "default" },
                    { action: "delete", label: fb.t("fb.common.delete", "Delete"), kind: "destructive" },
                ],
            });
            if (answer !== "delete") return false;
            try { await this.api.trash(ids); }
            catch (err) { sac.toast(fb.errors.text(err, this.t("err-delete", "Couldn't delete that.")), { kind: "error" }); return false; }
            for (const id of ids) this.marks.delete(id);
            await Promise.all([this.reload(), this.loadAlbums()]);
            sac.toast(ids.length === 1 ? this.t("deleted-1", "Moved to the trash.") : this.t("deleted", "{n} moved to the trash.", { n: ids.length }));
            return true;
        }

        async onAlbumPick(e, ids) {
            const action = e.detail?.action || e.detail?.item?.dataset?.action || "";
            if (action === "new") return this.addToNewAlbum(ids);
            if (action.startsWith("album:")) return this.addToAlbum(action.slice(6), ids);
        }

        async addToAlbum(albumId, ids) {
            try {
                const r = await this.api.albums.add(albumId, ids);
                const name = this.albums.find((a) => a.id === albumId)?.name || "";
                sac.toast(this.t("added-to", "Added to “{name}”.", { name, n: r.added }));
            } catch (err) { sac.toast(fb.errors.text(err, this.t("err-album", "Couldn't change the album.")), { kind: "error" }); return; }
            this.clearMarks();
            await this.loadAlbums();
            this._viewerSync?.();
        }

        async askAlbumName(title, value = "") {
            return sac.dialog.prompt({
                title,
                label: this.t("album-name", "Name"),
                value,
                select: "all",
                validate: (v) => v.trim() ? (v.trim().length > 200 ? this.t("name-too-long", "At most 200 characters.") : null)
                    : this.t("name-required", "An album needs a name."),
            });
        }

        async newAlbum() {
            const name = await this.askAlbumName(this.t("new-album", "New album"));
            if (!name) return null;
            try {
                const album = await this.api.albums.create(name.trim());
                await this.loadAlbums();
                return album;
            } catch (err) { sac.toast(fb.errors.text(err, this.t("err-album", "Couldn't change the album.")), { kind: "error" }); return null; }
        }

        async addToNewAlbum(ids) {
            const album = await this.newAlbum();
            if (album) await this.addToAlbum(album.id, ids);
        }

        async removeFromAlbum(ids) {
            if (this.view.kind !== "album") return;
            try { await this.api.albums.take(this.view.album, ids); }
            catch (err) { sac.toast(fb.errors.text(err, this.t("err-album", "Couldn't change the album.")), { kind: "error" }); return; }
            this.clearMarks(false);
            await Promise.all([this.reload(), this.loadAlbums()]);
        }

        async renameAlbum(id) {
            const album = this.albums.find((a) => a.id === id);
            if (!album) return;
            const name = await this.askAlbumName(this.t("album-rename-title", "Rename album"), album.name);
            if (!name || name.trim() === album.name) return;
            try { await this.api.albums.rename(id, name.trim()); }
            catch (err) { sac.toast(fb.errors.text(err, this.t("err-album", "Couldn't change the album.")), { kind: "error" }); return; }
            await this.loadAlbums();
            this.paintBar();
        }

        async deleteAlbum(id) {
            const album = this.albums.find((a) => a.id === id);
            if (!album) return;
            const answer = await sac.dialog.confirm({
                title: this.t("album-delete-title", "Delete “{name}”?", { name: album.name }),
                message: this.t("album-delete-msg", "Its photos stay in the library."),
                buttons: [
                    { action: "cancel", label: fb.t("fb.common.cancel", "Cancel"), kind: "default" },
                    { action: "delete", label: fb.t("fb.common.delete", "Delete"), kind: "destructive" },
                ],
            });
            if (answer !== "delete") return;
            try { await this.api.albums.remove(id); }
            catch (err) { sac.toast(fb.errors.text(err, this.t("err-album", "Couldn't change the album.")), { kind: "error" }); return; }
            if (this.view.album === id) this.view = { kind: "library", album: null };
            await Promise.all([this.loadAlbums(), this.reload()]);
        }

        // ───────────────────────────── upload ─────────────────────────────

        pick() {
            if (!this.writable) return;
            this.querySelector("#pv-file")?.click();
        }

        async upload(files) {
            files = [...files].filter(Boolean);
            if (!this.writable || files.length === 0) return;
            const bar = this.querySelector("#pv-progress");
            const total = files.reduce((n, f) => n + (f.size || 0), 0) || 1;
            let sent = 0, added = 0, failed = 0, firstError = null;
            bar.hidden = false;
            bar.value = 0;
            for (const file of files) {
                try {
                    await this.api.upload(file, { onProgress: (loaded) => { bar.value = Math.min(100, 100 * (sent + loaded) / total); } });
                    added++;
                } catch (err) { failed++; firstError ??= err; }
                sent += file.size || 0;
                bar.value = Math.min(100, 100 * sent / total);
            }
            bar.hidden = true;
            await this.reload();
            if (failed) sac.toast(fb.errors.text(firstError, this.t("err-upload", "Couldn't add {n} of them.", { n: failed })), { kind: "error" });
            if (added) sac.toast(added === 1 ? this.t("added-1", "1 added.") : this.t("added", "{n} added.", { n: added }));
        }

        // ───────────────────────────── the viewer ─────────────────────────────

        async openViewer(index) {
            const photo = this.at(index) ?? await this.fetchAt(index);
            if (!photo) return;
            if (this._viewer?.isConnected) { this._viewer.close(); }
            let current = index;
            let detail = null;
            let live = false;
            const win = document.createElement("sac-window");
            win.className = "fb-photo-viewer";
            win.setAttribute("escape-closes", "");
            win.setAttribute("controls", "max close");
            win.setAttribute("title", this.t("photo", "Photo"));
            win.innerHTML = `
                <style>
                    sac-window.fb-photo-viewer .pvw { display: flex; height: 100%; min-height: 0; gap: 0; }
                    sac-window.fb-photo-viewer sac-quick-look { flex: 1; min-width: 0; height: 100%; }
                    sac-window.fb-photo-viewer .pvw-info { width: 300px; flex: none; overflow-y: auto; padding: 4px 4px 4px 20px; border-left: 1px solid var(--border); }
                    sac-window.fb-photo-viewer .pvw-info dl { display: grid; grid-template-columns: auto 1fr; gap: 6px 12px; margin: 0; font-size: 0.875rem; }
                    sac-window.fb-photo-viewer .pvw-info dt { color: var(--text-muted); }
                    sac-window.fb-photo-viewer .pvw-info dd { margin: 0; overflow-wrap: anywhere; }
                    sac-window.fb-photo-viewer .pvw-info h3 { margin: 0 0 12px; font-size: 1rem; }
                    sac-window.fb-photo-viewer .pvw-chips { display: flex; flex-wrap: wrap; gap: 4px; }
                    sac-window.fb-photo-viewer .wa-bar .btn.on sac-icon { color: var(--accent); }
                    @media (max-width: 768px) { sac-window.fb-photo-viewer .pvw-info { display: none; } }
                </style>
                <div class="toolbar wa-bar" slot="toolbar">
                    <button type="button" class="btn pv-write" data-act="fav"><sac-icon name="fb-heart"></sac-icon> <span>${esc(this.t("favorite", "Favourite"))}</span></button>
                    <button type="button" class="btn" data-act="live" hidden><sac-icon name="fb-live"></sac-icon> Live</button>
                    <button type="button" class="btn" data-act="info"><sac-icon name="info"></sac-icon> ${esc(this.t("info", "Info"))}</button>
                    <a class="btn" data-act="download" download><sac-icon name="download"></sac-icon> ${esc(this.t("download", "Download"))}</a>
                    <button type="button" class="btn danger pv-write" data-act="delete"><sac-icon name="trash"></sac-icon> ${esc(fb.t("fb.common.delete", "Delete"))}</button>
                </div>
                <div class="pvw">
                    <sac-quick-look></sac-quick-look>
                    <aside class="pvw-info" id="pvw-info"></aside>
                </div>`;
            if (!this.writable) for (const el of win.querySelectorAll(".pv-write")) el.remove();
            const look = win.querySelector("sac-quick-look");
            const info = win.querySelector("#pvw-info");
            let showInfo = true;
            try { showInfo = localStorage.getItem("fb.photos.info") !== "0"; } catch { /* storage off */ }
            info.hidden = !showInfo;
            const btn = (act) => win.querySelector(`[data-act="${act}"]`);

            const sourceFor = (p, d, asLive) => {
                const part = (role) => d?.parts?.find((x) => x.role === role);
                if (asLive && part("live")) {
                    const movie = part("live");
                    return { url: this.files.contentUrl(movie.path, { inline: true }), type: "video/quicktime", name: movie.path.split("/").pop() };
                }
                if (p.kind === "video") {
                    const path = d?.path ?? part("original")?.path;
                    return path ? { url: this.files.contentUrl(path, { inline: true }), type: /\.mp4$|\.m4v$/i.test(path) ? "video/mp4" : "video/quicktime", name: path.split("/").pop() } : null;
                }
                if (p.preview) return { url: this.api.previewUrl(p, "l"), type: "image/webp", name: (d?.path || "").split("/").pop() || "photo.webp" };
                // No preview yet: a picture the browser shows itself, as it is.
                if (d?.path && /\.(jpe?g|png|gif|webp|avif)$/i.test(d.path))
                    return { url: this.files.contentUrl(d.path, { inline: true }), type: "", name: d.path.split("/").pop() };
                return null;
            };

            const paintInfo = () => {
                const p = this.at(current) || photo;
                if (!detail || detail.id !== p.id) { info.innerHTML = ""; return; }
                const when = wallClock(detail);
                const rows = [
                    [this.t("taken", "Taken"), `${fb.format.longDate(when)} ${when.getFullYear()}, ${fb.format.time(when)}`],
                    detail.camera ? [this.t("camera", "Camera"), detail.camera] : null,
                    detail.width && detail.height ? [this.t("size", "Size"), `${detail.width} × ${detail.height}`] : null,
                    detail.duration ? [this.t("length", "Length"), duration(detail.duration)] : null,
                    detail.latitude != null ? [this.t("place", "Place"),
                        `<a href="https://www.openstreetmap.org/?mlat=${detail.latitude}&mlon=${detail.longitude}#map=15/${detail.latitude}/${detail.longitude}" target="_blank" rel="noopener noreferrer">${detail.latitude.toFixed(5)}, ${detail.longitude.toFixed(5)}</a>`, true] : null,
                    [this.t("files", "Files"), detail.parts.map((x) => esc(x.path)).join("<br>"), true],
                ].filter(Boolean);
                const albums = this.albums.filter((a) => detail.albums.includes(a.id));
                info.innerHTML = `
                    <h3>${esc(detail.title || (detail.path || "").split("/").pop())}</h3>
                    <dl>${rows.map(([k, v, html]) => `<dt>${esc(k)}</dt><dd>${html ? v : esc(v)}</dd>`).join("")}</dl>
                    ${albums.length ? `<h3 style="margin-top:18px">${esc(this.t("albums", "Albums"))}</h3>
                        <div class="pvw-chips">${albums.map((a) => `<sac-chip>${esc(a.name)}</sac-chip>`).join("")}</div>` : ""}`;
            };

            const paint = async () => {
                const p = this.at(current) ?? await this.fetchAt(current);
                if (!p) return;
                const when = wallClock(p);
                win.setAttribute("title", fb.format.dateTime(when));
                look.setAttribute("nav", current <= 0 ? (this.total > 1 ? "next" : "") : current >= this.total - 1 ? "prev" : "");
                if (this.total < 2) look.removeAttribute("nav");
                const fav = btn("fav");
                if (fav) {
                    fav.classList.toggle("on", !!p.favorite);
                    fav.querySelector("sac-icon").setAttribute("name", p.favorite ? "fb-heart-fill" : "fb-heart");
                    fav.querySelector("span").textContent = p.favorite ? this.t("unfavorite", "Remove from favourites") : this.t("favorite", "Favourite");
                }
                btn("info").classList.toggle("on", !info.hidden);
                const id = p.id;
                if (!detail || detail.id !== id) {
                    detail = null;
                    live = false;
                    look.source = sourceFor(p, null, false);
                    try { detail = await this.api.get(id); } catch { detail = null; }
                    if ((this.at(current)?.id ?? id) !== id) return;
                }
                const hasLive = !!detail?.parts?.some((x) => x.role === "live");
                btn("live").hidden = !hasLive;
                btn("live").classList.toggle("on", live);
                const src = sourceFor(p, detail, live);
                if (src && look.source?.url !== src.url) look.source = src;
                const original = detail?.parts?.find((x) => x.role === "original") || detail?.parts?.[0];
                const dl = btn("download");
                if (original) { dl.href = this.files.contentUrl(original.path); dl.setAttribute("download", original.path.split("/").pop()); }
                paintInfo();
            };
            this._viewerSync = () => { detail = null; paint(); };

            look.addEventListener("sac:nav", (e) => {
                const next = clamp(current + e.detail.direction, 0, this.total - 1);
                if (next === current) return;
                current = next;
                this.cursor = current;
                this.reveal(current);
                paint();
            });
            look.addEventListener("sac:dismiss", () => win.close());
            win.addEventListener("keydown", (e) => {
                if (e.target.closest?.("input, textarea")) return;
                if (e.key === "i" && !e.ctrlKey && !e.metaKey) { e.preventDefault(); btn("info").click(); }
                else if (e.key === "f" && !e.ctrlKey && !e.metaKey && btn("fav")) { e.preventDefault(); btn("fav").click(); }
                else if ((e.key === "Delete" || (e.key === "Backspace" && (e.metaKey || e.ctrlKey))) && btn("delete")) { e.preventDefault(); btn("delete").click(); }
            });
            btn("info").addEventListener("click", () => {
                info.hidden = !info.hidden;
                try { localStorage.setItem("fb.photos.info", info.hidden ? "0" : "1"); } catch { /* storage off */ }
                paint();
            });
            btn("live").addEventListener("click", () => { live = !live; paint(); });
            btn("fav")?.addEventListener("click", async () => {
                const p = this.at(current);
                if (p) { await this.setFavorite([p.id], !p.favorite); paint(); }
            });
            btn("delete")?.addEventListener("click", async () => {
                const p = this.at(current);
                if (!p) return;
                const total = this.total;
                if (!(await this.trash([p.id]))) return;
                if (total <= 1) { win.close(); return; }
                current = clamp(current, 0, this.total - 1);
                detail = null;
                paint();
            });
            win.addEventListener("sac:close", () => {
                win.remove();
                this._viewer = null;
                this._viewerSync = null;
                this.scroller.focus({ preventScroll: true });
            }, { once: true });

            document.body.appendChild(win);
            this._viewer = win;
            setTimeout(() => {
                win.open();
                win.maximize?.();
                paint();
                look.focus?.();
            }, 0);
        }
    }

    customElements.define("fb-photos-view", FbPhotosView);
    sac.router.register("#/photos", "fb-photos-view", { label: "Photos", icon: "image", palette: false });
})();
