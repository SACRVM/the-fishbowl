/**
 * <fb-contacts-view>  (mounted at #/contacts, personal and space)
 *
 * People and organisations (space-apps spec, phase 6) on the kit's
 * <sac-split>, like Todos: the list (search, a People / Organisations filter,
 * "+" for a new person or organisation) and the open contact's fields in
 * sections — saved as you type. A person belongs to at most one
 * organisation; an organisation shows its people; both show the table rows
 * that link to them. The toolbar imports (vCard / CSV), exports, and finds
 * duplicates to merge. A space Reader reads only.
 *
 * Light-DOM so the kit's tokens and classes apply; view CSS is scoped with
 * `fb-contacts-view`.
 */
class FbContactsView extends HTMLElement {
    constructor() {
        super();
        this.contacts = [];
        this.selectedId = null;
        this.filter = "all";
        this.query = "";
        this._saveTimer = null;
    }

    async connectedCallback() {
        this.render();
        this.writable = await fb.access.canWrite();
        this.toggleAttribute("readonly", !this.writable);
        this.querySelector("#cv-new").hidden = !this.writable;
        await this.load();
        if (!this.isConnected) return;
        this.paintToolbar();
    }

    disconnectedCallback() {
        this.flush();
        if (window.fb?.toolbar) fb.toolbar.clear();
    }

    t(key, fallback, vars) { return fb.t(`fb.contacts.${key}`, fallback, vars); }

    // The list's share of the width, as the user left it (like Notes and Todos).
    _splitPosition() {
        try { return localStorage.getItem("fb.contacts.split") || "28%"; } catch { return "28%"; }
    }

    async load() {
        try { this.contacts = await fb.api.contacts.list(); }
        catch (err) { console.error("[fb-contacts-view] list failed:", err); this.contacts = []; }
        this.renderList();
        if (this.selectedId && !this.byId(this.selectedId)) this.open(null);
    }

    byId(id) { return this.contacts.find((c) => c.id === id) || null; }
    orgName(id) { return this.byId(id)?.name || ""; }

