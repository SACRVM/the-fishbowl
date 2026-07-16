/**
 * <fb-context-switcher></fb-context-switcher>
 *
 * Compact pill that shows the current workspace (personal vs space) and lets
 * the user switch between them. Reads the active context from fb.context,
 * lists the user's spaces from fb.api.spaces.list(), and navigates via
 * fb.context.set({ type, slug }) when picked.
 *
 * Rendered in the light DOM slot so the ribbon's Shadow DOM layout picks it
 * up without a dedicated slot attribute — it slides in next to the toolbar
 * content.
 */
class FbContextSwitcher extends HTMLElement {
    constructor() {
        super();
        this.attachShadow({ mode: "open" });
        this.spaces = null;      // null until loaded
        this.open = false;
    }

    connectedCallback() {
        this.render();
        this._loadSpaces();

        // Every context switch (including the one we trigger ourselves)
        // re-renders so the pill label matches the URL.
        this._onContext = () => this.render();
        window.addEventListener("fb:context-changed", this._onContext);

        // Click-outside closes the dropdown. Capture phase so shadow root
        // clicks still register as "inside".
        this._onDocClick = (e) => {
            if (!this.open) return;
            const path = e.composedPath?.() || [];
            if (!path.includes(this)) this._setOpen(false);
        };
        document.addEventListener("click", this._onDocClick, true);
    }

    disconnectedCallback() {
        if (this._onContext) window.removeEventListener("fb:context-changed", this._onContext);
        if (this._onDocClick) document.removeEventListener("click", this._onDocClick, true);
    }

    async _loadSpaces() {
        try {
            this.spaces = await fb.api.spaces.list();
        } catch (err) {
            // 401 redirects happen in fb.api; everything else we degrade
            // silently — the switcher still works for personal-only.
            console.warn("[fb-context-switcher] spaces load failed:", err?.message || err);
            this.spaces = [];
        }
        this.render();
    }

