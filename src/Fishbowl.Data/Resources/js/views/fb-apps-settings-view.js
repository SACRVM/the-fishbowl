/**
 * <fb-apps-settings-view>  (a window app, fb.windowApps "apps")
 *
 * The apps installed on the active workspace's desktop, in one list: where
 * each comes from, its version, how it runs (a sandboxed / space-app chip) and
 * what it was given — with Update (only when fb.desktopApps found a newer
 * version), Permissions (sandboxed apps) and Remove. The same flows as the
 * desktop's tile menu (js/lib/desktop-apps.js), so both stay one thing.
 * "Install an app" sits in the toolbar for whoever may install here; a card
 * says what the admin's policy allows. Members of a space see its apps, no
 * write buttons. In a space its Designers also see the app error store
 * (what broke in the space's own apps) with Clear.
 *
 * Light-DOM so app.css tokens apply; page frame, cards and rows are the
 * kit's (app.css .fb-page / .fb-row).
 */
class FbAppsSettingsView extends HTMLElement {
    async connectedCallback() {
        this.render();
        this._refresh = () => this.refresh();
        this._repaint = () => this.renderList();
        window.addEventListener("fb:apps-changed", this._refresh);
        window.addEventListener("fb:app-updates", this._repaint);
        window.addEventListener("sac:scope-changed", this._refresh);
        await this.refresh();
    }

    disconnectedCallback() {
        window.removeEventListener("fb:apps-changed", this._refresh);
        window.removeEventListener("fb:app-updates", this._repaint);
        window.removeEventListener("sac:scope-changed", this._refresh);
    }

    render() {
        this.classList.add("fb-page");
        const space = sac.scope.get().type === "scoped";
        this.innerHTML = `
            <header><div>
                <h1>${fb.t("fb.apps.page-title", "Apps")}</h1>
                <p class="subtitle">${space
                    ? fb.t("fb.apps.page-subtitle-space", "Apps on this space's desktop. A sandboxed app gets only what you allowed it.")
                    : fb.t("fb.apps.page-subtitle", "Apps on your desktop. A sandboxed app gets only what you allowed it.")}</p>
            </div></header>
            <div class="fb-block">
                <sac-section title="${fb.t("fb.apps.page-installed", "Installed")}"></sac-section>
                <div id="fb-apps-list"></div>
            </div>
            <div class="fb-block" id="fb-apps-errors-card" hidden>
                <sac-section title="${fb.t("fb.apps.errors", "Errors")}"></sac-section>
                <div id="fb-apps-errors"></div>
            </div>
            <div class="fb-block">
                <sac-section title="${fb.t("fb.apps.page-who", "Who may install")}"></sac-section>
                <p class="muted" id="fb-apps-policy"></p>
            </div>`;
    }

    async refresh() {
        try { await fb.desktop.load(); }        // syncs fb.desktopApps
        catch (err) { console.warn("[fb-apps-settings-view] load failed:", err?.message || err); }
        if (!this.isConnected) return;
        this.render();
        this.renderList();
        this.renderPolicy();
        this.renderErrors();
        const st = fb.desktopApps.state;
        fb.windowApps.toolbar(this, st.canInstall ? [{ icon: "plus", title: fb.t("fb.apps.page-install", "Install an app"), onClick: () => fb.desktopApps.install() }] : []);
    }

    renderList() {
        const list = this.querySelector("#fb-apps-list");
        if (!list) return;
        const st = fb.desktopApps.state;
        if (!st.apps.length) {
            list.innerHTML = `
                <div class="empty-state">
                    <sac-icon name="grid"></sac-icon>
                    <h3>${fb.t("fb.apps.page-empty", "No apps yet")}</h3>
                    <p>${st.canInstall ? fb.t("fb.apps.page-empty-install", "Install one from a repository URL — it runs in its own sandbox.") : fb.t("fb.apps.page-empty-none", "Nothing is installed on this desktop.")}</p>
                </div>`;
            if (st.canInstall) {
                const btn = document.createElement("button");
                btn.type = "button";
                btn.className = "btn primary";
                btn.innerHTML = `<sac-icon name="plus"></sac-icon> ${fb.t("fb.apps.page-install", "Install an app")}`;
                btn.addEventListener("click", () => fb.desktopApps.install());
                list.querySelector(".empty-state").appendChild(btn);
            }
            return;
        }
        list.replaceChildren(...st.apps.map((app) => this.row(app, st.canArrange)));
    }

