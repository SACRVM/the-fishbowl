/**
 * <fb-keys-settings-view>  (mounted at #/keys)
 *
 * Mint, list, and revoke API keys. The raw token returned from `fb.api.keys.create`
 * is displayed exactly once — after the user closes the reveal modal there is
 * no way to see it again. All persistent state lives server-side as a SHA-256
 * hash; we never write the raw token to localStorage or anywhere else.
 *
 * Light-DOM so app.css tokens apply.
 */
class FbKeysSettingsView extends HTMLElement {
    constructor() {
        super();
        this.keys = [];
        this.spaces = [];
        this.busy = false;
    }

    async connectedCallback() {
        this.render();
        await this.refresh();
    }

    async refresh() {
        try {
            const [keys, spaces] = await Promise.all([
                fb.api.keys.list(),
                fb.api.spaces.list(),
            ]);
            this.keys = keys || [];
            this.spaces = spaces || [];
        } catch (err) {
            console.error("[fb-keys-settings-view] load failed:", err);
            this.keys = [];
            this.spaces = [];
        }
        this.renderForm();
        this.renderList();
    }

    render() {
        this.innerHTML = `
            <style>
                fb-keys-settings-view { display: block; padding: clamp(1.25rem, 5vw, 40px) clamp(1rem, 5vw, 48px); max-width: 820px; }
                fb-keys-settings-view header { margin-bottom: 28px; }
                fb-keys-settings-view h1 {
                    font-family: 'Outfit', 'Inter', sans-serif;
                    font-size: 28px;
                    font-weight: 700;
                    margin: 0 0 6px;
                    color: var(--text);
                }
                fb-keys-settings-view .subtitle {
                    color: var(--text-muted);
                    font-size: 14px;
                    margin: 0;
                }
                fb-keys-settings-view .panel {
                    background: var(--panel);
                    border: 1px solid var(--border);
                    border-radius: var(--radius-l);
                    padding: 20px;
                    margin-bottom: 28px;
                }
                fb-keys-settings-view .panel h2 {
                    font-size: 13px;
                    font-weight: 600;
                    text-transform: uppercase;
                    letter-spacing: 0.06em;
                    color: var(--text-muted);
                    margin: 0 0 14px;
                }
                fb-keys-settings-view .field { margin-bottom: 14px; }
                fb-keys-settings-view label {
                    display: block;
                    font-size: 12px;
                    font-weight: 600;
                    color: var(--text-muted);
                    margin-bottom: 4px;
                }
                fb-keys-settings-view input[type="text"],
                fb-keys-settings-view select {
                    width: 100%;
                    background: rgba(0, 0, 0, 0.3);
                    border: 1px solid var(--border);
                    border-radius: var(--radius-m);
                    padding: 8px 12px;
                    color: var(--text);
                    font: inherit;
                    font-size: 14px;
                    outline: none;
                    box-sizing: border-box;
                }
                fb-keys-settings-view select { padding-right: 32px; }   /* room for the kit's chevron */
                fb-keys-settings-view input:focus,
                fb-keys-settings-view select:focus { border-color: var(--accent); }
                fb-keys-settings-view .scopes {
                    display: grid;
                    grid-template-columns: repeat(2, 1fr);
                    gap: 6px 16px;
                    padding: 10px 12px;
                    background: rgba(0, 0, 0, 0.2);
                    border: 1px solid var(--border);
                    border-radius: var(--radius-m);
                }
                fb-keys-settings-view .scopes label {
                    display: flex;
                    align-items: center;
                    gap: 6px;
                    font-size: 13px;
                    color: var(--text);
                    margin: 0;
                    font-weight: 400;
                    text-transform: none;
                    letter-spacing: 0;
                    cursor: pointer;
                }
                fb-keys-settings-view .primary-btn {
                    background: var(--accent);
                    border: none;
                    border-radius: var(--radius-m);
                    color: #fff;
                    padding: 8px 18px;
                    font: inherit;
                    font-weight: 600;
                    cursor: pointer;
                    transition: filter 120ms;
                }
                fb-keys-settings-view .primary-btn:disabled { opacity: 0.5; cursor: not-allowed; }
                fb-keys-settings-view .primary-btn:not(:disabled):hover { filter: brightness(1.1); }

                fb-keys-settings-view .key-row {
                    display: flex;
                    align-items: center;
                    gap: 14px;
                    padding: 14px 16px;
                    background: var(--panel);
                    border: 1px solid var(--border);
                    border-radius: var(--radius-l);
                    margin-bottom: 8px;
                }
                fb-keys-settings-view .key-row sac-icon { --icon-size: 18px; color: var(--accent); flex-shrink: 0; }
                fb-keys-settings-view .key-info { flex: 1; min-width: 0; }
                fb-keys-settings-view .key-name {
                    font-size: 14px;
                    font-weight: 600;
                    color: var(--text);
                    margin: 0 0 4px;
                    word-break: break-word;
                }
                fb-keys-settings-view .key-meta {
                    display: flex;
                    flex-wrap: wrap;
                    gap: 8px;
                    font-size: 12px;
                    color: var(--text-muted);
                }
                fb-keys-settings-view .key-facts { column-gap: 20px; }
                fb-keys-settings-view .key-prefix {
                    font-family: 'SFMono-Regular', Consolas, monospace;
                    color: var(--text);
                    background: rgba(0, 0, 0, 0.3);
                    padding: 1px 6px;
                    border-radius: var(--radius-m);
                }
                fb-keys-settings-view .key-scope {
                    font-family: 'SFMono-Regular', Consolas, monospace;
                    font-size: 11px;
                    padding: 1px 6px;
                    border-radius: var(--radius-m);
                    background: rgba(59, 130, 246, 0.15);
                    color: var(--accent);
                }
                fb-keys-settings-view .revoke-btn {
                    background: transparent;
                    border: none;
                    color: var(--text-muted);
                    cursor: pointer;
                    padding: 6px 8px;
                    border-radius: var(--radius-m);
                    transition: color 100ms, background 100ms;
                }
                fb-keys-settings-view .revoke-btn sac-icon { --icon-size: 16px; }
                fb-keys-settings-view .revoke-btn:hover {
                    color: var(--danger, #ef4444);
                    background: rgba(239, 68, 68, 0.12);
                }
                fb-keys-settings-view .key-ctx {
                    display: inline-flex;
                    align-items: center;
                    gap: 4px;
                    color: var(--text);
                }
                fb-keys-settings-view .key-ctx sac-icon { --icon-size: 12px; }
                fb-keys-settings-view .key-ctx.space { color: var(--accent-warm); }
                fb-keys-settings-view .key-group {
                    display: flex;
                    align-items: center;
                    gap: 6px;
                    font-size: 12px;
                    font-weight: 600;
                    text-transform: uppercase;
                    letter-spacing: 0.06em;
                    color: var(--text-muted);
                    margin: 20px 0 10px;
                }
                fb-keys-settings-view .key-group:first-child { margin-top: 0; }
                fb-keys-settings-view .key-group sac-icon { --icon-size: 14px; }
                fb-keys-settings-view .key-group.current { color: var(--text); }
                fb-keys-settings-view .empty {
                    text-align: center;
                    color: var(--text-muted);
                    padding: 32px 0;
                    font-size: 13px;
                }

                /* Raw-token reveal (sac-dialog body, light DOM). */
                .fb-token-warn {
                    color: var(--accent-warm);
                    font-size: 13px;
                }
                .fb-token-row {
                    display: flex;
                    align-items: center;
                    gap: 8px;
                    margin-top: 12px;
                }
                /* The kit's ghost copy button, a size up (it inherits
                   --icon-btn-* into its shadow root). */
                .fb-token-row sac-copy-button {
                    --icon-btn-size: 36px;
                    --icon-btn-icon: 18px;
                }
                .fb-token-block {
                    flex: 1;
                    font-family: ui-monospace, 'SFMono-Regular', Consolas, monospace;
                    font-size: 13px;
                    background: color-mix(in srgb, var(--bg) 60%, transparent);
                    border: 1px solid var(--border);
                    border-radius: var(--radius-l);
                    padding: 12px 14px;
                    color: var(--text);
                    word-break: break-all;
                    user-select: all;
                }
            </style>

            <header>
                <h1>API keys</h1>
                <p class="subtitle">
                    Bearer tokens for MCP and programmatic clients. The raw token is
                    shown exactly once when you create it — copy it immediately.
                </p>
            </header>

            <div class="panel">
                <h2>New key</h2>
                <sac-status-banner id="form-status"></sac-status-banner>
                <div id="form-mount"></div>
            </div>

            <div id="key-list"></div>
        `;
    }

