/**
 * <fb-files-view>  (mounted at #/files/*)
 *
 * The Files app (docs/superpowers/specs/2026-09-26-files-app-design.md):
 * a two-pane commander over the workspaces' real files, built from kit
 * parts only — two <sac-file-browser>s side by side (fixed 50/50), <sac-shortcut-bar>
 * for the modern chords, <sac-quick-look> for the preview pane and the
 * quick-look window, <sac-drop-zone overlay> for uploads.
 *
 * Each pane has its own workspace (Personal or a space). The LEFT pane is the
 * app's workspace and lives in the URL: `#/files/Photos/2026` (and
 * `#/space/<slug>/files/…`), a prefix route, so walking folders never
 * remounts the view. The right pane's workspace and folder are a per-viewer
 * convenience in sessionStorage.
 *
 * Everything that crosses panes — Alt+C/Alt+M, drag and drop, paste — goes
 * through POST /files/transfer, the one call that works within and between
 * workspaces. Name clashes ask Replace / Keep both / Skip. Delete trashes
 * without asking; Shift+Delete asks, then deletes for good.
 *
 * Phone (≤768px): one pane; Copy to… / Move to… open a
 * destination picker instead of the other pane, a tap-and-open (sac:choose)
 * shows the quick look, and the shortcut bar folds into the nav's "…".
 */
