/**
 * <fb-nav app-name="NOTES">
 *   <button slot="toolbar">...</button>
 * </fb-nav>
 *
 * Fixed 50px ribbon with glassmorphic background + slide-out 300px panel.
 * Panel nav list is computed from fb.router.routes() and re-rendered whenever
 * a new route registers (fb:route-registered event) or the hash changes.
 *
 * Attributes:
 *   app-name — uppercase text after "THE FISHBOWL ·" in the brand area.
 *
 * Slots:
 *   toolbar — right-aligned content inside the ribbon.
 */
class FbNav extends HTMLElement {
    constructor() {
        super();
        this.attachShadow({ mode: "open" });
        this.isOpen = false;
        this.userMenuOpen = false;
        this.user = null; // { id, name, email, avatarUrl }
    }

    connectedCallback() {
        this.render();
        this.attachPersistentHandlers();
        // Register as the fb.toolbar renderer + render any items already set.
        if (window.fb?.toolbar) {
            fb.toolbar._nav = this;
            this.renderToolbar();
        }
        // Load the current user in the background — don't block first paint.
        this._loadUser();
    }

    disconnectedCallback() {
        if (this._escHandler)      document.removeEventListener("keydown", this._escHandler);
        if (this._hashHandler)     window.removeEventListener("hashchange", this._hashHandler);
        if (this._routeHandler)    window.removeEventListener("fb:route-registered", this._routeHandler);
        if (this._contextHandler)  window.removeEventListener("fb:context-changed", this._contextHandler);
        if (this._docClickHandler) document.removeEventListener("click", this._docClickHandler, true);
        if (window.fb?.toolbar?._nav === this) fb.toolbar._nav = null;
    }

    async _loadUser() {
        try {
            this.user = await fb.api.me.get();
        } catch (err) {
            // 401 → already redirected by api.js. Anything else we silently
            // degrade: the nav still works, just without the user widget.
            console.warn("[fb-nav] failed to load user:", err?.message || err);
            return;
        }
        this._renderUserWidget();
    }

    _initials(name) {
        if (!name) return "?";
        const parts = name.trim().split(/\s+/).slice(0, 2);
        return parts.map(p => p[0]?.toUpperCase() || "").join("") || "?";
    }

    /**
     * Rewrites the .toolbar container's children from fb.toolbar._items.
     * Called by fb.toolbar.set() and on connect. Keeps the Shadow DOM
     * scoped; no light-DOM manipulation needed.
     */
    renderToolbar() {
        const container = this.shadowRoot.querySelector(".toolbar");
        if (!container) return;
        const items = (window.fb?.toolbar?._items) || [];
        container.innerHTML = items.map((item, i) => `
            <button class="toolbar-btn ${item.active ? "active" : ""}"
                    data-idx="${i}"
                    ${item.disabled ? "disabled" : ""}
                    title="${(item.title || "").replace(/"/g, "&quot;")}">
                <fb-icon name="${item.icon}"></fb-icon>
            </button>
        `).join("");
        container.querySelectorAll("button[data-idx]").forEach(btn => {
            btn.addEventListener("click", () => {
                const idx = parseInt(btn.dataset.idx, 10);
                const item = (window.fb?.toolbar?._items || [])[idx];
                if (item?.disabled) return;
                item?.onClick?.();
            });
        });
    }

