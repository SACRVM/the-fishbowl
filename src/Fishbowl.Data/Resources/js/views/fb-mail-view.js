/**
 * <fb-mail-view>  (mounted at #/mail, personal and space)
 *
 * Mail (spec 2026-10-07-mail-design, phase 1) on the kit's <sac-split>, the
 * list-and-editor frame: every account of the workspace in ONE list of
 * conversations — no folder tree — newest activity first, each marked in or
 * out by its latest message; the open conversation on the right with all its
 * messages, both directions, oldest first. Search is the server's full text;
 * the list header switches to Unread or Archived; the nav toolbar filters by
 * tag and by account and opens the Mail accounts window. A conversation's
 * tags are the workspace's tags. Opening an unread one marks it read here
 * and on the server.
 *
 * Reading is safe by default (decision 7): a message's HTML is its own
 * page at …/messages/{id}/html, framed with a sandbox and a policy that runs
 * no script and fetches nothing remote — remote images (tracking pixels)
 * only after "Show images". Attachments are fetched from the server on
 * demand. A space Reader reads only.
 *
 * Light-DOM so the kit's tokens and classes apply; view CSS is scoped with
 * `fb-mail-view`.
 */
class FbMailView extends HTMLElement {
    constructor() {
        super();
        this.threads = [];
        this.accounts = [];
        this.selectedId = null;
        this.view = "list";        // list | unread | archived
        this.tag = null;
        this.account = null;
        this.query = "";
        this.more = false;         // the last page was full: there may be older ones
        this._contactsByAddress = null;
    }

    async connectedCallback() {
        this.ws = fb.api.workspace();
        this.api = fb.api.mail.in(this.ws);
        this.render();
        this.writable = await fb.access.canWrite();
        this.manager = await this._canManage();
        this.toggleAttribute("readonly", !this.writable);
        await this.reload();
        if (!this.isConnected) return;
        this.paintToolbar();
        this._onIntent = () => {
            const it = fb.desktop?.takeIntent("mail");
            if (it?.action === "open" && it.id) this.open(it.id);
        };
        window.addEventListener("fb:intent", this._onIntent);
        this._onIntent();
        this._onVisible = () => { if (document.visibilityState === "visible") this.reload({ keep: true }); };
        document.addEventListener("visibilitychange", this._onVisible);
        this._schedule();
    }

    disconnectedCallback() {
        if (this._onIntent) window.removeEventListener("fb:intent", this._onIntent);
        if (this._onVisible) document.removeEventListener("visibilitychange", this._onVisible);
        clearTimeout(this._poll);
        clearTimeout(this._searchTimer);
        if (window.fb?.toolbar) fb.toolbar.clear();
    }

    t(key, fallback, vars) { return fb.t(`fb.mail.${key}`, fallback, vars); }

    /** Personal: the owner. A space: its Admins (the accounts speak for it). */
    async _canManage() {
        if (this.ws === "personal") return true;
        try {
            const slug = this.ws.slice(6);
            const space = (await fb.api.spaces.list()).find((s) => s.slug === slug);
            return ["admin", "owner"].includes(space?.role);
        } catch { return false; }
    }

    _splitPosition() {
        try { return localStorage.getItem("fb.mail.split") || "34%"; } catch { return "34%"; }
    }

    // While an account still reads its history the list fills: look again
    // soon; otherwise every two minutes (and when the tab comes back).
    _schedule() {
        clearTimeout(this._poll);
        const busy = this.accounts.some((a) => a.state === "new" || !a.backfillDone);
        this._poll = setTimeout(async () => {
            if (!this.isConnected) return;
            await this.reload({ keep: true });
            this._schedule();
        }, busy ? 10000 : 120000);
    }

    async reload({ keep = false } = {}) {
        try { this.accounts = await this.api.accounts(); }
        catch (err) { console.warn("[fb-mail-view] accounts failed:", err?.status); this.accounts = []; }
        await this.loadThreads({ keep });
        this.paintStatus();
        if (!this.accounts.length) this.showEmpty();
        else if (!this.selectedId) this.showNone();
    }

    query_() {
        return {
            q: this.query || null,
            unread: this.view === "unread" || null,
            archived: this.view === "archived" || null,
            tag: this.tag,
            account: this.account,
            limit: 50,
        };
    }

