/**
 * Fishbowl — SPA shell wiring around the kit's <sac-nav id="fb-nav">.
 *
 * Loaded last (after every view has registered its route):
 *   - renders fb.toolbar items as light-DOM .nav-icon-btn buttons in the
 *     nav's toolbar slot, so the kit's overflow "…" menu picks them up on a
 *     narrow screen
 *   - fills the account menu (avatar → Profile / Log out)
 *   - keeps the phone ribbon's title on the active view's name
 *   - applies the user's UI language (fb.i18n) and repaints on a switch
 *   - mounts the router into #app-root
 */
(function () {
    const nav = document.getElementById("fb-nav");

    // --- View toolbar -----------------------------------------------------
    // Item shape (unchanged from the fb-nav era): { icon, title, onClick,
    // active?, disabled? }. Views may mutate items in place and call
    // fb.toolbar._nav.renderToolbar() to repaint cheaply. An item with `menu`
    // is a kit menu instead: menu = [{ action, label, icon?, color?, checked? }
    // or "-" for a separator], onSelect(action) takes the pick (the kit's "…"
    // folds it on a phone like the account menu).
    const toolbarEl = document.getElementById("fb-view-toolbar");
    function toolbarMenu(item) {
        const menu = document.createElement("sac-menu");
        menu.className = "fb-toolbar-menu";
        if (item.id) menu.id = item.id;
        const trigger = document.createElement("button");
        trigger.slot = "trigger";
        trigger.type = "button";
        trigger.className = "nav-icon-btn";
        trigger.title = item.title || "";
        trigger.setAttribute("aria-label", item.title || item.icon || "");
        const icon = document.createElement("sac-icon");
        icon.setAttribute("name", item.icon);
        trigger.appendChild(icon);
        menu.appendChild(trigger);
        for (const entry of item.menu) {
            if (entry === "-") { menu.appendChild(document.createElement("hr")); continue; }
            const b = document.createElement("button");
            b.dataset.action = entry.action;
            b.innerHTML = `<sac-icon name="${entry.icon || "check"}"></sac-icon><span></span>`;
            b.querySelector("span").textContent = entry.label;
            // The menu forces the item's colour, not its icon's (as in the
            // workspace switcher); the ✓ says what is on.
            if (entry.color) b.querySelector("sac-icon").style.color = entry.color;
            if (entry.checked) {
                b.setAttribute("aria-checked", "true");
                b.insertAdjacentHTML("beforeend", `<sac-icon class="fb-context-check" name="check"></sac-icon>`);
            }
            menu.appendChild(b);
        }
        menu.addEventListener("sac:select", (e) => item.onSelect?.(e.detail.action));
        return menu;
    }
    fb.toolbar._nav = {
        renderToolbar() {
            const items = fb.toolbar._items || [];
            toolbarEl.replaceChildren(...items.map((item) => {
                if (item.menu) return toolbarMenu(item);
                const btn = document.createElement("button");
                btn.type = "button";
                btn.className = "nav-icon-btn" + (item.active ? " active" : "");
                if (item.id) btn.id = item.id;
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

    // --- Which app you are in ---------------------------------------------
    // The ribbon names the active page after the brand — the kit's app-icon
    // and app-name, in accent, like SACRVM Desktop's "SACRVM DESKTOP  COLOR
    // BUCKET"; a phone's ribbon shows that icon and name. The hub keeps the
    // brand.
    function syncTitle() {
        const current = sac.router.currentResource();
        // A prefix route (Files, Tables) names every address below it too.
        const route = sac.router.routes().find((r) => r.hash === current || (r.prefix && current.startsWith(r.hash + "/")));
        if (route && current !== "#/") {
            nav.setAttribute("app-name", route.label.toUpperCase());
            if (route.icon) nav.setAttribute("app-icon", route.icon);
            else nav.removeAttribute("app-icon");
        } else {
            nav.removeAttribute("app-name");
            nav.removeAttribute("app-icon");
        }
    }
    window.addEventListener("hashchange", syncTitle);

    // --- Home in the burger -------------------------------------------------
    // The kit's panel lists every route but "#/" (Home is only its host jump,
    // with a HOST group). Ours is the panel's first line: the desktop of the
    // active workspace.
    const homeLink = document.getElementById("fb-home-link");
    function syncHome() {
        if (!homeLink) return;
        homeLink.href = sac.scope.hashFor("#/");
        homeLink.classList.toggle("active", sac.router.currentResource() === "#/");
    }
    window.addEventListener("sac:scope-changed", syncHome);
    window.addEventListener("hashchange", syncHome);
    syncHome();

    // The burger lists every app the desktop has: pages and built-in window
    // apps are router entries (fb.desktop); the installed apps of the active
    // workspace come and go with it, so they are rows of ours right under
    // Home, styled like it (fb:desktop-entries from fb.desktop.syncCommands).
    window.addEventListener("fb:desktop-entries", (ev) => {
        const nav = document.getElementById("fb-nav");
        if (!nav || !homeLink) return;
        nav.querySelectorAll(":scope > a.fb-window-link").forEach((a) => a.remove());
        const rows = (ev.detail?.entries || []).filter((e) => e.app).map((e) => {
            const a = document.createElement("a");
            a.slot = "panel";
            a.className = "fb-home-link fb-window-link";
            a.href = "#";
            a.dataset.key = e.key;
            a.setAttribute("data-nav-close", "");
            // The icon and name are the app's (an author's app.json): never markup.
            const icon = document.createElement("sac-icon");
            icon.setAttribute("name", e.icon || "cube");
            const label = document.createElement("span");
            label.textContent = e.name;
            a.append(icon, label);
            a.addEventListener("click", (c) => {
                c.preventDefault();
                fb.desktopApps.open(e.app.id);
            });
            return a;
        });
        homeLink.after(...rows);
    });

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

    // The kit's own accents, read before anything is set inline on :root —
    // what a workspace without a colour falls back to.
    const kitTokens = (() => {
        const cs = getComputedStyle(document.documentElement);
        return { accent: cs.getPropertyValue("--accent").trim(), warm: cs.getPropertyValue("--accent-warm").trim() };
    })();

    /** The accents a workspace wears ("personal" | "space:<slug>"), as CSS
     *  values — for a part of the page that shows another workspace than the
     *  active one (Files' right pane). Same rule as applyAccents. */
    function accentsFor(workspace) {
        const slug = typeof workspace === "string" && workspace.startsWith("space:") ? workspace.slice(6) : null;
        const spaceColor = slug ? spaces?.find(s => s.slug === slug)?.color : null;
        const accent = isSlot(spaceColor) ? spaceColor : personalAccent;
        return {
            accent: accent ? paletteVar(accent) : kitTokens.accent,
            warm: isSlot(spaceColor) ? paletteVar(spaceColor) : kitTokens.warm,
        };
    }

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
        pill.querySelector(".fb-context-label").textContent = inSpace ? (space?.name || ctx.slug) : fb.t("fb.shell.personal", "Personal");

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
        const nodes = [item("ctx:user", "user", fb.t("fb.shell.personal", "Personal"), !inSpace), document.createElement("hr")];
        if (spaces?.length) {
            for (const s of spaces) nodes.push(item(`ctx:space:${s.slug}`, "users", s.name || s.slug, inSpace && s.slug === ctx.slug, s.color));
        } else if (spaces) {
            const empty = document.createElement("span");
            empty.className = "fb-context-empty";
            empty.textContent = fb.t("fb.shell.no-spaces", "No spaces yet");
            nodes.push(empty);
        }
        nodes.push(document.createElement("hr"), item("manage-spaces", "settings", fb.t("fb.shell.manage-spaces", "Manage spaces…"), false));

        for (const el of [...switcher.children]) if (el.getAttribute("slot") !== "trigger") el.remove();
        switcher.append(...nodes);
        applyAccents();
    }

    switcher?.addEventListener("sac:select", (e) => {
        const action = e.detail.action || "";
        if (action === "ctx:user") sac.scope.set({ type: "root" });
        else if (action.startsWith("ctx:space:")) sac.scope.set({ type: "scoped", slug: action.slice("ctx:space:".length) });
        else if (action === "manage-spaces") fb.windowApps.open("spaces");
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
        applyLanguage(me.language);
        fb.vault?.setAutoLock?.(me.vaultAutoLockMinutes);
        const display = me.name || me.email || fb.t("fb.shell.user", "User");
        avatar.setAttribute("name", display);
        if (me.avatarUrl) avatar.setAttribute("src", me.avatarUrl);
        account.querySelector('[slot="trigger"]').title = display;
        account.hidden = false;
        messagesBtn.hidden = false;
        refreshUnread();
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
    function labelSearch() {
        searchBtn.title = fb.t("fb.shell.search", "Search and commands ({key})", { key: paletteKey });
        searchBtn.setAttribute("aria-label", searchBtn.title);
    }
    labelSearch();
    searchBtn.addEventListener("click", () => sac.palette?.open());

    // --- Notifications (internally: messages) -------------------------------
    // The bell opens the Notifications window; its badge is the unread count, kept fresh
    // on every change (fb.api fires fb:messages-changed), on navigation and
    // once a minute (an admin sees a new request without reloading).
    const messagesBtn = document.getElementById("fb-messages-btn");
    messagesBtn.addEventListener("click", () => fb.windowApps.open("messages"));
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
            messagesBtn.title = fb.t("fb.shell.messages-unread", "Notifications — {n} unread", { n });
        } else {
            badge?.remove();
            messagesBtn.title = fb.t("fb.shell.messages", "Notifications");
        }
        messagesBtn.setAttribute("aria-label", messagesBtn.title);
    }
    window.addEventListener("fb:messages-changed", refreshUnread);
    window.addEventListener("hashchange", refreshUnread);
    setInterval(refreshUnread, 60_000);

    account.addEventListener("sac:select", (e) => {
        if (e.detail.action === "profile") openProfile();
        if (e.detail.action === "your-data") fb.yourData.open();
        if (e.detail.action === "logout")  logout();
        if (e.detail.action === "lock-secrets") {
            fb.vault?.lock().then(() => window.sac?.toast?.(fb.t("fb.shell.secrets-locked", "Secrets locked."), { kind: "success" }));
        }
        if (e.detail.action === "unlock-secrets") {
            // Cancelling the dialog is a normal answer, not an error.
            fb.vault?.ensureUnlocked().catch(() => {});
        }
        if (e.detail.action?.startsWith("theme:")) setTheme(e.detail.action.slice(6));
    });

    // Light / dark: the kit's flag (sac.apps.theme — <html data-theme> plus
    // localStorage "sac-theme", per browser; theme.js applies it before the
    // first paint). Three items in their own group above "Log out"; kit menus
    // force item colours, so the current one wears a check instead of its icon.
    const THEMES = [
        ["dark", "fb-theme-dark", "fb.shell.theme-dark", "Dark"],
        ["light", "fb-theme-light", "fb.shell.theme-light", "Light"],
        ["auto", "fb-theme-auto", "fb.shell.theme-auto", "Like the system"],
    ];
    const themeItems = THEMES.map(([mode, icon, key, label]) => {
        const b = document.createElement("button");
        b.dataset.action = "theme:" + mode;
        b.dataset.icon = icon;
        b.innerHTML = `<sac-icon name="${icon}"></sac-icon> <span data-t="${key}">${label}</span>`;
        fb.i18n.apply(b);
        return b;
    });
    account.querySelector("hr")?.before(document.createElement("hr"), ...themeItems);
    function syncThemeItems() {
        const current = sac.apps?.theme?.get() ?? "dark";
        for (const b of themeItems) {
            const on = b.dataset.action === "theme:" + current;
            b.querySelector("sac-icon").setAttribute("name", on ? "check" : b.dataset.icon);
        }
    }
    function setTheme(mode) {
        try { sac.apps?.theme?.set(mode); } catch (err) { /* storage refused: applies to this page only */ }
        syncThemeItems();
    }
    syncThemeItems();

    // The vault pair: "Lock secrets" while unlocked, "Unlock secrets" while
    // locked, neither while there is no vault yet (nothing to unlock) or in a
    // space (secrets are personal). Both live right after "Profile", above
    // the separator, and toggle `hidden` (kit >= 2.19 honours it).
    const vaultItem = (action, icon, key, label) => {
        const b = document.createElement("button");
        b.id = "fb-" + action + "-item";
        b.dataset.action = action;
        b.hidden = true;
        b.innerHTML = `<sac-icon name="${icon}"></sac-icon> <span data-t="${key}">${label}</span>`;
        fb.i18n.apply(b);
        return b;
    };
    const lockItem = vaultItem("lock-secrets", "lock", "fb.shell.lock-secrets", "Lock secrets");
    const unlockItem = vaultItem("unlock-secrets", "unlock", "fb.shell.unlock-secrets", "Unlock secrets");
    account.querySelector('[data-action="your-data"]')?.after(lockItem, unlockItem);
    async function syncVaultItems() {
        const s = await (fb.vault?.status?.() ?? { available: false });
        lockItem.hidden = !(s.available && s.unlocked);
        unlockItem.hidden = !(s.available && !s.unlocked && s.initialized);
    }
    window.addEventListener("fb:vault-changed", syncVaultItems);
    window.addEventListener("sac:scope-changed", syncVaultItems);
    syncVaultItems();

    function openProfile() {
        let win = document.getElementById("fb-profile-window");
        if (!win) {
            win = document.createElement("sac-window");
            win.setAttribute("escape-closes", "");
            win.id = "fb-profile-window";
            win.classList.add("fb-window");
            win.setAttribute("title", fb.t("fb.profile.title", "Profile"));
            win.setAttribute("width", "380px");
            win.setAttribute("height", "auto");
            win.setAttribute("top", "70px");
            win.setAttribute("left", "calc(100vw - 400px)");
            win.setAttribute("controls", "close");
            document.body.appendChild(win);
        }
        renderProfile(win);
        requestAnimationFrame(() => win.open());
    }

    // The profile's content; re-rendered in place on a language switch.
    function renderProfile(win) {
        win.setAttribute("title", fb.t("fb.profile.title", "Profile"));
        const joined = user?.createdAt ? fb.format.date(user.createdAt) : "—";
        const mode = sac.lang?.mode?.() || "auto";
        const languages = [
            ["auto", fb.t("fb.profile.language-auto", "Automatic ({language})", { language: sac.lang?.name?.(detectLanguage()) || "English" })],
            // Each language in its own words.
            ...(sac.lang?.available?.() || ["en"]).map((c) => [c, sac.lang.name(c)]),
        ];
        win.innerHTML = `
            <div class="fb-profile">
                <sac-avatar style="--avatar-size: 72px"></sac-avatar>
                <div class="fb-profile-name"></div>
                <div class="fb-profile-email"></div>
                <dl>
                    <div><dt>${fb.t("fb.profile.joined", "Joined")}</dt><dd>${joined}</dd></div>
                    <div><dt>${fb.t("fb.profile.user-id", "User ID")}</dt><dd class="fb-profile-id"></dd></div>
                </dl>
                <div class="fb-profile-accent">
                    <div class="fb-profile-label">${fb.t("fb.profile.accent", "Accent colour")}</div>
                    <sac-swatch-grid selectable columns="11"></sac-swatch-grid>
                </div>
                <div class="fb-profile-accent">
                    <label class="fb-profile-label" for="fb-language">${fb.t("fb.profile.language", "Language")}</label>
                    <span class="select"><select id="fb-language" class="fb-profile-select">
                        ${languages.map(([v]) => `<option value="${v}"${v === mode ? " selected" : ""}></option>`).join("")}
                    </select></span>
                </div>
                <div class="fb-profile-accent">
                    <label class="fb-profile-label" for="fb-date-format">${fb.t("fb.profile.date-format", "Date &amp; time format")}</label>
                    <span class="select"><select id="fb-date-format" class="fb-profile-select">
                        ${fb.format.NAMES.map((n) => `<option value="${n}"${n === fb.format.name ? " selected" : ""}>${fb.format.example(n)}</option>`).join("")}
                    </select></span>
                </div>
            </div>`;
        // Language names come from Intl — set as text, never as markup.
        win.querySelectorAll("#fb-language option").forEach((o, i) => { o.textContent = languages[i][1]; });
        win.querySelector("#fb-language").addEventListener("change", async (e) => {
            const before = user?.language ?? null;
            const next = e.target.value === "auto" ? null : e.target.value;
            applyLanguage(next);
            try {
                await fb.api.me.update({ language: next });
                if (user) user.language = next;
            } catch (err) {
                console.warn("[fb-shell] language save failed:", err?.message || err);
                applyLanguage(before);
                window.sac?.toast?.(fb.t("fb.profile.language-failed", "Couldn't save the language."), { kind: "error" });
            }
        });
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
                window.sac?.toast?.(fb.t("fb.profile.date-format-failed", "Couldn't save the date format."), { kind: "error" });
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
                window.sac?.toast?.(fb.t("fb.profile.accent-failed", "Couldn't save the accent colour."), { kind: "error" });
            }
        });
        // User-sourced strings go in via textContent / attributes only.
        const pic = win.querySelector("sac-avatar");
        pic.setAttribute("name", user?.name || user?.email || "");
        if (user?.avatarUrl) pic.setAttribute("src", user.avatarUrl);
        win.querySelector(".fb-profile-name").textContent  = user?.name || fb.t("fb.profile.unnamed", "Unnamed");
        win.querySelector(".fb-profile-email").textContent = user?.email || "";
        win.querySelector(".fb-profile-id").textContent    = (user?.id || "").slice(0, 8) + "…";
    }

    // UI language: a code, or null for automatic (the browser's language
    // when there is a table for it). sac.lang persists the choice in
    // localStorage too, for pages that load before /me. A real change
    // repaints through onLanguage below.
    function applyLanguage(code) {
        sac.lang?.set?.(code || "auto");
    }
    // What "automatic" resolves to here, whatever is chosen now.
    function detectLanguage() {
        const tables = sac.lang?.available?.() || ["en"];
        for (const p of navigator.languages?.length ? navigator.languages : [navigator.language || "en"]) {
            const c = String(p).toLowerCase().split(/[-_]/)[0];
            if (tables.includes(c)) return c;
        }
        return "en";
    }

    // A language switch: route labels (burger, phone title), the shell's own
    // strings, the palette, then the view itself — remounted, like a date
    // format change, so everything on screen speaks the new language.
    function onLanguage() {
        fb.i18n.relabelRoutes();
        fb.i18n.apply(document);
        syncTitle();
        labelSearch();
        renderSwitcher();
        refreshUnread();
        fb.desktop?.syncCommands?.();
        const win = document.getElementById("fb-profile-window");
        if (win) renderProfile(win);
        remountView();
    }
    sac.lang?.onChange?.(onLanguage);

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
    fb.accents = { swatches: accentSwatches, slotOf: swatchSlot, cssVar: paletteVar, forWorkspace: accentsFor };

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
        // The language this page starts in (the last choice, or automatic):
        // labels in place before the first view mounts.
        fb.i18n.relabelRoutes();
        fb.i18n.apply(document);
        // Registered before sac.router's own hashchange listener (added
        // inside mount), so the toolbar is empty by the time the incoming
        // view's connectedCallback runs — an outgoing view never has to
        // clean up its buttons. Keep the order.
        window.addEventListener("hashchange", () => fb.toolbar.clear());
        sac.router.mount("#app-root");
        syncTitle();
        noticeInvite();
    });

    // /invite/<token> sends a signed-in account back here with ?invite=<why>
    // when the link can't be used — say why once, then drop it from the URL.
    function noticeInvite() {
        const why = new URLSearchParams(window.location.search).get("invite");
        if (!why) return;
        history.replaceState(null, "", window.location.pathname + window.location.hash);
        const text = {
            "invite-used": fb.t("fb.spaces.invite-used", "This invitation link was used already. Ask for a new one."),
            "invite-expired": fb.t("fb.spaces.invite-expired", "This invitation link has expired. Ask for a new one."),
        }[why] || fb.t("fb.spaces.invite-unknown", "This invitation link doesn't work. Ask for a new one.");
        window.sac?.toast?.(text, { kind: "error" });
    }
})();