    render() {
        const appName = this.getAttribute("app-name") || "";
        const routes = (fb.router?.routes() || []).filter(r => r.hash !== "#/");
        // currentResource() strips any /space/SLUG prefix so "active" state
        // highlights the right nav item in both contexts.
        const currentResource = fb.router?.currentResource?.() || fb.router?.current() || "#/";
        // Prefix each nav href with /space/SLUG when a space is active, so
        // clicking "Notes" from a space workspace keeps you in the space.
        const hrefFor = (personalHash) =>
            window.fb?.context?.hashFor ? fb.context.hashFor(personalHash) : personalHash;

        this.shadowRoot.innerHTML = `
            <style>
                :host {
                    position: fixed;
                    top: 0; left: 0; right: 0;
                    z-index: 9999;
                    --accent: #3b82f6;
                    --accent-warm: #f97316;
                }
                .ribbon {
                    height: 50px;
                    display: flex;
                    align-items: center;
                    padding: 0 1rem;
                    background: rgba(15, 23, 42, 0.85);
                    backdrop-filter: blur(12px);
                    -webkit-backdrop-filter: blur(12px);
                    border-bottom: 1px solid rgba(255, 255, 255, 0.1);
                }
                .menu-btn {
                    background: none;
                    border: none;
                    color: #f8fafc;
                    cursor: pointer;
                    padding: 0.25rem 0.5rem;
                    margin-right: 0.5rem;
                    font-size: 1.2rem;
                    border-radius: 6px;
                    display: flex;
                    align-items: center;
                }
                .menu-btn:hover { background: rgba(255,255,255,0.08); }
                .menu-btn fb-icon { --icon-size: 20px; }
                .brand-mark {
                    color: var(--accent);
                    margin-right: 0.6rem;
                    display: flex;
                    align-items: center;
                }
                .brand-mark fb-icon { --icon-size: 22px; }
                .brand {
                    font-family: 'Outfit', sans-serif;
                    font-weight: 800;
                    font-size: 0.95rem;
                    letter-spacing: 0.08em;
                    color: #f8fafc;
                    display: flex; align-items: center; gap: 0.5rem;
                    text-decoration: none;
                }
                .brand .sep { color: rgba(255,255,255,0.3); }
                .brand .app-name { color: var(--accent); }
                .spacer { flex: 1; }
                .toolbar { display: flex; gap: 0.25rem; align-items: center; }
                .context { display: flex; align-items: center; margin-right: 0.6rem; }
                .context:empty { margin-right: 0; }
                .toolbar-sep {
                    width: 1px;
                    height: 22px;
                    background: rgba(255, 255, 255, 0.1);
                    margin: 0 0.5rem;
                }

                /* --- USER WIDGET ------------------------------------------ */
                .user {
                    position: relative;
                    display: flex;
                    align-items: center;
                }
                .user-btn {
                    display: flex;
                    align-items: center;
                    gap: 0.5rem;
                    padding: 4px 8px 4px 4px;
                    background: none;
                    border: 1px solid transparent;
                    border-radius: 20px;
                    color: #f8fafc;
                    cursor: pointer;
                    font: 500 0.85rem 'Inter', sans-serif;
                    transition: background 120ms, border-color 120ms;
                }
                .user-btn:hover {
                    background: rgba(255, 255, 255, 0.06);
                    border-color: rgba(255, 255, 255, 0.12);
                }
                .user-btn.open {
                    background: rgba(255, 255, 255, 0.08);
                    border-color: rgba(255, 255, 255, 0.18);
                }
                .user-avatar {
                    width: 28px;
                    height: 28px;
                    border-radius: 50%;
                    background: linear-gradient(135deg, var(--accent) 0%, var(--accent-warm) 100%);
                    color: #fff;
                    font: 600 0.72rem 'Inter', sans-serif;
                    display: flex;
                    align-items: center;
                    justify-content: center;
                    overflow: hidden;
                    flex-shrink: 0;
                }
                .user-avatar img {
                    width: 100%;
                    height: 100%;
                    object-fit: cover;
                }
                .user-name {
                    max-width: 140px;
                    overflow: hidden;
                    text-overflow: ellipsis;
                    white-space: nowrap;
                    color: #e2e8f0;
                }
                .user-btn fb-icon { --icon-size: 14px; color: #94a3b8; }
                .user-menu {
                    position: absolute;
                    top: calc(100% + 8px);
                    right: 0;
                    min-width: 220px;
                    background: rgba(15, 23, 42, 0.92);
                    backdrop-filter: blur(16px) saturate(180%);
                    -webkit-backdrop-filter: blur(16px) saturate(180%);
                    border: 1px solid rgba(255, 255, 255, 0.12);
                    border-radius: 12px;
                    box-shadow: 0 18px 40px rgba(0, 0, 0, 0.45);
                    padding: 6px;
                    opacity: 0;
                    transform: translateY(-4px);
                    pointer-events: none;
                    transition: opacity 140ms, transform 140ms;
                    z-index: 10;
                }
                .user-menu.open {
                    opacity: 1;
                    transform: translateY(0);
                    pointer-events: auto;
                }
                .user-menu .meta {
                    padding: 8px 10px 10px;
                    border-bottom: 1px solid rgba(255, 255, 255, 0.08);
                    margin-bottom: 6px;
                }
                .user-menu .meta-name {
                    font-weight: 600;
                    font-size: 0.9rem;
                    color: #f8fafc;
                    overflow: hidden;
                    text-overflow: ellipsis;
                    white-space: nowrap;
                }
                .user-menu .meta-email {
                    margin-top: 2px;
                    font-size: 0.75rem;
                    color: #64748b;
                    overflow: hidden;
                    text-overflow: ellipsis;
                    white-space: nowrap;
                }
                .user-menu-item {
                    display: flex;
                    align-items: center;
                    gap: 0.6rem;
                    width: 100%;
                    padding: 8px 10px;
                    background: none;
                    border: none;
                    border-radius: 8px;
                    color: #cbd5e1;
                    font: 500 0.88rem 'Inter', sans-serif;
                    text-align: left;
                    cursor: pointer;
                    transition: background 120ms, color 120ms;
                }
                .user-menu-item:hover {
                    background: rgba(255, 255, 255, 0.06);
                    color: #f8fafc;
                }
                .user-menu-item.danger:hover {
                    background: rgba(239, 68, 68, 0.14);
                    color: var(--danger, #ef4444);
                }
                .user-menu-item fb-icon { --icon-size: 15px; }
                .toolbar-btn {
                    background: none;
                    border: none;
                    color: #cbd5e1;
                    padding: 5px 7px;
                    border-radius: 6px;
                    cursor: pointer;
                    display: inline-flex;
                    align-items: center;
                    transition: background 0.15s, color 0.15s;
                }
                .toolbar-btn:hover {
                    background: rgba(255, 255, 255, 0.08);
                    color: #f8fafc;
                }
                .toolbar-btn.active {
                    background: rgba(59, 130, 246, 0.18);
                    color: var(--accent);
                }
                .toolbar-btn:disabled {
                    opacity: 0.35;
                    cursor: not-allowed;
                }
                .toolbar-btn:disabled:hover {
                    background: none;
                    color: #cbd5e1;
                }
                .toolbar-btn fb-icon { --icon-size: 18px; }

                .backdrop {
                    position: fixed;
                    top: 50px; left: 0; right: 0; bottom: 0;
                    background: rgba(0,0,0,0.5);
                    opacity: 0;
                    visibility: hidden;
                    transition: opacity 0.3s, visibility 0.3s;
                    z-index: 1;
                }
                .backdrop.open { opacity: 1; visibility: visible; }

                .panel {
                    position: fixed;
                    top: 50px; left: 0; bottom: 0;
                    width: 300px;
                    background: rgba(15, 23, 42, 0.95);
                    backdrop-filter: blur(20px);
                    -webkit-backdrop-filter: blur(20px);
                    border-right: 1px solid rgba(255,255,255,0.1);
                    transform: translateX(-110%);
                    visibility: hidden;
                    transition: transform 0.5s cubic-bezier(0.77, 0, 0.175, 1), visibility 0.5s;
                    z-index: 2;
                    padding: 1.5rem 0;
                    overflow-y: auto;
                }
                .panel.open {
                    transform: translateX(0);
                    visibility: visible;
                }
                .nav-list { list-style: none; padding: 0; margin: 0; }
                .nav-item {
                    display: flex;
                    align-items: center;
                    gap: 0.75rem;
                    padding: 0.75rem 1.5rem;
                    color: #cbd5e1;
                    text-decoration: none;
                    font-size: 0.95rem;
                    cursor: pointer;
                    transition: background 0.2s;
                }
                .nav-item:hover { background: rgba(255,255,255,0.05); color: #f8fafc; }
                .nav-item.active {
                    background: rgba(59, 130, 246, 0.15);
                    border-left: 3px solid var(--accent);
                    color: var(--accent);
                    padding-left: calc(1.5rem - 3px);
                }
                .nav-item fb-icon { --icon-size: 18px; }
                .panel-empty {
                    padding: 1rem 1.5rem;
                    color: #64748b;
                    font-size: 0.85rem;
                    font-style: italic;
                }

                /* Scrollbar theme — duplicated here because the global rule
                   in app.css doesn't pierce Shadow DOM. */
                ::-webkit-scrollbar { width: 10px; height: 10px; }
                ::-webkit-scrollbar-track { background: transparent; }
                ::-webkit-scrollbar-thumb {
                    background: rgba(255, 255, 255, 0.18);
                    background-clip: content-box;
                    border: 2px solid transparent;
                    border-radius: 8px;
                }
                ::-webkit-scrollbar-thumb:hover {
                    background: rgba(255, 255, 255, 0.32);
                    background-clip: content-box;
                }
            </style>
            <nav class="ribbon">
                <button class="menu-btn" aria-label="Menu">
                    <fb-icon name="menu"></fb-icon>
                </button>
                <a class="brand" href="${hrefFor("#/")}">
                    <span class="brand-mark"><fb-icon name="fish"></fb-icon></span>
                    <span>THE FISHBOWL</span>
                    ${appName ? `<span class="sep">·</span><span class="app-name">${appName}</span>` : ``}
                </a>
                <div class="spacer"></div>
                <div class="context"><slot name="context"></slot></div>
                <div class="toolbar"><slot name="toolbar"></slot></div>
                <div class="toolbar-sep" id="user-sep" style="display:none;"></div>
                <div class="user" id="user-widget" style="display:none;">
                    <button class="user-btn" id="user-btn" aria-haspopup="menu" aria-expanded="false">
                        <span class="user-avatar" id="user-avatar"></span>
                        <span class="user-name"   id="user-name"></span>
                        <fb-icon name="chevron-down"></fb-icon>
                    </button>
                    <div class="user-menu" id="user-menu" role="menu">
                        <div class="meta">
                            <div class="meta-name"  id="user-meta-name"></div>
                            <div class="meta-email" id="user-meta-email"></div>
                        </div>
                        <button class="user-menu-item" data-action="profile" role="menuitem">
                            <fb-icon name="user"></fb-icon> <span>Profile</span>
                        </button>
                        <button class="user-menu-item danger" data-action="logout" role="menuitem">
                            <fb-icon name="log-out"></fb-icon> <span>Log out</span>
                        </button>
                    </div>
                </div>
            </nav>
            <div class="backdrop"></div>
            <aside class="panel">
                ${routes.length === 0
                    ? `<div class="panel-empty">No sections yet.</div>`
                    : `<ul class="nav-list">
                         ${routes.map(r => `
                             <li>
                                 <a class="nav-item ${r.hash === currentResource ? "active" : ""}" href="${hrefFor(r.hash)}">
                                     ${r.icon ? `<fb-icon name="${r.icon}"></fb-icon>` : ""}
                                     <span>${r.label}</span>
                                 </a>
                             </li>
                         `).join("")}
                       </ul>`}
            </aside>
        `;

        // Re-attach handlers on freshly-rendered shadow-root children.
        this.attachEphemeralHandlers();
    }