    async loadThreads({ keep = false } = {}) {
        let page = [];
        try { page = this.accounts.length ? await this.api.threads(this.query_()) : []; }
        catch (err) { console.warn("[fb-mail-view] threads failed:", err?.status); }
        // A refresh keeps what was scrolled in beyond the first page.
        if (keep && this.threads.length > page.length) {
            const ids = new Set(page.map((x) => x.threadId));
            page = page.concat(this.threads.filter((x) => !ids.has(x.threadId) && (!page.length || x.latestAt < page[page.length - 1].latestAt)));
        }
        this.threads = page;
        this.more = page.length >= 50;
        this.renderList();
    }

    async loadMore() {
        if (!this.more || this._loadingMore || !this.threads.length) return;
        this._loadingMore = true;
        try {
            const before = this.threads[this.threads.length - 1].latestAt;
            const page = await this.api.threads({ ...this.query_(), before });
            const ids = new Set(this.threads.map((x) => x.threadId));
            this.threads = this.threads.concat(page.filter((x) => !ids.has(x.threadId)));
            this.more = page.length >= 50;
            this.renderList();
        } catch (err) { console.warn("[fb-mail-view] more failed:", err?.status); }
        finally { this._loadingMore = false; }
    }

    render() {
        const t = (k, f) => this.t(k, f);
        this.innerHTML = `
            <style>
                fb-mail-view {
                    display: block;
                    height: calc(100vh - 50px);
                    height: calc(100dvh - 50px - env(safe-area-inset-top, 0px));
                    background: var(--bg);
                }
                fb-mail-view [hidden] { display: none !important; }
                fb-mail-view .mv-list-pane { height: 100%; background: var(--panel); display: flex; flex-direction: column; }
                fb-mail-view .mv-search { padding: 12px 12px 0; }
                fb-mail-view .mv-search input { width: 100%; box-sizing: border-box; }
                fb-mail-view .mv-head { display: flex; align-items: center; gap: 2px; padding: 12px 12px 6px 24px; }
                fb-mail-view .mv-head-title {
                    flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
                    font-family: 'Outfit', sans-serif; font-weight: 700; font-size: 11px;
                    text-transform: uppercase; letter-spacing: 0.1em; color: var(--text-muted);
                }
                fb-mail-view .mv-head .icon-btn { color: var(--text-muted); }
                fb-mail-view .mv-head .icon-btn.active { background: var(--accent-tint); color: var(--accent); }
                /* A line about the accounts — fetching, failing — on the rows' text edge. */
                fb-mail-view .mv-status {
                    display: flex; align-items: center; gap: 8px; margin: 0 12px 6px; padding: 6px 12px;
                    border: 0; border-radius: var(--radius-m); background: none; width: calc(100% - 24px);
                    color: var(--text-muted); font: inherit; font-size: 0.8125rem; text-align: left; cursor: pointer;
                }
                fb-mail-view .mv-status:hover { background: var(--hover); }
                fb-mail-view .mv-status.is-failed { color: var(--danger); }
                fb-mail-view .mv-status sac-icon { --icon-size: 14px; flex: none; }
                fb-mail-view .mv-items { flex: 1; min-height: 0; overflow-y: auto; padding: 2px 12px 12px; }
                fb-mail-view .mv-items:focus { outline: none; }
                fb-mail-view .mv-none { padding: 8px 12px; color: var(--text-muted); }
                fb-mail-view .mv-item {
                    display: flex; gap: 10px; padding: 9px 12px; margin-bottom: 2px; cursor: pointer;
                    border-radius: var(--radius-m); border: 1px solid transparent;
                }
                fb-mail-view .mv-item:hover { background: var(--hover); }
                fb-mail-view .mv-item.selected { background: var(--accent-tint); border-color: color-mix(in srgb, var(--accent) 28%, transparent); }
                fb-mail-view .mv-dir { flex: none; color: var(--text-muted); margin-top: 2px; --icon-size: 16px; }
                fb-mail-view .mv-item.unread .mv-dir { color: var(--accent); }
                fb-mail-view .mv-item-text { flex: 1; min-width: 0; }
                fb-mail-view .mv-line { display: flex; align-items: baseline; gap: 8px; min-width: 0; }
                fb-mail-view .mv-who, fb-mail-view .mv-subject, fb-mail-view .mv-snippet {
                    overflow: hidden; text-overflow: ellipsis; white-space: nowrap; min-width: 0;
                }
                fb-mail-view .mv-who { flex: 1; }
                fb-mail-view .mv-item.unread .mv-who, fb-mail-view .mv-item.unread .mv-subject { font-weight: 700; }
                fb-mail-view .mv-when, fb-mail-view .mv-count { flex: none; color: var(--text-muted); font-size: 0.8125rem; }
                fb-mail-view .mv-subject { flex: 1; }
                fb-mail-view .mv-marks { flex: none; display: inline-flex; gap: 4px; color: var(--text-muted); --icon-size: 13px; }
                fb-mail-view .mv-snippet { color: var(--text-muted); font-size: 0.8125rem; }
                fb-mail-view .mv-item-tags { display: flex; flex-wrap: wrap; gap: 4px; margin-top: 4px; }
                fb-mail-view .mv-editor-pane { height: 100%; overflow-y: auto; }
                fb-mail-view .mv-empty { min-height: 60%; justify-content: center; }
                fb-mail-view .mv-thread { max-width: 760px; margin: 0 auto; padding: 24px 24px 40px; }
                fb-mail-view .mv-thread-head { display: flex; align-items: flex-start; gap: 10px; }
                fb-mail-view .mv-thread-head h2 { flex: 1; min-width: 0; margin: 0; overflow-wrap: anywhere; }
                fb-mail-view .mv-tags { display: block; margin: 12px 0 20px; }
                fb-mail-view .mv-msg { border-top: 1px solid var(--border); }
                fb-mail-view .mv-msg:last-child { border-bottom: 1px solid var(--border); }
                fb-mail-view .mv-msg-head {
                    display: grid; grid-template-columns: auto minmax(0, 1fr) auto; gap: 2px 10px; align-items: baseline;
                    padding: 12px 4px; cursor: pointer;
                }
                fb-mail-view .mv-msg-head .mv-dir { grid-row: 1 / span 2; align-self: start; margin-top: 3px; }
                fb-mail-view .mv-msg-from { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
                fb-mail-view .mv-msg-from .muted { font-size: 0.8125rem; }
                fb-mail-view .mv-msg-head time { color: var(--text-muted); font-size: 0.8125rem; white-space: nowrap; }
                fb-mail-view .mv-msg-sub { grid-column: 2 / -1; color: var(--text-muted); font-size: 0.8125rem;
                    overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
                fb-mail-view .mv-contact { --icon-btn-size: 22px; --icon-btn-icon: 13px; vertical-align: middle; }
                fb-mail-view .mv-msg-body { padding: 0 4px 16px 30px; }
                fb-mail-view .mv-msg:not(.open) .mv-msg-body { display: none; }
                fb-mail-view .mv-text { white-space: pre-wrap; overflow-wrap: anywhere; margin: 0; font: inherit; line-height: 1.55; }
                /* A message's HTML is the sender's page: on paper, whatever the theme. */
                fb-mail-view .mv-html { display: block; width: 100%; height: 120px; border: 1px solid var(--border);
                    border-radius: var(--radius-l); background: #fff; }
                /* Remote images: one quiet line above the message, two small text buttons. */
                fb-mail-view .mv-images { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 12px; margin: 0 0 10px;
                    color: var(--text-muted); font-size: 0.8125rem; }
                fb-mail-view .mv-images sac-icon { --icon-size: 14px; }
                fb-mail-view .mv-images button {
                    padding: 0; border: 0; background: none; font: inherit; color: var(--accent); cursor: pointer;
                }
                fb-mail-view .mv-images button:hover { text-decoration: underline; }
                fb-mail-view .mv-attachments { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 12px; }
                fb-mail-view .mv-att {
                    display: inline-flex; align-items: center; gap: 6px; max-width: 100%; padding: 6px 10px;
                    border: 1px solid var(--border); border-radius: var(--radius-m); color: var(--text); text-decoration: none;
                    font-size: 0.8125rem;
                }
                fb-mail-view .mv-att:hover { background: var(--hover); }
                fb-mail-view .mv-att span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
                fb-mail-view .mv-att .muted { flex: none; }
            </style>
            <sac-split id="split" collapse show="start" position="${this._splitPosition()}" min-start="300px" min-end="380px"
                       aria-label="${t("resize", "Resize the mail list")}">
                <aside class="mv-list-pane" slot="start">
                    <div class="mv-search">
                        <input type="search" id="mv-search" placeholder="${t("search", "Search mail")}"/>
                    </div>
                    <div class="mv-head">
                        <span class="mv-head-title" id="mv-head-title"></span>
                        <button type="button" class="icon-btn" data-view="unread" title="${t("only-unread", "Only unread")}" aria-label="${t("only-unread", "Only unread")}"><sac-icon name="eye"></sac-icon></button>
                        <button type="button" class="icon-btn" data-view="archived" title="${t("archived", "Archived")}" aria-label="${t("archived", "Archived")}"><sac-icon name="archive"></sac-icon></button>
                        <button type="button" class="icon-btn mv-write" id="mv-refresh" title="${t("refresh", "Fetch new mail")}" aria-label="${t("refresh", "Fetch new mail")}"><sac-icon name="sync"></sac-icon></button>
                    </div>
                    <button type="button" class="mv-status" id="mv-status" hidden></button>
                    <div class="mv-items fb-scroll-fade" id="mv-items" tabindex="-1"></div>
                </aside>
                <main class="mv-editor-pane" slot="end" id="mv-pane"></main>
            </sac-split>`;

        for (const btn of this.querySelectorAll(".mv-head [data-view]")) {
            btn.addEventListener("click", () => {
                this.view = this.view === btn.dataset.view ? "list" : btn.dataset.view;
                this.paintHead();
                this.loadThreads();
            });
        }
        this.querySelector("#mv-refresh").addEventListener("click", async () => {
            for (const a of this.accounts) { try { await this.api.sync(a.id); } catch { /* the status says */ } }
            sac.toast?.(this.t("fetching", "Fetching new mail…"));
            setTimeout(() => this.reload({ keep: true }), 4000);
        });
        this.querySelector("#mv-status").addEventListener("click", () => this.openAccounts());
        this.querySelector("#split").addEventListener("sac:resize", (e) => {
            try { localStorage.setItem("fb.mail.split", e.detail.position); } catch { /* storage off */ }
        });
        this.querySelector("#mv-search").addEventListener("input", (e) => {
            clearTimeout(this._searchTimer);
            this._searchTimer = setTimeout(() => { this.query = e.target.value.trim(); this.loadThreads(); }, 250);
        });
        const items = this.querySelector("#mv-items");
        items.addEventListener("scroll", () => {
            if (items.scrollTop + items.clientHeight > items.scrollHeight - 200) this.loadMore();
        });
        this.paintHead();
    }

