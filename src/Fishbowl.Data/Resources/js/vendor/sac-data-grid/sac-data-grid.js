/**
 * <sac-data-grid label="Orders" mode="sheet" save-mode="row" paging="scroll">
 *
 * A spreadsheet-grade data table for SACRVM APPKIT apps (the full contract is
 * SPEC.md): virtualized rows (100k+ scroll smoothly), a fixed header and
 * footer, frozen columns, sort / filter / resize / hide per column, Excel-
 * style keyboard and range selection, TSV clipboard. Built from the kit's
 * tokens and fields, so it themes, translates and formats like the rest of
 * the app.
 *
 * Requires kit ≥ 2.22.0 (css/ui.css, lib/globals.js, and the components it
 * uses: sac-icon, sac-menu, sac-spinner, sac-toast, sac-dialog, sac-tooltip,
 * sac-chip, sac-segmented-control and the cell editors) plus the grid's own
 * scripts, in this order:
 *   js/sac-data-grid-types.js, js/sac-data-grid.js, js/sac-data-grid-edit.js
 *   (editing; without it the grid is read-only), js/sac-data-grid-form.js (the
 *   record form), js/sac-data-grid-source.js, js/sac-data-grid.de.js
 *
 * Usage:
 *   <sac-data-grid id="orders" label="Orders"></sac-data-grid>
 *
 *   grid.columns = [
 *       { field: "name", label: "Name", type: "text", frozen: true, required: true },
 *       { field: "amount", label: "Amount", type: "number", decimals: 2, aggregate: "sum" },
 *   ];
 *   grid.source = SacDataGrid.arraySource(rows, { key: "id" });
 *
 * Attributes:
 *   label      — the grid's accessible name.
 *   mode       — "read" | "sheet" (default) | "form" (SPEC §2). A source
 *                without save() is read-only whatever the mode.
 *   save-mode  — "cell" | "row" (default) | "batch" (SPEC §7-1).
 *   paging     — "scroll" (default: incremental / virtual scrolling) or
 *                "pages" (a pager in the footer) (SPEC §7-2).
 *   page-size  — rows per page for paging="pages" (default 100).
 *   lines      — "quiet" (default: row lines, a cell outline on the cursor)
 *                or "grid" (cell grid lines) (SPEC §7-3).
 *   mode-toggle — presence shows a Read · Sheet · Form switch in the footer.
 *   compact-edit — phones (the kit's compact viewport): "form" (default: the
 *                record form is the way to edit, no inline editing) or
 *                "read" (read-only there) (SPEC §7-4).
 *   row-header — the column before the data: "marks" (default: narrow, it
 *                shows only a row's state: new, an error), "numbers" (the
 *                row's position, as in a spreadsheet) or "none".
 *
 * Properties:
 *   columns — [{ field, label, labelKey?, type, width?, minWidth?, frozen?,
 *             editable?, inline?, required?, validate?, options?, format?,
 *             parse?, aggregate?, align?, hidden?, sortable?, filterable? }]
 *             (types: SacDataGridTypes).
 *   source  — { key, load, save?, create?, remove? } (SPEC §4). An optional
 *             `pageSize` asks the grid to load that many rows per request.
 *   view    — { columns: { field: { width, hidden } }, sort, filter }, get/set.
 *   mode    — get/set, mirrors the attribute.
 *   dirty   — read-only: the number of rows with unsaved changes.
 *
 * Methods: reload(), save(), revert(), addRow(values?), focusCell(id, field).
 *
 * Events (bubble + composed):
 *   sac:change   one cell committed, detail { id, field, value, old }
 *   sac:selection  detail { cursor: { index, id, field }, range, count }
 *   sac:save     detail = the source's save() result
 *   sac:request-delete  cancelable, detail { ids }
 *   sac:view     widths / visibility / sort / filter changed, detail = view
 *   sac:load-error  detail { error, offset, limit }
 *
 * Filter descriptors (grid → source.load, per field):
 *   { op: "contains", value }        text, longtext, color, readonly
 *   { op: "range", min?, max? }      number, date, datetime, time (ISO strings
 *                                    for dates; a date-only bound matches the
 *                                    whole day of a datetime)
 *   { op: "any", values: [] }        select, tags (a tag row matches when it
 *                                    carries any of the values)
 *   { op: "is", value: true|false }  bool
 * load() also receives `aggregate: [{ field, fn }]` for the footer; a source
 * may answer with `aggregates: { field: value }` over the whole result. When
 * it does not, the grid shows totals once every row is loaded.
 *
 * Keyboard (the grid is one tab stop; SPEC §3): arrows move, Shift extends,
 * Ctrl+arrows jump to the data edge, Home / End, Ctrl+Home / Ctrl+End,
 * PgUp / PgDn, Tab / Shift+Tab (leaving the grid past the last / first cell),
 * Esc clears the range, Ctrl+A selects all, Shift+Space the row, Ctrl+Space
 * the column, Alt+↓ opens the column menu, Shift+F10 / the menu key the row
 * menu, Ctrl+C / X / V copy, cut and paste TSV, Alt+PgUp / Alt+PgDn change
 * the page. Editing (js/sac-data-grid-edit.js): Enter, F2 or typing edit in
 * sheet mode (Enter opens the record form in form and read mode, Shift+Enter
 * everywhere), Enter / Shift+Enter / Tab commit and move, Esc cancels,
 * Delete / Backspace clear, Space toggles a bool, Ctrl+Z / Ctrl+Y undo and
 * redo, Ctrl+S saves.
 *
 * CSS custom properties: --grid-bg (the ground frozen cells paint, default
 * --bg; set it to the surface the grid sits on), --cell-padding-inline.
 * CSS parts: grid, status.
 */
