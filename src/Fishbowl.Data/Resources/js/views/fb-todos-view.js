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
        this.render();
        // Drag an open todo to reorder (the kit's sortable: mouse drags after
        // a 4px move, touch after a long-press, Escape cancels). Done todos
        // stay where they are, below the open ones.
        this._sortable = sac.sortable(this.querySelector("#todo-list"), {
            items: ".tv-item:not(.completed)",
            onReorder: (from, to) => this._moveTodo(from, to),
        });
        await this.loadTodos();
        // "New todo" from the Ctrl-K palette (fb.desktop.go).
        this._onIntent = () => { if (fb.desktop?.takeIntent("todos")?.action === "create") this.createTodo(); };
        window.addEventListener("fb:intent", this._onIntent);
        this._onIntent();
    }

    disconnectedCallback() {
        if (this._onIntent) window.removeEventListener("fb:intent", this._onIntent);
        this._sortable?.destroy();
        this.flushSave();
        if (window.fb?.toolbar) fb.toolbar.clear();
    }

    async loadTodos() {
        try {
            this.todos = await fb.api.todos.list();
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
                /* --- LIST PANE ------------------------------------------------ */
                /* The kit's list-detail recipe, as in the notes list: flat
                   rows edge to edge, a divider under each, --hover on hover,
                   --accent-tint when open; search, header and row content
                   share one 1rem gutter. */
                fb-todos-view .tv-list-pane {
                    min-height: 100%;
                    background: var(--panel);
                    display: flex;
                    flex-direction: column;
                }
                fb-todos-view .tv-search {
                    position: relative;
                    padding: 12px 1rem 0;
                }
                fb-todos-view .tv-search sac-icon {
                    position: absolute;
                    left: calc(1rem + 10px);
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
                   from the kit's global input rule; only the icon inset is ours. */
                fb-todos-view .tv-search input { padding-left: 32px; }
                fb-todos-view .tv-list-header {
                    display: flex;
                    align-items: center;
                    gap: 4px;
                    padding: 12px 1rem 8px;
                    border-bottom: 1px solid var(--border);
                }
                fb-todos-view .tv-list-header .sac-caption { flex: 1; min-width: 0; }

                fb-todos-view .tv-items {
                    flex: 1;
                    overflow-y: auto;
                    padding-bottom: env(safe-area-inset-bottom, 0px);
                }
                fb-todos-view .tv-item {
                    display: flex;
                    align-items: flex-start;
                    gap: 0.75rem;
                    min-height: 56px;
                    padding: 0.6rem 1rem;
                    border-bottom: 1px solid var(--border);
                    background: var(--panel);
                    cursor: pointer;
                }
                fb-todos-view .tv-item:hover { background: var(--hover); }
                fb-todos-view .tv-item.selected { background: var(--accent-tint); }
                fb-todos-view .tv-item:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; }
                /* The row being dragged lifts with the kit's shadow token. */
                fb-todos-view .tv-item[data-sortable-dragging] {
                    box-shadow: var(--shadow-1);
                    z-index: 2;
                }
                /* The done-check is the kit's checkbox (accent-color, 20px
                   with a 44px row on touch) — the todo's primary action, so
                   never hover-gated. */
                fb-todos-view .tv-check { flex: none; margin: 3px 0 0; }
                fb-todos-view .tv-item-main {
                    flex: 1;
                    min-width: 0;
                    display: flex;
                    flex-direction: column;
                    gap: 2px;
                }
                fb-todos-view .tv-item-title {
                    font-weight: 600;
                    overflow: hidden;
                    text-overflow: ellipsis;
                    white-space: nowrap;
                }
                fb-todos-view .tv-item-meta {
                    display: flex;
                    gap: 6px;
                    align-items: center;
                    font-size: 0.85rem;
                    color: var(--text-muted);
                }
                fb-todos-view .tv-item-due {
                    display: inline-flex;
                    gap: 4px;
                    align-items: center;
                    font-variant-numeric: tabular-nums;
                }
                fb-todos-view .tv-item-due sac-icon { --icon-size: 12px; }
                fb-todos-view .tv-item-due.overdue { color: var(--danger-text); }
                fb-todos-view .tv-item-due.soon    { color: var(--accent-warm-text); }

                /* Completed rows: dimmed + strikethrough title. */
                fb-todos-view .tv-item.completed .tv-item-title {
                    opacity: 0.55;
                    text-decoration: line-through;
                }
                fb-todos-view .tv-item.completed .tv-item-meta { opacity: 0.55; }

                /* Delete: the kit's .icon-btn, revealed on hover
                   (.reveal-on-hover; always shown on touch). */
                fb-todos-view .tv-item-actions {
                    display: flex;
                    flex: none;
                    gap: 2px;
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
                    /* clamp(), not a breakpoint: roomy on a monitor, tight
                       on a phone. */
                    padding: clamp(1.25rem, 4vw, 36px) clamp(1rem, 5vw, 56px);
                    display: flex;
                    flex-direction: column;
                }
                fb-todos-view .tv-editor-body > .empty-state { margin: auto; }
                /* The title is a heading that happens to be editable, as in
                   sacrvm-notes' .ed-title. */
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
                fb-todos-view .tv-title-input::placeholder { color: var(--text-dim); }

                fb-todos-view .tv-field {
                    display: flex;
                    flex-direction: column;
                    gap: 6px;
                    margin-bottom: 16px;
                }
                fb-todos-view .tv-date-input { max-width: 260px; }
                fb-todos-view .tv-field .toolbar { align-self: flex-start; }
                /* The kit's textarea; only its height is ours (it grows with
                   the text, the body scrolls). */
                fb-todos-view .tv-desc-input {
                    display: block;
                    width: 100%;
                    min-height: 200px;
                    resize: none;
                    overflow: hidden;
                }

                /* One quiet meta line, like sacrvm-notes' .ed-meta. */
                fb-todos-view .tv-meta {
                    display: flex;
                    align-items: center;
                    gap: 0.75rem;
                    min-height: 32px;
                    padding: 8px clamp(1rem, 4vw, 20px);
                    padding-bottom: calc(8px + env(safe-area-inset-bottom, 0px));
                    font-size: 0.72rem;
                    color: var(--text-dim);
                    flex-shrink: 0;
                }
                /* Our display rules beat the UA's [hidden]; say it per class,
                   as the kit does (.btn[hidden]). */
                fb-todos-view .tv-meta[hidden],
                fb-todos-view .toolbar[hidden],
                fb-todos-view .empty-state[hidden] { display: none; }
            </style>

            <sac-split class="tv-split" id="split" collapse show="start"
                       position="${this._splitPosition()}" min-start="260px" min-end="360px"
                       aria-label="Resize the todo list">
                <aside class="tv-list-pane" slot="start">
                    <div class="tv-search">
                        <sac-icon name="search"></sac-icon>
                        <input type="search" id="search-input" placeholder="Search all todos"/>
                    </div>
                    <div class="tv-list-header">
                        <span class="sac-caption" id="list-title">Open Todos</span>
                        <button class="icon-btn tool" id="toggle-completed-btn" title="Show completed" aria-label="Show completed" aria-pressed="false">
                            <sac-icon name="check"></sac-icon>
                        </button>
                        <button class="icon-btn tool" id="new-btn" title="New todo" aria-label="New todo">
                            <sac-icon name="plus"></sac-icon>
                        </button>
                    </div>
                    <div class="tv-items" id="todo-list"></div>
                </aside>

                <main class="tv-editor-pane" slot="end">
                    <div class="tv-editor-body">
                        <div class="empty-state" id="editor-empty">
                            <sac-icon name="check"></sac-icon>
                            <h3>No todo open</h3>
                            <p>Pick one from the list, or add a new one.</p>
                            <button type="button" class="btn primary" id="empty-new-btn">
                                <sac-icon name="plus"></sac-icon>New todo
                            </button>
                        </div>
                        <div id="editor" hidden>
                            <input id="title" class="tv-title-input" placeholder="What needs doing?"/>
                            <div class="tv-field">
                                <label for="due-at">Due</label>
                                <input id="due-at" class="tv-date-input"/>
                                <span class="toolbar" id="due-clear-bar" hidden>
                                    <button type="button" class="btn" id="due-clear">Clear due date</button>
                                </span>
                            </div>
                            <div class="tv-field">
                                <label for="description">Notes</label>
                                <textarea id="description" class="tv-desc-input" placeholder="Anything else worth remembering?"></textarea>
                            </div>
                        </div>
                    </div>
                    <footer class="tv-meta" id="editor-footer" hidden>
                        <span>Updated <span id="timestamp"></span></span>
                        <span id="completed-pill" hidden>Completed</span>
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
            this.querySelector("#toggle-completed-btn").setAttribute("aria-pressed", String(!this.hideCompleted));
            this.querySelector("#list-title").textContent = this.hideCompleted ? "Open Todos" : "All Todos";
            this.renderList();
        });
        this.querySelector("#search-input").addEventListener("input", (e) => {
            this.searchQuery = e.target.value.trim().toLowerCase();
            this.renderList();
        });

        const titleEl = this.querySelector("#title");
        titleEl.addEventListener("blur",  () => this.flushSave());
        titleEl.addEventListener("input", () => this.scheduleAutoSave());

        const descEl = this.querySelector("#description");
        descEl.addEventListener("blur",  () => this.flushSave());
        descEl.addEventListener("input", () => {
            this.autosizeDesc();
            this.scheduleAutoSave();
        });

        const dueEl = fb.format.attachInput(this.querySelector("#due-at"), { time: true });
        dueEl.addEventListener("change", () => this.saveSelected());
        this.querySelector("#due-clear").addEventListener("click", () => {
            fb.format.writeInput(dueEl, null);
            this.querySelector("#due-clear-bar").hidden = true;
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

    autosizeDesc() {
        const el = this.querySelector("#description");
        if (!el) return;
        el.style.height = "auto";
        el.style.height = el.scrollHeight + "px";
    }

    updateToolbar(todo) {
        const completed = !!todo.completedAt;
        fb.toolbar.set([
            {
                icon:    "check",
                title:   completed ? "Mark as not done" : "Mark as done",
                active:  completed,
                onClick: () => this.toggleCompleted()
            },
            {
                icon:    "trash",
                title:   "Delete todo",
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
            await fb.api.todos.update(todo.id, todo);
        } catch (err) {
            console.error("[fb-todos-view] reorder failed:", err);
            todo.position = before;
            this.renderList();
            window.sac?.toast?.("Couldn't save the new order.", { kind: "error" });
        }
    }

    renderList() {
        const filtered = this.todos.filter(t => {
            if (this.hideCompleted && t.completedAt) return false;
            if (this.searchQuery) {
                const haystack = ((t.title || "") + " " + (t.description || "")).toLowerCase();
                if (!haystack.includes(this.searchQuery)) return false;
            }
            return true;
        });

        // Sort: open before done, then the user's own order (position —
        // drag to change it; a new todo is appended). Ticking one off and
        // back never moves it. Due dates show on the row but don't reorder.
        filtered.sort(FbTodosView._byOrder);

        const list = this.querySelector("#todo-list");
        if (filtered.length === 0) {
            const filtering = !!this.querySelector("#search-input")?.value.trim();
            list.innerHTML = filtering
                ? `<div class="empty-state"><sac-icon name="search"></sac-icon><h3>No matches</h3><p>No todo fits this search.</p></div>`
                : `<div class="empty-state"><sac-icon name="check"></sac-icon><h3>Nothing to do</h3><p>Add a todo — it stays here until it's done.</p>
                       <button type="button" class="btn primary" data-new><sac-icon name="plus"></sac-icon>New todo</button></div>`;
            list.querySelector("[data-new]")?.addEventListener("click", () => this.createTodo());
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
            const checkTitle = isDone ? "Mark as not done" : "Mark as done";
            return `
                <div class="${rowClasses}" data-id="${t.id}" tabindex="0">
                    <input type="checkbox" class="tv-check" data-action="check" ${isDone ? "checked" : ""}
                           title="${checkTitle}" aria-label="${checkTitle}">
                    <div class="tv-item-main">
                        <div class="tv-item-title">${escapeHtml(t.title || "Untitled")}</div>
                        ${dueInfo.text ? `
                            <div class="tv-item-meta">
                                <span class="tv-item-due ${dueInfo.urgency}">
                                    <sac-icon name="clock"></sac-icon>${dueInfo.text}
                                </span>
                            </div>
                        ` : ""}
                    </div>
                    <div class="tv-item-actions">
                        <button class="icon-btn hover-reveal danger tv-item-action delete" data-action="delete" title="Delete" aria-label="Delete"><sac-icon name="trash"></sac-icon></button>
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
        if (sameDay) {
            text = "Today · " + fb.format.time(due);
        } else if (isTomorrow) {
            text = "Tomorrow";
        } else if (diff < 0) {
            const days = Math.ceil(-diff / msPerDay);
            text = `${days} day${days === 1 ? "" : "s"} overdue`;
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
        this.querySelector("#description").value = todo.description || "";
        fb.format.writeInput(this.querySelector("#due-at"), todo.dueAt ? new Date(todo.dueAt) : null);
        this.querySelector("#due-clear-bar").hidden  = !todo.dueAt;
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
        if (!this.selectedId) return;
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

        todo.title       = newTitle;
        todo.description = newDesc;
        todo.dueAt       = newDue;

        try {
            await fb.api.todos.update(todo.id, todo);
            todo.updatedAt = new Date().toISOString();
            this.querySelector("#timestamp").textContent = this.formatFullTimestamp(todo.updatedAt);
            this.querySelector("#due-clear-bar").hidden = !todo.dueAt;
            this._updateRowInPlace(todo);
        } catch (err) {
            console.error("[fb-todos-view] update failed:", err);
        }
    }

    /** Update one row in place — avoids wiping the list during autosave. */
    _updateRowInPlace(todo) {
        const row = this.querySelector(`.tv-item[data-id="${todo.id}"]`);
        if (!row) return;
        const titleEl = row.querySelector(".tv-item-title");
        if (titleEl) titleEl.textContent = todo.title || "Untitled";
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
        try {
            await fb.api.todos.update(todo.id, todo);
            if (id === this.selectedId) {
                this.querySelector("#completed-pill").hidden = !todo.completedAt;
                this.updateToolbar(todo);
                if (todo.completedAt && this.hideCompleted) {
                    this.clearSelection();
                }
            }
            this.renderList();
        } catch (err) {
            console.error("[fb-todos-view] toggle complete failed:", err);
            todo.completedAt = was;
        }
    }

    async deleteById(id) {
        const todo = this.todos.find(t => t.id === id);
        if (!todo) return;

        const buttons = [
            { action: "cancel", label: "Cancel", kind: "default" },
            { action: "delete", label: "Delete", kind: "destructive", armAfterMs: 2000 },
        ];
        const result = await sac.dialog.confirm({
            title:   "Delete this todo?",
            message: "This todo will be permanently deleted.",
            buttons,
        });
        if (result !== "delete") return;

        if (id === this.selectedId) {
            clearTimeout(this._saveDebounce);
            this._saveDebounce = null;
        }
        try {
            await fb.api.todos.delete(id);
            this.todos = this.todos.filter(t => t.id !== id);
            if (id === this.selectedId) this.clearSelection();
            this.renderList();
        } catch (err) {
            console.error("[fb-todos-view] delete failed:", err);
        }
    }

    async createTodo() {
        try {
            const created = await fb.api.todos.create({ title: "" });
            this.todos.push(created);
            await this.select(created.id);
            this.querySelector("#title").focus();
        } catch (err) {
            console.error("[fb-todos-view] create failed:", err);
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
