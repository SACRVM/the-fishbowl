/**
 * <fb-calendar-view>  (mounted at #/calendar)
 *
 * Month calendar over /api/v1/events:
 *   - List pane: month navigation (‹ month year ›), agenda of the visible
 *     month grouped by day, today-jump + new-event actions.
 *   - Main pane: 6-week Monday-first month grid with event chips. Selecting
 *     an event (chip or agenda row) swaps the grid for an inline editor;
 *     the ribbon toolbar carries "back" + "delete" while editing.
 *
 * New events start as client-side drafts (id === null) because the server
 * rejects title-less events — the POST happens on the first save that has
 * a title, so abandoning a draft leaves no junk row behind.
 *
 * Recurrence: the editor offers RRULE presets (daily/weekly/…); the server
 * expands rules into per-occurrence instances on range reads, so the grid
 * and agenda show every occurrence. Instances share the master's id —
 * opening one loads the master, and edits apply to the whole series.
 *
 * Light-DOM so app.css tokens apply; all component-specific CSS scoped
 * with `fb-calendar-view`.
 */
class FbCalendarView extends HTMLElement {
    constructor() {
        super();
        this.events = [];          // events within the visible 6-week grid
        const now = new Date();
        this.cursor = new Date(now.getFullYear(), now.getMonth(), 1);
        this.selectedDay = FbCalendarView.dayKey(now);
        this.editing = null;       // event open in the editor (draft when id === null)
        this._saveDebounce = null;
    }

    static dayKey(d) {
        const pad = n => String(n).padStart(2, "0");
        return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
    }

    async connectedCallback() {
        this.render();
        await this.loadEvents();
    }

    disconnectedCallback() {
        this.flushSave();
        if (window.fb?.toolbar) fb.toolbar.clear();
    }

    /** Monday on/before the 1st of the cursor month. */
    _gridStart() {
        const first = new Date(this.cursor.getFullYear(), this.cursor.getMonth(), 1);
        const offset = (first.getDay() + 6) % 7; // Monday-first week
        return new Date(first.getFullYear(), first.getMonth(), 1 - offset);
    }

    async loadEvents() {
        const from = this._gridStart();
        const to = new Date(from.getFullYear(), from.getMonth(), from.getDate() + 42);
        try {
            const list = await fb.api.events.list({ from, to });
            this.events = (Array.isArray(list) ? list : [])
                .sort((a, b) => new Date(a.startAt) - new Date(b.startAt));
        } catch (err) {
            console.error("[fb-calendar-view] list failed:", err);
            this.events = [];
        }
        this.renderMonth();
    }

