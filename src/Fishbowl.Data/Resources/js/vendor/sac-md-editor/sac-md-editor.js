/**
 * <sac-md-editor> — SACRVM APPKIT add-on module (not part of the core kit).
 *
 * Zero dependencies beyond the kit's vendored marked + DOMPurify, zero build
 * step, one classic deferred script. Styling lives in the shadow root but
 * inherits the kit's seed tokens (--fg, --field, --accent, --text, --border,
 * --on-accent, ...), each with a dark fallback, so the editor rethemes with
 * the host page in light and dark like every kit component and still renders
 * standalone.
 *
 * Obsidian-style "live preview" markdown editor. There is no mode switch:
 *   - The line your caret sits on shows as raw markdown source (flat text).
 *   - Every other line shows as rendered markdown, with syntax markers
 *     (**, *, #, backticks, brackets) dimmed via a .marker class.
 *
 * The canonical document is the concatenated textContent of every line div.
 * Every transformation preserves textContent exactly - marker characters
 * stay in the DOM, they just get wrapped in inline elements so CSS can
 * style them. This is what makes the save/load round-trip trivial and the
 * active/inactive flip cheap: we never synthesize or delete real text.
 *
 * Attributes:
 *   placeholder - shown when the document is empty.
 *   readonly    - disables editing and hides the formatting toolbar.
 *
 * Properties:
 *   value - string; markdown source (getter + setter).
 *
 * Events - the kit convention (CustomEvent, detail { value }, bubbles, NOT
 * composed; never fired by a programmatic `value` set):
 *   sac:input  - once after every edit (keystroke, paste, toolbar action,
 *                undo/redo, table re-alignment); detail.value = the source.
 *   sac:change - when focus leaves the component after an edit;
 *                detail.value = the source.
 * Legacy, still fired until the consumers have moved (see CLAUDE.md):
 *   input  - native Event after every edit (may arrive more than once per
 *            keystroke: the browser's own plus the editor's).
 *   change - native Event when focus leaves after an edit.
 *
 * Methods:
 *   focus() - focus the editor surface.
 *
 * Language:
 *   The editor's own strings (toolbar labels and tooltips, the reveal
 *   toggle, the link prompt) follow the page language the kit way: sac.t()
 *   with the English text as the inline fallback, relabelled in place on
 *   sac.lang.onChange. A German table registers itself through
 *   sac.i18n.add (keys "md-editor.*"); a host adds other languages with the
 *   same keys. Without the kit's globals.js the editor stays English. The
 *   `placeholder` attribute is the host's string - translate it there.
 *
 * Keyboard:
 *   Ctrl/Cmd+B, Ctrl/Cmd+I, Ctrl/Cmd+K    bold / italic / link
 *   Enter                                  split; continues `- ` `* ` `+ ` `1. ` `1) `
 *                                          lists with the same marker
 *   Enter on empty list item               exits the list
 *   Backspace at start of non-first line   merge with previous line
 *   Tab                                    two-space soft tab
 *
 * Tables (GFM pipe tables): rendered as a real grid on inactive lines, raw
 * source on the caret line. The toolbar's Table button inserts a 2-column
 * table with the first header cell selected. Inside a table:
 *   Tab / Shift+Tab                        next / previous cell, content
 *                                          selected; past the last cell a
 *                                          new row is added
 *   Enter                                  new empty row below; on an empty
 *                                          row it leaves the table
 *   Esc                                    leaves the table: an empty row
 *                                          goes, else the caret moves to the
 *                                          (empty) line after the table
 * With the caret in a table the Table button (highlighted then) opens a
 * dialog instead: a grid to add, delete and move columns and rows and set
 * each column's alignment. Enter or Apply writes the table back formatted
 * (an undoable edit); Esc or Cancel changes nothing. Pipes typed in a
 * cell are escaped (\|) on the way back.
 * Leaving a table (caret moves out, or focus leaves the editor) lines its
 * pipes up again. That rewrites source text, so it is an edit: `input`
 * fires and it is undoable.
 *
 * Bounded blocks (registry): the editor knows no block names of its own. A
 * host registers them, once per page, on the element class:
 *
 *   const Editor = customElements.get("sac-md-editor");
 *   const off = Editor.registerBlock({
 *       name:      "spoiler",                 // a-z 0-9 -, unique
 *       open:      /^:::spoiler(\s|$)/,       // opening line, and
 *       close:     /^:::end(\s|$)/,           // closing line - or instead:
 *       match:     (src) => "open" | "close" | "line" | null,
 *       masked:    true,     // body lines blurred until revealed
 *       toggle:    true,     // eye button on the opening line
 *       label:     "Spoiler" or () => "Spoiler",   // pill on the opening line
 *       className: "",       // extra class on every line of the block
 *       color:     "#8b5cf6",                 // card tint (default warm)
 *       css:       ".line.block-spoiler.block-body { font-style: italic; }",
 *   });
 *   off();                                   // or Editor.unregisterBlock(name)
 *   Editor.blocks                            // registered names
 *
 * In read-only mode (no caret) a click or tap on any line of a masked
 * block reveals / hides the whole block, like its eye; not when the click
 * ends a text selection or hits a link.
 *
 * match(src) is the general form: "open" / "close" bound a block, "line"
 * makes one self-contained line a block (a marker form). open/close is the
 * shorthand. Blocks are only recognised outside code fences, do not nest
 * (inside a block only its own close counts) and render inline markdown in
 * their body. Every line of a block gets .block-<part> (open, close, body,
 * line) and .block-<name>; `css` is injected into every editor's shadow
 * root, which is the only way a host can style them. Registering (or
 * unregistering) re-renders every editor on the page.
 *
 * :::secret is not built in: js/sac-md-secret.js registers it (load it
 * after this file). The demo does, and so does any host that wants it.
 *
 * SECURITY: masking is a purely VISUAL layer. The plaintext stays in the DOM
 * and in whatever the host saves from `value`. The blur is a courtesy
 * against shoulder-surfing, never a security boundary - a host that needs
 * content kept from an index, an agent or a wire has to enforce that
 * server-side. Do not build on the blur.
 *
 * Dependencies (loaded as globals before this script):
 *   marked     - CommonMark + GFM tokenizer. We only use marked.Lexer.lexInline
 *                and walk the token tree ourselves (see renderInline below).
 *   DOMPurify  - HTML sanitiser. Second line of defence behind our own
 *                escape-don't-parse policy for inline HTML.
 */

// Reveal-toggle SVG: open eye with a diagonal slash that's hidden by default
// and surfaced via CSS when the block carries .block-revealed. One-line so
// no whitespace text nodes end up in line.textContent and break round-trip.
const REVEAL_EYE_SVG =
    `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" ` +
    `stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">` +
    `<path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z"/>` +
    `<circle cx="12" cy="12" r="3"/>` +
    `<line class="sac-eye-slash" x1="3" y1="3" x2="21" y2="21"/>` +
    `</svg>`;

// Kit i18n: sac.t when globals.js is loaded, the English fallback when the
// editor runs without the kit.
const mdT = (key, fallback) =>
    (window.sac && typeof window.sac.t === "function") ? window.sac.t("md-editor." + key, fallback) : fallback;

// German strings. Registered lazily (first connect), not at parse time: a
// page may load this file before the kit's globals.js. ASCII source, so
// umlauts are escaped.
let mdStringsAdded = false;
function mdAddStrings() {
    if (mdStringsAdded) return;
    if (!(window.sac && window.sac.i18n && typeof window.sac.i18n.add === "function")) return;
    mdStringsAdded = true;
    window.sac.i18n.add("de", {
        "md-editor.bold":        "Fett (Strg+B)",
        "md-editor.italic":      "Kursiv (Strg+I)",
        "md-editor.strike":      "Durchgestrichen",
        "md-editor.h1":          "\u00dcberschrift 1",
        "md-editor.h2":          "\u00dcberschrift 2",
        "md-editor.h3":          "\u00dcberschrift 3",
        "md-editor.link":        "Link (Strg+K)",
        "md-editor.linkLabel":   "Link",
        "md-editor.code":        "Code im Text",
        "md-editor.codeLabel":   "Code",
        "md-editor.ul":          "Aufz\u00e4hlung",
        "md-editor.ulLabel":     "Liste",
        "md-editor.ol":          "Nummerierte Liste",
        "md-editor.olLabel":     "1. Liste",
        "md-editor.quote":       "Zitat",
        "md-editor.quoteLabel":  "Zitat",
        "md-editor.hr":          "Trennlinie",
        "md-editor.hrLabel":     "Linie",
        "md-editor.tableLabel":  "Tabelle",
        "md-editor.tableColumn": "Spalte",
        "md-editor.table":       "Tabelle einf\u00fcgen oder bearbeiten",
        "md-editor.tdTitle":     "Tabelle bearbeiten",
        "md-editor.tdAddRow":    "Zeile hinzuf\u00fcgen",
        "md-editor.tdAddCol":    "Spalte hinzuf\u00fcgen",
        "md-editor.tdCancel":    "Abbrechen",
        "md-editor.tdApply":     "\u00dcbernehmen",
        "md-editor.tdLeft":      "Spalte nach links",
        "md-editor.tdRight":     "Spalte nach rechts",
        "md-editor.tdDelCol":    "Spalte l\u00f6schen",
        "md-editor.tdUp":        "Zeile nach oben",
        "md-editor.tdDown":      "Zeile nach unten",
        "md-editor.tdDelRow":    "Zeile l\u00f6schen",
        "md-editor.tdAlign":     "Ausrichtung",
        "md-editor.tdAl-":       "keine",
        "md-editor.tdAll":       "links",
        "md-editor.tdAlc":       "zentriert",
        "md-editor.tdAlr":       "rechts",
        "md-editor.tdHeader":    "Kopf",
        "md-editor.tdCell":      "Zelle",
        "md-editor.linkPrompt":  "Link-Adresse",
        "md-editor.linkText":    "Linktext",
        "md-editor.reveal":      "Inhalt zeigen / verbergen",
        "md-editor.revealHint":  "Klicken zum Zeigen oder Verbergen",
    });
}

// Toolbar strings per data-fmt: [tooltip key, English tooltip, label key,
// English label]. A null label key keeps the template's glyph (B, I, S, H1..).
const MD_TOOLBAR_TEXT = {
    bold:   ["bold",   "Bold (Ctrl+B)",   null, null],
    italic: ["italic", "Italic (Ctrl+I)", null, null],
    strike: ["strike", "Strikethrough",   null, null],
    h1:     ["h1",     "Heading 1",       null, null],
    h2:     ["h2",     "Heading 2",       null, null],
    h3:     ["h3",     "Heading 3",       null, null],
    link:   ["link",   "Link (Ctrl+K)",   "linkLabel",  "Link"],
    code:   ["code",   "Inline code",     "codeLabel",  "Code"],
    ul:     ["ul",     "Bullet list",     "ulLabel",    "List"],
    ol:     ["ol",     "Numbered list",   "olLabel",    "1. List"],
    quote:  ["quote",  "Blockquote",      "quoteLabel", "Quote"],
    hr:     ["hr",     "Horizontal rule", "hrLabel",    "HR"],
    table:  ["table",  "Insert a table, or edit the one at the caret", "tableLabel", "Table"],
};

class SacMdEditor extends HTMLElement {
    static observedAttributes = ["placeholder", "readonly"];

    constructor() {
        super();
        this.attachShadow({ mode: "open" });
        this._activeLine  = null;
        this._dirty       = false;
        this._onDocSelect = null;
        this._suppressSelect = false;
        // Undo/redo. Snapshots are { value, lineIdx, offset } so caret position
        // restores too, not just text. Keystrokes are coalesced via debounced
        // scheduling (_historyTimer) - a one-shot burst of typing becomes one
        // undo step, not one step per character. Capped at 200 entries so a
        // long editing session doesn't balloon memory. _suppressHistory is set
        // while replaying a snapshot to stop the replay itself from pushing a
        // new history entry (which would truncate the redo stack).
        this._history         = [];
        this._historyIdx      = -1;
        this._historyTimer    = null;
        this._suppressHistory = false;
    }

    // Block registry - see the header. Static: blocks are a page-wide
    // vocabulary, like custom elements themselves.
    static registerBlock(def) {
        const block = compileBlock(def);
        BLOCKS.delete(block.name);           // re-registering replaces
        BLOCKS.set(block.name, block);
        refreshBlocks();
        return () => {
            if (BLOCKS.get(block.name) !== block) return;
            BLOCKS.delete(block.name);
            refreshBlocks();
        };
    }
    static unregisterBlock(name) {
        if (BLOCKS.delete(name)) refreshBlocks();
    }
    static get blocks() { return [...BLOCKS.keys()]; }

    connectedCallback() {
        mdAddStrings();
        if (!this.shadowRoot.firstChild) this._render();
        LIVE_EDITORS.add(this);
        // Registrations made while this editor was detached.
        if (this._blockGen !== blockGen) this._refreshBlocks();
        this._relabel();
        if (window.sac && window.sac.lang && !this._offLang) {
            this._offLang = window.sac.lang.onChange(() => this._relabel());
        }
        // selectionchange fires on document, not the element - the only way
        // to reliably track caret movement inside a contenteditable.
        this._onDocSelect = () => this._handleSelectionChange();
        document.addEventListener("selectionchange", this._onDocSelect);
    }

    disconnectedCallback() {
        if (this._onDocSelect) {
            document.removeEventListener("selectionchange", this._onDocSelect);
            this._onDocSelect = null;
        }
        if (this._offLang) { this._offLang(); this._offLang = null; }
        LIVE_EDITORS.delete(this);
    }

    /** Kit value events: detail { value }, bubbles, not composed. */
    _fireSac(type) {
        this.dispatchEvent(new CustomEvent(type, {
            detail: { value: this.value },
            bubbles: true,
            composed: false,
        }));
    }

    /** One sac:input per edit. A keystroke reaches the host as the browser's
     *  input AND the editor's synthetic one, and the browser runs microtasks
     *  between the two listeners, so batching cannot merge them. Instead
     *  sac:input fires only when the value differs from the last one it
     *  carried (or from the last programmatic set) - an event that changes
     *  nothing reports nothing. */
    _queueSacInput() {
        const value = this.value;
        if (value === this._sacValue) return;
        this._sacValue = value;
        this.dispatchEvent(new CustomEvent("sac:input", {
            detail: { value },
            bubbles: true,
            composed: false,
        }));
    }

    /** The registry changed: new block CSS, and every line re-derived. */
    _refreshBlocks() {
        this._blockGen = blockGen;
        if (!this._editor) return;
        this._blockStyle.textContent = blockCss;
        for (const line of this._editor.children) line._blockState = undefined;
        this._syncBlockState();
    }

    /** Every editor-owned string in the current language, in place - no
     *  re-render, so the document and the caret are untouched. Only
     *  attributes and toolbar text change; line textContent never does. */
    _relabel() {
        this._toolbar.querySelectorAll("button[data-fmt]").forEach((btn) => {
            const spec = MD_TOOLBAR_TEXT[btn.dataset.fmt];
            if (!spec) return;
            const tip = mdT(spec[0], spec[1]);
            btn.title = tip;
            btn.setAttribute("aria-label", tip);
            if (spec[2]) btn.textContent = mdT(spec[2], spec[3]);
        });
        // Block pills are CSS generated content; a label may be a function
        // of the page language, so every pill is re-read here.
        this._editor.querySelectorAll(".line.block-open, .line.block-line").forEach((l) => {
            const def = BLOCKS.get(l.dataset.blockName);
            if (def) setBlockLabel(l, def);
        });
        if (this._tdlg) {
            this._tdLabels();
            if (this._tdlg.open) this._tdRender();
        }
        this._syncRevealHints();
        const reveal = this._revealLabel();
        this._editor.querySelectorAll(".sac-reveal-toggle").forEach((el) => {
            el.setAttribute("aria-label", reveal);
            el.title = reveal;
        });
    }

    _revealLabel() { return mdT("reveal", "Show / hide content"); }

    get value() {
        if (!this._editor) return "";
        return Array.from(this._editor.children)
            .map(line => line.textContent)
            .join("\n");
    }

    set value(v) {
        this._applyValue(v);
        this._sacValue = this.value;      // the baseline for sac:input
        // External value-set is a "new document" - reset history so Ctrl+Z
        // can't restore the previous note's content into this one.
        this._resetHistory();
    }

    /** DOM-only value replacement. Extracted from the setter so undo/redo
     *  can restore state without resetting history. Does not touch
     *  _history/_historyIdx - caller decides. */
    _applyValue(v) {
        if (!this.shadowRoot.firstChild) this._render();
        const text = v == null ? "" : String(v);
        const lines = text.split("\n");
        // Always at least one line - an empty document still needs a caret target.
        if (lines.length === 0) lines.push("");
        this._activeLine = null;
        // Plain lines first, then one sweep renders them all: a line's look
        // can depend on lines BELOW it (a setext underline, a link
        // definition), which a top-down build cannot see yet. Rendered while
        // still detached, then inserted in one go - far cheaper than
        // rewriting thousands of attached lines.
        const built = lines.map((src) => {
            const line = document.createElement("div");
            line.className = "line";
            line.textContent = src;
            return line;
        });
        this._syncBlockState(built);
        this._editor.replaceChildren(...built);
        this._dirty = false;
    }

    focus() {
        if (!this._editor) return;
        const first = this._editor.firstElementChild;
        if (first) this._placeCaretInLine(first, first.textContent.length);
        else this._editor.focus();
    }

    attributeChangedCallback(name, _, newVal) {
        if (!this._editor) return;
        if (name === "placeholder") {
            this._editor.setAttribute("data-placeholder", newVal || "");
        }
        if (name === "readonly") {
            const ro = newVal !== null;
            this._editor.setAttribute("contenteditable", ro ? "false" : "plaintext-only");
            this.classList.toggle("is-readonly", ro);
            if (ro) {
                // Fully re-render so no line is left in "active" flat mode.
                this._renderAllInactive();
                this._activeLine = null;
            }
            this._syncRevealHints();
        }
    }

