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
                fb-keys-settings-view { display: block; padding: 40px 48px; max-width: 820px; }
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
                    border-radius: 10px;
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
                    border-radius: 8px;
                    padding: 8px 12px;
                    color: var(--text);
                    font: inherit;
                    font-size: 14px;
                    outline: none;
                    box-sizing: border-box;
                }
                fb-keys-settings-view input:focus,
                fb-keys-settings-view select:focus { border-color: var(--accent); }
                fb-keys-settings-view .scopes {
                    display: grid;
                    grid-template-columns: repeat(2, 1fr);
                    gap: 6px 16px;
                    padding: 10px 12px;
                    background: rgba(0, 0, 0, 0.2);
                    border: 1px solid var(--border);
                    border-radius: 8px;
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
                    border-radius: 8px;
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
                    border-radius: 10px;
                    margin-bottom: 8px;
                }
                fb-keys-settings-view .key-row fb-icon { --icon-size: 18px; color: var(--accent); flex-shrink: 0; }
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
                fb-keys-settings-view .key-prefix {
                    font-family: 'SFMono-Regular', Consolas, monospace;
                    font-size: 12px;
                    color: var(--text);
                    background: rgba(0, 0, 0, 0.3);
                    padding: 1px 6px;
                    border-radius: 4px;
                }
                fb-keys-settings-view .key-scope {
                    font-family: 'SFMono-Regular', Consolas, monospace;
                    font-size: 11px;
                    padding: 1px 6px;
                    border-radius: 999px;
                    background: rgba(59, 130, 246, 0.15);
                    color: var(--accent);
                }
                fb-keys-settings-view .revoke-btn {
                    background: transparent;
                    border: none;
                    color: var(--text-muted);
                    cursor: pointer;
                    padding: 6px 8px;
                    border-radius: 6px;
                    transition: color 100ms, background 100ms;
                }
                fb-keys-settings-view .revoke-btn fb-icon { --icon-size: 16px; }
                fb-keys-settings-view .revoke-btn:hover {
                    color: var(--danger, #ef4444);
                    background: rgba(239, 68, 68, 0.12);
                }
                fb-keys-settings-view .empty {
                    text-align: center;
                    color: var(--text-muted);
                    padding: 32px 0;
                    font-size: 13px;
                }

                /* Raw-token reveal modal. Uses its own overlay rather than
                   fb-dialog because the content is custom (monospace token
                   block + copy button + warning). */
                fb-keys-settings-view .reveal-overlay {
                    position: fixed;
                    inset: 0;
                    background: rgba(0, 0, 0, 0.7);
                    display: flex;
                    align-items: center;
                    justify-content: center;
                    z-index: 1000;
                    animation: fb-keys-fade 120ms ease-out;
                }
                @keyframes fb-keys-fade { from { opacity: 0; } to { opacity: 1; } }
                fb-keys-settings-view .reveal-panel {
                    background: var(--panel);
                    border: 1px solid var(--accent);
                    border-radius: 12px;
                    padding: 28px;
                    max-width: 520px;
                    width: calc(100% - 48px);
                    box-shadow: 0 8px 40px rgba(0, 0, 0, 0.5);
                }
                fb-keys-settings-view .reveal-panel h3 {
                    margin: 0 0 8px;
                    font-size: 18px;
                    font-weight: 700;
                    color: var(--text);
                }
                fb-keys-settings-view .reveal-panel .warn {
                    color: var(--accent-warm, #f59e0b);
                    font-size: 13px;
                    margin: 0 0 16px;
                }
                fb-keys-settings-view .token-block {
                    font-family: 'SFMono-Regular', Consolas, monospace;
                    font-size: 13px;
                    background: rgba(0, 0, 0, 0.4);
                    border: 1px solid var(--border);
                    border-radius: 8px;
                    padding: 12px 14px;
                    color: var(--text);
                    word-break: break-all;
                    margin-bottom: 14px;
                }
                fb-keys-settings-view .reveal-actions {
                    display: flex;
                    gap: 10px;
                    justify-content: flex-end;
                }
                fb-keys-settings-view .reveal-actions button {
                    font: inherit;
                    font-size: 14px;
                    padding: 8px 16px;
                    border-radius: 8px;
                    border: 1px solid var(--border);
                    background: transparent;
                    color: var(--text);
                    cursor: pointer;
                }
                fb-keys-settings-view .reveal-actions button.primary {
                    background: var(--accent);
                    color: #fff;
                    border-color: var(--accent);
                    font-weight: 600;
                }
                fb-keys-settings-view .reveal-actions button:hover { filter: brightness(1.08); }
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
                <fb-status-banner id="form-status"></fb-status-banner>
                <div id="form-mount"></div>
            </div>

            <h2 style="font-size: 12px; font-weight: 600; text-transform: uppercase;
                       letter-spacing: 0.06em; color: var(--text-muted); margin: 0 0 10px;">
                Your keys
            </h2>
            <div id="key-list"></div>
        `;
    }

    renderForm() {
        const mount = this.querySelector("#form-mount");
        if (!mount) return;

        const contextOptions = [
            `<option value="user::">Personal</option>`,
            ...this.spaces.map(t =>
                `<option value="space::${escapeAttr(t.slug)}">Space — ${escapeHtml(t.name)}</option>`),
        ].join("");

        mount.innerHTML = `
            <div class="field">
                <label for="key-name">Name</label>
                <input type="text" id="key-name" placeholder="e.g. 'Claude Code on laptop'" maxlength="80"/>
            </div>
            <div class="field">
                <label for="key-context">Context</label>
                <select id="key-context">${contextOptions}</select>
            </div>
            <div class="field">
                <label>Scopes</label>
                <div class="scopes">
                    ${["read:notes","write:notes","read:tags","write:tags",
                       "read:tasks","write:tasks","read:events","write:events"].map(s => `
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

        list.innerHTML = this.keys.map(k => {
            const scopes = (k.scopes || []).map(s =>
                `<span class="key-scope">${escapeHtml(s)}</span>`).join(" ");
            const lastUsed = k.lastUsedAt
                ? `last used ${formatRelative(k.lastUsedAt)}`
                : "never used";
            const ctxLabel = k.contextType === "space"
                ? `space/${escapeHtml(k.contextId)}`
                : "personal";
            return `
                <div class="key-row" data-id="${escapeAttr(k.id)}">
                    <fb-icon name="key"></fb-icon>
                    <div class="key-info">
                        <p class="key-name">${escapeHtml(k.name)}</p>
                        <div class="key-meta">
                            <span class="key-prefix">${escapeHtml(k.keyPrefix)}…</span>
                            <span>${ctxLabel}</span>
                            <span>${lastUsed}</span>
                        </div>
                        <div class="key-meta" style="margin-top: 4px;">${scopes}</div>
                    </div>
                    <button type="button" class="revoke-btn" title="Revoke" aria-label="Revoke">
                        <fb-icon name="trash"></fb-icon>
                    </button>
                </div>
            `;
        }).join("");

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

        const result = await fb.dialog.confirm({
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

    // One-time reveal of the raw token. Returns when the user closes the modal.
    _revealToken(created) {
        return new Promise((resolve) => {
            const overlay = document.createElement("div");
            overlay.className = "reveal-overlay";
            overlay.innerHTML = `
                <div class="reveal-panel">
                    <h3>Key created</h3>
                    <p class="warn">
                        ⚠️ Copy the token now — it is <strong>never</strong> shown again.
                    </p>
                    <div class="token-block" id="token-text"></div>
                    <div class="reveal-actions">
                        <button type="button" id="copy-btn">Copy</button>
                        <button type="button" class="primary" id="done-btn">I've saved it</button>
                    </div>
                </div>
            `;
            overlay.querySelector("#token-text").textContent = created.rawToken;

            const close = () => {
                overlay.remove();
                resolve();
            };

            overlay.querySelector("#copy-btn").addEventListener("click", async () => {
                try {
                    await navigator.clipboard.writeText(created.rawToken);
                    const btn = overlay.querySelector("#copy-btn");
                    btn.textContent = "Copied";
                    setTimeout(() => { btn.textContent = "Copy"; }, 1500);
                } catch (err) {
                    console.warn("[fb-keys-settings-view] clipboard failed:", err);
                    const btn = overlay.querySelector("#copy-btn");
                    btn.textContent = "Clipboard blocked";
                    setTimeout(() => { btn.textContent = "Copy"; }, 2000);
                }
            });
            overlay.querySelector("#done-btn").addEventListener("click", close);

            this.appendChild(overlay);
        });
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
fb.router.register("#/keys", "fb-keys-settings-view", { label: "API keys", icon: "key" });
