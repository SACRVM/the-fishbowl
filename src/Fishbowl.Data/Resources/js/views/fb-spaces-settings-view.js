/**
 * <fb-spaces-settings-view>  (a window app, fb.windowApps "spaces")
 *
 * Minimal management surface for space workspaces — list, create, delete,
 * members (fb.spaceMembers: roles, invitation links, leaving),
 * and the archived spaces a delete left behind (Files phase 3): deleting asks
 * "Archive before deleting" (checked by default); archives are listed with
 * Download / Restore / Delete and the days left before the purge.
 * Each space is a shared data context backed by its own SQLite file under
 * fishbowl-data/spaces/{spaceId}/space.db (keyed by ULID, not slug — so renames
 * don't move the file). Notes live inside via /api/v1/spaces/{slug}/notes; the API
 * translates slug→id before opening the DB. Renders the id next to each
 * space (and the user's own id for the personal workspace) so operators can
 * match rows to files on disk.
 *
 * Light-DOM so app.css tokens apply.
 */
class FbSpacesSettingsView extends HTMLElement {
    constructor() {
        super();
        this.spaces = [];
        this.archives = [];
        this.me = null;
        this.busy = false;
    }

    async connectedCallback() {
        this.render();
        await this.refresh();
    }

    async refresh() {
        try {
            const [spaces, me, archives] = await Promise.all([
                fb.api.spaces.list(),
                fb.api.me.get().catch(() => null),
                fb.api.archive.list().catch(() => []),
            ]);
            this.spaces = spaces || [];
            this.me = me;
            this.archives = archives || [];
        } catch (err) {
            console.error("[fb-spaces-settings-view] list failed:", err);
            this.spaces = [];
            this.archives = [];
            this.me = null;
        }
        this.renderPersonal();
        this.renderList();
        this.renderArchives();
    }

    render() {
        this.classList.add("fb-page");
        this.innerHTML = `
            <style>
                /* Page frame, cards, rows, buttons and chips are the kit's
                   (app.css .fb-page / .fb-row); only this page's own bits. */
                fb-spaces-settings-view .create-row { display: flex; align-items: center; gap: 8px; }
                fb-spaces-settings-view .create-row input { flex: 1; min-width: 0; }
                /* The space's colour, leading the row. For the owner it is
                   a button that opens the colour picker. */
                fb-spaces-settings-view .space-color {
                    flex-shrink: 0;
                    width: 22px;
                    height: 22px;
                    padding: 0;
                    border-radius: 50%;
                    border: 1px solid var(--border);
                    background: var(--space-color);
                }
                fb-spaces-settings-view button.space-color { cursor: pointer; }
                fb-spaces-settings-view button.space-color:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
                /* The delete dialog's body lives in the dialog, outside the view. */
                .fb-space-delete p { margin: 0 0 12px; }
                .fb-space-delete .fb-space-delete-hint { color: var(--text-muted); font-size: 13px; margin: 8px 0 0; }
            </style>

            <header><div>
                <h1>${fb.t("fb.spaces.title", "Spaces")}</h1>
                <p class="subtitle">
                    ${fb.t("fb.spaces.subtitle", "Shared workspaces — each space has its own notes, separate from your personal data.")}
                </p>
            </div></header>

            <div class="fb-block">
                <sac-section title="${fb.t("fb.spaces.personal", "Personal workspace")}">
                    <div id="personal-row"></div>
                </sac-section>
            </div>

            <div class="fb-block">
                <sac-section title="${fb.t("fb.spaces.new", "New space")}">
                    <sac-status-banner id="form-status"></sac-status-banner>
                    <div class="create-row">
                        <input type="text" id="name-input" placeholder="${fb.t("fb.spaces.name-placeholder", "New space name (e.g. 'Fishbowl Dev')")}" maxlength="60"/>
                        <div class="toolbar">
                            <button type="button" class="btn primary" id="create-btn">${fb.t("fb.spaces.create", "Create space")}</button>
                        </div>
                    </div>
                </sac-section>
            </div>

            <div class="fb-block">
                <sac-section title="${fb.t("fb.spaces.yours", "Your spaces")}">
                    <div id="space-list"></div>
                </sac-section>
            </div>

            <div class="fb-block" id="archive-section" hidden>
                <sac-section title="${fb.t("fb.spaces.archived", "Archived spaces")}">
                    <div id="archive-list"></div>
                </sac-section>
            </div>
        `;

        const input  = this.querySelector("#name-input");
        const btn    = this.querySelector("#create-btn");

        btn.addEventListener("click", () => this._create());
        input.addEventListener("keydown", (e) => {
            if (e.key === "Enter") this._create();
        });
        input.addEventListener("input", () => this._clearStatus());
    }

