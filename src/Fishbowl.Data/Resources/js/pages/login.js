// /login's script — a file, not inline, so the page's CSP can stay script-src 'self'.
// English / German, before the SPA's i18n exists: the language the user
// picked last (the SPA mirrors it into localStorage "sac-lang"), else the
// browser's. English is the markup and the fallback.
const LANG = (() => {
    let chosen = "";
    try { chosen = String(localStorage.getItem("sac-lang") || "").toLowerCase().split(/[-_]/)[0]; } catch { /* storage off */ }
    const code = chosen && chosen !== "auto" ? chosen : String(navigator.language || "").toLowerCase().split(/[-_]/)[0];
    return code === "de" ? "de" : "en";
})();
const DE = {
    "title": "Anmelden · The Fishbowl",
    "tagline": "Dein Gedächtnis lebt hier. Du nicht.",
    "username": "Benutzername",
    "password": "Passwort",
    "sign-in": "Anmelden",
    "reset-intro": "Ein Admin hat dein Passwort zurückgesetzt. Wähl ein neues, um weiterzumachen.",
    "temp-password": "Vorläufiges Passwort",
    "new-password": "Neues Passwort",
    "min-length": "mindestens 12 Zeichen",
    "save-sign-in": "Speichern und anmelden",
    "continue-with": "Weiter mit {name}",
    "providers-failed": "Die Anmeldewege lassen sich nicht laden: {message}",
    "wrong": "Falscher Benutzername oder falsches Passwort.",
    "cant-sign-in": "Dieses Konto kann sich nicht anmelden.",
    "sign-in-failed": "Anmelden fehlgeschlagen: {message}",
    "request-failed": "Anfrage fehlgeschlagen: {message}",
    "new-too-short": "Das neue Passwort braucht mindestens 12 Zeichen.",
    "new-same": "Das neue Passwort muss sich vom vorläufigen unterscheiden.",
    "temp-wrong": "Das vorläufige Passwort stimmt nicht. Bitte den Admin um ein neues.",
    "change-failed": "Das Passwort konnte nicht geändert werden: {message}",
    // Sign-in refusals the server sends back as ?authError=… (its English).
    "This Fishbowl only accepts accounts from certain e-mail domains.": "Dieses Fishbowl nimmt nur Konten aus bestimmten E-Mail-Domains an.",
    "This Fishbowl isn't open for new accounts. Ask its admin to add you.": "Dieses Fishbowl nimmt keine neuen Konten an. Bitte den Admin, dich hinzuzufügen.",
    "This account can't sign in to this Fishbowl.": "Dieses Konto kann sich bei diesem Fishbowl nicht anmelden.",
    "This account is disabled. Ask the admin of this Fishbowl.": "Dieses Konto ist deaktiviert. Frag den Admin dieses Fishbowl.",
    "This account was deleted.": "Dieses Konto wurde gelöscht.",
    "This invitation link was used already. Ask for a new one.": "Dieser Einladungslink wurde schon benutzt. Bitte um einen neuen.",
    "This invitation link has expired. Ask for a new one.": "Dieser Einladungslink ist abgelaufen. Bitte um einen neuen.",
    "This invitation link doesn't work. Ask for a new one.": "Dieser Einladungslink funktioniert nicht. Bitte um einen neuen.",
    "Sign-in refused.": "Anmeldung abgelehnt.",
    "Too many sign-in attempts — wait a minute and try again.": "Zu viele Anmeldeversuche — warte eine Minute und versuch es dann noch einmal.",
};
// T(key, english, vars): the German text when German, else the English.
function T(key, en, vars) {
    let s = LANG === "de" && DE[key] ? DE[key] : en;
    if (vars) s = s.replace(/\{(\w+)\}/g, (m, k) => (k in vars ? String(vars[k]) : m));
    return s;
}
document.documentElement.lang = LANG;
if (LANG === "de") {
    document.title = DE.title;
    for (const el of document.querySelectorAll("[data-t]")) el.textContent = T(el.dataset.t, el.textContent.trim());
    for (const el of document.querySelectorAll("[data-t-placeholder]")) el.placeholder = T(el.dataset.tPlaceholder, el.placeholder);
}