    attachEphemeralHandlers() {
        const menuBtn  = this.shadowRoot.querySelector(".menu-btn");
        const backdrop = this.shadowRoot.querySelector(".backdrop");
        const panel    = this.shadowRoot.querySelector(".panel");

        const setOpen = (open) => {
            this.isOpen = open;
            backdrop.classList.toggle("open", open);
            panel.classList.toggle("open", open);
        };
        const toggle = () => setOpen(!this.isOpen);

        menuBtn.addEventListener("click",  toggle);
        backdrop.addEventListener("click", () => setOpen(false));

        // Clicking a panel nav-item also closes the panel.
        this.shadowRoot.querySelectorAll(".nav-item").forEach(el => {
            el.addEventListener("click", () => setOpen(false));
        });

        // User widget: click toggles the dropdown; menu items dispatch.
        const userBtn  = this.shadowRoot.getElementById("user-btn");
        const userMenu = this.shadowRoot.getElementById("user-menu");
        if (userBtn && userMenu) {
            userBtn.addEventListener("click", (e) => {
                e.stopPropagation();
                this._setUserMenuOpen(!this.userMenuOpen);
            });
            userMenu.querySelectorAll(".user-menu-item").forEach(item => {
                item.addEventListener("click", () => {
                    const action = item.dataset.action;
                    this._setUserMenuOpen(false);
                    if (action === "profile") this._openProfile();
                    if (action === "logout")  this._onLogout();
                });
            });
        }

        // Re-paint the user widget (re-render wipes the shadow root, so the
        // filled-in avatar/name need reapplying).
        if (this.user) this._renderUserWidget();

        // Restore panel state after a re-render (routes changed while open).
        if (this.isOpen) setOpen(true);
        if (this.userMenuOpen) this._setUserMenuOpen(true);
    }

