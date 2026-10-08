/**
 * <fb-todos-view>  (mounted at #/todos)
 *
 * Two-pane todos UI mirroring fb-notes-view, on the kit's <sac-split> —
 * side by side on a wide screen, one pane at a time on a phone (opening a
 * todo brings the editor forward, the back bar returns to the list):
 *   - List pane: search, "All Todos" header with "hide completed" filter +
 *     new-todo action, rich items (checkbox + title + due-date).
 *   - Editor pane: centered timestamp, title input, due-date input,
 *     description textarea. Toolbar exposes "Completed" toggle + delete.
 *
 * Completed todos are always hidden unless the "hide completed" filter
 * is toggled off. When shown, they render dimmed + strikethrough.
 *
 * Light-DOM so the kit's tokens and classes apply; all view-specific CSS
 * scoped with `fb-todos-view`.
 */
class FbTodosView extends HTMLElement {
    constructor() {
        super();
        this.todos = [];
        this.selectedId = null;
        this.hideCompleted = true;
        this.searchQuery = "";
        this._saveDebounce = null;
    }

    async connectedCallback() {
        // The workspace this view shows, pinned: a save flushed on leaving
        // still goes there, not to the workspace the hash moved on to.
        this.api = fb.api.todos.in(fb.api.workspace());
        this.render();
        // A space Reader gets no write actions (the server would refuse them).
        this.writable = await fb.access.canWrite();
        this.querySelector("#new-btn").hidden = !this.writable;
        this.querySelector("#empty-new-btn").hidden = !this.writable;
        for (const id of ["#title", "#description"]) this.querySelector(id)?.toggleAttribute("readonly", !this.writable);
        this.querySelector("#due-at")?.toggleAttribute("disabled", !this.writable);
        this.toggleAttribute("readonly", !this.writable);   // CSS hides #due-clear
        // Drag an open todo to reorder (the kit's sortable: mouse drags after
        // a 4px move, touch after a long-press, Escape cancels). Done todos
        // stay where they are, below the open ones.
        if (this.writable) this._sortable = sac.sortable(this.querySelector("#todo-list"), {
            items: ".tv-item:not(.completed)",
            onReorder: (from, to) => this._moveTodo(from, to),
        });
        await this.loadTodos();
        if (!this.isConnected) return;   // left during the load — don't hook a dead view
        // "New todo" from the Ctrl-K palette (fb.desktop.go).
        this._onIntent = () => { if (fb.desktop?.takeIntent("todos")?.action === "create" && this.writable) this.createTodo(); };
        window.addEventListener("fb:intent", this._onIntent);
        this._onIntent();
    }

    disconnectedCallback() {
        for (const t of this._leaving?.values() || []) clearTimeout(t);
        this._leaving?.clear();
        if (this._onIntent) window.removeEventListener("fb:intent", this._onIntent);
        this._sortable?.destroy();
        this.flushSave();
        if (window.fb?.toolbar) fb.toolbar.clear();
    }

    async loadTodos() {
        try {
            // Completed ones too: the list filters them itself ("Show completed").
            this.todos = await this.api.list({ includeCompleted: true });
            this.renderList();
        } catch (err) {
            console.error("[fb-todos-view] list failed:", err);
        }
    }

