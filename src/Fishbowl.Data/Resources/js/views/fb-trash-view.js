/**
 * <fb-trash-view>  (mounted at #/trash, both workspaces)
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
            <div class="card"><div id="trash-list"></div></div>`;
        this._onScope = () => this.refresh();
        window.addEventListener("sac:scope-changed", this._onScope);
        this.refresh();
    }

    disconnectedCallback() {
        window.removeEventListener("sac:scope-changed", this._onScope);
    }

    async refresh() {
        const scope = sac.scope.get();
        const [records, files, spaces] = await Promise.all([
            fb.api.trash.list().catch(() => []),
            fb.api.files.trashList().catch(() => []),
            scope.type === "scoped" ? fb.api.spaces.list().catch(() => []) : Promise.resolve([]),
        ]);
        if (scope.type === "scoped") {
            const role = spaces.find((s) => s.slug === scope.slug)?.role;
            this.writable = role !== "reader";
        } else {
            this.writable = true;
        }
        this.items = [
            ...(records || []).map((r) => ({ ...r, source: "record" })),
            ...(files || []).map((f) => ({
                id: f.id, source: "file", kind: f.kind === "folder" ? "folder" : "file",
                title: f.originalPath, deletedAt: f.deletedAt, deletedBy: null,
            })),
        ].sort((a, b) => String(b.deletedAt).localeCompare(String(a.deletedAt)));
        this.render();
    }

    render() {
        const list = this.querySelector("#trash-list");
        fb.toolbar.set(this.writable && this.items.length ? [{
            id: "fb-trash-empty", icon: "trash", title: fb.t("fb.trash.empty-action", "Empty trash"), onClick: () => this.emptyAll(),
        }] : []);
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
        const icons = { note: "note", todo: "check", event: "calendar", contact: "user", file: "document", folder: "folder" };
        const kinds = {
            note: fb.t("fb.trash.kind-note", "Note"), todo: fb.t("fb.trash.kind-todo", "Todo"),
            event: fb.t("fb.trash.kind-event", "Event"), contact: fb.t("fb.trash.kind-contact", "Contact"),
            file: fb.t("fb.trash.kind-file", "File"), folder: fb.t("fb.trash.kind-folder", "Folder"),
        };
        const row = document.createElement("div");
        row.className = "fb-row fb-trash-row";
        row.dataset.id = item.id;
        row.dataset.kind = item.kind;
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
        if (this.writable) {
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

    async restore(item) {
        try {
            if (item.source === "record") {
                await fb.api.trash.restore(item.id);
            } else {
                try {
                    await fb.api.files.restore(item.id);
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
                    if (answer !== "rename") return;
                    await fb.api.files.restore(item.id, "rename");
                }
            }
            sac.toast(fb.t("fb.trash.restored", "“{name}” is back.", { name: item.title || "" }), { kind: "success" });
        } catch (err) {
            sac.toast(fb.errors.text(err, fb.t("fb.trash.restore-failed", "Couldn't restore that.")), { kind: "error" });
        }
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
            if (item.source === "record") await fb.api.trash.remove(item.id);
            else await fb.api.files.purge(item.id);
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
            await Promise.all([fb.api.trash.empty(), fb.api.files.emptyTrash()]);
        } catch (err) {
            sac.toast(fb.errors.text(err, fb.t("fb.trash.delete-failed", "Couldn't delete that.")), { kind: "error" });
        }
        await this.refresh();
    }
}

customElements.define("fb-trash-view", FbTrashView);
sac.router.register("#/trash", "fb-trash-view", { label: "Trash", icon: "trash", palette: false });
