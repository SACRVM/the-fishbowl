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
        this.innerHTML = `
            <style>
                fb-messages-view { display: block; padding: clamp(1.25rem, 5vw, 40px) clamp(1rem, 5vw, 48px); max-width: 780px; }
                fb-messages-view header { margin-bottom: 24px; }
                fb-messages-view h1 {
                    font-family: 'Outfit', 'Inter', sans-serif;
                    font-size: 28px;
                    font-weight: 700;
                    margin: 0 0 6px;
                    color: var(--text);
                }
                fb-messages-view .subtitle { color: var(--text-muted); font-size: 14px; margin: 0; }
                fb-messages-view .panel {
                    padding: 6px 20px;
                    background: var(--panel);
                    border: 1px solid var(--border);
                    border-radius: var(--radius-l);
                }
                fb-messages-view .empty { padding: 14px 0; color: var(--text-muted); font-size: 14px; margin: 0; }
                fb-messages-view .msg-row {
                    display: flex;
                    align-items: flex-start;
                    gap: 14px;
                    padding: 14px 0;
                }
                fb-messages-view .msg-row + .msg-row { border-top: 1px solid var(--border); }
                fb-messages-view .msg-row > sac-icon { --icon-size: 20px; color: var(--text-muted); flex-shrink: 0; margin-top: 1px; }
                fb-messages-view .msg-row.unread > sac-icon { color: var(--accent); }
                fb-messages-view .msg-body { flex: 1; min-width: 0; }
                fb-messages-view .msg-text { font-size: 14px; color: var(--text); margin: 0 0 2px; overflow-wrap: anywhere; }
                fb-messages-view .msg-row.unread .msg-text { font-weight: 600; }
                fb-messages-view .msg-meta { font-size: 12px; color: var(--text-muted); }
                fb-messages-view .msg-actions {
                    display: flex;
                    align-items: center;
                    flex-wrap: wrap;
                    gap: 8px;
                    margin-top: 10px;
                }
                fb-messages-view .msg-actions .btn { width: auto; }
                fb-messages-view .fb-quota-field { display: inline-flex; align-items: center; gap: 6px; font-size: 13px; color: var(--text-muted); }
                fb-messages-view .fb-quota-field input { width: 6em; }
                fb-messages-view .msg-read { flex-shrink: 0; }
            </style>
            <header>
                <h1>Messages</h1>
                <p class="subtitle">What this Fishbowl has to tell you.</p>
            </header>
            <div class="panel" id="messages-body"><p class="empty">Loading…</p></div>
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
            mount.innerHTML = `<p class="empty">Messages can't be loaded right now.</p>`;
            return;
        }
        const items = data?.items || [];
        if (!items.length) {
            mount.innerHTML = `<p class="empty">No messages.</p>`;
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
        row.className = "msg-row" + (m.readAt ? "" : " unread");
        row.dataset.kind = m.kind;
        row.dataset.id = m.id;
        row.innerHTML = `
            <sac-icon></sac-icon>
            <div class="msg-body">
                <p class="msg-text"></p>
                <div class="msg-meta"></div>
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
        actions.className = "msg-actions";
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
sac.router.register("#/messages", "fb-messages-view", { label: "Messages", icon: "mail" });