(function () {
    const RIGHT_KEY = "fb.files.right";
    const baseName = (p) => p.slice(p.lastIndexOf("/") + 1);
    const join = (folder, name) => (folder ? `${folder}/${name}` : name);
    const isPdf = (stat) => /pdf/i.test(stat?.type || "") || /\.pdf$/i.test(stat?.name || "");

    function sizeText(bytes) {
        if (bytes == null) return "";
        if (bytes < 1024) return `${bytes} B`;
        if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(bytes < 10240 ? 1 : 0)} KB`;
        if (bytes < 1024 ** 3) return `${(bytes / 1024 ** 2).toFixed(1)} MB`;
        return `${(bytes / 1024 ** 3).toFixed(1)} GB`;
    }

    function escapeHtml(s) {
        return String(s ?? "").replace(/[&<>"']/g, (c) =>
            ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
    }

    /** The server's message for a refused files call, else a fallback. */
    function explain(err, fallback) {
        const info = fb.api.files.errorInfo(err);
        return info.message || fallback;
    }

    /** A sac-dialog with custom content; resolves { action, all }. */
    function ask({ title, message, buttons, allLabel }) {
        return new Promise((resolve) => {
            const dlg = document.createElement("sac-dialog");
            dlg.setAttribute("title", title);
            dlg.buttons = buttons;
            for (const part of Array.isArray(message) ? message : [message]) {
                const p = document.createElement("p");
                p.textContent = part;
                dlg.appendChild(p);
            }
            let box = null;
            if (allLabel) {
                const label = document.createElement("label");
                label.className = "fv-all";
                box = document.createElement("input");
                box.type = "checkbox";
                label.append(box, document.createTextNode(" " + allLabel));
                dlg.appendChild(label);
            }
            dlg.addEventListener("sac:action", (e) => {
                setTimeout(() => {
                    dlg.remove();
                    resolve({ action: e.detail.action, all: !!box?.checked });
                }, 120);
            }, { once: true });
            document.body.appendChild(dlg);
            setTimeout(() => dlg.open(), 0);
        });
    }

    /** One clash policy for a whole operation: asks per clash until the
     *  user ticks "for the rest too". Resolves "replace" | "rename" | "skip". */
    function clashResolver() {
        let sticky = null;
        return async (name, remaining) => {
            if (sticky) return sticky;
            const { action, all } = await ask({
                title: "A file with this name exists",
                message: `“${name}” is already in the target folder.`,
                allLabel: remaining > 1 ? `Do this for the other ${remaining - 1} too` : null,
                buttons: [
                    { action: "skip", label: "Skip" },
                    { action: "rename", label: "Keep both" },
                    { action: "replace", label: "Replace", kind: "primary" },
                ],
            });
            const answer = action || "skip";
            if (all) sticky = answer;
            return answer;
        };
    }

    class FbFilesView extends HTMLElement {
        connectedCallback() {
            this._spaces = [];
            this._active = "left";
            this._rightMode = "browse";   // browse | preview | props
            this._clipboard = null;
            this._offs = [];
            this.render();
            this._wire();
            this._init();
        }

        disconnectedCallback() {
            this._offs.forEach((off) => { try { off(); } catch { /* already gone */ } });
            this._offs = [];
            this._quickLookWin?.remove();
            this._trashWin?.remove();
            if (window.fb?.toolbar) fb.toolbar.clear();
        }

        /* ------------------------------------------------------------ DOM */

        render() {
            this.innerHTML = `
                <style>
                    fb-files-view {
                        display: flex;
                        flex-direction: column;
                        height: calc(100vh - 50px);
                        height: calc(100dvh - 50px - env(safe-area-inset-top, 0px));
                        background: var(--bg);
                    }
                    fb-files-view [hidden] { display: none !important; }
                    /* Flat, like the notes editor: two equal columns, one
                       hairline between them, no frames, no corners, no
                       splitter — a commander is always 50/50. */
                    fb-files-view .fv-split {
                        flex: 1;
                        min-height: 0;
                        display: grid;
                        grid-template-columns: 1fr 1fr;
                    }
                    /* Header and footer share one height, so the two bars
                       frame each column symmetrically. */
                    fb-files-view { --fv-bar-h: 36px; }
                    fb-files-view .fv-col {
                        display: flex;
                        flex-direction: column;
                        min-width: 0;
                        min-height: 0;
                    }
                    fb-files-view .fv-end { border-left: 1px solid var(--border); }
                    fb-files-view .fv-pane {
                        position: relative;
                        display: flex;
                        flex-direction: column;
                        flex: 1;
                        min-height: 0;
                        min-width: 0;
                        box-sizing: border-box;
                        overflow: hidden;
                    }
                    /* The column's footer: tabs, then the status as plain
                       text. The tab group's own hairline and ours sit on the
                       same bottom line, so the whole footer reads as one bar. */
                    fb-files-view .fv-foot {
                        flex: none;
                        display: flex;
                        align-items: stretch;
                        gap: 12px;
                        height: var(--fv-bar-h);
                        box-sizing: border-box;
                        padding: 0 5px;
                        border-top: 1px solid var(--border);
                        border-bottom: 1px solid var(--border);
                    }
                    fb-files-view .fv-foot sac-tab-group { flex: none; align-self: end; }
                    fb-files-view .fv-status {
                        flex: 1;
                        min-width: 0;
                        align-self: center;
                        color: var(--text-muted);
                        font-size: 0.85rem;
                        white-space: nowrap;
                        overflow: hidden;
                        text-overflow: ellipsis;
                    }
                    @media (max-width: 768px) {
                        fb-files-view .fv-split { grid-template-columns: 1fr; }
                        fb-files-view .fv-end { display: none; }
                    }
                    /* Quiet selection: a hover-tint row, no ring, no bar. */
                    fb-files-view .fv-pane sac-file-browser::part(selected) {
                        background: var(--hover);
                        color: var(--text);
                    }
                    fb-files-view .fv-pane sac-file-browser::part(cursor) { box-shadow: none; }
                    fb-files-view .fv-pane[data-active] sac-file-browser::part(cursor) { background: var(--hover); }
                    /* Which pane has the keys shows in its header only: an
                       accent hairline under it and full-strength text; the
                       other header is muted. */
                    fb-files-view .fv-pane sac-file-browser::part(bar) {
                        /* One centre line: the kit pads the bar at the bottom
                           only; here it is a fixed-height row, items centred,
                           the side gutter the rows' highlight uses (5px). */
                        height: var(--fv-bar-h);
                        box-sizing: border-box;
                        padding: 0 5px;
                        margin-bottom: 4px;
                        border-bottom: 1px solid var(--border);
                        opacity: 0.6;
                        transition: opacity 0.15s, border-color 0.15s;
                    }
                    fb-files-view .fv-pane sac-file-browser::part(crumbs) { align-self: stretch; }
                    fb-files-view .fv-ws { display: inline-flex; align-items: center; }
                    fb-files-view .fv-pane[data-active] sac-file-browser::part(bar) {
                        border-bottom-color: var(--accent);
                        opacity: 1;
                    }
                    fb-files-view .fv-pane sac-file-browser { flex: 1; min-height: 0; }
                    /* The pane is the frame: the kit list's own rounded box
                       sat 1–2px inside it. Transparent, not removed, so the
                       kit's row geometry (border + padding) stays exact. */
                    fb-files-view .fv-pane sac-file-browser::part(list) {
                        border-color: transparent;
                        background: transparent;
                    }
                    fb-files-view .fv-pane sac-file-browser::part(row) { font-family: var(--font-mono); }
                    fb-files-view .fv-pane sac-file-browser::part(marked) { color: var(--accent-warm-text); }
                    fb-files-view .fv-filter {
                        display: flex;
                        gap: 8px;
                        align-items: center;
                        padding: 8px 12px 0;
                    }
                    fb-files-view .fv-filter input { flex: 1; }
                    fb-files-view .fv-ws-btn {
                        display: inline-flex;
                        align-items: center;
                        gap: 6px;
                        box-sizing: border-box;
                        height: 28px;
                        max-width: 16rem;
                        padding: 0 8px;
                        line-height: 1;
                        border: 1px solid var(--border);
                        border-radius: var(--radius-m);
                        background: var(--field);
                        color: inherit;
                        font: inherit;
                        cursor: pointer;
                    }
                    fb-files-view .fv-pane[data-space] .fv-ws-btn { border-color: var(--accent-warm); }
                    fb-files-view .fv-ws-btn sac-icon { --icon-size: 14px; flex: none; }
                    /* One pane on a phone: it is the app's workspace, which the
                       nav's switcher already names — the icon is enough here. */
                    @media (max-width: 768px) {
                        fb-files-view .fv-ws-btn span { display: none; }
                    }
                    fb-files-view .fv-ws-btn span {
                        overflow: hidden;
                        text-overflow: ellipsis;
                        white-space: nowrap;
                    }
                    fb-files-view .fv-preview {
                        display: flex;
                        flex-direction: column;
                    }
                    fb-files-view .fv-preview sac-quick-look { flex: 1; min-height: 0; }
                    fb-files-view .fv-props { overflow-y: auto; }
                    fb-files-view .fv-props-body { padding: 12px 16px; font-size: 0.85rem; }
                    fb-files-view .fv-props-empty { color: var(--text-muted); padding: 24px 16px; }
                    fb-files-view .fv-props h3 {
                        margin: 0 0 12px;
                        font-size: 1rem;
                        overflow-wrap: anywhere;
                    }
                    fb-files-view .fv-props dl {
                        display: grid;
                        grid-template-columns: max-content 1fr;
                        gap: 6px 16px;
                        margin: 0 0 16px;
                    }
                    fb-files-view .fv-props dt { color: var(--text-muted); }
                    fb-files-view .fv-props dd { margin: 0; min-width: 0; overflow-wrap: anywhere; }
                    fb-files-view .fv-props .fv-mono { font-family: var(--font-mono); }
                    fb-files-view .fv-props-actions { display: flex; flex-wrap: wrap; gap: 8px; }
                    fb-files-view .fv-props-actions > .btn { width: auto; flex: none; }
                    fb-files-view .fv-bottom {
                        display: flex;
                        align-items: center;
                        gap: 12px;
                        padding: 6px 4px;
                    }
                    fb-files-view .fv-bottom sac-shortcut-bar { flex: 1; min-width: 0; }
                    fb-files-view .fv-bottom sac-progress { width: 12rem; }
                    .fv-all { display: flex; align-items: center; gap: 6px; margin-top: 12px; }
                    .fv-trash-body { display: flex; flex-direction: column; gap: 12px; padding: 12px; }
                    .fv-trash-list { list-style: none; margin: 0; padding: 0; }
                    .fv-trash-row {
                        display: flex;
                        align-items: center;
                        gap: 12px;
                        padding: 8px 12px;
                        border-radius: var(--radius-m);
                    }
                    .fv-trash-row:hover { background: var(--hover); }
                    .fv-trash-name { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
                    .fv-trash-meta { color: var(--text-muted); white-space: nowrap; }
                    .fv-trash-empty { color: var(--text-muted); padding: 24px 12px; text-align: center; }
                    .fv-pick { display: flex; flex-direction: column; gap: 12px; }
                    .fv-pick sac-file-browser { height: 50dvh; }
                </style>
                <div class="fv-split">
                    <div class="fv-col" data-col="left">
                        <section class="fv-pane" data-side="left"></section>
                        <div class="fv-foot">
                            <sac-tab-group active="browse" aria-label="Left pane">
                                <sac-tab name="browse">Browse</sac-tab>
                            </sac-tab-group>
                            <div class="fv-status" aria-live="polite"></div>
                        </div>
                    </div>
                    <div class="fv-col fv-end" data-col="right">
                        <section class="fv-pane" data-side="right"></section>
                        <section class="fv-pane fv-preview" hidden>
                            <sac-quick-look></sac-quick-look>
                        </section>
                        <section class="fv-pane fv-props" hidden></section>
                        <div class="fv-foot">
                            <sac-tab-group class="fv-right-tabs" active="browse" aria-label="Right pane">
                                <sac-tab name="browse">Browse</sac-tab>
                                <sac-tab name="preview">Preview</sac-tab>
                                <sac-tab name="props">Properties</sac-tab>
                            </sac-tab-group>
                            <div class="fv-status" aria-live="polite"></div>
                        </div>
                    </div>
                </div>
                <div class="fv-bottom">
                    <sac-shortcut-bar group="Files"></sac-shortcut-bar>
                    <sac-progress hidden label="Uploading" value="0" max="100"></sac-progress>
                </div>
            `;
            // One pane on a phone — the same breakpoint the CSS uses.
            this._phone = window.matchMedia("(max-width: 768px)");
            this._bar = this.querySelector("sac-shortcut-bar");
            this._progress = this.querySelector("sac-progress");
            this._previewPane = this.querySelector(".fv-preview");
            this._previewLook = this._previewPane.querySelector("sac-quick-look");
            this._propsPane = this.querySelector(".fv-props");
            this._rightTabs = this.querySelector(".fv-right-tabs");
            this._rightStatus = this.querySelector('.fv-col[data-col="right"] .fv-status');
            this._panes = {
                left: this._buildPane(this.querySelector('.fv-pane[data-side="left"]'), "left",
                    this.querySelector('.fv-col[data-col="left"] .fv-status')),
                right: this._buildPane(this.querySelector('.fv-pane[data-side="right"]'), "right", this._rightStatus),
            };
        }

        _buildPane(section, side, status) {
            section.innerHTML = `
                <div class="fv-filter" hidden>
                    <input type="search" placeholder="Filter this folder" aria-label="Filter this folder">
                </div>
                <sac-file-browser multiple no-thumbnails columns="size date">
                    <sac-menu slot="title" class="fv-ws">
                        <button slot="trigger" class="fv-ws-btn" type="button" aria-label="Workspace">
                            <sac-icon name="user"></sac-icon><span>Personal</span>
                            <sac-icon name="chevron-down"></sac-icon>
                        </button>
                    </sac-menu>
                </sac-file-browser>
                <sac-drop-zone overlay multiple label="Drop to upload here" hint=""></sac-drop-zone>
            `;
            return {
                side,
                section,
                browser: section.querySelector("sac-file-browser"),
                menu: section.querySelector("sac-menu"),
                filterRow: section.querySelector(".fv-filter"),
                filter: section.querySelector(".fv-filter input"),
                status,
                drop: section.querySelector("sac-drop-zone"),
                store: null,
                workspace: null,
                usage: null,
            };
        }

        /* --------------------------------------------------------- wiring */

        _wire() {
            for (const pane of Object.values(this._panes)) {
                const b = pane.browser;
                pane.section.addEventListener("focusin", () => this._setActive(pane.side));
                pane.section.addEventListener("pointerdown", () => this._setActive(pane.side));
                b.addEventListener("sac:cursor", () => { this._paintStatus(pane); this._followPreview(pane); });
                b.addEventListener("sac:mark", () => this._paintStatus(pane));
                b.addEventListener("sac:navigate", (e) => this._navigated(pane, e.detail.path));
                b.addEventListener("sac:choose", (e) => this._quickLook(pane, e.detail.paths[0]));
                b.addEventListener("sac:request-remove", (e) => {
                    e.preventDefault();
                    this._remove(pane, e.detail.paths, e.detail.permanent);
                });
                b.addEventListener("sac:request-rename", (e) => {
                    e.preventDefault();
                    this._rename(pane, e.detail.from, e.detail.to);
                });
                b.addEventListener("sac:drop", (e) => this._dropped(pane, e.detail));
                pane.menu.addEventListener("sac:select", (e) => this._pickWorkspace(pane, e.detail.action));
                pane.drop.addEventListener("sac:files", (e) =>
                    this._upload(pane, e.detail.files, pane.browser.path, e.detail.folders || []));
                pane.filter.addEventListener("input", () => this._applyFilter(pane));
                pane.filter.addEventListener("keydown", (e) => {
                    if (e.key === "Escape") { e.preventDefault(); this._closeFilter(pane); }
                    if (e.key === "Enter" || e.key === "ArrowDown") { e.preventDefault(); this._focusPane(pane.side); }
                });
            }

            // Tab switches panes while a pane's list has focus — the commander
            // rule; everywhere else Tab moves focus as usual.
            this.addEventListener("keydown", (e) => {
                if (e.key !== "Tab" || e.ctrlKey || e.altKey || e.metaKey || this._phone.matches || this._preview) return;
                const inList = Object.values(this._panes).some((p) => p.browser.contains(e.target) || p.browser === e.target);
                if (!inList) return;
                e.preventDefault();
                this._focusPane(this._active === "left" ? "right" : "left");
            });

            // Prefix route: a folder change in the URL, a scope switch.
            this.addEventListener("sac:route", (e) => this._routed(e.detail.subpath));
            const onSpaces = () => this._loadSpaces().then(() => this._paintMenus());
            window.addEventListener("fb:spaces-changed", onSpaces);
            this._offs.push(() => window.removeEventListener("fb:spaces-changed", onSpaces));

            const onPhone = () => {
                if (this._phone.matches && this._preview) this._setRightMode("browse");
                this._setActive("left");
                this._paintBar();
            };
            this._phone.addEventListener("change", onPhone);
            this._offs.push(() => this._phone.removeEventListener("change", onPhone));

            this._previewLook.addEventListener("sac:dismiss", () => this._setRightMode("browse"));
            this._rightTabs.addEventListener("sac:tab-show", (e) => this._setRightMode(e.detail.name));

            // Extra hotkeys the bar doesn't carry: workspace menus, clipboard.
            const reg = (combo, fn, description, opts = {}) =>
                this._offs.push(sac.hotkeys.register(combo, fn, { description, group: "Files", ...opts }));
            reg("alt+1", () => this._panes.left.menu.open(), "Workspace of the left pane");
            reg("alt+2", () => { if (!this._phone.matches) this._panes.right.menu.open(); }, "Workspace of the right pane");
            reg("mod+c", () => this._clip("copy"), "Copy", { skipInInput: true });
            reg("mod+x", () => this._clip("move"), "Cut", { skipInInput: true });
            reg("mod+v", () => this._paste(), "Paste", { skipInInput: true });
            reg("alt+i", () => this._setRightMode(this._rightMode === "props" ? "browse" : "props"), "Properties");
            this._offs.push(sac.shortcuts.add([
                { group: "Files", keys: "Tab", description: "Switch pane" },
                { group: "Files", keys: "Shift + ↑/↓", description: "Mark a range" },
                { group: "Files", keys: ["Ctrl", "click"], description: "Mark one" },
            ]));
            this._offs.push(sac.shortcuts.bind());
        }

        async _init() {
            await this._loadSpaces();
            const left = this._scopeWorkspace();
            let right = null;
            try { right = JSON.parse(sessionStorage.getItem(RIGHT_KEY) || "null"); } catch { right = null; }
            if (!right || !this._known(right.workspace)) right = { workspace: left, path: "" };
            this._bind(this._panes.left, left, sac.router.subpath() || "");
            this._bind(this._panes.right, right.workspace, right.path || "");
            this._paintMenus();
            this._setActive("left", true);
            this._paintToolbar();
            this._paintBar();
            setTimeout(() => this._focusPane("left"), 0);
        }

        async _loadSpaces() {
            try { this._spaces = await fb.api.spaces.list(); }
            catch { this._spaces = []; }
        }

        _scopeWorkspace() {
            const s = sac.scope.get();
            return s.type === "scoped" ? `space:${s.slug}` : "personal";
        }

        _known(ws) {
            return ws === "personal" || this._spaces.some((s) => `space:${s.slug}` === ws);
        }

        _spaceOf(ws) {
            return ws?.startsWith("space:") ? this._spaces.find((s) => `space:${s.slug}` === ws) || null : null;
        }

        _label(ws) {
            if (ws === "personal") return "Personal";
            return this._spaceOf(ws)?.name || ws.slice("space:".length);
        }

        _writable(pane) {
            const space = this._spaceOf(pane.workspace);
            return !space || space.role !== "readonly";
        }

        /** Point a pane at a workspace (and folder). */
        _bind(pane, workspace, path) {
            const changed = pane.workspace !== workspace;
            pane.workspace = workspace;
            if (changed || !pane.store) {
                pane.store = fb.filesStore({ workspace });
                pane.browser.toggleAttribute("readonly", !this._writable(pane));
                pane.browser.store = pane.store;
                // The workspace menu in the title names the workspace; the
                // breadcrumb starts at its files.
                pane.browser.setAttribute("root-label", "Files");
                pane.usage = null;
                this._loadUsage(pane);
            }
            pane.section.toggleAttribute("data-space", workspace !== "personal");
            pane.drop.toggleAttribute("disabled", !this._writable(pane));
            if (path && pane.browser.path !== path) pane.browser.path = path;
            this._paintMenus();
            this._paintBar();
        }

        async _loadUsage(pane) {
            try { pane.usage = await pane.store.api.usage(); }
            catch { pane.usage = null; }
            this._paintStatus(pane);
        }

        _paintMenus() {
            for (const pane of Object.values(this._panes)) {
                const current = pane.workspace;
                const items = [["personal", "Personal", "user"],
                    ...this._spaces.map((s) => [`space:${s.slug}`, s.name, "users"])];
                pane.menu.querySelectorAll(":scope > button:not([slot])").forEach((b) => b.remove());
                for (const [ws, name, icon] of items) {
                    const item = document.createElement("button");
                    item.dataset.action = ws;
                    item.innerHTML = `<sac-icon name="${ws === current ? "check" : icon}"></sac-icon> `;
                    item.append(document.createTextNode(name));
                    pane.menu.appendChild(item);
                }
                const trigger = pane.menu.querySelector(".fv-ws-btn");
                trigger.querySelector("span").textContent = this._label(current || "personal");
                trigger.querySelector("sac-icon").setAttribute("name", current === "personal" ? "user" : "users");
            }
        }

        _pickWorkspace(pane, ws) {
            if (!ws || ws === pane.workspace) return;
            if (pane.side === "left") {
                // The left pane IS the app's workspace — switching it is a
                // scope switch, and the URL follows.
                const slug = ws === "personal" ? null : ws.slice("space:".length);
                location.hash = slug ? `#/space/${encodeURIComponent(slug)}/files` : "#/files";
                return;
            }
            this._bind(pane, ws, "");
            this._saveRight();
        }

        _saveRight() {
            const p = this._panes.right;
            try { sessionStorage.setItem(RIGHT_KEY, JSON.stringify({ workspace: p.workspace, path: p.browser.path || "" })); }
            catch { /* private mode: the pane simply starts fresh next time */ }
        }

        /* ---------------------------------------------------- navigation */

        _navigated(pane, path) {
            this._closeFilter(pane, false);
            this._paintStatus(pane);
            // A folder made with Alt+N leaves focus on <body> when its name
            // field goes away — the keyboard stays in the pane.
            if (document.activeElement === document.body) this._focusPane(pane.side);
            if (pane.side === "right") { this._saveRight(); return; }
            // The left pane's folder is the URL.
            if ((sac.router.subpath() || "") === (path || "")) return;
            const encoded = (path || "").split("/").filter(Boolean).map(encodeURIComponent).join("/");
            location.hash = sac.scope.hashFor(encoded ? `#/files/${encoded}` : "#/files");
        }

        _routed(subpath) {
            // A hash change clears the toolbar (shell.js) — paint it again.
            this._paintToolbar();
            const left = this._panes.left;
            const ws = this._scopeWorkspace();
            if (ws !== left.workspace) {
                this._bind(left, ws, subpath || "");
                this._focusPane("left");
                return;
            }
            if ((left.browser.path || "") !== (subpath || "")) left.browser.path = subpath || "";
        }

        _setActive(side, force) {
            if (this._phone.matches) side = "left";
            if (this._active === side && !force) return;
            this._active = side;
            for (const p of Object.values(this._panes)) p.section.toggleAttribute("data-active", p.side === side);
            this._paintBar();
        }

        _focusPane(side) {
            this._setActive(side);
            const list = this._panes[side].browser.shadowRoot?.querySelector(".list");
            list?.focus({ preventScroll: true });
        }

        get _activePane() { return this._panes[this._active]; }
        get _otherPane() { return this._panes[this._active === "left" ? "right" : "left"]; }

        /** What an action works on: the marked rows, else the cursor row. */
        _targets(pane) {
            const marked = pane.browser.marked;
            if (marked.length) return marked;
            const cursor = pane.browser.cursor;
            return cursor ? [cursor] : [];
        }

        _kindOf(pane, path) {
            return pane.browser.items.find((r) => r.path === path)?.kind || "file";
        }

        /* ------------------------------------------------------ painting */

        _paintStatus(pane) {
            const items = pane.browser.items || [];
            const files = items.filter((r) => r.kind === "file");
            const folders = items.length - files.length;
            const total = files.reduce((n, r) => n + (r.stat?.size || 0), 0);
            const marked = pane.browser.marked;
            let text;
            if (marked.length) {
                const size = items.filter((r) => marked.includes(r.path)).reduce((n, r) => n + (r.stat?.size || 0), 0);
                text = `${marked.length} of ${items.length} marked · ${sizeText(size)}`;
            } else {
                const parts = [];
                if (folders) parts.push(`${folders} folder${folders === 1 ? "" : "s"}`);
                parts.push(`${files.length} file${files.length === 1 ? "" : "s"}`);
                parts.push(sizeText(total));
                text = parts.join(" · ");
            }
            const u = pane.usage;
            if (u && u.quotaBytes > 0) text += ` · ${sizeText(Math.max(0, u.quotaBytes - u.bytes))} free`;
            if (!this._writable(pane)) text += " · read-only";
            // While the right column previews, its footer speaks for the
            // right pane only when it is shown.
            if (pane.side === "right" && this._preview) return;
            pane.status.textContent = text;
        }

        _paintToolbar() {
            const pane = this._activePane;
            const items = [];
            if (pane && this._writable(pane)) {
                items.push({ icon: "upload", title: "Upload (Alt+U)", onClick: () => this._browse() });
            }
            items.push({ icon: "trash", title: "Trash", onClick: () => this._openTrash(this._activePane) });
            fb.toolbar.set(items);
        }

        _paintBar() {
            if (!this._panes?.left?.store) return;
            const pane = this._activePane;
            const other = this._otherPane;
            const canWrite = this._writable(pane);
            const phone = this._phone.matches;
            const items = [];
            if (canWrite) items.push({ id: "new", label: "New folder", combo: "alt+n", icon: "folder-plus", action: () => pane.browser.newFolder() });
            if (canWrite) items.push({ id: "rename", label: "Rename", combo: "alt+r", icon: "pencil", action: () => pane.browser.rename() });
            if (phone) {
                items.push({ id: "copy", label: "Copy to…", combo: "alt+c", icon: "copy", action: () => this._pickAndTransfer("copy") });
                if (canWrite) items.push({ id: "move", label: "Move to…", combo: "alt+m", icon: "move", action: () => this._pickAndTransfer("move") });
            } else if (!this._preview && this._writable(other)) {
                items.push({ id: "copy", label: "Copy →", combo: "alt+c", icon: "copy", action: () => this._transferTo(other, "copy") });
                if (canWrite) items.push({ id: "move", label: "Move →", combo: "alt+m", icon: "move", action: () => this._transferTo(other, "move") });
            }
            if (canWrite) {
                items.push({
                    id: "trash", label: "Trash", combo: "delete", icon: "trash",
                    action: () => this._remove(pane, this._targets(pane), false),
                    shiftLabel: "Delete permanently",
                    shiftAction: () => this._remove(pane, this._targets(pane), true),
                });
            }
            if (!phone) {
                const mode = this._rightMode;
                items.push({ id: "preview", label: mode === "preview" ? "Close preview" : "Preview", combo: "alt+p", icon: "eye", action: () => this._togglePreview() });
                items.push({ id: "props", label: mode === "props" ? "Close properties" : "Properties", combo: "alt+i", icon: "info", action: () => this._setRightMode(mode === "props" ? "browse" : "props") });
            }
            items.push({ id: "look", label: "Quick look", combo: "space", icon: "image", action: () => this._quickLook(pane) });
            if (canWrite) items.push({ id: "upload", label: "Upload", combo: "alt+u", icon: "upload", action: () => this._browse() });
            items.push({ id: "download", label: "Download", combo: "alt+s", icon: "download", action: () => this._download(pane) });
            items.push({ id: "filter", label: "Filter", combo: "mod+f", icon: "search", action: () => this._openFilter(pane) });
            this._bar.items = items;
            this._paintToolbar();
        }

        /* -------------------------------------------------------- filter */

        _openFilter(pane) {
            pane.filterRow.hidden = false;
            pane.filter.focus();
            pane.filter.select();
        }

        _applyFilter(pane) {
            pane.store.filter = pane.filter.value;
            pane.browser.refresh();
        }

        _closeFilter(pane, focus = true) {
            if (pane.filterRow.hidden && !pane.store?.filter) return;
            pane.filterRow.hidden = true;
            if (pane.store?.filter) {
                pane.filter.value = "";
                pane.store.filter = "";
                pane.browser.refresh();
            }
            if (focus) this._focusPane(pane.side);
        }

        /* ------------------------------------------------------ previews */

        _descriptor(pane, path) {
            const stat = pane.store.cached(path) || pane.browser.items.find((r) => r.path === path)?.stat;
            if (!stat) return null;
            return {
                url: pane.store.api.contentUrl(path),
                type: stat.type || "",
                name: stat.name || baseName(path),
                size: stat.size,
            };
        }

        /** The info card's buttons: PDFs open in their own tab. */
        _actionsFor(look, pane, path) {
            look.querySelectorAll('[slot="actions"]').forEach((el) => el.remove());
            const stat = pane.store.cached(path);
            if (!stat) return;
            const buttons = [];
            if (isPdf(stat)) {
                const open = document.createElement("button");
                open.className = "btn";
                open.innerHTML = `<sac-icon name="external-link"></sac-icon> Open in new tab`;
                open.addEventListener("click", () => window.open(pane.store.api.contentUrl(path, { inline: true }), "_blank", "noopener"));
                buttons.push(open);
            }
            if (stat.link) {
                const note = document.createElement("span");
                note.textContent = "A link on the server — Fishbowl never follows it.";
                buttons.push(note);
            } else {
                const dl = document.createElement("a");
                dl.className = "btn";
                dl.href = pane.store.api.contentUrl(path);
                dl.download = stat.name;
                dl.innerHTML = `<sac-icon name="download"></sac-icon> Download`;
                buttons.push(dl);
            }
            for (const b of buttons) { b.slot = "actions"; look.appendChild(b); }
        }

        _show(look, pane, path) {
            const source = path ? this._descriptor(pane, path) : null;
            this._actionsFor(look, pane, path);
            look.source = source;
        }

        /** The right column shows something other than its own pane. */
        get _preview() { return this._rightMode !== "browse"; }

        /** Alt+P: Preview ↔ Browse. */
        _togglePreview(on = this._rightMode !== "preview") {
            this._setRightMode(on ? "preview" : "browse");
        }

        /** The right column's tab: its own pane, or the left pane's cursor
         *  item previewed or described. */
        _setRightMode(mode) {
            if (this._phone.matches || !["browse", "preview", "props"].includes(mode)) mode = "browse";
            const was = this._rightMode;
            this._rightMode = mode;
            this._panes.right.section.hidden = mode !== "browse";
            this._previewPane.hidden = mode !== "preview";
            this._propsPane.hidden = mode !== "props";
            if (this._rightTabs.active !== mode) this._rightTabs.active = mode;
            if (mode === "browse") {
                this._previewLook.source = null;
                this._paintStatus(this._panes.right);
            } else {
                if (was === "browse") this._focusPane("left");
                this._followPreview(this._panes.left);
            }
            this._paintBar();
        }

        _followPreview(pane) {
            if (!this._preview || pane.side !== "left") return;
            const cursor = pane.browser.cursor;
            const kind = cursor ? this._kindOf(pane, cursor) : null;
            if (this._rightMode === "preview") {
                this._show(this._previewLook, pane, kind === "file" ? cursor : null);
            } else {
                this._paintProps(pane, cursor, kind);
            }
            const stat = cursor && kind === "file" ? pane.store.cached(cursor) : null;
            this._rightStatus.textContent = !cursor ? "Nothing selected"
                : stat ? `${stat.name} · ${sizeText(stat.size)}` : `${baseName(cursor)} · folder`;
        }

        /** Properties of the left pane's cursor item. Folders get their
         *  date and item count from one stat + one listing, fetched lazily. */
        _paintProps(pane, path, kind) {
            const box = this._propsPane;
            const token = (this._propsToken = (this._propsToken || 0) + 1);
            if (!path) {
                box.innerHTML = `<div class="fv-props-empty">Nothing selected in the left pane.</div>`;
                return;
            }
            const stat = kind === "file" ? pane.store.cached(path) : null;
            const rows = [];
            const row = (label, value, mono) => rows.push(
                `<dt>${escapeHtml(label)}</dt><dd${mono ? ' class="fv-mono"' : ""}>${value}</dd>`);
            const name = stat?.name || baseName(path);
            row("Kind", escapeHtml(kind === "folder" ? "Folder" : stat?.link ? "Link" : (stat?.type || "File")));
            if (stat) row("Size", `${escapeHtml(sizeText(stat.size))} <span class="fv-mono">(${Number(stat.size).toLocaleString("en-US")} bytes)</span>`);
            if (kind === "folder") row("Items", `<span data-prop="items">…</span>`);
            row("Modified", `<span data-prop="modified">${stat?.modified ? escapeHtml(fb.format.dateTime(new Date(stat.modified))) : "…"}</span>`);
            row("Workspace", escapeHtml(this._label(pane.workspace)));
            row("Path", escapeHtml("/" + path), true);
            box.innerHTML = `
                <div class="fv-props-body">
                    <h3>${escapeHtml(name)}</h3>
                    <dl>${rows.join("")}</dl>
                    <div class="fv-props-actions"></div>
                </div>`;
            const actions = box.querySelector(".fv-props-actions");
            if (kind === "file" && stat && !stat.link) {
                const open = document.createElement("button");
                open.className = "btn";
                open.innerHTML = `<sac-icon name="external-link"></sac-icon> Open in new tab`;
                open.addEventListener("click", () => window.open(pane.store.api.contentUrl(path, { inline: true }), "_blank", "noopener"));
                const dl = document.createElement("a");
                dl.className = "btn";
                dl.href = pane.store.api.contentUrl(path);
                dl.download = stat.name;
                dl.innerHTML = `<sac-icon name="download"></sac-icon> Download`;
                actions.append(open, dl);
            }
            if (kind !== "folder") return;
            const api = pane.store.api;
            api.stat(path).then((e) => {
                if (token !== this._propsToken) return;
                const el = box.querySelector('[data-prop="modified"]');
                if (el) el.textContent = e?.mtime ? fb.format.dateTime(new Date(e.mtime)) : "—";
            }).catch(() => {
                const el = box.querySelector('[data-prop="modified"]');
                if (el && token === this._propsToken) el.textContent = "—";
            });
            api.list(path).then(({ entries }) => {
                if (token !== this._propsToken) return;
                const folders = entries.filter((e) => e.kind === "folder").length;
                const files = entries.length - folders;
                const el = box.querySelector('[data-prop="items"]');
                if (el) el.textContent = `${folders} folder${folders === 1 ? "" : "s"}, ${files} file${files === 1 ? "" : "s"}`;
            }).catch(() => {
                const el = box.querySelector('[data-prop="items"]');
                if (el && token === this._propsToken) el.textContent = "—";
            });
        }

        /** Space: the preview renderer, maximised; ←/→ walk the folder's files. */
        _quickLook(pane, path) {
            if (this._quickLookWin) { this._quickLookWin.close(); return; }
            const files = pane.browser.items.filter((r) => r.kind === "file").map((r) => r.path);
            let current = path || pane.browser.cursor;
            if (!current || !files.includes(current)) return;
            const win = document.createElement("sac-window");
            win.setAttribute("title", baseName(current));
            win.setAttribute("width", "80vw");
            win.setAttribute("height", "80vh");
            const look = document.createElement("sac-quick-look");
            look.style.height = "100%";
            win.appendChild(look);
            document.body.appendChild(win);
            this._quickLookWin = win;
            const paintNav = () => {
                const i = files.indexOf(current);
                if (files.length < 2) look.removeAttribute("nav");
                else look.setAttribute("nav", i === 0 ? "next" : i === files.length - 1 ? "prev" : "");
                win.setAttribute("title", baseName(current));
                this._show(look, pane, current);
            };
            look.addEventListener("sac:nav", (e) => {
                const i = files.indexOf(current) + e.detail.direction;
                if (i < 0 || i >= files.length) return;
                current = files[i];
                pane.browser.cursor = current;
                paintNav();
            });
            look.addEventListener("sac:dismiss", () => win.close());
            win.addEventListener("sac:close", () => {
                win.remove();
                this._quickLookWin = null;
                this._focusPane(pane.side);
            }, { once: true });
            setTimeout(() => {
                win.open();
                win.maximize?.();
                paintNav();
                look.focus();
            }, 0);
        }

        /* -------------------------------------------------- file actions */

        async _rename(pane, from, to) {
            try {
                await pane.store.move(from, to);
            } catch (err) {
                sac.toast(explain(err, "Couldn't rename that."), { kind: "error" });
                return;
            }
            await pane.browser.refresh();
            pane.browser.cursor = to;
            this._refreshTwin(pane);
        }

        /** Delete: to the trash, no question. Shift: gone for good, asked. */
        async _remove(pane, paths, permanent) {
            if (!paths?.length || !this._writable(pane)) return;
            if (permanent) {
                const n = paths.length;
                const answer = await sac.dialog.confirm({
                    title: n === 1 ? "Delete permanently?" : `Delete ${n} items permanently?`,
                    message: n === 1
                        ? `“${baseName(paths[0])}” will be deleted for good — it won't go to the trash.`
                        : `${n} items will be deleted for good — they won't go to the trash.`,
                    buttons: [
                        { action: "cancel", label: "Cancel" },
                        { action: "delete", label: "Delete permanently", kind: "destructive", armAfterMs: 1200 },
                    ],
                });
                if (answer !== "delete") return;
            }
            let done = 0;
            for (const p of paths) {
                try {
                    if (permanent) await pane.store.api.destroy(p);
                    else await pane.store.api.trash(p);
                    done++;
                } catch (err) {
                    sac.toast(explain(err, `Couldn't delete “${baseName(p)}”.`), { kind: "error" });
                    break;
                }
            }
            await pane.browser.refresh();
            this._loadUsage(pane);
            this._refreshTwin(pane);
            if (done) {
                sac.toast(permanent
                    ? `Deleted ${done} item${done === 1 ? "" : "s"} permanently.`
                    : `Moved ${done} item${done === 1 ? "" : "s"} to the trash.`, { kind: "success" });
            }
            this._focusPane(pane.side);
        }

        /** The other pane shows the same folder of the same workspace? Refresh it. */
        _refreshTwin(pane) {
            const other = this._panes[pane.side === "left" ? "right" : "left"];
            if (other.workspace === pane.workspace) other.browser.refresh();
        }

        /**
         * Copy or move paths from one workspace into a folder of another (or
         * the same) — POST /files/transfer, then one clash question per name
         * that is taken, retried with the answer.
         */
        async _transfer({ op, from, paths, to, folder }) {
            if (!paths.length) return 0;
            const resolve = clashResolver();
            let moved = 0;
            let pending = paths.map((p) => ({ path: p, mode: "fail" }));
            while (pending.length) {
                const byMode = new Map();
                for (const item of pending) {
                    if (!byMode.has(item.mode)) byMode.set(item.mode, []);
                    byMode.get(item.mode).push(item.path);
                }
                const clashes = [];
                for (const [mode, group] of byMode) {
                    let res;
                    try {
                        res = await fb.api.files.transfer({ op, from, paths: group, to, folder, onConflict: mode });
                    } catch (err) {
                        sac.toast(explain(err, "Couldn't do that."), { kind: "error" });
                        return moved;
                    }
                    for (const r of res.results || []) {
                        if (!r.error) { moved++; continue; }
                        if (r.error === "name_exists") clashes.push(r.from);
                        else sac.toast(r.message || `Couldn't ${op} “${baseName(r.from)}”.`, { kind: "error" });
                    }
                }
                pending = [];
                for (let i = 0; i < clashes.length; i++) {
                    const answer = await resolve(baseName(clashes[i]), clashes.length - i);
                    if (answer !== "skip") pending.push({ path: clashes[i], mode: answer });
                }
            }
            return moved;
        }

        async _afterTransfer(op, source, targetPane, n, targetLabel) {
            await Promise.all(Object.values(this._panes).map((p) => p.browser.refresh()));
            Object.values(this._panes).forEach((p) => this._loadUsage(p));
            if (n) {
                const verb = op === "move" ? "Moved" : "Copied";
                sac.toast(`${verb} ${n} item${n === 1 ? "" : "s"}${targetLabel ? ` to ${targetLabel}` : ""}.`, { kind: "success" });
            }
            this._focusPane(source?.side || "left");
        }

        /** Alt+C / Alt+M: the selection → the other pane's folder. */
        async _transferTo(target, op) {
            const pane = this._activePane;
            if (target === pane) return;
            if (op === "move" && !this._writable(pane)) return;
            if (!this._writable(target)) return;
            const paths = this._targets(pane);
            const n = await this._transfer({ op, from: pane.workspace, paths, to: target.workspace, folder: target.browser.path || "" });
            await this._afterTransfer(op, pane, target, n);
        }

        /** Phone: Copy to… / Move to… — pick a workspace and a folder. */
        async _pickAndTransfer(op) {
            const pane = this._activePane;
            const paths = this._targets(pane);
            if (!paths.length) return;
            const dest = await this._pickDestination(op, pane.workspace);
            if (!dest) return;
            const n = await this._transfer({ op, from: pane.workspace, paths, to: dest.workspace, folder: dest.folder });
            await this._afterTransfer(op, pane, null, n, `${this._label(dest.workspace)}${dest.folder ? ` › ${dest.folder}` : ""}`);
        }

        _pickDestination(op, workspace) {
            return new Promise((resolve) => {
                const dlg = document.createElement("sac-dialog");
                dlg.setAttribute("title", op === "move" ? "Move to…" : "Copy to…");
                dlg.style.setProperty("--dialog-width", "560px");
                const writable = [["personal", "Personal"],
                    ...this._spaces.filter((s) => s.role !== "readonly").map((s) => [`space:${s.slug}`, s.name])];
                const wrap = document.createElement("div");
                wrap.className = "fv-pick";
                const select = document.createElement("select");
                select.setAttribute("aria-label", "Workspace");
                for (const [ws, name] of writable) {
                    const o = document.createElement("option");
                    o.value = ws;
                    o.textContent = name;
                    if (ws === workspace) o.selected = true;
                    select.appendChild(o);
                }
                const selectWrap = document.createElement("span");
                selectWrap.className = "select";
                selectWrap.appendChild(select);
                const picker = document.createElement("sac-file-browser");
                picker.setAttribute("readonly", "");
                picker.setAttribute("no-thumbnails", "");
                picker.setAttribute("columns", "");
                const bindPicker = () => {
                    picker.setAttribute("root-label", this._label(select.value));
                    picker.store = fb.filesStore({ workspace: select.value });
                };
                select.addEventListener("change", bindPicker);
                wrap.append(selectWrap, picker);
                dlg.appendChild(wrap);
                dlg.buttons = [
                    { action: "cancel", label: "Cancel" },
                    { action: "ok", label: op === "move" ? "Move here" : "Copy here", kind: "primary" },
                ];
                dlg.addEventListener("sac:action", (e) => {
                    const result = e.detail.action === "ok" ? { workspace: select.value, folder: picker.path || "" } : null;
                    setTimeout(() => { dlg.remove(); resolve(result); }, 120);
                }, { once: true });
                document.body.appendChild(dlg);
                setTimeout(() => { dlg.open(); bindPicker(); }, 0);
            });
        }

        /** Drag and drop: rows from a browser (move, Ctrl = copy) or OS files. */
        async _dropped(pane, detail) {
            if (detail.files) { this._upload(pane, detail.files, detail.target || ""); return; }
            const source = Object.values(this._panes).find((p) => p.browser === detail.source);
            if (!source || !detail.paths?.length) return;
            const op = detail.copy ? "copy" : "move";
            if (op === "move" && !this._writable(source)) {
                sac.toast("That workspace is read-only — hold Ctrl to copy instead.", { kind: "warn" });
                return;
            }
            const n = await this._transfer({ op, from: source.workspace, paths: detail.paths, to: pane.workspace, folder: detail.target || "" });
            await this._afterTransfer(op, source, pane, n);
        }

        /** mod+C / mod+X: remember the selection; mod+V drops it here. */
        _clip(op) {
            const pane = this._activePane;
            const paths = this._targets(pane);
            if (!paths.length) return;
            if (op === "move" && !this._writable(pane)) return;
            this._clipboard = { op, workspace: pane.workspace, paths };
            const n = paths.length;
            sac.toast(`${op === "move" ? "Cut" : "Copied"} ${n} item${n === 1 ? "" : "s"} — paste with ${sac.hotkeys.format("mod+v")}.`);
        }

        async _paste() {
            const clip = this._clipboard;
            const pane = this._activePane;
            if (!clip || !this._writable(pane)) return;
            const n = await this._transfer({ op: clip.op, from: clip.workspace, paths: clip.paths, to: pane.workspace, folder: pane.browser.path || "" });
            if (clip.op === "move") this._clipboard = null;
            await this._afterTransfer(clip.op, pane, pane, n);
        }

        /* ------------------------------------------------ upload/download */

        _browse() {
            const pane = this._activePane;
            if (!this._writable(pane)) return;
            pane.drop.browse();
        }

        async _freeName(api, folder, name) {
            const dot = name.lastIndexOf(".");
            const stem = dot > 0 ? name.slice(0, dot) : name;
            const ext = dot > 0 ? name.slice(dot) : "";
            for (let i = 2; i < 1000; i++) {
                const candidate = `${stem} (${i})${ext}`;
                try { await api.stat(join(folder, candidate)); }
                catch (err) { if (err.status === 404) return candidate; throw err; }
            }
            throw new Error("No free name");
        }

        async _upload(pane, files, folder, folders = []) {
            if (!this._writable(pane) || (!files?.length && !folders.length)) return;
            const api = pane.store.api;
            for (const f of folders) {
                try { await api.createFolder(join(folder, f)); }
                catch (err) { if (err.status !== 409) { sac.toast(explain(err, "Couldn't create a folder."), { kind: "error" }); return; } }
            }
            const total = files.reduce((n, f) => n + f.size, 0) || 1;
            let base = 0;
            let done = 0;
            const resolve = clashResolver();
            this._progress.hidden = false;
            this._progress.value = "0";
            const clashes = [];
            const send = async (file, path, mode) => {
                await api.upload(path, file, {
                    mode,
                    onProgress: (loaded) => { this._progress.value = String(Math.round(((base + loaded) / total) * 100)); },
                });
                done++;
            };
            for (const file of files) {
                const rel = file.relativePath || file.name;
                const path = join(folder, rel);
                try {
                    await send(file, path, "create");
                } catch (err) {
                    if (err.status === 412) clashes.push({ file, path });
                    else { sac.toast(explain(err, `Couldn't upload “${file.name}”.`), { kind: "error" }); break; }
                }
                base += file.size;
                this._progress.value = String(Math.round((base / total) * 100));
            }
            for (let i = 0; i < clashes.length; i++) {
                const { file, path } = clashes[i];
                const answer = await resolve(baseName(path), clashes.length - i);
                try {
                    if (answer === "replace") await send(file, path, "replace");
                    else if (answer === "rename") {
                        const dir = path.includes("/") ? path.slice(0, path.lastIndexOf("/")) : "";
                        await send(file, join(dir, await this._freeName(api, dir, baseName(path))), "create");
                    }
                } catch (err) {
                    sac.toast(explain(err, `Couldn't upload “${file.name}”.`), { kind: "error" });
                }
            }
            this._progress.hidden = true;
            await pane.browser.refresh();
            this._loadUsage(pane);
            this._refreshTwin(pane);
            if (done) sac.toast(`Uploaded ${done} file${done === 1 ? "" : "s"}.`, { kind: "success" });
        }

        _download(pane) {
            const paths = this._targets(pane).filter((p) => this._kindOf(pane, p) === "file");
            if (!paths.length) {
                sac.toast("Pick a file to download — folders come as ZIPs later.", { kind: "info" });
                return;
            }
            for (const p of paths) {
                const a = document.createElement("a");
                a.href = pane.store.api.contentUrl(p);
                a.download = baseName(p);
                document.body.appendChild(a);
                a.click();
                a.remove();
            }
        }

        /* ---------------------------------------------------------- trash */

        async _openTrash(pane) {
            this._trashWin?.remove();
            const win = document.createElement("sac-window");
            win.setAttribute("title", `Trash — ${this._label(pane.workspace)}`);
            win.setAttribute("width", "640px");
            win.setAttribute("height", "520px");
            win.innerHTML = `<div class="fv-trash-body">
                <ul class="fv-trash-list"></ul>
                <div><button class="btn fv-trash-emptyall" type="button" hidden>Empty trash</button></div>
            </div>`;
            document.body.appendChild(win);
            this._trashWin = win;
            win.addEventListener("sac:close", () => { win.remove(); if (this._trashWin === win) this._trashWin = null; }, { once: true });
            const api = pane.store.api;
            const writable = this._writable(pane);
            const list = win.querySelector(".fv-trash-list");
            const emptyAll = win.querySelector(".fv-trash-emptyall");
            const paint = async () => {
                let entries = [];
                try { entries = await api.trashList(); }
                catch (err) { sac.toast(explain(err, "Couldn't read the trash."), { kind: "error" }); }
                list.innerHTML = entries.length ? "" : `<li class="fv-trash-empty">The trash is empty.</li>`;
                emptyAll.hidden = !entries.length || !writable;
                for (const e of entries) {
                    const li = document.createElement("li");
                    li.className = "fv-trash-row";
                    li.dataset.id = e.id;
                    li.innerHTML = `
                        <sac-icon name="${e.kind === "folder" ? "folder" : "document"}"></sac-icon>
                        <span class="fv-trash-name" title="${escapeHtml(e.originalPath)}">${escapeHtml(e.originalPath)}</span>
                        <span class="fv-trash-meta">${escapeHtml(sizeText(e.size))} · ${escapeHtml(fb.format.dateTime(new Date(e.deletedAt)))}</span>
                        ${writable ? `<button class="btn fv-restore" type="button">Restore</button>
                        <button class="icon-btn fv-purge" type="button" title="Delete permanently" aria-label="Delete permanently"><sac-icon name="trash"></sac-icon></button>` : ""}`;
                    list.appendChild(li);
                }
            };
            list.addEventListener("click", async (ev) => {
                const row = ev.target.closest(".fv-trash-row");
                if (!row) return;
                const id = row.dataset.id;
                const name = row.querySelector(".fv-trash-name").textContent;
                if (ev.target.closest(".fv-restore")) {
                    try {
                        await api.restore(id);
                    } catch (err) {
                        if (err.status !== 409) { sac.toast(explain(err, "Couldn't restore that."), { kind: "error" }); return; }
                        const { action } = await ask({
                            title: "Something is in the way",
                            message: `“${name}” exists again at its old place.`,
                            buttons: [{ action: "cancel", label: "Cancel" }, { action: "rename", label: "Restore as a copy", kind: "primary" }],
                        });
                        if (action !== "rename") return;
                        try { await api.restore(id, "rename"); }
                        catch (err2) { sac.toast(explain(err2, "Couldn't restore that."), { kind: "error" }); return; }
                    }
                    sac.toast("Restored.", { kind: "success" });
                } else if (ev.target.closest(".fv-purge")) {
                    const answer = await sac.dialog.confirm({
                        title: "Delete permanently?",
                        message: `“${name}” will be gone for good.`,
                        buttons: [{ action: "cancel", label: "Cancel" }, { action: "delete", label: "Delete permanently", kind: "destructive", armAfterMs: 1200 }],
                    });
                    if (answer !== "delete") return;
                    try { await api.purge(id); }
                    catch (err) { sac.toast(explain(err, "Couldn't delete that."), { kind: "error" }); return; }
                } else {
                    return;
                }
                await paint();
                Object.values(this._panes).forEach((p) => { if (p.workspace === pane.workspace) { p.browser.refresh(); this._loadUsage(p); } });
            });
            emptyAll.addEventListener("click", async () => {
                const answer = await sac.dialog.confirm({
                    title: "Empty the trash?",
                    message: "Everything in it will be gone for good.",
                    buttons: [{ action: "cancel", label: "Cancel" }, { action: "empty", label: "Empty trash", kind: "destructive", armAfterMs: 1200 }],
                });
                if (answer !== "empty") return;
                try { await api.emptyTrash(); }
                catch (err) { sac.toast(explain(err, "Couldn't empty the trash."), { kind: "error" }); return; }
                await paint();
                this._loadUsage(pane);
            });
            setTimeout(() => win.open(), 0);
            await paint();
        }
    }

    customElements.define("fb-files-view", FbFilesView);
    sac.router.register("#/files/*", "fb-files-view", { label: "Files", icon: "folder", palette: false });
})();
