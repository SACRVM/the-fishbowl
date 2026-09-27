// /setup's script — a file, not inline, so the page's CSP can stay script-src 'self'.
// English / German like /login: the last choice (localStorage "sac-lang"),
// else the browser's language. English is the markup and the fallback.
const LANG = (() => {
    let chosen = "";
    try { chosen = String(localStorage.getItem("sac-lang") || "").toLowerCase().split(/[-_]/)[0]; } catch { /* storage off */ }
    const code = chosen && chosen !== "auto" ? chosen : String(navigator.language || "").toLowerCase().split(/[-_]/)[0];
    return code === "de" ? "de" : "en";
})();
// Values used with data-t-html / innerHTML are this page's own markup.
const DE = {
    "title": "Einrichtung · The Fishbowl",
    "welcome": "WILLKOMMEN",
    "pick-method": "Wähl mindestens einen Anmeldeweg, um loszulegen.",
    "google-summary": "Google-Anmeldung — optional (braucht Internet)",
    "google-intro": "Anmeldung über Google OAuth. Lass diesen Teil weg, wenn du im LAN ohne Internet arbeitest — richte stattdessen unten ein lokales Konto ein.",
    "min-20": "mindestens 20 Zeichen",
    "local-summary": "Lokales Admin-Konto — optional (geht offline)",
    "local-intro": "Leg einen Admin mit Benutzername und Passwort an. Geht ohne Internet und kann später weitere lokale Benutzer anlegen. Wähl ein starkes Passwort — ein Zurücksetzen per E-Mail gibt es nicht.",
    "username": "Benutzername",
    "password": "Passwort",
    "min-12": "mindestens 12 Zeichen",
    "tls-summary": "Automatisches TLS (Let's Encrypt) — optional",
    "tls-intro": "Fishbowl kann sein eigenes Zertifikat ausstellen und erneuern. Dazu braucht es eine öffentliche Domain, die auf diesen Server zeigt, und eingehende Ports 80 + 443. Wirkt nach einem Neustart.",
    "domains": "Domainname(n)",
    "contact-email": "Kontakt-E-Mail",
    "accept-tos": "Ich nehme die <a href=\"https://letsencrypt.org/repository/\" target=\"_blank\" rel=\"noopener\">Nutzungsvereinbarung von Let's Encrypt</a> an.",
    "discord-summary": "Discord-Bot — optional",
    "discord-intro": "Verbinde einen Discord-Bot, um per Direktnachricht Notizen festzuhalten, zu suchen und Erinnerungen zu bekommen. Leg einen Bot im <a href=\"https://discord.com/developers/applications\" target=\"_blank\" rel=\"noopener\">Discord Developer Portal</a> an, aktiviere <em>User Install</em> im Tab „Installation“ und füg das Token unten ein. Wirkt nach einem Neustart.",
    "bot-token": "Bot-Token",
    "token-placeholder": "MTIz...xyz (~70 Zeichen)",
    "save-continue": "Speichern & weiter",
    "err-pick": "Wähl mindestens einen Anmeldeweg: Google oder lokaler Admin.",
    "err-client-id": "Die Client-ID muss auf .apps.googleusercontent.com enden.",
    "err-client-secret": "Das Client-Secret braucht mindestens 20 Zeichen.",
    "err-username-length": "Der lokale Benutzername muss 3–64 Zeichen lang sein.",
    "err-username-chars": "Der lokale Benutzername darf nur Buchstaben, Ziffern, _, . oder - enthalten.",
    "err-password-length": "Das lokale Passwort braucht mindestens 12 Zeichen.",
    "err-domain": "Gib mindestens eine Domain für TLS ein.",
    "err-email": "Gib eine gültige Kontakt-E-Mail für Let's Encrypt ein.",
    "err-tos": "Du musst die Nutzungsvereinbarung von Let's Encrypt annehmen.",
    "err-token-short": "Das Discord-Bot-Token ist zu kurz — sie haben etwa 70 Zeichen.",
    "err-token-dots": "Das Discord-Bot-Token sollte zwei „.“ enthalten.",
    "err-rejected": "Der Server hat abgelehnt: {text}",
    "err-request": "Anfrage fehlgeschlagen: {message}",
    "almost": "FAST GESCHAFFT",
    "tls-and-discord": "TLS und der Discord-Bot sind eingerichtet",
    "tls-only": "TLS ist eingerichtet",
    "restart-visit": "{what}. Starte Fishbowl neu und öffne dann <strong>https://{domain}</strong>, um dich anzumelden.",
    "discord-restart": "Der Discord-Bot ist eingerichtet. Starte Fishbowl neu und melde dich dann an.",
    "continue": "Weiter",
};
function T(key, en, vars) {
    let s = LANG === "de" && DE[key] ? DE[key] : en;
    if (vars) s = s.replace(/\{(\w+)\}/g, (m, k) => (k in vars ? String(vars[k]) : m));
    return s;
}
// The server's refusal code (ApiErrors: { error, message, field… }) →
// the same wording the checks above use; unknown codes return null.
function serverError(body) {
    const c = body?.error, f = body?.field;
    const map = {
        setup_no_sign_in: ["err-pick", "Pick at least one sign-in method: Google or local admin."],
        google_client_id: ["err-client-id", "Client ID must end with .apps.googleusercontent.com"],
        length_range: ["err-username-length", "Local username must be 3–64 characters."],
        username_chars: ["err-username-chars", "Local username may only contain letters, digits, _, . or -."],
        invalid_email: ["err-email", "Enter a valid contact email for Let's Encrypt."],
        tos_required: ["err-tos", "You must accept the Let's Encrypt subscriber agreement."],
        discord_token_short: ["err-token-short", "Discord bot token looks too short — they're ~70 characters."],
        discord_token_dots: ["err-token-dots", "Discord bot token should contain two '.' separators."],
    };
    if (c === "min_length" && f === "clientSecret") return T("err-client-secret", "Client Secret must be at least 20 characters.");
    if (c === "min_length" && f === "localPassword") return T("err-password-length", "Local password must be at least 12 characters.");
    if (c === "required" && f === "acmeDomains") return T("err-domain", "Enter at least one domain for TLS.");
    return map[c] ? T(map[c][0], map[c][1]) : null;
}
document.documentElement.lang = LANG;
if (LANG === "de") {
    document.title = DE.title;
    for (const el of document.querySelectorAll("[data-t]")) el.textContent = T(el.dataset.t, el.textContent.trim());
    for (const el of document.querySelectorAll("[data-t-html]")) el.innerHTML = T(el.dataset.tHtml, el.innerHTML);
    for (const el of document.querySelectorAll("[data-t-placeholder]")) el.placeholder = T(el.dataset.tPlaceholder, el.placeholder);
}