    render() {
        const ctx = fb.context?.get() || { type: "user" };
        const active = ctx.type === "space" ? ctx.slug : "personal";
        const label = ctx.type === "space"
            ? (this._spaceName(ctx.slug) || ctx.slug)
            : "Personal";
        const inSpace = ctx.type === "space";

        this.shadowRoot.innerHTML = `
            <style>
                :host { position: relative; display: inline-flex; }
                .pill {
                    display: inline-flex;
                    align-items: center;
                    gap: 6px;
                    padding: 4px 10px 4px 8px;
                    background: none;
                    border: 1px solid rgba(255, 255, 255, 0.12);
                    border-radius: 16px;
                    color: #e2e8f0;
                    cursor: pointer;
                    font: 500 0.82rem 'Inter', sans-serif;
                    transition: background 120ms, border-color 120ms, color 120ms;
                    max-width: 220px;
                }
                .pill:hover {
                    background: rgba(255, 255, 255, 0.06);
                    border-color: rgba(255, 255, 255, 0.22);
                }
                .pill.open {
                    background: rgba(255, 255, 255, 0.09);
                    border-color: rgba(255, 255, 255, 0.3);
                }
                /* Space context gets the warm accent so you can't miss that
                   you're looking at shared data. */
                .pill.space {
                    color: var(--accent-warm, #f97316);
                    border-color: color-mix(in srgb, var(--accent-warm, #f97316) 40%, transparent);
                }
                .pill.space:hover,
                .pill.space.open {
                    background: color-mix(in srgb, var(--accent-warm, #f97316) 14%, transparent);
                    border-color: color-mix(in srgb, var(--accent-warm, #f97316) 60%, transparent);
                }
                .label {
                    overflow: hidden;
                    text-overflow: ellipsis;
                    white-space: nowrap;
                }
                .pill fb-icon { --icon-size: 14px; }
                .pill .caret { --icon-size: 12px; opacity: 0.7; }

                .menu {
                    position: absolute;
                    top: calc(100% + 6px);
                    right: 0;
                    min-width: 220px;
                    background: rgba(15, 23, 42, 0.95);
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
                    z-index: 20;
                }
                .menu.open {
                    opacity: 1;
                    transform: translateY(0);
                    pointer-events: auto;
                }
                .item {
                    display: flex;
                    align-items: center;
                    gap: 0.6rem;
                    width: 100%;
                    padding: 7px 10px;
                    background: none;
                    border: none;
                    border-radius: 8px;
                    color: #cbd5e1;
                    font: 500 0.85rem 'Inter', sans-serif;
                    text-align: left;
                    cursor: pointer;
                    transition: background 120ms, color 120ms;
                }
                .item:hover {
                    background: rgba(255, 255, 255, 0.07);
                    color: #f8fafc;
                }
                .item.active {
                    background: rgba(59, 130, 246, 0.14);
                    color: var(--accent, #3b82f6);
                }
                .item.space.active {
                    background: color-mix(in srgb, var(--accent-warm, #f97316) 18%, transparent);
                    color: var(--accent-warm, #f97316);
                }
                .item fb-icon { --icon-size: 14px; }
                .divider {
                    height: 1px;
                    background: rgba(255, 255, 255, 0.08);
                    margin: 4px 0;
                }
                .empty {
                    padding: 7px 10px;
                    color: #64748b;
                    font: italic 0.8rem 'Inter', sans-serif;
                }
                .create {
                    color: var(--accent, #3b82f6);
                }
            </style>
            <button class="pill ${inSpace ? "space" : ""} ${this.open ? "open" : ""}"
                    aria-haspopup="menu"
                    aria-expanded="${this.open}">
                <fb-icon name="${inSpace ? "users" : "user"}"></fb-icon>
                <span class="label">${this._escape(label)}</span>
                <fb-icon class="caret" name="chevron-down"></fb-icon>
            </button>
            <div class="menu ${this.open ? "open" : ""}" role="menu">
                <button class="item ${active === "personal" ? "active" : ""}" data-type="user">
                    <fb-icon name="user"></fb-icon>
                    <span>Personal</span>
                </button>
                ${this.spaces === null ? "" : (this.spaces.length === 0
                    ? `<div class="divider"></div><div class="empty">No spaces yet</div>`
                    : `<div class="divider"></div>
                       ${this.spaces.map(t => `
                           <button class="item space ${active === t.slug ? "active" : ""}"
                                   data-type="space" data-slug="${this._escape(t.slug)}">
                               <fb-icon name="users"></fb-icon>
                               <span>${this._escape(t.name || t.slug)}</span>
                           </button>
                       `).join("")}`)}
                <div class="divider"></div>
                <button class="item create" data-action="manage">
                    <fb-icon name="settings"></fb-icon>
                    <span>Manage spaces…</span>
                </button>
            </div>
        `;

        const pill = this.shadowRoot.querySelector(".pill");
        pill.addEventListener("click", (e) => {
            e.stopPropagation();
            this._setOpen(!this.open);
        });
        this.shadowRoot.querySelectorAll(".item[data-type]").forEach(item => {
            item.addEventListener("click", () => {
                const type = item.dataset.type;
                const slug = item.dataset.slug;
                this._setOpen(false);
                if (type === "user") fb.context.set({ type: "user" });
                else if (type === "space" && slug) fb.context.set({ type: "space", slug });
            });
        });
        const manage = this.shadowRoot.querySelector('[data-action="manage"]');
        manage?.addEventListener("click", () => {
            this._setOpen(false);
            fb.router?.navigate?.("#/spaces");
        });
    }

    _setOpen(open) {
        this.open = open;
        this.render();
    }

    _spaceName(slug) {
        if (!this.spaces) return null;
        const found = this.spaces.find(t => t.slug === slug);
        return found?.name || null;
    }

    _escape(s) {
        return String(s ?? "").replace(/[&<>"']/g, c => ({
            "&":"&amp;","<":"&lt;",">":"&gt;","\"":"&quot;","'":"&#39;"
        }[c]));
    }
}

customElements.define("fb-context-switcher", FbContextSwitcher);
