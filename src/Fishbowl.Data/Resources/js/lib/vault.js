/**
 * fb.vault — secret vault v2 (docs/superpowers/specs/2026-09-25-secret-vault-design.md).
 *
 * One random Vault Key (VK, AES-GCM 256) per user encrypts every :::secret
 * block. The server keeps wrapped copies of it — "key slots" — one per way
 * to unlock: a passphrase (PBKDF2-SHA256), the 24-word recovery key
 * (HKDF-SHA256) or a passkey (WebAuthn PRF → HKDF-SHA256). The server never
 * sees the VK or anything that opens it.
 *
 * Unlocking unwraps the VK as a NON-extractable CryptoKey held in memory
 * only: a reload asks again, and page script can use the key but not copy
 * it out. Auto-lock after N minutes without a secret operation (a user
 * setting, 15 by default).
 *
 * Because the session key can't be exported, managing slots (adding a
 * passkey, changing the passphrase, a new recovery key) asks for a way in
 * once more and works on a short-lived extractable copy — which is also
 * the re-authentication such changes should have.
 *
 * Personal workspace only. In a space nothing is offered or triggered
 * (spaces have no shared vault yet); ensureUnlocked() refuses there.
 *
 * Public surface on fb.vault:
 *   isUnlocked()                 → boolean
 *   ensureUnlocked({ setup })    → Promise<void>; asks to unlock. With
 *                                  setup: true (writing a secret) a vault
 *                                  that doesn't exist yet is set up first;
 *                                  otherwise (reading) that throws
 *                                  VaultError("locked"). Also throws on
 *                                  cancel or in a space.
 *   lock()                       → Promise; runs the before-lock hooks
 *                                  (views flush unsaved secrets while the
 *                                  key still exists), then forgets the key
 *                                  and fires fb:vault-changed
 *   onBeforeLock(fn)             → register an async hook; returns unhook
 *   status()                     → Promise<{ available, initialized, unlocked }>
 *                                  — what a menu needs to offer lock/unlock.
 *                                  `available` is false in a space.
 *   fb:vault-changed (window)    → detail { unlocked, initialized } on every
 *                                  unlock, lock, setup and slot change
 *   encryptBlock(text, aad)      → Promise<string>  base64(iv ‖ ct ‖ tag)
 *   decryptBlock(b64, aad)       → Promise<string>
 *
 *   Settings (the Secrets page):
 *   slots()                      → Promise<slot[]> (id, kind, label, createdAt, lastUsedAt, …)
 *   passkeySupported()           → Promise<boolean> — can this browser/device
 *                                  unlock with a passkey (WebAuthn PRF)?
 *   addPasskey()                 → asks for a way in, registers a passkey
 *   changePassphrase()           → asks for a way in, then a new passphrase
 *   newRecoveryKey()             → asks for a way in, shows new words, and
 *                                  replaces the old recovery key
 *   renameSlot(id, label), removeSlot(id)
 *   autoLockMinutes(), setAutoLock(minutes) — the running value; the
 *                                  shell sets it from the user setting
 *   AUTO_LOCK_CHOICES            → the minutes the settings page offers
 *   VaultError                   → { code: "cancelled" | "space" | "locked" | "unavailable" | "input" | … }
 */
