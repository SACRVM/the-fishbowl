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
                <h1>System</h1>
                <p class="subtitle">
                    How this Fishbowl is doing. Sizes and times only — nobody's notes or files.
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
                    <p>System is shown for the whole Fishbowl, from your personal workspace.</p>
                    <div class="toolbar"><button type="button" class="btn" id="to-personal">Open in Personal</button></div>
                </div>`;
            mount.querySelector("#to-personal").addEventListener("click", () => sac.router.navigate("#/admin/system"));
            return;
        }

        let s;
        try {
            s = await fb.api.admin.system();
        } catch (err) {
            console.warn("[fb-system-view] load failed:", err?.status);
            mount.innerHTML = `<div class="card"><p class="muted">System information can't be loaded right now.</p></div>`;
            return;
        }
        if (!this.isConnected) return;

        const bytes = fb.accounts.formatBytes;
        const size = (n) => (n ? bytes(n) : "0 KB");
        const count = (n, one, many) => `${n} ${n === 1 ? one : many}`;
        const when = (iso, verb) => (iso ? `Last ${verb} ${fb.format.dateTime(iso)}` : `Not ${verb} yet since the last start`);

        const running = section("Running");
        running.panel.append(
            row("info", "Version", s.version, "version"),
            row("search", "Search model", {
                ready: "Ready — search ranks by meaning and by words",
                downloading: "Not ready yet — search uses words only until the download finishes",
                off: "Not installed on this host",
            }[s.embedding?.status] || s.embedding?.status || "—", "embedding"),
            row("clock", "Reminders", when(s.scheduler?.reminderTickAt, "checked"), "reminders"),
            row("sync", "Daily maintenance", when(s.scheduler?.maintenanceAt, "run"), "maintenance"));

        const data = section("Data");
        const d = s.data || {};
        data.panel.append(
            row("user", "Personal workspaces", `${size(d.users?.bytes)} · ${count(d.users?.count ?? 0, "account", "accounts")}`, "users"),
            row("users", "Spaces", `${size(d.spaces?.bytes)} · ${count(d.spaces?.count ?? 0, "space", "spaces")}`, "spaces"),
            row("settings", "System database", size(d.systemBytes), "system"),
            row("search", "Search model files", size(d.modelsBytes), "models"),
            row("note", "Logs", size(d.logsBytes), "logs"));

        const archives = section("Archives");
        const a = s.archives || {};
        archives.panel.append(
            row("archive", "Deleted spaces", `${size(a.spaces?.bytes)} · ${count(a.spaces?.count ?? 0, "archive", "archives")}`, "archived-spaces"),
            row("archive", "Deleted accounts", `${size(a.users?.bytes)} · ${count(a.users?.count ?? 0, "archive", "archives")}`, "archived-users"));
        const keep = document.createElement("p");
        keep.className = "muted";
        keep.textContent = a.retentionDays > 0
            ? `Archives are kept ${count(a.retentionDays, "day", "days")}, then removed (Archive:RetentionDays in System settings).`
            : "Archives are kept until someone deletes them (Archive:RetentionDays is 0).";
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
