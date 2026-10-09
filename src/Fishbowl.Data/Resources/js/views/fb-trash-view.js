/**
 * <fb-trash-view>  (a window app, fb.windowApps "trash", both workspaces)
 *
 * One trash for everything (space-apps spec, decision 13): the workspace's
 * deleted notes, todos, events and contacts (fb.api.trash) and its deleted
 * files (fb.api.files trash) in one list, newest first. Restore puts an
 * item back — or says why it can't (a file offers "as a copy"); delete for
 * good asks first; "Empty trash" in the toolbar empties both. A reader in a
 * space sees the list without the write buttons.
 *
 * Light DOM, a settings-style page (.fb-page rows).
 */
class FbTrashView extends HTMLElement {
    constructor() {
        super();
        this.items = [];
        this.writable = true;
    }

    connectedCallback() {
        this.classList.add("fb-page");
        this.innerHTML = `
            <header><div>
                <h1>${fb.t("fb.trash.title", "Trash")}</h1>
                <p class="subtitle">${fb.t("fb.trash.subtitle", "What was deleted in this workspace. Items are removed for good after 30 days.")}</p>
            </div></header>
            <div class="fb-block" id="trash-list"></div>`;
        this._onScope = () => this.refresh();
        window.addEventListener("sac:scope-changed", this._onScope);
        // Keyboard: arrows move a cursor, Shift+arrows mark, Delete deletes
        // for good, Enter restores. Attached once; marks survive a repaint.
        const list = this.querySelector("#trash-list");
        this.sel = sac.selection.attach(list, {
            rows: ".fb-trash-row",
            disabled: () => !this.writable,
            onChange: () => this.paintToolbar(),
            keyboard: true,
        });
        list.addEventListener("sac:request-remove", (e) => this.removeMany(this.byIds(e.detail.ids)));
        list.addEventListener("sac:activate", (e) => this.restoreMany(this.byIds([e.detail.id])));
        this.refresh();
    }

    disconnectedCallback() {
        window.removeEventListener("sac:scope-changed", this._onScope);
        this.sel?.destroy();
    }

    // The items behind some row ids that this caller may change.
    byIds(ids) {
        if (!this.writable) return [];
        return this.items.filter((i) => ids.includes(i.id) && i.canChange !== false);
    }

    // Empty trash, and while rows are marked: restore / delete them, clear.
    paintToolbar() {
        const n = this.sel?.marked.length || 0;
        const items = [];
        if (this.writable && n) {
            const marked = () => this.byIds(this.sel.marked);
            items.push(
                { id: "fb-trash-restore-marked", icon: "undo", title: fb.t("fb.trash.restore-marked", "Restore selected ({n})", { n }), onClick: () => this.restoreMany(marked()) },
                { id: "fb-trash-delete-marked", icon: "trash", title: fb.t("fb.trash.delete-marked", "Delete selected for good ({n})", { n }), onClick: () => this.removeMany(marked()) },
                { id: "fb-trash-clear-marks", icon: "close", title: fb.t("fb.trash.clear-marks", "Clear selection"), onClick: () => this.sel.clear() });
        }
        if (this.writable && this.items.length) {
            items.push({ id: "fb-trash-empty", icon: "trash", title: fb.t("fb.trash.empty-action", "Empty trash"), onClick: () => this.emptyAll() });
        }
        fb.windowApps.toolbar(this, items);
    }

    async refresh() {
        // The list and its buttons belong to one workspace: the one it was
        // read from, whatever is active when a button is pressed. The newest
        // read wins (a scope switch while one is under way).
        const seq = this._seq = (this._seq || 0) + 1;
        const scope = sac.scope.get();
        const ws = fb.api.workspace();
        const records = fb.api.trash.in(ws);
        const files = fb.api.files.in(ws);
        const [recordList, fileList, spaces] = await Promise.all([
            records.list().catch(() => []),
            files.trashList().catch(() => []),
            scope.type === "scoped" ? fb.api.spaces.list().catch(() => []) : Promise.resolve([]),
        ]);
        if (!this.isConnected) return;   // left meanwhile: the toolbar belongs to another view now
        if (seq !== this._seq) return;
        this.recordsApi = records;
        this.filesApi = files;
        if (scope.type === "scoped") {
            const role = spaces.find((s) => s.slug === scope.slug)?.role;
            this.writable = role !== "reader";
        } else {
            this.writable = true;
        }
        this.items = [
            ...(recordList || []).map((r) => ({ ...r, source: "record" })),
            ...(fileList || []).map((f) => ({
                id: f.id, source: "file", kind: f.kind === "folder" ? "folder" : "file",
                title: f.originalPath, deletedAt: f.deletedAt, deletedBy: null,
            })),
        ].sort((a, b) => String(b.deletedAt).localeCompare(String(a.deletedAt)));
        this.render();
    }