    renderForm() {
        const mount = this.querySelector("#form-mount");
        if (!mount) return;

        // Preselect the workspace the user is in: opened from inside a
        // space, a new key is for that space unless they pick otherwise.
        const scope = sac.scope.get();
        const active = scope.type === "scoped" ? scope.slug : null;
        const contextOptions = [
            `<option value="user::">Personal</option>`,
            ...this.spaces.map(t =>
                `<option value="space::${escapeAttr(t.slug)}"${t.slug === active ? " selected" : ""}>Space — ${escapeHtml(t.name)}</option>`),
        ].join("");

        mount.innerHTML = `
            <div class="field">
                <label for="key-name">Name</label>
                <input type="text" id="key-name" placeholder="e.g. 'Claude Code on laptop'" maxlength="80"/>
            </div>
            <div class="field">
                <label for="key-context">Context</label>
                <span class="select"><select id="key-context">${contextOptions}</select></span>
            </div>
            <div class="field">
                <label>Scopes</label>
                <div class="scopes">
                    ${["read:notes","write:notes","read:tags","write:tags",
                       "read:tasks","write:tasks","read:events","write:events",
                       "read:files","write:files"].map(s => `
                        <label>
                            <input type="checkbox" value="${s}"
                                ${s === "read:notes" || s === "write:notes" ? "checked" : ""}/>
                            ${s}
                        </label>
                    `).join("")}
                </div>
            </div>
            <button type="button" class="primary-btn" id="create-btn">Create key</button>
        `;

        this.querySelector("#create-btn").addEventListener("click", () => this._create());
        const nameEl = this.querySelector("#key-name");
        nameEl.addEventListener("keydown", (e) => {
            if (e.key === "Enter") this._create();
        });
        nameEl.addEventListener("input", () => this._clearStatus());
    }

