/*
 * fb.notesBoard — the Notes app's board: one column per value of a tag
 * namespace (`status:draft`, `status:approved` … → draft, approved), a card
 * per note, and a last column for the notes without one. Moving a card swaps
 * the tag — every value of the namespace comes off, the column's goes on —
 * through the note's patch (tags in place, no approval: review:pending
 * stays). Columns are the namespace's tags of the workspace, also when empty
 * (somewhere to move to), plus any a shown note carries; their order is
 * kept per browser, workspace and namespace, changed from a column's menu.
 *
 * Built from kit parts and app code — the kit has no board (and its
 * sortable doesn't cross lists): native drag and drop for a mouse; a card's
 * context menu (a long press, Shift+F10) has Open and Move to …;
 * Alt+←/→ moves the focused card, Enter opens it. A Reader only opens.
 *
 *   fb.notesBoard.render(host, { notes, ns, values, writable, storageKey, onOpen(id), onMove(note, to) })
 *     values  — the namespace's tag values known in the workspace
 *     to      — the column's value, or null for "none"
 *   fb.notesBoard.namespaces(notes, tags) → namespaces in use, most used first
 */
(function () {
    if (!window.fb) return;

    const NONE = "\u0000none";
    let menu = null;

    function valuesOf(note, ns) {
        return (note.tags || []).filter((t) => t.startsWith(ns + ":")).map((t) => t.slice(ns.length + 1));
    }

    function namespaces(notes, tags) {
        const count = new Map();
        for (const name of [...(tags || []).map((t) => t.name), ...(notes || []).flatMap((n) => n.tags || [])]) {
            const i = name.indexOf(":");
            if (i <= 0 || name.startsWith("source:") || name.startsWith("review:")) continue;
            const ns = name.slice(0, i);
            count.set(ns, (count.get(ns) || 0) + 1);
        }
        return [...count.entries()].sort((a, b) => (b[0] === "status") - (a[0] === "status") || b[1] - a[1] || a[0].localeCompare(b[0])).map(([ns]) => ns);
    }

    function readOrder(key) {
        try { return JSON.parse(localStorage.getItem(key) || "[]"); } catch { return []; }
    }
    function writeOrder(key, order) {
        try { localStorage.setItem(key, JSON.stringify(order)); } catch { /* storage off: this session only */ }
    }

    function render(host, opts) {
        const { notes, ns, writable, storageKey, onOpen, onMove } = opts;
        const present = new Set([...(opts.values || []), ...notes.flatMap((n) => valuesOf(n, ns))]);
        const stored = readOrder(storageKey).filter((v) => present.has(v));
        const order = [...stored, ...[...present].filter((v) => !stored.includes(v)).sort((a, b) => a.localeCompare(b))];
        const columns = [...order, NONE];
        const columnOf = (note) => {
            const vs = valuesOf(note, ns);
            return order.find((v) => vs.includes(v)) ?? NONE;
        };

        host.replaceChildren();
        host.classList.add("nb");
        const label = (v) => v === NONE ? fb.t("fb.notes.board.none", "No {ns}", { ns }) : v;

        for (const value of columns) {
            const cards = notes.filter((n) => columnOf(n) === value);
            if (value === NONE && !cards.length) continue;
            const col = document.createElement("section");
            col.className = "nb-col";
            col.dataset.value = value;
            col.innerHTML = `<header class="nb-col-head"><span class="nb-col-name"></span><span class="nb-col-count"></span></header><div class="nb-cards" role="list"></div>`;
            col.querySelector(".nb-col-name").textContent = label(value);
            col.querySelector(".nb-col-count").textContent = String(cards.length);
            const list = col.querySelector(".nb-cards");
            list.setAttribute("aria-label", label(value));
            for (const note of cards) list.append(card(note, ns, writable));
            if (writable) {
                list.addEventListener("dragover", (e) => {
                    if (!e.dataTransfer.types.includes("application/x-fb-note")) return;
                    e.preventDefault();
                    e.dataTransfer.dropEffect = "move";
                    col.classList.add("drop");
                });
                list.addEventListener("dragleave", (e) => { if (!list.contains(e.relatedTarget)) col.classList.remove("drop"); });
                list.addEventListener("drop", (e) => {
                    e.preventDefault();
                    col.classList.remove("drop");
                    const id = e.dataTransfer.getData("application/x-fb-note");
                    const note = notes.find((n) => n.id === id);
                    if (note && columnOf(note) !== value) onMove(note, value === NONE ? null : value);
                });
            }
            host.append(col);
        }

        host.onclick = (e) => {
            const el = e.target.closest?.(".nb-card");
            if (el) onOpen(el.dataset.id);
        };
        host.onkeydown = (e) => {
            const el = e.target.closest?.(".nb-card");
            if (!el) return;
            const note = notes.find((n) => n.id === el.dataset.id);
            if (!note) return;
            if (e.key === "Enter") { e.preventDefault(); onOpen(note.id); return; }
            if (!writable || !e.altKey || (e.key !== "ArrowLeft" && e.key !== "ArrowRight")) return;
            e.preventDefault();
            const shown = [...host.querySelectorAll(".nb-col")].map((c) => c.dataset.value);
            const at = shown.indexOf(columnOf(note));
            const next = shown[at + (e.key === "ArrowRight" ? 1 : -1)];
            if (next !== undefined) onMove(note, next === NONE ? null : next, { focus: true });
        };

        menu?.destroy?.();
        menu = sac.contextMenu(host, {
            targets: ".nb-card, .nb-col-head",
            items: (el) => {
                if (el.classList.contains("nb-col-head")) {
                    const value = el.closest(".nb-col").dataset.value;
                    if (value === NONE) return null;
                    const i = order.indexOf(value);
                    const move = (d) => {
                        const next = [...order];
                        next.splice(i + d, 0, ...next.splice(i, 1));
                        writeOrder(storageKey, next);
                        render(host, opts);
                    };
                    return [
                        { id: "left", label: fb.t("fb.notes.board.col-left", "Move column left"), icon: "chevron-left", disabled: i <= 0, onClick: () => move(-1) },
                        { id: "right", label: fb.t("fb.notes.board.col-right", "Move column right"), icon: "chevron-right", disabled: i >= order.length - 1, onClick: () => move(1) },
                    ];
                }
                const note = notes.find((n) => n.id === el.dataset.id);
                if (!note) return null;
                const items = [{ id: "open", label: fb.t("fb.common.open", "Open"), icon: "document", onClick: () => onOpen(note.id) }];
                if (!writable) return items;
                const here = columnOf(note);
                const targets = columns.filter((v) => v !== here);
                if (targets.length) items.push("-", ...targets.map((v) => ({
                    id: "to:" + v,
                    label: fb.t("fb.notes.board.move-to", "Move to {column}", { column: label(v) }),
                    icon: "move",
                    onClick: () => onMove(note, v === NONE ? null : v),
                })));
                return items;
            },
        });
    }

    function card(note, ns, writable) {
        const el = document.createElement("div");
        el.className = "nb-card";
        el.tabIndex = 0;
        el.dataset.id = note.id;
        el.setAttribute("role", "listitem");
        el.draggable = !!writable;
        if ((note.tags || []).includes("review:pending")) el.classList.add("pending");
        const title = document.createElement("div");
        title.className = "nb-card-title";
        title.textContent = note.title || fb.t("fb.notes.untitled", "Untitled");
        el.append(title);
        const others = (note.tags || []).filter((t) => !t.startsWith(ns + ":") && t !== "source:mcp" && t !== "review:pending").slice(0, 4);
        if (others.length) {
            const tags = document.createElement("div");
            tags.className = "nb-card-tags";
            for (const t of others) {
                const chip = document.createElement("sac-chip");
                chip.setAttribute("label", t);
                chip.setAttribute("color", fb.tags.colorFor(t));
                tags.append(chip);
            }
            el.append(tags);
        }
        el.addEventListener("dragstart", (e) => {
            e.dataTransfer.setData("application/x-fb-note", note.id);
            e.dataTransfer.effectAllowed = "move";
            el.classList.add("dragging");
        });
        el.addEventListener("dragend", () => el.classList.remove("dragging"));
        return el;
    }

    fb.notesBoard = { render, namespaces, valuesOf };
})();
