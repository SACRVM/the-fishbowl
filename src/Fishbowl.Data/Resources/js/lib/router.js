/**
 * Fishbowl — router, delegating to the kit's sac.router.
 *
 * The hash router itself lives in the vendored appkit (kit/js/lib/router.js);
 * this file keeps the fb.router surface the views already call and configures
 * the kit's scope so a space workspace resolves to the same views:
 *
 *   #/notes               → personal workspace
 *   #/space/SLUG/notes    → space workspace (sac.scope strips the prefix)
 *
 * Views register via fb.router.register("#/hash", "tag-name", { label, icon }).
 * The shell's <sac-nav> lists the registered routes in its burger panel.
 *
 * Legacy views project ribbon actions via fb.toolbar.set([...]); the toolbar
 * is cleared on every hash change before the next view mounts, so an
 * outgoing view never has to clean up explicitly.
 */
(function () {
    sac.scope.configure({ prefix: "space" });

    fb.router = {
        register(hash, tagName, options = {}) {
            sac.router.register(hash, tagName, options);
        },
        routes()          { return sac.router.routes(); },
        current()         { return sac.router.current(); },
        // Personal-scope hash of the active view, with any /space/SLUG
        // prefix stripped — "am I on #/notes?" works in both workspaces.
        currentResource() { return sac.router.currentResource(); },
        navigate(hash)    { sac.router.navigate(hash); },
        mount(selector) {
            // Registered before sac.router's own listener, so the toolbar is
            // empty by the time the incoming view's connectedCallback runs.
            window.addEventListener("hashchange", () => fb.toolbar.clear());
            sac.router.mount(selector);
        }
    };
})();
