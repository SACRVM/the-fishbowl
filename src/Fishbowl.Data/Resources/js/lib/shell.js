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
    });

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
