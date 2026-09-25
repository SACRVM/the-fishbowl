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
})();
