/**
 * <fb-keys-settings-view>  (a window app, fb.windowApps "keys")
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
        this.renderGuide();
        this.renderList();
    }

    // "Connect an agent" (space-apps spec, decision 21): pick the workspace,
    // what the agent may do and the client — then exactly its steps.
    renderGuide() {
        const mount = this.querySelector("#guide-mount");
        if (!mount) return;
        const scope = sac.scope.get();
        const active = scope.type === "scoped" ? scope.slug : null;
        const t = (k, f, v) => fb.t(`fb.keys.${k}`, f, v);
        mount.innerHTML = `
            <div class="field">
                <label for="guide-client">${t("guide-client", "Client")}</label>
                <span class="select"><select id="guide-client">
                    <option value="connector">${t("guide-connector", "claude.ai or Claude Desktop (connector)")}</option>
                    <option value="code">Claude Code</option>
                    <option value="other">${t("guide-other", "Another MCP client or a script")}</option>
                </select></span>
            </div>
            <div class="field guide-key">
                <label for="guide-workspace">${t("context", "Context")}</label>
                <span class="select"><select id="guide-workspace">
                    <option value="user::">${t("personal", "Personal")}</option>
                    ${this.spaces.map((sp) => `<option value="space::${escapeAttr(sp.slug)}" data-role="${escapeAttr(sp.role || "")}"${sp.slug === active ? " selected" : ""}>${t("space-option", "Space — {name}", { name: escapeHtml(sp.name) })}</option>`).join("")}
                </select></span>
            </div>
            <div class="field guide-key">
                <label for="guide-access">${t("guide-access", "It may")}</label>
                <span class="select"><select id="guide-access">
                    <option value="read">${t("guide-read", "read")}</option>
                    <option value="write" selected>${t("guide-write", "read and write")}</option>
                    <option value="build">${t("guide-build", "read, write and build tables and apps")}</option>
                </select></span>
            </div>
            <div id="guide-steps"></div>`;
        const client = mount.querySelector("#guide-client");
        const ws = mount.querySelector("#guide-workspace");
        const access = mount.querySelector("#guide-access");
        const paint = () => {
            const role = ws.selectedOptions[0]?.dataset.role;
            const canBuild = ws.value !== "user::" && ["designer", "admin", "owner"].includes(role);
            access.querySelector("option[value=build]").disabled = !canBuild;
            if (!canBuild && access.value === "build") access.value = "write";
            for (const f of mount.querySelectorAll(".guide-key")) f.hidden = client.value === "connector";
            this._paintSteps(client.value, access.value);
        };
        for (const el of [client, ws, access]) el.addEventListener("change", paint);
        paint();
    }

    _paintSteps(client, access) {
        const t = (k, f, v) => fb.t(`fb.keys.${k}`, f, v);
        const steps = this.querySelector("#guide-steps");
        const mcp = `${location.origin}/mcp`;
        const tryLine = access === "build"
            ? t("try-build", "Read the space guide, then build a small app for this space that lists our open orders.")
            : access === "write" ? t("try-write", "Remember that the team meeting moved to Thursdays.")
            : t("try-read", "What do I have on my calendar this week?");
        if (client === "connector") {
            steps.innerHTML = `
                <ol class="guide-steps">
                    <li>${t("step-connector-1", "In claude.ai: Settings → Connectors → Add custom connector.")}</li>
                    <li>${t("step-connector-2", "URL:")} <code class="guide-url"></code> <sac-copy-button></sac-copy-button></li>
                    <li>${t("step-connector-3", "Sign in when asked, then pick the workspace and what it may do.")}</li>
                </ol>
                <p class="muted">${t("try", "Try:")} <em class="guide-try"></em></p>`;
            steps.querySelector(".guide-url").textContent = mcp;
            steps.querySelector("sac-copy-button").setAttribute("value", mcp);
        } else {
            steps.innerHTML = `
                <p>${client === "code"
                    ? t("step-code", "Makes a key for this and shows the command to add it to Claude Code — once.")
                    : t("step-other", "Makes a key for this and shows it with the MCP and API addresses — once.")}</p>
                <div class="toolbar"><button type="button" class="btn primary" id="guide-create">${t("guide-create", "Create key")}</button></div>
                <p class="muted">${t("try", "Try:")} <em class="guide-try"></em></p>`;
            steps.querySelector("#guide-create").addEventListener("click", () => this._guideCreate(client, access));
        }
        steps.querySelector(".guide-try").textContent = tryLine;
    }

    // Scopes per access level — the server's OAuthApi.ScopesFor.
    static scopesFor(access, space) {
        const read = ["read:notes", "read:tags", "read:tasks", "read:contacts", "read:events", "read:files", ...(space ? ["read:tables"] : [])];
        if (access === "read") return read;
        const write = [...read, "write:notes", "write:tags", "write:tasks", "write:contacts", "write:events", "write:files", ...(space ? ["write:tables"] : [])];
        return access === "build" && space ? [...write, "design:tables", "design:apps"] : write;
    }

    async _guideCreate(client, access) {
        const [contextType, contextId] = this.querySelector("#guide-workspace").value.split("::");
        const space = contextType === "space";
        try {
            const created = await fb.api.keys.create({
                name: client === "code" ? "Claude Code" : fb.t("fb.keys.guide-key-name", "MCP client"),
                contextType,
                contextId: space ? contextId : null,
                scopes: FbKeysSettingsView.scopesFor(access, space),
            });
            const mcp = `${location.origin}/mcp`;
            const text = client === "code"
                ? `claude mcp add --transport http fishbowl ${mcp} --header "Authorization: Bearer ${created.rawToken}"`
                : `MCP:   ${mcp}\nAPI:   ${location.origin}/api/v1${space ? `/spaces/${contextId}` : ""}\nToken: ${created.rawToken}`;
            await this._revealToken(created, text);
            await this.refresh();
        } catch (err) {
            sac.toast?.(fb.errors.text(err, fb.t("fb.keys.create-failed", "Failed to create key.")), { kind: "error" });
        }
    }

    render() {
        this.classList.add("fb-page");
        this.innerHTML = `
            <style>
                /* Page frame, cards, rows, buttons and chips are the kit's
                   (app.css .fb-page / .fb-row); only this page's own bits. */
                fb-keys-settings-view .scopes {
                    display: grid;
                    grid-template-columns: repeat(auto-fill, minmax(10rem, 1fr));
                    gap: 6px 16px;
                }
                fb-keys-settings-view .key-ctx { display: inline-flex; align-items: center; gap: 4px; color: var(--text); }
                fb-keys-settings-view .key-ctx sac-icon { --icon-size: 12px; }
                fb-keys-settings-view .key-ctx.space { color: var(--accent-warm); }
                fb-keys-settings-view .key-scopes { margin-top: 8px; gap: 6px; }

                /* Raw-token reveal (sac-dialog body, light DOM). */
                .fb-token-warn { color: var(--accent-warm); }
                .fb-token-row { display: flex; align-items: center; gap: 8px; margin-top: 12px; }
                /* The kit's ghost copy button, a size up (it inherits
                   --icon-btn-* into its shadow root). */
                .fb-token-row sac-copy-button { --icon-btn-size: 36px; --icon-btn-icon: 18px; }
                .fb-token-block {
                    flex: 1;
                    font-family: var(--font-mono);
                    background: var(--field);
                    border: 1px solid var(--border);
                    border-radius: var(--radius-m);
                    padding: 12px 14px;
                    word-break: break-all;
                    white-space: pre-wrap;
                    user-select: all;
                }
                /* "Connect an agent": numbered steps, the URL in mono. */
                .guide-steps { margin: 8px 0 12px; padding-left: 20px; display: grid; gap: 6px; }
                .guide-steps code { font-family: var(--font-mono); }
                .guide-steps sac-copy-button { vertical-align: middle; }
            </style>

            <header><div>
                <h1>${fb.t("fb.keys.title", "API keys")}</h1>
                <p class="subtitle">
                    ${fb.t("fb.keys.subtitle", "Bearer tokens for MCP and programmatic clients. The raw token is shown exactly once when you create it — copy it immediately.")}
                </p>
            </div></header>

            <div class="card" id="agent-guide">
                <sac-section title="${fb.t("fb.keys.agent", "Connect an agent")}">
                    <div id="guide-mount"></div>
                </sac-section>
            </div>

            <div class="card">
                <sac-section title="${fb.t("fb.keys.new", "New key")}">
                    <sac-status-banner id="form-status"></sac-status-banner>
                    <div id="form-mount"></div>
                </sac-section>
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
            `<option value="user::">${fb.t("fb.keys.personal", "Personal")}</option>`,
            ...this.spaces.map(t =>
                `<option value="space::${escapeAttr(t.slug)}"${t.slug === active ? " selected" : ""}>${fb.t("fb.keys.space-option", "Space — {name}", { name: escapeHtml(t.name) })}</option>`),
        ].join("");

        mount.innerHTML = `
            <div class="field">
                <label for="key-name">${fb.t("fb.keys.name", "Name")}</label>
                <input type="text" id="key-name" placeholder="${fb.t("fb.keys.name-placeholder", "e.g. 'Claude Code on laptop'")}" maxlength="80"/>
            </div>
            <div class="field">
                <label for="key-context">${fb.t("fb.keys.context", "Context")}</label>
                <span class="select"><select id="key-context">${contextOptions}</select></span>
            </div>
            <div class="field">
                <label>${fb.t("fb.keys.scopes", "Scopes")}</label>
                <div class="scopes">
                    ${["read:notes","write:notes","read:tags","write:tags",
                       "read:tasks","write:tasks","read:contacts","write:contacts","read:events","write:events",
                       "read:files","write:files","read:tables","write:tables","design:tables","design:apps"].map(s => `
                        <label class="fb-check">
                            <input type="checkbox" value="${s}"
                                ${s === "read:notes" || s === "write:notes" ? "checked" : ""}/>
                            ${s}
                        </label>
                    `).join("")}
                </div>
            </div>
            <div class="toolbar">
                <button type="button" class="btn primary" id="create-btn">${fb.t("fb.keys.create", "Create key")}</button>
            </div>
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
            list.innerHTML = `
                <div class="card"><div class="empty-state">
                    <sac-icon name="key"></sac-icon>
                    <h3>${fb.t("fb.keys.empty", "No keys yet")}</h3>
                    <p>${fb.t("fb.keys.empty-hint", "Create one above.")}</p>
                </div></div>`;
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
            if (k.contextType === "app") return { key: `app:${k.contextId}`, icon: "cube", label: fb.t("fb.keys.app", "App {id}", { id: k.contextId }) };
            return { key: "user:", icon: "user", label: fb.t("fb.keys.personal", "Personal") };
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
                `<sac-chip label="${escapeAttr(s)}"></sac-chip>`).join("");
            const lastUsed = k.lastUsedAt
                ? fb.t("fb.keys.last-used", "last used {when}", { when: formatRelative(k.lastUsedAt) })
                : fb.t("fb.keys.never-used", "never used");
            return `
                <div class="fb-row key-row" data-id="${escapeAttr(k.id)}">
                    <sac-icon name="key"></sac-icon>
                    <div class="fb-row-info">
                        <p class="fb-row-name">${escapeHtml(k.name)}</p>
                        <div class="fb-row-meta">
                            <code>${escapeHtml(k.keyPrefix)}…</code>
                            <span class="key-ctx${k.contextType === "space" ? " space" : ""}"><sac-icon name="${d.icon}"></sac-icon>${escapeHtml(d.label)}</span>
                            <span>${lastUsed}</span>
                        </div>
                        <div class="fb-row-meta key-scopes">${scopes}</div>
                    </div>
                    <button type="button" class="icon-btn danger revoke-btn" title="${fb.t("fb.keys.revoke", "Revoke")}" aria-label="${fb.t("fb.keys.revoke", "Revoke")}">
                        <sac-icon name="trash"></sac-icon>
                    </button>
                </div>
            `;
        };
        // One card per workspace, headed by its name.
        list.innerHTML = ordered.map(g => `
            <div class="card">
                <sac-section class="key-group" data-context="${escapeAttr(g.key)}" title="${escapeAttr(g.label)}">
                    ${g.keys.map(row).join("")}
                </sac-section>
            </div>
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
            this._showStatus(fb.t("fb.keys.name-required", "Name is required."));
            nameInput.focus();
            return;
        }

        const ctxValue = this.querySelector("#key-context").value; // "user::" or "space::{slug}"
        const [contextType, contextId] = ctxValue.split("::");
        const scopes = Array.from(this.querySelectorAll(".scopes input:checked"))
            .map(el => el.value);

        if (scopes.length === 0) {
            this._showStatus(fb.t("fb.keys.scope-required", "Pick at least one scope."));
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
            if (status === 400)      this._showStatus(fb.errors.text(err, fb.t("fb.keys.invalid", "Invalid — check the form.")));
            else if (status === 403) this._showStatus(fb.t("fb.keys.forbidden", "You can't mint a key for that context."));
            else if (status === 404) this._showStatus(fb.t("fb.keys.unknown-space", "Unknown space."));
            else                     this._showStatus(fb.t("fb.keys.create-failed", "Failed to create key."));
        } finally {
            this.busy = false;
            this._setBusy(false);
        }
    }

    async _revoke(id) {
        const key = this.keys.find(k => k.id === id);
        if (!key) return;

        const result = await sac.dialog.confirm({
            title: fb.t("fb.keys.revoke-title", "Revoke \"{name}\"?", { name: key.name }),
            message: fb.t("fb.keys.revoke-body", "The token will stop working immediately. This cannot be undone — you'll have to mint a new key."),
            buttons: [
                { action: "cancel", label: fb.t("fb.keys.cancel", "Cancel"), kind: "default" },
                { action: "revoke", label: fb.t("fb.keys.revoke", "Revoke"),  kind: "destructive", armAfterMs: 1200 },
            ],
        });
        if (result !== "revoke") return;

        try {
            await fb.api.keys.delete(id);
            await this.refresh();
        } catch (err) {
            console.warn("[fb-keys-settings-view] revoke failed:", err);
            this._showStatus(fb.t("fb.keys.revoke-failed", "Failed to revoke key."));
        }
    }

    // One-time reveal of the raw token. Resolves once the user confirms they
    // saved it. A sac-dialog also closes on Escape and on a backdrop click —
    // for a token that is never shown again, one slip would lose it for good,
    // so any close other than "I've saved it" shows the dialog again.
    async _revealToken(created, text = created.rawToken) {
        const show = () => new Promise((resolve) => {
            const dlg = document.createElement("sac-dialog");
            dlg.setAttribute("title", fb.t("fb.keys.created", "Key created"));
            dlg.style.setProperty("--dialog-width", "520px");
            dlg.buttons = [{ action: "done", label: fb.t("fb.keys.saved-it", "I've saved it"), kind: "primary" }];
            dlg.innerHTML = `
                <p class="fb-token-warn">${fb.t("fb.keys.copy-now", "Copy the token now — it is <strong>never</strong> shown again.")}</p>
                <div class="fb-token-row">
                    <div class="fb-token-block"></div>
                    <sac-copy-button label="${fb.t("fb.keys.copy-token", "Copy token")}"></sac-copy-button>
                </div>`;
            // The token goes in as text / an attribute value, never markup.
            dlg.querySelector(".fb-token-block").textContent = text;
            dlg.querySelector("sac-copy-button").setAttribute("value", text);
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
        if (delta < 60_000) return fb.t("fb.keys.just-now", "just now");
        if (delta < 3_600_000) return fb.t("fb.keys.minutes-ago", "{n}m ago", { n: Math.floor(delta / 60_000) });
        if (delta < 86_400_000) return fb.t("fb.keys.hours-ago", "{n}h ago", { n: Math.floor(delta / 3_600_000) });
        return fb.t("fb.keys.days-ago", "{n}d ago", { n: Math.floor(delta / 86_400_000) });
    } catch { return iso; }
}

customElements.define("fb-keys-settings-view", FbKeysSettingsView);
