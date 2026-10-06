/**
 * Fishbowl — the app's settings for the vendored kit. Runs right after the
 * kit's own lib scripts, before any view registers or the shell mounts.
 *
 * Routing, workspace scope and icons are the kit's own (sac.router,
 * sac.scope, sac.icons) — Fishbowl adds no layer over them:
 *
 *   #/notes               → personal workspace  sac.scope.get() → { type: "root" }
 *   #/space/SLUG/notes    → space workspace     sac.scope.get() → { type: "scoped", slug }
 *
 * The kit's default hash prefix is "scope"; Fishbowl's URLs say "space".
 * Note the REST nesting is plural (/api/v1/spaces/SLUG/…), so fb.api builds
 * its paths itself instead of using sac.scope.endpoint().
 */
(function () {
    sac.scope.configure({ prefix: "space" });

    // The nav's brand mark; the kit ships no fish. Registered before any
    // component renders so the nav's first paint already finds it.
    if (!sac.icons.has("fish")) {
        sac.icons.register("fish",
            '<ellipse cx="10" cy="12" rx="7" ry="4"/><path d="M17 12l4-3v6z"/>' +
            '<circle cx="6" cy="11" r="1" fill="currentColor"/><path d="M7 14q1 0.6 2 0"/>');
    }
    // The system inbox's envelope; the kit ships no mail icon yet.
    if (!sac.icons.has("mail")) {
        sac.icons.register("mail",
            '<rect x="2" y="4" width="20" height="16" rx="2"/><path d="m22 7-8.97 5.7a1.94 1.94 0 0 1-2.06 0L2 7"/>');
    }
    // The account menu's theme items (dark / light / like the system); the
    // kit's own switch draws these inline and registers none.
    sac.icons.register("fb-theme-dark", '<path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z"/>');
    sac.icons.register("fb-theme-light",
        '<circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.93 4.93l1.41 1.41M17.66 17.66l1.41 1.41' +
        'M2 12h2M20 12h2M6.34 17.66l-1.41 1.41M19.07 4.93l-1.41 1.41"/>');
    sac.icons.register("fb-theme-auto",
        '<circle cx="12" cy="12" r="9"/><path d="M12 3a9 9 0 0 1 0 18z" fill="currentColor"/>');
})();
