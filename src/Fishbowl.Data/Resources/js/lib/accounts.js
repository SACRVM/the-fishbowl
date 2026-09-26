/**
 * Fishbowl — fb.accounts: what the Messages and Users views share about
 * accounts. The quota field of an approval, the approve / reject / block
 * flows (confirm → API → toast), and the registration of the admin-only
 * routes, which shell.js calls once /me says the user is an admin, so for
 * everyone else those routes don't exist at all.
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

    function failed(err, fallback) {
        let msg = fallback;
        try {
            const body = JSON.parse(err?.body || "{}");
            if (body.error === "last-admin") msg = "That's the last admin — make someone else an admin first.";
            else if (body.error === "not-pending") msg = "Someone already handled this request.";
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
        sac.router.register("#/admin/users", "fb-users-admin-view", { label: "Users", icon: "users" });
    }

    fb.accounts = { formatBytes, quotaField, readQuota, approve, reject, block, unblock, registerAdminRoutes };
})();
