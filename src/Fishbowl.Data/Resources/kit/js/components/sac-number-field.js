/**
 * <sac-number-field value="1234.5" label="Price" decimals="2" min="0" step="0.5">
 *
 * A text input for numbers, shown in the page-wide sac.regional number
 * format ("1,234.50", "1.234,50", "1 234,50", "1'234.50") and re-shown live
 * when that format changes. The keyboard steps it; there are no ± buttons
 * (that is <sac-stepper>). Same row family as <sac-date-field>.
 *
 * Usage:
 *   <sac-number-field label="Price" value="1234.5" decimals="2"></sac-number-field>
 *   field.addEventListener("sac:change", e => save(e.detail.value));   // number | null
 *   field.value = 99;     // programmatic — updates the UI, fires nothing
 *
 * Attributes:
 *   value       — a plain JS number string ("1234.5", "-0.25"), REFLECTED so.
 *                 Empty or absent = no value (null). Garbage is rejected and
 *                 the last valid value put back.
 *   min / max   — inclusive bounds. A typed number outside them is invalid
 *                 and never commits; stepping clamps into them.
 *   step        — what ↑ / ↓ add (default 1); Shift steps 10×.
 *   decimals    — fraction digits: the committed value is rounded to them
 *                 and the text always shows exactly that many. Absent = as
 *                 many as the number has.
 *   label       — text above the row (kit form-label); also the accessible name.
 *   placeholder — the input's placeholder.
 *   disabled    — greys the row out and blocks the input.
 *   size        — "compact" (default, the sac-date-field compact row),
 *                 "regular" (a plain kit <input>'s metrics) or "cell" (the
 *                 cell-editor contract — style guide → Cell editors).
 *
 * Properties:
 *   value — get/set, a number or null. Setting is programmatic: no event.
 *   focus({ select }) — focuses the input; select: true selects its text.
 *
 * Events:
 *   sac:change — detail { value } (number | null), on a USER commit that
 *                moved the value: Enter, or focus leaving. Bubbles, not composed.
 *   sac:commit / sac:cancel — size="cell" only, see Cell editors.
 *
 * Keyboard: ↑ / ↓ step (Shift ×10, clamped), Enter commits, Escape reverts.
 * Invalid text shows --danger and reverts on blur. Typing is tolerant: both
 * "," and "." are understood (sac.regional.parseNumber).
 *
 * Compact/touch: inputmode="decimal" brings up the number pad. Under
 * (pointer: coarse) the input is 44px tall with 16px type.
 *
 * CSS parts: label, input.
 */