    render() {
        const t = (k, f) => this.t(k, f);
        this.innerHTML = `
            <style>
                fb-contacts-view {
                    display: block;
                    height: calc(100vh - 50px);
                    height: calc(100dvh - 50px - env(safe-area-inset-top, 0px));
                    background: var(--bg);
                }
                fb-contacts-view [hidden] { display: none !important; }
                fb-contacts-view .cv-list-pane { min-height: 100%; background: var(--panel); display: flex; flex-direction: column; }
                fb-contacts-view .cv-search { padding: 12px 12px 0; }
                fb-contacts-view .cv-search input { width: 100%; box-sizing: border-box; }
                /* The list header like Todos': a small muted label on the rows' text edge, icon actions on the right. */
                fb-contacts-view .cv-head { display: flex; align-items: center; gap: 2px; padding: 12px 12px 6px 24px; }
                fb-contacts-view .cv-head-title {
                    flex: 1;
                    font-family: 'Outfit', sans-serif;
                    font-weight: 700;
                    font-size: 11px;
                    text-transform: uppercase;
                    letter-spacing: 0.1em;
                    color: var(--text-muted);
                }
                fb-contacts-view .cv-head .icon-btn { color: var(--text-muted); }
                fb-contacts-view .cv-head .icon-btn.active {
                    background: var(--accent-tint);
                    color: var(--accent);
                }
                fb-contacts-view .cv-items { flex: 1; overflow-y: auto; padding: 2px 12px 12px; }
                fb-contacts-view .cv-item {
                    display: flex; align-items: center; gap: 10px;
                    padding: 8px 12px; border-radius: var(--radius-m); cursor: pointer;
                    border: 1px solid transparent; margin-bottom: 2px;
                }
                fb-contacts-view .cv-item:hover { background: var(--hover); }
                fb-contacts-view .cv-item.selected { background: var(--accent-tint); border-color: color-mix(in srgb, var(--accent) 28%, transparent); }
                fb-contacts-view .cv-item-text { min-width: 0; }
                fb-contacts-view .cv-item-name { font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
                fb-contacts-view .cv-item-sub { color: var(--text-muted); font-size: 0.8125rem; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
                fb-contacts-view .cv-item sac-icon { color: var(--text-muted); flex: none; }
                fb-contacts-view .cv-editor-pane { height: 100%; overflow-y: auto; }
                fb-contacts-view .cv-editor { max-width: 760px; margin: 0 auto; padding: 24px 24px 40px; }
                fb-contacts-view .cv-empty { min-height: 60%; justify-content: center; }
                fb-contacts-view .cv-title { display: flex; align-items: center; gap: 10px; margin-bottom: 8px; }
                fb-contacts-view .cv-title h2 { margin: 0; flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; }
                fb-contacts-view .cv-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(200px, 1fr)); gap: 10px 14px; margin: 8px 0 18px; }
                fb-contacts-view .cv-grid label, fb-contacts-view .cv-list label { display: grid; gap: 4px; font-size: 0.8125rem; color: var(--text-muted); }
                fb-contacts-view .cv-grid input, fb-contacts-view .cv-list input, fb-contacts-view textarea { width: 100%; box-sizing: border-box; color: var(--text); }
                fb-contacts-view .cv-wide { grid-column: 1 / -1; }
                fb-contacts-view .cv-list { margin: 8px 0 12px; display: grid; gap: 8px; }
                fb-contacts-view .cv-list:empty { display: none; }
                fb-contacts-view .cv-row { display: grid; grid-template-columns: 120px 1fr auto; gap: 8px; align-items: end; }
                fb-contacts-view .cv-addr { display: grid; grid-template-columns: 120px 2fr 1fr 1.5fr 1fr 1fr auto; gap: 8px; align-items: end; }
                @media (max-width: 768px) {
                    fb-contacts-view .cv-row, fb-contacts-view .cv-addr { grid-template-columns: 1fr auto; }
                    fb-contacts-view .cv-row label:first-child, fb-contacts-view .cv-addr label:first-child { grid-column: 1 / -1; }
                }
                fb-contacts-view textarea { min-height: 90px; resize: vertical; }
                fb-contacts-view .cv-links a { cursor: pointer; }
                fb-contacts-view[readonly] .cv-write { display: none !important; }
            </style>
            <sac-split id="split" collapse show="start" position="${this._splitPosition()}" min-start="260px" min-end="360px"
                       aria-label="${t("resize", "Resize the contact list")}">
                <aside class="cv-list-pane" slot="start">
                    <div class="cv-search">
                        <input type="search" id="cv-search" placeholder="${t("search", "Search contacts")}"/>
                    </div>
                    <div class="cv-head">
                        <span class="cv-head-title" id="cv-head-title"></span>
                        <button type="button" class="icon-btn" data-filter="person" title="${t("only-people", "Only people")}" aria-label="${t("only-people", "Only people")}"><sac-icon name="user"></sac-icon></button>
                        <button type="button" class="icon-btn" data-filter="organisation" title="${t("only-organisations", "Only organisations")}" aria-label="${t("only-organisations", "Only organisations")}"><sac-icon name="users"></sac-icon></button>
                        <sac-menu id="cv-new">
                            <button slot="trigger" type="button" class="icon-btn" title="${t("new", "New contact")}" aria-label="${t("new", "New contact")}"><sac-icon name="plus"></sac-icon></button>
                            <button type="button" data-action="person"><sac-icon name="user"></sac-icon> ${t("new-person", "New person")}</button>
                            <button type="button" data-action="organisation"><sac-icon name="users"></sac-icon> ${t("new-organisation", "New organisation")}</button>
                        </sac-menu>
                    </div>
                    <div class="cv-items" id="cv-items"></div>
                </aside>
                <main class="cv-editor-pane" slot="end">
                    <div class="empty-state cv-empty" id="cv-empty">
                        <sac-icon name="contact"></sac-icon>
                        <h3>${t("none-open", "No contact open")}</h3>
                    </div>
                    <div class="cv-editor" id="cv-editor" hidden></div>
                </main>
            </sac-split>`;

        // People / organisations only: one toggle at a time, the label says what the list shows.
        const titles = { all: t("all-contacts", "All contacts"), person: t("people", "People"), organisation: t("organisations", "Organisations") };
        const showFilter = () => {
            for (const b of this.querySelectorAll(".cv-head [data-filter]")) b.classList.toggle("active", b.dataset.filter === this.filter);
            this.querySelector("#cv-head-title").textContent = titles[this.filter];
        };
        showFilter();
        for (const btn of this.querySelectorAll(".cv-head [data-filter]")) {
            btn.addEventListener("click", () => {
                this.filter = this.filter === btn.dataset.filter ? "all" : btn.dataset.filter;
                showFilter();
                this.renderList();
            });
        }
        this.querySelector("#split").addEventListener("sac:resize", (e) => {
            try { localStorage.setItem("fb.contacts.split", e.detail.position); } catch { /* storage off */ }
        });
        this.querySelector("#cv-search").addEventListener("input", (e) => { this.query = e.target.value.trim().toLowerCase(); this.renderList(); });
        this.querySelector("#cv-new").addEventListener("sac:select", (e) => this.create(e.detail.action));
    }

