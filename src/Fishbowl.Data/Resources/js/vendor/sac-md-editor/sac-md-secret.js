/**
 * :::secret blocks for <sac-md-editor> - a block registration, not part of
 * the editor. Load it after js/sac-md-editor.js:
 *
 *   <script defer src="js/sac-md-editor.js"></script>
 *   <script defer src="js/sac-md-secret.js"></script>
 *
 * Syntax (fenced-div style; two or three colons accepted on read, three
 * written - content from before 2026-08 carries two):
 *
 *   :::secret
 *   the lines in here are blurred
 *   :::end
 *
 * Lines between the boundaries render blurred; an eye toggle on the
 * opening line reveals the block for the session. The pill reads
 * "Secret" / "Geheim" with the page language (kit i18n when present).
 *
 * SECURITY: this is a purely VISUAL layer. The plaintext stays in the DOM
 * and in whatever the host saves from `value`. The blur is a courtesy
 * against shoulder-surfing, never a security boundary - a host that needs
 * secrets kept from an index, an agent or a wire has to enforce that
 * server-side (the upstream app strips these blocks on every machine-facing
 * path). Do not build on the blur.
 */
(function () {
    const Editor = customElements.get("sac-md-editor");
    if (!Editor || typeof Editor.registerBlock !== "function") {
        console.warn("[sac-md-secret] load js/sac-md-editor.js first - :::secret not registered.");
        return;
    }

    let stringsAdded = false;
    const t = (key, fallback) => {
        const sac = window.sac;
        if (!stringsAdded && sac && sac.i18n && typeof sac.i18n.add === "function") {
            stringsAdded = true;
            sac.i18n.add("de", { "md-editor.secret": "Geheim" });
        }
        return sac && typeof sac.t === "function" ? sac.t(key, fallback) : fallback;
    };

    Editor.registerBlock({
        name:   "secret",
        open:   /^:{2,3}secret(\s|$)/,
        close:  /^:{2,3}end(\s|$)/,
        masked: true,
        toggle: true,
        // Lock glyph + the word, in the page language.
        label:  () => "\uD83D\uDD12 " + t("md-editor.secret", "Secret"),
        // The secret look: warm monospace body text.
        css:    ".line.block-secret.block-body {" +
                " color: var(--mdb-c);" +
                " font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;" +
                " font-size: 0.9em; }",
    });
})();