    paintHead() {
        const title = this.view === "unread" ? this.t("head-unread", "Unread")
            : this.view === "archived" ? this.t("head-archived", "Archived")
            : this.t("head-all", "Mail");
        const parts = [title, this.tag ? `#${this.tag}` : null, this.account ? this.accounts.find((a) => a.id === this.account)?.name : null];
        this.querySelector("#mv-head-title").textContent = parts.filter(Boolean).join(" · ");
        for (const btn of this.querySelectorAll(".mv-head [data-view]")) {
            btn.classList.toggle("active", btn.dataset.view === this.view);
            btn.setAttribute("aria-pressed", String(btn.dataset.view === this.view));
        }
        this.querySelector("#mv-refresh").hidden = !this.writable || !this.accounts.length;
    }

    /** One line about the accounts: one that fails, else one that fetches. */
    paintStatus() {
        const el = this.querySelector("#mv-status");
        const failed = this.accounts.find((a) => a.state === "failed");
        const busy = this.accounts.filter((a) => a.state === "new" || !a.backfillDone);
        el.classList.toggle("is-failed", !!failed);
        if (failed) {
            el.innerHTML = `<sac-icon name="warn"></sac-icon><span></span>`;
            el.querySelector("span").textContent = `${failed.name}: ${fb.mailAccounts.errorText(failed.lastError)}`;
        } else if (busy.length) {
            const n = busy.reduce((s, a) => s + (a.messageCount || 0), 0);
            el.innerHTML = `<sac-icon name="sync"></sac-icon><span></span>`;
            el.querySelector("span").textContent = this.t("status-fetching", "Fetching mail — {n} so far", { n: fb.format.num(n) });
        }
        el.hidden = !failed && !busy.length;
        this.paintHead();
    }

