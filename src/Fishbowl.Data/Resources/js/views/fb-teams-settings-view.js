/**
 * <fb-teams-settings-view>  (mounted at #/teams)
 *
 * Minimal management surface for team workspaces — list, create, delete.
 * Each team is a shared data context backed by its own SQLite file under
 * fishbowl-data/teams/{teamId}/team.db (keyed by ULID, not slug — so renames
 * don't move the file). Notes live inside via /api/v1/teams/{slug}/notes; the API
 * translates slug→id before opening the DB. Renders the id next to each
 * team (and the user's own id for the personal workspace) so operators can
 * match rows to files on disk.
 *
 * Light-DOM so app.css tokens apply.
 */
class FbTeamsSettingsView extends HTMLElement {
    constructor() {
        super();
        this.teams = [];
        this.me = null;
        this.busy = false;
    }

    async connectedCallback() {
        this.render();
        await this.refresh();
    }

    async refresh() {
        try {
            const [teams, me] = await Promise.all([
                fb.api.teams.list(),
                fb.api.me.get().catch(() => null),
            ]);
            this.teams = teams || [];
            this.me = me;
        } catch (err) {
            console.error("[fb-teams-settings-view] list failed:", err);
            this.teams = [];
            this.me = null;
        }
        this.renderPersonal();
        this.renderList();
    }

    render() {
        this.innerHTML = `
            <style>
                fb-teams-settings-view { display: block; padding: 40px 48px; max-width: 780px; }
                fb-teams-settings-view header { margin-bottom: 28px; }
                fb-teams-settings-view h1 {
                    font-family: 'Outfit', 'Inter', sans-serif;
                    font-size: 28px;
                    font-weight: 700;
                    margin: 0 0 6px;
                    color: var(--text);
                }
                fb-teams-settings-view .subtitle {
                    color: var(--text-muted);
                    font-size: 14px;
                    margin: 0;
                }
                fb-teams-settings-view .create-row {
                    display: flex;
                    gap: 8px;
                    margin-bottom: 32px;
                    padding: 16px;
                    background: var(--panel);
                    border: 1px solid var(--border);
                    border-radius: 10px;
                }
                fb-teams-settings-view .create-row input {
                    flex: 1;
                    background: rgba(0, 0, 0, 0.3);
                    border: 1px solid var(--border);
                    border-radius: 8px;
                    padding: 8px 12px;
                    color: var(--text);
                    font: inherit;
                    font-size: 14px;
                    outline: none;
                }
                fb-teams-settings-view .create-row input:focus { border-color: var(--accent); }
                fb-teams-settings-view .create-row button {
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
                fb-teams-settings-view .create-row button:disabled {
                    opacity: 0.5; cursor: not-allowed;
                }
                fb-teams-settings-view .create-row button:not(:disabled):hover { filter: brightness(1.1); }

                fb-teams-settings-view .list-title {
                    font-size: 12px;
                    font-weight: 600;
                    text-transform: uppercase;
                    letter-spacing: 0.06em;
                    color: var(--text-muted);
                    margin: 0 0 10px;
                }
                fb-teams-settings-view .team-row {
                    display: flex;
                    align-items: center;
                    gap: 14px;
                    padding: 14px 16px;
                    background: var(--panel);
                    border: 1px solid var(--border);
                    border-radius: 10px;
                    margin-bottom: 8px;
                }
                fb-teams-settings-view .team-row fb-icon { --icon-size: 20px; color: var(--accent); flex-shrink: 0; }
                fb-teams-settings-view .team-info { flex: 1; min-width: 0; }
                fb-teams-settings-view .team-name {
                    font-size: 14px;
                    font-weight: 600;
                    color: var(--text);
                    margin: 0 0 2px;
                }
                fb-teams-settings-view .team-meta {
                    display: flex;
                    gap: 12px;
                    font-size: 12px;
                    color: var(--text-muted);
                }
                fb-teams-settings-view .team-meta .role {
                    text-transform: uppercase;
                    letter-spacing: 0.05em;
                    font-size: 10px;
                    padding: 2px 8px;
                    border-radius: 999px;
                    background: rgba(59, 130, 246, 0.15);
                    color: var(--accent);
                    font-weight: 700;
                }
                fb-teams-settings-view .id-chip {
                    font-family: 'SFMono-Regular', Consolas, monospace;
                    font-size: 11px;
                    padding: 1px 6px;
                    border-radius: 4px;
                    background: rgba(0, 0, 0, 0.3);
                    color: var(--text);
                    user-select: all;
                }
                fb-teams-settings-view .id-chip[title] { cursor: help; }
                fb-teams-settings-view .delete-btn {
                    background: transparent;
                    border: none;
                    color: var(--text-muted);
                    cursor: pointer;
                    padding: 6px 8px;
                    border-radius: 6px;
                    transition: color 100ms, background 100ms;
                }
                fb-teams-settings-view .delete-btn fb-icon { --icon-size: 16px; }
                fb-teams-settings-view .delete-btn:hover {
                    color: var(--danger, #ef4444);
                    background: rgba(239, 68, 68, 0.12);
                }
                fb-teams-settings-view .empty {
                    text-align: center;
                    color: var(--text-muted);
                    padding: 32px 0;
                    font-size: 13px;
                }
            </style>

            <header>
                <h1>Teams</h1>
                <p class="subtitle">
                    Shared workspaces — each team has its own notes, separate from your personal data.
                </p>
            </header>

            <h2 class="list-title">Personal workspace</h2>
            <div id="personal-row"></div>

            <div style="margin-top: 32px;">
                <fb-status-banner id="form-status"></fb-status-banner>
                <div class="create-row">
                    <input type="text" id="name-input" placeholder="New team name (e.g. 'Fishbowl Dev')" maxlength="60"/>
                    <button type="button" id="create-btn">Create team</button>
                </div>
            </div>

            <h2 class="list-title">Your teams</h2>
            <div id="team-list"></div>
        `;

        const input  = this.querySelector("#name-input");
        const btn    = this.querySelector("#create-btn");

        btn.addEventListener("click", () => this._create());
        input.addEventListener("keydown", (e) => {
            if (e.key === "Enter") this._create();
        });
        input.addEventListener("input", () => this._clearStatus());
    }

