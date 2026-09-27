/**
 * <fb-apps-settings-view>  (mounted at #/apps — Settings → Apps)
 *
 * The apps installed on the active workspace's desktop, in one list: where
 * each comes from, its version, how it runs (a sandboxed / trusted chip) and
 * what it was given — with Update (only when fb.desktopApps found a newer
 * version), Permissions (sandboxed apps) and Remove. The same flows as the
 * desktop's tile menu (js/lib/desktop-apps.js), so both stay one thing.
 * "Install an app" sits in the toolbar for whoever may install here; a card
 * says what the admin's policy allows. Members of a space see its apps, no
 * write buttons.
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
        fb.toolbar?.clear?.();
    }

    render() {
        this.classList.add("fb-page");
        const space = sac.scope.get().type === "scoped";
        this.innerHTML = `
            <header><div>
                <h1>Apps</h1>
                <p class="subtitle">Apps on ${space ? "this space's" : "your"} desktop. A sandboxed app gets only what you allowed it.</p>
            </div></header>
            <div class="card">
                <sac-section title="Installed"></sac-section>
                <div id="fb-apps-list"></div>
            </div>
            <div class="card">
                <sac-section title="Who may install"></sac-section>
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
        const st = fb.desktopApps.state;
        fb.toolbar.set(st.canInstall ? [{ icon: "plus", title: "Install an app", onClick: () => fb.desktopApps.install() }] : []);
    }

    renderList() {
        const list = this.querySelector("#fb-apps-list");
        if (!list) return;
        const st = fb.desktopApps.state;
        if (!st.apps.length) {
            list.innerHTML = `
                <div class="empty-state">
                    <sac-icon name="grid"></sac-icon>
                    <h3>No apps yet</h3>
                    <p>${st.canInstall ? "Install one from a repository URL — it runs in its own sandbox." : "Nothing is installed on this desktop."}</p>
                </div>`;
            if (st.canInstall) {
                const btn = document.createElement("button");
                btn.type = "button";
                btn.className = "btn primary";
                btn.innerHTML = `<sac-icon name="plus"></sac-icon> Install an app`;
                btn.addEventListener("click", () => fb.desktopApps.install());
                list.querySelector(".empty-state").appendChild(btn);
            }
            return;
        }
        list.replaceChildren(...st.apps.map((app) => this.row(app, st.canArrange)));
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
        where.textContent = app.mode === "trusted" ? `${host} · follows latest` : `${host}${app.version ? ` · v${app.version}` : ""}`;
        const chip = document.createElement("sac-chip");
        chip.setAttribute("label", app.mode);
        const grants = document.createElement("span");
        grants.textContent = this.grantsText(app);
        meta.append(where, chip, grants);
        info.append(name, meta);

        row.append(icon, info);
        if (canWrite) {
            const tools = document.createElement("div");
            tools.className = "toolbar";
            const fresh = fb.desktopApps.updateFor(app.id);
            if (fresh) {
                const up = document.createElement("button");
                up.type = "button";
                up.className = "btn primary";
                up.dataset.action = "update";
                up.textContent = `Update to v${fresh.version || "?"}`;
                up.addEventListener("click", () => fb.desktopApps.update(app.id));
                tools.appendChild(up);
            }
            if (app.mode === "sandboxed") {
                const perms = document.createElement("button");
                perms.type = "button";
                perms.className = "btn";
                perms.dataset.action = "perms";
                perms.textContent = "Permissions";
                perms.addEventListener("click", () => fb.desktopApps.permissions(app.id));
                tools.appendChild(perms);
            }
            const del = document.createElement("button");
            del.type = "button";
            del.className = "icon-btn danger";
            del.dataset.action = "remove";
            del.title = `Remove ${app.manifest?.name || app.id}`;
            del.setAttribute("aria-label", del.title);
            del.innerHTML = `<sac-icon name="trash"></sac-icon>`;
            del.addEventListener("click", () => fb.desktopApps.remove(app.id));
            tools.appendChild(del);
            row.appendChild(tools);
        }
        return row;
    }

    grantsText(app) {
        if (app.mode === "trusted") return "Full access, personal only";
        const parts = [];
        if ((app.granted || []).includes("files")) parts.push("Files");
        const id = fb.desktopApps.identityOf(app);
        if (id === "full") parts.push("your name");
        else if (id === "pseudonymous") parts.push("anonymous id");
        const n = Array.isArray(app.manifest?.connect) ? app.manifest.connect.length : 0;
        if (n) parts.push(n === 1 ? "1 server" : `${n} servers`);
        return parts.length ? parts.join(" · ") : "Nothing beyond its own frame";
    }

    renderPolicy() {
        const p = this.querySelector("#fb-apps-policy");
        if (!p) return;
        const st = fb.desktopApps.state;
        const space = st.ws !== "personal";
        if (space) {
            p.textContent = st.canInstall
                ? "You own this space: you install its apps, sandboxed only. Every member sees and opens them."
                : st.canArrange
                    ? "Your admin turned installing apps off."
                    : "The space's owner installs its apps; you see and open them.";
            return;
        }
        if (!st.canInstall) {
            p.textContent = "Your admin keeps installing apps to admins, or turned it off.";
            return;
        }
        p.textContent = st.canTrust
            ? "You can install apps sandboxed or, for your own apps, trusted."
            : "You can install sandboxed apps. Trusted apps are kept to admins.";
    }
}

customElements.define("fb-apps-settings-view", FbAppsSettingsView);
sac.router.register("#/apps", "fb-apps-settings-view", { label: "Apps", icon: "grid", palette: false });
