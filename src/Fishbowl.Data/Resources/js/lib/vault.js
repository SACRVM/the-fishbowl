/**
 * fb.vault — secret vault v2 (docs/superpowers/specs/2026-09-25-secret-vault-design.md).
 *
 * One random Vault Key (VK, AES-GCM 256) per user encrypts every :::secret
 * block. The server keeps wrapped copies of it — "key slots" — one per way
 * to unlock: a passphrase (PBKDF2-SHA256) or the 24-word recovery key
 * (HKDF-SHA256). The server never sees the VK or anything that opens it.
 *
 * Unlocking unwraps the VK as a NON-extractable CryptoKey held in memory
 * only: a reload asks again, and page script can use the key but not copy
 * it out. Auto-lock after 15 minutes without a secret operation.
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
 *                                  unlock, lock and setup
 *   encryptBlock(text, aad)      → Promise<string>  base64(iv ‖ ct ‖ tag)
 *   decryptBlock(b64, aad)       → Promise<string>
 *   VaultError                   → { code: "cancelled" | "space" | "locked" | "unavailable" }
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
                     encryptBlock: unavailable, decryptBlock: unavailable, VaultError };
        return;
    }

    const AUTO_LOCK_MS    = 15 * 60 * 1000;
    const PBKDF2_ITER     = 600_000;            // OWASP 2023 PBKDF2-SHA256
    const MIN_PASSPHRASE  = 12;
    const SUGGEST_WORDS   = 6;                  // 6 × 11 bits ≈ 66 bits
    const RECOVERY_WORDS  = 24;                 // 256-bit entropy + 8-bit checksum (BIP-39)
    const RECOVERY_INFO   = "fishbowl-vault-recovery-v2";
    const WORDLIST_URL    = "/js/vendor/bip39/english.txt";
    // The recovery words ARE the key. A changed list (a disk-overlay mod, a
    // bad upgrade) would silently turn every written-down key into garbage,
    // so the list is pinned by hash, not just by path.
    const WORDLIST_SHA256 = "2f5eed53a4727b4bf8880d8f3f199efc90e58503646d9ff8eff3a2ed3b24dbda";

    const enc = new TextEncoder();
    const dec = new TextDecoder();
    const toB64   = (bytes) => btoa(String.fromCharCode(...bytes));
    const fromB64 = (s) => Uint8Array.from(atob(s), c => c.charCodeAt(0));
    const random  = (n) => crypto.getRandomValues(new Uint8Array(n));
    const concat  = (a, b) => { const o = new Uint8Array(a.length + b.length); o.set(a); o.set(b, a.length); return o; };
    const hex     = (buf) => [...new Uint8Array(buf)].map(b => b.toString(16).padStart(2, "0")).join("");

    let _vk = null;          // non-extractable CryptoKey while unlocked
    let _initialized = null; // does a vault exist? null = not asked yet
    let _lockTimer = null;

    function inSpace() { return window.fb?.context?.get?.().type === "space"; }

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
        _lockTimer = setTimeout(() => lock(), AUTO_LOCK_MS);
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
        const base = await crypto.subtle.importKey("raw", entropy, "HKDF", false, ["deriveKey"]);
        return crypto.subtle.deriveKey(
            { name: "HKDF", hash: "SHA-256", salt: fromB64(p.salt), info: enc.encode(RECOVERY_INFO) },
            base, { name: "AES-GCM", length: 256 }, false, ["wrapKey", "unwrapKey"]);
    }

    async function wrap(vk, kek, kind, kdf) {
        const iv = random(12);
        const ct = await crypto.subtle.wrapKey("raw", vk, kek, { name: "AES-GCM", iv, additionalData: slotAad(kind, kdf) });
        return concat(iv, new Uint8Array(ct));
    }

    async function unwrap(slot, kek) {
        const bytes = fromB64(slot.wrappedKey);
        return crypto.subtle.unwrapKey("raw", bytes.slice(12), kek,
            { name: "AES-GCM", iv: bytes.slice(0, 12), additionalData: slotAad(slot.kind, slot.kdf) },
            { name: "AES-GCM", length: 256 }, false, ["encrypt", "decrypt"]);
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
    const getVault = () => call(() => fb.api.vault.get());
    const addSlot  = (slot) => call(() => fb.api.vault.addSlot(slot));
    const markUsed = (id) => fb.api.vault.touch(id).catch(() => {});

    // ───── unlock / setup ─────
    let _inFlight = null;

    async function ensureUnlocked({ setup = false } = {}) {
        if (inSpace()) throw new VaultError("space", "Secrets are personal for now — they can't be used in a space.");
        if (isUnlocked()) { touch(); return; }
        if (_inFlight) return _inFlight;
        _inFlight = (async () => {
            const vault = await getVault();
            _initialized = vault.initialized;
            if (vault.initialized) await unlockFlow(vault.slots);
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

    async function unlockFlow(slots) {
        const passSlots = slots.filter(s => s.kind === "passphrase");
        const recSlots  = slots.filter(s => s.kind === "recovery");

        const key = await dialog({
            title: "Unlock secrets",
            build(body, dlg) {
                body.innerHTML = `
                    <p>Enter your secrets passphrase.</p>
                    <label class="fb-vault-field"><span>Passphrase</span>
                        <input type="password" name="pass" autocomplete="current-password"></label>
                    <label class="fb-vault-field" hidden><span>Recovery key — the 24 words</span>
                        <textarea name="words" rows="4" autocomplete="off" autocapitalize="off" spellcheck="false"></textarea></label>
                    <p class="fb-vault-error" role="alert" hidden></p>
                    <button type="button" class="fb-vault-link">Use the recovery key instead</button>`;
                const [passField, wordsField] = body.querySelectorAll(".fb-vault-field");
                const toggle = body.querySelector(".fb-vault-link");
                let useRecovery = false;
                toggle.hidden = recSlots.length === 0;
                toggle.addEventListener("click", () => {
                    useRecovery = !useRecovery;
                    passField.hidden = useRecovery;
                    wordsField.hidden = !useRecovery;
                    toggle.textContent = useRecovery ? "Use the passphrase instead" : "Use the recovery key instead";
                    (useRecovery ? wordsField : passField).querySelector("input, textarea").focus();
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
                        if (useRecovery) {
                            const entropy = await wordsToEntropy(body.querySelector("textarea").value);
                            for (const s of recSlots) {
                                try { const vk = await unwrap(s, await recoveryKek(entropy, s.kdf)); markUsed(s.id); return vk; }
                                catch { /* next slot */ }
                            }
                            throw new VaultError("input", "This recovery key doesn't open your secrets.");
                        }
                        const pass = body.querySelector("input").value;
                        if (!pass) throw new VaultError("input", "Enter your passphrase.");
                        for (const s of passSlots) {
                            try { const vk = await unwrap(s, await passphraseKek(pass, s.kdf)); markUsed(s.id); return vk; }
                            catch { /* next slot */ }
                        }
                        throw new VaultError("input", "Wrong passphrase.");
                    },
                };
            },
        });
        _vk = key;
    }

    async function setupFlow() {
        // Step 1 — passphrase.
        const passphrase = await dialog({
            title: "Set up secrets",
            build(body, dlg) {
                body.innerHTML = `
                    <p>Secret blocks are encrypted in this browser before they are saved. Choose a
                       passphrase to unlock them — on any device, in any browser.</p>
                    <p class="fb-vault-hint">Long beats complicated: a few random words are easy to
                       remember and hard to guess.</p>
                    <label class="fb-vault-field"><span>Passphrase</span>
                        <input type="password" name="pass" autocomplete="new-password"></label>
                    <label class="fb-vault-field"><span>Repeat it</span>
                        <input type="password" name="confirm" autocomplete="new-password"></label>
                    <button type="button" class="fb-vault-link">Suggest one</button>
                    <p class="fb-vault-suggestion" hidden></p>
                    <p class="fb-vault-error" role="alert" hidden></p>`;
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

        // Step 2 — the recovery key, confirmed by two of its words.
        const entropy = random(32);
        const words = await entropyToWords(entropy);
        const a = Math.floor(Math.random() * 12), b = 12 + Math.floor(Math.random() * 12);
        await dialog({
            title: "Your recovery key",
            width: "560px",
            build(body) {
                body.innerHTML = `
                    <p>If you forget the passphrase, these 24 words are the only way back to your
                       secrets. <strong>Write them down and keep them somewhere safe, outside
                       Fishbowl.</strong> They are shown once.</p>
                    <ol class="fb-vault-words"></ol>
                    <p>To confirm, enter word <b>${a + 1}</b> and word <b>${b + 1}</b>:</p>
                    <div class="fb-vault-confirm-row">
                        <label class="fb-vault-field"><span>Word ${a + 1}</span><input name="a" autocomplete="off" autocapitalize="off" spellcheck="false"></label>
                        <label class="fb-vault-field"><span>Word ${b + 1}</span><input name="b" autocomplete="off" autocapitalize="off" spellcheck="false"></label>
                    </div>
                    <p class="fb-vault-error" role="alert" hidden></p>`;
                const list = body.querySelector(".fb-vault-words");
                for (const w of words) list.appendChild(Object.assign(document.createElement("li"), { textContent: w }));
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

        // Step 3 — create the vault. The VK is extractable only here, long
        // enough to wrap it into both slots; the session copy is re-imported
        // as non-extractable.
        const vk = await crypto.subtle.generateKey({ name: "AES-GCM", length: 256 }, true, ["encrypt", "decrypt"]);
        const passKdf = JSON.stringify({ alg: "pbkdf2-sha256", iter: PBKDF2_ITER, salt: toB64(random(16)) });
        const recKdf  = JSON.stringify({ alg: "hkdf-sha256", salt: toB64(random(16)) });
        const passWrapped = await wrap(vk, await passphraseKek(passphrase, passKdf), "passphrase", passKdf);
        const recWrapped  = await wrap(vk, await recoveryKek(entropy, recKdf), "recovery", recKdf);
        entropy.fill(0);

        try {
            await addSlot({ kind: "passphrase", label: "Passphrase", kdf: passKdf, wrappedKey: toB64(passWrapped), initialize: true });
        } catch (e) {
            // Another device finished setting up first: its vault key wins,
            // this one is discarded — unlock that vault instead.
            if (e.code === "already-initialized") { const v = await getVault(); return unlockFlow(v.slots); }
            throw e;
        }
        try {
            await addSlot({ kind: "recovery", label: "Recovery key", kdf: recKdf, wrappedKey: toB64(recWrapped) });
        } catch (e) {
            window.sac?.toast?.("Your passphrase works, but the recovery key could not be saved. Set up a new one in Settings.",
                { kind: "warn", duration: 0, title: "Recovery key not saved" });
        }

        _initialized = true;
        const raw = new Uint8Array(await crypto.subtle.exportKey("raw", vk));
        _vk = await crypto.subtle.importKey("raw", raw, { name: "AES-GCM" }, false, ["encrypt", "decrypt"]);
        raw.fill(0);
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
                    else reject(new VaultError("cancelled", "Unlocking secrets was cancelled."));
                }, 120);
            }, { once: true });
            document.body.appendChild(dlg);
            setTimeout(() => {
                dlg.open();
                requestAnimationFrame(() => body.querySelector("input:not([hidden]), textarea")?.focus());
            }, 0);
        });
    }

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

    fb.vault = { isUnlocked, ensureUnlocked, lock, onBeforeLock, status, encryptBlock, decryptBlock, VaultError };
})();
