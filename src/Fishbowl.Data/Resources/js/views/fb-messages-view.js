/**
 * Messages (a window app, fb.windowApps "messages"): the system inbox.
 *
 * What the system tells you — not chat. Today: "someone wants to join"
 * (admins, with Approve / Reject / Block right in the row) and "your
 * account was approved". A message another admin already handled shows
 * as done. Rows hold ids only; the server adds who a request is about.
 * A space's own apps write here too (app.message: "Space · App" and the
 * app's text), each with a menu to mute that app or the whole space; what
 * is muted comes back from the toolbar's "Muted (n)".
 * Messages are personal: the same list in every workspace.
 */
class FbMessagesView extends HTMLElement {
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
                /* Page frame, card, rows and buttons are the kit's (app.css
                   .fb-page / .fb-row); only this page's own bits. */
                fb-messages-view .msg-row { align-items: flex-start; }
                fb-messages-view .msg-row > sac-icon { color: var(--text-muted); margin-top: 1px; }
                fb-messages-view .msg-row.unread > sac-icon { color: var(--accent); }
                fb-messages-view .msg-row.unread .msg-text { font-weight: 600; }
                fb-messages-view .msg-text { font-weight: 400; }
                fb-messages-view .msg-actions { flex-wrap: wrap; margin-top: 10px; }
            </style>
            <header><div>
                <h1>${fb.t("fb.messages.title", "Messages")}</h1>
                <p class="subtitle">${fb.t("fb.messages.subtitle", "What this Fishbowl has to tell you.")}</p>
            </div></header>
            <div class="fb-block" id="messages-body"><p class="muted">${fb.t("fb.common.loading", "Loading…")}</p></div>
        `;
    }

    async refresh() {
        const mount = this.querySelector("#messages-body");
        if (!mount) return;
        let data;
        try {
            data = await fb.api.messages.list();
        } catch (err) {
            console.warn("[fb-messages-view] load failed:", err?.status);
            mount.innerHTML = `<p class="muted">${fb.t("fb.messages.load-failed", "Messages can't be loaded right now.")}</p>`;
            return;
        }
        const items = data?.items || [];
        this._paintToolbar();
        if (!items.length) {
            mount.innerHTML = `
                <div class="empty-state">
                    <sac-icon name="mail"></sac-icon>
                    <h3>${fb.t("fb.messages.empty", "No messages")}</h3>
                </div>`;
            return;
        }

        // Open join requests need the instance's default quota for the field.
        let defaultQuota = null;
        if (items.some(m => m.kind === "user.pending" && !m.doneAt)) {
            try { defaultQuota = (await fb.api.admin.users())?.defaultQuotaBytes ?? null; }
            catch { /* not an admin any more: the actions will say so */ }
        }

        mount.replaceChildren(...items.map(m => this._row(m, defaultQuota)));
    }

    // An app message's "…": mute this app, or the whole space.
    _muteMenu(spaceId, app, appName, spaceName) {
        const menu = document.createElement("sac-menu");
        menu.className = "msg-mute";
        menu.innerHTML = `<button slot="trigger" type="button" class="icon-btn" aria-label="${fb.t("fb.messages.more", "More")}" title="${fb.t("fb.messages.more", "More")}"><sac-icon name="more"></sac-icon></button>`;
        const item = (action, label) => {
            const b = document.createElement("button");
            b.type = "button";
            b.dataset.action = action;
            b.textContent = label;
            return b;
        };
        menu.append(
            item("mute-app", fb.t("fb.messages.mute-app", "Mute {app}", { app: appName || app })),
            item("mute-space", fb.t("fb.messages.mute-space", "Mute everything from {space}", { space: spaceName })));
        menu.addEventListener("sac:select", async (ev) => {
            const whole = ev.detail.action === "mute-space";
            try {
                await fb.api.messages.mute(spaceId, whole ? null : app, true);
                sac.toast?.(fb.t("fb.messages.muted-toast", "Muted. “Muted” above the list undoes it."));
            } catch (err) {
                sac.toast?.(fb.errors.text(err, fb.t("fb.messages.mute-failed", "Couldn't mute it.")), { kind: "error" });
            }
        });
        return menu;
    }

    // "Muted…" in the toolbar only while something is muted.
    async _paintToolbar() {
        let muted = [];
        try { muted = (await fb.api.messages.muted()).muted || []; } catch { /* nothing to offer */ }
        if (!this.isConnected) return;
        fb.windowApps.toolbar(this, muted.length
            ? [{ id: "fb-messages-muted", icon: "eye-off", title: fb.t("fb.messages.muted", "Muted ({n})", { n: muted.length }), onClick: () => this._openMuted(muted) }]
            : []);
    }

    _openMuted(muted) {
        const dlg = document.createElement("sac-dialog");
        dlg.setAttribute("title", fb.t("fb.messages.muted-title", "Muted"));
        dlg.id = "fb-muted-dialog";
        const list = document.createElement("div");
        for (const m of muted) {
            const row = document.createElement("div");
            row.className = "fb-row";
            const info = document.createElement("div");
            info.className = "fb-row-info";
            const name = document.createElement("p");
            name.className = "fb-row-name";
            const space = m.spaceName || fb.t("fb.messages.a-space", "A space");
            name.textContent = m.app ? `${space} · ${m.app}` : fb.t("fb.messages.muted-space", "{space} — all apps", { space });
            info.appendChild(name);
            const un = document.createElement("button");
            un.type = "button";
            un.className = "btn";
            un.dataset.action = "unmute";
            un.textContent = fb.t("fb.messages.unmute", "Unmute");
            un.addEventListener("click", async () => {
                try { await fb.api.messages.mute(m.space, m.app, false); row.remove(); }
                catch (err) { sac.toast?.(fb.errors.text(err, fb.t("fb.messages.mute-failed", "Couldn't mute it.")), { kind: "error" }); }
                if (!list.children.length) dlg.close?.();
            });
            row.append(info, un);
            list.appendChild(row);
        }
        dlg.appendChild(list);
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

    _row(m, defaultQuota) {
        const row = document.createElement("div");
        row.className = "fb-row msg-row" + (m.readAt ? "" : " unread");
        row.dataset.kind = m.kind;
        row.dataset.id = m.id;
        row.innerHTML = `
            <sac-icon></sac-icon>
            <div class="fb-row-info msg-body">
                <p class="fb-row-name msg-text"></p>
                <div class="fb-row-meta msg-meta"></div>
            </div>`;
        const icon = row.querySelector("sac-icon");
        const text = row.querySelector(".msg-text");
        const meta = row.querySelector(".msg-meta");
        const body = row.querySelector(".msg-body");
        const when = fb.format.dateTime(m.createdAt);

        if (m.kind === "user.pending") {
            const u = m.subject || {};
            const label = u.name && u.email ? `${u.name} (${u.email})` : (u.name || u.email || fb.t("fb.messages.someone", "Someone"));
            icon.setAttribute("name", "user");
            text.textContent = fb.t("fb.messages.wants-to-join", "{who} wants to join this Fishbowl.", { who: label });
            if (m.doneAt) {
                const outcome = !u.exists ? fb.t("fb.messages.rejected", "rejected")
                    : u.state === "active" ? fb.t("fb.messages.approved", "approved")
                    : u.state === "blocked" ? fb.t("fb.messages.blocked", "blocked")
                    : fb.t("fb.messages.handled", "handled");
                meta.textContent = `${when} · ${outcome}`;
            } else {
                meta.textContent = when;
                body.appendChild(this._pendingActions(u, defaultQuota));
            }
        } else if (m.kind === "user.invited") {
            const u = m.subject || {};
            const label = u.name && u.email ? `${u.name} (${u.email})` : (u.name || u.email || fb.t("fb.messages.someone", "Someone"));
            icon.setAttribute("name", "user");
            text.textContent = fb.t("fb.messages.joined-by-invite", "{who} joined this Fishbowl through a space invitation.", { who: label });
            meta.textContent = when;
        } else if (m.kind === "password.reset") {
            icon.setAttribute("name", "key");
            text.textContent = m.data?.via === "import"
                ? fb.t("fb.messages.password-import", "A Global Admin set up your account with a password. Choose your own when you sign in.")
                : fb.t("fb.messages.password-reset", "A Global Admin reset your password. If you didn't ask for it, tell them.");
            meta.textContent = when;
        } else if (m.kind === "password.reset-used") {
            icon.setAttribute("name", "warn");
            text.textContent = fb.t("fb.messages.password-reset-used",
                "Someone signed in with the temporary password from the reset and chose a new one. If that wasn't you, an admin used your account — ask them, and change your password.");
            meta.textContent = when;
        } else if (m.kind === "user.approved") {
            icon.setAttribute("name", "success");
            text.textContent = fb.t("fb.messages.welcome", "Your account was approved. Welcome to this Fishbowl.");
            meta.textContent = when;
        } else if (m.kind === "app.message") {
            const d = m.data || {};
            const space = m.subject?.name || fb.t("fb.messages.a-space", "A space");
            icon.setAttribute("name", "grid");
            text.textContent = d.text || "";
            meta.textContent = `${space} · ${d.appName || d.app || ""} · ${when}`;
            row.appendChild(this._muteMenu(m.subject?.id, d.app, d.appName, space));
        } else if (m.kind === "quota.warning") {
            const d = m.data || {};
            const gb = (n) => `${fb.format.num((Number(n) || 0) / 1073741824, 1)} GB`;
            icon.setAttribute("name", "warn");
            text.textContent = fb.t("fb.messages.quota", "Your storage is almost full: {used} of {quota}.", { used: gb(d.usedBytes), quota: gb(d.quotaBytes) });
            meta.textContent = when;
            const link = document.createElement("a");
            link.href = "#";   // the quota is the user's own: the Your data window
            link.addEventListener("click", (e) => { e.preventDefault(); fb.yourData.open(); });
            link.textContent = fb.t("fb.messages.see-data", "See your data");
            link.className = "msg-link";
            body.appendChild(link);
        } else {
            icon.setAttribute("name", "info");
            text.textContent = fb.t("fb.messages.unknown", "A message this version can't show yet.");
            meta.textContent = when;
        }

        if (!m.readAt) {
            const read = document.createElement("button");
            read.type = "button";
            read.className = "icon-btn msg-read";
            read.title = fb.t("fb.messages.mark-read", "Mark read");
            read.setAttribute("aria-label", read.title);
            read.innerHTML = `<sac-icon name="check"></sac-icon>`;
            read.addEventListener("click", () => fb.api.messages.read(m.id).catch(() => {}));
            row.appendChild(read);
        }
        return row;
    }

    _pendingActions(user, defaultQuota) {
        const actions = document.createElement("div");
        actions.className = "toolbar msg-actions";
        const quota = fb.accounts.quotaField(defaultQuota);
        const approve = button(fb.t("fb.accounts.approve", "Approve"), "btn primary");
        const reject = button(fb.t("fb.accounts.reject", "Reject"), "btn");
        const block = button(fb.t("fb.accounts.block", "Block"), "btn danger");
        approve.addEventListener("click", () => fb.accounts.approve(user, fb.accounts.readQuota(quota)));
        reject.addEventListener("click", () => fb.accounts.reject(user));
        block.addEventListener("click", () => fb.accounts.block(user));
        actions.append(quota, approve, reject, block);
        return actions;

        function button(label, cls) {
            const b = document.createElement("button");
            b.type = "button";
            b.className = cls;
            b.textContent = label;
            return b;
        }
    }
}

customElements.define("fb-messages-view", FbMessagesView);