    // ------------------------------------------------------------ list --

    matches(c) {
        if (this.filter !== "all" && c.kind !== this.filter) return false;
        if (!this.query) return true;
        const hay = [c.name, c.role, this.orgName(c.organisationId), ...(c.emails || []).map((e) => e.value), ...(c.phones || []).map((p) => p.value), c.customerNumber]
            .filter(Boolean).join(" ").toLowerCase();
        return this.query.split(/\s+/).every((w) => hay.includes(w));
    }

    subline(c) {
        if (c.kind === "organisation") {
            const n = this.contacts.filter((p) => p.organisationId === c.id).length;
            return [c.legalForm, c.industry, n === 1 ? this.t("people-1", "1 person") : n ? this.t("people-n", "{n} people", { n }) : null].filter(Boolean).join(" · ");
        }
        return [c.role, this.orgName(c.organisationId), (c.emails || [])[0]?.value].filter(Boolean).join(" · ");
    }

    renderList() {
        const box = this.querySelector("#cv-items");
        if (!box) return;
        const list = this.contacts.filter((c) => this.matches(c));
        if (!list.length) {
            box.innerHTML = `<p class="muted" style="padding: 8px 12px">${this.contacts.length ? this.t("no-match", "Nothing matches.") : this.t("empty", "No contacts yet.")}</p>`;
            return;
        }
        box.replaceChildren(...list.map((c) => {
            const row = document.createElement("div");
            row.className = "cv-item" + (c.id === this.selectedId ? " selected" : "");
            row.dataset.id = c.id;
            row.innerHTML = `<sac-icon name="${c.kind === "organisation" ? "users" : "user"}"></sac-icon>
                <div class="cv-item-text"><div class="cv-item-name"></div><div class="cv-item-sub"></div></div>`;
            row.querySelector(".cv-item-name").textContent = c.name;
            row.querySelector(".cv-item-sub").textContent = this.subline(c);
            row.addEventListener("click", () => this.open(c.id));
            return row;
        }));
    }

    // ---------------------------------------------------------- editor --

    async create(kind) {
        if (!this.writable) return;
        try {
            const body = kind === "organisation"
                ? { kind, name: this.t("new-organisation-name", "New organisation") }
                : { kind: "person", firstName: this.t("new-person-first", "New"), lastName: this.t("new-person-last", "person") };
            const made = await fb.api.contacts.create(body);
            await this.load();
            this.open(made.id);
        } catch (err) {
            sac.toast?.(fb.errors.text(err, this.t("create-failed", "Couldn't create the contact.")), { kind: "error" });
        }
    }

