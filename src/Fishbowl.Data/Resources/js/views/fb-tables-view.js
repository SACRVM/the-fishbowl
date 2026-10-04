/**
 * <fb-tables-view>  (#/space/<slug>/tables/<table>, spaces only)
 *
 * The plain "Tables" app of the space-apps spec: the space's tables on the
 * left (name, description, rows), the chosen one on the right in the
 * vendored <sac-data-grid> — sort, filter, edit, add and delete rows like a
 * spreadsheet. The schema is read-only here (an agent or a Designer changes
 * it through the API). The table lives in the URL; a Reader gets the grid in
 * read mode. Column mapping and the data source: fb.tableGrid.
 *
 * Light DOM on the kit's <sac-split collapse> (one pane at a time on a phone).
 */
class FbTablesView extends HTMLElement {
    constructor() {
        super();
        this.tables = [];
        this.current = null;
    }

    async connectedCallback() {
        this.render();
        this.querySelector("#split").addEventListener("sac:resize", (e) => {
            try { localStorage.setItem("fb.tables.split", e.detail.position); } catch { /* storage off */ }
        });
        this.writable = await fb.access.canWrite();
        await this.loadTables();
        if (!this.isConnected) return;
        // Prefix route: the table in the URL.
        this.addEventListener("sac:route", (e) => this.open(decodeURIComponent(e.detail.subpath || "")));
        const sub = (location.hash.split("/tables/")[1] || "").split("?")[0];
        if (sub) this.open(decodeURIComponent(sub));
    }

    render() {
        this.innerHTML = `
            <style>
                fb-tables-view {
                    display: block;
                    height: calc(100dvh - 50px - env(safe-area-inset-top, 0px));
                }
                fb-tables-view [hidden] { display: none !important; }
                fb-tables-view .tb-list {
                    min-height: 100%;
                    background: var(--panel);
                    display: flex;
                    flex-direction: column;
                    padding: 12px;
                    gap: 4px;
                }
                /* The list header like Todos' and Calendar's: a small muted label on the rows' text edge. */
                fb-tables-view .tb-list-title {
                    margin: 0 0 6px 12px;
                    font-family: 'Outfit', sans-serif;
                    font-weight: 700;
                    font-size: 11px;
                    text-transform: uppercase;
                    letter-spacing: 0.1em;
                    color: var(--text-muted);
                }
                fb-tables-view .tb-item {
                    display: block;
                    padding: 8px 12px;
                    border-radius: var(--radius-m);
                    color: var(--text);
                    text-decoration: none;
                }
                fb-tables-view .tb-item:hover { background: var(--hover); }
                fb-tables-view .tb-item.selected { background: var(--accent-tint); }
                fb-tables-view .tb-item .tb-name { font-weight: 600; }
                fb-tables-view .tb-item .tb-meta { color: var(--text-muted); font-size: 13px; }
                fb-tables-view .tb-main {
                    height: 100%;
                    display: flex;
                    flex-direction: column;
                    padding: 24px;
                    gap: 8px;
                    box-sizing: border-box;
                }
                fb-tables-view .tb-head h2 { margin: 0; }
                fb-tables-view .tb-head p { margin: 2px 0 0; color: var(--text-muted); font-size: 13px; }
                fb-tables-view sac-data-grid { flex: 1; min-height: 240px; --grid-bg: var(--bg); }
            </style>
            <sac-split id="split" collapse show="start" position="${this._splitPosition()}" min-start="260px" min-end="360px"
                       aria-label="${fb.t("fb.tables.resize", "Resize the table list")}">
                <aside class="tb-list" slot="start">
                    <div class="tb-list-title">${fb.t("fb.tables.title", "Tables")}</div>
                    <div id="tb-items"></div>
                </aside>
                <section class="tb-main" slot="end">
                    <div class="empty-state" id="tb-empty">
                        <sac-icon name="grid"></sac-icon>
                        <h3 id="tb-empty-title"></h3>
                        <p id="tb-empty-text"></p>
                    </div>
                    <div class="tb-head" id="tb-head" hidden><h2 id="tb-name"></h2><p id="tb-desc"></p></div>
                    <div id="tb-grid-slot"></div>
                </section>
            </sac-split>`;
    }