    // A space's app errors — only a Designer gets them (the server says 403
    // to everyone else, and the card stays hidden).
    async renderErrors() {
        const scope = sac.scope.get();
        const card = this.querySelector("#fb-apps-errors-card");
        if (!card || scope.type !== "scoped") return;
        let list;
        try { list = (await fb.api.appErrors(scope.slug).list({ limit: 100 })).errors || []; }
        catch { return; }
        if (!this.isConnected) return;
        card.hidden = false;
        const box = this.querySelector("#fb-apps-errors");
        if (!list.length) {
            box.innerHTML = `<p class="muted">${fb.t("fb.apps.errors-none", "No errors.")}</p>`;
            return;
        }
        const tools = document.createElement("div");
        tools.className = "toolbar";
        const clear = document.createElement("button");
        clear.type = "button";
        clear.className = "btn";
        clear.dataset.action = "clear-errors";
        clear.textContent = fb.t("fb.apps.errors-clear", "Clear");
        clear.addEventListener("click", async () => {
            try { await fb.api.appErrors(scope.slug).clear(); }
            catch (err) { sac.toast?.(fb.errors.text(err, fb.t("fb.apps.errors-clear-failed", "Couldn't clear the errors.")), { kind: "error" }); }
            this.renderErrors();
        });
        tools.appendChild(clear);
        const kinds = {
            manifest: fb.t("fb.apps.error-manifest", "app.json"),
            load: fb.t("fb.apps.error-load", "start"),
            runtime: fb.t("fb.apps.error-runtime", "running"),
            call: fb.t("fb.apps.error-call", "refused call"),
            reported: fb.t("fb.apps.error-reported", "reported"),
            trigger: fb.t("fb.apps.error-trigger", "trigger"),
        };
        box.replaceChildren(tools, ...list.map((e) => {
            const row = document.createElement("div");
            row.className = "fb-row";
            row.dataset.error = e.id;
            const icon = document.createElement("sac-icon");
            icon.setAttribute("name", "warn");
            const info = document.createElement("div");
            info.className = "fb-row-info";
            const msg = document.createElement("p");
            msg.className = "fb-row-name";
            msg.textContent = e.message;
            if (e.detail) msg.title = e.detail;
            const meta = document.createElement("div");
            meta.className = "fb-row-meta";
            meta.textContent = `.apps/${e.app} · ${kinds[e.kind] || e.kind} · ${fb.format.dateTime(new Date(e.at))}`;
            info.append(msg, meta);
            row.append(icon, info);
            return row;
        }));
    }

