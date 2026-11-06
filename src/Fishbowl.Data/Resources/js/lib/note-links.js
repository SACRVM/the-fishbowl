/*
 * fb.noteLinks — links from one note to another, as documents carry them:
 *
 *   [[Title]]  [[Title|shown text]]  [[decisions/0003-forward]]   a wikilink, by title or slug
 *   [text](note:01J…)                                             by id
 *   [text](../decisions/0003-forward.md#why)                      a relative file, by slug
 *
 * A wikilink resolves by title (any case), else by slug; a relative path
 * against the linking note's own slug folder (`a/b` links `../c.md` → `c`),
 * with or without ".md", else by its file name alone. Code (fenced or
 * inline) holds no links. The Notes app shows a note's links and the notes
 * that link to it under its text, and opens one on Ctrl/⌘+click in it — the
 * vendored editor renders none of these forms itself.
 *
 *   fb.noteLinks.parse(text)                → [{ kind: "wiki"|"id"|"path", target, label }]
 *   fb.noteLinks.resolve(link, from, notes) → note | null
 *   fb.noteLinks.outgoing(note, notes)      → [{ link, note|null }] (each target once)
 *   fb.noteLinks.backlinks(note, notes)     → notes that link to it
 *   fb.noteLinks.at(lineText, column)       → the link at a column of a line, or null
 */
(function () {
    if (!window.fb) return;

    const WIKI = /\[\[([^\[\]\n|]+?)(?:\|([^\[\]\n]+?))?\]\]/g;
    const MD = /\[([^\[\]\n]*)\]\(\s*<?([^()\s<>]+)>?(?:\s+"[^"]*")?\s*\)/g;
    const SCHEME = /^[a-z][a-z0-9+.-]*:/i;

    // The text without code: fenced blocks and inline spans become spaces,
    // so positions stay where they were.
    function withoutCode(text) {
        const lines = String(text || "").split("\n");
        let fence = null;
        return lines.map((line) => {
            const f = line.match(/^\s*(```|~~~)/);
            if (fence) {
                if (f && f[1] === fence) fence = null;
                return " ".repeat(line.length);
            }
            if (f) { fence = f[1]; return " ".repeat(line.length); }
            return line.replace(/(`+)[^`]*?\1/g, (m) => " ".repeat(m.length));
        }).join("\n");
    }

    function linksIn(text, withPos) {
        const src = withoutCode(text);
        const out = [];
        for (const m of src.matchAll(WIKI)) {
            const target = m[1].trim();
            if (target) out.push({ kind: "wiki", target, label: (m[2] || m[1]).trim(), start: m.index, end: m.index + m[0].length });
        }
        for (const m of src.matchAll(MD)) {
            const href = m[2].trim();
            let link = null;
            if (/^note:/i.test(href)) link = { kind: "id", target: href.slice(5) };
            else if (!SCHEME.test(href) && !href.startsWith("#") && !href.startsWith("/") && /\.md(#.*)?$/i.test(href))
                link = { kind: "path", target: decodeURIComponent(href.replace(/#.*$/, "")) };
            if (link) out.push({ ...link, label: m[1].trim() || link.target, start: m.index, end: m.index + m[0].length });
        }
        out.sort((a, b) => a.start - b.start);
        return withPos ? out : out.map(({ kind, target, label }) => ({ kind, target, label }));
    }

    const parse = (text) => linksIn(text, false);

    const lower = (s) => String(s || "").toLowerCase();
    const stripMd = (s) => s.replace(/\.md$/i, "");

    // `../c.md` from a note with slug `a/b/x` → `a/c`.
    function join(fromSlug, rel) {
        const dir = (fromSlug || "").split("/").slice(0, -1);
        for (const part of rel.split("/")) {
            if (part === "" || part === ".") continue;
            if (part === "..") dir.pop();
            else dir.push(part);
        }
        return dir.join("/");
    }

    function resolve(link, from, notes) {
        const list = notes || [];
        const bySlug = (slug) => list.find((n) => n.slug && lower(n.slug) === lower(slug))
            || list.find((n) => n.slug && lower(stripMd(n.slug)) === lower(stripMd(slug)));
        if (link.kind === "id") return list.find((n) => n.id === link.target) || null;
        if (link.kind === "wiki") {
            const t = lower(link.target.replace(/#.*$/, "").trim());
            return list.find((n) => lower(n.title).trim() === t) || bySlug(t) || null;
        }
        const path = link.target;
        const found = bySlug(join(from?.slug, path)) || bySlug(path);
        if (found) return found;
        const file = lower(stripMd(path.split("/").pop()));
        return list.find((n) => n.slug && lower(stripMd(n.slug.split("/").pop())) === file) || null;
    }

    function outgoing(note, notes) {
        const seen = new Set();
        const out = [];
        for (const link of parse(note?.content)) {
            const target = resolve(link, note, notes);
            const key = target ? "n:" + target.id : "t:" + lower(link.target);
            if (seen.has(key) || target?.id === note.id) continue;
            seen.add(key);
            out.push({ link, note: target });
        }
        return out;
    }

    function backlinks(note, notes) {
        if (!note) return [];
        return (notes || []).filter((n) => n.id !== note.id
            && parse(n.content).some((link) => resolve(link, n, notes)?.id === note.id));
    }

    function at(lineText, column) {
        return linksIn(lineText, true).find((l) => column >= l.start && column <= l.end) || null;
    }

    fb.noteLinks = { parse, resolve, outgoing, backlinks, at };
})();
