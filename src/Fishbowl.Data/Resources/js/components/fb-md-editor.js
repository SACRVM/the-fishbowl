/**
 * <fb-md-editor>
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
 * Events (bubble out of host):
 *   input  - fires after every edit (keystroke, paste, toolbar action).
 *   change - fires when focus leaves the component after an edit.
 *
 * Methods:
 *   focus() - focus the editor surface.
 *
 * Keyboard:
 *   Ctrl/Cmd+B, Ctrl/Cmd+I, Ctrl/Cmd+K    bold / italic / link
 *   Enter                                  split; continues `- `/`* `/`1. ` lists
 *   Enter on empty list item               exits the list
 *   Backspace at start of non-first line   merge with previous line
 *   Tab                                    two-space soft tab
 *
 * ::secret / ::end blocks: lines stay editable in v1; visual masking of
 * secret bodies is intentionally deferred - secrets in preview would be a
 * separate feature touching the crypto path, not just rendering.
 *
 * Dependencies (loaded as globals before this script):
 *   marked     - CommonMark + GFM tokenizer. We only use marked.Lexer.lexInline
 *                and walk the token tree ourselves (see renderInline below).
 *   DOMPurify  - HTML sanitiser. Second line of defence behind our own
 *                escape-don't-parse policy for inline HTML.
 */

class FbMdEditor extends HTMLElement {
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

    connectedCallback() {
        if (!this.shadowRoot.firstChild) this._render();
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
    }

    get value() {
        if (!this._editor) return "";
        return Array.from(this._editor.children)
            .map(line => line.textContent)
            .join("\n");
    }

