/*
 * fb.spaceMembers — who is in a space, and letting people in (space-apps
 * spec, phase 1). Built from kit parts, no component of its own: a
 * <sac-window> listing the members with their role; for an Admin or the
 * Owner a "…" <sac-menu> per member (the roles up to their own, Remove),
 * "Invite…" (a <sac-dialog>: role + how long → a one-time link shown once)
 * and the open invitations with Withdraw. Everyone but the owner can leave.
 *
 *   fb.spaceMembers.open(space, { onChanged })
 *     space     — { slug, name }
 *     onChanged — called after a change that alters the caller's own list
 *                 of spaces (leaving).
 */
(function () {
    const ROLES = ["reader", "member", "designer", "admin", "owner"];
    const rank = (role) => ROLES.indexOf(role);

    function roleLabel(role) {
        return {
            reader: fb.t("fb.spaces.role-reader", "reader"),
            member: fb.t("fb.spaces.role-member", "member"),
            designer: fb.t("fb.spaces.role-designer", "designer"),
            admin: fb.t("fb.spaces.role-admin", "admin"),
            owner: fb.t("fb.spaces.role-owner", "owner"),
        }[role] || role;
    }

    let win = null;
    let current = null;   // { space, onChanged }

    function ensureWindow() {
        if (win) return win;
        win = document.createElement("sac-window");
        win.id = "fb-space-members";
        win.classList.add("fb-window");
        win.setAttribute("width", "560px");
        win.setAttribute("height", "520px");
        win.setAttribute("top", "80px");
        win.setAttribute("left", "calc(50vw - 280px)");
        win.setAttribute("controls", "close");
        win.innerHTML = `
            <div class="fb-members">
                <div class="toolbar fb-members-bar"></div>
                <div class="fb-members-list"></div>
                <sac-section class="fb-members-invites" hidden><div class="fb-members-invite-list"></div></sac-section>
            </div>`;
        document.body.appendChild(win);
        return win;
    }

    async function open(space, opts = {}) {
        current = { space, onChanged: opts.onChanged || null };
        const w = ensureWindow();
        w.setAttribute("title", fb.t("fb.spaces.members-title", "Members — {name}", { name: space.name }));
        w.querySelector(".fb-members-invites").setAttribute("title", fb.t("fb.spaces.invites-open", "Open invitations"));
        await paint();
        requestAnimationFrame(() => w.open());
    }

    async function paint() {
        const { space } = current;
        const list = win.querySelector(".fb-members-list");
        const bar = win.querySelector(".fb-members-bar");
        let data;
        try {
            data = await fb.api.spaces.members(space.slug);
        } catch (err) {
            console.warn("[fb.spaceMembers] members unavailable:", err);
            list.innerHTML = `<p class="muted"></p>`;
            list.firstChild.textContent = fb.t("fb.spaces.members-failed", "Couldn't load the members.");
            bar.replaceChildren();
            return;
        }
        const mine = data.role;
        bar.replaceChildren();
        if (data.canManage) {
            const invite = document.createElement("button");
            invite.type = "button";
            invite.className = "btn primary fb-members-invite";
            invite.textContent = fb.t("fb.spaces.invite", "Invite…");
            invite.addEventListener("click", () => askInvite(mine));
            bar.appendChild(invite);
        }
        list.replaceChildren(...data.items.map((m) => memberRow(m, mine, data.canManage)));
        await paintInvites(data.canManage);
    }

    function memberRow(m, mine, canManage) {
        const row = document.createElement("div");
        row.className = "fb-row fb-member-row";
        row.dataset.user = m.userId;
        row.innerHTML = `
            <sac-icon name="user"></sac-icon>
            <div class="fb-row-info">
                <p class="fb-row-name"></p>
                <div class="fb-row-meta"><sac-chip class="role"></sac-chip></div>
            </div>`;
        row.querySelector(".fb-row-name").textContent =
            (m.name || fb.t("fb.messages.someone", "Someone")) + (m.you ? " " + fb.t("fb.spaces.you-suffix", "(you)") : "");
        row.querySelector("sac-chip").setAttribute("label", roleLabel(m.role));

        const manageable = canManage && !m.you && m.role !== "owner" && rank(m.role) <= rank(mine);
        const leavable = m.you && m.role !== "owner";
        if (manageable || leavable) row.appendChild(menuFor(m, mine, manageable));
        return row;
    }

    function menuFor(m, mine, manageable) {
        const menu = document.createElement("sac-menu");
        const trigger = document.createElement("button");
        trigger.slot = "trigger";
        trigger.type = "button";
        trigger.className = "icon-btn";
        trigger.title = m.you ? fb.t("fb.spaces.leave", "Leave space") : fb.t("fb.spaces.change-role", "Change role");
        trigger.setAttribute("aria-label", trigger.title);
        const more = document.createElement("sac-icon");
        more.setAttribute("name", "more");
        trigger.appendChild(more);
        menu.appendChild(trigger);

        const item = (action, label, icon, danger) => {
            const b = document.createElement("button");
            b.dataset.action = action;
            if (danger) b.dataset.danger = "";
            const i = document.createElement("sac-icon");
            i.setAttribute("name", icon);
            b.append(i, document.createTextNode(" " + label));
            menu.appendChild(b);
        };
        if (manageable) {
            // Kit menus force item colours, so the current role wears a check.
            for (const role of ROLES.slice(0, rank(mine) + 1).filter((r) => r !== "owner"))
                item("role:" + role, roleLabel(role), role === m.role ? "check" : "user");
            menu.appendChild(document.createElement("hr"));
            item("remove", fb.t("fb.spaces.remove-member", "Remove from the space"), "trash", true);
        } else {
            item("leave", fb.t("fb.spaces.leave", "Leave space"), "close", true);
        }
        menu.addEventListener("sac:select", (e) => {
            const action = e.detail.action || "";
            if (action.startsWith("role:")) setRole(m, action.slice(5));
            else if (action === "remove") remove(m);
            else if (action === "leave") leave(m);
        });
        return menu;
    }

    async function setRole(m, role) {
        if (role === m.role) return;
        try {
            await fb.api.spaces.setRole(current.space.slug, m.userId, role);
            window.sac?.toast?.(fb.t("fb.spaces.role-changed", "{who} is now {role}.", { who: m.name || "", role: roleLabel(role) }), { kind: "success" });
        } catch (err) {
            window.sac?.toast?.(fb.errors.text(err, fb.t("fb.spaces.member-action-failed", "That didn't work.")), { kind: "error" });
        }
        await paint();
    }

    async function remove(m) {
        const who = m.name || fb.t("fb.messages.someone", "Someone");
        const result = await sac.dialog.confirm({
            title: fb.t("fb.spaces.remove-member-title", "Remove {who} from \"{space}\"?", { who, space: current.space.name }),
            message: fb.t("fb.spaces.remove-member-body", "{who} no longer sees this space's data. What {who} wrote stays.", { who }),
            buttons: [
                { action: "cancel", label: fb.t("fb.spaces.cancel", "Cancel"), kind: "default" },
                { action: "remove", label: fb.t("fb.spaces.remove", "Remove"), kind: "destructive" },
            ],
        });
        if (result !== "remove") return;
        try {
            await fb.api.spaces.removeMember(current.space.slug, m.userId);
        } catch (err) {
            window.sac?.toast?.(fb.errors.text(err, fb.t("fb.spaces.member-action-failed", "That didn't work.")), { kind: "error" });
        }
        await paint();
    }

    async function leave(m) {
        const space = current.space;
        const result = await sac.dialog.confirm({
            title: fb.t("fb.spaces.leave-title", "Leave \"{space}\"?", { space: space.name }),
            message: fb.t("fb.spaces.leave-body", "You won't see this space's data until someone invites you again."),
            buttons: [
                { action: "cancel", label: fb.t("fb.spaces.cancel", "Cancel"), kind: "default" },
                { action: "leave", label: fb.t("fb.spaces.leave", "Leave space"), kind: "destructive" },
            ],
        });
        if (result !== "leave") return;
        try {
            await fb.api.spaces.leave(space.slug, m.userId);
        } catch (err) {
            window.sac?.toast?.(fb.errors.text(err, fb.t("fb.spaces.member-action-failed", "That didn't work.")), { kind: "error" });
            return;
        }
        win.close?.();
        window.sac?.toast?.(fb.t("fb.spaces.left", "You left \"{space}\".", { space: space.name }), { kind: "success" });
        // Standing in the space just left: back to the personal workspace.
        const scope = sac.scope.get();
        if (scope.type === "scoped" && scope.slug === space.slug) sac.scope.set({ type: "root" });
        current.onChanged?.();
    }

    async function paintInvites(canManage) {
        const section = win.querySelector(".fb-members-invites");
        const list = win.querySelector(".fb-members-invite-list");
        let invites = [];
        if (canManage) {
            try { invites = await fb.api.spaces.invites.list(current.space.slug); }
            catch (err) { console.warn("[fb.spaceMembers] invites unavailable:", err); }
        }
        section.hidden = invites.length === 0;
        list.replaceChildren(...invites.map((i) => {
            const row = document.createElement("div");
            row.className = "fb-row fb-invite-row";
            row.innerHTML = `
                <sac-icon name="link"></sac-icon>
                <div class="fb-row-info"><p class="fb-row-name"></p></div>
                <button type="button" class="icon-btn danger">
                    <sac-icon name="trash"></sac-icon>
                </button>`;
            const vars = { role: roleLabel(i.role), when: fb.format.dateTime(i.expiresAt), who: i.createdBy || "" };
            row.querySelector(".fb-row-name").textContent = i.createdBy
                ? fb.t("fb.spaces.invite-meta-by", "{role} · valid until {when} · by {who}", vars)
                : fb.t("fb.spaces.invite-meta", "{role} · valid until {when}", vars);
            const revoke = row.querySelector("button");
            revoke.title = fb.t("fb.spaces.revoke", "Withdraw the invitation");
            revoke.setAttribute("aria-label", revoke.title);
            revoke.addEventListener("click", async () => {
                try { await fb.api.spaces.invites.revoke(current.space.slug, i.id); }
                catch (err) { window.sac?.toast?.(fb.errors.text(err, fb.t("fb.spaces.member-action-failed", "That didn't work.")), { kind: "error" }); }
                await paint();
            });
            return row;
        }));
    }

    // Role (up to the caller's own, never owner) and how long the link
    // lasts; then the link, shown this one time.
    function askInvite(mine) {
        const dlg = document.createElement("sac-dialog");
        dlg.id = "fb-invite-dialog";
        dlg.setAttribute("title", fb.t("fb.spaces.invite-title", "Invite to \"{space}\"", { space: current.space.name }));
        dlg.style.setProperty("--dialog-width", "440px");
        dlg.buttons = [
            { action: "cancel", label: fb.t("fb.spaces.cancel", "Cancel"), kind: "default" },
            { action: "create", label: fb.t("fb.spaces.invite-create", "Create link"), kind: "primary" },
        ];
        const roles = ROLES.slice(0, rank(mine) + 1).filter((r) => r !== "owner");
        dlg.innerHTML = `
            <div class="fb-invite-form">
                <label class="fb-invite-field"><span>${fb.t("fb.spaces.invite-role", "Role")}</span>
                    <span class="select"><select name="role">${roles.map((r) =>
                        `<option value="${r}"${r === "member" ? " selected" : ""}></option>`).join("")}</select></span></label>
                <label class="fb-invite-field"><span>${fb.t("fb.spaces.invite-days", "Valid for")}</span>
                    <span class="select"><select name="days">${[1, 7, 14, 30].map((n) =>
                        `<option value="${n}"${n === 7 ? " selected" : ""}></option>`).join("")}</select></span></label>
                <p class="fb-invite-hint"></p>
            </div>`;
        dlg.querySelectorAll("select[name=role] option").forEach((o) => { o.textContent = roleLabel(o.value); });
        dlg.querySelectorAll("select[name=days] option").forEach((o) => {
            o.textContent = o.value === "1" ? fb.t("fb.spaces.invite-days-1", "1 day") : fb.t("fb.spaces.invite-days-n", "{n} days", { n: o.value });
        });
        dlg.querySelector(".fb-invite-hint").textContent = fb.t("fb.spaces.invite-hint",
            "A link for one person. Send it any way you like — whoever opens it and signs in is in, even when new accounts otherwise wait for approval.");
        dlg.addEventListener("sac:action", async (e) => {
            setTimeout(() => dlg.remove(), 120);
            if (e.detail?.action !== "create") return;
            const role = dlg.querySelector("select[name=role]").value;
            const days = Number(dlg.querySelector("select[name=days]").value);
            try {
                const invite = await fb.api.spaces.invites.create(current.space.slug, { role, days });
                showLink(invite);
            } catch (err) {
                window.sac?.toast?.(fb.errors.text(err, fb.t("fb.spaces.invite-failed", "Couldn't create the invitation.")), { kind: "error" });
            }
            await paint();
        }, { once: true });
        document.body.appendChild(dlg);
        setTimeout(() => dlg.open(), 0);
    }

    function showLink(invite) {
        const dlg = document.createElement("sac-dialog");
        dlg.id = "fb-invite-link";
        dlg.setAttribute("title", fb.t("fb.spaces.invite-link-title", "Invitation link"));
        dlg.style.setProperty("--dialog-width", "520px");
        dlg.buttons = [{ action: "close", label: fb.t("fb.spaces.close", "Close"), kind: "primary" }];
        dlg.innerHTML = `
            <div class="fb-invite-form">
                <p class="fb-invite-hint"></p>
                <div class="fb-invite-link-row">
                    <input type="text" readonly class="fb-invite-url">
                    <button type="button" class="btn fb-invite-copy"><sac-icon name="copy"></sac-icon> <span></span></button>
                </div>
            </div>`;
        dlg.querySelector(".fb-invite-hint").textContent = fb.t("fb.spaces.invite-link-body",
            "Copy the link now — it is shown only this once. Valid until {when}, for one person, as {role}.",
            { when: fb.format.dateTime(invite.expiresAt), role: roleLabel(invite.role) });
        const input = dlg.querySelector("input");
        input.value = invite.url;
        input.addEventListener("focus", () => input.select());
        dlg.querySelector(".fb-invite-copy span").textContent = fb.t("fb.spaces.copy", "Copy");
        dlg.querySelector(".fb-invite-copy").addEventListener("click", async () => {
            try {
                await navigator.clipboard.writeText(invite.url);
                window.sac?.toast?.(fb.t("fb.spaces.copied", "Link copied."), { kind: "success" });
            } catch {
                input.focus();
                input.select();
            }
        });
        dlg.addEventListener("sac:action", () => setTimeout(() => dlg.remove(), 120), { once: true });
        document.body.appendChild(dlg);
        setTimeout(() => dlg.open(), 0);
    }

    fb.spaceMembers = { open, roleLabel };
})();
