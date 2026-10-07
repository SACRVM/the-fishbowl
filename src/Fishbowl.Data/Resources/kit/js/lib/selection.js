/**
 * SACRVM APPKIT — sac.selection: mark several rows of a plain list.
 *
 *   const sel = sac.selection.attach(listEl, {
 *       rows: ".item",                     // the markable rows (descendants)
 *       id: (row) => row.dataset.id,       // default — a row's stable id
 *       current: () => openId,             // optional — the row that is open
 *       icon: "sac-icon",                  // optional — swapped for "check" while marked
 *       disabled: () => !writable,         // optional — boolean or function
 *       onChange(ids) { paintHead(ids); }, // the marks changed
 *   });
 *   sel.marked;        // [ids] in row order
 *   sel.clear();  sel.set(ids);  sel.toggle(id);  sel.has(id);
 *   sel.destroy();
 *
 * The gestures of <sac-file-browser>, for any list an app renders itself
 * (contacts, notes, todos):
 *   click            — the app's own (open the row); a plain click while
 *                      rows are marked clears the marks first.
 *   Ctrl/⌘+click     — toggles a mark. The first one also marks the open row
 *                      (`current`), like a file manager.
 *   Shift+click      — marks the range from the last clicked row (the anchor,
 *                      else the open row) in the visible order; with
 *                      Ctrl/⌘ the range is added to the marks.
 *   Ctrl/⌘+A         — with the list focused: marks every visible row.
 *   Esc              — clears the marks (and only then stops the key).
 *   touch / pen      — a 500ms long-press marks the row (moving >10px is a
 *                      scroll); while anything is marked a tap toggles. The
 *                      click and the context menu after a long-press are
 *                      eaten.
 * A mark gesture never reaches the app's click handler (it is stopped on
 * the way down); clicks on a button, link or field inside a row always do.
 *
 * Only what the list shows stays marked: whenever its rows change (a
 * re-render, a search, a filter) marks of rows no longer shown are dropped,
 * so a bulk action never reaches what the user cannot see. Marks are kept
 * by id, so an app that re-renders its rows keeps them.
 *
 * What it sets: `marked` + aria-selected on marked rows, `marking` on the
 * list while anything is marked, data-sac-row on every row (ui.css: no text
 * selection, no touch callout, the marked tint). With `icon`, that element
 * of a marked row shows "check" in --accent (its own name comes back after).
 * Fires sac:mark { ids } on the list (bubbles, composed) — like the file
 * browser's — and calls onChange(ids). The app keeps its own header ("3
 * selected") and does the bulk action itself.
 */
