/**
 * <fb-notes-view>  (mounted at #/notes)
 *
 * iCloud-style two-pane notes UI on the kit's <sac-split>:
 *   - List pane (slot "start"): search, "All Notes" header with filter +
 *     new-note actions, rich items (title + date + snippet + pin indicator).
 *   - Editor pane (slot "end"): one frameless <sac-md-editor> filling the
 *     pane (its first line is the note's title), tag bar, footer.
 * On a wide screen both panes sit side by side and the divider resizes them.
 * Once the split is 768px or narrower (a phone) it shows one pane at a time:
 * opening a note brings the editor forward, the split's back bar returns to
 * the list. Row actions stay quiet until hover on a desktop and are always
 * shown on a touch screen (the kit's .reveal-on-hover utility).
 *
 * Pinned notes always sort first; archived are hidden unless the archive
 * button is toggled. Typing in the search input hits the hybrid ranker at
 * /api/v1/search (vector + FTS blend) — results keep server score order
 * and skip the pinned-first boost so relevance stays visible.
 *
 * Light-DOM so the kit's tokens and classes apply; all view-specific CSS is
 * scoped with `fb-notes-view` to avoid leaking into other views.
 */
class FbNotesView extends HTMLElement {
    constructor() {
        super();
        this.notes = [];
        this.selectedId = null;
        // The note the editor shows — what saves write. Not looked up by
        // selectedId: while another row loads (or asks for the key) the
        // editor still holds this one. Set by _showInEditor only.
        this._openNote = null;
        this.showArchived = false;
        // Search state. When searchActive, `this.notes` holds the ranked
        // server results (in score order). An empty input flips back to
        // browse mode and triggers a full loadNotes(). The seq counter
        // guards against out-of-order responses from rapid typing.
        this.searchActive = false;
        this.searchDegraded = false;
        this._searchDebounce = null;
        this._searchSeq = 0;
        // Tag filter is always AND ("match all"). The strip narrows itself
        // after each click to only show tags that appear on the remaining
        // notes (plus currently-selected ones), so no UI toggle is needed.
        this.tagFilter = { tags: [] };
        // Saved filters (a name + a tag expression, the workspace's — in a
        // space everyone's), the one in use per browser and workspace, and
        // the board: null = the list, else the tag namespace it groups by.
        this.filters = [];
        this.activeFilter = null;
        this.boardNs = null;
        // Clamp + "more"/"less" tab is owned by <sac-collapsible> around
        // the chip strip; expanded state lives on that element, not here.
        this._saveDebounce = null;
        this._onTagsInvalidated = () => { this._renderTagFilter(); this._refreshTagSuggestions(); };
        // Locking must take already-decrypted secrets off the page too — the
        // key going away alone would leave them in this.notes and the editor.
        // Unlocking elsewhere (the account menu) shows the open note's
        // secrets right away.
        this._onVaultChanged = (e) => {
            if (!e.detail.unlocked) this._repaintLocked();
            else this._repaintUnlocked();
        };
    }

    async connectedCallback() {
        // The workspace this view loaded: a save flushed on leaving still
        // goes there, even when the hash has already moved on.
        this.api = fb.api.notes.in(fb.api.workspace());
        this.render();
        window.addEventListener("fb-tags-invalidated", this._onTagsInvalidated);
        window.addEventListener("fb:vault-changed", this._onVaultChanged);
        // Save pending edits while the key still exists; after the lock a
        // flush of a note with a secret would have to ask for it again.
        this._unhookLock = fb.vault?.onBeforeLock?.(() => this.flushSave());
        // A space Reader gets no write actions (the server would refuse them).
        this.writable = await fb.access.canWrite();
        this.canAdmin = await fb.access.canAdmin();
        this.ws = fb.api.workspace();
        this._readPrefs();
        this.toggleAttribute("readonly", !this.writable);
        this.querySelector("#new-btn").hidden = !this.writable;
        this.querySelector("#empty-new-btn").hidden = !this.writable;
        // A note's own menu (kit 2.29): a right-click or a long press — the
        // row's actions, plus Open; for a writer "Select" comes first and
        // starts marking (how marking starts on touch).
        this._rowMenu = sac.contextMenu(this.querySelector("#note-list"), {
            targets: ".nv-item",
            selection: this.writable ? this.sel : undefined,
            items: (row) => this._rowItems(row),
        });
        this.paintHead();
        this._setViewToolbar();
        await this._loadFilters();
        await this.loadNotes();
        if (!this.isConnected) return;   // left during the load — don't hook a dead view
        this._setViewToolbar();          // the board's namespaces come from the notes
        this._renderTagFilter();
        this._refreshTagSuggestions();
        // "New note" / a note picked in the Ctrl-K palette (fb.desktop.go).
        this._onIntent = () => this._takeIntent();
        window.addEventListener("fb:intent", this._onIntent);
        this._takeIntent();
    }

    _takeIntent() {
        const it = fb.desktop?.takeIntent("notes");
        if (it?.action === "create" && this.writable !== false) this.createNote();
        if (it?.action === "open" && it.id) this.select(it.id);
    }

    /** Top-nav toolbar for this view. Per-note actions (pin/archive/delete)
     *  already live on each list row on hover, so the toolbar is reserved
     *  for view-scoped actions. Undo/redo proxy into the active editor;
     *  their disabled state is driven by the editor's history-change event
     *  via _refreshHistoryToolbar(). */
    _setViewToolbar() {
        fb.toolbar.set([
            {
                icon:     "undo",
                title:    fb.t("fb.notes.undo", "Undo (Ctrl+Z)"),
                onClick:  () => this._editorUndo(),
                disabled: true,
            },
            {
                icon:     "redo",
                title:    fb.t("fb.notes.redo", "Redo (Ctrl+Y)"),
                onClick:  () => this._editorRedo(),
                disabled: true,
            },
            // Manual refresh - placeholder until we wire auto-refresh to
            // MCP writes landing. Order is left-to-right as rendered.
            {
                icon:    "sync",
                title:   fb.t("fb.notes.refresh", "Refresh notes"),
                onClick: () => this.loadNotes(),
            },
            // List or board, grouped by a tag namespace (status:, kind: …).
            {
                id:    "nv-view-menu",
                icon:  this.boardNs ? "fb-board" : "note",
                title: fb.t("fb.notes.view", "View: list or board"),
                menu: [
                    { action: "list", label: fb.t("fb.notes.view-list", "List"), icon: "note", checked: !this.boardNs },
                    ...this._boardNamespaces().map((ns) => ({
                        action: "board:" + ns,
                        label: fb.t("fb.notes.view-board", "Board by {ns}", { ns }),
                        icon: "fb-board",
                        checked: this.boardNs === ns,
                    })),
                ],
                onSelect: (action) => this._setView(action === "list" ? null : action.slice(6)),
            },
            ...(this.writable === false ? [] : [{
                icon:    "settings",
                title:   fb.t("fb.notes.manage-tags", "Manage tags"),
                onClick: () => this._openManageDialog(),
            }]),
        ]);
    }

    _editorUndo() {
        const editor = this.querySelector("#content");
        editor?.undo?.();
    }

    _editorRedo() {
        const editor = this.querySelector("#content");
        editor?.redo?.();
    }

    /** Flip the undo/redo toolbar buttons' disabled state from the editor's
     *  latest history-change detail. Mutates the items in place then asks
     *  the nav to re-render - fb.toolbar.set() would work too but re-renders
     *  every item; this keeps the event cadence cheap on every keystroke. */
    _refreshHistoryToolbar(detail) {
        const items = fb.toolbar?._items;
        if (!items || items.length < 2) return;
        if (items[0]?.icon === "undo") items[0].disabled = !detail.canUndo;
        if (items[1]?.icon === "redo") items[1].disabled = !detail.canRedo;
        fb.toolbar._nav?.renderToolbar();
    }

    disconnectedCallback() {
        if (this._onIntent) window.removeEventListener("fb:intent", this._onIntent);
        this.sel?.destroy();
        this._rowMenu?.destroy();
        // Router already clears on swap, but guard against any other unmount.
        // Fire-and-forget any pending autosave so a quick view-switch mid-typing
        // doesn't drop edits.
        this.flushSave();
        window.removeEventListener("fb-tags-invalidated", this._onTagsInvalidated);
        window.removeEventListener("fb:vault-changed", this._onVaultChanged);
        this._unhookLock?.();
        if (window.fb?.toolbar) fb.toolbar.clear();
    }

    async loadNotes({ prompt = true } = {}) {
        this._linkPool = null;
        try {
            const opts = this.tagFilter.tags.length > 0
                ? { tags: this.tagFilter.tags, match: "all" }
                : undefined;
            this.notes = await this.api.list(opts, { prompt });
            this.renderList();
        } catch (err) {
            console.error("[fb-notes-view] list failed:", err);
        }
    }

    /** Hybrid search path — calls /api/v1/search (vector+FTS blend) and
     *  parks the ranked results in `this.notes`. The server already strips
     *  archived and secret content, so the only client-side filtering in
     *  search mode is the tag chip strip (the server doesn't take tags).
     *  `degraded: true` means the embedding model wasn't ready and ranking
     *  fell back to FTS-only — surfaced as a muted hint under the input.
     *
     *  Relevance cutoff: min-max normalisation on the server spans [0, 1]
     *  every query, even when nothing really matches, so "best of the
     *  garbage" still gets 1.0. Pure-vec noise (notes with no FTS overlap
     *  but coincidentally close embeddings) routinely lands at 0.6–0.7,
     *  which is why the threshold has to be aggressive. We trim anything
     *  below max(top*0.7, 0.5) — keeps the top hit always, kills the
     *  long tail of "somewhat vaguely related via the embedding space"
     *  that clutters results for short or lexical queries. */
    async _doSearch(q) {
        const seq = ++this._searchSeq;
        try {
            const res = await fb.api.search.query(q, { limit: 20, includePending: true });
            // A faster keystroke already bumped seq — drop this stale result.
            if (seq !== this._searchSeq) return;
            const all = res.notes || [];
            const kept = this._applyRelevanceCutoff(all);
            this.searchActive = true;
            this.searchDegraded = !!res.degraded;
            // A hit is not the note: its secrets are a placeholder and it
            // has no ciphertext, so writing it back would replace them for
            // good. Marked `_hit`, it is loaded before it is opened or
            // changed (select, _noteFor); the open note stays the editor's.
            const open = this._openNote;
            this.notes = kept.map(h => open && !open._hit && h.id === open.id ? open : Object.assign(h, { _hit: true }));
            this._updateDegradedHint();
            this.renderList();
            // Narrow the chip strip to tags that actually appear on the
            // kept results — otherwise it stays showing every tag in the
            // DB, which is noisy and misleading during a tight search.
            this._renderTagFilter();
        } catch (err) {
            console.error("[fb-notes-view] search failed:", err);
        }
    }

    /** Trim the ranked results to just the ones that actually look relevant.
     *  Two filters stacked:
     *    1) A note where both title and content came back empty is a
     *       secret-only shell (SecretStripper gutted it) — it still
     *       matched FTS on the pre-strip text but is useless to display.
     *    2) A note with every word of the query (`words`) always stays —
     *       the server ranks those first. One found by meaning alone must
     *       clear max(topMeaning * 0.7, 0.5): relative-to-top handles the
     *       "top is 0.7, rank 2 is 0.43" semantic case; the 0.5 floor
     *       catches "top is 0.5, everything below is noise". */
    _applyRelevanceCutoff(results) {
        const nonEmpty = results.filter(n =>
            (n.title && n.title.trim()) || (n.content && n.content.trim())
        );
        if (nonEmpty.length === 0) return [];
        const top = nonEmpty.find(n => !n.words)?.score ?? 0;
        const cutoff = Math.max(top * 0.7, 0.5);
        return nonEmpty.filter(n => n.words || (n.score ?? 0) >= cutoff);
    }

    _updateDegradedHint() {
        const hint = this.querySelector("#search-degraded-hint");
        if (!hint) return;
        hint.hidden = !(this.searchActive && this.searchDegraded);
    }

