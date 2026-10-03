/*
 * fb.tableGrid — a space table on the vendored <sac-data-grid>.
 *
 *   const { columns, source } = await fb.tableGrid.build(def)
 *
 * Maps the table's column types onto the grid's (Fishbowl.Core.Tables
 * ColumnKind → sac-data-grid type) and wraps fb.api.tables as the grid's
 * data source: sort and filters become the query DSL, edits become row
 * PATCH/POST/DELETE, and the server's refusals land on the cell they are
 * about. Links show their target's title, members their name.
 */
(function () {
    const LINK_OPTIONS = 500;   // the most targets a link's select offers

    const pad = (n) => String(n).padStart(2, "0");
    // Stored UTC ISO ⇄ the grid's local "yyyy-mm-ddTHH:MM".
    function toLocal(iso) {
        if (!iso) return null;
        const d = new Date(iso);
        return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
    }
    const toIso = (local) => (local ? new Date(local).toISOString() : null);

    // What a link may point at, as select options (id → title).
    async function linkOptions(target) {
        try {
            if (target === "notes") return (await fb.api.notes.list({ limit: LINK_OPTIONS })).map((n) => ({ value: n.id, label: n.title || n.id }));
            if (target === "todos") return (await fb.api.todos.list({ includeCompleted: true })).slice(0, LINK_OPTIONS).map((x) => ({ value: x.id, label: x.title || x.id }));
            if (target === "events") return (await fb.api.events.list()).slice(0, LINK_OPTIONS).map((x) => ({ value: x.id, label: x.title || x.id }));
            if (target === "contacts") return (await fb.api.contacts.list()).slice(0, LINK_OPTIONS).map((x) => ({ value: x.id, label: x.name || x.id }));
            const rows = await fb.api.tables.query(target, { limit: LINK_OPTIONS, orderBy: [{ field: "title", direction: "asc" }] });
            return rows.map((r) => ({ value: r.id, label: r.title }));
        } catch {
            return [];
        }
    }

    async function build(def) {
        const scope = sac.scope.get();
        const members = scope.type === "scoped"
            ? ((await fb.api.spaces.members(scope.slug).catch(() => null))?.items || [])
            : [];
        const memberOptions = members.map((m) => ({ value: m.userId, label: m.name || m.userId }));
        const memberName = (id) => memberOptions.find((o) => o.value === id)?.label || id || "";

        // Per column: the grid column, and how a value goes to and from the server.
        const map = [];
        map.push({
            col: { field: "title", label: fb.t("fb.tables.col-title", "Title"), type: "text", required: true, frozen: true, width: 220 },
            in: (v) => v, out: (v) => v,
        });
        for (const c of def.columns) {
            const base = { field: c.name, label: c.name, required: !!c.required };
            if (c.description) base.title = c.description;
            const same = { in: (v) => v, out: (v) => v };
            switch (c.type) {
                case "text": case "file":
                    map.push({ col: { ...base, type: "text" }, ...same }); break;
                case "longtext":
                    map.push({ col: { ...base, type: "longtext" }, ...same }); break;
                case "integer":
                    map.push({ col: { ...base, type: "number", decimals: 0, step: 1 }, ...same }); break;
                case "decimal":
                    map.push({ col: { ...base, type: "number" }, ...same }); break;
                case "yesno":
                    map.push({ col: { ...base, type: "bool" }, in: (v) => !!v, out: (v) => !!v }); break;
                case "date":
                    map.push({ col: { ...base, type: "date" }, ...same }); break;
                case "datetime":
                    map.push({ col: { ...base, type: "datetime" }, in: toLocal, out: toIso }); break;
                case "choice":
                    if (c.multiple) {
                        // Tag values must be tag-safe: the option's index stands in.
                        const opts = (c.options || []).map((o, i) => ({ value: "o" + i, label: o }));
                        map.push({
                            col: { ...base, type: "tags", options: opts, allowCreate: false, sortable: false, filterable: false },
                            in: (v) => (v || []).map((x) => opts.find((o) => o.label === x)?.value).filter(Boolean),
                            out: (v) => (v || []).map((x) => opts.find((o) => o.value === x)?.label).filter(Boolean),
                        });
                    } else {
                        map.push({ col: { ...base, type: "select", options: (c.options || []).map((o) => ({ value: o, label: o })) }, ...same });
                    }
                    break;
                case "member":
                    map.push({ col: { ...base, type: "select", options: memberOptions }, ...same }); break;
                case "link": {
                    const opts = await linkOptions(c.link);
                    if (c.multiple) {
                        // Ids lower-cased are tag-safe; the map turns them back.
                        const byTag = new Map(opts.map((o) => [o.value.toLowerCase(), o.value]));
                        map.push({
                            col: { ...base, type: "tags", options: opts.map((o) => ({ value: o.value.toLowerCase(), label: o.label })),
                                   allowCreate: false, sortable: false, filterable: false },
                            in: (v) => (v || []).map((id) => id.toLowerCase()),
                            out: (v) => (v || []).map((x) => byTag.get(x) || x),
                        });
                    } else {
                        map.push({ col: { ...base, type: "select", options: opts }, ...same });
                    }
                    break;
                }
                default:
                    map.push({ col: { ...base, type: "text" }, ...same });
            }
        }
        map.push({
            col: { field: "author", label: fb.t("fb.tables.col-author", "Added by"), type: "readonly", hidden: true,
                   format: (v) => memberName(v), sortable: false, filterable: false },
            in: (v) => v, out: null,
        });
        map.push({
            col: { field: "last_modified", label: fb.t("fb.tables.col-modified", "Changed"), type: "readonly", hidden: true,
                   format: (v) => (v ? fb.format.dateTime(new Date(v)) : "") },
            in: (v) => v, out: null,
        });

        const byField = new Map(map.map((m) => [m.col.field, m]));
        const fromServer = (row) => {
            const r = { id: row.id, row_version: row.row_version };
            for (const m of map) r[m.col.field] = m.in(row[m.col.field]);
            return r;
        };
        const toServer = (fields) => {
            const out = {};
            for (const [f, v] of Object.entries(fields || {})) {
                const m = byField.get(f);
                if (m?.out) out[f] = m.out(v === "" ? null : v);
            }
            return out;
        };

        // The grid's filter descriptors → the query DSL.
        function where(filter) {
            const all = [];
            for (const [field, d] of Object.entries(filter || {})) {
                const m = byField.get(field);
                if (!m || !d) continue;
                if (d.op === "contains" && d.value) all.push({ [field]: { $like: `%${d.value}%` } });
                else if (d.op === "range") {
                    const cond = {};
                    const conv = m.col.type === "datetime"
                        ? (v, end) => toIso(/T/.test(v) ? v : `${v}T${end ? "23:59" : "00:00"}`)
                        : (v) => v;
                    if (d.min != null && d.min !== "") cond.$gte = conv(d.min, false);
                    if (d.max != null && d.max !== "") cond.$lte = conv(d.max, true);
                    if (Object.keys(cond).length) all.push({ [field]: cond });
                } else if (d.op === "any" && d.values?.length) all.push({ [field]: { $in: d.values } });
                else if (d.op === "is") all.push({ [field]: d.value ? 1 : 0 });
            }
            return all.length === 0 ? undefined : all.length === 1 ? all[0] : { $and: all };
        }

        // A refusal as the grid's error: on the column it names, in the page's language.
        const refusal = (id, err) => {
            let body = null;
            try { body = typeof err?.body === "string" ? JSON.parse(err.body) : err?.body; } catch { /* not JSON */ }
            return { id, field: body?.column && byField.has(body.column) ? body.column : undefined,
                     message: fb.errors.text(err, fb.t("fb.tables.save-failed", "Couldn't save the row.")) };
        };

        let lastWhere = null;
        let total = null;
        const source = {
            key: "id",
            pageSize: 100,
            async load({ offset, limit, sort, filter }) {
                const w = where(filter);
                const key = JSON.stringify(w || null);
                if (key !== lastWhere) { lastWhere = key; total = await fb.api.tables.count(def.name, w).then((r) => r.count); }
                const rows = await fb.api.tables.query(def.name, {
                    where: w,
                    orderBy: (sort || []).map((s) => ({ field: s.field, direction: s.dir })),
                    limit, offset,
                });
                return { rows: rows.map(fromServer), total };
            },
            async save(changes) {
                const saved = [];
                const errors = [];
                for (const c of changes) {
                    try {
                        if (c.op === "create") {
                            // A new row's typed values are in c.fields; c.row is the blank it started from.
                            const created = await fb.api.tables.insert(def.name, toServer({ ...c.row, ...c.fields }));
                            Object.assign(c.row, fromServer(created));
                            total = total == null ? null : total + 1;
                        } else if (c.op === "update") {
                            const updated = await fb.api.tables.update(def.name, c.id, toServer(c.fields));
                            Object.assign(c.row, fromServer(updated));
                        }
                        // The id the grid knows the change by — a new row's
                        // temporary "new-<n>", whatever id the server gave it.
                        saved.push(c.id);
                    } catch (err) {
                        errors.push(refusal(c.id, err));
                    }
                }
                return { saved, errors };
            },
            async remove(ids) {
                const removed = [];
                const errors = [];
                for (const id of ids) {
                    try { await fb.api.tables.remove(def.name, id); removed.push(id); total = total == null ? null : total - 1; }
                    catch (err) { errors.push(refusal(id, err)); }
                }
                return { removed, errors };
            },
        };
        return { columns: map.map((m) => m.col), source };
    }

    fb.tableGrid = { build };
})();