(function () {
    if (customElements.get("sac-data-grid")) return;

    const t = (key, fallback, vars) => {
        let s = (window.sac && sac.t) ? sac.t(key, fallback) : fallback;
        if (vars) s = String(s).replace(/\{(\w+)\}/g, (m, k) => (vars[k] !== undefined ? String(vars[k]) : m));
        return s;
    };
    const Types = () => window.SacDataGridTypes;
    const clamp = (v, lo, hi) => (v < lo ? lo : v > hi ? hi : v);
    const int = (n) => (window.sac && sac.regional) ? sac.regional.formatNumber(n) : String(n);
    const clone = (v) => (v === undefined ? v : JSON.parse(JSON.stringify(v)));

    const OVERSCAN = 8;
    const ROW_H = 32, ROW_H_COARSE = 44;
    const HEAD_H = 36, HEAD_H_COARSE = 44;
    const MODES = ["read", "sheet", "form"];
    const SAVE_MODES = ["cell", "row", "batch"];
    const AGGREGATES = ["sum", "avg", "count", "min", "max"];
    const STATS_CELL_CAP = 2000000;   // selection stats stop scanning past this
    const STATS_BLOCK_CAP = 20;       // …and load at most this many missing blocks (a local
                                      // source's 100k rows are 20 blocks; a server's are not loaded)

    let instances = 0;

    const STYLE = `
        :host {
            --grid-bg: var(--bg);
            --row-hover: color-mix(in srgb, var(--fg) 3%, transparent);
            --dirty-tint: color-mix(in srgb, var(--accent-warm) 14%, transparent);
            --invalid-tint: color-mix(in srgb, var(--danger) 12%, transparent);
            display: block;
            position: relative;
            height: 420px;
            min-height: 160px;
            overflow: hidden;
            border: 1px solid var(--border);
            border-radius: var(--radius-l);
            background: var(--grid-bg);
            color: var(--text);
            font-family: 'Inter', -apple-system, BlinkMacSystemFont, sans-serif;
            font-size: 0.875rem;
            line-height: 1.4;
        }
        :host([hidden]) { display: none; }
        *, *::before, *::after { box-sizing: border-box; }
        [hidden] { display: none !important; }

        .wrap { display: flex; flex-direction: column; height: 100%; }
        .scroller {
            position: relative;
            flex: 1 1 auto;
            min-height: 0;
            overflow: auto;
            outline: none;
            overscroll-behavior: contain;
            -webkit-user-select: none;
            user-select: none;
        }
        /* The kit's scrollbar (ui.css §5) — global rules do not pierce a shadow root. */
        .scroller::-webkit-scrollbar { width: 10px; height: 10px; }
        .scroller::-webkit-scrollbar-track { background: transparent; }
        .scroller::-webkit-scrollbar-thumb {
            background: var(--scrollbar-thumb);
            background-clip: content-box;
            border: 2px solid transparent;
            border-radius: 999px;
        }
        .scroller::-webkit-scrollbar-thumb:hover { background: var(--scrollbar-thumb-hover); background-clip: content-box; }
        .scroller::-webkit-scrollbar-corner { background: transparent; }
        @supports not selector(::-webkit-scrollbar) {
            .scroller { scrollbar-width: thin; scrollbar-color: var(--scrollbar-thumb) transparent; }
        }

        .canvas { position: relative; width: var(--row-w); min-width: 100%; }
        .row {
            display: grid;
            grid-template-columns: var(--cols);
            min-width: 100%;
            width: var(--row-w);
        }

        /* ---- header ---- */
        .head { position: sticky; top: 0; z-index: 4; background: var(--grid-bg); }
        .hrow { height: var(--head-h); border-bottom: 1px solid var(--border-strong); }
        .hcell, .corner {
            position: relative;
            display: flex;
            align-items: center;
            gap: 2px;
            min-width: 0;
            padding: 0 var(--cell-padding-inline, 8px);
            background: var(--grid-bg);
            font-size: 0.68rem;
            font-weight: 700;
            letter-spacing: 0.08em;
            text-transform: uppercase;
            color: var(--text-muted);
            cursor: pointer;
        }
        .corner { padding: 0; justify-content: center; position: sticky; left: 0; z-index: 2; border-right: 1px solid var(--border); }
        .hcell.fz { position: sticky; z-index: 2; }
        .hcell.fz-last { border-right: 1px solid var(--border-strong); }
        .hcell:hover, .corner:hover { color: var(--text); }
        .hcell.sel { color: var(--accent-text); }
        /* Nothing in a header takes width beside the title, so it lines up
           with its column's values: the sort mark sits above (ascending) or
           below (descending) the title, a filter tints the title, and the
           column menu opens on a right-click (long press, Alt+Down). */
        .hcell.ar { justify-content: flex-end; }
        .hcell.ac { justify-content: center; }
        .hcell.open { color: var(--text); }
        .hcell.nosort { cursor: default; }
        .hcell .ttl { position: relative; display: flex; min-width: 0; max-width: 100%; }
        .hcell .lbl { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
        .hcell .sort {
            position: absolute;
            left: 50%;
            transform: translateX(-50%);
            display: flex;
            align-items: center;
            gap: 2px;
            line-height: 1;
            color: var(--accent-text);
            pointer-events: none;
        }
        .hcell .sort[hidden] { display: none; }
        .hcell .sort::before { content: ""; border-inline: 3.5px solid transparent; }
        .hcell .sort.asc { bottom: 100%; margin-bottom: 2px; }
        .hcell .sort.asc::before { border-bottom: 4px solid currentColor; }
        .hcell .sort.desc { top: 100%; margin-top: 2px; }
        .hcell .sort.desc::before { border-top: 4px solid currentColor; }
        .hcell .prio { font-size: 0.55rem; letter-spacing: 0; }
        /* A filtered column: its title in the accent colour, dotted
           underline (the colour alone is also the cursor column's). */
        .hcell.filtered .lbl {
            color: var(--accent-text);
            text-decoration: underline dotted 1px;
            text-underline-offset: 2px;
        }
        .hcell .rs {
            position: absolute;
            top: 0;
            right: 0;
            width: 7px;
            height: 100%;
            cursor: col-resize;
            touch-action: none;
        }
        /* On the column's edge: over the grid line (lines="grid"), else on
           the column's last pixel, where that line would be. */
        .hcell .rs::after {
            content: "";
            position: absolute;
            top: 25%;
            right: 0;
            width: 1px;
            height: 50%;
            background: var(--border-strong);
            opacity: 0;
        }
        .hcell:hover .rs::after, .hcell .rs.active::after { opacity: 1; }
        .hcell .rs.active::after { background: var(--accent); top: 0; height: 100%; }
        :host([lines="grid"]) .hcell { border-right: 1px solid var(--border); }
        :host([lines="grid"]) .hcell .rs::after { right: -1px; }

        /* ---- body ---- */
        .body { position: relative; }
        .body .row {
            position: absolute;
            top: 0;
            left: 0;
            height: var(--row-h);
            border-bottom: 1px solid var(--border);
            will-change: transform;
        }
        .cell, .rh {
            display: block;
            min-width: 0;
            height: calc(var(--row-h) - 1px);
            line-height: calc(var(--row-h) - 1px);
            padding: 0 var(--cell-padding-inline, 8px);
            overflow: hidden;
            white-space: nowrap;
            text-overflow: ellipsis;
            background-image:
                linear-gradient(var(--l-sel, transparent), var(--l-sel, transparent)),
                linear-gradient(var(--l-state, transparent), var(--l-state, transparent)),
                linear-gradient(var(--l-hover, transparent), var(--l-hover, transparent));
        }
        .cell.ar { text-align: right; font-variant-numeric: tabular-nums; }
        .cell.ac { text-align: center; }
        .cell.fz, .rh { position: sticky; z-index: 1; background-color: var(--grid-bg); }
        .cell.fz-last { border-right: 1px solid var(--border-strong); }
        :host([lines="grid"]) .cell { border-right: 1px solid var(--border); }
        .rh {
            left: 0;
            padding: 0 8px 0 4px;
            text-align: right;
            font-size: 0.72rem;
            font-variant-numeric: tabular-nums;
            color: var(--text-dim);
            border-right: 1px solid var(--border);
            cursor: default;
        }
        .row:hover > .cell, .row:hover > .rh { --l-hover: var(--row-hover); }
        .rh.sel { --l-sel: var(--accent-tint); color: var(--accent-text); }
        .rh.err { color: var(--danger-text); }
        /* row-header="marks": narrow, only a row's state (* new, + the new line, a dot for errors). */
        .canvas[data-rh="marks"] .rh { padding: 0; text-align: center; }
        .canvas[data-rh="marks"] .rh.err::before {
            content: "";
            display: inline-block;
            width: 6px;
            height: 6px;
            border-radius: 50%;
            background: var(--danger);
            vertical-align: middle;
        }
        .canvas[data-rh="none"] :is(.rh, .corner, .fcell.first) { display: none; }
        .cell.sel { --l-sel: var(--accent-tint); }
        .cell.dirty { --l-state: var(--dirty-tint); }
        .cell.invalid { --l-state: var(--invalid-tint); box-shadow: inset 0 -1px 0 var(--danger); }
        .cell.cur { box-shadow: inset 0 0 0 1px var(--accent); }
        .scroller:focus .cell.cur, .cell.cur.editing { box-shadow: inset 0 0 0 2px var(--accent); }
        .cell.ro { color: var(--text-muted); }

        .row.skel .cell::after {
            content: "";
            display: inline-block;
            vertical-align: middle;
            width: 60%;
            height: 8px;
            border-radius: var(--radius-s);
            background: color-mix(in srgb, var(--fg) 7%, transparent);
            animation: pulse 1.2s ease-in-out infinite;
        }
        @keyframes pulse { 50% { opacity: 0.45; } }

        .note { display: none; }
        .row.msg > .cell, .row.msg > .rh { display: none; }
        .row.msg > .note {
            display: flex;
            align-items: center;
            gap: 8px;
            grid-column: 1 / -1;
            position: sticky;
            left: 0;
            width: var(--vw, 100%);
            height: calc(var(--row-h) - 1px);
            padding: 0 12px;
            color: var(--text-muted);
            font-size: 0.8rem;
            --spinner-size: 14px;
        }
        .row.msg.error > .note { color: var(--danger-text); }

        /* ---- editing (sheet mode) ---- */
        .cell.editing { padding: 0; background-color: var(--grid-bg); z-index: 2; }
        /* The kit's .cell-input recipe (ui.css does not pierce the shadow root). */
        .cell-input,
        .cell-input:hover,
        .cell-input:focus {
            display: block;
            width: 100%;
            height: 100%;
            margin: 0;
            padding: 0 var(--cell-padding-inline, 8px);
            font: inherit;
            color: inherit;
            background: transparent;
            border: 0;
            border-radius: 0;
            box-shadow: none;
            outline: none;
        }
        .cell.ar .cell-input { text-align: right; }
        /* The time field takes what it needs, the date (with its calendar
           button) the rest: an even split cut the date short. */
        .dt-editor { display: flex; height: 100%; }
        .dt-editor > * { flex: 1 1 0; min-width: 0; }
        .dt-editor > sac-time-field { flex: 0 0 auto; width: max-content; }
        /* A lifted editor: the editing cell, only wider (over its neighbours). */
        .lift-pop {
            position: fixed;
            inset: auto;
            margin: 0;
            padding: 0;
            border: 0;
            background: var(--grid-bg);
            box-shadow: inset 0 0 0 2px var(--accent), var(--shadow-1);
            color: var(--text);
            overflow: visible;
            font: inherit;
        }
        .long-pop {
            position: fixed;
            inset: auto;
            margin: 0;
            padding: 0;
            border: 1px solid var(--accent);
            border-radius: var(--radius-m);
            background: var(--panel-2);
            box-shadow: var(--shadow-2);
            color: var(--text);
            overflow: hidden;
        }
        .long-pop textarea.cell-input {
            padding: 6px var(--cell-padding-inline, 8px);
            resize: none;
            line-height: 1.45;
            white-space: pre-wrap;
        }
        .row.deleted > .cell { text-decoration: line-through; color: var(--text-dim); }
        .row.created > .rh { color: var(--accent-warm-text); }
        .row.newline > .cell, .row.newline > .rh { color: var(--text-dim); }
        .scroller[data-mode="sheet"][aria-readonly="false"] .cell:not(.ro) .bool { cursor: pointer; }
        .status .extra { display: flex; align-items: center; gap: 8px; }
        .status .extra[hidden] { display: none; }
        .status .err { color: var(--danger-text); }
        .status .busy { display: inline-flex; align-items: center; gap: 6px; --spinner-size: 12px; }

        /* cell renders */
        .bool {
            display: inline-block;
            vertical-align: middle;
            width: 14px;
            height: 14px;
            border-radius: var(--radius-s);
            box-shadow: inset 0 0 0 1px var(--border-strong);
            font-size: 0;
            color: transparent;
        }
        .bool.on {
            box-shadow: none;
            background-color: var(--accent-fill);
            -webkit-mask: url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'%3E%3Crect width='16' height='16' rx='2' fill='white'/%3E%3Cpath d='M4 8.2l2.6 2.6L12 5.4' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'/%3E%3C/svg%3E") center / 14px 14px no-repeat;
                    mask: url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'%3E%3Crect width='16' height='16' rx='2' fill='white'/%3E%3Cpath d='M4 8.2l2.6 2.6L12 5.4' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'/%3E%3C/svg%3E") center / 14px 14px no-repeat;
            -webkit-mask-mode: luminance;
                    mask-mode: luminance;
        }
        .cell > sac-chip { vertical-align: middle; max-width: 100%; margin-right: 4px; }
        .swatch {
            display: inline-block;
            vertical-align: middle;
            width: 14px;
            height: 14px;
            margin-right: 6px;
            border-radius: var(--radius-s);
            background: var(--swatch);
            box-shadow: inset 0 0 0 1px var(--border-strong);
        }
        .hex { vertical-align: middle; font-family: var(--font-mono); font-size: 0.8rem; color: var(--text-muted); }

        /* ---- footer (per-column aggregates) ---- */
        .foot { position: sticky; bottom: 0; z-index: 4; background: var(--grid-bg); }
        .frow { height: var(--row-h); border-top: 1px solid var(--border-strong); }
        .fcell {
            display: flex;
            align-items: center;
            gap: 6px;
            min-width: 0;
            padding: 0 var(--cell-padding-inline, 8px);
            overflow: hidden;
            white-space: nowrap;
            background: var(--grid-bg);
            font-variant-numeric: tabular-nums;
            color: var(--text);
            font-weight: 600;
        }
        .fcell.ar { justify-content: flex-end; }
        .fcell.ac { justify-content: center; }
        .agg-v { flex: none; }
        .fcell.fz { position: sticky; z-index: 1; }
        .fcell.fz-last { border-right: 1px solid var(--border-strong); }
        .fcell.first { position: sticky; left: 0; z-index: 1; border-right: 1px solid var(--border); }
        .agg-l {
            flex: 0 1 auto;
            min-width: 0;
            overflow: hidden;
            font-size: 0.62rem;
            font-weight: 700;
            letter-spacing: 0.08em;
            text-transform: uppercase;
            color: var(--text-dim);
        }

        /* ---- status line ---- */
        .status {
            flex: none;
            display: flex;
            flex-wrap: wrap;                 /* a narrow grid stacks them, never hides a Save */
            align-items: center;
            gap: 2px 14px;
            min-height: 32px;
            padding: 3px 8px 3px 12px;
            border-top: 1px solid var(--border);
            font-size: 0.75rem;
            color: var(--text-muted);
            font-variant-numeric: tabular-nums;
            white-space: nowrap;
        }
        .status .spacer { flex: 1 1 auto; }
        .status .selinfo { overflow: hidden; text-overflow: ellipsis; }
        .pager { display: flex; align-items: center; gap: 2px; }
        .pager button {
            display: inline-flex;
            align-items: center;
            justify-content: center;
            min-width: 26px;
            height: 26px;
            padding: 0;
            border: 0;
            border-radius: var(--radius-m);
            background: transparent;
            color: var(--text-muted);
            cursor: pointer;
            --icon-size: 14px;
        }
        .pager button:hover:not(:disabled) { background: var(--hover); color: var(--text); }
        .pager button:disabled { opacity: 0.35; cursor: not-allowed; }
        .pager .pg { padding: 0 6px; }

        /* ---- buttons and fields inside the grid (kit recipes; ui.css does not pierce) ---- */
        .btn {
            display: inline-flex;
            align-items: center;
            justify-content: center;
            gap: 6px;
            height: 26px;
            padding: 0 10px;
            border: 1px solid var(--border);
            border-radius: var(--radius-m);
            background: color-mix(in srgb, var(--fg) 5%, transparent);
            color: var(--text);
            font: inherit;
            font-size: 0.68rem;
            font-weight: 600;
            letter-spacing: 0.02em;
            text-transform: uppercase;
            cursor: pointer;
            transition: background 0.2s ease, border-color 0.2s ease;
            --icon-size: 14px;
        }
        .btn:hover:not(:disabled) { background: var(--hover); border-color: var(--accent); }
        .btn.primary { background: var(--accent-fill); color: var(--on-accent); border-color: transparent; }
        .btn.primary:hover:not(:disabled) { background: var(--accent-strong); }
        .btn:disabled { opacity: 0.3; cursor: not-allowed; }
        .btn:focus-visible, .pager button:focus-visible {
            outline: 2px solid var(--accent);
            outline-offset: 1px;
        }
        .field {
            width: 100%;
            padding: 0.6rem;
            border: 1px solid var(--border);
            border-radius: var(--radius-m);
            background: var(--field);
            color: var(--text);
            font: inherit;
            transition: border-color 0.2s ease;
        }
        .field:hover { border-color: var(--border-strong); }
        .field:focus {
            outline: none;
            border-color: var(--accent);
            box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent) 10%, transparent);
        }
        .field::placeholder { color: var(--text-dim); }

        /* ---- filter popover ---- */
        .pop {
            position: fixed;
            inset: auto;
            margin: 0;
            width: 280px;
            max-width: calc(100vw - 16px);
            padding: 12px;
            border: 1px solid var(--border-strong);
            border-radius: var(--radius-l);
            background: var(--glass-strong);
            -webkit-backdrop-filter: blur(16px);
            backdrop-filter: blur(16px);
            box-shadow: var(--shadow-2);
            color: var(--text);
            font-size: 0.8rem;
        }
        .pop-title {
            margin: 0 0 10px;
            font-family: var(--caption-font);
            font-size: var(--caption-size);
            font-weight: var(--caption-weight);
            letter-spacing: var(--caption-tracking);
            text-transform: uppercase;
            color: var(--text-muted);
        }
        .pop-body { display: flex; flex-direction: column; gap: 8px; }
        .pop-row { display: flex; align-items: center; gap: 8px; }
        .pop-row > * { flex: 1 1 0; min-width: 0; }
        .pop-row sac-number-field, .pop-row sac-date-field, .pop-row sac-time-field { width: 100%; }
        .pop-label { font-size: 0.68rem; font-weight: 700; letter-spacing: 0.05em; text-transform: uppercase; color: var(--text-muted); }
        .opts { display: flex; flex-direction: column; max-height: 220px; overflow: auto; margin: 0 -4px; }
        .opt { display: flex; align-items: center; gap: 8px; min-height: 30px; padding: 0 4px; border-radius: var(--radius-m); cursor: pointer; }
        .opt:hover { background: var(--hover); }
        .opt input { margin: 0; accent-color: var(--accent); }
        .pop-foot { display: flex; justify-content: flex-end; gap: 8px; margin-top: 12px; }

        .sr {
            position: absolute;
            width: 1px;
            height: 1px;
            margin: -1px;
            padding: 0;
            overflow: hidden;
            clip: rect(0 0 0 0);
            white-space: nowrap;
            border: 0;
        }

        .pop:focus { outline: none; }
        sac-menu { position: absolute; }    /* its panel opens in the top layer */
        /* An empty grid has no cursor cell to show that it has focus. */
        .scroller.empty:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; }

        @media (pointer: coarse) {
            .field { min-height: 44px; font-size: max(16px, 1rem); }
            .pager button { min-width: 44px; height: 44px; }
            .opt { min-height: 44px; }
            .btn { height: 44px; }
        }
        @media (prefers-reduced-motion: reduce) {
            .row.skel .cell::after { animation: none; }
            .btn { transition: none; }
        }
        /* Windows high contrast: shadows and background images are dropped,
           so the cursor, the range and the cell states need system colors. */
        @media (forced-colors: active) {
            .cell.cur, .scroller:focus .cell.cur { outline: 2px solid Highlight; outline-offset: -2px; }
            .cell.sel, .rh.sel { forced-color-adjust: none; background: Highlight; color: HighlightText; }
            .cell.dirty { text-decoration: underline dotted; }
            .cell.invalid { outline: 2px dashed CanvasText; outline-offset: -3px; }
            .bool { border: 1px solid CanvasText; }
            .bool.on { forced-color-adjust: none; background-color: CanvasText; }
            .hcell.sel { text-decoration: underline; }
        }
    `;

    function el(tag, cls, text) {
        const e = document.createElement(tag);
        if (cls) e.className = cls;
        if (text != null) e.textContent = text;
        return e;
    }

    function icon(name) {
        const i = document.createElement("sac-icon");
        i.setAttribute("name", name);
        i.setAttribute("aria-hidden", "true");
        return i;
    }

class SacDataGrid extends HTMLElement {
    static get observedAttributes() {
        return ["mode", "save-mode", "paging", "page-size", "lines", "label", "mode-toggle", "compact-edit", "row-header"];
    }

    constructor() {
        super();
        this.attachShadow({ mode: "open" });
        this._gid = "g" + (++instances);
        this._defs = [];            // columns as given
        this._all = [];             // normalized, definition order
        this._cols = [];            // the visible ones
        this._source = null;
        this._sort = [];
        this._filter = {};
        this._widths = Object.create(null);
        this._hidden = new Set();
        this._blocks = new Map();   // block index → { rows, error, ctrl, promise }
        this._query = 0;            // bumped when loaded data goes stale
        this._total = null;         // known row count, or null
        this._known = 0;            // total unknown: rows loaded so far
        this._aggs = null;          // source-provided aggregates
        this._page = 0;
        this._cur = { r: 0, c: 0 };     // the active cell
        this._anchor = { r: 0, c: 0 };  // the range = bounding box of anchor and end
        this._end = { r: 0, c: 0 };
        this._pool = [];
        this._stamp = 0;            // bumped when rendered cell content goes stale
        this._raf = 0;
        this._st = 0;               // cached scroll offsets and viewport size:
        this._sl = 0;               // keyboard navigation never reads layout
        this._vw = 0;
        this._vh = 0;
        this._rowH = ROW_H;
        this._headH = HEAD_H;
        this._x = [];
        this._w = [];
        this._nf = 0;
        this._frozenW = 0;
        this._rhW = 48;
        this._bs = 100;
        this._stats = null;
        this._onDocCopy = (e) => this._onClipboard(e);
    }

    /* ------------------------------------------------------- lifecycle -- */

    connectedCallback() {
        if (!this.shadowRoot.firstChild) this._build();
        for (const p of ["columns", "source", "view", "mode"]) {
            if (Object.prototype.hasOwnProperty.call(this, p)) {
                const v = this[p];
                delete this[p];
                this[p] = v;
            }
        }
        this._hook();
        if (!this._coarse) {
            this._coarse = matchMedia("(pointer: coarse)");
            this._onCoarse = () => this._metrics();
        }
        this._coarse.addEventListener("change", this._onCoarse);
        this._ro = this._ro || new ResizeObserver(() => this._measure());
        this._ro.observe(this._scroller);
        document.addEventListener("copy", this._onDocCopy);
        document.addEventListener("cut", this._onDocCopy);
        document.addEventListener("paste", this._onDocCopy);
        this._metrics();
        this._measure();
        runHooks(this, "connect");
    }

    disconnectedCallback() {
        runHooks(this, "disconnect");
        if (this._offLang) { this._offLang(); this._offLang = null; }
        if (this._offReg) { this._offReg(); this._offReg = null; }
        if (this._coarse) this._coarse.removeEventListener("change", this._onCoarse);
        if (this._ro) this._ro.disconnect();
        document.removeEventListener("copy", this._onDocCopy);
        document.removeEventListener("cut", this._onDocCopy);
        document.removeEventListener("paste", this._onDocCopy);
        if (this._raf) { cancelAnimationFrame(this._raf); this._raf = 0; }
        this._closeFilter();
        if (this._menu && typeof this._menu.close === "function") this._menu.close();
        clearTimeout(this._tipTimer);
        this._tipHover = null;
        this._tipCell = null;
        this._cancelHeadPress();
    }

    attributeChangedCallback(name, old, value) {
        if (!this._built || old === value) return;
        switch (name) {
            case "label":
                this._scroller.setAttribute("aria-label", value || t("data-grid.label", "Data grid"));
                break;
            case "mode":
                this._modeChanged();
                break;
            case "paging":
            case "page-size":
                this._page = 0;
                this._resetData();
                break;
            case "save-mode":
            case "mode-toggle":
                this._renderStatus();
                break;
            case "compact-edit":
                this._stamp++;
                this._scheduleRender();
                break;
            case "row-header":
                this._layout();
                // Its width counts towards the frozen columns' 60 %.
                if (this._cols.length && this._frozenCount() !== this._nf) this._visible();
                this._stamp++;
                this._scheduleRender();
                break;
        }
    }

    /** Subscribe to language / regional changes once the kit is there (the
     *  kit's scripts may load after this one). */
    _hook() {
        if (!window.sac) return;
        if (sac.lang && !this._offLang) this._offLang = sac.lang.onChange(() => this._relabel());
        if (sac.regional && !this._offReg) this._offReg = sac.regional.onChange(() => this._refresh());
    }

    /* ------------------------------------------------------ public API -- */

    get columns() { return this._defs.slice(); }
    set columns(v) {
        this._defs = Array.isArray(v) ? v.filter((c) => c && c.field != null) : [];
        if (!this._built) return;
        this._initColumns(true);
    }

    get source() { return this._source; }
    set source(v) {
        if (this._onSourceChange && v !== this._source) this._onSourceChange();
        this._source = v || null;
        if (!this._built) return;
        this._resetSelection();
        this._page = 0;
        this._resetData();
    }

    get mode() {
        const m = this.getAttribute("mode");
        return MODES.includes(m) ? m : "sheet";
    }
    set mode(v) { this.setAttribute("mode", MODES.includes(v) ? v : "sheet"); }

    get saveMode() {
        const m = this.getAttribute("save-mode");
        return SAVE_MODES.includes(m) ? m : "row";
    }
    set saveMode(v) { this.setAttribute("save-mode", SAVE_MODES.includes(v) ? v : "row"); }

    get dirty() { return this._dirtyCount ? this._dirtyCount() : 0; }

    get view() {
        const columns = {};
        for (const c of this._all) columns[c.field] = { width: this._widthOf(c), hidden: this._hidden.has(c.field) };
        return { columns, sort: clone(this._sort), filter: clone(this._filter) };
    }
    set view(v) {
        if (!v || typeof v !== "object") return;
        this._pendingView = v;
        if (!this._built) return;
        this._applyView(v);
    }

    /** Throw away loaded rows and load again; keeps scroll position and selection. */
    reload() {
        if (!this._built) return Promise.resolve();
        this._resetData(true);
        return this._settled();
    }

    /** Move the cursor to a loaded row's cell and focus the grid. */
    focusCell(id, field) {
        const r = this._indexOfId(id);
        if (r < 0) return false;
        let c = field == null ? this._cur.c : this._cols.findIndex((col) => col.field === field);
        if (c < 0) return false;
        this._setCursor(r, c);
        this._scroller.focus({ preventScroll: true });
        return true;
    }

    save() { return Promise.resolve({ saved: [], errors: [] }); }
    revert() {}
    addRow() { return null; }

    /** Focusing the element focuses its grid (the one tab stop) — also what a
     *  closing sac-menu or sac-dialog hands focus back to. */
    focus(opts) {
        if (this._scroller) this._scroller.focus(opts);
        else super.focus(opts);
    }

    /* ------------------------------------------------------------ build -- */

    _build() {
        this.shadowRoot.innerHTML = `
            <style>${STYLE}</style>
            <div class="wrap" part="grid">
                <div class="scroller" tabindex="0" role="grid" aria-multiselectable="true">
                    <div class="canvas">
                        <div class="head" role="rowgroup"><div class="row hrow" role="row" aria-rowindex="1"></div></div>
                        <div class="body" role="rowgroup"></div>
                        <div class="foot" role="rowgroup" hidden><div class="row frow" role="row"></div></div>
                    </div>
                </div>
                <div class="status" part="status">
                    <span class="count"></span>
                    <span class="selinfo"></span>
                    <span class="spacer"></span>
                    <span class="extra"></span>
                    <span class="modes" hidden></span>
                    <nav class="pager" hidden></nav>
                </div>
                <div class="sr live" aria-live="polite"></div>
            </div>
            <div class="pop" popover="manual" role="dialog"></div>
        `;
        const $ = (s) => this.shadowRoot.querySelector(s);
        this._scroller = $(".scroller");
        this._canvas = $(".canvas");
        this._head = $(".head");
        this._hrow = $(".hrow");
        this._body = $(".body");
        this._foot = $(".foot");
        this._frow = $(".frow");
        this._status = $(".status");
        this._countEl = $(".count");
        this._selEl = $(".selinfo");
        this._extraEl = $(".extra");
        this._modesEl = $(".modes");
        this._pager = $(".pager");
        this._live = $(".live");
        this._pop = $(".pop");
        this._menu = document.createElement("sac-menu");
        this.shadowRoot.appendChild(this._menu);
        // One kit bubble for validation messages, moved to the cell it explains.
        this._tip = document.createElement("sac-tooltip");
        this.shadowRoot.appendChild(this._tip);
        this._tipCell = null;
        this._tipHover = null;
        this._tipTimer = 0;
        this._activeCell = null;
        this._headPress = null;
        this._headPressAt = -Infinity;
        // sac-menu has closed and handed focus back when it reports the item.
        this._menu.addEventListener("sac:select", (e) => {
            e.stopPropagation();
            const fn = this._menuActions && this._menuActions[e.detail && e.detail.action];
            if (fn) fn();
        });
        // Keep the column's header lit while its menu is open.
        new MutationObserver(() => {
            if (this._menu.hasAttribute("open")) return;
            for (const h of this._hcells || []) h.classList.remove("open");
        }).observe(this._menu, { attributes: true, attributeFilter: ["open"] });

        // The kit fields inside (editors, filters, menus) announce their own
        // composed events; the grid speaks for itself on the host.
        for (const type of ["sac:change", "sac:input", "sac:select", "sac:commit", "sac:cancel", "sac:create", "sac:open", "sac:close", "sac:remove", "sac:action"]) {
            this.shadowRoot.addEventListener(type, (e) => e.stopPropagation());
        }
        this._scroller.setAttribute("aria-label", this.getAttribute("label") || t("data-grid.label", "Data grid"));
        this._scroller.addEventListener("scroll", () => this._onScroll(), { passive: true });
        this._scroller.addEventListener("keydown", (e) => this._onKeydown(e));
        this._scroller.addEventListener("focus", () => this._paintSelection());
        this._scroller.addEventListener("blur", () => this._syncTip());
        this._body.addEventListener("pointerover", (e) => this._onBodyPointerOver(e));
        this._body.addEventListener("pointerout", (e) => this._onBodyPointerOut(e));
        this._body.addEventListener("pointerdown", (e) => this._onBodyPointerDown(e));
        this._body.addEventListener("dblclick", (e) => this._onBodyDblClick(e));
        this._body.addEventListener("contextmenu", (e) => this._onBodyContextMenu(e));
        this._body.addEventListener("click", (e) => this._onBodyClick(e));
        this._hrow.addEventListener("click", (e) => this._onHeadClick(e));
        this._hrow.addEventListener("pointerdown", (e) => this._onHeadPointerDown(e));
        this._hrow.addEventListener("dblclick", (e) => this._onHeadDblClick(e));
        this._hrow.addEventListener("contextmenu", (e) => this._onHeadContextMenu(e));
        this._pager.addEventListener("click", (e) => this._onPagerClick(e));
        this._built = true;
        this._initColumns(false);
        if (this._pendingView) this._applyView(this._pendingView, true);
        this._modeChanged();
        this._resetData();
    }

    /* ---------------------------------------------------------- columns -- */

    _normColumn(def) {
        const T = Types();
        const typeName = typeof def.type === "string" ? def.type : (def.type ? "custom" : "text");
        const type = def.type && typeof def.type === "object" ? T.custom(def.type) : T.get(typeName);
        const col = {
            def,
            field: def.field,
            type,
            typeName: type.name,
            label: def.label != null ? String(def.label) : String(def.field),
            labelKey: def.labelKey,
            minWidth: Math.max(24, +def.minWidth || 48),
            frozen: !!def.frozen,
            readonly: !!type.readonly || def.editable === false,
            inline: def.inline !== false,
            required: !!def.required,
            validate: typeof def.validate === "function" ? def.validate : null,
            options: T.normOptions(def.options),
            decimals: def.decimals,
            min: def.min,
            max: def.max,
            step: def.step,
            allowCreate: def.allowCreate,
            aggregate: AGGREGATES.includes(def.aggregate) ? def.aggregate : null,
            sortable: def.sortable !== false,
            filterable: def.filterable !== false,
            align: ["left", "right", "center"].includes(def.align) ? def.align : type.align,
        };
        col.width = Math.max(col.minWidth, +def.width || type.width || 140);
        col.labelText = () => col.labelKey ? t(col.labelKey, col.label) : col.label;
        col.text = (v, row) => {
            if (typeof def.format === "function") {
                const s = def.format(v, row);
                return s == null ? "" : String(s);
            }
            return type.text(v, col, row);
        };
        col.copy = (v, row) => (typeof def.format !== "function" && type.copy) ? type.copy(v, col, row) : col.text(v, row);
        col.render = (typeof def.format !== "function" && type.render) ? type.render : null;
        return col;
    }

    _initColumns(keepData) {
        this._all = this._defs.map((d) => this._normColumn(d));
        const fields = new Set(this._all.map((c) => c.field));
        if (!keepData || !this._seenColumns) {
            this._hidden = new Set(this._all.filter((c) => c.def.hidden).map((c) => c.field));
        } else {
            for (const f of [...this._hidden]) if (!fields.has(f)) this._hidden.delete(f);
            for (const c of this._all) if (!this._seenColumns.has(c.field) && c.def.hidden) this._hidden.add(c.field);
        }
        this._seenColumns = fields;
        // Sort / filter on a column that is gone (or no longer takes them)
        // would silently shape the data.
        const byField = new Map(this._all.map((c) => [c.field, c]));
        const sort = this._sort.filter((s) => byField.has(s.field) && byField.get(s.field).sortable);
        const filter = {};
        for (const [f, d] of Object.entries(this._filter)) if (byField.has(f) && byField.get(f).filterable) filter[f] = d;
        const dataChanged = sort.length !== this._sort.length || Object.keys(filter).length !== Object.keys(this._filter).length;
        this._sort = sort;
        this._filter = filter;
        this._visible();
        if (dataChanged && keepData) this._resetData();
    }

    /** Recompute the visible column list and rebuild what depends on it. */
    _visible() {
        if (this._editor) this._commitEdit(null);          // its cell is about to go
        let cols = this._all.filter((c) => !this._hidden.has(c.field));
        if (!cols.length && this._all.length) {       // never hide the last one
            this._hidden.delete(this._all[0].field);
            cols = [this._all[0]];
        }
        this._cols = cols;
        this._nf = this._frozenCount();
        this._cur.c = clamp(this._cur.c, 0, Math.max(0, cols.length - 1));
        this._anchor.c = clamp(this._anchor.c, 0, Math.max(0, cols.length - 1));
        this._end.c = clamp(this._end.c, 0, Math.max(0, cols.length - 1));
        for (const row of this._pool) row.remove();
        this._pool = [];
        this._buildHead();
        this._buildFoot();
        this._layout();
        this._scroller.setAttribute("aria-colcount", String(cols.length + 1));
        this._stamp++;
        this._scheduleRender();
    }

    _widthOf(col) {
        const w = this._widths[col.field];
        return Math.max(col.minWidth, w != null ? w : col.width);
    }

    /** The leading frozen columns — as many as leave the grid room to scroll
     *  (at most 60 % of its width: a phone keeps one, not all). */
    _frozenCount() {
        const cols = this._cols;
        let nf = 0;
        while (nf < cols.length && cols[nf].frozen) nf++;
        if (!this._vw) return nf;
        let w = this._rhW, fit = 0;
        for (let i = 0; i < nf; i++) {
            w += this._widthOf(cols[i]);
            if (w > this._vw * 0.6) break;
            fit = i + 1;
        }
        return fit;
    }

    /** The row header: "marks" (default), "numbers" or "none". */
    _rhMode() {
        const v = this.getAttribute("row-header");
        return v === "numbers" || v === "none" ? v : "marks";
    }

    /** Column geometry → CSS variables; frozen offsets. No layout reads. */
    _layout() {
        const digits = String(Math.max(1, this._base() + this._rowCount())).length;
        this._rhDigits = digits;
        const mode = this._rhMode();
        this._canvas.dataset.rh = mode;
        this._rhW = mode === "none" ? 0 : mode === "marks" ? 24 : Math.max(44, 18 + digits * 7);
        const w = this._cols.map((c) => this._widthOf(c));
        const x = [];
        let acc = this._rhW;
        for (const wi of w) { x.push(acc); acc += wi; }
        this._w = w;
        this._x = x;
        this._rowW = acc;
        this._frozenW = this._nf ? x[this._nf - 1] + w[this._nf - 1] : this._rhW;
        const s = this._canvas.style;
        // row-header="none" hides the header cells: no track for them either.
        s.setProperty("--cols", (mode === "none" ? w : [this._rhW, ...w]).map((n) => n + "px").join(" "));
        s.setProperty("--row-w", acc + "px");
        const lefts = (cells) => {
            for (let c = 0; c < this._nf && c < cells.length; c++) cells[c].style.left = x[c] + "px";
        };
        lefts(this._hcells || []);
        lefts(this._fcells || []);
        for (const row of this._pool) lefts(row._cells);
    }

    _cellClass(c) {
        const col = this._cols[c];
        let cls = "cell";
        if (col.align === "right") cls += " ar";
        else if (col.align === "center") cls += " ac";
        if (c < this._nf) cls += " fz";
        if (c === this._nf - 1) cls += " fz-last";
        return cls;
    }

    _buildHead() {
        const row = this._hrow;
        row.textContent = "";
        const corner = el("div", "corner");
        corner.setAttribute("role", "columnheader");
        corner.setAttribute("aria-colindex", "1");
        corner.title = t("data-grid.select-all", "Select all");
        const cornerLabel = el("span", "sr", t("data-grid.row-number", "Row"));
        corner.appendChild(cornerLabel);
        row.appendChild(corner);
        this._corner = corner;
        this._hcells = this._cols.map((col, c) => {
            const h = el("div", this._cellClass(c).replace(/^cell/, "hcell"));
            h.setAttribute("role", "columnheader");
            h.setAttribute("aria-colindex", String(c + 2));
            h._c = c;
            h.classList.toggle("nosort", !col.sortable);
            const lbl = el("span", "lbl", col.labelText());
            const sort = el("span", "sort");
            sort.hidden = true;
            const ttl = el("span", "ttl");
            ttl.append(lbl, sort);
            const rs = el("span", "rs");
            rs.setAttribute("aria-hidden", "true");
            h.append(ttl, rs);
            h._lbl = lbl; h._sort = sort; h._rs = rs;
            row.appendChild(h);
            return h;
        });
        this._paintHead();
    }

    _paintHead() {
        if (!this._hcells) return;
        const sorted = new Map(this._sort.map((s, i) => [s.field, { dir: s.dir, i }]));
        this._cols.forEach((col, c) => {
            const h = this._hcells[c];
            const filtered = !!this._filter[col.field];
            h._lbl.textContent = col.labelText();
            h.title = filtered ? `${col.labelText()} · ${t("data-grid.filtered", "Filtered")}` : col.labelText();
            const s = sorted.get(col.field);
            h._sort.hidden = !s;
            h._sort.className = s ? "sort " + (s.dir === "desc" ? "desc" : "asc") : "sort";
            h._sort.textContent = "";
            if (s && this._sort.length > 1) h._sort.appendChild(el("span", "prio", String(s.i + 1)));
            if (s && s.i === 0) h.setAttribute("aria-sort", s.dir === "desc" ? "descending" : "ascending");
            else h.removeAttribute("aria-sort");
            h.classList.toggle("filtered", filtered);
            if (filtered) h.setAttribute("aria-description", t("data-grid.filtered", "Filtered"));
            else h.removeAttribute("aria-description");
        });
        this._corner.title = t("data-grid.select-all", "Select all");
        this._corner.firstChild.textContent = t("data-grid.row-number", "Row");
    }

    _buildFoot() {
        const has = this._cols.some((c) => c.aggregate);
        this._foot.hidden = !has;
        this._footH = has ? this._rowH : 0;
        this._frow.textContent = "";
        this._fcells = [];
        if (!has) return;
        const first = el("div", "fcell first");
        first.setAttribute("role", "rowheader");
        this._frow.appendChild(first);
        this._fcells = this._cols.map((col, c) => {
            const f = el("div", this._cellClass(c).replace(/^cell/, "fcell"));
            f.setAttribute("role", "gridcell");
            f.setAttribute("aria-colindex", String(c + 2));
            this._frow.appendChild(f);
            return f;
        });
    }

    /* ------------------------------------------------------------- view -- */

    _applyView(v, initial) {
        this._pendingView = null;
        if (v.columns && typeof v.columns === "object") {
            for (const [field, cv] of Object.entries(v.columns)) {
                if (!cv) continue;
                if (typeof cv.width === "number" && cv.width > 0) this._widths[field] = cv.width;
                if (typeof cv.hidden === "boolean") {
                    if (cv.hidden) this._hidden.add(field); else this._hidden.delete(field);
                }
            }
        }
        const byField = new Map(this._all.map((c) => [c.field, c]));
        const known = (f, what) => !this._all.length || (byField.has(f) && byField.get(f)[what]);
        if (Array.isArray(v.sort)) {
            this._sort = v.sort
                .filter((s) => s && known(s.field, "sortable"))
                .map((s) => ({ field: s.field, dir: s.dir === "desc" ? "desc" : "asc" }));
        }
        if (v.filter && typeof v.filter === "object") {
            this._filter = {};
            for (const [f, d] of Object.entries(v.filter)) if (d && known(f, "filterable")) this._filter[f] = clone(d);
        }
        this._visible();
        if (!initial) {
            this._resetSelection();
            this._resetData();
        }
    }

    _emitView() {
        this.dispatchEvent(new CustomEvent("sac:view", { detail: this.view, bubbles: true, composed: true }));
    }

    /* ------------------------------------------------------------- data -- */

    _paging() { return this.getAttribute("paging") === "pages" ? "pages" : "scroll"; }

    _pageSize() {
        const n = parseInt(this.getAttribute("page-size"), 10);
        return n > 0 ? Math.min(n, 10000) : 100;
    }

    /** Data index of display row 0 (pages: the page start). */
    _base() { return this._paging() === "pages" ? this._page * this._pageSize() : 0; }

    /** Rows in the current window: the source's, then rows added here and
     *  not yet reloaded (sheet mode). */
    _rowCount() {
        return this._dataCount() + (this._creates ? this._creates.length : 0);
    }

    /** Rows the cursor can reach: the rows, plus the "new row" line. */
    _navRows() {
        return this._rowCount() + (this._newLine ? this._newLine() : 0);
    }

    /** The source's rows in the current window (the whole result, or the page). */
    _dataCount() {
        if (this._paging() === "pages") {
            const size = this._pageSize();
            if (this._total != null) return clamp(this._total - this._base(), 0, size);
            const blk = this._blocks.get(this._page);
            return blk && blk.rows ? blk.rows.length : 0;
        }
        return this._total != null ? this._total : this._known;
    }

    /** The message row after the data rows: loading more, a failed load, or
     *  "no rows". (A failed block among known rows shows on its first row.) */
    _note(rows) {
        if (!this._source) return null;
        if (this._paging() === "pages") {
            if (rows) return null;
            const blk = this._blocks.get(this._page);
            if (blk && blk.error) return { kind: "error", block: this._page };
            if (!blk || !blk.rows) return { kind: "loading", block: this._page };
            return { kind: "empty" };
        }
        if (this._total == null) {
            const next = Math.floor(this._known / this._bs);
            const blk = this._blocks.get(next);
            if (blk && blk.error) return { kind: "error", block: next };
            return { kind: "loading", block: next };
        }
        return this._total === 0 ? { kind: "empty" } : null;
    }

    _resetData(keep) {
        if (this._onReset) this._onReset(keep);
        this._query++;
        for (const b of this._blocks.values()) if (b.ctrl) b.ctrl.abort();
        this._blocks.clear();
        this._total = null;
        this._known = 0;
        this._aggs = null;
        this._stats = null;
        this._bs = this._paging() === "pages" ? this._pageSize()
            : (this._source && this._source.pageSize > 0 ? Math.min(20000, Math.floor(this._source.pageSize)) : 100);
        if (!keep) {
            this._scrollTo(0, this._sl);
            this._resetSelection();
        }
        this._stamp++;
        this._scheduleRender();
    }

    _resetSelection() {
        this._cur = { r: 0, c: clamp(this._cur.c, 0, Math.max(0, this._cols.length - 1)) };
        this._anchor = { ...this._cur };
        this._end = { ...this._cur };
    }

    /** The row object at display index r: undefined while its block is not
     *  loaded, null past the rows (the "new row" line). */
    _rowAt(r) {
        const dc = this._dataCount();
        if (r >= dc) {
            const k = r - dc;
            return this._creates && k < this._creates.length ? this._creates[k] : null;
        }
        const i = this._base() + r;
        const blk = this._blocks.get(Math.floor(i / this._bs));
        return blk && blk.rows ? blk.rows[i - Math.floor(i / this._bs) * this._bs] : undefined;
    }

    _keyField() { return (this._source && this._source.key) || "id"; }

    _idOf(row) { return row ? row[this._keyField()] : undefined; }

    _indexOfId(id) {
        const key = this._keyField();
        const base = this._base(), rows = this._dataCount(), bs = this._bs;
        if (this._creates) {
            const k = this._creates.findIndex((row) => row[key] === id);
            if (k >= 0) return rows + k;
        }
        for (const [b, blk] of this._blocks) {
            if (!blk.rows) continue;
            for (let k = 0; k < blk.rows.length; k++) {
                const row = blk.rows[k];
                if (row && row[key] === id) {
                    const r = b * bs + k - base;
                    if (r >= 0 && r < rows) return r;
                }
            }
        }
        return -1;
    }

    /** The shown value of a row's field (sheet mode overlays unsaved edits). */
    _get(row, col) {
        return this._overlay ? this._overlay(row, col) : (row ? row[col.field] : undefined);
    }

    /** Make sure the blocks behind display rows first…last are loading. */
    _request(first, last, note) {
        if (!this._source || typeof this._source.load !== "function") return;
        const bs = this._bs, base = this._base();
        const want = new Set();
        if (last >= first) {
            const b0 = Math.floor((base + first) / bs), b1 = Math.floor((base + last) / bs);
            for (let b = b0; b <= b1; b++) want.add(b);
        }
        if (note && note.kind === "loading" && note.block != null) want.add(note.block);
        for (const b of want) this._load(b);
        // A block nobody looks at any more is not worth waiting for.
        for (const [b, blk] of this._blocks) {
            if (blk.ctrl && !want.has(b) && !want.has(b - 1) && !want.has(b + 1) && !blk.pinned) {
                blk.ctrl.abort();
                this._blocks.delete(b);
            }
        }
    }

    _load(b, pinned) {
        const existing = this._blocks.get(b);
        if (existing) {
            if (pinned) existing.pinned = true;
            return existing.promise;
        }
        const bs = this._bs;
        if (b < 0 || (this._total != null && b * bs >= this._total && !(this._total === 0 && b === 0))) return null;
        const ctrl = new AbortController();
        const q = this._query;
        const blk = { rows: null, error: null, ctrl, pinned: !!pinned, promise: null };
        this._blocks.set(b, blk);
        const offset = b * bs;
        // Footer totals cover the whole result: ask once per query.
        const aggregate = this._aggAsked === this._query ? []
            : this._all.filter((c) => c.aggregate).map((c) => ({ field: c.field, fn: c.aggregate }));
        if (aggregate.length) this._aggAsked = q;
        blk.promise = (async () => {
            try {
                const res = await this._source.load({
                    offset,
                    limit: bs,
                    sort: clone(this._sort),
                    filter: clone(this._filter),
                    signal: ctrl.signal,
                    aggregate,
                });
                if (q !== this._query || this._blocks.get(b) !== blk) return;
                const rows = res && Array.isArray(res.rows) ? res.rows : [];
                blk.rows = rows;
                blk.ctrl = null;
                if (res && typeof res.total === "number" && res.total >= 0) this._total = Math.floor(res.total);
                else if (rows.length < bs) this._total = offset + rows.length;
                if (this._total == null) this._known = Math.max(this._known, offset + rows.length);
                if (res && res.aggregates && typeof res.aggregates === "object") this._aggs = res.aggregates;
                this._stamp++;
                this._stats = null;
                this._clampSelection();
                if (String(Math.max(1, this._base() + this._rowCount())).length !== this._rhDigits) this._layout();
                this._scheduleRender();
            } catch (err) {
                // The totals were asked of this load: the next one asks again.
                // Before the checks below: _request drops an aborted block
                // from the map first.
                if (aggregate.length && this._aggAsked === q) this._aggAsked = -1;
                if (q !== this._query || this._blocks.get(b) !== blk) return;
                if (ctrl.signal.aborted || (err && err.name === "AbortError")) {
                    this._blocks.delete(b);
                    return;
                }
                blk.error = err || new Error("load failed");
                blk.ctrl = null;
                this._stamp++;
                this._scheduleRender();
                this._announce(t("data-grid.load-failed", "Couldn't load rows."));
                this.dispatchEvent(new CustomEvent("sac:load-error", {
                    detail: { error: blk.error, offset, limit: bs }, bubbles: true, composed: true,
                }));
            }
        })();
        return blk.promise;
    }

    _retry(b) {
        const blk = this._blocks.get(b);
        if (blk && blk.error) this._blocks.delete(b);
        this._stamp++;
        this._scheduleRender();
    }

    /** Resolves when nothing is loading any more. */
    async _settled() {
        for (let i = 0; i < 50; i++) {
            await new Promise((r) => requestAnimationFrame(r));
            const pending = [...this._blocks.values()].filter((b) => b.ctrl).map((b) => b.promise);
            if (!pending.length && !this._raf) return;
            await Promise.all(pending);
        }
    }

    _clampSelection() {
        const rows = this._navRows(), cols = this._cols.length;
        const fix = (p) => ({ r: clamp(p.r, 0, Math.max(0, rows - 1)), c: clamp(p.c, 0, Math.max(0, cols - 1)) });
        this._cur = fix(this._cur);
        this._anchor = fix(this._anchor);
        this._end = fix(this._end);
    }

    _allLoaded() {
        if (this._total == null) return false;
        const base = this._base(), rows = this._dataCount(), bs = this._bs;
        if (!rows) return true;
        for (let b = Math.floor(base / bs); b <= Math.floor((base + rows - 1) / bs); b++) {
            const blk = this._blocks.get(b);
            if (!blk || !blk.rows) return false;
        }
        return true;
    }

    /* ----------------------------------------------------------- render -- */

    _metrics() {
        const coarse = this._coarse && this._coarse.matches;
        this._rowH = coarse ? ROW_H_COARSE : ROW_H;
        this._headH = coarse ? HEAD_H_COARSE : HEAD_H;
        this._footH = this._foot && !this._foot.hidden ? this._rowH : 0;
        if (this._canvas) {
            this._canvas.style.setProperty("--row-h", this._rowH + "px");
            this._canvas.style.setProperty("--head-h", this._headH + "px");
        }
        this._stamp++;
        this._scheduleRender();
    }

    _measure() {
        if (!this._scroller) return;
        this._vw = this._scroller.clientWidth;
        this._vh = this._scroller.clientHeight;
        this._st = this._scroller.scrollTop;
        this._sl = this._scroller.scrollLeft;
        this._canvas.style.setProperty("--vw", this._vw + "px");
        if (this._cols.length && !this._editor && this._frozenCount() !== this._nf) this._visible();
        this._scheduleRender();
    }

    _viewH() { return Math.max(this._rowH, this._vh - this._headH - this._footH); }

    _onScroll() {
        this._st = this._scroller.scrollTop;
        this._sl = this._scroller.scrollLeft;
        if (this._raf) { cancelAnimationFrame(this._raf); this._raf = 0; }
        this._renderNow();
    }

    _scheduleRender() {
        if (!this._raf && this._built) this._raf = requestAnimationFrame(() => this._renderNow());
    }

    _renderNow() {
        this._raf = 0;
        if (!this._built) return;
        this._hook();
        const rows = this._rowCount();
        const note = this._note(rows);
        this._nl = this._newLine ? this._newLine() : 0;
        const n = rows + this._nl + (note ? 1 : 0);
        this._scroller.classList.toggle("empty", !rows && !this._nl);
        const h = n * this._rowH;
        if (this._bodyH !== h) {
            this._bodyH = h;
            this._body.style.height = h + "px";
        }
        const rh = this._rowH;
        const first = clamp(Math.floor(this._st / rh) - OVERSCAN, 0, Math.max(0, n - 1));
        const last = Math.min(n - 1, Math.ceil((this._st + this._viewH()) / rh) + OVERSCAN);

        const keep = new Map();
        const free = [];
        const pinned = this._editor ? this._editor.r : -1;   // the row being edited stays put
        for (const row of this._pool) {
            if (((row._r >= first && row._r <= last) || (row._r === pinned && row._r >= 0)) && !keep.has(row._r)) keep.set(row._r, row);
            else free.push(row);
        }
        if (pinned >= 0 && keep.has(pinned) && (pinned < first || pinned > last)) {
            const row = keep.get(pinned);
            if (row._stamp !== this._stamp) this._fillRow(row, pinned, rows, note);
        }
        for (let r = first; r <= last; r++) {
            let row = keep.get(r);
            if (!row) {
                row = free.pop() || this._newRow();
                this._fillRow(row, r, rows, note);
            } else if (row._stamp !== this._stamp) {
                this._fillRow(row, r, rows, note);
            }
        }
        for (const row of free) {
            if (row._r !== -1) { row._r = -1; row.hidden = true; }
        }
        // The note's block (the next page of an open-ended result) loads only
        // once the note scrolls into view: that is incremental loading.
        const noteAt = rows + this._nl;
        const noteSeen = note && noteAt >= first && noteAt <= last;
        this._request(first, Math.min(last, this._dataCount() - 1), noteSeen ? note : null);
        this._paintSelection();
        this._renderFoot();
        this._renderStatus();
        const total = this._total != null ? (this._paging() === "pages" ? this._total : rows) : -1;
        this._attr(this._scroller, "aria-rowcount", String(total < 0 ? -1 : total + 1 + (this._footH ? 1 : 0)));
        this._attr(this._scroller, "aria-readonly", String(!this._editable()));
    }

    /** setAttribute, skipped when nothing changes (no style invalidation). */
    _attr(node, name, value) {
        if (node.getAttribute(name) !== value) node.setAttribute(name, value);
    }

    _newRow() {
        const row = el("div", "row");
        row.setAttribute("role", "row");
        row._r = -1;
        const rh = el("div", "rh");
        rh.setAttribute("role", "rowheader");
        rh.setAttribute("aria-colindex", "1");
        row.appendChild(rh);
        row._rh = rh;
        const slot = this._pool.length;
        row._cells = this._cols.map((col, c) => {
            const cell = el("div", this._cellClass(c));
            cell.setAttribute("role", "gridcell");
            cell.setAttribute("aria-colindex", String(c + 2));
            cell.id = `${this._gid}-${slot}-${c}`;
            cell._c = c;
            if (c < this._nf) cell.style.left = this._x[c] + "px";
            row.appendChild(cell);
            return cell;
        });
        const note = el("div", "note");
        note.setAttribute("role", "gridcell");
        row.appendChild(note);
        row._note = note;
        row.hidden = true;
        this._pool.push(row);
        this._body.appendChild(row);
        return row;
    }

    _fillRow(row, r, rows, note) {
        row._r = r;
        row._stamp = this._stamp;
        row.hidden = false;
        row.style.transform = `translateY(${r * this._rowH}px)`;
        const idx = this._base() + r;
        row.setAttribute("aria-rowindex", String(idx + 2));
        if (r >= rows) {
            if (this._nl && r === rows) this._fillNewLine(row);
            else this._fillNote(row, note);
            return;
        }
        row.classList.remove("msg", "error", "newline");
        row._rh.textContent = this._canvas.dataset.rh === "numbers" ? String(idx + 1) : "";
        const data = this._rowAt(r);
        if (data === undefined) {
            const b = Math.floor(idx / this._bs);
            const blk = this._blocks.get(b);
            if (blk && blk.error && idx === Math.max(b * this._bs, this._base())) {
                this._fillNote(row, { kind: "error", block: b });
                return;
            }
            row.classList.add("skel");
            row.classList.remove("deleted", "created");
            for (const cell of row._cells) {
                if (cell._text !== "") { cell.textContent = ""; cell._text = ""; }
                cell.classList.remove("dirty", "invalid", "ro");
                this._cellError(cell, "");
            }
            row._rh.classList.remove("err");
            return;
        }
        row.classList.remove("skel");
        const editable = this._editable();
        const view = this._view ? this._view(data) : data;    // with unsaved edits, for computed columns
        const editing = this._editor && this._editor.r === r ? this._editor.c : -1;
        let invalid = false;
        for (let c = 0; c < this._cols.length; c++) {
            const col = this._cols[c], cell = row._cells[c];
            if (c === editing) continue;                        // the editor owns that cell
            const v = this._get(data, col);
            if (col.render) {
                col.render(cell, v, col, view);
                cell._text = null;
            } else {
                const s = col.text(v, view);
                if (cell._text !== s) { cell.textContent = s; cell._text = s; }
            }
            const ro = !editable || col.readonly;
            cell.classList.toggle("ro", ro && editable);
            if (editable) this._attr(cell, "aria-readonly", String(ro));
            else cell.removeAttribute("aria-readonly");
            const state = this._cellState ? this._cellState(data, col) : null;
            cell.classList.toggle("dirty", !!(state && state.dirty));
            cell.classList.toggle("invalid", !!(state && state.error));
            this._cellError(cell, (state && state.error) || "");
            if (state && state.error) invalid = true;
        }
        row._rh.classList.toggle("err", this._rowHasError ? this._rowHasError(data) : invalid);
        if (this._paintRowState) this._paintRowState(row, data);
    }

    /** A cell's validation message: the bubble's text, and its accessible description. */
    _cellError(cell, msg) {
        if (cell._err === msg) return;
        cell._err = msg;
        if (msg) {
            cell.setAttribute("aria-invalid", "true");
            cell.setAttribute("aria-description", msg);
        } else {
            cell.removeAttribute("aria-invalid");
            cell.removeAttribute("aria-description");
        }
    }

    _fillNote(row, note) {
        row.classList.remove("skel", "newline", "deleted", "created");
        row.classList.add("msg");
        row.classList.toggle("error", note.kind === "error");
        const box = row._note;
        box.textContent = "";
        if (note.kind === "loading") {
            const sp = document.createElement("sac-spinner");
            sp.setAttribute("label", t("data-grid.loading", "Loading…"));
            box.append(sp, el("span", "", t("data-grid.loading", "Loading…")));
        } else if (note.kind === "error") {
            const btn = el("button", "btn retry");
            btn.type = "button";
            btn.append(icon("sync"), document.createTextNode(t("data-grid.retry", "Retry")));
            btn._block = note.block;
            box.append(el("span", "", t("data-grid.load-failed", "Couldn't load rows.")), btn);
        } else {
            box.append(el("span", "", t("data-grid.empty", "No rows")));
        }
    }

    /** Selection classes on the rendered cells, the headers and the status line. */
    _paintSelection() {
        if (!this._built) return;
        const s = this._rect();
        const single = s.r1 === s.r2 && s.c1 === s.c2;
        let active = null;
        for (const row of this._pool) {
            const r = row._r;
            if (r < 0 || row.classList.contains("msg")) continue;
            const inRows = r >= s.r1 && r <= s.r2;
            const full = inRows && s.c1 === 0 && s.c2 === this._cols.length - 1;
            if (row._rhSel !== full) { row._rhSel = full; row._rh.classList.toggle("sel", full); }
            for (let c = 0; c < row._cells.length; c++) {
                const cell = row._cells[c];
                const inRange = inRows && c >= s.c1 && c <= s.c2;
                const cur = r === this._cur.r && c === this._cur.c;
                const tint = inRange && !(cur && !single) && !single;
                if (cell._sel !== inRange) {
                    cell._sel = inRange;
                    cell.setAttribute("aria-selected", inRange ? "true" : "false");
                }
                if (cell._tint !== tint) { cell._tint = tint; cell.classList.toggle("sel", tint); }
                if (cell._cur !== cur) { cell._cur = cur; cell.classList.toggle("cur", cur); }
                if (cur) active = cell;
            }
        }
        if (this._hcells) {
            this._hcells.forEach((h, c) => h.classList.toggle("sel", c >= s.c1 && c <= s.c2 && this._rowCount() > 0));
        }
        if (active) this._scroller.setAttribute("aria-activedescendant", active.id);
        else this._scroller.removeAttribute("aria-activedescendant");
        this._activeCell = active;
        this._syncTip();
    }

    /* ------------------------------------------------------ error bubble -- */

    /**
     * The validation bubble shows on the hovered invalid cell, else on the
     * cursor cell while the grid itself has focus (keyboard users get no
     * hover). `force` shows it again after the bubble hid itself (scroll,
     * Escape, the pointer leaving); without it an unchanged target stays
     * as it is, so a render while scrolling does not bring it back.
     */
    _syncTip(force) {
        const tip = this._tip;
        if (!tip || typeof tip.attachTo !== "function") return;
        const hover = this._tipHover;
        const cur = this._activeCell;
        let cell = hover && hover.isConnected && hover._err ? hover : null;
        if (!cell && cur && cur._err && !this._editor && this.shadowRoot.activeElement === this._scroller) cell = cur;
        if (!cell) {
            // Emptied, not only hidden: the bubble listens to its anchor
            // itself, and an editor in that cell would bring it back.
            if (this._tipCell) { this._tipCell = null; tip.removeAttribute("content"); tip.hide(); }
            return;
        }
        if (!force && cell === this._tipCell && tip.getAttribute("content") === cell._err) return;
        this._tipCell = cell;
        tip.attachTo(cell);
        tip.setAttribute("content", cell._err);
        tip.show();
    }

    _onBodyPointerOver(e) {
        if (e.pointerType === "touch") return;
        const cell = e.target.closest && e.target.closest(".cell");
        if (!cell || cell === this._tipHover) return;
        clearTimeout(this._tipTimer);
        this._tipHover = null;
        if (!cell._err) return;
        this._tipTimer = setTimeout(() => {
            this._tipHover = cell;
            this._syncTip(true);
        }, 400);
    }

    _onBodyPointerOut(e) {
        if (e.pointerType === "touch") return;
        const cell = e.target.closest && e.target.closest(".cell");
        if (!cell || (e.relatedTarget && cell.contains(e.relatedTarget))) return;
        clearTimeout(this._tipTimer);
        const was = this._tipHover;
        this._tipHover = null;
        if (was) this._syncTip(true);
    }

    _renderFoot() {
        if (!this._fcells || !this._fcells.length || this._footStamp === this._stamp) return;
        this._footStamp = this._stamp;
        const all = this._allLoaded();
        this._cols.forEach((col, c) => {
            const f = this._fcells[c];
            if (!col.aggregate) { if (f._t !== "") { f.textContent = ""; f._t = ""; } return; }
            const v = this._aggregate(col, all);
            const label = t(`data-grid.agg-${col.aggregate}`, { sum: "Sum", avg: "Avg", count: "Count", min: "Min", max: "Max" }[col.aggregate]);
            const text = v == null ? "—" : (col.aggregate === "count" ? int(v) : this._aggText(col, v));
            const key = label + "|" + text;
            if (f._t === key) return;
            f._t = key;
            f.textContent = "";
            f.append(el("span", "agg-l", label), el("span", "agg-v", text));
            f.title = `${label}: ${text}`;
            f.setAttribute("aria-label", `${label}: ${text}`);
        });
    }

    _aggText(col, v) {
        if (typeof v !== "number") return col.text(v, null);
        if (col.typeName !== "number") return int(v);
        if (col.aggregate === "avg" && window.sac && sac.regional) {
            // An average needs more digits than the values it averages.
            const d = col.decimals != null ? +col.decimals : 0;
            return sac.regional.formatNumber(v, { minFractionDigits: d, maxFractionDigits: d + 2 });
        }
        return col.text(v, null);
    }

    /** A column's footer value: the source's, adjusted for unsaved edits where
     *  that is exact; else computed once every row is loaded. */
    _aggregate(col, allLoaded) {
        const fn = col.aggregate;
        if (this._aggs && Object.prototype.hasOwnProperty.call(this._aggs, col.field)) {
            const v = this._aggs[col.field];
            return this._adjustAgg ? this._adjustAgg(col, fn, v) : v;
        }
        if (!allLoaded) return null;
        const vals = [];
        const rows = this._rowCount();
        for (let r = 0; r < rows; r++) {
            const row = this._rowAt(r);
            if (!row || (this._isDeleted && this._isDeleted(row))) continue;
            const v = this._get(row, col);
            if (!col.type.empty(v)) vals.push(v);
        }
        return computeAggregate(fn, vals);
    }

    _renderStatus() {
        const rows = this._rowCount();
        let count = "";
        if (this._source) {
            if (this._paging() === "pages") {
                count = this._total != null
                    ? t(this._total === 1 ? "data-grid.rows-one" : "data-grid.rows", this._total === 1 ? "1 row" : "{n} rows", { n: int(this._total) })
                    : t("data-grid.rows", "{n} rows", { n: int(rows) });
            } else if (this._total != null) {
                count = t(rows === 1 ? "data-grid.rows-one" : "data-grid.rows", rows === 1 ? "1 row" : "{n} rows", { n: int(rows) });
            } else if (rows) {
                count = t("data-grid.rows-more", "{n}+ rows", { n: int(rows) });
            }
        }
        if (this._countEl._t !== count) { this._countEl._t = count; this._countEl.textContent = count; }
        this._renderSelInfo();
        this._renderPager();
        this._renderModes();
        if (this._renderExtra) this._renderExtra();
    }

    /** The optional mode switch (mode-toggle attribute): Read · Sheet · Form. */
    _renderModes() {
        const box = this._modesEl;
        const show = this.hasAttribute("mode-toggle");
        box.hidden = !show;
        if (!show) return;
        const labels = {
            read: t("data-grid.mode-read", "Read"),
            sheet: t("data-grid.mode-sheet", "Sheet"),
            form: t("data-grid.mode-form", "Form"),
        };
        const key = `${this.mode}|${labels.read}|${labels.sheet}|${labels.form}`;
        if (box._t === key) return;
        box._t = key;
        let seg = box.firstChild;
        if (!seg) {
            seg = document.createElement("sac-segmented-control");
            seg.addEventListener("sac:change", (e) => { this.mode = e.detail.value; });
            box.appendChild(seg);
        }
        seg.replaceChildren(...MODES.map((m) => {
            const b = el("button", "", labels[m]);
            b.type = "button";
            b.dataset.value = m;
            return b;
        }));
        seg.setAttribute("aria-label", t("data-grid.mode", "Editing mode"));
        seg.value = this.mode;
    }

    _renderSelInfo() {
        const s = this._rect();
        let text = "";
        if (!(s.r1 === s.r2 && s.c1 === s.c2) && this._rowCount() > 0) {
            const st = this._selStats(s);
            const parts = [t("data-grid.count", "Count: {n}", { n: int(st.count) })];
            if (st.numeric) {
                parts.push(t("data-grid.sum", "Sum: {n}", { n: this._statNum(st.sum) }));
                parts.push(t("data-grid.avg", "Avg: {n}", { n: this._statNum(st.sum / st.numeric) }));
            }
            if (st.partial) parts.push(t("data-grid.partial", "(loaded rows)"));
            text = parts.join(" · ");
        }
        if (this._selEl._t !== text) { this._selEl._t = text; this._selEl.textContent = text; }
    }

    _statNum(n) {
        if (!Number.isFinite(n)) return "—";
        return (window.sac && sac.regional) ? sac.regional.formatNumber(Math.round(n * 1e6) / 1e6, { maxFractionDigits: 6 }) : String(n);
    }

    /** Count (non-empty) / sum / numeric count over the range's loaded cells. */
    _selStats(s) {
        const key = `${this._stamp}|${s.r1}|${s.r2}|${s.c1}|${s.c2}`;
        if (this._stats && this._stats.key === key) return this._stats;
        const st = { key, count: 0, numeric: 0, sum: 0, partial: false };
        const cells = (s.r2 - s.r1 + 1) * (s.c2 - s.c1 + 1);
        if (cells > STATS_CELL_CAP) { st.partial = true; this._stats = st; return st; }
        const cols = this._cols.slice(s.c1, s.c2 + 1);
        const missing = new Set();
        for (let r = s.r1; r <= s.r2; r++) {
            const row = this._rowAt(r);
            if (row === undefined) {
                st.partial = true;
                missing.add(Math.floor((this._base() + r) / this._bs));
                continue;
            }
            for (const col of cols) {
                const v = this._get(row, col);
                if (col.type.empty(v) || (col.typeName === "bool" && !v)) continue;
                st.count++;
                if (typeof v === "number" && Number.isFinite(v) && col.typeName !== "bool") { st.numeric++; st.sum += v; }
            }
        }
        this._stats = st;
        // Fill the gaps when that is cheap (a local source loads big blocks).
        if (missing.size && missing.size <= STATS_BLOCK_CAP) {
            for (const b of missing) this._load(b, true);
        }
        return st;
    }

    _renderPager() {
        const pages = this._paging() === "pages" && !!this._source;
        this._pager.hidden = !pages;
        if (!pages) return;
        const size = this._pageSize();
        const last = this._total != null ? Math.max(0, Math.ceil(this._total / size) - 1) : null;
        const blk = this._blocks.get(this._page);
        const more = last != null ? this._page < last : !!(blk && blk.rows && blk.rows.length >= size);
        const label = last != null
            ? t("data-grid.page-of", "Page {n} of {m}", { n: int(this._page + 1), m: int(last + 1) })
            : t("data-grid.page", "Page {n}", { n: int(this._page + 1) });
        const key = `${label}|${more}|${this._page}|${last}`;
        if (this._pager._t === key) return;
        this._pager._t = key;
        this._pager.setAttribute("aria-label", t("data-grid.pages", "Pages"));
        const btn = (act, name, text, disabled) => {
            const b = el("button");
            b.type = "button";
            b.dataset.act = act;
            b.disabled = disabled;
            b.setAttribute("aria-label", text);
            b.title = text;
            b.appendChild(icon(name));
            return b;
        };
        this._pager.replaceChildren(
            btn("first", "chevron-double-left", t("data-grid.first-page", "First page"), this._page === 0),
            btn("prev", "chevron-left", t("data-grid.prev-page", "Previous page"), this._page === 0),
            el("span", "pg", label),
            btn("next", "chevron-right", t("data-grid.next-page", "Next page"), !more),
            btn("last", "chevron-double-right", t("data-grid.last-page", "Last page"), last == null || this._page >= last),
        );
    }

    _onPagerClick(e) {
        const b = e.target.closest("button");
        if (!b || b.disabled) return;
        const size = this._pageSize();
        const last = this._total != null ? Math.max(0, Math.ceil(this._total / size) - 1) : Infinity;
        const to = { first: 0, prev: this._page - 1, next: this._page + 1, last }[b.dataset.act];
        this._goPage(to);
    }

    _goPage(p) {
        if (this._paging() !== "pages" || !Number.isFinite(p)) return;
        const size = this._pageSize();
        const last = this._total != null ? Math.max(0, Math.ceil(this._total / size) - 1) : null;
        p = Math.max(0, last != null ? Math.min(last, p) : p);
        if (p === this._page) return;
        if (last == null && p > this._page) {
            const blk = this._blocks.get(this._page);
            if (!blk || !blk.rows || blk.rows.length < size) return;
        }
        if (this._leavePage && this._leavePage() === false) return;
        this._page = p;
        this._resetSelection();
        this._scrollTo(0, this._sl);
        this._stamp++;
        this._layout();
        this._scheduleRender();
    }

    _relabel() {
        if (!this._built) return;
        this._scroller.setAttribute("aria-label", this.getAttribute("label") || t("data-grid.label", "Data grid"));
        this._paintHead();
        for (const f of this._fcells || []) f._t = null;
        this._countEl._t = null;
        this._selEl._t = null;
        this._pager._t = null;
        this._modesEl._t = null;
        this._stamp++;
        this._scheduleRender();
        if (this._onRelabel) this._onRelabel();
    }

    _refresh() {
        if (!this._built) return;
        for (const f of this._fcells || []) f._t = null;
        this._selEl._t = null;
        this._stats = null;
        this._stamp++;
        this._scheduleRender();
    }

    _announce(text) {
        this._live.textContent = "";
        requestAnimationFrame(() => { this._live.textContent = text; });
    }

    /* -------------------------------------------------------- selection -- */

    _rect() {
        const a = this._anchor, b = this._end;
        return {
            r1: Math.min(a.r, b.r), r2: Math.max(a.r, b.r),
            c1: Math.min(a.c, b.c), c2: Math.max(a.c, b.c),
        };
    }

    /** Move the active cell (and collapse the range), or with extend move the
     *  range's far corner. */
    _setCursor(r, c, extend) {
        const rows = this._navRows(), cols = this._cols.length;
        if (!rows || !cols) return;
        r = clamp(r, 0, rows - 1);
        c = clamp(c, 0, cols - 1);
        if (extend) {
            this._end = { r, c };
            this._reveal(this._end);
        } else {
            if (this._leaveRow && r !== this._cur.r) this._leaveRow(this._cur.r);
            this._cur = { r, c };
            this._anchor = { r, c };
            this._end = { r, c };
            this._reveal(this._cur);
        }
        this._selChanged();
    }

    _select(anchor, end, reveal) {
        this._anchor = anchor;
        this._end = end;
        if (reveal) this._reveal(end);
        this._selChanged();
    }

    _selectAll() {
        const rows = this._rowCount(), cols = this._cols.length;
        if (!rows || !cols) return;
        this._select({ r: 0, c: 0 }, { r: rows - 1, c: cols - 1 }, false);
    }

    _selectRows(r1, r2) {
        const cols = this._cols.length;
        this._select({ r: r1, c: 0 }, { r: r2, c: cols - 1 }, true);
    }

    _selectCols(c1, c2) {
        const rows = this._rowCount();
        if (!rows) return;
        this._select({ r: 0, c: c1 }, { r: rows - 1, c: c2 }, false);
        this._reveal({ r: this._cur.r, c: c2 });
    }

    _selChanged() {
        this._paintSelection();
        this._renderSelInfo();
        const s = this._rect();
        const row = this._rowAt(this._cur.r);
        const col = this._cols[this._cur.c];
        const key = `${this._cur.r}|${this._cur.c}|${s.r1}|${s.r2}|${s.c1}|${s.c2}`;
        if (key === this._lastSel) return;
        this._lastSel = key;
        this.dispatchEvent(new CustomEvent("sac:selection", {
            detail: {
                cursor: { index: this._base() + this._cur.r, id: this._idOf(row), field: col ? col.field : null },
                range: { top: this._base() + s.r1, bottom: this._base() + s.r2, left: s.c1, right: s.c2 },
                fields: this._cols.slice(s.c1, s.c2 + 1).map((c) => c.field),
                count: (s.r2 - s.r1 + 1) * (s.c2 - s.c1 + 1),
            },
            bubbles: true, composed: true,
        }));
    }

    /** Scroll so display cell p is in view. Cached geometry only. */
    _reveal(p) {
        const rh = this._rowH, vh = this._viewH();
        const top = p.r * rh;
        let st = this._st;
        if (top < st) st = top;
        else if (top + rh > st + vh) st = top + rh - vh;
        let sl = this._sl;
        if (p.c >= this._nf && this._x.length) {
            const x = this._x[p.c], w = this._w[p.c];
            if (x - this._frozenW < sl) sl = x - this._frozenW;
            else if (x + w > sl + this._vw) sl = Math.min(x - this._frozenW, x + w - this._vw);
        }
        this._scrollTo(st, sl);
    }

    _scrollTo(st, sl) {
        st = Math.max(0, Math.round(st));
        sl = Math.max(0, Math.round(sl));
        if (st !== this._st) { this._scroller.scrollTop = st; this._st = st; }
        if (sl !== this._sl) { this._scroller.scrollLeft = sl; this._sl = sl; }
        this._scheduleRender();
    }

    /** Ctrl+arrow: Excel's data edge. Unloaded rows end the search at the edge. */
    _edge(from, dr, dc) {
        const rows = this._rowCount(), cols = this._cols.length;
        const edge = { r: dr > 0 ? rows - 1 : dr < 0 ? 0 : from.r, c: dc > 0 ? cols - 1 : dc < 0 ? 0 : from.c };
        const inside = (r, c) => r >= 0 && r < rows && c >= 0 && c < cols;
        const empty = (r, c) => {
            const row = this._rowAt(r);
            if (row === undefined) return null;
            const col = this._cols[c];
            const v = this._get(row, col);
            return col.type.empty(v);
        };
        let r = from.r + dr, c = from.c + dc;
        if (!inside(r, c)) return from;
        const here = empty(from.r, from.c), next = empty(r, c);
        if (here === null || next === null) return edge;
        if (!here && !next) {
            while (inside(r + dr, c + dc)) {
                const e = empty(r + dr, c + dc);
                if (e === null) return edge;
                if (e) break;
                r += dr; c += dc;
            }
            return { r, c };
        }
        while (inside(r, c)) {
            const e = empty(r, c);
            if (e === null) return edge;
            if (!e) return { r, c };
            r += dr; c += dc;
        }
        return edge;
    }

    /* --------------------------------------------------------- keyboard -- */

    _onKeydown(e) {
        if (e.defaultPrevented) return;
        if (this._editor) { if (this._onEditKeydown) this._onEditKeydown(e); return; }
        if (e.target !== this._scroller) return;               // a button or field inside the grid
        const cols = this._cols.length, rows = this._navRows();
        if (!cols) return;
        const mod = e.ctrlKey || e.metaKey;
        const shift = e.shiftKey;
        const k = e.key;
        const from = shift ? this._end : this._cur;
        let handled = true;
        switch (k) {
            case "ArrowUp":
            case "ArrowDown":
            case "ArrowLeft":
            case "ArrowRight": {
                if (e.altKey) {
                    if (k === "ArrowDown") this._openColumnMenu(this._cur.c);
                    else handled = false;
                    break;
                }
                if (!rows) break;
                const dr = k === "ArrowUp" ? -1 : k === "ArrowDown" ? 1 : 0;
                const dc = k === "ArrowLeft" ? -1 : k === "ArrowRight" ? 1 : 0;
                const to = mod ? this._edge(from, dr, dc) : { r: from.r + dr, c: from.c + dc };
                this._setCursor(to.r, to.c, shift);
                break;
            }
            case "Home":
                this._setCursor(mod ? 0 : from.r, 0, shift);
                break;
            case "End":
                // Ctrl+End: the last row with data, not the "new row" line.
                this._setCursor(mod ? Math.max(0, this._rowCount() - 1) : from.r, cols - 1, shift);
                break;
            case "PageUp":
            case "PageDown": {
                // Ctrl+PgUp / PgDn switch browser tabs (a page never sees them).
                if (e.altKey) { this._goPage(this._page + (k === "PageDown" ? 1 : -1)); break; }
                const n = Math.max(1, Math.floor(this._viewH() / this._rowH) - 1);
                this._setCursor(from.r + (k === "PageDown" ? n : -n), from.c, shift);
                break;
            }
            case "Tab": {
                let { r, c } = this._cur;
                c += shift ? -1 : 1;
                if (c >= cols) { c = 0; r++; } else if (c < 0) { c = cols - 1; r--; }
                if (r < 0 || r >= rows) { handled = false; break; }   // past the ends: leave the grid
                this._setCursor(r, c, false);
                break;
            }
            case "Escape": {
                const s = this._rect();
                if (s.r1 === s.r2 && s.c1 === s.c2) { handled = false; break; }
                this._select({ ...this._cur }, { ...this._cur }, true);
                break;
            }
            case " ":
                if (shift && !mod) { if (rows) this._selectRows(this._cur.r, this._cur.r); }
                else if (mod && !shift) this._selectCols(this._cur.c, this._cur.c);
                else if (this._onSpace) this._onSpace(e);          // never scrolls the grid
                break;
            case "F10":
                if (shift) this._openRowMenu(null);
                else handled = false;
                break;
            case "ContextMenu":
                this._openRowMenu(null);
                break;
            default:
                if (mod && !e.altKey && (k === "a" || k === "A")) { this._selectAll(); break; }
                handled = this._onOtherKey ? this._onOtherKey(e) : false;
        }
        if (handled) {
            e.preventDefault();
            e.stopPropagation();
        }
    }

    /* ------------------------------------------------------------ mouse -- */

    /** Display cell under a viewport point, from cached geometry. */
    _hit(clientX, clientY) {
        const rect = this._scrollerRect || this._scroller.getBoundingClientRect();
        const y = clientY - rect.top - this._headH + this._st;
        const r = Math.floor(y / this._rowH);
        const vx = clientX - rect.left;
        const x = vx < this._frozenW ? vx : vx + this._sl;
        let c = this._cols.length - 1;
        for (let i = 0; i < this._x.length; i++) {
            if (x < this._x[i] + this._w[i]) { c = i; break; }
        }
        return { r, c: Math.max(0, c), inRowHeader: x < this._rhW };
    }

    _rowOf(target) {
        const row = target.closest && target.closest(".row");
        return row && row._r >= 0 && !row.classList.contains("msg") ? row : null;
    }

    _onBodyPointerDown(e) {
        this._downAt = null;
        if (e.button !== 0 && e.button !== 2) return;
        if (this._editor) {
            if (this._inEditor && this._inEditor(e)) return;      // clicks inside the editor are its own
            this._commitEdit(null);
        }
        if (e.target.closest("button")) return;
        const row = this._rowOf(e.target);
        if (!row) return;
        const r = row._r;
        const rh = e.target.closest(".rh");
        const cell = e.target.closest(".cell");
        // A tap on the cell that was already active (phones: open the record).
        // Remembered here: pointer capture sends the click to the body.
        this._tapActive = !!cell && !e.shiftKey && r === this._cur.r && cell._c === this._cur.c;
        this._tapCell = cell ? { r, c: cell._c } : null;
        this._downAt = { r, c: cell ? cell._c : null };
        e.preventDefault();
        this._scroller.focus({ preventScroll: true });
        if (e.button === 2) {
            const s = this._rect();
            const c = cell ? cell._c : this._cur.c;
            if (r < s.r1 || r > s.r2 || (cell && (c < s.c1 || c > s.c2))) this._setCursor(r, c, false);
            return;
        }
        if (rh) {
            if (e.shiftKey) this._select({ r: this._anchor.r, c: 0 }, { r, c: this._cols.length - 1 }, false);
            else {
                this._setCursor(r, this._cur.c, false);
                this._selectRows(r, r);
            }
            this._drag = "rows";
        } else if (cell) {
            if (this._onCellPointerDown && this._onCellPointerDown(e, r, cell._c)) return;
            if (e.shiftKey) this._setCursor(r, cell._c, true);
            else this._setCursor(r, cell._c, false);
            this._drag = "cells";
        } else return;
        if (e.pointerType === "touch") { this._drag = null; return; }   // a finger scrolls; it does not paint ranges
        this._startDrag(e);
    }

    _startDrag(e) {
        this._scrollerRect = this._scroller.getBoundingClientRect();
        this._dragPt = { x: e.clientX, y: e.clientY };
        const move = (ev) => {
            this._dragPt = { x: ev.clientX, y: ev.clientY };
            this._dragTo();
        };
        const up = () => {
            this._body.removeEventListener("pointermove", move);
            this._body.removeEventListener("pointerup", up);
            this._body.removeEventListener("pointercancel", up);
            this._drag = null;
            this._scrollerRect = null;
            if (this._autoRaf) { cancelAnimationFrame(this._autoRaf); this._autoRaf = 0; }
        };
        try { this._body.setPointerCapture(e.pointerId); } catch (err) { /* synthetic events */ }
        this._body.addEventListener("pointermove", move);
        this._body.addEventListener("pointerup", up);
        this._body.addEventListener("pointercancel", up);
    }

    _dragTo() {
        if (!this._drag) return;
        const rect = this._scrollerRect;
        const p = this._dragPt;
        const h = this._hit(p.x, p.y);
        const rows = this._navRows();
        const r = clamp(h.r, 0, rows - 1);
        if (this._drag === "rows") this._select({ r: this._anchor.r, c: 0 }, { r, c: this._cols.length - 1 }, true);
        else this._setCursor(r, h.c, true);
        // Auto-scroll while the pointer is past an edge.
        const top = rect.top + this._headH, bottom = rect.top + this._headH + this._viewH();
        const left = rect.left + this._frozenW, right = rect.left + this._vw;
        const dy = p.y < top ? p.y - top : p.y > bottom ? p.y - bottom : 0;
        const dx = this._drag === "cells" ? (p.x < left ? p.x - left : p.x > right ? p.x - right : 0) : 0;
        if ((dy || dx) && !this._autoRaf) {
            this._autoRaf = requestAnimationFrame(() => {
                this._autoRaf = 0;
                if (!this._drag) return;
                this._scrollTo(this._st + clamp(dy, -60, 60), this._sl + clamp(dx, -60, 60));
                this._dragTo();
            });
        }
    }

    _onBodyClick(e) {
        const retry = e.target.closest("button.retry");
        if (retry) { this._retry(retry._block); return; }
        const tap = this._tapCell;
        this._tapCell = null;
        if (tap && this._onCellClick && tap.r === this._cur.r && tap.c === this._cur.c) {
            this._onCellClick(tap.r, tap.c, this._tapActive);
        }
    }

    _onBodyDblClick(e) {
        // Pointer capture sends the dblclick to the body, not the cell: use
        // where the second press landed (null: in an editor, on a button).
        const at = this._downAt;
        this._downAt = null;
        if (at && this._onCellDblClick) this._onCellDblClick(at.r, at.c, e);
    }

    _onBodyContextMenu(e) {
        if (this._inEditor && this._inEditor(e)) return;          // the editor's text: the browser's menu
        const row = this._rowOf(e.target);
        if (!row) return;
        e.preventDefault();
        this._openRowMenu({ clientX: e.clientX, clientY: e.clientY });
    }

    /* ----------------------------------------------------------- header -- */

    _hcellOf(target) {
        const h = target.closest && target.closest(".hcell");
        return h && h._c != null ? h : null;
    }

    _onHeadClick(e) {
        if (this._suppressClick) { this._suppressClick = false; return; }
        if (performance.now() - this._headPressAt < 1000) return;      // ends a long press
        if (e.target.closest(".corner")) {
            this._scroller.focus({ preventScroll: true });
            this._selectAll();
            return;
        }
        const h = this._hcellOf(e.target);
        if (!h || e.target.closest(".rs")) return;
        if (this._cols[h._c].sortable) this._toggleSort(this._cols[h._c].field, e.shiftKey);
    }

    _onHeadContextMenu(e) {
        const h = this._hcellOf(e.target);
        if (!h) return;
        e.preventDefault();
        const touch = !!this._headPress;
        this._cancelHeadPress();
        if (performance.now() - this._headPressAt < 1000) return;   // the long press opened it
        if (touch) this._headPressAt = performance.now();           // the click after it is no tap
        this._openColumnMenu(h._c, { clientX: e.clientX, clientY: e.clientY });
    }

    /** Touch: a long press on a header opens its menu (no right-click there). */
    _headLongPress(e) {
        const h = this._hcellOf(e.target);
        if (!h || e.target.closest(".rs")) return;
        this._cancelHeadPress();
        const x = e.clientX, y = e.clientY;
        const press = {
            timer: setTimeout(() => {
                this._cancelHeadPress();
                this._headPressAt = performance.now();
                this._openColumnMenu(h._c, { clientX: x, clientY: y });
            }, 500),
            move: (ev) => { if (Math.hypot(ev.clientX - x, ev.clientY - y) > 10) this._cancelHeadPress(); },
            end: () => this._cancelHeadPress(),
        };
        this._headPress = press;
        window.addEventListener("pointermove", press.move, true);
        window.addEventListener("pointerup", press.end, true);
        window.addEventListener("pointercancel", press.end, true);
    }

    _cancelHeadPress() {
        const press = this._headPress;
        if (!press) return;
        this._headPress = null;
        clearTimeout(press.timer);
        window.removeEventListener("pointermove", press.move, true);
        window.removeEventListener("pointerup", press.end, true);
        window.removeEventListener("pointercancel", press.end, true);
    }

    _onHeadPointerDown(e) {
        if (e.pointerType === "touch") this._headLongPress(e);
        const rs = e.target.closest(".rs");
        if (!rs || e.button !== 0) return;
        const h = this._hcellOf(rs);
        if (!h) return;
        e.preventDefault();
        e.stopPropagation();
        const col = this._cols[h._c];
        const startX = e.clientX, startW = this._widthOf(col);
        rs.classList.add("active");
        let moved = false;
        const move = (ev) => {
            const w = Math.max(col.minWidth, Math.round(startW + ev.clientX - startX));
            if (w !== this._widthOf(col)) {
                moved = true;
                this._widths[col.field] = w;
                this._layout();
            }
        };
        const up = () => {
            rs.classList.remove("active");
            rs.removeEventListener("pointermove", move);
            rs.removeEventListener("pointerup", up);
            rs.removeEventListener("pointercancel", up);
            if (moved) {
                this._suppressClick = true;
                setTimeout(() => { this._suppressClick = false; }, 0);
                this._emitView();
            }
        };
        try { rs.setPointerCapture(e.pointerId); } catch (err) { /* synthetic */ }
        rs.addEventListener("pointermove", move);
        rs.addEventListener("pointerup", up);
        rs.addEventListener("pointercancel", up);
    }

    _onHeadDblClick(e) {
        if (!e.target.closest(".rs")) return;
        const h = this._hcellOf(e.target);
        if (h) this._fitColumn(h._c);
    }

    _toggleSort(field, add) {
        const i = this._sort.findIndex((s) => s.field === field);
        let sort;
        if (add) {
            sort = this._sort.slice();
            if (i < 0) sort.push({ field, dir: "asc" });
            else if (sort[i].dir === "asc") sort[i] = { field, dir: "desc" };
            else sort.splice(i, 1);
        } else if (i >= 0 && this._sort.length === 1) {
            sort = this._sort[0].dir === "asc" ? [{ field, dir: "desc" }] : [];
        } else {
            sort = [{ field, dir: "asc" }];
        }
        this._setSort(sort);
    }

    _setSort(sort) {
        if (this._editor) this._commitEdit(null);          // before the row is saved and reloaded
        if (this._leaveRow) this._leaveRow(this._cur.r);
        this._sort = sort;
        this._paintHead();
        this._resetData();
        this._emitView();
    }

    _setFilter(field, desc) {
        const had = JSON.stringify(this._filter[field] || null);
        if (desc) this._filter[field] = desc;
        else delete this._filter[field];
        if (JSON.stringify(desc || null) === had) return;
        if (this._editor) this._commitEdit(null);
        if (this._leaveRow) this._leaveRow(this._cur.r);
        this._page = 0;
        this._paintHead();
        this._resetData();
        this._emitView();
    }

    /** Double-click on a resize edge: as wide as the widest loaded value. */
    _fitColumn(c) {
        const col = this._cols[c];
        if (!col) return;
        const ctx = (this._measureCtx = this._measureCtx || document.createElement("canvas").getContext("2d"));
        const cs = getComputedStyle(this._scroller);
        ctx.font = `${cs.fontSize} ${cs.fontFamily}`;
        let max = 0;
        let seen = 0;
        for (const blk of this._blocks.values()) {
            if (!blk.rows) continue;
            for (const row of blk.rows) {
                const s = col.text(this._get(row, col), row);
                if (s) max = Math.max(max, ctx.measureText(s).width);
                if (++seen > 20000) break;
            }
        }
        const h = this._hcells[c];
        const hs = getComputedStyle(h);
        ctx.font = `${hs.fontWeight} ${hs.fontSize} ${hs.fontFamily}`;
        const label = col.labelText().toUpperCase();
        const head = ctx.measureText(label).width + label.length * 0.08 * parseFloat(hs.fontSize)
            + parseFloat(hs.paddingLeft) + parseFloat(hs.paddingRight) + 2;
        const extra = col.typeName === "tags" ? 40 : col.typeName === "color" ? 24 : 0;
        this._widths[col.field] = Math.max(col.minWidth, Math.min(800, Math.ceil(Math.max(max + 18 + extra, head))));
        this._layout();
        this._emitView();
    }

    /* ------------------------------------------------------------ menus -- */

    _menuItem(action, iconName, text, run, opts) {
        const b = el("button");
        b.type = "button";
        b.dataset.action = action;
        if (iconName) b.appendChild(icon(iconName));
        b.appendChild(document.createTextNode(" " + text));
        if (opts && opts.danger) b.setAttribute("data-danger", "");
        if (opts && opts.disabled) b.disabled = true;
        this._menuActions[action] = run;
        return b;
    }

    _openMenu(items, point) {
        const menu = this._menu;
        if (this._editor) this._commitEdit(null);          // a header menu while editing
        // The menu hands focus back to whatever had it: make that the grid
        // (its one tab stop), not a header button that a click focused.
        if (this.shadowRoot.activeElement !== this._scroller) this._scroller.focus({ preventScroll: true });
        menu.replaceChildren(...items.filter(Boolean));
        if (typeof menu.openAt === "function") menu.openAt(point);
    }

    _pointUnder(elm) {
        const r = elm.getBoundingClientRect();
        return { clientX: r.left, clientY: r.bottom };
    }

    _openColumnMenu(c, point) {
        const col = this._cols[c];
        if (!col) return;
        this._menuActions = {};
        const sorted = this._sort.find((s) => s.field === col.field);
        const hidden = this._all.filter((x) => this._hidden.has(x.field));
        const f = col.field;
        // A column that takes no sort (or filter) is not offered one.
        const items = [
            ...(col.sortable ? [
                this._menuItem("sort-asc", "chevron-up", t("data-grid.sort-asc", "Sort ascending"), () => this._setSort([{ field: f, dir: "asc" }])),
                this._menuItem("sort-desc", "chevron-down", t("data-grid.sort-desc", "Sort descending"), () => this._setSort([{ field: f, dir: "desc" }])),
                sorted ? this._menuItem("sort-clear", "close", t("data-grid.sort-clear", "Clear sort"), () => this._setSort(this._sort.filter((s) => s.field !== f))) : null,
                el("hr"),
            ] : []),
            ...(col.filterable ? [
                this._menuItem("filter", "search", t("data-grid.filter", "Filter…"), () => this._openFilter(this._cols.findIndex((x) => x.field === f))),
                this._filter[f] ? this._menuItem("filter-clear", "close", t("data-grid.filter-clear", "Clear filter"), () => this._setFilter(f, null)) : null,
                el("hr"),
            ] : []),
            this._menuItem("select-column", "grid", t("data-grid.select-column", "Select column"), () => {
                const i = this._cols.findIndex((x) => x.field === f);
                this._scroller.focus({ preventScroll: true });
                this._setCursor(this._cur.r, i, false);
                this._selectCols(i, i);
            }),
            this._menuItem("fit", "fit", t("data-grid.fit-width", "Fit width"), () => this._fitColumn(this._cols.findIndex((x) => x.field === f))),
            this._menuItem("hide", "eye-off", t("data-grid.hide-column", "Hide column"), () => this._setHidden(f, true), { disabled: this._cols.length < 2 }),
        ];
        if (hidden.length) {
            items.push(el("hr"));
            for (const h of hidden) {
                items.push(this._menuItem("show:" + h.field, "eye", t("data-grid.show-column", "Show {label}", { label: h.labelText() }), () => this._setHidden(h.field, false)));
            }
            if (hidden.length > 1) {
                items.push(this._menuItem("show-all", "eye", t("data-grid.show-all", "Show all columns"), () => {
                    this._hidden.clear();
                    this._visible();
                    this._emitView();
                }));
            }
        }
        const h = this._hcells[c];
        this._openMenu(items, point || this._pointUnder(h || this._scroller));
        if (h && this._menu.hasAttribute("open")) h.classList.add("open");
    }

    _setHidden(field, hidden) {
        if (hidden && this._cols.length < 2) return;
        const curField = this._cols[this._cur.c] && this._cols[this._cur.c].field;
        if (hidden) this._hidden.add(field); else this._hidden.delete(field);
        this._visible();
        const c = this._cols.findIndex((x) => x.field === curField);
        if (c >= 0) { this._cur.c = c; this._anchor = { ...this._cur }; this._end = { ...this._cur }; }
        this._selChanged();
        this._emitView();
    }

    _openRowMenu(point) {
        const rows = this._rowCount();
        if (!rows) return;
        this._menuActions = {};
        const items = this._rowMenuItems ? this._rowMenuItems() : [];
        items.push(this._menuItem("copy", "copy", t("data-grid.copy", "Copy"), () => this._copyToClipboard()));
        let pt = point;
        if (!pt) {
            const cellEl = this._cellEl(this._cur.r, this._cur.c);
            pt = cellEl ? this._pointUnder(cellEl) : this._pointUnder(this._scroller);
        }
        this._openMenu(items, pt);
    }

    _cellEl(r, c) {
        for (const row of this._pool) if (row._r === r && !row.classList.contains("msg")) return row._cells[c] || null;
        return null;
    }

    /* ----------------------------------------------------------- filter -- */

    _openFilter(c) {
        const col = this._cols[c];
        if (!col || !col.filterable) return;
        this._closeFilter();
        const pop = this._pop;
        const f = col.field;
        const cur = this._filter[f] || null;
        let kind = col.type.filter || "text";
        if (kind === "any" && !col.options.length) kind = "text";
        pop.textContent = "";
        pop.setAttribute("aria-label", t("data-grid.filter-title", "Filter {label}", { label: col.labelText() }));
        pop.appendChild(el("div", "pop-title", col.labelText()));
        const body = el("div", "pop-body");
        pop.appendChild(body);
        let focusEl = null;
        const apply = (desc) => this._setFilter(f, desc);

        if (kind === "text") {
            const input = el("input", "field");
            input.type = "search";
            input.placeholder = t("data-grid.contains", "Contains…");
            input.value = cur && cur.op === "contains" ? cur.value : "";
            let timer = 0;
            input.addEventListener("input", () => {
                clearTimeout(timer);
                timer = setTimeout(() => apply(input.value.trim() ? { op: "contains", value: input.value.trim() } : null), 250);
            });
            input.addEventListener("keydown", (e) => {
                if (e.key === "Enter") { clearTimeout(timer); apply(input.value.trim() ? { op: "contains", value: input.value.trim() } : null); this._closeFilter(true); }
            });
            body.appendChild(input);
            focusEl = input;
        } else if (kind === "number" || kind === "date" || kind === "time") {
            const tag = { number: "sac-number-field", date: "sac-date-field", time: "sac-time-field" }[kind];
            const mk = (which, label) => {
                const box = el("div");
                box.appendChild(el("div", "pop-label", label));
                const fld = document.createElement(tag);
                fld.setAttribute("size", "regular");
                fld.setAttribute("aria-label", label);
                if (kind === "number" && col.decimals != null) fld.setAttribute("decimals", String(col.decimals));
                const v = cur && cur.op === "range" ? cur[which] : null;
                if (v != null && v !== "") fld.value = v;
                box.appendChild(fld);
                return { box, fld };
            };
            const lo = mk("min", t("data-grid.from", "From"));
            const hi = mk("max", t("data-grid.to", "To"));
            const row = el("div", "pop-row");
            row.append(lo.box, hi.box);
            body.appendChild(row);
            const read = (fld) => {
                const v = fld.value;
                return v === "" || v == null ? null : v;
            };
            const update = () => {
                const min = read(lo.fld), max = read(hi.fld);
                apply(min == null && max == null ? null : Object.assign({ op: "range" }, min != null ? { min } : {}, max != null ? { max } : {}));
            };
            lo.fld.addEventListener("sac:change", update);
            hi.fld.addEventListener("sac:change", update);
            focusEl = lo.fld;
        } else if (kind === "any") {
            const chosen = new Set(cur && cur.op === "any" ? cur.values.map(String) : []);
            const list = el("div", "opts");
            for (const o of col.options) {
                const lab = el("label", "opt");
                const cb = el("input");
                cb.type = "checkbox";
                cb.checked = chosen.has(String(o.value));
                cb.addEventListener("change", () => {
                    if (cb.checked) chosen.add(String(o.value)); else chosen.delete(String(o.value));
                    const values = col.options.filter((x) => chosen.has(String(x.value))).map((x) => x.value);
                    apply(values.length ? { op: "any", values } : null);
                });
                lab.append(cb, el("span", "", Types().optionLabel(o)));
                list.appendChild(lab);
                if (!focusEl) focusEl = cb;
            }
            body.appendChild(list);
        } else if (kind === "bool") {
            const seg = document.createElement("sac-segmented-control");
            const val = cur && cur.op === "is" ? (cur.value ? "yes" : "no") : "any";
            for (const [v, text] of [["any", t("data-grid.any", "Any")], ["yes", t("data-grid.yes", "Yes")], ["no", t("data-grid.no", "No")]]) {
                const b = el("button", "", text);
                b.type = "button";
                b.dataset.value = v;
                seg.appendChild(b);
            }
            seg.setAttribute("value", val);
            seg.addEventListener("sac:change", (e) => {
                const v = e.detail.value;
                apply(v === "any" ? null : { op: "is", value: v === "yes" });
            });
            body.appendChild(seg);
            focusEl = seg.querySelector("button");
        }

        const foot = el("div", "pop-foot");
        const clear = el("button", "btn", t("data-grid.filter-clear", "Clear filter"));
        clear.type = "button";
        clear.addEventListener("click", () => { apply(null); this._closeFilter(true); });
        const done = el("button", "btn primary", t("data-grid.done", "Done"));
        done.type = "button";
        done.addEventListener("click", () => this._closeFilter(true));
        foot.append(clear, done);
        pop.appendChild(foot);

        pop.tabIndex = -1;                  // a click on its body keeps focus inside
        pop.style.left = "0px";
        pop.style.top = "0px";
        try { pop.showPopover(); } catch (err) { return; }
        this._popField = f;
        this._placePop();

        this._popOpen = true;
        this._popOutside = (e) => {
            if (e.composedPath().includes(pop)) return;
            this._closeFilter(false);
        };
        this._popKey = (e) => {
            if (e.key === "Escape" && !e.defaultPrevented) {
                e.preventDefault();
                e.stopPropagation();
                this._closeFilter(true);
            }
        };
        // Tab (or anything) taking focus elsewhere closes it; a field's own
        // popover (a calendar) still counts as inside.
        this._popBlur = () => setTimeout(() => {
            if (!this._popOpen) return;
            const a = this.shadowRoot.activeElement;
            if (!a || !pop.contains(a)) this._closeFilter(false);
        }, 0);
        // It stays under its header while the page or the grid scrolls.
        this._popPlace = () => this._placePop();
        setTimeout(() => {
            if (!this._popOpen) return;
            document.addEventListener("pointerdown", this._popOutside, true);
        }, 0);
        pop.addEventListener("keydown", this._popKey);
        pop.addEventListener("focusout", this._popBlur);
        window.addEventListener("scroll", this._popPlace, true);
        window.addEventListener("resize", this._popPlace);
        this._scroller.addEventListener("scroll", this._popPlace);
        if (focusEl) requestAnimationFrame(() => focusEl.focus());
    }

    /** Anchor the filter popover under its column's header, inside the viewport. */
    _placePop() {
        const pop = this._pop;
        const c = this._cols.findIndex((x) => x.field === this._popField);
        const h = c >= 0 ? this._hcells[c] : null;
        const r = (h || this._scroller).getBoundingClientRect();
        const pw = pop.offsetWidth, ph = pop.offsetHeight;
        const vv = window.visualViewport;
        const vwid = vv ? vv.width : innerWidth, vhei = vv ? vv.height : innerHeight;
        const left = clamp(r.left, 8, Math.max(8, vwid - pw - 8));
        let top = r.bottom + 4;
        if (top + ph > vhei - 8 && r.top - ph - 4 > 8) top = r.top - ph - 4;
        pop.style.left = left + "px";
        pop.style.top = Math.max(8, top) + "px";
    }

    _closeFilter(refocus) {
        if (!this._popOpen) return;
        this._popOpen = false;
        document.removeEventListener("pointerdown", this._popOutside, true);
        this._pop.removeEventListener("keydown", this._popKey);
        this._pop.removeEventListener("focusout", this._popBlur);
        window.removeEventListener("scroll", this._popPlace, true);
        window.removeEventListener("resize", this._popPlace);
        this._scroller.removeEventListener("scroll", this._popPlace);
        try { this._pop.hidePopover(); } catch (err) { /* already closed */ }
        if (refocus) this._scroller.focus({ preventScroll: true });
    }

    /* -------------------------------------------------------- clipboard -- */

    /** Is the grid itself (not an editor, not a popover) the focused thing? */
    _gridFocused() {
        return this.shadowRoot.activeElement === this._scroller;
    }

    _onClipboard(e) {
        if (!this._gridFocused() || this._editor) return;
        if (e.type === "paste") {
            if (this._paste) this._paste(e);
            return;
        }
        const text = this._rangeTSV();
        if (text == null) return;
        e.preventDefault();
        e.clipboardData.setData("text/plain", text);
        if (e.type === "cut" && this._cut) this._cut();
    }

    /** The range as TSV (Excel / Sheets quoting), or null while rows are missing. */
    _rangeTSV() {
        const s = this._rect();
        const cols = this._cols.slice(s.c1, s.c2 + 1);
        const lines = [];
        for (let r = s.r1; r <= s.r2; r++) {
            const row = this._rowAt(r);
            if (row === undefined) {
                this._announce(t("data-grid.copy-wait", "Some rows are still loading — try again in a moment."));
                return null;
            }
            lines.push(cols.map((col) => tsvField(col.copy(this._get(row, col), row))).join("\t"));
        }
        return lines.join("\r\n");
    }

    async _copyToClipboard() {
        const text = this._rangeTSV();
        if (text == null) return;
        try { await navigator.clipboard.writeText(text); }
        catch (err) { this._announce(t("data-grid.copy-failed", "Couldn't copy.")); }
    }

    /* ------------------------------------------------------------ modes -- */

    /** Editing is possible at all: sheet / form mode and a source that saves. */
    _editable() {
        return this.mode !== "read" && !!this._source && typeof this._source.save === "function";
    }

    _modeChanged() {
        if (!this._built) return;
        if (this._onModeChanged) this._onModeChanged();
        this._modesEl._t = null;
        this._stamp++;
        this._scheduleRender();
    }
}

    /** One field of a TSV line: quoted when it holds a tab, a newline or a quote. */
    function tsvField(s) {
        s = s == null ? "" : String(s);
        return /[\t\r\n"]/.test(s) ? '"' + s.replace(/"/g, '""') + '"' : s;
    }

    /** TSV text → rows of fields (Excel / Sheets quoting; a trailing newline
     *  does not add an empty row). */
    function parseTSV(text) {
        const rows = [];
        let row = [], field = "", i = 0, quoted = false;
        const s = String(text == null ? "" : text);
        while (i < s.length) {
            const ch = s[i];
            if (quoted) {
                if (ch === '"') {
                    if (s[i + 1] === '"') { field += '"'; i += 2; continue; }
                    quoted = false; i++; continue;
                }
                field += ch; i++; continue;
            }
            if (ch === '"' && field === "") { quoted = true; i++; continue; }
            if (ch === "\t") { row.push(field); field = ""; i++; continue; }
            if (ch === "\r" || ch === "\n") {
                row.push(field); field = "";
                rows.push(row); row = [];
                i += ch === "\r" && s[i + 1] === "\n" ? 2 : 1;
                continue;
            }
            field += ch; i++;
        }
        if (field !== "" || row.length) { row.push(field); rows.push(row); }
        return rows;
    }

    function computeAggregate(fn, vals) {
        if (fn === "count") return vals.length;
        if (!vals.length) return null;
        if (fn === "sum" || fn === "avg") {
            let sum = 0, n = 0;
            for (const v of vals) if (typeof v === "number" && Number.isFinite(v)) { sum += v; n++; }
            if (!n) return null;
            return fn === "sum" ? sum : sum / n;
        }
        let best = vals[0];
        for (const v of vals) if (fn === "min" ? v < best : v > best) best = v;
        return best;
    }

    /* The browser reads lifecycle callbacks once, at define(): the other grid
     * scripts (loaded after this one) join connect / disconnect here. A
     * connect hook also runs at once for the grids already on the page —
     * they were upgraded when this script defined the element. */
    const HOOKS = { connect: [], disconnect: [] };
    const LIVE = new Set();
    function runHooks(grid, name) {
        if (name === "connect") LIVE.add(grid);
        else LIVE.delete(grid);
        for (const fn of HOOKS[name]) {
            try { fn.call(grid); } catch (err) { console.error(`[sac-data-grid] ${name} hook:`, err); }
        }
    }
    SacDataGrid.hook = (name, fn) => {
        if (!HOOKS[name] || typeof fn !== "function") return;
        HOOKS[name].push(fn);
        if (name === "connect") {
            for (const grid of LIVE) {
                try { fn.call(grid); } catch (err) { console.error("[sac-data-grid] connect hook:", err); }
            }
        }
    };

    SacDataGrid.tsvField = tsvField;
    SacDataGrid.parseTSV = parseTSV;
    SacDataGrid.computeAggregate = computeAggregate;

    customElements.define("sac-data-grid", SacDataGrid);
    window.SacDataGrid = SacDataGrid;
})();
