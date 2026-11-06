/*
 * fb.noteHistory — what agents changed in a note (note_versions, kept for
 * every write from a key): a <sac-window> listing each change, newest first,
 * and beside it what that change did — the title, the tags added and taken
 * off, and the text as a line diff against the state after it (the next
 * change, or the note as it is now). A writer puts the note back to the
 * state before a change ("Restore this version"); a locked note must be
 * unlocked first.
 *
 *   fb.noteHistory.open({ api, note, writable, onRestored(note) })
 *     api  — fb.api.notes.in(ws), the workspace the note is in
 */
(function () {
    if (!window.fb) return;

    let win = null;
    let state = null;

    function ensureWindow() {
        if (win) return win;
        win = document.createElement("sac-window");
        win.id = "fb-note-history";
        win.classList.add("fb-window");
        win.setAttribute("escape-closes", "");
        win.setAttribute("width", "880px");
        win.setAttribute("height", "580px");
        win.setAttribute("top", "70px");
        win.setAttribute("left", "calc(50vw - 440px)");
        win.setAttribute("controls", "maximize close");
        win.innerHTML = `
            <div class="fb-history">
                <div class="fb-history-list" role="listbox"></div>
                <div class="fb-history-detail">
                    <div class="fb-history-head">
                        <span class="fb-history-when"></span>
                        <button type="button" class="btn fb-history-restore" hidden></button>
                    </div>
                    <div class="fb-history-meta"></div>
                    <div class="fb-history-diff"></div>
                </div>
            </div>`;
        document.body.appendChild(win);
        win.querySelector(".fb-history-restore").addEventListener("click", () => restore());
        win.querySelector(".fb-history-list").addEventListener("keydown", (e) => {
            if (e.key !== "ArrowDown" && e.key !== "ArrowUp") return;
            e.preventDefault();
            const i = state.versions.findIndex((v) => v.id === state.selected) + (e.key === "ArrowDown" ? 1 : -1);
            if (i >= 0 && i < state.versions.length) select(state.versions[i].id, true);
        });
        return win;
    }

    async function open({ api, note, writable, onRestored }) {
        const w = ensureWindow();
        w.setAttribute("title", fb.t("fb.notes.history.title", "History — {title}", { title: note.title || fb.t("fb.notes.untitled", "Untitled") }));
        state = { api, note, writable, onRestored, versions: [], texts: new Map(), selected: null };
        const list = w.querySelector(".fb-history-list");
        list.replaceChildren();
        w.querySelector(".fb-history-diff").replaceChildren();
        w.querySelector(".fb-history-meta").replaceChildren();
        w.querySelector(".fb-history-when").textContent = "";
        w.querySelector(".fb-history-restore").hidden = true;
        requestAnimationFrame(() => w.open());
        try { state.versions = await api.versions(note.id); }
        catch (err) { console.warn("[fb.noteHistory] versions unavailable:", err); state.versions = []; }
        if (!state.versions.length) {
            const empty = document.createElement("p");
            empty.className = "empty-state";
            empty.textContent = fb.t("fb.notes.history.empty", "No agent has changed this note — every change an agent makes is kept here.");
            w.querySelector(".fb-history-diff").replaceChildren(empty);
            return;
        }
        list.replaceChildren(...state.versions.map((v) => {
            const row = document.createElement("button");
            row.type = "button";
            row.className = "fb-history-row";
            row.setAttribute("role", "option");
            row.dataset.id = v.id;
            const when = document.createElement("span");
            when.textContent = fb.format.dateTime(new Date(v.savedAt));
            const who = document.createElement("span");
            who.className = "fb-history-who";
            who.textContent = fb.t("fb.notes.history.by-agent", "Changed by an agent");
            row.append(when, who);
            row.addEventListener("click", () => select(v.id));
            return row;
        }));
        select(state.versions[0].id, false);
    }

    // The state before the change (`v`) and after it (the next newer
    // version, or the note now).
    async function textOf(v) {
        if (!state.texts.has(v.id)) state.texts.set(v.id, state.api.version(state.note.id, v.id));
        return state.texts.get(v.id);
    }

    async function select(id, focus) {
        state.selected = id;
        const w = win;
        w.querySelectorAll(".fb-history-row").forEach((r) => {
            const on = r.dataset.id === id;
            r.classList.toggle("selected", on);
            r.setAttribute("aria-selected", String(on));
            if (on && focus) r.focus();
        });
        const i = state.versions.findIndex((v) => v.id === id);
        const v = state.versions[i];
        let before, after;
        try {
            before = await textOf(v);
            after = i === 0
                ? { title: state.note.title, content: state.note.content, tags: state.note.tags || [] }
                : await textOf(state.versions[i - 1]);
        } catch (err) {
            console.warn("[fb.noteHistory] version unavailable:", err);
            return;
        }
        if (state.selected !== id) return;

        w.querySelector(".fb-history-when").textContent = fb.t("fb.notes.history.change", "The change of {when}", { when: fb.format.dateTime(new Date(v.savedAt)) });
        const restoreBtn = w.querySelector(".fb-history-restore");
        restoreBtn.textContent = fb.t("fb.notes.history.restore", "Restore the version before it");
        restoreBtn.hidden = !state.writable;
        restoreBtn.disabled = !!state.note.locked;
        restoreBtn.title = state.note.locked ? fb.t("fb.notes.history.locked", "Unlock the note to restore an earlier version") : "";

        const meta = w.querySelector(".fb-history-meta");
        meta.replaceChildren();
        if ((before.title || "") !== (after.title || "")) {
            const p = document.createElement("p");
            p.textContent = fb.t("fb.notes.history.title-changed", "Title: \"{old}\" → \"{new}\"", { old: before.title || "", new: after.title || "" });
            meta.append(p);
        }
        const was = new Set(before.tags || []), now = new Set(after.tags || []);
        const added = [...now].filter((t) => !was.has(t)), removed = [...was].filter((t) => !now.has(t));
        if (added.length || removed.length) {
            const p = document.createElement("p");
            p.className = "fb-history-tags";
            for (const [list, sign] of [[added, "+"], [removed, "−"]]) {
                for (const t of list) {
                    const chip = document.createElement("sac-chip");
                    chip.setAttribute("label", `${sign} ${t}`);
                    chip.setAttribute("color", fb.tags.colorFor?.(t) || "gray");
                    p.append(chip);
                }
            }
            meta.append(p);
        }
        w.querySelector(".fb-history-diff").replaceChildren(diffView(before.content || "", after.content || ""));
    }

    async function restore() {
        const v = state.versions.find((x) => x.id === state.selected);
        if (!v) return;
        const answer = await sac.dialog.confirm({
            title: fb.t("fb.notes.history.restore-title", "Restore this version?"),
            message: fb.t("fb.notes.history.restore-msg", "The note's title, text and tags go back to how they were before this change. What came after stays in the history."),
            buttons: [
                { action: "cancel", label: fb.t("fb.common.cancel", "Cancel"), kind: "default" },
                { action: "restore", label: fb.t("fb.notes.history.restore-btn", "Restore"), kind: "primary" },
            ],
        });
        if (answer !== "restore") return;
        try {
            const note = await state.api.restoreVersion(state.note.id, v.id);
            win.close();
            state.onRestored?.(note);
            sac.toast?.(fb.t("fb.notes.history.restored", "The earlier version is back."), { kind: "success" });
        } catch (err) {
            sac.toast?.(fb.errors?.text(err, fb.t("fb.notes.history.restore-failed", "Couldn't restore the version.")) || String(err), { kind: "error" });
        }
    }

    // A line diff (longest common subsequence), unchanged runs folded to two
    // lines of context around each change.
    function diffView(a, b) {
        const box = document.createElement("div");
        box.className = "fb-diff";
        const x = a.split("\n"), y = b.split("\n");
        if (a === b) {
            box.dataset.same = fb.t("fb.notes.history.text-same", "The text didn't change.");
            return box;
        }
        let ops;
        if (x.length * y.length > 4_000_000) {
            ops = [...x.map((l) => ["-", l]), ...y.map((l) => ["+", l])];
        } else {
            const n = x.length, m = y.length;
            const dp = Array.from({ length: n + 1 }, () => new Uint32Array(m + 1));
            for (let i = n - 1; i >= 0; i--)
                for (let j = m - 1; j >= 0; j--)
                    dp[i][j] = x[i] === y[j] ? dp[i + 1][j + 1] + 1 : Math.max(dp[i + 1][j], dp[i][j + 1]);
            ops = [];
            let i = 0, j = 0;
            while (i < n && j < m) {
                if (x[i] === y[j]) { ops.push([" ", x[i]]); i++; j++; }
                else if (dp[i + 1][j] >= dp[i][j + 1]) ops.push(["-", x[i++]]);
                else ops.push(["+", y[j++]]);
            }
            while (i < n) ops.push(["-", x[i++]]);
            while (j < m) ops.push(["+", y[j++]]);
        }
        const near = ops.map((op, k) => op[0] !== " "
            || ops.slice(Math.max(0, k - 2), k + 3).some((o) => o[0] !== " "));
        for (let k = 0; k < ops.length; k++) {
            if (!near[k]) {
                let run = 0;
                while (k + run < ops.length && !near[k + run]) run++;
                const fold = document.createElement("div");
                fold.className = "fb-diff-fold";
                fold.textContent = run === 1 ? fb.t("fb.notes.history.same-1", "1 unchanged line")
                    : fb.t("fb.notes.history.same", "{n} unchanged lines", { n: run });
                box.append(fold);
                k += run - 1;
                continue;
            }
            const [sign, text] = ops[k];
            const line = document.createElement("div");
            line.className = "fb-diff-line" + (sign === "-" ? " del" : sign === "+" ? " add" : "");
            line.dataset.sign = sign === "-" ? "−" : sign;
            line.textContent = text || " ";
            box.append(line);
        }
        return box;
    }

    fb.noteHistory = { open, diffView };
})();
