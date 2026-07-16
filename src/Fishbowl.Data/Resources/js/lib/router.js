/**
 * Fishbowl — hash-based SPA router.
 * Views are custom elements registered via fb.router.register("#/hash", "tag-name", { label, icon }).
 * On hashchange, the root mount point's innerHTML is swapped to the matching tag.
 */
(function () {
    const routes = new Map();    // "#/notes" → { tag, label, icon }
    let rootElement = null;

    function currentHash() {
        return window.location.hash || "#/";
    }

    // Strips `/space/SLUG` from the current hash so space-context routes share
    // the same view registrations as personal-context ones. A view in a space
    // workspace looks identical — only the data (via fb.context + fb.api)
    // differs. This keeps `fb.router.register("#/notes", ...)` working for
    // both `#/notes` and `#/space/SLUG/notes`.
    function resourceHash() {
        const hash = currentHash();
        const m = hash.match(/^#\/space\/[^\/]+(\/.*)?$/);
        if (m) return "#" + (m[1] || "/");
        return hash;
    }

    function render() {
        if (!rootElement) return;
        // Clear any per-view toolbar items left over from the previous view.
        // Views that need toolbar actions call fb.toolbar.set(...) in
        // connectedCallback (or on selection change) — the outgoing view
        // never needs to clean up explicitly.
        if (window.fb?.toolbar) window.fb.toolbar.clear();

        const key = resourceHash();
        const entry = routes.get(key) || routes.get("#/");
        if (!entry) { rootElement.innerHTML = ""; return; }
        rootElement.innerHTML = `<${entry.tag}></${entry.tag}>`;
    }

    fb.router = {
        register(hash, tagName, options = {}) {
            routes.set(hash, {
                tag: tagName,
                label: options.label || tagName,
                icon:  options.icon  || null
            });
            // Notify listeners (e.g. <fb-nav>) that the route table changed.
            // Needed because nav components are instantiated before view
            // scripts register, so their first render sees an empty map.
            window.dispatchEvent(new CustomEvent("fb:route-registered", {
                detail: { hash, tag: tagName }
            }));
            if (rootElement && resourceHash() === hash) render();
        },
        routes() {
            return Array.from(routes.entries()).map(([hash, info]) => ({ hash, ...info }));
        },
        current() {
            return currentHash();
        },
        // Returns the personal-scope hash of the currently-active view,
        // regardless of space prefix. Useful for "am I on #/notes?" checks
        // that should succeed in both contexts.
        currentResource() {
            return resourceHash();
        },
        navigate(hash) {
            window.location.hash = hash;
        },
        mount(selector) {
            rootElement = document.querySelector(selector);
            if (!rootElement) {
                console.error(`[fb.router] mount: no element matches ${selector}`);
                return;
            }
            window.addEventListener("hashchange", render);
            render();
        }
    };
})();