    _setUserMenuOpen(open) {
        this.userMenuOpen = open;
        const btn  = this.shadowRoot.getElementById("user-btn");
        const menu = this.shadowRoot.getElementById("user-menu");
        btn?.classList.toggle("open", open);
        btn?.setAttribute("aria-expanded", open ? "true" : "false");
        menu?.classList.toggle("open", open);
    }

    _renderUserWidget() {
        const widget = this.shadowRoot.getElementById("user-widget");
        const sep    = this.shadowRoot.getElementById("user-sep");
        const avatar = this.shadowRoot.getElementById("user-avatar");
        const name   = this.shadowRoot.getElementById("user-name");
        const mName  = this.shadowRoot.getElementById("user-meta-name");
        const mEmail = this.shadowRoot.getElementById("user-meta-email");
        if (!widget || !this.user) return;

        widget.style.display = "";
        if (sep) sep.style.display = "";

        const display = this.user.name || this.user.email || "User";
        name.textContent  = display;
        mName.textContent = display;
        mEmail.textContent = this.user.email || "";

        if (this.user.avatarUrl) {
            // If Google's image fails to load (CORS, expired URL, etc.) we
            // fall back to initials via onerror.
            avatar.innerHTML = `<img src="${this.user.avatarUrl}" alt="" referrerpolicy="no-referrer" />`;
            const img = avatar.querySelector("img");
            img.addEventListener("error", () => {
                avatar.textContent = this._initials(this.user.name);
            }, { once: true });
        } else {
            avatar.textContent = this._initials(this.user.name);
        }
    }