    render() {
        this.innerHTML = `
            <style>
                /* The view fills the space below the fixed nav; the split
                   inside takes it over (sac-split sizes to its parent). */
                fb-notes-view {
                    display: block;
                    height: calc(100vh - 50px);
                    height: calc(100dvh - 50px - env(safe-area-inset-top, 0px));
                    background: var(--bg);
                }
                /* [hidden] must beat our scoped display rules */
                fb-notes-view [hidden] { display: none !important; }

                /* --- LIST PANE ------------------------------------------------- */
                fb-notes-view .nv-list-pane {
                    /* Exactly the pane's height: the search and the header stay, only the list scrolls. */
                    height: 100%;
                    background: var(--panel);
                    display: flex;
                    flex-direction: column;
                }
                fb-notes-view .nv-search {
                    position: relative;
                    padding: 12px 12px 0;
                }
                fb-notes-view .nv-search sac-icon {
                    position: absolute;
                    left: 22px;
                    top: 12px;
                    bottom: 0;
                    margin: auto 0;     /* centre the fixed-height icon */
                    display: flex;
                    align-items: center;
                    color: var(--text-muted);
                    --icon-size: 14px;
                    pointer-events: none;
                }
                /* Field look, touch height and the 16px-on-touch type come
                   from the kit's global input rule; only the icon inset and
                   the compact desktop size are ours. */
                fb-notes-view .nv-search input {
                    padding-left: 32px;
                    font-size: max(13px, 0.8125rem);
                    outline: none;
                }
                @media (pointer: coarse) {
                    fb-notes-view .nv-search input { font-size: max(16px, 1rem); }
                }
                fb-notes-view .nv-search input::placeholder { color: var(--text-muted); }
                fb-notes-view .nv-search-hint {
                    padding: 6px 12px 0;
                    font-size: 11px;
                    color: var(--text-muted);
                    font-style: italic;
                }
                fb-notes-view .nv-search-hint[hidden] { display: none !important; }

                /* One 12px gutter for the whole list pane: the search field,
                   the header's buttons and the (highlighted) rows all end
                   12px from the edge; the header label lines up with the
                   rows' text (12px gutter + 12px row padding). */
                fb-notes-view .nv-list-header {
                    display: flex;
                    align-items: center;
                    padding: 12px 12px 6px 24px;
                    gap: 2px;
                }
                fb-notes-view .nv-list-title {
                    flex: 1;
                    font-family: 'Outfit', sans-serif;
                    font-weight: 700;
                    font-size: 11px;
                    text-transform: uppercase;
                    letter-spacing: 0.1em;
                    color: var(--text-muted);
                }
                /* Header buttons are the kit's .icon-btn (44px hit area on
                   touch); the archive toggle's "on" state is ours. */
                fb-notes-view .nv-list-header .icon-btn { color: var(--text-muted); }
                fb-notes-view .nv-list-header .icon-btn.active {
                    background: color-mix(in srgb, var(--accent-warm) 15%, transparent);
                    color: var(--accent-warm);
                }

                fb-notes-view .nv-items {
                    flex: 1;
                    min-height: 0;
                    overflow-y: auto;
                    padding: 2px 12px 12px;
                }

                fb-notes-view .nv-item {
                    position: relative;
                    padding: 10px 12px;
                    border-radius: var(--radius-m);
                    cursor: pointer;
                    margin-bottom: 2px;
                    border: 1px solid transparent;
                    transition: background 0.12s, border-color 0.12s;
                }
                fb-notes-view .nv-item:hover { background: var(--hover); }
                fb-notes-view #note-list:focus { outline: none; }
                fb-notes-view .nv-item.selected {
                    background: var(--accent-tint);
                    border-color: color-mix(in srgb, var(--accent) 28%, transparent);
                }
                fb-notes-view .nv-item-title-row {
                    display: flex;
                    align-items: center;
                    gap: 6px;
                    margin-bottom: 4px;
                    /* Touch: the actions are always shown, so reserve their
                       row (3 × 22px + gaps) and they never sit on the title. */
                    padding-right: 72px;
                }
                /* A note awaiting review has a fourth button (approve). */
                fb-notes-view .nv-item.pending .nv-item-title-row { padding-right: 96px; }
                /* Mouse: nothing is reserved — the title runs the full width,
                   and on hover / focus the actions lay over the row's right
                   end on a fade of the row's own colour. At rest only a
                   persistent indicator (active pin / archive, approve) shows,
                   on the same fade. */
                @media (hover: hover) and (pointer: fine) {
                    fb-notes-view .nv-item-title-row,
                    fb-notes-view .nv-item.pending .nv-item-title-row { padding-right: 0; }
                    fb-notes-view .nv-item {
                        --nv-row-bg: var(--panel);
                    }
                    fb-notes-view .nv-item:hover { --nv-row-bg: var(--hover); }
                    fb-notes-view .nv-item.selected { --nv-row-bg: var(--accent-tint); }
                    fb-notes-view .nv-item-action { --icon-btn-size: 26px; --icon-btn-icon: 14px; }
                    fb-notes-view .nv-item-actions {
                        /* The backing reaches below the title's descenders. */
                        top: 2px;
                        right: 4px;
                        gap: 0;
                        padding: 2px 0 6px 24px;
                        border-radius: var(--radius-m);
                        /* A tint (hover / selection) is translucent: lay it
                           over the pane colour so the fade hides the title. */
                        background:
                            linear-gradient(to right, transparent, var(--nv-row-bg) 20px),
                            linear-gradient(to right, transparent, var(--panel) 20px);
                    }
                    fb-notes-view .nv-item:not(:hover):not(:focus-within) .nv-item-action:not(.active):not(.approve) { display: none; }
                    fb-notes-view .nv-item:not(:hover):not(:focus-within) .nv-item-actions:not(:has(.active, .approve)) { background: none; }
                }
                fb-notes-view .nv-item-title {
                    font-weight: 600;
                    font-size: 14px;
                    overflow: hidden;
                    text-overflow: ellipsis;
                    white-space: nowrap;
                    color: var(--text);
                    flex: 1;
                }

                /* Action row: pin (persistent when active), archive, delete.
                   The kit's .reveal-on-hover / .hover-reveal pair keeps them
                   quiet until the row is hovered or focused — and always
                   shown on a touch screen, which has no hover. An active pin
                   or archive overrides that so the indicator never hides. */
                fb-notes-view .nv-item-actions {
                    position: absolute;
                    top: 6px;
                    right: 6px;
                    display: flex;
                    gap: 1px;
                }
                fb-notes-view .nv-item-action {
                    --icon-btn-size: 22px;
                    --icon-btn-icon: 13px;
                    color: var(--text-muted);
                }
                fb-notes-view .nv-item-action:hover { color: var(--text); }
                fb-notes-view .nv-item-action.pin.active {
                    opacity: 1;
                    color: var(--accent-warm);
                }
                /* Archive indicator: same persistent-icon treatment as pin,
                   in muted grey so it reads as "backgrounded / shelved"
                   instead of "highlighted". */
                fb-notes-view .nv-item-action.archive.active {
                    opacity: 1;
                    color: var(--text-muted);
                }
                fb-notes-view .nv-item-action.delete:hover { color: var(--danger); }
                /* Approve: only rendered when the note carries review:pending.
                   Green hover so it reads as the positive counterpart to the
                   (red) delete button — same row, opposite semantics. */
                fb-notes-view .nv-item-action.approve {
                    opacity: 1;
                    color: var(--accent-warm, #f59e0b);
                }
                fb-notes-view .nv-item-action.approve:hover {
                    color: var(--ok-text);
                    background: color-mix(in srgb, var(--ok) 14%, transparent);
                }

                /* Archived rows (only shown when "show archived" is on) get
                   dimmed title + snippet so they're visually distinct from
                   active notes at a glance. Date and the persistent archive
                   icon are left at normal opacity to stay scannable. */
                fb-notes-view .nv-item.archived .nv-item-title,
                fb-notes-view .nv-item.archived .nv-item-snippet {
                    opacity: 0.55;
                }
                fb-notes-view .nv-item.archived .nv-item-title {
                    font-style: italic;
                }
                fb-notes-view .nv-item-preview {
                    display: flex;
                    gap: 6px;
                    font-size: 12px;
                    line-height: 1.4;
                    color: var(--text-muted);
                    align-items: baseline;
                }
                fb-notes-view .nv-item-date {
                    flex-shrink: 0;
                    font-variant-numeric: tabular-nums;
                }
                fb-notes-view .nv-item-snippet {
                    overflow: hidden;
                    text-overflow: ellipsis;
                    white-space: nowrap;
                    flex: 1;
                }
                fb-notes-view .nv-empty-list {
                    padding: 10px 12px;
                    color: var(--text-muted);
                    font-size: 13px;
                }

                /* Per-row tag preview: single line, ellipsis-truncated, full
                   list shown via title tooltip on hover. Coloured dots act as
                   tag-aware glyphs without taking the space full chips would. */
                fb-notes-view .nv-item-tagline {
                    display: flex;
                    align-items: center;
                    gap: 4px;
                    margin-top: 4px;
                    font-size: 11px;
                    color: var(--text-muted);
                    overflow: hidden;
                    white-space: nowrap;
                    text-overflow: ellipsis;
                    cursor: default;
                }
                fb-notes-view .nv-item-tagline .tag-dot {
                    flex-shrink: 0;
                    width: 7px; height: 7px;
                    border-radius: 50%;
                }
                fb-notes-view .nv-item-tagline .tag-name {
                    overflow: hidden;
                    text-overflow: ellipsis;
                }
                fb-notes-view .nv-item-tagline .tag-sep {
                    opacity: 0.4;
                    margin: 0 2px;
                }
                fb-notes-view .nv-item.archived .nv-item-tagline { opacity: 0.55; }

                /* --- EDITOR PANE ---------------------------------------------- */
                fb-notes-view .nv-editor-pane {
                    height: 100%;
                    display: flex;
                    flex-direction: column;
                    min-width: 0;
                }
                /* Collapsed (phone): the split's pane scrolls under its
                   sticky back bar, so the editor flows at its natural
                   height — note, tag bar and footer scroll as one page. */
                fb-notes-view sac-split[collapsed] .nv-editor-pane {
                    height: auto;
                    min-height: 100%;
                }
                fb-notes-view sac-split[collapsed] .nv-editor-body {
                    flex: 1 0 auto;
                    overflow: visible;
                }
                /* The editor's formatting toolbar is sticky (sac-md-editor
                   v1.1.0). Collapsed, the note scrolls under the split's
                   sticky back bar, so the toolbar sticks just below it — at
                   the bar's height in the kit's numbers (sac-split: a 36px
                   button, 44px on touch, 0.35rem above and below, a 1px
                   line). Guarded by Phone_LongNote_EditorToolbarStaysBelowTheBackBar_Test. */
                fb-notes-view sac-split[collapsed] sac-md-editor::part(toolbar) {
                    top: calc(36px + 0.7rem + 1px);
                }
                @media (pointer: coarse) {
                    fb-notes-view sac-split[collapsed] sac-md-editor::part(toolbar) {
                        top: calc(44px + 0.7rem + 1px);
                    }
                }
                fb-notes-view .nv-editor-footer {
                    display: flex;
                    align-items: center;
                    gap: 12px;
                    min-height: 32px;
                    padding: 8px clamp(1rem, 4vw, 20px);
                    padding-bottom: calc(8px + env(safe-area-inset-bottom, 0px));
                    border-top: 1px solid var(--border);
                    background: var(--panel);
                    font-size: 11px;
                    color: var(--text-muted);
                    flex-shrink: 0;
                }
                fb-notes-view .nv-editor-footer-spacer { flex: 1; }
                fb-notes-view .nv-editor-footer #timestamp {
                    font-variant-numeric: tabular-nums;
                }
                /* Dedicated band between body and footer for the tag input.
                   Lives above the footer so the autocomplete dropdown opens
                   downward into the footer's visual whitespace instead of
                   covering editor content. */
                fb-notes-view .nv-editor-tagbar {
                    display: flex;
                    align-items: center;
                    flex-wrap: wrap;
                    gap: 6px;
                    padding: 10px clamp(1rem, 4vw, 20px);
                    background: var(--panel);
                    border-top: 1px solid var(--border);
                    flex-shrink: 0;
                    min-height: 44px;
                }
                fb-notes-view .nv-editor-tagbar sac-chip-input {
                    flex: 1 1 200px;
                    min-width: min(200px, 100%);
                }

                /* --- TAG FILTER STRIP (list pane) ----------------------------- */
                /* Just the chip row layout — clamp, fade, separator line
                 * and the "more"/"less" tab all come from <sac-collapsible>. */
                fb-notes-view .nv-tag-filter {
                    display: flex;
                    flex-wrap: wrap;
                    align-items: center;
                    gap: 4px;
                    padding: 10px 12px 8px;
                }
                fb-notes-view .nv-tag-collapsible[hidden] { display: none !important; }
                /* Phone: one line that scrolls sideways — the collapsible then
                   never overflows, so no "more" tab. */
                @media (max-width: 768px) {
                    fb-notes-view .nv-tag-filter {
                        flex-wrap: nowrap;
                        overflow-x: auto;
                        scrollbar-width: none;
                    }
                    fb-notes-view .nv-tag-filter::-webkit-scrollbar { display: none; }
                    fb-notes-view .nv-tag-filter > * { flex: none; }
                }
                fb-notes-view .nv-editor-body {
                    flex: 1;
                    overflow: auto;
                    /* No padding: the editor runs edge to edge and brings
                       its own text inset. */
                    display: flex;
                    flex-direction: column;
                }
                /* Nothing open: the kit's .empty-state, centred in the pane. */
                fb-notes-view .nv-empty { flex: 1; justify-content: center; }
                fb-notes-view .nv-empty[hidden] { display: none; }
                /* The editor IS the pane: no card, no rounded corners, no
                   focus ring — a sheet of paper, not a form field. These
                   beat the component's own :host frame (outer-document
                   declarations win over :host ones), so the vendored file
                   stays untouched. It grows with the pane and with its
                   content; .nv-editor-body does the scrolling. */
                fb-notes-view .nv-content-input {
                    display: block;
                    flex: 1 0 auto;
                    width: 100%;
                    background: transparent;
                    border: 0;
                    border-radius: 0;
                    box-shadow: none;
                    transition: none;
                }

                /* Small pill in the editor footer when viewing an archived
                   note, so the read-only state is explained, not just felt. */
                fb-notes-view .nv-archived-pill {
                    display: inline-flex;
                    align-items: center;
                    gap: 4px;
                    padding: 2px 8px;
                    border-radius: var(--radius-m);
                    background: var(--hover);
                    border: 1px solid var(--border);
                    color: var(--text-muted);
                    font-size: 10px;
                    font-weight: 600;
                    text-transform: uppercase;
                    letter-spacing: 0.08em;
                }
                fb-notes-view .nv-archived-pill sac-icon { --icon-size: 10px; }
                fb-notes-view .nv-locked-pill { cursor: pointer; font: inherit; font-size: 10px; font-weight: 600; }
                fb-notes-view .nv-locked-pill:hover { color: var(--text); border-color: var(--accent); }
                fb-notes-view .nv-locked-pill[hidden] { display: none; }

                fb-notes-view .nv-editor {
                    /* Fills the body at least, grows past it with the note —
                       then .nv-editor-body scrolls, never the editor itself. */
                    display: flex;
                    flex-direction: column;
                    flex: 1 0 auto;
                }
                fb-notes-view .nv-editor[hidden] { display: none; }

                /* A saved filter in use: its funnel wears the accent. */
                fb-notes-view #filters-btn.active { color: var(--accent); }
                fb-notes-view .nv-slug { font-size: 11px; color: var(--text-muted); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; max-width: 30%; }
                fb-notes-view .nv-slug[hidden], fb-notes-view .nv-links[hidden] { display: none; }
                fb-notes-view .nv-history-btn { width: auto; flex: none; gap: 6px; font-size: 12px; }
                fb-notes-view .nv-history-btn sac-icon { --icon-size: 14px; }

                /* The note's links and the notes that link to it (fb.noteLinks). */
                fb-notes-view .nv-links {
                    display: flex;
                    flex-direction: column;
                    gap: 6px;
                    padding: 10px clamp(1rem, 4vw, 20px);
                    border-top: 1px solid var(--border);
                    background: var(--panel);
                    flex-shrink: 0;
                }
                fb-notes-view .nv-links-group { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; }
                fb-notes-view .nv-links-group[hidden] { display: none; }
                fb-notes-view .nv-links-label {
                    font-size: 11px; font-weight: 600; text-transform: uppercase; letter-spacing: 0.08em;
                    color: var(--text-muted); margin-right: 4px;
                }
                fb-notes-view .nv-link-missing { font-size: 12px; color: var(--text-muted); text-decoration: line-through dotted; }

                /* The board (fb.notesBoard) in place of the editor: columns
                   side by side, scrolling sideways; each column scrolls its cards. */
                fb-notes-view .nv-board {
                    flex: 1;
                    min-height: 0;
                    display: flex;
                    align-items: stretch;
                    gap: 12px;
                    padding: 16px 24px;
                    overflow-x: auto;
                }
                fb-notes-view .nv-board[hidden] { display: none; }
                fb-notes-view .nb-col {
                    flex: 0 0 260px;
                    display: flex;
                    flex-direction: column;
                    min-height: 0;
                    border: 1px solid var(--border);
                    border-radius: var(--radius-l);
                    background: var(--panel);
                }
                fb-notes-view .nb-col.drop { border-color: var(--accent); background: var(--accent-tint); }
                fb-notes-view .nb-col-head {
                    display: flex; align-items: center; gap: 8px;
                    padding: 10px 12px;
                    font-size: 11px; font-weight: 700; text-transform: uppercase; letter-spacing: 0.08em;
                    color: var(--text-muted);
                    border-bottom: 1px solid var(--border);
                }
                fb-notes-view .nb-col-name { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; }
                fb-notes-view .nb-cards { flex: 1; min-height: 60px; overflow-y: auto; padding: 8px; display: flex; flex-direction: column; gap: 8px; }
                fb-notes-view .nb-card {
                    padding: 10px 12px;
                    border: 1px solid var(--border);
                    border-radius: var(--radius-m);
                    background: var(--field);
                    cursor: pointer;
                }
                fb-notes-view .nb-card:hover { border-color: var(--border-strong); background: var(--hover); }
                fb-notes-view .nb-card:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; }
                fb-notes-view .nb-card.dragging { opacity: 0.5; }
                fb-notes-view .nb-card.pending { border-left: 3px solid var(--accent-warm); }
                fb-notes-view .nb-card-title { font-weight: 600; font-size: 13px; overflow-wrap: anywhere; }
                fb-notes-view .nb-card-tags { display: flex; flex-wrap: wrap; gap: 4px; margin-top: 6px; }
            </style>

            <sac-split class="nv-split" id="split" collapse show="start"
                       position="${this._splitPosition()}" min-start="260px" min-end="360px"
                       aria-label="${fb.t("fb.notes.resize", "Resize the note list")}">
                <aside class="nv-list-pane" slot="start">
                    <div class="nv-search">
                        <sac-icon name="search"></sac-icon>
                        <input type="search" id="search-input" placeholder="${fb.t("fb.notes.search", "Search all notes")}"/>
                    </div>
                    <div class="nv-search-hint" id="search-degraded-hint" hidden>
                        ${fb.t("fb.notes.search-degraded", "Full-text ranking — embeddings still loading.")}
                    </div>
                    <sac-collapsible class="nv-tag-collapsible" id="tag-filter-wrapper" max-height="82px" hidden>
                        <div class="nv-tag-filter" id="tag-filter"></div>
                    </sac-collapsible>
                    <div class="nv-list-header">
                        <span class="nv-list-title" id="list-title">${fb.t("fb.notes.all", "All Notes")}</span>
                        <button type="button" class="icon-btn" id="approve-marked-btn" hidden title="${fb.t("fb.notes.approve-marked", "Approve selected")}" aria-label="${fb.t("fb.notes.approve-marked", "Approve selected")}"><sac-icon name="check"></sac-icon></button>
                        <button type="button" class="icon-btn danger" id="delete-marked-btn" hidden title="${fb.t("fb.notes.delete-marked", "Delete selected")}" aria-label="${fb.t("fb.notes.delete-marked", "Delete selected")}"><sac-icon name="trash"></sac-icon></button>
                        <button type="button" class="icon-btn" id="clear-marks-btn" hidden title="${fb.t("fb.notes.clear-marks", "Clear selection")}" aria-label="${fb.t("fb.notes.clear-marks", "Clear selection")}"><sac-icon name="close"></sac-icon></button>
                        <sac-menu id="filters-menu">
                            <button slot="trigger" type="button" class="icon-btn" id="filters-btn" title="${fb.t("fb.notes.filters", "Saved filters")}" aria-label="${fb.t("fb.notes.filters", "Saved filters")}"><sac-icon name="fb-filter"></sac-icon></button>
                        </sac-menu>
                        <button class="icon-btn" id="toggle-archived-btn" title="${fb.t("fb.notes.show-archived", "Show archived")}" aria-label="${fb.t("fb.notes.show-archived", "Show archived")}">
                            <sac-icon name="archive"></sac-icon>
                        </button>
                        <button class="icon-btn" id="new-btn" title="${fb.t("fb.notes.new", "New note")}" aria-label="${fb.t("fb.notes.new", "New note")}">
                            <sac-icon name="plus"></sac-icon>
                        </button>
                    </div>
                    <div class="nv-items fb-scroll-fade" id="note-list"></div>
                </aside>

                <main class="nv-editor-pane" slot="end">
                    <div class="nv-board" id="board" hidden></div>
                    <div class="nv-editor-body">
                        <div class="empty-state nv-empty" id="editor-empty">
                            <sac-icon name="note"></sac-icon>
                            <h3>${fb.t("fb.notes.none-open", "No note open")}</h3>
                            <p>${fb.t("fb.notes.none-open-hint", "Pick one from the list, or start a new one.")}</p>
                            <button type="button" class="btn primary" id="empty-new-btn"><sac-icon name="plus"></sac-icon> ${fb.t("fb.notes.new", "New note")}</button>
                        </div>
                        <div id="editor" class="nv-editor" hidden>
                            <sac-md-editor id="content" class="nv-content-input" links="host" placeholder="${fb.t("fb.notes.placeholder", "Start writing — the first line is the title")}"></sac-md-editor>
                        </div>
                    </div>
                    <section class="nv-links" id="links" hidden>
                        <div class="nv-links-group" id="links-out"><span class="nv-links-label">${fb.t("fb.notes.links", "Links")}</span></div>
                        <div class="nv-links-group" id="links-in"><span class="nv-links-label">${fb.t("fb.notes.linked-from", "Linked from")}</span></div>
                    </section>
                    <section class="nv-editor-tagbar" id="tagbar" hidden>
                        <sac-chip-input id="tag-input" add-label="${fb.t("fb.notes.add-tag", "Add tag")}" allow-create></sac-chip-input>
                    </section>
                    <footer class="nv-editor-footer" id="editor-footer" hidden>
                        <span class="nv-editor-footer-meta">
                            ${fb.t("fb.notes.updated", "Updated")} <span id="timestamp"></span>
                        </span>
                        <code class="nv-slug" id="slug" hidden title="${fb.t("fb.notes.slug", "The note's stable key")}"></code>
                        <span class="nv-archived-pill" id="note-locked-pill" hidden title="${fb.t("fb.notes.locked-title", "Locked: the title and text don't change; text can only be added at the end")}">
                            <sac-icon name="lock"></sac-icon> ${fb.t("fb.notes.locked-note", "Locked")}
                        </span>
                        <span class="nv-archived-pill" id="archived-pill" hidden>
                            <sac-icon name="archive"></sac-icon> ${fb.t("fb.notes.archived-ro", "Archived · Read-only")}
                        </span>
                        <button type="button" class="nv-archived-pill nv-locked-pill" id="locked-pill" hidden
                                title="${fb.t("fb.notes.unlock-title", "Unlock to read and edit this note's secrets")}">
                            <sac-icon name="lock"></sac-icon> ${fb.t("fb.notes.locked-pill", "Secrets locked · Unlock")}
                        </button>
                        <div class="nv-editor-footer-spacer"></div>
                        <button type="button" class="icon-btn tool" id="back-to-board-btn" hidden title="${fb.t("fb.notes.back-to-board", "Back to the board")}" aria-label="${fb.t("fb.notes.back-to-board", "Back to the board")}"><sac-icon name="fb-board"></sac-icon></button>
                        <button type="button" class="btn nv-history-btn" id="history-btn" hidden><sac-icon name="clock"></sac-icon><span></span></button>
                        <button type="button" class="icon-btn tool" id="lock-btn" hidden><sac-icon name="lock"></sac-icon></button>
                    </footer>
                </main>
            </sac-split>
        `;
        this.attachHandlers();
    }