    open(id) {
        this.flush();
        this.selectedId = id;
        this.renderList();
        const c = id ? this.byId(id) : null;
        this.querySelector("#cv-empty").hidden = !!c;
        const ed = this.querySelector("#cv-editor");
        ed.hidden = !c;
        if (c) {
            this.editing = structuredClone(c);
            this.renderEditor();
            if (matchMedia("(max-width: 768px)").matches) this.querySelector("#split").show = "end";
        }
        this.paintToolbar();
    }

    field(label, key, opts = {}) {
        const v = this.editing[key] ?? "";
        return `<label class="${opts.wide ? "cv-wide" : ""}">${label}<input data-key="${key}" value="${fb.escape ? fb.escape(v) : String(v).replace(/&/g, "&amp;").replace(/"/g, "&quot;").replace(/</g, "&lt;")}" ${opts.type ? `type="${opts.type}"` : ""} ${this.writable ? "" : "readonly"}/></label>`;
    }

    renderEditor() {
        const c = this.editing;
        const t = (k, f) => this.t(k, f);
        const ed = this.querySelector("#cv-editor");
        const person = c.kind === "person";
        ed.innerHTML = `
            <div class="cv-title">
                <sac-icon name="${person ? "user" : "users"}"></sac-icon>
                <h2 id="cv-name"></h2>
                <sac-chip label="${person ? t("person", "Person") : t("organisation", "Organisation")}"></sac-chip>
            </div>
            <sac-section title="${t("names", "Name")}"></sac-section>
            <div class="cv-grid">
                ${person
                    ? this.field(t("salutation", "Salutation"), "salutation") + this.field(t("honorific", "Title"), "honorific")
                      + this.field(t("first-name", "First name"), "firstName") + this.field(t("last-name", "Last name"), "lastName")
                    : this.field(t("org-name", "Name"), "name") + this.field(t("legal-form", "Legal form"), "legalForm")}
            </div>
            ${person ? `
            <sac-section title="${t("work", "Work")}"></sac-section>
            <div class="cv-grid">
                <label>${t("organisation", "Organisation")}<sac-select id="cv-org" size="regular" ${this.writable ? "" : "disabled"}></sac-select></label>
                ${this.field(t("role", "Role"), "role")}
                <label>${t("birthday", "Birthday")}<sac-date-field id="cv-birthday" size="regular" ${this.writable ? "" : "disabled"}></sac-date-field></label>
            </div>` : ""}
            <sac-section title="${t("reach", "Email, phone, address")}"></sac-section>
            <div class="cv-list" id="cv-emails"></div>
            <div class="cv-list" id="cv-phones"></div>
            <div class="cv-list" id="cv-addresses"></div>
            <div class="toolbar cv-write">
                <button type="button" class="btn" data-add="emails"><sac-icon name="plus"></sac-icon> ${t("add-email", "Email")}</button>
                <button type="button" class="btn" data-add="phones"><sac-icon name="plus"></sac-icon> ${t("add-phone", "Phone")}</button>
                <button type="button" class="btn" data-add="addresses"><sac-icon name="plus"></sac-icon> ${t("add-address", "Address")}</button>
            </div>
            <div class="cv-grid">${this.field(t("website", "Website"), "website", { wide: true })}</div>
            <sac-section title="${t("business", "Business")}"></sac-section>
            <div class="cv-grid">
                ${!person ? this.field(t("industry", "Industry"), "industry") : ""}
                ${this.field(t("customer-number", "Customer number"), "customerNumber")}
                ${this.field(t("vat-id", "VAT ID"), "vatId")}
                ${this.field(t("tax-number", "Tax number"), "taxNumber")}
                ${!person ? this.field(t("trade-register", "Trade register"), "tradeRegister") : ""}
            </div>
            <sac-section title="${t("bank", "Bank")}"></sac-section>
            <div class="cv-grid">
                ${this.field("IBAN", "iban")}${this.field("BIC", "bic")}${this.field(t("account-holder", "Account holder"), "accountHolder")}
            </div>
            <sac-section title="${t("more", "More")}"></sac-section>
            <div class="cv-grid">
                ${this.field(t("language", "Language"), "language")}
                <label>${t("tags", "Tags (comma-separated)")}<input data-tags value="" ${this.writable ? "" : "readonly"}/></label>
                <label class="cv-wide">${t("notes", "Notes")}<textarea data-key="notes" ${this.writable ? "" : "readonly"}></textarea></label>
            </div>
            <div id="cv-links"></div>`;

        ed.querySelector("#cv-name").textContent = c.name;
        ed.querySelector("[data-tags]").value = (c.tags || []).join(", ");
        ed.querySelector("textarea[data-key=notes]").value = c.notes || "";
        for (const input of ed.querySelectorAll("[data-key]")) {
            input.addEventListener("input", () => { this.editing[input.dataset.key] = input.value; this.changed(); });
        }
        ed.querySelector("[data-tags]").addEventListener("input", (e) => {
            this.editing.tags = e.target.value.split(",").map((s) => s.trim()).filter(Boolean);
            this.changed();
        });
        if (person) {
            const org = ed.querySelector("#cv-org");
            org.options = [{ value: "", label: t("no-organisation", "—") },
                ...this.contacts.filter((x) => x.kind === "organisation").map((o) => ({ value: o.id, label: o.name }))];
            org.value = c.organisationId || "";
            org.addEventListener("change", () => { this.editing.organisationId = org.value || null; this.changed(); });
            const bday = ed.querySelector("#cv-birthday");
            bday.value = c.birthday || "";
            bday.addEventListener("change", () => { this.editing.birthday = bday.value || null; this.changed(); });
        }
        for (const btn of ed.querySelectorAll("[data-add]")) {
            btn.addEventListener("click", () => {
                const list = btn.dataset.add;
                this.editing[list] = [...(this.editing[list] || []), list === "addresses" ? { label: "" } : { label: "", value: "" }];
                this.renderLists();
            });
        }
        this.renderLists();
        this.renderLinks();
    }