    _showStatus(message, kind = "error") {
        this.querySelector("#form-status")?.show(message, kind);
    }

    _clearStatus() {
        this.querySelector("#form-status")?.hide();
    }

    renderPersonal() {
        const mount = this.querySelector("#personal-row");
        if (!mount) return;

        if (!this.me?.id) {
            mount.innerHTML = `<div class="empty">loading…</div>`;
            return;
        }

        mount.innerHTML = `
            <div class="team-row">
                <fb-icon name="user"></fb-icon>
                <div class="team-info">
                    <p class="team-name">${escapeHtml(this.me.displayName || this.me.email || "You")}</p>
                    <div class="team-meta">
                        <span class="id-chip"
                              title="users/${escapeAttr(this.me.id)}/personal.db">${escapeHtml(this.me.id)}</span>
                    </div>
                </div>
            </div>
        `;
    }

    renderList() {
        const list = this.querySelector("#team-list");
        if (!list) return;

        if (this.teams.length === 0) {
            list.innerHTML = `<div class="empty">no teams yet — create one above</div>`;
            return;
        }

        list.innerHTML = this.teams.map(t => `
            <div class="team-row" data-slug="${escapeAttr(t.slug)}">
                <fb-icon name="users"></fb-icon>
                <div class="team-info">
                    <p class="team-name">${escapeHtml(t.name)}</p>
                    <div class="team-meta">
                        <span>/${escapeHtml(t.slug)}</span>
                        <span class="role">${escapeHtml(t.role)}</span>
                        <span class="id-chip"
                              title="teams/${escapeAttr(t.id)}/team.db">${escapeHtml(t.id)}</span>
                    </div>
                </div>
                ${t.role === "owner"
                    ? `<button type="button" class="delete-btn" title="Delete team" aria-label="Delete">
                           <fb-icon name="trash"></fb-icon>
                       </button>`
                    : ""}
            </div>
        `).join("");

        list.querySelectorAll(".delete-btn").forEach(btn => {
            btn.addEventListener("click", async (e) => {
                const row  = e.currentTarget.closest(".team-row");
                const slug = row?.dataset.slug;
                if (!slug) return;
                await this._delete(slug);
            });
        });
    }

    async _create() {
        if (this.busy) return;
        this._clearStatus();

        const input = this.querySelector("#name-input");
        const name  = input.value.trim();
        if (!name) {
            this._showStatus("Team name is required.");
            input.focus();
            return;
        }

        this.busy = true;
        this._setBusy(true);
        try {
            await fb.api.teams.create({ name });
            input.value = "";
            await this.refresh();
        } catch (err) {
            console.warn("[fb-teams-settings-view] create failed:", err);
            const status = err?.status;
            if (status === 400) this._showStatus("Invalid team name.");
            else                this._showStatus("Failed to create team.");
        } finally {
            this.busy = false;
            this._setBusy(false);
        }
    }

    async _delete(slug) {
        const team = this.teams.find(t => t.slug === slug);
        if (!team) return;

        const result = await fb.dialog.confirm({
            title: `Delete team "${team.name}"?`,
            message: `The team's notes stay on disk (recoverable) but the team is removed. Undo requires manual DB surgery.`,
            buttons: [
                { action: "cancel", label: "Cancel", kind: "default" },
                { action: "delete", label: "Delete",  kind: "destructive", armAfterMs: 1500 },
            ],
        });
        if (result !== "delete") return;

        try {
            await fb.api.teams.delete(slug);
            await this.refresh();
        } catch (err) {
            console.warn("[fb-teams-settings-view] delete failed:", err);
            const status = err?.status;
            if (status === 403) this._showStatus("Only the owner can delete this team.");
            else                this._showStatus("Failed to delete team.");
        }
    }

    _setBusy(busy) {
        const btn = this.querySelector("#create-btn");
        if (btn) btn.disabled = busy;
    }
}

function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, c => ({"&":"&amp;","<":"&lt;",">":"&gt;","\"":"&quot;","'":"&#39;"}[c]));
}
function escapeAttr(s) { return escapeHtml(s); }

customElements.define("fb-teams-settings-view", FbTeamsSettingsView);
fb.router.register("#/teams", "fb-teams-settings-view", { label: "Teams", icon: "users" });