    /** The divider position the user left last time (per browser), else the
     *  default. A convenience only — storage may be unavailable. */
    _splitPosition() {
        try { return localStorage.getItem("fb.notes.split") || "28%"; }
        catch { return "28%"; }
    }

    attachHandlers() {
        const split = this.querySelector("#split");
        split.addEventListener("sac:resize", (e) => {
            try { localStorage.setItem("fb.notes.split", e.detail.position); } catch { /* ignore */ }
        });
        // Collapsed (phone): the back bar returns to the list. Save on the
        // way out — the editor stays mounted, so nothing is lost either way.
        split.addEventListener("sac:split-back", () => this.flushSave());

        // Marking several notes: the kit's gestures and keys (kit 2.30), kept
        // by id across re-renders; an arrow opens the next note, Shift marks,
        // Delete asks for the marked ones (else the one open).
        const list = this.querySelector("#note-list");
        this.sel = sac.selection.attach(list, {
            rows: ".nv-item",
            current: () => this.selectedId,
            disabled: () => !this.writable,
            onChange: () => this.paintHead(),
            keyboard: true,
            onCursor: (id) => this.select(id),
        });
        list.addEventListener("sac:request-remove", (e) => this.removeMany(e.detail.ids));
        this.querySelector("#delete-marked-btn").addEventListener("click", () => this.removeMany(this.sel.marked));
        this.querySelector("#approve-marked-btn").addEventListener("click", () => this.approveMany(this.sel.marked));
        this.querySelector("#clear-marks-btn").addEventListener("click", () => this.sel.clear());
        this.querySelector("#new-btn").addEventListener("click", () => this.createNote());
        this.querySelector("#empty-new-btn").addEventListener("click", () => this.createNote());
        this.querySelector("#toggle-archived-btn").addEventListener("click", () => {
            this.showArchived = !this.showArchived;
            this.querySelector("#toggle-archived-btn").classList.toggle("active", this.showArchived);
            this.paintHead();
            this.renderList();
        });
        this.querySelector("#search-input").addEventListener("input", (e) => {
            const q = e.target.value.trim();
            clearTimeout(this._searchDebounce);
            if (q.length === 0) {
                // Cancel any in-flight search and return to browse mode.
                this._searchSeq++;
                if (this.searchActive) {
                    this.searchActive = false;
                    this.searchDegraded = false;
                    this._updateDegradedHint();
                    this.loadNotes().then(() => this._renderTagFilter());
                }
                return;
            }
            // 250ms debounce is long enough to absorb fast typing without
            // masking the perceived latency of the server hop.
            this._searchDebounce = setTimeout(() => this._doSearch(q), 250);
        });
        // ↑/↓ in the search field go on in the list, from the open note.
        this.querySelector("#search-input").addEventListener("keydown", (e) => {
            if (!["ArrowDown", "ArrowUp", "PageDown", "PageUp"].includes(e.key) || e.altKey || e.ctrlKey || e.metaKey || e.isComposing) return;
            e.preventDefault();
            list.focus({ preventScroll: true });
            list.dispatchEvent(new KeyboardEvent("keydown", { key: e.key, shiftKey: e.shiftKey, bubbles: true, cancelable: true }));
        });
        this.querySelector("#locked-pill").addEventListener("click", () => this._unlockOpenNote());
        const contentEl = this.querySelector("#content");
        // sac-md-editor re-dispatches `input` on every keystroke and `change`
        // on blur-after-edit from its host, matching native textarea
        // semantics. Autosize is handled inside the component.
        contentEl.addEventListener("change", () => this.flushSave());
        contentEl.addEventListener("input",  () => this.scheduleAutoSave());
        contentEl.addEventListener("history-change", (e) => this._refreshHistoryToolbar(e.detail));
        const tagInput = this.querySelector("#tag-input");
        tagInput.addEventListener("sac:change", () => {
            // Don't mutate note.tags here — saveSelected compares the
            // current input value against the stored note to decide whether
            // the PUT is needed. Pre-mutating would always produce "no
            // change" and silently drop every tag edit.
            if (this._openNote) this.scheduleAutoSave();
        });
        // A tag created in the input (name + colour from its picker) is
        // persisted right away, so the colour sticks before the note saves.
        tagInput.addEventListener("sac:create", async (e) => {
            try {
                await fb.api.tags.upsertColor(e.detail.name, e.detail.color);
                fb.tags.invalidate();
            } catch (err) {
                console.error("[fb-notes-view] creating tag failed:", err);
            }
        });
        // Pin, archive and delete live on each list row (see renderList's
        // action buttons).

        this.querySelector("#filters-menu").addEventListener("sac:select", (e) => this._onFilterMenu(e.detail.action));
        this.querySelector("#lock-btn").addEventListener("click", () => this._toggleLock());
        this.querySelector("#history-btn").addEventListener("click", () => this._openHistory());
        this.querySelector("#back-to-board-btn").addEventListener("click", () => this._showBoard());
        // A link to another note (fb.noteLinks): its chip under the text, or
        // the link in the text — the editor's sac:link, on Ctrl/⌘+click while
        // editing and on a click in a read-only note.
        this.querySelector("#links").addEventListener("click", (e) => {
            const chip = e.target.closest("sac-chip[data-note]");
            if (chip) this.select(chip.dataset.note);
        });
        contentEl.addEventListener("sac:link", (e) => this._followLink(e));
    }

