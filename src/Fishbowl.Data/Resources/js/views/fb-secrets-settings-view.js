/**
 * Settings → Secrets (#/secrets): the ways in to the secret vault.
 *
 * Lists the vault's key slots — passphrase, recovery key, passkeys — with
 * when each was added and last used, and manages them through fb.vault:
 * add a passkey, change the passphrase, replace the recovery key, rename or
 * remove a slot. Every change to a slot asks for a way in once more (the
 * session key can't be exported to re-wrap it). The server refuses to
 * remove the last passphrase/recovery slot; that message is shown as is.
 * Also: lock / unlock now, and the auto-lock time (a user setting).
 *
 * Personal only. In a space the page says so and points back to Personal —
 * the kit's nav lists every route in every workspace.
 */
class FbSecretsSettingsView extends HTMLElement {
    connectedCallback() {
        this.render();
        this._onVault = () => this.refresh();
        window.addEventListener("fb:vault-changed", this._onVault);
        this.refresh();
    }

    disconnectedCallback() {
        window.removeEventListener("fb:vault-changed", this._onVault);
    }

    render() {
        this.innerHTML = `
            <style>
                fb-secrets-settings-view { display: block; padding: clamp(1.25rem, 5vw, 40px) clamp(1rem, 5vw, 48px); max-width: 780px; }
                fb-secrets-settings-view header { margin-bottom: 24px; }
                fb-secrets-settings-view h1 {
                    font-family: 'Outfit', 'Inter', sans-serif;
                    font-size: 28px;
                    font-weight: 700;
                    margin: 0 0 6px;
                    color: var(--text);
                }
                fb-secrets-settings-view .subtitle { color: var(--text-muted); font-size: 14px; margin: 0; }
                fb-secrets-settings-view .panel {
                    padding: 18px 20px;
                    background: var(--panel);
                    border: 1px solid var(--border);
                    border-radius: var(--radius-l);
                    margin-bottom: 20px;
                }
                fb-secrets-settings-view .panel h2 {
                    font-size: 12px;
                    font-weight: 600;
                    text-transform: uppercase;
                    letter-spacing: 0.06em;
                    color: var(--text-muted);
                    margin: 0 0 12px;
                }
                fb-secrets-settings-view .panel p { margin: 0 0 12px; color: var(--text); font-size: 14px; }
                fb-secrets-settings-view .panel p.muted { color: var(--text-muted); font-size: 13px; }
                fb-secrets-settings-view .status-row {
                    display: flex;
                    align-items: center;
                    gap: 12px;
                }
                fb-secrets-settings-view .status-row .btn { width: auto; flex-shrink: 0; }
                fb-secrets-settings-view .status-text { flex: 1; min-width: 12em; font-size: 14px; }
                fb-secrets-settings-view .status-text sac-icon { --icon-size: 16px; vertical-align: -3px; margin-right: 6px; }
                fb-secrets-settings-view .autolock {
                    display: flex;
                    align-items: center;
                    gap: 10px;
                    margin-top: 14px;
                    font-size: 14px;
                }
                fb-secrets-settings-view .autolock { flex-wrap: wrap; color: var(--text); }
                fb-secrets-settings-view .autolock .select { width: auto; }
                fb-secrets-settings-view .autolock select { width: auto; }
                fb-secrets-settings-view .slot-actions .btn { width: auto; }
                fb-secrets-settings-view .slot-row {
                    display: flex;
                    align-items: center;
                    gap: 14px;
                    padding: 12px 0;
                }
                fb-secrets-settings-view .slot-row + .slot-row { border-top: 1px solid var(--border); }
                fb-secrets-settings-view .slot-row > sac-icon { --icon-size: 20px; color: var(--accent); flex-shrink: 0; }
                fb-secrets-settings-view .slot-info { flex: 1; min-width: 0; }
                fb-secrets-settings-view .slot-name { font-size: 14px; font-weight: 600; color: var(--text); margin: 0 0 2px; }
                fb-secrets-settings-view .slot-name input { font: inherit; padding: 2px 6px; width: min(100%, 22em); }
                fb-secrets-settings-view .slot-meta {
                    display: flex;
                    flex-wrap: wrap;
                    column-gap: 20px;
                    font-size: 12px;
                    color: var(--text-muted);
                }
                fb-secrets-settings-view .slot-actions { display: flex; align-items: center; gap: 4px; flex-shrink: 0; }
                fb-secrets-settings-view .panel-foot {
                    display: flex;
                    align-items: center;
                    gap: 12px;
                    flex-wrap: wrap;
                    margin-top: 14px;
                    padding-top: 14px;
                    border-top: 1px solid var(--border);
                }
                fb-secrets-settings-view .panel-foot .muted { margin: 0; flex-basis: 100%; }
                fb-secrets-settings-view .panel-foot .btn { width: auto; }
            </style>
            <header>
                <h1>Secrets</h1>
                <p class="subtitle">
                    Secret blocks in your notes are encrypted in this browser. These are the ways
                    to unlock them — any one of them opens your secrets on any device.
                </p>
            </header>
            <div id="secrets-body"></div>
        `;
    }