(function () {
    if (!window.sac) { console.warn("[sac.selection] globals.js must load first — selection unavailable."); return; }

    const LONG_PRESS_MS = 500;
    const PRESS_SLOP = 10;
    const PASS = "button, a[href], input, textarea, select, [contenteditable], sac-menu";

    function attach(list, opts = {}) {
        const {
            rows: rowSel = "[data-id]",
            id: idOf = (row) => row.dataset.id,
            current = null,
            icon = null,
            disabled = false,
            onChange = null,
        } = opts;

        const off = () => (typeof disabled === "function" ? disabled() : !!disabled);
        let marks = new Set();
        let anchor = null;
        let press = null;           // a touch / pen long-press in progress
        let eatClick = false;       // the click after a long-press
        let pointer = "mouse";      // the last pointer type
        let syncing = false;

        list.setAttribute("data-sac-selection", "");

        const shown = () => Array.from(list.querySelectorAll(rowSel)).filter((r) => r.getClientRects().length > 0);
        const rowOf = (target) => {
            const r = target && target.closest ? target.closest(rowSel) : null;
            return r && list.contains(r) ? r : null;
        };
        const key = (row) => { const v = idOf(row); return v == null ? null : String(v); };

        function paint() {
            syncing = true;
            for (const row of list.querySelectorAll(rowSel)) {
                row.setAttribute("data-sac-row", "");
                const on = marks.has(key(row));
                row.classList.toggle("marked", on);
                if (on) row.setAttribute("aria-selected", "true");
                else row.removeAttribute("aria-selected");
                const ic = icon ? row.querySelector(icon) : null;
                if (ic && ic.localName === "sac-icon") {
                    if (on && !ic.hasAttribute("data-sac-mark-icon")) {
                        ic.setAttribute("data-sac-mark-icon", ic.getAttribute("name") || "");
                        ic.setAttribute("name", "check");
                    } else if (!on && ic.hasAttribute("data-sac-mark-icon")) {
                        ic.setAttribute("name", ic.getAttribute("data-sac-mark-icon"));
                        ic.removeAttribute("data-sac-mark-icon");
                    }
                }
            }
            list.classList.toggle("marking", marks.size > 0);
            // The observer's records for our own writes are dropped.
            mo.takeRecords();
            syncing = false;
        }

        function changed() {
            paint();
            const ids = ordered();
            list.dispatchEvent(new CustomEvent("sac:mark", { detail: { ids }, bubbles: true, composed: true }));
            if (typeof onChange === "function") onChange(ids);
        }

        /** The marks in the list's visible order. */
        function ordered() {
            return shown().map(key).filter((k) => marks.has(k));
        }

        /** Keep only marks of rows the list shows; repaint a re-render. */
        function prune() {
            const visible = new Set(shown().map(key));
            let lost = false;
            for (const k of Array.from(marks)) if (!visible.has(k)) { marks.delete(k); lost = true; }
            if (lost) changed(); else paint();
        }

        function toggle(k) {
            if (marks.has(k)) marks.delete(k); else marks.add(k);
        }

        function onClickCapture(e) {
            if (eatClick) { eatClick = false; e.stopPropagation(); e.preventDefault(); return; }
            if (off()) return;
            const row = rowOf(e.target);
            if (!row) return;
            const inner = e.target.closest(PASS);
            if (inner && inner !== row && row.contains(inner)) return;
            const k = key(row);
            if (k == null) return;
            const mod = e.ctrlKey || e.metaKey;
            if (e.shiftKey) {
                const ids = shown().map(key);
                const to = ids.indexOf(k);
                const cur = typeof current === "function" ? current() : null;
                let from = ids.indexOf(anchor != null ? anchor : cur != null ? String(cur) : k);
                if (from < 0) from = to;
                if (!mod) marks.clear();
                for (const x of ids.slice(Math.min(from, to), Math.max(from, to) + 1)) marks.add(x);
                stop(e);
                changed();
                return;
            }
            const touchToggle = marks.size > 0 && (pointer === "touch" || pointer === "pen");
            if (mod || touchToggle) {
                anchor = k;
                const cur = typeof current === "function" ? current() : null;
                if (mod && !marks.size && cur != null && String(cur) !== k) marks.add(String(cur));
                toggle(k);
                stop(e);
                changed();
                return;
            }
            // A plain click: the app's own. It lets go of the marks first.
            anchor = k;
            if (marks.size) { marks.clear(); changed(); }
        }

        function stop(e) {
            e.stopPropagation();
            e.preventDefault();
        }

        function onPointerDown(e) {
            pointer = e.pointerType || "mouse";
            eatClick = false;
            endPress();
            if (off() || (e.pointerType !== "touch" && e.pointerType !== "pen")) return;
            const row = rowOf(e.target);
            if (!row || e.target.closest(PASS)) return;
            const k = key(row);
            if (k == null) return;
            press = {
                id: e.pointerId, x: e.clientX, y: e.clientY,
                timer: setTimeout(() => {
                    press = null;
                    eatClick = true;
                    anchor = k;
                    marks.add(k);
                    changed();
                }, LONG_PRESS_MS),
            };
        }
        function onPointerMove(e) {
            if (press && e.pointerId === press.id && Math.hypot(e.clientX - press.x, e.clientY - press.y) > PRESS_SLOP) endPress();
        }
        function endPress() {
            if (press) { clearTimeout(press.timer); press = null; }
        }
        function onContext(e) { if (eatClick || press) e.preventDefault(); }

        function onKey(e) {
            if (e.key === "Escape" && marks.size) {
                e.stopPropagation();
                marks.clear();
                changed();
                return;
            }
            if ((e.ctrlKey || e.metaKey) && !e.altKey && !e.shiftKey && String(e.key).toLowerCase() === "a"
                && e.target === list && !off()) {
                e.preventDefault();
                marks = new Set(shown().map(key).filter((k) => k != null));
                changed();
            }
        }

        // A re-render, a filter, a search: drop what is no longer shown.
        let queued = false;
        const mo = new MutationObserver(() => {
            if (syncing || queued) return;
            queued = true;
            queueMicrotask(() => { queued = false; prune(); });
        });
        mo.observe(list, { childList: true, subtree: true, attributes: true, attributeFilter: ["hidden", "class", "style"] });

        list.addEventListener("click", onClickCapture, true);
        list.addEventListener("pointerdown", onPointerDown);
        list.addEventListener("pointermove", onPointerMove);
        list.addEventListener("pointerup", endPress);
        list.addEventListener("pointercancel", endPress);
        list.addEventListener("contextmenu", onContext);
        list.addEventListener("keydown", onKey);
        paint();

        return {
            get marked() { return ordered(); },
            has: (k) => marks.has(String(k)),
            /** Replace the marks (rows not shown are skipped); fires nothing. */
            set(ids) {
                const visible = new Set(shown().map(key));
                marks = new Set(Array.from(ids || [], String).filter((k) => visible.has(k)));
                paint();
            },
            clear() { if (marks.size) { marks.clear(); changed(); } },
            toggle(k) { toggle(String(k)); changed(); },
            /** Re-check after the app changed what is shown without touching the DOM tree. */
            sync: prune,
            destroy() {
                endPress();
                mo.disconnect();
                list.removeEventListener("click", onClickCapture, true);
                list.removeEventListener("pointerdown", onPointerDown);
                list.removeEventListener("pointermove", onPointerMove);
                list.removeEventListener("pointerup", endPress);
                list.removeEventListener("pointercancel", endPress);
                list.removeEventListener("contextmenu", onContext);
                list.removeEventListener("keydown", onKey);
                marks.clear();
                paint();
                list.removeAttribute("data-sac-selection");
            },
        };
    }

    sac.selection = { attach };
})();
