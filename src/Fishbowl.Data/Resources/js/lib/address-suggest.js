/**
 * fb.addressSuggest — suggestions under a recipients field (To, Cc, Bcc).
 * The field stays one line of text with the addresses separated by commas
 * (never chips: an address stays text you can correct in place, decided
 * 2026-10-08). The address being typed, the one around the caret, is looked
 * up, and a pick replaces it with "Name <address>, ".
 *
 *   const s = fb.addressSuggest.attach(input, { search: (q) => api.recipients(q) });
 *   s.destroy();
 *
 * Keys: ↓ / ↑ walk the list (the first is marked), Enter or Tab takes the
 * marked one, and Escape closes the list. The input keeps the focus
 * (role="combobox", aria-activedescendant). The list is the kit's dropdown
 * level as light DOM: fixed, in the top layer (popover), under the field, or
 * above it when there is more room there.
 */
(function () {
    let uid = 0;

    /** The address around the caret: where it starts and ends in the text.
     *  Commas and semicolons outside quotes and <…> separate addresses. */
    function tokenAt(text, caret) {
        const seps = [];
        let quoted = false, angle = false;
        for (let i = 0; i < text.length; i++) {
            const ch = text[i];
            if (ch === '"') quoted = !quoted;
            else if (ch === "<" && !quoted) angle = true;
            else if (ch === ">" && !quoted) angle = false;
            else if ((ch === "," || ch === ";") && !quoted && !angle) seps.push(i);
        }
        let start = 0;
        for (const s of seps) if (s < caret) start = s + 1;
        const end = seps.find((s) => s >= caret) ?? text.length;
        return { start, end, text: text.slice(start, end) };
    }

    /** "Ada <ada@x.org>" or "ada@x.org" → "ada@x.org". */
    const addressOf = (part) => {
        const m = /<([^>]*)>/.exec(part);
        return (m ? m[1] : part).trim().toLowerCase();
    };

    function attach(input, { search, delay = 120 }) {
        const id = `fb-suggest-${++uid}`;
        const list = document.createElement("div");
        list.className = "fb-suggest";
        list.id = id;
        list.setAttribute("role", "listbox");
        list.setAttribute("popover", "manual");
        list.setAttribute("aria-label", fb.t("fb.mail.suggest-label", "Suggestions"));
        input.insertAdjacentElement("afterend", list);
        input.setAttribute("role", "combobox");
        input.setAttribute("aria-autocomplete", "list");
        input.setAttribute("aria-expanded", "false");
        input.setAttribute("aria-controls", id);

        let items = [];
        let hl = -1;
        let token = null;
        let timer = null;
        let seq = 0;

        const isOpen = () => list.matches(":popover-open");

        function close() {
            clearTimeout(timer);
            seq++;
            if (isOpen()) list.hidePopover();
            input.setAttribute("aria-expanded", "false");
            input.removeAttribute("aria-activedescendant");
            items = [];
            hl = -1;
        }

        function place() {
            const r = input.getBoundingClientRect();
            const vh = window.visualViewport?.height ?? innerHeight;
            const below = vh - r.bottom - 12, above = r.top - 12;
            const up = below < 180 && above > below;
            const width = Math.min(Math.max(r.width, 280), innerWidth - 16);
            list.style.left = `${Math.max(8, Math.min(r.left, innerWidth - width - 8))}px`;
            list.style.width = `${width}px`;
            list.style.maxHeight = `${Math.max(120, Math.min(320, up ? above : below))}px`;
            list.style.top = up ? "auto" : `${r.bottom + 4}px`;
            list.style.bottom = up ? `${innerHeight - r.top + 4}px` : "auto";
        }

        function row(it, i) {
            const el = document.createElement("div");
            el.className = "fb-suggest-row";
            el.id = `${id}-${i}`;
            el.setAttribute("role", "option");
            const icon = document.createElement("sac-icon");
            icon.setAttribute("name", !it.contactId ? "mail" : it.contactKind === "organisation" ? "users" : "user");
            const name = document.createElement("span");
            name.className = "fb-suggest-name";
            name.textContent = it.name || it.address;
            const sub = document.createElement("span");
            sub.className = "fb-suggest-sub";
            sub.textContent = [it.name ? it.address : null, it.label, it.organisation].filter(Boolean).join(" · ");
            el.append(icon, name, sub);
            // The input keeps the focus: a press on a row doesn't take it.
            el.addEventListener("pointerdown", (e) => e.preventDefault());
            el.addEventListener("click", () => pick(i));
            el.addEventListener("pointermove", () => { if (hl !== i) mark(i, false); });
            return el;
        }

        function mark(i, scroll = true) {
            hl = i;
            list.querySelectorAll(".fb-suggest-row").forEach((el, n) => {
                el.classList.toggle("is-active", n === i);
                el.setAttribute("aria-selected", String(n === i));
            });
            const el = list.children[i];
            if (el) {
                input.setAttribute("aria-activedescendant", el.id);
                if (scroll) el.scrollIntoView({ block: "nearest" });
            }
        }

        function pick(i) {
            const it = items[i];
            if (!it || !token) return;
            const v = input.value;
            const before = v.slice(0, token.start);
            const rest = v.slice(token.end).replace(/^\s*[,;]?\s*/, "");
            const lead = before && !/\s$/.test(before) ? " " : "";
            const head = before + lead + it.text + ", ";
            input.value = head + rest;
            input.setSelectionRange(head.length, head.length);
            close();
            input.dispatchEvent(new Event("input", { bubbles: true }));
            close();   // the input just fired looked the empty address up
        }

        async function lookup() {
            token = tokenAt(input.value, input.selectionStart ?? input.value.length);
            const q = token.text.trim();
            // Nothing typed, or a whole "Name <address>": nothing to look up.
            if (!q || /<[^>]*@[^>]*>/.test(q)) { close(); return; }
            const mine = ++seq;
            let found = [];
            try { found = await search(q); } catch { found = []; }
            if (mine !== seq || document.activeElement !== input) return;
            const others = new Set(fb.mailCompose.splitAddresses(input.value.slice(0, token.start) + input.value.slice(token.end)).map(addressOf));
            items = (found || []).filter((it) => !others.has(String(it.address).toLowerCase()));
            if (!items.length) { close(); return; }
            list.replaceChildren(...items.map(row));
            place();
            if (!isOpen()) list.showPopover();
            input.setAttribute("aria-expanded", "true");
            mark(0);
        }

        function onInput() {
            clearTimeout(timer);
            timer = setTimeout(lookup, delay);
        }

        function onKey(e) {
            if (e.isComposing || !isOpen() || !items.length) return;
            if (e.key === "ArrowDown") { e.preventDefault(); mark((hl + 1) % items.length); }
            else if (e.key === "ArrowUp") { e.preventDefault(); mark((hl - 1 + items.length) % items.length); }
            else if ((e.key === "Enter" || (e.key === "Tab" && !e.shiftKey)) && hl >= 0) {
                e.preventDefault();
                e.stopPropagation();
                pick(hl);
            } else if (e.key === "Escape") {
                e.preventDefault();
                e.stopPropagation();
                close();
            }
        }

        const onBlur = () => close();
        const onMove = () => { if (isOpen()) place(); };
        input.addEventListener("input", onInput);
        input.addEventListener("keydown", onKey);
        input.addEventListener("blur", onBlur);
        window.addEventListener("resize", onMove);
        window.addEventListener("scroll", onMove, true);

        return {
            close,
            destroy() {
                close();
                input.removeEventListener("input", onInput);
                input.removeEventListener("keydown", onKey);
                input.removeEventListener("blur", onBlur);
                window.removeEventListener("resize", onMove);
                window.removeEventListener("scroll", onMove, true);
                list.remove();
                for (const a of ["role", "aria-autocomplete", "aria-expanded", "aria-controls", "aria-activedescendant"]) input.removeAttribute(a);
            },
        };
    }

    window.fb = window.fb || {};
    fb.addressSuggest = { attach, tokenAt };
})();