    render() {
        this.innerHTML = `
            <style>
                fb-calendar-view { display: block; height: 100%; }
                fb-calendar-view [hidden] { display: none !important; }
                fb-calendar-view .cv-layout {
                    display: flex;
                    height: calc(100vh - 50px);
                    background: var(--bg);
                }

                /* --- LIST PANE ------------------------------------------------ */
                fb-calendar-view .cv-list-pane {
                    width: 340px;
                    background: var(--panel);
                    border-right: 1px solid var(--border);
                    display: flex;
                    flex-direction: column;
                    flex-shrink: 0;
                }
                fb-calendar-view .cv-month-nav {
                    display: flex;
                    align-items: center;
                    gap: 4px;
                    padding: 12px 12px 0;
                }
                fb-calendar-view .cv-month-title {
                    flex: 1;
                    text-align: center;
                    font-family: 'Outfit', sans-serif;
                    font-weight: 700;
                    font-size: 14px;
                    color: var(--text);
                }
                fb-calendar-view .cv-icon-btn {
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
                fb-calendar-view .cv-icon-btn:hover {
                    background: rgba(255, 255, 255, 0.06);
                    color: var(--text);
                }
                fb-calendar-view .cv-icon-btn fb-icon { --icon-size: 16px; }

                fb-calendar-view .cv-list-header {
                    display: flex;
                    align-items: center;
                    padding: 12px 16px 6px;
                    gap: 2px;
                }
                fb-calendar-view .cv-list-title {
                    flex: 1;
                    font-family: 'Outfit', sans-serif;
                    font-weight: 700;
                    font-size: 11px;
                    text-transform: uppercase;
                    letter-spacing: 0.1em;
                    color: var(--text-muted);
                }
                fb-calendar-view .cv-items {
                    flex: 1;
                    overflow-y: auto;
                    padding: 2px 8px 12px;
                }
                fb-calendar-view .cv-agenda-day-header {
                    padding: 10px 8px 4px;
                    font-family: 'Outfit', sans-serif;
                    font-weight: 700;
                    font-size: 11px;
                    text-transform: uppercase;
                    letter-spacing: 0.08em;
                    color: var(--text-muted);
                }
                fb-calendar-view .cv-agenda-day-header.today { color: var(--accent); }
                fb-calendar-view .cv-agenda-item {
                    display: flex;
                    align-items: baseline;
                    gap: 8px;
                    padding: 8px 10px;
                    border-radius: 10px;
                    cursor: pointer;
                    border: 1px solid transparent;
                    transition: background 0.12s, border-color 0.12s;
                }
                fb-calendar-view .cv-agenda-item:hover { background: rgba(255, 255, 255, 0.04); }
                fb-calendar-view .cv-agenda-item.selected {
                    background: rgba(59, 130, 246, 0.12);
                    border-color: rgba(59, 130, 246, 0.28);
                }
                fb-calendar-view .cv-agenda-time {
                    flex-shrink: 0;
                    width: 52px;
                    font-size: 12px;
                    color: var(--text-muted);
                    font-variant-numeric: tabular-nums;
                }
                fb-calendar-view .cv-agenda-title {
                    flex: 1;
                    font-weight: 600;
                    font-size: 13px;
                    color: var(--text);
                    overflow: hidden;
                    text-overflow: ellipsis;
                    white-space: nowrap;
                }
                fb-calendar-view .cv-agenda-loc {
                    font-size: 11px;
                    color: var(--text-muted);
                    overflow: hidden;
                    text-overflow: ellipsis;
                    white-space: nowrap;
                    max-width: 90px;
                }
                fb-calendar-view .cv-empty-list {
                    padding: 40px 16px;
                    text-align: center;
                    color: var(--text-muted);
                    font-size: 13px;
                }

                /* --- MAIN PANE: MONTH GRID ------------------------------------ */
                fb-calendar-view .cv-main-pane {
                    flex: 1;
                    display: flex;
                    flex-direction: column;
                    min-width: 0;
                }
                fb-calendar-view .cv-grid-wrap {
                    flex: 1;
                    display: flex;
                    flex-direction: column;
                    padding: 16px 20px 20px;
                    min-height: 0;
                }
                fb-calendar-view .cv-weekdays {
                    display: grid;
                    grid-template-columns: repeat(7, 1fr);
                    gap: 6px;
                    margin-bottom: 6px;
                }
                fb-calendar-view .cv-weekday {
                    font-family: 'Outfit', sans-serif;
                    font-weight: 700;
                    font-size: 10px;
                    text-transform: uppercase;
                    letter-spacing: 0.1em;
                    color: var(--text-muted);
                    text-align: center;
                    padding: 4px 0;
                }
                fb-calendar-view .cv-grid {
                    flex: 1;
                    display: grid;
                    grid-template-columns: repeat(7, 1fr);
                    grid-auto-rows: 1fr;
                    gap: 6px;
                    min-height: 0;
                    overflow-y: auto;
                }
                fb-calendar-view .cv-day {
                    background: var(--panel);
                    border: 1px solid var(--border);
                    border-radius: 10px;
                    padding: 6px;
                    min-height: 86px;
                    cursor: pointer;
                    display: flex;
                    flex-direction: column;
                    gap: 3px;
                    overflow: hidden;
                    transition: background 0.12s, border-color 0.12s;
                }
                fb-calendar-view .cv-day:hover { background: rgba(255, 255, 255, 0.04); }
                fb-calendar-view .cv-day.other-month { opacity: 0.45; }
                fb-calendar-view .cv-day.selected {
                    border-color: rgba(59, 130, 246, 0.45);
                    background: rgba(59, 130, 246, 0.08);
                }
                fb-calendar-view .cv-day-num {
                    align-self: flex-end;
                    width: 22px;
                    height: 22px;
                    border-radius: 50%;
                    display: flex;
                    align-items: center;
                    justify-content: center;
                    font-size: 12px;
                    font-weight: 600;
                    color: var(--text-muted);
                    flex-shrink: 0;
                }
                fb-calendar-view .cv-day.today .cv-day-num {
                    background: var(--accent);
                    color: #fff;
                }
                fb-calendar-view .cv-chip {
                    font-size: 11px;
                    line-height: 1.4;
                    padding: 2px 6px;
                    border-radius: 5px;
                    background: rgba(59, 130, 246, 0.15);
                    border-left: 2px solid var(--accent);
                    color: var(--text);
                    white-space: nowrap;
                    overflow: hidden;
                    text-overflow: ellipsis;
                    cursor: pointer;
                    flex-shrink: 0;
                }
                fb-calendar-view .cv-chip:hover { background: rgba(59, 130, 246, 0.28); }
                fb-calendar-view .cv-chip.all-day {
                    background: rgba(245, 158, 11, 0.14);
                    border-left-color: var(--accent-warm);
                }
                fb-calendar-view .cv-chip.all-day:hover { background: rgba(245, 158, 11, 0.26); }
                fb-calendar-view .cv-chip-time {
                    color: var(--text-muted);
                    margin-right: 4px;
                    font-variant-numeric: tabular-nums;
                }
                fb-calendar-view .cv-more {
                    font-size: 10px;
                    color: var(--text-muted);
                    padding-left: 6px;
                    flex-shrink: 0;
                }

                /* --- MAIN PANE: EDITOR ---------------------------------------- */
                fb-calendar-view .cv-back-btn {
                    display: inline-flex;
                    align-items: center;
                    gap: 6px;
                    align-self: flex-start;
                    background: rgba(255, 255, 255, 0.06);
                    border: 1px solid var(--border);
                    border-radius: 999px;
                    color: var(--text-muted);
                    font-family: inherit;
                    font-size: 12px;
                    font-weight: 600;
                    padding: 6px 14px 6px 8px;
                    cursor: pointer;
                    margin-bottom: 22px;
                    transition: background 0.15s, color 0.15s, border-color 0.15s;
                }
                fb-calendar-view .cv-back-btn:hover {
                    background: rgba(59, 130, 246, 0.14);
                    color: var(--text);
                    border-color: var(--accent);
                }
                fb-calendar-view .cv-back-btn fb-icon { --icon-size: 14px; }
                fb-calendar-view .cv-editor-wrap {
                    flex: 1;
                    display: flex;
                    flex-direction: column;
                    min-height: 0;
                }
                fb-calendar-view .cv-editor-body {
                    flex: 1;
                    overflow: auto;
                    padding: 36px 56px;
                }
                fb-calendar-view .cv-title-input {
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
                fb-calendar-view .cv-title-input::placeholder {
                    color: var(--text-muted);
                    opacity: 0.4;
                }
                fb-calendar-view .cv-field {
                    display: flex;
                    flex-direction: column;
                    gap: 6px;
                    margin-bottom: 16px;
                }
                fb-calendar-view .cv-field label {
                    font-family: 'Outfit', sans-serif;
                    font-weight: 700;
                    font-size: 10px;
                    text-transform: uppercase;
                    letter-spacing: 0.1em;
                    color: var(--text-muted);
                }
                fb-calendar-view .cv-field-row {
                    display: flex;
                    gap: 20px;
                    flex-wrap: wrap;
                }
                fb-calendar-view .cv-date-input,
                fb-calendar-view .cv-text-input,
                fb-calendar-view .cv-select {
                    background: rgba(0, 0, 0, 0.3);
                    border: 1px solid var(--border);
                    border-radius: 8px;
                    padding: 8px 10px;
                    color: var(--text);
                    font-family: inherit;
                    font-size: 13px;
                    outline: none;
                    max-width: 260px;
                    color-scheme: dark;
                    transition: border-color 0.15s;
                }
                fb-calendar-view .cv-date-input:focus,
                fb-calendar-view .cv-text-input:focus,
                fb-calendar-view .cv-select:focus { border-color: var(--accent); }
                fb-calendar-view .cv-text-input { max-width: 420px; width: 100%; }
                fb-calendar-view .cv-allday-label {
                    display: inline-flex;
                    align-items: center;
                    gap: 8px;
                    font-size: 13px;
                    color: var(--text);
                    cursor: pointer;
                    user-select: none;
                    margin-bottom: 16px;
                }
                fb-calendar-view .cv-allday-label input {
                    accent-color: var(--accent);
                    width: 15px;
                    height: 15px;
                    cursor: pointer;
                }
                fb-calendar-view .cv-desc-input {
                    display: block;
                    width: 100%;
                    min-height: 160px;
                    background: none;
                    border: none;
                    color: var(--text);
                    font-family: inherit;
                    font-size: 14px;
                    line-height: 1.6;
                    outline: none;
                    resize: none;
                    padding: 0;
                    overflow: hidden;
                }
                fb-calendar-view .cv-desc-input::placeholder {
                    color: var(--text-muted);
                    opacity: 0.4;
                }
                fb-calendar-view .cv-repeat-mark {
                    color: var(--text-muted);
                    font-size: 0.9em;
                    margin-right: 3px;
                }
                fb-calendar-view .cv-editor-footer {
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
                fb-calendar-view .cv-hint { color: var(--accent-warm); }
            </style>

            <div class="cv-layout">
                <aside class="cv-list-pane">
                    <div class="cv-month-nav">
                        <button class="cv-icon-btn" id="cv-prev" title="Previous month"><fb-icon name="chevron-left"></fb-icon></button>
                        <div class="cv-month-title" id="cv-month-title"></div>
                        <button class="cv-icon-btn" id="cv-next" title="Next month"><fb-icon name="chevron-right"></fb-icon></button>
                    </div>
                    <div class="cv-list-header">
                        <span class="cv-list-title">Agenda</span>
                        <button class="cv-icon-btn" id="cv-today-btn" title="Jump to today"><fb-icon name="clock"></fb-icon></button>
                        <button class="cv-icon-btn" id="cv-new-btn" title="New event"><fb-icon name="plus"></fb-icon></button>
                    </div>
                    <div class="cv-items" id="cv-agenda"></div>
                </aside>

                <main class="cv-main-pane">
                    <div class="cv-grid-wrap" id="cv-grid-wrap">
                        <div class="cv-weekdays" id="cv-weekdays"></div>
                        <div class="cv-grid" id="cv-grid"></div>
                    </div>
                    <div class="cv-editor-wrap" id="cv-editor-wrap" hidden>
                        <div class="cv-editor-body">
                            <button class="cv-back-btn" id="cv-back" type="button">
                                <fb-icon name="chevron-left"></fb-icon> Back to calendar
                            </button>
                            <input id="cv-title" class="cv-title-input" placeholder="What's happening?"/>
                            <label class="cv-allday-label">
                                <input type="checkbox" id="cv-allday"/> All day
                            </label>
                            <div class="cv-field-row">
                                <div class="cv-field">
                                    <label for="cv-start">Starts</label>
                                    <input id="cv-start" class="cv-date-input" type="datetime-local"/>
                                </div>
                                <div class="cv-field">
                                    <label for="cv-end">Ends</label>
                                    <input id="cv-end" class="cv-date-input" type="datetime-local"/>
                                </div>
                                <div class="cv-field">
                                    <label for="cv-reminder">Reminder</label>
                                    <select id="cv-reminder" class="cv-select">
                                        <option value="">No reminder</option>
                                        <option value="0">At start</option>
                                        <option value="5">5 minutes before</option>
                                        <option value="15">15 minutes before</option>
                                        <option value="30">30 minutes before</option>
                                        <option value="60">1 hour before</option>
                                        <option value="120">2 hours before</option>
                                        <option value="1440">1 day before</option>
                                    </select>
                                </div>
                                <div class="cv-field">
                                    <label for="cv-repeat">Repeat</label>
                                    <select id="cv-repeat" class="cv-select">
                                        <option value="">Never</option>
                                        <option value="FREQ=DAILY">Daily</option>
                                        <option value="FREQ=WEEKLY">Weekly</option>
                                        <option value="FREQ=WEEKLY;INTERVAL=2">Every 2 weeks</option>
                                        <option value="FREQ=MONTHLY">Monthly</option>
                                        <option value="FREQ=YEARLY">Yearly</option>
                                    </select>
                                </div>
                            </div>
                            <div class="cv-field">
                                <label for="cv-location">Location</label>
                                <input id="cv-location" class="cv-text-input" placeholder="Where?"/>
                            </div>
                            <div class="cv-field">
                                <label for="cv-desc">Notes</label>
                                <textarea id="cv-desc" class="cv-desc-input" placeholder="Anything else worth remembering?"></textarea>
                            </div>
                        </div>
                        <footer class="cv-editor-footer">
                            <span id="cv-timestamp"></span>
                            <span class="cv-hint" id="cv-hint"></span>
                        </footer>
                    </div>
                </main>
            </div>
        `;
        this.attachHandlers();
    }

