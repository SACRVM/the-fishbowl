# Secret vault v2 — design

Status: **approved** · 2026-09-25 (open questions decided the same day)

## Problem

Fishbowl is where the user keeps the things they must not forget — including
secrets. The v1 vault (`js/lib/vault.js`) makes that unsafe to rely on:

- **The key is tied to one browser profile.** The PBKDF2 salt lives in
  `localStorage`. Clear site data, reinstall, switch browser or device → the
  same passphrase derives a different key, and every secret written before is
  gone. No recovery.
- **Secrets don't travel.** Written on the laptop, unreadable on the phone.
  In a space, unreadable by every other member.
- **The passphrase has to carry everything.** It is the only key, so it has to
  be strong — and a strong passphrase is exactly the kind of secret the user
  wants Fishbowl to remember for them. Circular.
- **A wrong passphrase isn't detected.** It derives a different key; the user
  only finds out when blocks fail to decrypt.

v1 is not in real use (confirmed 2026-09-25), so v2 **replaces it outright —
no migration, no compatibility path**.

## Goals

1. Secrets survive clearing the browser, and open on every device with the
   same credentials.
2. A lost passphrase is recoverable — by something that lives *outside*
   Fishbowl.
3. Day-to-day unlock is cheap: fingerprint / face / Windows Hello where the
   platform supports it; a memorable passphrase everywhere else.
4. The server — and anyone holding the DB file, a backup or a snapshot — never
   sees plaintext secrets or a key that opens them.
5. Everything the vault needs lives in the context DB, so the folder-per-context
   cold import (`users/<id>/` onto another instance) keeps working.

## Non-goals

- Protection against a malicious or compromised server serving modified
  JavaScript. That is inherent to in-browser crypto; self-hosting is the
  mitigation.
- Protection against same-origin XSS while the vault is unlocked. Reduced (the
  key is non-extractable, see below), not eliminated.
- Sharing secrets between space members (phase 4, needs its own design call).
- Secrets outside notes (contacts, events, apps).

## Threat model in one table

| Attacker has | v2 outcome |
|---|---|
| DB file / backup / `snapshot-data` output | Ciphertext + wrapped keys only. Offline attack against the passphrase slot is bounded by the KDF cost; recovery and passkey slots are 256-bit. |
| An API key (MCP agent, bot) | Nothing — `SecretStripper` still removes blocks and `content_secret` from every Bearer/MCP path, and vault endpoints are cookie-only. |
| The user's logged-in browser, vault locked | Ciphertext. Needs a credential to unlock. |
| The user's logged-in browser, vault unlocked | Can decrypt while unlocked (by design). Cannot copy the raw key out. |

## Key hierarchy

```
            ┌───────────── Vault Key (VK) ──────────────┐
            │ random 256-bit AES-GCM, one per context,   │
            │ never stored in the clear, never leaves    │
            │ the browser, non-extractable once unwrapped│
            └───────▲──────────────▲──────────────▲──────┘
                    │ wrapped by   │ wrapped by   │ wrapped by
        ┌───────────┴───┐  ┌───────┴───────┐  ┌───┴────────────────┐
        │ passphrase    │  │ recovery key  │  │ passkey (PRF)      │
        │ PBKDF2-SHA256 │  │ 256-bit random│  │ WebAuthn PRF output│
        │ + server salt │  │ → HKDF        │  │ → HKDF             │
        └───────────────┘  └───────────────┘  └────────────────────┘
                 each wrapped copy = one row in vault_keyslots
```

- **One VK per context.** It encrypts every secret block in that context.
  Changing a passphrase or adding a passkey re-wraps the VK; no note is ever
  re-encrypted.
- **Keyslots** are the wrapped copies. Any one slot unlocks the vault.
  AES-GCM wrapping means a wrong credential fails authentication cleanly —
  "wrong passphrase" is detected, not guessed.
- **Rule:** a vault must always keep at least one passphrase or recovery slot.
  A passkey is a convenience, never the only way in.

### Slot kinds

