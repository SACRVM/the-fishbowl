/**
 * fb.vault — client-side encryption for ::secret blocks.
 *
 * Per-device salt (localStorage), passphrase-derived AES-GCM key cached in
 * sessionStorage. Losing the passphrase or clearing localStorage = losing
 * access to secrets on this device; no recovery. Same passphrase on a
 * different device yields a different key because the salt is per-device,
 * so secrets written on device A can't be decrypted on device B. These
 * tradeoffs are the user's explicit choice (Phase 3 design: option A, no
 * portability).
 *
 * Public surface on fb.vault:
 *   isUnlocked()          → boolean (derived key is cached)
 *   hasBeenSet()          → boolean (a passphrase has ever been set here)
 *   ensureUnlocked()      → Promise<void>, prompts if needed, throws on cancel
 *   lock()                → clear session-cached key, force re-prompt
 *   encryptBlock(string)  → Promise<string>   base64(iv ‖ ciphertext ‖ tag)
 *   decryptBlock(string)  → Promise<string>   plaintext
 *
 * The passphrase modal is plain DOM (no custom element) — just enough to
 * collect a string once per session. Styled via app.css (.fb-vault-*).
 *
 * Note on space workspaces: every device has its own salt+key, so secrets
 * written by one space member can't be decrypted by another. Space member
 * viewing Alice's encrypted secret will see "[decryption failed]" in the
 * body. Acceptable v1 limitation — cross-user sharing of secrets is not
 * what this feature is for.
 */