    attachHandlers() {
        this.querySelector("#cv-prev").addEventListener("click", () => this.navMonth(-1));
        this.querySelector("#cv-next").addEventListener("click", () => this.navMonth(1));
        this.querySelector("#cv-today-btn").addEventListener("click", () => this.goToday());
        this.querySelector("#cv-new-btn").addEventListener("click", () => this.createEvent());
        this.querySelector("#cv-back").addEventListener("click", () => this.closeEditor());

        const debounced = ["#cv-title", "#cv-location", "#cv-desc"];
        for (const sel of debounced) {
            const el = this.querySelector(sel);
            el.addEventListener("input", () => {
                if (sel === "#cv-desc") this.autosizeDesc();
                this.scheduleAutoSave();
            });
            el.addEventListener("blur", () => this.flushSave());
        }
        this.querySelector("#cv-start").addEventListener("change", () => this.saveEditing());
        this.querySelector("#cv-end").addEventListener("change", () => this.saveEditing());
        this.querySelector("#cv-reminder").addEventListener("change", () => this.saveEditing());
        this.querySelector("#cv-repeat").addEventListener("change", () => this.saveEditing());
        this.querySelector("#cv-allday").addEventListener("change", (e) => {
            this._applyAllDayMode(e.target.checked);
            this.saveEditing();
        });
    }

