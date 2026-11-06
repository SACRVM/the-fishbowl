/*
 * fb.tagExpr — a filter over tags, as a person writes it:
 *
 *   kind:decision AND NOT status:superseded
 *   kind:feature OR kind:plan
 *   area:lighting (status:draft OR status:review)      — side by side is AND
 *   -status:superseded                                  — "-" is NOT
 *   status:*                                            — any tag of a namespace
 *
 * AND / OR / NOT in any case, and the German UND / ODER / NICHT; NOT binds
 * before AND, AND before OR, parentheses group. Tags are compared as stored
 * (lower case). Saved filters of the Notes app (note_filters) hold this text.
 *
 *   const f = fb.tagExpr.compile(text)   → { test(tags) → boolean, tags: [...] }
 *                                          throws Error (with .pos) when it can't read it
 *   fb.tagExpr.check(text)               → null, or the message why not
 */
(function () {
    if (!window.fb) return;

    const OPS = { and: "AND", und: "AND", or: "OR", oder: "OR", not: "NOT", nicht: "NOT" };

    function tokenize(text) {
        const tokens = [];
        const re = /\s*(\(|\)|-(?=[^\s()])|[^\s()]+)/gy;
        let m;
        while (re.lastIndex < text.length && (m = re.exec(text))) {
            const raw = m[1];
            const pos = m.index + m[0].length - raw.length;
            if (raw === "(" || raw === ")") tokens.push({ type: raw, pos });
            else if (raw === "-") tokens.push({ type: "NOT", pos });
            else if (OPS[raw.toLowerCase()]) tokens.push({ type: OPS[raw.toLowerCase()], pos });
            else tokens.push({ type: "TAG", value: raw.toLowerCase(), pos });
        }
        if (re.lastIndex < text.length && text.slice(re.lastIndex).trim()) {
            throw fail(fb.t("fb.notes.expr.unreadable", "Can't read this filter."), re.lastIndex);
        }
        return tokens;
    }

    function fail(message, pos) {
        const err = new Error(message);
        err.pos = pos;
        return err;
    }

    function compile(text) {
        const tokens = tokenize(String(text || ""));
        if (!tokens.length) throw fail(fb.t("fb.notes.expr.empty", "Write at least one tag."), 0);
        let i = 0;
        const peek = () => tokens[i];
        const tags = new Set();

        function or() {
            let left = and();
            while (peek()?.type === "OR") { i++; const l = left, r = and(); left = (t) => l(t) || r(t); }
            return left;
        }
        function and() {
            let left = not();
            for (;;) {
                const t = peek();
                if (t?.type === "AND") { i++; }
                else if (!t || t.type === "OR" || t.type === ")") break;
                const l = left, r = not();
                left = (s) => l(s) && r(s);
            }
            return left;
        }
        function not() {
            if (peek()?.type === "NOT") { i++; const inner = not(); return (t) => !inner(t); }
            return atom();
        }
        function atom() {
            const t = peek();
            if (!t) throw fail(fb.t("fb.notes.expr.missing", "A tag is missing at the end."), tokens.length ? tokens[tokens.length - 1].pos : 0);
            if (t.type === "(") {
                i++;
                const inner = or();
                if (peek()?.type !== ")") throw fail(fb.t("fb.notes.expr.paren", "A closing bracket is missing."), peek()?.pos ?? t.pos);
                i++;
                return inner;
            }
            if (t.type === "TAG") {
                i++;
                const name = t.value;
                tags.add(name);
                if (name.endsWith(":*")) {
                    const prefix = name.slice(0, -1);
                    return (set) => [...set].some((x) => x.startsWith(prefix));
                }
                return (set) => set.has(name);
            }
            throw fail(fb.t("fb.notes.expr.unexpected", "\"{word}\" can't stand here.", { word: t.type === ")" ? ")" : t.type }), t.pos);
        }

        const fn = or();
        if (i < tokens.length) throw fail(fb.t("fb.notes.expr.unexpected", "\"{word}\" can't stand here.", { word: tokens[i].value || tokens[i].type }), tokens[i].pos);
        return {
            test: (list) => fn(new Set((list || []).map((x) => String(x).toLowerCase()))),
            tags: [...tags],
        };
    }

    function check(text) {
        try { compile(text); return null; }
        catch (err) { return err.message; }
    }

    fb.tagExpr = { compile, check };
})();
