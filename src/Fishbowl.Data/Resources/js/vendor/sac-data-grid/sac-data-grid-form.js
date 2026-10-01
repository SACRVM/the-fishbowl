/**
 * sac-data-grid — the record form (SPEC §2): one row as a form in a
 * <sac-dialog>: every column with its label (regular-size kit fields),
 * validation, Save / Cancel, Previous / Next and New.
 *
 *   form mode    Enter, a double-click or "Open record" edits the row here
 *   sheet mode   Shift+Enter or "Open record" (row menu); columns with
 *                inline: false are edited only here
 *   read mode    the same form, read-only
 *   phones       the way to edit whatever the mode (the dialog becomes a
 *                bottom sheet); a tap on the active cell opens it.
 *                compact-edit="read" makes it read-only there (SPEC §7-4).
 *
 * The form commits one record — one entry in source.save(changes) — through
 * the grid's own validation, dirty tracking and save mode. Ctrl+Enter saves.
 * Escape or a click beside it with changes in the form asks before they are
 * thrown away; the explicit Cancel does not ask.
 *
 * The dialog lives in the document (not in the grid's shadow root), where
 * the kit's form styles (ui.css) reach it.
 *
 * Loaded after js/sac-data-grid-edit.js.
 */
(function () {
    const Grid = window.SacDataGrid;
    if (!Grid || !Grid.prototype._startEdit || Grid.prototype._openRecord) return;
    const P = Grid.prototype;

    const t = (key, fallback, vars) => {
        let s = (window.sac && sac.t) ? sac.t(key, fallback) : fallback;
        if (vars) s = String(s).replace(/\{(\w+)\}/g, (m, k) => (vars[k] !== undefined ? String(vars[k]) : m));
        return s;
    };
    const int = (n) => (window.sac && sac.regional) ? sac.regional.formatNumber(n) : String(n);
    const Types = () => window.SacDataGridTypes;
    const isBlank = (v) => v == null || v === "" || (Array.isArray(v) && !v.length);
    const same = (a, b) => a === b || (isBlank(a) && isBlank(b))
        || (a != null && b != null && typeof a === "object" && JSON.stringify(a) === JSON.stringify(b));
    let uid = 0;

    // Light-DOM content needs light-DOM rules; namespaced, tokens only.
    const CSS = `
        .sdg-nav { flex-wrap: wrap; margin: 0 0 16px; }
        .sdg-nav .sdg-pos { margin-left: auto; font-size: 0.75rem; color: var(--text-muted); font-variant-numeric: tabular-nums; }
        .sdg-form { display: grid; grid-template-columns: repeat(auto-fill, minmax(190px, 1fr)); gap: 14px 16px; }
        .sdg-form .sdg-field { min-width: 0; }
        .sdg-form .sdg-wide { grid-column: 1 / -1; }
        .sdg-form .sdg-pair { display: flex; flex-wrap: wrap; align-items: flex-end; gap: 8px; }
        .sdg-form .sdg-ro { min-height: 2.2rem; padding: 0.5rem 0; color: var(--text-muted); font-variant-numeric: tabular-nums; overflow-wrap: anywhere; }
        .sdg-form textarea { min-height: 96px; resize: vertical; }
        .sdg-form input[type="checkbox"] { width: 18px; height: 18px; margin: 0.45rem 0; }
        .sdg-form .sdg-err { margin-top: 4px; font-size: 0.72rem; color: var(--danger-text); }
        .sdg-form .sdg-err:empty { display: none; }
        @media (pointer: coarse) {
            .sdg-form input[type="checkbox"] { width: 24px; height: 24px; margin: 10px 0; }
        }
    `;

    function ensureStyle() {
        if (document.getElementById("sac-data-grid-form-style")) return;
        const s = document.createElement("style");
        s.id = "sac-data-grid-form-style";
        s.textContent = CSS;
        document.head.appendChild(s);
    }

    const warned = new Set();
    function kit(tag, attrs) {
        if (!customElements.get(tag)) {
            if (!warned.has(tag)) {
                warned.add(tag);
                console.warn(`[sac-data-grid] <${tag}> is not loaded: the form uses a text input (SACRVM APPKIT ≥ 2.22.0).`);
            }
            return null;
        }
        const el = document.createElement(tag);
        for (const [k, v] of Object.entries(attrs || {})) {
            if (v != null && v !== false) el.setAttribute(k, v === true ? "" : String(v));
        }
        return el;
    }

    /* ------------------------------------------------------------- hooks -- */

    const prevDblClick = P._onCellDblClick;
    P._onCellDblClick = function (r, c, e) {
        if (r >= this._rowCount() && (this.mode === "form" || this._compact())) { this._openRecord(r); return; }
        return prevDblClick.call(this, r, c, e);
    };

    /** Phones: a tap on the cell that was already active opens the record. */
    const prevCellClick = P._onCellClick;
    P._onCellClick = function (r, c, wasActive) {
        if (prevCellClick) prevCellClick.call(this, r, c, wasActive);
        if (wasActive && this._compact() && !this._editor) this._openRecord(r);
    };

    const prevModeChanged = P._onModeChanged;
    P._onModeChanged = function () {
        prevModeChanged.call(this);
        const st = this._formState;
        if (st && st.readOnly !== !this._formEditable()) this._formRefill();
    };

    /** A language switch relabels an open form, keeping what was typed. */
    const prevRelabel = P._onRelabel;
    P._onRelabel = function () {
        prevRelabel.call(this);
        if (this._formState) this._formRefill();
    };

    Grid.hook("disconnect", function () {
        const st = this._formState;
        if (!st) return;
        this._formState = null;
        if (typeof st.dlg.close === "function") st.dlg.close(null);
        st.dlg.remove();
    });

    P._formEditable = function () {
        if (!this._editable() || this.mode === "read") return false;
        return !(this._compact() && this.getAttribute("compact-edit") === "read");
    };

    /* -------------------------------------------------------------- open -- */

    /** Open display row r (default: the cursor's) in the record form. On the
     *  "new row" line (or past the rows) it opens a new record. `restore`:
     *  field states to put back (a reopened form). */
    P._openRecord = function (r, restore) {
        if (!this._built || !this._cols.length || this._formState) return;
        if (this._editor) this._commitEdit(null);
        if (r == null) r = this._cur.r;
        if (r >= this._rowCount()) {
            if (!this._formEditable() || !this.addRow()) return;
            r = this._cur.r;
        }
        if (!this._rowAt(r)) return;
        ensureStyle();
        const dlg = document.createElement("sac-dialog");
        dlg.style.setProperty("--dialog-width", "min(680px, calc(100vw - 32px))");
        const nav = document.createElement("div");
        nav.className = "sdg-nav toolbar";
        const form = document.createElement("div");
        form.className = "sdg-form";
        dlg.append(nav, form);
        const st = { dlg, nav, form, r, row: null, fields: [], readOnly: true, uid: ++uid };
        this._formState = st;
        dlg.beforeAction = async (action) => (action === "save" ? this._formCommit(false) : true);
        dlg.addEventListener("sac:action", (e) => this._formClosed(st, e.detail ? e.detail.action : null));
        dlg.addEventListener("keydown", (e) => {
            if (e.key === "Enter" && (e.ctrlKey || e.metaKey) && !st.readOnly) {
                e.preventDefault();
                e.stopPropagation();
                dlg.trigger("save");
            }
        });
        this._formFill(r);
        if (restore) this._formRestore(restore);
        document.body.appendChild(dlg);
        dlg.open();
        requestAnimationFrame(() => {
            const first = st.fields.find((f) => f.focus);
            if (first) first.focus.focus();
        });
    };

    /** (Re)build the form for display row r. */
    P._formFill = function (r) {
        const st = this._formState;
        if (!st) return;
        const row = this._rowAt(r);
        if (!row) return;
        if (r !== this._cur.r) this._setCursor(r, this._cur.c, false);
        r = this._cur.r;
        st.r = r;
        st.row = row;
        st.readOnly = !this._formEditable() || this._isDeleted(row);
        const e = this._entryOf(row);
        const isNew = !!(e && e.op === "create");
        const view = this._view(row);

        // Title: the first text column's value, else "Record n".
        const titleCol = this._all.find((c) => c.typeName === "text" && !c.def.hidden);
        const titleText = titleCol ? titleCol.text(this._get(row, titleCol), view) : "";
        st.dlg.setAttribute("title", isNew ? t("data-grid.new-record", "New record")
            : (titleText || t("data-grid.record", "Record {n}", { n: int(this._base() + r + 1) })));

        // Previous · Next · New · position
        const rows = this._rowCount();
        const btn = (act, iconName, text, disabled, after) => {
            const b = document.createElement("button");
            b.type = "button";
            b.className = "btn";
            b.disabled = disabled;
            const i = document.createElement("sac-icon");
            i.setAttribute("name", iconName);
            i.setAttribute("aria-hidden", "true");
            if (after) b.append(document.createTextNode(text), i);
            else b.append(i, document.createTextNode(text));
            b.addEventListener("click", () => this._formNav(act));
            return b;
        };
        const pos = document.createElement("span");
        pos.className = "sdg-pos";
        pos.setAttribute("aria-live", "polite");
        pos.textContent = isNew ? t("data-grid.new-record", "New record")
            : (this._total != null || this._paging() === "pages"
                ? t("data-grid.record-of", "Record {n} of {m}", { n: int(r + 1), m: int(rows) })
                : t("data-grid.record", "Record {n}", { n: int(r + 1) }));
        st.nav.replaceChildren(
            btn("prev", "chevron-left", t("data-grid.previous", "Previous"), r <= 0),
            btn("next", "chevron-right", t("data-grid.next", "Next"), r >= rows - 1, true),
            ...(this._formEditable() ? [btn("new", "plus", t("data-grid.new", "New"), false)] : []),
            pos,
        );

        // Fields: every column, definition order (hidden ones too — the form
        // is where a row is seen whole).
        st.fields = this._all.map((col) => this._formField(col, row, this._get(row, col), view, st));
        st.form.replaceChildren(...st.fields.map((f) => f.wrap));
        this._formShowErrors(e ? e.errors : {});

        st.dlg.buttons = st.readOnly
            ? [{ action: "close", label: t("data-grid.close", "Close"), labelKey: "data-grid.close", kind: "primary" }]
            : [
                { action: "cancel", label: t("data-grid.cancel", "Cancel"), labelKey: "data-grid.cancel" },
                { action: "save", label: t("data-grid.save", "Save"), labelKey: "data-grid.save", kind: "primary" },
            ];
    };

    /** Rebuild the open form in place (language, mode), keeping typed values
     *  and the focused field. */
    P._formRefill = function () {
        const st = this._formState;
        if (!st) return;
        const snap = this._formSnapshot();
        const at = st.fields.findIndex((f) => f.focus && (f.focus === document.activeElement || f.focus.contains(document.activeElement)));
        const r = this._indexOfId(this._idOf(st.row));
        this._formFill(r >= 0 ? r : st.r);
        this._formRestore(snap);
        if (at >= 0 && st.fields[at] && st.fields[at].focus) st.fields[at].focus.focus();
    };

    /** field → what the user has in it (text for a plain input). */
    P._formSnapshot = function () {
        const st = this._formState;
        const out = {};
        if (!st) return out;
        for (const f of st.fields) if (f.snap) out[f.col.field] = f.snap();
        return out;
    };

    P._formRestore = function (snap) {
        const st = this._formState;
        if (!st || st.readOnly || !snap) return;
        for (const f of st.fields) {
            if (f.restore && Object.prototype.hasOwnProperty.call(snap, f.col.field)) f.restore(snap[f.col.field]);
        }
    };

    /** Does the form hold anything the row does not? */
    P._formDirty = function () {
        const st = this._formState;
        if (!st || st.readOnly) return false;
        for (const f of st.fields) {
            if (!f.get) continue;
            const res = f.get();
            if (res.error || !same(res.value, this._get(st.row, f.col))) return true;
        }
        return false;
    };

    /**
     * One labelled field: { col, wrap, get(), snap(), restore(s), focus, err }.
     * get → { value } or { error }; snap / restore carry the raw state (the
     * text of a plain input, the value of a kit field). get null = shown only.
     */
    P._formField = function (col, row, value, view, st) {
        const T = Types();
        const wrap = document.createElement("div");
        wrap.className = "sdg-field";
        const id = `sdg-${st.uid}-${String(col.field).replace(/[^\w-]/g, "_")}`;
        const label = col.labelText();
        const ro = st.readOnly;
        const caption = (forId) => {
            const l = document.createElement("label");
            l.textContent = label + (col.required && !ro && !col.readonly ? " *" : "");
            if (forId) l.htmlFor = forId;
            return l;
        };
        const f = { col, wrap, get: null, snap: null, restore: null, focus: null, err: null };
        const type = col.type;
        const name = col.typeName;

        if (col.readonly) {
            const d = document.createElement("div");
            d.className = "sdg-ro";
            d.textContent = col.text(value, view) || "—";
            wrap.append(caption(), d);
            return f;
        }

        // A kit field whose value is the column's value (type conversions in
        // and out); `into` and `out` default to the identity.
        const field = (el, into, out) => {
            el.value = into ? into(value) : value;
            f.focus = el;
            f.get = () => ({ value: out ? out(el.value) : el.value });
            f.snap = () => el.value;
            f.restore = (s) => { el.value = s; };
            return el;
        };
        const plain = (multi) => {
            const input = document.createElement(multi ? "textarea" : "input");
            if (!multi) input.type = "text";
            input.id = id;
            input.value = (type.plainEditor || type.longEditor) ? (value == null ? "" : String(value)) : col.copy(value, row);
            input.disabled = ro;
            wrap.append(caption(id), input);
            f.focus = input;
            f.get = () => {
                const s = input.value;
                if (type.plainEditor || type.longEditor) return { value: s };
                const v = this._parseText(col, s, row);
                return v === undefined ? { error: t("data-grid.invalid", "Not a valid value") } : { value: v };
            };
            f.snap = () => input.value;
            f.restore = (s) => { input.value = s; };
        };
        const reg = (tag, attrs) => kit(tag, Object.assign({ size: "regular", label: label + (col.required && !ro ? " *" : ""), disabled: ro }, attrs));

        let el;
        if (type.customEditor && !ro) {
            try { el = type.customEditor({ value, row: view, field: col.field, column: col.def }); } catch (err) { el = null; }
            if (el) {
                wrap.append(caption());
                wrap.append(field(el, null, (v) => {
                    if (typeof v === "string" && (type.customParse || typeof col.def.parse === "function")) {
                        const p = this._parseText(col, v, row);
                        return p === undefined ? v : p;
                    }
                    return v;
                }));
            } else plain(false);
        } else if (name === "longtext") {
            plain(true);
            wrap.classList.add("sdg-wide");
        } else if (name === "number" && (el = reg("sac-number-field", { decimals: col.decimals, min: col.min, max: col.max, step: col.step }))) {
            wrap.append(field(el));
        } else if (name === "date" && (el = reg("sac-date-field", { min: col.min && T.normDate(col.min), max: col.max && T.normDate(col.max) }))) {
            wrap.append(field(el, T.normDate, (v) => v || ""));
        } else if (name === "time" && (el = reg("sac-time-field", { step: col.step, min: col.min && T.normTime(col.min), max: col.max && T.normTime(col.max) }))) {
            wrap.append(field(el, T.normTime, (v) => v || ""));
        } else if (name === "datetime" && customElements.get("sac-date-field") && customElements.get("sac-time-field")) {
            const s = T.normDateTime(value);
            const d = reg("sac-date-field", {});
            const tm = kit("sac-time-field", { size: "regular", disabled: ro, "aria-label": label });
            d.value = s ? s.slice(0, 10) : "";
            tm.value = s ? s.slice(11) : "";
            const pair = document.createElement("div");
            pair.className = "sdg-pair";
            pair.append(d, tm);
            wrap.append(pair);
            f.focus = d;
            f.get = () => ({ value: d.value ? `${d.value}T${tm.value || "00:00"}` : "" });
            f.snap = () => [d.value, tm.value];
            f.restore = (st2) => { d.value = st2[0]; tm.value = st2[1]; };
        } else if (name === "bool") {
            const input = document.createElement("input");
            input.type = "checkbox";
            input.id = id;
            input.checked = !!value;
            input.disabled = ro;
            wrap.append(caption(id), input);
            f.focus = input;
            f.get = () => ({ value: input.checked });
            f.snap = () => input.checked;
            f.restore = (s) => { input.checked = !!s; };
        } else if (name === "select" && (el = reg("sac-select", {}))) {
            el.options = col.options.map((o) => ({ value: String(o.value), label: T.optionLabel(o) }));
            wrap.append(field(el, (v) => (v == null ? "" : String(v)), (v) => type.fromEditor(v, col)));
        } else if (name === "tags" && (el = kit("sac-chip-input", { "allow-create": col.allowCreate !== false, "aria-label": label }))) {
            el.suggestions = T.tagSuggestions(col);
            if (ro) el.setAttribute("disabled", "");
            wrap.append(caption());
            wrap.append(field(el, (v) => (Array.isArray(v) ? v.slice() : []), (v) => (Array.isArray(v) ? v.slice() : [])));
            wrap.classList.add("sdg-wide");
        } else if (name === "color" && (el = kit("sac-color-field", { label, disabled: ro }))) {
            wrap.append(field(el, T.normColor, (v) => v || ""));
        } else {
            plain(false);
        }
        if (ro) { f.get = null; f.snap = null; f.restore = null; }
        const err = document.createElement("div");
        err.className = "sdg-err";
        err.id = id + "-err";
        err.setAttribute("role", "alert");
        wrap.append(err);
        f.err = err;
        if (f.focus && f.focus.setAttribute) f.focus.setAttribute("aria-describedby", err.id);
        return f;
    };

    P._formShowErrors = function (errors) {
        const st = this._formState;
        if (!st) return;
        for (const f of st.fields) {
            if (!f.err) continue;
            const m = (errors && errors[f.col.field]) || "";
            f.err.textContent = m;
            if (f.focus && f.focus.setAttribute) {
                if (m) f.focus.setAttribute("aria-invalid", "true");
                else f.focus.removeAttribute("aria-invalid");
            }
        }
    };

    /**
     * Take the form's values into the row: validate, apply as one undo step,
     * and save it unless the grid saves in batches. forNav: an untouched new
     * record is simply dropped. Resolves false (the form stays) on errors.
     */
    P._formCommit = async function (forNav) {
        const st = this._formState;
        if (!st || st.readOnly) return true;
        const row = st.row;
        const values = {}, errors = {};
        for (const f of st.fields) {
            if (!f.get) continue;
            const res = f.get();
            if (res.error) errors[f.col.field] = res.error;
            else values[f.col.field] = res.value;
        }
        const e0 = this._entryOf(row);
        if (forNav && e0 && e0.op === "create" && !Object.keys(e0.fields).length
            && Object.values(values).every((v) => isBlank(v) || v === false)) {
            this._dropCreate(e0.id);
            return true;
        }
        const view = Object.assign({}, this._view(row), values);
        for (const f of st.fields) {
            if (!f.get || errors[f.col.field]) continue;
            const m = this._check(f.col, values[f.col.field], view);
            if (m) errors[f.col.field] = m;
        }
        this._formShowErrors(errors);
        if (Object.keys(errors).length) {
            const bad = st.fields.find((f) => errors[f.col.field] && f.focus);
            if (bad) bad.focus.focus();
            this._announce(t("data-grid.form-errors", "Please fix the marked fields."));
            return false;
        }
        const list = st.fields.filter((f) => f.get).map((f) => ({ row, col: f.col, value: values[f.col.field] }));
        this._applyChanges(list, true);
        const e = this._entryOf(row);
        if (e && this.saveMode !== "batch" && this._isDirtyEntry(e)) {
            const res = await this._save([e.id]);
            const after = this._entryOf(row);
            const failed = res.invalid.length || res.errors.some((er) => String(er.id) === String(e.id));
            if (failed && after) {
                this._formShowErrors(after.errors);
                return false;
            }
        }
        return true;
    };

    P._formNav = async function (act) {
        const st = this._formState;
        if (!st || st.busy) return;
        st.busy = true;
        try {
            if (!(await this._formCommit(true))) return;
            if (!this._formState) return;
            if (act === "new") {
                if (!this.addRow()) return;
                this._formFill(this._cur.r);
            } else {
                const here = this._indexOfId(this._idOf(st.row));
                const from = here >= 0 ? here : Math.min(st.r, this._rowCount() - 1);
                const to = Math.max(0, Math.min(this._rowCount() - 1, from + (act === "next" ? 1 : -1)));
                this._formFill(to);
            }
            const first = st.fields.find((f) => f.focus);
            if (first) first.focus.focus();
        } finally {
            st.busy = false;
        }
    };

    /** After the dialog closed. Escape or the backdrop with changes in the
     *  form ask first ("Keep editing" reopens it as it was). */
    P._formClosed = function (st, action) {
        if (this._formState !== st) return;
        const dirty = action == null && this._formDirty();
        const snap = dirty ? this._formSnapshot() : null;
        this._formState = null;
        setTimeout(() => st.dlg.remove(), 400);
        const dropIfEmpty = () => {
            // A new record that was never filled in does not stay behind.
            const e = this._entryOf(st.row);
            if (e && e.op === "create" && !Object.keys(e.fields).length) this._dropCreate(e.id);
        };
        this._stamp++;
        this._scheduleRender();
        if (!dirty || !(window.sac && sac.dialog && typeof sac.dialog.confirm === "function")) {
            if (action !== "save") dropIfEmpty();
            return;
        }
        sac.dialog.confirm({
            title: t("data-grid.discard-title", "Discard your changes?"),
            message: t("data-grid.discard-message", "This record has changes that are not saved yet."),
            buttons: [
                { action: "discard", label: t("data-grid.discard", "Discard"), kind: "destructive" },
                { action: "keep", label: t("data-grid.keep-editing", "Keep editing"), kind: "primary" },
            ],
        }).then((answer) => {
            if (answer === "keep") {
                const r = this._indexOfId(this._idOf(st.row));
                if (r >= 0) { this._openRecord(r, snap); return; }
            }
            dropIfEmpty();
        });
    };
})();
