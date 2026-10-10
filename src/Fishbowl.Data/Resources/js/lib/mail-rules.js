/**
 * fb.mailRules — the "Mail rules" window of the Mail app (mail spec,
 * decision 11). Each rule of the workspace as a row — its name, what it
 * looks for and what it does, "Off" while it is off — with a "…" menu
 * (Edit…, Apply to the mail here, Turn off / on, Delete…) and "New rule" in
 * the toolbar; the write items only for whoever may manage (personal: the
 * owner; a space: its Admins). Every rule that meets a message applies — the
 * actions only add up, so there is no order. A condition is "contains", in
 * any case: "Weygandt" under From or to meets the name and the address.
 *
 *   fb.mailRules.open({ ws, canManage, accounts, onChanged })
 */
(function () {
    if (!window.fb) return;

    const t = (k, f, v) => fb.t(`fb.mail.${k}`, f, v);
    let win = null;
    let opts = {};

    const FIELDS = [
        { value: "anyone", label: () => t("rule-anyone", "From or to") },
        { value: "from", label: () => t("rule-from", "From") },
        { value: "to", label: () => t("rule-to", "To or Cc") },
        { value: "subject", label: () => t("rule-subject", "Subject") },
        { value: "list", label: () => t("rule-list", "Mailing list") },
        { value: "account", label: () => t("rule-account", "Account") },
    ];

    // Account only where there is more than one to tell apart.
    const fields = () => FIELDS.filter((f) => f.value !== "account" || (opts.accounts || []).length > 1);
    const fieldLabel = (v) => FIELDS.find((f) => f.value === v)?.label() ?? v;
    const accountName = (id) => (opts.accounts || []).find((a) => a.id === id)?.name ?? id;
    const valueText = (c) => (c.field === "account" ? accountName(c.value) : c.value);

    function ensureWindow() {
        if (win) return win;
        win = document.createElement("sac-window");
        win.setAttribute("escape-closes", "");
        win.id = "fb-mail-rules";
        win.classList.add("fb-window");
        win.setAttribute("width", "640px");
        win.setAttribute("height", "520px");
        win.setAttribute("top", "96px");
        win.setAttribute("left", "calc(50vw - 300px)");
        win.setAttribute("controls", "close");
        win.innerHTML = `
            <div class="toolbar wa-bar" slot="toolbar"></div>
            <div class="fb-block fb-mail-rules"></div>`;
        document.body.appendChild(win);
        return win;
    }

    async function open(o = {}) {
        opts = o;
        const w = ensureWindow();
        w.setAttribute("title", t("rules", "Mail rules"));
        const bar = w.querySelector(".wa-bar");
        bar.replaceChildren();
        if (opts.canManage) {
            const add = document.createElement("button");
            add.type = "button";
            add.className = "btn";
            add.innerHTML = `<sac-icon name="plus"></sac-icon><span>${t("rule-new", "New rule")}</span>`;
            add.addEventListener("click", () => edit(null));
            bar.appendChild(add);
            bar.slot = "toolbar";
        } else {
            bar.removeAttribute("slot");
        }
        await paint();
        requestAnimationFrame(() => w.open());
    }

    async function paint() {
        const list = win.querySelector(".fb-mail-rules");
        let rules = [];
        try { rules = await fb.api.mail.in(opts.ws).rules(); }
        catch { list.innerHTML = `<p class="muted">${t("rules-failed", "The rules can't be loaded right now.")}</p>`; return; }
        if (!rules.length) {
            list.innerHTML = `<div class="empty-state"><sac-icon name="fb-rule"></sac-icon><h3>${t("no-rules", "No rules yet")}</h3></div>`;
            return;
        }
        list.replaceChildren(...rules.map(row));
    }

    /** "From or to: Weygandt" — the conditions, joined as the rule joins them. */
    function conditionsText(r) {
        const join = r.matchAll ? " · " : ` ${t("rule-or", "or")} `;
        return r.conditions.map((c) => `${fieldLabel(c.field)}: ${valueText(c)}`).join(join);
    }

    /** "Tags helvetia, versicherungen · Mark read · Archive". */
    function actionsText(r) {
        const parts = [];
        if (r.addTags.length) parts.push(`${t("rule-tags-short", "Tags")} ${r.addTags.join(", ")}`);
        if (r.markRead) parts.push(t("rule-read-short", "Mark read"));
        if (r.archive) parts.push(t("rule-archive-short", "Archive"));
        return parts.join(" · ");
    }

    function row(r) {
        const el = document.createElement("div");
        el.className = "fb-row fb-mail-rule";
        el.classList.toggle("is-off", !r.enabled);
        el.innerHTML = `
            <sac-icon name="fb-rule"></sac-icon>
            <div class="fb-row-info">
                <div class="fb-row-name"></div>
                <div class="fb-row-meta"><span class="fb-mail-rule-when"></span><span class="fb-mail-rule-then"></span></div>
            </div>`;
        el.querySelector(".fb-row-name").textContent = r.enabled ? r.name : `${r.name} · ${t("rule-off", "Off")}`;
        el.querySelector(".fb-mail-rule-when").textContent = conditionsText(r);
        el.querySelector(".fb-mail-rule-then").textContent = `→ ${actionsText(r)}`;
        if (!opts.canManage) return el;

        // The row edits — a click or Enter —, the "…" holds the rest.
        el.tabIndex = 0;
        el.addEventListener("click", (e) => { if (!e.target.closest("sac-menu")) edit(r); });
        el.addEventListener("keydown", (e) => { if (e.key === "Enter" && e.target === el) { e.preventDefault(); edit(r); } });

        const menu = document.createElement("sac-menu");
        menu.innerHTML = `<button slot="trigger" type="button" class="icon-btn" aria-label="${t("account-more", "More")}" title="${t("account-more", "More")}"><sac-icon name="more"></sac-icon></button>`;
        const item = (action, label, icon) => {
            const b = document.createElement("button");
            b.type = "button";
            b.dataset.action = action;
            b.innerHTML = `<sac-icon name="${icon}"></sac-icon> `;
            b.append(label);
            menu.appendChild(b);
        };
        item("edit", t("rule-edit", "Edit…"), "pencil");
        item("apply", t("rule-apply", "Apply to the mail here"), "play");
        item("toggle", r.enabled ? t("rule-turn-off", "Turn off") : t("rule-turn-on", "Turn on"), r.enabled ? "pause" : "play");
        item("delete", t("rule-delete", "Delete…"), "trash");
        menu.addEventListener("sac:select", (e) => act(e.detail.action, r));
        el.appendChild(menu);
        return el;
    }

    async function act(action, r) {
        const api = fb.api.mail.in(opts.ws);
        try {
            if (action === "edit") return edit(r);
            if (action === "apply") await apply(r);
            else if (action === "toggle") await api.updateRule(r.id, { ...body(r), enabled: !r.enabled });
            else if (action === "delete") {
                const answer = await sac.dialog.confirm({
                    title: t("rule-delete-title", "Delete the rule {name}?", { name: r.name }),
                    message: t("rule-delete-msg", "Mail that comes in from now on no longer meets it. What it did stays — tags, read, archived."),
                    buttons: [
                        { action: "cancel", label: fb.t("fb.common.cancel", "Cancel") },
                        { action: "delete", label: t("rule-delete-ok", "Delete"), kind: "destructive" },
                    ],
                });
                if (answer !== "delete") return;
                await api.removeRule(r.id);
            }
        } catch (err) {
            sac.toast?.(fb.errors.text(err, t("account-failed", "That didn't work.")), { kind: "error" });
        }
        await paint();
    }

    const body = (r) => ({
        name: r.name, enabled: r.enabled, matchAll: r.matchAll, conditions: r.conditions,
        addTags: r.addTags, archive: r.archive, markRead: r.markRead,
    });

    /** Runs the rule over the mail already here and says what it did. */
    async function apply(r) {
        const res = await fb.api.mail.in(opts.ws).applyRule(r.id);
        const n = res.conversations;
        sac.toast?.(n === 1 ? t("rule-applied-1", "Applied to 1 conversation.") : t("rule-applied", "Applied to {n} conversations.", { n: fb.format.num(n) }));
        if (res.archiveFolderMissing)
            sac.toast?.(t("rule-no-archive", "An account has no archive folder — its mail stayed in the inbox."), { kind: "warning" });
        opts.onChanged?.();
    }

    function conditionRow(c) {
        const el = document.createElement("div");
        el.className = "fb-rule-cond";
        el.innerHTML = `
            <sac-select data-field aria-label="${t("rule-field", "Field")}">${fields().map((f) => `<option value="${f.value}">${f.label()}</option>`).join("")}</sac-select>
            <span class="fb-rule-value"></span>
            <button type="button" class="icon-btn" data-remove aria-label="${t("rule-remove-cond", "Remove condition")}" title="${t("rule-remove-cond", "Remove condition")}"><sac-icon name="close"></sac-icon></button>`;
        const field = el.querySelector("[data-field]");
        field.setAttribute("value", c.field);
        const paintValue = (value) => {
            const slot = el.querySelector(".fb-rule-value");
            if (field.value === "account") {
                const sel = document.createElement("sac-select");
                sel.dataset.value = "";
                sel.setAttribute("aria-label", t("rule-account", "Account"));
                sel.innerHTML = (opts.accounts || []).map((a) => `<option value="${a.id}"></option>`).join("");
                (opts.accounts || []).forEach((a, i) => { sel.querySelectorAll("option")[i].textContent = a.name; });
                sel.setAttribute("value", (opts.accounts || []).some((a) => a.id === value) ? value : (opts.accounts?.[0]?.id ?? ""));
                slot.replaceChildren(sel);
            } else {
                const input = document.createElement("input");
                input.type = "text";
                input.dataset.value = "";
                input.maxLength = 200;
                input.spellcheck = false;
                input.setAttribute("aria-label", t("rule-contains", "contains"));
                input.placeholder = t("rule-contains", "contains");
                input.value = value ?? "";
                slot.replaceChildren(input);
            }
        };
        paintValue(c.value);
        let was = c.field;
        for (const type of ["change", "sac:change"])
            field.addEventListener(type, () => {
                // From text to the account list and back: the text doesn't carry over.
                if ((was === "account") !== (field.value === "account")) paintValue("");
                was = field.value;
            });
        return el;
    }

    function edit(r) {
        const isNew = !r;
        const rule = r ?? { name: "", enabled: true, matchAll: true, conditions: [{ field: "anyone", value: "" }], addTags: [], archive: false, markRead: false };
        return fb.mailAccounts.formDialog({
            title: isNew ? t("rule-new-title", "New mail rule") : t("rule-edit-title", "Edit {name}", { name: rule.name }),
            primary: isNew ? t("rule-create", "Create") : fb.t("fb.common.save", "Save"),
            width: "560px",
            html: `
                <label><span>${t("rule-name", "Name")} <span class="muted">${fb.t("fb.admin.optional", "(optional)")}</span></span>
                    <input name="name" type="text" maxlength="80"></label>
                <label class="fb-rule-head">
                    <span>${t("rule-when", "When")}</span>
                    <sac-select name="match" aria-label="${t("rule-when", "When")}">
                        <option value="all">${t("rule-all", "all of these are met")}</option>
                        <option value="any">${t("rule-any", "any of these is met")}</option>
                    </sac-select>
                </label>
                <div class="fb-rule-conds"></div>
                <div><button type="button" class="btn" data-add-cond><sac-icon name="plus"></sac-icon><span>${t("rule-add-cond", "Add condition")}</span></button></div>
                <label class="fb-rule-head"><span>${t("rule-then", "Then")}</span></label>
                <label>${t("rule-add-tags", "Add tags")}
                    <sac-chip-input name="tags" add-label="${fb.t("fb.notes.add-tag", "Add tag")}" allow-create></sac-chip-input></label>
                <label class="fb-check"><input type="checkbox" name="markRead"> ${t("rule-mark-read", "Mark as read")}</label>
                <label class="fb-check"><input type="checkbox" name="archive"> ${t("rule-archive", "Archive the conversation")}</label>
                <label class="fb-check fb-rule-apply"><input type="checkbox" name="applyNow"> ${t("rule-apply-now", "Also apply to the mail already here")}</label>`,
            // Values via properties, never markup.
            init: (form) => {
                form.querySelector('[name="name"]').value = rule.name;
                const match = form.querySelector('[name="match"]');
                match.setAttribute("value", rule.matchAll ? "all" : "any");
                const conds = form.querySelector(".fb-rule-conds");
                const sync = () => {
                    // "all / any" only says something with two or more.
                    match.hidden = conds.children.length < 2;
                    for (const b of conds.querySelectorAll("[data-remove]")) b.disabled = conds.children.length < 2;
                };
                conds.replaceChildren(...rule.conditions.map(conditionRow));
                conds.addEventListener("click", (e) => {
                    const remove = e.target.closest("[data-remove]");
                    if (!remove) return;
                    remove.closest(".fb-rule-cond").remove();
                    sync();
                    conds.querySelector("[data-value]")?.focus();
                });
                form.querySelector("[data-add-cond]").addEventListener("click", () => {
                    const c = conditionRow({ field: "anyone", value: "" });
                    conds.appendChild(c);
                    sync();
                    c.querySelector("input[data-value]")?.focus();
                });
                sync();

                const tags = form.querySelector('[name="tags"]');
                tags.value = rule.addTags;
                fb.tags.all().then((all) => {
                    tags.suggestions = all.filter((x) => x.userAssignable !== false)
                        .map((x) => ({ name: x.name, color: x.color, count: x.mailCount }));
                }).catch(() => { /* no suggestions — typing still works */ });
                tags.addEventListener("sac:create", async (e) => {
                    try { await fb.api.tags.upsertColor(e.detail.name, e.detail.color); fb.tags.invalidate(); }
                    catch { /* the rule's save makes the tag anyway */ }
                });
                form.querySelector('[name="markRead"]').checked = rule.markRead;
                form.querySelector('[name="archive"]').checked = rule.archive;
                // A new rule is usually wanted for what is already here too.
                form.querySelector('[name="applyNow"]').checked = isNew;
                requestAnimationFrame(() => conds.querySelector("input[data-value]")?.focus());
            },
            submit: async (dlg) => {
                const form = dlg.querySelector(".fb-mail-form");
                const conditions = [...form.querySelectorAll(".fb-rule-cond")].map((el) => ({
                    field: el.querySelector("[data-field]").value,
                    value: (el.querySelector("[data-value]").value || "").trim(),
                })).filter((c) => c.value);
                if (!conditions.length) return t("rule-need-cond", "Enter what the mail should contain.");
                const next = {
                    name: form.querySelector('[name="name"]').value.trim() || valueText(conditions[0]),
                    enabled: rule.enabled,
                    matchAll: form.querySelector('[name="match"]').value !== "any",
                    conditions,
                    addTags: form.querySelector('[name="tags"]').value,
                    markRead: form.querySelector('[name="markRead"]').checked,
                    archive: form.querySelector('[name="archive"]').checked,
                };
                if (!next.addTags.length && !next.markRead && !next.archive)
                    return t("rule-need-action", "Choose what the rule does: tags, read or archive.");
                const api = fb.api.mail.in(opts.ws);
                let saved;
                try { saved = isNew ? await api.addRule(next) : await api.updateRule(rule.id, next); }
                catch (err) { return fb.errors.text(err, t("rule-save-failed", "The rule couldn't be saved.")); }
                if (form.querySelector('[name="applyNow"]').checked) {
                    try { await apply(saved); }
                    catch (err) { sac.toast?.(fb.errors.text(err, t("rule-apply-failed", "The rule couldn't be applied.")), { kind: "error" }); }
                }
                await paint();
                return null;
            },
        });
    }

    fb.mailRules = { open };
})();