    // ------------------------------------------------------------ list --

    /** Who a conversation is with: names (or the address before the @), at most three. */
    who(th) {
        const names = th.participants.map((p) => p.name || p.address.split("@")[0]);
        const shown = names.slice(0, 3).join(", ");
        if (!shown) return this.t("nobody", "(nobody)");
        return names.length > 3 ? `${shown} +${names.length - 3}` : shown;
    }

    when(iso) {
        const d = new Date(iso);
        const now = new Date();
        if (d.toDateString() === now.toDateString()) return fb.format.time(d);
        if (d.getFullYear() === now.getFullYear()) return fb.format.dayMonth(d);
        return fb.format.date(d);
    }

    renderList() {
        const box = this.querySelector("#mv-items");
        if (!box) return;
        if (!this.threads.length) {
            const text = !this.accounts.length ? this.t("no-mail", "No mail")
                : this.query ? this.t("no-match", "Nothing matches")
                : this.view === "unread" ? this.t("no-unread", "Nothing unread")
                : this.view === "archived" ? this.t("no-archived", "Nothing archived")
                : this.t("no-mail", "No mail");
            box.innerHTML = `<div class="mv-none"></div>`;
            box.firstElementChild.textContent = text;
            return;
        }
        box.replaceChildren(...this.threads.map((th) => {
            const row = document.createElement("div");
            row.className = "mv-item" + (th.unread ? " unread" : "") + (th.threadId === this.selectedId ? " selected" : "");
            row.dataset.id = th.threadId;
            row.innerHTML = `
                <sac-icon class="mv-dir"></sac-icon>
                <div class="mv-item-text">
                    <div class="mv-line"><span class="mv-who"></span><span class="mv-count"></span><span class="mv-when"></span></div>
                    <div class="mv-line"><span class="mv-subject"></span><span class="mv-marks"></span></div>
                    <div class="mv-snippet"></div>
                    <div class="mv-item-tags"></div>
                </div>`;
            const dir = row.querySelector(".mv-dir");
            dir.setAttribute("name", th.latestDirection === "out" ? "fb-mail-out" : "fb-mail-in");
            dir.setAttribute("title", th.latestDirection === "out" ? this.t("sent", "Sent") : this.t("received", "Received"));
            row.querySelector(".mv-who").textContent = this.who(th);
            row.querySelector(".mv-count").textContent = th.count > 1 ? String(th.count) : "";
            row.querySelector(".mv-when").textContent = this.when(th.latestAt);
            row.querySelector(".mv-subject").textContent = th.subject || this.t("no-subject", "(no subject)");
            row.querySelector(".mv-snippet").textContent = th.snippet || "";
            const marks = row.querySelector(".mv-marks");
            if (th.hasAttachments) marks.innerHTML += `<sac-icon name="attachment" title="${this.t("has-attachments", "Attachments")}"></sac-icon>`;
            if (th.flagged) marks.innerHTML += `<sac-icon name="star" title="${this.t("flagged", "Flagged")}"></sac-icon>`;
            const tags = row.querySelector(".mv-item-tags");
            for (const name of th.tags) {
                const chip = document.createElement("sac-chip");
                chip.setAttribute("label", name);
                chip.setAttribute("color", fb.tags.colorFor(name));
                tags.appendChild(chip);
            }
            if (!th.tags.length) tags.remove();
            row.addEventListener("click", () => this.open(th.threadId));
            return row;
        }));
    }

