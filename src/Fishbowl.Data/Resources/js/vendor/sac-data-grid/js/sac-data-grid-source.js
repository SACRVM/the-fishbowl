/**
 * SacDataGrid.arraySource(rows, { key }) — the built-in local source (SPEC §4).
 *
 * Sort, filter and paging happen in memory over the given array; save,
 * create and remove change it in place. Good for the demo, for small tables
 * and for any list the app already holds. A query's sorted / filtered order
 * is kept until the data or the query changes, so paging through 100k rows
 * costs one sort.
 *
 *   const src = SacDataGrid.arraySource(rows, { key: "id" });
 *   grid.source = src;
 *   src.rows            // the live array
 *   src.setRows(next)   // replace the data (then grid.reload())
 *
 * Sorting compares numbers as numbers, booleans false < true, arrays by their
 * joined text, everything else as text in the page language (numeric-aware:
 * "Item 2" < "Item 10"). Empty values go last in both directions. Filter
 * descriptors: see js/sac-data-grid.js.
 *
 * Options:
 *   key       the row identity field (default "id")
 *   pageSize  rows the grid asks for per load (default 5000 — memory is cheap
 *             here, and whole-column stats and totals then need few loads)
 *   newId(row)  id for a created row that has none (default: max numeric
 *             id + 1, else a "new-<n>" string)
 *   compare   { field: (a, b) => number } — a field's own order for non-empty
 *             values, e.g. a select column by its shown label instead of its
 *             stored value. Re-sorted after a language switch.
 */
