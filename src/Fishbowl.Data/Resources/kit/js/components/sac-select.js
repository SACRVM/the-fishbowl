/**
 * <sac-select label="Fruit" value="apple" clearable placeholder="Pick one">
 *
 * A proper select: a combobox field showing the chosen option, and a
 * searchable list in a popover (the sac-date-field / sac-menu top-layer
 * technique). Type to filter, arrows to move, Enter to pick. Single value.
 *
 * Usage:
 *   <sac-select label="Fruit" value="pear">
 *       <option value="apple">Apple</option>
 *       <option value="pear" data-icon="star">Pear</option>
 *       <option value="plum" disabled>Plum</option>
 *   </sac-select>
 *
 *   sel.options = [{ value: "a", label: "Alpha", icon: "star" }, …];   // wins over <option>s
 *   sel.addEventListener("sac:change", e => save(e.detail.value));
 *   sel.value = "a";          // programmatic — updates the UI, fires nothing
 *
 * Options: the `options` property — [{ value, label, icon?, disabled? }] —
 * or light-DOM <option> children (value, text, data-icon, disabled,
 * selected), re-read when they change. The property wins once set; set it to
 * null to go back to the children.
 *
 * Attributes:
 *   value       — the chosen option's value, reflected. "" / absent = none.
 *                 A value no option carries is kept and shown as-is (the
 *                 options may arrive later).
 *   label       — text above the field (kit form-label); also the accessible name.
 *   placeholder — shown while nothing is chosen. Default "Select…", translated.
 *   clearable   — a × button clears the choice (value "").
 *   disabled    — greys the field out and blocks it.
 *   size        — "compact" (default, the sac-date-field compact row),
 *                 "regular" (a plain kit <input>'s metrics) or "cell" (the
 *                 cell-editor contract — style guide → Cell editors).
 *
 * Properties:
 *   value, options (get/set), selectedOption (read-only, the chosen option
 *   object or null), open (read-only), focus({ select }).
 *
 * Events:
 *   sac:change — detail { value, option } on a USER pick or clear.
 *                Bubbles, not composed.
 *   sac:commit / sac:cancel — size="cell" only, see Cell editors.
 *
 * Keyboard (in the field):
 *   typing        opens the list and filters it (labels, case-insensitive,
 *                 prefix matches first)
 *   ↓ / ↑         open (on the chosen option) / move, skipping disabled rows
 *   Home / End, PageUp / PageDown   first / last / ±10 while open
 *   Enter         pick the highlighted option (the list closes)
 *   Escape        close the list; closed: revert typed text
 *   Tab           closes the list — picking the highlighted option when you
 *                 typed or arrowed — and moves on (never swallowed)
 *
 * ARIA: the input is role="combobox" (aria-expanded, aria-controls,
 * aria-activedescendant); the list is role="listbox" of role="option" rows
 * with aria-selected / aria-disabled.
 *
 * Compact/touch: rows are 44px under (pointer: coarse), the field 44px with
 * 16px type. The popover is at most 100vw - 16px wide, clamped 8px inside
 * the viewport, flips above when there is more room there, and its height is
 * capped by the room it has — measured against the visual viewport, so an
 * on-screen keyboard shrinks it rather than covering it.
 *
 * CSS parts: label, field, input, clear, list, option.
 */
