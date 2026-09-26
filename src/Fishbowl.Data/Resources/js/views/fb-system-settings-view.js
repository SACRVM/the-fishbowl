/**
 * System settings (#/admin/settings): the instance's configuration, for
 * admins only. Registered by fb.accounts.registerAdminRoutes once /me says
 * the user is an admin — for anyone else the route, its nav entry and its
 * desktop tile don't exist.
 *
 * Rendered from GET /api/v1/admin/config (the server's ConfigSchema.Editable),
 * so a new key shows up here without a UI change: grouped by its prefix,
 * with an input that fits the value (a choice list, GB for byte sizes, a
 * number, or text). Each row saves on its own; the server validates and its
 * message shows under the row. Secrets are write-only: the page says "Set"
 * and offers Replace, never the value. Keys that bind at boot say that a
 * restart is needed once they're saved.
 *
 * Personal only: in a space the page says so and points back to Personal.
 */
(function () {
    const GB = 1024 ** 3;

    // Prefix → section, in page order. Anything unknown lands in "Other".
    const GROUPS = [
        { prefixes: ["Auth:", "Google:"], title: "Sign-in" },
        { prefixes: ["Files:"],           title: "Files & quotas" },
        { prefixes: ["Archive:", "Export:"], title: "Export & archive" },
        { prefixes: ["Apps:"],            title: "Apps" },
        { prefixes: ["Discord:"],         title: "Integrations" },
        { prefixes: ["Digest:"],          title: "Daily digest" },
        { prefixes: ["Logging:"],         title: "Logging" },
        { prefixes: ["Acme:"],            title: "HTTPS certificate" },
    ];

    // Keys with a fixed set of values get a choice list; the first entry of
    // `options` is the server's default and is labelled so.
    const CHOICES = {
        "Auth:SignUp":     ["approval", "open", "closed"],
        "Apps:Install":    ["everyone", "admins", "off"],
        "Apps:Trusted":    ["everyone", "admins", "off"],
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

    const labelOf = (key) => LABELS[key]
        || key.split(":").pop().replace(/([a-z])([A-Z])/g, "$1 $2");

    class FbSystemSettingsView extends HTMLElement {
        connectedCallback() {
            this.render();
            this.refresh();
        }

        render() {
            this.innerHTML = `
                <style>
                    fb-system-settings-view { display: block; padding: clamp(1.25rem, 5vw, 40px) clamp(1rem, 5vw, 48px); max-width: 860px; }
                    fb-system-settings-view header { margin-bottom: 24px; }
                    fb-system-settings-view h1 {
                        font-family: 'Outfit', 'Inter', sans-serif;
                        font-size: 28px;
                        font-weight: 700;
                        margin: 0 0 6px;
                        color: var(--text);
                    }
                    fb-system-settings-view .subtitle { color: var(--text-muted); font-size: 14px; margin: 0; }
                    fb-system-settings-view .restart-note {
                        margin: 0 0 20px;
                        padding: 10px 14px;
                        border: 1px solid var(--border);
                        border-radius: var(--radius-l);
                        background: var(--panel);
                        color: var(--accent-warm);
                        font-size: 13px;
                    }
                    fb-system-settings-view .panel {
                        padding: 18px 20px 6px;
                        background: var(--panel);
                        border: 1px solid var(--border);
                        border-radius: var(--radius-l);
                        margin-bottom: 20px;
                    }
                    fb-system-settings-view .panel h2 {
                        font-size: 12px;
                        font-weight: 600;
                        text-transform: uppercase;
                        letter-spacing: 0.06em;
                        color: var(--text-muted);
                        margin: 0 0 4px;
                    }
                    fb-system-settings-view .panel p { margin: 0 0 12px; color: var(--text); font-size: 14px; }
                    fb-system-settings-view .cfg-row { padding: 14px 0; }
                    fb-system-settings-view .cfg-row + .cfg-row { border-top: 1px solid var(--border); }
                    fb-system-settings-view .cfg-head { display: flex; align-items: baseline; gap: 10px; flex-wrap: wrap; }
                    fb-system-settings-view .cfg-label { font-size: 14px; font-weight: 600; color: var(--text); }
                    fb-system-settings-view .cfg-key { font-family: var(--font-mono); font-size: 12px; color: var(--text-muted); }
                    fb-system-settings-view .cfg-desc { margin: 4px 0 10px; font-size: 13px; color: var(--text-muted); }
                    fb-system-settings-view .cfg-edit { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
                    fb-system-settings-view .cfg-edit input { flex: 1; min-width: 12rem; }
                    fb-system-settings-view .cfg-edit input.cfg-num { flex: 0 0 8rem; min-width: 0; }
                    fb-system-settings-view .cfg-edit .btn { width: auto; }
                    fb-system-settings-view .cfg-unit { font-size: 13px; color: var(--text-muted); }
                    fb-system-settings-view .cfg-state { font-size: 13px; color: var(--text-muted); }
                    fb-system-settings-view .cfg-state.set { color: var(--ok); }
                    fb-system-settings-view .cfg-error { margin: 8px 0 0; font-size: 13px; color: var(--danger); }
                </style>
                <header>
                    <h1>System settings</h1>
                    <p class="subtitle">How this Fishbowl runs, for everyone on it. Each setting saves on its own.</p>
                </header>
                <p class="restart-note" hidden>Some saved changes take effect after the next restart of Fishbowl.</p>
                <div id="settings-body"></div>
            `;
        }

        async refresh() {
            const mount = this.querySelector("#settings-body");
            if (!mount) return;

            if (sac.scope.get().type === "scoped") {
                mount.innerHTML = `
                    <div class="panel">
                        <p>System settings belong to the whole Fishbowl — open them from your personal workspace.</p>
                        <button type="button" class="btn" id="to-personal">Open in Personal</button>
                    </div>`;
                mount.querySelector("#to-personal").addEventListener("click", () => sac.router.navigate("#/admin/settings"));
                return;
            }

            let rows;
            try {
                rows = await fb.api.admin.config();
            } catch (err) {
                console.warn("[fb-system-settings-view] load failed:", err?.status);
                mount.innerHTML = `<div class="panel"><p>The settings can't be loaded right now.</p></div>`;
                return;
            }

            const sections = GROUPS.map((g) => ({ ...g, rows: [] }));
            const other = { title: "Other", rows: [] };
            for (const r of rows || []) {
                const g = sections.find((s) => s.prefixes.some((p) => r.key.startsWith(p)));
                (g || other).rows.push(r);
            }
            const panels = [];
            for (const s of [...sections, other]) {
                if (!s.rows.length) continue;
                const panel = document.createElement("section");
                panel.className = "panel";
                panel.dataset.section = s.title;
                const h = document.createElement("h2");
                h.textContent = s.title;
                panel.appendChild(h);
                for (const r of s.rows) panel.appendChild(this._row(r));
                panels.push(panel);
            }
            mount.replaceChildren(...panels);
        }

        _row(r) {
            const row = document.createElement("div");
            row.className = "cfg-row";
            row.dataset.key = r.key;
            row.innerHTML = `
                <div class="cfg-head"><span class="cfg-label"></span><span class="cfg-key"></span></div>
                <p class="cfg-desc"></p>
                <div class="cfg-edit"></div>
                <p class="cfg-error" role="alert" hidden></p>`;
            row.querySelector(".cfg-label").textContent = labelOf(r.key);
            row.querySelector(".cfg-key").textContent = r.key;
            row.querySelector(".cfg-desc").textContent = r.description || "";
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
                    this._saved(res, labelOf(r.key));
                    await this.refresh();
                } catch (err) {
                    showErr(this._error(err, "Couldn't save this setting."));
                }
            };
            const clear = async () => {
                showErr("");
                try {
                    const res = await fb.api.admin.clearConfig(r.key);
                    this._saved(res, labelOf(r.key), true);
                    await this.refresh();
                } catch (err) {
                    showErr(this._error(err, "Couldn't reset this setting."));
                }
            };

            // Secrets: never shown. "Set" / "Not set", and Replace opens a field.
            if (r.secret) {
                const state = document.createElement("span");
                state.className = "cfg-state" + (r.isSet ? " set" : "");
                state.textContent = r.isSet ? "Set" : "Not set";
                const replace = button(r.isSet ? "Replace" : "Set");
                replace.addEventListener("click", () => {
                    const input = document.createElement("input");
                    input.type = "password";
                    input.autocomplete = "new-password";
                    input.setAttribute("aria-label", labelOf(r.key));
                    const ok = button("Save", "btn primary");
                    const cancel = button("Cancel");
                    ok.addEventListener("click", () => { if (input.value.trim()) save(input.value.trim()); });
                    input.addEventListener("keydown", (e) => { if (e.key === "Enter" && input.value.trim()) save(input.value.trim()); });
                    cancel.addEventListener("click", () => this.refresh());
                    edit.replaceChildren(input, ok, cancel);
                    input.focus();
                });
                edit.append(state, replace);
                if (r.isSet) {
                    const remove = button("Remove");
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
                    o.textContent = i === 0 ? `${v} (default)` : v;
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
                else input.placeholder = "Default";
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
                    unit.textContent = "GB · 0 = unlimited";
                    edit.appendChild(unit);
                }
            }

            const saveBtn = button("Save", "btn primary");
            saveBtn.disabled = true;
            const initial = read();
            const sync = () => { saveBtn.disabled = read() === initial; };
            input.addEventListener("input", sync);
            input.addEventListener("change", sync);
            const commit = () => {
                const v = read();
                if (v === initial) return;
                if (v === "bad") { showErr("Enter a number of GB, 0 for unlimited."); return; }
                if (v === "") { if (r.isSet) clear(); else sync(); return; }
                save(v);
            };
            saveBtn.addEventListener("click", commit);
            if (input.tagName === "INPUT") input.addEventListener("keydown", (e) => { if (e.key === "Enter") commit(); });
            edit.appendChild(saveBtn);
            if (r.isSet) {
                const reset = button("Use default");
                reset.addEventListener("click", clear);
                edit.appendChild(reset);
            }
            return row;
        }

        _saved(res, label, cleared) {
            if (res?.restartRequired) this.querySelector(".restart-note").hidden = false;
            window.sac?.toast?.(cleared ? `${label}: back to the default.` : `${label} saved.`, { kind: "success" });
        }

        _error(err, fallback) {
            try {
                const body = JSON.parse(err?.body || "{}");
                if (typeof body.error === "string" && body.error) return body.error;
            } catch { /* not JSON */ }
            return fallback;
        }
    }

    customElements.define("fb-system-settings-view", FbSystemSettingsView);
})();