    // ---------------------------------------------------------- thread --

    showEmpty() {
        this.selectedId = null;
        const pane = this.querySelector("#mv-pane");
        pane.innerHTML = `
            <div class="empty-state mv-empty">
                <sac-icon name="mail"></sac-icon>
                <h3>${this.t("no-accounts", "No mail account yet")}</h3>
                ${this.manager ? `<button type="button" class="btn primary" id="mv-add">${this.t("add-account", "Add account")}</button>` : ""}
            </div>`;
        pane.querySelector("#mv-add")?.addEventListener("click", () => this.openAccounts());
    }

    showNone() {
        const pane = this.querySelector("#mv-pane");
        pane.innerHTML = `
            <div class="empty-state mv-empty">
                <sac-icon name="mail"></sac-icon>
                <h3>${this.t("none-open", "No conversation open")}</h3>
            </div>`;
    }

    async open(threadId) {
        this.selectedId = threadId;
        for (const row of this.querySelectorAll(".mv-item")) row.classList.toggle("selected", row.dataset.id === threadId);
        const split = this.querySelector("#split");
        if (split) split.show = "end";
        const pane = this.querySelector("#mv-pane");
        let messages;
        try { messages = await this.api.thread(threadId); }
        catch (err) {
            if (this.selectedId !== threadId) return;
            pane.innerHTML = `<div class="empty-state mv-empty"><sac-icon name="mail"></sac-icon><h3></h3></div>`;
            pane.querySelector("h3").textContent = err?.status === 404 ? this.t("gone", "This conversation is gone.") : this.t("load-failed", "The conversation can't be loaded right now.");
            return;
        }
        if (this.selectedId !== threadId) return;
        await this.contactsByAddress();
        this.paintThread(threadId, messages);

        const th = this.threads.find((x) => x.threadId === threadId);
        if (this.writable && messages.some((m) => m.direction === "in" && !m.seen)) {
            try {
                await this.api.setSeen(threadId, true);
                if (th) { th.unread = 0; this.renderList(); }
            } catch (err) { console.warn("[fb-mail-view] seen failed:", err?.status); }
        }
    }

