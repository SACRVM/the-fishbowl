/**
 * Fishbowl — fb.accounts: what the Messages and Users views share about
 * accounts. The quota field of an approval, the approve / reject / block
 * flows (confirm → API → toast), managing an account that exists (add a
 * local user, reset a password, quota, admin, disable — A2; delete with
 * archive — A3), and the
 * registration of the admin-only routes, which shell.js calls once /me says
 * the user is an admin, so for everyone else those routes don't exist.
 */
(function () {
    const GB = 1024 ** 3;
    const MB = 1024 ** 2;

    /** Bytes as people say them: "20 GB", "512 MB", "Unlimited" for 0. */
    function formatBytes(bytes) {
        if (bytes == null) return fb.t("fb.accounts.default", "Default");
        if (bytes === 0) return fb.t("fb.accounts.unlimited", "Unlimited");
        if (bytes >= GB) return `${fb.format.num(bytes / GB, { min: 0, max: 1 })} GB`;
        if (bytes >= MB) return `${Math.round(bytes / MB)} MB`;
        return `${Math.max(1, Math.round(bytes / 1024))} KB`;
    }

    /**
     * A quota input in GB (decimals allowed, 0 = unlimited), pre-filled
     * with `defaultBytes`. Returns the label element; read it back with
     * readQuota(el).
     */
    function quotaField(defaultBytes, id) {
        const label = document.createElement("label");
        label.className = "fb-quota-field";
        label.innerHTML = `<span>${fb.t("fb.accounts.storage", "Storage")}</span><input type="number" min="0" step="0.5" inputmode="decimal"> <span>GB</span>`;
        const input = label.querySelector("input");
        if (id) input.id = id;
        input.value = defaultBytes == null ? "" : String(+(defaultBytes / GB).toFixed(1));
        input.title = fb.t("fb.accounts.quota-hint", "0 = unlimited");
        input.setAttribute("aria-label", fb.t("fb.accounts.quota-aria", "Storage quota in GB, 0 for unlimited"));
        return label;
    }

    /** The field's value in bytes; null when empty (the instance default). */
    function readQuota(field) {
        const raw = field?.querySelector("input")?.value?.trim();
        if (!raw) return null;
        const n = Number(raw);
        if (!Number.isFinite(n) || n < 0) return undefined;
        return Math.round(n * GB);
    }

    const who = (u) => u?.name || u?.email || fb.t("fb.accounts.this-account", "this account");

    async function approve(user, quotaBytes) {
        if (quotaBytes === undefined) {
            window.sac?.toast?.(fb.t("fb.accounts.quota-invalid", "The quota must be a number of GB, 0 for unlimited."), { kind: "error" });
            return false;
        }
        try {
            await fb.api.admin.approve(user.id, { quotaBytes });
            window.sac?.toast?.(fb.t("fb.accounts.approved", "{who} can use this Fishbowl now.", { who: who(user) }), { kind: "success" });
            return true;
        } catch (err) {
            return failed(err, fb.t("fb.accounts.approve-failed", "Couldn't approve the account."));
        }
    }

    async function reject(user) {
        const answer = await sac.dialog.confirm({
            title: fb.t("fb.accounts.reject-title", "Reject {who}?", { who: who(user) }),
            message: fb.t("fb.accounts.reject-message", "The request is deleted. They can sign in again later to ask once more — use Block to stop that."),
            buttons: [
                { action: "cancel", label: fb.t("fb.common.cancel", "Cancel") },
                { action: "reject", label: fb.t("fb.accounts.reject", "Reject"), kind: "destructive" },
            ],
        });
        if (answer !== "reject") return false;
        try {
            await fb.api.admin.reject(user.id);
            window.sac?.toast?.(fb.t("fb.accounts.rejected", "Request rejected."), { kind: "success" });
            return true;
        } catch (err) {
            return failed(err, fb.t("fb.accounts.reject-failed", "Couldn't reject the request."));
        }
    }

    async function block(user) {
        const answer = await sac.dialog.confirm({
            title: fb.t("fb.accounts.block-title", "Block {who}?", { who: who(user) }),
            message: fb.t("fb.accounts.block-message", "They can't sign in any more, and can't ask again. A session that is open now ends with its next click."),
            buttons: [
                { action: "cancel", label: fb.t("fb.common.cancel", "Cancel") },
                { action: "block", label: fb.t("fb.accounts.block", "Block"), kind: "destructive", armAfterMs: 800 },
            ],
        });
        if (answer !== "block") return false;
        try {
            await fb.api.admin.block(user.id);
            window.sac?.toast?.(fb.t("fb.accounts.blocked", "{who} is blocked.", { who: who(user) }), { kind: "success" });
            return true;
        } catch (err) {
            return failed(err, fb.t("fb.accounts.block-failed", "Couldn't block the account."));
        }
    }

    async function unblock(user) {
        try {
            const res = await fb.api.admin.unblock(user.id);
            window.sac?.toast?.(res?.state === "pending"
                ? fb.t("fb.admin.unblocked-pending", "{who} is waiting for approval again.", { who: who(user) })
                : fb.t("fb.admin.can-sign-in", "{who} can sign in again.", { who: who(user) }), { kind: "success" });
            return true;
        } catch (err) {
            return failed(err, fb.t("fb.admin.unblock-failed", "Couldn't unblock the account."));
        }
    }

    /* ------------------------------------------------------ manage (A2) -- */

    /**
     * A one-time password, shown exactly once. Like an API key's token: any
     * close other than "I've passed it on" shows the dialog again, because a
     * slip would lose it for good (the admin would have to reset again).
     */
    async function revealPassword({ title, username, tempPassword }) {
        const show = () => new Promise((resolve) => {
            const dlg = document.createElement("sac-dialog");
            dlg.setAttribute("title", title);
            dlg.style.setProperty("--dialog-width", "480px");
            dlg.buttons = [{ action: "done", label: fb.t("fb.admin.passed-on", "I've passed it on"), kind: "primary" }];
            dlg.innerHTML = `
                <p class="fb-temp-pw-note"></p>
                <div class="fb-temp-pw-row">
                    <div class="fb-temp-pw"></div>
                    <sac-copy-button label="${fb.t("fb.admin.copy-password", "Copy password")}"></sac-copy-button>
                </div>
                <p class="fb-temp-pw-hint">${fb.t("fb.admin.temp-pw-hint", "They must choose their own password when they first sign in. It is <strong>never</strong> shown again.")}</p>`;
            dlg.querySelector(".fb-temp-pw-note").textContent =
                fb.t("fb.admin.temp-pw-note", "Give {username} this temporary password, in person or by a channel you trust.", { username });
            dlg.querySelector(".fb-temp-pw").textContent = tempPassword;
            dlg.querySelector("sac-copy-button").setAttribute("value", tempPassword);
            dlg.addEventListener("sac:action", (e) => {
                setTimeout(() => { dlg.remove(); resolve(e.detail.action); }, 120);
            }, { once: true });
            document.body.appendChild(dlg);
            setTimeout(() => dlg.open(), 0);
        });
        while (await show() !== "done") { /* dismissed by accident — show it again */ }
    }

    /** "Add local user": username, display name, storage. Resolves true when created. */
    function addLocalUser(defaultQuota) {
        return new Promise((resolve) => {
            const dlg = document.createElement("sac-dialog");
            dlg.setAttribute("title", fb.t("fb.admin.add-title", "Add a local user"));
            dlg.style.setProperty("--dialog-width", "440px");
            dlg.buttons = [
                { action: "cancel", label: fb.t("fb.common.cancel", "Cancel") },
                { action: "create", label: fb.t("fb.admin.add-user", "Add user"), kind: "primary" },
            ];
            dlg.innerHTML = `
                <p class="fb-add-user-note">${fb.t("fb.admin.add-note", "For someone who signs in with a username and password instead of Google. They're active at once.")}</p>
                <label for="fb-add-username">${fb.t("fb.admin.username", "Username")}</label>
                <input id="fb-add-username" type="text" autocomplete="off" spellcheck="false"
                       autocapitalize="off" maxlength="64" placeholder="ada">
                <label for="fb-add-name">${fb.t("fb.admin.display-name", "Display name")} <span class="fb-add-optional">${fb.t("fb.admin.optional", "(optional)")}</span></label>
                <input id="fb-add-name" type="text" autocomplete="off" maxlength="100">
                <div class="fb-add-quota"></div>
                <p class="fb-add-error" role="alert" hidden></p>`;
            const quota = quotaField(defaultQuota, "fb-add-quota");
            dlg.querySelector(".fb-add-quota").appendChild(quota);
            const err = dlg.querySelector(".fb-add-error");
            const showErr = (text) => { err.textContent = text; err.hidden = !text; };
            let created = null;
            dlg.beforeAction = async (action) => {
                if (action !== "create") return true;
                const username = dlg.querySelector("#fb-add-username").value.trim().toLowerCase();
                const displayName = dlg.querySelector("#fb-add-name").value.trim();
                const quotaBytes = readQuota(quota);
                if (!/^[\p{L}\p{N}_.-]{3,64}$/u.test(username)) {
                    showErr(fb.t("fb.admin.username-rule", "A username is 3–64 letters, digits, _, . or -."));
                    return false;
                }
                if (quotaBytes === undefined) { showErr(fb.t("fb.accounts.quota-invalid", "The quota must be a number of GB, 0 for unlimited.")); return false; }
                try {
                    created = await fb.api.admin.createUser({ username, displayName: displayName || null, quotaBytes });
                    return true;
                } catch (e) {
                    showErr(errorText(e, fb.t("fb.admin.add-failed", "Couldn't add the user.")));
                    return false;
                }
            };
            dlg.querySelector("#fb-add-name").addEventListener("keydown", (e) => { if (e.key === "Enter") dlg.trigger("create"); });
            dlg.querySelector("#fb-add-username").addEventListener("keydown", (e) => { if (e.key === "Enter") dlg.trigger("create"); });
            dlg.addEventListener("sac:action", async (e) => {
                setTimeout(() => dlg.remove(), 120);
                if (e.detail.action !== "create" || !created) { resolve(false); return; }
                await revealPassword({ title: fb.t("fb.admin.user-added", "User added"), username: created.username, tempPassword: created.tempPassword });
                window.sac?.toast?.(fb.t("fb.admin.can-sign-in-now", "{who} can sign in now.", { who: created.username }), { kind: "success" });
                resolve(true);
            }, { once: true });
            document.body.appendChild(dlg);
            setTimeout(() => { dlg.open(); dlg.querySelector("#fb-add-username").focus(); }, 0);
        });
    }

    async function resetPassword(user) {
        const answer = await sac.dialog.confirm({
            title: fb.t("fb.admin.reset-title", "Reset the password of {who}?", { who: who(user) }),
            message: fb.t("fb.admin.reset-body", "Their current password stops working at once. A temporary one goes to their Discord DM if they have one linked — otherwise you get it to pass on. They choose a new one when they sign in, and they're told about the reset."),
            buttons: [
                { action: "cancel", label: fb.t("fb.common.cancel", "Cancel") },
                { action: "reset", label: fb.t("fb.admin.menu-reset", "Reset password"), kind: "destructive", armAfterMs: 800 },
            ],
        });
        if (answer !== "reset") return false;
        try {
            const res = await fb.api.admin.resetPassword(user.id);
            if (res.deliveredTo) {
                // Sent straight to the user's chat — the admin never sees it.
                window.sac?.toast?.(fb.t("fb.admin.reset-delivered", "{who} got a temporary password by {via} DM. They choose their own when they sign in.",
                    { who: who(user), via: res.deliveredTo === "discord" ? "Discord" : res.deliveredTo }), { kind: "success" });
            } else {
                await revealPassword({ title: fb.t("fb.admin.password-reset", "Password reset"), username: who(user), tempPassword: res.tempPassword });
            }
            return true;
        } catch (err) {
            return failed(err, fb.t("fb.admin.reset-failed", "Couldn't reset the password."));
        }
    }

    async function setQuota(user, defaultQuota) {
        const current = user.quotaBytes ?? defaultQuota;
        const raw = await sac.dialog.prompt({
            title: fb.t("fb.admin.quota-title", "Storage for {who}", { who: who(user) }),
            message: fb.t("fb.admin.quota-body", "Personal workspace plus every space they own. 0 = unlimited; empty = this Fishbowl's default."),
            label: fb.t("fb.admin.quota-label", "Quota in GB"),
            value: current == null ? "" : String(+(current / GB).toFixed(1)),
            select: "all",
            validate: (v) => {
                const t = v.trim();
                if (!t) return null;
                const n = Number(t);
                return Number.isFinite(n) && n >= 0 ? null : fb.t("fb.admin.quota-rule", "A number of GB, 0 for unlimited.");
            },
            buttons: [
                { action: "cancel", label: fb.t("fb.common.cancel", "Cancel") },
                { action: "save", label: fb.t("fb.admin.save", "Save"), kind: "primary" },
            ],
        });
        if (raw == null) return false;
        const t = raw.trim();
        const quotaBytes = t ? Math.round(Number(t) * GB) : null;
        try {
            await fb.api.admin.updateUser(user.id, { quotaBytes });
            window.sac?.toast?.(fb.t("fb.admin.quota-saved", "{who}: storage {size}.", { who: who(user), size: formatBytes(quotaBytes) }), { kind: "success" });
            return true;
        } catch (err) {
            return failed(err, fb.t("fb.admin.quota-failed", "Couldn't change the quota."));
        }
    }

    async function setAdmin(user, on) {
        const answer = await sac.dialog.confirm(on ? {
            title: fb.t("fb.admin.make-admin-title", "Make {who} a global admin?", { who: who(user) }),
            message: fb.t("fb.admin.make-admin-body", "Global admins approve and manage accounts and change this Fishbowl's settings. They still can't see anyone's notes, files or spaces."),
            buttons: [{ action: "cancel", label: fb.t("fb.common.cancel", "Cancel") }, { action: "ok", label: fb.t("fb.admin.menu-admin-on", "Make global admin"), kind: "primary" }],
        } : {
            title: fb.t("fb.admin.remove-admin-title", "Remove {who} as a global admin?", { who: who(user) }),
            message: user.self
                ? fb.t("fb.admin.remove-admin-self", "You lose access to Users and System settings at once.")
                : fb.t("fb.admin.remove-admin-body", "They keep their account and their data, just not the admin tools."),
            buttons: [{ action: "cancel", label: fb.t("fb.common.cancel", "Cancel") }, { action: "ok", label: fb.t("fb.admin.menu-admin-off", "Remove global admin"), kind: "destructive" }],
        });
        if (answer !== "ok") return false;
        try {
            await fb.api.admin.updateUser(user.id, { isAdmin: on });
            window.sac?.toast?.(on ? fb.t("fb.admin.is-admin", "{who} is a global admin now.", { who: who(user) }) : fb.t("fb.admin.no-longer-admin", "{who} is no longer a global admin.", { who: who(user) }), { kind: "success" });
            if (!on && user.self) window.location.hash = "#/";
            return true;
        } catch (err) {
            return failed(err, on ? fb.t("fb.admin.make-admin-failed", "Couldn't make them an admin.") : fb.t("fb.admin.remove-admin-failed", "Couldn't remove the admin."));
        }
    }

    async function setDisabled(user, on) {
        if (on) {
            const answer = await sac.dialog.confirm({
                title: fb.t("fb.admin.disable-title", "Disable {who}?", { who: who(user) }),
                message: fb.t("fb.admin.disable-body", "They can't sign in until you enable the account again, a session that is open now ends with its next click, and their API keys are revoked. Their data stays."),
                buttons: [
                    { action: "cancel", label: fb.t("fb.common.cancel", "Cancel") },
                    { action: "ok", label: fb.t("fb.admin.menu-disable", "Disable"), kind: "destructive", armAfterMs: 800 },
                ],
            });
            if (answer !== "ok") return false;
        }
        try {
            await fb.api.admin.updateUser(user.id, { disabled: on });
            window.sac?.toast?.(on ? fb.t("fb.admin.disabled", "{who} is disabled.", { who: who(user) }) : fb.t("fb.admin.can-sign-in", "{who} can sign in again.", { who: who(user) }), { kind: "success" });
            return true;
        } catch (err) {
            return failed(err, on ? fb.t("fb.admin.disable-failed", "Couldn't disable the account.") : fb.t("fb.admin.enable-failed", "Couldn't enable the account."));
        }
    }

    /* ------------------------------------------------------ lifecycle (A3) -- */

    /**
     * Delete an account. The dialog first asks the server what stands in the
     * way: spaces this account alone owns must be handed over or deleted
     * first (listed, and there's no Delete button then). Otherwise "Archive
     * their data first" is checked by default — a ZIP of their whole folder,
     * kept like archived spaces. Resolves true when the account is gone.
     */
    async function deleteUser(user) {
        let check;
        try {
            check = await fb.api.admin.deleteCheck(user.id);
        } catch (err) {
            return failed(err, fb.t("fb.admin.check-failed", "Couldn't check the account."));
        }
        const blocked = (check.ownedSpaces || []).length > 0;
        const choice = await new Promise((resolve) => {
            const dlg = document.createElement("sac-dialog");
            dlg.setAttribute("title", fb.t("fb.admin.delete-title", "Delete {who}?", { who: who(user) }));
            dlg.style.setProperty("--dialog-width", "460px");
            dlg.buttons = blocked
                ? [{ action: "cancel", label: fb.t("fb.admin.close", "Close"), kind: "default" }]
                : [
                    { action: "cancel", label: fb.t("fb.common.cancel", "Cancel"), kind: "default" },
                    { action: "delete", label: fb.t("fb.admin.delete-account", "Delete account"), kind: "destructive", armAfterMs: 1500 },
                ];
            const body = document.createElement("div");
            body.className = "fb-user-delete";
            if (blocked) {
                const p = document.createElement("p");
                p.textContent = fb.t("fb.admin.delete-owns", "They are the only owner of these spaces. Make someone else an owner, or delete the spaces, first:");
                const list = document.createElement("ul");
                list.className = "fb-user-delete-spaces";
                for (const s of check.ownedSpaces) {
                    const li = document.createElement("li");
                    li.textContent = s.name;
                    list.appendChild(li);
                }
                body.append(p, list);
            } else {
                body.innerHTML = `
                    <p>${fb.t("fb.admin.delete-body", "Their sign-ins, API keys, space memberships and their personal workspace — notes, todos, events, files — are removed.")}</p>
                    <label class="fb-check">
                        <input type="checkbox" name="archive" checked>
                        <span>${fb.t("fb.admin.archive-first", "Archive their data first")}</span>
                    </label>
                    <p class="fb-user-delete-hint"></p>`;
                const box = body.querySelector("input[name=archive]");
                const hint = body.querySelector(".fb-user-delete-hint");
                const paint = () => {
                    hint.textContent = box.checked
                        ? fb.t("fb.admin.archive-hint", "A ZIP of their whole folder stays on the server for the archive retention time (System shows it). Nobody opens it here.")
                        : fb.t("fb.admin.no-archive-hint", "Nothing is kept. This can't be undone.");
                };
                box.addEventListener("change", paint);
                paint();
                if (!check.hasData) {
                    box.checked = false;
                    box.disabled = true;
                    hint.textContent = fb.t("fb.admin.nothing-to-archive", "They never stored anything, so there's nothing to archive.");
                }
            }
            dlg.appendChild(body);
            dlg.addEventListener("sac:action", (e) => {
                const action = e.detail?.action;
                const archive = body.querySelector("input[name=archive]")?.checked ?? false;
                setTimeout(() => dlg.remove(), 120);
                resolve(action === "delete" ? { archive } : null);
            }, { once: true });
            document.body.appendChild(dlg);
            setTimeout(() => dlg.open(), 0);
        });
        if (!choice) return false;
        try {
            await fb.api.admin.deleteUser(user.id, { archive: choice.archive });
            window.sac?.toast?.(choice.archive
                ? fb.t("fb.admin.archived-deleted", "{who} was archived and deleted.", { who: who(user) })
                : fb.t("fb.admin.deleted", "{who} was deleted.", { who: who(user) }), { kind: "success" });
            return true;
        } catch (err) {
            if (err?.status === 507) return failed(err, fb.t("fb.admin.archive-no-disk", "Not enough disk space for the archive — nothing was deleted."));
            return failed(err, fb.t("fb.admin.delete-failed", "Couldn't delete the account."));
        }
    }

    // Server error codes → text in the page's language (fb.errors); a
    // sentence the server sent is shown as it is; else the fallback.
    function errorText(err, fallback) {
        return fb.errors ? fb.errors.text(err, fallback) : fallback;
    }

    function failed(err, fallback) {
        const msg = errorText(err, fallback);
        console.warn("[fb.accounts]", fallback, err?.status);
        window.sac?.toast?.(msg, { kind: "error" });
        return false;
    }

    let adminRegistered = false;
    function registerAdminRoutes() {
        if (adminRegistered) return;
        adminRegistered = true;
        sac.router.register("#/admin/users", "fb-users-admin-view", { label: "Users", icon: "users", palette: false, scope: "root" });
        sac.router.register("#/admin/settings", "fb-system-settings-view", { label: "System settings", icon: "settings", palette: false, scope: "root" });
        sac.router.register("#/admin/system", "fb-system-view", { label: "System Info", icon: "info", palette: false, scope: "root" });
    }

    fb.accounts = {
        formatBytes, quotaField, readQuota, approve, reject, block, unblock, registerAdminRoutes,
        addLocalUser, resetPassword, setQuota, setAdmin, setDisabled, revealPassword, deleteUser,
    };
})();
