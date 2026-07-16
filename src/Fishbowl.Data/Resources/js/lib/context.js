/**
 * Fishbowl — active context (personal vs space) derived from the URL hash.
 *
 *   #/notes               → { type: "user" }
 *   #/space/SLUG/notes    → { type: "space", slug: "SLUG" }
 *
 * The URL is authoritative — two tabs can hold different contexts, a reload
 * preserves state, and deep-linking works. fb.api wrappers call `endpoint()`
 * to route every request to the matching `/api/v1/...` or
 * `/api/v1/spaces/SLUG/...` shape server-side.
 *
 * Views that need to re-fetch on a context switch listen for
 * `window.addEventListener("fb:context-changed", handler)` — we emit it in
 * response to every hashchange so subscribers can stay blissfully unaware of
 * the parsing logic.
 */
(function () {
    function parse() {
        const hash = window.location.hash || "#/";
        // #/space/SLUG or #/space/SLUG/anything — slug captures any chars but
        // slash. Personal-context paths fall through to the default branch.
        const m = hash.match(/^#\/space\/([^\/]+)(\/.*)?$/);
        if (m && m[1]) return { type: "space", slug: decodeURIComponent(m[1]) };
        return { type: "user" };
    }

    /**
     * Prefixes a resource path with `/spaces/SLUG` when the current context is
     * a space. `path` should be the personal-scope path (leading slash), e.g.
     * "/notes" or "/search/reindex". The caller never has to know which
     * context is active — just asks fb.context for the final path.
     */
    function endpoint(path) {
        const ctx = parse();
        if (ctx.type === "space") {
            return `/spaces/${encodeURIComponent(ctx.slug)}${path}`;
        }
        return path;
    }

    /**
     * Builds a hash path in the current context. A personal path like
     * "#/notes" stays unchanged when personal is active, and becomes
     * "#/space/SLUG/notes" when a space is active. Pass "#/" to get back to
     * the hub in the current context.
     */
    function hashFor(personalPath) {
        const ctx = parse();
        if (!personalPath.startsWith("#/")) personalPath = "#/" + personalPath.replace(/^#?\/?/, "");
        if (ctx.type === "space") {
            const suffix = personalPath.slice(1); // drop leading "#"
            return `#/space/${encodeURIComponent(ctx.slug)}${suffix === "/" ? "" : suffix}`;
        }
        return personalPath;
    }

    /**
     * Switches the active context. Navigates to the equivalent hash — e.g.
     * if you're on "#/notes" and call set({type:"space", slug:"foo"}), you
     * land on "#/space/foo/notes". Setting back to user strips the prefix.
     */
    function set(target) {
        const hash = window.location.hash || "#/";
        // Operate on the path portion (strip the leading "#").
        const path = hash.startsWith("#") ? hash.slice(1) : hash;
        // Remove any current /space/SLUG prefix to get back to the
        // personal-scope path. An empty remainder collapses to "/".
        const base = path.replace(/^\/space\/[^\/]+/, "") || "/";
        const next = target.type === "space"
            ? `#/space/${encodeURIComponent(target.slug)}${base === "/" ? "" : base}`
            : `#${base}`;
        if (next !== hash) window.location.hash = next;
        else emit(); // force emit even on no-op so subscribers can re-render
    }

    function emit() {
        const ctx = parse();
        window.dispatchEvent(new CustomEvent("fb:context-changed", { detail: ctx }));
    }

    // Hashchange fires on every URL update — translate into our higher-level
    // event so subscribers don't have to re-parse.
    window.addEventListener("hashchange", emit);

    fb.context = {
        get: parse,
        endpoint,
        hashFor,
        set,
    };
})();
