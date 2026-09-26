/**
 * Users (#/admin/users): the admin's list of accounts. Registered only for
 * admins (fb.accounts.registerAdminRoutes, called by shell.js once /me says
 * so) — for anyone else the route, its nav entry and its palette entry don't
 * exist.
 *
 * Requests waiting for approval come first, each with a quota field and
 * Approve / Reject / Block. Then everyone else: who they are, how they sign
 * in, their state and quota, when they last signed in. Never what they
 * store — the admin runs the instance, not the people on it. Each account
 * has a "…" menu (A2): Storage…, Reset password (local sign-in only),
 * Make / Remove admin, Disable / Enable, Block / Unblock. "Add local user"
 * is the page's action in the nav toolbar.
 *
 * Personal only: in a space the page says so and points back to Personal.
 */
class FbUsersAdminView extends HTMLElement {
    connectedCallback() {
        this.render();
        this._onChanged = () => this.refresh();
        window.addEventListener("fb:messages-changed", this._onChanged);
        this.refresh();
    }

    disconnectedCallback() {
        window.removeEventListener("fb:messages-changed", this._onChanged);
    }

    render() {
        this.classList.add("fb-page");
        this.innerHTML = `
            <style>
                /* Page frame, cards, buttons and chips are the kit's (app.css
                   .fb-page / .fb-row); only this page's own bits. */
                fb-users-admin-view .user-row { align-items: flex-start; }
                fb-users-admin-view .user-row sac-avatar { --avatar-size: 32px; flex-shrink: 0; }
                fb-users-admin-view .user-email { font-weight: 400; color: var(--text-muted); }
                fb-users-admin-view .user-name sac-chip { margin-left: 6px; vertical-align: 1px; }
                fb-users-admin-view .user-actions { flex-wrap: wrap; margin-top: 10px; }

                /* Dialogs this view opens (light DOM, appended to <body>). */
                .fb-temp-pw-note, .fb-add-user-note { margin: 0 0 12px; }
                .fb-temp-pw-hint { margin: 12px 0 0; font-size: 13px; color: var(--text-muted); }
                .fb-temp-pw-row { display: flex; align-items: center; gap: 8px; }
                .fb-temp-pw-row sac-copy-button { --icon-btn-size: 36px; --icon-btn-icon: 18px; }
                .fb-temp-pw {
                    flex: 1;
                    font-family: var(--font-mono);
                    font-size: 15px;
                    letter-spacing: 0.04em;
                    background: var(--field);
                    border: 1px solid var(--border);
                    border-radius: var(--radius-m);
                    padding: 10px 12px;
                    user-select: all;
                }
                .fb-add-optional { color: var(--text-muted); font-weight: 400; }
                .fb-add-quota { margin-top: 12px; }
                .fb-add-error { margin: 10px 0 0; color: var(--danger-text); font-size: 13px; }
            </style>
            <header><div>
                <h1>Users</h1>
                <p class="subtitle">
                    Who can use this Fishbowl. You see who people are and how much room they have —
                    never what they keep here.
                </p>
            </div></header>
            <div id="users-body"></div>
        `;
    }

    async refresh() {
        const mount = this.querySelector("#users-body");
        if (!mount) return;

        if (sac.scope.get().type === "scoped") {
            fb.toolbar.set([]);
            mount.innerHTML = `
                <div class="card">
                    <p>Users are managed for the whole Fishbowl, from your personal workspace.</p>
                    <div class="toolbar"><button type="button" class="btn" id="to-personal">Open in Personal</button></div>
                </div>`;
            mount.querySelector("#to-personal").addEventListener("click", () => sac.router.navigate("#/admin/users"));
            return;
        }

        let data;
        try {
            data = await fb.api.admin.users();
        } catch (err) {
            console.warn("[fb-users-admin-view] load failed:", err?.status);
            mount.innerHTML = `<div class="card"><p class="muted">The user list can't be loaded right now.</p></div>`;
            return;
        }
        if (!this.isConnected) return; // navigated away while loading: the toolbar is another view's now
        this._defaultQuota = data?.defaultQuotaBytes ?? null;
        // The page's action sits in the nav toolbar, as every view's does.
        fb.toolbar.set([{
            icon: "plus",
            title: "Add local user",
            onClick: async () => { if (await fb.accounts.addLocalUser(this._defaultQuota)) this.refresh(); },
        }]);
        const users = data?.users || [];
        const pending = users.filter(u => u.state === "pending");
        const others = users.filter(u => u.state !== "pending");
        const policy = {
            approval: "New accounts wait for your approval.",
            open: "New accounts can use this Fishbowl right away.",
            closed: "New accounts can't sign up.",
        }[data?.signUp] || "";

        const parts = [];
        if (pending.length) {
            const { card, panel } = this._panel(`Waiting for approval · ${pending.length}`);
            card.dataset.section = "pending";
            for (const u of pending) panel.appendChild(this._pendingRow(u, data.defaultQuotaBytes));
            parts.push(card);
        }
        const { card: allCard, panel: all } = this._panel(`Accounts · ${others.length}`);
        allCard.dataset.section = "accounts";
        if (policy) {
            const p = document.createElement("p");
            p.className = "muted";
            p.textContent = policy;
            all.appendChild(p);
        }
        for (const u of others) all.appendChild(this._userRow(u));
        parts.push(allCard);
        mount.replaceChildren(...parts);
    }

    // A kit card headed by a <sac-section>; rows go into the section.
    _panel(title) {
        const card = document.createElement("div");
        card.className = "card";
        const panel = document.createElement("sac-section");
        panel.setAttribute("title", title);
        card.appendChild(panel);
        return { card, panel };
    }