    async refresh() {
        const mount = this.querySelector("#secrets-body");
        if (!mount) return;
        const status = await fb.vault.status();

        if (sac.scope.get().type === "scoped") {
            mount.innerHTML = `
                <div class="panel">
                    <p>Secrets are personal for now — a space has no vault.</p>
                    <button type="button" class="btn" id="to-personal">Open in Personal</button>
                </div>`;
            mount.querySelector("#to-personal").addEventListener("click", () => sac.router.navigate("#/secrets"));
            return;
        }
        if (!status.available) {
            mount.innerHTML = `<div class="panel"><p class="muted">The vault can't be reached right now.</p></div>`;
            return;
        }
        if (!status.initialized) {
            mount.innerHTML = `
                <div class="panel">
                    <h2>Not set up yet</h2>
                    <p>Write <code>:::secret</code> … <code>:::end</code> in a note, or set up now: you choose a
                       passphrase and get a recovery key to keep somewhere safe.</p>
                    <button type="button" class="btn primary" id="setup">Set up secrets</button>
                </div>`;
            mount.querySelector("#setup").addEventListener("click", () => this._run(() => fb.vault.ensureUnlocked({ setup: true })));
            return;
        }

        const [slots, passkeys] = await Promise.all([fb.vault.slots(), fb.vault.passkeySupported()]);
        const minutes = fb.vault.autoLockMinutes();
        const choiceLabel = (m) => m < 60 ? `${m} minutes` : m === 60 ? "1 hour" : `${m / 60} hours`;
        const order = { passkey: 0, passphrase: 1, recovery: 2 };
        slots.sort((a, b) => order[a.kind] - order[b.kind] || String(a.createdAt).localeCompare(String(b.createdAt)));

        mount.innerHTML = `
            <div class="panel">
                <h2>Status</h2>
                <div class="status-row">
                    <span class="status-text">${status.unlocked
                        ? `<sac-icon name="unlock"></sac-icon>Unlocked in this tab`
                        : `<sac-icon name="lock"></sac-icon>Locked`}</span>
                    <button type="button" class="btn" id="lock-toggle">${status.unlocked ? "Lock now" : "Unlock"}</button>
                </div>
                <div class="autolock"><span>Lock automatically after</span>
                    <span class="select"><select id="autolock" aria-label="Lock automatically after">
                        ${fb.vault.AUTO_LOCK_CHOICES.map(m => `<option value="${m}"${m === minutes ? " selected" : ""}>${choiceLabel(m)}</option>`).join("")}
                    </select></span>
                    <span>without using a secret</span>
                </div>
            </div>
            <div class="panel">
                <h2>Ways to unlock</h2>
                <div id="slot-list"></div>
                <div class="panel-foot">
                    ${slots.some(s => s.kind === "passphrase") ? "" : `<button type="button" class="btn" id="add-passphrase"><sac-icon name="key"></sac-icon> Set a passphrase</button>`}
                    ${slots.some(s => s.kind === "recovery") ? "" : `<button type="button" class="btn" id="add-recovery"><sac-icon name="document"></sac-icon> Create a recovery key</button>`}
                    ${passkeys
                        ? `<button type="button" class="btn primary" id="add-passkey"><sac-icon name="plus"></sac-icon> Add a passkey</button>
                           <p class="muted">Fingerprint, face or device PIN — Windows Hello, Touch ID, your phone.</p>`
                        : `<p class="muted">Passkeys aren't available in this browser or on this device (they need WebAuthn PRF support).</p>`}
                </div>
            </div>
        `;

        const list = mount.querySelector("#slot-list");
        for (const s of slots) list.appendChild(this._slotRow(s));

        mount.querySelector("#lock-toggle").addEventListener("click", () =>
            this._run(() => status.unlocked ? fb.vault.lock() : fb.vault.ensureUnlocked()));
        mount.querySelector("#autolock").addEventListener("change", (e) => this._setAutoLock(Number(e.target.value), minutes));
        mount.querySelector("#add-passkey")?.addEventListener("click", () =>
            this._run(() => fb.vault.addPasskey(), "Passkey added."));
        // Missing a passphrase or a recovery key: the same flows add one.
        mount.querySelector("#add-passphrase")?.addEventListener("click", () =>
            this._run(() => fb.vault.changePassphrase(), "Passphrase set."));
        mount.querySelector("#add-recovery")?.addEventListener("click", () =>
            this._run(() => fb.vault.newRecoveryKey(), "Recovery key saved."));
    }

    _slotRow(s) {
        const row = document.createElement("div");
        row.className = "slot-row";
        row.dataset.id = s.id;
        row.dataset.kind = s.kind;
        const icon = { passphrase: "key", recovery: "document", passkey: "user" }[s.kind] || "key";
        const kindText = { passphrase: "Passphrase", recovery: "Recovery key · 24 words", passkey: "Passkey" }[s.kind] || s.kind;
        const added = s.createdAt ? `Added ${fb.format.date(s.createdAt)}` : "";
        const used = s.lastUsedAt ? `Last used ${fb.format.dateTime(s.lastUsedAt)}` : "Never used";
        row.innerHTML = `
            <sac-icon name="${icon}"></sac-icon>
            <div class="slot-info">
                <p class="slot-name"></p>
                <div class="slot-meta"><span class="slot-kind"></span><span>${added}</span><span>${used}</span></div>
            </div>
            <div class="slot-actions"></div>`;
        row.querySelector(".slot-name").textContent = s.label || kindText;
        row.querySelector(".slot-kind").textContent = kindText;

        const actions = row.querySelector(".slot-actions");
        const button = (cls, label, onClick, iconName) => {
            const b = document.createElement("button");
            b.type = "button";
            b.className = cls;
            b.title = label;
            b.setAttribute("aria-label", label);
            b.innerHTML = iconName ? `<sac-icon name="${iconName}"></sac-icon>` : "";
            if (!iconName) b.textContent = label;
            b.addEventListener("click", onClick);
            actions.appendChild(b);
            return b;
        };
        if (s.kind === "passphrase") button("btn", "Change", () => this._run(() => fb.vault.changePassphrase(), "Passphrase changed."));
        if (s.kind === "recovery") button("btn", "Replace", () => this._run(() => fb.vault.newRecoveryKey(), "New recovery key saved — the old one no longer works."));
        if (s.kind === "passkey") button("icon-btn rename", "Rename", () => this._rename(row, s), "pencil");
        button("icon-btn danger remove", "Remove", () => this._remove(s, kindText), "trash");
        return row;
    }

    _rename(row, s) {
        const name = row.querySelector(".slot-name");
        const input = document.createElement("input");
        input.value = s.label || "";
        input.maxLength = 100;
        name.replaceChildren(input);
        input.focus();
        input.select();
        let done = false;
        const finish = async (save) => {
            if (done) return;
            done = true;
            const label = input.value.trim();
            if (save && label && label !== s.label) await this._run(() => fb.vault.renameSlot(s.id, label));
            else this.refresh();
        };
        input.addEventListener("keydown", (e) => {
            if (e.key === "Enter") { e.preventDefault(); finish(true); }
            if (e.key === "Escape") { e.preventDefault(); finish(false); }
        });
        input.addEventListener("blur", () => finish(true));
    }

    async _remove(s, kindText) {
        const what = s.kind === "passkey" ? `the passkey "${s.label || "Passkey"}"` : `the ${kindText.split(" ·")[0].toLowerCase()}`;
        const answer = await sac.dialog.confirm({
            title: `Remove ${what}?`,
            message: s.kind === "passkey"
                ? "It stops unlocking your secrets. Your passphrase and recovery key keep working."
                : "It stops unlocking your secrets. Make sure another way in still works for you.",
            buttons: [
                { action: "cancel", label: "Cancel" },
                { action: "remove", label: "Remove", kind: "destructive" },
            ],
        });
        if (answer === "remove") await this._run(() => fb.vault.removeSlot(s.id), "Removed.");
    }

    async _setAutoLock(minutes, before) {
        try {
            await fb.api.me.update({ vaultAutoLockMinutes: minutes });
            fb.vault.setAutoLock(minutes);
        } catch (err) {
            console.warn("[fb-secrets-settings-view] auto-lock save failed:", err);
            window.sac?.toast?.("Couldn't save the auto-lock time.", { kind: "error" });
            this.querySelector("#autolock").value = String(before);
        }
    }

    // Runs a vault action; a cancelled dialog is a normal answer, anything
    // else is shown. Refreshes afterwards either way.
    async _run(fn, success) {
        try {
            await fn();
            if (success) window.sac?.toast?.(success, { kind: "success" });
        } catch (e) {
            if (e?.code !== "cancelled") {
                console.warn("[fb-secrets-settings-view]", e);
                window.sac?.toast?.(e?.message || "That didn't work.", { kind: "error" });
            }
        }
        this.refresh();
    }
}

customElements.define("fb-secrets-settings-view", FbSecretsSettingsView);
sac.router.register("#/secrets", "fb-secrets-settings-view", { label: "Secrets", icon: "lock", palette: false });