(function () {

    const t = (key, fallback) =>
        (window.sac && window.sac.t) ? window.sac.t(key, fallback) : fallback;

    let uid = 0;

class SacSelect extends HTMLElement {
    static get observedAttributes() {
        return ["value", "label", "placeholder", "clearable", "disabled", "size"];
    }

    constructor() {
        super();
        this.attachShadow({ mode: "open" });
        this._uid = `s${++uid}`;
        this._value = "";
        this._optionsProp = null;
        this._options = [];
        this._filtered = [];
        this._open = false;
        this._hl = -1;
        this._query = null;        // null = the text is the chosen label, not a search
        this._engaged = false;     // typed or arrowed since the list opened
        this._reflecting = false;
        this._snapshot = "";
        this._inside = false;

        this._onDocPointer = this._onDocPointer.bind(this);
        this._onReposition = this._onReposition.bind(this);
    }

    connectedCallback() {
        if (!this.shadowRoot.firstChild) {
            this._render();
            for (const p of ["options", "value"]) {
                if (Object.prototype.hasOwnProperty.call(this, p)) {
                    const v = this[p]; delete this[p]; this[p] = v;
                }
            }
            this._adopt();
        }
        if (!this._mo) {
            this._mo = new MutationObserver(() => { if (!this._optionsProp) this._readOptions(); });
            this._mo.observe(this, { childList: true, subtree: true, attributes: true, characterData: true });
        }
        document.addEventListener("pointerdown", this._onDocPointer, true);
        window.addEventListener("scroll", this._onReposition, true);
        window.addEventListener("resize", this._onReposition);
        window.visualViewport?.addEventListener("resize", this._onReposition);
        if (window.sac && sac.lang && !this._offLang) this._offLang = sac.lang.onChange(() => this._relabel());
    }

    disconnectedCallback() {
        if (this._offLang) { this._offLang(); this._offLang = null; }
        if (this._mo) { this._mo.disconnect(); this._mo = null; }
        this._close(false);
        document.removeEventListener("pointerdown", this._onDocPointer, true);
        window.removeEventListener("scroll", this._onReposition, true);
        window.removeEventListener("resize", this._onReposition);
        window.visualViewport?.removeEventListener("resize", this._onReposition);
    }

    attributeChangedCallback(name) {
        if (!this.shadowRoot.firstChild) return;
        switch (name) {
            case "value":       this._onValueAttr(); break;
            case "label":
            case "placeholder": this._relabel(); break;
            case "clearable":   this._syncField(); break;
            case "disabled":    this._syncDisabled(); break;
        }
    }

    /* ---------------------------------------------------------------- API */

    get value() { return this._value; }
    set value(v) { this.setAttribute("value", v == null ? "" : String(v)); }

    get options() { return this._options.map((o) => ({ ...o })); }
    set options(list) {
        this._optionsProp = Array.isArray(list) ? list : null;
        if (this.shadowRoot.firstChild) this._readOptions();
    }

    get selectedOption() {
        const o = this._find(this._value);
        return o ? { ...o } : null;
    }

    get open() { return this._open; }

    get disabled() { return this.hasAttribute("disabled"); }
    set disabled(v) { this.toggleAttribute("disabled", !!v); }

    focus(opts) {
        if (!this._input) return super.focus(opts);
        this._input.focus(opts);
        if (opts && opts.select) this._input.select();
    }

    /* ------------------------------------------------------------- state */

    _cell() { return this.getAttribute("size") === "cell"; }

    _norm(o) {
        if (o == null) return null;
        if (typeof o !== "object") o = { value: o };
        const value = o.value == null ? (o.label == null ? "" : String(o.label)) : String(o.value);
        const label = o.label == null ? value : String(o.label);
        return { value, label, icon: o.icon ? String(o.icon) : "", disabled: !!o.disabled };
    }

    _readOptions() {
        let list;
        if (this._optionsProp) {
            list = this._optionsProp.map((o) => this._norm(o)).filter(Boolean);
        } else {
            list = [...this.querySelectorAll("option")].map((el) => ({
                value: el.hasAttribute("value") ? el.getAttribute("value") : el.textContent.trim(),
                label: el.textContent.trim(),
                icon: el.getAttribute("data-icon") || "",
                disabled: el.disabled,
                _selected: el.hasAttribute("selected"),
            }));
            if (!this.hasAttribute("value")) {
                const sel = list.find((o) => o._selected);
                if (sel) { this._value = sel.value; this._reflect(); }
            }
            list = list.map(({ _selected, ...o }) => o);
        }
        this._options = list;
        this._syncField();
        if (this._open) this._filter(true);
    }

    _find(v) { return this._options.find((o) => o.value === v) || null; }

    _adopt() {
        this._value = this.getAttribute("value") || "";
        this._readOptions();
        this._relabel();
        this._syncDisabled();
    }

    _reflect() {
        if (this._value === "" && !this.hasAttribute("value")) return;
        if (this.getAttribute("value") === this._value) return;
        this._reflecting = true;
        this.setAttribute("value", this._value);
        this._reflecting = false;
    }

    _onValueAttr() {
        if (this._reflecting) return;
        this._value = this.getAttribute("value") || "";
        this._syncField();
        if (this._open) this._renderList();
    }

    _setValue(v, fire) {
        const before = this._value;
        this._value = v;
        this._reflect();
        this._syncField();
        if (fire && v !== before) {
            this.dispatchEvent(new CustomEvent("sac:change", {
                detail: { value: v, option: this.selectedOption }, bubbles: true, composed: false,
            }));
        }
    }

    /* ------------------------------------------------------------- render */

    _render() {
        this.shadowRoot.innerHTML = `
            <style>
                *, *::before, *::after { box-sizing: border-box; }
                :host {
                    display: inline-block;
                    max-width: 100%;
                    width: 20ch;
                    font-family: 'Inter', -apple-system, BlinkMacSystemFont, sans-serif;
                    color: var(--text);
                }
                :host([disabled]) { opacity: 0.5; }
                .label {
                    display: block;
                    font-size: 0.7rem;
                    text-transform: uppercase;
                    color: var(--text-muted);
                    margin-bottom: 0.4rem;
                    font-weight: 700;
                    letter-spacing: 0.05em;
                }
                .label[hidden] { display: none; }

                /* The sac-date-field compact row: same padding, border, radius. */
                .field {
                    display: flex;
                    align-items: center;
                    gap: 6px;
                    width: 100%;
                    padding: 0 0.35rem 0 0.5rem;
                    font-size: 0.78rem;
                    color: var(--text);
                    background: var(--field);
                    border: 1px solid var(--border);
                    border-radius: var(--radius-m);
                    cursor: pointer;
                    transition: border-color 150ms var(--ease-smooth);
                }
                .field:hover { border-color: var(--border-strong); }
                .field:focus-within {
                    border-color: var(--accent);
                    box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent) 10%, transparent);
                }
                :host([disabled]) .field { cursor: not-allowed; border-color: var(--border); }
                .icon { display: inline-flex; color: var(--text-muted); --icon-size: 14px; }
                .icon[hidden] { display: none; }
                .text {
                    flex: 1 1 auto;
                    min-width: 0;
                    margin: 0;
                    padding: calc(0.35rem + 1px) 0;
                    font: inherit;
                    color: inherit;
                    background: transparent;
                    border: 0;
                    outline: none;
                    text-overflow: ellipsis;
                    cursor: inherit;
                }
                .text::placeholder { color: var(--text-dim); }
                .text:focus { cursor: text; }
                .clear, .chev {
                    flex: none;
                    display: inline-flex;
                    align-items: center;
                    justify-content: center;
                    width: 20px;
                    height: 20px;
                    padding: 0;
                    color: var(--text-muted);
                    background: none;
                    border: 0;
                    border-radius: var(--radius-s);
                    --icon-size: 13px;
                }
                .clear { cursor: pointer; }
                .clear:hover { color: var(--text); background: var(--hover); }
                .clear[hidden] { display: none; }
                .chev { transition: transform 150ms var(--ease-smooth); }
                :host([open]) .chev { transform: rotate(180deg); }

                :host([size="regular"]) .field { font-size: 13.3333px; padding: 0 0.4rem 0 0.6rem; }
                :host([size="regular"]) .text { padding: calc(0.6rem + 1px) 0; line-height: normal; }

                /* The dropdown level (sac-menu, sac-date-field): fixed,
                   shown in the top layer so no clipping ancestor catches it. */
                .list {
                    position: fixed;
                    inset: auto;
                    margin: 0;
                    display: block;
                    z-index: 9999;
                    max-width: calc(100vw - 16px);
                    max-height: 320px;
                    overflow-y: auto;
                    overscroll-behavior: contain;
                    padding: 4px;
                    font-family: 'Inter', -apple-system, BlinkMacSystemFont, sans-serif;
                    font-size: 0.8rem;
                    color: var(--text);
                    background: var(--glass-strong);
                    backdrop-filter: blur(12px);
                    -webkit-backdrop-filter: blur(12px);
                    border: 1px solid var(--border-strong);
                    border-radius: var(--radius-l);
                    box-shadow: var(--shadow-2);
                }
                .list:not(:popover-open) { display: none; }
                .opt {
                    display: flex;
                    align-items: center;
                    gap: 8px;
                    min-height: 30px;
                    padding: 5px 8px;
                    border-radius: var(--radius-m);
                    cursor: pointer;
                    --icon-size: 14px;
                }
                .opt .lbl { flex: 1 1 auto; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
                .opt .ic { display: inline-flex; color: var(--text-muted); }
                .opt .tick { display: inline-flex; color: var(--accent-text); visibility: hidden; }
                .opt[aria-selected="true"] { color: var(--accent-text); font-weight: 600; }
                .opt[aria-selected="true"] .tick { visibility: visible; }
                .opt.hl { background: var(--hover); }
                .opt[aria-disabled="true"] { color: var(--text-dim); cursor: not-allowed; }
                .opt[aria-disabled="true"].hl { background: transparent; }
                .empty { padding: 8px; color: var(--text-muted); cursor: default; }
                .list::-webkit-scrollbar { width: 6px; }
                .list::-webkit-scrollbar-track { background: transparent; }
                .list::-webkit-scrollbar-thumb { background: var(--scrollbar-thumb); border-radius: 999px; }

                @media (pointer: coarse) {
                    .field, :host([size="regular"]) .field { min-height: 44px; font-size: max(16px, 1rem); }
                    .opt { min-height: 44px; }
                    .clear { width: 36px; height: 36px; }
                }
                /* A tap leaves :hover stuck — keep the highlight keyboard-owned. */
                @media (hover: none) { .clear:hover { background: none; color: var(--text-muted); } }

                /* size="cell" — the cell-editor contract. */
                :host([size="cell"]) {
                    display: block;
                    width: 100%;
                    height: 100%;
                    font: inherit;
                    color: inherit;
                }
                :host([size="cell"]) .label { display: none; }
                :host([size="cell"]) .field,
                :host([size="cell"]) .field:hover,
                :host([size="cell"]) .field:focus-within {
                    height: 100%;
                    min-height: 0;
                    padding: 0 4px 0 var(--cell-padding-inline, 8px);
                    font-size: inherit;
                    color: inherit;
                    background: transparent;
                    border: 0;
                    border-radius: 0;
                    box-shadow: none;
                }
                :host([size="cell"]) .text { padding: 0; height: 100%; }
                @media (pointer: coarse) {
                    :host([size="cell"]) .field { font-size: max(16px, 1em); }
                }
                @media (prefers-reduced-motion: reduce) { .field, .chev { transition: none; } }
            </style>
            <label class="label" part="label" for="text" hidden></label>
            <div class="field" part="field">
                <span class="icon" aria-hidden="true" hidden><sac-icon></sac-icon></span>
                <input class="text" part="input" id="text" type="text" role="combobox"
                       aria-autocomplete="list" aria-expanded="false" aria-haspopup="listbox"
                       aria-controls="${this._uid}-list" autocomplete="off" spellcheck="false" autocapitalize="off">
                <button class="clear" part="clear" type="button" tabindex="-1" hidden><sac-icon name="close"></sac-icon></button>
                <span class="chev" aria-hidden="true"><sac-icon name="chevron-down"></sac-icon></span>
            </div>
            <div class="list" part="list" id="${this._uid}-list" role="listbox" popover="manual"></div>
        `;
        const $ = (s) => this.shadowRoot.querySelector(s);
        this._labelEl = $(".label");
        this._field = $(".field");
        this._iconWrap = $(".icon");
        this._iconEl = $(".icon sac-icon");
        this._input = $(".text");
        this._clearBtn = $(".clear");
        this._list = $(".list");

        this._field.addEventListener("mousedown", (e) => {
            if (this.disabled) return;
            if (e.target !== this._input) e.preventDefault();     // keep focus in the input
        });
        this._field.addEventListener("click", (e) => {
            if (this.disabled) return;
            if (e.composedPath().includes(this._clearBtn)) return;
            this._input.focus();
            if (this._open) this._close(false);
            else this._openList();
        });
        this._clearBtn.addEventListener("click", (e) => {
            e.stopPropagation();
            if (this.disabled) return;
            this._close(false);
            this._setValue("", true);
            this._input.focus();
        });

        this._input.addEventListener("input", (e) => {
            e.stopPropagation();
            this._query = this._input.value;
            this._engaged = true;
            if (!this._open) this._openList(true);
            else this._filter(false);
        });
        this._input.addEventListener("change", (e) => e.stopPropagation());
        this._input.addEventListener("keydown", (e) => this._onKeydown(e));
        this._input.addEventListener("blur", () => {
            this._close(false);
            this._restoreText();
        });

        this._list.addEventListener("mousedown", (e) => e.preventDefault());   // focus stays in the input
        this._list.addEventListener("click", (e) => {
            const row = e.target.closest(".opt");
            if (row) this._pick(Number(row.dataset.i));
        });
        this._list.addEventListener("pointermove", (e) => {
            if (e.pointerType !== "mouse") return;
            const row = e.target.closest(".opt");
            if (row && Number(row.dataset.i) !== this._hl) this._highlight(Number(row.dataset.i), false);
        });

        this.addEventListener("focusin", () => {
            if (this._inside) return;
            this._inside = true;
            this._snapshot = this._value;
        });
        this.addEventListener("focusout", (e) => {
            if (e.relatedTarget !== this) this._inside = false;
        });
    }

    /* ------------------------------------------------------------ in place */

    _syncField() {
        if (!this._input) return;
        const o = this._find(this._value);
        if (this._query == null) this._input.value = o ? o.label : this._value;
        const icon = o && o.icon;
        this._iconWrap.hidden = !icon || this._query != null;
        if (icon) this._iconEl.setAttribute("name", icon);
        this._clearBtn.hidden = !(this.hasAttribute("clearable") && this._value !== "" && !this.disabled);
    }

    _restoreText() {
        this._query = null;
        this._syncField();
    }

    _relabel() {
        if (!this._input) return;
        const text = this.getAttribute("label") || "";
        this._labelEl.textContent = text;
        this._labelEl.hidden = !text;
        this._input.setAttribute("aria-label", text || t("select.options", "Options"));
        this._list.setAttribute("aria-label", text || t("select.options", "Options"));
        this._input.placeholder = this.getAttribute("placeholder") || t("select.placeholder", "Select…");
        this._clearBtn.setAttribute("aria-label", t("select.clear", "Clear"));
        this._clearBtn.title = t("select.clear", "Clear");
        if (this._open) this._renderList();
    }

    _syncDisabled() {
        this._input.disabled = this.disabled;
        if (this.disabled) this._close(false);
        this._syncField();
    }

    /* ---------------------------------------------------------------- list */

    _filter(keepHl) {
        const q = (this._query || "").trim().toLowerCase();
        if (!q) this._filtered = this._options.slice();
        else {
            const hits = this._options.filter((o) => o.label.toLowerCase().includes(q));
            const starts = hits.filter((o) => o.label.toLowerCase().startsWith(q));
            this._filtered = [...starts, ...hits.filter((o) => !starts.includes(o))];
        }
        if (!keepHl || this._hl >= this._filtered.length) {
            this._hl = q ? this._nextEnabled(-1, 1) : this._filtered.findIndex((o) => o.value === this._value && !o.disabled);
        }
        this._renderList();
        this._position();
    }

    _renderList() {
        const list = this._list;
        list.textContent = "";
        if (!this._filtered.length) {
            const empty = document.createElement("div");
            empty.className = "empty";
            empty.textContent = t("select.no-matches", "No matches");
            list.appendChild(empty);
            this._input.removeAttribute("aria-activedescendant");
            return;
        }
        this._filtered.forEach((o, i) => {
            const row = document.createElement("div");
            row.className = "opt" + (i === this._hl ? " hl" : "");
            row.id = `${this._uid}-o${i}`;
            row.dataset.i = String(i);
            row.setAttribute("role", "option");
            row.setAttribute("part", "option");
            row.setAttribute("aria-selected", String(o.value === this._value));
            if (o.disabled) row.setAttribute("aria-disabled", "true");
            if (o.icon) {
                const ic = document.createElement("span");
                ic.className = "ic";
                ic.innerHTML = "<sac-icon></sac-icon>";
                ic.firstChild.setAttribute("name", o.icon);
                row.appendChild(ic);
            }
            const lbl = document.createElement("span");
            lbl.className = "lbl";
            lbl.textContent = o.label;
            row.appendChild(lbl);
            const tick = document.createElement("span");
            tick.className = "tick";
            tick.innerHTML = '<sac-icon name="check"></sac-icon>';
            row.appendChild(tick);
            list.appendChild(row);
        });
        this._syncActive();
    }

    _syncActive() {
        const row = this._hl >= 0 ? this.shadowRoot.getElementById(`${this._uid}-o${this._hl}`) : null;
        if (row) {
            this._input.setAttribute("aria-activedescendant", row.id);
            row.scrollIntoView({ block: "nearest" });
        } else {
            this._input.removeAttribute("aria-activedescendant");
        }
    }

    _highlight(i, scroll) {
        this._hl = i;
        for (const r of this._list.querySelectorAll(".opt")) r.classList.toggle("hl", Number(r.dataset.i) === i);
        if (scroll !== false) this._syncActive();
        else {
            const row = this.shadowRoot.getElementById(`${this._uid}-o${i}`);
            if (row) this._input.setAttribute("aria-activedescendant", row.id);
        }
    }

    /** Next enabled index from `from` in direction `dir`, clamped; -1 if none. */
    _nextEnabled(from, dir) {
        const n = this._filtered.length;
        for (let i = from + dir; i >= 0 && i < n; i += dir) if (!this._filtered[i].disabled) return i;
        return from >= 0 && from < n && !this._filtered[from].disabled ? from : -1;
    }

    _openList(typed) {
        if (this._open || this.disabled) return;
        this._open = true;
        this._engaged = !!typed;
        if (!typed) this._query = null;
        this.setAttribute("open", "");
        this._input.setAttribute("aria-expanded", "true");
        try { this._list.showPopover(); } catch (err) { /* already shown */ }
        this._syncField();
        this._filter(false);
        if (!typed) this._input.select();                // typing replaces the shown label
    }

    _close(restoreText) {
        if (!this._open) return;
        this._open = false;
        this._engaged = false;
        this.removeAttribute("open");
        this._input.setAttribute("aria-expanded", "false");
        this._input.removeAttribute("aria-activedescendant");
        try { this._list.hidePopover(); } catch (err) { /* already hidden */ }
        if (restoreText) this._restoreText();
    }

    _pick(i) {
        const o = this._filtered[i];
        if (!o || o.disabled) return;
        this._close(false);
        this._query = null;
        this._setValue(o.value, true);
    }

    /* ------------------------------------------------------------ keyboard */

    _onKeydown(e) {
        if (this.disabled) return;
        const cell = this._cell();
        const open = this._open;
        const move = (i) => { if (i >= 0) { this._engaged = true; this._highlight(i); } };
        switch (e.key) {
            case "ArrowDown":
            case "ArrowUp":
                e.preventDefault();
                if (!open) { this._openList(false); break; }
                if (e.altKey && e.key === "ArrowUp") { this._close(true); break; }
                if (this._hl < 0) move(e.key === "ArrowDown" ? this._nextEnabled(-1, 1) : this._nextEnabled(this._filtered.length, -1));
                else move(this._nextEnabled(this._hl, e.key === "ArrowDown" ? 1 : -1));
                break;
            case "Home":
            case "End":
                if (!open) break;
                e.preventDefault();
                move(e.key === "Home" ? this._nextEnabled(-1, 1) : this._nextEnabled(this._filtered.length, -1));
                break;
            case "PageDown":
            case "PageUp": {
                if (!open) break;
                e.preventDefault();
                const dir = e.key === "PageDown" ? 1 : -1;
                let i = this._hl < 0 ? (dir > 0 ? -1 : this._filtered.length) : this._hl;
                for (let k = 0; k < 10; k++) { const n = this._nextEnabled(i, dir); if (n === i || n < 0) break; i = n; }
                move(i);
                break;
            }
            case "Enter":
                if (open) {
                    e.preventDefault();
                    if (cell) e.stopPropagation();          // the NEXT Enter commits
                    if (this._hl >= 0) this._pick(this._hl);
                    else this._close(true);
                } else if (cell) {
                    e.preventDefault();
                    e.stopPropagation();
                    this._restoreText();
                    this._emit("sac:commit", this._value, e.shiftKey);
                }
                break;
            case "Escape":
                if (open) {
                    e.preventDefault();
                    e.stopPropagation();
                    this._close(true);
                } else if (cell) {
                    e.preventDefault();
                    e.stopPropagation();
                    this._setValue(this._snapshot, false);
                    this._restoreText();
                    this._emit("sac:cancel", this._value);
                } else if (this._query != null) {
                    e.stopPropagation();
                    this._restoreText();
                }
                break;
            case "Tab":                                     // never swallowed
                if (open && this._engaged && this._hl >= 0) this._pick(this._hl);
                else this._close(true);
                break;
        }
    }

    _emit(type, value, shiftKey) {
        const detail = type === "sac:commit" ? { value, shiftKey: !!shiftKey } : { value };
        this.dispatchEvent(new CustomEvent(type, { detail, bubbles: true, composed: true }));
    }

    /* ------------------------------------------------------------ position */

    _onReposition() { this._position(); }

    /** Under the field (or above it when there is more room there), as wide
     *  as the field (min 160px), never taller than the room it has. */
    _position() {
        if (!this._open) return;
        const rect = this._field.getBoundingClientRect();
        const vv = window.visualViewport;
        const viewTop = vv ? vv.offsetTop : 0;
        const viewBottom = vv ? vv.offsetTop + vv.height : window.innerHeight;
        const vw = document.documentElement.clientWidth;
        const margin = 8, gap = 4;
        const list = this._list;
        list.style.minWidth = `${Math.min(Math.max(rect.width, 160), vw - 16)}px`;
        list.style.maxHeight = "";
        const natural = Math.min(list.scrollHeight + 2, 320);
        const below = viewBottom - rect.bottom - gap - margin;
        const above = rect.top - viewTop - gap - margin;
        const up = below < natural && above > below;
        const room = Math.max(60, up ? above : below);
        const h = Math.min(natural, room);
        list.style.maxHeight = `${h}px`;
        const top = up ? rect.top - gap - h : rect.bottom + gap;
        list.style.top = `${Math.max(viewTop + margin, top)}px`;
        const w = list.offsetWidth;
        list.style.left = `${Math.max(margin, Math.min(rect.left, vw - margin - w))}px`;
    }

    _onDocPointer(e) {
        if (!this._open) return;
        if (e.composedPath().includes(this)) return;
        this._close(true);
    }
}

customElements.define("sac-select", SacSelect);
})();
