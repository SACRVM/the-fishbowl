/**
 * <fb-contacts-view>  (mounted at #/contacts, personal and space)
 *
 * People and organisations (space-apps spec, phase 6) on the kit's
 * <sac-split>, like Todos: the list (search, a People / Organisations filter,
 * "+" for a new person or organisation) and the open contact's fields in
 * sections — saved as you type. A person belongs to at most one
 * organisation; an organisation shows its people; both show the table rows
 * that link to them. The toolbar imports (vCard / CSV), exports, and finds
 * duplicates to merge. Several contacts are marked like files in Files —
 * the kit's sac.selection: Ctrl/⌘+click, Shift+click for a range, Ctrl/⌘+A,
 * a long press on a phone, Esc lets go — and the list header then deletes
 * them. A space Reader reads only.
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
        // The workspace this view shows, pinned: an edit flushed on leaving
        // still goes there, not to the workspace the hash moved on to.
        this.api = fb.api.contacts.in(fb.api.workspace());
        this.render();
        this.writable = await fb.access.canWrite();
        this.toggleAttribute("readonly", !this.writable);
        this.paintHead();
        await this.load();
        if (!this.isConnected) return;
        this.paintToolbar();
        // "New contact" from the palette or the desktop tile, a birthday in
        // the calendar opening its contact (fb.desktop.go).
        this._onIntent = () => {
            const it = fb.desktop?.takeIntent("contacts");
            if (it?.action === "create" && this.writable) this.create("person");
            else if (it?.action === "open" && it.id) this.open(it.id);
        };
        window.addEventListener("fb:intent", this._onIntent);
        this._onIntent();
    }

    disconnectedCallback() {
        if (this._onIntent) window.removeEventListener("fb:intent", this._onIntent);
        this.sel?.destroy();
        this.flush();
        if (window.fb?.toolbar) fb.toolbar.clear();
    }

    t(key, fallback, vars) { return fb.t(`fb.contacts.${key}`, fallback, vars); }

    // The list's share of the width, as the user left it (like Notes and Todos).
    _splitPosition() {
        try { return localStorage.getItem("fb.contacts.split") || "28%"; } catch { return "28%"; }
    }

    async load() {
        // A pending edit goes out first, so the list below has it.
        await this.flush();
        try { this.contacts = await this.api.list(); }
        catch (err) { console.error("[fb-contacts-view] list failed:", err); this.contacts = []; }
        this.renderList();
        if (!this.selectedId) return;
        if (!this.byId(this.selectedId)) this.open(null);
        // The open contact again from what the server has now (a merge moved
        // data into it): saves send the whole contact, so the editor must
        // never hold an older copy.
        else if (!this._saveTimer) this.open(this.selectedId);
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
                /* Exactly the pane's height: the search and the header stay, only the list scrolls. */
                fb-contacts-view .cv-list-pane { height: 100%; background: var(--panel); display: flex; flex-direction: column; }
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
                fb-contacts-view .cv-items { flex: 1; min-height: 0; overflow-y: auto; padding: 2px 12px 12px; }
                fb-contacts-view .cv-item {
                    display: flex; align-items: center; gap: 10px;
                    padding: 8px 12px; border-radius: var(--radius-m); cursor: pointer;
                    border: 1px solid transparent; margin-bottom: 2px;
                }
                fb-contacts-view .cv-item:hover { background: var(--hover); }
                fb-contacts-view .cv-item.selected { background: var(--accent-tint); border-color: color-mix(in srgb, var(--accent) 28%, transparent); }
                /* Marked rows are the kit's (sac.selection, ui.css). While marking,
                   the open contact keeps only its outline, so it doesn't read as marked. */
                fb-contacts-view .cv-items.marking .cv-item.selected:not(.marked) { background: none; }
                fb-contacts-view .cv-item-text { min-width: 0; flex: 1; }
                fb-contacts-view .cv-item-action { --icon-btn-size: 26px; --icon-btn-icon: 14px; flex: none; }
                fb-contacts-view #cv-items:focus { outline: none; }
                fb-contacts-view .cv-item-name { font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
                fb-contacts-view .cv-item-sub { color: var(--text-muted); font-size: 0.8125rem; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
                fb-contacts-view .cv-item sac-icon { color: var(--text-muted); flex: none; }
                fb-contacts-view .cv-editor-pane { height: 100%; overflow-y: auto; }
                fb-contacts-view .cv-editor { padding: 24px 24px 40px; }
                fb-contacts-view .cv-empty { min-height: 60%; justify-content: center; }
                fb-contacts-view .cv-title { display: flex; align-items: center; gap: 10px; margin-bottom: 8px; }
                fb-contacts-view .cv-title h2 { margin: 0; flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; }
                /* auto-fit: the fields a section has share its whole width (two fields → two halves). */
                fb-contacts-view .cv-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(200px, 1fr)); gap: 10px 14px; margin: 8px 0 18px; }
                fb-contacts-view .cv-grid sac-select { display: block; width: 100%; }
                /* A person's name on one line: salutation and title narrow, first and last name wide. */
                fb-contacts-view .cv-grid.cv-names { grid-template-columns: minmax(90px, 1fr) minmax(90px, 1fr) 2fr 2fr; }
                /* An organisation's people and what links here: rows like the list's, a click opens. */
                fb-contacts-view .cv-link-list { display: grid; gap: 2px; margin: 8px 0 18px; }
                fb-contacts-view .cv-link-row {
                    display: flex; align-items: center; gap: 10px; width: 100%;
                    padding: 8px 12px; border: 0; border-radius: var(--radius-m);
                    background: none; color: var(--text); font: inherit; text-align: left; text-decoration: none; cursor: pointer;
                }
                fb-contacts-view .cv-link-row:hover { background: var(--hover); }
                fb-contacts-view .cv-link-row sac-icon { color: var(--text-muted); flex: none; }
                fb-contacts-view .cv-link-text { flex: 1; min-width: 0; }
                fb-contacts-view .cv-link-name, fb-contacts-view .cv-link-sub { display: block; }
                fb-contacts-view .cv-link-name { font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
                fb-contacts-view .cv-link-sub { color: var(--text-muted); font-size: 0.8125rem; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
                fb-contacts-view .cv-grid label, fb-contacts-view .cv-list label { display: grid; gap: 4px; font-size: 0.8125rem; color: var(--text-muted); }
                fb-contacts-view .cv-grid input, fb-contacts-view .cv-list input, fb-contacts-view textarea { width: 100%; box-sizing: border-box; color: var(--text); }
                fb-contacts-view .cv-wide { grid-column: 1 / -1; }
                fb-contacts-view .cv-list { margin: 8px 0 12px; display: grid; gap: 8px; }
                fb-contacts-view .cv-list:empty { display: none; }
                fb-contacts-view .cv-row { display: grid; grid-template-columns: 120px 1fr auto; gap: 8px; align-items: end; }
                /* An address on two lines: label and street (and Remove), then
                   postal code, city, region and country under the street. Children:
                   1 label, 2 street, 3 postal code, 4 city, 5 region, 6 country, 7 Remove. */
                fb-contacts-view #cv-addresses { gap: 16px; }
                fb-contacts-view .cv-addr { display: grid; grid-template-columns: 120px minmax(120px, 1fr) 2fr 1.5fr 1.5fr auto; gap: 8px; align-items: end; }
                fb-contacts-view .cv-addr > :nth-child(1) { grid-area: 1 / 1; }
                fb-contacts-view .cv-addr > :nth-child(2) { grid-area: 1 / 2 / 2 / 6; }
                fb-contacts-view .cv-addr > :nth-child(3) { grid-area: 2 / 2; }
                fb-contacts-view .cv-addr > :nth-child(4) { grid-area: 2 / 3; }
                fb-contacts-view .cv-addr > :nth-child(5) { grid-area: 2 / 4; }
                fb-contacts-view .cv-addr > :nth-child(6) { grid-area: 2 / 5; }
                fb-contacts-view .cv-addr > :nth-child(7) { grid-area: 1 / 6; }
                @media (max-width: 768px) {
                    fb-contacts-view .cv-row { grid-template-columns: 1fr auto; }
                    fb-contacts-view .cv-grid.cv-names { grid-template-columns: 1fr 1fr; }
                    fb-contacts-view .cv-row label:first-child { grid-column: 1 / -1; }
                    /* A phone: label (and Remove), street, then two pairs. */
                    fb-contacts-view .cv-addr { grid-template-columns: 1fr 1fr auto; }
                    fb-contacts-view .cv-addr > :nth-child(1) { grid-area: 1 / 1 / 2 / 3; }
                    fb-contacts-view .cv-addr > :nth-child(2) { grid-area: 2 / 1 / 3 / 4; }
                    fb-contacts-view .cv-addr > :nth-child(3) { grid-area: 3 / 1; }
                    fb-contacts-view .cv-addr > :nth-child(4) { grid-area: 3 / 2 / 4 / 4; }
                    fb-contacts-view .cv-addr > :nth-child(5) { grid-area: 4 / 1; }
                    fb-contacts-view .cv-addr > :nth-child(6) { grid-area: 4 / 2 / 5 / 4; }
                    fb-contacts-view .cv-addr > :nth-child(7) { grid-area: 1 / 3; }
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
                        <button type="button" class="icon-btn danger" id="cv-delete-marked" hidden title="${t("delete-marked", "Delete selected")}" aria-label="${t("delete-marked", "Delete selected")}"><sac-icon name="trash"></sac-icon></button>
                        <button type="button" class="icon-btn" id="cv-clear-marks" hidden title="${t("clear-marks", "Clear selection")}" aria-label="${t("clear-marks", "Clear selection")}"><sac-icon name="close"></sac-icon></button>
                        <button type="button" class="icon-btn" data-filter="person" title="${t("only-people", "Only people")}" aria-label="${t("only-people", "Only people")}"><sac-icon name="user"></sac-icon></button>
                        <button type="button" class="icon-btn" data-filter="organisation" title="${t("only-organisations", "Only organisations")}" aria-label="${t("only-organisations", "Only organisations")}"><sac-icon name="users"></sac-icon></button>
                        <sac-menu id="cv-new">
                            <button slot="trigger" type="button" class="icon-btn" title="${t("new", "New contact")}" aria-label="${t("new", "New contact")}"><sac-icon name="plus"></sac-icon></button>
                            <button type="button" data-action="person"><sac-icon name="user"></sac-icon> ${t("new-person", "New person")}</button>
                            <button type="button" data-action="organisation"><sac-icon name="users"></sac-icon> ${t("new-organisation", "New organisation")}</button>
                        </sac-menu>
                    </div>
                    <div class="cv-items fb-scroll-fade" id="cv-items" tabindex="-1"></div>
                </aside>
                <main class="cv-editor-pane" slot="end">
                    <div class="empty-state cv-empty" id="cv-empty">
                        <sac-icon name="contacts"></sac-icon>
                        <h3>${t("none-open", "No contact open")}</h3>
                    </div>
                    <div class="cv-editor" id="cv-editor" hidden></div>
                </main>
            </sac-split>`;

        this.paintHead();
        for (const btn of this.querySelectorAll(".cv-head [data-filter]")) {
            btn.addEventListener("click", () => {
                this.filter = this.filter === btn.dataset.filter ? "all" : btn.dataset.filter;
                this.paintHead();
                this.renderList();
            });
        }
        // Marking several contacts: the kit's gestures, kept by id across
        // re-renders and to the rows the list shows; the header follows.
        this.sel = sac.selection.attach(this.querySelector("#cv-items"), {
            rows: ".cv-item",
            current: () => this.selectedId,
            icon: ":scope > sac-icon",
            disabled: () => !this.writable,
            onChange: () => this.paintHead(),
        });
        this.querySelector("#cv-delete-marked").addEventListener("click", () => this.removeMany(this.sel.marked));
        this.querySelector("#cv-clear-marks").addEventListener("click", () => this.sel.clear());
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
        this.paintHead();
        if (!list.length) {
            box.innerHTML = `<p class="muted" style="padding: 8px 12px">${this.contacts.length ? this.t("no-match", "Nothing matches.") : this.t("empty", "No contacts yet.")}</p>`;
            return;
        }
        const del = this.t("delete", "Delete contact");
        box.replaceChildren(...list.map((c) => {
            const row = document.createElement("div");
            row.className = "cv-item reveal-on-hover" + (c.id === this.selectedId ? " selected" : "");
            row.dataset.id = c.id;
            row.innerHTML = `<sac-icon name="${c.kind === "organisation" ? "users" : "user"}"></sac-icon>
                <div class="cv-item-text"><div class="cv-item-name"></div><div class="cv-item-sub"></div></div>
                ${this.writable ? `<button type="button" class="icon-btn hover-reveal danger cv-item-action" data-action="delete" title="${del}" aria-label="${del}"><sac-icon name="trash"></sac-icon></button>` : ""}`;
            row.querySelector(".cv-item-name").textContent = c.name;
            row.querySelector(".cv-item-sub").textContent = this.subline(c);
            // A mark gesture never gets here (sac.selection stops it); a plain click opens.
            row.addEventListener("click", (e) => {
                if (e.target.closest("[data-action='delete']")) { e.stopPropagation(); this.removeMany([c.id]); return; }
                this.open(c.id);
            });
            return row;
        }));
    }

    // The header names the filter — or, while contacts are marked, how many,
    // with Delete and Clear in place of the filter and "+".
    paintHead() {
        const head = this.querySelector(".cv-head");
        if (!head) return;
        const n = this.sel?.marked.length || 0;
        const titles = { all: this.t("all-contacts", "All contacts"), person: this.t("people", "People"), organisation: this.t("organisations", "Organisations") };
        this.querySelector("#cv-head-title").textContent = n
            ? (n === 1 ? this.t("marked-1", "1 selected") : this.t("marked", "{n} selected", { n }))
            : titles[this.filter];
        for (const b of head.querySelectorAll("[data-filter]")) {
            b.classList.toggle("active", b.dataset.filter === this.filter);
            b.hidden = n > 0;
        }
        this.querySelector("#cv-new").hidden = !this.writable || n > 0;
        this.querySelector("#cv-delete-marked").hidden = n === 0;
        this.querySelector("#cv-clear-marks").hidden = n === 0;
    }

    // ---------------------------------------------------------- editor --

    async create(kind) {
        if (!this.writable) return;
        try {
            const body = kind === "organisation"
                ? { kind, name: this.t("new-organisation-name", "New organisation") }
                : { kind: "person", firstName: this.t("new-person-first", "New"), lastName: this.t("new-person-last", "person") };
            const made = await this.api.create(body);
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
            <div class="cv-grid${person ? " cv-names" : ""}">
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
        try { data = await this.api.links(id); } catch { return; }
        const threads = await this.mailWith(this.byId(id));
        if (id !== this.selectedId) return;
        const box = this.querySelector("#cv-links");
        if (!box || (!data.people?.length && !data.links?.length && !threads.length)) { if (box) box.replaceChildren(); return; }
        const t = (k, f) => this.t(k, f);
        // One row: an icon, a name, a muted line, and the chevron that says "opens".
        const row = (tag, icon, name, sub) => {
            const el = document.createElement(tag);
            el.className = "cv-link-row";
            if (tag === "button") el.type = "button";
            el.innerHTML = `<sac-icon name="${icon}"></sac-icon><span class="cv-link-text"><span class="cv-link-name"></span><span class="cv-link-sub"></span></span><sac-icon name="chevron-right"></sac-icon>`;
            el.querySelector(".cv-link-name").textContent = name;
            const subEl = el.querySelector(".cv-link-sub");
            if (sub) subEl.textContent = sub; else subEl.remove();
            return el;
        };
        box.replaceChildren();
        // An organisation's people: a click opens the person.
        if (data.people?.length) {
            const head = document.createElement("sac-section");
            head.setAttribute("title", t("people", "People"));
            const list = document.createElement("div");
            list.className = "cv-link-list";
            for (const p of data.people) {
                const email = (this.byId(p.id)?.emails || [])[0]?.value;
                const el = row("button", "user", p.name, [p.role, email].filter(Boolean).join(" · "));
                el.addEventListener("click", () => this.open(p.id));
                list.appendChild(el);
            }
            box.append(head, list);
        }
        // Table rows that link here: a click opens the table.
        if (data.links?.length) {
            const head = document.createElement("sac-section");
            head.setAttribute("title", t("history", "Linked here"));
            const list = document.createElement("div");
            list.className = "cv-link-list";
            for (const l of data.links) {
                const el = row("a", "grid", l.title, `${l.table} · ${l.column}`);
                el.href = sac.scope.hashFor(`#/tables/${encodeURIComponent(l.table)}`);
                list.appendChild(el);
            }
            box.append(head, list);
        }
        // The latest conversations with this person: a click opens it in Mail.
        if (threads.length) {
            const head = document.createElement("sac-section");
            head.setAttribute("title", t("mail", "Mail"));
            const list = document.createElement("div");
            list.className = "cv-link-list";
            for (const th of threads) {
                const el = row("button", th.latestDirection === "out" ? "fb-mail-out" : "fb-mail-in",
                    th.subject || fb.t("fb.mail.no-subject", "(no subject)"),
                    [fb.format.date(new Date(th.latestAt)), th.count > 1 ? this.t("mail-count", "{n} messages", { n: th.count }) : null].filter(Boolean).join(" · "));
                el.addEventListener("click", () => fb.desktop.go("mail", "open", th.threadId));
                list.appendChild(el);
            }
            box.append(head, list);
        }
    }

    /** The five latest conversations with any of the contact's addresses. */
    async mailWith(c) {
        const addresses = (c?.emails || []).map((e) => e.value).filter(Boolean).slice(0, 5);
        if (!addresses.length) return [];
        try {
            const mail = fb.api.mail.in(fb.api.workspace());
            const lists = await Promise.all(addresses.map((address) => mail.threads({ address, limit: 5 }).catch(() => [])));
            const seen = new Set();
            return lists.flat().filter((th) => !seen.has(th.threadId) && seen.add(th.threadId))
                .sort((a, b) => new Date(b.latestAt) - new Date(a.latestAt)).slice(0, 5);
        } catch { return []; }
    }

    // ------------------------------------------------------------ save --

    changed() {
        if (!this.writable) return;
        clearTimeout(this._saveTimer);
        this._saveTimer = setTimeout(() => this.save(), 600);
    }

    /** Saves a pending edit now; resolves once every save is on the server. */
    flush() {
        if (this._saveTimer) { clearTimeout(this._saveTimer); this._saveTimer = null; return this.save(); }
        return this._saving || Promise.resolve();
    }

    /** The contact as it is now, after the saves ahead of it — one at a time, in order. */
    save() {
        this._saveTimer = null;
        const c = this.editing;
        if (!c || !this.writable) return Promise.resolve();
        // The lists are the truth; `email`/`phone` are derived on the server.
        const body = { ...c, email: null, phone: null };
        const p = (this._saving || Promise.resolve()).then(() => this._put(c, body));
        this._saving = p;
        p.then(() => { if (this._saving === p) this._saving = null; });
        return p;
    }

    async _put(c, body) {
        try {
            await this.api.update(c.id, body);
            const fresh = await this.api.get(c.id);
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

    remove() {
        if (this.selectedId) return this.removeMany([this.selectedId]);
    }

    // One delete for the toolbar, a row's trash and the marked contacts: asked
    // first, then into the trash one by one — people before organisations, so
    // an organisation going with its people isn't refused for them.
    async removeMany(ids) {
        const items = ids.map((id) => this.byId(id)).filter(Boolean);
        if (!items.length || !this.writable) return;
        const one = items.length === 1;
        const answer = await sac.dialog.confirm({
            title: one ? this.t("delete-title-1", "Delete {name}?", { name: items[0].name }) : this.t("delete-title", "Delete {n} contacts?", { n: items.length }),
            message: one ? this.t("delete-msg-1", "It moves to the trash — you can restore it there.") : this.t("delete-msg", "They move to the trash — you can restore them there."),
            buttons: [
                { action: "cancel", label: fb.t("fb.common.cancel", "Cancel"), kind: "default" },
                { action: "delete", label: fb.t("fb.common.delete", "Delete"), kind: "destructive", armAfterMs: 2000 },
            ],
        });
        if (answer !== "delete") return;
        if (items.some((c) => c.id === this.selectedId)) {
            // A pending edit would PUT the contact after it is gone.
            clearTimeout(this._saveTimer);
            this._saveTimer = null;
        }
        items.sort((a, b) => (a.kind === "organisation") - (b.kind === "organisation"));
        let done = 0, firstError = null;
        for (const c of items) {
            try {
                await this.api.delete(c.id);
                done++;
                if (c.id === this.selectedId) { this.selectedId = null; this.editing = null; }
            } catch (err) { firstError ??= err; }
        }
        await this.load();
        if (!this.selectedId) this.open(null);
        if (!firstError) {
            sac.toast?.(one ? this.t("deleted", "Moved to the trash.") : this.t("deleted-n", "{n} contacts moved to the trash.", { n: done }));
        } else if (one) {
            sac.toast?.(fb.errors.text(firstError, this.t("delete-failed", "Couldn't delete the contact.")), { kind: "error" });
        } else {
            sac.toast?.(this.t("delete-some-failed", "{done} moved to the trash; {failed} couldn't be deleted: {reason}",
                { done, failed: items.length - done, reason: fb.errors.text(firstError, this.t("delete-failed", "Couldn't delete the contact.")) }), { kind: "error" });
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
        a.href = this.api.exportUrl(format);
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
                const r = await this.api.import(text, format);
                sac.toast?.(r.skipped
                    ? this.t("imported-skipped", "Imported {n} contacts; {skipped} were already here.", { n: r.created, skipped: r.skipped })
                    : this.t("imported", "Imported {n} contacts.", { n: r.created }));
                await this.load();
            } catch (err) {
                sac.toast?.(fb.errors.text(err, this.t("import-failed", "Couldn't import the file.")), { kind: "error" });
            }
        });
        input.click();
    }

    async duplicates() {
        let pairs = [];
        try { pairs = (await this.api.duplicates()).pairs || []; }
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
                    // What was just typed goes in before the merge — a save
                    // after it would put the pre-merge fields back.
                    await this.flush();
                    await this.api.merge(p.a.id, p.b.id);
                    row.remove();
                    const wasB = this.selectedId === p.b.id;
                    await this.load();   // reopens the kept one fresh when it is open
                    if (wasB) this.open(p.a.id);
                } catch (err) {
                    sac.toast?.(fb.errors.text(err, this.t("merge-failed", "Couldn't merge them.")), { kind: "error" });
                }
            });
            row.appendChild(merge);
            list.appendChild(row);
        }
        dlg.appendChild(list);
        // The dialog's own button row (sac-dialog has no footer slot); any
        // way out — the button, Escape, the backdrop — ends in sac:action.
        dlg.buttons = [{ action: "close", label: fb.t("fb.common.close", "Close"), kind: "default" }];
        dlg.addEventListener("sac:action", () => setTimeout(() => dlg.remove(), 120));
        document.body.appendChild(dlg);
        setTimeout(() => dlg.open?.(), 0);
    }
}

customElements.define("fb-contacts-view", FbContactsView);
sac.router.register("#/contacts", "fb-contacts-view", { label: "Contacts", icon: "contacts", palette: false });