    paintThread(threadId, messages) {
        const pane = this.querySelector("#mv-pane");
        const subject = messages[0]?.subject || this.t("no-subject", "(no subject)");
        pane.innerHTML = `
            <div class="mv-thread">
                <div class="mv-thread-head">
                    <h2></h2>
                    <button type="button" class="icon-btn mv-write" id="mv-unread" title="${this.t("mark-unread", "Mark unread")}" aria-label="${this.t("mark-unread", "Mark unread")}"><sac-icon name="eye-off"></sac-icon></button>
                </div>
                <sac-chip-input class="mv-tags" add-label="${this.t("add-tag", "Add tag")}" allow-create></sac-chip-input>
                <div class="mv-msgs"></div>
            </div>`;
        pane.querySelector("h2").textContent = subject;
        pane.querySelector("#mv-unread").hidden = !this.writable;
        pane.querySelector("#mv-unread").addEventListener("click", async () => {
            try {
                await this.api.setSeen(threadId, false);
                const th = this.threads.find((x) => x.threadId === threadId);
                if (th) { th.unread = Math.max(1, th.unread); this.renderList(); }
                sac.toast?.(this.t("marked-unread", "Marked unread."));
            } catch (err) { sac.toast?.(fb.errors.text(err, this.t("failed", "That didn't work.")), { kind: "error" }); }
        });

        this.paintTags(pane.querySelector(".mv-tags"), threadId, messages);

        const list = pane.querySelector(".mv-msgs");
        // The last message open, and every unread one; the rest folded to a line.
        messages.forEach((m, i) => list.appendChild(this.messageEl(m, i === messages.length - 1 || (m.direction === "in" && !m.seen))));
        pane.scrollTop = 0;
    }

    async paintTags(input, threadId, messages) {
        input.value = [...new Set(messages.flatMap((m) => m.tags || []))];
        if (!this.writable) { input.setAttribute("readonly", ""); return; }
        try {
            input.suggestions = (await fb.tags.all())
                .filter((x) => x.userAssignable !== false)
                .map((x) => ({ name: x.name, color: x.color, count: x.usageCount }));
        } catch { /* no suggestions */ }
        const save = async () => {
            try {
                const res = await this.api.setTags(threadId, input.value);
                const th = this.threads.find((x) => x.threadId === threadId);
                if (th) { th.tags = res.tags; this.renderList(); }
                fb.tags.invalidate();
            } catch (err) { sac.toast?.(fb.errors.text(err, this.t("tags-failed", "The tags couldn't be saved.")), { kind: "error" }); }
        };
        input.addEventListener("sac:change", save);
        // A tag made here keeps the colour picked for it (like notes').
        input.addEventListener("sac:create", async (e) => {
            try { await fb.api.tags.upsertColor(e.detail.name, e.detail.color); fb.tags.invalidate(); }
            catch (err) { console.warn("[fb-mail-view] creating tag failed:", err?.status); }
        });
    }