    navMonth(delta) {
        this.cursor = new Date(this.cursor.getFullYear(), this.cursor.getMonth() + delta, 1);
        this.loadEvents();
    }

    goToday() {
        const now = new Date();
        this.cursor = new Date(now.getFullYear(), now.getMonth(), 1);
        this.selectedDay = FbCalendarView.dayKey(now);
        this.loadEvents();
    }

    _eventsByDay() {
        const map = new Map();
        for (const e of this.events) {
            const key = FbCalendarView.dayKey(new Date(e.startAt));
            if (!map.has(key)) map.set(key, []);
            map.get(key).push(e);
        }
        return map;
    }

    renderMonth() {
        this.querySelector("#cv-month-title").textContent =
            this.cursor.toLocaleDateString(undefined, { month: "long", year: "numeric" });
        this.renderGrid();
        this.renderAgenda();
    }

    renderGrid() {
        // Weekday header — derive localized names from a known Monday.
        const weekdaysEl = this.querySelector("#cv-weekdays");
        const someMonday = new Date(2024, 0, 1); // Mon Jan 1 2024
        weekdaysEl.innerHTML = Array.from({ length: 7 }, (_, i) => {
            const d = new Date(someMonday.getFullYear(), someMonday.getMonth(), someMonday.getDate() + i);
            return `<div class="cv-weekday">${d.toLocaleDateString(undefined, { weekday: "short" })}</div>`;
        }).join("");

        const byDay = this._eventsByDay();
        const start = this._gridStart();
        const todayKey = FbCalendarView.dayKey(new Date());
        const month = this.cursor.getMonth();
        const MAX_CHIPS = 3;

        const cells = [];
        for (let i = 0; i < 42; i++) {
            const d = new Date(start.getFullYear(), start.getMonth(), start.getDate() + i);
            const key = FbCalendarView.dayKey(d);
            const dayEvents = byDay.get(key) || [];
            const classes = [
                "cv-day",
                d.getMonth() !== month ? "other-month" : "",
                key === todayKey ? "today" : "",
                key === this.selectedDay ? "selected" : "",
            ].filter(Boolean).join(" ");

            const chips = dayEvents.slice(0, MAX_CHIPS).map(e => {
                const time = e.allDay ? "" : new Date(e.startAt)
                    .toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" });
                const repeat = e.rRule ? `<span class="cv-repeat-mark" title="Repeats">&#8635;</span>` : "";
                return `<div class="cv-chip ${e.allDay ? "all-day" : ""}" data-id="${e.id}" title="${escapeHtml(e.title || "Untitled")}">
                            ${time ? `<span class="cv-chip-time">${time}</span>` : ""}${repeat}${escapeHtml(e.title || "Untitled")}
                        </div>`;
            }).join("");
            const more = dayEvents.length - MAX_CHIPS;

            cells.push(`
                <div class="${classes}" data-key="${key}">
                    <div class="cv-day-num">${d.getDate()}</div>
                    ${chips}
                    ${more > 0 ? `<div class="cv-more">+${more} more</div>` : ""}
                </div>
            `);
        }
        const grid = this.querySelector("#cv-grid");
        grid.innerHTML = cells.join("");

        grid.querySelectorAll(".cv-day").forEach(cell => {
            cell.addEventListener("click", (e) => {
                if (e.target.closest(".cv-chip")) return;
                this.selectedDay = cell.dataset.key;
                this.renderGrid();
                this.renderAgenda();
            });
            cell.addEventListener("dblclick", (e) => {
                if (e.target.closest(".cv-chip")) return;
                this.selectedDay = cell.dataset.key;
                this.createEvent(cell.dataset.key);
            });
            cell.querySelectorAll(".cv-chip").forEach(chip => {
                chip.addEventListener("click", (e) => {
                    e.stopPropagation();
                    this.openById(chip.dataset.id);
                });
            });
        });
    }

