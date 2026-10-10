/**
 * fb.tagManager — the "Manage tags" window, in Notes and in Mail: a tag is
 * the workspace's, not an app's.
 *
 * Built from kit parts, no component of its own: a <sac-window> whose
 * light-DOM body lists every tag with an inline rename field, where it is
 * used (notes, mail conversations, mail rules), the palette as a
 * <sac-swatch-grid> and a delete button (sac.dialog confirms when the tag
 * is used anywhere); a system tag carries a <sac-chip>.
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
        win.setAttribute("escape-closes", "");
        win.id = "fb-tag-manager";
        win.classList.add("fb-window");
        win.setAttribute("title", fb.t("fb.notes.manage-tags", "Manage tags"));
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
        // The window outlives a language switch: relabel it on every open.
        w.setAttribute("title", fb.t("fb.notes.manage-tags", "Manage tags"));
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
            ${system ? `<sac-chip class="fb-tags-badge" label="${fb.t("fb.notes.tags.system-chip", "system")}" title="${fb.t("fb.notes.tags.system", "System tag")}"></sac-chip>` : ""}
            <input class="fb-tags-name" type="text" aria-label="${fb.t("fb.notes.tags.name", "Tag name")}"
                   ${system ? `readonly title="${fb.t("fb.notes.tags.protected", "System tag — the name is protected")}"` : ""}>
            <span class="fb-tags-count"></span>
            <sac-swatch-grid selectable columns="${fb.tags.SLOTS.length}" aria-label="${fb.t("fb.notes.tags.colour", "Tag colour")}"></sac-swatch-grid>
            ${system ? "" : `<button type="button" class="icon-btn fb-tags-delete" title="${fb.t("fb.notes.tags.delete", "Delete tag")}" aria-label="${fb.t("fb.notes.tags.delete", "Delete tag")}">
                                 <sac-icon name="trash"></sac-icon></button>`}`;

        // User-sourced strings go in through properties, never markup.
        const nameInput = row.querySelector(".fb-tags-name");
        nameInput.value = tag.name;
        const uses = usesOf(tag);
        row.querySelector(".fb-tags-count").replaceChildren(...uses.map((u) => {
            const part = document.createElement("span");
            part.title = u.text;
            part.setAttribute("aria-label", u.text);
            part.innerHTML = `<sac-icon name="${u.icon}"></sac-icon>`;
            part.append(String(u.n));
            return part;
        }));

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
            // off every note, conversation and rule that carries it, so ask first.
            if (uses.length) {
                const answer = await sac.dialog.confirm({
                    title: fb.t("fb.notes.tags.delete-title", "Delete tag \"{name}\"?", { name: currentName }),
                    message: fb.t("fb.tags.delete-msg", "Used by {uses}. Deleting takes the tag off all of them; they themselves stay.",
                        { uses: uses.map((u) => u.text).join(", ") }),
                    buttons: [
                        { action: "cancel", label: fb.t("fb.common.cancel", "Cancel"), kind: "default" },
                        { action: "delete", label: fb.t("fb.common.delete", "Delete"), kind: "destructive", armAfterMs: 1500 },
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

    /** Where a tag is used: notes, mail conversations, mail rules — the kinds it is on. */
    function usesOf(tag) {
        const kinds = [
            { n: tag.usageCount || 0, icon: "note", one: ["fb.tags.notes-1", "1 note"], many: ["fb.tags.notes", "{n} notes"] },
            { n: tag.mailCount || 0, icon: "mail", one: ["fb.tags.mails-1", "1 conversation"], many: ["fb.tags.mails", "{n} conversations"] },
            { n: tag.ruleCount || 0, icon: "fb-rule", one: ["fb.tags.rules-1", "1 mail rule"], many: ["fb.tags.rules", "{n} mail rules"] },
        ];
        return kinds.filter((k) => k.n > 0).map((k) => ({
            n: k.n, icon: k.icon,
            text: k.n === 1 ? fb.t(...k.one) : fb.t(k.many[0], k.many[1], { n: k.n }),
        }));
    }

    fb.tagManager = { open };
})();