| Kind | KEK derivation | Notes |
|---|---|---|
| `passphrase` | PBKDF2-SHA256, 600,000 iterations, 16-byte random salt **stored in the slot row** | WebCrypto-native. Params are per slot, so iterations can be raised later by re-wrapping. Setup suggests a 5–6 word diceware passphrase (EFF long list, vendored); length beats complexity. Minimum 12 characters stays. |
| `recovery` | HKDF-SHA256 over a 256-bit random key, info `fishbowl-vault-recovery-v2` | Shown **once** at setup as 24 words (BIP-39 English list) plus a printable emergency-kit page. High entropy, so no slow KDF needed. Regenerating creates a new slot and deletes the old one. |
| `passkey` | WebAuthn `prf` extension, eval salt = slot salt; 32-byte output → HKDF-SHA256, info `fishbowl-vault-passkey-v2` | Registered against the Fishbowl origin (rpId = host). Supported in current Chrome/Edge/Safari/Firefox; per-platform authenticator support (Windows Hello, iCloud Keychain, Android, security keys) varies and **must be probed at runtime** — no PRF, no passkey offer. |

## Storage

### `vault_keyslots` (user/space DB schema v7)

| Column | Type | |
|---|---|---|
| `id` | TEXT PK | ULID |
| `kind` | TEXT | `passphrase` \| `recovery` \| `passkey` |
| `label` | TEXT | user-facing, e.g. "MacBook Touch ID" |
| `kdf` | TEXT | JSON: `{ "alg": "pbkdf2-sha256", "iter": 600000, "salt": "<b64>" }` / `{ "alg": "hkdf-sha256", "salt": "<b64>" }` |
| `credential_id` | TEXT NULL | passkey only (b64url) |
| `wrapped_key` | BLOB | `iv ‖ AES-GCM(KEK, VK) ‖ tag`, AAD = `fishbowl-vault-v2\|<kind>\|<kdf JSON>` (binds the ciphertext to its slot kind and exact parameters; the slot id can't be used because the server assigns it after wrapping) |
| `created_at` / `last_used_at` | TEXT | ISO-8601 |

In the context DB, not `system.db`: the vault moves with the folder. The same
table in a space DB is the phase-4 hook.

### Secret blocks (`notes.content_secret`)

The inline syntax is unchanged (`:::secret` … `:::end` in the editor, a
`:::secret#N:::end` marker in the stored `content`). The envelope becomes:

```json
{ "v": 2, "blocks": ["<b64 iv ‖ ct ‖ tag>", "…"] }
```

Each block is AES-GCM under the VK with **AAD = `fishbowl-secret-v2|<note-id>|<index>`**,
so ciphertext can't be swapped between notes or reordered within one. Notes
are created before their first secret is written (`createNote` posts an empty
note), so the id is always known when encrypting; if it isn't, encryption
fails closed.

`v: 1` envelopes are not read. (No v1 data exists; a dev DB that has some
shows the block as "unreadable legacy secret" and the user re-types it.)

## Session behaviour

- Unlock = unwrap the VK with `extractable: false`. It lives **in memory
  only** — no `sessionStorage` (v1 stored the raw key there).
- A reload or a new tab asks again. With a passkey that is one touch; that
  cost is what makes memory-only acceptable.
- **Auto-lock** after 15 minutes without a secret operation (configurable in
  the vault settings), and on `fb.vault.lock()` from the account menu.
- Concurrent unlock requests coalesce onto one prompt (kept from v1).

## Flows

**First secret in a context** → "Set up secrets" dialog:
1. Generate VK.
2. Choose a passphrase (diceware suggestion button, strength hint).
3. Show the recovery key (24 words + "print emergency kit"); continue only
   after the user confirms two randomly chosen words.
4. If PRF is available: "Also unlock with Touch ID / Windows Hello?" → one
   WebAuthn registration.
5. Upload the slots; encrypt the block; save.

**Unlock** → passkey first if a slot exists and PRF works on this device,
with "use passphrase" and "use recovery key" as alternatives in the same
dialog.

**Settings → Secrets** (new section in the existing settings area):
list slots (kind, label, created, last used) · add passkey · change
passphrase · regenerate recovery key · remove slot (guarded by the rule
above) · auto-lock timeout · lock now.

**Lost everything** (no passphrase, no recovery key, no passkey) → the
secrets are gone. Stated plainly in the setup dialog, as v1 did.

## Server surface

Cookie-only; Bearer requests get **403** (agents never touch the vault —
same rule as export and reindex).

| Route | |
|---|---|
| `GET /api/v1/vault` | `{ initialized, slots: [{ id, kind, label, kdf, credentialId, wrappedKey, createdAt, lastUsedAt }] }` — wrapped keys are safe to hand to the owner |
| `POST /api/v1/vault/slots` | add a slot (first call initializes the vault) |
| `PATCH /api/v1/vault/slots/{id}` | label, or re-wrapped key after passphrase change |
| `DELETE /api/v1/vault/slots/{id}` | refuses to remove the last passphrase/recovery slot (409) |
| `POST /api/v1/vault/slots/{id}/used` | bump `last_used_at` |

Space-nested mirrors are **not** added in v2 (phase 4). The server validates
shape and sizes (existing `MaxContentSecretBytes`, a small cap on slot count
and field sizes) but cannot and does not check cryptographic content.

Unchanged invariants: `SecretStripper` on every MCP/Bearer path, secret-free
FTS and embeddings, and the note title never derived from inside a secret
block (the notes-view rule from #5).

## Spaces in v2

Secrets are **hidden in space workspaces**: nothing in a space offers or
points to them (the editor has no secret toolbar button today; a later one
is not shown in spaces), and no vault setup is ever triggered there. As a
safety net, a hand-typed `:::secret` block in a space note is **refused on
save** with a clear message ("Secrets are personal for now") — storing it
would put text that looks masked into the space DB in plaintext. Phase 4
decides the sharing model (likely: a space VK wrapped per member with an ECDH
key pair per user).

## Phases

1. **Core** — schema v7 + slots API, VK, passphrase + recovery slots,
   envelope v2 with AAD, memory-only session + auto-lock, setup and unlock
   dialogs, v1 vault code deleted (`vault.js` rewritten, `fb-vault-*` CSS
   replaced by kit dialogs), space save refused.
2. **Passkey** — PRF probe, registration, passkey-first unlock. *(done
   2026-09-26: kdf `{ "alg": "prf-hkdf-sha256", "salt" }`, the salt is both
   the PRF eval input and the HKDF salt; offered at setup when supported.)*
3. **Settings → Secrets** — slot management UI, emergency kit printout,
   auto-lock setting. *(done 2026-09-26: `#/secrets`; slot changes
   re-authenticate, since the session key is non-extractable; auto-lock is
   `users.vault_auto_lock_minutes`, 5/15/30/60/240.)*
4. **Spaces** — separate design.

## Testing

- **Server (xUnit):** schema v7 migration; slot repository CRUD; last-slot
  guard; Bearer → 403 on every vault route; envelope size caps;
  `SecretStripInvariantTests` extended to v2 envelopes.
- **UI (Playwright, shared host):** setup → write secret → reload → unlock
  with passphrase; unlock with recovery key; wrong passphrase shows an error
  and doesn't unlock; the stored note (via API) contains only markers and
  ciphertext; moving ciphertext to another note fails (AAD); space save is
  refused. Passkey via Chromium's virtual authenticator (CDP
  `WebAuthn.addVirtualAuthenticator` with PRF).

## Decisions (2026-09-25)

1. **Auto-lock:** 15 minutes without a secret operation.
2. **Recovery key:** 24 BIP-39 English words.
3. **KDF:** PBKDF2-SHA256 now; Argon2id stays possible later through the
   per-slot `kdf` JSON, without a format change.
4. **Spaces:** hidden (see "Spaces in v2"), with refuse-on-save as the
   safety net.
