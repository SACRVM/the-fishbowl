/**
 * sac-data-grid — sheet-mode editing (SPEC §2b, §3, §5): the kit's cell
 * editors, dirty tracking, validation, the three save modes, row add /
 * delete with sac.toast Undo, and undo / redo until saved.
 *
 * Loaded after js/sac-data-grid.js; it adds itself to SacDataGrid.prototype.
 * Without it the grid is read-only.
 *
 * Changes handed to source.save(changes):
 *   { id, row, fields, op: "update" }   fields = the changed values only
 *   { id, row, fields, op: "create", temp }  a new row; with temp: true, id
 *                                       (and row[key]) is the grid's own
 *                                       "new-<n>": assign the real id, and
 *                                       write it into row[key] to show it
 *   { id, row, fields: {}, op: "delete" }  (remove(ids) is used when the
 *                                       source has it)
 * source.create(row), when present, saves new rows instead (it gets the
 * values without the temporary id and answers with the saved row).
 *
 * Validation: `required` and `validate(value, row) → message | null` run on
 * every change; a new row checks every column before it is saved. A row with
 * a marked cell is not sent; save errors mark the cells and keep them dirty.
 */
(function () {
    const Grid = window.SacDataGrid;
    if (!Grid || Grid.prototype._startEdit) return;
    const P = Grid.prototype;

    const t = (key, fallback, vars) => {
        let s = (window.sac && sac.t) ? sac.t(key, fallback) : fallback;
        if (vars) s = String(s).replace(/\{(\w+)\}/g, (m, k) => (vars[k] !== undefined ? String(vars[k]) : m));
        return s;
    };
    const int = (n) => (window.sac && sac.regional) ? sac.regional.formatNumber(n) : String(n);
    const has = (o, k) => Object.prototype.hasOwnProperty.call(o, k);
    const isBlank = (v) => v == null || v === "" || (Array.isArray(v) && !v.length);
    const same = (a, b) => a === b || (isBlank(a) && isBlank(b))
        || (a != null && b != null && typeof a === "object" && typeof b === "object" && JSON.stringify(a) === JSON.stringify(b));
    const copy = (v) => (v != null && typeof v === "object") ? JSON.parse(JSON.stringify(v)) : v;
    const message = (err) => String((err && err.message) || err || "Error");
    const clamp = (v, lo, hi) => (v < lo ? lo : v > hi ? hi : v);
    const COMPACT = "(max-width: 768px), (max-height: 480px) and (pointer: coarse)";
    const UNDO_LIMIT = 500;
    let tempSeq = 0;

    /** What Delete / Backspace leaves in a cell. */
    function emptyOf(col) {
        switch (col.typeName) {
            case "number": return null;
            case "bool": return false;
            case "tags": return [];
            default: return "";
        }
    }

    P._edState = function () {
        if (this._edits) return;
        this._edits = new Map();        // id → { id, row, orig, fields, errors, op, … }
        this._creates = this._creates || [];
        this._savedCreates = new WeakSet();
        this._undo = [];
        this._redo = [];
        this._saving = 0;
        this._edGen = 0;                // bumped when the edits are dropped wholesale
    };

    /** A new source: the old one's unsaved edits, new rows and undo steps
     *  belong to it, not to this one. Saves still in flight are ignored. */
    P._onSourceChange = function () {
        if (!this._edits) return;
        if (this._editor) this._cancelEdit(this._editorHasFocus());
        for (const e of this._edits.values()) {
            if (e.toast) { try { e.toast.dismiss(); } catch (err) { /* gone */ } }
        }
        this._edits.clear();
        this._creates = [];
        this._savedCreates = new WeakSet();
        this._undo = [];
        this._redo = [];
        this._edGen++;
        this._stats = null;
        this._extraEl._t = null;
    };

    P._compact = function () {
        return !!(this._compactMq ? this._compactMq.matches : matchMedia(COMPACT).matches);
    };

    /* ----------------------------------------------------------- entries -- */

    P._entryOf = function (row) {
        if (!row || !this._edits || !this._edits.size) return null;
        return this._edits.get(row[this._keyField()]) || null;
    };

    P._entryFor = function (row) {
        this._edState();
        const id = row[this._keyField()];
        let e = this._edits.get(id);
        if (!e) {
            e = { id, row, orig: {}, fields: {}, errors: {}, op: "update" };
            this._edits.set(id, e);
        }
        return e;
    };

    P._isDirtyEntry = function (e) {
        return e.op === "delete" || e.op === "create" || Object.keys(e.fields).length > 0;
    };

    /** Forget an entry that holds nothing any more. */
    P._tidy = function (id) {
        const e = this._edits.get(id);
        if (e && e.op === "update" && !e.saving && !Object.keys(e.fields).length && !Object.keys(e.errors).length) {
            this._edits.delete(id);
        }
    };

    P._dirtyCount = function () {
        if (!this._edits) return 0;
        let n = 0;
        for (const e of this._edits.values()) if (this._isDirtyEntry(e)) n++;
        return n;
    };

    /* ------------------------------------------------- core render hooks -- */

    P._overlay = function (row, col) {
        if (!row) return undefined;
        const e = this._entryOf(row);
        return e && has(e.fields, col.field) ? e.fields[col.field] : row[col.field];
    };

    /** The row as it reads with its unsaved changes (computed columns, validate). */
    P._view = function (row) {
        const e = this._entryOf(row);
        if (!e || !Object.keys(e.fields).length) return row;
        if (!e.view) e.view = Object.assign({}, row, e.fields);
        return e.view;
    };

    P._cellState = function (row, col) {
        const e = this._entryOf(row);
        if (!e) return null;
        return { dirty: has(e.fields, col.field), error: e.errors[col.field] || null };
    };

    /** Any error on the row, in any column (hidden ones too). */
    P._rowHasError = function (row) {
        const e = this._entryOf(row);
        return !!(e && Object.keys(e.errors).length);
    };

    P._isDeleted = function (row) {
        const e = this._entryOf(row);
        return !!(e && e.op === "delete");
    };

    P._paintRowState = function (rowEl, row) {
        const e = this._entryOf(row);
        const created = !!(e && e.op === "create");
        rowEl.classList.toggle("deleted", !!(e && e.op === "delete"));
        rowEl.classList.toggle("created", created);
        if (created) rowEl._rh.textContent = "*";
    };

    /** The "new row" line: sheet mode, an editable source, the end in sight. */
    P._newLine = function () {
        if (this.mode !== "sheet" || !this._editable() || this._compact()) return 0;
        if (this._paging() === "pages") {
            if (this._total != null) return this._page >= Math.max(0, Math.ceil(this._total / this._pageSize()) - 1) ? 1 : 0;
            const blk = this._blocks.get(this._page);
            return blk && blk.rows && blk.rows.length < this._pageSize() ? 1 : 0;
        }
        return this._total != null ? 1 : 0;
    };

    P._isNewLine = function (r) {
        return r === this._rowCount() && !!this._newLine();
    };

    P._fillNewLine = function (rowEl) {
        rowEl.classList.remove("msg", "error", "skel", "deleted", "created");
        rowEl.classList.add("newline");
        rowEl._rh.textContent = "+";
        const first = this._cols.findIndex((c) => !c.readonly);
        rowEl._cells.forEach((cell, c) => {
            const s = c === first ? t("data-grid.new-row", "New row") : "";
            if (cell._text !== s) { cell.textContent = s; cell._text = s; }
            cell.classList.remove("dirty", "invalid", "ro");
            this._cellError(cell, "");
        });
        rowEl._rh.classList.remove("err");
    };

    P._onModeChanged = function () {
        this._scroller.dataset.mode = this.mode;
        if (this._editor && (this.mode !== "sheet" || !this._editable())) this._commitEdit(null);
        this._clampSelection();
        if (this._extraEl) this._extraEl._t = null;
    };

    P._onRelabel = function () {
        if (this._extraEl) this._extraEl._t = null;
    };

    P._onReset = function () {
        if (!this._edits) return;
        if (this._editor) this._commitEdit(null);
        // Saved rows are the source's now; unsaved ones stay at the end.
        this._creates = this._creates.filter((row) => !this._savedCreates.has(row));
    };

    P._leavePage = function () {
        if (this._editor) this._commitEdit(null);
        if (this.saveMode === "row") this._leaveRow(this._cur.r);
        return true;
    };

    /* ----------------------------------------------------------- editing -- */

    P._canEdit = function (r, c) {
        if (this.mode !== "sheet" || !this._editable() || this._compact()) return false;
        const col = this._cols[c];
        if (!col || col.readonly || !col.inline) return false;
        if (this._isNewLine(r)) return true;
        const row = this._rowAt(r);
        if (!row) return false;
        const e = this._entryOf(row);
        return !(e && e.op === "delete");
    };

    P._inEditor = function (e) {
        const ed = this._editor;
        if (!ed) return false;
        const path = e.composedPath();
        return path.includes(ed.el) || (ed.pop && path.includes(ed.pop));
    };

    P._editorHasFocus = function () {
        const ed = this._editor;
        if (!ed) return false;
        const a = this.shadowRoot.activeElement;
        return !!a && (a === ed.el || ed.el.contains(a) || (!!ed.pop && ed.pop.contains(a)));
    };

    P._makeEditor = function (col, row, value) {
        const type = col.type;
        let el = null, kind = "kit";
        if (type.customEditor) {
            kind = "custom";
            try { el = type.customEditor({ value: copy(value), row: this._view(row), field: col.field, column: col.def }); }
            catch (err) { console.error("[sac-data-grid] custom editor:", err); el = null; }
        } else if (type.longEditor) {
            el = document.createElement("textarea");
            kind = "long";
        } else if (!type.plainEditor) {
            el = type.editor(col);
        }
        if (!el) {
            el = document.createElement("input");
            el.type = "text";
            kind = kind === "long" ? "long" : "plain";
        }
        if (kind === "plain" || kind === "long") {
            el.className = "cell-input";
            el.setAttribute("aria-label", col.labelText());
            el.setAttribute("autocomplete", "off");
            el.spellcheck = kind === "long";
            el.value = (type.plainEditor || type.longEditor)
                ? (value == null ? "" : String(value))
                : col.copy(value, row);
        } else if (kind === "kit") {
            el.value = type.toEditor ? type.toEditor(value, col) : (value == null ? (col.typeName === "number" ? null : "") : value);
        } else if ("value" in el) {
            try { el.value = copy(value); } catch (err) { /* read-only value */ }
        }
        return { el, kind };
    };

    P._readEditor = function () {
        const ed = this._editor, col = ed.col, type = col.type;
        if (ed.kind === "plain" || ed.kind === "long") {
            const s = ed.el.value;
            if (type.plainEditor || type.longEditor) return { value: s };
            const v = this._parseText(col, s, ed.row);
            return v === undefined ? { error: t("data-grid.invalid", "Not a valid value") } : { value: v };
        }
        let v;
        try { v = ed.el.value; } catch (err) { v = ed.start; }
        if (type.fromEditor) v = type.fromEditor(v, col);
        // A custom editor built on a plain input hands back text: the type's
        // (or the column's) parse turns it into the value.
        if (ed.kind === "custom" && typeof v === "string" && (type.customParse || typeof col.def.parse === "function")) {
            const parsed = this._parseText(col, v, ed.row);
            if (parsed === undefined) return { error: t("data-grid.invalid", "Not a valid value") };
            v = parsed;
        }
        return { value: v === undefined ? ed.start : v };
    };

    P._parseText = function (col, text, row) {
        if (typeof col.def.parse === "function") {
            try { return col.def.parse(text, this._view(row)); } catch (err) { return undefined; }
        }
        return col.type.parse(text, col);
    };

    /**
     * Open the editor on display cell (r, c). opts.text: the key that started
     * it (replaces the value, like typing into the field); otherwise the value
     * is kept and the caret goes to the end.
     */
    P._startEdit = function (r, c, opts) {
        const o = opts || {};
        if (this._editor) this._commitEdit(null);
        if (!this._canEdit(r, c)) return false;
        let col = this._cols[c];
        if (col.typeName === "bool") return false;
        if (this._isNewLine(r)) {
            if (!this.addRow(null, { col: c })) return false;
            r = this._cur.r;
            col = this._cols[c];
        } else if (this._cur.r !== r || this._cur.c !== c) {
            this._setCursor(r, c, false);
        }
        const row = this._rowAt(r);
        if (!row) return false;
        this._reveal({ r, c });
        this._renderNow();
        const cell = this._cellEl(r, c);
        if (!cell) return false;
        const value = this._get(row, col);
        const { el, kind } = this._makeEditor(col, row, value);
        const ed = { el, kind, r, c, row, col, id: this._idOf(row), start: copy(value), cell, pop: null };
        this._editor = ed;
        cell.classList.add("editing");
        cell.textContent = "";
        cell._text = null;
        if (kind === "kit") {
            cell.appendChild(el);
            this._liftEditor(ed);
        } else if (kind === "long") {
            const pop = document.createElement("div");
            pop.className = "long-pop";
            pop.setAttribute("popover", "manual");
            pop.appendChild(el);
            cell.appendChild(pop);
            const rect = cell.getBoundingClientRect();
            const w = Math.min(Math.max(rect.width, 320), innerWidth - 16), h = 144;
            pop.style.width = w + "px";
            pop.style.height = h + "px";
            pop.style.left = clamp(rect.left, 8, Math.max(8, innerWidth - w - 8)) + "px";
            pop.style.top = (rect.top + h > innerHeight - 8 ? Math.max(8, rect.bottom - h) : rect.top) + "px";
            try { pop.showPopover(); } catch (err) { /* not in the document */ }
            ed.pop = pop;
        } else {
            cell.appendChild(el);
        }
        el.addEventListener("sac:commit", (e) => {
            e.stopPropagation();
            if (this._editor && this._editor.el === el) this._commitEdit(e.detail && e.detail.shiftKey ? "up" : "down", true);
        });
        el.addEventListener("sac:cancel", (e) => {
            e.stopPropagation();
            if (this._editor && this._editor.el === el) this._cancelEdit(true);
        });
        if (o.text != null) {
            el.focus({ select: true, preventScroll: true });
            if (kind === "plain" || kind === "long") el.select();
            // Type the key into the field, so it reacts as to real typing.
            let typed = false;
            try { typed = document.execCommand("insertText", false, o.text); } catch (err) { typed = false; }
            if (!typed && (kind === "plain" || kind === "long")) el.value = o.text;
        } else {
            el.focus({ select: false, preventScroll: true });
            if ((kind === "plain" || kind === "long") && typeof el.setSelectionRange === "function") {
                const n = el.value.length;
                el.setSelectionRange(n, n);
            }
        }
        this._paintSelection();
        return true;
    };

    /** How much wider an editor's content wants to be than it is: the most
     *  any input or clipping box inside it (kit shadow roots included) is
     *  cut short by. */
    function shortfall(el) {
        let d = 0;
        const walk = (root) => {
            for (const n of root.querySelectorAll("*")) {
                if (n.shadowRoot) walk(n.shadowRoot);
                if (n.tagName === "INPUT" || /auto|scroll|hidden/.test(getComputedStyle(n).overflowX)) {
                    d = Math.max(d, n.scrollWidth - n.clientWidth);
                }
            }
        };
        if (el.shadowRoot) walk(el.shadowRoot);
        walk(el);
        return d;
    }

    /**
     * A kit editor that does not fit its cell (a date and its calendar
     * button in a column sized for the date) is lifted: shown over the cell,
     * as wide as it needs, growing over the neighbours in the reading
     * direction (leftwards in a right-aligned column). The column keeps its
     * width; the editor follows the cell while the grid scrolls.
     */
    P._liftEditor = function (ed) {
        const { el, col, cell } = ed;
        const cellW = cell.getBoundingClientRect().width;
        let need = shortfall(el);
        const sample = col.type.editSample ? col.type.editSample(col) : null;
        if (sample != null) {
            // Measured with a full value too: typing into an empty cell needs the room.
            let cur;
            try { cur = el.value; } catch (err) { cur = undefined; }
            try {
                el.value = col.type.toEditor ? col.type.toEditor(sample, col) : sample;
                need = Math.max(need, shortfall(el));
            } catch (err) { /* keeps the measure of the value itself */ }
            try { el.value = cur; } catch (err) { /* read-only value */ }
        }
        if (need <= 0.5) return;
        const max = Math.max(cellW, Math.min(480, innerWidth - 16));
        const pop = document.createElement("div");
        pop.className = "lift-pop";
        pop.setAttribute("popover", "manual");
        cell.appendChild(pop);
        pop.appendChild(el);
        try { pop.showPopover(); } catch (err) { /* not in the document */ }
        ed.pop = pop;
        ed.liftW = Math.min(max, Math.ceil(cellW + need + 2));
        this._placeLift();
        // A compound editor shares the room anew once wider: measure again.
        for (let i = 0; i < 2 && ed.liftW < max; i++) {
            const more = shortfall(el);
            if (more <= 0.5) break;
            ed.liftW = Math.min(max, Math.ceil(ed.liftW + more + 2));
            this._placeLift();
        }
        ed.onMove = () => this._placeLift();
        this._scroller.addEventListener("scroll", ed.onMove);
        window.addEventListener("scroll", ed.onMove, true);
        window.addEventListener("resize", ed.onMove);
    };

    /** Put the lifted editor over its cell; clip it to the scrolling area
     *  (under the header, the footer and the frozen columns it is hidden). */
    P._placeLift = function () {
        const ed = this._editor;
        if (!ed || !ed.liftW || !ed.pop) return;
        const pop = ed.pop, r = ed.cell.getBoundingClientRect(), w = ed.liftW;
        const sr = this._scroller.getBoundingClientRect();
        const frozen = ed.c < this._nf;
        const vis = {
            left: sr.left + (frozen ? this._rhW : this._frozenW),
            top: sr.top + this._headH,
            right: sr.left + this._scroller.clientWidth,
            bottom: sr.top + this._scroller.clientHeight - this._footH,
        };
        let left = ed.col.align === "right" ? r.right - w : r.left;
        if (left + w > innerWidth - 8) left = r.right - w;          // no room on the right: grow left
        if (left < 8) left = r.left;                                 // nor on the left: grow right
        pop.style.width = w + "px";
        pop.style.height = r.height + "px";
        pop.style.left = left + "px";
        pop.style.top = r.top + "px";
        const cut = [
            Math.max(0, vis.top - r.top),
            Math.max(0, left + w - Math.max(vis.right, r.right)),
            Math.max(0, r.bottom - vis.bottom),
            Math.max(0, vis.left - left),
        ];
        pop.style.clipPath = cut.some((x) => x > 0) ? `inset(${cut.map((x) => x + "px").join(" ")})` : "";
        // Out of view: invisible, but not visibility: hidden, which would
        // take the focus from the field (and commit the edit).
        const gone = cut[0] + cut[2] >= r.height || cut[1] + cut[3] >= w;
        pop.style.opacity = gone ? "0" : "";
        pop.style.pointerEvents = gone ? "none" : "";
    };

    P._closeEditor = function () {
        const ed = this._editor;
        if (!ed) return;
        this._editor = null;
        if (ed.onMove) {
            this._scroller.removeEventListener("scroll", ed.onMove);
            window.removeEventListener("scroll", ed.onMove, true);
            window.removeEventListener("resize", ed.onMove);
        }
        if (ed.pop) { try { ed.pop.hidePopover(); } catch (err) { /* closed */ } ed.pop.remove(); }
        if (ed.el.isConnected) ed.el.remove();
        ed.cell.classList.remove("editing");
        ed.cell._text = null;
        this._stamp++;
        this._scheduleRender();
    };

    /** Commit the open editor; move: "down" | "up" | "right" | "left" | null. */
    P._commitEdit = function (move, refocus) {
        const ed = this._editor;
        if (!ed) return true;
        const back = refocus || this._editorHasFocus();
        // Blurring lets a kit field commit the text typed into it.
        if (back) this._scroller.focus({ preventScroll: true });
        if (this._editor !== ed) return true;
        const res = this._readEditor();
        this._closeEditor();
        const { r, c, row, col } = ed;
        if (res.error) {
            const e = this._entryFor(row);
            e.errors[col.field] = res.error;
            this._announce(res.error);
        } else if (!same(res.value, ed.start) || ((this._entryOf(row) || {}).errors || {})[col.field]) {
            this._applyChanges([{ row, col, value: res.value }], true);
        }
        this._stamp++;
        this._scheduleRender();
        if (move) this._moveAfterEdit(move, r, c);
        if (this.saveMode === "cell") this._save([ed.id]);
        return true;
    };

    P._cancelEdit = function (refocus) {
        const ed = this._editor;
        if (!ed) return;
        if (refocus || this._editorHasFocus()) this._scroller.focus({ preventScroll: true });
        this._closeEditor();
    };

    P._moveAfterEdit = function (move, r, c) {
        const rows = this._navRows(), cols = this._cols.length;
        if (move === "down") this._setCursor(Math.min(r + 1, rows - 1), c, false);
        else if (move === "up") this._setCursor(Math.max(r - 1, 0), c, false);
        else {
            let rr = r, cc = c + (move === "right" ? 1 : -1);
            if (cc >= cols) { cc = 0; rr++; } else if (cc < 0) { cc = cols - 1; rr--; }
            if (rr >= 0 && rr < rows) this._setCursor(rr, cc, false);
        }
    };

    P._onEditKeydown = function (e) {
        const ed = this._editor;
        if (!ed || e.defaultPrevented) return;
        const k = e.key;
        if (k === "Tab") {
            e.preventDefault();
            e.stopPropagation();
            this._commitEdit(e.shiftKey ? "left" : "right", true);
            return;
        }
        if ((e.ctrlKey || e.metaKey) && !e.altKey && (k === "s" || k === "S")) {
            e.preventDefault();
            e.stopPropagation();
            this._commitEdit(null, true);
            this.save();
            return;
        }
        if (ed.kind === "kit") return;          // Enter / Escape arrive as sac:commit / sac:cancel
        if (k === "Escape") {
            e.preventDefault();
            e.stopPropagation();
            this._cancelEdit(true);
        } else if (k === "Enter") {
            if (ed.kind === "long" && !(e.ctrlKey || e.metaKey)) return;     // a new line
            e.preventDefault();
            e.stopPropagation();
            this._commitEdit(e.shiftKey ? "up" : "down", true);
        }
    };

    P._onFocusOut = function (e) {
        const to = e.relatedTarget;
        if (to && (to === this || this.contains(to))) return;
        setTimeout(() => {
            if (this.matches(":focus-within")) return;
            if (this._editor) this._commitEdit(null, false);
            if (this.saveMode === "row") this._leaveRow(this._cur.r);
        }, 0);
    };

    /* ----------------------------------------------------------- changes -- */

    P._check = function (col, v, row) {
        if (col.required && col.type.empty(v)) return t("data-grid.required", "Required");
        if (col.validate) {
            try {
                const m = col.validate(v, row);
                if (m) return String(m);
            } catch (err) { return message(err); }
        }
        return null;
    };

    P._validateField = function (e, col) {
        const f = col.field;
        const v = has(e.fields, f) ? e.fields[f] : e.row[f];
        const m = this._check(col, v, this._view(e.row));
        if (m) e.errors[f] = m;
        else delete e.errors[f];
    };

    /** Before a save: a new row checks every column, a changed one its changes
     *  and every field still marked (a rejected text, an earlier save error). */
    P._validateEntry = function (e) {
        if (e.op === "delete") return true;
        for (const col of this._all) {
            if (col.readonly) continue;
            if (e.op === "create" || has(e.fields, col.field) || has(e.errors, col.field)) this._validateField(e, col);
        }
        return !Object.keys(e.errors).length;
    };

    /**
     * Apply cell values ([{ row, col, value }]); record: push one undo step.
     * Fires sac:change per cell. Returns the ids of the touched rows.
     */
    P._applyChanges = function (list, record) {
        this._edState();
        const undo = [], events = [], ids = [];
        for (const { row, col, value } of list) {
            if (!row) continue;
            const e = this._entryFor(row);
            const f = col.field;
            const old = has(e.fields, f) ? e.fields[f] : row[f];
            if (same(old, value)) {
                // Confirming the value it holds clears an error a rejected
                // text or paste left there.
                if (has(e.errors, f)) { this._validateField(e, col); ids.push(e.id); this._stamp++; }
                continue;
            }
            if (!has(e.orig, f)) e.orig[f] = copy(row[f]);
            if (e.op !== "create" && same(value, e.orig[f])) delete e.fields[f];
            else e.fields[f] = copy(value);
            e.view = null;
            this._validateField(e, col);
            undo.push({ id: e.id, row, field: f, old: copy(old), value: copy(value) });
            events.push({ id: e.id, field: f, value: copy(value), old: copy(old) });
            if (!ids.includes(e.id)) ids.push(e.id);
        }
        for (const id of ids) this._tidy(id);
        this._lastChanges = undo;          // for callers that build a compound undo step
        if (!undo.length) return ids;
        if (record) this._pushUndo({ type: "cells", changes: undo });
        this._stamp++;
        this._stats = null;
        this._scheduleRender();
        for (const detail of events) this.dispatchEvent(new CustomEvent("sac:change", { detail, bubbles: true, composed: true }));
        return ids;
    };

    /** After a change across rows: cell mode saves them all; row mode saves
     *  the rows the cursor is not in (it is "in" no other row to leave). */
    P._afterBulk = function (ids) {
        if (!ids || !ids.length) return;
        if (this.saveMode === "cell") this._save(ids);
        else if (this.saveMode === "row") {
            const here = this._idOf(this._rowAt(this._cur.r));
            const others = ids.filter((id) => id !== here);
            if (others.length) this._save(others);
        }
    };

    /** Delete / Backspace: empty the editable cells of the range. */
    P._clearRange = function () {
        if (this.mode !== "sheet" || !this._editable() || this._compact()) return;
        const s = this._rect();
        const list = [];
        for (let r = s.r1; r <= s.r2; r++) {
            const row = this._rowAt(r);
            if (!row) continue;
            for (let c = s.c1; c <= s.c2; c++) {
                if (!this._canEdit(r, c)) continue;
                const col = this._cols[c];
                list.push({ row, col, value: emptyOf(col) });
            }
        }
        this._afterBulk(this._applyChanges(list, true));
    };

    /** Cut = copy (done by the core) + clear. */
    P._cut = function () { this._clearRange(); };

    /**
     * Paste TSV (Excel, Google Sheets, SharePoint). One value fills the whole
     * range; a block tiles a range that is a multiple of it; otherwise it
     * lands at the range's top-left and extends from there — past the last
     * row it adds rows (sheet mode). Values are parsed per column type;
     * cells that fail parsing or validation are marked and not applied.
     * One undo step.
     */
    P._paste = function (e) {
        if (this.mode !== "sheet" || !this._editable() || this._compact()) return;
        const text = e.clipboardData ? e.clipboardData.getData("text/plain") : "";
        e.preventDefault();
        const data = Grid.parseTSV(text);
        if (!data.length) return;
        const R = data.length, C = data.reduce((m, line) => Math.max(m, line.length), 1);
        const s = this._rect();
        const SR = s.r2 - s.r1 + 1, SC = s.c2 - s.c1 + 1;
        let rows = R, cols = C, tile = false;
        if ((R === 1 && C === 1) || (SR % R === 0 && SC % C === 0 && (SR > R || SC > C))) {
            rows = SR; cols = SC; tile = true;
        }
        const r0 = s.r1, c0 = s.c1;
        const c1 = Math.min(this._cols.length - 1, c0 + cols - 1);
        const steps = [];
        const have = this._rowCount();
        if (r0 + rows > have) {
            if (this._newLine()) {
                for (let i = have; i < r0 + rows; i++) steps.push(this._newRowStep(this._createRow({})));
            } else {
                rows = Math.max(0, have - r0);
            }
        }
        const list = [], bad = [];
        for (let i = 0; i < rows; i++) {
            const row = this._rowAt(r0 + i);
            if (!row || this._isDeleted(row)) continue;
            const line = data[i % R];
            for (let c = c0; c <= c1; c++) {
                const col = this._cols[c];
                if (col.readonly || !col.inline) continue;
                const j = c - c0;
                if (!tile && j >= line.length) continue;
                const raw = line[j % line.length] != null ? line[j % line.length] : "";
                const v = this._parseText(col, raw, row);
                if (v === undefined) {
                    bad.push({ row, col, message: t("data-grid.paste-invalid", "Not a valid value: {text}", { text: raw }) });
                    continue;
                }
                const m = this._check(col, v, Object.assign({}, this._view(row), { [col.field]: v }));
                if (m) { bad.push({ row, col, message: m }); continue; }
                list.push({ row, col, value: v });
            }
        }
        const ids = this._applyChanges(list, false);
        if (this._lastChanges.length) steps.push({ type: "cells", changes: this._lastChanges });
        if (steps.length) this._pushUndo(steps.length === 1 ? steps[0] : { type: "group", steps });
        for (const b of bad) this._entryFor(b.row).errors[b.col.field] = b.message;
        if (bad.length) {
            this._stamp++;
            this._announce(t("data-grid.paste-rejected", "{n} cells were not pasted.", { n: int(bad.length) }));
        }
        this._select({ r: r0, c: c0 }, { r: Math.max(r0, r0 + rows - 1), c: c1 }, true);
        this._scheduleRender();
        this._afterBulk(ids);
    };

    /** A new row without moving the cursor and without an undo step of its own. */
    P._createRow = function (values) {
        this._edState();
        const key = this._keyField();
        const vals = values && typeof values === "object" ? values : {};
        const row = {};
        let temp = false;
        if (vals[key] == null) { row[key] = `new-${++tempSeq}`; temp = true; }
        else row[key] = vals[key];
        const e = { id: row[key], row, orig: {}, fields: {}, errors: {}, op: "create", temp };
        for (const [f, v] of Object.entries(vals)) if (f !== key) e.fields[f] = copy(v);
        this._creates.push(row);
        this._edits.set(e.id, e);
        this._stamp++;
        this._stats = null;
        this._extraEl._t = null;
        return row;
    };

    P._newRowStep = function (row) {
        const e = this._entryOf(row);
        return { type: "add", id: e.id, row, fields: copy(e.fields), temp: e.temp };
    };

    /** Space, Enter or F2 on a bool cell, or a click on its box: flip it —
     *  across the range, to the opposite of the active cell. */
    P._toggleBool = function () {
        const c = this._cur.c, col = this._cols[c];
        if (!col || col.typeName !== "bool" || !this._canEdit(this._cur.r, c)) return false;
        const row = this._rowAt(this._cur.r);
        if (!row) return false;
        const next = !this._get(row, col);
        const s = this._rect();
        const list = [];
        for (let r = s.r1; r <= s.r2; r++) {
            const rw = this._rowAt(r);
            if (!rw) continue;
            for (let cc = s.c1; cc <= s.c2; cc++) {
                if (this._cols[cc].typeName === "bool" && this._canEdit(r, cc)) list.push({ row: rw, col: this._cols[cc], value: next });
            }
        }
        this._afterBulk(this._applyChanges(list, true));
        return true;
    };

    P._onSpace = function () { return this._toggleBool(); };

    P._onCellPointerDown = function (e, r, c) {
        this._boolTap = null;
        if (e.shiftKey || e.button !== 0) return false;
        if (this._cols[c].typeName !== "bool" || !this._canEdit(r, c) || !this._rowAt(r)) return false;
        // On touch, anywhere in the cell (the box is no 44px target), but only
        // once the gesture turns out a tap: a scroll that starts here sends no
        // click, and must not flip the value.
        if (e.pointerType === "touch") { this._boolTap = { r, c }; return false; }
        if (!(e.target.closest && e.target.closest(".bool"))) return false;
        this._setCursor(r, c, false);
        this._toggleBool();
        return true;
    };

    P._onCellClick = function (r, c) {
        const tap = this._boolTap;
        this._boolTap = null;
        if (tap && tap.r === r && tap.c === c && this._canEdit(r, c)) this._toggleBool();
    };

    P._onCellDblClick = function (r, c) {
        if (c == null) { if (this._openRecord) this._openRecord(r); return; }
        const col = this._cols[c];
        if (this.mode === "sheet" && !this._compact() && col && col.inline) {
            if (col.typeName !== "bool") this._startEdit(r, c, {});
        } else if (this._openRecord) {
            this._openRecord(r);
        }
    };

    P._onOtherKey = function (e) {
        const k = e.key;
        const r = this._cur.r, c = this._cur.c;
        const col = this._cols[c];
        if ((e.ctrlKey || e.metaKey) && !e.altKey) {
            switch (k.toLowerCase()) {
                case "z": this._undoStep(e.shiftKey); return true;
                case "y": this._undoStep(true); return true;
                case "s": this.save(); return true;
                default: return false;               // Ctrl+C / X / V: the clipboard events
            }
        }
        const sheet = this.mode === "sheet" && !this._compact();
        if (k === "Enter") {
            if (e.shiftKey || !sheet || (col && !col.inline)) {
                if (this._openRecord) this._openRecord(r);
                return true;
            }
            if (col && col.typeName === "bool") this._toggleBool();
            else this._startEdit(r, c, {});
            return true;
        }
        if (k === "F2") {
            if (!sheet) return true;
            if (col && col.typeName === "bool") this._toggleBool();
            else if (col && !col.inline && this._openRecord) this._openRecord(r);
            else this._startEdit(r, c, {});
            return true;
        }
        if (k === "Delete" || k === "Backspace") {
            this._clearRange();
            return true;
        }
        const printable = k.length === 1 && !e.metaKey && !(e.ctrlKey && !e.altKey);
        if (printable && sheet && col && col.typeName !== "bool") return this._startEdit(r, c, { text: k });
        return false;
    };

    /* ----------------------------------------------------------- saving -- */

    P.save = function () { return this._save(null); };

    /** Leaving a row (row save mode): save it, or drop it when it is a new
     *  row nobody typed into. */
    P._leaveRow = function (r) {
        if (!this._edits || !this._edits.size) return;
        const row = this._rowAt(r);
        const e = this._entryOf(row);
        if (!e) return;
        if (e.op === "create" && !Object.keys(e.fields).length) {
            // After the cursor has moved (display indices shift when it goes).
            const id = e.id;
            queueMicrotask(() => {
                const cur = this._edits.get(id);
                if (!cur || cur.op !== "create" || Object.keys(cur.fields).length || this._editor) return;
                if (this._rowAt(this._cur.r) === cur.row) return;          // came back to it
                this._dropCreate(id);
            });
            return;
        }
        if (this.saveMode === "row" && this._isDirtyEntry(e) && e.op !== "delete") this._save([e.id]);
    };

    /**
     * Save rows (ids) or every dirty row. Resolves with { saved, errors }
     * (plus `invalid`: ids held back by validation). Fires sac:save.
     */
    P._save = async function (ids) {
        this._edState();
        const src = this._source;
        const result = { saved: [], errors: [], invalid: [] };
        if (!src || typeof src.save !== "function") return result;
        if (this._editor) this._commitEdit(null);
        const gen = this._edGen;
        let entries = ids ? ids.map((id) => this._edits.get(id)).filter(Boolean) : [...this._edits.values()];
        // A row already on its way: wait for that save, then send what
        // changed meanwhile (else it would stay dirty and unsent).
        const busy = [...new Set(entries.filter((e) => e.saving).map((e) => e.saving))];
        if (busy.length) {
            await Promise.all(busy);
            if (gen !== this._edGen) return result;
            return this._save(ids);
        }
        entries = entries.filter((e) => !e.pendingDelete && this._isDirtyEntry(e));
        if (!entries.length) return result;
        const ready = [];
        for (const e of entries) {
            if (this._validateEntry(e)) ready.push(e);
            else result.invalid.push(e.id);
        }
        if (result.invalid.length) {
            this._stamp++;
            this._scheduleRender();
            this._announce(t("data-grid.fix-errors", "Fix the marked cells: {n} rows were not saved.", { n: int(result.invalid.length) }));
        }
        if (!ready.length) { this._renderStatus(); return result; }

        const key = this._keyField();
        const sent = new Map();
        let settle;
        const settled = new Promise((resolve) => { settle = resolve; });
        for (const e of ready) {
            e.saving = settled;                 // truthy while in flight; a later save awaits it
            sent.set(e, { fields: copy(e.fields), op: e.op, id: e.id });
        }
        this._saving++;
        this._extraEl._t = null;
        this._renderStatus();
        const changes = [];
        const deletes = ready.filter((e) => e.op === "delete");
        try {
            for (const e of ready.filter((x) => x.op === "create")) {
                if (typeof src.create === "function") {
                    try {
                        const data = Object.assign({}, e.row, e.fields);
                        if (e.temp) delete data[key];
                        const saved = await src.create(data);
                        Object.assign(e.row, saved && typeof saved === "object" ? saved : e.fields);
                        result.saved.push(e.id);
                    } catch (err) { result.errors.push({ id: e.id, message: message(err) }); }
                } else {
                    changes.push({ id: e.id, row: e.row, fields: copy(e.fields), op: "create", temp: !!e.temp });
                }
            }
            if (deletes.length && typeof src.remove === "function") {
                try {
                    const res = (await src.remove(deletes.map((e) => e.id))) || {};
                    for (const id of Array.isArray(res.removed) ? res.removed : deletes.map((e) => e.id)) result.saved.push(id);
                    for (const er of res.errors || []) result.errors.push(er);
                } catch (err) {
                    for (const e of deletes) result.errors.push({ id: e.id, message: message(err) });
                }
            } else {
                for (const e of deletes) changes.push({ id: e.id, row: e.row, fields: {}, op: "delete" });
            }
            for (const e of ready.filter((x) => x.op === "update")) {
                changes.push({ id: e.id, row: e.row, fields: copy(e.fields), op: "update" });
            }
            if (changes.length) {
                try {
                    const res = (await src.save(changes)) || {};
                    for (const id of res.saved || []) result.saved.push(id);
                    for (const er of res.errors || []) result.errors.push(er);
                } catch (err) {
                    for (const ch of changes) result.errors.push({ id: ch.id, message: message(err) });
                }
            }
        } finally {
            this._saving--;
        }
        if (gen !== this._edGen) {          // the source changed meanwhile: not ours any more
            settle();
            this._renderStatus();
            return result;
        }

        const saved = new Set(result.saved.map(String));
        const errors = new Map();
        for (const er of result.errors) {
            const k = String(er && er.id);
            if (!errors.has(k)) errors.set(k, []);
            errors.get(k).push(er);
        }
        let reload = false;
        for (const e of ready) {
            e.saving = null;
            const snap = sent.get(e);
            const errs = errors.get(String(snap.id));
            if (errs) {
                const fields = Object.keys(snap.fields);
                for (const er of errs) {
                    const text = String(er.message || t("data-grid.save-error", "Couldn't save"));
                    const targets = er.field ? [er.field] : (fields.length ? fields : [this._cols.find((c) => !c.readonly) || this._cols[0]].filter(Boolean).map((c) => c.field));
                    for (const f of targets) e.errors[f] = text;
                }
                if (e.op === "delete") { e.op = e.prevOp || "update"; delete e.prevOp; }
                continue;
            }
            if (!saved.has(String(snap.id))) continue;          // the source said nothing: still dirty
            this._dropUndo(snap.id);
            if (snap.op === "delete") {
                this._edits.delete(e.id);
                reload = true;
                continue;
            }
            this._foldAggregates(e, snap);
            if (snap.op === "update") Object.assign(e.row, snap.fields);
            for (const [f, v] of Object.entries(snap.fields)) {
                if (has(e.fields, f) && same(e.fields[f], v)) { delete e.fields[f]; delete e.orig[f]; }
                delete e.errors[f];
            }
            e.view = null;
            if (snap.op === "create") {
                this._savedCreates.add(e.row);
                e.op = "update";
                e.temp = false;
                if (e.row[key] !== e.id && e.row[key] != null) {       // the real id arrived
                    this._edits.delete(e.id);
                    e.id = e.row[key];
                    this._edits.set(e.id, e);
                }
            }
            this._tidy(e.id);
        }
        for (const e of ready) if (!e.saving && e.op === "update") this._tidy(e.id);
        settle();
        this._stamp++;
        this._stats = null;
        this._extraEl._t = null;
        this._scheduleRender();
        if (result.errors.length) {
            this._announce(t("data-grid.save-failed", "{n} rows couldn't be saved.", { n: int(errors.size) }));
        }
        this.dispatchEvent(new CustomEvent("sac:save", { detail: result, bubbles: true, composed: true }));
        if (reload) this.reload();
        else if (this._aggNeedsRefresh) this._refreshAggregates();
        return result;
    };

    /** Unsaved-change arithmetic for the footer: sum and count follow the edits. */
    P._adjustAgg = function (col, fn, v) {
        if (!this._edits || !this._edits.size || v == null || (fn !== "sum" && fn !== "count")) return v;
        const f = col.field;
        const num = (x) => (typeof x === "number" && Number.isFinite(x) ? x : 0);
        let out = v;
        for (const e of this._edits.values()) {
            const inSource = !(e.op === "create" || (e.prevOp === "create"));
            const had = inSource ? (has(e.orig, f) ? e.orig[f] : e.row[f]) : undefined;
            const now = e.op === "delete" ? undefined : (has(e.fields, f) ? e.fields[f] : e.row[f]);
            if (!inSource && e.op === "delete") continue;
            if (fn === "sum") out += num(now) - num(had);
            else out += (isBlank(now) ? 0 : 1) - (isBlank(had) ? 0 : 1);
        }
        return out;
    };

    /** A saved change becomes part of the source-provided totals. */
    P._foldAggregates = function (e, snap) {
        if (!this._aggs) return;
        for (const col of this._all) {
            if (!col.aggregate || !has(snap.fields, col.field) || !has(this._aggs, col.field)) continue;
            const f = col.field;
            if (col.aggregate !== "sum" && col.aggregate !== "count") { this._aggNeedsRefresh = true; continue; }
            const had = snap.op === "create" ? undefined : (has(e.orig, f) ? e.orig[f] : e.row[f]);
            const now = snap.fields[f];
            const num = (x) => (typeof x === "number" && Number.isFinite(x) ? x : 0);
            const cur = this._aggs[f];
            if (typeof cur !== "number") continue;
            this._aggs[f] = col.aggregate === "sum"
                ? cur + num(now) - num(had)
                : cur + (isBlank(now) ? 0 : 1) - (isBlank(had) ? 0 : 1);
        }
    };

    /** Ask the source for fresh totals (after saves they cannot be derived from). */
    P._refreshAggregates = function () {
        this._aggNeedsRefresh = false;
        const aggregate = this._all.filter((c) => c.aggregate).map((c) => ({ field: c.field, fn: c.aggregate }));
        if (!aggregate.length || !this._aggs || !this._source) return;
        const q = this._query;
        Promise.resolve(this._source.load({ offset: 0, limit: 1, sort: [], filter: JSON.parse(JSON.stringify(this._filter)), aggregate }))
            .then((res) => {
                if (q !== this._query || !res || !res.aggregates) return;
                this._aggs = res.aggregates;
                this._stamp++;
                this._scheduleRender();
            })
            .catch(() => { /* keep the old totals */ });
    };

    P.revert = function () {
        this._edState();
        if (this._editor) this._cancelEdit(this._editorHasFocus());
        for (const e of [...this._edits.values()]) {
            if (e.saving) continue;
            if (e.toast) { try { e.toast.dismiss(); } catch (err) { /* gone */ } }
            this._edits.delete(e.id);
        }
        this._creates = this._creates.filter((row) => this._savedCreates.has(row));
        this._undo = [];
        this._redo = [];
        this._stamp++;
        this._stats = null;
        this._extraEl._t = null;
        this._clampSelection();
        this._scheduleRender();
    };

    /* -------------------------------------------------------- add / delete -- */

    /** Append a new, unsaved row (optionally with values); the cursor moves to it. */
    P.addRow = function (values, opts) {
        if (!this._built || !this._editable() || this.mode === "read") return null;
        this._edState();
        if (this._editor) this._commitEdit(null);
        const row = this._createRow(values);
        this._pushUndo(this._newRowStep(row));
        const c = opts && opts.col != null ? opts.col : Math.max(0, this._cols.findIndex((x) => !x.readonly));
        this._setCursor(this._rowCount() - 1, c, false);
        this._scheduleRender();
        return row;
    };

    P._dropCreate = function (id) {
        const e = this._edits.get(id);
        const row = e ? e.row : null;
        const i = row ? this._creates.indexOf(row) : -1;
        if (i >= 0) {
            // Rows below it move up by one: so does a selection down there.
            const at = this._dataCount() + i;
            for (const p of [this._cur, this._anchor, this._end]) if (p.r > at) p.r--;
            this._creates.splice(i, 1);
        }
        this._edits.delete(id);
        this._stamp++;
        this._stats = null;
        this._extraEl._t = null;
        this._clampSelection();
        this._scheduleRender();
    };

    /** Delete rows (display indices): sac:request-delete first; unsaved new
     *  rows just go, the others are marked and removed at the source — at
     *  once, or after the Undo toast (row / cell save modes), or on save(). */
    P._deleteRows = function (rs) {
        this._edState();
        if (!this._editable() || this.mode === "read") return;
        if (this._editor) this._commitEdit(null);
        const rows = [];
        for (const r of rs) {
            const row = this._rowAt(r);
            if (row && !this._isDeleted(row)) rows.push(row);
        }
        if (!rows.length) return;
        const ids = rows.map((row) => this._idOf(row));
        const ev = new CustomEvent("sac:request-delete", { detail: { ids }, bubbles: true, composed: true, cancelable: true });
        if (!this.dispatchEvent(ev)) return;
        const items = rows.map((row) => {
            const e = this._entryOf(row);
            if (e && e.op === "create") return { kind: "create", row, id: e.id, entry: e, index: this._creates.indexOf(row) };
            return { kind: "delete", row, id: this._idOf(row) };
        });
        const step = { type: "delete", items };
        this._applyDelete(step);
        this._pushUndo(step);
        this._select({ ...this._cur }, { ...this._cur }, false);
    };

    P._applyDelete = function (step) {
        const marked = [];
        for (const it of step.items) {
            if (it.kind === "create") {
                const i = this._creates.indexOf(it.row);
                if (i >= 0) this._creates.splice(i, 1);
                this._edits.delete(it.id);
            } else {
                const e = this._entryFor(it.row);
                if (e.op !== "delete") { e.prevOp = e.op; e.op = "delete"; }
                marked.push(e);
            }
        }
        this._stamp++;
        this._stats = null;
        this._extraEl._t = null;
        this._clampSelection();
        this._scheduleRender();
        if (this.saveMode === "batch") return;
        const n = step.items.length;
        const commit = () => {
            step.toast = null;
            const ids = [];
            for (const e of marked) {
                e.pendingDelete = false;
                e.toast = null;
                if (this._edits.get(e.id) === e && e.op === "delete") ids.push(e.id);
            }
            if (ids.length) this._save(ids);
        };
        if (window.sac && typeof sac.toast === "function") {
            for (const e of marked) e.pendingDelete = true;
            const toast = sac.toast(
                n === 1 ? t("data-grid.deleted-one", "1 row deleted") : t("data-grid.deleted", "{n} rows deleted", { n: int(n) }),
                { action: { label: t("data-grid.undo", "Undo"), labelKey: "data-grid.undo", onClick: () => this._undoStep(false, step) } },
            );
            step.toast = toast;
            for (const e of marked) e.toast = toast;
            toast.closed.then((how) => { if (how !== "action" && step.toast === toast) commit(); });
        } else {
            commit();
        }
    };

    P._restoreDelete = function (step) {
        if (step.toast) {
            const tst = step.toast;
            step.toast = null;
            try { tst.dismiss(); } catch (err) { /* gone */ }
        }
        const creates = step.items.filter((it) => it.kind === "create").sort((a, b) => a.index - b.index);
        for (const it of creates) {
            this._creates.splice(clamp(it.index, 0, this._creates.length), 0, it.row);
            this._edits.set(it.id, it.entry);
        }
        for (const it of step.items) {
            if (it.kind !== "delete") continue;
            const e = this._entryOf(it.row);
            if (!e || e.op !== "delete") continue;
            e.pendingDelete = false;
            e.toast = null;
            e.op = e.prevOp || "update";
            delete e.prevOp;
            this._tidy(e.id);
        }
        this._stamp++;
        this._stats = null;
        this._extraEl._t = null;
        this._scheduleRender();
    };

    /* -------------------------------------------------------------- undo -- */

    P._pushUndo = function (step) {
        this._undo.push(step);
        if (this._undo.length > UNDO_LIMIT) this._undo.shift();
        this._redo = [];
    };

    /** Saved changes can no longer be undone. */
    P._dropUndo = function (id) {
        const touches = (s) => (s.type === "group" ? s.steps.some(touches)
            : s.type === "cells" ? s.changes.some((ch) => ch.id === id)
            : s.type === "add" ? s.id === id : s.items.some((it) => it.id === id));
        this._undo = this._undo.filter((s) => !touches(s));
        this._redo = this._redo.filter((s) => !touches(s));
    };

    /** Undo (or redo) the last step — or a given step (the toast's Undo). */
    P._undoStep = function (redo, only) {
        this._edState();
        if (this._editor) this._commitEdit(null);
        const from = redo ? this._redo : this._undo;
        const to = redo ? this._undo : this._redo;
        let step;
        if (only) {
            const i = from.indexOf(only);
            if (i < 0) return false;
            step = from.splice(i, 1)[0];
        } else {
            step = from.pop();
        }
        if (!step) return false;
        this._runStep(step, redo);
        to.push(step);
        this._stamp++;
        this._scheduleRender();
        this._paintSelection();
        return true;
    };

    P._runStep = function (step, redo) {
        if (step.type === "group") {
            for (const s of (redo ? step.steps : step.steps.slice().reverse())) this._runStep(s, redo);
        } else if (step.type === "cells") {
            const list = [];
            for (const ch of step.changes) {
                const col = this._all.find((c) => c.field === ch.field);
                const e = this._edits.get(ch.id);
                const row = e ? e.row : ch.row;
                if (col) list.push({ row, col, value: redo ? ch.value : ch.old });
            }
            const ids = this._applyChanges(list, false);
            const first = step.changes[0];
            const r = first ? this._indexOfId(first.id) : -1;
            const c = first ? this._cols.findIndex((x) => x.field === first.field) : -1;
            if (r >= 0 && c >= 0) this._setCursor(r, c, false);
            this._afterBulk(ids);
        } else if (step.type === "add") {
            if (redo) {
                const e = { id: step.id, row: step.row, orig: {}, fields: copy(step.fields), errors: {}, op: "create", temp: step.temp };
                this._creates.push(step.row);
                this._edits.set(step.id, e);
                this._setCursor(this._rowCount() - 1, this._cur.c, false);
            } else {
                this._dropCreate(step.id);
            }
        } else if (step.type === "delete") {
            if (redo) this._applyDelete(step);
            else this._restoreDelete(step);
        }
    };

    /* -------------------------------------------------------- row menu -- */

    P._rowMenuItems = function () {
        const items = [];
        if (this._openRecord) {
            items.push(this._menuItem("open-record", "document", t("data-grid.open-record", "Open record"), () => this._openRecord(this._cur.r)));
        }
        if (this._editable() && this.mode !== "read") {
            items.push(this._menuItem("insert-row", "plus", t("data-grid.insert-row", "New row"), () => { this.addRow(); this.focus(); }));
            const s = this._rect();
            const rs = [];
            for (let r = s.r1; r <= s.r2; r++) if (this._rowAt(r)) rs.push(r);
            if (rs.length) {
                const label = rs.length === 1 ? t("data-grid.delete-row", "Delete row") : t("data-grid.delete-rows", "Delete {n} rows", { n: int(rs.length) });
                items.push(this._menuItem("delete-rows", "trash", label, () => { this._deleteRows(rs); this.focus(); }, { danger: true }));
            }
        }
        if (items.length) items.push(document.createElement("hr"));
        return items;
    };

    /* ------------------------------------------------------ status line -- */

    P._renderExtra = function () {
        const box = this._extraEl;
        if (!this._edits) { box.hidden = true; return; }
        let errors = 0;
        for (const e of this._edits.values()) if (Object.keys(e.errors).length) errors++;
        const dirty = this._dirtyCount();
        const saving = this._saving > 0;
        const batch = this.saveMode === "batch" && this._editable() && this.mode !== "read";
        const key = `${dirty}|${errors}|${saving}|${batch}`;
        if (box._t === key) return;
        box._t = key;
        box.textContent = "";
        if (saving) {
            const busy = document.createElement("span");
            busy.className = "busy";
            const sp = document.createElement("sac-spinner");
            sp.setAttribute("label", t("data-grid.saving", "Saving…"));
            busy.append(sp, document.createTextNode(t("data-grid.saving", "Saving…")));
            box.appendChild(busy);
        }
        if (errors) {
            const err = document.createElement("span");
            err.className = "err";
            err.textContent = errors === 1 ? t("data-grid.errors-one", "1 row with errors") : t("data-grid.errors", "{n} rows with errors", { n: int(errors) });
            box.appendChild(err);
        }
        if (batch && dirty) {
            const info = document.createElement("span");
            info.textContent = dirty === 1 ? t("data-grid.unsaved-one", "1 unsaved row") : t("data-grid.unsaved", "{n} unsaved rows", { n: int(dirty) });
            const revert = document.createElement("button");
            revert.type = "button";
            revert.className = "btn";
            revert.textContent = t("data-grid.revert", "Discard");
            revert.addEventListener("click", () => { this.revert(); this.focus(); });
            const save = document.createElement("button");
            save.type = "button";
            save.className = "btn primary";
            save.textContent = t("data-grid.save", "Save");
            save.disabled = saving;
            save.addEventListener("click", () => { this.save(); this.focus(); });
            box.append(info, revert, save);
        }
        box.hidden = !box.firstChild;
    };

    /* ---------------------------------------------------------- lifecycle -- */

    Grid.hook("connect", function () {
        this._edState();
        if (!this._edHooked) {
            this._edHooked = true;
            this.addEventListener("focusout", (e) => this._onFocusOut(e));
            this._compactMq = matchMedia(COMPACT);
            this._onCompact = () => {
                if (this._editor && this._compact()) this._commitEdit(null);
                this._stamp++;
                this._scheduleRender();
            };
        }
        this._compactMq.addEventListener("change", this._onCompact);
    });

    Grid.hook("disconnect", function () {
        if (this._editor) this._commitEdit(null);
        if (this._compactMq) this._compactMq.removeEventListener("change", this._onCompact);   // no leak per removed grid
    });
})();