    renderAgenda() {
        const byDay = this._eventsByDay();
        const month = this.cursor.getMonth();
        const todayKey = FbCalendarView.dayKey(new Date());

        // Only cursor-month days, chronological.
        const dayKeys = [...byDay.keys()]
            .filter(k => new Date(k + "T00:00").getMonth() === month)
            .sort();

        const agenda = this.querySelector("#cv-agenda");
        if (dayKeys.length === 0) {
            agenda.innerHTML = `<div class="cv-empty-list">No events this month. Click + to create one.</div>`;
            return;
        }

        agenda.innerHTML = dayKeys.map(key => {
            const d = new Date(key + "T00:00");
            const header = d.toLocaleDateString(undefined, { weekday: "short", month: "short", day: "numeric" });
            const rows = byDay.get(key).map(e => {
                const time = e.allDay ? "all day" : new Date(e.startAt)
                    .toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" });
                const repeat = e.rRule ? ` <span class="cv-repeat-mark" title="Repeats">&#8635;</span>` : "";
                return `
                    <div class="cv-agenda-item ${e.id === this.editing?.id ? "selected" : ""}" data-id="${e.id}">
                        <span class="cv-agenda-time">${time}</span>
                        <span class="cv-agenda-title">${escapeHtml(e.title || "Untitled")}${repeat}</span>
                        ${e.location ? `<span class="cv-agenda-loc">${escapeHtml(e.location)}</span>` : ""}
                    </div>
                `;
            }).join("");
            return `
                <div class="cv-agenda-day">
                    <div class="cv-agenda-day-header ${key === todayKey ? "today" : ""}">${header}</div>
                    ${rows}
                </div>
            `;
        }).join("");

        agenda.querySelectorAll(".cv-agenda-item").forEach(el => {
            el.addEventListener("click", () => this.openById(el.dataset.id));
        });
    }

