/**
 * fb.mailCompose — writing a mail (spec 2026-10-07-mail-design, decision 12)
 * in the Mail app's right pane, where the conversation was: From (one of the
 * workspace's accounts), To, Cc / Bcc, Subject, the text in <sac-md-editor>
 * (markdown — it goes out as text/plain plus the rendered text/html), the
 * files (one "Attach…": the kit's picker over the workspace's Files, which
 * also offers this device; or dropped onto the editor), and for a reply or a
 * forward the original under it, as it will be quoted.
 *
 * The draft lives on the server and is saved as it is typed (600 ms, like
 * Contacts); one nothing was written into is dropped when it is left.
 * Recipients are one plain text field each, addresses separated by commas —
 * never chips: an address stays text you can correct in place (decided
 * 2026-10-08).
 *
 *   const c = fb.mailCompose.mount(pane, { api, ws, draft, accounts, original, onClose });
 *   await c.leave();   // saves; an untouched draft goes
 *   onClose({ sent: { id, threadId } } | { discarded: true })
 */
(function () {
    const t = (k, f, v) => fb.t(`fb.mail.${k}`, f, v);

    /** "a@x.org, \"Doe, J\" <j@y.org>" → two: commas inside quotes or <…> don't split. */
    function splitAddresses(text) {
        const out = [];
        let cur = "", quoted = false, angle = false;
        for (const ch of String(text || "")) {
            if (ch === '"') quoted = !quoted;
            else if (ch === "<" && !quoted) angle = true;
            else if (ch === ">" && !quoted) angle = false;
            if ((ch === "," || ch === ";") && !quoted && !angle) { if (cur.trim()) out.push(cur.trim()); cur = ""; continue; }
            cur += ch;
        }
        if (cur.trim()) out.push(cur.trim());
        return out;
    }

    const TITLES = {
        "new": ["title-new", "New mail"],
        "reply": ["title-reply", "Reply"],
        "reply-all": ["title-reply-all", "Reply all"],
        "forward": ["title-forward", "Forward"],
    };

    function mount(pane, { api, ws, draft, accounts, original, onClose }) {
        let d = draft;
        let dirty = false;
        let timer = null;
        let saving = Promise.resolve();
        let done = false;
        const pending = {};

        pane.innerHTML = `
            <form class="mc" novalidate>
                <div class="mc-head"><h2></h2></div>
                <div class="mc-fields">
                    <label for="mc-from">${t("from", "From")}</label>
                    <sac-select id="mc-from"></sac-select>
                    <label for="mc-to">${t("to-label", "To")}</label>
                    <div class="mc-to">
                        <input id="mc-to" data-f="to" autocomplete="off" spellcheck="false"/>
                        <button type="button" class="mc-more" id="mc-more">${t("cc-bcc", "Cc / Bcc")}</button>
                    </div>
                    <label for="mc-cc" class="mc-cc" hidden>Cc</label>
                    <input id="mc-cc" data-f="cc" class="mc-cc" autocomplete="off" spellcheck="false" hidden/>
                    <label for="mc-bcc" class="mc-cc" hidden>Bcc</label>
                    <input id="mc-bcc" data-f="bcc" class="mc-cc" autocomplete="off" spellcheck="false" hidden/>
                    <label for="mc-subject">${t("subject", "Subject")}</label>
                    <input id="mc-subject" data-f="subject"/>
                </div>
                <div class="mc-body">
                    <sac-md-editor id="mc-text" placeholder="${t("write", "Write your message")}"></sac-md-editor>
                    <sac-drop-zone overlay multiple label="${t("drop", "Drop to attach")}" hint=""></sac-drop-zone>
                </div>
                <div class="mc-files" id="mc-files"></div>
                <div class="mc-quote" id="mc-quote" hidden><div class="mc-quote-head"></div><div class="mc-quote-text"></div></div>
                <div class="mc-actions toolbar">
                    <span class="mc-state" id="mc-state"></span>
                    <button type="button" class="btn" id="mc-attach"><sac-icon name="attachment"></sac-icon> ${t("attach", "Attach…")}</button>
                    <button type="button" class="btn" id="mc-discard">${t("discard", "Discard")}</button>
                    <button type="submit" class="btn primary" id="mc-send"><sac-icon name="fb-send"></sac-icon> ${t("send", "Send")}</button>
                </div>
            </form>`;

        const form = pane.querySelector("form");
        const [titleKey, titleText] = TITLES[d.kind] || TITLES.new;
        form.querySelector("h2").textContent = t(titleKey, titleText);

        const from = form.querySelector("#mc-from");
        from.options = accounts.map((a) => ({ value: a.id, label: a.displayName ? `${a.displayName} <${a.address}>` : a.address, icon: "mail" }));
        from.value = d.accountId || "";
        from.addEventListener("sac:change", (e) => change("accountId", e.detail.value || null));

        const field = (name) => form.querySelector(`[data-f="${name}"]`);
        field("to").value = (d.to || []).join(", ");
        field("cc").value = (d.cc || []).join(", ");
        field("bcc").value = (d.bcc || []).join(", ");
        field("subject").value = d.subject || "";
        const showCc = (on) => form.querySelectorAll(".mc-cc").forEach((el) => { el.hidden = !on; });
        showCc((d.cc || []).length > 0 || (d.bcc || []).length > 0);
        form.querySelector("#mc-more").addEventListener("click", () => { showCc(true); field("cc").focus(); });
        for (const name of ["to", "cc", "bcc"])
            field(name).addEventListener("input", () => change(name, splitAddresses(field(name).value)));
        field("subject").addEventListener("input", () => change("subject", field("subject").value));

        const editor = form.querySelector("#mc-text");
        editor.value = d.body || "";
        editor.addEventListener("sac:input", (e) => change("body", e.detail.value));

        // The original, as it will be quoted (a reply) or carried (a forward).
        if (original) {
            const quote = form.querySelector("#mc-quote");
            const sender = original.fromName ? `${original.fromName} <${original.fromAddress}>` : (original.fromAddress || "");
            const date = `${fb.format.date(new Date(original.sentAt))} ${fb.format.time(new Date(original.sentAt))}`;
            quote.querySelector(".mc-quote-head").textContent = d.kind === "forward"
                ? t("quote-forward", "Forwarded message — from {sender}, {date}:", { sender, date })
                : t("quote-reply", "On {date}, {sender} wrote:", { sender, date });
            quote.querySelector(".mc-quote-text").textContent = original.bodyText || "";
            quote.hidden = false;
        }

        // ---- saving: what changed, 600 ms after the last keystroke ----
        function change(key, value) {
            dirty = true;
            pending[key] = value;
            clearTimeout(timer);
            timer = setTimeout(save, 600);
            state("");
        }
        function save() {
            clearTimeout(timer);
            if (!Object.keys(pending).length) return saving;
            const body = { ...pending };
            for (const k of Object.keys(pending)) delete pending[k];
            saving = saving.then(async () => {
                try {
                    d = await api.updateDraft(d.id, body);
                    state(t("saved", "Draft saved"));
                } catch (err) {
                    // Kept for the next try; what was typed since wins.
                    for (const [k, v] of Object.entries(body)) if (!(k in pending)) pending[k] = v;
                    state(fb.errors.text(err, t("save-failed", "The draft couldn't be saved.")), true);
                }
            });
            return saving;
        }
        function state(text, error = false) {
            const el = form.querySelector("#mc-state");
            el.textContent = text;
            el.classList.toggle("is-error", error);
        }

        // ---- files ----
        function paintFiles() {
            const box = form.querySelector("#mc-files");
            box.replaceChildren(...(d.files || []).map((f) => {
                const el = document.createElement("span");
                el.className = "mc-file";
                el.innerHTML = `<sac-icon name="attachment"></sac-icon><span class="mc-file-name"></span><span class="muted"></span>
                    <button type="button" class="icon-btn"><sac-icon name="close"></sac-icon></button>`;
                el.querySelector(".mc-file-name").textContent = f.name;
                el.querySelector(".muted").textContent = fb.format.bytes(f.size);
                const remove = el.querySelector("button");
                remove.title = t("remove-file", "Remove");
                remove.setAttribute("aria-label", `${remove.title}: ${f.name}`);
                remove.addEventListener("click", async () => {
                    try {
                        await api.removeDraftFile(d.id, f.index);
                        d.files = d.files.filter((x) => x.index !== f.index);
                        dirty = true;
                        paintFiles();
                    } catch (err) { sac.toast?.(fb.errors.text(err, t("failed", "That didn't work.")), { kind: "error" }); }
                });
                return el;
            }));
            box.hidden = !box.childElementCount;
        }
        async function upload(file, name) {
            state(t("attaching", "Attaching {name}…", { name: name || file.name }));
            try {
                const added = await api.addDraftFile(d.id, file, name);
                d.files = [...(d.files || []), added];
                dirty = true;
                paintFiles();
                state("");
            } catch (err) {
                state("");
                sac.toast?.(fb.errors.text(err, t("attach-failed", "The file couldn't be attached.")), { kind: "error" });
            }
        }
        paintFiles();
        form.querySelector("#mc-attach").addEventListener("click", async () => {
            const picker = sac.files.virtual({ store: fb.filesStore({ workspace: ws }), label: fb.t("fb.apps.picker-label", "Files"), readonly: true });
            let picked;
            try { picked = await picker.open({ multiple: true, title: t("attach-title", "Attach files") }); }
            catch (err) { console.warn("[mail-compose] picker failed:", err); return; }
            for (const ref of [].concat(picked || [])) await upload(ref.file, ref.name);
        });
        form.querySelector("sac-drop-zone").addEventListener("sac:files", async (e) => {
            for (const file of e.detail.files) await upload(file, file.name);
        });

        // ---- send / discard ----
        form.addEventListener("submit", async (e) => {
            e.preventDefault();
            const button = form.querySelector("#mc-send");
            button.disabled = true;
            await save();
            try {
                const sent = await api.send(d.id, {
                    timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
                    language: sac.lang?.get?.(),
                });
                done = true;
                onClose?.({ sent });
            } catch (err) {
                button.disabled = false;
                sac.toast?.(fb.errors.text(err, t("send-failed", "The mail couldn't be sent.")), { kind: "error" });
            }
        });
        form.querySelector("#mc-discard").addEventListener("click", async () => {
            const written = dirty && (editor.value.trim() || (d.files || []).length);
            if (written) {
                const answer = await sac.dialog.confirm({
                    title: t("discard-title", "Discard this draft?"),
                    message: t("discard-message", "What you wrote is gone."),
                    buttons: [
                        { action: "cancel", label: fb.t("fb.common.cancel", "Cancel"), kind: "default" },
                        { action: "discard", label: t("discard", "Discard"), kind: "destructive" },
                    ],
                });
                if (answer !== "discard") return;
            }
            clearTimeout(timer);
            done = true;
            try { await api.deleteDraft(d.id); } catch { /* gone already */ }
            onClose?.({ discarded: true });
        });

        if (!field("to").value) field("to").focus();
        else requestAnimationFrame(() => editor.focus?.());

        return {
            get id() { return d.id; },
            /** Saves what is pending; a draft nothing was written into goes. */
            async leave() {
                if (done) return;
                done = true;
                if (!dirty) { try { await api.deleteDraft(d.id); } catch { /* gone */ } return; }
                await save();
            },
        };
    }

    window.fb = window.fb || {};
    fb.mailCompose = { mount, splitAddresses };
})();
