/**
 * Fishbowl — global namespace.
 * Loaded first. Populated incrementally by other lib scripts.
 *
 * Routing, workspace scope, icons and dialogs are the kit's (sac.router,
 * sac.scope, sac.icons, sac.dialog); window.fb holds only what is
 * Fishbowl's own — fb.api, fb.tags, fb.vault, fb.tagManager, fb.toolbar.
 */
(function () {
    if (window.fb) return; // idempotent
    window.fb = {
        version: null,                  // populated by /api/v1/version fetch
        api:     null,                  // populated by api.js
        /**
         * Nav ribbon toolbar — views call fb.toolbar.set([...]) to project
         * action icons into the shell's <sac-nav> ribbon. shell.js registers
         * the renderer; the router clears between view swaps.
         *
         * Item shape: { icon, title, onClick, active? }
         */
        toolbar: {
            _items: [],
            _nav:   null,
            set(items) {
                this._items = Array.isArray(items) ? items : [];
                this._nav?.renderToolbar();
            },
            clear() { this.set([]); }
        }
    };
})();
