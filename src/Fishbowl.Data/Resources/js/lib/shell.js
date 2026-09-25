/**
 * Fishbowl — SPA shell wiring around the kit's <sac-nav id="fb-nav">.
 *
 * Loaded last (after every view has registered its route):
 *   - renders fb.toolbar items as light-DOM .nav-icon-btn buttons in the
 *     nav's toolbar slot, so the kit's overflow "…" menu picks them up on a
 *     narrow screen
 *   - fills the account menu (avatar → Profile / Log out)
 *   - keeps the phone ribbon's title on the active view's name
 *   - mounts the router into #app-root
 */
(function () {
    const nav = document.getElementById("fb-nav");

    // --- View toolbar -----------------------------------------------------
    // Item shape (unchanged from the fb-nav era): { icon, title, onClick,
    // active?, disabled? }. Views may mutate items in place and call
    // fb.toolbar._nav.renderToolbar() to repaint cheaply.
    const toolbarEl = document.getElementById("fb-view-toolbar");
    fb.toolbar._nav = {
        renderToolbar() {
            const items = fb.toolbar._items || [];
            toolbarEl.replaceChildren(...items.map((item) => {
                const btn = document.createElement("button");
                btn.type = "button";
                btn.className = "nav-icon-btn" + (item.active ? " active" : "");
                btn.title = item.title || "";
                btn.setAttribute("aria-label", item.title || item.icon || "");
                btn.disabled = !!item.disabled;
                const icon = document.createElement("sac-icon");
                icon.setAttribute("name", item.icon);
                btn.appendChild(icon);
                btn.addEventListener("click", () => { if (!item.disabled) item.onClick?.(); });
                return btn;
            }));
        }
    };
    fb.toolbar._nav.renderToolbar();

    // --- Phone ribbon title ---------------------------------------------
    // On compact the ribbon shows one name; give it the active view's label
    // (the hub keeps the brand).
    function syncTitle() {
        const current = fb.router.currentResource();
        const route = fb.router.routes().find((r) => r.hash === current);
        if (route && current !== "#/") nav.setAttribute("compact-title", route.label);
        else nav.removeAttribute("compact-title");
    }
    window.addEventListener("hashchange", syncTitle);

    // --- Workspace switcher -------------------------------------------------
    // The kit menu in the nav's `context` slot (it survives toolbar repaints).
    // The pill names the active workspace and turns --accent-warm inside a
    // space, so edits to shared data are unmissable. Items: Personal, the
    // user's spaces (a check marks the active one — the kit fixes item
    // colours), and "Manage spaces…".
    const switcher = document.getElementById("fb-context");
    let spaces = null; // null until loaded

    function renderSwitcher() {
        if (!switcher) return;
        const ctx = fb.context.get();
        const inSpace = ctx.type === "space";
        const space = inSpace ? spaces?.find(s => s.slug === ctx.slug) : null;

        const pill = switcher.querySelector('[slot="trigger"]');
        pill.classList.toggle("space", inSpace);
        pill.querySelector("sac-icon").setAttribute("name", inSpace ? "users" : "user");
        pill.querySelector(".fb-context-label").textContent = inSpace ? (space?.name || ctx.slug) : "Personal";

        const item = (action, icon, label, active) => {
            const b = document.createElement("button");
            b.dataset.action = action;
            b.innerHTML = `<sac-icon name="${icon}"></sac-icon><span></span>`;
            b.querySelector("span").textContent = label;
            if (active) {
                b.setAttribute("aria-current", "true");
                b.insertAdjacentHTML("beforeend", `<sac-icon class="fb-context-check" name="check"></sac-icon>`);
            }
            return b;
        };
        const nodes = [item("ctx:user", "user", "Personal", !inSpace), document.createElement("hr")];
        if (spaces?.length) {
            for (const s of spaces) nodes.push(item(`ctx:space:${s.slug}`, "users", s.name || s.slug, inSpace && s.slug === ctx.slug));
        } else if (spaces) {
            const empty = document.createElement("span");
            empty.className = "fb-context-empty";
            empty.textContent = "No spaces yet";
            nodes.push(empty);
        }
        nodes.push(document.createElement("hr"), item("manage-spaces", "settings", "Manage spaces…", false));

        for (const el of [...switcher.children]) if (el.getAttribute("slot") !== "trigger") el.remove();
        switcher.append(...nodes);
    }

    switcher?.addEventListener("sac:select", (e) => {
        const action = e.detail.action || "";
        if (action === "ctx:user") fb.context.set({ type: "user" });
        else if (action.startsWith("ctx:space:")) fb.context.set({ type: "space", slug: action.slice("ctx:space:".length) });
        else if (action === "manage-spaces") fb.router.navigate("#/spaces");
    });
    window.addEventListener("fb:context-changed", renderSwitcher);
    fb.api.spaces.list()
        .then((list) => { spaces = list; })
        .catch((err) => { spaces = []; console.warn("[fb-shell] spaces load failed:", err?.message || err); })
        .finally(renderSwitcher);
    renderSwitcher();

    // --- Account menu -----------------------------------------------------
    const account = document.getElementById("fb-account");
    const avatar  = document.getElementById("fb-account-avatar");
    let user = null;

    fb.api.me.get().then((me) => {
        user = me;
        const display = me.name || me.email || "User";
        avatar.setAttribute("name", display);
        if (me.avatarUrl) avatar.setAttribute("src", me.avatarUrl);
        account.querySelector('[slot="trigger"]').title = display;
        account.hidden = false;
    }).catch((err) => {
        // 401 → already redirected by api.js. Anything else degrades
        // silently: the shell works, just without the account menu.
        console.warn("[fb-shell] failed to load user:", err?.message || err);
    });

    account.addEventListener("sac:select", (e) => {
        if (e.detail.action === "profile") openProfile();
        if (e.detail.action === "logout")  logout();
        if (e.detail.action === "lock-secrets") {
            fb.vault?.lock().then(() => window.sac?.toast?.("Secrets locked.", { kind: "success" }));
        }
        if (e.detail.action === "unlock-secrets") {
            // Cancelling the dialog is a normal answer, not an error.
            fb.vault?.ensureUnlocked().catch(() => {});
        }
    });

    // One vault item: "Lock secrets" while unlocked, "Unlock secrets" while
    // locked, absent while there is no vault yet (nothing to unlock) or in a
    // space (secrets are personal). Added and removed rather than `hidden`:
    // <sac-menu> styles its items `display: flex !important`, which beats
    // the hidden attribute, so a hidden item would still show.
    const vaultItem = document.createElement("button");
    vaultItem.id = "fb-vault-item";
    async function syncVaultItems() {
        const s = await (fb.vault?.status?.() ?? { available: false });
        const mode = !s.available ? null
            : s.unlocked ? "lock"
            : s.initialized ? "unlock"
            : null;
        if (!mode) { vaultItem.remove(); return; }
        vaultItem.dataset.action = mode === "lock" ? "lock-secrets" : "unlock-secrets";
        vaultItem.innerHTML = mode === "lock"
            ? '<sac-icon name="lock"></sac-icon> Lock secrets'
            : '<sac-icon name="unlock"></sac-icon> Unlock secrets';
        // Right after "Profile", above the separator.
        if (!vaultItem.isConnected) account.querySelector('[data-action="profile"]')?.after(vaultItem);
    }
    window.addEventListener("fb:vault-changed", syncVaultItems);
    window.addEventListener("fb:context-changed", syncVaultItems);
    syncVaultItems();

    function openProfile() {
        let win = document.getElementById("fb-profile-window");
        if (!win) {
            win = document.createElement("sac-window");
            win.id = "fb-profile-window";
            win.setAttribute("title", "Profile");
            win.setAttribute("width", "380px");
            win.setAttribute("height", "auto");
            win.setAttribute("top", "70px");
            win.setAttribute("left", "calc(100vw - 400px)");
            win.setAttribute("controls", "close");
            document.body.appendChild(win);
        }
        const joined = user?.createdAt
            ? new Date(user.createdAt).toLocaleDateString(undefined, { year: "numeric", month: "long", day: "numeric" })
            : "—";
        win.innerHTML = `
            <div class="fb-profile">
                <sac-avatar style="--avatar-size: 72px"></sac-avatar>
                <div class="fb-profile-name"></div>
                <div class="fb-profile-email"></div>
                <dl>
                    <div><dt>Joined</dt><dd>${joined}</dd></div>
                    <div><dt>User ID</dt><dd class="fb-profile-id"></dd></div>
                </dl>
            </div>`;
        // User-sourced strings go in via textContent / attributes only.
        const pic = win.querySelector("sac-avatar");
        pic.setAttribute("name", user?.name || user?.email || "");
        if (user?.avatarUrl) pic.setAttribute("src", user.avatarUrl);
        win.querySelector(".fb-profile-name").textContent  = user?.name || "Unnamed";
        win.querySelector(".fb-profile-email").textContent = user?.email || "";
        win.querySelector(".fb-profile-id").textContent    = (user?.id || "").slice(0, 8) + "…";
        requestAnimationFrame(() => win.open());
    }

    async function logout() {
        try {
            await fb.api.auth.logout();
        } catch (err) {
            console.warn("[fb-shell] logout error:", err?.message || err);
        }
        window.location.href = "/login";
    }

    // --- Mount ------------------------------------------------------------
    // Deferred scripts have all run by DOMContentLoaded, so every view has
    // registered its route by now.
    window.addEventListener("DOMContentLoaded", () => {
        fb.router.mount("#app-root");
        syncTitle();
    });
})();