    /** Suggestions for the tag input: every tag the user may assign, most
     *  used first. System tags that only the server sets (source:mcp) are
     *  left out — the input would offer something the user can't add. */
    async _refreshTagSuggestions() {
        const input = this.querySelector("#tag-input");
        if (!input) return;
        try {
            const tags = await fb.tags.all();
            input.suggestions = tags
                .filter(t => t.userAssignable !== false)
                .map(t => ({ name: t.name, color: t.color, count: t.usageCount }));
        } catch (err) {
            console.warn("[fb-notes-view] tag suggestions unavailable:", err);
        }
    }

    /** Render the per-tag chip strip. AND-only filter; the strip narrows
     *  itself after each click to only show tags that still appear on at
     *  least one of the currently-visible notes (so every chip is a
     *  guaranteed-non-empty further filter). Currently-selected chips stay
     *  visible regardless so the user can always deselect.
     *
     *  Chips are sorted by usage frequency (descending) so the tags the
     *  user touches most live at the top. The strip is clamped to ~3 rows
     *  with a "more" toggle when the set overflows. */
    async _renderTagFilter() {
        const wrapper = this.querySelector("#tag-filter-wrapper");
        const strip = this.querySelector("#tag-filter");
        if (!strip) return;
        let allTags = [];
        try {
            allTags = await fb.tags.all();
        } catch {
            allTags = [];
        }
        // The board's columns are the namespace's tags, also when empty.
        this._allTags = allTags;
        if (this.boardNs && !this._openNote) this._renderBoard();

        const selected = new Set(this.tagFilter.tags);
        let visible;
        if (selected.size === 0) {
            // Initial state: tags with at least one referencing note.
            visible = allTags.filter(t => (t.usageCount || 0) > 0);
        } else {
            // Filtered state: only tags reachable from the current result set
            // (so clicking them is guaranteed to narrow further), plus the
            // selected tags themselves so they can be deselected.
            const reachable = new Set();
            for (const note of this.notes) {
                for (const name of (note.tags || [])) reachable.add(name);
            }
            visible = allTags.filter(t => reachable.has(t.name) || selected.has(t.name));
        }

        if (visible.length === 0) {
            if (wrapper) wrapper.hidden = true;
            strip.innerHTML = "";
            return;
        }

        // Most-used tags first; name as the stable tie-breaker.
        visible.sort((a, b) => {
            const diff = (b.usageCount || 0) - (a.usageCount || 0);
            return diff !== 0 ? diff : a.name.localeCompare(b.name);
        });

        if (wrapper) wrapper.hidden = false;
        strip.innerHTML = visible.map(t =>
            `<sac-chip label="${t.name}" color="${t.color}" clickable
                      ${selected.has(t.name) ? "selected" : ""}></sac-chip>`
        ).join("");
        // <sac-collapsible> listens to slotchange + ResizeObserver, so it
        // re-measures overflow on its own after the innerHTML swap.

        strip.querySelectorAll("sac-chip").forEach(chip => {
            chip.addEventListener("click", async () => {
                // A chip takes no focus: the list keeps the keys, so the arrows go on from the open note.
                this.querySelector("#note-list")?.focus({ preventScroll: true });
                const name = chip.getAttribute("label");
                if (selected.has(name)) {
                    this.tagFilter.tags = this.tagFilter.tags.filter(t => t !== name);
                } else {
                    this.tagFilter.tags = [...this.tagFilter.tags, name];
                }
                // In search mode, tag filtering is applied client-side in
                // renderList against the existing result set — skip the
                // browse-mode reload so we don't trash the ranked results.
                if (this.searchActive) {
                    this.renderList();
                } else {
                    // Order matters: load first so this.notes is fresh, then
                    // re-render the strip from the new result set.
                    await this.loadNotes();
                }
                this._renderTagFilter();
            });
        });
    }

    _openManageDialog() {
        fb.tagManager.open({
            onChanged: async () => {
                // Names/colors may have shifted; reload notes (and re-render
                // chips on the active note) so stale chips don't linger.
                await this.loadNotes();
                this._renderTagFilter();
                // The open note's tags may have been renamed: only those
                // change — its text stays the editor's.
                const open = this._openNote;
                if (open) {
                    const refreshed = await this.api.get(open.id, { prompt: false }).catch(() => null);
                    if (refreshed && this._openNote === open) {
                        open.tags = refreshed.tags || [];
                        this.querySelector("#tag-input").value = open.tags;
                    }
                }
            },
        });
    }

    /** Schedule an autosave after the user stops typing. */
    scheduleAutoSave() {
        clearTimeout(this._saveDebounce);
        this._saveDebounce = setTimeout(() => this.saveSelected(), 700);
    }

    /** Cancel any pending autosave and save immediately. */
    async flushSave() {
        clearTimeout(this._saveDebounce);
        this._saveDebounce = null;
        await this.saveSelected();
    }

    /** Kept as a thin alias for per-note callers (select, togglePinned) so
     *  the toolbar stays in sync with view state. Today the toolbar only
     *  shows tag management, which doesn't depend on the active note. */
    updateToolbar(_note) {
        this._setViewToolbar();
    }

    // The header names the list — or, while notes are marked, how many, with
    // Approve (when any awaits review), Delete and Clear in place of the
    // archive toggle and "+".
    paintHead() {
        const n = this.sel?.marked.length || 0;
        const filter = this.filters.find((f) => f.id === this.activeFilter);
        this.querySelector("#list-title").textContent = n
            ? (n === 1 ? fb.t("fb.notes.marked-1", "1 selected") : fb.t("fb.notes.marked", "{n} selected", { n }))
            : filter
                ? filter.name
                : this.showArchived
                    ? fb.t("fb.notes.all-archived", "All + Archived")
                    : fb.t("fb.notes.all", "All Notes");
        this.querySelector("#toggle-archived-btn").hidden = n > 0;
        this.querySelector("#filters-menu").hidden = n > 0;
        this.querySelector("#filters-btn").classList.toggle("active", !!filter);
        this.querySelector("#new-btn").hidden = this.writable === false || n > 0;
        this.querySelector("#approve-marked-btn").hidden = n === 0 || this.writable === false
            || !this.sel.marked.some((id) => isPending(this.notes.find((x) => x.id === id)));
        this.querySelector("#delete-marked-btn").hidden = n === 0;
        this.querySelector("#clear-marks-btn").hidden = n === 0;
    }

    renderList() {
        const expr = this._filterExpr();
        const filtered = this.notes.filter(n => {
            if (!this.showArchived && n.archived) return false;
            if (expr && !expr.test(n.tags)) return false;
            // Tag filter is server-side in browse mode (loadNotes passes it
            // as a query param) but search results come back unfiltered,
            // so apply it here whenever a search is active.
            if (this.searchActive && this.tagFilter.tags.length > 0) {
                const noteTags = new Set(n.tags || []);
                if (!this.tagFilter.tags.every(t => noteTags.has(t))) return false;
            }
            return true;
        });

        if (!this.searchActive) {
            // Browse mode: pinned first, then most-recently-updated.
            filtered.sort((a, b) => {
                if (a.pinned && !b.pinned) return -1;
                if (!a.pinned && b.pinned) return 1;
                return new Date(b.updatedAt || 0) - new Date(a.updatedAt || 0);
            });
        }
        // Search mode: preserve the server's score order — pinned boost
        // would hide the relevance signal that's the whole point of search.

        this._shown = filtered;
        if (this.boardNs && !this._openNote) this._renderBoard();
        const list = this.querySelector("#note-list");
        if (filtered.length === 0) {
            list.innerHTML = `<div class="nv-empty-list">${fb.t("fb.notes.empty-list", "No notes.")}</div>`;
            return;
        }

        list.innerHTML = filtered.map(n => {
            const snippet = snippetFor(n);
            const date = this.formatDate(n.updatedAt);
            const isSelected = n.id === this.selectedId;
            const archiveTitle = n.archived ? fb.t("fb.notes.unarchive", "Unarchive") : fb.t("fb.notes.archive", "Archive");
            const pinTitle = n.pinned ? fb.t("fb.notes.unpin", "Unpin") : fb.t("fb.notes.pin", "Pin to top");
            const rowClasses = [
                "nv-item",
                "reveal-on-hover",
                isSelected  ? "selected" : "",
                n.archived  ? "archived" : "",
                isPending(n) ? "pending" : "",
            ].filter(Boolean).join(" ");
            const tagLine = this._renderRowTagLine(n.tags);
            // Approve only surfaces when the note needs review. One click
            // takes review:pending off (approveMany: a patch, tags in place).
            const approveBtnHtml = isPending(n)
                ? `<button class="icon-btn hover-reveal nv-item-action approve" data-action="approve" title="${fb.t("fb.notes.approve-title", "Approve (clear review:pending)")}" aria-label="${fb.t("fb.notes.approve", "Approve")}"><sac-icon name="check"></sac-icon></button>`
                : "";
            return `
                <div class="${rowClasses}" data-id="${n.id}" tabindex="0">
                    <div class="nv-item-title-row">
                        <span class="nv-item-title">${escapeHtml(n.title || fb.t("fb.notes.untitled", "Untitled"))}</span>
                    </div>
                    <div class="nv-item-preview">
                        <span class="nv-item-date">${date}</span>
                        <span class="nv-item-snippet">${escapeHtml(snippet || fb.t("fb.notes.no-text", "No additional text"))}</span>
                    </div>
                    ${tagLine}
                    <div class="nv-item-actions" ${this.writable === false ? "hidden" : ""}>
                        ${approveBtnHtml}
                        <button class="icon-btn hover-reveal nv-item-action pin ${n.pinned ? "active" : ""}" data-action="pin" title="${pinTitle}" aria-label="${pinTitle}"><sac-icon name="pin"></sac-icon></button>
                        <button class="icon-btn hover-reveal nv-item-action archive ${n.archived ? "active" : ""}" data-action="archive" title="${archiveTitle}" aria-label="${archiveTitle}"><sac-icon name="archive"></sac-icon></button>
                        <button class="icon-btn hover-reveal danger nv-item-action delete" data-action="delete" title="${fb.t("fb.common.delete", "Delete")}" aria-label="${fb.t("fb.common.delete", "Delete")}"><sac-icon name="trash"></sac-icon></button>
                    </div>
                </div>
            `;
        }).join("");

        list.querySelectorAll(".nv-item").forEach(el => {
            el.addEventListener("click", (e) => {
                if (e.target.closest(".nv-item-action")) return;
                this.select(el.dataset.id);
            });
            el.addEventListener("keydown", (e) => {
                if (e.target !== el || (e.key !== "Enter" && e.key !== " ")) return;
                e.preventDefault();
                this.select(el.dataset.id);
            });
            el.querySelectorAll(".nv-item-action").forEach(btn => {
                btn.addEventListener("click", (e) => {
                    e.stopPropagation();
                    const id = el.dataset.id;
                    switch (btn.dataset.action) {
                        case "approve": this.approveById(id);         break;
                        case "pin":     this.togglePinnedById(id);    break;
                        case "archive": this.toggleArchivedById(id);  break;
                        case "delete":  this.deleteById(id);          break;
                    }
                });
            });
        });
    }