    messageEl(m, open) {
        const el = document.createElement("article");
        el.className = "mv-msg" + (open ? " open" : "");
        el.innerHTML = `
            <header class="mv-msg-head">
                <sac-icon class="mv-dir"></sac-icon>
                <span class="mv-msg-from"><strong></strong> <span class="muted"></span></span>
                <time></time>
                <div class="mv-msg-sub"></div>
            </header>
            <div class="mv-msg-body"></div>`;
        const dir = el.querySelector(".mv-dir");
        dir.setAttribute("name", m.direction === "out" ? "fb-mail-out" : "fb-mail-in");
        dir.setAttribute("title", m.direction === "out" ? this.t("sent", "Sent") : this.t("received", "Received"));
        const from = el.querySelector(".mv-msg-from");
        from.querySelector("strong").textContent = m.fromName || m.fromAddress || this.t("unknown-sender", "(unknown sender)");
        from.querySelector(".muted").textContent = m.fromName && m.fromAddress ? `<${m.fromAddress}>` : "";
        const contact = m.fromAddress && this._contactsByAddress?.get(m.fromAddress.toLowerCase());
        if (contact) {
            const go = document.createElement("button");
            go.type = "button";
            go.className = "icon-btn mv-contact";
            go.title = this.t("go-contact", "Go to contact");
            go.setAttribute("aria-label", go.title);
            go.innerHTML = `<sac-icon name="contact"></sac-icon>`;
            go.addEventListener("click", (e) => { e.stopPropagation(); fb.desktop.go("contacts", "open", contact.id); });
            from.appendChild(go);
        }
        const time = el.querySelector("time");
        time.dateTime = m.sentAt;
        time.textContent = `${fb.format.date(new Date(m.sentAt))} ${fb.format.time(new Date(m.sentAt))}`;
        const sub = el.querySelector(".mv-msg-sub");
        const recipients = (list) => list.map((p) => p.name || p.address).join(", ");
        const toLine = () => [
            m.toList?.length ? `${this.t("to", "to")} ${recipients(m.toList)}` : null,
            m.ccList?.length ? `${this.t("cc", "cc")} ${recipients(m.ccList)}` : null,
        ].filter(Boolean).join(" · ");
        const paintSub = () => { sub.textContent = el.classList.contains("open") ? toLine() : (m.snippet || ""); };
        paintSub();
        el.querySelector(".mv-msg-head").addEventListener("click", () => {
            el.classList.toggle("open");
            paintSub();
            if (el.classList.contains("open")) this.paintBody(el, m);
        });
        if (open) this.paintBody(el, m);
        return el;
    }

    paintBody(el, m) {
        const body = el.querySelector(".mv-msg-body");
        if (body.dataset.painted) return;
        body.dataset.painted = "1";
        if (m.hasHtml) {
            const shown = this._imagesAllowed(m);
            if (m.remoteImages && !shown) {
                const note = document.createElement("div");
                note.className = "mv-images";
                note.innerHTML = `<sac-icon name="image"></sac-icon><span>${this.t("images-hidden", "Remote images are hidden.")}</span>
                    <button type="button" data-images="once">${this.t("show-images", "Show images")}</button>
                    <button type="button" data-images="sender">${this.t("always-images", "Always from this sender")}</button>`;
                note.addEventListener("click", (e) => {
                    const how = e.target.closest("[data-images]")?.dataset.images;
                    if (!how) return;
                    if (how === "sender") this._allowImagesFrom(m.fromAddress);
                    note.remove();
                    frame.src = this.api.htmlUrl(m.id, true);
                });
                body.appendChild(note);
            }
            const frame = document.createElement("iframe");
            frame.className = "mv-html";
            frame.setAttribute("sandbox", "allow-same-origin allow-popups allow-popups-to-escape-sandbox");
            frame.setAttribute("referrerpolicy", "no-referrer");
            frame.title = m.subject || this.t("message", "Message");
            frame.src = this.api.htmlUrl(m.id, shown);
            // Same origin, no script: the frame is measured from here, and
            // follows its own size as images load.
            frame.addEventListener("load", () => {
                const doc = frame.contentDocument;
                if (!doc?.body) return;
                // The page's own height: its body plus the body's margins —
                // measured now, again once fonts and images settle, and
                // whenever the body changes size.
                const fit = () => {
                    const h = Math.ceil(Math.max(doc.body.scrollHeight, doc.body.getBoundingClientRect().height));
                    frame.style.height = `${Math.max(60, h + 2)}px`;
                };
                fit();
                requestAnimationFrame(fit);
                setTimeout(fit, 300);
                doc.fonts?.ready?.then(fit);
                try { new ResizeObserver(fit).observe(doc.body); } catch { /* fitted above */ }
            });
            body.appendChild(frame);
        } else {
            const pre = document.createElement("pre");
            pre.className = "mv-text";
            pre.textContent = m.bodyText || "";
            body.appendChild(pre);
        }
        if (m.attachments?.length) {
            const box = document.createElement("div");
            box.className = "mv-attachments";
            for (const a of m.attachments) {
                const link = document.createElement("a");
                link.className = "mv-att";
                link.href = this.api.attachmentUrl(m.id, a.index);
                link.setAttribute("download", a.name || "");
                link.innerHTML = `<sac-icon name="attachment"></sac-icon><span></span><span class="muted"></span>`;
                link.children[1].textContent = a.name || this.t("attachment", "Attachment");
                link.children[2].textContent = a.size ? fb.accounts?.formatBytes?.(a.size) ?? "" : "";
                box.appendChild(link);
            }
            body.appendChild(box);
        }
    }