(function () {
    const form = document.getElementById("setup-form");
    const errorEl = document.getElementById("error");

    function showError(msg) {
        errorEl.textContent = msg;
        errorEl.style.display = "block";
    }

    form.addEventListener("submit", async (e) => {
        e.preventDefault();
        errorEl.style.display = "none";

        const clientId = document.getElementById("clientId").value.trim();
        const clientSecret = document.getElementById("clientSecret").value;
        const localUsername = document.getElementById("localUsername").value.trim();
        const localPassword = document.getElementById("localPassword").value;

        const hasGoogle = clientId.length > 0 || clientSecret.length > 0;
        const hasLocal = localUsername.length > 0 || localPassword.length > 0;

        if (!hasGoogle && !hasLocal) {
            showError(T("err-pick", "Pick at least one sign-in method: Google or local admin."));
            return;
        }

        // Google-side checks mirror server-side rules.
        if (hasGoogle) {
            if (!clientId.endsWith(".apps.googleusercontent.com")) {
                showError(T("err-client-id", "Client ID must end with .apps.googleusercontent.com"));
                return;
            }
            if (clientSecret.length < 20) {
                showError(T("err-client-secret", "Client Secret must be at least 20 characters."));
                return;
            }
        }

        // Local-side checks mirror server-side rules.
        if (hasLocal) {
            if (localUsername.length < 3 || localUsername.length > 64) {
                showError(T("err-username-length", "Local username must be 3–64 characters."));
                return;
            }
            if (!/^[a-zA-Z0-9_.\-]+$/.test(localUsername)) {
                showError(T("err-username-chars", "Local username may only contain letters, digits, _, . or -."));
                return;
            }
            if (localPassword.length < 12) {
                showError(T("err-password-length", "Local password must be at least 12 characters."));
                return;
            }
        }

        // ACME fields — all optional, but all-or-nothing if the operator starts filling them in.
        const acmeDomains = document.getElementById("acmeDomains").value.trim();
        const acmeEmail = document.getElementById("acmeEmail").value.trim();
        const acmeAcceptTos = document.getElementById("acmeAcceptTos").checked;
        const anyAcme = acmeDomains || acmeEmail || acmeAcceptTos;
        if (anyAcme) {
            if (!acmeDomains) { showError(T("err-domain", "Enter at least one domain for TLS.")); return; }
            if (!acmeEmail || !acmeEmail.includes("@")) {
                showError(T("err-email", "Enter a valid contact email for Let's Encrypt.")); return;
            }
            if (!acmeAcceptTos) {
                showError(T("err-tos", "You must accept the Let's Encrypt subscriber agreement.")); return;
            }
        }

        // Discord bot token — optional. Mirror the server's loose check so
        // the operator gets feedback before a network round-trip.
        const discordBotToken = document.getElementById("discordBotToken").value.trim();
        if (discordBotToken) {
            if (discordBotToken.length < 50) {
                showError(T("err-token-short", "Discord bot token looks too short — they're ~70 characters.")); return;
            }
            if ((discordBotToken.match(/\./g) || []).length < 2) {
                showError(T("err-token-dots", "Discord bot token should contain two '.' separators.")); return;
            }
        }

        try {
            const body = {};
            if (hasGoogle) {
                body.clientId = clientId;
                body.clientSecret = clientSecret;
            }
            if (hasLocal) {
                body.localUsername = localUsername;
                body.localPassword = localPassword;
            }
            if (anyAcme) {
                body.acmeDomains = acmeDomains;
                body.acmeEmail = acmeEmail;
                body.acmeAcceptTos = true;
            }
            if (discordBotToken) {
                body.discordBotToken = discordBotToken;
            }
            const res = await fetch("/api/setup", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify(body)
            });
            if (!res.ok) {
                const text = await res.text();
                let body = null;
                try { body = JSON.parse(text); } catch { /* not JSON */ }
                showError(serverError(body) || T("err-rejected", "Server rejected: {text}", { text: body?.message || text }));
                return;
            }
            const result = await res.json().catch(() => ({}));
            if (result?.acmeConfigured) {
                const restartFor = result?.discordConfigured
                    ? T("tls-and-discord", "TLS and the Discord bot are configured")
                    : T("tls-only", "TLS is configured");
                const domain = acmeDomains.split(",")[0].trim().replace(/[<>&"]/g, "");
                document.querySelector(".center-card").innerHTML = `
                    <h1>${T("almost", "ALMOST THERE")}</h1>
                    <p class="tagline">${T("restart-visit", "{what}. Restart Fishbowl, then visit <strong>https://{domain}</strong> to finish signing in.", { what: restartFor, domain })}</p>`;
            } else if (result?.discordConfigured) {
                document.querySelector(".center-card").innerHTML = `
                    <h1>${T("almost", "ALMOST THERE")}</h1>
                    <p class="tagline">${T("discord-restart", "The Discord bot is configured. Restart Fishbowl, then continue to sign in.")}</p>
                    <a href="/" class="btn primary">${T("continue", "Continue")}</a>`;
            } else {
                window.location.href = "/";
            }
        } catch (err) {
            showError(T("err-request", "Request failed: {message}", { message: err.message }));
        }
    });
})();
