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
        await this.loadTodos();
    }

    disconnectedCallback() {
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
                fb-todos-view [hidden] { display: none !important; }

                /* --- LIST PANE ------------------------------------------------ */
                fb-todos-view .tv-list-pane {
                    min-height: 100%;
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

                fb-todos-view .tv-list-header {
                    display: flex;
                    align-items: center;
                    padding: 12px 16px 6px;
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
                    overflow-y: auto;
                    padding: 2px 8px 12px;
                }

                fb-todos-view .tv-item {
                    position: relative;
                    padding: 10px 12px 10px 38px;
                    border-radius: 10px;
                    cursor: pointer;
                    margin-bottom: 2px;
                    border: 1px solid transparent;
                    transition: background 0.12s, border-color 0.12s;
                }
                fb-todos-view .tv-item:hover { background: var(--hover); }
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
                    padding: 40px 16px;
                    text-align: center;
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
                    /* clamp(), not a breakpoint: roomy on a monitor, tight
                       on a phone. */
                    padding: clamp(1.25rem, 4vw, 36px) clamp(1rem, 5vw, 56px);
                    display: flex;
                    flex-direction: column;
                }
                fb-todos-view .tv-empty-state {
                    flex: 1;
                    display: flex;
                    flex-direction: column;
                    align-items: center;
                    justify-content: center;
                    color: var(--text-muted);
                    gap: 12px;
                }
                fb-todos-view .tv-empty-state sac-icon {
                    --icon-size: 72px;
                    opacity: 0.2;
                }
                fb-todos-view .tv-empty-state p {
                    margin: 0;
                    font-size: 14px;
                }
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
                fb-todos-view .tv-date-clear {
                    background: none;
                    border: none;
                    color: var(--text-muted);
                    cursor: pointer;
                    font-size: 12px;
                    padding: 4px 0;
                    align-self: flex-start;
                    text-decoration: underline;
                }
                fb-todos-view .tv-date-clear:hover { color: var(--text); }
                fb-todos-view .tv-desc-input {
                    display: block;
                    width: 100%;
                    min-height: 200px;
                    background: var(--field);
                    border: 1px solid var(--border);
                    border-radius: 10px;
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
                fb-todos-view .tv-completed-pill {
                    display: inline-flex;
                    align-items: center;
                    gap: 4px;
                    padding: 2px 8px;
                    border-radius: 999px;
                    background: color-mix(in srgb, var(--ok) 12%, transparent);
                    border: 1px solid color-mix(in srgb, var(--ok) 35%, transparent);
                    color: var(--ok-text);
                    font-size: 10px;
                    font-weight: 600;
                    text-transform: uppercase;
                    letter-spacing: 0.08em;
                }
                fb-todos-view .tv-completed-pill sac-icon { --icon-size: 10px; }
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
                        <span class="tv-list-title" id="list-title">Open Todos</span>
                        <button class="icon-btn" id="toggle-completed-btn" title="Show completed" aria-label="Show completed">
                            <sac-icon name="check"></sac-icon>
                        </button>
                        <button class="icon-btn" id="new-btn" title="New todo" aria-label="New todo">
                            <sac-icon name="plus"></sac-icon>
                        </button>
                    </div>
                    <div class="tv-items" id="todo-list"></div>
                </aside>

                <main class="tv-editor-pane" slot="end">
                    <div class="tv-editor-body">
                        <div class="tv-empty-state" id="editor-empty">
                            <sac-icon name="check"></sac-icon>
                            <p>Select a todo to edit</p>
                        </div>
                        <div id="editor" hidden>
                            <input id="title" class="tv-title-input" placeholder="What needs doing?"/>
                            <div class="tv-field">
                                <label for="due-at">Due</label>
                                <input id="due-at" class="tv-date-input" type="datetime-local"/>
                                <button class="tv-date-clear" id="due-clear" hidden>Clear due date</button>
                            </div>
                            <div class="tv-field">
                                <label for="description">Notes</label>
                                <textarea id="description" class="tv-desc-input" placeholder="Anything else worth remembering?"></textarea>
                            </div>
                        </div>
                    </div>
                    <footer class="tv-editor-footer" id="editor-footer" hidden>
                        <span class="tv-editor-footer-meta">
                            Updated <span id="timestamp"></span>
                        </span>
                        <span class="tv-completed-pill" id="completed-pill" hidden>
                            <sac-icon name="check"></sac-icon> Completed
                        </span>
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
        this.querySelector("#toggle-completed-btn").addEventListener("click", () => {
            this.hideCompleted = !this.hideCompleted;
            this.querySelector("#toggle-completed-btn").classList.toggle("active", !this.hideCompleted);
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

        const dueEl = this.querySelector("#due-at");
        dueEl.addEventListener("change", () => this.saveSelected());
        this.querySelector("#due-clear").addEventListener("click", () => {
            dueEl.value = "";
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

    renderList() {
        const filtered = this.todos.filter(t => {
            if (this.hideCompleted && t.completedAt) return false;
            if (this.searchQuery) {
                const haystack = ((t.title || "") + " " + (t.description || "")).toLowerCase();
                if (!haystack.includes(this.searchQuery)) return false;
            }
            return true;
        });

        // Sort: incomplete first, then by due-at (earliest first, nulls last),
        // then by most recently updated.
        filtered.sort((a, b) => {
            const aDone = !!a.completedAt;
            const bDone = !!b.completedAt;
            if (aDone !== bDone) return aDone ? 1 : -1;

            const aDue = a.dueAt ? new Date(a.dueAt).getTime() : Infinity;
            const bDue = b.dueAt ? new Date(b.dueAt).getTime() : Infinity;
            if (aDue !== bDue) return aDue - bDue;

            return new Date(b.updatedAt || 0) - new Date(a.updatedAt || 0);
        });

        const list = this.querySelector("#todo-list");
        if (filtered.length === 0) {
            list.innerHTML = `<div class="tv-empty-list">No todos. Click + to create one.</div>`;
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
                    <button class="tv-check ${isDone ? "checked" : ""}" data-action="check"
                            title="${isDone ? "Mark as not done" : "Mark as done"}"
                            aria-label="${isDone ? "Mark as not done" : "Mark as done"}">${checkSvg}</button>
                    <div class="tv-item-title">${escapeHtml(t.title || "Untitled")}</div>
                    ${dueInfo.text ? `
                        <div class="tv-item-meta">
                            <span class="tv-item-due ${dueInfo.urgency}">
                                <sac-icon name="clock"></sac-icon>${dueInfo.text}
                            </span>
                        </div>
                    ` : ""}
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
            text = "Today · " + due.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" });
        } else if (isTomorrow) {
            text = "Tomorrow";
        } else if (diff < 0) {
            const days = Math.ceil(-diff / msPerDay);
            text = `${days} day${days === 1 ? "" : "s"} overdue`;
        } else if (diff < 7 * msPerDay) {
            text = due.toLocaleDateString(undefined, { weekday: "short" });
        } else {
            const sameYear = due.getFullYear() === now.getFullYear();
            text = sameYear
                ? due.toLocaleDateString(undefined, { month: "short", day: "numeric" })
                : due.toLocaleDateString(undefined, { year: "2-digit", month: "numeric", day: "numeric" });
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
        this.querySelector("#due-at").value      = todo.dueAt ? this._toLocalInput(todo.dueAt) : "";
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
        if (!this.selectedId) return;
        clearTimeout(this._saveDebounce);
        this._saveDebounce = null;
        const todo = this.todos.find(t => t.id === this.selectedId);
        if (!todo) return;
        const newTitle = this.querySelector("#title").value;
        const newDesc  = this.querySelector("#description").value;
        const dueVal   = this.querySelector("#due-at").value;
        const newDue   = dueVal ? new Date(dueVal).toISOString() : null;

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
            this.querySelector("#due-clear").hidden = !todo.dueAt;
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
            this.todos.unshift(created);
            await this.select(created.id);
            this.querySelector("#title").focus();
        } catch (err) {
            console.error("[fb-todos-view] create failed:", err);
        }
    }

    /** datetime-local expects "yyyy-MM-ddThh:mm" in LOCAL time. */
    _toLocalInput(iso) {
        const d = new Date(iso);
        const pad = n => String(n).padStart(2, "0");
        return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
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

customElements.define("fb-todos-view", FbTodosView);
fb.router.register("#/todos", "fb-todos-view", { label: "Todos", icon: "check" });