    render() {
        const list = this.querySelector("#trash-list");
        this.paintToolbar();
        if (!this.items.length) {
            list.innerHTML = `
                <div class="empty-state">
                    <sac-icon name="trash"></sac-icon>
                    <h3>${fb.t("fb.trash.empty", "The trash is empty")}</h3>
                </div>`;
            return;
        }
        list.replaceChildren(...this.items.map((item) => this.row(item)));
    }

    row(item) {
        const icons = { note: "note", todo: "check", event: "calendar", contact: "user", mail: "mail", row: "grid", table: "grid", file: "document", folder: "folder" };
        const kinds = {
            note: fb.t("fb.trash.kind-note", "Note"), todo: fb.t("fb.trash.kind-todo", "Todo"),
            event: fb.t("fb.trash.kind-event", "Event"), contact: fb.t("fb.trash.kind-contact", "Contact"),
            mail: fb.t("fb.trash.kind-mail", "Mail"),
            row: fb.t("fb.trash.kind-row", "Row"), table: fb.t("fb.trash.kind-table", "Table"),
            file: fb.t("fb.trash.kind-file", "File"), folder: fb.t("fb.trash.kind-folder", "Folder"),
        };
        const row = document.createElement("div");
        row.className = "fb-row fb-trash-row";
        row.dataset.id = item.id;
        row.dataset.kind = item.kind;
        row.tabIndex = -1;   // takes focus as the keyboard cursor moves
        row.innerHTML = `
            <sac-icon name="${icons[item.kind] || "document"}"></sac-icon>
            <div class="fb-row-info">
                <p class="fb-row-name"></p>
                <div class="fb-row-meta"><span class="kind"></span><span class="when"></span></div>
            </div>`;
        row.querySelector(".fb-row-name").textContent = item.title || fb.t("fb.trash.untitled", "Untitled");
        row.querySelector(".kind").textContent = kinds[item.kind] || item.kind;
        const when = fb.format.dateTime(new Date(item.deletedAt));
        row.querySelector(".when").textContent = item.deletedBy
            ? fb.t("fb.trash.deleted-by", "deleted {when} by {who}", { when, who: item.deletedBy })
            : fb.t("fb.trash.deleted", "deleted {when}", { when });
        // canChange: the server says this caller may restore / delete it
        // (a dropped table needs a Designer, another's own-rows row too).
        if (this.writable && item.canChange !== false) {
            row.append(
                this.button("undo", fb.t("fb.trash.restore", "Restore"), "restore-btn", () => this.restore(item)),
                this.button("trash", fb.t("fb.trash.delete-forever", "Delete for good"), "delete-btn danger", () => this.remove(item)));
        }
        return row;
    }

    button(icon, label, cls, onClick) {
        const b = document.createElement("button");
        b.type = "button";
        b.className = "icon-btn " + cls;
        b.title = label;
        b.setAttribute("aria-label", label);
        b.innerHTML = `<sac-icon name="${icon}"></sac-icon>`;
        b.addEventListener("click", onClick);
        return b;
    }

    // Puts one item back; false when the person declined a file's copy.
    async restoreOne(item) {
        if (item.source === "record") {
            await this.recordsApi.restore(item.id);
        } else {
            try {
                await this.filesApi.restore(item.id);
            } catch (err) {
                if (err?.status !== 409) throw err;
                const answer = await sac.dialog.confirm({
                    title: fb.t("fb.files.in-the-way", "Something is in the way"),
                    message: fb.t("fb.files.in-the-way-msg", "“{name}” exists again at its old place.", { name: item.title }),
                    buttons: [
                        { action: "cancel", label: fb.t("fb.common.cancel", "Cancel") },
                        { action: "rename", label: fb.t("fb.files.restore-copy", "Restore as a copy"), kind: "primary" },
                    ],
                });
                if (answer !== "rename") return false;
                await this.filesApi.restore(item.id, "rename");
            }
        }
        return true;
    }