    _head(u) {
        const row = document.createElement("div");
        row.className = "fb-row user-row";
        row.dataset.userId = u.id;
        row.dataset.state = u.state;
        row.innerHTML = `
            <sac-avatar></sac-avatar>
            <div class="fb-row-info user-info">
                <p class="fb-row-name user-name"><span class="n"></span> <span class="user-email"></span></p>
                <div class="fb-row-meta user-meta"></div>
            </div>`;
        const avatar = row.querySelector("sac-avatar");
        avatar.setAttribute("name", u.name || u.email || "?");
        row.querySelector(".n").textContent = u.name || u.email || "Unnamed";
        if (u.name && u.email) row.querySelector(".user-email").textContent = u.email;
        return row;
    }

    _meta(row, items) {
        const meta = row.querySelector(".user-meta");
        for (const text of items.filter(Boolean)) {
            const span = document.createElement("span");
            span.textContent = text;
            meta.appendChild(span);
        }
    }

    _tag(row, text, warn) {
        const tag = document.createElement("sac-chip");
        tag.className = "tag";
        tag.setAttribute("label", text);
        if (warn) tag.setAttribute("color", "red");
        row.querySelector(".user-name").appendChild(tag);
    }

    _providers(u) {
        const names = (u.providers || []).map(p => p === "google" ? "Google" : p === "local" ? "Password" : p);
        return names.length ? `Signs in with ${names.join(" & ")}` : null;
    }

    _pendingRow(u, defaultQuota) {
        const row = this._head(u);
        this._meta(row, [`Asked ${fb.format.dateTime(u.createdAt)}`, this._providers(u)]);
        const actions = document.createElement("div");
        actions.className = "toolbar user-actions";
        const quota = fb.accounts.quotaField(defaultQuota);
        const approve = this._button("Approve", "btn primary");
        const reject = this._button("Reject", "btn");
        const block = this._button("Block", "btn danger");
        approve.addEventListener("click", () => fb.accounts.approve(u, fb.accounts.readQuota(quota)));
        reject.addEventListener("click", () => fb.accounts.reject(u));
        block.addEventListener("click", () => fb.accounts.block(u));
        actions.append(quota, approve, reject, block);
        row.querySelector(".user-info").appendChild(actions);
        return row;
    }

    _userRow(u) {
        const row = this._head(u);
        if (u.isAdmin) this._tag(row, "Admin");
        if (u.self) this._tag(row, "You");
        if (u.state === "blocked") this._tag(row, "Blocked", true);
        if (u.state === "disabled") this._tag(row, "Disabled", true);
        this._meta(row, [
            this._providers(u),
            `Storage ${fb.accounts.formatBytes(u.quotaBytes)}`,
            u.lastSignInAt ? `Last sign-in ${fb.format.dateTime(u.lastSignInAt)}` : "Never signed in since the upgrade",
        ]);
        row.appendChild(this._menu(u));
        return row;
    }

    /**
     * The account's "…" menu. Only what applies to this account is in it —
     * the menu is rebuilt with each render, so it simply holds the items
     * that fit the account's state.
     */
    _menu(u) {
        const menu = document.createElement("sac-menu");
        const trigger = document.createElement("button");
        trigger.slot = "trigger";
        trigger.type = "button";
        trigger.className = "icon-btn";
        trigger.title = `Manage ${u.name || u.email || "account"}`;
        trigger.setAttribute("aria-label", trigger.title);
        const more = document.createElement("sac-icon");
        more.setAttribute("name", "more");
        trigger.appendChild(more);
        menu.appendChild(trigger);

        const item = (action, label, icon, danger) => {
            const b = document.createElement("button");
            b.dataset.action = action;
            if (danger) b.dataset.danger = "";
            const i = document.createElement("sac-icon");
            i.setAttribute("name", icon);
            b.append(i, document.createTextNode(" " + label));
            menu.appendChild(b);
        };
        const active = u.state === "active";
        item("quota", "Storage…", "backup");
        if ((u.providers || []).includes("local") && u.state !== "blocked") item("reset", "Reset password", "key");
        if (active && !u.isAdmin) item("admin-on", "Make admin", "star");
        if (u.isAdmin) item("admin-off", "Remove admin", "star");
        if (!u.self) {
            menu.appendChild(document.createElement("hr"));
            if (active) item("disable", "Disable", "lock", true);
            if (u.state === "disabled") item("enable", "Enable", "unlock");
            if (u.state === "blocked") item("unblock", "Unblock", "unlock");
            else item("block", "Block", "close", true);
        }

        const acts = {
            quota: () => fb.accounts.setQuota(u, this._defaultQuota),
            reset: () => fb.accounts.resetPassword(u),
            "admin-on": () => fb.accounts.setAdmin(u, true),
            "admin-off": () => fb.accounts.setAdmin(u, false),
            disable: () => fb.accounts.setDisabled(u, true),
            enable: () => fb.accounts.setDisabled(u, false),
            block: () => fb.accounts.block(u),
            unblock: () => fb.accounts.unblock(u),
        };
        menu.addEventListener("sac:select", async (e) => {
            const run = acts[e.detail.action];
            if (run && await run()) this.refresh();
        });
        return menu;
    }

    _button(label, cls) {
        const b = document.createElement("button");
        b.type = "button";
        b.className = cls;
        b.textContent = label;
        return b;
    }
}

customElements.define("fb-users-admin-view", FbUsersAdminView);
