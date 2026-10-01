/**
 * fb.i18n — Fishbowl's strings on the kit's language layer (sac.lang, sac.t,
 * sac.i18n.add). No Fishbowl i18n system: English stays inline as the
 * fallback, js/i18n/de.js adds German (keys "fb.<area>.*"), and the kit's
 * own table (kit/js/i18n/de.js) translates the kit's components.
 *
 * The UI language is independent of the date format (fb.format). It is a
 * per-user setting (users.language, PATCH /api/v1/me): null = automatic —
 * the browser's language when there is a table for it, else English. The
 * shell applies it on load (sac.lang.set), which also mirrors it into
 * localStorage for pages that load before /me (the login page).
 *
 *   fb.t(key, fallback, vars)  sac.t plus {name} placeholders from vars
 *   fb.i18n.apply(root)        static markup: data-t (text), data-t-title,
 *                              data-t-aria, data-t-placeholder — the English
 *                              in the markup is the fallback
 *   fb.i18n.relabelRoutes()    route labels (burger, phone title) under
 *                              "fb.route.<path>" keys, English as registered
 *
 * A language switch repaints: the shell relabels the routes and the static
 * markup, then remounts the current view (as a date-format change does).
 */
(function () {
    function t(key, fallback, vars) {
        let s = window.sac?.t ? sac.t(key, fallback) : fallback;
        if (vars && typeof s === "string") {
            s = s.replace(/\{(\w+)\}/g, (m, k) => (Object.prototype.hasOwnProperty.call(vars, k) ? String(vars[k]) : m));
        }
        return s;
    }

    // --- Static markup ------------------------------------------------------
    // The English in the markup is kept on first use (data-t-en…), so a
    // switch back to English restores it without a table.
    const ATTRS = [
        ["t", null],
        ["tTitle", "title"],
        ["tAria", "aria-label"],
        ["tPlaceholder", "placeholder"],
    ];

    function apply(root = document) {
        const scope = root.querySelectorAll ? root : document;
        const list = [...scope.querySelectorAll("[data-t], [data-t-title], [data-t-aria], [data-t-placeholder]")];
        if (root.dataset && root.matches?.("[data-t], [data-t-title], [data-t-aria], [data-t-placeholder]")) list.push(root);
        for (const el of list) {
            for (const [prop, attr] of ATTRS) {
                const key = el.dataset[prop];
                if (!key) continue;
                const store = prop + "En";
                if (el.dataset[store] === undefined) {
                    el.dataset[store] = attr ? (el.getAttribute(attr) || "") : el.textContent.trim();
                }
                const text = t(key, el.dataset[store]);
                if (attr) el.setAttribute(attr, text);
                else el.textContent = text;
            }
        }
    }

    // --- Route labels -------------------------------------------------------
    // Views register their routes in English; the burger and the phone title
    // show route labels, so a switch re-registers each route with its label
    // in the current language ("#/admin/users" → "fb.route.admin-users").
    const routeKey = (hash) => "fb.route." + (hash === "#/" ? "home" : hash.replace(/^#\//, "").replace(/\//g, "-"));
    const english = new Map();
    let relabeling = false;

    function relabelRoutes() {
        if (!window.sac?.router) return;
        relabeling = true;
        try {
            for (const r of sac.router.routes()) {
                if (!english.has(r.hash)) english.set(r.hash, r.label);
                const label = t(routeKey(r.hash), english.get(r.hash));
                if (label === r.label) continue;
                const hash = r.prefix ? (r.hash === "#/" ? "#/*" : `${r.hash}/*`) : r.hash;
                sac.router.register(hash, r.tag, { label, icon: r.icon, palette: r.palette, scope: r.scope });
            }
        } finally {
            relabeling = false;
        }
    }

    // A route registered later (the admin apps, after /me) arrives in
    // English — note it, then relabel it.
    let queued = false;
    window.addEventListener("sac:route-registered", (e) => {
        if (relabeling) return;
        const hash = e.detail?.hash;
        const route = hash && sac.router.routes().find((r) => r.hash === hash);
        if (route) english.set(hash, route.label);
        if (queued) return;
        queued = true;
        queueMicrotask(() => { queued = false; relabelRoutes(); });
    });

    window.fb = window.fb || {};
    fb.t = t;
    fb.i18n = { t, apply, relabelRoutes, routeKey };
})();