    render() {
        this.innerHTML = `
            <style>
                /* The view fills the space below the fixed nav; the split
                   inside takes it over (sac-split sizes to its parent). */
                fb-todos-view {
                    display: block;
                    height: calc(100vh - 50px);
                    height: calc(100dvh - 50px - env(safe-area-inset-top, 0px));
                    background: var(--bg);
                }
                fb-todos-view [hidden] { display: none !important; }

                /* --- LIST PANE ------------------------------------------------ */
                fb-todos-view .tv-list-pane {
                    /* Exactly the pane's height: the search and the header stay, only the list scrolls. */
                    height: 100%;
                    background: var(--panel);
                    display: flex;
                    flex-direction: column;
                }
                fb-todos-view .tv-search {
                    position: relative;
                    padding: 12px 12px 0;
                }
                fb-todos-view .tv-search sac-icon {
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
                fb-todos-view .tv-search input {
                    padding-left: 32px;
                    font-size: max(13px, 0.8125rem);
                    outline: none;
                }
                @media (pointer: coarse) {
                    fb-todos-view .tv-search input { font-size: max(16px, 1rem); }
                }
                fb-todos-view .tv-search input::placeholder { color: var(--text-muted); }

                /* One 12px gutter for the whole list pane: the search field,
                   the header's buttons and the (highlighted) rows all end
                   12px from the edge; the header label lines up with the
                   rows' text (12px gutter + 12px row padding). */
                fb-todos-view .tv-list-header {
                    display: flex;
                    align-items: center;
                    padding: 12px 12px 6px 24px;
                    gap: 2px;
                }
                fb-todos-view .tv-list-title {
                    flex: 1;
                    font-family: 'Outfit', sans-serif;
                    font-weight: 700;
                    font-size: 11px;
                    text-transform: uppercase;
                    letter-spacing: 0.1em;
                    color: var(--text-muted);
                }
                /* Header buttons are the kit's .icon-btn (44px hit area on
                   touch); the completed toggle's "on" state is ours. */
                fb-todos-view .tv-list-header .icon-btn { color: var(--text-muted); }
                fb-todos-view .tv-list-header .icon-btn.active {
                    background: color-mix(in srgb, var(--ok) 15%, transparent);
                    color: var(--ok-text);
                }

                fb-todos-view .tv-items {
                    flex: 1;
                    min-height: 0;
                    overflow-y: auto;
                    padding: 2px 12px 12px;
                }

                fb-todos-view .tv-item {
                    position: relative;
                    padding: 10px 12px 10px 38px;
                    border-radius: var(--radius-m);
                    cursor: pointer;
                    margin-bottom: 2px;
                    border: 1px solid transparent;
                    transition: background 0.12s, border-color 0.12s;
                }
                fb-todos-view .tv-item:hover { background: var(--hover); }
                fb-todos-view .tv-item[data-sortable-dragging] {
                    background: var(--panel);
                    border-color: var(--border);
                    box-shadow: 0 6px 18px rgba(0, 0, 0, 0.35);
                    z-index: 2;
                }
                fb-todos-view .tv-item.selected {
                    background: var(--accent-tint);
                    border-color: color-mix(in srgb, var(--accent) 28%, transparent);
                }

                /* Persistent checkbox on the left — primary interaction of a
                   todo, so it's always visible (not hover-gated like
                   secondary actions). Round, subtle until completed. */
                fb-todos-view .tv-check {
                    position: absolute;
                    left: 10px;
                    top: 10px;
                    width: 18px;
                    height: 18px;
                    border-radius: 50%;
                    border: 1.5px solid var(--text-muted);
                    background: transparent;
                    cursor: pointer;
                    display: inline-flex;
                    align-items: center;
                    justify-content: center;
                    color: transparent;
                    transition: background 120ms, border-color 120ms, color 120ms;
                    padding: 0;
                }
                fb-todos-view .tv-check:hover { border-color: var(--text); }
                fb-todos-view[readonly] #due-clear { display: none; }
                fb-todos-view .tv-check.checked {
                    background: var(--ok-fill);
                    border-color: var(--ok-fill);
                    color: var(--on-ok);
                }
                fb-todos-view .tv-check sac-icon { --icon-size: 12px; }
                /* A finger needs more than 18px: an invisible halo takes the
                   hit area to 44px without moving the layout. */
                @media (pointer: coarse) {
                    fb-todos-view .tv-check::after {
                        content: "";
                        position: absolute;
                        inset: -13px;
                    }
                }

                fb-todos-view .tv-item-title {
                    font-weight: 600;
                    font-size: 14px;
                    overflow: hidden;
                    text-overflow: ellipsis;
                    white-space: nowrap;
                    color: var(--text);
                    padding-right: 22px;
                }
                fb-todos-view .tv-item-meta {
                    display: flex;
                    gap: 6px;
                    align-items: center;
                    margin-top: 4px;
                    font-size: 12px;
                    color: var(--text-muted);
                }
                fb-todos-view .tv-item-due {
                    display: inline-flex;
                    gap: 4px;
                    align-items: center;
                    font-variant-numeric: tabular-nums;
                }
                fb-todos-view .tv-item-due sac-icon { --icon-size: 11px; }
                fb-todos-view .tv-item-due.overdue { color: var(--danger); }
                fb-todos-view .tv-item-due.soon    { color: var(--accent-warm); }

                /* Completed rows: dimmed + strikethrough title. Checkbox stays
                   vivid green so completion is still unmistakable. */
                fb-todos-view .tv-item.completed .tv-item-title {
                    opacity: 0.55;
                    text-decoration: line-through;
                }
                fb-todos-view .tv-item.completed .tv-item-meta { opacity: 0.55; }
                /* Ticked off while completed todos are hidden: it shows as
                   done for a moment, then fades out (_leave). */
                fb-todos-view .tv-item.leaving {
                    opacity: 0;
                    transition: opacity 0.3s ease;
                }

                /* Action row: delete only (the checkbox is always visible,
                   so it's not in this row). Quiet until hover/focus on a
                   desktop, always shown on touch (.reveal-on-hover). */
                fb-todos-view .tv-item-actions {
                    position: absolute;
                    top: 6px;
                    right: 6px;
                    display: flex;
                    gap: 1px;
                }
                fb-todos-view .tv-item-action {
                    --icon-btn-size: 22px;
                    --icon-btn-icon: 13px;
                    color: var(--text-muted);
                }

                fb-todos-view .tv-empty-list {
                    padding: 10px 12px;
                    color: var(--text-muted);
                    font-size: 13px;
                }

                /* --- EDITOR PANE ---------------------------------------------- */
                fb-todos-view .tv-editor-pane {
                    height: 100%;
                    display: flex;
                    flex-direction: column;
                    min-width: 0;
                }
                /* Collapsed (phone): the split's pane scrolls under its
                   sticky back bar, so the editor flows at natural height. */
                fb-todos-view sac-split[collapsed] .tv-editor-pane {
                    height: auto;
                    min-height: 100%;
                }
                fb-todos-view sac-split[collapsed] .tv-editor-body {
                    flex: 1 0 auto;
                    overflow: visible;
                }
                fb-todos-view .tv-editor-body {
                    flex: 1;
                    overflow: auto;
                    /* Like every editor pane: centred, at most 760px, 24px around. */
                    padding: 24px;
                    display: flex;
                    flex-direction: column;
                    align-items: center;
                }
                fb-todos-view .tv-editor-body > * { width: 100%; }
                fb-todos-view .tv-title-input {
                    width: 100%;
                    font-family: 'Outfit', sans-serif;
                    font-weight: 800;
                    font-size: 1.75rem;
                    letter-spacing: -0.02em;
                    background: none;
                    border: none;
                    color: var(--text);
                    outline: none;
                    margin-bottom: 18px;
                    padding: 0;
                }
                fb-todos-view .tv-title-input::placeholder {
                    color: var(--text-muted);
                    opacity: 0.4;
                }

                fb-todos-view .tv-field {
                    display: flex;
                    flex-direction: column;
                    gap: 6px;
                    margin-bottom: 16px;
                }
                fb-todos-view .tv-field label {
                    font-family: 'Outfit', sans-serif;
                    font-weight: 700;
                    font-size: 10px;
                    text-transform: uppercase;
                    letter-spacing: 0.1em;
                    color: var(--text-muted);
                }
                /* Field look from the kit's input rule (16px on touch). */
                fb-todos-view .tv-date-input {
                    outline: none;
                    max-width: 260px;
                }
                fb-todos-view .tv-desc-input {
                    display: block;
                    width: 100%;
                    min-height: 200px;
                    background: var(--field);
                    border: 1px solid var(--border);
                    border-radius: var(--radius-m);
                    color: var(--text);
                    font-family: inherit;
                    font-size: 14px;
                    line-height: 1.6;
                    outline: none;
                    resize: none;
                    padding: 12px 14px;
                    overflow: hidden;
                    transition: border-color 0.15s;
                }
                fb-todos-view .tv-desc-input:focus { border-color: var(--accent); }
                fb-todos-view .tv-desc-input::placeholder {
                    color: var(--text-muted);
                    opacity: 0.55;
                }

                fb-todos-view .tv-editor-footer {
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
                fb-todos-view .tv-editor-footer-spacer { flex: 1; }
                fb-todos-view textarea.tv-title-input {
                    display: block;
                    resize: none;
                    overflow: hidden;
                    line-height: 1.2;
                }
                fb-todos-view .fb-date-wrap { display: inline-flex; align-items: center; gap: 8px; }
                /* Mouse: nothing reserved — the title runs the full width; on
                   hover / focus the delete lays over the row's right end on a
                   fade of the row's own colour (as in the notes list). */
                @media (hover: hover) and (pointer: fine) {
                    fb-todos-view .tv-item-title { padding-right: 0; }
                    fb-todos-view .tv-item { --tv-row-bg: var(--panel); }
                    fb-todos-view .tv-item:hover { --tv-row-bg: var(--hover); }
                    fb-todos-view .tv-item.selected { --tv-row-bg: var(--accent-tint); }
                    fb-todos-view .tv-item-action { --icon-btn-size: 26px; --icon-btn-icon: 14px; }
                    fb-todos-view .tv-item-actions {
                        top: 5px;
                        right: 4px;
                        gap: 0;
                        padding: 2px 0 6px 24px;
                        border-radius: var(--radius-m);
                        background:
                            linear-gradient(to right, transparent, var(--tv-row-bg) 20px),
                            linear-gradient(to right, transparent, var(--panel) 20px);
                    }
                    fb-todos-view .tv-item:not(:hover):not(:focus-within) .tv-item-actions { display: none; }
                }
                /* Nothing open: the kit's .empty-state, centred in the pane. */
                fb-todos-view .tv-empty { flex: 1; justify-content: center; }
            </style>

            <sac-split class="tv-split" id="split" collapse show="start"
                       position="${this._splitPosition()}" min-start="260px" min-end="360px"
                       aria-label="${fb.t("fb.todos.resize", "Resize the todo list")}">
                <aside class="tv-list-pane" slot="start">
                    <div class="tv-search">
                        <sac-icon name="search"></sac-icon>
                        <input type="search" id="search-input" placeholder="${fb.t("fb.todos.search", "Search all todos")}"/>
                    </div>
                    <div class="tv-list-header">
                        <span class="tv-list-title" id="list-title">${fb.t("fb.todos.open", "Open Todos")}</span>
                        <button class="icon-btn" id="toggle-completed-btn" title="${fb.t("fb.todos.show-completed", "Show completed")}" aria-label="${fb.t("fb.todos.show-completed", "Show completed")}">
                            <sac-icon name="check"></sac-icon>
                        </button>
                        <button class="icon-btn" id="new-btn" title="${fb.t("fb.todos.new", "New todo")}" aria-label="${fb.t("fb.todos.new", "New todo")}">
                            <sac-icon name="plus"></sac-icon>
                        </button>
                    </div>
                    <div class="tv-items fb-scroll-fade" id="todo-list"></div>
                </aside>

                <main class="tv-editor-pane" slot="end">
                    <div class="tv-editor-body">
                        <div class="empty-state tv-empty" id="editor-empty">
                            <sac-icon name="check"></sac-icon>
                            <h3>${fb.t("fb.todos.none-open", "No todo open")}</h3>
                            <p>${fb.t("fb.todos.none-open-hint", "Pick one from the list, or add a new one.")}</p>
                            <button type="button" class="btn primary" id="empty-new-btn"><sac-icon name="plus"></sac-icon> ${fb.t("fb.todos.new", "New todo")}</button>
                        </div>
                        <div id="editor" hidden>
                            <textarea id="title" class="tv-title-input" rows="1" placeholder="${fb.t("fb.todos.title-placeholder", "What needs doing?")}"></textarea>
                            <div class="tv-field">
                                <label for="due-at">${fb.t("fb.todos.due", "Due")}</label>
                                <input id="due-at" class="tv-date-input"/>
                                <button class="icon-btn tv-date-clear-btn" id="due-clear" title="${fb.t("fb.todos.clear-due", "Clear due date")}" aria-label="${fb.t("fb.todos.clear-due", "Clear due date")}" hidden><sac-icon name="close"></sac-icon></button>
                            </div>
                            <div class="tv-field">
                                <label for="description">${fb.t("fb.todos.notes", "Notes")}</label>
                                <textarea id="description" class="tv-desc-input" placeholder="${fb.t("fb.todos.notes-placeholder", "Anything else worth remembering?")}"></textarea>
                            </div>
                        </div>
                    </div>
                    <footer class="tv-editor-footer" id="editor-footer" hidden>
                        <span class="tv-editor-footer-meta">
                            ${fb.t("fb.todos.updated", "Updated")} <span id="timestamp"></span>
                        </span>
                        <sac-chip id="completed-pill" label="${fb.t("fb.todos.completed", "Completed")}" color="green" hidden></sac-chip>
                        <div class="tv-editor-footer-spacer"></div>
                    </footer>
                </main>
            </sac-split>
        `;
        this.attachHandlers();
    }