    _openProfile() {
        // Reuse the existing window if already open.
        let dlg = document.getElementById("fb-profile-window");
        if (dlg) { dlg.open(); return; }

        dlg = document.createElement("fb-window");
        dlg.id = "fb-profile-window";
        dlg.setAttribute("title", "Profile");
        dlg.setAttribute("width",  "380px");
        dlg.setAttribute("height", "auto");
        dlg.setAttribute("top",  "90px");
        dlg.setAttribute("left", "calc(100vw - 420px)");

        const joined = this.user?.createdAt
            ? new Date(this.user.createdAt).toLocaleDateString(undefined, {
                year: "numeric", month: "long", day: "numeric"
              })
            : "—";

        const avatarHtml = this.user?.avatarUrl
            ? `<img src="${this.user.avatarUrl}" alt="" referrerpolicy="no-referrer"
                   style="width:72px;height:72px;border-radius:50%;object-fit:cover;"/>`
            : `<div style="width:72px;height:72px;border-radius:50%;
                     background:linear-gradient(135deg,#3b82f6 0%,#f97316 100%);
                     color:#fff;font:700 1.4rem 'Inter',sans-serif;
                     display:flex;align-items:center;justify-content:center;">
                   ${this._initials(this.user?.name)}
               </div>`;

        dlg.innerHTML = `
            <div style="display:flex;flex-direction:column;align-items:center;text-align:center;gap:10px;">
                ${avatarHtml}
                <div style="font:700 1.1rem 'Outfit',sans-serif;color:#f8fafc;">
                    ${escapeHtml(this.user?.name || "Unnamed")}
                </div>
                <div style="color:#94a3b8;font-size:0.85rem;">
                    ${escapeHtml(this.user?.email || "")}
                </div>
                <div style="width:100%;margin-top:12px;padding-top:12px;
                            border-top:1px solid rgba(255,255,255,0.08);
                            display:grid;grid-template-columns:1fr 1fr;gap:8px;
                            font-size:0.78rem;color:#64748b;text-align:left;">
                    <div>
                        <div style="text-transform:uppercase;letter-spacing:0.08em;font-weight:600;margin-bottom:2px;">Joined</div>
                        <div style="color:#cbd5e1;">${joined}</div>
                    </div>
                    <div>
                        <div style="text-transform:uppercase;letter-spacing:0.08em;font-weight:600;margin-bottom:2px;">User ID</div>
                        <div style="color:#cbd5e1;font-family:monospace;font-size:0.72rem;word-break:break-all;">
                            ${escapeHtml((this.user?.id || "").slice(0, 8))}&hellip;
                        </div>
                    </div>
                </div>
            </div>
        `;
        document.body.appendChild(dlg);
        // Defer open() to next frame so CSS transitions trigger.
        requestAnimationFrame(() => dlg.open());
    }