    row(app, canWrite) {
        const row = document.createElement("div");
        row.className = "fb-row";
        row.dataset.app = app.id;
        const icon = document.createElement("sac-icon");
        icon.setAttribute("name", app.manifest?.icon || "cube");

        const info = document.createElement("div");
        info.className = "fb-row-info";
        const name = document.createElement("p");
        name.className = "fb-row-name";
        name.textContent = app.manifest?.name || app.id;
        const meta = document.createElement("div");
        meta.className = "fb-row-meta";
        const where = document.createElement("span");
        let host = app.origin;
        try { host = new URL(app.origin).host; } catch { /* keep */ }
        where.textContent = app.mode === "space" ? `${app.folder}${app.version ? ` · v${app.version}` : ""}`
            : `${host}${app.version ? ` · v${app.version}` : ""}`;
        const chip = document.createElement("sac-chip");
        chip.setAttribute("label", app.mode === "space" ? fb.t("fb.apps.page-mode-space", "space app")
            : fb.t("fb.apps.page-mode-sandboxed", "sandboxed"));
        const grants = document.createElement("span");
        grants.textContent = this.grantsText(app);
        meta.append(where, chip, grants);
        info.append(name, meta);

        row.append(icon, info);
        if (canWrite && app.mode !== "space") {
            const tools = document.createElement("div");
            tools.className = "toolbar";
            const fresh = fb.desktopApps.updateFor(app.id);
            if (fresh) {
                const up = document.createElement("button");
                up.type = "button";
                up.className = "btn primary";
                up.dataset.action = "update";
                up.textContent = fb.t("fb.apps.page-update-to", "Update to v{version}", { version: fresh.version || "?" });
                up.addEventListener("click", () => fb.desktopApps.update(app.id));
                tools.appendChild(up);
            }
            if (app.mode === "sandboxed") {
                const perms = document.createElement("button");
                perms.type = "button";
                perms.className = "btn";
                perms.dataset.action = "perms";
                perms.textContent = fb.t("fb.apps.page-permissions", "Permissions");
                perms.addEventListener("click", () => fb.desktopApps.permissions(app.id));
                tools.appendChild(perms);
            }
            const del = document.createElement("button");
            del.type = "button";
            del.className = "icon-btn danger";
            del.dataset.action = "remove";
            del.title = fb.t("fb.apps.page-remove", "Remove {name}", { name: app.manifest?.name || app.id });
            del.setAttribute("aria-label", del.title);
            del.innerHTML = `<sac-icon name="trash"></sac-icon>`;
            del.addEventListener("click", () => fb.desktopApps.remove(app.id));
            tools.appendChild(del);
            row.appendChild(tools);
        }
        return row;
    }

    grantsText(app) {
        if (app.mode === "space") return "";
        const parts = [];
        if ((app.granted || []).includes("files")) parts.push(fb.t("fb.apps.page-grant-files", "Files"));
        const id = fb.desktopApps.identityOf(app);
        if (id === "full") parts.push(fb.t("fb.apps.page-grant-name", "your name"));
        else if (id === "pseudonymous") parts.push(fb.t("fb.apps.page-grant-anon", "anonymous id"));
        const n = Array.isArray(app.manifest?.connect) ? app.manifest.connect.length : 0;
        if (n) parts.push(n === 1 ? fb.t("fb.apps.page-server-1", "1 server") : fb.t("fb.apps.page-server-n", "{n} servers", { n }));
        return parts.length ? parts.join(" · ") : fb.t("fb.apps.page-nothing", "Nothing beyond its own frame");
    }

    renderPolicy() {
        const p = this.querySelector("#fb-apps-policy");
        if (!p) return;
        const st = fb.desktopApps.state;
        const space = st.ws !== "personal";
        if (space) {
            p.textContent = st.canInstall
                ? fb.t("fb.apps.page-policy-owner", "You own this space: you install its apps, sandboxed only. Every member sees and opens them.")
                : st.canArrange
                    ? fb.t("fb.apps.page-policy-off", "Installing apps is turned off on this Fishbowl (System → Apps).")
                    : fb.t("fb.apps.page-policy-member", "The space's owner installs its apps; you see and open them.");
            return;
        }
        if (!st.canInstall) {
            p.textContent = st.installPolicy === "off"
                ? fb.t("fb.apps.page-policy-off", "Installing apps is turned off on this Fishbowl (System → Apps).")
                : fb.t("fb.apps.page-policy-admins", "Only Global Admins may install apps on this Fishbowl.");
            return;
        }
        p.textContent = fb.t("fb.apps.page-policy-sandboxed", "You can install apps. They run sandboxed: their own frame, pinned code, only what you allow.");
    }
}

customElements.define("fb-apps-settings-view", FbAppsSettingsView);