    async openById(id) {
        await this.flushSave();
        const evt = this.events.find(e => e.id === id);
        if (!evt) return;
        if (evt.isRecurringInstance) {
            // Instances share the master's id and carry a shifted startAt —
            // editing must happen on the master or a blur would silently
            // move the whole series' start to this occurrence.
            try {
                const master = await fb.api.events.get(id);
                if (master) { this._openEditor(master); return; }
            } catch (err) {
                console.error("[fb-calendar-view] master fetch failed:", err);
                return;
            }
        }
        this._openEditor(evt);
    }

    createEvent(dayStr) {
        const key = dayStr || this.selectedDay || FbCalendarView.dayKey(new Date());
        const now = new Date();
        let start;
        if (key === FbCalendarView.dayKey(now)) {
            start = new Date(now);
            start.setMinutes(0, 0, 0);
            start.setHours(start.getHours() + 1);
        } else {
            const [y, m, d] = key.split("-").map(Number);
            start = new Date(y, m - 1, d, 9, 0, 0, 0);
        }
        const end = new Date(start.getTime() + 3600_000);
        const draft = {
            id: null,
            title: "",
            allDay: false,
            startAt: start.toISOString(),
            endAt: end.toISOString(),
            location: null,
            reminderMinutes: null,
            rRule: null,
            description: null,
        };
        this._openEditor(draft);
    }