    _showStatus(message, kind = "error") {
        this.querySelector("#form-status")?.show(message, kind);
    }

    _clearStatus() {
        this.querySelector("#form-status")?.hide();
    }

    renderPersonal() {
        const mount = this.querySelector("#personal-row");
        if (!mount) return;

        if (!this.me?.id) {
            mount.innerHTML = `<p class="muted">${fb.t("fb.spaces.loading", "Loading…")}</p>`;
            return;
        }

        mount.innerHTML = `
            <div class="fb-row space-row">
                <sac-icon name="user"></sac-icon>
                <div class="fb-row-info">
                    <p class="fb-row-name">${escapeHtml(this.me.name || this.me.email || fb.t("fb.spaces.you", "You"))}</p>
                    <div class="fb-row-meta">
                        <code title="users/${escapeAttr(this.me.id)}/personal.db">${escapeHtml(this.me.id)}</code>
                    </div>
                </div>
            </div>
        `;
    }

    renderList() {
        const list = this.querySelector("#space-list");
        if (!list) return;

        if (this.spaces.length === 0) {
            list.innerHTML = `
                <div class="empty-state">
                    <sac-icon name="users"></sac-icon>
                    <h3>${fb.t("fb.spaces.empty", "No spaces yet")}</h3>
                    <p>${fb.t("fb.spaces.empty-hint", "Create one above.")}</p>
                </div>`;
            return;
        }

        list.innerHTML = this.spaces.map(t => `
            <div class="fb-row space-row" data-slug="${escapeAttr(t.slug)}">
                ${t.role === "owner"
                    ? `<button type="button" class="space-color" title="${fb.t("fb.spaces.change-colour", "Change colour")}" aria-label="${fb.t("fb.spaces.change-colour", "Change colour")}"
                               style="--space-color: ${this._colorVar(t.color)}"></button>`
                    : `<span class="space-color" title="${fb.t("fb.spaces.colour", "Space colour")}" style="--space-color: ${this._colorVar(t.color)}"></span>`}
                <div class="fb-row-info">
                    <p class="fb-row-name">${escapeHtml(t.name)}</p>
                    <div class="fb-row-meta">
                        <span>/${escapeHtml(t.slug)}</span>
                        <sac-chip class="role" label="${escapeAttr(roleLabel(t.role))}"></sac-chip>
                        <code title="spaces/${escapeAttr(t.id)}/space.db">${escapeHtml(t.id)}</code>
                    </div>
                </div>
                ${t.role === "owner"
                    ? `<button type="button" class="icon-btn settings-btn" title="${fb.t("fb.spaces.settings", "Settings")}" aria-label="${fb.t("fb.spaces.settings", "Settings")}">
                           <sac-icon name="settings"></sac-icon>
                       </button>`
                    : ""}
                <button type="button" class="icon-btn members-btn" title="${fb.t("fb.spaces.members", "Members")}" aria-label="${fb.t("fb.spaces.members", "Members")}">
                    <sac-icon name="users"></sac-icon>
                </button>
                <button type="button" class="icon-btn open-btn" title="${fb.t("fb.spaces.open", "Open this space")}" aria-label="${fb.t("fb.spaces.open-aria", "Open")}">
                    <sac-icon name="chevron-right"></sac-icon>
                </button>
                ${t.role === "owner"
                    ? `<button type="button" class="icon-btn danger delete-btn" title="${fb.t("fb.spaces.delete", "Delete space")}" aria-label="${fb.t("fb.spaces.delete-aria", "Delete")}">
                           <sac-icon name="trash"></sac-icon>
                       </button>`
                    : ""}
            </div>
        `).join("");

        list.querySelectorAll("button.space-color").forEach(btn => {
            btn.addEventListener("click", (e) => {
                const slug = e.currentTarget.closest(".space-row")?.dataset.slug;
                if (slug) this._pickColor(slug);
            });
        });

        list.querySelectorAll(".settings-btn").forEach(btn => {
            btn.addEventListener("click", (e) => {
                const slug = e.currentTarget.closest(".space-row")?.dataset.slug;
                const space = this.spaces.find(t => t.slug === slug);
                if (space) this._openSettings(space);
            });
        });

        list.querySelectorAll(".members-btn").forEach(btn => {
            btn.addEventListener("click", (e) => {
                const slug = e.currentTarget.closest(".space-row")?.dataset.slug;
                const space = this.spaces.find(t => t.slug === slug);
                if (space) fb.spaceMembers.open(space, { onChanged: () => this.refresh() });
            });
        });

        // Open = switch the workspace to that space and land on its desktop.
        list.querySelectorAll(".open-btn").forEach(btn => {
            btn.addEventListener("click", (e) => {
                const slug = e.currentTarget.closest(".space-row")?.dataset.slug;
                if (!slug) return;
                this.closest("sac-window")?.close?.();   // the space is what you came for
                sac.router.navigate(`#/space/${encodeURIComponent(slug)}/`);
            });
        });

        list.querySelectorAll(".delete-btn").forEach(btn => {
            btn.addEventListener("click", async (e) => {
                const row  = e.currentTarget.closest(".space-row");
                const slug = row?.dataset.slug;
                if (!slug) return;
                await this._delete(slug);
            });
        });
    }