    async select(id) {
        // Collapsed (phone), opening a row brings the editor forward — also
        // when it is the row already open behind the list.
        this.querySelector("#split").show = "end";
        if (this.selectedId === id) return;
        // Persist any pending edits on the outgoing note BEFORE we swap the
        // editor's DOM values — otherwise the pending debounced save would fire
        // against the incoming note's values.
        await this.flushSave();
        this.selectedId = id;
        let note = this.notes.find(n => n.id === id);
        if (!note) return;
        // A search hit (or a copy masked by a lock) isn't the note: load it.
        // Opening a note whose secrets are still markers (the vault was
        // locked when the list loaded) is the moment to ask for the key.
        if (note._hit || hasLockedSecrets(note)) note = await this._decryptInCache(note);
        // Another row was opened while this one loaded or asked for the
        // key: that one wins — this note must not land in its editor.
        if (this.selectedId !== id) return;
        if (!note) {
            this.selectedId = this._openNote?.id ?? null;
            window.sac?.toast?.(fb.t("fb.notes.open-failed", "Couldn't open the note."), { kind: "error" });
            this.renderList();
            return;
        }
        this.querySelector("#board").hidden         = true;
        this.querySelector(".nv-editor-body").hidden = false;
        this.querySelector("#editor-empty").hidden  = true;
        this.querySelector("#editor").hidden        = false;
        this.querySelector("#tagbar").hidden        = false;
        this.querySelector("#editor-footer").hidden = false;
        this._showInEditor(note);
        this.querySelector("#tag-input").value = note.tags || [];
        this.querySelector("#timestamp").textContent = this.formatFullTimestamp(note.updatedAt);
        this.querySelector("#back-to-board-btn").hidden = !this.boardNs;
        this._paintNoteMeta(note);
        this._renderLinks(note);
        this._refreshHistory(note);
        this.updateToolbar(note);
        // sac-md-editor autosizes itself on value-set (deferred to next
        // frame internally), so no explicit autosize hook needed here.
        this.renderList();
    }

    /** Puts a note in the editor; from here on it is the one saves write. */
    _showInEditor(note) {
        this._openNote = note;
        this._loadedText = editorTextFor(note);
        // A note from elsewhere is shown with its title on top (editorTextFor);
        // a save puts that line back into the title, never into the text.
        this._titleOnTop = this._loadedText !== displayContent(note);
        const editor = this.querySelector("#content");
        editor.value = this._loadedText;
        // What the editor reads back for the text as loaded. Unchanged from
        // that is not an edit, even where the editor reads it back otherwise
        // than it was given — opening a note never writes it.
        this._editorBaseline = editor.value;
        this._applyReadOnly(note);
    }

    /** Re-fetch one note through the decrypting path (asks to unlock) and
     *  swap it into the cache. Cancelled → the note stays as it was; a
     *  search hit or a masked copy that can't be loaded gives null — it is
     *  never opened or written as it is. */
    async _decryptInCache(note) {
        try {
            return this._swapIn(await this.api.get(note.id));
        } catch (err) {
            console.warn("[fb-notes-view] loading the note failed:", err);
            return note._hit ? null : note;
        }
    }

    /** Replace the cached copy of a note with this one. */
    _swapIn(note) {
        const i = this.notes.findIndex(n => n.id === note.id);
        if (i >= 0) this.notes[i] = note;
        return note;
    }

    /** The note to write back for a row. Never a search hit or a masked copy
     *  (`_hit`): those hold their secrets as a placeholder and no ciphertext,
     *  so a PUT of one would replace the secrets for good. The open note is
     *  the editor's own; anything else in that state is loaded first —
     *  without asking for the key: a locked note's markers and ciphertext go
     *  back as they are. */
    async _noteFor(id) {
        const open = this._openNote;
        if (open?.id === id && !open._hit) return this._swapIn(open);
        const cached = this.notes.find(n => n.id === id);
        if (cached && !cached._hit) return cached;
        try {
            return this._swapIn(await this.api.get(id, { prompt: false }));
        } catch (err) {
            console.error("[fb-notes-view] loading the note failed:", err);
            window.sac?.toast?.(fb.errors.text(err, fb.t("fb.notes.change-failed", "Couldn't save that change.")), { kind: "error" });
            return null;
        }
    }

    /** After a lock: every cached copy holding decrypted secrets is masked
     *  in place (`lockedCopy`, loaded again before it is opened or written)
     *  — no list reload, so nothing else in the cache is touched. The editor
     *  is repainted only when it shows decrypted secrets: a note without
     *  any keeps what is being typed. The split keeps showing what it
     *  showed. */
    async _repaintLocked() {
        const showsSecrets = (text) => fb.api.secrets.ranges(text || "").length > 0;
        this.notes = this.notes.map(n => !n._hit && !hasLockedSecrets(n) && showsSecrets(n.content) ? lockedCopy(n) : n);
        this.renderList();
        const open = this._openNote;
        const content = this.querySelector("#content");
        if (!open || open._hit || hasLockedSecrets(open) || !showsSecrets(content.value)) return;
        const split = this.querySelector("#split");
        const shown = split?.show;
        const fresh = await this.api.get(open.id, { prompt: false }).catch(() => null);
        if (this._openNote !== open) return;   // another note was opened meanwhile
        // Offline: the masked text, read-only until the pill unlocks it.
        this._showInEditor(fresh ? this._swapIn(fresh) : lockedCopy({ ...open, content: content.value }));
        this.renderList();
        if (split && shown) split.show = shown;
    }

    /** After an unlock (the account menu, or writing a secret): decrypt
     *  just the cached notes still holding markers (or masked by a lock).
     *  Everything else in the cache stays as it is — a save may be in flight
     *  on one of them, and a whole-list reload could put an older server
     *  copy over it. The open note is repainted only if it was showing
     *  placeholders, and only if it is still the open one. */
    async _repaintUnlocked() {
        const isLocked = (n) => hasLockedSecrets(n) || !!n._locked;
        const open = this._openNote;
        const openWasLocked = !!open && isLocked(open);
        const locked = this.notes.filter(isLocked);
        if (locked.length === 0 && !openWasLocked) return;
        await Promise.all(locked.map(n => this._decryptInCache(n)));
        if (openWasLocked && this._openNote === open) {
            let note = this.notes.find(n => n.id === open.id);
            if (!note || note._hit) note = await this._decryptInCache(open);
            if (note && this._openNote === open && !isLocked(note) && !note._hit) this._showInEditor(note);
        }
        this.renderList();
    }

    /** "Secrets locked" pill → unlock, then show the note decrypted. */
    async _unlockOpenNote() {
        const open = this._openNote;
        if (!open) return;
        const fresh = await this._decryptInCache(open);
        // Failed, or another note was opened while the dialog was up.
        if (!fresh || this._openNote !== open) return;
        if (fresh.secretsUnreadable) {
            window.sac?.toast?.(fb.t("fb.notes.secrets-unreadable",
                "This note's secrets can't be decrypted. It stays read-only so they aren't overwritten."), { kind: "error" });
        }
        if (hasLockedSecrets(fresh)) return; // cancelled, or unreadable
        this._showInEditor(fresh);
        this.renderList();
    }

    /** Read-only while archived, while the note's secrets are locked —
     *  editing around `:::secret#N:::end` markers is too easy to get wrong —
     *  and while the editor holds a masked copy (`_hit`) instead of the
     *  note. */
    _applyReadOnly(note) {
        const locked = hasLockedSecrets(note) || !!note._hit;
        this.querySelector("#locked-pill").hidden = !locked;
        const ro = !!note.archived || locked || !!note.locked || this.writable === false;
        this.querySelector("#tag-input")?.toggleAttribute("disabled", this.writable === false || !!note._hit);
        const editor = this.querySelector("#editor");
        editor.classList.toggle("readonly", ro);
        this.querySelector("#content").toggleAttribute("readonly", ro);
        this.querySelector("#archived-pill").hidden = !note.archived;
    }

    clearSelection() {
        this.selectedId = null;
        this._openNote = null;
        // Collapsed, an editor with nothing in it is a dead end.
        this.querySelector("#split").show = "start";
        this.querySelector("#editor-empty").hidden  = false;
        this.querySelector("#editor").hidden        = true;
        this.querySelector("#tagbar").hidden        = true;
        this.querySelector("#editor-footer").hidden = true;
        this.querySelector("#timestamp").textContent = "";
        const tagInput = this.querySelector("#tag-input");
        if (tagInput) tagInput.value = [];
        this.querySelector("#links").hidden = true;
        // Toolbar stays — manage-tags is view-scoped, not per-note.
        if (this.boardNs) {
            this.querySelector("#split").show = "end";
            this._renderBoard();
        }
    }

    async saveSelected() {
        // Any debounced save firing is equivalent to an explicit flush.
        clearTimeout(this._saveDebounce);
        this._saveDebounce = null;
        // The note in the editor — see _openNote. A masked copy (`_hit`,
        // after a lock without the server) isn't the note: never written.
        const note = this._openNote;
        if (!note || note._hit) return;
        const newTags  = this.querySelector("#tag-input").value;
        const tagsChanged = !this._sameTags(note.tags || [], newTags);
        // A note with locked secrets shows placeholders in the editor. Its
        // text must never be saved — that would replace the real secrets
        // with the placeholder. Only a tag change goes out; content and
        // ciphertext stay exactly as the server has them.
        const locked = hasLockedSecrets(note);
        const text     = locked ? this._editorBaseline : this.querySelector("#content").value;
        // Compare against what the editor gave back when the note was
        // loaded, not note.content: a note shown with its title on top (see
        // editorTextFor) must not be rewritten just because it was opened.
        const edited = text !== this._editorBaseline;
        if (!edited && !tagsChanged) return;
        const previous = { baseline: this._editorBaseline, titleOnTop: this._titleOnTop };
        this._editorBaseline = text;
        if (edited) {
            const saved = fromEditorText(text, this._titleOnTop, note.title);
            note.title   = saved.title;
            note.content = saved.content;
            this._titleOnTop = saved.titleOnTop;
        }
        note.tags    = newTags;
        try {
            await this.api.update(note.id, note);
            note.updatedAt = new Date().toISOString();
            if (this._openNote === note) this.querySelector("#timestamp").textContent = this.formatFullTimestamp(note.updatedAt);
            if (tagsChanged) {
                // A new tag may have been auto-registered server-side via
                // EnsureExistsAsync; refresh the registry + filter strip so
                // the new chip is selectable.
                fb.tags.invalidate();
                await this._renderTagFilter();
            }
            // Update just this row in place — a full renderList() here would
            // wipe the list DOM mid-click and cause the user's click on
            // another row to be dropped (mousedown/mouseup land on different
            // elements so the browser doesn't fire the click). Re-sort on
            // next explicit render (select, delete, archive, pin). The list's
            // copy (a search hit, an older load) becomes the saved note.
            this._swapIn(note);
            this._updateRowInPlace(note);
            this._lastSaveError = null;
            if (this._openNote === note) this._renderLinks(note);
        } catch (err) {
            // Not saved: the next edit or flush must try again.
            if (this._openNote === note && this._editorBaseline === text) {
                this._editorBaseline = previous.baseline;
                this._titleOnTop = previous.titleOnTop;
            }
            console.error("[fb-notes-view] update failed:", err);
            this._reportSaveError(err);
        }
    }