    async _onLogout() {
        try {
            await fb.api.auth.logout();
        } catch (err) {
            console.warn("[fb-nav] logout error:", err?.message || err);
        }
        window.location.href = "/login";
    }

    attachPersistentHandlers() {
        this._escHandler = (e) => {
            if (e.key === "Escape" && this.isOpen) {
                const backdrop = this.shadowRoot.querySelector(".backdrop");
                const panel    = this.shadowRoot.querySelector(".panel");
                this.isOpen = false;
                backdrop.classList.remove("open");
                panel.classList.remove("open");
            }
            if (e.key === "Escape" && this.userMenuOpen) {
                this._setUserMenuOpen(false);
            }
        };
        document.addEventListener("keydown", this._escHandler);

        this._hashHandler = () => this.render();
        window.addEventListener("hashchange", this._hashHandler);

        // Re-render when a new route is registered so the panel fills in
        // even if view scripts run after the nav's first render.
        this._routeHandler = () => this.render();
        window.addEventListener("fb:route-registered", this._routeHandler);

        // Context switch: hrefs pick up the new space prefix, active-state
        // moves to the correct item.
        this._contextHandler = () => this.render();
        window.addEventListener("fb:context-changed", this._contextHandler);

        // Any click outside the user widget closes the dropdown. We handle
        // this in the capture phase because the shadow root's boundary means
        // clicks inside the widget reach the document as a single event
        // targeted at the host element — we detect "inside" by checking the
        // composedPath against the widget's DOM.
        this._docClickHandler = (e) => {
            if (!this.userMenuOpen) return;
            const widget = this.shadowRoot.getElementById("user-widget");
            const path = e.composedPath?.() || [];
            if (widget && !path.includes(widget)) {
                this._setUserMenuOpen(false);
            }
        };
        document.addEventListener("click", this._docClickHandler, true);
    }
}

function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, c => ({"&":"&amp;","<":"&lt;",">":"&gt;","\"":"&quot;","'":"&#39;"}[c]));
}

customElements.define("fb-nav", FbNav);