    /** The divider position the user left last time (per browser), else the
     *  default. A convenience only — storage may be unavailable. */
    _splitPosition() {
        try { return localStorage.getItem("fb.todos.split") || "28%"; }
        catch { return "28%"; }
    }

    attachHandlers() {
        const split = this.querySelector("#split");
        split.addEventListener("sac:resize", (e) => {
            try { localStorage.setItem("fb.todos.split", e.detail.position); } catch { /* ignore */ }
        });
        // Collapsed (phone): the back bar returns to the list — save on the
        // way out.
        split.addEventListener("sac:split-back", () => this.flushSave());

        this.querySelector("#new-btn").addEventListener("click", () => this.createTodo());
        this.querySelector("#empty-new-btn").addEventListener("click", () => this.createTodo());
        this.querySelector("#toggle-completed-btn").addEventListener("click", () => {
            this.hideCompleted = !this.hideCompleted;
            this.querySelector("#toggle-completed-btn").classList.toggle("active", !this.hideCompleted);
            this.querySelector("#list-title").textContent = this.hideCompleted
                ? fb.t("fb.todos.open", "Open Todos")
                : fb.t("fb.todos.all", "All Todos");
            this.renderList();
        });
        this.querySelector("#search-input").addEventListener("input", (e) => {
            this.searchQuery = e.target.value.trim().toLowerCase();
            this.renderList();
        });

        const titleEl = this.querySelector("#title");
        titleEl.addEventListener("blur",  () => this.flushSave());
        titleEl.addEventListener("input", () => { this.autosizeTitle(); this.scheduleAutoSave(); });
        // A title is one line: Enter leaves it (no newline).
        titleEl.addEventListener("keydown", (e) => { if (e.key === "Enter") { e.preventDefault(); titleEl.blur(); } });

        const descEl = this.querySelector("#description");
        descEl.addEventListener("blur",  () => this.flushSave());
        descEl.addEventListener("input", () => {
            this.autosizeDesc();
            this.scheduleAutoSave();
        });

        const dueEl = fb.format.attachInput(this.querySelector("#due-at"), { time: true });
        // Clear sits in the field row, after the time.
        dueEl.parentElement.append(this.querySelector("#due-clear"));
        dueEl.addEventListener("change", () => this.saveSelected());
        this.querySelector("#due-clear").addEventListener("click", () => {
            fb.format.writeInput(dueEl, null);
            this.querySelector("#due-clear").hidden = true;
            this.saveSelected();
        });
    }