    _showStatus(message, kind = "error") {
        this.querySelector("#form-status")?.show(message, kind);
    }

    _clearStatus() {
        this.querySelector("#form-status")?.hide();
    }

    renderList() {
        const list = this.querySelector("#key-list");
        if (!list) return;

        if (this.keys.length === 0) {
            list.innerHTML = `<div class="empty">no keys yet — create one above</div>`;
            return;
        }

        // Every key is listed, grouped by the workspace it is bound to; the
        // workspace the user is in comes first, then Personal, spaces, apps.
        const scope = sac.scope.get();
        const activeKey = scope.type === "scoped" ? `space:${scope.slug}` : "user:";
        const groups = new Map();
        const describe = (k) => {
            if (k.contextType === "space") {
                const space = this.spaces.find(t => t.slug === k.contextId);
                return { key: `space:${k.contextId}`, icon: "users", label: space?.name || k.contextId };
            }
            if (k.contextType === "app") return { key: `app:${k.contextId}`, icon: "cube", label: `App ${k.contextId}` };
            return { key: "user:", icon: "user", label: "Personal" };
        };
        for (const k of this.keys) {
            const d = describe(k);
            if (!groups.has(d.key)) groups.set(d.key, { ...d, keys: [] });
            groups.get(d.key).keys.push(k);
        }
        const rank = (g) => g.key === activeKey ? 0 : g.key === "user:" ? 1 : g.key.startsWith("space:") ? 2 : 3;
        const ordered = [...groups.values()].sort((a, b) => rank(a) - rank(b) || a.label.localeCompare(b.label));

        const row = (k) => {
            const d = describe(k);
            const scopes = (k.scopes || []).map(s =>
                `<span class="key-scope">${escapeHtml(s)}</span>`).join(" ");
            const lastUsed = k.lastUsedAt
                ? `last used ${formatRelative(k.lastUsedAt)}`
                : "never used";
            return `
                <div class="key-row" data-id="${escapeAttr(k.id)}">
                    <sac-icon name="key"></sac-icon>
                    <div class="key-info">
                        <p class="key-name">${escapeHtml(k.name)}</p>
                        <div class="key-meta key-facts">
                            <span class="key-prefix">${escapeHtml(k.keyPrefix)}…</span>
                            <span class="key-ctx${k.contextType === "space" ? " space" : ""}"><sac-icon name="${d.icon}"></sac-icon>${escapeHtml(d.label)}</span>
                            <span>${lastUsed}</span>
                        </div>
                        <div class="key-meta" style="margin-top: 10px;">${scopes}</div>
                    </div>
                    <button type="button" class="revoke-btn" title="Revoke" aria-label="Revoke">
                        <sac-icon name="trash"></sac-icon>
                    </button>
                </div>
            `;
        };
        list.innerHTML = ordered.map(g => `
            <h2 class="key-group${g.key === activeKey ? " current" : ""}" data-context="${escapeAttr(g.key)}">
                <sac-icon name="${g.icon}"></sac-icon><span>${escapeHtml(g.label)}</span>
            </h2>
            ${g.keys.map(row).join("")}
        `).join("");

        list.querySelectorAll(".revoke-btn").forEach(btn => {
            btn.addEventListener("click", async (e) => {
                const row = e.currentTarget.closest(".key-row");
                const id = row?.dataset.id;
                if (!id) return;
                await this._revoke(id);
            });
        });
    }