(async function () {
    const providersEl = document.getElementById("providers");
    const localForm = document.getElementById("local-form");
    const changeForm = document.getElementById("change-form");
    const errorEl = document.getElementById("error");

    function showError(msg) {
        errorEl.textContent = msg;
        errorEl.style.display = "block";
    }

    function clearError() { errorEl.style.display = "none"; }
    // A refused sign-in (Google or the account gate) comes back here with
    // the reason as ?authError=… — shown as text, never as HTML.
    const authError = new URLSearchParams(window.location.search).get("authError");
    if (authError) showError(T(authError, authError));

    // Swap the visible form. Carries the username forward and pre-fills the
    // "temporary password" field with whatever the user just typed (saves
    // them re-entering on the next screen).
    function showChangeForm(username, currentPassword) {
        localForm.style.display = "none";
        providersEl.style.display = "none";
        changeForm.style.display = "block";
        document.getElementById("change-username").value = username;
        document.getElementById("change-current").value = currentPassword || "";
        document.getElementById("change-new").focus();
    }

    try {
        const res = await fetch("/api/auth/providers");
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        const providers = await res.json();

        if (!providers || providers.length === 0) {
            // Unconfigured — go to setup
            window.location.href = "/setup";
            return;
        }

        // OAuth providers (everything except local) get a button each.
        const oauthProviders = providers.filter(p => p.id !== "local");
        providersEl.innerHTML = oauthProviders.map(p => `
            <a class="btn" href="/login/challenge/${p.id}">
                <span>${T("continue-with", "Continue with {name}", { name: p.name })}</span>
            </a>
        `).join("");

        // Local form is its own block — username + password don't fit the
        // "click a provider button" shape.
        const hasLocal = providers.some(p => p.id === "local");
        if (hasLocal) {
            localForm.style.display = "block";
        }
    } catch (err) {
        showError(T("providers-failed", "Unable to load providers: {message}", { message: err.message }));
    }

    localForm.addEventListener("submit", async (e) => {
        e.preventDefault();
        clearError();

        const username = document.getElementById("local-username").value.trim();
        const password = document.getElementById("local-password").value;

        try {
            const res = await fetch("/api/auth/login", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ username, password })
            });
            if (res.status === 204) {
                window.location.href = "/";
                return;
            }
            if (res.status === 200) {
                // Must-change branch — the temp password was correct but
                // the server refused to issue a cookie. Move them to the
                // change-password form.
                const body = await res.json().catch(() => ({}));
                if (body?.mustChangePassword) {
                    showChangeForm(body.username || username, password);
                    return;
                }
            }
            if (res.status === 401) {
                showError(T("wrong", "Wrong username or password."));
                return;
            }
            if (res.status === 429) {
                showError(T("Too many sign-in attempts — wait a minute and try again.", "Too many sign-in attempts — wait a minute and try again."));
                return;
            }
            if (res.status === 403) {
                const body = await res.json().catch(() => ({}));
                showError(body?.message ? T(body.message, body.message) : T("cant-sign-in", "This account can't sign in."));
                return;
            }
            const text = await res.text().catch(() => "");
            showError(T("sign-in-failed", "Sign-in failed: {message}", { message: text || res.status }));
        } catch (err) {
            showError(T("request-failed", "Request failed: {message}", { message: err.message }));
        }
    });

    changeForm.addEventListener("submit", async (e) => {
        e.preventDefault();
        clearError();

        const username = document.getElementById("change-username").value.trim();
        const currentPassword = document.getElementById("change-current").value;
        const newPassword = document.getElementById("change-new").value;

        if (newPassword.length < 12) {
            showError(T("new-too-short", "New password must be at least 12 characters."));
            return;
        }
        if (newPassword === currentPassword) {
            showError(T("new-same", "New password must differ from the temporary one."));
            return;
        }

        try {
            const res = await fetch("/api/auth/change-password", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ username, currentPassword, newPassword })
            });
            if (res.status === 204) {
                window.location.href = "/";
                return;
            }
            if (res.status === 401) {
                showError(T("temp-wrong", "Temporary password didn't match. Ask the admin for a fresh reset."));
                return;
            }
            const text = await res.text().catch(() => "");
            let body = null;
            try { body = JSON.parse(text); } catch { /* not JSON */ }
            // The server's refusal codes (ApiErrors) in the page's language.
            if (res.status === 429) { showError(T("Too many sign-in attempts — wait a minute and try again.", "Too many sign-in attempts — wait a minute and try again.")); return; }
            if (body?.error === "min_length") { showError(T("new-too-short", "New password must be at least 12 characters.")); return; }
            if (body?.error === "password_unchanged") { showError(T("new-same", "New password must differ from the temporary one.")); return; }
            showError(T("change-failed", "Couldn't change password: {message}", { message: body?.message || text || res.status }));
        } catch (err) {
            showError(T("request-failed", "Request failed: {message}", { message: err.message }));
        }
    });
})();
