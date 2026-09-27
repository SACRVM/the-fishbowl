// /pending's script — a file, not inline, so the page's CSP can stay script-src 'self'.
// English / German like /login: the last choice (localStorage "sac-lang"),
// else the browser's language. English is the markup and the fallback.
const LANG = (() => {
    let chosen = "";
    try { chosen = String(localStorage.getItem("sac-lang") || "").toLowerCase().split(/[-_]/)[0]; } catch { /* storage off */ }
    const code = chosen && chosen !== "auto" ? chosen : String(navigator.language || "").toLowerCase().split(/[-_]/)[0];
    return code === "de" ? "de" : "en";
})();
const DE = {
    "title": "Warten auf Freigabe · The Fishbowl",
    "waiting": "Dein Konto wartet auf Freigabe.",
    "told": "Ein Admin dieses Fishbowl weiß Bescheid. Du bekommst Zugang, sobald er es freigibt — melde dich dann noch einmal an oder lade diese Seite neu.",
    "check": "Erneut prüfen",
    "sign-out": "Abmelden",
    "signed-in-as": "Angemeldet als {name}.",
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
    const who = document.getElementById("pending-who");
    async function check() {
        try {
            const res = await fetch("/api/v1/me");
            if (res.status === 401) { window.location.href = "/login"; return; }
            const me = await res.json();
            if (me.state && me.state !== "pending") { window.location.href = "/"; return; }
            const name = me.email || me.name || "";
            who.textContent = name ? T("signed-in-as", "Signed in as {name}.", { name }) : "";
        } catch { /* offline: stay here */ }
    }
    document.getElementById("pending-check").addEventListener("click", check);
    check();
})();
