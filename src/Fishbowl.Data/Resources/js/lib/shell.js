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
        const current = sac.router.currentResource();
        const route = sac.router.routes().find((r) => r.hash === current);
        if (route && current !== "#/") nav.setAttribute("compact-title", route.label);
        else nav.removeAttribute("compact-title");
    }
    window.addEventListener("hashchange", syncTitle);

    // --- Accent colours -----------------------------------------------------
    // Both are kit palette slot names ("teal"…) or null for the kit default.
    // The personal accent is --accent. Inside a space that has a colour,
    // the space's colour takes over the whole UI: --accent and --accent-warm
    // (the switcher pill / shared-data tint) both become it, so you see at
    // a glance whose data you are in. Set inline on :root, so every derived
    // token (--accent-tint, --accent-fill…) follows. The personal accent is
    // remembered per browser so the first paint doesn't flash the default.
    const ACCENT_KEY = "fb.accent";
    const isSlot = (v) => typeof v === "string" && fb.tags.SLOTS.includes(v);
    const paletteVar = (slot) => `var(--palette-${slot})`;
    let personalAccent = null;
    try { const v = localStorage.getItem(ACCENT_KEY); if (isSlot(v)) personalAccent = v; } catch { /* storage blocked */ }

    function applyAccents() {
        const root = document.documentElement.style;
        const ctx = sac.scope.get();
        const spaceColor = ctx.type === "scoped" ? spaces?.find(s => s.slug === ctx.slug)?.color : null;
        const accent = isSlot(spaceColor) ? spaceColor : personalAccent;
        if (accent) root.setProperty("--accent", paletteVar(accent));
        else root.removeProperty("--accent");
        if (isSlot(spaceColor)) root.setProperty("--accent-warm", paletteVar(spaceColor));
        else root.removeProperty("--accent-warm");
    }

    function setPersonalAccent(slot) {
        personalAccent = isSlot(slot) ? slot : null;
        try {
            if (personalAccent) localStorage.setItem(ACCENT_KEY, personalAccent);
            else localStorage.removeItem(ACCENT_KEY);
        } catch { /* storage blocked */ }
        applyAccents();
    }

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
        const ctx = sac.scope.get();
        const inSpace = ctx.type === "scoped";
        const space = inSpace ? spaces?.find(s => s.slug === ctx.slug) : null;

        const pill = switcher.querySelector('[slot="trigger"]');
        pill.classList.toggle("space", inSpace);
        pill.querySelector("sac-icon").setAttribute("name", inSpace ? "users" : "user");
        pill.querySelector(".fb-context-label").textContent = inSpace ? (space?.name || ctx.slug) : "Personal";

        const item = (action, icon, label, active, color) => {
            const b = document.createElement("button");
            b.dataset.action = action;
            b.innerHTML = `<sac-icon name="${icon}"></sac-icon><span></span>`;
            b.querySelector("span").textContent = label;
            // The menu forces the item's colour, not its icon's: a space's
            // own colour shows on its icon.
            if (color) b.querySelector("sac-icon").style.color = paletteVar(color);
            if (active) {
                b.setAttribute("aria-current", "true");
                b.insertAdjacentHTML("beforeend", `<sac-icon class="fb-context-check" name="check"></sac-icon>`);
            }
            return b;
        };
        const nodes = [item("ctx:user", "user", "Personal", !inSpace), document.createElement("hr")];
        if (spaces?.length) {
            for (const s of spaces) nodes.push(item(`ctx:space:${s.slug}`, "users", s.name || s.slug, inSpace && s.slug === ctx.slug, s.color));
        } else if (spaces) {
            const empty = document.createElement("span");
            empty.className = "fb-context-empty";
            empty.textContent = "No spaces yet";
            nodes.push(empty);
        }
        nodes.push(document.createElement("hr"), item("manage-spaces", "settings", "Manage spaces…", false));

        for (const el of [...switcher.children]) if (el.getAttribute("slot") !== "trigger") el.remove();
        switcher.append(...nodes);
        applyAccents();
    }

    switcher?.addEventListener("sac:select", (e) => {
        const action = e.detail.action || "";
        if (action === "ctx:user") sac.scope.set({ type: "root" });
        else if (action.startsWith("ctx:space:")) sac.scope.set({ type: "scoped", slug: action.slice("ctx:space:".length) });
        else if (action === "manage-spaces") sac.router.navigate("#/spaces");
    });
    window.addEventListener("sac:scope-changed", renderSwitcher);

    // Reloaded on every create/delete (fb.api fires fb:spaces-changed), so a
    // new space is in the menu at once. If the active space is gone, fall
    // back to the personal workspace instead of a scope that 404s.
    function loadSpaces() {
        return fb.api.spaces.list()
            .then((list) => {
                spaces = list || [];
                const ctx = sac.scope.get();
                if (ctx.type === "scoped" && !spaces.some(s => s.slug === ctx.slug)) sac.scope.set({ type: "root" });
            })
            .catch((err) => { spaces = spaces || []; console.warn("[fb-shell] spaces load failed:", err?.message || err); })
            .finally(renderSwitcher);
    }
    window.addEventListener("fb:spaces-changed", loadSpaces);
    loadSpaces();
    renderSwitcher();

    // --- Account menu -----------------------------------------------------
    const account = document.getElementById("fb-account");
    const avatar  = document.getElementById("fb-account-avatar");
    let user = null;

    fb.api.me.get().then((me) => {
        user = me;
        setPersonalAccent(me.accent);
        applyDateFormat(me.dateFormat);
        fb.vault?.setAutoLock?.(me.vaultAutoLockMinutes);
        const display = me.name || me.email || "User";
        avatar.setAttribute("name", display);
        if (me.avatarUrl) avatar.setAttribute("src", me.avatarUrl);
        account.querySelector('[slot="trigger"]').title = display;
        account.hidden = false;
        messagesBtn.hidden = false;
        refreshUnread();
        // The admin apps exist only for admins: registered here, never for
        // anyone else, so they are in no nav, palette or route table.
        if (me.isAdmin) fb.accounts.registerAdminRoutes();
    }).catch((err) => {
        // 401 → already redirected by api.js. Anything else degrades
        // silently: the shell works, just without the account menu.
        console.warn("[fb-shell] failed to load user:", err?.message || err);
    });

    // --- Palette -----------------------------------------------------------
    // The search button opens the kit's Ctrl/⌘K palette (fb.desktop fills
    // it); its tooltip names the key the way this platform spells it.
    const searchBtn = document.getElementById("fb-search-btn");
    const paletteKey = sac.hotkeys?.format ? sac.hotkeys.format("mod+k") : "Ctrl+K";
    searchBtn.title = `Search and commands (${paletteKey})`;
    searchBtn.setAttribute("aria-label", searchBtn.title);
    searchBtn.addEventListener("click", () => sac.palette?.open());

    // --- Messages ----------------------------------------------------------
    // The envelope opens #/messages; its badge is the unread count, kept fresh
    // on every change (fb.api fires fb:messages-changed), on navigation and
    // once a minute (an admin sees a new request without reloading).
    const messagesBtn = document.getElementById("fb-messages-btn");
    messagesBtn.addEventListener("click", () => sac.router.navigate("#/messages"));
    async function refreshUnread() {
        if (messagesBtn.hidden) return;
        let n = 0;
        try { n = (await fb.api.messages.unreadCount())?.unread || 0; }
        catch { return; }
        let badge = messagesBtn.querySelector(".badge-count");
        if (n > 0) {
            if (!badge) {
                badge = document.createElement("span");
                badge.className = "badge-count";
                messagesBtn.appendChild(badge);
            }
            badge.textContent = n > 99 ? "99+" : String(n);
            messagesBtn.title = `Messages — ${n} unread`;
        } else {
            badge?.remove();
            messagesBtn.title = "Messages";
        }
        messagesBtn.setAttribute("aria-label", messagesBtn.title);
    }
    window.addEventListener("fb:messages-changed", refreshUnread);
    window.addEventListener("hashchange", refreshUnread);
    setInterval(refreshUnread, 60_000);

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
    window.addEventListener("sac:scope-changed", syncVaultItems);
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
        const joined = user?.createdAt ? fb.format.date(user.createdAt) : "—";
        win.innerHTML = `
            <div class="fb-profile">
                <sac-avatar style="--avatar-size: 72px"></sac-avatar>
                <div class="fb-profile-name"></div>
                <div class="fb-profile-email"></div>
                <dl>
                    <div><dt>Joined</dt><dd>${joined}</dd></div>
                    <div><dt>User ID</dt><dd class="fb-profile-id"></dd></div>
                </dl>
                <div class="fb-profile-accent">
                    <div class="fb-profile-label">Accent colour</div>
                    <sac-swatch-grid selectable columns="11"></sac-swatch-grid>
                </div>
                <div class="fb-profile-accent">
                    <label class="fb-profile-label" for="fb-date-format">Date &amp; time format</label>
                    <span class="select"><select id="fb-date-format" class="fb-profile-select">
                        ${fb.format.NAMES.map((n) => `<option value="${n}"${n === fb.format.name ? " selected" : ""}>${fb.format.example(n)}</option>`).join("")}
                    </select></span>
                </div>
            </div>`;
        win.querySelector("#fb-date-format").addEventListener("change", async (e) => {
            const before = fb.format.name;
            applyDateFormat(e.target.value);
            try {
                await fb.api.me.update({ dateFormat: fb.format.name });
                if (user) user.dateFormat = fb.format.name;
            } catch (err) {
                console.warn("[fb-shell] date format save failed:", err?.message || err);
                applyDateFormat(before);
                e.target.value = before;
                window.sac?.toast?.("Couldn't save the date format.", { kind: "error" });
            }
        });
        const grid = win.querySelector("sac-swatch-grid");
        grid.colors = accentSwatches(personalAccent);
        grid.addEventListener("sac:change", async (e) => {
            const slot = swatchSlot(e.detail.value);
            const before = personalAccent;
            setPersonalAccent(slot);
            try {
                await fb.api.me.update({ accent: slot });
                if (user) user.accent = slot;
            } catch (err) {
                console.warn("[fb-shell] accent save failed:", err?.message || err);
                setPersonalAccent(before);
                grid.colors = accentSwatches(before);
                window.sac?.toast?.("Couldn't save the accent colour.", { kind: "error" });
            }
        });
        // User-sourced strings go in via textContent / attributes only.
        const pic = win.querySelector("sac-avatar");
        pic.setAttribute("name", user?.name || user?.email || "");
        if (user?.avatarUrl) pic.setAttribute("src", user.avatarUrl);
        win.querySelector(".fb-profile-name").textContent  = user?.name || "Unnamed";
        win.querySelector(".fb-profile-email").textContent = user?.email || "";
        win.querySelector(".fb-profile-id").textContent    = (user?.id || "").slice(0, 8) + "…";
        requestAnimationFrame(() => win.open());
    }

    // Date & time format: fb.format does the formatting; the setting lives on
    // the user. A change repaints the current view (a fresh element, as a
    // navigation would) so every date on screen follows.
    function applyDateFormat(next) {
        const before = fb.format.name;
        fb.format.set(next);
        if (fb.format.name !== before) remountView();
    }
    function remountView() {
        const root = document.getElementById("app-root");
        const view = root?.firstElementChild;
        if (view) root.replaceChildren(document.createElement(view.localName));
    }

    // Swatches for a palette-slot picker: "Default" (no colour) plus every
    // kit palette slot. Shared with the spaces settings via fb.accents.
    function accentSwatches(selected) {
        return [
            { value: "transparent", label: "Default", selected: !selected },
            ...fb.tags.SLOTS.map((slot) => ({ value: paletteVar(slot), label: slot, selected: slot === selected })),
        ];
    }
    function swatchSlot(value) {
        const m = /^var\(--palette-([a-z]+)\)$/.exec(value || "");
        return m && isSlot(m[1]) ? m[1] : null;
    }
    fb.accents = { swatches: accentSwatches, slotOf: swatchSlot, cssVar: paletteVar };

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
        // Registered before sac.router's own hashchange listener (added
        // inside mount), so the toolbar is empty by the time the incoming
        // view's connectedCallback runs — an outgoing view never has to
        // clean up its buttons. Keep the order.
        window.addEventListener("hashchange", () => fb.toolbar.clear());
        sac.router.mount("#app-root");
        syncTitle();
    });
})();