    // The colour a space shows: its palette slot, or the kit's default
    // warm accent (orange) — the palette var, not --accent-warm, which
    // the active space itself may have overridden.
    _colorVar(slot) {
        return fb.accents.cssVar(fb.tags.SLOTS.includes(slot) ? slot : "orange");
    }

    // Colour picker window: the kit palette plus "Default". Picking one
    // saves it and closes; it tints the space's switcher pill and the
    // shared-data highlights while the space is active. Owner-only.
    // The owner's settings for a space: today, whether messages from the
    // space's apps reach chat with their text (default: a fixed line).
    _openSettings(space) {
        const dlg = document.createElement("sac-dialog");
        dlg.id = "fb-space-settings";
        dlg.setAttribute("title", fb.t("fb.spaces.settings-title", "{name} — settings", { name: space.name }));
        const label = document.createElement("label");
        label.className = "fb-check";
        const toggle = document.createElement("sac-toggle");
        toggle.id = "fb-space-app-text";
        if (space.appMessageText) toggle.setAttribute("checked", "");
        label.append(toggle, document.createTextNode(" " + fb.t("fb.spaces.app-message-text",
            "Messages from this space's apps reach chat with their text (otherwise: a line without it)")));
        toggle.addEventListener("change", async () => {
            const on = !!toggle.checked;
            try {
                await fb.api.spaces.update(space.slug, { appMessageText: on });
                space.appMessageText = on;
            } catch (err) {
                toggle.checked = !on;
                sac.toast?.(fb.errors.text(err, fb.t("fb.spaces.settings-failed", "Couldn't save the setting.")), { kind: "error" });
            }
        });
        dlg.appendChild(label);
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

    _pickColor(slug) {
        const space = this.spaces.find(t => t.slug === slug);
        if (!space) return;
        const dlg = document.createElement("sac-dialog");
        dlg.setAttribute("title", fb.t("fb.spaces.colour-title", "Colour — {name}", { name: space.name }));
        dlg.style.setProperty("--dialog-width", "340px");
        dlg.buttons = [{ action: "close", label: fb.t("fb.spaces.close", "Close"), kind: "default" }];
        const grid = document.createElement("sac-swatch-grid");
        grid.setAttribute("selectable", "");
        grid.setAttribute("columns", "6");
        grid.className = "fb-space-color-grid";
        dlg.appendChild(grid);
        grid.colors = fb.accents.swatches(space.color);
        // close() fires sac:action too, so one listener removes it either way.
        dlg.addEventListener("sac:action", () => setTimeout(() => dlg.remove(), 120), { once: true });
        grid.addEventListener("sac:change", async (e) => {
            try {
                await fb.api.spaces.update(slug, { color: fb.accents.slotOf(e.detail.value) });
                dlg.close("picked");
                await this.refresh();
            } catch (err) {
                console.warn("[fb-spaces-settings-view] colour failed:", err);
                grid.colors = fb.accents.swatches(space.color);
                window.sac?.toast?.(err?.status === 403 ? fb.t("fb.spaces.colour-owner-only", "Only the owner can change the colour.") : fb.t("fb.spaces.colour-failed", "Failed to save the colour."), { kind: "error" });
            }
        });
        document.body.appendChild(dlg);
        setTimeout(() => dlg.open(), 0);
    }

    async _create() {
        if (this.busy) return;
        this._clearStatus();

        const input = this.querySelector("#name-input");
        const name  = input.value.trim();
        if (!name) {
            this._showStatus(fb.t("fb.spaces.name-required", "Space name is required."));
            input.focus();
            return;
        }

        this.busy = true;
        this._setBusy(true);
        try {
            await fb.api.spaces.create({ name });
            input.value = "";
            await this.refresh();
        } catch (err) {
            console.warn("[fb-spaces-settings-view] create failed:", err);
            const status = err?.status;
            if (status === 400) this._showStatus(fb.errors.text(err, fb.t("fb.spaces.name-invalid", "Invalid space name.")));
            else                this._showStatus(fb.t("fb.spaces.create-failed", "Failed to create space."));
        } finally {
            this.busy = false;
            this._setBusy(false);
        }
    }

    // The delete dialog: "Archive before deleting" is checked by default —
    // a ZIP of the whole space you can download or restore until the purge.
    _askDelete(space) {
        return new Promise((resolve) => {
            const dlg = document.createElement("sac-dialog");
            dlg.setAttribute("title", fb.t("fb.spaces.delete-title", "Delete space \"{name}\"?", { name: space.name }));
            dlg.style.setProperty("--dialog-width", "440px");
            dlg.buttons = [
                { action: "cancel", label: fb.t("fb.spaces.cancel", "Cancel"), kind: "default" },
                { action: "delete", label: fb.t("fb.spaces.delete-confirm", "Delete"), kind: "destructive", armAfterMs: 1500 },
            ];
            dlg.innerHTML = `
                <div class="fb-space-delete">
                    <p>${fb.t("fb.spaces.delete-body", "Its notes, todos, events and files are removed for every member, and its API keys stop working.")}</p>
                    <label class="fb-check fb-space-archive">
                        <input type="checkbox" name="archive" checked>
                        <span>${fb.t("fb.spaces.archive-first", "Archive before deleting")}</span>
                    </label>
                    <p class="fb-space-delete-hint"></p>
                </div>`;
            const box = dlg.querySelector("input[name=archive]");
            const hint = dlg.querySelector(".fb-space-delete-hint");
            const paint = () => {
                hint.textContent = box.checked
                    ? fb.t("fb.spaces.archive-hint", "A ZIP of the whole space is kept under Archived spaces — download it or restore it as a new space.")
                    : fb.t("fb.spaces.no-archive-hint", "Nothing is kept. This can't be undone.");
            };
            box.addEventListener("change", paint);
            paint();
            dlg.addEventListener("sac:action", (e) => {
                const action = e.detail?.action;
                setTimeout(() => dlg.remove(), 120);
                resolve(action === "delete" ? { archive: box.checked } : null);
            }, { once: true });
            document.body.appendChild(dlg);
            setTimeout(() => dlg.open(), 0);
        });
    }

    async _delete(slug) {
        const space = this.spaces.find(t => t.slug === slug);
        if (!space) return;

        const choice = await this._askDelete(space);
        if (!choice) return;

        try {
            await fb.api.spaces.delete(slug, { archive: choice.archive });
            await this.refresh();
            if (choice.archive) window.sac?.toast?.(fb.t("fb.spaces.archived-deleted", "\"{name}\" was archived and deleted.", { name: space.name }), { kind: "success" });
        } catch (err) {
            console.warn("[fb-spaces-settings-view] delete failed:", err);
            const status = err?.status;
            if (status === 403)      this._showStatus(fb.t("fb.spaces.delete-owner-only", "Only the owner can delete this space."));
            else if (status === 507) this._showStatus(fb.t("fb.spaces.archive-no-disk", "Not enough disk space to archive it — nothing was deleted."));
            else                     this._showStatus(fb.t("fb.spaces.delete-failed", "Failed to delete space."));
        }
    }

    // Archived spaces — only while there are any (no empty section).
    renderArchives() {
        const section = this.querySelector("#archive-section");
        const list = this.querySelector("#archive-list");
        if (!section || !list) return;
        section.hidden = this.archives.length === 0;
        list.replaceChildren(...this.archives.map(a => {
            const row = document.createElement("div");
            row.className = "fb-row space-row archive-row";
            row.dataset.archive = a.id;
            row.innerHTML = `
                <span class="space-color" style="--space-color: ${this._colorVar(a.color)}"></span>
                <div class="fb-row-info">
                    <p class="fb-row-name space-name"></p>
                    <div class="fb-row-meta"><span class="archived-at"></span><span class="expires"></span></div>
                </div>
                <a class="icon-btn open-btn" title="${fb.t("fb.spaces.archive-download", "Download the archive")}" aria-label="${fb.t("fb.spaces.archive-download-aria", "Download")}" download>
                    <sac-icon name="download"></sac-icon>
                </a>
                <button type="button" class="icon-btn open-btn restore-btn" title="${fb.t("fb.spaces.archive-restore", "Restore as a new space")}" aria-label="${fb.t("fb.spaces.archive-restore-aria", "Restore")}">
                    <sac-icon name="undo"></sac-icon>
                </button>
                <button type="button" class="icon-btn danger delete-btn" title="${fb.t("fb.spaces.archive-delete", "Delete the archive")}" aria-label="${fb.t("fb.spaces.archive-delete-aria", "Delete archive")}">
                    <sac-icon name="trash"></sac-icon>
                </button>`;
            row.querySelector(".space-name").textContent = a.name;
            row.querySelector(".archived-at").textContent = fb.t("fb.spaces.archived-at", "Archived {when} · {size}", { when: fb.format.dateTime(a.archivedAt), size: fmtSize(a.sizeBytes) });
            row.querySelector(".expires").textContent = a.expiresAt
                ? daysLeft(a.expiresAt)
                : fb.t("fb.spaces.kept", "Kept until you delete it");
            row.querySelector("a").href = fb.api.archive.downloadUrl(a.id);
            row.querySelector(".restore-btn").addEventListener("click", () => this._restore(a));
            row.querySelector(".delete-btn").addEventListener("click", () => this._deleteArchive(a));
            return row;
        }));

        function daysLeft(iso) {
            const days = Math.ceil((new Date(iso).getTime() - Date.now()) / 86400000);
            return days <= 0 ? fb.t("fb.spaces.expires-now", "Deleted at the next cleanup")
                : days === 1 ? fb.t("fb.spaces.expires-1", "Deleted in 1 day")
                : fb.t("fb.spaces.expires-n", "Deleted in {n} days", { n: days });
        }
    }

    async _restore(a) {
        try {
            const space = await fb.api.archive.restore(a.id);
            window.sac?.toast?.(fb.t("fb.spaces.restored", "Restored as \"{name}\" ({slug}) — you're its only member.", { name: space.name, slug: space.slug }), { kind: "success" });
            await this.refresh();
        } catch (err) {
            console.warn("[fb-spaces-settings-view] restore failed:", err);
            window.sac?.toast?.(err?.status === 507 ? fb.t("fb.spaces.restore-no-disk", "Not enough disk space to restore it.") : fb.t("fb.spaces.restore-failed", "Failed to restore the space."), { kind: "error" });
        }
    }

    async _deleteArchive(a) {
        const result = await sac.dialog.confirm({
            title: fb.t("fb.spaces.archive-delete-title", "Delete the archive of \"{name}\"?", { name: a.name }),
            message: fb.t("fb.spaces.archive-delete-body", "The archive is removed for good — it can't be restored afterwards."),
            buttons: [
                { action: "cancel", label: fb.t("fb.spaces.cancel", "Cancel"), kind: "default" },
                { action: "delete", label: fb.t("fb.spaces.delete-confirm", "Delete"), kind: "destructive", armAfterMs: 1500 },
            ],
        });
        if (result !== "delete") return;
        try {
            await fb.api.archive.remove(a.id);
            await this.refresh();
        } catch (err) {
            console.warn("[fb-spaces-settings-view] archive delete failed:", err);
            window.sac?.toast?.(fb.t("fb.spaces.archive-delete-failed", "Failed to delete the archive."), { kind: "error" });
        }
    }

    _setBusy(busy) {
        const btn = this.querySelector("#create-btn");
        if (btn) btn.disabled = busy;
    }
}

function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, c => ({"&":"&amp;","<":"&lt;",">":"&gt;","\"":"&quot;","'":"&#39;"}[c]));
}
function escapeAttr(s) { return escapeHtml(s); }
function roleLabel(role) { return fb.spaceMembers.roleLabel(role); }
function fmtSize(n) { return fb.format.bytes(n); }

customElements.define("fb-spaces-settings-view", FbSpacesSettingsView);
