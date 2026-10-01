/**
 * fb.systemSettings — the instance's configuration rows for the System app
 * (fb-system-view: one tab per group), admins only.
 *
 *   fb.systemSettings.sections(rows) → [{ id, title, rows }]  (non-empty, in order)
 *   fb.systemSettings.row(r, { refresh, saved }) → the row's element
 *
 * Rendered from GET /api/v1/admin/config (the server's ConfigSchema.Editable),
 * so a new key shows up here without a UI change: grouped by its prefix,
 * with an input that fits the value (a choice list, GB for byte sizes, a
 * number, or text). Each row saves on its own; the server validates and its
 * message shows under the row. Secrets are write-only: the page says "Set"
 * and offers Replace, never the value. Keys that bind at boot say that a
 * restart is needed once they're saved.
 */
(function () {
    const GB = 1024 ** 3;

    // Prefix → tab, in tab order (the id is the tab's URL part). Anything
    // unknown lands in "Other".
    const GROUPS = [
        { id: "sign-in",      prefixes: ["Auth:", "Google:"],    title: "Sign-in" },
        { id: "files",        prefixes: ["Files:"],              title: "Files & quotas" },
        { id: "export",       prefixes: ["Archive:", "Export:"], title: "Export & archive" },
        { id: "apps",         prefixes: ["Apps:"],               title: "Apps" },
        { id: "integrations", prefixes: ["Discord:"],            title: "Integrations" },
        { id: "digest",       prefixes: ["Digest:"],             title: "Daily digest" },
        { id: "logging",      prefixes: ["Logging:"],            title: "Logging" },
        { id: "https",        prefixes: ["Acme:"],               title: "HTTPS certificate" },
    ];

    // Keys with a fixed set of values get a choice list; the first entry of
    // `options` is the server's default and is labelled so.
    const CHOICES = {
        "Auth:SignUp":     ["approval", "open", "closed"],
        "Apps:Install":    ["everyone", "admins", "off"],
        "Apps:Trusted":    ["admins", "everyone", "off"],
        "Logging:Format":  ["plain", "json"],
        "Digest:Enabled":  ["false", "true"],
        "Acme:AcceptTos":  ["false", "true"],
    };

    // Readable names; anything missing falls back to the key's last part.
    const LABELS = {
        "Auth:SignUp": "New accounts",
        "Auth:AllowedEmailDomains": "Allowed e-mail domains",
        "Google:ClientId": "Google client id",
        "Google:ClientSecret": "Google client secret",
        "Files:DefaultUserQuotaBytes": "Default storage per account",
        "Archive:RetentionDays": "Keep archived spaces (days)",
        "Export:CombinedMaxBytes": "One-ZIP export up to",
        "Apps:Install": "Who may install apps",
        "Apps:Trusted": "Who may trust an app",
        "Apps:AllowedOrigins": "Allowed app origins",
        "Apps:StoreOwners": "App Store owners",
        "Apps:StoreTopic": "App Store topic",
        "Discord:BotToken": "Discord bot token",
        "Digest:Enabled": "Send the daily digest",
        "Digest:Hour": "Digest hour",
        "Logging:RetentionDays": "Keep log files (days)",
        "Logging:Format": "Log format",
        "Acme:Domains": "Domains",
        "Acme:Email": "Contact e-mail",
        "Acme:AcceptTos": "Accept the Let's Encrypt terms",
    };

    const kindOf = (key) => CHOICES[key] ? "choice"
        : /Bytes$/.test(key) ? "bytes"
        : /(Days|Hour|Hours|Minutes|Count)$/.test(key) ? "number"
        : "text";

    // Label, description, group title and choice names in the page's
    // language: "fb.admin.cfg.<Key>", "fb.admin.cfg-desc.<Key>",
    // "fb.admin.cfg-group.<English title>", "fb.admin.cfg-choice.<value>".
    // The key itself (shown beside the label) stays as it is — an identifier.
    const labelOf = (key) => fb.t(`fb.admin.cfg.${key}`, LABELS[key]
        || key.split(":").pop().replace(/([a-z])([A-Z])/g, "$1 $2"));
    const descOf = (r) => r.description ? fb.t(`fb.admin.cfg-desc.${r.key}`, r.description) : "";
    const groupOf = (title) => fb.t(`fb.admin.cfg-group.${title}`, title);
    const choiceOf = (v) => fb.t(`fb.admin.cfg-choice.${v}`, v);

    function sections(rows) {
        const all = [...GROUPS.map((g) => ({ ...g, rows: [] })), { id: "other", prefixes: [], title: "Other", rows: [] }];
        for (const r of rows || []) {
            const g = all.find((s) => s.prefixes.some((p) => r.key.startsWith(p))) || all[all.length - 1];
            g.rows.push(r);
        }
        return all.filter((s) => s.rows.length).map((s) => ({ id: s.id, title: groupOf(s.title), rows: s.rows }));
    }

    // One setting: label, key, description, the input that fits it and its
    // own Save. `on.refresh()` re-renders after a change, `on.saved(res,
    // label, cleared)` reports it.
    function row(r, on) {
        const row = document.createElement("div");
        row.className = "cfg-row";
        row.dataset.key = r.key;
        row.innerHTML = `
            <div class="cfg-head"><span class="cfg-label"></span><span class="cfg-key"></span></div>
            <p class="cfg-desc"></p>
            <div class="toolbar cfg-edit"></div>
            <p class="cfg-error" role="alert" hidden></p>`;
        row.querySelector(".cfg-label").textContent = labelOf(r.key);
        row.querySelector(".cfg-key").textContent = r.key;
        row.querySelector(".cfg-desc").textContent = descOf(r);
        const edit = row.querySelector(".cfg-edit");
        const errEl = row.querySelector(".cfg-error");
        const showErr = (text) => { errEl.textContent = text || ""; errEl.hidden = !text; };

        const button = (label, cls = "btn") => {
            const b = document.createElement("button");
            b.type = "button";
            b.className = cls;
            b.textContent = label;
            return b;
        };

        const save = async (value) => {
            showErr("");
            try {
                const res = await fb.api.admin.setConfig(r.key, value);
                on.saved(res, labelOf(r.key));
                await on.refresh();
            } catch (err) {
                showErr(fb.errors.text(err, fb.t("fb.admin.cfg-save-failed", "Couldn't save this setting.")));
            }
        };
        const clear = async () => {
            showErr("");
            try {
                const res = await fb.api.admin.clearConfig(r.key);
                on.saved(res, labelOf(r.key), true);
                await on.refresh();
            } catch (err) {
                showErr(fb.errors.text(err, fb.t("fb.admin.cfg-reset-failed", "Couldn't reset this setting.")));
            }
        };

        // Secrets: never shown. "Set" / "Not set", and Replace opens a field.
        if (r.secret) {
            const state = document.createElement("span");
            state.className = "cfg-state" + (r.isSet ? " set" : "");
            state.textContent = r.isSet ? fb.t("fb.admin.cfg-set", "Set") : fb.t("fb.admin.cfg-not-set", "Not set");
            const replace = button(r.isSet ? fb.t("fb.admin.cfg-replace", "Replace") : fb.t("fb.admin.cfg-set-action", "Set"));
            replace.addEventListener("click", () => {
                const input = document.createElement("input");
                input.type = "password";
                input.autocomplete = "new-password";
                input.setAttribute("aria-label", labelOf(r.key));
                const ok = button(fb.t("fb.admin.save", "Save"), "btn primary");
                const cancel = button(fb.t("fb.common.cancel", "Cancel"));
                ok.addEventListener("click", () => { if (input.value.trim()) save(input.value.trim()); });
                input.addEventListener("keydown", (e) => { if (e.key === "Enter" && input.value.trim()) save(input.value.trim()); });
                cancel.addEventListener("click", () => on.refresh());
                edit.replaceChildren(input, ok, cancel);
                input.focus();
            });
            edit.append(state, replace);
            if (r.isSet) {
                const remove = button(fb.t("fb.admin.cfg-remove", "Remove"));
                remove.addEventListener("click", clear);
                edit.appendChild(remove);
            }
            return row;
        }

        const kind = kindOf(r.key);
        let input, read;
        if (kind === "choice") {
            const wrap = document.createElement("span");
            wrap.className = "select";
            const sel = document.createElement("select");
            sel.setAttribute("aria-label", labelOf(r.key));
            CHOICES[r.key].forEach((v, i) => {
                const o = document.createElement("option");
                o.value = v;
                o.textContent = i === 0 ? fb.t("fb.admin.cfg-default-choice", "{value} (default)", { value: choiceOf(v) }) : choiceOf(v);
                sel.appendChild(o);
            });
            sel.value = r.isSet ? r.value : CHOICES[r.key][0];
            wrap.appendChild(sel);
            input = sel;
            read = () => sel.value;
            edit.appendChild(wrap);
        } else {
            input = document.createElement("input");
            input.setAttribute("aria-label", labelOf(r.key));
            if (kind === "bytes" || kind === "number") {
                input.type = "number";
                input.min = "0";
                input.className = "cfg-num";
                input.inputMode = "decimal";
                if (kind === "bytes") input.step = "0.5";
            } else {
                input.type = "text";
                input.spellcheck = false;
                input.autocomplete = "off";
            }
            if (r.isSet) input.value = kind === "bytes" ? String(+(Number(r.value) / GB).toFixed(2)) : r.value;
            else input.placeholder = fb.t("fb.admin.cfg-default", "Default");
            read = () => {
                const t = input.value.trim();
                if (!t) return "";
                if (kind === "bytes") {
                    const n = Number(t);
                    return Number.isFinite(n) && n >= 0 ? String(Math.round(n * GB)) : "bad";
                }
                return t;
            };
            edit.appendChild(input);
            if (kind === "bytes") {
                const unit = document.createElement("span");
                unit.className = "cfg-unit";
                unit.textContent = fb.t("fb.admin.cfg-gb", "GB · 0 = unlimited");
                edit.appendChild(unit);
            }
        }

        const saveBtn = button(fb.t("fb.admin.save", "Save"), "btn primary");
        saveBtn.disabled = true;
        const initial = read();
        const sync = () => { saveBtn.disabled = read() === initial; };
        input.addEventListener("input", sync);
        input.addEventListener("change", sync);
        const commit = () => {
            const v = read();
            if (v === initial) return;
            if (v === "bad") { showErr(fb.t("fb.admin.cfg-gb-rule", "Enter a number of GB, 0 for unlimited.")); return; }
            if (v === "") { if (r.isSet) clear(); else sync(); return; }
            save(v);
        };
        saveBtn.addEventListener("click", commit);
        if (input.tagName === "INPUT") input.addEventListener("keydown", (e) => { if (e.key === "Enter") commit(); });
        edit.appendChild(saveBtn);
        if (r.isSet) {
            const reset = button(fb.t("fb.admin.cfg-use-default", "Use default"));
            reset.addEventListener("click", clear);
            edit.appendChild(reset);
        }
        return row;
    }

    fb.systemSettings = { sections, row };
})();