(function () {
    if (!window.fb) return;

    class VaultError extends Error {
        constructor(code, message) { super(message); this.code = code; }
    }

    if (!window.crypto?.subtle) {
        console.warn("fb.vault: WebCrypto unavailable; secrets disabled.");
        const unavailable = () => Promise.reject(new VaultError("unavailable", "Secrets need a secure (HTTPS) connection."));
        fb.vault = { isUnlocked: () => false, ensureUnlocked: unavailable, lock() {},
                     status: async () => ({ available: false, initialized: false, unlocked: false }),
                     encryptBlock: unavailable, decryptBlock: unavailable, VaultError };
        return;
    }

    const AUTO_LOCK_CHOICES = [5, 15, 30, 60, 240];   // mirrors Fishbowl.Core.Util.VaultSettings
    const PBKDF2_ITER     = 600_000;            // OWASP 2023 PBKDF2-SHA256
    const MIN_PASSPHRASE  = 12;
    const SUGGEST_WORDS   = 6;                  // 6 × 11 bits ≈ 66 bits
    const RECOVERY_WORDS  = 24;                 // 256-bit entropy + 8-bit checksum (BIP-39)
    const RECOVERY_INFO   = "fishbowl-vault-recovery-v2";
    const PASSKEY_INFO    = "fishbowl-vault-passkey-v2";
    const WORDLIST_URL    = "/js/vendor/bip39/english.txt";
    // The recovery words ARE the key. A changed list (a disk-overlay mod, a
    // bad upgrade) would silently turn every written-down key into garbage,
    // so the list is pinned by hash, not just by path.
    const WORDLIST_SHA256 = "2f5eed53a4727b4bf8880d8f3f199efc90e58503646d9ff8eff3a2ed3b24dbda";

    const enc = new TextEncoder();
    const dec = new TextDecoder();
    const toB64   = (bytes) => btoa(String.fromCharCode(...bytes));
    const fromB64 = (s) => Uint8Array.from(atob(s), c => c.charCodeAt(0));
    const toB64Url   = (bytes) => toB64(bytes).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
    const fromB64Url = (s) => fromB64(s.replace(/-/g, "+").replace(/_/g, "/") + "===".slice((s.length + 3) % 4));
    const random  = (n) => crypto.getRandomValues(new Uint8Array(n));
    const concat  = (a, b) => { const o = new Uint8Array(a.length + b.length); o.set(a); o.set(b, a.length); return o; };
    const hex     = (buf) => [...new Uint8Array(buf)].map(b => b.toString(16).padStart(2, "0")).join("");

    let _vk = null;          // non-extractable CryptoKey while unlocked
    let _initialized = null; // does a vault exist? null = not asked yet
    let _lockTimer = null;
    let _autoLockMin = 15;

    function inSpace() { return window.sac?.scope?.get?.().type === "scoped"; }

    // ───── session ─────
    function announce() {
        window.dispatchEvent(new CustomEvent("fb:vault-changed",
            { detail: { unlocked: isUnlocked(), initialized: !!_initialized } }));
    }

    async function status() {
        if (inSpace()) return { available: false, initialized: false, unlocked: false };
        if (_initialized === null) {
            try { _initialized = (await getVault()).initialized; }
            catch { return { available: false, initialized: false, unlocked: false }; }
        }
        return { available: true, initialized: _initialized, unlocked: isUnlocked() };
    }
    function touch() {
        clearTimeout(_lockTimer);
        _lockTimer = setTimeout(() => lock(), _autoLockMin * 60 * 1000);
    }
    function autoLockMinutes() { return _autoLockMin; }
    function setAutoLock(minutes) {
        _autoLockMin = AUTO_LOCK_CHOICES.includes(minutes) ? minutes : 15;
        if (isUnlocked()) touch();   // restart the countdown with the new length
    }

    // Views register here to save pending edits while the key still exists —
    // after lock() a flush of a note with a secret would need a new unlock.
    const _beforeLock = new Set();
    function onBeforeLock(fn) {
        _beforeLock.add(fn);
        return () => _beforeLock.delete(fn);
    }

    async function lock() {
        clearTimeout(_lockTimer);
        _lockTimer = null;
        if (!isUnlocked()) return;
        for (const fn of [..._beforeLock]) {
            try { await fn(); } catch (e) { console.warn("[fb.vault] before-lock hook failed:", e); }
        }
        _vk = null;
        clearTimeout(_lockTimer);
        _lockTimer = null;
        announce();
    }
    function isUnlocked() { return _vk !== null; }

    // ───── word list + BIP-39 encoding ─────
    let _words = null;
    async function wordlist() {
        if (_words) return _words;
        const res = await fetch(WORDLIST_URL, { cache: "no-cache" });
        if (!res.ok) throw new VaultError("unavailable", "The recovery word list could not be loaded.");
        // Normalise line endings before hashing: the pinned hash is of the
        // LF file, and a CRLF checkout must not lock anyone out.
        const text = (await res.text()).replace(/\r\n/g, "\n");
        if (hex(await crypto.subtle.digest("SHA-256", enc.encode(text))) !== WORDLIST_SHA256)
            throw new VaultError("unavailable", "The recovery word list failed its integrity check.");
        const words = text.split("\n").map(w => w.trim()).filter(Boolean);
        if (words.length !== 2048) throw new VaultError("unavailable", "The recovery word list is malformed.");
        return (_words = words);
    }

    // 256 bits of entropy + the first 8 bits of its SHA-256 = 264 bits =
    // 24 × 11-bit word indices. The checksum catches a mistyped word.
    async function entropyToWords(entropy) {
        const words = await wordlist();
        const check = new Uint8Array(await crypto.subtle.digest("SHA-256", entropy))[0];
        const bits = [...entropy, check].map(b => b.toString(2).padStart(8, "0")).join("");
        const out = [];
        for (let i = 0; i < RECOVERY_WORDS; i++) out.push(words[parseInt(bits.slice(i * 11, i * 11 + 11), 2)]);
        return out;
    }

    async function wordsToEntropy(input) {
        const words = await wordlist();
        const given = String(input).toLowerCase().split(/[^a-z]+/).filter(Boolean);
        if (given.length !== RECOVERY_WORDS) throw new VaultError("input", `A recovery key has ${RECOVERY_WORDS} words — this has ${given.length}.`);
        const bits = given.map((w, i) => {
            const idx = words.indexOf(w);
            if (idx < 0) throw new VaultError("input", `Word ${i + 1} ("${w}") is not a recovery word.`);
            return idx.toString(2).padStart(11, "0");
        }).join("");
        const bytes = new Uint8Array(33);
        for (let i = 0; i < 33; i++) bytes[i] = parseInt(bits.slice(i * 8, i * 8 + 8), 2);
        const entropy = bytes.slice(0, 32);
        const check = new Uint8Array(await crypto.subtle.digest("SHA-256", entropy))[0];
        if (check !== bytes[32]) throw new VaultError("input", "These words don't form a valid recovery key — check for a typo.");
        return entropy;
    }

    // 2048 divides 65536, so masking a random uint16 is uniform.
    async function suggestPassphrase() {
        const words = await wordlist();
        const idx = crypto.getRandomValues(new Uint16Array(SUGGEST_WORDS));
        return [...idx].map(i => words[i & 2047]).join("-");
    }

    // ───── key wrapping ─────
    // AAD binds a wrapped key to its slot kind and exact kdf params, so a
    // slot's ciphertext can't be replayed under different parameters.
    const slotAad = (kind, kdf) => enc.encode(`fishbowl-vault-v2|${kind}|${kdf}`);
    const newPassKdf = () => JSON.stringify({ alg: "pbkdf2-sha256", iter: PBKDF2_ITER, salt: toB64(random(16)) });
    const newRecKdf  = () => JSON.stringify({ alg: "hkdf-sha256", salt: toB64(random(16)) });

    async function passphraseKek(passphrase, kdf) {
        const p = JSON.parse(kdf);
        if (p.alg !== "pbkdf2-sha256") throw new Error(`unsupported kdf ${p.alg}`);
        const base = await crypto.subtle.importKey("raw", enc.encode(passphrase), "PBKDF2", false, ["deriveKey"]);
        return crypto.subtle.deriveKey(
            { name: "PBKDF2", hash: "SHA-256", salt: fromB64(p.salt), iterations: p.iter },
            base, { name: "AES-GCM", length: 256 }, false, ["wrapKey", "unwrapKey"]);
    }

    async function recoveryKek(entropy, kdf) {
        const p = JSON.parse(kdf);
        if (p.alg !== "hkdf-sha256") throw new Error(`unsupported kdf ${p.alg}`);
        return hkdfKek(entropy, fromB64(p.salt), RECOVERY_INFO);
    }

    // The passkey's PRF output (32 bytes the authenticator derives from its
    // own secret and the slot's salt) → HKDF → the wrapping key.
    async function passkeyKek(prfOutput, kdf) {
        const p = JSON.parse(kdf);
        if (p.alg !== "prf-hkdf-sha256") throw new Error(`unsupported kdf ${p.alg}`);
        return hkdfKek(new Uint8Array(prfOutput), fromB64(p.salt), PASSKEY_INFO);
    }

    async function hkdfKek(ikm, salt, info) {
        const base = await crypto.subtle.importKey("raw", ikm, "HKDF", false, ["deriveKey"]);
        return crypto.subtle.deriveKey(
            { name: "HKDF", hash: "SHA-256", salt, info: enc.encode(info) },
            base, { name: "AES-GCM", length: 256 }, false, ["wrapKey", "unwrapKey"]);
    }

    async function wrap(vk, kek, kind, kdf) {
        const iv = random(12);
        const ct = await crypto.subtle.wrapKey("raw", vk, kek, { name: "AES-GCM", iv, additionalData: slotAad(kind, kdf) });
        return concat(iv, new Uint8Array(ct));
    }

    // extractable: only for slot management, which has to wrap the key again.
    async function unwrap(slot, kek, extractable = false) {
        const bytes = fromB64(slot.wrappedKey);
        return crypto.subtle.unwrapKey("raw", bytes.slice(12), kek,
            { name: "AES-GCM", iv: bytes.slice(0, 12), additionalData: slotAad(slot.kind, slot.kdf) },
            { name: "AES-GCM", length: 256 }, extractable, ["encrypt", "decrypt"]);
    }

    // The session copy is always non-extractable, whatever unlocked it.
    async function sessionCopy(key) {
        if (!key.extractable) return key;
        const raw = new Uint8Array(await crypto.subtle.exportKey("raw", key));
        try { return await crypto.subtle.importKey("raw", raw, { name: "AES-GCM" }, false, ["encrypt", "decrypt"]); }
        finally { raw.fill(0); }
    }

    // ───── passkeys (WebAuthn PRF) ─────
    // PRF support depends on the browser AND the authenticator, so it is
    // probed, never assumed: getClientCapabilities() where the browser has
    // it; otherwise offer when a platform authenticator exists and let the
    // registration itself prove PRF (it fails cleanly without).
    let _prf = null;
    function passkeySupported() {
        if (_prf) return _prf;
        return (_prf = (async () => {
            if (!window.PublicKeyCredential || !navigator.credentials?.create) return false;
            try {
                if (PublicKeyCredential.getClientCapabilities) {
                    const caps = await PublicKeyCredential.getClientCapabilities();
                    if ("extension:prf" in caps) return caps["extension:prf"] === true;
                }
                return await PublicKeyCredential.isUserVerifyingPlatformAuthenticatorAvailable?.() === true;
            } catch { return false; }
        })());
    }

    function passkeyError(e) {
        if (e instanceof VaultError) return e;
        if (e?.name === "NotAllowedError") return new VaultError("input", "The passkey prompt was cancelled or timed out.");
        if (e?.name === "InvalidStateError") return new VaultError("input", "This device already has a passkey for your secrets.");
        return new VaultError("input", "The passkey didn't work here — use your passphrase instead.");
    }

    // Registers a new passkey and returns its slot parts and PRF output.
    async function registerPasskey(existingCredentialIds = []) {
        const salt = random(32);
        let me = null;
        try { me = await fb.api.me.get(); } catch { /* the name is cosmetic */ }
        let cred;
        try {
            cred = await navigator.credentials.create({ publicKey: {
                rp: { name: "Fishbowl secrets", id: location.hostname },
                // A random handle: the passkey only unlocks the vault, it
                // identifies nobody.
                user: { id: random(16), name: me?.email || "secrets", displayName: `Fishbowl secrets${me?.name ? ` — ${me.name}` : ""}` },
                challenge: random(32),
                pubKeyCredParams: [{ type: "public-key", alg: -7 }, { type: "public-key", alg: -257 }],
                authenticatorSelection: { residentKey: "preferred", userVerification: "required" },
                excludeCredentials: existingCredentialIds.map(id => ({ type: "public-key", id: fromB64Url(id) })),
                timeout: 120000,
                extensions: { prf: { eval: { first: salt } } },
            } });
        } catch (e) { throw passkeyError(e); }
        const ext = cred.getClientExtensionResults?.().prf;
        if (!ext?.enabled && !ext?.results?.first)
            throw new VaultError("input", "This passkey can't unlock secrets (no PRF support on this device).");
        const credentialId = toB64Url(new Uint8Array(cred.rawId));
        // Some authenticators return the PRF output at creation, most only on
        // the first assertion — then ask the new passkey once.
        const output = ext.results?.first ?? (await assertPasskey([{ credentialId, salt }])).output;
        const kdf = JSON.stringify({ alg: "prf-hkdf-sha256", salt: toB64(salt) });
        return { credentialId, kdf, output };
    }

    // One WebAuthn assertion over the given passkeys, each with its own PRF
    // salt. Returns the credential used and its PRF output.
    async function assertPasskey(list) {
        let cred;
        try {
            cred = await navigator.credentials.get({ publicKey: {
                challenge: random(32),
                rpId: location.hostname,
                allowCredentials: list.map(p => ({ type: "public-key", id: fromB64Url(p.credentialId) })),
                userVerification: "required",
                timeout: 120000,
                extensions: { prf: { evalByCredential: Object.fromEntries(list.map(p => [p.credentialId, { first: p.salt }])) } },
            } });
        } catch (e) { throw passkeyError(e); }
        const output = cred.getClientExtensionResults?.().prf?.results?.first;
        if (!output) throw new VaultError("input", "This passkey can't unlock secrets (no PRF output).");
        return { credentialId: toB64Url(new Uint8Array(cred.rawId)), output };
    }

    async function unlockWithPasskey(passkeySlots, extractable) {
        const list = passkeySlots.map(s => ({ credentialId: s.credentialId, salt: fromB64(JSON.parse(s.kdf).salt), slot: s }));
        const { credentialId, output } = await assertPasskey(list);
        const slot = passkeySlots.find(s => s.credentialId === credentialId);
        if (!slot) throw new VaultError("input", "That passkey isn't one of your unlock methods.");
        try {
            const vk = await unwrap(slot, await passkeyKek(output, slot.kdf), extractable);
            markUsed(slot.id);
            return vk;
        } catch { throw new VaultError("input", "This passkey doesn't open your secrets."); }
    }

    // ───── server (through fb.api.vault) ─────
    // The API's 409/400 bodies carry a machine-readable `error`; surface it
    // as the VaultError code so callers can branch on "already-initialized".
    async function call(fn) {
        try { return await fn(); }
        catch (e) {
            let body = null;
            try { body = JSON.parse(e?.body || "null"); } catch { /* not JSON */ }
            throw new VaultError(body?.error || "server", body?.reason || e?.message || "Vault request failed.");
        }
    }
    const getVault   = () => call(() => fb.api.vault.get());
    const addSlot    = (slot) => call(() => fb.api.vault.addSlot(slot));
    const updateSlot = (id, patch) => call(() => fb.api.vault.updateSlot(id, patch));
    const deleteSlot = (id) => call(() => fb.api.vault.deleteSlot(id));
    const markUsed   = (id) => fb.api.vault.touch(id).catch(() => {});

    // ───── unlock / setup ─────
    let _inFlight = null;

    async function ensureUnlocked({ setup = false } = {}) {
        if (inSpace()) throw new VaultError("space", "Secrets are personal for now — they can't be used in a space.");
        if (isUnlocked()) { touch(); return; }
        if (_inFlight) return _inFlight;
        _inFlight = (async () => {
            const vault = await getVault();
            _initialized = vault.initialized;
            if (vault.initialized) _vk = await sessionCopy(await unlockFlow(vault.slots));
            // Setup belongs to writing the first secret. A read that meets
            // no vault (ciphertext left over from a removed vault) must not
            // pop "Set up secrets" — there is nothing a new vault could open.
            else if (setup) await setupFlow();
            else throw new VaultError("locked", "There is no vault to unlock.");
            touch();
            announce();
        })();
        try { await _inFlight; }
        finally { _inFlight = null; }
    }

    // The unlock dialog. Passkey first when this device can use one; the
    // passphrase and the recovery key are always there as the other ways in.
    // Resolves with the vault key (extractable only when asked, for slot
    // management); rejects with VaultError("cancelled").
    async function unlockFlow(slots, { extractable = false, title = "Unlock secrets", intro } = {}) {
        const passSlots = slots.filter(s => s.kind === "passphrase");
        const recSlots  = slots.filter(s => s.kind === "recovery");
        const keySlots  = slots.filter(s => s.kind === "passkey");
        const canPasskey = keySlots.length > 0 && await passkeySupported();

        return dialog({
            title,
            build(body, dlg) {
                body.innerHTML = `
                    ${intro ? `<p class="fb-vault-intro"></p>` : ""}
                    <div class="fb-vault-passkey" ${canPasskey ? "" : "hidden"}>
                        <button type="button" class="btn primary fb-vault-passkey-btn"><sac-icon name="key"></sac-icon> Unlock with a passkey</button>
                        <p class="fb-vault-or">or</p>
                    </div>
                    <label class="fb-vault-field"><span>Passphrase</span>
                        <input type="password" name="pass" autocomplete="current-password"></label>
                    <label class="fb-vault-field" hidden><span>Recovery key — the 24 words</span>
                        <textarea name="words" rows="4" autocomplete="off" autocapitalize="off" spellcheck="false"></textarea></label>
                    <p class="fb-vault-error" role="alert" hidden></p>
                    <button type="button" class="fb-vault-link">Use the recovery key instead</button>`;
                if (intro) body.querySelector(".fb-vault-intro").textContent = intro;
                const [passField, wordsField] = body.querySelectorAll(".fb-vault-field");
                const toggle = body.querySelector(".fb-vault-link");
                let mode = "passphrase";   // | "recovery"
                toggle.hidden = recSlots.length === 0;
                toggle.addEventListener("click", () => {
                    mode = mode === "recovery" ? "passphrase" : "recovery";
                    passField.hidden = mode === "recovery";
                    wordsField.hidden = mode !== "recovery";
                    toggle.textContent = mode === "recovery" ? "Use the passphrase instead" : "Use the recovery key instead";
                    (mode === "recovery" ? wordsField : passField).querySelector("input, textarea").focus();
                });
                // The passkey button runs the same primary action, flagged;
                // the flag is consumed at once, so a failed passkey leaves
                // the dialog on the field that was showing.
                let viaPasskey = false;
                body.querySelector(".fb-vault-passkey-btn").addEventListener("click", () => {
                    viaPasskey = true;
                    dlg.trigger("unlock");
                });
                body.querySelector("input").addEventListener("keydown", (e) => {
                    if (e.key === "Enter") { e.preventDefault(); dlg.trigger("unlock"); }
                });
                return {
                    buttons: [
                        { action: "cancel", label: "Cancel" },
                        { action: "unlock", label: "Unlock", kind: "primary" },
                    ],
                    async onAction() {
                        if (viaPasskey) { viaPasskey = false; return unlockWithPasskey(keySlots, extractable); }
                        if (mode === "recovery") {
                            const entropy = await wordsToEntropy(body.querySelector("textarea").value);
                            for (const s of recSlots) {
                                try { const vk = await unwrap(s, await recoveryKek(entropy, s.kdf), extractable); markUsed(s.id); return vk; }
                                catch { /* next slot */ }
                            }
                            throw new VaultError("input", "This recovery key doesn't open your secrets.");
                        }
                        const pass = body.querySelector("input").value;
                        if (!pass) throw new VaultError("input", "Enter your passphrase.");
                        for (const s of passSlots) {
                            try { const vk = await unwrap(s, await passphraseKek(pass, s.kdf), extractable); markUsed(s.id); return vk; }
                            catch { /* next slot */ }
                        }
                        throw new VaultError("input", "Wrong passphrase.");
                    },
                };
            },
        });
    }

    async function setupFlow() {
        // Step 1 — passphrase.
        const passphrase = await choosePassphrase({
            title: "Set up secrets",
            intro: "Secret blocks are encrypted in this browser before they are saved. Choose a passphrase to unlock them — on any device, in any browser.",
        });

        // Step 2 — the recovery key, confirmed by two of its words.
        const entropy = random(32);
        await showRecoveryKey(await entropyToWords(entropy));

        // Step 3 — create the vault. The VK is extractable only here, long
        // enough to wrap it into its slots; the session copy is re-imported
        // as non-extractable.
        const vk = await crypto.subtle.generateKey({ name: "AES-GCM", length: 256 }, true, ["encrypt", "decrypt"]);
        const passKdf = newPassKdf();
        const recKdf  = newRecKdf();
        const passWrapped = await wrap(vk, await passphraseKek(passphrase, passKdf), "passphrase", passKdf);
        const recWrapped  = await wrap(vk, await recoveryKek(entropy, recKdf), "recovery", recKdf);
        entropy.fill(0);

        try {
            await addSlot({ kind: "passphrase", label: "Passphrase", kdf: passKdf, wrappedKey: toB64(passWrapped), initialize: true });
        } catch (e) {
            // Another device finished setting up first: its vault key wins,
            // this one is discarded — unlock that vault instead.
            if (e.code === "already-initialized") { const v = await getVault(); _vk = await sessionCopy(await unlockFlow(v.slots)); return; }
            throw e;
        }
        try {
            await addSlot({ kind: "recovery", label: "Recovery key", kdf: recKdf, wrappedKey: toB64(recWrapped) });
        } catch (e) {
            window.sac?.toast?.("Your passphrase works, but the recovery key could not be saved. Set up a new one under Secrets.",
                { kind: "warn", duration: 0, title: "Recovery key not saved" });
        }
        _initialized = true;

        // Step 4 — optional: a passkey for day-to-day unlocking.
        if (await passkeySupported()) {
            try {
                const add = await confirmStep({
                    title: "Unlock with a passkey too?",
                    text: "Use your fingerprint, face or device PIN (Windows Hello, Touch ID, your phone) instead of typing the passphrase. The passphrase and recovery key keep working.",
                    yes: "Add a passkey",
                    no: "Not now",
                });
                if (add) await addPasskeyWith(vk);
            } catch (e) {
                if (e?.code !== "cancelled") window.sac?.toast?.(e?.message || "The passkey could not be added.", { kind: "warn" });
            }
        }

        _vk = await sessionCopy(vk);
    }

    // Asks for a new passphrase (twice), with a diceware suggestion.
    function choosePassphrase({ title, intro }) {
        return dialog({
            title,
            build(body, dlg) {
                body.innerHTML = `
                    <p class="fb-vault-intro"></p>
                    <p class="fb-vault-hint">Long beats complicated: a few random words are easy to
                       remember and hard to guess.</p>
                    <label class="fb-vault-field"><span>Passphrase</span>
                        <input type="password" name="pass" autocomplete="new-password"></label>
                    <label class="fb-vault-field"><span>Repeat it</span>
                        <input type="password" name="confirm" autocomplete="new-password"></label>
                    <button type="button" class="fb-vault-link">Suggest one</button>
                    <p class="fb-vault-suggestion" hidden></p>
                    <p class="fb-vault-error" role="alert" hidden></p>`;
                body.querySelector(".fb-vault-intro").textContent = intro;
                const [pass, confirm] = body.querySelectorAll("input");
                const suggestion = body.querySelector(".fb-vault-suggestion");
                body.querySelector(".fb-vault-link").addEventListener("click", async () => {
                    const s = await suggestPassphrase();
                    pass.value = confirm.value = s;
                    suggestion.textContent = s;
                    suggestion.hidden = false;
                });
                confirm.addEventListener("keydown", (e) => {
                    if (e.key === "Enter") { e.preventDefault(); dlg.trigger("next"); }
                });
                return {
                    buttons: [
                        { action: "cancel", label: "Cancel" },
                        { action: "next", label: "Next", kind: "primary" },
                    ],
                    async onAction() {
                        if (pass.value.length < MIN_PASSPHRASE) throw new VaultError("input", `Use at least ${MIN_PASSPHRASE} characters.`);
                        if (pass.value !== confirm.value) throw new VaultError("input", "The two entries don't match.");
                        return pass.value;
                    },
                };
            },
        });
    }

    // Shows the 24 words once, with a printable emergency kit, and continues
    // only after two randomly chosen words are typed back.
    function showRecoveryKey(words) {
        const a = Math.floor(Math.random() * 12), b = 12 + Math.floor(Math.random() * 12);
        return dialog({
            title: "Your recovery key",
            width: "560px",
            build(body) {
                body.innerHTML = `
                    <p>If you forget the passphrase, these 24 words are the only way back to your
                       secrets. <strong>Write them down and keep them somewhere safe, outside
                       Fishbowl.</strong> They are shown once.</p>
                    <ol class="fb-vault-words"></ol>
                    <button type="button" class="fb-vault-link fb-vault-print"><sac-icon name="document"></sac-icon> Print an emergency kit</button>
                    <p>To confirm, enter word <b>${a + 1}</b> and word <b>${b + 1}</b>:</p>
                    <div class="fb-vault-confirm-row">
                        <label class="fb-vault-field"><span>Word ${a + 1}</span><input name="a" autocomplete="off" autocapitalize="off" spellcheck="false"></label>
                        <label class="fb-vault-field"><span>Word ${b + 1}</span><input name="b" autocomplete="off" autocapitalize="off" spellcheck="false"></label>
                    </div>
                    <p class="fb-vault-error" role="alert" hidden></p>`;
                const list = body.querySelector(".fb-vault-words");
                for (const w of words) list.appendChild(Object.assign(document.createElement("li"), { textContent: w }));
                body.querySelector(".fb-vault-print").addEventListener("click", () => printEmergencyKit(words));
                return {
                    buttons: [
                        { action: "cancel", label: "Cancel" },
                        { action: "done", label: "I wrote them down", kind: "primary" },
                    ],
                    async onAction() {
                        const ga = body.querySelector('[name="a"]').value.trim().toLowerCase();
                        const gb = body.querySelector('[name="b"]').value.trim().toLowerCase();
                        if (ga !== words[a] || gb !== words[b]) throw new VaultError("input", "Those words don't match — check what you wrote down.");
                        return true;
                    },
                };
            },
        });
    }

    // A one-page emergency kit, printed from a hidden frame (no popup, no
    // copy left in the page). The words are only in that frame while it
    // prints.
    function printEmergencyKit(words) {
        const frame = document.createElement("iframe");
        frame.setAttribute("aria-hidden", "true");
        frame.style.cssText = "position:fixed;right:0;bottom:0;width:0;height:0;border:0;visibility:hidden";
        document.body.appendChild(frame);
        const doc = frame.contentDocument;
        const esc = (s) => String(s).replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
        const today = window.fb?.format ? fb.format.date(new Date()) : new Date().toISOString().slice(0, 10);
        doc.open();
        doc.write(`<!doctype html><html><head><meta charset="utf-8"><title>Fishbowl — emergency kit</title>
            <style>
                body { font: 12pt/1.5 system-ui, sans-serif; color: #000; margin: 2cm; }
                h1 { font-size: 18pt; margin: 0 0 4pt; }
                .meta { color: #444; margin: 0 0 16pt; }
                ol { columns: 3; column-gap: 1.2cm; padding-left: 1.4em; font: 13pt/1.9 ui-monospace, Consolas, monospace; }
                .box { border: 1px solid #000; padding: 10pt 14pt; margin-top: 18pt; }
            </style></head><body>
            <h1>Fishbowl secrets — emergency kit</h1>
            <p class="meta">${esc(location.origin)} · created ${esc(today)}</p>
            <p>These 24 words are your <strong>recovery key</strong>. With your Fishbowl login they unlock your
               secrets if you forget your passphrase or lose your passkeys.</p>
            <ol>${words.map(w => `<li>${esc(w)}</li>`).join("")}</ol>
            <div class="box">
                <p><strong>Keep this page offline and private</strong> — in a drawer or a safe, not in a photo
                   or a file. Fishbowl cannot recover or reset it. If you create a new recovery key,
                   this page stops working: destroy it.</p>
            </div></body></html>`);
        doc.close();
        const done = () => setTimeout(() => frame.remove(), 1000);
        frame.contentWindow.addEventListener("afterprint", done, { once: true });
        setTimeout(() => { try { frame.contentWindow.focus(); frame.contentWindow.print(); } finally { setTimeout(done, 60000); } }, 50);
    }

    // A two-button question in the vault's dialog style. Resolves true/false.
    function confirmStep({ title, text, yes, no }) {
        return dialog({
            title,
            build(body) {
                body.innerHTML = `<p class="fb-vault-intro"></p><p class="fb-vault-error" role="alert" hidden></p>`;
                body.querySelector(".fb-vault-intro").textContent = text;
                return {
                    buttons: [{ action: "no", label: no }, { action: "yes", label: yes, kind: "primary" }],
                    async onAction() { return true; },
                };
            },
        }).catch((e) => { if (e?.code === "cancelled") return false; throw e; });
    }

    // A <sac-dialog> whose primary action runs onAction(); a thrown
    // VaultError("input") shows inline and keeps the dialog open. Resolves
    // with onAction's result; rejects with VaultError("cancelled").
    function dialog({ title, width, build }) {
        return new Promise((resolve, reject) => {
            const dlg = document.createElement("sac-dialog");
            dlg.setAttribute("title", title);
            if (width) dlg.style.setProperty("--dialog-width", width);
            const body = document.createElement("div");
            body.className = "fb-vault";
            dlg.appendChild(body);
            const spec = build(body, dlg);
            dlg.buttons = spec.buttons;

            const err = () => body.querySelector(".fb-vault-error");
            let result, done = false;
            dlg.beforeAction = async (action) => {
                if (action !== spec.buttons.at(-1).action) return true;
                const primary = spec.buttons.at(-1).action;
                dlg.setDisabled?.(primary, true);
                try {
                    result = await spec.onAction();
                    done = true;
                    return true;
                } catch (e) {
                    if (e instanceof VaultError && e.code === "input") {
                        err().textContent = e.message;
                        err().hidden = false;
                    } else {
                        console.error("[fb.vault]", e);
                        err().textContent = "Something went wrong — try again.";
                        err().hidden = false;
                    }
                    return false;
                } finally {
                    dlg.setDisabled?.(primary, false);
                }
            };
            dlg.addEventListener("sac:action", () => {
                setTimeout(() => {
                    dlg.remove();
                    if (done) resolve(result);
                    else reject(new VaultError("cancelled", "Cancelled."));
                }, 120);
            }, { once: true });
            document.body.appendChild(dlg);
            setTimeout(() => {
                dlg.open();
                requestAnimationFrame(() => body.querySelector("input:not([hidden]), textarea")?.focus());
            }, 0);
        });
    }

    // ───── slot management (the Secrets settings page) ─────

    // Asks for any way in and returns an EXTRACTABLE copy of the vault key
    // for one management step. As a side effect the vault is unlocked for
    // the session too (non-extractable), since the user just proved access.
    async function authorize(reason) {
        if (inSpace()) throw new VaultError("space", "Secrets are personal for now.");
        const vault = await getVault();
        if (!vault.initialized) throw new VaultError("locked", "There is no vault yet.");
        const vk = await unlockFlow(vault.slots, { extractable: true, title: "Confirm it's you", intro: reason });
        if (!isUnlocked()) { _vk = await sessionCopy(vk); announce(); }
        touch();
        return { vk, slots: vault.slots };
    }

    async function addPasskeyWith(vk, slots = null) {
        const existing = (slots ?? (await getVault()).slots).filter(s => s.kind === "passkey").map(s => s.credentialId);
        const reg = await registerPasskey(existing);
        const kek = await passkeyKek(reg.output, reg.kdf);
        const wrapped = await wrap(vk, kek, "passkey", reg.kdf);
        await addSlot({ kind: "passkey", label: guessDeviceLabel(), kdf: reg.kdf, credentialId: reg.credentialId, wrappedKey: toB64(wrapped) });
    }

    // A starting label for a new passkey; the user can rename it.
    function guessDeviceLabel() {
        const ua = navigator.userAgent;
        const os = /Windows/.test(ua) ? "Windows" : /iPhone|iPad/.test(ua) ? "iPhone / iPad" : /Mac OS X/.test(ua) ? "Mac"
                 : /Android/.test(ua) ? "Android" : /Linux/.test(ua) ? "Linux" : "This device";
        const browser = /Edg\//.test(ua) ? "Edge" : /Firefox\//.test(ua) ? "Firefox" : /Chrome\//.test(ua) ? "Chrome" : /Safari\//.test(ua) ? "Safari" : "";
        return browser ? `${os} · ${browser}` : os;
    }

    async function addPasskey() {
        if (!(await passkeySupported())) throw new VaultError("unavailable", "This browser or device can't unlock with a passkey.");
        const { vk, slots } = await authorize("To add a passkey, unlock once more.");
        await addPasskeyWith(vk, slots);
        announce();
    }

    async function changePassphrase() {
        const { vk, slots } = await authorize("To change the passphrase, unlock with your current one — or another way in.");
        const passphrase = await choosePassphrase({ title: "New passphrase", intro: "Choose the new passphrase. The old one stops working." });
        const kdf = newPassKdf();
        const wrapped = toB64(await wrap(vk, await passphraseKek(passphrase, kdf), "passphrase", kdf));
        const current = slots.find(s => s.kind === "passphrase");
        if (current) await updateSlot(current.id, { kdf, wrappedKey: wrapped });
        else await addSlot({ kind: "passphrase", label: "Passphrase", kdf, wrappedKey: wrapped });
        // More than one passphrase slot (older data): keep just the new one.
        for (const s of slots.filter(s => s.kind === "passphrase" && s.id !== current?.id)) await deleteSlot(s.id).catch(() => {});
        announce();
    }

    async function newRecoveryKey() {
        const { vk, slots } = await authorize("To create a new recovery key, unlock once more.");
        const entropy = random(32);
        await showRecoveryKey(await entropyToWords(entropy));
        const kdf = newRecKdf();
        const wrapped = await wrap(vk, await recoveryKek(entropy, kdf), "recovery", kdf);
        entropy.fill(0);
        // Add the new one before removing the old, so there is never a
        // moment without a recovery key.
        await addSlot({ kind: "recovery", label: "Recovery key", kdf, wrappedKey: toB64(wrapped) });
        for (const s of slots.filter(s => s.kind === "recovery")) await deleteSlot(s.id);
        announce();
    }

    async function renameSlot(id, label) {
        await updateSlot(id, { label: String(label).trim().slice(0, 100) });
        announce();
    }

    async function removeSlot(id) {
        try { await deleteSlot(id); }
        catch (e) {
            if (e.code === "last-recoverable-slot")
                throw new VaultError("input", "Keep at least a passphrase or a recovery key — a passkey alone can't be the only way in.");
            throw e;
        }
        const left = (await getVault()).slots;
        if (left.length === 0) { _initialized = false; _vk = null; clearTimeout(_lockTimer); }
        announce();
    }

    async function slots() { return (await getVault()).slots; }

    // ───── block encryption ─────
    // AAD ties each ciphertext to its note and position, so blocks can't be
    // moved between notes or reordered inside one.
    const blockAad = (aad) => enc.encode(`fishbowl-secret-v2|${aad}`);

    async function encryptBlock(plaintext, aad) {
        await ensureUnlocked({ setup: true });
        const iv = random(12);
        const ct = await crypto.subtle.encrypt({ name: "AES-GCM", iv, additionalData: blockAad(aad) }, _vk, enc.encode(plaintext));
        return toB64(concat(iv, new Uint8Array(ct)));
    }

    async function decryptBlock(b64, aad) {
        await ensureUnlocked();
        const bytes = fromB64(b64);
        const pt = await crypto.subtle.decrypt(
            { name: "AES-GCM", iv: bytes.slice(0, 12), additionalData: blockAad(aad) }, _vk, bytes.slice(12));
        return dec.decode(pt);
    }

    fb.vault = {
        isUnlocked, ensureUnlocked, lock, onBeforeLock, status, encryptBlock, decryptBlock,
        slots, passkeySupported, addPasskey, changePassphrase, newRecoveryKey, renameSlot, removeSlot,
        autoLockMinutes, setAutoLock, AUTO_LOCK_CHOICES,
        VaultError,
    };
})();