    set value(v) {
        this._applyValue(v);
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
        const state = { inFence: false };
        this._editor.replaceChildren(
            ...lines.map(src => this._buildRenderedLine(src, state))
        );
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
        }
    }

    // -----------------------------------------------------------------------
    // Shadow DOM + stylesheet
    // -----------------------------------------------------------------------

    _render() {
        this.shadowRoot.innerHTML = TEMPLATE;

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
        this._editor.replaceChildren(this._buildRenderedLine("", { inFence: false }));
        this._resetHistory();

        // Host-level input listener: catches both the native input events
        // that bubble up from the internal contenteditable AND the synthetic
        // input events we dispatch from structural edits (toolbar, paste,
        // splitAt, etc.). One listener covers both paths.
        this.addEventListener("input", () => this._scheduleHistorySnapshot());

        this._editor.addEventListener("input",       (e) => this._handleInput(e));
        this._editor.addEventListener("beforeinput", (e) => this._handleBeforeInput(e));
        this._editor.addEventListener("keydown",     (e) => this._handleKeyDown(e));
        this._editor.addEventListener("paste",       (e) => this._handlePaste(e));
        this._editor.addEventListener("cut",         (e) => this._handleCut(e));
        this._editor.addEventListener("copy",        (e) => this._handleCopy(e));
        this._editor.addEventListener("focusout",    (e) => this._handleFocusOut(e));

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
     *  the cross-line code-fence tracker so lines inside ``` get monospaced. */
    _renderLine(line, src = null, state = null) {
        if (src === null) src = line.textContent;

        // Fence state advances on every line - must be updated even if this
        // line isn't going to be rendered differently.
        const isFenceBoundary = /^```/.test(src);
        const insideFenceNow = state ? state.inFence : false;
        if (isFenceBoundary && state) state.inFence = !state.inFence;

        line.classList.remove("active");
        this._applyBlockClass(line, src, insideFenceNow, isFenceBoundary);

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
                line.innerHTML = `<span class="block-marker">${escapeHtml(prefix)}</span>${renderInline(rest)}`;
                return;
            }
        }
        if (blockType === "task") {
            const m = src.match(/^(\s*[-*]\s+)\[([ xX])\](\s+)(.*)$/);
            if (m) {
                const checked = m[2] === "x" || m[2] === "X";
                const boxClass = checked ? "task-box checked" : "task-box";
                line.innerHTML =
                    `<span class="block-marker">${escapeHtml(m[1])}</span>` +
                    `<span class="${boxClass}">[${m[2]}]</span>` +
                    `<span class="block-marker">${escapeHtml(m[3])}</span>` +
                    renderInline(m[4]);
                return;
            }
        }
        if (blockType === "secret-open" || blockType === "secret-close") {
            line.innerHTML = `<span class="block-marker">${escapeHtml(src)}</span>`;
            return;
        }
        // Plain paragraph (or empty line).
        if (src === "") {
            line.innerHTML = "";
            return;
        }
        line.innerHTML = renderInline(src);
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
        this._applyBlockClass(line, src, state.inFence, /^```/.test(src));
    }

    /** Recompute the line's block-level class without touching its inner DOM.
     *  Called on every keystroke so that as you type `# `, the font jumps to
     *  heading size live. Also stores the block type in dataset.block. */
    _applyBlockClass(line, src, inFence, isFenceBoundary) {
        const active = line.classList.contains("active");
        const block = inFence && !isFenceBoundary
            ? { type: "fence-body", classes: "fence-body" }
            : isFenceBoundary
                ? { type: "fence", classes: "fence" }
                : parseLineBlock(src);
        const classes = ["line"];
        if (active) classes.push("active");
        if (block.classes) classes.push(block.classes);
        if (src === "") classes.push("empty");
        line.className = classes.join(" ");
        line.dataset.block = block.type;
    }

    /** Rebuild classes + inner DOM for every line. Use after multi-line state
     *  may have shifted (blur, paste, programmatic value set). */
    _renderAllInactive() {
        const state = { inFence: false };
        for (const line of Array.from(this._editor.children)) {
            this._renderLine(line, null, state);
        }
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
        this._activeLine = newActive;
        if (newActive) this._flattenLine(newActive);
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
            const line = this._buildRenderedLine("", { inFence: false });
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

        if (e.key === "Enter" && !e.shiftKey && !(e.ctrlKey || e.metaKey)) {
            e.preventDefault();
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
        // Sweep every line carrying .active, not just _activeLine - in case
        // any earlier edit left a stale one around.
        for (const line of this._editor.querySelectorAll(".line.active")) {
            this._renderLine(line, null, this._stateBefore(line));
        }
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

        // List continuation: detect a `- `/`* `/`1. ` prefix on the current
        // line and duplicate it on the new line, unless the current prefix
        // is all we had (empty item) - in which case exit the list.
        const listMatch = before.match(/^(\s*)([-*]|(\d+)\.)\s+(.*)$/);
        if (listMatch) {
            const [, indent, marker, numStr, content] = listMatch;
            if (!content && !after.trim()) {
                // Empty item + nothing after = exit the list.
                line.replaceChildren(document.createTextNode(""));
                this._applyBlockClassFor(line, "");
                this._placeCaretInLine(line, 0);
                this._dirty = true;
                this.dispatchEvent(new Event("input", { bubbles: true }));
                return;
            }
            const nextMarker = numStr ? `${parseInt(numStr) + 1}.` : marker;
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
            case "code": {
                const selectedLines = this._getSelectedLines();
                if (selectedLines.length > 1) return this._wrapAsCodeFence(selectedLines);
                return this._wrapInLine(line, sel, "`", "`", "code");
            }
            case "link": {
                const url = window.prompt("Link URL", "https://");
                if (!url) return;
                const safe = /^(https?:|mailto:|#)/i.test(url) ? url : "https://" + url;
                return this._wrapInLine(line, sel, "[", `](${safe})`, "link text");
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
        const prefixRe  = isOrdered ? /^\d+\.\s/ : null;
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
     *  ``` markers before the first and after the last. The enclosed lines
     *  re-render as fence-body (monospace + background). */
    _wrapAsCodeFence(lines) {
        if (lines.length === 0) return;
        const first = lines[0];
        const last  = lines[lines.length - 1];

        const open = document.createElement("div");
        open.className = "line";
        open.textContent = "```";
        first.before(open);
        this._renderLine(open, "```", this._stateBefore(open));

        const close = document.createElement("div");
        close.className = "line";
        close.textContent = "```";
        last.after(close);
        this._renderLine(close, "```", this._stateBefore(close));

        // Re-render the enclosed inactive lines so they pick up fence-body.
        // The active line stays flat but gets its class updated.
        for (const line of lines) {
            if (line === this._activeLine) {
                this._applyBlockClass(line, line.textContent, true, false);
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

    /** Compute the fence state just before a given line - lets us render a
     *  single line correctly without re-rendering the whole document. */
    _stateBefore(line) {
        const state = { inFence: false };
        for (const sib of Array.from(this._editor.children)) {
            if (sib === line) return state;
            if (/^```/.test(sib.textContent)) state.inFence = !state.inFence;
        }
        return state;
    }
}

// ---------------------------------------------------------------------------
// Block parser (per-line, state passed in separately for code fences).
// ---------------------------------------------------------------------------

const BLOCK_PREFIX_RE = {
    heading: /^(#{1,6}\s)/,
    ul:      /^(\s*[-*]\s)/,
    ol:      /^(\s*\d+\.\s)/,
    quote:   /^(>\s?)/,
};

function parseLineBlock(src) {
    if (src === "") return { type: "empty", classes: "" };
    const h = src.match(/^(#{1,6})\s+/);
    if (h) return { type: "heading", classes: `heading h${h[1].length}` };
    // Task lists get their own type so the `[ ]`/`[x]` render as a checkbox.
    // Tested before plain lists because the pattern is a strict superset.
    if (/^\s*[-*]\s+\[[ xX]\]\s+/.test(src)) {
        const checked = /\[[xX]\]/.test(src);
        return { type: "task", classes: checked ? "task task-done" : "task" };
    }
    if (/^\s*[-*]\s+/.test(src))    return { type: "ul",    classes: "list ul" };
    if (/^\s*\d+\.\s+/.test(src))   return { type: "ol",    classes: "list ol" };
    if (/^>\s?/.test(src))          return { type: "quote", classes: "quote" };
    if (/^\s*(---|\*\*\*|___)\s*$/.test(src)) return { type: "hr", classes: "hr" };
    if (/^```/.test(src))           return { type: "fence", classes: "fence" };
    if (/^::secret(\s|$)/.test(src)) return { type: "secret-open",  classes: "secret-marker" };
    if (/^::end(\s|$)/.test(src))    return { type: "secret-close", classes: "secret-marker" };
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
    ALLOWED_TAGS: ["span", "strong", "em", "del", "code", "a", "br"],
    ALLOWED_ATTR: ["class", "href", "target", "rel"],
    ALLOW_DATA_ATTR: false,
    KEEP_CONTENT: true,
};

const SAFE_HREF_RE = /^(https?:|mailto:|#)/i;

function renderInline(text) {
    if (!text) return "";
    // Graceful degradation if vendor scripts haven't loaded yet (e.g. in a
    // unit test harness that imports just the component file).
    if (typeof marked === "undefined" || typeof DOMPurify === "undefined") {
        return escapeHtml(text);
    }
    let tokens;
    try {
        tokens = marked.Lexer.lexInline(text, { gfm: true, breaks: false });
    } catch { return escapeHtml(text); }
    return DOMPurify.sanitize(walkInlineTokens(tokens), PURIFY_CONFIG);
}

function walkInlineTokens(tokens) {
    let out = "";
    for (const t of tokens) out += renderInlineToken(t);
    return out;
}

function renderInlineToken(t) {
    switch (t.type) {
        case "text":
            return escapeHtml(t.text);
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
            const tailIdx = t.raw.lastIndexOf("](");
            if (tailIdx < 0) return escapeHtml(t.raw);
            const tail = t.raw.substring(tailIdx);
            return `<span class="marker">[</span><a href="${escapeAttr(safe)}" target="_blank" rel="noopener noreferrer">${walkInlineTokens(t.tokens || [])}</a><span class="marker">${escapeHtml(tail)}</span>`;
        }
        case "image":
            // Don't actually render images — too many layout/network surprises
            // for a personal-memory editor. Keep the source visible as markers
            // so textContent round-trips.
            return `<span class="marker">${escapeHtml(t.raw)}</span>`;
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
    }
    :host(.is-readonly) .toolbar { display: none; }

    .toolbar {
        display: flex;
        gap: 2px;
        flex-wrap: wrap;
        margin-bottom: 12px;
        padding-bottom: 8px;
        border-bottom: 1px solid var(--border, rgba(255,255,255,0.08));
    }
    .toolbar button {
        padding: 5px 9px;
        min-width: 30px;
        height: 28px;
        border-radius: 6px;
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
        background: rgba(255,255,255,0.06);
        color: var(--text, #fff);
    }
    .toolbar button b, .toolbar button i { font-size: 13px; }
    .toolbar .sep {
        width: 1px;
        height: 20px;
        margin: 0 4px;
        background: var(--border, rgba(255,255,255,0.08));
        align-self: center;
    }

    .editor {
        outline: none;
        position: relative;
        min-height: 60vh;
        color: var(--text, #fff);
        font-family: inherit;
        font-size: 15px;
        line-height: 1.6;
        caret-color: var(--accent, #3b82f6);
    }
    /* Placeholder: show when the only line is empty. Uses :has() - evergreen. */
    .editor[data-placeholder]:has(> .line:only-child:empty)::before {
        content: attr(data-placeholder);
        position: absolute;
        top: 1px;
        left: 0;
        color: var(--text-muted, #888);
        opacity: 0.4;
        pointer-events: none;
    }

    .line {
        position: relative;
        min-height: 1.6em;
        padding: 2px 10px;
        margin: 0 -10px;
        border-radius: 4px;
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
        background: rgba(255, 255, 255, 0.045);
    }
    /* Within that band, the actual text selection uses the accent color
       at low opacity - keeps the "exactly-these-chars-are-selected"
       affordance without the default bright system blue. */
    .editor ::selection {
        background: rgba(59, 130, 246, 0.28);
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
        background: rgba(255, 255, 255, 0.025);
        padding-left: 18px;
        margin: 0 -10px;
        border-left: 3px solid rgba(255, 255, 255, 0.28);
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
        border-bottom: 1px solid var(--border, rgba(255,255,255,0.15));
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
        background: rgba(255, 255, 255, 0.05);
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
        background: rgba(255, 255, 255, 0.07);
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
        border-top-left-radius: 6px;
        border-top-right-radius: 6px;
        min-height: 10px;
        padding-top: 6px;
        box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.08),
                    inset 1px 0 0 rgba(255, 255, 255, 0.08),
                    inset -1px 0 0 rgba(255, 255, 255, 0.08);
    }
    /* Closing fence: rounded bottom + bottom border. */
    .line.fence-body + .line.fence:not(.active) {
        border-bottom-left-radius: 6px;
        border-bottom-right-radius: 6px;
        min-height: 10px;
        padding-bottom: 6px;
        box-shadow: inset 0 -1px 0 rgba(255, 255, 255, 0.08),
                    inset 1px 0 0 rgba(255, 255, 255, 0.08),
                    inset -1px 0 0 rgba(255, 255, 255, 0.08);
    }
    /* Body lines get left/right borders so the card has continuous sides. */
    .line.fence-body {
        box-shadow: inset 1px 0 0 rgba(255, 255, 255, 0.08),
                    inset -1px 0 0 rgba(255, 255, 255, 0.08);
    }

    /* Active fence marker (user is editing the fence boundary line):
       dim the raw markers so they read as syntax, keep the card bg. */
    .editor:focus-within .line.fence.active,
    .line.fence.active {
        background: rgba(255, 255, 255, 0.07);
        margin: 0 -10px;
        padding: 2px 14px;
        border-radius: 0;
        color: var(--text-muted, #888);
        box-shadow: inset 1px 0 0 rgba(255, 255, 255, 0.08),
                    inset -1px 0 0 rgba(255, 255, 255, 0.08);
    }

    /* Selection band inside a code block: keep the card bg, don't let
       the generic .sel highlight paint over it. Active fence/body lines
       already have their own bg set by the dedicated .active rules. */
    .editor:focus-within .line.sel.fence-body,
    .editor:focus-within .line.sel.fence {
        background: rgba(255, 255, 255, 0.09);
    }

    /* Secret marker lines stay visible - they're structural boundaries
       the user needs to see. */
    .line.secret-marker {
        color: var(--accent-warm, #f59e0b);
        font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
        font-size: 0.9em;
    }
    .line.secret-marker .block-marker {
        color: inherit;
        opacity: 0.85;
    }

    /* Task list: render [ ] / [x] as a visual checkbox while inactive. The
       bracket characters stay in textContent (font-size: 0 hides them
       visually) so save/load round-trips unchanged. */
    .line.task:not(.active) { padding-left: 28px; }
    .line.task .task-box {
        display: inline-block;
        width: 14px;
        height: 14px;
        vertical-align: -3px;
        border: 1.5px solid var(--border, rgba(255,255,255,0.3));
        border-radius: 3px;
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
        color: #fff;
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
        background: rgba(255, 255, 255, 0.08);
        padding: 1px 5px;
        border-radius: 3px;
        font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
        font-size: 0.9em;
    }
    .line a {
        color: var(--accent, #3b82f6);
        text-decoration: underline;
        text-decoration-color: rgba(59, 130, 246, 0.4);
        text-underline-offset: 2px;
    }
    .line a:hover { text-decoration-color: var(--accent, #3b82f6); }
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
</div>
<div class="editor" part="editor" contenteditable="plaintext-only" spellcheck="true"></div>
`;

customElements.define("fb-md-editor", FbMdEditor);