    async _create() {
        if (this.busy) return;
        this._clearStatus();

        const nameInput = this.querySelector("#key-name");
        const name = nameInput.value.trim();
        if (!name) {
            this._showStatus("Name is required.");
            nameInput.focus();
            return;
        }

        const ctxValue = this.querySelector("#key-context").value; // "user::" or "space::{slug}"
        const [contextType, contextId] = ctxValue.split("::");
        const scopes = Array.from(this.querySelectorAll(".scopes input:checked"))
            .map(el => el.value);

        if (scopes.length === 0) {
            this._showStatus("Pick at least one scope.");
            return;
        }

        this.busy = true;
        this._setBusy(true);
        try {
            const created = await fb.api.keys.create({
                name,
                contextType,
                contextId: contextType === "space" ? contextId : null,
                scopes,
            });
            await this._revealToken(created);
            this.querySelector("#key-name").value = "";
            await this.refresh();
        } catch (err) {
            console.warn("[fb-keys-settings-view] create failed:", err);
            const status = err?.status;
            if (status === 400)      this._showStatus(`Invalid: ${err?.body || "check form"}`);
            else if (status === 403) this._showStatus("You can't mint a key for that context.");
            else if (status === 404) this._showStatus("Unknown space.");
            else                     this._showStatus("Failed to create key.");
        } finally {
            this.busy = false;
            this._setBusy(false);
        }
    }

    async _revoke(id) {
        const key = this.keys.find(k => k.id === id);
        if (!key) return;

        const result = await sac.dialog.confirm({
            title: `Revoke "${key.name}"?`,
            message: `The token will stop working immediately. This cannot be undone — you'll have to mint a new key.`,
            buttons: [
                { action: "cancel", label: "Cancel", kind: "default" },
                { action: "revoke", label: "Revoke",  kind: "destructive", armAfterMs: 1200 },
            ],
        });
        if (result !== "revoke") return;

        try {
            await fb.api.keys.delete(id);
            await this.refresh();
        } catch (err) {
            console.warn("[fb-keys-settings-view] revoke failed:", err);
            this._showStatus("Failed to revoke key.");
        }
    }

    // One-time reveal of the raw token. Resolves once the user confirms they
    // saved it. A sac-dialog also closes on Escape and on a backdrop click —
    // for a token that is never shown again, one slip would lose it for good,
    // so any close other than "I've saved it" shows the dialog again.
    async _revealToken(created) {
        const show = () => new Promise((resolve) => {
            const dlg = document.createElement("sac-dialog");
            dlg.setAttribute("title", "Key created");
            dlg.style.setProperty("--dialog-width", "520px");
            dlg.buttons = [{ action: "done", label: "I've saved it", kind: "primary" }];
            dlg.innerHTML = `
                <p class="fb-token-warn">Copy the token now — it is <strong>never</strong> shown again.</p>
                <div class="fb-token-row">
                    <div class="fb-token-block"></div>
                    <sac-copy-button label="Copy token"></sac-copy-button>
                </div>`;
            // The token goes in as text / an attribute value, never markup.
            dlg.querySelector(".fb-token-block").textContent = created.rawToken;
            dlg.querySelector("sac-copy-button").setAttribute("value", created.rawToken);
            dlg.addEventListener("sac:action", (e) => {
                setTimeout(() => { dlg.remove(); resolve(e.detail.action); }, 120);
            }, { once: true });
            document.body.appendChild(dlg);
            setTimeout(() => dlg.open(), 0);
        });
        while (await show() !== "done") { /* dismissed by accident — show it again */ }
    }

    _setBusy(busy) {
        const btn = this.querySelector("#create-btn");
        if (btn) btn.disabled = busy;
    }
}

function escapeHtml(s) {
    return String(s ?? "").replace(/[&<>"']/g,
        c => ({"&":"&amp;","<":"&lt;",">":"&gt;","\"":"&quot;","'":"&#39;"}[c]));
}
function escapeAttr(s) { return escapeHtml(s); }
function formatRelative(iso) {
    try {
        const then = new Date(iso).getTime();
        const delta = Date.now() - then;
        if (delta < 60_000) return "just now";
        if (delta < 3_600_000) return `${Math.floor(delta / 60_000)}m ago`;
        if (delta < 86_400_000) return `${Math.floor(delta / 3_600_000)}h ago`;
        return `${Math.floor(delta / 86_400_000)}d ago`;
    } catch { return iso; }
}

customElements.define("fb-keys-settings-view", FbKeysSettingsView);
sac.router.register("#/keys", "fb-keys-settings-view", { label: "API keys", icon: "key", palette: false });