    // The list's share of the width, as the user left it (sac-split takes a percentage).
    _splitPosition() {
        try { return localStorage.getItem("fb.tables.split") || "28%"; } catch { return "28%"; }
    }

    async loadTables() {
        try { this.tables = await fb.api.tables.list(); }
        catch (err) { console.warn("[fb-tables-view] list failed:", err); this.tables = []; }
        const items = this.querySelector("#tb-items");
        items.replaceChildren(...this.tables.map((t) => {
            const a = document.createElement("a");
            a.className = "tb-item";
            a.dataset.table = t.name;
            a.href = sac.scope.hashFor(`#/tables/${encodeURIComponent(t.name)}`);
            a.innerHTML = `<div class="tb-name"></div><div class="tb-meta"></div>`;
            a.querySelector(".tb-name").textContent = t.name;
            a.querySelector(".tb-meta").textContent = [
                t.rows === 1 ? fb.t("fb.tables.rows-1", "1 row") : fb.t("fb.tables.rows", "{n} rows", { n: t.rows ?? 0 }),
                t.description || "",
            ].filter(Boolean).join(" · ");
            return a;
        }));
        const none = this.tables.length === 0;
        this.querySelector("#tb-empty-title").textContent = none
            ? fb.t("fb.tables.none", "No tables yet")
            : fb.t("fb.tables.pick", "Pick a table");
        this.querySelector("#tb-empty-text").textContent = none
            ? fb.t("fb.tables.none-hint", "Tables are made by an agent or a Designer of this space — ask yours to build what you need.")
            : "";
    }

    async open(name) {
        const def = this.tables.find((t) => t.name === name);
        this.querySelectorAll(".tb-item").forEach((a) => a.classList.toggle("selected", a.dataset.table === name));
        const slot = this.querySelector("#tb-grid-slot");
        if (!def) {
            this.current = null;
            slot.replaceChildren();
            this.querySelector("#tb-head").hidden = true;
            this.querySelector("#tb-empty").hidden = false;
            this.querySelector("#split").show = "start";
            return;
        }
        this.current = name;
        this.querySelector("#tb-empty").hidden = true;
        this.querySelector("#tb-head").hidden = false;
        this.querySelector("#tb-name").textContent = def.name;
        this.querySelector("#tb-desc").textContent = [
            def.description || "",
            def.ownRows ? fb.t("fb.tables.own-rows", "Members change only their own rows.") : "",
        ].filter(Boolean).join(" ");
        this.querySelector("#split").show = "end";

        const { columns, source } = await fb.tableGrid.build(def);
        if (this.current !== name || !this.isConnected) return;   // another table was picked meanwhile
        const grid = document.createElement("sac-data-grid");
        grid.setAttribute("label", def.name);
        grid.setAttribute("mode", this.writable ? "sheet" : "read");
        grid.style.height = "100%";
        grid.columns = columns;
        grid.source = this.writable ? source : { ...source, save: undefined, remove: undefined };
        // The user's column layout per table, in this browser.
        const viewKey = `fb.tables.view.${sac.scope.get().slug}.${def.name}`;
        try { const v = JSON.parse(localStorage.getItem(viewKey) || "null"); if (v) grid.view = v; } catch { /* storage off */ }
        grid.addEventListener("sac:view", (e) => { try { localStorage.setItem(viewKey, JSON.stringify(e.detail)); } catch { /* storage off */ } });
        grid.addEventListener("sac:save", () => this.refreshCounts());
        slot.replaceChildren(grid);
        slot.style.flex = "1";
        slot.style.minHeight = "0";
    }

    async refreshCounts() {
        const current = this.current;
        await this.loadTables();
        this.querySelectorAll(".tb-item").forEach((a) => a.classList.toggle("selected", a.dataset.table === current));
    }
}

customElements.define("fb-tables-view", FbTablesView);
sac.router.register("#/tables/*", "fb-tables-view", { label: "Tables", icon: "grid", palette: false, scope: "scoped" });