    // Senders whose remote images show without asking — per browser.
    _imagesAllowed(m) {
        try { return !!m.fromAddress && JSON.parse(localStorage.getItem("fb.mail.images") || "[]").includes(m.fromAddress.toLowerCase()); }
        catch { return false; }
    }
    _allowImagesFrom(address) {
        if (!address) return;
        try {
            const list = JSON.parse(localStorage.getItem("fb.mail.images") || "[]");
            if (!list.includes(address.toLowerCase())) list.push(address.toLowerCase());
            localStorage.setItem("fb.mail.images", JSON.stringify(list.slice(-500)));
        } catch { /* storage off */ }
    }

    /** email (lower case) → contact, for "Go to contact"; loaded once per mount. */
    async contactsByAddress() {
        if (this._contactsByAddress) return this._contactsByAddress;
        const map = new Map();
        try {
            for (const c of await fb.api.contacts.in(this.ws).list())
                for (const e of c.emails || []) if (e.value) map.set(e.value.toLowerCase(), c);
        } catch { /* no contacts — no links */ }
        return (this._contactsByAddress = map);
    }

    // --------------------------------------------------------- toolbar --

    openAccounts() {
        fb.mailAccounts.open({
            ws: this.ws,
            canManage: this.manager,
            canWrite: this.writable,
            onChanged: () => this.reload({ keep: true }).then(() => { this.paintToolbar(); this._schedule(); }),
        });
    }

    async paintToolbar() {
        const items = [];
        let tags = [];
        try { tags = (await fb.tags.all()).map((x) => x.name); } catch { /* no tags */ }
        if (tags.length) {
            items.push({
                id: "mv-tag", icon: "tag", title: this.t("filter-tag", "Filter by tag"),
                menu: [
                    { action: "", label: this.t("all-tags", "All conversations"), checked: !this.tag },
                    "-",
                    ...tags.map((name) => ({ action: name, label: name, icon: "tag", color: fb.accents.cssVar(fb.tags.colorFor(name)), checked: this.tag === name })),
                ],
                onSelect: (action) => { this.tag = action || null; this.paintHead(); this.loadThreads(); this.paintToolbar(); },
            });
        }
        if (this.accounts.length > 1) {
            items.push({
                id: "mv-account", icon: "users", title: this.t("filter-account", "Filter by account"),
                menu: [
                    { action: "", label: this.t("all-accounts", "All accounts"), checked: !this.account },
                    "-",
                    ...this.accounts.map((a) => ({ action: a.id, label: a.name, icon: "mail", checked: this.account === a.id })),
                ],
                onSelect: (action) => { this.account = action || null; this.paintHead(); this.loadThreads(); this.paintToolbar(); },
            });
        }
        items.push({ id: "mv-accounts", icon: "settings", title: this.t("accounts", "Mail accounts"), onClick: () => this.openAccounts() });
        if (this.isConnected) fb.toolbar.set(items);
    }
}

customElements.define("fb-mail-view", FbMailView);
sac.router.register("#/mail", "fb-mail-view", { label: "Mail", icon: "mail", palette: false });
