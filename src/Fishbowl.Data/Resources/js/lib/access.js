/*
 * fb.access — may this person write in the active workspace?
 *
 * The personal workspace: always. A space: unless their role there is
 * Reader. Views ask before they offer a write action, so a Reader never
 * meets a button the server would refuse (no dead buttons). The answer is
 * cached per space and dropped when the space list changes.
 *
 *   await fb.access.canWrite()   → boolean, for the active workspace
 */
(function () {
    let cache = new Map();   // slug → Promise<boolean>
    window.addEventListener("fb:spaces-changed", () => { cache = new Map(); });

    function canWrite() {
        const scope = window.sac?.scope?.get?.();
        if (scope?.type !== "scoped") return Promise.resolve(true);
        if (!cache.has(scope.slug)) {
            cache.set(scope.slug, fb.api.spaces.list()
                .then((spaces) => (spaces || []).find((s) => s.slug === scope.slug)?.role !== "reader")
                // Unknown: offer nothing the server may refuse.
                .catch(() => { cache.delete(scope.slug); return false; }));
        }
        return cache.get(scope.slug);
    }

    fb.access = { canWrite };
})();