    // -----------------------------------------------------------------------
    // Shadow DOM + stylesheet
    // -----------------------------------------------------------------------

    _render() {
        this.shadowRoot.innerHTML = TEMPLATE;
        // Host CSS for registered blocks (registerBlock({ css })), after the
        // template's own style so it can override it.
        this._blockStyle = document.createElement("style");
        this._blockStyle.textContent = blockCss;
        this.shadowRoot.appendChild(this._blockStyle);
        this._blockGen = blockGen;

        this._editor     = this.shadowRoot.querySelector(".editor");
        this._toolbar    = this.shadowRoot.querySelector(".toolbar");
        this._placeholder = this.getAttribute("placeholder") || "";
        if (this._placeholder) this._editor.setAttribute("data-placeholder", this._placeholder);

        if (this.hasAttribute("readonly")) {
            this._editor.setAttribute("contenteditable", "false");
            this.classList.add("is-readonly");
        } else {
            this._editor.setAttribute("contenteditable", "plaintext-only");
        }

        // Seed with a single empty line so the caret has somewhere to go.
        this._editor.replaceChildren(this._buildRenderedLine("", freshState()));
        this._resetHistory();

        // Host-level input listener: catches both the native input events
        // that bubble up from the internal contenteditable AND the synthetic
        // input events we dispatch from structural edits (toolbar, paste,
        // splitAt, etc.). One listener covers both paths.
        this.addEventListener("input", (e) => {
            // Our own sac:input is a CustomEvent named differently, so this
            // only sees the native / synthetic "input" events.
            this._syncBlockState();
            this._scheduleHistorySnapshot();
            this._queueSacInput();
            this._syncTableButton();
        });

        this._editor.addEventListener("input",       (e) => this._handleInput(e));
        this._editor.addEventListener("beforeinput", (e) => this._handleBeforeInput(e));
        this._editor.addEventListener("keydown",     (e) => this._handleKeyDown(e));
        this._editor.addEventListener("paste",       (e) => this._handlePaste(e));
        this._editor.addEventListener("cut",         (e) => this._handleCut(e));
        this._editor.addEventListener("copy",        (e) => this._handleCopy(e));
        this._editor.addEventListener("focusout",    (e) => this._handleFocusOut(e));

        // Reveal-toggle click (eye icon on a block's opening line). mousedown.prevent
        // keeps the caret in the surrounding line rather than leaping into
        // the non-editable span. Use closest() because the real click target
        // is the inner <svg> (or a path/circle/line) — classList on e.target
        // would miss the wrapping .sac-reveal-toggle span.
        this._editor.addEventListener("mousedown", (e) => {
            if (e.target.closest?.(".sac-reveal-toggle")) e.preventDefault();
        });
        this._editor.addEventListener("click", (e) => {
            const toggle = e.target.closest?.(".sac-reveal-toggle");
            if (toggle) {
                const line = this._lineContaining(toggle);
                if (line) this._toggleBlockReveal(line);
                return;
            }
            // Read-only has no caret, so no active line shows a masked body
            // sharp - the eye would be the only way in. A click (or tap) on
            // any line of a masked block toggles it instead. Not when the
            // click ends a text selection (copying) or lands on a link.
            if (!this.hasAttribute("readonly")) return;
            if (e.target.closest?.("a")) return;
            const sel = this._currentSelection();
            if (sel && sel.rangeCount && !sel.getRangeAt(0).collapsed) return;
            const line = this._lineContaining(e.target);
            if (!line || !line.classList.contains("block-masked")) return;
            const open = this._blockOpenLine(line);
            if (open) this._toggleBlockReveal(open);
        });

        // Toolbar: mousedown.preventDefault keeps caret focus in the editor so
        // format actions can read the current selection. The click still fires.
        this._toolbar.addEventListener("mousedown", (e) => {
            if (e.target.closest("button[data-fmt]")) e.preventDefault();
        });
        this._toolbar.addEventListener("click", (e) => {
            const btn = e.target.closest("button[data-fmt]");
            if (btn) this._applyFormat(btn.dataset.fmt);
        });
    }

    // -----------------------------------------------------------------------
    // Line lifecycle
    // -----------------------------------------------------------------------

    /** Build a line div pre-rendered (inactive). Used by value-setter. */
    _buildRenderedLine(src, state) {
        const line = document.createElement("div");
        line.className = "line";
        this._renderLine(line, src, state);
        return line;
    }

    /** Render a line's inner DOM from its source. Adds marker spans and
     *  inline wrappers while preserving textContent exactly. `state` carries
     *  the cross-line trackers: inFence (``` / ~~~ group - the opening run) and inBlock (registered
     *  group) — each must advance on every line even when rendering wouldn't
     *  otherwise change the output. */
    _renderLine(line, src = null, state = null, ahead) {
        if (src === null) src = line.textContent;

        // No state passed = unknown context: render as a top-of-document
        // line and leave the key unset, so the next sweep redoes it.
        const known = !!state;
        if (!state) state = freshState();
        if (ahead === undefined) ahead = known ? this._aheadFor(line, state, src) : NO_AHEAD;
        const block = classifyLine(src, state, ahead);
        // Remember what this render assumed, for _syncBlockState.
        line._blockState = known ? this._lineKey(state, ahead, src) : undefined;
        advanceState(state, src, classifyLine(src, state).type);

        line.classList.remove("active");
        this._applyBlockClass(line, src, block);
        const defs = this._linkDefs;

        const blockType = line.dataset.block || "";
        if (blockType === "fence-body") {
            // Inside code fence - no inline parsing, monospace whole line.
            line.innerHTML = `<span class="code-body">${escapeHtml(src)}</span>`;
            return;
        }
        if (blockType === "fence") {
            line.innerHTML = `<span class="block-marker">${escapeHtml(src)}</span>`;
            return;
        }
        if (blockType === "hr") {
            line.innerHTML = `<span class="block-marker">${escapeHtml(src)}</span>`;
            return;
        }
        if (blockType === "heading" || blockType === "ul" || blockType === "ol" || blockType === "quote") {
            const m = src.match(BLOCK_PREFIX_RE[blockType]);
            if (m) {
                const prefix = m[1];
                const rest   = src.substring(prefix.length);
                line.innerHTML = `<span class="block-marker">${escapeHtml(prefix)}</span>${renderInline(rest, defs)}`;
                return;
            }
        }
        if (blockType === "task") {
            const m = src.match(TASK_RE);
            if (m) {
                const checked = m[2] === "x" || m[2] === "X";
                const boxClass = checked ? "task-box checked" : "task-box";
                line.innerHTML =
                    `<span class="block-marker">${escapeHtml(m[1])}</span>` +
                    `<span class="${boxClass}">[${m[2]}]</span>` +
                    `<span class="block-marker">${escapeHtml(m[3])}</span>` +
                    renderInline(m[4], defs);
                return;
            }
        }
        if (blockType === "block-open" || blockType === "block-line") {
            // Reveal toggle is a contenteditable=false span carrying an
            // inline SVG eye. SVG shape children (path/circle/line) have no
            // text nodes, so line.textContent stays equal to the source —
            // critical for `value` round-trip, _flattenLine, etc. Slash
            // line is always rendered but hidden by CSS unless the block
            // has .block-revealed. All SVG attributes sit on one line so
            // no whitespace text nodes creep in. The editor-level click
            // listener below wires the interaction.
            line.innerHTML =
                `<span class="block-marker">${escapeHtml(src)}</span>` +
                (block.def.toggle
                    ? `<span class="sac-reveal-toggle" contenteditable="false" ` +
                      `      role="button" tabindex="-1" ` +
                      `      aria-label="${escapeAttr(this._revealLabel())}" ` +
                      `      title="${escapeAttr(this._revealLabel())}">` +
                      REVEAL_EYE_SVG +
                      `</span>`
                    : "");
            return;
        }
        if (blockType === "block-close") {
            line.innerHTML = `<span class="block-marker">${escapeHtml(src)}</span>`;
            return;
        }
        if (blockType === "block-body") {
            // Content wrapped in .block-body-text so a mask blurs just the
            // text - blurring the whole line would smear the card's left
            // border too. Source is preserved (line.textContent equals src).
            const inner = src === "" ? "" : renderInline(src, defs);
            line.innerHTML = `<span class="block-body-text">${inner}</span>`;
            return;
        }
        if (TABLE_TYPES.has(blockType)) {
            line.innerHTML = renderTableRow(src, blockType, block.aligns, defs);
            return;
        }
        if (blockType === "indent-code") {
            // The 4-space / tab indent is syntax, not code: a marker, hidden
            // on inactive lines, so the card shows the code flush.
            const indent = INDENT_CODE_RE.exec(src)[1];
            line.innerHTML = `<span class="block-marker">${escapeHtml(indent)}</span>` +
                `<span class="code-body">${escapeHtml(src.substring(indent.length))}</span>`;
            return;
        }
        if (blockType === "setext" || blockType === "linkdef") {
            line.innerHTML = `<span class="block-marker">${escapeHtml(src)}</span>`;
            return;
        }
        // Plain paragraph (setext heading text included) or empty line.
        if (src === "") {
            line.innerHTML = "";
            return;
        }
        line.innerHTML = renderInline(src, defs);
    }

    /** Flatten a line to a single text node, preserving the caret position
     *  as a character offset from the line start. Active-line representation. */
    _flattenLine(line) {
        const offset = this._caretOffsetInLine(line);
        const src = line.textContent;
        // replaceChildren with a text node is the one move that guarantees
        // the line ends with exactly one text node (simplifies restoreCaret).
        line.replaceChildren(document.createTextNode(src));
        line.classList.add("active");
        this._applyBlockClassFor(line, src);
        if (offset !== null) this._placeCaretInLine(line, offset);
    }

    /** Thin wrapper around _applyBlockClass that supplies the real
     *  cross-line fence state. Every mutating codepath used to hard-code
     *  inFence: false, which quietly stripped the .fence-body class off
     *  any line inside a code block as soon as it became active - losing
     *  monospace + the code-card background. Route through this helper
     *  to keep the state honest. */
    _applyBlockClassFor(line, src = null) {
        if (src === null) src = line.textContent;
        const state = this._stateBefore(line);
        const ahead = this._aheadFor(line, state, src);
        line._blockState = this._lineKey(state, ahead, src);
        this._applyBlockClass(line, src, classifyLine(src, state, ahead));
    }

    /** The sweep key for a line: its state, its setext level and - for a
     *  line that could hold a reference link - the link definitions. */
    _lineKey(state, ahead, src) {
        return stateKey(state, ahead) + (src.includes("]") ? "|" + (this._linkDefsKey || "") : "");
    }

    /** Lookahead for ONE line (the single-line edit paths; the sweep
     *  computes it for all lines in one backward pass instead). A paragraph
     *  line is a table header when the next line is a matching delimiter
     *  row, and a setext heading when its paragraph ends in an underline. */
    _aheadFor(line, state, src) {
        if (classifyLine(src, state).type !== "paragraph") return NO_AHEAD;
        const st = advanceState({ ...state }, src, "paragraph");
        let first = true;
        for (let cur = line.nextElementSibling; cur; cur = cur.nextElementSibling) {
            const text = cur.textContent;
            const type = classifyLine(text, st).type;
            if (first && type === "table-delim") return { setext: 0, table: delimAligns(text) };
            first = false;
            if (type === "setext") return { setext: text.trim()[0] === "=" ? 1 : 2, table: "" };
            if (type !== "paragraph") return NO_AHEAD;
            advanceState(st, text, type);
        }
        return NO_AHEAD;
    }

    /** Recompute the line's block-level class without touching its inner DOM.
     *  Called on every keystroke so that as you type `# `, the font jumps to
     *  heading size live. Also stores the block type in dataset.block.
     *
     *  Preserves the .block-revealed class across re-classification so a
     *  keystroke on a revealed boundary line doesn't re-mask the block. */
    _applyBlockClass(line, src, block) {
        const active   = line.classList.contains("active");
        const revealed = line.classList.contains("block-revealed");
        const classes = ["line"];
        if (active)    classes.push("active");
        if (revealed && block.def) classes.push("block-revealed");
        if (block.classes) classes.push(block.classes);
        if (src === "") classes.push("empty");
        line.className = classes.join(" ");
        line.dataset.block = block.type;
        this._revealHint(line);
        // Registered blocks carry their name, tint and pill label on the
        // line itself (inline custom properties); anything else sheds them.
        if (block.def) {
            line.dataset.blockName = block.def.name;
            line.style.setProperty("--mdb-color", block.def.color);
            if (block.type === "block-open" || block.type === "block-line") setBlockLabel(line, block.def);
            else line.style.removeProperty("--mdb-label");
        } else if (line.dataset.blockName) {
            delete line.dataset.blockName;
            line.style.removeProperty("--mdb-color");
            line.style.removeProperty("--mdb-label");
        }
    }

    /** Rebuild classes + inner DOM for every line. Use after multi-line state
     *  may have shifted (blur, paste, programmatic value set). */
    _renderAllInactive() {
        // Every line, the active one included, renders inactive; the sweep
        // only skips this._activeLine, so hide it from the sweep. The field
        // itself is the caller's business, exactly as before.
        const active = this._activeLine;
        this._activeLine = null;
        for (const line of this._editor.children) line._blockState = undefined;
        this._syncBlockState();
        this._activeLine = active;
    }

    // -----------------------------------------------------------------------
    // Events
    // -----------------------------------------------------------------------

    _handleSelectionChange() {
        if (this._suppressSelect) return;
        if (!this._editor) return;
        const sel = this._currentSelection();
        if (!sel || !sel.rangeCount) return;
        const range = sel.getRangeAt(0);

        const anchor = range.startContainer;
        // Ignore if selection isn't in this editor.
        if (!this._editor.contains(anchor) && anchor !== this._editor) return;

        // Floating caret: user clicked in the editor's padding/between lines.
        // Pull them into the nearest line so our line-based model stays sane.
        // Only do this for collapsed carets - a non-collapsed selection that
        // happens to anchor on the editor root is still a valid selection.
        if (anchor === this._editor && range.collapsed) {
            const kids = this._editor.children;
            if (kids.length === 0) return;
            const idx = Math.min(range.startOffset, kids.length - 1);
            const target = kids[idx];
            this._placeCaretInLine(target, target.textContent.length);
            return; // a fresh selectionchange will land us back here.
        }

        // Always keep the multi-line band in sync so users see what's selected.
        this._syncLineSelection();

        // Non-collapsed selection (Ctrl+A, drag, shift+click, shift+arrows):
        // do NOT flatten lines or touch the active line. Flattening calls
        // replaceChildren on the line, which destroys the DOM nodes the
        // browser's range object references and silently clobbers the
        // selection. Just track the band and bail out.
        if (!range.collapsed) return;

        const newActive = this._lineContaining(anchor);

        // Defensive sweep: if any line besides the incoming active carries
        // the .active class, render it back to inactive. Catches the cases
        // where a structural edit earlier forgot to flip a line properly.
        const stragglers = this._editor.querySelectorAll(".line.active");
        for (const line of stragglers) {
            if (line === newActive) continue;
            this._renderLine(line, null, this._stateBefore(line));
        }

        if (newActive === this._activeLine) return;
        const left = this._activeLine;
        this._activeLine = newActive;
        // Leaving a table (not just moving between its rows) tidies it:
        // the pipes line up again.
        if (left && left.isConnected && TABLE_TYPES.has(left.dataset.block) &&
            !(newActive && this._tableBlock(left).includes(newActive))) {
            this._formatTableAt(left);
        }
        if (newActive) this._flattenLine(newActive);
        this._syncTableButton();
    }

    /** Intercept destructive edits that cross a multi-line selection.
     *  plaintext-only contenteditable's default behavior for deleting
     *  across line divs is unpredictable - it can leave orphan text
     *  nodes, collapse adjacent divs, or drop structure entirely. We own
     *  the line model, so we own the delete. Collapsed-selection edits
     *  (the normal typing case) fall through to the browser unchanged. */
    _handleBeforeInput(e) {
        if (this.hasAttribute("readonly")) return;
        const sel = this._currentSelection();
        if (!sel || !sel.rangeCount) return;
        const range = sel.getRangeAt(0);
        if (range.collapsed) return;
        if (!this._editor.contains(range.startContainer) &&
            range.startContainer !== this._editor) return;

        const t = e.inputType || "";
        // insertFromPaste is intentionally not handled here - the dedicated
        // paste listener owns that flow, and handling both would double-insert.
        if (t === "insertText" ||
            t === "insertCompositionText" || t === "insertReplacementText") {
            e.preventDefault();
            this._deleteSelection();
            if (e.data) this._insertTextAtCaret(e.data);
            return;
        }
        if (t === "insertParagraph" || t === "insertLineBreak") {
            e.preventDefault();
            this._deleteSelection();
            this._handleEnter();
            return;
        }
        if (t.startsWith("delete")) {
            e.preventDefault();
            this._deleteSelection();
            return;
        }
    }

    _handleCut(e) {
        if (this.hasAttribute("readonly")) return;
        const text = this._selectedText();
        if (!text) return;
        e.preventDefault();
        if (e.clipboardData) e.clipboardData.setData("text/plain", text);
        this._deleteSelection();
    }

    _handleCopy(e) {
        // Native copy inside a shadow DOM sometimes emits an empty string
        // (browser serializes the DOM and loses lines). Serialize from our
        // own line model instead so users get clean round-trip markdown.
        const text = this._selectedText();
        if (!text) return;
        e.preventDefault();
        if (e.clipboardData) e.clipboardData.setData("text/plain", text);
    }

