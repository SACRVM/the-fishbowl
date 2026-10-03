/**
 * fb.errors — a server error in the page's language.
 *
 * The API answers with a machine-readable code, the English `message`
 * agents and logs read, and named arguments (ApiErrors on the server):
 * `{ error: "whole_number_range", message: "Hour must be…", field:
 * "Digest:Hour", min: 0, max: 23 }`. The client turns a known code into
 * text through the i18n table ("fb.errors.<code>", German in
 * js/i18n/de-settings.js), filling {min}, {max}, {value}… from the body.
 * A file-name refusal also carries its `rule` ("fb.errors.invalid_name.<rule>").
 * Without a translation for the page's language the server's English
 * `message` shows as it is; the English below is only the last resort for
 * a code that came without one.
 *
 *   fb.errors.text(err, fallback)  err = what fb.api threw (status + body)
 *   fb.errors.code(err)            the code, or null
 */
(function () {
    // Code → English (with {placeholders}). Keys are "fb.errors.<code>".
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
        "username_taken": "Username is already taken.",
        "no_such_user": "No such user.",
        "no_local_login": "This account doesn't sign in with a password here.",
        "self_delete": "You can't delete yourself.",
        "self_disable": "You can't disable yourself.",
        "self_block": "You can't block yourself.",
        "nothing_to_change": "Nothing to change.",
        "user_exists": "A user with this id is already registered.",
        "invalid_folder_name": "That folder name isn't a single folder.",
        "no_personal_db": "There's no personal database in that folder.",
        "not_a_personal_db": "That folder doesn't look like a Fishbowl personal database.",
        "db_unreadable": "The database is unreadable or damaged.",
        "password_unchanged": "The new password must differ from the current one.",
        "username_chars": "A username may only contain letters, digits, _, . or -.",
        // Generic validation
        "invalid_body": "The request wasn't understood.",
        "invalid_value": "That value isn't allowed.",
        "required": "Please fill in: {field}.",
        "min_length": "Must be at least {min} characters.",
        "max_length": "Must be {max} characters or fewer.",
        "length_range": "Must be {min}–{max} characters.",
        "range_incomplete": "Give both a start and an end, or neither.",
        "resource_invalid": "The server refused that ({field}).",
        "resource_too_large": "That's too large ({field}).",
        "invalid_app_error": "An app error needs app (the folder), kind (load, runtime, call, reported) and message.",
        // System settings and setup
        "unknown_config_key": "That setting doesn't exist.",
        "value_required": "Enter a value — or remove the setting to use the default.",
        "one_of": "Must be one of: {allowed}.",
        "list_empty": "Enter at least one entry.",
        "whole_number_min": "Enter a whole number, {min} or more.",
        "whole_number_range": "Enter a whole number from {min} to {max}.",
        "invalid_hostname": "Not a valid host name: {value}",
        "invalid_email": "Enter a valid e-mail address.",
        "invalid_email_domain": "Not a valid e-mail domain: {value}",
        "invalid_origin": "Not an https origin (scheme and host only): {value}",
        "invalid_github_owner": "Not a GitHub owner name: {value}",
        "invalid_topic": "A topic is lower-case letters, digits and hyphens (max {max}).",
        "google_client_id": "The client ID must end with .apps.googleusercontent.com",
        "discord_token_short": "That Discord bot token looks too short — they're about 70 characters.",
        "discord_token_dots": "A Discord bot token contains two '.' separators.",
        "tos_required": "You have to accept the Let's Encrypt subscriber agreement.",
        "setup_no_sign_in": "Pick at least one sign-in method: Google or a local admin account.",
        // API keys and spaces
        "scopes_required": "Pick at least one permission.",
        "unknown_scopes": "Unknown permission requested.",
        "unknown_space": "That space doesn't exist.",
        // Secrets vault
        "already-initialized": "Secrets are already set up — unlock them instead.",
        "last-recoverable-slot": "Keep at least a passphrase or a recovery key — a passkey alone can't be the only way in.",
        // Files
        "invalid_name": "That name isn't allowed here.",
        "invalid_name.empty": "A name can't be empty.",
        "invalid_name.dot_name": "“{segment}” isn't a usable name.",
        "invalid_name.separator": "Names can't contain / or \\.",
        "invalid_name.reserved_fishbowl": "“{segment}” is reserved by Fishbowl.",
        "invalid_name.control_char": "Names can't contain control characters.",
        "invalid_name.bidi_override": "Names can't contain text-direction override characters.",
        "invalid_name.forbidden_char": "That character isn't allowed in names on this server.",
        "invalid_name.reserved_device": "“{segment}” is a reserved name on this server.",
        "invalid_name.trailing_dot_space": "Names can't end with a dot or a space on this server.",
        "invalid_name.short_name_alias": "“{segment}” looks like a short-name alias on this server.",
        "invalid_name.segment_too_long": "That name is too long for this server.",
        "invalid_path": "That path isn't valid.",
        "name_exists": "Something with that name is already there.",
        "not_found": "It isn't there any more.",
        "role_invalid": "Role must be reader, member, designer or admin.",
        "role_above_yours": "You can't hand out a role above your own.",
        "member_self": "You can't change your own role.",
        "member_missing": "That person isn't a member of this space.",
        "member_above_you": "That member's role is above yours.",
        "owner_cannot_leave": "The owner can't leave the space — delete it instead.",
        "invite_days": "An invitation is valid for 1 to {max} days.",
        "still_linked": "It can't be deleted while something links to it — remove those links first.",
        "not_your_row": "Here members change only their own rows.",
        "role_design": "Changing tables needs the Designer role or above.",
        "role_write": "Changing rows needs the Member role or above.",
        "value_required": "“{column}” is required.",
        "value_unique": "“{column}” must be unique — another row already has that value.",
        "value_invalid": "“{column}” expects {expected}.",
        "link_missing": "“{column}” links to something that isn't there.",
        "table_missing": "There is no table “{table}” in this space.",
        "row_missing": "That row isn't there any more.",
        "version_conflict": "Someone changed this in the meantime — reload and try again.",
        "invalid_value": "{reason}",
        "invalid_username": "{reason}",
        "query_invalid": "That filter doesn't work here: {reason}",
        "change_invalid": "{reason}",
        "secret_note": "This note holds secrets — agents can't change it; edit it in Fishbowl.",
        "too_many_attempts": "Too many sign-in attempts — wait a minute and try again.",
        "restore_conflict": "It can't come back: its place is taken, or something it needs is gone.",
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

    // Returned by sac.t when the page's language has no entry for a key.
    const MISSING = "\u0000";

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

    // The page language's text for a key, or null when it has none.
    function translated(key, vars) {
        const t = window.fb?.t;
        if (!t) return null;
        const s = t(key, MISSING, vars);
        return s === MISSING ? null : s;
    }

    function fill(s, vars) {
        return s.replace(/\{(\w+)\}/g, (m, k) => (vars && vars[k] != null ? String(vars[k]) : m));
    }

    function text(err, fallback) {
        const b = body(err) || {};
        const c = code(err);
        if (c) {
            const vars = { ...b };
            const ruleKey = b.rule ? `${c}.${b.rule}` : null;
            const own = (ruleKey && translated(`fb.errors.${ruleKey}`, vars)) || translated(`fb.errors.${c}`, vars);
            if (own) return own;
            if (typeof b.message === "string" && b.message) return b.message;
            if (ruleKey && EN[ruleKey]) return fill(EN[ruleKey], vars);
            if (EN[c]) return fill(EN[c], vars);
        }
        // An older server's sentence in `error` (or a bare message).
        const sentence = typeof b.error === "string" && b.error.includes(" ") ? b.error
            : typeof b.message === "string" ? b.message : null;
        return sentence || fallback;
    }

    window.fb = window.fb || {};
    fb.errors = { text, code, CODES: Object.keys(EN) };
})();
