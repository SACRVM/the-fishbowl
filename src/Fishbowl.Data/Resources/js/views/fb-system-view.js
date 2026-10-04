/**
 * System (admin spec § System): the instance's own app — a window app, not
 * a page: fb.systemApp.open(tab?) opens it in a wide <sac-window> (the
 * desktop tile and the palette do), through fb.windowApps. Admins only,
 * personal workspace only (the tile is). Tabs:
 *
 *   Info      the instance at a glance — the running version, how much the
 *             data takes by kind (personal workspaces, spaces, system.db, the
 *             embedding model, logs), the archives (deleted spaces and
 *             accounts, and how long they're kept), whether the embedding
 *             model is ready, and when the scheduler last ran. Numbers only,
 *             never anyone's content.
 *   one tab per settings group (fb.systemSettings): Sign-in, Files &
 *             quotas, … — a new editable key lands in its group's tab
 *             without UI work.
 *
 * The last tab is remembered in this browser. <fb-system-view> is the
 * window's content; there is no route.
 */
(function () {
class FbSystemView extends HTMLElement {
    connectedCallback() {
        this.render();
        this._tabs = this.querySelector("#sys-tabs");
        this._tabs.addEventListener("sac:tab-show", (e) => {
            try { localStorage.setItem("fb.system.tab", e.detail.name); } catch { /* storage off */ }
        });
        this.build();
    }

    render() {
        this.innerHTML = `
            <style>
                /* The window's padding is the edge (app.css .fb-window). */
                fb-system-view { display: block; }
                fb-system-view .fb-block { display: block; margin-bottom: 24px; }
                fb-system-view .fb-block[hidden] { display: none; }
                fb-system-view .fb-block p { margin: 0 0 12px; }
                fb-system-view .muted { color: var(--text-muted); font-size: 13px; }
                fb-system-view .restart-note { margin-bottom: 12px; }
                fb-system-view sac-tab-panel > .fb-block:first-child { margin-top: 24px; }
                fb-system-view .cfg-row { padding: 14px 0; }
                fb-system-view .cfg-row + .cfg-row { border-top: 1px solid var(--border); }
                fb-system-view .cfg-head { display: flex; align-items: baseline; gap: 10px; flex-wrap: wrap; }
                fb-system-view .cfg-label { font-weight: 600; }
                fb-system-view .cfg-key { font-family: var(--font-mono); font-size: 12px; color: var(--text-muted); }
                fb-system-view .cfg-desc { margin: 4px 0 10px; font-size: 13px; color: var(--text-muted); }
                fb-system-view .cfg-edit { flex-wrap: wrap; }
                fb-system-view .cfg-edit input { flex: 1; min-width: 12rem; }
                fb-system-view .cfg-edit input.cfg-num { flex: 0 0 8rem; min-width: 0; }
                fb-system-view .cfg-edit .select { width: auto; }
                fb-system-view .cfg-unit,
                fb-system-view .cfg-state { font-size: 13px; color: var(--text-muted); }
                fb-system-view .cfg-state.set { color: var(--ok); }
                fb-system-view .cfg-error { margin: 8px 0 0; font-size: 13px; color: var(--danger-text); }
            </style>
            <sac-status-banner class="restart-note" kind="warn"
                message="${fb.t("fb.admin.restart-note", "Some saved changes take effect after the next restart of Fishbowl.")}"></sac-status-banner>
            <sac-tab-group id="sys-tabs" overflow="wrap" aria-label="${fb.t("fb.admin.system-title", "System")}"></sac-tab-group>
        `;
    }

    // Info + one tab per settings group; the URL picks the active one.
    async build() {
        let rows = null;
        try { rows = await fb.api.admin.config(); }
        catch (err) { console.warn("[fb-system-view] config failed:", err?.status); }
        if (!this.isConnected) return;
        this._sections = rows ? fb.systemSettings.sections(rows) : [];

        const tab = (name, label) => {
            const t = document.createElement("sac-tab");
            t.setAttribute("name", name);
            t.textContent = label;
            return t;
        };
        const panel = (name) => {
            const p = document.createElement("sac-tab-panel");
            p.setAttribute("name", name);
            return p;
        };
        const info = panel("info");
        const parts = [tab("info", fb.t("fb.admin.tab-info", "Info")), info];
        for (const s of this._sections) parts.push(tab(s.id, s.title), panel(s.id));
        this._tabs.replaceChildren(...parts);
        this._fillSettings();
        let last = null;
        try { last = localStorage.getItem("fb.system.tab"); } catch { /* storage off */ }
        this._show(this.getAttribute("tab") || last);
        await this.renderInfo(info);
        if (!rows) {
            const p = document.createElement("div");
            p.className = "fb-block";
            p.innerHTML = `<p>${fb.t("fb.admin.settings-unavailable", "The settings can't be loaded right now.")}</p>`;
            info.appendChild(p);
        }
    }

    _show(name) {
        const known = name === "info" || this._sections?.some((s) => s.id === name);
        this._tabs.active = known ? name : "info";
    }

    // Each settings tab: its group's rows in one card.
    _fillSettings() {
        for (const s of this._sections) {
            const p = this._tabs.querySelector(`sac-tab-panel[name="${s.id}"]`);
            if (!p) continue;
            const card = document.createElement("div");
            card.className = "fb-block";
            card.dataset.section = s.id;
            for (const r of s.rows) card.appendChild(fb.systemSettings.row(r, {
                refresh: () => this._reloadSettings(),
                saved: (res, label, cleared) => this._saved(res, label, cleared),
            }));
            p.replaceChildren(card);
        }
    }

    // After a save: the same tabs with fresh values (a new group rebuilds).
    async _reloadSettings() {
        let rows;
        try { rows = await fb.api.admin.config(); } catch { return; }
        const next = fb.systemSettings.sections(rows);
        const same = next.length === this._sections.length && next.every((s, i) => s.id === this._sections[i].id);
        this._sections = next;
        if (same) this._fillSettings();
        else this.build();
    }

    _saved(res, label, cleared) {
        if (res?.restartRequired) this.querySelector(".restart-note").setAttribute("open", "");
        window.sac?.toast?.(cleared ? fb.t("fb.admin.cfg-cleared", "{label}: back to the default.", { label }) : fb.t("fb.admin.cfg-saved", "{label} saved.", { label }), { kind: "success" });
    }

    // The Info tab: the instance at a glance.
    async renderInfo(mount) {
        let s;
        try {
            s = await fb.api.admin.system();
        } catch (err) {
            console.warn("[fb-system-view] load failed:", err?.status);
            mount.innerHTML = `<div class="fb-block"><p class="muted">${fb.t("fb.admin.system-unavailable", "System information can't be loaded right now.")}</p></div>`;
            return;
        }
        if (!this.isConnected) return;

        const bytes = fb.accounts.formatBytes;
        const size = (n) => (n ? bytes(n) : "0 KB");
        // "{n} account" / "{n} accounts" — a key per form, English inline.
        const count = (n, key, one, many) => n === 1
            ? fb.t(`fb.admin.count-${key}-1`, `{n} ${one}`, { n })
            : fb.t(`fb.admin.count-${key}-n`, `{n} ${many}`, { n });
        const when = (iso, what) => iso
            ? (what === "checked"
                ? fb.t("fb.admin.last-checked", "Last checked {when}", { when: fb.format.dateTime(iso) })
                : fb.t("fb.admin.last-run", "Last run {when}", { when: fb.format.dateTime(iso) }))
            : (what === "checked"
                ? fb.t("fb.admin.not-checked", "Not checked yet since the last start")
                : fb.t("fb.admin.not-run", "Not run yet since the last start"));

        const running = section(fb.t("fb.admin.running", "Running"));
        running.panel.append(
            row("info", fb.t("fb.admin.version", "Version"), s.version, "version"),
            row("search", fb.t("fb.admin.search-model", "Search model"), {
                ready: fb.t("fb.admin.model-ready", "Ready — search ranks by meaning and by words"),
                downloading: fb.t("fb.admin.model-downloading", "Not ready yet — search uses words only until the download finishes"),
                off: fb.t("fb.admin.model-off", "Not installed on this host"),
            }[s.embedding?.status] || s.embedding?.status || "—", "embedding"),
            row("clock", fb.t("fb.admin.reminders", "Reminders"), when(s.scheduler?.reminderTickAt, "checked"), "reminders"),
            row("sync", fb.t("fb.admin.maintenance", "Daily maintenance"), when(s.scheduler?.maintenanceAt, "run"), "maintenance"));

        const data = section(fb.t("fb.admin.data", "Data"));
        const d = s.data || {};
        data.panel.append(
            row("user", fb.t("fb.admin.personal-workspaces", "Personal workspaces"), `${size(d.users?.bytes)} · ${count(d.users?.count ?? 0, "account", "account", "accounts")}`, "users"),
            row("users", fb.t("fb.admin.spaces", "Spaces"), `${size(d.spaces?.bytes)} · ${count(d.spaces?.count ?? 0, "space", "space", "spaces")}`, "spaces"),
            row("settings", fb.t("fb.admin.system-db", "System database"), size(d.systemBytes), "system"),
            row("search", fb.t("fb.admin.model-files", "Search model files"), size(d.modelsBytes), "models"),
            row("note", fb.t("fb.admin.logs", "Logs"), size(d.logsBytes), "logs"));

        const archives = section(fb.t("fb.admin.archives", "Archives"));
        const a = s.archives || {};
        archives.panel.append(
            row("archive", fb.t("fb.admin.deleted-spaces", "Deleted spaces"), `${size(a.spaces?.bytes)} · ${count(a.spaces?.count ?? 0, "archive", "archive", "archives")}`, "archived-spaces"),
            row("archive", fb.t("fb.admin.deleted-accounts", "Deleted accounts"), `${size(a.users?.bytes)} · ${count(a.users?.count ?? 0, "archive", "archive", "archives")}`, "archived-users"));
        const keep = document.createElement("p");
        keep.className = "muted";
        keep.textContent = a.retentionDays > 0
            ? fb.t("fb.admin.archives-kept", "Archives are kept {days}, then removed (Archive:RetentionDays under Export & archive).", { days: count(a.retentionDays, "day", "day", "days") })
            : fb.t("fb.admin.archives-forever", "Archives are kept until someone deletes them (Archive:RetentionDays is 0).");
        archives.panel.appendChild(keep);

        mount.replaceChildren(running.card, data.card, archives.card);
    }
}

// A kit card headed by a <sac-section>; rows go into the section.
function section(title) {
    const card = document.createElement("div");
    card.className = "fb-block";
    const panel = document.createElement("sac-section");
    panel.setAttribute("title", title);
    card.appendChild(panel);
    return { card, panel };
}

function row(icon, name, meta, key) {
    const r = document.createElement("div");
    r.className = "fb-row";
    r.dataset.key = key;
    r.innerHTML = `
        <sac-icon name="${icon}"></sac-icon>
        <div class="fb-row-info">
            <p class="fb-row-name"></p>
            <div class="fb-row-meta"></div>
        </div>`;
    r.querySelector(".fb-row-name").textContent = name;
    r.querySelector(".fb-row-meta").textContent = meta;
    return r;
}

customElements.define("fb-system-view", FbSystemView);

fb.systemApp = { open: (tab) => fb.windowApps.open("system", { tab }) };
})();