    _openEditor(evt) {
        this.editing = evt;
        this._fillEditor(evt);
        this.querySelector("#cv-grid-wrap").hidden = true;
        this.querySelector("#cv-editor-wrap").hidden = false;
        this._refreshFooter();
        fb.toolbar.set([
            {
                icon:    "chevron-left",
                title:   "Back to calendar",
                onClick: () => this.closeEditor()
            },
            {
                icon:    "trash",
                title:   "Delete event",
                onClick: () => this.deleteEditing()
            }
        ]);
        if (!evt.id) this.querySelector("#cv-title").focus();
        requestAnimationFrame(() => this.autosizeDesc());
        this.renderAgenda();
    }

    _fillEditor(e) {
        this.querySelector("#cv-title").value = e.title || "";
        this.querySelector("#cv-allday").checked = !!e.allDay;
        this._applyAllDayMode(!!e.allDay);
        const start = this.querySelector("#cv-start");
        const end = this.querySelector("#cv-end");
        start.value = e.startAt ? this._toInputValue(e.startAt, !!e.allDay) : "";
        end.value   = e.endAt   ? this._toInputValue(e.endAt,   !!e.allDay) : "";
        this.querySelector("#cv-reminder").value = e.reminderMinutes ?? "";
        this._fillRepeat(e.rRule);
        this.querySelector("#cv-location").value = e.location || "";
        this.querySelector("#cv-desc").value = e.description || "";
    }

    /** Map the event's rrule onto a preset; unknown rules (e.g. imported
     *  BYDAY combinations) get a transient "Custom" option so saving
     *  doesn't clobber them. */
    _fillRepeat(rrule) {
        const select = this.querySelector("#cv-repeat");
        select.querySelector("option[data-custom]")?.remove();
        const value = rrule || "";
        if (value && ![...select.options].some(o => o.value === value)) {
            const opt = document.createElement("option");
            opt.value = value;
            opt.textContent = "Custom rule";
            opt.setAttribute("data-custom", "");
            select.appendChild(opt);
        }
        select.value = value;
    }

    /** Switch start/end inputs between date and datetime-local, converting values. */
    _applyAllDayMode(allDay) {
        for (const sel of ["#cv-start", "#cv-end"]) {
            const input = this.querySelector(sel);
            const old = input.value;
            input.type = allDay ? "date" : "datetime-local";
            if (!old) continue;
            input.value = allDay
                ? old.slice(0, 10)
                : (old.length === 10 ? old + "T09:00" : old);
        }
    }

    async closeEditor() {
        await this.flushSave();
        this._discardEditor();
    }

    _discardEditor() {
        clearTimeout(this._saveDebounce);
        this._saveDebounce = null;
        this.editing = null;
        this.querySelector("#cv-editor-wrap").hidden = true;
        this.querySelector("#cv-grid-wrap").hidden = false;
        fb.toolbar.clear();
        this.renderMonth();
    }

    scheduleAutoSave() {
        clearTimeout(this._saveDebounce);
        this._saveDebounce = setTimeout(() => this.saveEditing(), 700);
    }

    async flushSave() {
        clearTimeout(this._saveDebounce);
        this._saveDebounce = null;
        await this.saveEditing();
    }

    autosizeDesc() {
        const el = this.querySelector("#cv-desc");
        if (!el || this.querySelector("#cv-editor-wrap").hidden) return;
        el.style.height = "auto";
        el.style.height = el.scrollHeight + "px";
    }

    _collectForm() {
        const hint = this.querySelector("#cv-hint");
        const title = this.querySelector("#cv-title").value.trim();
        const allDay = this.querySelector("#cv-allday").checked;
        const startVal = this.querySelector("#cv-start").value;
        const endVal = this.querySelector("#cv-end").value;

        if (!title) {
            hint.textContent = "Add a title to save";
            return null;
        }
        if (!startVal) {
            hint.textContent = "Start is required";
            return null;
        }
        const startAt = allDay ? new Date(startVal + "T00:00") : new Date(startVal);
        let endAt = null;
        if (endVal) endAt = allDay ? new Date(endVal + "T00:00") : new Date(endVal);
        if (endAt && endAt < startAt) {
            hint.textContent = "End can't be before start";
            return null;
        }
        hint.textContent = "";
        const reminderVal = this.querySelector("#cv-reminder").value;
        return {
            title,
            allDay,
            startAt: startAt.toISOString(),
            endAt: endAt ? endAt.toISOString() : null,
            location: this.querySelector("#cv-location").value.trim() || null,
            reminderMinutes: reminderVal === "" ? null : parseInt(reminderVal, 10),
            rRule: this.querySelector("#cv-repeat").value || null,
            description: this.querySelector("#cv-desc").value || null,
        };
    }

