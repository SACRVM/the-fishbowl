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
 * sits in the header.
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
        this.innerHTML = `
            <style>
                fb-users-admin-view { display: block; padding: clamp(1.25rem, 5vw, 40px) clamp(1rem, 5vw, 48px); max-width: 860px; }
                fb-users-admin-view header { margin-bottom: 24px; display: flex; align-items: flex-start; gap: 16px; }
                fb-users-admin-view header .head-text { flex: 1; min-width: 0; }
                fb-users-admin-view header .btn { width: auto; flex-shrink: 0; }
                fb-users-admin-view h1 {
                    font-family: 'Outfit', 'Inter', sans-serif;
                    font-size: 28px;
                    font-weight: 700;
                    margin: 0 0 6px;
                    color: var(--text);
                }
                fb-users-admin-view .subtitle { color: var(--text-muted); font-size: 14px; margin: 0; }
                fb-users-admin-view .panel {
                    padding: 18px 20px 6px;
                    background: var(--panel);
                    border: 1px solid var(--border);
                    border-radius: var(--radius-l);
                    margin-bottom: 20px;
                }
                fb-users-admin-view .panel h2 {
                    font-size: 12px;
                    font-weight: 600;
                    text-transform: uppercase;
                    letter-spacing: 0.06em;
                    color: var(--text-muted);
                    margin: 0 0 4px;
                }
                fb-users-admin-view .panel p { margin: 0 0 12px; color: var(--text); font-size: 14px; }
                fb-users-admin-view .panel p.muted { color: var(--text-muted); font-size: 13px; }
                fb-users-admin-view .user-row {
                    display: flex;
                    align-items: flex-start;
                    gap: 14px;
                    padding: 14px 0;
                }
                fb-users-admin-view .user-row + .user-row { border-top: 1px solid var(--border); }
                fb-users-admin-view .user-row sac-avatar { --avatar-size: 32px; flex-shrink: 0; }
                fb-users-admin-view .user-info { flex: 1; min-width: 0; }
                fb-users-admin-view .user-name { font-size: 14px; font-weight: 600; color: var(--text); margin: 0 0 2px; overflow-wrap: anywhere; }
                fb-users-admin-view .user-email { font-weight: 400; color: var(--text-muted); }
                fb-users-admin-view .user-meta {
                    display: flex;
                    flex-wrap: wrap;
                    column-gap: 20px;
                    font-size: 12px;
                    color: var(--text-muted);
                }
                fb-users-admin-view .tag {
                    display: inline-block;
                    font-size: 11px;
                    font-weight: 600;
                    padding: 1px 6px;
                    margin-left: 6px;
                    border-radius: var(--radius-m);
                    background: var(--accent-tint);
                    color: var(--text);
                    vertical-align: 1px;
                }
                fb-users-admin-view .tag.warn { background: var(--hover); color: var(--danger); }
                fb-users-admin-view .user-actions {
                    display: flex;
                    align-items: center;
                    flex-wrap: wrap;
                    gap: 8px;
                    margin-top: 10px;
                }
                fb-users-admin-view .user-actions .btn { width: auto; }
                fb-users-admin-view .fb-quota-field { display: inline-flex; align-items: center; gap: 6px; font-size: 13px; color: var(--text-muted); }
                fb-users-admin-view .fb-quota-field input { width: 6em; }
                fb-users-admin-view .user-row sac-menu { flex-shrink: 0; }

                /* Dialogs this view opens (light DOM, appended to <body>). */
                .fb-temp-pw-note, .fb-add-user-note { margin: 0 0 12px; font-size: 14px; }
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
                    color: var(--text);
                    user-select: all;
                }
                .fb-add-optional { color: var(--text-muted); font-weight: 400; }
                .fb-add-quota { margin-top: 12px; }
                .fb-add-error { margin: 10px 0 0; color: var(--danger); font-size: 13px; }
            </style>
            <header>
                <div class="head-text">
                    <h1>Users</h1>
                    <p class="subtitle">
                        Who can use this Fishbowl. You see who people are and how much room they have —
                        never what they keep here.
                    </p>
                </div>
                <button type="button" class="btn" id="add-user" hidden>Add local user</button>
            </header>
            <div id="users-body"></div>
        `;
    }

    async refresh() {
        const mount = this.querySelector("#users-body");
        if (!mount) return;

        const add = this.querySelector("#add-user");
        if (sac.scope.get().type === "scoped") {
            add.hidden = true;
            mount.innerHTML = `
                <div class="panel">
                    <p>Users are managed for the whole Fishbowl, from your personal workspace.</p>
                    <button type="button" class="btn" id="to-personal">Open in Personal</button>
                </div>`;
            mount.querySelector("#to-personal").addEventListener("click", () => sac.router.navigate("#/admin/users"));
            return;
        }

        let data;
        try {
            data = await fb.api.admin.users();
        } catch (err) {
            console.warn("[fb-users-admin-view] load failed:", err?.status);
            mount.innerHTML = `<div class="panel"><p class="muted">The user list can't be loaded right now.</p></div>`;
            return;
        }
        this._defaultQuota = data?.defaultQuotaBytes ?? null;
        add.hidden = false;
        add.onclick = async () => { if (await fb.accounts.addLocalUser(this._defaultQuota)) this.refresh(); };
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
            const panel = this._panel(`Waiting for approval · ${pending.length}`);
            panel.dataset.section = "pending";
            for (const u of pending) panel.appendChild(this._pendingRow(u, data.defaultQuotaBytes));
            parts.push(panel);
        }
        const all = this._panel(`Accounts · ${others.length}`);
        all.dataset.section = "accounts";
        if (policy) {
            const p = document.createElement("p");
            p.className = "muted";
            p.textContent = policy;
            all.appendChild(p);
        }
        for (const u of others) all.appendChild(this._userRow(u));
        parts.push(all);
        mount.replaceChildren(...parts);
    }

    _panel(title) {
        const panel = document.createElement("div");
        panel.className = "panel";
        const h = document.createElement("h2");
        h.textContent = title;
        panel.appendChild(h);
        return panel;
    }

    _head(u) {
        const row = document.createElement("div");
        row.className = "user-row";
        row.dataset.userId = u.id;
        row.dataset.state = u.state;
        row.innerHTML = `
            <sac-avatar></sac-avatar>
            <div class="user-info">
                <p class="user-name"><span class="n"></span> <span class="user-email"></span></p>
                <div class="user-meta"></div>
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
        const tag = document.createElement("span");
        tag.className = "tag" + (warn ? " warn" : "");
        tag.textContent = text;
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
        actions.className = "user-actions";
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
     * items are added, never hidden (kit menus ignore `hidden`).
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