    renderLists() {
        const ed = this.querySelector("#cv-editor");
        const t = (k, f) => this.t(k, f);
        const ro = this.writable ? "" : "readonly";
        const make = (list, html, bind) => {
            const box = ed.querySelector(`#cv-${list}`);
            box.replaceChildren(...(this.editing[list] || []).map((item, i) => {
                const row = document.createElement("div");
                row.className = list === "addresses" ? "cv-addr" : "cv-row";
                row.innerHTML = html + `<button type="button" class="icon-btn cv-write" title="${t("remove", "Remove")}" aria-label="${t("remove", "Remove")}"><sac-icon name="close"></sac-icon></button>`;
                for (const input of row.querySelectorAll("input[data-f]")) {
                    input.value = item[input.dataset.f] || "";
                    input.addEventListener("input", () => { item[input.dataset.f] = input.value; this.changed(); });
                }
                row.querySelector("button").addEventListener("click", () => {
                    this.editing[list].splice(i, 1);
                    this.renderLists();
                    this.changed();
                });
                return row;
            }));
        };
        const label = `<label>${t("label", "Label")}<input data-f="label" ${ro} placeholder="${t("label-hint", "work, home…")}"/></label>`;
        make("emails", label + `<label>${t("email", "Email")}<input data-f="value" type="email" ${ro}/></label>`);
        make("phones", label + `<label>${t("phone", "Phone")}<input data-f="value" type="tel" ${ro}/></label>`);
        make("addresses", label
            + `<label>${t("street", "Street")}<input data-f="street" ${ro}/></label>`
            + `<label>${t("postal-code", "Postal code")}<input data-f="postalCode" ${ro}/></label>`
            + `<label>${t("city", "City")}<input data-f="city" ${ro}/></label>`
            + `<label>${t("region", "Region")}<input data-f="region" ${ro}/></label>`
            + `<label>${t("country", "Country")}<input data-f="country" ${ro}/></label>`);
    }