    /** Reconstruct the currently-selected text from the line model.
     *  Handles selections that span multiple line divs - sel.toString()
     *  is unreliable inside shadow DOM and across divs. */
    _selectedText() {
        const sel = this._currentSelection();
        if (!sel || !sel.rangeCount) return "";
        const range = sel.getRangeAt(0);
        if (range.collapsed) return "";

        const startLine = this._lineContaining(range.startContainer);
        const endLine   = this._lineContaining(range.endContainer);
        if (!startLine || !endLine) return "";

        let first = startLine, last = endLine;
        let startOff = this._charOffset(startLine, range.startContainer, range.startOffset);
        let endOff   = this._charOffset(endLine,   range.endContainer,   range.endOffset);
        if (first !== last) {
            const pos = first.compareDocumentPosition(last);
            if (pos & Node.DOCUMENT_POSITION_PRECEDING) {
                [first, last] = [last, first];
                [startOff, endOff] = [endOff, startOff];
            }
        } else if (startOff > endOff) {
            [startOff, endOff] = [endOff, startOff];
        }

        if (first === last) {
            return first.textContent.substring(startOff, endOff);
        }
        const parts = [first.textContent.substring(startOff)];
        for (let cur = first.nextElementSibling; cur && cur !== last; cur = cur.nextElementSibling) {
            parts.push(cur.textContent);
        }
        parts.push(last.textContent.substring(0, endOff));
        return parts.join("\n");
    }

    /** Delete whatever's currently selected, even if the selection spans
     *  multiple line divs. Leaves the caret collapsed at the deletion
     *  point on the (now merged) first line. Returns true if anything
     *  was removed. Used by paste, cut, type-over-selection, and delete-
     *  over-selection. Keeping it in one helper means there's exactly
     *  one place that owns the line-merge logic for cross-line deletes. */
    _deleteSelection() {
        const sel = this._currentSelection();
        if (!sel || !sel.rangeCount) return false;
        const range = sel.getRangeAt(0);
        if (range.collapsed) return false;

        const startLine = this._lineContaining(range.startContainer);
        const endLine   = this._lineContaining(range.endContainer);
        if (!startLine || !endLine) return false;

        let first = startLine, last = endLine;
        let startOff = this._charOffset(startLine, range.startContainer, range.startOffset);
        let endOff   = this._charOffset(endLine,   range.endContainer,   range.endOffset);
        if (first !== last) {
            const pos = first.compareDocumentPosition(last);
            if (pos & Node.DOCUMENT_POSITION_PRECEDING) {
                [first, last] = [last, first];
                [startOff, endOff] = [endOff, startOff];
            }
        } else if (startOff > endOff) {
            [startOff, endOff] = [endOff, startOff];
        }

        const prefix = first.textContent.substring(0, startOff);
        const suffix = last.textContent.substring(endOff);
        const merged = prefix + suffix;

        // Strip intermediate lines and the last line (if different).
        if (first !== last) {
            let cur = first.nextElementSibling;
            while (cur && cur !== last) {
                const next = cur.nextElementSibling;
                cur.remove();
                cur = next;
            }
            last.remove();
        }

        // Clear any leftover selection-band classes.
        for (const line of this._editor.querySelectorAll(".line.sel")) {
            line.classList.remove("sel");
        }

        // Rebuild the surviving line as the new active line.
        first.replaceChildren(document.createTextNode(merged));
        first.classList.add("active");
        this._activeLine = first;
        this._applyBlockClassFor(first, merged);
        this._placeCaretInLine(first, startOff);

        this._dirty = true;
        this.dispatchEvent(new Event("input", { bubbles: true }));
        return true;
    }

    /** Tag every line the selection currently touches with `.sel` so the
     *  CSS band-highlight tracks a multi-line selection. Single-line,
     *  in-line selections get the class too - keeps the rule simple
     *  (the whole line brightens; the in-line ::selection sits on top).
     *  Called from selectionchange; cheap enough per-event because
     *  toggles are conditional on the class state actually changing. */
    _syncLineSelection() {
        const lines = this._getSelectedLines();
        const selected = new Set(lines);
        for (const line of this._editor.children) {
            const should = selected.has(line);
            if (line.classList.contains("sel") !== should) {
                line.classList.toggle("sel", should);
            }
        }
    }

    _handleInput(e) {
        this._dirty = true;

        // Some browsers insert a stray <br> when the last line is empty.
        // Normalise by stripping trailing <br>s from the active line.
        if (this._activeLine) {
            const strayBrs = this._activeLine.querySelectorAll("br");
            strayBrs.forEach(br => br.remove());
            this._applyBlockClassFor(this._activeLine);
        }

        // Guard against the editor ever being emptied out completely - keep
        // at least one line so there's always a caret target.
        if (this._editor.children.length === 0) {
            const line = this._buildRenderedLine("", freshState());
            this._editor.appendChild(line);
            this._placeCaretInLine(line, 0);
        }

        this.dispatchEvent(new Event("input", { bubbles: true }));
    }

    _handleKeyDown(e) {
        if (this.hasAttribute("readonly")) return;

        // Undo/redo. Check these before formatting shortcuts so Ctrl+Z in
        // any state (including inside a contenteditable that has its own
        // native undo stack) always goes through our history model - the
        // native stack doesn't know about our per-line divs and would
        // restore broken state.
        if ((e.ctrlKey || e.metaKey) && !e.altKey) {
            const k = e.key.toLowerCase();
            if (k === "z" && !e.shiftKey) {
                e.preventDefault();
                this.undo();
                return;
            }
            if ((k === "y" && !e.shiftKey) || (k === "z" && e.shiftKey)) {
                e.preventDefault();
                this.redo();
                return;
            }
        }

        // Formatting shortcuts.
        if ((e.ctrlKey || e.metaKey) && !e.altKey && !e.shiftKey) {
            const kbd = { b: "bold", i: "italic", k: "link" }[e.key.toLowerCase()];
            if (kbd) {
                e.preventDefault();
                this._applyFormat(kbd);
                return;
            }
        }

        // Esc in a table: out of it. Stopped here so no host binding for
        // Escape (a panel, a dialog) fires as well.
        if (e.key === "Escape" && this._inTable()) {
            e.preventDefault();
            e.stopPropagation();
            this._tableEscape();
            return;
        }

        if (e.key === "Enter" && !e.shiftKey && !(e.ctrlKey || e.metaKey)) {
            e.preventDefault();
            // In a table Enter adds a row - it must not delete a selected
            // cell (Tab selects the cell it moves to).
            if (this._inTable()) { this._tableEnter(); return; }
            // Enter over a selection: delete the selection first (could span
            // multiple lines), then split at the now-collapsed caret. The
            // beforeinput listener is skipped for Enter because we
            // preventDefault on keydown - so we own the delete here.
            this._deleteSelection();
            this._handleEnter();
            return;
        }
        if (e.key === "Backspace" && !e.shiftKey && !(e.ctrlKey || e.metaKey)) {
            if (this._tryMergeBackspace()) e.preventDefault();
            return;
        }
        if (e.key === "Tab") {
            e.preventDefault();
            if (this._inTable()) { this._tableTab(e.shiftKey); return; }
            // Tab with a selection: replace the selection with a tab stop
            // instead of expanding it.
            this._deleteSelection();
            this._insertAtCaret(e.shiftKey ? "" : "  ");
            return;
        }
    }

    _handlePaste(e) {
        // plaintext-only contenteditable already strips HTML, but we also want
        // multi-line pastes to become multiple line divs rather than one big
        // line with embedded newlines. Intercept and insert manually - and
        // delete any current multi-line selection first (the native paste
        // path would leave the selected lines behind while adding the paste).
        e.preventDefault();
        const text = e.clipboardData?.getData("text/plain") ?? "";
        if (!text) return;
        this._deleteSelection();
        this._insertTextAtCaret(text);
    }

    _handleFocusOut(e) {
        // relatedTarget is null when focus leaves the whole page OR the shadow
        // boundary. If it's still inside our component, ignore.
        if (e.relatedTarget && this.contains(e.relatedTarget)) return;
        // Nor is focus moving into our own table dialog (shadow DOM, so
        // this.contains() cannot see it) - or anything while it is open.
        if (this._tdlg && (this._tdlg.open || this._tdlg.contains(e.relatedTarget))) return;
        // Full re-render on blur. We used to just re-render active lines, but
        // that left cross-line state drift — e.g. typing :::secret on a line
        // doesn't mask the lines below until every subsequent line is re-
        // classified with the new block state. Doing it on blur is cheap
        // and guarantees the "resting" view is always correct.
        if (this._inTable()) this._formatTableAt(this._activeLine);
        this._renderAllInactive();
        // Drop the multi-line selection band too. The `:focus-within` CSS
        // already hides it, but clearing the class means the DOM is clean
        // if anything inspects it.
        for (const line of this._editor.querySelectorAll(".line.sel")) {
            line.classList.remove("sel");
        }
        this._activeLine = null;
        if (this._dirty) {
            this._dirty = false;
            this.dispatchEvent(new Event("change", { bubbles: true }));
            this._fireSac("sac:change");
        }
    }

    // -----------------------------------------------------------------------
    // Structural edits (Enter, Backspace, paste)
    // -----------------------------------------------------------------------

    _handleEnter() {
        const line = this._activeLine;
        if (!line) return;
        const offset = this._caretOffsetInLine(line) ?? line.textContent.length;
        const src = line.textContent;
        const before = src.substring(0, offset);
        const after  = src.substring(offset);

        // List continuation: detect a `- `/`* `/`+ `/`1. `/`1) ` prefix on
        // the current line and continue it on the new line with the same
        // marker (ordered: next number, same delimiter), unless the current
        // prefix is all we had (empty item) - in which case exit the list.
        // Inside a code fence `- ` and `> ` are code, not structure: a plain
        // split, no continuation.
        if (line.dataset.block === "fence-body" || line.dataset.block === "indent-code") {
            this._splitAt(line, before, after, 0);
            return;
        }

        const listMatch = before.match(/^(\s*)([-*+]|(\d{1,9})([.)]))\s+(.*)$/);
        if (listMatch) {
            const [, indent, marker, numStr, delim, content] = listMatch;
            if (!content && !after.trim()) {
                // Empty item + nothing after = exit the list.
                line.replaceChildren(document.createTextNode(""));
                this._applyBlockClassFor(line, "");
                this._placeCaretInLine(line, 0);
                this._dirty = true;
                this.dispatchEvent(new Event("input", { bubbles: true }));
                return;
            }
            const nextMarker = numStr ? `${parseInt(numStr, 10) + 1}${delim}` : marker;
            const newSrc = `${indent}${nextMarker} `;
            this._splitAt(line, before, newSrc + after, newSrc.length);
            return;
        }

        // Blockquote continuation, with the same empty-line exit rule as lists.
        const quoteMatch = before.match(/^>\s(.*)$/);
        if (quoteMatch) {
            if (!quoteMatch[1] && !after.trim()) {
                line.replaceChildren(document.createTextNode(""));
                this._applyBlockClassFor(line, "");
                this._placeCaretInLine(line, 0);
                this._dirty = true;
                this.dispatchEvent(new Event("input", { bubbles: true }));
                return;
            }
            this._splitAt(line, before, "> " + after, 2);
            return;
        }

