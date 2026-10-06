/**
 * The page theme before the first paint. Loaded without `defer` in <head>
 * of every page (the SPA and /login /setup /pending /oauth): the choice is
 * made in the account menu (shell.js, through sac.apps.theme), but every
 * other script is deferred, so a light page would flash dark first.
 *
 * The kit's rule: localStorage "sac-theme" = "light" | "auto" sets
 * <html data-theme>, anything else (or no storage) is the kit's dark ground.
 */
(function () {
    let stored = null;
    try { stored = localStorage.getItem("sac-theme"); } catch (err) { /* refused: dark */ }
    if (stored === "light" || stored === "auto") document.documentElement.setAttribute("data-theme", stored);
})();