    /** One toast per distinct failure — autosave retries every few hundred
     *  milliseconds while the user types, and the same refusal (a secret in
     *  a space, a locked vault) shouldn't stack a toast per keystroke. */
    _reportSaveError(err) {
        const message = err?.userFacing ? err.message
            : err?.status === 400 ? (safeJson(err.body)?.reason || fb.t("fb.notes.refused", "The note was refused by the server."))
            : fb.t("fb.notes.save-failed", "Couldn't save the note.");
        if (message === this._lastSaveError) return;
        this._lastSaveError = message;
        window.sac?.toast?.(message, { kind: "error", title: fb.t("fb.notes.not-saved", "Not saved") });
    }

    _sameTags(a, b) {
        if (a.length !== b.length) return false;
        const sa = [...a].sort(), sb = [...b].sort();
        for (let i = 0; i < sa.length; i++) if (sa[i] !== sb[i]) return false;
        return true;
    }

    /** Build the per-row tag preview HTML. Returns "" when there are no tags
     *  (so the row stays compact). Tags render as a coloured dot + name on a
     *  single ellipsis-truncated line; full list goes in the title tooltip. */
    _renderRowTagLine(tags) {
        if (!tags || tags.length === 0) return "";
        const tooltip = tags.join(", ");
        const parts = tags.map((name, i) => {
            const color = `var(--tag-${fb.tags.colorFor(name)}, var(--tag-gray))`;
            const sep = i === 0 ? "" : `<span class="tag-sep">·</span>`;
            return `${sep}<span class="tag-dot" style="background:${color}"></span><span class="tag-name">${escapeHtml(name)}</span>`;
        }).join("");
        return `<div class="nv-item-tagline" title="${escapeHtml(tooltip)}">${parts}</div>`;
    }

    /** Refresh a single row's title/date/snippet/tags without wiping the list DOM. */
    _updateRowInPlace(note) {
        const row = this.querySelector(`.nv-item[data-id="${note.id}"]`);
        if (!row) return;
        const titleEl   = row.querySelector(".nv-item-title");
        const dateEl    = row.querySelector(".nv-item-date");
        const snippetEl = row.querySelector(".nv-item-snippet");
        if (titleEl) titleEl.textContent = note.title || fb.t("fb.notes.untitled", "Untitled");
        if (dateEl)  dateEl.textContent  = this.formatDate(note.updatedAt);
        if (snippetEl) {
            const snippet = snippetFor(note);
            snippetEl.textContent = snippet || fb.t("fb.notes.no-text", "No additional text");
        }
        // Replace (or insert) the tagline so colors/names update on save.
        const existing = row.querySelector(".nv-item-tagline");
        if (existing) existing.remove();
        const tagHtml = this._renderRowTagLine(note.tags || []);
        if (tagHtml) {
            const actions = row.querySelector(".nv-item-actions");
            actions.insertAdjacentHTML("beforebegin", tagHtml);
        }
    }

    /** Toolbar-triggered delegates that just dispatch to the id-based methods. */
    /** A note row's context menu: Open, then what the row's buttons do. */
    _rowItems(row) {
        const n = this.notes.find((x) => x.id === row.dataset.id);
        if (!n) return null;
        const items = [{ id: "open", label: fb.t("fb.common.open", "Open"), icon: "document", onClick: () => this.select(n.id) }];
        if (this.writable === false) return items;
        items.push("-");
        const marked = this.sel?.marked || [];
        const several = marked.length > 1 && marked.includes(n.id);
        if (several && marked.some((id) => isPending(this.notes.find((x) => x.id === id))))
            items.push({ id: "approve", label: fb.t("fb.notes.approve-marked", "Approve selected"), icon: "check", onClick: () => this.approveMany(marked) });
        else if (!several && isPending(n))
            items.push({ id: "approve", label: fb.t("fb.notes.approve", "Approve"), icon: "check", onClick: () => this.approveById(n.id) });
        items.push(
            { id: "pin", label: n.pinned ? fb.t("fb.notes.unpin", "Unpin") : fb.t("fb.notes.pin", "Pin to top"), icon: "pin",
              onClick: () => this.togglePinnedById(n.id) },
            { id: "archive", label: n.archived ? fb.t("fb.notes.unarchive", "Unarchive") : fb.t("fb.notes.archive", "Archive"), icon: "archive",
              onClick: () => this.toggleArchivedById(n.id) },
            "-",
            several
                ? { id: "delete", label: fb.t("fb.notes.delete-marked", "Delete selected"), icon: "trash", danger: true, onClick: () => this.removeMany(marked) }
                : { id: "delete", label: fb.t("fb.common.delete", "Delete"), icon: "trash", danger: true, onClick: () => this.deleteById(n.id) },
        );
        return items;
    }

    togglePinned()    { if (this.selectedId) this.togglePinnedById(this.selectedId); }
    deleteSelected()  { if (this.selectedId) this.deleteById(this.selectedId); }

    async togglePinnedById(id) {
        // If the target is the active editor, flush text edits first so they
        // ride in the same PUT as the pinned flip (one round-trip, no clobber).
        if (id === this._openNote?.id) await this.flushSave();
        const note = await this._noteFor(id);
        if (!note) return;
        note.pinned = !note.pinned;
        try {
            await this.api.update(note.id, note);
            if (id === this._openNote?.id) this.updateToolbar(note);
            this.renderList();
        } catch (err) {
            console.error("[fb-notes-view] pin toggle failed:", err);
            window.sac?.toast?.(fb.errors.text(err, fb.t("fb.notes.change-failed", "Couldn't save that change.")), { kind: "error" });
            note.pinned = !note.pinned;
        }
    }

    async toggleArchivedById(id) {
        if (id === this._openNote?.id) await this.flushSave();
        const note = await this._noteFor(id);
        if (!note) return;
        note.archived = !note.archived;
        try {
            await this.api.update(note.id, note);
            // If we archived the currently-open note and archive view is off,
            // it just disappeared from the list — clear the editor too.
            if (note === this._openNote && note.archived && !this.showArchived) {
                this.clearSelection();
            } else if (note === this._openNote) {
                // Still visible (either unarchived, or archived with
                // showArchived on) — refresh the editor's read-only state.
                this._applyReadOnly(note);
            }
            this.renderList();
        } catch (err) {
            console.error("[fb-notes-view] archive toggle failed:", err);
            window.sac?.toast?.(fb.errors.text(err, fb.t("fb.notes.change-failed", "Couldn't save that change.")), { kind: "error" });
            note.archived = !note.archived;
        }
    }

    /** Approve: review:pending off and nothing else — one patch a note,
     *  tags in place, so the text stays as stored (a locked note's too, and
     *  a note with secrets needs no key). Editing is approval as well; this
     *  is the explicit handle for whoever reviews by tag. */
    approveById(id) { return this.approveMany([id]); }

    /** Several at once — the marked notes, or every note the list shows
     *  (`_approveShown`). A few patches run side by side; a refusal leaves
     *  that note as it was and says so once. The marks go when it is done. */
    async approveMany(ids) {
        if (this.writable === false) return;
        const notes = ids.map((id) => this.notes.find((n) => n.id === id)
            || (this._openNote?.id === id ? this._openNote : null)).filter(isPending);
        if (notes.length === 0) return;
        // A pending autosave goes first, so its PUT can't carry the tag back.
        if (notes.some((n) => n.id === this._openNote?.id)) await this.flushSave();
        const queue = [...notes];
        let done = 0, firstError = null;
        const worker = async () => {
            for (let n; (n = queue.shift()); ) {
                try {
                    const updated = await this.api.patch(n.id, { removeTags: ["review:pending"] });
                    this._approved(n.id, updated);
                    done++;
                } catch (err) { firstError ??= err; }
            }
        };
        await Promise.all([worker(), worker(), worker()]);
        if (ids.length > 1) this.sel?.clear();
        this.renderList();
        this.paintHead();
        if (firstError) {
            console.error("[fb-notes-view] approve failed:", firstError);
            const reason = fb.errors.text(firstError, fb.t("fb.notes.change-failed", "Couldn't save that change."));
            window.sac?.toast?.(notes.length === 1 ? reason
                : fb.t("fb.notes.approve-some-failed", "{done} approved; {failed} couldn't be: {reason}",
                    { done, failed: notes.length - done, reason }), { kind: "error" });
        } else if (done > 1) {
            window.sac?.toast?.(fb.t("fb.notes.approved-n", "{n} notes approved.", { n: done }));
        }
    }

    /** A note's tags as the server has them after an approval, wherever the
     *  view holds it — the list, a search hit, the open note and its tags. */
    _approved(id, updated) {
        for (const note of new Set([...this.notes.filter((n) => n.id === id), this._openNote?.id === id ? this._openNote : null])) {
            if (!note) continue;
            note.tags = updated.tags || (note.tags || []).filter((t) => t !== "review:pending");
            if (updated.updatedAt) note.updatedAt = updated.updatedAt;
        }
        if (this._openNote?.id === id) this.querySelector("#tag-input").value = this._openNote.tags;
        const row = this.querySelector(`.nv-item[data-id="${CSS.escape(id)}"]`);
        if (row) {
            row.classList.remove("pending");
            row.querySelector('[data-action="approve"]')?.remove();
            const note = this.notes.find((n) => n.id === id);
            if (note) this._updateRowInPlace(note);
        }
    }

    /** Every note the list shows that awaits review — the saved filter, the
     *  tag strip and a search narrow it — after one question. */
    async _approveShown() {
        const ids = (this._shown || []).filter(isPending).map((n) => n.id);
        if (ids.length === 0) {
            window.sac?.toast?.(fb.t("fb.notes.approve-none", "Nothing here awaits review."));
            return;
        }
        const answer = await sac.dialog.confirm({
            title: ids.length === 1 ? fb.t("fb.notes.approve-shown-title-1", "Approve 1 note?")
                : fb.t("fb.notes.approve-shown-title", "Approve {n} notes?", { n: ids.length }),
            message: fb.t("fb.notes.approve-shown-msg", "Every note shown here that awaits review loses review:pending. Their text and their other tags stay as they are."),
            buttons: [
                { action: "cancel", label: fb.t("fb.common.cancel", "Cancel"), kind: "default" },
                { action: "approve", label: fb.t("fb.notes.approve", "Approve"), kind: "primary" },
            ],
        });
        if (answer === "approve") await this.approveMany(ids);
    }

    async deleteById(id) {
        const note = this.notes.find(n => n.id === id);
        if (!note) return;

        // Offer Archive as the safer alternative, unless the note is already
        // archived (then it'd be a no-op and clutters the dialog).
        const buttons = [{ action: "cancel", label: fb.t("fb.common.cancel", "Cancel"), kind: "default" }];
        if (!note.archived) {
            buttons.push({ action: "archive", label: fb.t("fb.notes.archive", "Archive"), kind: "primary" });
        }
        // armAfterMs gives the user 2s to read before Delete becomes the
        // Enter default — see spec 2026-04-20-fb-dialog-design.md.
        buttons.push({ action: "delete", label: fb.t("fb.common.delete", "Delete"), kind: "destructive", armAfterMs: 2000 });

        const message = note.archived
            ? fb.t("fb.notes.delete-msg", "This note moves to the trash — you can restore it there.")
            : fb.t("fb.notes.delete-msg-archive", "This note moves to the trash — you can restore it there. Archive it instead to keep it out of sight here.");

        const result = await sac.dialog.confirm({ title: fb.t("fb.notes.delete-title", "Delete this note?"), message, buttons });

        if (result === "archive") return this.toggleArchivedById(id);
        if (result !== "delete") return;

        // Cancel any pending autosave for this note so it doesn't re-create it
        // after the DELETE round-trip.
        if (id === this._openNote?.id) {
            clearTimeout(this._saveDebounce);
            this._saveDebounce = null;
        }
        try {
            await this.api.delete(id);
            this.notes = this.notes.filter(n => n.id !== id);
            if (id === this._openNote?.id || id === this.selectedId) {
                // Still loading when deleted: the editor holds another note.
                if (this._openNote && this._openNote.id !== id) await this.flushSave();
                this.clearSelection();
            }
            this.renderList();
        } catch (err) {
            console.error("[fb-notes-view] delete failed:", err);
            window.sac?.toast?.(fb.errors.text(err, fb.t("fb.notes.delete-failed", "Couldn't delete the note.")), { kind: "error" });
        }
    }