(function () {
    const Grid = window.SacDataGrid;
    if (!Grid || Grid.arraySource) return;

    const isEmpty = (v) => v == null || v === "" || (Array.isArray(v) && v.length === 0) || (typeof v === "number" && Number.isNaN(v));

    Grid.arraySource = function arraySource(rows, opts) {
        const o = opts || {};
        const key = o.key || "id";
        let data = Array.isArray(rows) ? rows : [];
        let version = 0;
        let memo = null;
        let index = null;
        let seq = 0;

        const collator = () => {
            const loc = (window.sac && sac.lang) ? sac.lang.locale() : undefined;
            if (!collator.c || collator.loc !== loc) {
                collator.loc = loc;
                try { collator.c = new Intl.Collator(loc, { numeric: true, sensitivity: "base" }); }
                catch (err) { collator.c = new Intl.Collator(undefined, { numeric: true, sensitivity: "base" }); }
            }
            return collator.c;
        };

        function compare(a, b, coll) {
            if (typeof a === "number" && typeof b === "number") return a - b;
            if (typeof a === "boolean" && typeof b === "boolean") return (a ? 1 : 0) - (b ? 1 : 0);
            const sa = Array.isArray(a) ? a.join(", ") : String(a);
            const sb = Array.isArray(b) ? b.join(", ") : String(b);
            return coll.compare(sa, sb);
        }

        function matches(v, d) {
            switch (d && d.op) {
                case "contains": {
                    const needle = String(d.value == null ? "" : d.value).toLocaleLowerCase();
                    if (!needle) return true;
                    const hay = Array.isArray(v) ? v.join(", ") : (v == null ? "" : String(v));
                    return hay.toLocaleLowerCase().includes(needle);
                }
                case "range": {
                    if (isEmpty(v)) return false;
                    let x = v;
                    // A date-only bound compares the date part of a datetime.
                    const dateOnly = (b) => typeof b === "string" && /^\d{4}-\d{2}-\d{2}$/.test(b);
                    if (typeof x === "string" && (dateOnly(d.min) || dateOnly(d.max)) && /^\d{4}-\d{2}-\d{2}T/.test(x)) x = x.slice(0, 10);
                    if (d.min != null && d.min !== "" && x < d.min) return false;
                    if (d.max != null && d.max !== "" && x > d.max) return false;
                    return true;
                }
                case "any": {
                    const values = Array.isArray(d.values) ? d.values.map(String) : [];
                    if (!values.length) return true;
                    if (Array.isArray(v)) return v.some((x) => values.includes(String(x)));
                    return !isEmpty(v) && values.includes(String(v));
                }
                case "is":
                    return !!v === !!d.value;
                default:
                    return true;
            }
        }

        function query(sort, filter) {
            // The language is part of the query: text order (and labels) follow it.
            const sig = JSON.stringify([sort || [], filter || {}, (window.sac && sac.lang) ? sac.lang.get() : ""]);
            if (memo && memo.sig === sig && memo.version === version) return memo.view;
            let view = data;
            const fields = filter ? Object.keys(filter) : [];
            if (fields.length) view = data.filter((row) => fields.every((f) => matches(row[f], filter[f])));
            if (sort && sort.length) {
                const coll = collator();
                const own = o.compare || {};
                const keys = sort.map((s) => ({
                    field: s.field,
                    dir: s.dir === "desc" ? -1 : 1,
                    cmp: typeof own[s.field] === "function" ? own[s.field] : null,
                }));
                view = view.map((row, i) => ({ row, i }));
                view.sort((x, y) => {
                    for (const k of keys) {
                        const a = x.row[k.field], b = y.row[k.field];
                        const ea = isEmpty(a), eb = isEmpty(b);
                        if (ea || eb) {
                            if (ea && eb) continue;
                            return ea ? 1 : -1;      // empty last, whatever the direction
                        }
                        const c = k.cmp ? k.cmp(a, b) : compare(a, b, coll);
                        if (c) return c * k.dir;
                    }
                    return x.i - y.i;
                });
                view = view.map((x) => x.row);
            }
            memo = { sig, version, view };
            return view;
        }

        function aggregates(view, list) {
            const out = {};
            for (const { field, fn } of list || []) {
                const vals = [];
                for (const row of view) {
                    const v = row[field];
                    if (!isEmpty(v)) vals.push(v);
                }
                out[field] = Grid.computeAggregate(fn, vals);
            }
            return out;
        }

        function byId() {
            if (!index || index.layout !== layout) {
                const map = new Map();
                data.forEach((row, i) => map.set(row[key], i));
                index = { layout, map };
            }
            return index.map;
        }

        function newId(row) {
            if (typeof o.newId === "function") return o.newId(row);
            let max = -Infinity, numeric = true;
            for (const r of data) {
                const id = r[key];
                if (typeof id !== "number") { numeric = false; break; }
                if (id > max) max = id;
            }
            if (numeric) return (max === -Infinity ? 0 : max) + 1;
            return `new-${++seq}`;
        }

        // version: any change (sorted / filtered views go stale);
        // layout: rows added or removed (the id → position index goes stale).
        let layout = 0;
        const touch = () => { version++; };
        const moved = () => { version++; layout++; };

        return {
            key,
            pageSize: o.pageSize > 0 ? o.pageSize : 5000,
            get rows() { return data; },
            setRows(next) { data = Array.isArray(next) ? next : []; moved(); },

            load(q) {
                const { offset = 0, limit = 100, sort, filter, aggregate, signal } = q || {};
                if (signal && signal.aborted) return Promise.reject(new DOMException("Aborted", "AbortError"));
                const view = query(sort, filter);
                const res = { rows: view.slice(offset, offset + limit), total: view.length };
                if (aggregate && aggregate.length) res.aggregates = aggregates(view, aggregate);
                return Promise.resolve(res);
            },

            save(changes) {
                const saved = [], errors = [];
                for (const ch of changes || []) {
                    const map = byId();
                    if (ch.op === "delete") {
                        const i = map.get(ch.id);
                        if (i != null) { data.splice(i, 1); moved(); }
                        saved.push(ch.id);
                    } else if (ch.op === "create") {
                        const row = Object.assign({}, ch.row || {}, ch.fields || {});
                        if (row[key] == null || ch.temp || map.has(row[key])) row[key] = newId(row);
                        data.push(row);
                        moved();
                        saved.push(ch.id);
                        if (ch.row && ch.row !== row) Object.assign(ch.row, row);
                    } else {
                        const i = map.get(ch.id);
                        if (i == null) { errors.push({ id: ch.id, message: "Row not found" }); continue; }
                        Object.assign(data[i], ch.fields || {});
                        touch();
                        saved.push(ch.id);
                    }
                }
                return Promise.resolve({ saved, errors });
            },

            create(row) {
                const r = Object.assign({}, row || {});
                if (r[key] == null || byId().has(r[key])) r[key] = newId(r);
                data.push(r);
                moved();
                return Promise.resolve(r);
            },

            remove(ids) {
                const set = new Set(ids || []);
                let w = 0;
                for (const row of data) if (!set.has(row[key])) data[w++] = row;   // in place: src.rows stays the live array
                if (w !== data.length) { data.length = w; moved(); }
                return Promise.resolve({ removed: [...set], errors: [] });
            },
        };
    };
})();
