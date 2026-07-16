/**
 * fb.tags — in-memory cache over /api/v1/tags.
 *
 * - all()        -> Promise<Tag[]> (cached after first call)
 * - colorFor(n)  -> string         (sync; falls back to deterministic hash
 *                                    matching Fishbowl.Core.Util.TagPalette)
 * - byName(n)    -> Tag | undefined (sync; only after all() has resolved)
 * - invalidate() -> void           (drops cache; emits 'tags-changed' on window)
 *
 * The deterministic hash MUST stay byte-for-byte identical to TagPalette.cs's
 * FNV-1a so an unsaved chip color matches what the server will assign on first
 * EnsureExistsAsync — otherwise chips would flicker between client/server hash.
 */
(function () {
    const SLOTS = [
        "blue", "orange", "red", "green", "purple",
        "pink", "yellow", "teal", "gray", "indigo",
    ];

    function defaultColorFor(name) {
        let hash = 2166136261 >>> 0;
        for (let i = 0; i < name.length; i++) {
            hash ^= name.charCodeAt(i);
            hash = Math.imul(hash, 16777619) >>> 0;
        }
        return SLOTS[hash % SLOTS.length];
    }

    let cache = null;       // Promise<Tag[]> | null
    let byNameMap = null;   // Map<string, Tag> | null

    async function loadInto(promise) {
        const tags = await promise;
        byNameMap = new Map(tags.map(t => [t.name, t]));
        return tags;
    }

    fb.tags = {
        SLOTS,

        all() {
            if (!cache) cache = loadInto(fb.api.tags.list());
            return cache;
        },

        byName(name) {
            return byNameMap ? byNameMap.get(name) : undefined;
        },

        colorFor(name) {
            const known = byNameMap?.get(name);
            return known ? known.color : defaultColorFor(name);
        },

        defaultColorFor,

        invalidate() {
            cache = null;
            byNameMap = null;
            window.dispatchEvent(new CustomEvent("fb-tags-invalidated"));
        }
    };

    // Context switch flips the underlying DB. Tag names + colours come from
    // that DB, so a space tag won't exist in the personal registry (and vice
    // versa) — drop the cache so the next all()/colorFor() call re-hydrates
    // from the new context. Without this, a tag added in a space workspace
    // never shows up until a manual reload.
    window.addEventListener("fb:context-changed", () => {
        if (fb.tags) fb.tags.invalidate();
    });
})();
