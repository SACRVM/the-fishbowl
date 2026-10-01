/**
 * sac-data-grid — column types.
 *
 * One entry per column `type`: how a value is shown in a cell, written to the
 * clipboard, read back from pasted text, filtered, and (sheet mode) which kit
 * field edits it (SPEC §5). The grid reads this registry at runtime, so it
 * can load before or after this file as long as both are there before the
 * first grid renders.
 *
 *   SacDataGridTypes.get(name)            the type object ("text" when unknown)
 *   SacDataGridTypes.define(name, type)   add or replace a type for every grid
 *
 * A column may also carry its own type object instead of a name (SPEC §5,
 * custom types): { render(value, row), editor(cell), parse, format }. Missing
 * members fall back to the "text" type.
 *
 * Values, per built-in type:
 *   text, longtext  string
 *   number          a finite number, or null
 *   date            "yyyy-mm-dd", or ""
 *   time            "HH:MM" (24-hour, whatever the display), or ""
 *   datetime        "yyyy-mm-ddTHH:MM" (local, no zone), or ""
 *   bool            true / false
 *   select          an option's value, or "" / null
 *   tags            string[]
 *   color           "#rrggbb" (or "#rrggbbaa"), or ""
 *   readonly        anything; shown through the column's format(value, row)
 *
 * Column options the types read (all optional):
 *   options     select / tags: [{ value, label?, labelKey?, color? }] or plain
 *               strings. color is a kit palette slot ("blue", "orange", …).
 *   decimals, min, max, step   number (and min / max / step for date, time)
 *   allowCreate tags: false restricts entries to the options (default true)
 *   format(value, row)  → the shown text, for every type (readonly: required)
 *   parse(text, row)    → the value, for pasted text; throw or return
 *                         undefined to reject it
 *
 * Dates, times and numbers are shown and read the kit's way (sac.regional:
 * formatDate / parseDate, formatTime / parseTime, formatNumber / parseNumber;
 * kit ≥ 2.22.0) and follow its settings live.
 */
