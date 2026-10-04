/*
 * fb.windowApps — the apps that open as a window over the desktop, not as a
 * page: lists, settings and administration you open, finish and close.
 * Pages (their own route, in the burger) are only the apps that need the
 * whole screen: Notes, Todos, Calendar, Files, Tables.
 *
 *   fb.windowApps.open(name, { tab })   messages | trash | spaces | apps |
 *                                       keys | secrets | users | system
 *   fb.windowApps.toolbar(view, items)  a window's own actions ({ id?, icon,
 *                                       title, onClick }), the fb.toolbar
 *                                       shape — outside a window it is
 *                                       fb.toolbar.set(items)
 *
 * One kit <sac-window> per app (a phone maximizes it); opening it again
 * brings it back with fresh content. The view inside is the same light-DOM
 * element a page would be, marked `in-window` (app.css drops the page frame
 * and its heading — the window's title says it). The personal-only apps
 * close when a space becomes active.
 */
(function () {
    // width/height in px, capped to the viewport.
    const APPS = {
        messages: { tag: "fb-messages-view",        key: "fb.messages.title",    title: "Messages", w: 720,  h: 640 },
        trash:    { tag: "fb-trash-view",           key: "fb.trash.title",       title: "Trash",    w: 820,  h: 680 },
        spaces:   { tag: "fb-spaces-settings-view", key: "fb.spaces.title",      title: "Spaces",   w: 860,  h: 720 },
        apps:     { tag: "fb-apps-settings-view",   key: "fb.apps.page-title",   title: "Apps",     w: 820,  h: 680 },
        keys:     { tag: "fb-keys-settings-view",   key: "fb.keys.title",        title: "API keys", w: 860,  h: 720 },
        secrets:  { tag: "fb-secrets-settings-view", key: "fb.secrets.title",    title: "Secrets",  w: 820,  h: 720, personal: true },
        users:    { tag: "fb-users-admin-view",     key: "fb.admin.users-title", title: "Users",    w: 1000, h: 760, personal: true },
        system:   { tag: "fb-system-view",          key: "fb.admin.system-title", title: "System",  w: 1100, h: 820, personal: true },
    };

    function open(name, opts = {}) {
        const app = APPS[name];
        if (!app) return null;
        const id = `fb-win-${name}`;
        let win = document.getElementById(id);
        if (!win) {
            win = document.createElement("sac-window");
            win.id = id;
            win.dataset.app = name;
            win.setAttribute("width", `min(${app.w}px, 94vw)`);
            win.setAttribute("height", `min(${app.h}px, 86vh)`);
            win.setAttribute("top", "60px");
            win.setAttribute("left", `max(3vw, calc(50vw - ${app.w / 2}px))`);
            win.setAttribute("controls", "max close");
            win.classList.add("fb-window");
            document.body.appendChild(win);
        }
        win.setAttribute("title", fb.t(app.key, app.title));
        const bar = document.createElement("div");
        bar.className = "toolbar wa-bar";
        bar.hidden = true;
        const view = document.createElement(app.tag);
        view.classList.add("in-window");
        if (opts.tab) view.setAttribute("tab", opts.tab);
        win.replaceChildren(bar, view);
        requestAnimationFrame(() => win.open());
        return win;
    }

    function toolbar(view, items) {
        const bar = view?.closest?.("sac-window")?.querySelector(":scope > .wa-bar");
        if (!bar) { fb.toolbar.set(items); return; }
        bar.replaceChildren(...(items || []).map((item) => {
            const b = document.createElement("button");
            b.type = "button";
            b.className = "btn";
            if (item.id) b.id = item.id;
            b.title = item.title || "";
            b.innerHTML = `<sac-icon name="${item.icon || "plus"}"></sac-icon><span></span>`;
            b.querySelector("span").textContent = item.title || "";
            b.addEventListener("click", () => item.onClick?.());
            return b;
        }));
        bar.hidden = !bar.childElementCount;
    }

    // A space became active: the personal-only windows go.
    window.addEventListener("sac:scope-changed", () => {
        if (sac.scope.get().type !== "scoped") return;
        for (const [name, app] of Object.entries(APPS)) {
            if (app.personal) document.getElementById(`fb-win-${name}`)?.close?.();
        }
    });

    fb.windowApps = { open, toolbar, names: Object.keys(APPS) };
})();