    async restore(item) {
        try {
            if (await this.restoreOne(item)) {
                sac.toast(fb.t("fb.trash.restored", "“{name}” is back.", { name: item.title || "" }), { kind: "success" });
            } else return;
        } catch (err) {
            sac.toast(fb.errors.text(err, fb.t("fb.trash.restore-failed", "Couldn't restore that.")), { kind: "error" });
        }
        await this.refresh();
    }

    // Several at once (the keyboard / the marked rows): one toast at the end.
    async restoreMany(items) {
        if (items.length === 1) return this.restore(items[0]);
        if (!items.length) return;
        let done = 0;
        let failed = null;
        for (const item of items) {
            try { if (await this.restoreOne(item)) done++; }
            catch (err) { failed = err; }
        }
        this.sel.clear();
        if (failed) sac.toast(fb.errors.text(failed, fb.t("fb.trash.restore-failed", "Couldn't restore that.")), { kind: "error" });
        else if (done) sac.toast(fb.t("fb.trash.restored-many", "{n} items are back.", { n: done }), { kind: "success" });
        await this.refresh();
    }

    // Delete for good, one question for however many.
    async removeMany(items) {
        if (!items.length) return;
        if (items.length === 1) return this.remove(items[0]);
        const answer = await sac.dialog.confirm({
            title: fb.t("fb.trash.delete-many-title", "Delete {n} items for good?", { n: items.length }),
            message: fb.t("fb.trash.delete-many-body", "They will be gone and can't be restored."),
            buttons: [
                { action: "cancel", label: fb.t("fb.common.cancel", "Cancel") },
                { action: "delete", label: fb.t("fb.trash.delete-forever", "Delete for good"), kind: "destructive", armAfterMs: 2000 },
            ],
        });
        if (answer !== "delete") return;
        let done = 0;
        let failed = null;
        for (const item of items) {
            try {
                if (item.source === "record") await this.recordsApi.remove(item.id);
                else await this.filesApi.purge(item.id);
                done++;
            } catch (err) { failed = err; }
        }
        this.sel.clear();
        if (failed) sac.toast(fb.errors.text(failed, fb.t("fb.trash.delete-failed", "Couldn't delete that.")), { kind: "error" });
        else sac.toast(fb.t("fb.trash.deleted-many", "{n} items deleted.", { n: done }), { kind: "success" });
        await this.refresh();
    }

    async remove(item) {
        const answer = await sac.dialog.confirm({
            title: fb.t("fb.trash.delete-title", "Delete for good?"),
            message: fb.t("fb.trash.delete-body", "“{name}” will be gone and can't be restored.", { name: item.title || "" }),
            buttons: [
                { action: "cancel", label: fb.t("fb.common.cancel", "Cancel") },
                { action: "delete", label: fb.t("fb.trash.delete-forever", "Delete for good"), kind: "destructive" },
            ],
        });
        if (answer !== "delete") return;
        try {
            if (item.source === "record") await this.recordsApi.remove(item.id);
            else await this.filesApi.purge(item.id);
        } catch (err) {
            sac.toast(fb.errors.text(err, fb.t("fb.trash.delete-failed", "Couldn't delete that.")), { kind: "error" });
        }
        await this.refresh();
    }

    async emptyAll() {
        const answer = await sac.dialog.confirm({
            title: fb.t("fb.trash.empty-title", "Empty the trash?"),
            message: fb.t("fb.trash.empty-body", "Everything in it — records and files — is deleted for good."),
            buttons: [
                { action: "cancel", label: fb.t("fb.common.cancel", "Cancel") },
                { action: "empty", label: fb.t("fb.trash.empty-action", "Empty trash"), kind: "destructive", armAfterMs: 1000 },
            ],
        });
        if (answer !== "empty") return;
        try {
            await Promise.all([this.recordsApi.empty(), this.filesApi.emptyTrash()]);
        } catch (err) {
            sac.toast(fb.errors.text(err, fb.t("fb.trash.delete-failed", "Couldn't delete that.")), { kind: "error" });
        }
        await this.refresh();
    }
}

customElements.define("fb-trash-view", FbTrashView);