(function () {
    if (window.SacDataGridTypes) return;

    const t = (key, fallback) => (window.sac && sac.t) ? sac.t(key, fallback) : fallback;
    const regional = () => (window.sac && sac.regional)
        ? sac.regional.get() : { date: "iso", hourCycle: "h23", number: "1,234.5" };
    const pad = (n, w) => String(n).padStart(w || 2, "0");

    /** No value at all: the empty cell. */
    const isEmpty = (v) => v == null || v === "" || (Array.isArray(v) && v.length === 0);

    /* -------------------------------------------------- dates and times -- */

    // Shown and typed text follow the kit (sac.regional, kit ≥ 2.22.0): the
    // same rules as <sac-date-field> and <sac-time-field>. The norm* helpers
    // only tidy a STORED value first (a Date, a datetime string, seconds).
    // Without the kit helpers, dates and times stay ISO.
    let regionalWarned = false;
    function kitRegional(name) {
        const r = window.sac && sac.regional;
        if (r && typeof r[name] === "function") return r[name];
        if (!regionalWarned) {
            regionalWarned = true;
            console.warn(`[sac-data-grid] sac.regional.${name} is missing: dates and times show as ISO. `
                + "Load SACRVM APPKIT ≥ 2.22.0.");
        }
        return null;
    }

    /** A real local calendar date as ISO, or null. */
    function ymd(y, m, d) {
        if (!(y >= 1 && y <= 9999 && m >= 1 && m <= 12 && d >= 1 && d <= 31)) return null;
        const dt = new Date(2000, m - 1, d);
        dt.setFullYear(y);
        if (dt.getFullYear() !== y || dt.getMonth() !== m - 1 || dt.getDate() !== d) return null;
        return `${pad(y, 4)}-${pad(m)}-${pad(d)}`;
    }

    /** Any stored date value → ISO "yyyy-mm-dd", or "" when there is none. */
    function normDate(v) {
        if (v instanceof Date) return isNaN(v) ? "" : ymd(v.getFullYear(), v.getMonth() + 1, v.getDate()) || "";
        const m = /^(\d{4})-(\d{1,2})-(\d{1,2})/.exec(String(v == null ? "" : v).trim());
        return m ? ymd(+m[1], +m[2], +m[3]) || "" : "";
    }

    /** A stored date → its regional text; fmt ("dmy." …) overrides the page. */
    function formatDate(v, fmt) {
        const iso = normDate(v);
        const kit = kitRegional("formatDate");
        return iso && kit ? kit(iso, fmt ? { format: fmt } : undefined) : iso;
    }

    /** Typed or pasted text → ISO date, "" for empty text, null for garbage.
     *  ISO always works; otherwise day and month come in the order of the
     *  regional format, any of . / - between, two-digit years allowed. */
    function parseDate(text, fmt) {
        const s = String(text == null ? "" : text).trim();
        const kit = kitRegional("parseDate");
        if (kit) return kit(s, fmt ? { format: fmt } : undefined);
        return s === "" ? "" : (/^\d{4}-\d{1,2}-\d{1,2}$/.test(s) && normDate(s)) || null;
    }

    /** Any stored time value → "HH:MM", or "". */
    function normTime(v) {
        const m = /^(\d{1,2}):(\d{1,2})/.exec(String(v == null ? "" : v).trim());
        if (!m || +m[1] > 23 || +m[2] > 59) return "";
        return `${pad(+m[1])}:${pad(+m[2])}`;
    }

    /** A stored time → its text in the page hour cycle, or in cycle ("h12"). */
    function formatTime(v, cycle) {
        const s = normTime(v);
        const kit = kitRegional("formatTime");
        return s && kit ? kit(s, cycle ? { hourCycle: cycle } : undefined) : s;
    }

    /** Text → "HH:MM", "" for empty, null for garbage. Both hour cycles are
     *  read: "9:05", "14.30", "14:30:00", "2:30 pm", "2 PM". */
    function parseTime(text) {
        const s = String(text == null ? "" : text).trim();
        const kit = kitRegional("parseTime");
        if (kit) return kit(s);
        return s === "" ? "" : (/^\d{1,2}:\d{2}$/.test(s) && normTime(s)) || null;
    }

    /** Any stored datetime value → "yyyy-mm-ddTHH:MM", or "". A date alone
     *  means midnight. */
    function normDateTime(v) {
        if (v instanceof Date) {
            if (isNaN(v)) return "";
            return `${normDate(v)}T${pad(v.getHours())}:${pad(v.getMinutes())}`;
        }
        const s = String(v == null ? "" : v).trim();
        const d = normDate(s);
        if (!d) return "";
        const tm = normTime(s.slice(s.search(/[T ]/) + 1 || s.length)) || "00:00";
        return `${d}T${tm}`;
    }

    function formatDateTime(v) {
        const s = normDateTime(v);
        return s ? `${formatDate(s.slice(0, 10))} ${formatTime(s.slice(11))}` : "";
    }

    /** "25.09.2026 14:30", "9/25/2026 2:30 PM", "2026-09-25T14:30"; a date
     *  alone means midnight. A date may hold spaces itself ("25. 9. 2026"),
     *  so every split point is tried in turn. */
    function parseDateTime(text) {
        const s = String(text == null ? "" : text).trim();
        if (s === "") return "";
        // Date and time first: sac.regional.parseDate (kit ≥ 2.22.2) also takes
        // "date time" and drops the time, which a datetime must keep.
        const split = /T|\s+/g;
        for (let m; (m = split.exec(s));) {
            const d = parseDate(s.slice(0, m.index));
            const tm = d && parseTime(s.slice(m.index + m[0].length));
            if (tm) return `${d}T${tm}`;
        }
        const date = parseDate(s);
        return date ? `${date}T00:00` : null;
    }

    /* ---------------------------------------------------------- numbers -- */

    // sac.regional.formatNumber builds an Intl formatter per call; a scrolling
    // grid asks for the same few thousand values again and again.
    const numCache = new Map();
    let numFmt = null;

    function formatNumber(v, decimals, group) {
        if (typeof v !== "number" || !Number.isFinite(v)) return isEmpty(v) ? "" : String(v);
        const fmt = regional().number;
        if (fmt !== numFmt) { numCache.clear(); numFmt = fmt; }
        const key = `${decimals == null ? "" : decimals}|${group === false ? 0 : 1}|${v}`;
        let s = numCache.get(key);
        if (s === undefined) {
            const opts = { group: group !== false };
            if (decimals != null) opts.decimals = decimals;
            s = (window.sac && sac.regional) ? sac.regional.formatNumber(v, opts) : String(v);
            if (numCache.size > 20000) numCache.clear();
            numCache.set(key, s);
        }
        return s;
    }

    function parseNumber(text) {
        const s = String(text == null ? "" : text).trim();
        if (s === "") return null;
        const n = (window.sac && sac.regional) ? sac.regional.parseNumber(s) : Number(s);
        return Number.isFinite(n) ? n : NaN;
    }

    /* ---------------------------------------------------------- options -- */

    /** Column options → [{ value, label, labelKey, color }]. */
    function normOptions(options) {
        if (!Array.isArray(options)) return [];
        return options.map((o) => (o !== null && typeof o === "object")
            ? { value: o.value !== undefined ? o.value : o.name, label: o.label, labelKey: o.labelKey, color: o.color }
            : { value: o, label: String(o) });
    }

    const optionLabel = (o) => o.labelKey ? t(o.labelKey, o.label != null ? o.label : String(o.value))
        : (o.label != null ? String(o.label) : String(o.value));

    function findOption(col, value) {
        const opts = col.options || [];
        for (const o of opts) if (o.value === value) return o;
        const s = String(value);
        for (const o of opts) if (String(o.value) === s) return o;
        return null;
    }

    /** Pasted text → an option, by label (current language) or by value. */
    function matchOption(col, text) {
        const s = String(text).trim().toLocaleLowerCase();
        const opts = col.options || [];
        return opts.find((o) => optionLabel(o).toLocaleLowerCase() === s)
            || opts.find((o) => String(o.value).toLocaleLowerCase() === s)
            || null;
    }

    const PALETTE = ["blue", "orange", "red", "green", "purple", "pink", "yellow", "teal", "gray", "indigo"];
    const slot = (color) => PALETTE.includes(color) ? color : "gray";

    /** What <sac-chip-input> accepts as a tag name (it lower-cases input). */
    const TAG_NAME = /^[a-z0-9_:-]{1,50}$/;

    /** A tags column's options as <sac-chip-input> suggestions: the value is
     *  the name, label / labelKey the shown text (it relabels live). */
    const tagSuggestions = (col) => (col.options || []).map((o) => ({
        name: String(o.value),
        color: slot(o.color),
        label: o.label != null ? String(o.label) : undefined,
        labelKey: o.labelKey,
    }));

    /* ------------------------------------------------------------ colors -- */

    function normColor(v) {
        const m = /^#?([0-9a-f]{3}|[0-9a-f]{6}|[0-9a-f]{8})$/i.exec(String(v == null ? "" : v).trim());
        if (!m) return "";
        let h = m[1].toLowerCase();
        if (h.length === 3) h = h.replace(/./g, (c) => c + c);
        return "#" + h;
    }

    /* ----------------------------------------------------------- editors -- */

    const warned = new Set();

    /** A kit field for a cell, or null when its script is not loaded (the
     *  grid then falls back to a plain .cell-input, and says so once). */
    function kitField(tag, col, attrs) {
        if (!customElements.get(tag)) {
            if (!warned.has(tag)) {
                warned.add(tag);
                console.warn(`[sac-data-grid] <${tag}> is not loaded: ${col.typeName} cells fall back to a text input. `
                    + "Load kit/js/components/" + tag + ".js (SACRVM APPKIT ≥ 2.22.0).");
            }
            return null;
        }
        const el = document.createElement(tag);
        el.setAttribute("size", "cell");
        for (const [k, v] of Object.entries(attrs || {})) {
            if (v != null && v !== false) el.setAttribute(k, v === true ? "" : String(v));
        }
        el.setAttribute("aria-label", col.labelText());
        return el;
    }

    /** The date + time pair of a datetime cell: one editor with the cell-
     *  editor contract (value, focus, sac:commit / sac:cancel), Tab moving
     *  between its two halves before it leaves the cell. */
    function dateTimeEditor(col) {
        const date = kitField("sac-date-field", col, { min: col.min && normDate(col.min), max: col.max && normDate(col.max) });
        const time = kitField("sac-time-field", col, { step: col.step });
        if (!date || !time) return null;
        const box = document.createElement("div");
        box.className = "dt-editor";
        box.append(date, time);
        let snapshot = "";
        Object.defineProperty(box, "value", {
            get() {
                const d = date.value;
                return d ? `${d}T${time.value || "00:00"}` : "";
            },
            set(v) {
                const s = normDateTime(v);
                snapshot = s;
                date.value = s ? s.slice(0, 10) : "";
                time.value = s ? s.slice(11) : "";
            },
        });
        box.focus = (opts) => date.focus(opts);
        box.addEventListener("keydown", (e) => {
            if (e.key !== "Tab") return;
            const inDate = e.composedPath().includes(date);
            if (inDate && !e.shiftKey) { e.preventDefault(); e.stopPropagation(); time.focus(); }
            else if (!inDate && e.shiftKey) { e.preventDefault(); e.stopPropagation(); date.focus(); }
        }, true);
        // The halves report their own commit / cancel; the pair reports one,
        // carrying the combined value.
        for (const type of ["sac:commit", "sac:cancel"]) {
            box.addEventListener(type, (e) => {
                if (e.target === box) return;
                e.stopPropagation();
                if (type === "sac:cancel") box.value = snapshot;
                box.dispatchEvent(new CustomEvent(type, {
                    detail: { value: box.value, shiftKey: !!(e.detail && e.detail.shiftKey) },
                    bubbles: true, composed: true,
                }));
            });
        }
        return box;
    }

    /* ------------------------------------------------------------- types -- */

    const TYPES = Object.create(null);

    /**
     * A type:
     *   align          "left" | "right" | "center"
     *   width          default column width in px
     *   filter         the filter UI kind: "text" | "number" | "date" | "time" | "any" | "bool"
     *   text(v, col, row)   the shown text (also what copy writes, unless copy())
     *   copy(v, col, row)   clipboard text, when it differs from text()
     *   parse(text, col)    pasted text → value; undefined = not a valid value
     *   render(cell, v, col, row)   optional: paint the cell itself (bool, tags, color)
     *   editor(col)         sheet-mode editor element, or null = no editor
     *   empty(v)            true when the cell counts as empty
     *   numeric             true: sum / avg apply
     */
    const base = {
        align: "left",
        width: 160,
        filter: "text",
        text(v) { return isEmpty(v) ? "" : String(v); },
        parse(text) { return String(text); },
        editor() { return null; },
        empty: isEmpty,
        numeric: false,
    };

    function define(name, type) {
        TYPES[name] = Object.assign(Object.create(base), type, { name });
        return TYPES[name];
    }

    define("text", {
        editor() { return null; },       // the grid's own .cell-input
        plainEditor: true,
    });

    define("longtext", {
        width: 240,
        text(v) {
            if (isEmpty(v)) return "";
            const s = String(v);
            const i = s.search(/\r?\n/);
            return i < 0 ? s : s.slice(0, i) + " …";
        },
        copy(v) { return isEmpty(v) ? "" : String(v); },
        editor() { return null; },       // the grid's own popover textarea
        longEditor: true,
    });

    define("number", {
        align: "right",
        width: 120,
        filter: "number",
        numeric: true,
        text(v, col) { return formatNumber(v, col && col.decimals); },
        copy(v, col) { return formatNumber(v, col && col.decimals, false); },
        parse(text) {
            const n = parseNumber(text);
            return Number.isNaN(n) ? undefined : n;
        },
        editor(col) {
            return kitField("sac-number-field", col, { decimals: col.decimals, min: col.min, max: col.max, step: col.step });
        },
    });

    // editSample: a value as wide as the type gets, so an editor opened on an
    // empty (or short) value still has room for what will be typed.
    define("date", {
        width: 130,
        filter: "date",
        text(v) { return formatDate(v); },
        parse(text) { const v = parseDate(text); return v == null ? undefined : v; },
        editor(col) {
            return kitField("sac-date-field", col, { min: col.min && normDate(col.min), max: col.max && normDate(col.max) });
        },
        editSample() { return "2026-12-28";
        },
    });

    define("time", {
        width: 100,
        filter: "time",
        text(v) { return formatTime(v); },
        parse(text) { const v = parseTime(text); return v == null ? undefined : v; },
        editor(col) {
            return kitField("sac-time-field", col, { step: col.step, min: col.min && normTime(col.min), max: col.max && normTime(col.max) });
        },
        editSample() { return "12:58";
        },
    });

    define("datetime", {
        width: 190,
        filter: "date",
        text(v) { return formatDateTime(v); },
        parse(text) { const v = parseDateTime(text); return v == null ? undefined : v; },
        editor: dateTimeEditor,
        editSample() { return "2026-12-28T12:58"; },
    });

    define("bool", {
        align: "center",
        width: 80,
        filter: "bool",
        text(v) { return v ? t("data-grid.yes", "Yes") : t("data-grid.no", "No"); },
        copy(v) { return v ? "TRUE" : "FALSE"; },
        parse(text) {
            const s = String(text).trim().toLocaleLowerCase();
            const yes = t("data-grid.yes", "Yes").toLocaleLowerCase();
            const no = t("data-grid.no", "No").toLocaleLowerCase();
            if (["true", "yes", "y", "1", "x", "✓", "✔", "on", "wahr", "ja", yes].includes(s)) return true;
            if (["false", "no", "n", "0", "", "off", "falsch", "nein", no].includes(s)) return false;
            return undefined;
        },
        empty() { return false; },
        render(cell, v) {
            let mark = cell.firstChild;
            if (!mark || mark.className !== "bool") {
                cell.textContent = "";
                mark = document.createElement("span");
                mark.className = "bool";
                cell.appendChild(mark);
            }
            mark.classList.toggle("on", !!v);
            mark.textContent = v ? t("data-grid.yes", "Yes") : t("data-grid.no", "No");
        },
    });

    define("select", {
        width: 150,
        filter: "any",
        text(v, col) {
            if (isEmpty(v)) return "";
            const o = findOption(col, v);
            return o ? optionLabel(o) : String(v);
        },
        parse(text, col) {
            if (String(text).trim() === "") return "";
            const o = matchOption(col, text);
            return o ? o.value : undefined;
        },
        editor(col) {
            const el = kitField("sac-select", col, {});
            if (el) el.options = (col.options || []).map((o) => ({ value: String(o.value), label: optionLabel(o) }));
            return el;
        },
        // The option with the longest label: picking it must not cut it short.
        editSample(col) {
            let best = null;
            for (const o of col.options || []) if (!best || optionLabel(o).length > optionLabel(best).length) best = o;
            return best ? String(best.value) : null;
        },
        // sac-select speaks strings; hand back the option's own value.
        fromEditor(v, col) {
            if (isEmpty(v)) return "";
            const o = findOption(col, v);
            return o ? o.value : v;
        },
        toEditor(v) { return isEmpty(v) ? "" : String(v); },
    });

    define("tags", {
        width: 220,
        filter: "any",
        text(v, col) {
            const list = Array.isArray(v) ? v : (isEmpty(v) ? [] : [v]);
            return list.map((name) => {
                const o = findOption(col, name);
                return o ? optionLabel(o) : String(name);
            }).join(", ");
        },
        // Labels or names; a new name follows the kit's tag-name rule, so the
        // chip editor can hold it.
        parse(text, col) {
            const list = [];
            for (const part of String(text).split(/[,;\n]/)) {
                const s = part.trim();
                if (!s) continue;
                const o = matchOption(col, s);
                const name = o ? String(o.value) : s.toLowerCase();
                if (!o && (col.allowCreate === false || !TAG_NAME.test(name))) return undefined;
                if (!list.includes(name)) list.push(name);
            }
            return list;
        },
        render(cell, v, col) {
            const list = Array.isArray(v) ? v : [];
            const labels = list.map((name) => { const o = findOption(col, name); return [o ? optionLabel(o) : String(name), slot(o && o.color)]; });
            if (!customElements.get("sac-chip")) {
                if (!warned.has("sac-chip")) {
                    warned.add("sac-chip");
                    console.warn("[sac-data-grid] <sac-chip> is not loaded: tags cells show plain text. "
                        + "Load kit/js/components/sac-chip.js (SACRVM APPKIT ≥ 2.22.0).");
                }
                cell.textContent = labels.map((l) => l[0]).join(", ");
                return;
            }
            // The kit's chip, the one the chip editor shows: view and edit look
            // alike. The chips a (pooled) cell already holds are reused: a new
            // one sets up a shadow root, which scrolling should not pay for.
            for (const n of [...cell.childNodes]) if (n.nodeName !== "SAC-CHIP") n.remove();
            labels.forEach(([label, color], i) => {
                let chip = cell.children[i];
                if (!chip) { chip = document.createElement("sac-chip"); cell.appendChild(chip); }
                if (chip.getAttribute("color") !== color) chip.setAttribute("color", color);
                if (chip.getAttribute("label") !== label) chip.setAttribute("label", label);
            });
            while (cell.children.length > labels.length) cell.lastElementChild.remove();
        },
        editor(col) {
            const el = kitField("sac-chip-input", col, { "allow-create": col.allowCreate !== false });
            if (el) el.suggestions = tagSuggestions(col);
            return el;
        },
        toEditor(v) { return Array.isArray(v) ? v.slice() : []; },
        fromEditor(v) { return Array.isArray(v) ? v.slice() : []; },
    });

    define("color", {
        width: 130,
        text(v) { return normColor(v); },
        parse(text) {
            if (String(text).trim() === "") return "";
            const c = normColor(text);
            return c || undefined;
        },
        render(cell, v) {
            const c = normColor(v);
            cell.textContent = "";
            if (!c) return;
            const sw = document.createElement("span");
            sw.className = "swatch";
            sw.style.setProperty("--swatch", c);
            const hex = document.createElement("span");
            hex.className = "hex";
            hex.textContent = c;
            cell.append(sw, hex);
        },
        editor(col) { return kitField("sac-color-field", col, {}); },
    });

    define("readonly", {
        text(v) { return isEmpty(v) ? "" : String(v); },
        parse() { return undefined; },
        readonly: true,
    });

    window.SacDataGridTypes = {
        get(name) { return TYPES[name] || TYPES.text; },
        define,
        names() { return Object.keys(TYPES); },
        /**
         * A column's own type object (SPEC §5) → a registry-shaped type.
         *   render(value, row) → a string or a Node for the cell
         *   format(value, row) → the text (copy, fit width, screen readers)
         *   parse(text)        → the value for pasted text (throw = invalid)
         *   editor(cell)       → the sheet-mode editor element; cell is
         *                        { value, row, field, column }. It should
         *                        follow the kit's cell-editor contract.
         * align / width / filter / numeric are taken over when present.
         */
        custom(spec) {
            const type = Object.create(TYPES.text);
            type.name = spec.name || "custom";
            for (const k of ["align", "width", "filter", "numeric"]) if (spec[k] !== undefined) type[k] = spec[k];
            if (typeof spec.format === "function") {
                type.text = (v, col, row) => { const s = spec.format(v, row); return s == null ? "" : String(s); };
            }
            if (typeof spec.render === "function") {
                type.render = (cell, v, col, row) => {
                    const out = spec.render(v, row);
                    if (out instanceof Node) cell.replaceChildren(out);
                    else cell.textContent = out == null ? "" : String(out);
                };
            }
            if (typeof spec.parse === "function") {
                type.parse = (text) => { try { return spec.parse(text); } catch (err) { return undefined; } };
                type.customParse = true;
            }
            if (typeof spec.editor === "function") {
                type.plainEditor = false;
                type.customEditor = spec.editor;
            }
            return type;
        },
        isEmpty,
        normOptions,
        optionLabel,
        findOption,
        tagSuggestions,
        normDate, formatDate, parseDate,
        normTime, formatTime, parseTime,
        normDateTime, formatDateTime, parseDateTime,
        formatNumber, parseNumber,
        normColor,
    };
})();
