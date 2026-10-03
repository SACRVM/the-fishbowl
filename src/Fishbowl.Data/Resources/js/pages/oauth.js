// /oauth/authorize's script — a file, not inline, so the page's CSP can stay
// script-src 'self'. The person picks the workspace and how much the app may
// do; Allow asks the server for a one-time code and goes back to the app.
// English / German like /login and /pending.
const LANG = (() => {
    let chosen = "";
    try { chosen = String(localStorage.getItem("sac-lang") || "").toLowerCase().split(/[-_]/)[0]; } catch { /* storage off */ }
    const code = chosen && chosen !== "auto" ? chosen : String(navigator.language || "").toLowerCase().split(/[-_]/)[0];
    return code === "de" ? "de" : "en";
})();
const DE = {
    "title": "App verbinden · The Fishbowl",
    "ask": "{client} möchte auf dein Fishbowl zugreifen.",
    "workspace": "Arbeitsbereich",
    "personal": "Persönlich",
    "access": "Es darf",
    "read": "lesen",
    "write": "lesen und schreiben",
    "build": "lesen, schreiben und Tabellen und Apps bauen",
    "back": "Danach geht es zurück zu {host}. Den Zugang findest du unter API-Schlüssel und kannst ihn dort widerrufen.",
    "allow": "Erlauben",
    "deny": "Abbrechen",
    "invalid": "Dieser Anmelde-Link ist nicht gültig — starte noch einmal in der App.",
    "failed": "Das hat nicht geklappt — versuch es noch einmal.",
};
function T(key, en, vars) {
    let s = LANG === "de" && DE[key] ? DE[key] : en;
    if (vars) s = s.replace(/\{(\w+)\}/g, (m, k) => (k in vars ? String(vars[k]) : m));
    return s;
}
document.documentElement.lang = LANG;
if (LANG === "de") {
    document.title = DE.title;
    for (const el of document.querySelectorAll("[data-t]")) el.textContent = T(el.dataset.t, el.textContent.trim());
}

(async function () {
    const q = new URLSearchParams(window.location.search);
    const p = {
        clientId: q.get("client_id"),
        redirectUri: q.get("redirect_uri"),
        codeChallenge: q.get("code_challenge"),
        state: q.get("state"),
    };
    const error = document.getElementById("oauth-error");
    const fail = (text) => { error.textContent = text; error.hidden = false; document.getElementById("oauth-form").hidden = true; };
    if (q.get("response_type") !== "code" || q.get("code_challenge_method") !== "S256" || !p.clientId || !p.redirectUri || !p.codeChallenge) {
        fail(T("invalid", "This sign-in link isn't valid — start again from the app."));
        return;
    }

    let info;
    try {
        const res = await fetch(`/api/v1/oauth/request?client_id=${encodeURIComponent(p.clientId)}&redirect_uri=${encodeURIComponent(p.redirectUri)}`);
        if (res.status === 401) { window.location.href = "/login?returnUrl=" + encodeURIComponent(location.pathname + location.search); return; }
        if (!res.ok) throw new Error(String(res.status));
        info = await res.json();
    } catch {
        fail(T("invalid", "This sign-in link isn't valid — start again from the app."));
        return;
    }

    document.getElementById("oauth-ask").textContent = T("ask", "{client} wants to use your Fishbowl.", { client: info.client });
    document.getElementById("oauth-back").textContent = T("back",
        "Then you go back to {host}. The access shows up under API keys, where you can revoke it.", { host: info.redirectHost });
    const select = document.getElementById("oauth-workspace");
    for (const w of info.workspaces) {
        const o = document.createElement("option");
        o.value = w.id;
        o.textContent = w.id === "personal" ? T("personal", "Personal") : w.name;
        o.dataset.role = w.role || "";
        select.appendChild(o);
    }
    // "Build" (tables and app code) is a Designer's — and only a space has them.
    const build = document.getElementById("oauth-build");
    const paint = () => {
        const role = select.selectedOptions[0]?.dataset.role;
        const canBuild = select.value !== "personal" && ["designer", "admin", "owner"].includes(role);
        build.hidden = !canBuild;
        if (!canBuild && build.querySelector("input").checked) document.querySelector("input[value=write]").checked = true;
    };
    select.addEventListener("change", paint);
    paint();
    const form = document.getElementById("oauth-form");
    form.hidden = false;

    document.getElementById("oauth-deny").addEventListener("click", () => {
        const u = new URL(p.redirectUri);
        u.searchParams.set("error", "access_denied");
        if (p.state) u.searchParams.set("state", p.state);
        window.location.href = u.toString();
    });
    form.addEventListener("submit", async (e) => {
        e.preventDefault();
        const access = form.querySelector("input[name=access]:checked")?.value || "read";
        try {
            const res = await fetch("/api/v1/oauth/authorize", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ ...p, workspace: select.value, access }),
            });
            if (!res.ok) throw new Error(String(res.status));
            window.location.href = (await res.json()).redirect;
        } catch {
            fail(T("failed", "That didn't work — try again."));
        }
    });
})();
