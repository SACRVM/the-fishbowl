/**
 * <fb-mail-view>  (mounted at #/mail, personal and space)
 *
 * Mail (spec 2026-10-07-mail-design, phase 1) on the kit's <sac-split>, the
 * list-and-editor frame: every account of the workspace in ONE list of
 * conversations — no folder tree — newest activity first, each marked in or
 * out by its latest message; the open conversation on the right with all its
 * messages, both directions, oldest first. Search is the server's full text;
 * the list header switches to Unread or Archived; tags filter like Notes' — a
 * strip of chips, all of them must match — and where a message came from is
 * a tag too: each account's source tag — its name, "icloud" — system-given,
 * never assigned or removed by hand, always in the strip. A conversation's other tags are the
 * workspace's, given in its tag bar. The nav toolbar opens the Mail accounts
 * window. Opening an unread one marks it read here and on the server.
 *
 * Writing (decision 12, fb.mailCompose) takes the right pane: "+" in the
 * list head (and the desktop tile's, the palette's) starts a new mail, the
 * open conversation's head replies, replies to all or forwards its latest
 * message; drafts are saved as they are typed and listed under Drafts in the
 * list head. Archive and the flag (phase 2) sit in the conversation's head
 * and in a row's context menu.
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
        this.view = "list";        // list | unread | archived | drafts
        this.drafts = [];          // the Drafts view: the caller's own
        this._compose = null;      // the mail being written in the right pane
        this.tags = [];            // the filter strip's picks (all of them)
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
            if (it?.action === "create") this.compose("new");
        };
        window.addEventListener("fb:intent", this._onIntent);
        this._onIntent();
        this._onVisible = () => { if (document.visibilityState === "visible") this.reload({ keep: true }); };
        document.addEventListener("visibilitychange", this._onVisible);
        // A conversation's own menu (kit 2.29): a right-click or a long press.
        this._rowMenu = sac.contextMenu(this.querySelector("#mv-items"), {
            targets: ".mv-item",
            items: (row) => this.rowItems(row),
        });
        this._schedule();
    }

    disconnectedCallback() {
        if (this._onIntent) window.removeEventListener("fb:intent", this._onIntent);
        if (this._onVisible) document.removeEventListener("visibilitychange", this._onVisible);
        this._rowMenu?.destroy();
        this._compose?.leave();
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
            tag: this.tags,
            limit: 50,
        };
    }

    async loadThreads({ keep = false } = {}) {
        if (this.view === "drafts") {
            try { this.drafts = await this.api.drafts(); }
            catch (err) { console.warn("[fb-mail-view] drafts failed:", err?.status); this.drafts = []; }
            this.threads = [];
            this.more = false;
            this.renderList();
            this.renderTagFilter();
            return;
        }
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
        this.renderTagFilter();
    }

    /** The accounts' source tags (their names, system-given). */
    sourceTags() { return new Set(this.accounts.map((a) => a.sourceTag)); }

    /** A row's chips: the source only when there is more than one account to tell apart. */
    shownTags(tags) {
        const sources = this.sourceTags();
        return this.accounts.length > 1 ? tags : tags.filter((x) => !sources.has(x));
    }

    /**
     * The tag strip, as Notes': nothing picked — every tag mail carries;
     * something picked — what the shown conversations still carry (so a chip
     * always narrows further) and the picks themselves. The sources first
     * (always — where mail came from is a filter too), then the most used.
     */
    async renderTagFilter() {
        const wrapper = this.querySelector("#mv-tag-wrapper");
        const strip = this.querySelector("#mv-tag-filter");
        if (!strip) return;
        let all = [];
        try { all = this.accounts.length ? await this.api.tags() : []; } catch { all = []; }
        if (!this.isConnected) return;
        const picked = new Set(this.tags);
        const reachable = new Set(this.threads.flatMap((th) => th.tags));
        const visible = all.filter((x) => (picked.size === 0 ? x.count > 0 : reachable.has(x.name)) || picked.has(x.name))
            .sort((a, b) => (b.source - a.source) || (b.count - a.count) || a.name.localeCompare(b.name));
        wrapper.hidden = visible.length === 0;
        strip.replaceChildren(...visible.map((x) => {
            const chip = document.createElement("sac-chip");
            chip.setAttribute("label", x.name);
            chip.setAttribute("color", fb.tags.colorFor(x.name));
            chip.setAttribute("clickable", "");
            if (picked.has(x.name)) chip.setAttribute("selected", "");
            chip.addEventListener("click", () => {
                this.tags = picked.has(x.name) ? this.tags.filter((n) => n !== x.name) : [...this.tags, x.name];
                this.loadThreads();
            });
            return chip;
        }));
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
                /* The tag strip, Notes' own: the row layout only — clamp, fade
                   and the more/less tab are <sac-collapsible>'s. */
                fb-mail-view .mv-tag-filter { display: flex; flex-wrap: wrap; align-items: center; gap: 4px; padding: 10px 12px 8px; }
                fb-mail-view .mv-tag-collapsible[hidden] { display: none !important; }
                @media (max-width: 768px) {
                    fb-mail-view .mv-tag-filter { flex-wrap: nowrap; overflow-x: auto; scrollbar-width: none; }
                    fb-mail-view .mv-tag-filter::-webkit-scrollbar { display: none; }
                    fb-mail-view .mv-tag-filter > * { flex: none; }
                }
                /* A conversation's tag bar: the source (fixed) then what was given. */
                fb-mail-view .mv-tagbar { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; margin: 12px 0 20px; }
                fb-mail-view .mv-tagbar sac-chip-input { flex: 1; min-width: 160px; }
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
                    position: relative;
                    display: flex; gap: 10px; padding: 9px 12px; margin-bottom: 2px; cursor: pointer;
                    border-radius: var(--radius-m); border: 1px solid transparent;
                }
                /* New and unread: a round dot in the accent under the direction
                   arrow, on the subject's line — the row keeps its edge. The
                   arrow is 16px from the 12px padding; a line is 1.5em. */
                fb-mail-view .mv-item.unread::before {
                    content: ""; position: absolute; left: calc(12px + 8px - 4px); top: calc(9px + 2.25em - 4px);
                    width: 8px; height: 8px; border-radius: 50%; background: var(--accent);
                }
                fb-mail-view .mv-item:hover { background: var(--hover); }
                fb-mail-view .mv-item.selected { background: var(--accent-tint); border-color: color-mix(in srgb, var(--accent) 28%, transparent); }
                fb-mail-view .mv-dir { flex: none; color: var(--text-muted); margin-top: 2px; --icon-size: 16px; }
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
                fb-mail-view .mv-thread { padding: 24px 24px 40px; }
                fb-mail-view .mv-thread-head { display: flex; align-items: flex-start; gap: 10px; }
                /* A flagged conversation: the star on, like a pinned note's. */
                fb-mail-view .mv-thread-head .icon-btn.active { color: var(--accent-warm); }
                fb-mail-view .mv-thread-head h2 { flex: 1; min-width: 0; margin: 0; overflow-wrap: anywhere; }
                fb-mail-view .mv-msg { border-top: 1px solid var(--border); }
                fb-mail-view .mv-msg:last-child { border-bottom: 1px solid var(--border); }
                fb-mail-view .mv-msg-head {
                    display: grid; grid-template-columns: auto minmax(0, 1fr) auto auto; gap: 2px 10px; align-items: baseline;
                    padding: 12px 4px; cursor: pointer;
                }
                fb-mail-view .mv-msg-head .mv-dir { grid-row: 1 / span 2; align-self: start; margin-top: 3px; }
                fb-mail-view .mv-msg-from { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
                fb-mail-view .mv-msg-from .muted { font-size: 0.8125rem; }
                fb-mail-view .mv-msg-head time { color: var(--text-muted); font-size: 0.8125rem; white-space: nowrap; }
                fb-mail-view .mv-msg-head .mv-msg-del { align-self: center; margin: -6px 0; }
                fb-mail-view .mv-msg-sub { grid-column: 2 / -1; color: var(--text-muted); font-size: 0.8125rem;
                    overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
                fb-mail-view .mv-contact { --icon-btn-size: 22px; --icon-btn-icon: 13px; vertical-align: middle; }
                fb-mail-view .mv-msg-body { padding: 0 0 16px; }
                fb-mail-view .mv-msg:not(.open) .mv-msg-body { display: none; }
                fb-mail-view .mv-text { white-space: pre-wrap; overflow-wrap: anywhere; margin: 0; font: inherit; line-height: 1.55; }
                /* A message's HTML is the sender's page: on paper, whatever the
                   theme, and set light — the frame says so too, so a mail's own
                   dark rules (prefers-color-scheme) never fire on white. */
                fb-mail-view .mv-html { display: block; width: 100%; height: 60px; border: 1px solid var(--border);
                    border-radius: var(--radius-l); background: #fff; color-scheme: light; }
                /* A plain mail (no backgrounds of its own) is part of the
                   conversation: no paper, no frame, the app's colours. */
                /* Its page is set light (the dark theme inverts it); the frame
                   element says the same, or the browser lays an opaque white
                   canvas under a page whose colour scheme differs from its
                   frame's. */
                fb-mail-view .mv-html.plain { border: 0; border-radius: 0; background: transparent; }
                /* A mail made for light and dark (Revolut: color-scheme "light
                   dark"), in the dark theme: set dark, its own dark design on
                   the app's ground — no paper. */
                fb-mail-view .mv-html.own-dark { border: 0; border-radius: 0; background: transparent; color-scheme: dark; }
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
                /* Writing (fb.mailCompose): the fields on one grid, the text
                   below growing with the pane, the actions held at the bottom. */
                fb-mail-view .mc { display: flex; flex-direction: column; min-height: 100%; padding: 24px 24px 0; box-sizing: border-box; }
                fb-mail-view .mc-head h2 { margin: 0 0 16px; }
                fb-mail-view .mc-fields { display: grid; grid-template-columns: auto minmax(0, 1fr); gap: 8px 12px; align-items: center; }
                fb-mail-view .mc-fields > label { color: var(--text-muted); font-size: 0.8125rem; }
                fb-mail-view .mc-fields input, fb-mail-view .mc-fields sac-select { width: 100%; box-sizing: border-box; }
                fb-mail-view .mc-to { display: flex; align-items: center; gap: 10px; min-width: 0; }
                fb-mail-view .mc-to input { flex: 1; min-width: 0; }
                fb-mail-view .mc-more {
                    flex: none; padding: 0; border: 0; background: none; font: inherit; font-size: 0.8125rem;
                    color: var(--accent); cursor: pointer;
                }
                fb-mail-view .mc-more:hover { text-decoration: underline; }
                fb-mail-view .mc-body { position: relative; margin-top: 16px; }
                fb-mail-view .mc-body sac-md-editor { display: block; width: 100%; }
                /* The editor's own surface is 60vh tall (a note fills a page);
                   a mail's text starts smaller and grows as it is written. */
                fb-mail-view .mc-body sac-md-editor::part(editor) { min-height: 220px; }
                fb-mail-view .mc-files { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 12px; }
                fb-mail-view .mc-files[hidden] { display: none; }
                fb-mail-view .mc-file {
                    display: inline-flex; align-items: center; gap: 6px; max-width: 100%; padding: 2px 2px 2px 10px;
                    border: 1px solid var(--border); border-radius: var(--radius-m); font-size: 0.8125rem;
                }
                fb-mail-view .mc-file .mc-file-name { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
                fb-mail-view .mc-file .muted { flex: none; }
                fb-mail-view .mc-file .icon-btn { --icon-btn-size: 24px; --icon-btn-icon: 13px; }
                fb-mail-view .mc-quote { margin: 16px 0; padding-left: 12px; border-left: 2px solid var(--border); color: var(--text-muted); font-size: 0.8125rem; }
                fb-mail-view .mc-quote-text { margin-top: 4px; white-space: pre-wrap; overflow-wrap: anywhere; max-height: 40vh; overflow: auto; }
                fb-mail-view .mc-actions {
                    position: sticky; bottom: 0; flex-wrap: wrap; justify-content: flex-end;
                    margin-top: auto; padding: 12px 0 16px; background: var(--bg); border-top: 1px solid var(--border);
                }
                fb-mail-view .mc-state { flex: 1; min-width: 0; color: var(--text-muted); font-size: 0.8125rem; }
                fb-mail-view .mc-state.is-error { color: var(--danger); }
            </style>
            <sac-split id="split" collapse show="start" position="${this._splitPosition()}" min-start="300px" min-end="380px"
                       aria-label="${t("resize", "Resize the mail list")}">
                <aside class="mv-list-pane" slot="start">
                    <div class="mv-search">
                        <input type="search" id="mv-search" placeholder="${t("search", "Search mail")}"/>
                    </div>
                    <sac-collapsible class="mv-tag-collapsible" id="mv-tag-wrapper" max-height="82px" hidden>
                        <div class="mv-tag-filter" id="mv-tag-filter"></div>
                    </sac-collapsible>
                    <div class="mv-head">
                        <span class="mv-head-title" id="mv-head-title"></span>
                        <button type="button" class="icon-btn" data-view="unread" title="${t("only-unread", "Only unread")}" aria-label="${t("only-unread", "Only unread")}"><sac-icon name="eye"></sac-icon></button>
                        <button type="button" class="icon-btn" data-view="archived" title="${t("archived", "Archived")}" aria-label="${t("archived", "Archived")}"><sac-icon name="archive"></sac-icon></button>
                        <button type="button" class="icon-btn mv-write" data-view="drafts" title="${t("drafts", "Drafts")}" aria-label="${t("drafts", "Drafts")}"><sac-icon name="pencil"></sac-icon></button>
                        <button type="button" class="icon-btn mv-write" id="mv-refresh" title="${t("refresh", "Fetch new mail")}" aria-label="${t("refresh", "Fetch new mail")}"><sac-icon name="sync"></sac-icon></button>
                        <button type="button" class="icon-btn mv-write" id="mv-new" title="${t("new", "New mail")}" aria-label="${t("new", "New mail")}"><sac-icon name="plus"></sac-icon></button>
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
        this.querySelector("#mv-new").addEventListener("click", () => this.compose("new"));
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
            : this.view === "drafts" ? this.t("head-drafts", "Drafts")
            : this.t("head-all", "Mail");
        this.querySelector("#mv-head-title").textContent = title;
        for (const btn of this.querySelectorAll(".mv-head [data-view]")) {
            btn.classList.toggle("active", btn.dataset.view === this.view);
            btn.setAttribute("aria-pressed", String(btn.dataset.view === this.view));
        }
        this.querySelector("#mv-refresh").hidden = !this.writable || !this.accounts.length;
        this.querySelector("#mv-new").hidden = !this.writable || !this.accounts.length;
        this.querySelector(".mv-head [data-view='drafts']").hidden = !this.writable || !this.accounts.length;
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
        if (this.view === "drafts") { this.renderDrafts(box); return; }
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
            for (const name of this.shownTags(th.tags)) {
                const chip = document.createElement("sac-chip");
                chip.setAttribute("label", name);
                chip.setAttribute("color", fb.tags.colorFor(name));
                tags.appendChild(chip);
            }
            if (!tags.childElementCount) tags.remove();
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
        await this.leaveCompose();
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
                    <button type="button" class="icon-btn mv-write" id="mv-reply"><sac-icon name="fb-reply"></sac-icon></button>
                    <button type="button" class="icon-btn mv-write" id="mv-reply-all"><sac-icon name="fb-reply-all"></sac-icon></button>
                    <button type="button" class="icon-btn mv-write" id="mv-forward"><sac-icon name="fb-forward"></sac-icon></button>
                    <button type="button" class="icon-btn mv-write" id="mv-flag"><sac-icon name="star"></sac-icon></button>
                    <button type="button" class="icon-btn mv-write" id="mv-archive"><sac-icon></sac-icon></button>
                    <button type="button" class="icon-btn mv-write" id="mv-unread" title="${this.t("mark-unread", "Mark unread")}" aria-label="${this.t("mark-unread", "Mark unread")}"><sac-icon name="eye-off"></sac-icon></button>
                    <button type="button" class="icon-btn mv-write" id="mv-delete" title="${this.t("delete", "Delete")}" aria-label="${this.t("delete", "Delete")}"><sac-icon name="trash"></sac-icon></button>
                </div>
                <div class="mv-tagbar">
                    <sac-chip-input class="mv-tags" add-label="${this.t("add-tag", "Add tag")}" allow-create></sac-chip-input>
                </div>
                <div class="mv-msgs"></div>
            </div>`;
        pane.querySelector("h2").textContent = subject;
        // A flag sits on a message; the conversation shows one when any has it.
        const flagged = messages.some((m) => m.flagged);
        const flag = pane.querySelector("#mv-flag");
        flag.classList.toggle("active", flagged);
        flag.setAttribute("aria-pressed", String(flagged));
        flag.title = flagged ? this.t("unflag", "Remove flag") : this.t("flag", "Flag");
        flag.setAttribute("aria-label", flag.title);
        flag.addEventListener("click", () => this.flag(threadId, !flagged));
        // Archived: nothing of it in an inbox, nothing in Sent left in the list.
        const archived = !messages.some((m) => m.state === "inbox" || m.state === "sent");
        const arch = pane.querySelector("#mv-archive");
        arch.querySelector("sac-icon").setAttribute("name", archived ? "fb-inbox" : "archive");
        arch.title = archived ? this.t("unarchive", "Move to inbox") : this.t("archive", "Archive");
        arch.setAttribute("aria-label", arch.title);
        arch.addEventListener("click", () => this.archive(threadId, !archived));
        // Reply, reply to all, forward: the latest message (reply to all only
        // when it went to more than one).
        const latest = messages[messages.length - 1];
        const buttons = [
            ["#mv-reply", "reply", this.t("reply", "Reply")],
            ["#mv-reply-all", "reply-all", this.t("reply-all", "Reply all")],
            ["#mv-forward", "forward", this.t("forward", "Forward")],
        ].map(([sel, kind, label]) => {
            const b = pane.querySelector(sel);
            b.title = label;
            b.setAttribute("aria-label", label);
            b.addEventListener("click", () => this.compose(kind, latest));
            return b;
        });
        for (const b of [...buttons, flag, arch]) b.hidden = !this.writable;
        if ((latest.toList?.length || 0) + (latest.ccList?.length || 0) < 2) buttons[1].hidden = true;
        pane.querySelector("#mv-unread").hidden = !this.writable;
        pane.querySelector("#mv-delete").hidden = !this.writable;
        pane.querySelector("#mv-delete").addEventListener("click", () => this.deleteMail(threadId, null));
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
        // With more than one, each can be deleted on its own.
        const single = messages.length < 2;
        messages.forEach((m, i) => list.appendChild(this.messageEl(m, i === messages.length - 1 || (m.direction === "in" && !m.seen), threadId, single)));
        pane.scrollTop = 0;
    }

    async paintTags(input, threadId, messages) {
        // Where it came from: the accounts' source tags, fixed — shown, never edited.
        const sources = [...new Set(messages.map((m) => this.accounts.find((a) => a.id === m.accountId)?.sourceTag).filter(Boolean))];
        for (const name of sources.reverse()) {
            const chip = document.createElement("sac-chip");
            chip.setAttribute("label", name);
            chip.setAttribute("color", fb.tags.colorFor(name));
            chip.title = this.t("source-tag", "Where it came from — set by Fishbowl");
            input.before(chip);
        }
        input.value = [...new Set(messages.flatMap((m) => m.tags || []))];
        if (!this.writable) { input.setAttribute("readonly", ""); return; }
        try {
            input.suggestions = (await fb.tags.all())
                .filter((x) => x.userAssignable !== false && !this.sourceTags().has(x.name))
                .map((x) => ({ name: x.name, color: x.color, count: x.usageCount }));
        } catch { /* no suggestions */ }
        const save = async () => {
            try {
                const res = await this.api.setTags(threadId, input.value);
                const th = this.threads.find((x) => x.threadId === threadId);
                if (th) { const src = this.sourceTags(); th.tags = th.tags.filter((x) => src.has(x)).concat(res.tags); this.renderList(); }
                fb.tags.invalidate();
                this.renderTagFilter();
            } catch (err) { sac.toast?.(fb.errors.text(err, this.t("tags-failed", "The tags couldn't be saved.")), { kind: "error" }); }
        };
        input.addEventListener("sac:change", save);
        // A tag made here keeps the colour picked for it (like notes').
        input.addEventListener("sac:create", async (e) => {
            try { await fb.api.tags.upsertColor(e.detail.name, e.detail.color); fb.tags.invalidate(); }
            catch (err) { console.warn("[fb-mail-view] creating tag failed:", err?.status); }
        });
    }

    messageEl(m, open, threadId, single) {
        const el = document.createElement("article");
        el.className = "mv-msg" + (open ? " open" : "");
        el.innerHTML = `
            <header class="mv-msg-head reveal-on-hover">
                <sac-icon class="mv-dir"></sac-icon>
                <span class="mv-msg-from"><strong></strong> <span class="muted"></span></span>
                <time></time>
                <span></span>
                <div class="mv-msg-sub"></div>
            </header>
            <div class="mv-msg-body"></div>`;
        if (this.writable && !single) {
            const del = document.createElement("button");
            del.type = "button";
            del.className = "icon-btn hover-reveal mv-msg-del";
            del.title = this.t("delete-message", "Delete this message");
            del.setAttribute("aria-label", del.title);
            del.innerHTML = `<sac-icon name="trash"></sac-icon>`;
            del.addEventListener("click", (e) => { e.stopPropagation(); this.deleteMail(threadId, m); });
            el.querySelector(".mv-msg-head time").nextElementSibling.replaceWith(del);
        }
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

    /** A conversation row's context menu: open it, read / unread, delete. */
    rowItems(row) {
        if (row.dataset.draft) {
            const id = row.dataset.draft;
            return [
                { id: "open", label: fb.t("fb.common.open", "Open"), icon: "pencil", onClick: () => this.openDraft(id) },
                "-",
                { id: "discard", label: this.t("discard", "Discard"), icon: "trash", danger: true, onClick: () => this.discardDraft(id) },
            ];
        }
        const th = this.threads.find((x) => x.threadId === row.dataset.id);
        if (!th) return null;
        const items = [{ id: "open", label: fb.t("fb.common.open", "Open"), icon: "mail", onClick: () => this.open(th.threadId) }];
        if (!this.writable) return items;
        items.push(th.unread
            ? { id: "read", label: this.t("mark-read", "Mark read"), icon: "eye", onClick: () => this.markSeen(th, true) }
            : { id: "unread", label: this.t("mark-unread", "Mark unread"), icon: "eye-off", onClick: () => this.markSeen(th, false) });
        items.push(th.flagged
            ? { id: "unflag", label: this.t("unflag", "Remove flag"), icon: "star", onClick: () => this.flag(th.threadId, false) }
            : { id: "flag", label: this.t("flag", "Flag"), icon: "star", onClick: () => this.flag(th.threadId, true) });
        items.push(th.archived
            ? { id: "unarchive", label: this.t("unarchive", "Move to inbox"), icon: "fb-inbox", onClick: () => this.archive(th.threadId, false) }
            : { id: "archive", label: this.t("archive", "Archive"), icon: "archive", onClick: () => this.archive(th.threadId, true) });
        items.push("-", { id: "delete", label: this.t("delete-more", "Delete…"), icon: "trash", danger: true,
            onClick: () => this.deleteMail(th.threadId, null) });
        return items;
    }

    async markSeen(th, seen) {
        try {
            await this.api.setSeen(th.threadId, seen);
            th.unread = seen ? 0 : Math.max(1, th.unread);
            this.renderList();
        } catch (err) { sac.toast?.(fb.errors.text(err, this.t("failed", "That didn't work.")), { kind: "error" }); }
    }

    // ---------------------------------------------------------- writing --

    renderDrafts(box) {
        if (!this.drafts.length) {
            box.innerHTML = `<div class="mv-none"></div>`;
            box.firstElementChild.textContent = this.t("no-drafts", "No drafts");
            return;
        }
        const open = this._compose?.id;
        box.replaceChildren(...this.drafts.map((d) => {
            const row = document.createElement("div");
            row.className = "mv-item" + (d.id === open ? " selected" : "");
            row.dataset.draft = d.id;
            row.innerHTML = `
                <sac-icon class="mv-dir" name="pencil"></sac-icon>
                <div class="mv-item-text">
                    <div class="mv-line"><span class="mv-who"></span><span class="mv-when"></span></div>
                    <div class="mv-line"><span class="mv-subject"></span></div>
                    <div class="mv-snippet"></div>
                </div>`;
            const names = (d.to || []).map((x) => x.replace(/\s*<[^>]*>\s*$/, "").replace(/^"|"$/g, "") || x);
            row.querySelector(".mv-who").textContent = names.join(", ") || this.t("no-recipients", "(no recipients)");
            row.querySelector(".mv-when").textContent = this.when(d.updatedAt);
            row.querySelector(".mv-subject").textContent = d.subject || this.t("no-subject", "(no subject)");
            row.querySelector(".mv-snippet").textContent = (d.body || "").split("\n").find((l) => l.trim()) || "";
            row.addEventListener("click", () => this.openDraft(d.id));
            return row;
        }));
    }

    /** A new mail, or a reply / reply to all / forward of `message`. */
    async compose(kind, message = null) {
        if (!this.writable) return;
        if (!this.accounts.length) { this.showEmpty(); return; }
        await this.leaveCompose();
        let draft;
        try { draft = await this.api.createDraft({ kind, messageId: message?.id }); }
        catch (err) { sac.toast?.(fb.errors.text(err, this.t("failed", "That didn't work.")), { kind: "error" }); return; }
        this.mountCompose(draft, message);
    }

    async openDraft(id) {
        await this.leaveCompose();
        let draft;
        try { draft = await this.api.draft(id); }
        catch (err) { sac.toast?.(fb.errors.text(err, this.t("failed", "That didn't work.")), { kind: "error" }); this.loadThreads(); return; }
        let original = null;
        if (draft.refMessageId && draft.threadId) {
            try { original = (await this.api.thread(draft.threadId)).find((m) => m.id === draft.refMessageId) || null; }
            catch { /* the original is gone: no quote shown, the server says so on send */ }
        }
        this.mountCompose(draft, original);
    }

    mountCompose(draft, original) {
        this.selectedId = draft.threadId || null;
        for (const row of this.querySelectorAll(".mv-item"))
            row.classList.toggle("selected", row.dataset.draft ? row.dataset.draft === draft.id : row.dataset.id === this.selectedId);
        const split = this.querySelector("#split");
        if (split) split.show = "end";
        this._compose = fb.mailCompose.mount(this.querySelector("#mv-pane"), {
            api: this.api,
            ws: this.ws,
            draft,
            accounts: this.accounts,
            original,
            onClose: (result) => this.composeClosed(draft, result),
        });
    }

    /** Leaves the mail being written: saved, or gone when nothing was written. */
    async leaveCompose() {
        const c = this._compose;
        this._compose = null;
        if (!c) return;
        await c.leave();
        if (this.view === "drafts") this.loadThreads();
    }

    async composeClosed(draft, result) {
        this._compose = null;
        if (result?.sent) {
            sac.toast?.(this.t("sent-done", "Sent."));
            if (this.view === "drafts" || this.view === "archived") { this.view = "list"; this.paintHead(); }
            await this.loadThreads();
            this.open(result.sent.threadId);
            return;
        }
        await this.loadThreads();
        if (draft.threadId && this.threads.some((x) => x.threadId === draft.threadId)) this.open(draft.threadId);
        else { this.selectedId = null; this.showNone(); }
    }

    async discardDraft(id) {
        if (this._compose?.id === id) { this._compose = null; this.selectedId = null; this.showNone(); }
        try { await this.api.deleteDraft(id); }
        catch (err) { sac.toast?.(fb.errors.text(err, this.t("failed", "That didn't work.")), { kind: "error" }); }
        this.loadThreads();
    }

    async flag(threadId, flagged) {
        try { await this.api.setFlagged(threadId, flagged); }
        catch (err) { sac.toast?.(fb.errors.text(err, this.t("failed", "That didn't work.")), { kind: "error" }); return; }
        const th = this.threads.find((x) => x.threadId === threadId);
        if (th) { th.flagged = flagged; this.renderList(); }
        if (this.selectedId === threadId) this.open(threadId);
    }

    /**
     * Decision 3: archived on the mail server too (the inbox's copy moves to
     * the archive folder), or back to the inbox. The conversation leaves the
     * list it was in; the toast can take it back.
     */
    async archive(threadId, archived, undo = true) {
        try { await this.api.archive(threadId, archived); }
        catch (err) { sac.toast?.(fb.errors.text(err, this.t("archive-failed", "That couldn't be moved on the mail server.")), { kind: "error" }); return; }
        const text = archived ? this.t("archived-done", "Archived.") : this.t("unarchived-done", "Moved to the inbox.");
        sac.toast?.(text, undo ? { action: { label: "Undo", labelKey: "fb.common.undo", onClick: () => this.archive(threadId, !archived, false) } } : undefined);
        await this.loadThreads();
        if (this.selectedId !== threadId) return;
        if (this.threads.some((x) => x.threadId === threadId)) this.open(threadId);
        else { this.selectedId = null; this.showNone(); }
    }

    /**
     * Decision 8 of the mail spec: only in Fishbowl (the mail server keeps it)
     * or everywhere (also into the server's Trash). Either way it waits in
     * Fishbowl's trash. A whole conversation, or one message of it.
     */
    async deleteMail(threadId, message) {
        const answer = await sac.dialog.confirm({
            title: message ? this.t("delete-message-title", "Delete this message?") : this.t("delete-title", "Delete this conversation?"),
            message: this.t("delete-how", "Only in Fishbowl: your mail server keeps it. Everywhere: it goes to the server's trash too. Either way it waits in Fishbowl's trash."),
            buttons: [
                { action: "cancel", label: fb.t("fb.common.cancel", "Cancel"), kind: "default" },
                { action: "fishbowl", label: this.t("delete-here", "Only in Fishbowl"), kind: "default" },
                { action: "everywhere", label: this.t("delete-everywhere", "Everywhere"), kind: "destructive" },
            ],
        });
        if (answer !== "fishbowl" && answer !== "everywhere") return;
        try {
            if (message) await this.api.deleteMessage(message.id, answer);
            else await this.api.deleteThread(threadId, answer);
        } catch (err) {
            sac.toast?.(fb.errors.text(err, this.t("delete-failed", "That couldn't be deleted.")), { kind: "error" });
            return;
        }
        sac.toast?.(answer === "everywhere"
            ? this.t("deleted-everywhere", "Deleted — in the trash here and on the server.")
            : this.t("deleted-here", "Deleted here — it's in the trash. Your mail server keeps it."));
        await this.loadThreads();
        if (message && this.threads.some((x) => x.threadId === threadId)) this.open(threadId);
        else if (this.selectedId === threadId) { this.selectedId = null; this.showNone(); }
    }

    paintBody(el, m) {
        const body = el.querySelector(".mv-msg-body");
        if (body.dataset.painted) return;
        body.dataset.painted = "1";
        if (m.hasHtml) {
            const shown = this._imagesAllowed(m);
            let watchFrame = () => {};
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
                    frame.src = this.api.htmlUrl(m.id, true, this.theme());
                    watchFrame();
                });
                body.appendChild(note);
            }
            const frame = document.createElement("iframe");
            frame.className = "mv-html" + (m.htmlLook === "plain" ? " plain"
                : m.htmlLook === "adaptive" && this.theme() === "dark" ? " own-dark" : "");
            frame.setAttribute("sandbox", "allow-same-origin allow-popups allow-popups-to-escape-sandbox");
            frame.setAttribute("referrerpolicy", "no-referrer");
            frame.title = m.subject || this.t("message", "Message");
            frame.src = this.api.htmlUrl(m.id, shown, this.theme());
            // Same origin, no script: the frame is measured from here, and
            // follows its own size as images load — from the moment its page
            // is parsed, not at load, which waits for every remote image: a
            // slow image server would leave the frame empty until then.
            const fitted = new WeakSet();
            const setup = (doc) => {
                const root = doc?.documentElement;
                if (!root || fitted.has(doc) || doc.URL === "about:blank") return;
                fitted.add(doc);
                // The whole page's height — the <html> with its inset and any
                // margin that runs out of a body set to margin: 0 — plus a
                // sideways scrollbar when a wide table needs one: measured now,
                // again once fonts and images settle, and whenever it changes.
                const fit = () => {
                    const bar = Math.max(0, (frame.contentWindow?.innerHeight || 0) - root.clientHeight);
                    frame.style.height = `${Math.ceil(root.scrollHeight + bar) + 2}px`;
                };
                fit();
                requestAnimationFrame(fit);
                setTimeout(fit, 300);
                doc.fonts?.ready?.then(fit);
                for (const img of doc.images) if (!img.complete) img.addEventListener("load", fit, { once: true });
                try { const ro = new ResizeObserver(fit); ro.observe(root); if (doc.body) ro.observe(doc.body); } catch { /* fitted above */ }
            };
            // Every frame until its page is parsed (a new src starts it again).
            watchFrame = () => {
                let left = 600;
                const tick = () => {
                    const doc = frame.contentDocument;
                    if (doc && doc.URL !== "about:blank" && doc.readyState !== "loading") setup(doc);
                    if ((!doc || !fitted.has(doc)) && --left > 0) requestAnimationFrame(tick);
                };
                requestAnimationFrame(tick);
            };
            frame.addEventListener("load", () => setup(frame.contentDocument));
            watchFrame();
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

    /** "dark" or "light", from the page's actual background — whatever set it. */
    theme() {
        const rgb = (getComputedStyle(document.body).backgroundColor.match(/\d+(\.\d+)?/g) || [0, 0, 0]).map(Number);
        return (0.2126 * rgb[0] + 0.7152 * rgb[1] + 0.0722 * rgb[2]) / 255 < 0.5 ? "dark" : "light";
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

    paintToolbar() {
        if (this.isConnected) fb.toolbar.set([{ id: "mv-accounts", icon: "settings", title: this.t("accounts", "Mail accounts"), onClick: () => this.openAccounts() }]);
    }
}

customElements.define("fb-mail-view", FbMailView);
sac.router.register("#/mail", "fb-mail-view", { label: "Mail", icon: "mail", palette: false });
