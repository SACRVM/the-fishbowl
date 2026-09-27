/**
 * fb.errors — a server error in the page's language.
 *
 * The API answers with a machine-readable code (`{ error: "last-admin" }`,
 * `{ error: "name_exists" }`…) or, for validation, an English sentence. The
 * client turns a known code into text through the i18n table
 * ("fb.errors.<code>", German in js/i18n/de-settings.js, English here as
 * the fallback). A sentence the server wrote is shown as it is — English
 * stays the fallback for anything without a key. Agents and logs never see
 * these texts; they read the code.
 *
 *   fb.errors.text(err, fallback)  err = what fb.api threw (status + body)
 *   fb.errors.code(err)            the code, or null
 */
(function () {
    // Code → English. Keys are "fb.errors.<code>".
    const EN = {
        // Accounts and admin
        "pending": "Your account is waiting for an admin's approval.",
        "disabled": "This account is disabled.",
        "blocked": "This account is blocked.",
        "deleted": "This account was deleted.",
        "last-admin": "That's the last admin — make someone else an admin first.",
        "not-pending": "Someone already handled this request.",
        "not-active": "Only an active account can do that.",
        "not-disabled": "This account isn't disabled.",
        "not-blocked": "This account isn't blocked.",
        "owns-spaces": "They still own spaces alone — hand them over or delete them first.",
        // Secrets vault
        "already-initialized": "Secrets are already set up — unlock them instead.",
        "last-recoverable-slot": "Keep at least a passphrase or a recovery key — a passkey alone can't be the only way in.",
        // Files
        "invalid_name": "That name isn't allowed here.",
        "invalid_path": "That path isn't valid.",
        "name_exists": "Something with that name is already there.",
        "not_found": "It isn't there any more.",
        "is_folder": "That's a folder.",
        "not_a_folder": "That isn't a folder.",
        "into_own_subtree": "A folder can't go into itself.",
        "link_not_followed": "Links aren't followed.",
        "too_large": "That's too large.",
        "too_many_items": "Too many items at once.",
        "user_quota": "Your storage is full.",
        "insufficient_storage": "The server is running out of disk space.",
        "precondition_failed": "It changed in the meantime — reload and try again.",
        "precondition_required": "The upload is missing its version check.",
        "upload_header_required": "The upload is missing its header.",
        "cursor_expired": "The change feed moved on — reload.",
        "invalid_workspace": "That workspace doesn't exist or isn't yours.",
        "invalid_request": "That request isn't valid.",
        // Desktop apps
        "invalid_manifest": "The app's manifest isn't valid.",
        "invalid_manifest_url": "That isn't an app address Fishbowl can use.",
        "manifest_too_large": "The app's manifest is too large.",
        // Integrations
        "discord_not_configured": "Discord isn't set up on this Fishbowl.",
    };

    // Sentences the server writes that people see often. Matched exactly;
    // anything else stays English.
    const SENTENCES = {
        "Username is already taken.": "username-taken",
        "No such user.": "no-such-user",
        "You can't delete yourself.": "self-delete",
        "You can't disable yourself.": "self-disable",
        "You can't block yourself.": "self-block",
    };
    const SENTENCE_EN = {
        "username-taken": "Username is already taken.",
        "no-such-user": "No such user.",
        "self-delete": "You can't delete yourself.",
        "self-disable": "You can't disable yourself.",
        "self-block": "You can't block yourself.",
    };

    function body(err) {
        if (!err) return null;
        if (err.body && typeof err.body === "object") return err.body;
        try { return JSON.parse(err.body || "null"); } catch { return null; }
    }

    function code(err) {
        const b = body(err);
        const c = b?.error ?? b?.code ?? err?.code;
        return typeof c === "string" && !c.includes(" ") ? c : null;
    }

    function text(err, fallback) {
        const t = window.fb?.t || ((k, en) => en);
        const c = code(err);
        if (c && Object.prototype.hasOwnProperty.call(EN, c)) return t(`fb.errors.${c}`, EN[c]);
        const b = body(err);
        const sentence = typeof b?.error === "string" && b.error.includes(" ") ? b.error
            : typeof b?.message === "string" ? b.message : null;
        if (sentence) {
            const k = SENTENCES[sentence];
            return k ? t(`fb.errors.${k}`, SENTENCE_EN[k]) : sentence;
        }
        return fallback;
    }

    window.fb = window.fb || {};
    fb.errors = { text, code, CODES: Object.keys(EN) };
})();
