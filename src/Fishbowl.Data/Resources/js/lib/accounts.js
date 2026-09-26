/**
 * Fishbowl — fb.accounts: what the Messages and Users views share about
 * accounts. The quota field of an approval, the approve / reject / block
 * flows (confirm → API → toast), managing an account that exists (add a
 * local user, reset a password, quota, admin, disable — A2), and the
 * registration of the admin-only routes, which shell.js calls once /me says
 * the user is an admin, so for everyone else those routes don't exist.
 */
(function () {
    const GB = 1024 ** 3;
    const MB = 1024 ** 2;

    /** Bytes as people say them: "20 GB", "512 MB", "Unlimited" for 0. */
    function formatBytes(bytes) {
        if (bytes == null) return "Default";
        if (bytes === 0) return "Unlimited";
        if (bytes >= GB) return `${+(bytes / GB).toFixed(1)} GB`;
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
        label.innerHTML = `<span>Storage</span><input type="number" min="0" step="0.5" inputmode="decimal"> <span>GB</span>`;
        const input = label.querySelector("input");
        if (id) input.id = id;
        input.value = defaultBytes == null ? "" : String(+(defaultBytes / GB).toFixed(1));
        input.title = "0 = unlimited";
        input.setAttribute("aria-label", "Storage quota in GB, 0 for unlimited");
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

    const who = (u) => u?.name || u?.email || "this account";

    async function approve(user, quotaBytes) {
        if (quotaBytes === undefined) {
            window.sac?.toast?.("The quota must be a number of GB, 0 for unlimited.", { kind: "error" });
            return false;
        }
        try {
            await fb.api.admin.approve(user.id, { quotaBytes });
            window.sac?.toast?.(`${who(user)} can use this Fishbowl now.`, { kind: "success" });
            return true;
        } catch (err) {
            return failed(err, "Couldn't approve the account.");
        }
    }

    async function reject(user) {
        const answer = await sac.dialog.confirm({
            title: `Reject ${who(user)}?`,
            message: "The request is deleted. They can sign in again later to ask once more — use Block to stop that.",
            buttons: [
                { action: "cancel", label: "Cancel" },
                { action: "reject", label: "Reject", kind: "destructive" },
            ],
        });
        if (answer !== "reject") return false;
        try {
            await fb.api.admin.reject(user.id);
            window.sac?.toast?.("Request rejected.", { kind: "success" });
            return true;
        } catch (err) {
            return failed(err, "Couldn't reject the request.");
        }
    }

    async function block(user) {
        const answer = await sac.dialog.confirm({
            title: `Block ${who(user)}?`,
            message: "They can't sign in any more, and can't ask again. A session that is open now ends with its next click.",
            buttons: [
                { action: "cancel", label: "Cancel" },
                { action: "block", label: "Block", kind: "destructive", armAfterMs: 800 },
            ],
        });
        if (answer !== "block") return false;
        try {
            await fb.api.admin.block(user.id);
            window.sac?.toast?.(`${who(user)} is blocked.`, { kind: "success" });
            return true;
        } catch (err) {
            return failed(err, "Couldn't block the account.");
        }
    }

    async function unblock(user) {
        try {
            const res = await fb.api.admin.unblock(user.id);
            window.sac?.toast?.(res?.state === "pending"
                ? `${who(user)} is waiting for approval again.`
                : `${who(user)} can sign in again.`, { kind: "success" });
            return true;
        } catch (err) {
            return failed(err, "Couldn't unblock the account.");
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
            dlg.buttons = [{ action: "done", label: "I've passed it on", kind: "primary" }];
            dlg.innerHTML = `
                <p class="fb-temp-pw-note"></p>
                <div class="fb-temp-pw-row">
                    <div class="fb-temp-pw"></div>
                    <sac-copy-button label="Copy password"></sac-copy-button>
                </div>
                <p class="fb-temp-pw-hint">They must choose their own password when they first sign in.
                    It is <strong>never</strong> shown again.</p>`;
            dlg.querySelector(".fb-temp-pw-note").textContent =
                `Give ${username} this temporary password, in person or by a channel you trust.`;
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
            dlg.setAttribute("title", "Add a local user");
            dlg.style.setProperty("--dialog-width", "440px");
            dlg.buttons = [
                { action: "cancel", label: "Cancel" },
                { action: "create", label: "Add user", kind: "primary" },
            ];
            dlg.innerHTML = `
                <p class="fb-add-user-note">For someone who signs in with a username and password
                    instead of Google. They're active at once.</p>
                <label for="fb-add-username">Username</label>
                <input id="fb-add-username" type="text" autocomplete="off" spellcheck="false"
                       autocapitalize="off" maxlength="64" placeholder="ada">
                <label for="fb-add-name">Display name <span class="fb-add-optional">(optional)</span></label>
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
                    showErr("A username is 3–64 letters, digits, _, . or -.");
                    return false;
                }
                if (quotaBytes === undefined) { showErr("The quota must be a number of GB, 0 for unlimited."); return false; }
                try {
                    created = await fb.api.admin.createUser({ username, displayName: displayName || null, quotaBytes });
                    return true;
                } catch (e) {
                    showErr(errorText(e, "Couldn't add the user."));
                    return false;
                }
            };
            dlg.querySelector("#fb-add-name").addEventListener("keydown", (e) => { if (e.key === "Enter") dlg.trigger("create"); });
            dlg.querySelector("#fb-add-username").addEventListener("keydown", (e) => { if (e.key === "Enter") dlg.trigger("create"); });
            dlg.addEventListener("sac:action", async (e) => {
                setTimeout(() => dlg.remove(), 120);
                if (e.detail.action !== "create" || !created) { resolve(false); return; }
                await revealPassword({ title: "User added", username: created.username, tempPassword: created.tempPassword });
                window.sac?.toast?.(`${created.username} can sign in now.`, { kind: "success" });
                resolve(true);
            }, { once: true });
            document.body.appendChild(dlg);
            setTimeout(() => { dlg.open(); dlg.querySelector("#fb-add-username").focus(); }, 0);
        });
    }

    async function resetPassword(user) {
        const answer = await sac.dialog.confirm({
            title: `Reset the password of ${who(user)}?`,
            message: "Their current password stops working at once. You get a temporary one to pass on; they choose a new one when they sign in.",
            buttons: [
                { action: "cancel", label: "Cancel" },
                { action: "reset", label: "Reset password", kind: "destructive", armAfterMs: 800 },
            ],
        });
        if (answer !== "reset") return false;
        try {
            const res = await fb.api.admin.resetPassword(user.id);
            await revealPassword({ title: "Password reset", username: who(user), tempPassword: res.tempPassword });
            return true;
        } catch (err) {
            return failed(err, "Couldn't reset the password.");
        }
    }

    async function setQuota(user, defaultQuota) {
        const current = user.quotaBytes ?? defaultQuota;
        const raw = await sac.dialog.prompt({
            title: `Storage for ${who(user)}`,
            message: "Personal workspace plus every space they own. 0 = unlimited; empty = this Fishbowl's default.",
            label: "Quota in GB",
            value: current == null ? "" : String(+(current / GB).toFixed(1)),
            select: "all",
            validate: (v) => {
                const t = v.trim();
                if (!t) return null;
                const n = Number(t);
                return Number.isFinite(n) && n >= 0 ? null : "A number of GB, 0 for unlimited.";
            },
            buttons: [
                { action: "cancel", label: "Cancel" },
                { action: "save", label: "Save", kind: "primary" },
            ],
        });
        if (raw == null) return false;
        const t = raw.trim();
        const quotaBytes = t ? Math.round(Number(t) * GB) : null;
        try {
            await fb.api.admin.updateUser(user.id, { quotaBytes });
            window.sac?.toast?.(`${who(user)}: storage ${formatBytes(quotaBytes)}.`, { kind: "success" });
            return true;
        } catch (err) {
            return failed(err, "Couldn't change the quota.");
        }
    }

    async function setAdmin(user, on) {
        const answer = await sac.dialog.confirm(on ? {
            title: `Make ${who(user)} an admin?`,
            message: "Admins approve and manage accounts and change this Fishbowl's settings. They still can't see anyone's notes or files.",
            buttons: [{ action: "cancel", label: "Cancel" }, { action: "ok", label: "Make admin", kind: "primary" }],
        } : {
            title: `Remove ${who(user)} as an admin?`,
            message: user.self
                ? "You lose access to Users and System settings at once."
                : "They keep their account and their data, just not the admin tools.",
            buttons: [{ action: "cancel", label: "Cancel" }, { action: "ok", label: "Remove admin", kind: "destructive" }],
        });
        if (answer !== "ok") return false;
        try {
            await fb.api.admin.updateUser(user.id, { isAdmin: on });
            window.sac?.toast?.(on ? `${who(user)} is an admin now.` : `${who(user)} is no longer an admin.`, { kind: "success" });
            if (!on && user.self) window.location.hash = "#/";
            return true;
        } catch (err) {
            return failed(err, on ? "Couldn't make them an admin." : "Couldn't remove the admin.");
        }
    }

    async function setDisabled(user, on) {
        if (on) {
            const answer = await sac.dialog.confirm({
                title: `Disable ${who(user)}?`,
                message: "They can't sign in until you enable the account again, a session that is open now ends with its next click, and their API keys are revoked. Their data stays.",
                buttons: [
                    { action: "cancel", label: "Cancel" },
                    { action: "ok", label: "Disable", kind: "destructive", armAfterMs: 800 },
                ],
            });
            if (answer !== "ok") return false;
        }
        try {
            await fb.api.admin.updateUser(user.id, { disabled: on });
            window.sac?.toast?.(on ? `${who(user)} is disabled.` : `${who(user)} can sign in again.`, { kind: "success" });
            return true;
        } catch (err) {
            return failed(err, on ? "Couldn't disable the account." : "Couldn't enable the account.");
        }
    }

    function errorText(err, fallback) {
        try {
            const body = JSON.parse(err?.body || "{}");
            if (body.error === "last-admin") return "That's the last admin — make someone else an admin first.";
            if (body.error === "not-pending") return "Someone already handled this request.";
            if (body.error === "not-active") return "Only an active account can do that.";
            if (typeof body.error === "string" && body.error.includes(" ")) return body.error;
        } catch { /* not JSON */ }
        return fallback;
    }

    function failed(err, fallback) {
        let msg = fallback;
        try {
            const body = JSON.parse(err?.body || "{}");
            if (body.error === "last-admin") msg = "That's the last admin — make someone else an admin first.";
            else if (body.error === "not-pending") msg = "Someone already handled this request.";
            else if (body.error === "not-active") msg = "Only an active account can do that.";
            else if (typeof body.error === "string" && body.error.includes(" ")) msg = body.error;
        } catch { /* not JSON */ }
        console.warn("[fb.accounts]", fallback, err?.status);
        window.sac?.toast?.(msg, { kind: "error" });
        return false;
    }

    let adminRegistered = false;
    function registerAdminRoutes() {
        if (adminRegistered) return;
        adminRegistered = true;
        sac.router.register("#/admin/users", "fb-users-admin-view", { label: "Users", icon: "users", palette: false });
        sac.router.register("#/admin/settings", "fb-system-settings-view", { label: "System settings", icon: "settings", palette: false });
    }

    fb.accounts = {
        formatBytes, quotaField, readQuota, approve, reject, block, unblock, registerAdminRoutes,
        addLocalUser, resetPassword, setQuota, setAdmin, setDisabled, revealPassword,
    };
})();
