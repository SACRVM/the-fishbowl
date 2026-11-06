/**
 * fb.tags — in-memory cache over /api/v1/tags.
 *
 * - all()        -> Promise<Tag[]> (cached after first call)
 * - colorFor(n)  -> string         (sync; falls back to deterministic hash
 *                                    matching Fishbowl.Core.Util.TagPalette)
 * - byName(n)    -> Tag | undefined (sync; only after all() has resolved)
 * - invalidate() -> void           (the next all() reads again; emits
 *                                    'fb-tags-invalidated' on window — a view
 *                                    that paints chips repaints after all())
 *
 * Until that read lands, colorFor keeps answering from the last one: a tag
 * just made or recoloured would otherwise flash in its hash colour (often
 * grey) wherever a list repaints in between.
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

    // Only the newest read fills the map: one started before an invalidate
    // that lands after the next would put the old colours back. A failed read
    // isn't kept, so the next all() tries again.
    function load() {
        const p = fb.api.tags.list().then(
            (tags) => { if (cache === p) byNameMap = new Map(tags.map(t => [t.name, t])); return tags; },
            (err) => { if (cache === p) cache = null; throw err; });
        return p;
    }

    fb.tags = {
        SLOTS,

        all() {
            if (!cache) cache = load();
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
            window.dispatchEvent(new CustomEvent("fb-tags-invalidated"));
        }
    };

    // Context switch flips the underlying DB. Tag names + colours come from
    // that DB, so a space tag won't exist in the personal registry (and vice
    // versa) — drop the cache so the next all()/colorFor() call re-hydrates
    // from the new context. Without this, a tag added in a space workspace
    // never shows up until a manual reload.
    // The old workspace's colours don't carry over, not even until the read.
    window.addEventListener("sac:scope-changed", () => {
        byNameMap = null;
        if (fb.tags) fb.tags.invalidate();
    });
})();