(function () {
    if (!window.fb) return;
    if (!window.crypto?.subtle) {
        console.warn("fb.vault: WebCrypto unavailable; secret encryption disabled.");
        return;
    }

    const SALT_KEY   = "fb.vault.salt";
    const KEY_KEY    = "fb.vault.key";
    const ITERATIONS = 600_000; // OWASP 2023 PBKDF2-SHA256 guidance

    let _sessionKey = null; // CryptoKey, mirrors sessionStorage

    // ───── encoding helpers ─────
    const textEnc = new TextEncoder();
    const textDec = new TextDecoder();
    function toB64(bytes)  { return btoa(String.fromCharCode(...bytes)); }
    function fromB64(s)    { return Uint8Array.from(atob(s), c => c.charCodeAt(0)); }

    // ───── salt (per-device, permanent) ─────
    function getSalt() {
        const b64 = localStorage.getItem(SALT_KEY);
        if (b64) return fromB64(b64);
        const fresh = crypto.getRandomValues(new Uint8Array(16));
        localStorage.setItem(SALT_KEY, toB64(fresh));
        return fresh;
    }
    function hasBeenSet() { return localStorage.getItem(SALT_KEY) !== null; }

    // ───── PBKDF2 derive ─────
    async function deriveKey(passphrase) {
        const salt = getSalt();
        const passKey = await crypto.subtle.importKey(
            "raw", textEnc.encode(passphrase),
            "PBKDF2", false, ["deriveKey"]);
        return crypto.subtle.deriveKey(
            { name: "PBKDF2", hash: "SHA-256", salt, iterations: ITERATIONS },
            passKey,
            { name: "AES-GCM", length: 256 },
            true,  // exportable so we can cache in sessionStorage
            ["encrypt", "decrypt"]);
    }

    // ───── session cache ─────
    // The exported raw key sits in sessionStorage so a soft page reload
    // doesn't re-prompt. Closing the tab drops it. A malicious browser
    // extension or same-origin XSS could read it — standard in-browser
    // crypto tradeoff, documented.
    async function cacheKey(key) {
        _sessionKey = key;
        const raw = await crypto.subtle.exportKey("raw", key);
        sessionStorage.setItem(KEY_KEY, toB64(new Uint8Array(raw)));
    }
    async function restoreSessionKey() {
        const b64 = sessionStorage.getItem(KEY_KEY);
        if (!b64) return null;
        try {
            _sessionKey = await crypto.subtle.importKey(
                "raw", fromB64(b64), "AES-GCM", true, ["encrypt", "decrypt"]);
            return _sessionKey;
        } catch { return null; }
    }
    function lock() {
        _sessionKey = null;
        sessionStorage.removeItem(KEY_KEY);
    }
    function isUnlocked() { return _sessionKey !== null; }

    // Try to restore on module load so a soft reload doesn't re-prompt.
    restoreSessionKey();

    // ───── unlock flow ─────
    // _unlockInFlight coalesces concurrent callers. Without it, a notes-list
    // inbound transform that decrypts N notes in parallel would stack N
    // passphrase modals when the vault is locked. First caller opens the
    // modal; the rest await the same promise.
    let _unlockInFlight = null;

    async function ensureUnlocked() {
        if (isUnlocked()) return;
        await restoreSessionKey();
        if (isUnlocked()) return;
        if (_unlockInFlight) return _unlockInFlight;

        _unlockInFlight = (async () => {
            const isNew = !hasBeenSet();
            const passphrase = await promptPassphrase({ isNew });
            if (!passphrase) throw new Error("vault unlock cancelled");
            const key = await deriveKey(passphrase);
            await cacheKey(key);
        })();
        try { await _unlockInFlight; }
        finally { _unlockInFlight = null; }
    }

    // ───── encrypt / decrypt ─────
    async function encryptBlock(plaintext) {
        await ensureUnlocked();
        const iv = crypto.getRandomValues(new Uint8Array(12));
        const ct = await crypto.subtle.encrypt(
            { name: "AES-GCM", iv }, _sessionKey, textEnc.encode(plaintext));
        const ctBytes = new Uint8Array(ct);
        const out = new Uint8Array(iv.length + ctBytes.length);
        out.set(iv);
        out.set(ctBytes, iv.length);
        return toB64(out);
    }

    async function decryptBlock(b64) {
        await ensureUnlocked();
        const combined = fromB64(b64);
        const iv = combined.slice(0, 12);
        const ct = combined.slice(12);
        const pt = await crypto.subtle.decrypt(
            { name: "AES-GCM", iv }, _sessionKey, ct);
        return textDec.decode(new Uint8Array(pt));
    }

    // ───── passphrase modal ─────
    // Plain DOM overlay — lightest possible implementation. Styles live in
    // app.css under .fb-vault-*.
    function promptPassphrase({ isNew }) {
        return new Promise((resolve) => {
            const overlay = document.createElement("div");
            overlay.className = "fb-vault-overlay";
            overlay.innerHTML = `
                <div class="fb-vault-dialog" role="dialog" aria-modal="true" aria-labelledby="fb-vault-title">
                    <h2 id="fb-vault-title">${isNew ? "Set a device passphrase" : "Unlock secrets"}</h2>
                    <p class="fb-vault-msg"></p>
                    <label class="fb-vault-label">
                        <span>Passphrase</span>
                        <input type="password" class="fb-vault-input" autocomplete="${isNew ? "new-password" : "current-password"}" />
                    </label>
                    ${isNew ? `
                    <label class="fb-vault-label">
                        <span>Confirm</span>
                        <input type="password" class="fb-vault-input fb-vault-confirm" autocomplete="new-password" />
                    </label>` : ""}
                    <p class="fb-vault-error" hidden></p>
                    <div class="fb-vault-actions">
                        <button type="button" class="fb-vault-btn fb-vault-cancel">Cancel</button>
                        <button type="button" class="fb-vault-btn fb-vault-ok">${isNew ? "Set passphrase" : "Unlock"}</button>
                    </div>
                </div>
            `;
            // Set message via textContent/innerHTML separately to keep user
            // copy out of the initial template literal (safer and easier to
            // edit). The <strong> in the new-passphrase copy is intentional.
            const msg = overlay.querySelector(".fb-vault-msg");
            if (isNew) {
                msg.innerHTML =
                    "Secret blocks in your notes are encrypted locally with a passphrase of your choosing. " +
                    "You'll enter it once per session. <strong>This encryption is tied to this browser on this device</strong> — " +
                    "a different browser, profile, or incognito window uses a fresh key and cannot read secrets written here. " +
                    "<strong>Not recoverable if lost</strong> — back it up somewhere safe.";
            } else {
                msg.textContent =
                    "Enter your device passphrase to decrypt secret blocks in your notes. " +
                    "Same passphrase, different browser/profile = different key — you won't be able to read secrets " +
                    "encrypted elsewhere.";
            }
            document.body.appendChild(overlay);

            const sel = (s) => overlay.querySelector(s);
            const input   = sel(".fb-vault-input");
            const confirm = sel(".fb-vault-confirm");
            const err     = sel(".fb-vault-error");
            const ok      = sel(".fb-vault-ok");
            const cancel  = sel(".fb-vault-cancel");

            // requestAnimationFrame lets the browser paint the un-focused
            // overlay first so the autofocus visibly flows.
            requestAnimationFrame(() => input.focus());

            function close(value) {
                document.removeEventListener("keydown", onKey, true);
                overlay.remove();
                resolve(value);
            }
            function showErr(m) { err.textContent = m; err.hidden = false; }
            function submit() {
                const val = input.value;
                if (!val) { showErr("Passphrase can't be empty."); input.focus(); return; }
                if (isNew) {
                    if (val.length < 12) { showErr("Use at least 12 characters."); input.focus(); return; }
                    if (confirm.value !== val) { showErr("Passphrases don't match."); confirm.focus(); return; }
                }
                close(val);
            }
            function onKey(e) {
                if (e.key === "Escape") { e.preventDefault(); close(null); }
                else if (e.key === "Enter") { e.preventDefault(); submit(); }
            }

            ok.addEventListener("click", submit);
            cancel.addEventListener("click", () => close(null));
            document.addEventListener("keydown", onKey, true);
        });
    }

    fb.vault = {
        isUnlocked, hasBeenSet, ensureUnlocked, lock,
        encryptBlock, decryptBlock,
    };
})();