    async saveEditing() {
        if (!this.editing) return;
        clearTimeout(this._saveDebounce);
        this._saveDebounce = null;
        const form = this._collectForm();
        if (!form) return; // invalid — hint shown, keep local state untouched

        // ISO formats differ between server ("o", 7 fractional digits) and
        // toISOString (3 digits) — compare instants, not strings, so an
        // open-then-close without edits doesn't fire a no-op PUT.
        const sameInstant = (a, b) => {
            if (!a && !b) return true;
            if (!a || !b) return false;
            return new Date(a).getTime() === new Date(b).getTime();
        };
        const e = this.editing;
        const unchanged = e.id
            && form.title === e.title
            && form.allDay === !!e.allDay
            && sameInstant(form.startAt, e.startAt)
            && sameInstant(form.endAt, e.endAt)
            && form.location === (e.location || null)
            && form.reminderMinutes === (e.reminderMinutes ?? null)
            && form.rRule === (e.rRule || null)
            && form.description === (e.description || null);
        if (unchanged) return;

        Object.assign(e, form);
        try {
            if (!e.id) {
                const created = await fb.api.events.create(e);
                if (created && created.id) e.id = created.id;
            } else {
                await fb.api.events.update(e.id, e);
            }
            e.updatedAt = new Date().toISOString();
            this._refreshFooter();
            // Re-fetch instead of patching locally: recurring rules fan a
            // single row out into many grid instances only the server
            // computes. renderMonth runs inside loadEvents.
            await this.loadEvents();
        } catch (err) {
            console.error("[fb-calendar-view] save failed:", err);
            this.querySelector("#cv-hint").textContent = "Save failed — will retry on next change";
        }
    }

    _refreshFooter() {
        const ts = this.querySelector("#cv-timestamp");
        const hint = this.querySelector("#cv-hint");
        if (!this.editing) { ts.textContent = ""; hint.textContent = ""; return; }
        if (!this.editing.id) {
            ts.textContent = "";
            hint.textContent = "Not saved yet — add a title";
        } else {
            ts.textContent = "Updated " + this.formatFullTimestamp(this.editing.updatedAt)
                + (this.editing.rRule ? " · repeats — edits apply to the whole series" : "");
            hint.textContent = "";
        }
    }

    async deleteEditing() {
        const e = this.editing;
        if (!e) return;
        if (!e.id) { this._discardEditor(); return; } // unsaved draft — just drop it

        const result = await fb.dialog.confirm({
            title:   "Delete this event?",
            message: e.rRule
                ? "This event repeats — the whole series and its reminders will be permanently deleted."
                : "This event and its reminders will be permanently deleted.",
            buttons: [
                { action: "cancel", label: "Cancel", kind: "default" },
                { action: "delete", label: "Delete", kind: "destructive", armAfterMs: 2000 },
            ],
        });
        if (result !== "delete") return;

        clearTimeout(this._saveDebounce);
        this._saveDebounce = null;
        try {
            await fb.api.events.delete(e.id);
            this.events = this.events.filter(x => x.id !== e.id);
            this._discardEditor();
        } catch (err) {
            console.error("[fb-calendar-view] delete failed:", err);
        }
    }

    /** datetime-local expects "yyyy-MM-ddThh:mm" LOCAL; date expects "yyyy-MM-dd". */
    _toInputValue(iso, dateOnly) {
        const d = new Date(iso);
        const pad = n => String(n).padStart(2, "0");
        const date = `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
        return dateOnly ? date : `${date}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
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

customElements.define("fb-calendar-view", FbCalendarView);
fb.router.register("#/calendar", "fb-calendar-view", { label: "Calendar", icon: "calendar" });