    // An organisation's people and the table rows that link here.
    async renderLinks() {
        const id = this.selectedId;
        let data;
        try { data = await fb.api.contacts.links(id); } catch { return; }
        if (id !== this.selectedId) return;
        const box = this.querySelector("#cv-links");
        if (!box || (!data.people?.length && !data.links?.length)) { if (box) box.replaceChildren(); return; }
        const t = (k, f) => this.t(k, f);
        box.innerHTML = `<sac-section title="${t("history", "Linked here")}"></sac-section><div class="cv-links"></div>`;
        const list = box.querySelector(".cv-links");
        for (const p of data.people || []) {
            const row = document.createElement("div");
            row.className = "fb-row";
            row.innerHTML = `<sac-icon name="user"></sac-icon><div class="fb-row-info"><a class="fb-row-name"></a><div class="fb-row-meta"></div></div>`;
            row.querySelector("a").textContent = p.name;
            row.querySelector(".fb-row-meta").textContent = p.role || "";
            row.querySelector("a").addEventListener("click", () => this.open(p.id));
            list.appendChild(row);
        }
        for (const l of data.links || []) {
            const row = document.createElement("div");
            row.className = "fb-row";
            row.innerHTML = `<sac-icon name="grid"></sac-icon><div class="fb-row-info"><a class="fb-row-name"></a><div class="fb-row-meta"></div></div>`;
            const a = row.querySelector("a");
            a.textContent = l.title;
            a.href = sac.scope.hashFor(`#/tables/${encodeURIComponent(l.table)}`);
            row.querySelector(".fb-row-meta").textContent = `${l.table} · ${l.column}`;
            list.appendChild(row);
        }
    }

    // ------------------------------------------------------------ save --

    changed() {
        if (!this.writable) return;
        clearTimeout(this._saveTimer);
        this._saveTimer = setTimeout(() => this.save(), 600);
    }

    flush() {
        if (this._saveTimer) { clearTimeout(this._saveTimer); this._saveTimer = null; this.save(); }
    }

    async save() {
        this._saveTimer = null;
        const c = this.editing;
        if (!c || !this.writable) return;
        // The lists are the truth; `email`/`phone` are derived on the server.
        const body = { ...c, email: null, phone: null };
        try {
            await fb.api.contacts.update(c.id, body);
            const fresh = await fb.api.contacts.get(c.id);
            const i = this.contacts.findIndex((x) => x.id === c.id);
            if (i >= 0) this.contacts[i] = fresh;
            if (this.selectedId === c.id) {
                this.editing.name = fresh.name;
                this.querySelector("#cv-name").textContent = fresh.name;
            }
            this.renderList();
        } catch (err) {
            sac.toast?.(fb.errors.text(err, this.t("save-failed", "Couldn't save the contact.")), { kind: "error" });
        }
    }

    async remove() {
        const c = this.byId(this.selectedId);
        if (!c) return;
        try {
            await fb.api.contacts.delete(c.id);
            this.selectedId = null;
            this.editing = null;
            await this.load();
            this.open(null);
            sac.toast?.(this.t("deleted", "Moved to the trash."));
        } catch (err) {
            sac.toast?.(fb.errors.text(err, this.t("delete-failed", "Couldn't delete the contact.")), { kind: "error" });
        }
    }

    // --------------------------------------------------------- toolbar --