    scheduleAutoSave() {
        clearTimeout(this._saveDebounce);
        this._saveDebounce = setTimeout(() => this.saveSelected(), 700);
    }

    async flushSave() {
        clearTimeout(this._saveDebounce);
        this._saveDebounce = null;
        await this.saveSelected();
    }

    autosizeTitle() {
        const el = this.querySelector("#title");
        if (!el) return;
        el.style.height = "auto";
        el.style.height = el.scrollHeight + "px";
    }

    autosizeDesc() {
        const el = this.querySelector("#description");
        if (!el) return;
        el.style.height = "auto";
        el.style.height = el.scrollHeight + "px";
    }

    updateToolbar(todo) {
        const completed = !!todo.completedAt;
        if (this.writable === false) { fb.toolbar.clear(); return; }
        fb.toolbar.set([
            {
                icon:    "check",
                title:   completed ? fb.t("fb.todos.mark-undone", "Mark as not done") : fb.t("fb.todos.mark-done", "Mark as done"),
                active:  completed,
                onClick: () => this.toggleCompleted()
            },
            {
                icon:    "trash",
                title:   fb.t("fb.todos.delete", "Delete todo"),
                onClick: () => this.deleteSelected()
            }
        ]);
    }

    static _byOrder(a, b) {
        const aDone = !!a.completedAt;
        const bDone = !!b.completedAt;
        if (aDone !== bDone) return aDone ? 1 : -1;
        return (a.position ?? Infinity) - (b.position ?? Infinity)
            || new Date(a.createdAt || 0) - new Date(b.createdAt || 0)
            || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0);   // ULIDs sort by creation
    }

    // A drag moved the open row at index `from` to `to` (indices among the
    // rendered open rows). Give it a position between its new neighbours —
    // one row written, the others keep theirs.
    async _moveTodo(from, to) {
        const ids = [...this.querySelectorAll("#todo-list .tv-item:not(.completed)")].map(el => el.dataset.id);
        // The DOM already shows the new order; `ids` is it.
        const byId = (id) => this.todos.find(t => t.id === id);
        const todo = byId(ids[to]);
        if (!todo) return;
        const prev = byId(ids[to - 1]);
        const next = byId(ids[to + 1]);
        const before = todo.position;
        todo.position = prev && next ? (prev.position + next.position) / 2
            : prev ? prev.position + 1
            : next ? next.position - 1
            : todo.position;
        this.renderList();
        try {
            await this.api.update(todo.id, todo);
        } catch (err) {
            console.error("[fb-todos-view] reorder failed:", err);
            todo.position = before;
            this.renderList();
            window.sac?.toast?.(fb.t("fb.todos.order-failed", "Couldn't save the new order."), { kind: "error" });
        }
    }

    renderList() {
        const filtered = this.todos.filter(t => {
            if (this.hideCompleted && t.completedAt && !this._leaving?.has(t.id)) return false;
            if (this.searchQuery) {
                const haystack = ((t.title || "") + " " + (t.description || "")).toLowerCase();
                if (!haystack.includes(this.searchQuery)) return false;
            }
            return true;
        });

        // Sort: open before done, then the user's own order (position —
        // drag to change it; a new todo is appended). Ticking one off and
        // back never moves it. Due dates show on the row but don't reorder.
        // A todo just ticked off keeps its place while it shows as done.
        const leaving = this._leaving;
        filtered.sort(leaving?.size
            ? (a, b) => FbTodosView._byOrder(
                leaving.has(a.id) ? { ...a, completedAt: null } : a,
                leaving.has(b.id) ? { ...b, completedAt: null } : b)
            : FbTodosView._byOrder);

        const list = this.querySelector("#todo-list");
        if (filtered.length === 0) {
            list.innerHTML = `<div class="tv-empty-list">${fb.t("fb.todos.empty-list", "No todos.")}</div>`;
            return;
        }

        list.innerHTML = filtered.map(t => {
            const isSelected = t.id === this.selectedId;
            const isDone     = !!t.completedAt;
            const dueInfo    = this.describeDue(t.dueAt, isDone);
            const rowClasses = [
                "tv-item",
                "reveal-on-hover",
                isSelected ? "selected"  : "",
                isDone     ? "completed" : "",
            ].filter(Boolean).join(" ");
            const checkSvg = isDone
                ? `<sac-icon name="check"></sac-icon>`
                : "";
            return `
                <div class="${rowClasses}" data-id="${t.id}" tabindex="0">
                    <button class="tv-check ${isDone ? "checked" : ""}" data-action="check" ${this.writable === false ? "disabled" : ""}
                            title="${isDone ? fb.t("fb.todos.mark-undone", "Mark as not done") : fb.t("fb.todos.mark-done", "Mark as done")}"
                            aria-label="${isDone ? fb.t("fb.todos.mark-undone", "Mark as not done") : fb.t("fb.todos.mark-done", "Mark as done")}">${checkSvg}</button>
                    <div class="tv-item-title">${escapeHtml(t.title || fb.t("fb.todos.untitled", "Untitled"))}</div>
                    ${dueInfo.text ? `
                        <div class="tv-item-meta">
                            <span class="tv-item-due ${dueInfo.urgency}">
                                <sac-icon name="clock"></sac-icon>${dueInfo.text}
                            </span>
                        </div>
                    ` : ""}
                    <div class="tv-item-actions" ${this.writable === false ? "hidden" : ""}>
                        <button class="icon-btn hover-reveal danger tv-item-action delete" data-action="delete" title="${fb.t("fb.common.delete", "Delete")}" aria-label="${fb.t("fb.common.delete", "Delete")}"><sac-icon name="trash"></sac-icon></button>
                    </div>
                </div>
            `;
        }).join("");

        list.querySelectorAll(".tv-item").forEach(el => {
            el.addEventListener("click", (e) => {
                if (e.target.closest(".tv-item-action")) return;
                if (e.target.closest(".tv-check"))       return;
                this.select(el.dataset.id);
            });
            el.addEventListener("keydown", (e) => {
                if (e.target !== el || (e.key !== "Enter" && e.key !== " ")) return;
                e.preventDefault();
                this.select(el.dataset.id);
            });
            el.querySelectorAll(".tv-check").forEach(btn => {
                btn.addEventListener("click", (e) => {
                    e.stopPropagation();
                    this.toggleCompletedById(el.dataset.id);
                });
            });
            el.querySelectorAll(".tv-item-action").forEach(btn => {
                btn.addEventListener("click", (e) => {
                    e.stopPropagation();
                    if (btn.dataset.action === "delete") this.deleteById(el.dataset.id);
                });
            });
        });
    }

    describeDue(iso, isDone) {
        if (!iso) return { text: "", urgency: "" };
        const due  = new Date(iso);
        const now  = new Date();
        const diff = due - now;
        const msPerDay = 86400000;

        const sameDay = due.toDateString() === now.toDateString();
        const tomorrow = new Date(now); tomorrow.setDate(now.getDate() + 1);
        const isTomorrow = due.toDateString() === tomorrow.toDateString();

        let text;
        if (isDone && diff < 0 && !sameDay) {
            const sameYear = due.getFullYear() === now.getFullYear();
            text = sameYear ? fb.format.dayMonth(due) : fb.format.date(due);
        } else if (sameDay) {
            text = fb.t("fb.todos.today-at", "Today · {time}", { time: fb.format.time(due) });
        } else if (isTomorrow) {
            text = fb.t("fb.common.tomorrow", "Tomorrow");
        } else if (diff < 0) {
            const days = Math.ceil(-diff / msPerDay);
            text = days === 1
                ? fb.t("fb.todos.overdue-1", "1 day overdue")
                : fb.t("fb.todos.overdue", "{n} days overdue", { n: days });
        } else if (diff < 7 * msPerDay) {
            text = fb.format.weekday(due);
        } else {
            const sameYear = due.getFullYear() === now.getFullYear();
            text = sameYear
                ? fb.format.dayMonth(due)
                : fb.format.date(due);
        }

        let urgency = "";
        if (!isDone) {
            if (diff < 0) urgency = "overdue";
            else if (diff < msPerDay) urgency = "soon";
        }
        return { text, urgency };
    }

    async select(id) {
        // Collapsed (phone), opening a row brings the editor forward — also
        // when it is the row already open behind the list.
        this.querySelector("#split").show = "end";
        if (this.selectedId === id) return;
        await this.flushSave();
        this.selectedId = id;
        const todo = this.todos.find(t => t.id === id);
        if (!todo) return;
        this.querySelector("#editor-empty").hidden  = true;
        this.querySelector("#editor").hidden        = false;
        this.querySelector("#editor-footer").hidden = false;
        this.querySelector("#title").value       = todo.title       || "";
        requestAnimationFrame(() => this.autosizeTitle());
        this.querySelector("#description").value = todo.description || "";
        fb.format.writeInput(this.querySelector("#due-at"), todo.dueAt ? new Date(todo.dueAt) : null);
        this.querySelector("#due-clear").hidden  = !todo.dueAt;
        this.querySelector("#timestamp").textContent = this.formatFullTimestamp(todo.updatedAt);
        this.querySelector("#completed-pill").hidden = !todo.completedAt;
        this.updateToolbar(todo);
        requestAnimationFrame(() => this.autosizeDesc());
        this.renderList();
    }

    clearSelection() {
        this.selectedId = null;
        // Collapsed, an editor with nothing in it is a dead end.
        this.querySelector("#split").show = "start";
        this.querySelector("#editor-empty").hidden  = false;
        this.querySelector("#editor").hidden        = true;
        this.querySelector("#editor-footer").hidden = true;
        this.querySelector("#timestamp").textContent = "";
        fb.toolbar.clear();
    }

    async saveSelected() {
        if (!this.selectedId || this.writable === false) return;
        clearTimeout(this._saveDebounce);
        this._saveDebounce = null;
        const todo = this.todos.find(t => t.id === this.selectedId);
        if (!todo) return;
        const newTitle = this.querySelector("#title").value;
        const newDesc  = this.querySelector("#description").value;
        const due      = fb.format.readInput(this.querySelector("#due-at"));
        const newDue   = due ? due.toISOString() : null;

        if (newTitle === todo.title
            && newDesc === (todo.description || "")
            && newDue === (todo.dueAt || null)) return;

        const before = { title: todo.title, description: todo.description, dueAt: todo.dueAt };
        todo.title       = newTitle;
        todo.description = newDesc;
        todo.dueAt       = newDue;

        try {
            await this.api.update(todo.id, todo);
            todo.updatedAt = new Date().toISOString();
            this.querySelector("#timestamp").textContent = this.formatFullTimestamp(todo.updatedAt);
            this.querySelector("#due-clear").hidden = !todo.dueAt;
            this._updateRowInPlace(todo);
        } catch (err) {
            console.error("[fb-todos-view] update failed:", err);
            // Back to what the server has, so the next change saves it again.
            Object.assign(todo, before);
            window.sac?.toast?.(fb.errors.text(err, fb.t("fb.todos.save-failed", "Couldn't save the todo.")), { kind: "error" });
        }
    }

    /** Update one row in place — avoids wiping the list during autosave. */
    _updateRowInPlace(todo) {
        const row = this.querySelector(`.tv-item[data-id="${todo.id}"]`);
        if (!row) return;
        const titleEl = row.querySelector(".tv-item-title");
        if (titleEl) titleEl.textContent = todo.title || fb.t("fb.todos.untitled", "Untitled");
        // Due date row: rebuild the meta section if presence changed, else
        // just update the text + urgency class.
        const dueInfo = this.describeDue(todo.dueAt, !!todo.completedAt);
        let meta = row.querySelector(".tv-item-meta");
        if (dueInfo.text) {
            if (!meta) {
                // Rebuild the row so the meta slot is re-inserted in order.
                this.renderList();
                return;
            }
            const dueEl = meta.querySelector(".tv-item-due");
            dueEl.className = `tv-item-due ${dueInfo.urgency}`;
            dueEl.innerHTML = `<sac-icon name="clock"></sac-icon>${dueInfo.text}`;
        } else if (meta) {
            meta.remove();
        }
    }

    /** Toolbar-triggered delegates. */
    toggleCompleted() { if (this.selectedId) this.toggleCompletedById(this.selectedId); }
    deleteSelected()  { if (this.selectedId) this.deleteById(this.selectedId); }

    async toggleCompletedById(id) {
        if (id === this.selectedId) await this.flushSave();
        const todo = this.todos.find(t => t.id === id);
        if (!todo) return;
        const was = todo.completedAt;
        todo.completedAt = was ? null : new Date().toISOString();
        // Unticked again while it was still on its way out: it stays.
        if (!todo.completedAt) this._stopLeaving(id);
        // Ticked while completed todos are hidden: show it struck through
        // first, then let it go (_leave).
        if (todo.completedAt && this.hideCompleted) this._leave(id);
        try {
            await this.api.update(todo.id, todo);
            if (id === this.selectedId) {
                this.querySelector("#completed-pill").hidden = !todo.completedAt;
                this.updateToolbar(todo);
                if (todo.completedAt && this.hideCompleted && !this._leaving?.has(id)) {
                    this.clearSelection();
                }
            }
            this.renderList();
        } catch (err) {
            console.error("[fb-todos-view] toggle complete failed:", err);
            todo.completedAt = was;
            this._stopLeaving(id);
            this.renderList();
        }
    }

    /** A todo ticked off while completed ones are hidden stays in its row,
     *  struck through, for a moment — then fades out and leaves the list. */
    _leave(id) {
        this._leaving ??= new Map();
        clearTimeout(this._leaving.get(id));
        this._leaving.set(id, setTimeout(() => {
            this.querySelector(`#todo-list .tv-item[data-id="${id}"]`)?.classList.add("leaving");
            this._leaving.set(id, setTimeout(() => {
                this._leaving.delete(id);
                const todo = this.todos.find(t => t.id === id);
                if (id === this.selectedId && todo?.completedAt && this.hideCompleted) this.clearSelection();
                this.renderList();
            }, 300));
        }, 1200));
    }

    _stopLeaving(id) {
        if (!this._leaving?.has(id)) return;
        clearTimeout(this._leaving.get(id));
        this._leaving.delete(id);
    }

    async deleteById(id) {
        const todo = this.todos.find(t => t.id === id);
        if (!todo) return;

        const buttons = [
            { action: "cancel", label: fb.t("fb.common.cancel", "Cancel"), kind: "default" },
            { action: "delete", label: fb.t("fb.common.delete", "Delete"), kind: "destructive", armAfterMs: 2000 },
        ];
        const result = await sac.dialog.confirm({
            title:   fb.t("fb.todos.delete-title", "Delete this todo?"),
            message: fb.t("fb.todos.delete-msg", "This todo moves to the trash — you can restore it there."),
            buttons,
        });
        if (result !== "delete") return;

        if (id === this.selectedId) {
            clearTimeout(this._saveDebounce);
            this._saveDebounce = null;
        }
        try {
            await this.api.delete(id);
            this.todos = this.todos.filter(t => t.id !== id);
            if (id === this.selectedId) this.clearSelection();
            this.renderList();
        } catch (err) {
            console.error("[fb-todos-view] delete failed:", err);
            window.sac?.toast?.(fb.errors.text(err, fb.t("fb.todos.delete-failed", "Couldn't delete the todo.")), { kind: "error" });
        }
    }

    async createTodo() {
        try {
            const created = await this.api.create({ title: "" });
            this.todos.push(created);
            await this.select(created.id);
            this.querySelector("#title").focus();
        } catch (err) {
            console.error("[fb-todos-view] create failed:", err);
            window.sac?.toast?.(fb.errors.text(err, fb.t("fb.todos.create-failed", "Couldn't create the todo.")), { kind: "error" });
        }
    }

    formatFullTimestamp(iso) {
        return iso ? fb.format.dateTime(iso) : "";
    }
}

function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, c => ({"&":"&amp;","<":"&lt;",">":"&gt;","\"":"&quot;","'":"&#39;"}[c]));
}

customElements.define("fb-todos-view", FbTodosView);
sac.router.register("#/todos", "fb-todos-view", { label: "Todos", icon: "check", palette: false });
