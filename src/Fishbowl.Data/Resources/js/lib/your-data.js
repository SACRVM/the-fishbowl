/*
 * fb.yourData — "Your data…" from the account menu: storage and export in a
 * <sac-window> (Files phase 3), not a page.
 *
 *   fb.yourData.open()
 *
 * Always the personal workspace; while a space is active, that space too.
 * Export is three independent downloads, each a ZIP the server streams (so a
 * plain link, never a Blob in memory): the database, all files (without the
 * trash), or both in one ZIP — the last offered only while the files are
 * below Export:CombinedMaxBytes; above it a line says why instead of a dead
 * button. Storage shows what the workspace's owner uses against their quota
 * (one quota covers the personal workspace and every space they own). A
 * space's export is owner-only, so a member sees a note instead of buttons.
 */
(function () {
    let win = null;

    function ensureWindow() {
        if (win?.isConnected) return win;
        win = document.createElement("sac-window");
        win.id = "fb-your-data";
        win.setAttribute("width", "520px");
        win.setAttribute("height", "auto");
        win.setAttribute("top", "70px");
        win.setAttribute("left", "calc(50vw - 260px)");
        win.setAttribute("controls", "close");
        document.body.appendChild(win);
        return win;
    }

    async function open() {
        const w = ensureWindow();
        w.setAttribute("title", fb.t("fb.data.title", "Your data"));
        w.innerHTML = `
            <style>
                #fb-your-data .yd-body { padding: 4px 16px 12px; }
                #fb-your-data sac-section { margin-bottom: 16px; }
                #fb-your-data .yd-text { margin: 0 0 4px; }
                #fb-your-data sac-progress { margin: 4px 0 8px; }
                #fb-your-data .muted { color: var(--text-muted); font-size: 13px; margin: 8px 0 0; }
            </style>
            <div class="yd-body">
                <div class="yd-sections"></div>
            </div>`;
        requestAnimationFrame(() => w.open());

        const scope = sac.scope.get();
        const parts = [await workspaceSection("personal", fb.t("fb.data.personal", "Personal"), false)];
        if (scope.type === "scoped") {
            const space = (await fb.api.spaces.list().catch(() => []))?.find((s) => s.slug === scope.slug);
            parts.push(await workspaceSection(`space:${scope.slug}`, space?.name || scope.slug, true));
        }
        w.querySelector(".yd-sections")?.replaceChildren(...parts);
    }

    async function workspaceSection(ws, title, inSpace) {
        let info = null, usage = null, denied = false;
        try { info = await fb.api.export.info(ws); }
        catch (err) { if (err?.status === 403) denied = true; else console.warn("[fb.yourData] info failed:", err?.status); }
        try { usage = await fb.api.files.in(ws).usage(); } catch { usage = null; }

        const section = document.createElement("sac-section");
        section.setAttribute("title", title);
        section.dataset.workspace = ws;
        if (usage) section.append(...storage(usage, inSpace));

        const note = (text) => {
            const p = document.createElement("p");
            p.className = "muted";
            p.textContent = text;
            return p;
        };
        if (denied) {
            section.appendChild(note(fb.t("fb.data.owner-only", "Only the space's owner can export it.")));
        } else if (!info) {
            section.appendChild(note(fb.t("fb.data.unavailable", "The export can't be reached right now.")));
        } else {
            section.appendChild(row(ws, "backup", fb.t("fb.data.db", "Database"),
                fb.t("fb.data.db-meta", "{size} · notes, todos, events, contacts — a SQLite file", { size: bytes(info.dbBytes) }), "db"));
            section.appendChild(row(ws, "folder", fb.t("fb.data.files", "All files"),
                fb.t("fb.data.files-meta", "{size} · the file tree as it is, without the trash", { size: bytes(info.filesBytes) }), "files"));
            if (info.combinedAllowed) {
                section.appendChild(row(ws, "download", fb.t("fb.data.all", "Database and files"),
                    fb.t("fb.data.all-meta", "{size} · both in one ZIP", { size: bytes(info.dbBytes + info.filesBytes) }), "all"));
            } else {
                section.appendChild(note(fb.t("fb.data.all-too-big", "Both in one ZIP is offered up to {size} of files — download them separately.", { size: bytes(info.combinedMaxBytes) })));
            }
        }
        return section;
    }

    function storage(usage, inSpace) {
        const out = [];
        const text = document.createElement("p");
        text.className = "yd-text storage-text";
        const quota = usage.ownerQuotaBytes || 0;
        const used = usage.ownerBytes || 0;
        const vars = { used: bytes(used), quota: bytes(quota) };
        text.textContent = quota > 0
            ? (inSpace ? fb.t("fb.data.owner-uses-of", "The space's owner uses {used} of {quota}.", vars)
                       : fb.t("fb.data.you-use-of", "You use {used} of {quota}.", vars))
            : (inSpace ? fb.t("fb.data.owner-uses", "The space's owner uses {used} — no quota.", vars)
                       : fb.t("fb.data.you-use", "You use {used} — no quota.", vars));
        out.push(text);
        if (quota > 0) {
            const bar = document.createElement("sac-progress");
            bar.setAttribute("max", "1000");
            bar.setAttribute("value", String(Math.round(Math.min(1, used / quota) * 1000)));
            bar.setAttribute("aria-label", fb.t("fb.data.storage-used", "Storage used"));
            out.push(bar);
        }
        const note = document.createElement("p");
        note.className = "muted";
        note.textContent = inSpace
            ? fb.t("fb.data.space-files", "This space's files: {size}. A space counts toward its owner's quota.", { size: bytes(usage.bytes) })
            : fb.t("fb.data.one-quota", "One quota covers your personal workspace and every space you own.");
        out.push(note);
        return out;
    }

    function row(ws, icon, name, meta, kind) {
        const r = document.createElement("div");
        r.className = "fb-row export-row";
        r.dataset.kind = kind;
        r.innerHTML = `
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
        r.querySelector(".export-name").textContent = name;
        r.querySelector(".export-meta").textContent = meta;
        r.querySelector("a").href = fb.api.export.url(kind, ws);
        return r;
    }

    const bytes = (n) => fb.format.bytes(n);

    fb.yourData = { open };
})();
