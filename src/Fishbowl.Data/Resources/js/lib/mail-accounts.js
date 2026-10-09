/**
 * fb.mailAccounts — the "Mail accounts" window of the Mail app (mail spec,
 * decision 13). A <sac-window> in the one window frame: each account of the
 * workspace with its state (in sync, reading its history, failing — and why),
 * how many messages it has here and when it last synced; a "…" menu per row
 * (Sync now, Edit…, New password…, Remove…) and "Add account" in the
 * toolbar — the write items only for whoever may manage (personal: the
 * owner; a space: its Admins). Adding tries the sign-in first; the dialog
 * says plainly that whoever runs the server can read the mail.
 *
 *   fb.mailAccounts.open({ ws, canManage, onChanged })
 *   fb.mailAccounts.formDialog({ title, primary, html, init, submit, width }) — also the rules' editor
 */
(function () {
    if (!window.fb) return;

    const t = (k, f, v) => fb.t(`fb.mail.${k}`, f, v);
    let win = null;
    let opts = {};
    let timer = null;

    const PROVIDERS = [
        { value: "icloud", label: "iCloud" },
        { value: "gmail", label: "Gmail" },
        { value: "ionos", label: "IONOS" },
        { value: "custom", label: () => t("provider-other", "Other (IMAP)") },
    ];

    function hint(provider) {
        switch (provider) {
            case "icloud": return t("hint-icloud", "An app-specific password — appleid.apple.com → Sign-In and Security → App-Specific Passwords (two-factor authentication must be on).");
            case "gmail": return t("hint-gmail", "An app password — Google account → Security → 2-Step Verification → App passwords.");
            case "ionos": return t("hint-ionos", "The mailbox's password.");
            default: return t("hint-custom", "The mailbox's password and its servers, as your provider names them.");
        }
    }

    /** What an account's last error means, in words. */
    function errorText(code) {
        switch (code) {
            case "auth_failed": return t("err-auth", "The server refused the sign-in — enter the password again.");
            case "unreachable": return t("err-unreachable", "The server can't be reached right now.");
            case "tls_failed": return t("err-tls", "The secure connection failed.");
            case "credentials_unreadable": return t("err-credentials", "This server can't read the stored password — enter it again.");
            default: return t("err-sync", "The mailbox couldn't be read.");
        }
    }

    function stateLine(a) {
        const n = a.messageCount || 0;
        const count = n === 1 ? t("count-1", "1 message") : t("count-n", "{n} messages", { n: fb.format.num(n) });
        if (a.state === "failed") return errorText(a.lastError);
        if (a.state === "new") return t("state-new", "Connecting…");
        if (!a.backfillDone) return t("state-history", "Fetching mail — {count} so far", { count });
        const when = a.lastSyncAt ? fb.format.time(new Date(a.lastSyncAt)) : "";
        return when ? t("state-ok", "{count} · synced {when}", { count, when }) : count;
    }

    function stateIcon(a) {
        if (a.state === "failed") return "warn";
        if (a.state === "new" || !a.backfillDone) return "sync";
        return "success";
    }

    function ensureWindow() {
        if (win) return win;
        win = document.createElement("sac-window");
        win.setAttribute("escape-closes", "");
        win.id = "fb-mail-accounts";
        win.classList.add("fb-window");
        win.setAttribute("width", "640px");
        win.setAttribute("height", "520px");
        win.setAttribute("top", "80px");
        win.setAttribute("left", "calc(50vw - 320px)");
        win.setAttribute("controls", "close");
        win.innerHTML = `
            <div class="toolbar wa-bar" slot="toolbar"></div>
            <div class="fb-block fb-mail-accounts"></div>`;
        win.addEventListener("sac:close", () => { clearInterval(timer); timer = null; });
        document.body.appendChild(win);
        return win;
    }

    async function open(o = {}) {
        opts = o;
        const w = ensureWindow();
        w.setAttribute("title", t("accounts", "Mail accounts"));
        const bar = w.querySelector(".wa-bar");
        bar.replaceChildren();
        if (opts.canManage) {
            const add = document.createElement("button");
            add.type = "button";
            add.className = "btn";
            add.innerHTML = `<sac-icon name="plus"></sac-icon><span>${t("add-account", "Add account")}</span>`;
            add.addEventListener("click", () => addAccount());
            bar.appendChild(add);
            bar.slot = "toolbar";
        } else {
            bar.removeAttribute("slot");
        }
        await paint();
        clearInterval(timer);
        // While an account reads its history the counts move: keep them fresh.
        timer = setInterval(() => { if (w.isConnected) paint(); }, 5000);
        requestAnimationFrame(() => w.open());
    }

    async function paint() {
        const list = win.querySelector(".fb-mail-accounts");
        let accounts = [];
        try { accounts = await fb.api.mail.in(opts.ws).accounts(); }
        catch (err) { list.innerHTML = `<p class="muted">${t("accounts-failed", "The accounts can't be loaded right now.")}</p>`; return; }
        if (!accounts.length) {
            list.innerHTML = `<div class="empty-state"><sac-icon name="mail"></sac-icon><h3>${t("no-accounts", "No mail account yet")}</h3></div>`;
            return;
        }
        list.replaceChildren(...accounts.map(row));
    }

    function row(a) {
        const r = document.createElement("div");
        r.className = "fb-row fb-mail-account";
        r.innerHTML = `
            <sac-icon></sac-icon>
            <div class="fb-row-info">
                <div class="fb-row-name"></div>
                <div class="fb-row-meta"><span class="fb-mail-account-address"></span><span class="fb-mail-account-state"></span></div>
            </div>`;
        const icon = r.querySelector("sac-icon");
        icon.setAttribute("name", stateIcon(a));
        r.classList.toggle("is-failed", a.state === "failed");
        r.classList.toggle("is-busy", a.state !== "failed" && (a.state === "new" || !a.backfillDone));
        r.querySelector(".fb-row-name").textContent = a.name;
        r.querySelector(".fb-mail-account-address").textContent = a.displayName ? `${a.displayName} <${a.address}>` : a.address;
        r.querySelector(".fb-mail-account-state").textContent = stateLine(a);

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
        if (opts.canWrite) item("sync", t("sync-now", "Sync now"), "sync");
        if (opts.canManage) {
            item("edit", t("edit-account", "Edit…"), "pencil");
            item("password", t("new-password", "New password…"), "key");
            item("remove", t("remove-account", "Remove…"), "trash");
        }
        if (menu.querySelectorAll("button[data-action]").length) {
            menu.addEventListener("sac:select", (e) => act(e.detail.action, a));
            r.appendChild(menu);
        }
        return r;
    }

    async function act(action, a) {
        const api = fb.api.mail.in(opts.ws);
        try {
            if (action === "sync") { await api.sync(a.id); sac.toast?.(t("sync-started", "Syncing {name}…", { name: a.name })); }
            else if (action === "edit") { if (await editAccount(a)) changed(); }
            else if (action === "password") { if (await newPassword(a)) changed(); }
            else if (action === "remove") {
                const answer = await sac.dialog.confirm({
                    title: t("remove-title", "Remove {name}?", { name: a.name }),
                    message: t("remove-msg", "Its mail leaves Fishbowl — tags included. The mail server keeps every message; adding the account again fetches it anew."),
                    buttons: [
                        { action: "cancel", label: fb.t("fb.common.cancel", "Cancel") },
                        { action: "remove", label: t("remove", "Remove"), kind: "destructive" },
                    ],
                });
                if (answer !== "remove") return;
                await api.removeAccount(a.id);
                changed();
            }
        } catch (err) {
            sac.toast?.(fb.errors.text(err, t("account-failed", "That didn't work.")), { kind: "error" });
        }
        await paint();
    }

    function changed() { opts.onChanged?.(); }

    /** A <sac-dialog> with fields; `submit()` returns true to close, else shows its error. */
    function formDialog({ title, primary, html, submit, init, width = "480px" }) {
        return new Promise((resolve) => {
            const dlg = document.createElement("sac-dialog");
            dlg.setAttribute("title", title);
            dlg.style.setProperty("--dialog-width", width);
            dlg.buttons = [
                { action: "cancel", label: fb.t("fb.common.cancel", "Cancel") },
                { action: "ok", label: primary, kind: "primary" },
            ];
            dlg.innerHTML = `<div class="fb-mail-form">${html}</div><p class="fb-mail-form-error" role="alert" hidden></p>`;
            const err = dlg.querySelector(".fb-mail-form-error");
            init?.(dlg.querySelector(".fb-mail-form"));
            let done = false;
            dlg.beforeAction = async (action) => {
                if (action !== "ok") return true;
                err.hidden = true;
                const problem = await submit(dlg);
                if (problem) { err.textContent = problem; err.hidden = false; return false; }
                done = true;
                return true;
            };
            dlg.addEventListener("sac:action", () => setTimeout(() => { dlg.remove(); resolve(done); }, 120), { once: true });
            document.body.appendChild(dlg);
            setTimeout(() => dlg.open(), 0);
        });
    }

    function addAccount() {
        const security = (name, value) => `
            <sac-select name="${name}" value="${value}">
                <option value="ssl">SSL/TLS</option><option value="starttls">STARTTLS</option><option value="none">${t("security-none", "None")}</option>
            </sac-select>`;
        return formDialog({
            title: t("add-title", "Add a mail account"),
            primary: t("add", "Add"),
            width: "520px",
            html: `
                <label>${t("provider", "Provider")}
                    <sac-select name="provider" value="icloud">
                        ${PROVIDERS.map((p) => `<option value="${p.value}">${typeof p.label === "function" ? p.label() : p.label}</option>`).join("")}
                    </sac-select>
                </label>
                <label>${t("address", "Email address")}<input name="address" type="email" autocomplete="off" spellcheck="false"></label>
                <label>${t("password", "Password")}<input name="password" type="password" autocomplete="new-password"></label>
                <p class="fb-mail-form-hint" data-hint></p>
                <label><span>${t("name", "Name in Fishbowl")} <span class="muted">${fb.t("fb.admin.optional", "(optional)")}</span></span><input name="name" type="text" maxlength="60"></label>
                <fieldset class="fb-mail-servers" hidden>
                    <div class="fb-mail-server-row">
                        <label>${t("imap-host", "IMAP server")}<input name="imapHost" type="text" spellcheck="false"></label>
                        <label>${t("port", "Port")}<input name="imapPort" type="number" min="1" max="65535" value="993"></label>
                        <label>${t("security", "Security")}${security("imapSecurity", "ssl")}</label>
                    </div>
                    <div class="fb-mail-server-row">
                        <label>${t("smtp-host", "SMTP server")}<input name="smtpHost" type="text" spellcheck="false"></label>
                        <label>${t("port", "Port")}<input name="smtpPort" type="number" min="1" max="65535" value="587"></label>
                        <label>${t("security", "Security")}${security("smtpSecurity", "starttls")}</label>
                    </div>
                </fieldset>
                <p class="fb-mail-form-note"><sac-icon name="info"></sac-icon>
                    <span>${t("operator-note", "Fishbowl keeps a copy of this mailbox and needs the password to fetch new mail, so it is stored on the server, encrypted. Whoever runs this Fishbowl can read this mail.")}</span></p>`,
            submit: async (dlg) => {
                const v = (n) => dlg.querySelector(`[name="${n}"]`).value.trim();
                const provider = v("provider");
                const body = { provider, address: v("address"), password: dlg.querySelector('[name="password"]').value, name: v("name") || null };
                if (!body.address) return t("need-address", "Enter the email address.");
                if (!body.password) return t("need-password", "Enter the password.");
                if (provider === "custom") {
                    Object.assign(body, {
                        imapHost: v("imapHost"), imapPort: Number(v("imapPort")) || 993, imapSecurity: v("imapSecurity"),
                        smtpHost: v("smtpHost"), smtpPort: Number(v("smtpPort")) || 587, smtpSecurity: v("smtpSecurity"),
                    });
                    if (!body.imapHost || !body.smtpHost) return t("need-servers", "Enter the IMAP and SMTP servers.");
                }
                try {
                    await fb.api.mail.in(opts.ws).addAccount(body);
                    changed();
                    await paint();
                    return null;
                } catch (err) {
                    return fb.errors.text(err, t("add-failed", "The account couldn't be added."));
                }
            },
            // The provider decides the hint and whether the servers are asked for.
            init: (form) => {
                syncProvider(form);
                const sel = form.querySelector('[name="provider"]');
                for (const type of ["change", "sac:change"]) sel.addEventListener(type, () => syncProvider(form));
            },
        });
    }

    function syncProvider(form) {
        const provider = form.querySelector('[name="provider"]')?.value || "icloud";
        const hintEl = form.querySelector("[data-hint]");
        if (hintEl) hintEl.textContent = hint(provider);
        const servers = form.querySelector(".fb-mail-servers");
        if (servers) servers.hidden = provider !== "custom";
    }

    function editAccount(a) {
        return formDialog({
            title: t("edit-title", "Edit {name}", { name: a.name }),
            primary: fb.t("fb.common.save", "Save"),
            html: `
                <label>${t("name", "Name in Fishbowl")}<input name="name" type="text" maxlength="60"></label>
                <label>${t("display-name", "Your name on outgoing mail")}<input name="displayName" type="text" maxlength="100"></label>
                <label><span>${t("aliases", "Further addresses of yours")} <span class="muted">${t("aliases-hint", "(comma-separated — mail from them counts as yours)")}</span></span>
                    <input name="aliases" type="text" spellcheck="false"></label>`,
            // Values via properties, never markup.
            init: (form) => {
                form.querySelector('[name="name"]').value = a.name || "";
                form.querySelector('[name="displayName"]').value = a.displayName || "";
                form.querySelector('[name="aliases"]').value = (a.aliases || []).join(", ");
            },
            submit: async (dlg) => {
                const v = (n) => dlg.querySelector(`[name="${n}"]`).value.trim();
                try {
                    await fb.api.mail.in(opts.ws).updateAccount(a.id, {
                        name: v("name") || a.name,
                        displayName: v("displayName"),
                        aliases: v("aliases").split(",").map((s) => s.trim()).filter(Boolean),
                    });
                    return null;
                } catch (err) { return fb.errors.text(err, t("account-failed", "That didn't work.")); }
            },
        });
    }

    function newPassword(a) {
        return formDialog({
            title: t("password-title", "New password for {name}", { name: a.name }),
            primary: fb.t("fb.common.save", "Save"),
            html: `<label>${t("password", "Password")}<input name="password" type="password" autocomplete="new-password"></label>`,
            submit: async (dlg) => {
                const pw = dlg.querySelector('[name="password"]').value;
                if (!pw) return t("need-password", "Enter the password.");
                try { await fb.api.mail.in(opts.ws).updateAccount(a.id, { password: pw }); return null; }
                catch (err) { return fb.errors.text(err, t("account-failed", "That didn't work.")); }
            },
        });
    }

    fb.mailAccounts = { open, errorText, formDialog };
})();
