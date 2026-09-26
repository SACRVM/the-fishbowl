/**
 * Messages (#/messages): the system inbox.
 *
 * What the system tells you — not chat. Today: "someone wants to join"
 * (admins, with Approve / Reject / Block right in the row) and "your
 * account was approved". A message another admin already handled shows
 * as done. Rows hold ids only; the server adds who a request is about.
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
                <h1>Messages</h1>
                <p class="subtitle">What this Fishbowl has to tell you.</p>
            </div></header>
            <div class="card" id="messages-body"><p class="muted">Loading…</p></div>
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
            mount.innerHTML = `<p class="muted">Messages can't be loaded right now.</p>`;
            return;
        }
        const items = data?.items || [];
        if (!items.length) {
            mount.innerHTML = `
                <div class="empty-state">
                    <sac-icon name="mail"></sac-icon>
                    <h3>No messages</h3>
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
            const label = u.name && u.email ? `${u.name} (${u.email})` : (u.name || u.email || "Someone");
            icon.setAttribute("name", "user");
            text.textContent = `${label} wants to join this Fishbowl.`;
            if (m.doneAt) {
                const outcome = !u.exists ? "rejected"
                    : u.state === "active" ? "approved"
                    : u.state === "blocked" ? "blocked"
                    : "handled";
                meta.textContent = `${when} · ${outcome}`;
            } else {
                meta.textContent = when;
                body.appendChild(this._pendingActions(u, defaultQuota));
            }
        } else if (m.kind === "user.approved") {
            icon.setAttribute("name", "success");
            text.textContent = "Your account was approved. Welcome to this Fishbowl.";
            meta.textContent = when;
        } else if (m.kind === "quota.warning") {
            const d = m.data || {};
            const gb = (n) => `${((Number(n) || 0) / 1073741824).toFixed(1)} GB`;
            icon.setAttribute("name", "warn");
            text.textContent = `Your storage is almost full: ${gb(d.usedBytes)} of ${gb(d.quotaBytes)}.`;
            meta.textContent = when;
            const link = document.createElement("a");
            link.href = "#/data";   // personal: the quota is the user's own
            link.textContent = "See your data";
            link.className = "msg-link";
            body.appendChild(link);
        } else {
            icon.setAttribute("name", "info");
            text.textContent = "A message this version can't show yet.";
            meta.textContent = when;
        }

        if (!m.readAt) {
            const read = document.createElement("button");
            read.type = "button";
            read.className = "icon-btn msg-read";
            read.title = "Mark read";
            read.setAttribute("aria-label", "Mark read");
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
        const approve = button("Approve", "btn primary");
        const reject = button("Reject", "btn");
        const block = button("Block", "btn danger");
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
sac.router.register("#/messages", "fb-messages-view", { label: "Messages", icon: "mail", palette: false });
