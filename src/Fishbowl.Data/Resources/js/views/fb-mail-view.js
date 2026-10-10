/**
 * <fb-mail-view>  (mounted at #/mail, personal and space)
 *
 * Mail (spec 2026-10-07-mail-design, phase 1) on the kit's <sac-split>, the
 * list-and-editor frame: every account of the workspace in ONE list of
 * conversations — no folder tree — newest activity first, each marked in or
 * out by its latest message; the open conversation on the right with all its
 * messages, both directions, oldest first. Tabs over the list switch between
 * Inbox, Archive, Drafts and Spam (Drafts and Spam with their count, Inbox
 * with its unread); Only unread in the list head filters Inbox and Archive.
 * Search is the server's full text; tags filter like Notes' — a
 * strip of chips, all of them must match — and where a message came from is
 * a tag too: each account's source tag — its name, "icloud" — system-given,
 * never assigned or removed by hand, always in the strip. A conversation's other tags are the
 * workspace's, given in its tag bar. The nav toolbar opens the Mail accounts
 * window. Opening an unread one marks it read here and on the server.
 *
 * Spam is read live from each account's spam folder, never stored (no
 * search, tags, agents or trash for it): the Spam tab, a count on it for
 * what came since the last look, each as plain text with
 * "Not spam" — moved into the inbox on the server, the sync brings it in.
 *
 * Writing (decision 12, fb.mailCompose) takes the right pane: "+" in the
 * list head (and the desktop tile's, the palette's) starts a new mail, the
 * open conversation's head replies, replies to all or forwards its latest
 * message; drafts are saved as they are typed and listed under the Drafts
 * tab. Archive and the flag (phase 2) sit in the conversation's head
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
        this.view = "list";        // the tab: list (Inbox) | archived | drafts | spam
        this.unreadOnly = false;   // Only unread, in Inbox and Archive
        this.unreadCount = 0;      // the Inbox tab's count
        this.draftCount = 0;       // the Drafts tab's
        this.spam = { items: [], failed: [], loaded: false };
        this.spamOpen = null;      // "<accountId>:<uid>" of the spam message shown
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
        // The list's keys and marks (kit 2.30), one for all three tabs: an
        // arrow opens the next conversation, draft or spam message, Shift
        // marks, Delete asks for the marked ones (else the one open). Spam is
        // read here, never deleted: there the arrows only move.
        const items = this.querySelector("#mv-items");
        this.sel = sac.selection.attach(items, {
            rows: ".mv-item",
            id: (row) => this.rowKey(row),
            current: () => this.openKey(),
            disabled: () => !this.writable || this.view === "spam",
            onChange: () => this.paintHead(),
            keyboard: true,
            onCursor: (key) => this.openRow(key),
        });
        items.addEventListener("sac:request-remove", (e) => this.removeMany(e.detail.ids));
        // A conversation's own menu (kit 2.29): a right-click or a long press.
        this._rowMenu = sac.contextMenu(items, {
            targets: ".mv-item",
            selection: this.writable ? this.sel : undefined,
            items: (row) => this.rowItems(row),
        });
        // A tab's own menu: Inbox and Archive read whole at once, Spam lets its count go.
        this._tabMenu = sac.contextMenu(this.querySelector("#mv-tabs"), {
            targets: "sac-tab",
            items: (tab) => this.tabItems(tab.getAttribute("name")),
        });
        this._schedule();
    }

    disconnectedCallback() {
        if (this._onIntent) window.removeEventListener("fb:intent", this._onIntent);
        if (this._onVisible) document.removeEventListener("visibilitychange", this._onVisible);
        this._rowMenu?.destroy();
        this._tabMenu?.destroy();
        this.sel?.destroy();
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

    // Every 10 s while the page is seen, the sync's stamp (in memory, cheap):
    // when it moved — mail the server pushed (IDLE) or the poll found — the
    // list loads again. It loads anyway while an account still reads its
    // history, every two minutes, and when the tab comes back.
    _schedule() {
        clearTimeout(this._poll);
        this._poll = setTimeout(async () => {
            if (!this.isConnected) return;
            if (document.visibilityState === "visible") {
                let stamp = null;
                try { stamp = (await this.api.stamp()).stamp; } catch { /* the next look */ }
                const moved = stamp !== null && this._stamp !== undefined && stamp !== this._stamp;
                if (stamp !== null) this._stamp = stamp;
                const busy = this.accounts.some((a) => a.state === "new" || !a.backfillDone);
                if (this.isConnected && (moved || busy || Date.now() - (this._loadedAt || 0) >= 120000))
                    await this.reload({ keep: true });
            }
            this._schedule();
        }, 10000);
    }

    async reload({ keep = false } = {}) {
        // A chip's colour comes from the tag registry (fb.tags.colorFor reads
        // it synchronously), so it is in before the first chip is painted.
        const tags = fb.tags.all().catch(() => null);
        this._loadedAt = Date.now();
        try { this.accounts = await this.api.accounts(); }
        catch (err) { console.warn("[fb-mail-view] accounts failed:", err?.status); this.accounts = []; }
        await tags;
        await this.loadThreads({ keep });
        this.paintStatus();
        this.loadCounts();
        this.checkSpam();
        if (!this.accounts.length) this.showEmpty();
        else if (!this.selectedId) this.showNone();
    }

    query_() {
        return {
            q: this.query || null,
            unread: this.unreadOnly || null,
            archived: this.view === "archived" || null,
            tag: this.tags,
            limit: 50,
        };
    }

    async loadThreads({ keep = false } = {}) {
        if (this.view === "spam") {
            // The servers take seconds: what was read last (or "Reading…")
            // shows at once, the fresh read when it's in.
            this.threads = [];
            this.more = false;
            this.renderList();
            this.renderTagFilter();
            if (await this.loadSpam() && this.view === "spam") { this.renderList(); this.renderTagFilter(); }
            return;
        }
        if (this.view === "drafts") {
            try { this.drafts = await this.api.drafts(); }
            catch (err) { console.warn("[fb-mail-view] drafts failed:", err?.status); this.drafts = []; }
            this.draftCount = this.drafts.length;
            this.paintTabs();
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
        // Conversations on their way out stay out while their servers answer.
        if (this._deleting?.size) page = page.filter((x) => !this._deleting.has(x.threadId));
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
        const picked = new Set(this.tags);
        const local = this.isLocalList();
        let visible;
        if (local) {
            // Spam and drafts carry no tags, only where they came from: the
            // accounts, filtered here.
            const items = this.view === "spam" ? this.spam.items : this.drafts;
            const sourceOf = this.sourceOf();
            const present = new Set(items.map(sourceOf));
            const reachable = new Set(this.narrowed(items).map(sourceOf));
            visible = [...this.sourceTags()].filter((n) => (picked.size === 0 ? present.has(n) : reachable.has(n)) || picked.has(n));
        } else {
            let all = [];
            try { all = this.accounts.length ? await this.api.tags() : []; } catch { all = []; }
            if (!this.isConnected) return;
            const reachable = new Set(this.threads.flatMap((th) => th.tags));
            visible = all.filter((x) => (picked.size === 0 ? x.count > 0 : reachable.has(x.name)) || picked.has(x.name))
                .sort((a, b) => (b.source - a.source) || (b.count - a.count) || a.name.localeCompare(b.name))
                .map((x) => x.name);
        }
        wrapper.hidden = visible.length === 0;
        strip.replaceChildren(...visible.map((name) => {
            const chip = document.createElement("sac-chip");
            chip.setAttribute("label", name);
            chip.setAttribute("color", fb.tags.colorFor(name));
            chip.setAttribute("clickable", "");
            if (picked.has(name)) chip.setAttribute("selected", "");
            chip.addEventListener("click", () => {
                this.tags = picked.has(name) ? this.tags.filter((n) => n !== name) : [...this.tags, name];
                if (local) { this.renderList(); this.renderTagFilter(); } else this.loadThreads();
                // A chip takes no focus: the list keeps the keys, so the arrows go on from the open row.
                this.querySelector("#mv-items")?.focus({ preventScroll: true });
            });
            return chip;
        }));
    }

    /** Spam and drafts are read whole and narrowed here, not by the server. */
    isLocalList() { return this.view === "spam" || this.view === "drafts"; }

    /** An item's account as its source tag. */
    sourceOf() {
        const by = new Map(this.accounts.map((a) => [a.id, a.sourceTag]));
        return (x) => by.get(x.accountId);
    }

    /** Spam or drafts as the search and the picked accounts narrow them (other tags don't apply there). */
    narrowed(items) {
        const sources = this.sourceTags();
        const picked = this.tags.filter((x) => sources.has(x));
        const sourceOf = this.sourceOf();
        const q = this.query.toLowerCase();
        const text = (x) => [x.fromName, x.fromAddress, x.subject, x.to, x.body].filter(Boolean).join(" ").toLowerCase();
        return items.filter((x) => picked.every((p) => p === sourceOf(x)) && (!q || text(x).includes(q)));
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
                /* The tabs: Inbox, Archive, Drafts, Spam — a count beside a name. */
                fb-mail-view .mv-tabs { flex: none; padding: 8px 12px 0; }
                fb-mail-view .mv-tab-count { margin-left: 6px; color: var(--text-muted); font-variant-numeric: tabular-nums; }
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
                /* A spam message: who, when and which account under the subject. */
                fb-mail-view .mv-spam-meta { margin: 6px 0 16px; color: var(--text-muted); font-size: 0.8125rem; overflow-wrap: anywhere; }
                fb-mail-view .mv-spam-actions { margin-bottom: 20px; }
                fb-mail-view .mv-text.is-waiting { color: var(--text-muted); }
                fb-mail-view .mv-text a { color: var(--accent); }
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
                    <sac-tab-group class="mv-tabs" id="mv-tabs" active="list" overflow="scroll" aria-label="${t("folders", "Mail folders")}" hidden>
                        <sac-tab name="list">${t("tab-inbox", "Inbox")}<span class="mv-tab-count" hidden></span></sac-tab>
                        <sac-tab name="archived">${t("tab-archive", "Archive")}</sac-tab>
                        <sac-tab name="drafts">${t("drafts", "Drafts")}<span class="mv-tab-count" hidden></span></sac-tab>
                        <sac-tab name="spam">${t("spam", "Spam")}<span class="mv-tab-count" hidden></span></sac-tab>
                    </sac-tab-group>
                    <div class="mv-search">
                        <input type="search" id="mv-search" placeholder="${t("search", "Search mail")}"/>
                    </div>
                    <sac-collapsible class="mv-tag-collapsible" id="mv-tag-wrapper" max-height="82px" hidden>
                        <div class="mv-tag-filter" id="mv-tag-filter"></div>
                    </sac-collapsible>
                    <div class="mv-head">
                        <span class="mv-head-title" id="mv-head-title"></span>
                        <button type="button" class="icon-btn" id="mv-unread-only" title="${t("only-unread", "Only unread")}" aria-label="${t("only-unread", "Only unread")}" aria-pressed="false"><sac-icon name="eye"></sac-icon></button>
                        <button type="button" class="icon-btn mv-write" id="mv-refresh" title="${t("refresh", "Fetch new mail")}" aria-label="${t("refresh", "Fetch new mail")}"><sac-icon name="sync"></sac-icon></button>
                        <button type="button" class="icon-btn mv-write" id="mv-new" title="${t("new", "New mail")}" aria-label="${t("new", "New mail")}"><sac-icon name="plus"></sac-icon></button>
                        <button type="button" class="icon-btn danger" id="mv-delete-marked" hidden title="${t("delete-marked", "Delete selected")}" aria-label="${t("delete-marked", "Delete selected")}"><sac-icon name="trash"></sac-icon></button>
                        <button type="button" class="icon-btn" id="mv-clear-marks" hidden title="${t("clear-marks", "Clear selection")}" aria-label="${t("clear-marks", "Clear selection")}"><sac-icon name="close"></sac-icon></button>
                    </div>
                    <button type="button" class="mv-status" id="mv-status" hidden></button>
                    <div class="mv-items fb-scroll-fade" id="mv-items"></div>
                </aside>
                <main class="mv-editor-pane" slot="end" id="mv-pane"></main>
            </sac-split>`;

        this.querySelector("#mv-tabs").addEventListener("sac:tab-show", (e) => this.setView(e.detail.name));
        this.querySelector("#mv-unread-only").addEventListener("click", () => {
            this.unreadOnly = !this.unreadOnly;
            this.paintHead();
            this.loadThreads();
        });
        this.querySelector("#mv-refresh").addEventListener("click", async () => {
            for (const a of this.accounts) { try { await this.api.sync(a.id); } catch { /* the status says */ } }
            sac.toast?.(this.t("fetching", "Fetching new mail…"));
            setTimeout(() => this.reload({ keep: true }), 4000);
        });
        this.querySelector("#mv-new").addEventListener("click", () => this.compose("new"));
        this.querySelector("#mv-delete-marked").addEventListener("click", () => this.removeMany(this.sel.marked));
        this.querySelector("#mv-clear-marks").addEventListener("click", () => this.sel.clear());
        this.querySelector("#mv-status").addEventListener("click", () => this.openAccounts());
        this.querySelector("#split").addEventListener("sac:resize", (e) => {
            try { localStorage.setItem("fb.mail.split", e.detail.position); } catch { /* storage off */ }
        });
        this.querySelector("#mv-search").addEventListener("input", (e) => {
            clearTimeout(this._searchTimer);
            this._searchTimer = setTimeout(() => {
                this.query = e.target.value.trim();
                if (this.isLocalList()) { this.renderList(); this.renderTagFilter(); } else this.loadThreads();
            }, 250);
        });
        const items = this.querySelector("#mv-items");
        // ↑/↓ in the search field go on in the list, from the open row.
        this.querySelector("#mv-search").addEventListener("keydown", (e) => {
            if (!["ArrowDown", "ArrowUp", "PageDown", "PageUp"].includes(e.key) || e.altKey || e.ctrlKey || e.metaKey || e.isComposing) return;
            e.preventDefault();
            items.focus({ preventScroll: true });
            items.dispatchEvent(new KeyboardEvent("keydown", { key: e.key, shiftKey: e.shiftKey, bubbles: true, cancelable: true }));
        });
        items.addEventListener("scroll", () => {
            if (items.scrollTop + items.clientHeight > items.scrollHeight - 200) this.loadMore();
        });
        this.paintHead();
    }

    /** Another tab: its list, the open spam message forgotten. */
    setView(view) {
        if (this.view === view) return;
        this.view = view;
        this.spamOpen = null;
        this.paintHead();
        this.loadThreads();
    }

    paintHead() {
        const filtered = this.view === "list" || this.view === "archived";
        const tab = this.view === "archived" ? this.t("tab-archive", "Archive")
            : this.view === "drafts" ? this.t("drafts", "Drafts")
            : this.view === "spam" ? this.t("spam", "Spam")
            : this.t("tab-inbox", "Inbox");
        // While rows are marked: how many, with Delete and Clear in place of
        // the head's own buttons.
        const n = this.sel?.marked.length || 0;
        this.querySelector("#mv-head-title").textContent = n
            ? (n === 1 ? this.t("marked-1", "1 selected") : this.t("marked", "{n} selected", { n }))
            : filtered && this.unreadOnly ? `${tab} · ${this.t("head-unread", "Unread")}` : tab;
        const unread = this.querySelector("#mv-unread-only");
        unread.hidden = !filtered || !this.accounts.length || n > 0;
        unread.classList.toggle("active", this.unreadOnly);
        unread.setAttribute("aria-pressed", String(this.unreadOnly));
        this.querySelector("#mv-refresh").hidden = !this.writable || !this.accounts.length || n > 0;
        this.querySelector("#mv-new").hidden = !this.writable || !this.accounts.length || n > 0;
        this.querySelector("#mv-delete-marked").hidden = n === 0;
        this.querySelector("#mv-clear-marks").hidden = n === 0;
        this.paintTabs();
    }

    /** The tabs: the one shown, and the counts — Inbox unread, Drafts, new Spam. */
    paintTabs() {
        const tabs = this.querySelector("#mv-tabs");
        if (!tabs) return;
        tabs.hidden = !this.accounts.length;
        if (tabs.active !== this.view) tabs.active = this.view;
        tabs.querySelector("sac-tab[name='drafts']").hidden = !this.writable;
        const count = (name, n) => {
            const el = tabs.querySelector(`sac-tab[name='${name}'] .mv-tab-count`);
            el.textContent = n > 99 ? "99+" : String(n);
            el.hidden = !n;
        };
        count("list", this.unreadCount);
        count("drafts", this.draftCount);
        count("spam", this.spamFresh());
    }

    /** A tab's menu: what goes for everything it holds at once (none for Drafts). */
    tabItems(name) {
        if (!this.accounts.length) return null;
        const readAll = (onClick) => [{ id: "read-all", label: this.t("read-all", "Mark all as read"), icon: "eye", onClick }];
        if ((name === "list" && this.unreadCount > 0 || name === "archived") && this.writable)
            return readAll(() => this.readAll(name === "archived"));
        if (name === "spam" && this.writable && (this.spamFresh() > 0 || this.spam.items.some((i) => !i.seen)))
            return readAll(() => this.spamReadAll());
        if (name === "spam" && this.spamFresh() > 0)
            return [{ id: "spam-seen", label: this.t("spam-seen-all", "Mark all as seen"), icon: "eye", onClick: () => this.spamSeenAll() }];
        return null;
    }

    /** Every unread conversation of the tab read — the whole tab, not what is
     *  loaded; here at once, on the servers behind. */
    async readAll(archived) {
        let res;
        try { res = await this.api.readAll(archived); }
        catch (err) { sac.toast?.(fb.errors.text(err, this.t("failed", "That didn't work.")), { kind: "error" }); return; }
        const n = res.conversations || 0;
        sac.toast?.(n === 0 ? this.t("read-all-none", "Nothing was unread")
            : n === 1 ? this.t("read-all-done-1", "1 conversation marked read")
            : this.t("read-all-done", "{n} conversations marked read", { n }));
        if (this.view === (archived ? "archived" : "list")) {
            for (const th of this.threads) th.unread = 0;
            this.loadThreads({ keep: true });
        }
        this.loadCounts();
    }

    /** Spam's count goes, as if it had been looked at. */
    /** The spam folders read on the servers — the whole folders — and their
     *  count gone, as after a look. */
    async spamReadAll() {
        this.spamSeenAll();
        let res;
        try { res = await this.api.spamReadAll(); }
        catch (err) { sac.toast?.(fb.errors.text(err, this.t("failed", "That didn't work.")), { kind: "error" }); return; }
        for (const i of this.spam.items) i.seen = true;
        const n = res.messages || 0;
        sac.toast?.(n === 0 ? this.t("read-all-none", "Nothing was unread")
            : n === 1 ? this.t("spam-read-done-1", "1 message marked read")
            : this.t("spam-read-done", "{n} messages marked read", { n }));
        if (res.failed?.length) {
            const names = res.failed.map((id) => this.accounts.find((a) => a.id === id)?.name || id).join(", ");
            sac.toast?.(this.t("spam-read-unreached", "Not reached: {names}", { names }), { kind: "error" });
        }
    }

    spamSeenAll() {
        const newest = this.spam.items.reduce((a, i) => (i.date > a ? i.date : a), "");
        if (newest) { try { localStorage.setItem(this._spamSeenKey(), newest); } catch { /* storage off */ } }
        this.paintTabs();
    }

    /** The Inbox's unread and the caller's drafts, for the tabs. */
    async loadCounts() {
        try {
            const [unread, drafts] = await Promise.all([
                this.api.unreadCount(),
                this.writable ? this.api.drafts() : Promise.resolve([]),
            ]);
            if (!this.isConnected) return;
            this.unreadCount = unread.unread || 0;
            this.draftCount = drafts.length;
            this.paintTabs();
        } catch (err) { console.warn("[fb-mail-view] counts failed:", err?.status); }
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
        if (this.view === "spam") { this.renderSpam(box); return; }
        if (!this.threads.length) {
            const text = !this.accounts.length ? this.t("no-mail", "No mail")
                : this.query ? this.t("no-match", "Nothing matches")
                : this.unreadOnly ? this.t("no-unread", "Nothing unread")
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
                this.loadCounts();
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
                this.loadCounts();
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
                .map((x) => ({ name: x.name, color: x.color, count: x.mailCount }));
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
        if (row.dataset.spam) {
            const item = this.spam.items.find((i) => `${i.accountId}:${i.uid}` === row.dataset.spam);
            if (!item) return null;
            const items = [{ id: "open", label: fb.t("fb.common.open", "Open"), icon: "fb-spam", onClick: () => this.openSpam(item) }];
            if (this.writable) items.push({ id: "not-spam", label: this.t("not-spam", "Not spam"), icon: "fb-inbox", onClick: () => this.notSpam(item) });
            return items;
        }
        const marked = this.sel?.marked || [];
        const many = marked.length > 1 && marked.includes(this.rowKey(row));
        if (row.dataset.draft) {
            const id = row.dataset.draft;
            return [
                { id: "open", label: fb.t("fb.common.open", "Open"), icon: "pencil", onClick: () => this.openDraft(id) },
                "-",
                many
                    ? { id: "discard", label: this.t("discard-marked", "Discard selected"), icon: "trash", danger: true, onClick: () => this.removeMany(marked) }
                    : { id: "discard", label: this.t("discard", "Discard"), icon: "trash", danger: true, onClick: () => this.discardDrafts([id]) },
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
        items.push("-", many
            ? { id: "delete", label: this.t("delete-marked-more", "Delete selected…"), icon: "trash", danger: true, onClick: () => this.removeMany(marked) }
            : { id: "delete", label: this.t("delete-more", "Delete…"), icon: "trash", danger: true, onClick: () => this.deleteMail(th.threadId, null) });
        return items;
    }

    // ------------------------------------------------- keys and marks --

    /** One key per row over the three tabs: a conversation's id, "d:" a
     *  draft's, "s:" a spam message's account and UID. */
    rowKey(row) {
        if (row.dataset.draft) return "d:" + row.dataset.draft;
        if (row.dataset.spam) return "s:" + row.dataset.spam;
        return row.dataset.id || null;
    }

    openKey() {
        if (this.view === "spam") return this.spamOpen ? "s:" + this.spamOpen : null;
        if (this.view === "drafts") return this._compose?.id ? "d:" + this._compose.id : null;
        return this.selectedId;
    }

    /** An arrow moved onto a row: it opens, as a click would. */
    openRow(key) {
        if (!key) return;
        if (key.startsWith("d:")) return this.openDraft(key.slice(2));
        if (key.startsWith("s:")) {
            const item = this.spam.items.find((i) => `${i.accountId}:${i.uid}` === key.slice(2));
            return item ? this.openSpam(item) : undefined;
        }
        return this.open(key);
    }

    /** Delete on the keys, the head's trash or a marked row's menu: the
     *  conversations asked like one (only here or everywhere), drafts
     *  discarded after a question; spam is never deleted here. */
    removeMany(keys) {
        if (!this.writable || !keys.length) return;
        const drafts = keys.filter((k) => k.startsWith("d:")).map((k) => k.slice(2));
        if (drafts.length) return this.discardDrafts(drafts);
        const threads = keys.filter((k) => !k.startsWith("d:") && !k.startsWith("s:"));
        if (threads.length) return this.deleteThreads(threads);
    }

    async markSeen(th, seen) {
        try {
            await this.api.setSeen(th.threadId, seen);
            th.unread = seen ? 0 : Math.max(1, th.unread);
            this.renderList();
            this.loadCounts();
        } catch (err) { sac.toast?.(fb.errors.text(err, this.t("failed", "That didn't work.")), { kind: "error" }); }
    }

    /**
     * A mail's text with its web addresses as links: only http(s), the link
     * always the address shown (text can't hide where it goes), in a new tab
     * that learns nothing of Fishbowl (noopener, noreferrer). With `warn`
     * (spam) a link first says where it goes and what spam does there.
     */
    fillText(el, text, { warn = false } = {}) {
        el.replaceChildren();
        let at = 0;
        for (const match of text.matchAll(/\bhttps?:\/\/[^\s<>"]+/gi)) {
            let url = match[0];
            // Sentence punctuation and an unopened closing bracket aren't the address's.
            for (;;) {
                const last = url.at(-1);
                const close = { ")": "(", "]": "[", "}": "{" }[last];
                if (/[.,;:!?'*]/.test(last)) url = url.slice(0, -1);
                else if (close && url.split(close).length < url.split(last).length) url = url.slice(0, -1);
                else break;
            }
            let ok = false;
            try { ok = ["http:", "https:"].includes(new URL(url).protocol); } catch { /* not an address */ }
            if (!ok) continue;
            el.append(text.slice(at, match.index));
            const a = document.createElement("a");
            a.href = url;
            a.textContent = url;
            a.target = "_blank";
            a.rel = "noopener noreferrer";
            if (warn) {
                a.addEventListener("click", (e) => { e.preventDefault(); this.openWarned(url); });
                a.addEventListener("auxclick", (e) => { if (e.button === 1) { e.preventDefault(); this.openWarned(url); } });
            }
            el.append(a);
            at = match.index + url.length;
        }
        el.append(text.slice(at));
    }

    /** A link from spam: where it really goes (the host as the browser reads it — a look-alike shows as xn--…), then open or not. */
    async openWarned(url) {
        const answer = await sac.dialog.confirm({
            title: this.t("link-title", "Open a link from spam?"),
            message: [
                this.t("link-where", "It goes to {host}:", { host: new URL(url).hostname }),
                url,
                this.t("link-warn", "Spam often leads to fake sign-in pages. Don't enter a password or payment details there."),
            ],
            buttons: [
                { action: "cancel", label: fb.t("fb.common.cancel", "Cancel"), kind: "primary" },
                { action: "open", label: this.t("link-open", "Open link"), kind: "destructive" },
            ],
        });
        if (answer === "open") window.open(url, "_blank", "noopener,noreferrer");
    }

    // ------------------------------------------------------------ spam --

    _spamSeenKey() { return `fb.mail.spam-seen.${this.ws}`; }

    /** Reads the spam folders; false when a later read overtook this one. */
    async loadSpam() {
        const token = this._spamRead = (this._spamRead || 0) + 1;
        let spam;
        try { spam = await this.api.spam(); }
        catch (err) { console.warn("[fb-mail-view] spam failed:", err?.status); spam = { items: [], failed: [], error: err }; }
        if (token !== this._spamRead || !this.isConnected) return false;
        this.spam = { ...spam, loaded: true };
        // Looking at the Spam view is the last look: its count goes.
        if (this.view === "spam" && this.spam.items.length) {
            try { localStorage.setItem(this._spamSeenKey(), this.spam.items[0].date); } catch { /* storage off */ }
        }
        this.paintTabs();
        return true;
    }

    /** Now and then, beside the list: is there new spam since the last look? */
    async checkSpam() {
        if (!this.accounts.length || this.view === "spam" || Date.now() - (this._spamAt || 0) < 5 * 60 * 1000) return;
        this._spamAt = Date.now();
        await this.loadSpam();
    }

    /** Spam that came since the last look (none while looking). */
    spamFresh() {
        if (this.view === "spam") return 0;
        let seen = "";
        try { seen = localStorage.getItem(this._spamSeenKey()) || ""; } catch { /* storage off */ }
        return this.spam.items.filter((i) => i.date > seen).length;
    }

    renderSpam(box) {
        const failedNames = this.spam.failed.map((id) => this.accounts.find((a) => a.id === id)?.name || id);
        const rows = this.narrowed(this.spam.items).map((item) => {
            const row = document.createElement("div");
            row.dataset.spam = `${item.accountId}:${item.uid}`;
            row.className = "mv-item" + (row.dataset.spam === this.spamOpen ? " selected" : "");
            row.innerHTML = `
                <sac-icon class="mv-dir" name="fb-spam"></sac-icon>
                <div class="mv-item-text">
                    <div class="mv-line"><span class="mv-who"></span><span class="mv-when"></span></div>
                    <div class="mv-line"><span class="mv-subject"></span></div>
                    <div class="mv-item-tags"></div>
                </div>`;
            row.querySelector(".mv-who").textContent = item.fromName || item.fromAddress || this.t("unknown-sender", "(unknown sender)");
            row.querySelector(".mv-when").textContent = this.when(item.date);
            row.querySelector(".mv-subject").textContent = item.subject || this.t("no-subject", "(no subject)");
            const tags = row.querySelector(".mv-item-tags");
            const account = this.accounts.find((a) => a.id === item.accountId);
            if (this.accounts.length > 1 && account) {
                const chip = document.createElement("sac-chip");
                chip.setAttribute("label", account.sourceTag);
                chip.setAttribute("color", fb.tags.colorFor(account.sourceTag));
                tags.appendChild(chip);
            } else tags.remove();
            row.addEventListener("click", () => this.openSpam(item));
            return row;
        });
        if (!rows.length) {
            const none = document.createElement("div");
            none.className = "mv-none";
            none.textContent = !this.spam.loaded ? this.t("spam-reading", "Reading the spam folders…")
                : this.spam.error ? fb.errors.text(this.spam.error, this.t("spam-unreadable", "Spam can't be read right now."))
                : this.spam.items.length ? this.t("no-match", "Nothing matches")
                : this.t("no-spam", "Nothing in spam");
            rows.push(none);
        }
        if (failedNames.length) {
            const failed = document.createElement("div");
            failed.className = "mv-none";
            failed.textContent = this.t("spam-failed", "Spam of {names} can't be read right now.", { names: failedNames.join(", ") });
            rows.push(failed);
        }
        box.replaceChildren(...rows);
    }

    /** A spam message as plain text — never its HTML — with "Not spam". */
    async openSpam(item) {
        await this.leaveCompose();
        const key = `${item.accountId}:${item.uid}`;
        this.selectedId = null;
        this.spamOpen = key;
        for (const row of this.querySelectorAll(".mv-item")) row.classList.toggle("selected", row.dataset.spam === key);
        const split = this.querySelector("#split");
        if (split) split.show = "end";
        const pane = this.querySelector("#mv-pane");
        // What the row knows shows at once; the text comes from the server.
        pane.innerHTML = `
            <div class="mv-thread">
                <div class="mv-thread-head"><h2></h2></div>
                <p class="mv-spam-meta"></p>
                <div class="toolbar mv-spam-actions">
                    <button type="button" class="btn primary" id="mv-not-spam"><sac-icon name="fb-inbox"></sac-icon> ${this.t("not-spam", "Not spam")}</button>
                </div>
                <pre class="mv-text"></pre>
                <p class="mv-spam-meta" id="mv-spam-atts" hidden></p>
            </div>`;
        pane.querySelector("h2").textContent = item.subject || this.t("no-subject", "(no subject)");
        const from = item.fromName ? `${item.fromName} <${item.fromAddress}>` : (item.fromAddress || "");
        const account = this.accounts.find((a) => a.id === item.accountId)?.name || "";
        pane.querySelector(".mv-spam-meta").textContent =
            [from, `${fb.format.date(new Date(item.date))} ${fb.format.time(new Date(item.date))}`, account].filter(Boolean).join(" · ");
        const button = pane.querySelector("#mv-not-spam");
        button.hidden = !this.writable;
        button.addEventListener("click", () => this.notSpam(item));
        const text = pane.querySelector(".mv-text");
        text.classList.add("is-waiting");
        text.textContent = fb.t("fb.common.loading", "Loading…");

        let m;
        try { m = await this.api.spamMessage(item.accountId, item.uid); }
        catch (err) {
            if (this.spamOpen === key && text.isConnected)
                text.textContent = fb.errors.text(err, this.t("load-failed", "The conversation can't be loaded right now."));
            return;
        }
        if (this.spamOpen !== key || !text.isConnected) return;
        text.classList.remove("is-waiting");
        this.fillText(text, m.text || "", { warn: true });
        if (m.attachments?.length) {
            const atts = pane.querySelector("#mv-spam-atts");
            atts.textContent = this.t("spam-attachments", "Attachments: {names}", { names: m.attachments.join(", ") });
            atts.hidden = false;
        }
    }

    async notSpam(item) {
        try { await this.api.notSpam(item.accountId, item.uid, item.uidValidity); }
        catch (err) { sac.toast?.(fb.errors.text(err, this.t("failed", "That didn't work.")), { kind: "error" }); return; }
        sac.toast?.(this.t("not-spam-done", "Moved to the inbox."));
        this.spam.items = this.spam.items.filter((i) => i !== item);
        this.spamOpen = null;
        if (this.view === "spam") this.renderList();
        this.showNone();
    }

    // ---------------------------------------------------------- writing --

    renderDrafts(box) {
        const shown = this.narrowed(this.drafts);
        if (!shown.length) {
            box.innerHTML = `<div class="mv-none"></div>`;
            box.firstElementChild.textContent = this.drafts.length ? this.t("no-match", "Nothing matches") : this.t("no-drafts", "No drafts");
            return;
        }
        const open = this._compose?.id;
        box.replaceChildren(...shown.map((d) => {
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
        else this.loadCounts();
    }

    async composeClosed(draft, result) {
        this._compose = null;
        if (result?.sent) {
            sac.toast?.(this.t("sent-done", "Sent."));
            if (this.view !== "list") { this.view = "list"; this.paintHead(); }
            this.loadCounts();
            await this.loadThreads();
            this.open(result.sent.threadId);
            return;
        }
        await this.loadThreads();
        if (draft.threadId && this.threads.some((x) => x.threadId === draft.threadId)) this.open(draft.threadId);
        else { this.selectedId = null; this.showNone(); }
    }

    async discardDrafts(ids) {
        const one = ids.length === 1;
        const answer = await sac.dialog.confirm({
            title: one ? this.t("discard-title", "Discard this draft?") : this.t("discard-title-n", "Discard {n} drafts?", { n: ids.length }),
            message: one ? this.t("discard-msg-1", "It's gone for good.") : this.t("discard-msg", "They're gone for good."),
            buttons: [
                { action: "cancel", label: fb.t("fb.common.cancel", "Cancel"), kind: "default" },
                { action: "discard", label: this.t("discard", "Discard"), kind: "destructive" },
            ],
        });
        if (answer !== "discard") return;
        if (ids.includes(this._compose?.id)) { this._compose = null; this.selectedId = null; this.showNone(); }
        let firstError = null;
        for (const id of ids) {
            try { await this.api.deleteDraft(id); }
            catch (err) { firstError ??= err; }
        }
        this.sel?.clear();
        if (firstError) sac.toast?.(fb.errors.text(firstError, this.t("failed", "That didn't work.")), { kind: "error" });
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
        if (!message) return this.deleteThreads([threadId]);
        const answer = await sac.dialog.confirm({
            title: this.t("delete-message-title", "Delete this message?"),
            message: this.t("delete-how", "Only in Fishbowl: your mail server keeps it. Everywhere: it goes to the server's trash too. Either way it waits in Fishbowl's trash."),
            buttons: [
                { action: "cancel", label: fb.t("fb.common.cancel", "Cancel"), kind: "default" },
                { action: "fishbowl", label: this.t("delete-here", "Only in Fishbowl"), kind: "default" },
                { action: "everywhere", label: this.t("delete-everywhere", "Everywhere"), kind: "destructive" },
            ],
        });
        if (answer !== "fishbowl" && answer !== "everywhere") return;
        try { await this.api.deleteMessage(message.id, answer); }
        catch (err) {
            sac.toast?.(fb.errors.text(err, this.t("delete-failed", "That couldn't be deleted.")), { kind: "error" });
            return;
        }
        sac.toast?.(answer === "everywhere"
            ? this.t("deleted-everywhere", "Deleted — in the trash here and on the server.")
            : this.t("deleted-here", "Deleted here — it's in the trash. Your mail server keeps it."));
        await this.loadThreads();
        if (this.threads.some((x) => x.threadId === threadId)) this.open(threadId);
        else if (this.selectedId === threadId) { this.selectedId = null; this.showNone(); }
    }

    /** Whole conversations, one or several, with one question for all. */
    async deleteThreads(ids) {
        const one = ids.length === 1;
        const answer = await sac.dialog.confirm({
            title: one ? this.t("delete-title", "Delete this conversation?") : this.t("delete-title-n", "Delete {n} conversations?", { n: ids.length }),
            message: this.t("delete-how", "Only in Fishbowl: your mail server keeps it. Everywhere: it goes to the server's trash too. Either way it waits in Fishbowl's trash."),
            buttons: [
                { action: "cancel", label: fb.t("fb.common.cancel", "Cancel"), kind: "default" },
                { action: "fishbowl", label: this.t("delete-here", "Only in Fishbowl"), kind: "default" },
                { action: "everywhere", label: this.t("delete-everywhere", "Everywhere"), kind: "destructive" },
            ],
        });
        if (answer !== "fishbowl" && answer !== "everywhere") return;
        // The rows go at once — a mail server takes seconds — and come back
        // when a server refuses (then nothing was deleted). All in one request,
        // so each server is visited once; a refresh meanwhile leaves them out.
        const gone = new Set(ids);
        this._deleting ??= new Set();
        for (const id of ids) this._deleting.add(id);
        // The open one went: the next conversation takes its place, as in any
        // mail app — the one below the last that went, else the one above.
        const order = this.threads.map((x) => x.threadId);
        const at = ids.map((id) => order.indexOf(id)).filter((i) => i >= 0);
        const next = !gone.has(this.selectedId) || !at.length ? undefined
            : order.slice(Math.max(...at) + 1).find((id) => !gone.has(id))
                ?? order.slice(0, Math.min(...at)).reverse().find((id) => !gone.has(id))
                ?? null;
        this.threads = this.threads.filter((x) => !gone.has(x.threadId));
        this.sel?.clear();
        this.renderList();
        if (next) {
            this.open(next);
            this.querySelector(`.mv-item[data-id="${CSS.escape(next)}"]`)?.scrollIntoView({ block: "nearest" });
        } else if (next === null) { this.selectedId = null; this.showNone(); }
        let error = null;
        try { await this.api.deleteThreads(ids, answer); }
        catch (err) { error = err; }
        for (const id of ids) this._deleting.delete(id);
        if (error) {
            sac.toast?.(fb.errors.text(error, this.t("delete-failed", "That couldn't be deleted.")), { kind: "error" });
            await this.loadThreads({ keep: true });
            return;
        }
        const done = ids.length;
        if (one) sac.toast?.(answer === "everywhere"
            ? this.t("deleted-everywhere", "Deleted — in the trash here and on the server.")
            : this.t("deleted-here", "Deleted here — it's in the trash. Your mail server keeps it."));
        else sac.toast?.(answer === "everywhere"
            ? this.t("deleted-n-everywhere", "{n} conversations deleted — in the trash here and on the server.", { n: done })
            : this.t("deleted-n-here", "{n} conversations deleted here — they're in the trash. Your mail server keeps them.", { n: done }));
        await this.loadThreads({ keep: true });
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
            this.fillText(pre, m.bodyText || "");
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

    openRules() {
        fb.mailRules.open({
            ws: this.ws,
            canManage: this.manager,
            accounts: this.accounts,
            onChanged: () => this.reload({ keep: true }),
        });
    }

    /** The workspace's tags — the same window as in Notes; what changed shows here at once. */
    openTags() {
        fb.tagManager.open({
            onChanged: async () => {
                await this.reload({ keep: true });
                if (this.selectedId && !this._compose && (this.view === "list" || this.view === "archived")) this.open(this.selectedId);
            },
        });
    }

    paintToolbar() {
        if (this.isConnected) fb.toolbar.set([
            ...(this.writable ? [{ id: "mv-tags", icon: "tag", title: fb.t("fb.notes.manage-tags", "Manage tags"), onClick: () => this.openTags() }] : []),
            { id: "mv-rules", icon: "fb-rule", title: this.t("rules", "Mail rules"), onClick: () => this.openRules() },
            { id: "mv-accounts", icon: "settings", title: this.t("accounts", "Mail accounts"), onClick: () => this.openAccounts() },
        ]);
    }
}

customElements.define("fb-mail-view", FbMailView);
sac.router.register("#/mail", "fb-mail-view", { label: "Mail", icon: "mail", palette: false });