        this._splitAt(line, before, after, 0);
    }

    /** Shared two-line split. Leaves `before` on the old line and creates a
     *  new line with `newContent`; caret lands at `caretOffset` within the
     *  new line. The old line is rendered inactive here (not just "has its
     *  content truncated") so its .active class doesn't stick around - the
     *  most common source of the "focus indicator stays after I left the
     *  line" bug. */
    _splitAt(line, before, newContent, caretOffset) {
        // Render old line inactive with the part before the caret.
        this._renderLine(line, before, this._stateBefore(line));

        const newLine = document.createElement("div");
        newLine.className = "line active";
        newLine.textContent = newContent;
        line.after(newLine);
        this._applyBlockClassFor(newLine, newContent);

        this._activeLine = newLine;
        this._placeCaretInLine(newLine, caretOffset);
        this._dirty = true;
        this.dispatchEvent(new Event("input", { bubbles: true }));
    }

    _tryMergeBackspace() {
        const line = this._activeLine;
        if (!line) return false;
        // Only fire for a collapsed caret at column 0. A non-collapsed
        // selection starting at column 0 would otherwise false-positive
        // this path and merge a line while the selection was still meant
        // to be deleted whole - that's the beforeinput handler's job.
        const sel = this._currentSelection();
        if (!sel || !sel.rangeCount || !sel.getRangeAt(0).collapsed) return false;
        const offset = this._caretOffsetInLine(line);
        if (offset !== 0) return false;
        const prev = line.previousElementSibling;
        if (!prev) return false;
        // Merge: put prev.textContent + line.textContent into prev, delete line.
        const prevLen = prev.textContent.length;
        const merged = prev.textContent + line.textContent;
        line.remove();
        prev.replaceChildren(document.createTextNode(merged));
        prev.classList.add("active");
        this._activeLine = prev;
        this._applyBlockClassFor(prev, merged);
        this._placeCaretInLine(prev, prevLen);
        this._dirty = true;
        this.dispatchEvent(new Event("input", { bubbles: true }));
        return true;
    }

    _insertAtCaret(text) {
        this._insertTextAtCaret(text);
    }

    _insertTextAtCaret(text) {
        const lines = text.replace(/\r\n/g, "\n").split("\n");
        if (!this._activeLine) {
            // No active line - drop onto the first line.
            const first = this._editor.firstElementChild;
            if (!first) return;
            this._activeLine = first;
            first.classList.add("active");
            this._placeCaretInLine(first, first.textContent.length);
        }
        const active = this._activeLine;
        const offset = this._caretOffsetInLine(active) ?? active.textContent.length;
        const src = active.textContent;
        const before = src.substring(0, offset);
        const after  = src.substring(offset);

        if (lines.length === 1) {
            const combined = before + lines[0] + after;
            active.replaceChildren(document.createTextNode(combined));
            this._applyBlockClassFor(active, combined);
            this._placeCaretInLine(active, before.length + lines[0].length);
        } else {
            // Multi-line paste: first line joins `before`, last line joins
            // `after`, middle lines become their own line divs. The old
            // active line is rendered inactive as part of the process -
            // leaving it active would leave a stale focus indicator.
            const firstText = before + lines[0];
            this._renderLine(active, firstText, this._stateBefore(active));

            let anchor = active;
            for (let i = 1; i < lines.length; i++) {
                const isLast = (i === lines.length - 1);
                const content = isLast ? lines[i] + after : lines[i];
                const newLine = document.createElement("div");
                newLine.className = isLast ? "line active" : "line";
                newLine.textContent = content;
                anchor.after(newLine);
                this._applyBlockClassFor(newLine, content);
                if (!isLast) {
                    // Render intermediate lines with markers, same as any other
                    // inactive line.
                    this._renderLine(newLine, content, this._stateBefore(newLine));
                }
                anchor = newLine;
            }

            this._activeLine = anchor;
            const lastLen = lines[lines.length - 1].length;
            this._placeCaretInLine(anchor, lastLen);
        }
        this._dirty = true;
        this.dispatchEvent(new Event("input", { bubbles: true }));
    }

    // -----------------------------------------------------------------------
    // Formatting toolbar
    // -----------------------------------------------------------------------

    _applyFormat(kind) {
        if (this.hasAttribute("readonly")) return;
        this._ensureActiveLine();
        if (!this._activeLine) return;

        // Inline wrap / per-line prefix actions only touch the active line.
        // List / quote / code span the whole selection when there is one.
        const line = this._activeLine;
        const sel  = this._getLineSelection(line);

        switch (kind) {
            case "bold":   return this._wrapInLine(line, sel, "**", "**", "bold text");
            case "italic": return this._wrapInLine(line, sel, "*",  "*",  "italic");
            case "strike": return this._wrapInLine(line, sel, "~~", "~~", "struck");
            case "h1":     return this._togglePrefix(line, "# ");
            case "h2":     return this._togglePrefix(line, "## ");
            case "h3":     return this._togglePrefix(line, "### ");
            case "ul":     return this._togglePrefixMulti("- ");
            case "ol":     return this._togglePrefixMulti("1. ");
            case "quote":  return this._togglePrefixMulti("> ");
            case "hr":     return this._insertHr(line);
            case "table":  return this._inTable() ? this._openTableDialog(line) : this._insertTable(line);
            case "code": {
                const selectedLines = this._getSelectedLines();
                if (selectedLines.length > 1) return this._wrapAsCodeFence(selectedLines);
                return this._wrapInLine(line, sel, "`", "`", "code");
            }
            case "link": {
                const url = window.prompt(mdT("linkPrompt", "Link URL"), "https://");
                if (!url) return;
                const safe = /^(https?:|mailto:|#)/i.test(url) ? url : "https://" + url;
                return this._wrapInLine(line, sel, "[", `](${safe})`, mdT("linkText", "link text"));
            }
        }
    }

    _ensureActiveLine() {
        if (this._activeLine) return;
        const first = this._editor.firstElementChild;
        if (!first) return;
        this._flattenLine(first);
        this._activeLine = first;
        this._placeCaretInLine(first, 0);
    }

    /** Return every line div touched by the current selection, in document
     *  order. Returns [] for a collapsed selection so callers can fall back
     *  to the active line on their own terms. */
    _getSelectedLines() {
        const sel = this._currentSelection();
        if (!sel || !sel.rangeCount) return [];
        const range = sel.getRangeAt(0);
        if (range.collapsed) return [];

        const startLine = this._lineContaining(range.startContainer);
        const endLine   = this._lineContaining(range.endContainer);
        if (!startLine || !endLine) return [];

        // Normalise direction - user may have selected bottom-to-top.
        let first = startLine, last = endLine;
        if (first !== last) {
            const pos = first.compareDocumentPosition(last);
            if (pos & Node.DOCUMENT_POSITION_PRECEDING) {
                [first, last] = [last, first];
            }
        }

        const out = [];
        for (let cur = first; cur; cur = cur.nextElementSibling) {
            out.push(cur);
            if (cur === last) break;
        }
        return out;
    }

    /** Toggle a line-prefix across every selected line (or the active line
     *  if the selection is collapsed). If every targeted line already has
     *  the prefix, strip it; otherwise add it to whichever don't. Ordered
     *  lists get auto-numbering so "1." becomes 1., 2., 3., .... */
    _togglePrefixMulti(prefix) {
        const selectedLines = this._getSelectedLines();
        const target = selectedLines.length > 0
            ? selectedLines
            : (this._activeLine ? [this._activeLine] : []);
        if (target.length === 0) return;

        const isOrdered = /^\d+\.\s$/.test(prefix);
        // Any existing number counts as "already ordered", `1)` included.
        const prefixRe  = isOrdered ? /^\d{1,9}[.)]\s/ : null;
        const hasPrefix = (text) => prefixRe ? prefixRe.test(text) : text.startsWith(prefix);
        const stripPrefix = (text) => prefixRe
            ? text.replace(prefixRe, "")
            : (text.startsWith(prefix) ? text.substring(prefix.length) : text);

        const allPrefixed = target.every(l => hasPrefix(l.textContent));

        let counter = 1;
        for (const line of target) {
            const text = line.textContent;
            let next;
            if (allPrefixed) {
                next = stripPrefix(text);
            } else if (isOrdered) {
                const base = stripPrefix(text);
                next = `${counter}. ${base}`;
                counter++;
            } else {
                next = hasPrefix(text) ? text : prefix + text;
            }

            if (line === this._activeLine) {
                line.replaceChildren(document.createTextNode(next));
                this._applyBlockClassFor(line, next);
            } else {
                this._renderLine(line, next, this._stateBefore(line));
            }
        }

        // Move the active line to the last modified row and drop the caret
        // at its end - gives a predictable resting point after the sweep.
        const newActive = target[target.length - 1];
        if (this._activeLine && this._activeLine !== newActive && this._activeLine.isConnected) {
            this._renderLine(this._activeLine, null, this._stateBefore(this._activeLine));
        }
        this._activeLine = newActive;
        this._flattenLine(newActive);
        this._placeCaretInLine(newActive, newActive.textContent.length);

        this._dirty = true;
        this.dispatchEvent(new Event("input", { bubbles: true }));
    }

    /** Wrap a run of selected lines in a fenced code block by inserting
     *  fence markers before the first and after the last. The fence is ```
     *  unless a selected line starts with a backtick run itself - then one
     *  backtick longer, so the inner run cannot close it. The enclosed lines
     *  re-render as fence-body (monospace + background). */
    _wrapAsCodeFence(lines) {
        if (lines.length === 0) return;
        const first = lines[0];
        const last  = lines[lines.length - 1];
        const inner = Math.max(0, ...lines.map(l => (/^`*/.exec(l.textContent) || [""])[0].length));
        const marker = "`".repeat(Math.max(3, inner + 1));

        const open = document.createElement("div");
        open.className = "line";
        open.textContent = marker;
        first.before(open);
        this._renderLine(open, marker, this._stateBefore(open));

        const close = document.createElement("div");
        close.className = "line";
        close.textContent = marker;
        last.after(close);
        this._renderLine(close, marker, this._stateBefore(close));

        // Re-render the enclosed inactive lines so they pick up fence-body.
        // The active line stays flat but gets its class updated.
        for (const line of lines) {
            if (line === this._activeLine) {
                this._applyBlockClassFor(line);
            } else {
                this._renderLine(line, null, this._stateBefore(line));
            }
        }

        this._dirty = true;
        this.dispatchEvent(new Event("input", { bubbles: true }));
    }

    _wrapInLine(line, sel, left, right, placeholder) {
        const text = line.textContent;
        const pick = text.substring(sel.start, sel.end);
        const body = pick || placeholder;
        const next = text.substring(0, sel.start) + left + body + right + text.substring(sel.end);
        line.replaceChildren(document.createTextNode(next));
        this._applyBlockClassFor(line, next);
        // Select the body so the user can type over a placeholder immediately.
        const startCaret = sel.start + left.length;
        const endCaret   = startCaret + body.length;
        this._selectInLine(line, startCaret, endCaret);
        this._dirty = true;
        this.dispatchEvent(new Event("input", { bubbles: true }));
    }

    _togglePrefix(line, prefix) {
        const text = line.textContent;
        const has = text.startsWith(prefix);
        const caret = this._caretOffsetInLine(line) ?? text.length;
        const next = has ? text.substring(prefix.length) : prefix + text;
        line.replaceChildren(document.createTextNode(next));
        this._applyBlockClassFor(line, next);
        const delta = has ? -prefix.length : prefix.length;
        this._placeCaretInLine(line, Math.max(0, caret + delta));
        this._dirty = true;
        this.dispatchEvent(new Event("input", { bubbles: true }));
    }

    // -----------------------------------------------------------------------
    // Tables
    // -----------------------------------------------------------------------

    _inTable() {
        return !!(this._activeLine && TABLE_TYPES.has(this._activeLine.dataset.block));
    }

    /** The table block around a table line: header, delimiter, rows. */
    _tableBlock(line) {
        let first = line;
        while (first.previousElementSibling && TABLE_TYPES.has(first.previousElementSibling.dataset.block) &&
               first.dataset.block !== "table-head") {
            first = first.previousElementSibling;
        }
        const rows = [first];
        for (let cur = first.nextElementSibling; cur && (cur.dataset.block === "table-delim" ||
             cur.dataset.block === "table-row"); cur = cur.nextElementSibling) rows.push(cur);
        return rows;
    }

    /** Make `target` the active (raw) line and put the caret, or a
     *  selection, in it. The old active line renders back first. */
    _activateLine(target, start, end = start) {
        const old = this._activeLine;
        if (old && old !== target && old.isConnected) this._renderLine(old, null, this._stateBefore(old));
        target.replaceChildren(document.createTextNode(target.textContent));
        target.classList.add("active");
        this._activeLine = target;
        this._applyBlockClassFor(target);
        if (end === start) this._placeCaretInLine(target, start);
        else this._selectInLine(target, start, end);
    }

    /** Line up the pipes of the table around `line`. Rewrites only lines
     *  whose text changes; an edit like any other (input event, undoable). */
    _formatTableAt(line) {
        const rows = this._tableBlock(line);
        if (rows.length < 2 || rows[0].dataset.block !== "table-head") return;
        const texts = rows.map((l) => l.textContent);
        const next  = formatTable(texts);
        let changed = false;
        rows.forEach((l, i) => {
            if (next[i] === texts[i]) return;
            changed = true;
            if (l === this._activeLine) l.replaceChildren(document.createTextNode(next[i]));
            else l.textContent = next[i];
            l._blockState = undefined;
        });
        if (!changed) return;
        this._dirty = true;
        this.dispatchEvent(new Event("input", { bubbles: true }));
    }

    /** Tab / Shift+Tab: next / previous cell, selecting its content; past
     *  the last cell of the last row a new row is added. The delimiter row
     *  is skipped. */
    _tableTab(back) {
        const line  = this._activeLine;
        const rows  = this._tableBlock(line).filter((l) => l.dataset.block !== "table-delim");
        const cellsOf = (text) => splitTableRow(text).filter((g) => g.kind === "cell");
        const caret = this._caretOffsetInLine(line) ?? 0;
        const cells = cellsOf(line.textContent);
        // The cell the caret is in: the last one starting at or before it
        // (its leading padding counts as inside).
        let idx = 0;
        cells.forEach((c, i) => { if (c.start - 1 <= caret) idx = i; });
        let row = rows.indexOf(line);
        if (row < 0) row = 0;            // on the delimiter row: treat as header
        let target = idx + (back ? -1 : 1);
        if (target < 0 || target >= cells.length) {
            row += back ? -1 : 1;
            if (row < 0) return;
            if (row >= rows.length) {
                this._tableAddRow(rows[rows.length - 1]);
                return;
            }
            const len = cellsOf(rows[row].textContent).length;
            target = back ? len - 1 : 0;
        }
        const dest = cellsOf(rows[row].textContent)[target];
        if (!dest) return;
        this._activateLine(rows[row], dest.start, dest.start + dest.text.length);
    }

    /** Enter in a table: a new empty row below (below the delimiter when
     *  on the header). Enter on an empty row leaves the table, like an
     *  empty list item leaves a list. */
    _tableEnter() {
        const line = this._activeLine;
        const type = line.dataset.block;
        if (this._isEmptyRow(line)) {
            this._tableExitRow(line);
            return;
        }
        const anchor = type === "table-head" && line.nextElementSibling ? line.nextElementSibling : line;
        this._tableAddRow(anchor);
    }

    _isEmptyRow(line) {
        return line.dataset.block === "table-row" && tableCells(line.textContent).every((c) => c === "");
    }

    /** An empty body row leaves the table: it becomes the plain empty line
     *  under it, caret there, and the table's pipes line up. */
    _tableExitRow(line) {
        const above = line.previousElementSibling;
        line.replaceChildren(document.createTextNode(""));
        this._applyBlockClassFor(line, "");
        this._placeCaretInLine(line, 0);
        if (above && TABLE_TYPES.has(above.dataset.block)) this._formatTableAt(above);
        this._dirty = true;
        this.dispatchEvent(new Event("input", { bubbles: true }));
    }

    /** Esc in a table. On an empty row it is Enter's exit (the row goes);
     *  anywhere else the caret moves to the line after the table - a new
     *  empty one when that line holds text or the table ends the document. */
    _tableEscape() {
        const line = this._activeLine;
        if (this._isEmptyRow(line)) {
            this._tableExitRow(line);
            return;
        }
        const rows = this._tableBlock(line);
        const last = rows[rows.length - 1];
        let after = last.nextElementSibling;
        const added = !after || after.textContent !== "";
        if (added) {
            after = document.createElement("div");
            after.className = "line";
            last.after(after);
        }
        this._activateLine(after, 0);
        this._formatTableAt(rows[0]);
        if (added) {
            this._dirty = true;
            this.dispatchEvent(new Event("input", { bubbles: true }));
        }
    }

    /** Insert an empty row after `anchor` and put the caret in its first cell. */
    _tableAddRow(anchor) {
        const n = tableCells(this._tableBlock(anchor)[0].textContent).length;
        const row = document.createElement("div");
        row.className = "line";
        row.textContent = "|" + "  |".repeat(n);
        anchor.after(row);
        this._activateLine(row, 2);
        this._dirty = true;
        this.dispatchEvent(new Event("input", { bubbles: true }));
    }

    _syncTableButton() {
        const btn = this._toolbar && this._toolbar.querySelector('button[data-fmt="table"]');
        if (btn) btn.classList.toggle("ctx", this._inTable());
    }

    // ---------------------------------------------------------------------
    // Table dialog: the grid for what is tedious in pipe source - columns
    // and rows added, removed, moved; alignment. Opened by the Table
    // button while the caret is in a table. Native <dialog> in the shadow
    // root, so it needs no kit. Apply rewrites the table block (formatted,
    // an undoable edit); Esc or Cancel changes nothing.
    // ---------------------------------------------------------------------

    _openTableDialog(line) {
        const rows = this._tableBlock(line);
        if (rows.length < 2 || rows[0].dataset.block !== "table-head") return;
        const texts = rows.map((l) => l.textContent);
        const cells = texts.map(tableCells).filter((_, i) => i !== 1);
        // Cells beyond the header's count exist in the source; the grid
        // shows them as columns rather than hide them.
        const width = Math.max(1, ...cells.map((r) => r.length));
        const aligns = delimAligns(texts[1]).padEnd(width, "-").slice(0, width).split("");
        const data = cells.map((r) => Array.from({ length: width }, (_, c) => unescapePipes(r[c] || "")));
        this._td = { rows, aligns, data, caret: line };
        this._tdEnsure();
        this._tdLabels();
        this._tdRender();
        this._tdlg.returnValue = "";
        this._tdlg.showModal();
        this._tdFocus('input[data-r="0"][data-c="0"]');
    }

    _tdEnsure() {
        if (this._tdlg) return;
        const d = document.createElement("dialog");
        d.className = "tdlg";
        d.setAttribute("part", "table-dialog");
        d.innerHTML =
            `<form method="dialog">` +
            `<div class="tdlg-title"></div>` +
            `<div class="tdlg-scroll"><table class="tdlg-grid"></table></div>` +
            `<div class="tdlg-actions">` +
            `<button type="button" class="tdlg-btn" data-td="add-row"></button>` +
            `<button type="button" class="tdlg-btn" data-td="add-col"></button>` +
            `<span class="tdlg-gap"></span>` +
            `<button type="button" class="tdlg-btn" data-td="cancel"></button>` +
            `<button type="submit" class="tdlg-btn primary" value="apply" data-td="apply"></button>` +
            `</div></form>`;
        this.shadowRoot.appendChild(d);
        this._tdlg = d;
        d.addEventListener("click", (e) => {
            const b = e.target.closest("button[data-td]");
            if (!b || b.type === "submit" || b.disabled) return;
            this._tdAction(b.dataset.td, Number(b.dataset.col), Number(b.dataset.row));
        });
        // The grid's own input events are not edits of the document: keep
        // them inside, or a host listening for input would see them.
        d.addEventListener("input", (e) => {
            e.stopPropagation();
            const i = e.target;
            if (i.dataset && i.dataset.r !== undefined) this._td.data[Number(i.dataset.r)][Number(i.dataset.c)] = i.value;
        });
        d.addEventListener("change", (e) => e.stopPropagation());
        // Esc cancels the dialog (native) and goes no further.
        d.addEventListener("keydown", (e) => { if (e.key === "Escape") e.stopPropagation(); });
        // Enter in a cell submits the form = Apply; so does the button.
        d.addEventListener("close", () => this._tdClosed());
    }

    _tdLabels() {
        const d = this._tdlg;
        d.querySelector(".tdlg-title").textContent = mdT("tdTitle", "Edit table");
        const set = (td, key, en) => { d.querySelector(`[data-td="${td}"]`).textContent = mdT(key, en); };
        set("add-row", "tdAddRow", "Add row");
        set("add-col", "tdAddCol", "Add column");
        set("cancel",  "tdCancel", "Cancel");
        set("apply",   "tdApply",  "Apply");
    }

    _tdRender() {
        const { data, aligns } = this._td;
        const w = aligns.length, last = data.length - 1;
        const alName = { "-": ["tdAl-", "none"], l: ["tdAll", "left"], c: ["tdAlc", "center"], r: ["tdAlr", "right"] };
        const btn = (td, col, row, icon, label, disabled = false) =>
            `<button type="button" class="tdlg-ic" data-td="${td}"` +
            (col === null ? "" : ` data-col="${col}"`) + (row === null ? "" : ` data-row="${row}"`) +
            ` title="${escapeAttr(label)}" aria-label="${escapeAttr(label)}"${disabled ? " disabled" : ""}>` +
            TD_ICONS[icon] + `</button>`;
        let h = `<thead><tr><th></th>`;
        aligns.forEach((a, c) => {
            const al = `${mdT("tdAlign", "Alignment")}: ${mdT(alName[a][0], alName[a][1])}`;
            h += `<th><div class="tdlg-ctl">` +
                btn("col-left",  c, null, "left",  mdT("tdLeft", "Move column left"), c === 0) +
                btn("col-align", c, null, "al" + a, al) +
                btn("col-right", c, null, "right", mdT("tdRight", "Move column right"), c === w - 1) +
                btn("col-del",   c, null, "del",   mdT("tdDelCol", "Delete column"), w === 1) +
                `</div></th>`;
        });
        h += `</tr></thead><tbody>`;
        data.forEach((row, r) => {
            h += `<tr${r === 0 ? ' class="tdlg-head"' : ""}><td><div class="tdlg-ctl">` +
                (r === 0
                    ? `<span class="tdlg-tag">${escapeHtml(mdT("tdHeader", "Header"))}</span>`
                    : btn("row-up",   null, r, "up",   mdT("tdUp", "Move row up"), r === 1) +
                      btn("row-down", null, r, "down", mdT("tdDown", "Move row down"), r === last) +
                      btn("row-del",  null, r, "del",  mdT("tdDelRow", "Delete row"))) +
                `</div></td>`;
            row.forEach((v, c) => {
                h += `<td><input type="text" class="ta-${aligns[c]}" data-r="${r}" data-c="${c}" ` +
                     `value="${escapeAttr(v)}" aria-label="${escapeAttr(`${mdT("tdCell", "Cell")} ${r + 1}/${c + 1}`)}"></td>`;
            });
            h += `</tr>`;
        });
        this._tdlg.querySelector(".tdlg-grid").innerHTML = h + `</tbody>`;
    }

    _tdFocus(selector) {
        const el = this._tdlg.querySelector(selector);
        if (el && !el.disabled) el.focus();
    }

    _tdAction(kind, col, row) {
        const td = this._td, { data, aligns } = td;
        const swap = (arr, i, j) => { [arr[i], arr[j]] = [arr[j], arr[i]]; };
        let focus = null;
        switch (kind) {
            case "cancel": this._tdlg.close("cancel"); return;
            case "add-row":
                data.push(aligns.map(() => ""));
                focus = `input[data-r="${data.length - 1}"][data-c="0"]`;
                break;
            case "add-col":
                data.forEach((r) => r.push(""));
                aligns.push("-");
                focus = `input[data-r="0"][data-c="${aligns.length - 1}"]`;
                break;
            case "col-left": case "col-right": {
                const to = col + (kind === "col-left" ? -1 : 1);
                if (to < 0 || to >= aligns.length) return;
                data.forEach((r) => swap(r, col, to));
                swap(aligns, col, to);
                focus = `[data-td="${kind}"][data-col="${to}"]`;
                break;
            }
            case "col-align":
                aligns[col] = { "-": "l", l: "c", c: "r", r: "-" }[aligns[col]];
                focus = `[data-td="col-align"][data-col="${col}"]`;
                break;
            case "col-del":
                if (aligns.length < 2) return;
                data.forEach((r) => r.splice(col, 1));
                aligns.splice(col, 1);
                focus = `[data-td="col-del"][data-col="${Math.min(col, aligns.length - 1)}"]`;
                break;
            case "row-up": case "row-down": {
                const to = row + (kind === "row-up" ? -1 : 1);
                if (to < 1 || to >= data.length) return;      // the header stays first
                swap(data, row, to);
                focus = `[data-td="${kind}"][data-row="${to}"]`;
                break;
            }
            case "row-del":
                if (row < 1) return;
                data.splice(row, 1);
                focus = data.length > 1 ? `[data-td="row-del"][data-row="${Math.min(row, data.length - 1)}"]`
                                        : `[data-td="add-row"]`;
                break;
            default: return;
        }
        this._tdRender();
        if (focus) this._tdFocus(focus);
    }

    _tdClosed() {
        const td = this._td;
        this._td = null;
        if (!td) return;
        this._editor.focus();
        if (this._tdlg.returnValue === "apply" && td.rows[0].isConnected) {
            this._tdApply(td);
            return;
        }
        // Cancelled: back where the caret was, nothing changed.
        const back = td.caret.isConnected ? td.caret : null;
        if (back) this._activateLine(back, 0);
    }

    /** Write the grid back: pipes escaped, formatted, replacing the table's
     *  lines in place (reusing line elements where it can). An edit like
     *  any other - input fires, undoable. */
    _tdApply(td) {
        const rowText = (r) => "| " + r.map((c) => escapePipes(c.trim())).join(" | ") + " |";
        const delim = "| " + td.aligns.map((a) => ({ "-": "---", l: ":--", c: ":-:", r: "--:" })[a]).join(" | ") + " |";
        const texts = formatTable([rowText(td.data[0]), delim, ...td.data.slice(1).map(rowText)]);
        const old = td.rows;
        let last = null;
        texts.forEach((t, i) => {
            let l = old[i];
            if (!l) {
                l = document.createElement("div");
                l.className = "line";
                last.after(l);
            }
            if (l.textContent !== t || !old[i]) {
                if (l === this._activeLine) l.replaceChildren(document.createTextNode(t));
                else l.textContent = t;
                l._blockState = undefined;
            }
            last = l;
        });
        for (const l of old.slice(texts.length)) l.remove();
        const head = old[0];
        const first = splitTableRow(texts[0]).find((g) => g.kind === "cell");
        this._activateLine(head, first ? first.start : 0);
        this._dirty = true;
        this.dispatchEvent(new Event("input", { bubbles: true }));
    }

    /** Toolbar: a 2-column table with one empty row, header cell selected. */
    _insertTable(line) {
        const col = mdT("tableColumn", "Column");
        const texts = formatTable([`| ${col} 1 | ${col} 2 |`, "| --- | --- |", "|  |  |"]);
        let first;
        if (line.textContent === "") {
            first = line;
            line.textContent = texts[0];
        } else {
            // A table after text needs its own block: a blank line first.
            const blank = document.createElement("div");
            blank.className = "line";
            line.after(blank);
            first = document.createElement("div");
            first.className = "line";
            first.textContent = texts[0];
            blank.after(first);
        }
        let prev = first;
        for (const t of texts.slice(1)) {
            const l = document.createElement("div");
            l.className = "line";
            l.textContent = t;
            prev.after(l);
            prev = l;
        }
        const head = splitTableRow(texts[0]).find((g) => g.kind === "cell");
        this._activateLine(first, head.start, head.start + head.text.length);
        this._dirty = true;
        this.dispatchEvent(new Event("input", { bubbles: true }));
    }

    _insertHr(line) {
        // Insert `---` as its own line after the current one. The original
        // line is rendered inactive here so its .active class doesn't leak.
        this._renderLine(line, null, this._stateBefore(line));

        const hr = document.createElement("div");
        hr.className = "line hr";
        hr.textContent = "---";
        line.after(hr);
        this._applyBlockClassFor(hr, "---");

        const after = document.createElement("div");
        after.className = "line active";
        after.textContent = "";
        hr.after(after);
        this._applyBlockClassFor(after, "");

        this._activeLine = after;
        this._placeCaretInLine(after, 0);
        this._dirty = true;
        this.dispatchEvent(new Event("input", { bubbles: true }));
    }

    // -----------------------------------------------------------------------
    // Caret + selection helpers
    // -----------------------------------------------------------------------

    _currentSelection() {
        // Shadow DOM selection API varies. Chromium: shadowRoot.getSelection().
        // Everything else: window.getSelection() returns the host-anchored sel.
        if (this.shadowRoot.getSelection) return this.shadowRoot.getSelection();
        return window.getSelection();
    }

    _lineContaining(node) {
        while (node && node !== this._editor) {
            if (node.parentNode === this._editor) return node;
            node = node.parentNode;
        }
        return null;
    }

    _caretOffsetInLine(line) {
        const sel = this._currentSelection();
        if (!sel || !sel.rangeCount) return null;
        const range = sel.getRangeAt(0);
        if (!line.contains(range.startContainer) && range.startContainer !== line) {
            return null;
        }
        return this._charOffset(line, range.startContainer, range.startOffset);
    }

    _getLineSelection(line) {
        const sel = this._currentSelection();
        if (!sel || !sel.rangeCount) {
            const pos = line.textContent.length;
            return { start: pos, end: pos };
        }
        const range = sel.getRangeAt(0);
        if (!line.contains(range.startContainer) && range.startContainer !== line) {
            const pos = line.textContent.length;
            return { start: pos, end: pos };
        }
        const start = this._charOffset(line, range.startContainer, range.startOffset);
        const end   = this._charOffset(line, range.endContainer,   range.endOffset);
        return { start: Math.min(start, end), end: Math.max(start, end) };
    }

    _charOffset(root, container, offset) {
        // If the container is the root (happens at empty-line boundaries),
        // count children up to offset.
        if (container === root) {
            let total = 0;
            for (let i = 0; i < offset; i++) {
                total += (root.childNodes[i]?.textContent ?? "").length;
            }
            return total;
        }
        let total = 0;
        const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
        let node;
        while ((node = walker.nextNode())) {
            if (node === container) return total + offset;
            total += node.length;
        }
        return root.textContent.length;
    }

    _placeCaretInLine(line, offset) {
        this._suppressSelect = true;
        try {
            const sel = this._currentSelection();
            const range = document.createRange();
            const target = this._textNodeAtOffset(line, offset);
            if (target.node) {
                range.setStart(target.node, target.offset);
            } else {
                range.setStart(line, 0);
            }
            range.collapse(true);
            if (sel) {
                sel.removeAllRanges();
                sel.addRange(range);
            }
        } finally {
            // Allow the next real selection change to fire.
            queueMicrotask(() => { this._suppressSelect = false; });
        }
    }

    _selectInLine(line, start, end) {
        this._suppressSelect = true;
        try {
            const sel = this._currentSelection();
            const range = document.createRange();
            const a = this._textNodeAtOffset(line, start);
            const b = this._textNodeAtOffset(line, end);
            if (a.node) range.setStart(a.node, a.offset); else range.setStart(line, 0);
            if (b.node) range.setEnd(b.node, b.offset);   else range.setEnd(line, 0);
            if (sel) {
                sel.removeAllRanges();
                sel.addRange(range);
            }
        } finally {
            queueMicrotask(() => { this._suppressSelect = false; });
        }
    }

    _textNodeAtOffset(line, offset) {
        let total = 0;
        const walker = document.createTreeWalker(line, NodeFilter.SHOW_TEXT);
        let node;
        while ((node = walker.nextNode())) {
            if (total + node.length >= offset) {
                return { node, offset: offset - total };
            }
            total += node.length;
        }
        // Fallback: end of line
        const lastText = this._lastTextNode(line);
        if (lastText) return { node: lastText, offset: lastText.length };
        return { node: null, offset: 0 };
    }

    _lastTextNode(line) {
        const walker = document.createTreeWalker(line, NodeFilter.SHOW_TEXT);
        let last = null;
        let node;
        while ((node = walker.nextNode())) last = node;
        return last;
    }

    // -----------------------------------------------------------------------
    // Undo / redo
    //
    // Snapshot-based history. Keeps it in one conceptual unit so debug is
    // straightforward: the current document is either the live DOM (editing)
    // or an exact past snapshot (after undo/redo). There's no operational
    // log to replay, no diff to rebase - the whole value string is small
    // enough that storing N copies is fine.
    // -----------------------------------------------------------------------

    canUndo() { return this._historyIdx > 0; }
    canRedo() { return this._historyIdx < this._history.length - 1; }

    undo() {
        if (this.hasAttribute("readonly")) return;
        // If the user typed a burst and hit undo before the debounce fired,
        // push that pending state first - otherwise undo would jump past it.
        this._commitPendingHistory();
        if (!this.canUndo()) return;
        this._historyIdx--;
        this._restoreSnapshot(this._history[this._historyIdx]);
        this._notifyHistory();
    }

    redo() {
        if (this.hasAttribute("readonly")) return;
        this._commitPendingHistory();
        if (!this.canRedo()) return;
        this._historyIdx++;
        this._restoreSnapshot(this._history[this._historyIdx]);
        this._notifyHistory();
    }

    _resetHistory() {
        clearTimeout(this._historyTimer);
        this._historyTimer = null;
        this._history    = [this._snapshot()];
        this._historyIdx = 0;
        this._notifyHistory();
    }

    _snapshot() {
        const line = this._activeLine;
        let lineIdx = 0;
        let offset  = 0;
        if (line && line.isConnected) {
            lineIdx = Array.from(this._editor.children).indexOf(line);
            if (lineIdx < 0) lineIdx = 0;
            offset = this._caretOffsetInLine(line) ?? 0;
        }
        return { value: this.value, lineIdx, offset };
    }

    _scheduleHistorySnapshot() {
        if (this._suppressHistory) return;
        // Per-keystroke undo: push a snapshot on every input event. Duplicate-
        // value pushes are dropped inside _pushHistory, so non-text events
        // (caret movement, focus) don't pollute the stack. _commitPendingHistory
        // is a no-op now but kept as a hook in case we ever re-introduce
        // coalescing - undo/redo call it before stepping, so the contract
        // "stack is caught up before a step" holds either way.
        this._pushHistory(this._snapshot());
    }

    _commitPendingHistory() {
        // No debounced work to flush; per-keystroke snapshots are synchronous.
    }

    _pushHistory(entry) {
        // New edit after an undo: drop any redo-forward entries. Once you
        // diverge from the timeline, the branch you walked away from is gone.
        if (this._historyIdx < this._history.length - 1) {
            this._history.length = this._historyIdx + 1;
        }
        const last = this._history[this._history.length - 1];
        if (last && last.value === entry.value) return;
        this._history.push(entry);
        // Cap memory. 1000 steps ≈ 1000 characters typed; deep enough for
        // realistic editing, still trivial memory against a few-KB note.
        if (this._history.length > 1000) this._history.shift();
        this._historyIdx = this._history.length - 1;
        this._notifyHistory();
    }

    _restoreSnapshot(entry) {
        this._suppressHistory = true;
        try {
            this._applyValue(entry.value);
            const target = this._editor.children[entry.lineIdx];
            if (target) {
                this._activeLine = target;
                this._flattenLine(target);
                this._placeCaretInLine(target, entry.offset);
            }
            this._dirty = true;
            this.dispatchEvent(new Event("input", { bubbles: true }));
        } finally {
            // Unflip after the synchronous event dispatch completes, so the
            // input listener's _scheduleHistorySnapshot call (which runs
            // during dispatch) sees _suppressHistory === true and bails out.
            // Without this defer, the restore would immediately queue a new
            // snapshot for the state we just restored, which would truncate
            // the redo stack and make Ctrl+Y a no-op.
            queueMicrotask(() => { this._suppressHistory = false; });
        }
    }

    _notifyHistory() {
        this.dispatchEvent(new CustomEvent("history-change", {
            bubbles:  true,
            composed: true,
            detail:   { canUndo: this.canUndo(), canRedo: this.canRedo() },
        }));
    }

    /** Compute the fence + block state just before a given line - lets us
     *  render a single line correctly without re-rendering the whole document. */
    _stateBefore(line) {
        const state = freshState();
        for (const sib of Array.from(this._editor.children)) {
            if (sib === line) return state;
            advanceState(state, sib.textContent);
        }
        return state;
    }

    /** Cross-line consistency sweep, run after every edit. A keystroke can
     *  change the state of every line below it (type ``` or :::secret on a
     *  line), but the edit paths only re-render the line they touch. Each
     *  line remembers the state it was rendered with (_blockState); this
     *  walks the document once and re-renders exactly the lines whose state
     *  no longer matches. Typing plain text changes no state, so the walk
     *  re-renders nothing - one regex pass per line. */
    _syncBlockState(lines = null) {
        if (!this._editor) return;
        if (!lines) lines = Array.from(this._editor.children);
        const texts = lines.map((l) => l.textContent);
        // Pass 1, top-down: the state before each line, its type, and the
        // link definitions (anywhere in the document, like CommonMark).
        const states = [], types = [], defs = {};
        const state = freshState();
        for (const text of texts) {
            states.push({ ...state });
            const type = classifyLine(text, state).type;
            types.push(type);
            if (type === "linkdef") addLinkDef(defs, text);
            advanceState(state, text, type);
        }
        // Pass 2, bottom-up: which paragraph lines an underline turns into a
        // setext heading (the whole paragraph above it, like CommonMark),
        // and which are table headers.
        // A paragraph line directly above a delimiter row is a table header.
        const ahead = new Array(lines.length).fill(NO_AHEAD);
        let carry = 0;
        for (let i = lines.length - 1; i >= 0; i--) {
            if (types[i] === "setext") carry = texts[i].trim()[0] === "=" ? 1 : 2;
            else if (types[i] === "paragraph") {
                if (types[i + 1] === "table-delim") { ahead[i] = { setext: 0, table: delimAligns(texts[i + 1]) }; carry = 0; }
                else if (carry) ahead[i] = { setext: carry, table: "" };
            }
            else carry = 0;
        }
        this._linkDefs = defs;
        this._linkDefsKey = JSON.stringify(defs);
        // Pass 3: re-render exactly the lines whose context changed.
        for (let i = 0; i < lines.length; i++) {
            const line = lines[i];
            if (line._blockState === this._lineKey(states[i], ahead[i], texts[i])) continue;
            if (line === this._activeLine) {
                line._blockState = this._lineKey(states[i], ahead[i], texts[i]);
                this._applyBlockClass(line, texts[i], classifyLine(texts[i], states[i], ahead[i]));
            } else {
                this._renderLine(line, texts[i], { ...states[i] }, ahead[i]);
            }
        }
    }

    /** The opening line of the block `line` belongs to (itself for an
     *  opening line), or null. */
    _blockOpenLine(line) {
        for (let cur = line; cur; cur = cur.previousElementSibling) {
            const b = cur.dataset.block;
            if (b === "block-open") return cur;
            if (b !== "block-body" && b !== "block-close") return null;
        }
        return null;
    }

    /** Read-only: masked block lines say they can be clicked (a tooltip; the
     *  cursor is CSS). Editable: no tooltip - the caret reveals there. */
    _revealHint(line) {
        if (this.hasAttribute("readonly") && line.classList.contains("block-masked")) {
            line.title = mdT("revealHint", "Click to show or hide");
        } else if (line.hasAttribute("title")) {
            line.removeAttribute("title");
        }
    }
    _syncRevealHints() {
        if (!this._editor) return;
        for (const line of this._editor.querySelectorAll(".line.block-masked, .line[title]")) this._revealHint(line);
    }

    /** Reveal-toggle click handler. From the opening line the eye icon
     *  lives on, walks forward to the block's close (or end of document) and
     *  flips .block-revealed on every line in the range; a one-line block
     *  flips just itself. Session-only - a value set drops it. */
    _toggleBlockReveal(openLine) {
        const reveal = !openLine.classList.contains("block-revealed");
        openLine.classList.toggle("block-revealed", reveal);
        if (openLine.dataset.block === "block-line") return;
        for (let cur = openLine.nextElementSibling; cur; cur = cur.nextElementSibling) {
            if (cur.dataset.block !== "block-body" && cur.dataset.block !== "block-close") break;
            cur.classList.toggle("block-revealed", reveal);
            if (cur.dataset.block === "block-close") break;
        }
    }
}

// ---------------------------------------------------------------------------
// Block parser (per-line, state passed in separately for code fences).
// ---------------------------------------------------------------------------

const BLOCK_PREFIX_RE = {
    heading: /^(#{1,6}\s)/,
    ul:      /^(\s*[-*+]\s)/,
    ol:      /^(\s*\d{1,9}[.)]\s)/,
    quote:   /^(>\s?)/,
};

// List item prefixes (CommonMark): bullets `-` `*` `+`; ordered `1.` or
// `1)` with at most 9 digits. TASK_RE captures prefix, box state, gap, rest.
const TASK_RE = /^(\s*(?:[-*+]|\d{1,9}[.)])\s+)\[([ xX])\](\s+)(.*)$/;
// Thematic break (CommonMark): up to 3 leading spaces, then 3 or more of
// the same `-` `*` `_`, spaces and tabs allowed between them.
const HR_RE = /^ {0,3}([-*_])(?:[ \t]*\1){2,}[ \t]*$/;

/** Code fence step (CommonMark). Outside a fence (`open` false) a line
 *  opens one with 3+ backticks or 3+ tildes, info string allowed. Inside,
 *  only the SAME character, at least as many of it and nothing else on the
 *  line closes it - so ~~~ can quote ``` and ```` can quote ```, and a
 *  fence documenting fences stays text. Returns { boundary, open } where
 *  `open` is the opening run while inside a fence, false outside. */
function fenceStep(src, open) {
    if (!open) {
        const m = /^(`{3,}|~{3,})/.exec(src);
        return m ? { boundary: true, open: m[1] } : { boundary: false, open: false };
    }
    const m = /^(`{3,}|~{3,})[ \t]*$/.exec(src);
    if (m && m[1][0] === open[0] && m[1].length >= open.length) return { boundary: true, open: false };
    return { boundary: false, open };
}

// Setext heading underline (CommonMark): `=` for h1, `-` for h2, up to 3
// leading spaces. Deviation: a dash underline needs 2+ dashes, so starting
// a list ("- ") under a paragraph line does not flash the line above into
// an h2 while you type.
const SETEXT_RE = /^ {0,3}(=+|-{2,})[ \t]*$/;
// Indented code block: 4 spaces or a tab.
const INDENT_CODE_RE = /^( {4}|\t)/;
// Link reference definition: [label]: url "optional title".
const LINKDEF_RE = /^ {0,3}\[([^\]]+)\]:[ \t]*(<[^>]*>|\S+)(?:[ \t]+("[^"]*"|'[^']*'|\([^)]*\)))?[ \t]*$/;

// GFM table delimiter row: cells of dashes with optional alignment colons.
const TABLE_DELIM_RE = /^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?\s*$/;
const TABLE_TYPES = new Set(["table-head", "table-delim", "table-row"]);

/** Split a table row into segments that cover `src` exactly, in order:
 *  { kind: "pipe" | "ws" | "cell", text, start }. Pipes escaped with a
 *  backslash do not split (GFM). A leading or trailing blank segment is
 *  padding, not a cell. Rendering and formatting both use this, so the
 *  two can never disagree about where a cell is. */
function splitTableRow(src) {
    const pipes = [];
    for (let i = 0; i < src.length; i++) {
        if (src[i] === "\\") { i++; continue; }
        if (src[i] === "|") pipes.push(i);
    }
    const out = [];
    let start = 0;
    for (let k = 0; k <= pipes.length; k++) {
        const end  = k < pipes.length ? pipes[k] : src.length;
        const part = src.slice(start, end);
        const edge = k === 0 || k === pipes.length;
        if (edge && part.trim() === "") {
            if (part) out.push({ kind: "ws", text: part, start });
        } else {
            const lead  = /^\s*/.exec(part)[0];
            const body  = part.slice(lead.length).replace(/\s+$/, "");
            const trail = part.slice(lead.length + body.length);
            if (lead)  out.push({ kind: "ws", text: lead, start });
            // An empty cell's caret spot is one space in, not against the
            // closing pipe, so typing there reads "| x |".
            const at = body ? start + lead.length : start + Math.min(1, lead.length);
            out.push({ kind: "cell", text: body, start: at });
            if (trail) out.push({ kind: "ws", text: trail, start: start + lead.length + body.length });
        }
        if (k < pipes.length) out.push({ kind: "pipe", text: "|", start: end });
        start = end + 1;
    }
    return out;
}
const tableCells = (src) => splitTableRow(src).filter((g) => g.kind === "cell").map((g) => g.text);

/** Column alignments from a delimiter row: one char per column,
 *  l / c / r, or "-" for none. */
function delimAligns(src) {
    return tableCells(src).map((c) => {
        const l = c.startsWith(":"), r = c.endsWith(":");
        return l && r ? "c" : r ? "r" : l ? "l" : "-";
    }).join("");
}

// A cell's pipes: the source keeps them escaped (\|, or they would split
// the cell); the dialog shows and takes them plain.
const unescapePipes = (s) => s.replace(/\\\|/g, "|");
const escapePipes   = (s) => s.replace(/\|/g, "\\|");

// Table dialog icons: one-line SVGs, stroke only, currentColor.
const tdIcon = (d) =>
    `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" ` +
    `stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="${d}"/></svg>`;
const TD_ICONS = {
    left:  tdIcon("M15 18l-6-6 6-6"),
    right: tdIcon("M9 18l6-6-6-6"),
    up:    tdIcon("M18 15l-6-6-6 6"),
    down:  tdIcon("M6 9l6 6 6-6"),
    del:   tdIcon("M18 6L6 18M6 6l12 12"),
    "al-": tdIcon("M4 6h16M4 12h16M4 18h16"),
    all:   tdIcon("M4 6h16M4 12h10M4 18h14"),
    alc:   tdIcon("M4 6h16M7 12h10M5 18h14"),
    alr:   tdIcon("M4 6h16M10 12h10M6 18h14"),
};

/** Pretty-print a table block (header, delimiter, rows) so the pipes line
 *  up: every cell padded to its column's width, delimiter dashes to match,
 *  alignment colons kept. Cells beyond the header's count are kept
 *  verbatim at the end of their row - formatting never drops content.
 *  Returns the new line texts, same length as `texts`. */
function formatTable(texts) {
    const rows   = texts.map(tableCells);
    const n      = rows[0].length;
    const aligns = delimAligns(texts[1]);
    const width  = Array.from({ length: n }, (_, c) =>
        Math.max(3, ...rows.map((r, i) => (i === 1 ? 0 : (r[c] || "").length))));
    const pad = (t, w, a) => {
        const gap = w - t.length;
        if (a === "r") return " ".repeat(gap) + t;
        if (a === "c") return " ".repeat(Math.floor(gap / 2)) + t + " ".repeat(Math.ceil(gap / 2));
        return t + " ".repeat(gap);
    };
    return rows.map((r, i) => {
        if (i === 1) {
            return "| " + width.map((w, c) => {
                const a = aligns[c] || "-";
                const dashes = "-".repeat(w - (a === "c" ? 2 : a === "-" ? 0 : 1));
                return a === "c" ? ":" + dashes + ":" : a === "l" ? ":" + dashes : a === "r" ? dashes + ":" : dashes;
            }).join(" | ") + " |";
        }
        const cells = width.map((w, c) => pad(r[c] || "", w, aligns[c]));
        return "| " + cells.concat(r.slice(n)).join(" | ") + " |";
    });
}

/** Render a table line: pipes and padding become hidden markers, each cell
 *  a .tcell (CSS display: table-cell). The line itself is display:
 *  table-row, so consecutive rows form one anonymous table and the columns
 *  line up with no measuring. textContent stays the source. */
function renderTableRow(src, kind, aligns, defs) {
    let col = 0;
    return splitTableRow(src).map((g) => {
        if (g.kind !== "cell") return `<span class="block-marker">${escapeHtml(g.text)}</span>`;
        const a = (aligns && aligns[col++]) || "-";
        if (kind === "table-delim") {
            return `<span class="tcell tdelim"><span class="block-marker">${escapeHtml(g.text)}</span></span>`;
        }
        return `<span class="tcell ta-${a}">${renderInline(g.text, defs)}</span>`;
    }).join("");
}

// ---------------------------------------------------------------------------
// Block registry (static API on the element class - see the header)
// ---------------------------------------------------------------------------

const BLOCKS = new Map();          // name -> compiled definition, in order
const LIVE_EDITORS = new Set();    // connected editors, refreshed on changes
let blockCss = "";                 // every definition's css, concatenated
let blockGen = 0;                  // bumps on every change

function compileBlock(def) {
    if (!def || typeof def.name !== "string" || !/^[a-z][a-z0-9-]*$/.test(def.name)) {
        throw new TypeError("sac-md-editor registerBlock: name must be a-z, 0-9 and dashes");
    }
    // A /g or /y RegExp keeps lastIndex between test() calls - strip them.
    const plain = (re) => new RegExp(re.source, re.flags.replace(/[gy]/g, ""));
    let match = def.match;
    if (typeof match !== "function") {
        if (!(def.open instanceof RegExp) || !(def.close instanceof RegExp)) {
            throw new TypeError(`sac-md-editor registerBlock("${def.name}"): give match(src), or open and close RegExps`);
        }
        const open = plain(def.open), close = plain(def.close);
        match = (src) => open.test(src) ? "open" : close.test(src) ? "close" : null;
    }
    return {
        name:      def.name,
        match,
        masked:    !!def.masked,
        toggle:    !!def.toggle,
        label:     def.label == null ? null : def.label,
        className: def.className ? String(def.className).trim() : "",
        color:     def.color ? String(def.color) : "",
        css:       def.css ? String(def.css) : "",
    };
}

/** A host's match() is foreign code: a throw must not break rendering. */
function blockMatch(def, src) {
    try { return def.match(src); } catch { return null; }
}

function blockClasses(def, part) {
    return `block-${part} block-${def.name}` +
        (def.masked ? " block-masked" : "") + (def.className ? " " + def.className : "");
}

/** The pill text travels as a CSS string in a custom property (JSON quoting
 *  is valid CSS); no label means no pill. */
function setBlockLabel(line, def) {
    let label = def.label;
    if (typeof label === "function") { try { label = label(); } catch { label = null; } }
    if (label == null || label === "") line.style.removeProperty("--mdb-label");
    else line.style.setProperty("--mdb-label", JSON.stringify(String(label)));
}

function refreshBlocks() {
    blockCss = [...BLOCKS.values()].map((d) => d.css).filter(Boolean).join("\n");
    blockGen++;
    for (const editor of LIVE_EDITORS) editor._refreshBlocks();
}

/** Cross-line state at the top of a document. `inFence` and `inBlock` (the
 *  open registered block's name, "" outside one) as before; `prev` is what the previous line was ("blank", "para",
 *  "quote", "list", "code", "other") and `listCtx` whether a list is still
 *  open (a blank line keeps it, an unindented non-list line ends it).
 *  `table` is the open table's alignments ("" outside one) and `pipeCols`
 *  the cell count of the line above if it could be a table header. */
function freshState() {
    return { inFence: false, inBlock: "", prev: "blank", listCtx: false, table: "", pipeCols: 0 };
}

// Lookahead facts about a line: its setext level and, for a table header,
// the alignments of the delimiter row below it.
const NO_AHEAD = Object.freeze({ setext: 0, table: "" });

/** THE line classifier. Everything that decides what a line is lives here:
 *  fences first (inside one nothing else counts), then registered blocks
 *  (bounds, bodies, one-line markers), tables, setext underlines, indented code, link definitions and
 *  finally the single-line syntax (parseLineBlock). `ahead` carries what
 *  only the lines BELOW can tell: the setext level (1/2) when an underline
 *  turns this paragraph into a heading, and the table alignments when a
 *  delimiter row makes it a table header. Never part of `state`. */
function classifyLine(src, state, ahead = NO_AHEAD) {
    const fence = fenceStep(src, state.inFence);
    if (state.inFence) {
        return fence.boundary ? { type: "fence", classes: "fence" }
                              : { type: "fence-body", classes: "fence-body" };
    }
    if (fence.boundary) return { type: "fence", classes: "fence" };
    // Registered blocks. Inside one only its own close counts - no nesting,
    // and another block's opener is body text.
    if (state.inBlock) {
        const def = BLOCKS.get(state.inBlock);
        if (def) {
            return blockMatch(def, src) === "close"
                ? { type: "block-close", classes: blockClasses(def, "close"), def }
                : { type: "block-body",  classes: blockClasses(def, "body"),  def };
        }
    }
    for (const def of BLOCKS.values()) {
        const m = blockMatch(def, src);
        if (m === "open") return { type: "block-open", classes: blockClasses(def, "open"), def };
        if (m === "line") return { type: "block-line", classes: blockClasses(def, "line"), def };
    }
    // Tables (GFM): body rows continue while lines hold a pipe and start no
    // other block; the delimiter row must match the header's cell count.
    const hasPipe = src.includes("|");
    if (state.table && hasPipe && parseLineBlock(src).type === "paragraph") {
        return { type: "table-row", classes: "table-row", aligns: state.table };
    }
    if (state.pipeCols && hasPipe && TABLE_DELIM_RE.test(src) && tableCells(src).length === state.pipeCols) {
        return { type: "table-delim", classes: "table-delim", aligns: delimAligns(src) };
    }
    // An underline only counts directly under paragraph text.
    if (state.prev === "para" && SETEXT_RE.test(src)) {
        const level = src.trim()[0] === "=" ? 1 : 2;
        return { type: "setext", classes: `setext-underline setext-${level}` };
    }
    // Indented code cannot interrupt a paragraph, and inside a list an
    // indented line is the item's continuation, not code.
    if (INDENT_CODE_RE.test(src) && src.trim() !== "" &&
        state.prev !== "para" && state.prev !== "quote" && !state.listCtx) {
        return { type: "indent-code", classes: "fence-body indent-code" };
    }
    // A definition cannot interrupt a paragraph either.
    if (state.prev !== "para" && LINKDEF_RE.test(src)) return { type: "linkdef", classes: "linkdef" };
    const base = parseLineBlock(src);
    if (base.type === "paragraph" && ahead.table) {
        return { type: "table-head", classes: "table-head", aligns: ahead.table };
    }
    if (base.type === "paragraph" && ahead.setext) {
        return { type: "heading", classes: `heading h${ahead.setext} setext-heading` };
    }
    return base;
}

/** Advance the cross-line state past one line whose type is already known
 *  (classifyLine with setext 0). Mutates and returns `state`. The ONE
 *  definition of how state flows downward - _stateBefore and
 *  _syncBlockState both walk with it. */
function advanceState(state, text, type = classifyLine(text, state).type) {
    if (type === "fence") state.inFence = fenceStep(text, state.inFence).open;
    else if (type === "block-open") {
        state.inBlock = "";
        for (const def of BLOCKS.values()) {
            if (blockMatch(def, text) === "open") { state.inBlock = def.name; break; }
        }
    }
    else if (type === "block-close") state.inBlock = "";

    const isList = type === "ul" || type === "ol" || type === "task";
    if (isList) state.listCtx = true;
    else if (type === "empty") { /* a blank line keeps the list open */ }
    else if (!(state.listCtx && /^\s/.test(text))) state.listCtx = false;

    state.table = type === "table-delim" ? delimAligns(text)
        : type === "table-row" ? state.table : "";
    state.pipeCols = type === "paragraph" && text.includes("|") ? tableCells(text).length : 0;

    state.prev = type === "empty" ? "blank"
        : type === "paragraph" ? "para"
        : type === "quote" ? "quote"
        : isList ? "list"
        : type === "indent-code" ? "code"
        : "other";
    return state;
}

/** What a line's rendering depends on besides its own text: the parts of
 *  the state classifyLine reads, plus the lookahead facts. */
function stateKey(state, ahead = NO_AHEAD) {
    const prev = state.prev === "para" ? "p" : state.prev === "quote" ? "q" : "";
    return `${state.inFence || ""}|${state.inBlock}|${prev}|${state.listCtx ? "l" : ""}` +
           `|${state.table}|${state.pipeCols || ""}|${ahead.setext || ""}|${ahead.table}`;
}

/** Collect a link reference definition into `defs` (first one wins, labels
 *  normalised the way marked looks them up). */
function addLinkDef(defs, src) {
    const m = LINKDEF_RE.exec(src);
    if (!m) return;
    const label = m[1].trim().replace(/\s+/g, " ").toLowerCase();
    if (!label || Object.prototype.hasOwnProperty.call(defs, label)) return;
    const href = m[2].replace(/^<|>$/g, "");
    const title = m[3] ? m[3].slice(1, -1) : null;
    defs[label] = { href, title };
}

function parseLineBlock(src) {
    if (src === "") return { type: "empty", classes: "" };
    const h = src.match(/^(#{1,6})\s+/);
    if (h) return { type: "heading", classes: `heading h${h[1].length}` };
    // Before lists: `- - -` and `* * *` would otherwise read as bullets.
    if (HR_RE.test(src))            return { type: "hr", classes: "hr" };
    // Task lists get their own type so the `[ ]`/`[x]` render as a checkbox.
    // Tested before plain lists because the pattern is a strict superset.
    const task = TASK_RE.exec(src);
    if (task) {
        const checked = task[2] !== " ";
        return { type: "task", classes: checked ? "task task-done" : "task" };
    }
    if (/^\s*[-*+]\s+/.test(src))        return { type: "ul",    classes: "list ul" };
    if (/^\s*\d{1,9}[.)]\s+/.test(src))  return { type: "ol",    classes: "list ol" };
    if (/^>\s?/.test(src))          return { type: "quote", classes: "quote" };
    return { type: "paragraph", classes: "" };
}

// ---------------------------------------------------------------------------
// Inline renderer. Produces HTML where every marker character stays in the
// DOM (wrapped in <span class="marker">), so the line's textContent equals
// the original source. This is the invariant that makes active/inactive
// flipping safe and keeps `value` round-tripping exactly.
//
// Tokenization is delegated to marked v15 (CommonMark + GFM). We walk the
// token tree ourselves so we can re-inject the raw marker characters as
// hidden-but-present spans — marked's own renderer strips them.
//
// Inline HTML is ALWAYS escaped (never parsed as live DOM). This is a
// deliberate deviation from CommonMark: inline <tag> content gets rendered
// as literal text, not as real HTML. Two reasons:
//   1. Content fidelity: DOMPurify-stripped tags would silently disappear
//      from the inactive render, and then from line.textContent, corrupting
//      the user's source on the next save.
//   2. Defense in depth: a self-hosted memory tool should never execute
//      arbitrary HTML typed into a note. DOMPurify below is a second gate
//      in case anything slips through the walker.
// ---------------------------------------------------------------------------

const PURIFY_CONFIG = {
    ALLOWED_TAGS: ["span", "strong", "em", "del", "code", "a", "br", "img"],
    ALLOWED_ATTR: ["class", "href", "target", "rel", "src", "alt", "title",
                   "loading", "referrerpolicy", "data-glyph"],
    ALLOW_DATA_ATTR: false,
    KEEP_CONTENT: true,
};

const SAFE_HREF_RE = /^(https?:|mailto:|#)/i;
// Images: http/https only. Block data: URIs (SVG-XSS, canvas leaks),
// javascript:, and anything else that could exfil or execute.
const SAFE_IMG_SRC_RE = /^https?:/i;

/** `defs`: the document's link reference definitions ({ label: { href,
 *  title } }), so [text][id], [text][] and [text] resolve. */
function renderInline(text, defs = null) {
    if (!text) return "";
    // Graceful degradation if vendor scripts haven't loaded yet (e.g. in a
    // unit test harness that imports just the component file).
    if (typeof marked === "undefined" || typeof DOMPurify === "undefined") {
        return escapeHtml(text);
    }
    let tokens;
    try {
        if (defs && Object.keys(defs).length) {
            const lexer = new marked.Lexer({ gfm: true, breaks: false });
            lexer.tokens.links = defs;
            tokens = lexer.inlineTokens(text);
        } else {
            tokens = marked.Lexer.lexInline(text, { gfm: true, breaks: false });
        }
    } catch { return escapeHtml(text); }
    return DOMPurify.sanitize(walkInlineTokens(tokens), PURIFY_CONFIG);
}

// HTML entity: named, decimal or hex. Rendered as its character via CSS
// generated content while the source stays in the DOM as a marker - the
// line's textContent never changes.
const ENTITY_RE = /&(?:#[0-9]{1,7}|#[xX][0-9a-fA-F]{1,6}|[A-Za-z][A-Za-z0-9]{1,31});/g;
let entityDecoder = null;
const entityCache = new Map();
function decodeEntity(ent) {
    let glyph = entityCache.get(ent);
    if (glyph === undefined) {
        glyph = decodeEntityUncached(ent);
        if (entityCache.size < 512) entityCache.set(ent, glyph);
    }
    return glyph;
}
function decodeEntityUncached(ent) {
    if (ent[1] === "#") {
        const hex = ent[2] === "x" || ent[2] === "X";
        const cp = parseInt(ent.slice(hex ? 3 : 2, -1), hex ? 16 : 10);
        // CommonMark: 0 and invalid code points become U+FFFD.
        if (!(cp > 0 && cp <= 0x10ffff) || (cp >= 0xd800 && cp <= 0xdfff)) return "\ufffd";
        return String.fromCodePoint(cp);
    }
    // Named: let the HTML parser decode it. A <textarea> never runs markup.
    if (!entityDecoder) entityDecoder = document.createElement("textarea");
    entityDecoder.innerHTML = ent;
    return entityDecoder.value;
}
function renderText(text) {
    if (!text.includes("&")) return escapeHtml(text);
    let out = "", last = 0;
    for (const m of text.matchAll(ENTITY_RE)) {
        const glyph = decodeEntity(m[0]);
        if (glyph === m[0]) continue;             // unknown name: plain text
        out += escapeHtml(text.slice(last, m.index)) +
               `<span class="entity" data-glyph="${escapeAttr(glyph)}"><span class="marker">${escapeHtml(m[0])}</span></span>`;
        last = m.index + m[0].length;
    }
    return out + escapeHtml(text.slice(last));
}

function walkInlineTokens(tokens) {
    let out = "";
    for (const t of tokens) out += renderInlineToken(t);
    return out;
}

function renderInlineToken(t) {
    switch (t.type) {
        case "text":
            return renderText(t.text);
        case "escape":
            // raw like "\\*" — keep the backslash visible as a marker so
            // textContent round-trips; show the escaped char as content.
            return `<span class="marker">\\</span>${escapeHtml(t.text)}`;
        case "strong": {
            const delim = t.raw.startsWith("__") ? "__" : "**";
            return `<span class="marker">${delim}</span><strong>${walkInlineTokens(t.tokens || [])}</strong><span class="marker">${delim}</span>`;
        }
        case "em": {
            const delim = t.raw.charAt(0); // "*" or "_"
            return `<span class="marker">${delim}</span><em>${walkInlineTokens(t.tokens || [])}</em><span class="marker">${delim}</span>`;
        }
        case "del":
            return `<span class="marker">~~</span><del>${walkInlineTokens(t.tokens || [])}</del><span class="marker">~~</span>`;
        case "codespan": {
            const m = t.raw.match(/^(`+)/);
            const delim = m ? m[1] : "`";
            return `<span class="marker">${delim}</span><code>${escapeHtml(t.text)}</code><span class="marker">${delim}</span>`;
        }
        case "br":
            // Hard break — keep the raw source chars visible as a marker so
            // textContent stays faithful; the <br> is the visual break.
            return `<span class="marker">${escapeHtml(t.raw.replace(/\n$/, ""))}</span><br>`;
        case "link": {
            const href = t.href || "";
            const safe = SAFE_HREF_RE.test(href) ? href : "#";
            // Autolinks: <url>, <email>, or bare URL — one contiguous anchor,
            // no outer bracket markers.
            if (t.raw === href || t.raw === `<${href}>` || t.raw === `<${t.text}>`) {
                return `<a href="${escapeAttr(safe)}" target="_blank" rel="noopener noreferrer">${escapeHtml(t.raw)}</a>`;
            }
            // Inline link [label](href) — preserve the `](...)` tail literally.
            // Reference link [label][id] / [label][] / [label]: the tail is
            // `][id]`, `][]` or `]`. Either way "[" + label + tail must BE the
            // raw source, or we render it as plain text rather than risk the
            // textContent invariant.
            const inlineIdx = t.raw.lastIndexOf("](");
            const refIdx    = t.raw.lastIndexOf("][");
            const tail = inlineIdx >= 0 ? t.raw.substring(inlineIdx)
                : refIdx >= 0 ? t.raw.substring(refIdx)
                : "]";
            if ("[" + t.text + tail !== t.raw) return escapeHtml(t.raw);
            const titleAttr = t.title ? ` title="${escapeAttr(t.title)}"` : "";
            return `<span class="marker">[</span><a href="${escapeAttr(safe)}"${titleAttr} target="_blank" rel="noopener noreferrer">${walkInlineTokens(t.tokens || [])}</a><span class="marker">${escapeHtml(tail)}</span>`;
        }
        case "image": {
            // Preserve the raw source as a marker span (hidden on inactive
            // lines via the existing .line:not(.active) .marker rule), then
            // render the real <img> alongside. textContent of the line stays
            // equal to the source (markers contribute text, <img> doesn't).
            const srcMarker = `<span class="marker">${escapeHtml(t.raw)}</span>`;
            const href = t.href || "";
            if (!SAFE_IMG_SRC_RE.test(href)) return srcMarker;
            const altAttr   = escapeAttr(t.text || "");
            const titleAttr = t.title ? ` title="${escapeAttr(t.title)}"` : "";
            // referrerpolicy=no-referrer so the image host can't see which
            // note you're viewing. loading=lazy skips off-screen fetches.
            return `${srcMarker}<img src="${escapeAttr(href)}" alt="${altAttr}"${titleAttr}` +
                   ` loading="lazy" referrerpolicy="no-referrer">`;
        }
        case "html":
            // Policy: escape, don't parse. See header comment for why.
            return escapeHtml(t.raw);
        default:
            return escapeHtml(t.raw || "");
    }
}

function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, c => ({
        "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;"
    }[c]));
}

function escapeAttr(s) { return escapeHtml(s); }

// ---------------------------------------------------------------------------
// Shadow DOM template
// ---------------------------------------------------------------------------

const TEMPLATE = `
<style>
    :host {
        display: block;
        position: relative;
        /* Editor reads as a bordered writing surface with a toolbar strip
           on top — a bare transparent area gives no "you can type here"
           affordance at all. Focus pulls the border toward the accent. */
        background: var(--field, rgba(0, 0, 0, 0.18));
        border: 1px solid var(--border, color-mix(in srgb, var(--fg, #fff) 8%, transparent));
        border-radius: var(--radius-l, 6px);   /* a surface */
        transition: border-color 0.15s, box-shadow 0.15s;
    }
    :host(:focus-within) {
        border-color: color-mix(in srgb, var(--accent, #3b82f6) 50%, transparent);
        box-shadow: 0 0 0 3px color-mix(in srgb, var(--accent, #3b82f6) 10%, transparent);
    }
    :host(.is-readonly) .toolbar { display: none; }
    /* Read-only: a masked block reveals on click (no caret to do it). */
    :host(.is-readonly) .line.block-masked { cursor: pointer; }
    :host(.is-readonly) {
        background: transparent;
        border-color: transparent;
    }

    .toolbar {
        display: flex;
        gap: 2px;
        flex-wrap: wrap;
        padding: 8px 12px;
        background: color-mix(in srgb, var(--fg, #fff) 2%, transparent);
        border-bottom: 1px solid var(--border, color-mix(in srgb, var(--fg, #fff) 8%, transparent));
        border-radius: inherit;   /* the host's top corners - cannot drift */
        border-bottom-left-radius: 0;
        border-bottom-right-radius: 0;
    }
    .toolbar button {
        padding: 5px 9px;
        min-width: 30px;
        height: 28px;
        border-radius: var(--radius-m, 4px);   /* a control */
        background: transparent;
        border: 1px solid transparent;
        color: var(--text-muted, #888);
        font-family: inherit;
        font-size: 12px;
        font-weight: 600;
        cursor: pointer;
        transition: background 0.12s, color 0.12s;
        display: inline-flex;
        align-items: center;
        justify-content: center;
        line-height: 1;
    }
    .toolbar button:hover {
        background: color-mix(in srgb, var(--fg, #fff) 6%, transparent);
        color: var(--text, #fff);
    }
    .toolbar button b, .toolbar button i { font-size: 13px; }
    .toolbar .sep {
        width: 1px;
        height: 20px;
        margin: 0 4px;
        background: var(--border, color-mix(in srgb, var(--fg, #fff) 8%, transparent));
        align-self: center;
    }

    .editor {
        outline: none;
        position: relative;
        min-height: 60vh;
        padding: 14px 20px;
        cursor: text;
        color: var(--text, #fff);
        font-family: inherit;
        font-size: 15px;
        line-height: 1.6;
        caret-color: var(--accent, #3b82f6);
    }
    /* Placeholder: show when the only line is empty. Uses :has() - evergreen.
       Offsets = .editor padding + .line's own 2px/net-0 horizontal inset so
       it sits exactly where typed text will land. */
    .editor[data-placeholder]:has(> .line:only-child:empty)::before {
        content: attr(data-placeholder);
        position: absolute;
        top: 16px;
        left: 20px;
        color: var(--text-muted, #888);
        opacity: 0.55;
        pointer-events: none;
    }

    .line {
        position: relative;
        min-height: 1.6em;
        padding: 2px 10px;
        margin: 0 -10px;
        border-radius: var(--radius-m, 4px);
        white-space: pre-wrap;
        word-break: break-word;
        transition: background-color 0.12s ease;
    }
    .line.empty { min-height: 1.6em; }
    /* Focus indicator: the whole active line gets a subtly brighter
       background. Obsidian does the same - no colored bar, no layout
       shift, just a softly highlighted row. The .sel class is applied
       to every line a non-collapsed selection touches, so multi-line
       selections show as a band of highlighted rows rather than a
       jagged text-shape selection. */
    .editor:focus-within .line.active,
    .editor:focus-within .line.sel {
        background: color-mix(in srgb, var(--fg, #fff) 4.5%, transparent);
    }
    /* Within that band, the actual text selection uses the accent color
       at low opacity - keeps the "exactly-these-chars-are-selected"
       affordance without the default bright system blue. */
    .editor ::selection {
        background: color-mix(in srgb, var(--accent, #3b82f6) 28%, transparent);
        color: inherit;
    }

    /* Inline markers (bold/italic/code/link brackets). Obsidian behavior:
       fully invisible on inactive lines; on the active line they don't
       exist as spans at all - the line is a flat text node, so the raw
       characters just show. */
    .line:not(.active) .marker { display: none; }

    /* Block markers (line prefix: heading hash, list dash, quote angle,
       ordered-list number, fence triple-backticks).
       Default: keep visible but dimmed. Per-type overrides below decide
       which ones stay visible when inactive. */
    .line .block-marker {
        color: var(--text-muted, #888);
        opacity: 0.5;
    }

    /* Headings jump to size live as soon as a heading prefix is typed.
       When inactive, the hash prefix is fully hidden - the font size alone
       signals the heading (Obsidian's approach). */
    .line.heading {
        font-family: 'Outfit', sans-serif;
        font-weight: 700;
        letter-spacing: -0.01em;
        line-height: 1.25;
        margin-top: 0.6em;
        margin-bottom: 0.2em;
    }
    .line.h1 { font-size: 1.85em; }
    .line.h2 { font-size: 1.4em; }
    .line.h3 { font-size: 1.15em; }
    .line.h4 { font-size: 1.05em; }
    .line.h5 { font-size: 0.95em; }
    .line.h6 { font-size: 0.88em; color: var(--text-muted, #888); }
    .line.heading:not(.active) .block-marker { display: none; }

    /* Blockquote. A continuous left bar across a run of quote lines, plus
       a very soft tint. No per-line rounded corners - they break up the
       block visually when you stack multiple lines. The left bar sits at
       the inside edge of the line's negative margin so it reads flush
       with the gutter instead of hanging inside the padding. The angle-
       bracket marker is hidden when inactive; the left bar is the cue. */
    .line.quote {
        background: color-mix(in srgb, var(--fg, #fff) 2.5%, transparent);
        padding-left: 18px;
        margin: 0 -10px;
        border-left: 3px solid color-mix(in srgb, var(--fg, #fff) 28%, transparent);
        border-radius: 0;
        color: var(--text-muted, #bbb);
        font-style: italic;
    }
    .line.quote:not(.active) .block-marker { display: none; }

    /* Unordered lists: hide the raw dash marker when inactive, paint a
       real bullet in its place via ::before. When the line becomes
       active we drop the extra indent entirely so the raw "- foo" sits
       at the normal left margin - matches how Obsidian collapses list
       indentation back to the gutter in edit mode. */
    .line.list.ul:not(.active) { padding-left: 28px; }
    .line.list.ul:not(.active) .block-marker { display: none; }
    .line.list.ul:not(.active)::before {
        content: "\\2022";
        position: absolute;
        left: 10px;
        top: 1px;
        color: var(--text-muted, #888);
        opacity: 0.6;
        pointer-events: none;
    }

    /* Ordered lists: keep the number visible. */
    .line.list.ol:not(.active) .block-marker {
        color: var(--text-muted, #888);
        opacity: 0.75;
        margin-right: 2px;
    }

    /* Task lists: hide the dash prefix (the rendered checkbox is the
       structural cue). Same rule as ul - indent only when inactive. */
    .line.task:not(.active) .block-marker { display: none; }

    /* Horizontal rule. Markers stay in textContent (save/load round-trip),
       but visually the line collapses to a thin horizontal border.
       min-height + height both set to 1px so the base .line rule
       1.6em min-height doesn't win the cascade and leave the raw dashes
       visible. overflow:hidden clips the hidden text. */
    .line.hr {
        border-bottom: 1px solid var(--border, color-mix(in srgb, var(--fg, #fff) 15%, transparent));
        margin: 1em 0;
        padding: 0;
        min-height: 1px;
        height: 1px;
        overflow: hidden;
    }
    .line.hr.active {
        min-height: 1.6em;
        height: auto;
        border-bottom: none;
        padding: 1px 0;
        overflow: visible;
    }

    /* Fenced code block. Editor background is near-black, so a dark
       overlay would be invisible - we go *lighter* instead with a white
       tint plus a subtle inset border so the block reads as a distinct
       card. Per-line border-radius is suppressed inside the block and
       restored only on the outer edges via :has() sibling selectors. */

    /* Monospace font-stack for EVERY fence line, active or not, body or
       boundary. !important is a forcing function: earlier revisions of
       this rule kept losing font-family to generic highlight selectors
       that didn't set font-family but inherited over the Inter body
       font via some path I never pinned down. Using !important here
       stops that debate - code lines are monospace, full stop. */
    .line.fence,
    .line.fence-body,
    .line.fence.active,
    .line.fence-body.active,
    .line.fence *,
    .line.fence-body * {
        font-family: Consolas, "Cascadia Mono", "Courier New", monospace !important;
        font-size: 0.92em;
        font-variant-ligatures: none;
        letter-spacing: 0;
    }

    .line.fence-body,
    .line.fence:not(.active) {
        background: color-mix(in srgb, var(--fg, #fff) 5%, transparent);
        margin: 0 -10px;
        padding: 2px 14px;
        border-radius: 0;
    }
    .line.fence-body .code-body { display: inline; font-family: inherit; }

    /* Active fence body: keep the code-card background so it stays one
       visual block while you type. Uses .editor:focus-within to beat the
       generic .line.active highlight on specificity - otherwise the
       0.045 tint would paint right over the code card. */
    .editor:focus-within .line.fence-body.active,
    .line.fence-body.active {
        background: color-mix(in srgb, var(--fg, #fff) 7%, transparent);
        margin: 0 -10px;
        padding: 2px 14px;
        border-radius: 0;
    }

    /* Inactive fence boundary lines: hide the backtick text (font-size:0),
       keep the line navigable with arrow keys. Collapses to a short strip
       that caps the block top/bottom. */
    .line.fence:not(.active) {
        min-height: 8px;
        padding-top: 4px;
        padding-bottom: 4px;
    }
    .line.fence:not(.active) .block-marker {
        color: transparent;
        font-size: 0;
    }

    /* Opening fence: rounded top + top border for the code-card look. */
    .line.fence:not(.active):has(+ .line.fence-body) {
        border-top-left-radius: var(--radius-m, 4px);
        border-top-right-radius: var(--radius-m, 4px);
        min-height: 10px;
        padding-top: 6px;
        box-shadow: inset 0 1px 0 color-mix(in srgb, var(--fg, #fff) 8%, transparent),
                    inset 1px 0 0 color-mix(in srgb, var(--fg, #fff) 8%, transparent),
                    inset -1px 0 0 color-mix(in srgb, var(--fg, #fff) 8%, transparent);
    }
    /* Closing fence: rounded bottom + bottom border. */
    .line.fence-body + .line.fence:not(.active) {
        border-bottom-left-radius: var(--radius-m, 4px);
        border-bottom-right-radius: var(--radius-m, 4px);
        min-height: 10px;
        padding-bottom: 6px;
        box-shadow: inset 0 -1px 0 color-mix(in srgb, var(--fg, #fff) 8%, transparent),
                    inset 1px 0 0 color-mix(in srgb, var(--fg, #fff) 8%, transparent),
                    inset -1px 0 0 color-mix(in srgb, var(--fg, #fff) 8%, transparent);
    }
    /* Body lines get left/right borders so the card has continuous sides. */
    .line.fence-body {
        box-shadow: inset 1px 0 0 color-mix(in srgb, var(--fg, #fff) 8%, transparent),
                    inset -1px 0 0 color-mix(in srgb, var(--fg, #fff) 8%, transparent);
    }

    /* Indented code (4 spaces / tab): the lines carry .fence-body, so they
       get the code card above; with no fence lines around it, its own
       first and last lines cap the card. */
    .line.indent-code:not(.active) .block-marker { display: none; }
    .line.indent-code:not(.line.indent-code + .line.indent-code) {
        border-top-left-radius: var(--radius-m, 4px);
        border-top-right-radius: var(--radius-m, 4px);
        box-shadow: inset 0 1px 0 color-mix(in srgb, var(--fg, #fff) 8%, transparent),
                    inset 1px 0 0 color-mix(in srgb, var(--fg, #fff) 8%, transparent),
                    inset -1px 0 0 color-mix(in srgb, var(--fg, #fff) 8%, transparent);
    }
    .line.indent-code:not(:has(+ .line.indent-code)) {
        border-bottom-left-radius: var(--radius-m, 4px);
        border-bottom-right-radius: var(--radius-m, 4px);
        box-shadow: inset 0 -1px 0 color-mix(in srgb, var(--fg, #fff) 8%, transparent),
                    inset 1px 0 0 color-mix(in srgb, var(--fg, #fff) 8%, transparent),
                    inset -1px 0 0 color-mix(in srgb, var(--fg, #fff) 8%, transparent);
    }
    .line.indent-code:not(.line.indent-code + .line.indent-code):not(:has(+ .line.indent-code)) {
        box-shadow: inset 0 0 0 1px color-mix(in srgb, var(--fg, #fff) 8%, transparent);
    }

    /* Setext underline (=== / --- under paragraph text): the paragraph
       renders as the heading, the underline collapses to a sliver. */
    .line.setext-underline:not(.active) {
        min-height: 0;
        height: 4px;
        padding: 0;
        overflow: hidden;
    }
    .line.setext-underline:not(.active) .block-marker { display: none; }
    .line.setext-underline.active { color: var(--text-muted, #888); }

    /* Link reference definition ([id]: url): metadata, not prose. */
    .line.linkdef {
        color: var(--text-muted, #888);
        font-size: 0.85em;
    }

    /* Tables: every row line is a table-row, so consecutive rows form
       one anonymous CSS table and the columns align by themselves. Pipes
       and padding are hidden markers; the active row shows its raw
       source like every other line. */
    .line.table-head:not(.active),
    .line.table-row:not(.active),
    .line.table-delim:not(.active) { display: table-row; }
    .line.table-head:not(.active) .block-marker,
    .line.table-row:not(.active) .block-marker,
    .line.table-delim:not(.active) .block-marker { display: none; }
    .line .tcell {
        display: table-cell;
        padding: 4px 12px;
        border-bottom: 1px solid var(--border, color-mix(in srgb, var(--fg, #fff) 12%, transparent));
    }
    .line.table-head .tcell { font-weight: 600; }
    .line.table-delim .tcell {
        padding: 0;
        height: 0;
        border-bottom-width: 2px;
    }
    .line .tcell.ta-c { text-align: center; }
    .line .tcell.ta-r { text-align: right; }

    /* The Table button while the caret is in a table: it edits, not
       inserts - say so. */
    .toolbar button.ctx {
        color: var(--accent, #3b82f6);
        background: color-mix(in srgb, var(--accent, #3b82f6) 14%, transparent);
    }

    /* Table dialog (native <dialog>, top layer). */
    .tdlg {
        padding: 0;
        max-width: min(92vw, 960px);
        color: var(--text, #fff);
        background: var(--panel, #161b26);
        border: 1px solid var(--border-strong, color-mix(in srgb, var(--fg, #fff) 16%, transparent));
        border-radius: var(--radius-l, 6px);
        box-shadow: 0 16px 48px color-mix(in srgb, var(--sink, #000) 45%, transparent);
    }
    .tdlg::backdrop { background: color-mix(in srgb, var(--sink, #000) 45%, transparent); }
    .tdlg form {
        display: flex;
        flex-direction: column;
        gap: 12px;
        padding: 16px;
        max-height: 80vh;
        box-sizing: border-box;
    }
    .tdlg-title { font-weight: 700; }
    .tdlg-scroll { overflow: auto; min-height: 0; }
    .tdlg-grid { border-collapse: collapse; }
    .tdlg-grid th, .tdlg-grid td { padding: 2px; vertical-align: middle; }
    .tdlg-ctl { display: flex; gap: 1px; justify-content: center; align-items: center; }
    .tdlg-tag { font-size: 0.75em; color: var(--text-muted, #888); padding: 0 4px; }
    .tdlg input {
        width: 10em;
        min-width: 6em;
        box-sizing: border-box;
        font: inherit;
        color: inherit;
        background: var(--field, color-mix(in srgb, var(--fg, #fff) 4%, transparent));
        border: 1px solid var(--border, color-mix(in srgb, var(--fg, #fff) 8%, transparent));
        border-radius: var(--radius-m, 4px);
        padding: 5px 8px;
    }
    .tdlg input:focus {
        outline: 2px solid color-mix(in srgb, var(--accent, #3b82f6) 60%, transparent);
        outline-offset: -1px;
    }
    .tdlg-head input { font-weight: 600; }
    .tdlg input.ta-c { text-align: center; }
    .tdlg input.ta-r { text-align: right; }
    .tdlg-ic {
        appearance: none;
        display: inline-flex;
        padding: 3px;
        border: none;
        background: none;
        color: var(--text-muted, #888);
        border-radius: var(--radius-m, 4px);
        cursor: pointer;
    }
    .tdlg-ic svg { width: 14px; height: 14px; display: block; }
    .tdlg-ic:hover:not(:disabled) { background: var(--hover, color-mix(in srgb, var(--fg, #fff) 8%, transparent)); color: var(--text, #fff); }
    .tdlg-ic:disabled { opacity: 0.3; cursor: default; }
    .tdlg-actions { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
    .tdlg-gap { flex: 1; }
    .tdlg-btn {
        appearance: none;
        font: inherit;
        font-size: 0.85em;
        font-weight: 600;
        padding: 6px 12px;
        color: var(--text, #fff);
        background: color-mix(in srgb, var(--fg, #fff) 5%, transparent);
        border: 1px solid var(--border, color-mix(in srgb, var(--fg, #fff) 8%, transparent));
        border-radius: var(--radius-m, 4px);
        cursor: pointer;
    }
    .tdlg-btn:hover { background: var(--hover, color-mix(in srgb, var(--fg, #fff) 8%, transparent)); }
    .tdlg-btn.primary {
        color: var(--on-accent, #fff);
        background: var(--accent-fill, var(--accent, #3b82f6));
        border-color: transparent;
    }
    .tdlg-btn:focus-visible, .tdlg-ic:focus-visible {
        outline: 2px solid var(--accent, #3b82f6);
        outline-offset: 1px;
    }

    /* HTML entity (&copy;): the source stays in the DOM as a marker
       (hidden on inactive lines), the character is generated content. */
    .line:not(.active) .entity::before { content: attr(data-glyph); }

    /* Active fence marker (user is editing the fence boundary line):
       dim the raw markers so they read as syntax, keep the card bg. */
    .editor:focus-within .line.fence.active,
    .line.fence.active {
        background: color-mix(in srgb, var(--fg, #fff) 7%, transparent);
        margin: 0 -10px;
        padding: 2px 14px;
        border-radius: 0;
        color: var(--text-muted, #888);
        box-shadow: inset 1px 0 0 color-mix(in srgb, var(--fg, #fff) 8%, transparent),
                    inset -1px 0 0 color-mix(in srgb, var(--fg, #fff) 8%, transparent);
    }

    /* Selection band inside a code block: keep the card bg, don't let
       the generic .sel highlight paint over it. Active fence/body lines
       already have their own bg set by the dedicated .active rules. */
    .editor:focus-within .line.sel.fence-body,
    .editor:focus-within .line.sel.fence {
        background: color-mix(in srgb, var(--fg, #fff) 9%, transparent);
    }

    /* Registered blocks (registerBlock) - a tinted card in the block's
       colour (--mdb-color on the line, warm by default) across the opening
       line, the body lines and the closing line, so a multi-line block
       reads as one shape. When a boundary line goes active the card stays
       and its raw marker chars come back (like a fence). */
    .line.block-open,
    .line.block-close,
    .line.block-body,
    .line.block-line {
        --mdb-c: var(--mdb-color, var(--accent-warm, #f59e0b));
        background: color-mix(in srgb, var(--mdb-c) 4.5%, transparent);
        margin: 0 -10px;
        padding-left: 14px;
        padding-right: 14px;
        border-left: 3px solid color-mix(in srgb, var(--mdb-c) 35%, transparent);
        border-radius: 0;
    }
    .line.block-open,
    .line.block-close,
    .line.block-line {
        color: var(--mdb-c);
        font-size: 0.9em;
    }

    /* Inactive boundaries: hide the literal marker chars. They stay in
       textContent (font-size: 0), so the round-trip invariant holds. */
    .line.block-open:not(.active) .block-marker,
    .line.block-close:not(.active) .block-marker,
    .line.block-line:not(.active):not(.block-revealed) .block-marker {
        color: transparent;
        font-size: 0;
    }

    /* Opening (or one-line) block, inactive: the label pill via ::before.
       Pseudo content is invisible to textContent, safe for labels; no
       label, no pill. */
    .line.block-open:not(.active),
    .line.block-line:not(.active) {
        padding-top: 6px;
        padding-bottom: 4px;
    }
    .line.block-open:not(.active)::before,
    .line.block-line:not(.active)::before {
        content: var(--mdb-label, none);
        display: inline-block;
        padding: 2px 10px;
        font-size: 0.86em;
        font-weight: 600;
        letter-spacing: 0.02em;
        color: var(--mdb-c);
        background: color-mix(in srgb, var(--mdb-c) 12%, transparent);
        border: 1px solid color-mix(in srgb, var(--mdb-c) 25%, transparent);
        border-radius: 999px;
    }

    /* Close boundary (inactive): collapse to a thin footer - the card's
       bottom edge with no visible marker text. */
    .line.block-close:not(.active) {
        min-height: 6px;
        height: 6px;
        padding-top: 0;
        padding-bottom: 0;
        overflow: hidden;
    }

    /* Active boundaries drop the caret-ready padding. The card stays. */
    .line.block-open.active,
    .line.block-close.active,
    .line.block-line.active {
        padding-top: 2px;
        padding-bottom: 2px;
    }

    /* Masked bodies: inline markdown still renders, but a CSS blur on the
       inner .block-body-text span prevents incidental over-the-shoulder
       reads. Blurring the whole line would smear the card's left border -
       a child keeps the chrome sharp. The active (caret) line drops the
       mask so it's editable; the reveal toggle adds .block-revealed to
       every line of the block for session-only unmasking. */
    .line.block-masked.block-body:not(.active):not(.block-revealed) .block-body-text {
        filter: blur(4px);
        user-select: none;
        transition: filter 0.15s ease;
        display: inline-block;
    }
    .line.block-masked.block-body:not(.active):not(.block-revealed):hover .block-body-text {
        filter: blur(3px);
    }

    /* Reveal toggle - inline SVG eye on a block's opening line. SVG shape
       children have no text nodes, so line.textContent stays equal to the
       source (the whole editor model depends on that invariant).
       contenteditable=false keeps the caret out of it. The diagonal slash
       is always rendered; hidden unless the block carries .block-revealed. */
    .sac-reveal-toggle {
        display: inline-flex;
        align-items: center;
        justify-content: center;
        margin-left: 8px;
        padding: 2px 3px;
        opacity: 0.6;
        cursor: pointer;
        user-select: none;
        border-radius: var(--radius-m, 4px);
        transition: opacity 0.12s, background 0.12s;
        vertical-align: middle;
        color: var(--mdb-c);
    }
    /* Slightly taller than the pill glyph so the icon reads with matching
       visual weight. */
    .sac-reveal-toggle svg {
        width: 14px;
        height: 14px;
        display: block;
    }
    .sac-reveal-toggle .sac-eye-slash { visibility: hidden; }
    .sac-reveal-toggle:hover {
        opacity: 1;
        background: color-mix(in srgb, var(--mdb-c) 14%, transparent);
    }
    /* Revealed state: surface the slash through the eye. */
    .line.block-revealed .sac-reveal-toggle { opacity: 0.95; }
    .line.block-revealed .sac-reveal-toggle .sac-eye-slash { visibility: visible; }

    /* Task list: render [ ] / [x] as a visual checkbox while inactive. The
       bracket characters stay in textContent (font-size: 0 hides them
       visually) so save/load round-trips unchanged. */
    .line.task:not(.active) { padding-left: 28px; }
    .line.task .task-box {
        display: inline-block;
        width: 14px;
        height: 14px;
        /* middle, not a pixel offset: the box's own text is font-size 0,
           so its baseline sits at the TOP of the box and any baseline
           offset hangs the whole box below the text line. */
        vertical-align: middle;
        border: 1.5px solid var(--border, color-mix(in srgb, var(--fg, #fff) 30%, transparent));
        border-radius: var(--radius-s, 2px);
        position: relative;
        color: transparent;
        font-size: 0;
        cursor: default;
    }
    .line.task.active .task-box {
        /* Active = raw mode; don't render a fake box, show the text. */
        display: inline;
        width: auto;
        height: auto;
        border: none;
        color: inherit;
        font-size: inherit;
        vertical-align: baseline;
    }
    /* The hidden gap marker leaves the box touching the text. */
    .line.task:not(.active) .task-box { margin-right: 6px; }
    .line.task .task-box.checked {
        background: var(--accent, #3b82f6);
        border-color: var(--accent, #3b82f6);
    }
    .line.task .task-box.checked::after {
        content: "\\2713";
        position: absolute;
        left: 50%;
        top: 50%;
        transform: translate(-50%, -54%);
        color: var(--on-accent, #fff);
        font-size: 11px;
        line-height: 1;
        font-weight: 700;
    }
    .line.task.active .task-box.checked::after { content: none; }
    .line.task.task-done:not(.active) {
        text-decoration: line-through;
        color: var(--text-muted, #888);
    }

    /* Inline elements (applied inside rendered lines). */
    .line strong { font-weight: 700; color: var(--text, #fff); }
    .line em     { font-style: italic; }
    .line del    { text-decoration: line-through; opacity: 0.65; }
    .line code {
        background: color-mix(in srgb, var(--fg, #fff) 8%, transparent);
        padding: 1px 5px;
        border-radius: var(--radius-s, 2px);
        font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
        font-size: 0.9em;
    }
    .line a {
        color: var(--accent, #3b82f6);
        text-decoration: underline;
        text-decoration-color: color-mix(in srgb, var(--accent, #3b82f6) 40%, transparent);
        text-underline-offset: 2px;
    }
    .line a:hover { text-decoration-color: var(--accent, #3b82f6); }

    /* Inline images — cap so a giant remote image doesn't blow out the
       line. The source marker sits next to it; on inactive lines markers
       are hidden, so only the image shows. */
    .line img {
        max-width: 100%;
        max-height: 320px;
        height: auto;
        border-radius: var(--radius-m, 4px);
        vertical-align: middle;
        display: inline-block;
    }
    /* Active line flattens innerHTML to plain text — no <img> rendered then.
       That's the desired editing state. */
</style>
<div class="toolbar" part="toolbar">
    <button type="button" data-fmt="bold"   title="Bold (Ctrl+B)"><b>B</b></button>
    <button type="button" data-fmt="italic" title="Italic (Ctrl+I)"><i>I</i></button>
    <button type="button" data-fmt="strike" title="Strikethrough"><span style="text-decoration: line-through;">S</span></button>
    <span class="sep"></span>
    <button type="button" data-fmt="h1"     title="Heading 1">H1</button>
    <button type="button" data-fmt="h2"     title="Heading 2">H2</button>
    <button type="button" data-fmt="h3"     title="Heading 3">H3</button>
    <span class="sep"></span>
    <button type="button" data-fmt="link"   title="Link (Ctrl+K)">Link</button>
    <button type="button" data-fmt="code"   title="Inline code">Code</button>
    <span class="sep"></span>
    <button type="button" data-fmt="ul"     title="Bullet list">List</button>
    <button type="button" data-fmt="ol"     title="Numbered list">1. List</button>
    <button type="button" data-fmt="quote"  title="Blockquote">Quote</button>
    <button type="button" data-fmt="hr"     title="Horizontal rule">HR</button>
    <button type="button" data-fmt="table"  title="Table">Table</button>
</div>
<div class="editor" part="editor" contenteditable="plaintext-only" spellcheck="true"></div>
`;

customElements.define("sac-md-editor", SacMdEditor);
