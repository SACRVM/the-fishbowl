/*
 * fb.access — may this person write in the active workspace?
 *
 * The personal workspace: always. A space: unless their role there is
 * Reader. Views ask before they offer a write action, so a Reader never
 * meets a button the server would refuse (no dead buttons). The answer is
 * cached per space and dropped when the space list changes.
 *
 *   await fb.access.canWrite()   → boolean, for the active workspace
 *   await fb.access.role()       → "owner" in the personal workspace, else the
 *                                  space role ("reader" … "owner"), null unknown
 *   await fb.access.canAdmin()   → owner or a space's Admin+ (lifts a note's lock)
 */
(function () {
    let cache = new Map();   // slug → Promise<role | null>
    window.addEventListener("fb:spaces-changed", () => { cache = new Map(); });

    function role() {
        const scope = window.sac?.scope?.get?.();
        if (scope?.type !== "scoped") return Promise.resolve("owner");
        if (!cache.has(scope.slug)) {
            cache.set(scope.slug, fb.api.spaces.list()
                .then((spaces) => (spaces || []).find((s) => s.slug === scope.slug)?.role || null)
                // Unknown: offer nothing the server may refuse.
                .catch(() => { cache.delete(scope.slug); return null; }));
        }
        return cache.get(scope.slug);
    }

    const canWrite = async () => { const r = await role(); return !!r && r !== "reader"; };
    const canAdmin = async () => ["admin", "owner"].includes(await role());

    fb.access = { canWrite, role, canAdmin };
})();