(function () {

    const t = (key, fallback) =>
        (window.sac && window.sac.t) ? window.sac.t(key, fallback) : fallback;

    const reg = () => (window.sac && sac.regional && sac.regional.parseNumber) ? sac.regional : null;

    /** Fraction digits a number needs ("0.25" → 2). */
    const fracDigits = (n) => {
        const s = String(n);
        const e = /e-(\d+)$/.exec(s);
        if (e) return +e[1];
        const i = s.indexOf(".");
        return i < 0 ? 0 : s.length - i - 1;
    };
    const roundTo = (n, d) => Number(n.toFixed(Math.min(20, d)));

class SacNumberField extends HTMLElement {
    static get observedAttributes() {
        return ["value", "min", "max", "step", "decimals", "label", "placeholder", "disabled", "size"];
    }

    constructor() {
        super();
        this.attachShadow({ mode: "open" });
        this._value = null;
        this._dirty = false;
        this._reflecting = false;
        this._snapshot = null;      // value when focus entered (cell cancel)
        this._inside = false;
    }

    connectedCallback() {
        if (!this.shadowRoot.firstChild) {
            this._render();
            if (Object.prototype.hasOwnProperty.call(this, "value")) {
                const v = this.value; delete this.value; this.value = v;
            }
            this._adopt();
        }
        if (window.sac && sac.lang && !this._offLang) this._offLang = sac.lang.onChange(() => this._syncLabel());
        if (window.sac && sac.regional && !this._offRegional) this._offRegional = sac.regional.onChange(() => this._syncUI());
    }

    disconnectedCallback() {
        if (this._offLang) { this._offLang(); this._offLang = null; }
        if (this._offRegional) { this._offRegional(); this._offRegional = null; }
    }

    attributeChangedCallback(name) {
        if (!this.shadowRoot.firstChild) return;
        switch (name) {
            case "value":       this._onValueAttr(); break;
            case "decimals":    this._syncUI(); break;
            case "min":
            case "max":         this._syncAria(); break;
            case "label":       this._syncLabel(); break;
            case "placeholder": this._input.placeholder = this.getAttribute("placeholder") || ""; break;
            case "disabled":    this._input.disabled = this.disabled; break;
        }
    }

    /* ---------------------------------------------------------------- API */

    get value() { return this._value; }
    set value(v) {
        if (v == null || v === "") { this.setAttribute("value", ""); return; }
        const n = typeof v === "number" ? v : Number(String(v).trim());
        if (!Number.isFinite(n)) return;                 // garbage in → nothing changes
        this.setAttribute("value", String(n));
    }

    get disabled() { return this.hasAttribute("disabled"); }
    set disabled(v) { this.toggleAttribute("disabled", !!v); }

    focus(opts) {
        if (!this._input) return super.focus(opts);
        this._input.focus(opts);
        if (opts && opts.select) this._input.select();
    }

    /* ------------------------------------------------------------- state */

    _cell() { return this.getAttribute("size") === "cell"; }

    _num(name) {
        const raw = this.getAttribute(name);
        if (raw == null || raw.trim() === "") return null;
        const n = Number(raw);
        return Number.isFinite(n) ? n : null;
    }

    _decimals() {
        const d = this._num("decimals");
        return d == null ? null : Math.max(0, Math.min(20, Math.round(d)));
    }

    /** Round to `decimals` when set. */
    _round(n) {
        const d = this._decimals();
        return d == null ? n : roundTo(n, d);
    }

    _inRange(n) {
        const min = this._num("min"), max = this._num("max");
        return !(min != null && n < min) && !(max != null && n > max);
    }

    _parse(text) {
        const r = reg();
        if (r) return r.parseNumber(text);
        const s = String(text).trim();
        return /^[+-]?(\d+\.?\d*|\.\d+)$/.test(s) ? Number(s) : NaN;
    }

    _format(n) {
        if (n == null) return "";
        const d = this._decimals();
        const opts = d == null ? {} : { decimals: d };
        const r = reg();
        return r ? r.formatNumber(n, opts) : (d == null ? String(n) : n.toFixed(d));
    }

    _adopt() {
        const raw = this.getAttribute("value");
        const n = raw == null || raw.trim() === "" ? null : Number(raw);
        this._value = n != null && Number.isFinite(n) ? this._round(n) : null;
        this._syncLabel();
        this._input.placeholder = this.getAttribute("placeholder") || "";
        this._input.disabled = this.disabled;
        this._reflect();
        this._syncUI();
    }

    _reflect() {
        const s = this._value == null ? "" : String(this._value);
        if (s === "" && !this.hasAttribute("value")) return;
        if (this.getAttribute("value") === s) return;
        this._reflecting = true;
        this.setAttribute("value", s);
        this._reflecting = false;
    }

    _onValueAttr() {
        if (this._reflecting) return;
        const raw = this.getAttribute("value");
        if (raw == null || raw.trim() === "") { this._apply(null, false); return; }
        const n = Number(raw);
        if (!Number.isFinite(n)) { this._reflect(); return; }
        this._apply(this._round(n), false);
    }

    _apply(n, fire) {
        const before = this._value;
        this._value = n;
        this._reflect();
        this._syncUI();
        if (fire && n !== before) {
            this.dispatchEvent(new CustomEvent("sac:change", {
                detail: { value: n }, bubbles: true, composed: false,
            }));
        }
    }

    _syncUI() {
        if (!this._input) return;
        if (!this._dirty) {
            const shown = this._format(this._value);
            if (this._input.value !== shown) this._input.value = shown;
            this._input.classList.remove("invalid");
        }
        this._syncAria();
    }

    _syncAria() {
        const i = this._input;
        const set = (a, v) => (v == null ? i.removeAttribute(a) : i.setAttribute(a, String(v)));
        set("aria-valuenow", this._value);
        set("aria-valuemin", this._num("min"));
        set("aria-valuemax", this._num("max"));
        set("aria-valuetext", this._value == null ? null : this._format(this._value));
    }

    _syncLabel() {
        const text = this.getAttribute("label") || "";
        this._labelEl.textContent = text;
        this._labelEl.hidden = !text;
        this._input.setAttribute("aria-label", text || t("number-field.number", "Number"));
    }

    /* ------------------------------------------------------------- render */

    _render() {
        this.shadowRoot.innerHTML = `
            <style>
                *, *::before, *::after { box-sizing: border-box; }
                :host {
                    display: inline-block;
                    max-width: 100%;
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

                /* The sac-date-field compact input: same padding, type,
                   border and radius, so the two sit in one row. */
                .num {
                    display: block;
                    width: 12ch;
                    max-width: 100%;
                    margin: 0;
                    padding: 0.35rem 0.5rem;
                    font-family: var(--font-mono);
                    font-size: 0.78rem;
                    font-variant-numeric: tabular-nums;
                    color: var(--text);
                    background: var(--field);
                    border: 1px solid var(--border);
                    border-radius: var(--radius-m);
                    transition: border-color 150ms var(--ease-smooth), color 150ms var(--ease-smooth);
                }
                .num::placeholder { color: var(--text-dim); }
                .num:hover:not(:disabled) { border-color: var(--border-strong); }
                .num:focus {
                    outline: none;
                    border-color: var(--accent);
                    box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent) 10%, transparent);
                }
                .num:disabled { cursor: not-allowed; }
                .num.invalid { color: var(--danger-text); border-bottom-color: var(--danger); }
                .num.invalid:focus { border-color: var(--accent); border-bottom-color: var(--danger); }

                :host([size="regular"]) .num {
                    padding: 0.6rem;
                    font-family: inherit;
                    font-size: 13.3333px;
                    line-height: normal;
                    width: 14ch;
                }

                @media (pointer: coarse) {
                    .num, :host([size="regular"]) .num { min-height: 44px; font-size: max(16px, 1rem); }
                }

                /* size="cell" — the cell-editor contract: borderless, fills
                   the cell, the cell's font, no label, no own focus ring. */
                :host([size="cell"]) {
                    display: block;
                    width: 100%;
                    height: 100%;
                    font: inherit;
                    color: inherit;
                }
                :host([size="cell"]) .label { display: none; }
                :host([size="cell"]) .num,
                :host([size="cell"]) .num:focus,
                :host([size="cell"]) .num:hover {
                    width: 100%;
                    height: 100%;
                    min-height: 0;
                    padding: 0 var(--cell-padding-inline, 8px);
                    font: inherit;
                    font-variant-numeric: tabular-nums;
                    color: inherit;
                    text-align: right;
                    background: transparent;
                    border: 0;
                    border-radius: 0;
                    box-shadow: none;
                }
                :host([size="cell"]) .num.invalid { color: var(--danger-text); }
                @media (pointer: coarse) {
                    :host([size="cell"]) .num { font-size: max(16px, 1em); }
                }
                @media (prefers-reduced-motion: reduce) { .num { transition: none; } }
            </style>
            <label class="label" part="label" for="num" hidden></label>
            <input class="num" part="input" id="num" type="text" inputmode="decimal" role="spinbutton"
                   spellcheck="false" autocomplete="off" autocapitalize="off">
        `;
        this._labelEl = this.shadowRoot.querySelector(".label");
        this._input = this.shadowRoot.getElementById("num");

        this._input.addEventListener("input", (e) => {
            e.stopPropagation();
            this._dirty = true;
            this._markValidity();
        });
        this._input.addEventListener("change", (e) => e.stopPropagation());
        this._input.addEventListener("keydown", (e) => this._onKeydown(e));
        this._input.addEventListener("blur", () => this._commit(true));

        // Cell contract: remember the value focus arrived with.
        this.addEventListener("focusin", (e) => {
            if (this._inside) return;
            this._inside = true;
            this._snapshot = this._value;
        });
        this.addEventListener("focusout", (e) => {
            if (e.relatedTarget !== this) this._inside = false;
        });
    }

    /* ------------------------------------------------------------ editing */

    /** Text → number, null for empty, NaN for invalid / out of range. */
    _typed() {
        const s = this._input.value.trim();
        if (s === "") return null;
        const n = this._parse(s);
        return Number.isFinite(n) && this._inRange(this._round(n)) ? this._round(n) : NaN;
    }

    _markValidity() {
        const n = this._typed();
        this._input.classList.toggle("invalid", Number.isNaN(n));
    }

    _onKeydown(e) {
        if (this.disabled) return;
        const cell = this._cell();
        switch (e.key) {
            case "ArrowUp":
            case "ArrowDown":
                e.preventDefault();
                this._step((e.key === "ArrowUp" ? 1 : -1) * (e.shiftKey ? 10 : 1));
                break;
            case "Enter": {
                e.preventDefault();
                const ok = this._commit(false);
                if (cell) {
                    e.stopPropagation();
                    if (ok) this._emit("sac:commit", this._value, e.shiftKey);
                }
                break;
            }
            case "Escape":
                if (cell) {
                    e.preventDefault();
                    e.stopPropagation();
                    this._cancel();
                } else if (this._dirty) {
                    e.stopPropagation();
                    this._revert();
                }
                break;
            case "Tab":
                if (cell) this._commit(true);        // never swallowed: no preventDefault
                break;
        }
    }

    /** Step from the typed text (or the value); empty starts at 0 / min. */
    _step(dir) {
        const typed = this._dirty ? this._typed() : this._value;
        let base = typed;
        if (typed == null) base = null;
        else if (Number.isNaN(typed)) {
            const loose = this._parse(this._input.value);   // out of range still steps
            if (!Number.isFinite(loose)) return;
            base = loose;
        }
        const step = Math.abs(this._num("step") || 1);
        const min = this._num("min"), max = this._num("max");
        let n;
        if (base == null) n = min != null && min > 0 ? min : (max != null && max < 0 ? max : 0);
        else n = base + dir * step;
        const d = Math.max(fracDigits(step), base == null ? 0 : fracDigits(base), this._decimals() || 0);
        n = roundTo(n, d);
        if (min != null) n = Math.max(min, n);
        if (max != null) n = Math.min(max, n);
        n = this._round(n);
        this._dirty = true;
        this._input.value = this._format(n);
        this._input.classList.remove("invalid");
        this._input.setAttribute("aria-valuenow", String(n));
    }

    /** @returns true when the text is a valid value (committed or unchanged). */
    _commit(revertOnInvalid) {
        if (!this._dirty) return true;
        const n = this._typed();
        if (Number.isNaN(n)) {
            if (revertOnInvalid) this._revert();
            return false;
        }
        this._dirty = false;
        this._input.classList.remove("invalid");
        this._apply(n, true);
        return true;
    }

    _revert() {
        this._dirty = false;
        this._input.classList.remove("invalid");
        this._syncUI();
    }

    _cancel() {
        this._dirty = false;
        this._apply(this._snapshot, false);
        this._input.classList.remove("invalid");
        this._emit("sac:cancel", this._value);
    }

    _emit(type, value, shiftKey) {
        const detail = type === "sac:commit" ? { value, shiftKey: !!shiftKey } : { value };
        this.dispatchEvent(new CustomEvent(type, { detail, bubbles: true, composed: true }));
    }
}

customElements.define("sac-number-field", SacNumberField);
})();
