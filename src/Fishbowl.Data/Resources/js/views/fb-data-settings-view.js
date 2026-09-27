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
        this.classList.add("fb-page");
        this.innerHTML = `
            <style>
                /* Page frame, cards, rows, buttons and the meter are the
                   kit's (app.css .fb-page / .fb-row, <sac-progress>). */
                fb-data-settings-view .storage sac-progress { margin: 4px 0 8px; }
                fb-data-settings-view .storage .muted { margin: 12px 0 0; }
            </style>
            <header><div>
                <h1>${fb.t("fb.data.title", "Your data")}</h1>
                <p class="subtitle">
                    ${fb.t("fb.data.subtitle", "Download everything in this workspace — it's yours. Each download is a ZIP you can open anywhere; the database is a plain SQLite file.")}
                </p>
            </div></header>
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

        const { card, panel } = section(fb.t("fb.data.export", "Export"));
        if (denied) {
            const p = document.createElement("p");
            p.textContent = fb.t("fb.data.owner-only", "Only the space's owner can export it.");
            panel.appendChild(p);
        } else if (!info) {
            const p = document.createElement("p");
            p.className = "muted";
            p.textContent = fb.t("fb.data.unavailable", "The export can't be reached right now.");
            panel.appendChild(p);
        } else {
            panel.appendChild(this._row("backup", fb.t("fb.data.db", "Database"),
                fb.t("fb.data.db-meta", "{size} · notes, todos, events, contacts — a SQLite file", { size: fmtBytes(info.dbBytes) }), "db"));
            panel.appendChild(this._row("folder", fb.t("fb.data.files", "All files"),
                fb.t("fb.data.files-meta", "{size} · the file tree as it is, without the trash", { size: fmtBytes(info.filesBytes) }), "files"));
            if (info.combinedAllowed) {
                panel.appendChild(this._row("download", fb.t("fb.data.all", "Database and files"),
                    fb.t("fb.data.all-meta", "{size} · both in one ZIP", { size: fmtBytes(info.dbBytes + info.filesBytes) }), "all"));
            } else {
                const p = document.createElement("p");
                p.className = "muted";
                p.textContent = fb.t("fb.data.all-too-big", "Both in one ZIP is offered up to {size} of files — download them separately.", { size: fmtBytes(info.combinedMaxBytes) });
                panel.appendChild(p);
            }
        }
        mount.appendChild(card);
    }

    _storagePanel(usage, inSpace) {
        const { card, panel } = section(fb.t("fb.data.storage", "Storage"));
        card.classList.add("storage");
        const text = document.createElement("p");
        text.className = "storage-text";
        const quota = usage.ownerQuotaBytes || 0;
        const used = usage.ownerBytes || 0;
        const vars = { used: fmtBytes(used), quota: fmtBytes(quota) };
        text.textContent = quota > 0
            ? (inSpace ? fb.t("fb.data.owner-uses-of", "The space's owner uses {used} of {quota}.", vars)
                       : fb.t("fb.data.you-use-of", "You use {used} of {quota}.", vars))
            : (inSpace ? fb.t("fb.data.owner-uses", "The space's owner uses {used} — no quota.", vars)
                       : fb.t("fb.data.you-use", "You use {used} — no quota.", vars));
        panel.appendChild(text);
        if (quota > 0) {
            const ratio = Math.min(1, used / quota);
            const bar = document.createElement("sac-progress");
            bar.setAttribute("max", "1000");
            bar.setAttribute("value", String(Math.round(ratio * 1000)));
            bar.setAttribute("aria-label", fb.t("fb.data.storage-used", "Storage used"));
            panel.appendChild(bar);
        }
        const note = document.createElement("p");
        note.className = "muted";
        note.textContent = inSpace
            ? fb.t("fb.data.space-files", "This space's files: {size}. A space counts toward its owner's quota.", { size: fmtBytes(usage.bytes) })
            : fb.t("fb.data.one-quota", "One quota covers your personal workspace and every space you own.");
        panel.appendChild(note);
        return card;
    }

    _row(icon, name, meta, kind) {
        const row = document.createElement("div");
        row.className = "fb-row export-row";
        row.dataset.kind = kind;
        row.innerHTML = `
            <sac-icon name="${icon}"></sac-icon>
            <div class="fb-row-info">
                <p class="fb-row-name export-name"></p>
                <div class="fb-row-meta export-meta"></div>
            </div>
            <div class="toolbar">
                <a class="btn" download>
                    <sac-icon name="download"></sac-icon><span>${fb.t("fb.data.download", "Download")}</span>
                </a>
            </div>`;
        row.querySelector(".export-name").textContent = name;
        row.querySelector(".export-meta").textContent = meta;
        row.querySelector("a").href = fb.api.export.url(kind);
        return row;
    }
}

// A kit card headed by a <sac-section>; content goes into the section.
function section(title) {
    const card = document.createElement("div");
    card.className = "card";
    const panel = document.createElement("sac-section");
    panel.setAttribute("title", title);
    card.appendChild(panel);
    return { card, panel };
}

function fmtBytes(n) { return fb.format.bytes(n); }

customElements.define("fb-data-settings-view", FbDataSettingsView);
sac.router.register("#/data", "fb-data-settings-view", { label: "Your data", icon: "download", palette: false });
})();
