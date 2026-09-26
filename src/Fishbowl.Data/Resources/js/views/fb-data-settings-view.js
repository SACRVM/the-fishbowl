/**
 * Settings → Your data (#/data): export and storage (Files phase 3).
 *
 * Export is three independent downloads of the active workspace, each a ZIP
 * the server streams (so a plain link, never a Blob in memory): the database,
 * all files (without the trash), or both in one ZIP — the last offered only
 * while the files are below Export:CombinedMaxBytes; above it a line says
 * why instead of a dead button. Sizes come first, from /export/info.
 *
 * Storage shows what the workspace's owner uses against their quota (one
 * quota covers the personal workspace and every space they own).
 *
 * Works in both workspaces; a space's export is owner-only, so a member sees
 * a note instead of the buttons.
 */
(function () {
class FbDataSettingsView extends HTMLElement {
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
        this.innerHTML = `
            <style>
                fb-data-settings-view { display: block; padding: clamp(1.25rem, 5vw, 40px) clamp(1rem, 5vw, 48px); max-width: 780px; }
                fb-data-settings-view header { margin-bottom: 24px; }
                fb-data-settings-view h1 {
                    font-family: 'Outfit', 'Inter', sans-serif;
                    font-size: 28px;
                    font-weight: 700;
                    margin: 0 0 6px;
                    color: var(--text);
                }
                fb-data-settings-view .subtitle { color: var(--text-muted); font-size: 14px; margin: 0; }
                fb-data-settings-view .panel {
                    padding: 18px 20px;
                    background: var(--panel);
                    border: 1px solid var(--border);
                    border-radius: var(--radius-l);
                    margin-bottom: 20px;
                }
                fb-data-settings-view .panel h2 {
                    font-size: 12px;
                    font-weight: 600;
                    text-transform: uppercase;
                    letter-spacing: 0.06em;
                    color: var(--text-muted);
                    margin: 0 0 12px;
                }
                fb-data-settings-view .panel p { margin: 0 0 12px; color: var(--text); font-size: 14px; }
                fb-data-settings-view .panel p.muted { color: var(--text-muted); font-size: 13px; margin: 12px 0 0; }
                fb-data-settings-view .export-row {
                    display: flex;
                    align-items: center;
                    gap: 14px;
                    padding: 12px 0;
                }
                fb-data-settings-view .export-row + .export-row { border-top: 1px solid var(--border); }
                fb-data-settings-view .export-row > sac-icon { --icon-size: 20px; color: var(--accent); flex-shrink: 0; }
                fb-data-settings-view .export-info { flex: 1; min-width: 0; }
                fb-data-settings-view .export-name { font-size: 14px; font-weight: 600; color: var(--text); margin: 0 0 2px; }
                fb-data-settings-view .export-meta { font-size: 12px; color: var(--text-muted); }
                fb-data-settings-view .export-row .btn { width: auto; flex-shrink: 0; }
                fb-data-settings-view .storage-bar {
                    height: 6px;
                    border-radius: var(--radius-s);
                    background: var(--field);
                    overflow: hidden;
                    margin: 4px 0 8px;
                }
                fb-data-settings-view .storage-bar > span { display: block; height: 100%; background: var(--accent); }
                fb-data-settings-view .storage-bar.high > span { background: var(--danger); }
                fb-data-settings-view .storage-text { font-size: 14px; color: var(--text); margin: 0; }
            </style>
            <header>
                <h1>Your data</h1>
                <p class="subtitle">
                    Download everything in this workspace — it's yours. Each download is a ZIP
                    you can open anywhere; the database is a plain SQLite file.
                </p>
            </header>
            <div id="data-body"></div>
        `;
    }

    async refresh() {
        const mount = this.querySelector("#data-body");
        if (!mount) return;
        const scope = sac.scope.get();
        const inSpace = scope.type === "scoped";

        let info = null, usage = null, denied = false;
        try { info = await fb.api.export.info(); }
        catch (err) { if (err?.status === 403) denied = true; else console.warn("[fb-data-settings-view] info failed:", err); }
        try { usage = await fb.api.files.usage(); } catch { usage = null; }

        mount.replaceChildren();
        if (usage) mount.appendChild(this._storagePanel(usage, inSpace));

        const panel = document.createElement("div");
        panel.className = "panel";
        panel.innerHTML = `<h2>Export</h2>`;
        if (denied) {
            const p = document.createElement("p");
            p.textContent = "Only the space's owner can export it.";
            panel.appendChild(p);
        } else if (!info) {
            const p = document.createElement("p");
            p.className = "muted";
            p.textContent = "The export can't be reached right now.";
            panel.appendChild(p);
        } else {
            panel.appendChild(this._row("backup", "Database",
                `${fmtBytes(info.dbBytes)} · notes, todos, events, contacts — a SQLite file`, "db"));
            panel.appendChild(this._row("folder", "All files",
                `${fmtBytes(info.filesBytes)} · the file tree as it is, without the trash`, "files"));
            if (info.combinedAllowed) {
                panel.appendChild(this._row("download", "Database and files",
                    `${fmtBytes(info.dbBytes + info.filesBytes)} · both in one ZIP`, "all"));
            } else {
                const p = document.createElement("p");
                p.className = "muted";
                p.textContent = `Both in one ZIP is offered up to ${fmtBytes(info.combinedMaxBytes)} of files — download them separately.`;
                panel.appendChild(p);
            }
        }
        mount.appendChild(panel);
    }

    _storagePanel(usage, inSpace) {
        const panel = document.createElement("div");
        panel.className = "panel storage";
        panel.innerHTML = `<h2>Storage</h2>`;
        const text = document.createElement("p");
        text.className = "storage-text";
        const quota = usage.ownerQuotaBytes || 0;
        const used = usage.ownerBytes || 0;
        const whose = inSpace ? "The space's owner uses" : "You use";
        text.textContent = quota > 0
            ? `${whose} ${fmtBytes(used)} of ${fmtBytes(quota)}.`
            : `${whose} ${fmtBytes(used)} — no quota.`;
        panel.appendChild(text);
        if (quota > 0) {
            const ratio = Math.min(1, used / quota);
            const bar = document.createElement("div");
            bar.className = "storage-bar" + (ratio >= 0.9 ? " high" : "");
            bar.setAttribute("role", "meter");
            bar.setAttribute("aria-valuemin", "0");
            bar.setAttribute("aria-valuemax", String(quota));
            bar.setAttribute("aria-valuenow", String(used));
            bar.innerHTML = `<span style="width: ${(ratio * 100).toFixed(1)}%"></span>`;
            panel.appendChild(bar);
        }
        const note = document.createElement("p");
        note.className = "muted";
        note.textContent = inSpace
            ? `This space's files: ${fmtBytes(usage.bytes)}. A space counts toward its owner's quota.`
            : `One quota covers your personal workspace and every space you own.`;
        panel.appendChild(note);
        return panel;
    }

    _row(icon, name, meta, kind) {
        const row = document.createElement("div");
        row.className = "export-row";
        row.dataset.kind = kind;
        row.innerHTML = `
            <sac-icon name="${icon}"></sac-icon>
            <div class="export-info">
                <p class="export-name"></p>
                <div class="export-meta"></div>
            </div>
            <a class="btn" download>
                <sac-icon name="download"></sac-icon><span>Download</span>
            </a>`;
        row.querySelector(".export-name").textContent = name;
        row.querySelector(".export-meta").textContent = meta;
        row.querySelector("a").href = fb.api.export.url(kind);
        return row;
    }
}

function fmtBytes(n) {
    n = Number(n) || 0;
    if (n < 1024) return `${n} B`;
    const units = ["KB", "MB", "GB", "TB"];
    let v = n / 1024, i = 0;
    while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
    return `${v.toFixed(v < 10 ? 1 : 0)} ${units[i]}`;
}

customElements.define("fb-data-settings-view", FbDataSettingsView);
sac.router.register("#/data", "fb-data-settings-view", { label: "Your data", icon: "download", palette: false });
})();