    // The keyboard's and the header's delete: one note asks the usual question
    // (with Archive as the alternative); several ask once, then go to the trash.
    async removeMany(ids) {
        if (!this.writable) return;
        const items = ids.map(id => this.notes.find(n => n.id === id)).filter(Boolean);
        if (items.length === 0) return;
        if (items.length === 1) return this.deleteById(items[0].id);
        const answer = await sac.dialog.confirm({
            title: fb.t("fb.notes.delete-title-n", "Delete {n} notes?", { n: items.length }),
            message: fb.t("fb.notes.delete-msg-n", "They move to the trash — you can restore them there."),
            buttons: [
                { action: "cancel", label: fb.t("fb.common.cancel", "Cancel"), kind: "default" },
                { action: "delete", label: fb.t("fb.common.delete", "Delete"), kind: "destructive", armAfterMs: 2000 },
            ],
        });
        if (answer !== "delete") return;
        // A pending autosave would PUT the note after it is gone.
        if (items.some(n => n.id === this._openNote?.id)) {
            clearTimeout(this._saveDebounce);
            this._saveDebounce = null;
        }
        let done = 0, firstError = null;
        for (const n of items) {
            try {
                await this.api.delete(n.id);
                done++;
                this.notes = this.notes.filter(x => x.id !== n.id);
                if (n.id === this._openNote?.id || n.id === this.selectedId) this.clearSelection();
            } catch (err) { firstError ??= err; }
        }
        this.sel?.clear();
        this.renderList();
        if (!firstError) {
            window.sac?.toast?.(fb.t("fb.notes.deleted-n", "{n} notes moved to the trash.", { n: done }));
        } else {
            window.sac?.toast?.(fb.t("fb.notes.delete-some-failed", "{done} moved to the trash; {failed} couldn't be deleted: {reason}",
                { done, failed: items.length - done, reason: fb.errors.text(firstError, fb.t("fb.notes.delete-failed", "Couldn't delete the note.")) }), { kind: "error" });
        }
    }

    async createNote() {
        try {
            const created = await this.api.create({ title: "", content: "" });
            this.notes.unshift(created);
            // Let select() set selectedId so its dedupe guard doesn't short-circuit.
            await this.select(created.id);
            this.querySelector("#content").focus();
        } catch (err) {
            console.error("[fb-notes-view] create failed:", err);
            window.sac?.toast?.(fb.errors.text(err, fb.t("fb.notes.create-failed", "Couldn't create the note.")), { kind: "error" });
        }
    }

    // ── Saved filters, the board, links, the lock, history ───────────────
    // (spec 2026-11-06-notes-for-agents)

    _prefsKey(what) { return `fb.notes.${what}.${this.ws}`; }

    /** The filter in use and the board, per browser and workspace. */
    _readPrefs() {
        try {
            this.activeFilter = localStorage.getItem(this._prefsKey("filter")) || null;
            this.boardNs = localStorage.getItem(this._prefsKey("board")) || null;
        } catch { /* storage off: the list, unfiltered */ }
    }

    _writePrefs() {
        try {
            for (const [what, value] of [["filter", this.activeFilter], ["board", this.boardNs]]) {
                if (value) localStorage.setItem(this._prefsKey(what), value);
                else localStorage.removeItem(this._prefsKey(what));
            }
        } catch { /* storage off: this visit only */ }
    }

    async _loadFilters() {
        try { this.filters = await this.api.filters.list(); }
        catch (err) { console.warn("[fb-notes-view] saved filters unavailable:", err); this.filters = []; }
        if (this.activeFilter && !this.filters.some((f) => f.id === this.activeFilter)) this.activeFilter = null;
        this._renderFilterMenu();
        this.paintHead();
    }

    /** The saved filter in use, compiled — null when none (or unreadable). */
    _filterExpr() {
        const f = this.filters.find((x) => x.id === this.activeFilter);
        if (!f) return null;
        try { return fb.tagExpr.compile(f.expr); } catch { return null; }
    }

    _renderFilterMenu() {
        const menu = this.querySelector("#filters-menu");
        if (!menu) return;
        for (const el of [...menu.children]) if (el.getAttribute("slot") !== "trigger") el.remove();
        const item = (action, icon, label, checked, danger) => {
            const b = document.createElement("button");
            b.dataset.action = action;
            if (danger) b.setAttribute("data-danger", "");
            b.innerHTML = `<sac-icon name="${icon}"></sac-icon><span></span>`;
            b.querySelector("span").textContent = label;
            if (checked) {
                b.setAttribute("aria-checked", "true");
                b.insertAdjacentHTML("beforeend", `<sac-icon class="fb-context-check" name="check"></sac-icon>`);
            }
            return b;
        };
        const active = this.filters.find((f) => f.id === this.activeFilter);
        const nodes = [item("filter:", "note", fb.t("fb.notes.filter-none", "All notes"), !active)];
        if (this.filters.length) {
            nodes.push(document.createElement("hr"));
            for (const f of this.filters) {
                const b = item("filter:" + f.id, "fb-filter", f.name, f.id === this.activeFilter);
                b.title = f.expr;
                nodes.push(b);
            }
        }
        if (this.writable !== false) {
            nodes.push(document.createElement("hr"), item("approve-shown", "check", fb.t("fb.notes.approve-shown", "Approve all shown…")));
            nodes.push(document.createElement("hr"), item("new", "plus", fb.t("fb.notes.filter-new", "New filter…")));
            if (active) nodes.push(
                item("edit", "pencil", fb.t("fb.notes.filter-edit", "Edit “{name}”…", { name: active.name })),
                item("delete", "trash", fb.t("fb.notes.filter-delete", "Delete “{name}”", { name: active.name }), false, true));
        }
        menu.append(...nodes);
    }

    async _onFilterMenu(action) {
        if (action.startsWith("filter:")) {
            this.activeFilter = action.slice(7) || null;
            this._writePrefs();
            this._renderFilterMenu();
            this.paintHead();
            this.renderList();
            return;
        }
        if (action === "approve-shown") return this._approveShown();
        const active = this.filters.find((f) => f.id === this.activeFilter);
        if (action === "new") return this._filterDialog(null);
        if (action === "edit" && active) return this._filterDialog(active);
        if (action === "delete" && active) {
            const answer = await sac.dialog.confirm({
                title: fb.t("fb.notes.filter-delete-title", "Delete the filter “{name}”?", { name: active.name }),
                message: fb.t("fb.notes.filter-delete-msg", "Only the filter goes; the notes stay as they are."),
                buttons: [
                    { action: "cancel", label: fb.t("fb.common.cancel", "Cancel"), kind: "default" },
                    { action: "delete", label: fb.t("fb.common.delete", "Delete"), kind: "destructive" },
                ],
            });
            if (answer !== "delete") return;
            try {
                await this.api.filters.remove(active.id);
                this.activeFilter = null;
                this._writePrefs();
                await this._loadFilters();
                this.renderList();
            } catch (err) {
                sac.toast?.(fb.errors.text(err, fb.t("fb.notes.filter-failed", "Couldn't change the filter.")), { kind: "error" });
            }
        }
    }

    /** New or edited filter: a name and a tag expression, with how many of
     *  the notes here match while it is typed. */
    async _filterDialog(filter) {
        let saved = null;
        const ok = await fb.mailAccounts.formDialog({
            title: filter ? fb.t("fb.notes.filter-edit-title", "Edit the filter") : fb.t("fb.notes.filter-new-title", "New filter"),
            primary: fb.t("fb.notes.filter-save", "Save"),
            width: "520px",
            html: `
                <label>${fb.t("fb.notes.filter-name", "Name")}<input name="name" type="text" maxlength="100"></label>
                <label>${fb.t("fb.notes.filter-expr", "Tags")}<input name="expr" type="text" maxlength="1000" spellcheck="false"
                    placeholder="kind:decision AND NOT status:superseded"></label>
                <p class="fb-filter-hint">${fb.t("fb.notes.filter-hint", "Tags with AND, OR and NOT (or a leading -), brackets to group; side by side means AND. status:* is any status.")}</p>
                <p class="fb-filter-count" aria-live="polite"></p>`,
            init: (form) => {
                const name = form.querySelector("[name=name]"), expr = form.querySelector("[name=expr]");
                const count = form.querySelector(".fb-filter-count");
                name.value = filter?.name || "";
                expr.value = filter?.expr || "";
                const paint = () => {
                    if (!expr.value.trim()) { count.textContent = ""; return; }
                    try {
                        const f = fb.tagExpr.compile(expr.value);
                        const n = this.notes.filter((x) => (this.showArchived || !x.archived) && f.test(x.tags)).length;
                        count.textContent = n === 1 ? fb.t("fb.notes.filter-count-1", "1 note here matches")
                            : fb.t("fb.notes.filter-count", "{n} notes here match", { n });
                    } catch (err) { count.textContent = err.message; }
                };
                expr.addEventListener("input", paint);
                paint();
                setTimeout(() => (filter ? expr : name).focus(), 50);
            },
            submit: async (dlg) => {
                const name = dlg.querySelector("[name=name]").value.trim();
                const expr = dlg.querySelector("[name=expr]").value.trim();
                if (!name) return fb.t("fb.notes.filter-need-name", "Give the filter a name.");
                const problem = fb.tagExpr.check(expr);
                if (problem) return problem;
                try {
                    saved = filter ? await this.api.filters.update(filter.id, { name, expr })
                        : await this.api.filters.create({ name, expr });
                    return null;
                } catch (err) {
                    return fb.errors.text(err, fb.t("fb.notes.filter-failed", "Couldn't change the filter."));
                }
            },
        });
        if (!ok || !saved) return;
        this.activeFilter = saved.id;
        this._writePrefs();
        await this._loadFilters();
        this.renderList();
    }

    /** Namespaces to group a board by: status first, then the most used. */
    _boardNamespaces() {
        const list = fb.notesBoard.namespaces(this.notes, this._allTags || []);
        if (this.boardNs && !list.includes(this.boardNs)) list.unshift(this.boardNs);
        return list;
    }

    async _setView(ns) {
        await this.flushSave();
        this.boardNs = ns || null;
        this._writePrefs();
        this._setViewToolbar();
        if (this.boardNs) this.clearSelection();
        else {
            this.querySelector("#board").hidden = true;
            this.querySelector(".nv-editor-body").hidden = false;
            this.querySelector("#split").show = "start";
        }
    }

    async _showBoard() {
        await this.flushSave();
        this.clearSelection();
    }

    _renderBoard() {
        const board = this.querySelector("#board");
        if (!board || !this.boardNs) return;
        const ns = this.boardNs;
        board.hidden = false;
        this.querySelector(".nv-editor-body").hidden = true;
        fb.notesBoard.render(board, {
            notes: this._shown || [],
            ns,
            values: (this._allTags || []).filter((t) => t.name.startsWith(ns + ":")).map((t) => t.name.slice(ns.length + 1)),
            writable: this.writable !== false,
            storageKey: `fb.notes.board-order.${this.ws}.${ns}`,
            onOpen: (id) => this.select(id),
            onMove: (note, to, opts) => this._moveCard(note, to, opts),
        });
    }

    /** A card to another column: the namespace's tags off, the column's on —
     *  one patch, tags in place, so review:pending stays. */
    async _moveCard(note, to, { focus } = {}) {
        const ns = this.boardNs;
        const add = to ? [`${ns}:${to}`] : [];
        const remove = (note.tags || []).filter((t) => t.startsWith(ns + ":") && !add.includes(t));
        try {
            const updated = await this.api.patch(note.id, { addTags: add, removeTags: remove });
            note.tags = updated.tags || [];
            note.updatedAt = updated.updatedAt;
            this.renderList();
            if (focus) this.querySelector(`.nb-card[data-id="${CSS.escape(note.id)}"]`)?.focus();
        } catch (err) {
            sac.toast?.(fb.errors.text(err, fb.t("fb.notes.board.move-failed", "Couldn't move the note.")), { kind: "error" });
        }
    }

    /** Every note, for resolving links and finding who links here — the
     *  list itself unless a tag filter or a search narrowed it. */
    async _linkPoolFor() {
        if (!this.searchActive && this.tagFilter.tags.length === 0) return this.notes;
        if (!this._linkPool) this._linkPool = this.api.list(undefined, { prompt: false }).catch(() => this.notes);
        return this._linkPool;
    }

    async _renderLinks(note) {
        const box = this.querySelector("#links");
        if (!box) return;
        const pool = await this._linkPoolFor();
        if (this._openNote?.id !== note.id) return;
        this._pool = pool;
        const out = fb.noteLinks.outgoing(note, pool);
        const back = fb.noteLinks.backlinks(note, pool);
        const fill = (group, entries) => {
            const label = group.querySelector(".nv-links-label");
            group.replaceChildren(label, ...entries.map(({ link, note: target }) => {
                if (!target) {
                    const span = document.createElement("span");
                    span.className = "nv-link-missing";
                    span.textContent = link.label;
                    span.title = fb.t("fb.notes.link-missing", "No note with this title or slug");
                    return span;
                }
                const chip = document.createElement("sac-chip");
                chip.setAttribute("clickable", "");
                chip.setAttribute("label", target.title || fb.t("fb.notes.untitled", "Untitled"));
                chip.setAttribute("color", "gray");
                chip.dataset.note = target.id;
                return chip;
            }));
            group.hidden = entries.length === 0;
        };
        fill(this.querySelector("#links-out"), out);
        fill(this.querySelector("#links-in"), back.map((n) => ({ link: null, note: n })));
        box.hidden = !out.length && !back.length;
    }

