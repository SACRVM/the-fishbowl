/**
 * fb.tagManager — the "Manage tags" window.
 *
 * Built from kit parts, no component of its own: a <sac-window> whose
 * light-DOM body lists every tag with an inline rename field, the palette
 * as a <sac-swatch-grid>, its usage count and a delete button (sac.dialog
 * confirms when the tag is in use); a system tag carries a <sac-chip>.
 * Row layout lives in app.css under .fb-tags-*.
 *
 * System tags (source:mcp, review:pending) keep their name — workflows key
 * on it and the server refuses the rename — and can't be deleted; their
 * colour is presentation and stays editable.
 *
 *   fb.tagManager.open({ onChanged })
 *     onChanged — called once when the window closes, if any tag was
 *                 renamed, recoloured or deleted.
 */
(function () {
    if (!window.fb) return;

    const NAME_RE = /^[a-z0-9_:-]{1,50}$/;
    let win = null;
    let dirty = false;
    let onChanged = null;

    function ensureWindow() {
        if (win) return win;
        win = document.createElement("sac-window");
        win.id = "fb-tag-manager";
        win.setAttribute("title", "Manage tags");
        win.setAttribute("width", "640px");
        win.setAttribute("height", "480px");
        win.setAttribute("top", "80px");
        win.setAttribute("left", "calc(50vw - 320px)");
        win.setAttribute("controls", "close");
        win.innerHTML = `<div class="fb-tags-list"></div>`;
        // One refresh for the whole session instead of one per edit.
        win.addEventListener("sac:close", () => {
            if (!dirty) return;
            fb.tags.invalidate();
            onChanged?.();
        });
        document.body.appendChild(win);
        return win;
    }

    async function open(opts = {}) {
        onChanged = opts.onChanged || null;
        dirty = false;
        const w = ensureWindow();
        const list = w.querySelector(".fb-tags-list");
        let tags = [];
        try { tags = await fb.api.tags.list(); }
        catch (err) { console.warn("[fb.tagManager] tags unavailable:", err); }
        list.replaceChildren(...tags.map(renderRow));
        requestAnimationFrame(() => w.open());
    }

    function renderRow(tag) {
        const system = tag.isSystem === true;
        const row = document.createElement("div");
        row.className = "fb-tags-row";
        row.innerHTML = `
            ${system ? `<sac-chip class="fb-tags-badge" label="system" title="System tag"></sac-chip>` : ""}
            <input class="fb-tags-name" type="text" aria-label="Tag name"
                   ${system ? `readonly title="System tag — the name is protected"` : ""}>
            <sac-swatch-grid selectable columns="${fb.tags.SLOTS.length}" aria-label="Tag colour"></sac-swatch-grid>
            <span class="fb-tags-count" title="Notes using this tag"></span>
            ${system ? "" : `<button type="button" class="icon-btn fb-tags-delete" title="Delete tag" aria-label="Delete tag">
                                 <sac-icon name="trash"></sac-icon></button>`}`;

        // User-sourced strings go in through properties, never markup.
        const nameInput = row.querySelector(".fb-tags-name");
        nameInput.value = tag.name;
        row.querySelector(".fb-tags-count").textContent = String(tag.usageCount || 0);

        let currentName = tag.name;

        if (!system) {
            nameInput.addEventListener("keydown", (e) => { if (e.key === "Enter") nameInput.blur(); });
            nameInput.addEventListener("blur", async () => {
                const next = nameInput.value.trim().toLowerCase();
                if (!next || next === currentName) {
                    nameInput.value = currentName;
                    nameInput.classList.remove("invalid");
                    return;
                }
                if (!NAME_RE.test(next)) { nameInput.classList.add("invalid"); return; }
                try {
                    await fb.api.tags.rename(currentName, next);
                    currentName = next;
                    nameInput.value = next;
                    nameInput.classList.remove("invalid");
                    dirty = true;
                } catch (err) {
                    console.warn("[fb.tagManager] rename failed:", err);
                    nameInput.classList.add("invalid");
                }
            });
        }

        // The palette slots, the tag's own selected. A failed save puts the
        // old selection back.
        const grid = row.querySelector("sac-swatch-grid");
        customElements.upgrade(grid); // the row isn't in the document yet
        let currentColor = tag.color;
        const paint = () => {
            grid.colors = fb.tags.SLOTS.map(slot => ({
                value: fb.accents.cssVar(slot), label: slot, selected: slot === currentColor,
            }));
        };
        paint();
        grid.addEventListener("sac:change", async (e) => {
            const slot = fb.accents.slotOf(e.detail.value);
            if (!slot) return;
            try {
                await fb.api.tags.upsertColor(currentName, slot);
                currentColor = slot;
                dirty = true;
            } catch (err) {
                console.warn("[fb.tagManager] recolour failed:", err);
                paint();
            }
        });

        row.querySelector(".fb-tags-delete")?.addEventListener("click", async () => {
            // Unused tags go silently — nothing to lose. A tag in use comes
            // off every note that carries it, so ask first.
            const used = tag.usageCount || 0;
            if (used > 0) {
                const answer = await sac.dialog.confirm({
                    title: `Delete tag "${currentName}"?`,
                    message: `Used by ${used} ${used === 1 ? "note" : "notes"}. Deleting removes the tag from all of them. The notes themselves stay.`,
                    buttons: [
                        { action: "cancel", label: "Cancel", kind: "default" },
                        { action: "delete", label: "Delete", kind: "destructive", armAfterMs: 1500 },
                    ],
                });
                if (answer !== "delete") return;
            }
            try {
                await fb.api.tags.delete(currentName);
                row.remove();
                dirty = true;
            } catch (err) {
                console.warn("[fb.tagManager] delete failed:", err);
            }
        });

        return row;
    }

    fb.tagManager = { open };
})();
