/**
 * System (#/admin/system, admin spec § System, phase A3): the instance at a
 * glance — the running version, how much the data takes by kind (personal
 * workspaces, spaces, system.db, the embedding model, logs), the archives
 * (deleted spaces and accounts, and how long they're kept), whether the
 * embedding model is ready, and when the scheduler last ran. Numbers only,
 * never anyone's content.
 *
 * Registered only for admins (fb.accounts.registerAdminRoutes), like Users
 * and System settings. Personal only: in a space it points back.
 */
(function () {
class FbSystemView extends HTMLElement {
    connectedCallback() {
        this.render();
        this._onScope = () => this.refresh();
        window.addEventListener("sac:scope-changed", this._onScope);
        this.refresh();
    }

    disconnectedCallback() {
        window.removeEventListener("sac:scope-changed", this._onScope);
    }

    render() {
        this.classList.add("fb-page");
        this.innerHTML = `
            <header><div>
                <h1>${fb.t("fb.admin.system-title", "System")}</h1>
                <p class="subtitle">
                    ${fb.t("fb.admin.system-subtitle", "How this Fishbowl is doing. Sizes and times only — nobody's notes or files.")}
                </p>
            </div></header>
            <div id="system-body"></div>
        `;
    }

    async refresh() {
        const mount = this.querySelector("#system-body");
        if (!mount) return;

        if (sac.scope.get().type === "scoped") {
            mount.innerHTML = `
                <div class="card">
                    <p>${fb.t("fb.admin.system-personal-only", "System is shown for the whole Fishbowl, from your personal workspace.")}</p>
                    <div class="toolbar"><button type="button" class="btn" id="to-personal">${fb.t("fb.admin.open-personal", "Open in Personal")}</button></div>
                </div>`;
            mount.querySelector("#to-personal").addEventListener("click", () => sac.router.navigate("#/admin/system"));
            return;
        }

        let s;
        try {
            s = await fb.api.admin.system();
        } catch (err) {
            console.warn("[fb-system-view] load failed:", err?.status);
            mount.innerHTML = `<div class="card"><p class="muted">${fb.t("fb.admin.system-unavailable", "System information can't be loaded right now.")}</p></div>`;
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
            ? fb.t("fb.admin.archives-kept", "Archives are kept {days}, then removed (Archive:RetentionDays in System settings).", { days: count(a.retentionDays, "day", "day", "days") })
            : fb.t("fb.admin.archives-forever", "Archives are kept until someone deletes them (Archive:RetentionDays is 0).");
        archives.panel.appendChild(keep);

        mount.replaceChildren(running.card, data.card, archives.card);
    }
}

// A kit card headed by a <sac-section>; rows go into the section.
function section(title) {
    const card = document.createElement("div");
    card.className = "card";
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
})();