    paintToolbar() {
        const t = (k, f) => this.t(k, f);
        const items = [];
        if (this.writable) {
            items.push({ id: "cv-import", icon: "upload", title: t("import", "Import vCard or CSV"), onClick: () => this.import() });
        }
        items.push({ id: "cv-export-vcf", icon: "download", title: t("export-vcf", "Export as vCard"), onClick: () => this.export("vcf") });
        items.push({ id: "cv-export-csv", icon: "document", title: t("export-csv", "Export as CSV"), onClick: () => this.export("csv") });
        if (this.writable) {
            items.push({ id: "cv-duplicates", icon: "copy", title: t("duplicates", "Find duplicates"), onClick: () => this.duplicates() });
            if (this.selectedId) items.push({ id: "cv-delete", icon: "trash", title: t("delete", "Delete contact"), onClick: () => this.remove() });
        }
        fb.toolbar.set(items);
    }

    export(format) {
        const a = document.createElement("a");
        a.href = fb.api.contacts.exportUrl(format);
        a.download = `contacts.${format}`;
        document.body.appendChild(a);
        a.click();
        a.remove();
    }

    import() {
        const input = document.createElement("input");
        input.type = "file";
        input.accept = ".vcf,.vcard,.csv,text/vcard,text/csv";
        input.addEventListener("change", async () => {
            const file = input.files?.[0];
            if (!file) return;
            try {
                const text = await file.text();
                const format = /\.csv$/i.test(file.name) ? "csv" : "vcf";
                const r = await fb.api.contacts.import(text, format);
                sac.toast?.(this.t("imported", "Imported {n} contacts.", { n: r.created }));
                await this.load();
            } catch (err) {
                sac.toast?.(fb.errors.text(err, this.t("import-failed", "Couldn't import the file.")), { kind: "error" });
            }
        });
        input.click();
    }

    async duplicates() {
        let pairs = [];
        try { pairs = (await fb.api.contacts.duplicates()).pairs || []; }
        catch (err) { sac.toast?.(fb.errors.text(err, this.t("duplicates-failed", "Couldn't look for duplicates.")), { kind: "error" }); return; }
        const dlg = document.createElement("sac-dialog");
        dlg.id = "cv-duplicates-dialog";
        dlg.setAttribute("title", this.t("duplicates", "Find duplicates"));
        const list = document.createElement("div");
        if (!pairs.length) list.innerHTML = `<p class="muted">${this.t("no-duplicates", "No duplicates found.")}</p>`;
        for (const p of pairs) {
            const row = document.createElement("div");
            row.className = "fb-row";
            row.innerHTML = `<div class="fb-row-info"><p class="fb-row-name"></p><div class="fb-row-meta"></div></div>`;
            row.querySelector(".fb-row-name").textContent = `${p.a.name} ↔ ${p.b.name}`;
            row.querySelector(".fb-row-meta").textContent = p.reason === "email" ? this.t("same-email", "same email") : this.t("same-name", "same name");
            const merge = document.createElement("button");
            merge.type = "button";
            merge.className = "btn";
            merge.dataset.action = "merge";
            merge.textContent = this.t("merge", "Merge");
            merge.title = this.t("merge-hint", "Keep {a}, move {b}'s data and links into it", { a: p.a.name, b: p.b.name });
            merge.addEventListener("click", async () => {
                try {
                    await fb.api.contacts.merge(p.a.id, p.b.id);
                    row.remove();
                    await this.load();
                    if (this.selectedId === p.b.id) this.open(p.a.id);
                } catch (err) {
                    sac.toast?.(fb.errors.text(err, this.t("merge-failed", "Couldn't merge them.")), { kind: "error" });
                }
            });
            row.appendChild(merge);
            list.appendChild(row);
        }
        dlg.appendChild(list);
        const close = document.createElement("button");
        close.type = "button";
        close.slot = "footer";
        close.className = "btn";
        close.textContent = fb.t("fb.common.close", "Close");
        close.addEventListener("click", () => dlg.close?.());
        dlg.appendChild(close);
        dlg.addEventListener("sac:close", () => dlg.remove());
        document.body.appendChild(dlg);
        setTimeout(() => dlg.open?.(), 0);
    }
}

customElements.define("fb-contacts-view", FbContactsView);
sac.router.register("#/contacts", "fb-contacts-view", { label: "Contacts", icon: "contact", palette: false });
