/**
 * <fb-notes-view>  (mounted at #/notes)
 *
 * iCloud-style two-pane notes UI:
 *   - List pane: search, "All Notes" header with filter + new-note actions,
 *     rich items (title + date + snippet + pin indicator).
 *   - Editor pane: centered timestamp, title + content inputs, delete and
 *     pin actions in the top-left of the header.
 *
 * Pinned notes always sort first; archived are hidden unless the archive
 * button is toggled. Typing in the search input hits the hybrid ranker at
 * /api/v1/search (vector + FTS blend) — results keep server score order
 * and skip the pinned-first boost so relevance stays visible.
 *
 * Light-DOM so app.css tokens apply; all component-specific CSS is scoped
 * with `fb-notes-view` to avoid leaking into other views.
 */
class FbNotesView extends HTMLElement {
    constructor() {
        super();
        this.notes = [];
        this.selectedId = null;
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
        // Clamp + "more"/"less" tab is owned by <fb-collapsible> around
        // the chip strip; expanded state lives on that element, not here.
        this._saveDebounce = null;
        this._onTagsInvalidated = () => this._renderTagFilter();
    }

    async connectedCallback() {
        this.render();
        window.addEventListener("fb-tags-invalidated", this._onTagsInvalidated);
        this._setViewToolbar();
        await this.loadNotes();
        this._renderTagFilter();
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
                title:    "Undo (Ctrl+Z)",
                onClick:  () => this._editorUndo(),
                disabled: true,
            },
            {
                icon:     "redo",
                title:    "Redo (Ctrl+Y)",
                onClick:  () => this._editorRedo(),
                disabled: true,
            },
            // Manual refresh - placeholder until we wire auto-refresh to
            // MCP writes landing. Order is left-to-right as rendered.
            {
                icon:    "sync",
                title:   "Refresh notes",
                onClick: () => this.loadNotes(),
            },
            {
                icon:    "settings",
                title:   "Manage tags",
                onClick: () => this._openManageDialog(),
            },
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
        // Router already clears on swap, but guard against any other unmount.
        // Fire-and-forget any pending autosave so a quick view-switch mid-typing
        // doesn't drop edits.
        this.flushSave();
        window.removeEventListener("fb-tags-invalidated", this._onTagsInvalidated);
        if (window.fb?.toolbar) fb.toolbar.clear();
    }

    async loadNotes() {
        try {
            const opts = this.tagFilter.tags.length > 0
                ? { tags: this.tagFilter.tags, match: "all" }
                : undefined;
            this.notes = await fb.api.notes.list(opts);
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
            this.notes = kept;
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
     *    2) Score must clear max(topScore * 0.6, 0.3). Relative-to-top
     *       handles the "top is 0.7, rank 2 is 0.43" semantic case; the
     *       0.3 floor catches the pathological "top is 0.5, everything
     *       below is pure noise at 0.4–0.49". */
    _applyRelevanceCutoff(results) {
        const nonEmpty = results.filter(n =>
            (n.title && n.title.trim()) || (n.content && n.content.trim())
        );
        if (nonEmpty.length === 0) return [];
        const top = nonEmpty[0].score ?? 0;
        const cutoff = Math.max(top * 0.7, 0.5);
        return nonEmpty.filter(n => (n.score ?? 0) >= cutoff);
    }

    _updateDegradedHint() {
        const hint = this.querySelector("#search-degraded-hint");
        if (!hint) return;
        hint.hidden = !(this.searchActive && this.searchDegraded);
    }

    render() {
        this.innerHTML = `
            <style>
                fb-notes-view { display: block; height: 100%; }
                /* [hidden] must beat our scoped display rules */
                fb-notes-view [hidden] { display: none !important; }
                fb-notes-view .nv-layout {
                    display: flex;
                    height: calc(100vh - 50px);
                    background: var(--bg);
                }

                /* --- LIST PANE ------------------------------------------------- */
                fb-notes-view .nv-list-pane {
                    width: 340px;
                    background: var(--panel);
                    border-right: 1px solid var(--border);
                    display: flex;
                    flex-direction: column;
                    flex-shrink: 0;
                }
                fb-notes-view .nv-search {
                    position: relative;
                    padding: 12px 12px 0;
                }
                fb-notes-view .nv-search fb-icon {
                    position: absolute;
                    left: 22px;
                    top: 12px;
                    height: 32px;
                    display: flex;
                    align-items: center;
                    color: var(--text-muted);
                    --icon-size: 14px;
                    pointer-events: none;
                }
                fb-notes-view .nv-search input {
                    width: 100%;
                    background: rgba(0, 0, 0, 0.3);
                    border: 1px solid var(--border);
                    border-radius: 8px;
                    padding: 7px 10px 7px 32px;
                    color: var(--text);
                    font-family: inherit;
                    font-size: 13px;
                    outline: none;
                    transition: border-color 0.15s;
                }
                fb-notes-view .nv-search input::placeholder { color: var(--text-muted); }
                fb-notes-view .nv-search input:focus { border-color: var(--accent); }
                fb-notes-view .nv-search-hint {
                    padding: 6px 12px 0;
                    font-size: 11px;
                    color: var(--text-muted);
                    font-style: italic;
                }
                fb-notes-view .nv-search-hint[hidden] { display: none !important; }

                fb-notes-view .nv-list-header {
                    display: flex;
                    align-items: center;
                    padding: 12px 16px 6px;
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
                fb-notes-view .nv-icon-btn {
                    padding: 5px 7px;
                    border-radius: 6px;
                    background: transparent;
                    border: none;
                    color: var(--text-muted);
                    cursor: pointer;
                    display: inline-flex;
                    align-items: center;
                    transition: background 0.15s, color 0.15s;
                }
                fb-notes-view .nv-icon-btn:hover {
                    background: rgba(255, 255, 255, 0.06);
                    color: var(--text);
                }
                fb-notes-view .nv-icon-btn.active {
                    background: rgba(249, 115, 22, 0.15);
                    color: var(--accent-warm);
                }
                fb-notes-view .nv-icon-btn fb-icon { --icon-size: 16px; }

                fb-notes-view .nv-items {
                    flex: 1;
                    overflow-y: auto;
                    padding: 2px 8px 12px;
                }

                fb-notes-view .nv-item {
                    position: relative;
                    padding: 10px 12px;
                    border-radius: 10px;
                    cursor: pointer;
                    margin-bottom: 2px;
                    border: 1px solid transparent;
                    transition: background 0.12s, border-color 0.12s;
                }
                fb-notes-view .nv-item:hover { background: rgba(255, 255, 255, 0.04); }
                fb-notes-view .nv-item.selected {
                    background: rgba(59, 130, 246, 0.12);
                    border-color: rgba(59, 130, 246, 0.28);
                }
                fb-notes-view .nv-item-title-row {
                    display: flex;
                    align-items: center;
                    gap: 6px;
                    margin-bottom: 4px;
                    /* Reserve space so the absolute action row never collides with
                       the title. Pin indicator alone is ~22px; on hover the full
                       3-icon row needs more, but title already ellipsis-truncates. */
                    padding-right: 22px;
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

                /* Hover action row: pin (persistent when active), archive, delete.
                   Buttons are opacity:0 by default and fade in when the row is
                   hovered. The active pin overrides that so a pinned note always
                   shows its indicator. */
                fb-notes-view .nv-item-actions {
                    position: absolute;
                    top: 6px;
                    right: 6px;
                    display: flex;
                    gap: 1px;
                }
                fb-notes-view .nv-item-action {
                    padding: 4px;
                    background: transparent;
                    border: none;
                    border-radius: 5px;
                    color: var(--text-muted);
                    cursor: pointer;
                    display: inline-flex;
                    align-items: center;
                    opacity: 0;
                    transition: opacity 0.12s, background 0.12s, color 0.12s;
                }
                fb-notes-view .nv-item-action fb-icon { --icon-size: 13px; }
                fb-notes-view .nv-item:hover .nv-item-action { opacity: 1; }
                fb-notes-view .nv-item-action:hover {
                    background: rgba(255, 255, 255, 0.1);
                    color: var(--text);
                }
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
                    color: #22c55e;
                    background: rgba(34, 197, 94, 0.14);
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
                    padding: 40px 16px;
                    text-align: center;
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
                    flex: 1;
                    display: flex;
                    flex-direction: column;
                    min-width: 0;
                }
                fb-notes-view .nv-editor-footer {
                    display: flex;
                    align-items: center;
                    gap: 12px;
                    min-height: 32px;
                    padding: 8px 20px;
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
                    padding: 10px 20px;
                    background: var(--panel);
                    border-top: 1px solid var(--border);
                    flex-shrink: 0;
                    min-height: 44px;
                }
                fb-notes-view .nv-editor-tagbar fb-tag-input {
                    flex: 1 1 200px;
                    min-width: 200px;
                }

                /* --- TAG FILTER STRIP (list pane) ----------------------------- */
                /* Just the chip row layout — clamp, fade, separator line
                 * and the "more"/"less" tab all come from <fb-collapsible>. */
                fb-notes-view .nv-tag-filter {
                    display: flex;
                    flex-wrap: wrap;
                    align-items: center;
                    gap: 4px;
                    padding: 10px 12px 8px;
                }
                fb-notes-view .nv-tag-collapsible[hidden] { display: none !important; }
                fb-notes-view .nv-editor-body {
                    flex: 1;
                    overflow: auto;
                    padding: 36px 56px;
                    display: flex;
                    flex-direction: column;
                }
                fb-notes-view .nv-empty-state {
                    flex: 1;
                    display: flex;
                    flex-direction: column;
                    align-items: center;
                    justify-content: center;
                    color: var(--text-muted);
                    gap: 12px;
                }
                fb-notes-view .nv-empty-state fb-icon {
                    --icon-size: 72px;
                    opacity: 0.2;
                }
                fb-notes-view .nv-empty-state p {
                    margin: 0;
                    font-size: 14px;
                }
                fb-notes-view .nv-title-input {
                    width: 100%;
                    font-family: 'Outfit', sans-serif;
                    font-weight: 800;
                    font-size: 1.75rem;
                    letter-spacing: -0.02em;
                    background: none;
                    border: none;
                    color: var(--text);
                    outline: none;
                    margin-bottom: 14px;
                    padding: 0;
                }
                fb-notes-view .nv-title-input::placeholder {
                    color: var(--text-muted);
                    opacity: 0.55;
                }
                fb-notes-view .nv-content-input {
                    /* fb-md-editor handles its own autosize and renders the
                       Edit/Preview toggle. Styles here are a safety-net in
                       case the component fails to upgrade (the element would
                       show as an unknown inline element with no layout). */
                    display: block;
                    width: 100%;
                    min-height: 60vh;
                }

                /* Archived-note editor is read-only. The component's own
                   readonly attribute hides its toggle and forces preview;
                   the title input still gets the attr directly. */
                fb-notes-view .nv-editor.readonly .nv-title-input {
                    opacity: 0.7;
                    cursor: not-allowed;
                }

                /* Small pill in the editor footer when viewing an archived
                   note, so the read-only state is explained, not just felt. */
                fb-notes-view .nv-archived-pill {
                    display: inline-flex;
                    align-items: center;
                    gap: 4px;
                    padding: 2px 8px;
                    border-radius: 999px;
                    background: rgba(255, 255, 255, 0.06);
                    border: 1px solid var(--border);
                    color: var(--text-muted);
                    font-size: 10px;
                    font-weight: 600;
                    text-transform: uppercase;
                    letter-spacing: 0.08em;
                }
                fb-notes-view .nv-archived-pill fb-icon { --icon-size: 10px; }

                fb-notes-view .nv-editor {
                    /* No flex:1/column here — we want content to stack normally
                       and push the outer .nv-editor-body to scroll when it
                       overflows, rather than forcing the textarea to scroll
                       internally. */
                    display: block;
                }
            </style>

            <div class="nv-layout">
                <aside class="nv-list-pane">
                    <div class="nv-search">
                        <fb-icon name="search"></fb-icon>
                        <input type="search" id="search-input" placeholder="Search all notes"/>
                    </div>
                    <div class="nv-search-hint" id="search-degraded-hint" hidden>
                        Full-text ranking — embeddings still loading.
                    </div>
                    <fb-collapsible class="nv-tag-collapsible" id="tag-filter-wrapper" max-height="82px" hidden>
                        <div class="nv-tag-filter" id="tag-filter"></div>
                    </fb-collapsible>
                    <div class="nv-list-header">
                        <span class="nv-list-title" id="list-title">All Notes</span>
                        <button class="nv-icon-btn" id="toggle-archived-btn" title="Show archived">
                            <fb-icon name="archive"></fb-icon>
                        </button>
                        <button class="nv-icon-btn" id="new-btn" title="New note">
                            <fb-icon name="plus"></fb-icon>
                        </button>
                    </div>
                    <div class="nv-items" id="note-list"></div>
                </aside>

                <main class="nv-editor-pane">
                    <div class="nv-editor-body">
                        <div class="nv-empty-state" id="editor-empty">
                            <fb-icon name="note"></fb-icon>
                            <p>Select a note to start writing</p>
                        </div>
                        <div id="editor" class="nv-editor" hidden>
                            <input id="title" class="nv-title-input" placeholder="Untitled"/>
                            <fb-md-editor id="content" class="nv-content-input" placeholder="Start writing..."></fb-md-editor>
                        </div>
                    </div>
                    <section class="nv-editor-tagbar" id="tagbar" hidden>
                        <fb-tag-input id="tag-input"></fb-tag-input>
                    </section>
                    <footer class="nv-editor-footer" id="editor-footer" hidden>
                        <span class="nv-editor-footer-meta">
                            Updated <span id="timestamp"></span>
                        </span>
                        <span class="nv-archived-pill" id="archived-pill" hidden>
                            <fb-icon name="archive"></fb-icon> Archived · Read-only
                        </span>
                        <div class="nv-editor-footer-spacer"></div>
                    </footer>
                </main>
            </div>
        `;
        this.attachHandlers();
    }

    attachHandlers() {
        this.querySelector("#new-btn").addEventListener("click", () => this.createNote());
        this.querySelector("#toggle-archived-btn").addEventListener("click", () => {
            this.showArchived = !this.showArchived;
            this.querySelector("#toggle-archived-btn").classList.toggle("active", this.showArchived);
            this.querySelector("#list-title").textContent = this.showArchived ? "All + Archived" : "All Notes";
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
        const titleEl = this.querySelector("#title");
        titleEl.addEventListener("blur",  () => this.flushSave());
        titleEl.addEventListener("input", () => this.scheduleAutoSave());
        const contentEl = this.querySelector("#content");
        // fb-md-editor re-dispatches `input` on every keystroke and `change`
        // on blur-after-edit from its host, matching native textarea
        // semantics. Autosize is handled inside the component.
        contentEl.addEventListener("change", () => this.flushSave());
        contentEl.addEventListener("input",  () => this.scheduleAutoSave());
        contentEl.addEventListener("history-change", (e) => this._refreshHistoryToolbar(e.detail));
        const tagInput = this.querySelector("#tag-input");
        tagInput.addEventListener("change", () => {
            // Don't mutate note.tags here — saveSelected compares the
            // current input value against the stored note to decide whether
            // the PUT is needed. Pre-mutating would always produce "no
            // change" and silently drop every tag edit.
            if (this.notes.some(n => n.id === this.selectedId)) {
                this.scheduleAutoSave();
            }
        });
        // Pin + trash also live in fb-nav's toolbar (see updateToolbar) and on
        // each list row (see renderList's action buttons).
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
            `<fb-tag-chip name="${t.name}" color="${t.color}" clickable
                          ${selected.has(t.name) ? "selected" : ""}></fb-tag-chip>`
        ).join("");
        // <fb-collapsible> listens to slotchange + ResizeObserver, so it
        // re-measures overflow on its own after the innerHTML swap.

        strip.querySelectorAll("fb-tag-chip").forEach(chip => {
            chip.addEventListener("click", async () => {
                const name = chip.getAttribute("name");
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
        let dlg = this.querySelector("fb-tag-manage-dialog");
        if (!dlg) {
            dlg = document.createElement("fb-tag-manage-dialog");
            this.appendChild(dlg);
            dlg.addEventListener("tags-changed", async () => {
                // Names/colors may have shifted; reload notes (and re-render
                // chips on the active note) so stale chips don't linger.
                await this.loadNotes();
                this._renderTagFilter();
                if (this.selectedId) {
                    const refreshed = await fb.api.notes.get(this.selectedId).catch(() => null);
                    if (refreshed) {
                        const idx = this.notes.findIndex(n => n.id === this.selectedId);
                        if (idx >= 0) this.notes[idx] = refreshed;
                        this.querySelector("#tag-input").value = refreshed.tags || [];
                    }
                }
            });
        }
        dlg.open();
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

    renderList() {
        const filtered = this.notes.filter(n => {
            if (!this.showArchived && n.archived) return false;
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

        const list = this.querySelector("#note-list");
        if (filtered.length === 0) {
            list.innerHTML = `<div class="nv-empty-list">No notes. Click + to create one.</div>`;
            return;
        }

        list.innerHTML = filtered.map(n => {
            const snippet = (n.content || "").replace(/\s+/g, " ").slice(0, 80);
            const date = this.formatDate(n.updatedAt);
            const isSelected = n.id === this.selectedId;
            const archiveTitle = n.archived ? "Unarchive" : "Archive";
            const pinTitle = n.pinned ? "Unpin" : "Pin to top";
            const rowClasses = [
                "nv-item",
                isSelected  ? "selected" : "",
                n.archived  ? "archived" : "",
            ].filter(Boolean).join(" ");
            const tagLine = this._renderRowTagLine(n.tags);
            const isPending = (n.tags || []).includes("review:pending");
            // Approve only surfaces when the note needs review. One click
            // strips review:pending via the existing Human-update path
            // (NoteRepository.ApplySourceTags does the strip server-side).
            const approveBtnHtml = isPending
                ? `<button class="nv-item-action approve" data-action="approve" title="Approve (clear review:pending)"><fb-icon name="check"></fb-icon></button>`
                : "";
            return `
                <div class="${rowClasses}" data-id="${n.id}">
                    <div class="nv-item-title-row">
                        <span class="nv-item-title">${escapeHtml(n.title || "Untitled")}</span>
                    </div>
                    <div class="nv-item-preview">
                        <span class="nv-item-date">${date}</span>
                        <span class="nv-item-snippet">${escapeHtml(snippet || "No additional text")}</span>
                    </div>
                    ${tagLine}
                    <div class="nv-item-actions">
                        ${approveBtnHtml}
                        <button class="nv-item-action pin ${n.pinned ? "active" : ""}" data-action="pin" title="${pinTitle}"><fb-icon name="pin"></fb-icon></button>
                        <button class="nv-item-action archive ${n.archived ? "active" : ""}" data-action="archive" title="${archiveTitle}"><fb-icon name="archive"></fb-icon></button>
                        <button class="nv-item-action delete" data-action="delete" title="Delete"><fb-icon name="trash"></fb-icon></button>
                    </div>
                </div>
            `;
        }).join("");

        list.querySelectorAll(".nv-item").forEach(el => {
            el.addEventListener("click", (e) => {
                if (e.target.closest(".nv-item-action")) return;
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
        if (this.selectedId === id) return;
        // Persist any pending edits on the outgoing note BEFORE we swap the
        // editor's DOM values — otherwise the pending debounced save would fire
        // against the incoming note's values.
        await this.flushSave();
        this.selectedId = id;
        const note = this.notes.find(n => n.id === id);
        if (!note) return;
        this.querySelector("#editor-empty").hidden  = true;
        this.querySelector("#editor").hidden        = false;
        this.querySelector("#tagbar").hidden        = false;
        this.querySelector("#editor-footer").hidden = false;
        this.querySelector("#title").value   = note.title   || "";
        this.querySelector("#content").value = note.content || "";
        this.querySelector("#tag-input").value = note.tags || [];
        this.querySelector("#timestamp").textContent = this.formatFullTimestamp(note.updatedAt);
        this._applyReadOnly(note);
        this.updateToolbar(note);
        // fb-md-editor autosizes itself on value-set (deferred to next
        // frame internally), so no explicit autosize hook needed here.
        this.renderList();
    }

    /** Toggle editor inputs + footer pill based on whether the note is archived. */
    _applyReadOnly(note) {
        const ro = !!note.archived;
        const editor = this.querySelector("#editor");
        editor.classList.toggle("readonly", ro);
        const title   = this.querySelector("#title");
        const content = this.querySelector("#content");
        title.toggleAttribute("readonly", ro);
        content.toggleAttribute("readonly", ro);
        this.querySelector("#archived-pill").hidden = !ro;
    }

    clearSelection() {
        this.selectedId = null;
        this.querySelector("#editor-empty").hidden  = false;
        this.querySelector("#editor").hidden        = true;
        this.querySelector("#tagbar").hidden        = true;
        this.querySelector("#editor-footer").hidden = true;
        this.querySelector("#timestamp").textContent = "";
        const tagInput = this.querySelector("#tag-input");
        if (tagInput) tagInput.value = [];
        // Toolbar stays — manage-tags is view-scoped, not per-note.
    }

    async saveSelected() {
        if (!this.selectedId) return;
        // Any debounced save firing is equivalent to an explicit flush.
        clearTimeout(this._saveDebounce);
        this._saveDebounce = null;
        const note = this.notes.find(n => n.id === this.selectedId);
        if (!note) return;
        const newTitle   = this.querySelector("#title").value;
        const newContent = this.querySelector("#content").value;
        const newTags    = this.querySelector("#tag-input").value;
        const tagsChanged = !this._sameTags(note.tags || [], newTags);
        if (newTitle === note.title && newContent === note.content && !tagsChanged) return;
        note.title   = newTitle;
        note.content = newContent;
        note.tags    = newTags;
        try {
            await fb.api.notes.update(note.id, note);
            note.updatedAt = new Date().toISOString();
            this.querySelector("#timestamp").textContent = this.formatFullTimestamp(note.updatedAt);
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
            // next explicit render (select, delete, archive, pin).
            this._updateRowInPlace(note);
        } catch (err) {
            console.error("[fb-notes-view] update failed:", err);
        }
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
        if (titleEl) titleEl.textContent = note.title || "Untitled";
        if (dateEl)  dateEl.textContent  = this.formatDate(note.updatedAt);
        if (snippetEl) {
            const snippet = (note.content || "").replace(/\s+/g, " ").slice(0, 80);
            snippetEl.textContent = snippet || "No additional text";
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
    togglePinned()    { if (this.selectedId) this.togglePinnedById(this.selectedId); }
    deleteSelected()  { if (this.selectedId) this.deleteById(this.selectedId); }

    async togglePinnedById(id) {
        // If the target is the active editor, flush text edits first so they
        // ride in the same PUT as the pinned flip (one round-trip, no clobber).
        if (id === this.selectedId) await this.flushSave();
        const note = this.notes.find(n => n.id === id);
        if (!note) return;
        note.pinned = !note.pinned;
        try {
            await fb.api.notes.update(note.id, note);
            if (id === this.selectedId) this.updateToolbar(note);
            this.renderList();
        } catch (err) {
            console.error("[fb-notes-view] pin toggle failed:", err);
            note.pinned = !note.pinned;
        }
    }

    async toggleArchivedById(id) {
        if (id === this.selectedId) await this.flushSave();
        const note = this.notes.find(n => n.id === id);
        if (!note) return;
        note.archived = !note.archived;
        try {
            await fb.api.notes.update(note.id, note);
            // If we archived the currently-open note and archive view is off,
            // it just disappeared from the list — clear the editor too.
            if (id === this.selectedId && note.archived && !this.showArchived) {
                this.clearSelection();
            } else if (id === this.selectedId) {
                // Still visible (either unarchived, or archived with
                // showArchived on) — refresh the editor's read-only state.
                this._applyReadOnly(note);
            }
            this.renderList();
        } catch (err) {
            console.error("[fb-notes-view] archive toggle failed:", err);
            note.archived = !note.archived;
        }
    }

    async approveById(id) {
        // Editing is implicit approval; this button is just an explicit UX
        // handle for users filtering on `review:pending`. Flush any pending
        // autosave first so our tag change rides in the same PUT.
        if (id === this.selectedId) await this.flushSave();

        const note = this.notes.find(n => n.id === id);
        if (!note || !(note.tags || []).includes("review:pending")) return;

        const previousTags = [...note.tags];
        note.tags = note.tags.filter(t => t !== "review:pending");
        try {
            await fb.api.notes.update(note.id, note);
            // If the selected note just lost review:pending and the current
            // filter selects that tag, it'll vanish from the list on the
            // next render — renderList handles that. For the editor's
            // tag-input we just re-apply the new list.
            if (id === this.selectedId) {
                this.querySelector("#tag-input").value = note.tags;
            }
            this.renderList();
        } catch (err) {
            console.error("[fb-notes-view] approve failed:", err);
            note.tags = previousTags;
        }
    }

    async deleteById(id) {
        const note = this.notes.find(n => n.id === id);
        if (!note) return;

        // Offer Archive as the safer alternative, unless the note is already
        // archived (then it'd be a no-op and clutters the dialog).
        const buttons = [{ action: "cancel", label: "Cancel", kind: "default" }];
        if (!note.archived) {
            buttons.push({ action: "archive", label: "Archive", kind: "primary" });
        }
        // armAfterMs gives the user 2s to read before Delete becomes the
        // Enter default — see spec 2026-04-20-fb-dialog-design.md.
        buttons.push({ action: "delete", label: "Delete", kind: "destructive", armAfterMs: 2000 });

        const message = note.archived
            ? "This note will be permanently deleted."
            : "This note will be permanently deleted. Archive it instead to keep it hidden but recoverable.";

        const result = await fb.dialog.confirm({ title: "Delete this note?", message, buttons });

        if (result === "archive") return this.toggleArchivedById(id);
        if (result !== "delete") return;

        // Cancel any pending autosave for this note so it doesn't re-create it
        // after the DELETE round-trip.
        if (id === this.selectedId) {
            clearTimeout(this._saveDebounce);
            this._saveDebounce = null;
        }
        try {
            await fb.api.notes.delete(id);
            this.notes = this.notes.filter(n => n.id !== id);
            if (id === this.selectedId) this.clearSelection();
            this.renderList();
        } catch (err) {
            console.error("[fb-notes-view] delete failed:", err);
        }
    }

    async createNote() {
        try {
            const created = await fb.api.notes.create({ title: "", content: "" });
            this.notes.unshift(created);
            // Let select() set selectedId so its dedupe guard doesn't short-circuit.
            await this.select(created.id);
            this.querySelector("#title").focus();
        } catch (err) {
            console.error("[fb-notes-view] create failed:", err);
        }
    }

    formatDate(iso) {
        if (!iso) return "";
        const d = new Date(iso);
        const now = new Date();
        const sameDay = d.toDateString() === now.toDateString();
        const yesterday = new Date(now); yesterday.setDate(now.getDate() - 1);
        const isYesterday = d.toDateString() === yesterday.toDateString();
        const sameYear = d.getFullYear() === now.getFullYear();
        if (sameDay)     return d.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" });
        if (isYesterday) return "Yesterday";
        if (sameYear)    return d.toLocaleDateString(undefined, { month: "short", day: "numeric" });
        return d.toLocaleDateString(undefined, { year: "2-digit", month: "numeric", day: "numeric" });
    }

    formatFullTimestamp(iso) {
        if (!iso) return "";
        const d = new Date(iso);
        return d.toLocaleDateString(undefined, { year: "numeric", month: "long", day: "numeric" })
             + " at "
             + d.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" });
    }
}

function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, c => ({"&":"&amp;","<":"&lt;",">":"&gt;","\"":"&quot;","'":"&#39;"}[c]));
}

customElements.define("fb-notes-view", FbNotesView);
fb.router.register("#/notes", "fb-notes-view", { label: "Notes", icon: "note" });