    /** A link followed in the text (the editor's sac:link): a note link
     *  opens the note it names; a web link the editor opens itself. */
    _followLink(e) {
        const link = fb.noteLinks.fromHref(e.detail?.href);
        if (!link) return;
        e.preventDefault();
        const target = fb.noteLinks.resolve(link, this._openNote, this._pool || this.notes);
        if (target) this.select(target.id);
        else sac.toast?.(fb.t("fb.notes.link-missing", "No note with this title or slug"), { kind: "error" });
    }

    /** Footer: the slug, the lock (a pill while locked; locking for any
     *  writer, unlocking for the owner or a space's Admin). */
    _paintNoteMeta(note) {
        const slug = this.querySelector("#slug");
        slug.textContent = note.slug || "";
        slug.hidden = !note.slug;
        this.querySelector("#note-locked-pill").hidden = !note.locked;
        const btn = this.querySelector("#lock-btn");
        btn.hidden = this.writable === false || !!note._hit || (note.locked && !this.canAdmin);
        btn.querySelector("sac-icon").setAttribute("name", note.locked ? "unlock" : "lock");
        btn.title = note.locked ? fb.t("fb.notes.unlock-note", "Unlock this note") : fb.t("fb.notes.lock-note", "Lock this note");
        btn.setAttribute("aria-label", btn.title);
    }

    async _toggleLock() {
        const note = this._openNote;
        if (!note || note._hit) return;
        await this.flushSave();
        const next = !note.locked;
        if (next) {
            const answer = await sac.dialog.confirm({
                title: fb.t("fb.notes.lock-title", "Lock this note?"),
                message: this.canAdmin
                    ? fb.t("fb.notes.lock-msg-admin", "Its title and text won't change any more — text can only be added at the end. Tags still can. You can unlock it again.")
                    : fb.t("fb.notes.lock-msg", "Its title and text won't change any more — text can only be added at the end. Tags still can. Only the space's admins can unlock it."),
                buttons: [
                    { action: "cancel", label: fb.t("fb.common.cancel", "Cancel"), kind: "default" },
                    { action: "lock", label: fb.t("fb.notes.lock", "Lock"), kind: "primary" },
                ],
            });
            if (answer !== "lock") return;
        }
        try {
            const updated = await this.api.patch(note.id, { locked: next });
            note.locked = !!updated.locked;
            note.updatedAt = updated.updatedAt;
            if (this._openNote === note) {
                this._applyReadOnly(note);
                this._paintNoteMeta(note);
            }
            this._swapIn(note);
            this._updateRowInPlace(note);
        } catch (err) {
            sac.toast?.(fb.errors.text(err, fb.t("fb.notes.lock-failed", "Couldn't change the lock.")), { kind: "error" });
        }
    }

    /** The footer's "N agent changes" — what agents changed, kept as versions. */
    async _refreshHistory(note) {
        const btn = this.querySelector("#history-btn");
        btn.hidden = true;
        if (note._hit) return;
        let versions = [];
        try { versions = await this.api.versions(note.id); } catch { versions = []; }
        if (this._openNote?.id !== note.id) return;
        btn.hidden = versions.length === 0;
        btn.querySelector("span").textContent = versions.length === 1
            ? fb.t("fb.notes.history-1", "1 agent change")
            : fb.t("fb.notes.history-n", "{n} agent changes", { n: versions.length });
        btn.title = fb.t("fb.notes.history", "What agents changed in this note");
    }

    async _openHistory() {
        const note = this._openNote;
        if (!note) return;
        await this.flushSave();
        fb.noteHistory.open({
            api: this.api,
            note,
            writable: this.writable !== false,
            onRestored: async (restored) => {
                this.selectedId = null;
                this._openNote = null;
                await this.loadNotes({ prompt: false });
                await this.select(restored.id);
            },
        });
    }

    formatDate(iso) {
        if (!iso) return "";
        const d = new Date(iso);
        const now = new Date();
        const sameDay = d.toDateString() === now.toDateString();
        const yesterday = new Date(now); yesterday.setDate(now.getDate() - 1);
        const isYesterday = d.toDateString() === yesterday.toDateString();
        const sameYear = d.getFullYear() === now.getFullYear();
        if (sameDay)     return fb.format.time(d);
        if (isYesterday) return fb.t("fb.common.yesterday", "Yesterday");
        if (sameYear)    return fb.format.dayMonth(d);
        return fb.format.date(d);
    }

    formatFullTimestamp(iso) {
        if (!iso) return "";
        const d = new Date(iso);
        return fb.format.dateTime(d);
    }
}

function safeJson(s) {
    try { return JSON.parse(s); } catch { return null; }
}

function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, c => ({"&":"&amp;","<":"&lt;",">":"&gt;","\"":"&quot;","'":"&#39;"}[c]));
}

const TITLE_MAX = 200;
const FENCE        = /^\s*(```|~~~)/;
const SECRET_MARKER = /^\s*:{2,3}secret#\d+:{2,3}end\s*$/i;

/** The note's title is its first line of text, stripped of markdown line
 *  markers (heading, quote, list, task). Secret blocks are skipped whole —
 *  the title is stored in plain text and travels into FTS, embeddings and
 *  MCP responses, so a secret must never become one. Which lines are secret
 *  is the one grammar's call (fb.api.secrets — what the editor masks and
 *  the client encrypts), never a looser copy of it. Fence markers are
 *  skipped too; the code inside them can still title a note. */
function splitTitle(text) {
    const lines = String(text || "").split("\n");
    const secret = new Set();
    for (const { open, close } of fb.api.secrets.ranges(text)) {
        for (let k = open; k <= close; k++) secret.add(k);
    }
    for (let i = 0; i < lines.length; i++) {
        const raw = lines[i];
        if (secret.has(i)) continue;
        if (FENCE.test(raw) || SECRET_MARKER.test(raw)) continue;
        const line = raw
            .replace(/^\s*#{1,6}\s+/, "")
            .replace(/^\s*>\s?/, "")
            .replace(/^\s*(?:[-*+]|\d+[.)])\s+(?:\[[ xX]\]\s+)?/, "")
            .trim();
        if (line) {
            const title = line.length > TITLE_MAX ? line.slice(0, TITLE_MAX).trimEnd() + "…" : line;
            return { title, body: lines.slice(i + 1).join("\n") };
        }
    }
    return { title: "", body: "" };
}

function titleFromText(text) { return splitTitle(text).title; }

/** The note awaits review (an agent wrote it or changed it). */
const isPending = (note) => !!note && (note.tags || []).includes("review:pending");

/** List snippet: the text under the title line, so the row doesn't repeat
 *  its own title. Notes from elsewhere (title not in content) use it all. */
function snippetFor(note) {
    const content = note.content || "";
    const { title, body } = splitTitle(content);
    const rest = title && title === (note.title || "").trim() ? body : content;
    // A decrypted secret must not surface in the list preview — the editor
    // blurs secret bodies, a one-line snippet can't.
    return plainText(maskSecrets(rest)).replace(/\s+/g, " ").trim().slice(0, 80);
}

/** Markdown → the words a reader sees, for the one-line list preview:
 *  no heading/list/quote markers, emphasis, code ticks, link targets,
 *  table pipes or fences. Text only — never HTML. */
function plainText(md) {
    return String(md || "")
        .replace(/^[ \t]*(```|~~~)[^\n]*$/gm, "")                        // fence lines
        .replace(/^[ \t]*\|?(?:[ \t]*:?-{3,}:?[ \t]*\|)+[ \t]*(?::?-{3,}:?)?[ \t]*$/gm, "") // table rule
        .replace(/^[ \t]*([-*_])(?:[ \t]*\1){2,}[ \t]*$/gm, "")         // thematic break
        .replace(/^[ \t]{0,3}#{1,6}[ \t]+/gm, "")                        // headings
        .replace(/^[ \t]*>[ \t]?/gm, "")                                 // quotes
        .replace(/^[ \t]*(?:[-*+]|\d+[.)])[ \t]+(?:\[[ xX]\][ \t]+)?/gm, "") // list items, tasks
        .replace(/!\[([^\]]*)\]\([^)]*\)/g, "$1")                        // images → alt
        .replace(/\[([^\]]+)\]\([^)]*\)/g, "$1")                         // links → text
        .replace(/\[([^\]]+)\]\[[^\]]*\]/g, "$1")                        // reference links
        .replace(/`+([^`]*)`+/g, "$1")                                   // inline code
        .replace(/(\*\*|__)(.+?)\1/g, "$2")                              // bold
        .replace(/(^|[^\w*])[*_](?=\S)([^*_\n]*?\S)[*_](?=[^\w*]|$)/g, "$1$2") // italic
        .replace(/~~(.+?)~~/g, "$1")                                     // strike
        .replace(/[ \t]*\|[ \t]*/g, " ")                                 // table cells
        .replace(/&nbsp;/g, " ").replace(/&lt;/g, "<").replace(/&gt;/g, ">").replace(/&amp;/g, "&");
}

const SECRET_MARKER_RE = /:{2,3}secret#\d+:{2,3}end/gi;
// Display only: the editor shows it for a locked secret; never saved.
const lockedPlaceholder = () => fb.t("fb.notes.locked-placeholder", "locked — unlock to show");
// …and for a secret whose ciphertext doesn't decrypt (the note stays read-only).
const unreadablePlaceholder = () => fb.t("fb.notes.unreadable-placeholder", "can't be decrypted — left as it is");

/** Every secret block (the one grammar, fb.api.secrets — an unclosed block
 *  runs to the end) and every stored marker as a lock. */
function maskSecrets(text) {
    return fb.api.secrets.replace(String(text || ""), () => "🔒").replace(SECRET_MARKER_RE, "🔒")
        .replace(/\[secret content hidden\]/g, "🔒");   // SecretStripper's placeholder in a search hit
}

/** A cached note with its decrypted secrets masked, for after a lock: what
 *  the list shows until the note is loaded again (opening it asks for the
 *  key). Marked `_hit` like a search hit — never written back as it is. */
function lockedCopy(note) {
    return {
        ...note,
        content: fb.api.secrets.replace(note.content || "", () => `:::secret\n${lockedPlaceholder()}\n:::end`),
        contentSecret: null,
        _hit: true,
        _locked: true,
    };
}

/** The note came back with `:::secret#N:::end` markers still in place —
 *  its ciphertext wasn't decrypted because the vault is locked (or because
 *  a block doesn't decrypt: `secretsUnreadable`). */
function hasLockedSecrets(note) {
    return !!note?.contentSecret && /:{2,3}secret#\d+:{2,3}end/.test(note.content || "");
}

/** What the editor shows for a note. Notes written here already open with
 *  their title line. Notes from elsewhere (MCP `remember`, the Discord bot,
 *  seed data) carry title and content separately — the title is put back on
 *  top as a heading so the first-line rule holds for them too. */
function editorTextFor(note) {
    const title = (note.title || "").trim();
    const content = displayContent(note);
    if (!title || titleFromText(content) === title) return content;
    return content ? `# ${title}\n\n${content}` : `# ${title}\n`;
}

/** The note's text as the editor shows it, without a title put on top.
 *  Locked secrets show as ordinary (blurred) secret blocks with a
 *  placeholder body, not as the raw `:::secret#N:::end` storage marker.
 *  Display only: saveSelected never writes a locked note's text back. */
function displayContent(note) {
    const placeholder = note.secretsUnreadable ? unreadablePlaceholder() : lockedPlaceholder();
    return hasLockedSecrets(note)
        ? (note.content || "").replace(SECRET_MARKER_RE, `:::secret\n${placeholder}\n:::end`)
        : note.content || "";
}

/** The editor's text → what is saved. A note written here is its text,
 *  titled by its first line. A note shown with its title on top keeps its
 *  title out of its text: while the first line is still a heading, that
 *  line is the title and the text is the rest, exactly as editorTextFor
 *  joined them — so a note an agent wrote never gains a copy of its title.
 *  A first line that is no heading any more makes it a note like any other. */
function fromEditorText(text, titleOnTop, shownTitle) {
    if (titleOnTop) {
        const lines = text.split("\n");
        if (/^#{1,6}[ \t]/.test(lines[0])) {
            const rest = lines.slice(1);
            if (rest[0] === "") rest.shift();
            const content = rest.join("\n");
            // The heading as it was put there keeps the title as stored.
            const title = lines[0] === `# ${(shownTitle || "").trim()}` ? shownTitle
                : titleFromText(lines[0]) || titleFromText(content);
            if (title) return { title, content, titleOnTop: true };
        }
    }
    return { title: titleFromText(text), content: text, titleOnTop: false };
}

customElements.define("fb-notes-view", FbNotesView);
sac.router.register("#/notes", "fb-notes-view", { label: "Notes", icon: "note", palette: false });
