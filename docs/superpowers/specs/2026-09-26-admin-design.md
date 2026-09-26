# Administration, approval and messages — design

Status: **A1 and A2 done** (2026-09-26) — pending approval, sign-up policy,
system messages with the chat fan-out, the Users app (add local user,
reset, quota, admin, disable/enable), System settings and the admin log
are built; A3 is next. Revised after the first review. Prompted by
the desktop's install policy (`2026-09-26-desktop-apps-design.md`): a policy
needs someone who sets it.

## Three roles, three scopes

| Role | Scope | Who | Can |
|---|---|---|---|
| **User** | their personal workspace | everyone approved | everything in it, incl. installing apps. What they install there is theirs alone. |
| **Space owner** | one space | the space's creator (transferable) | members, roles, colour, **apps for the space**. Sharing a space shares its apps. |
| **Admin** | the instance | `users.is_admin` (at least one, always) | approve and manage users, quotas, system settings. Sees the admin apps on their desktop. |

Ownership is the admin role *inside* a workspace. The instance admin is a
separate, rarer role. An admin is not automatically an owner of anything,
and doesn't see into spaces they aren't a member of.

## What exists today

- `users.is_admin` (system schema). Set **only** for the local user created
  in `/setup`. An instance set up with Google only has **no admin at all**.
- Backend, cookie + `IsAdmin`, Bearer 403:
  - `/api/v1/admin/config` (list, set, delete over `ConfigSchema.Editable`,
    with secrets redacted)
  - cold user import
  - password reset (a temporary password, shown once)

  No UI uses any of it.
- **Sign-up is open.** `OnTicketReceived` provisions a full account, with a
  personal DB and storage, for *any* Google account that completes OAuth.
  The production instance is internet-facing.

## Principles

1. **The admin runs the instance, not the people on it.** No admin endpoint
   returns another user's notes, files, secrets or space content. The admin
   sees metadata: name, e-mail, sign-in method, storage used, quota, last
   sign-in, state.
2. **Always at least one admin.** Demoting, disabling or deleting the last
   admin gets a 409 (`last-admin`).
3. **Cookie-only, forever.** No admin action through a Bearer token or MCP.
4. **Every admin action is recorded** (actor, action, target, time). The
   record holds IDs only, no PII.

## Sign-up: approval first

A new sign-in (Google, or a local sign-up if enabled) creates the account in
state **`pending`**:

- **The account is a shell.** The `users` row and its mapping exist, but
  there is **no personal DB, no files folder and no API key**. Nothing can
  be stored, so nothing can be abused.
- **The user sees one page:** "Your account is waiting for approval. You'll
  get access as soon as an admin approves it." That page and sign-out are
  everything a pending user can reach. Every `/api` and `/mcp` call answers
  403 `pending`.
- **Every admin gets a message** (see Messages): "Ada (ada@…) wants to join —
  Approve / Reject".
- **Approve:**
  - The dialog sets the **quota** at the same moment, pre-filled from
    `Files:DefaultUserQuota`, which is editable.
  - It optionally makes the user an admin.
  - The account becomes `active`: the personal DB is created on first open,
    as today.
  - The user gets a message and, if they linked Discord, a DM (see
    Messages).
- **Reject:** deletes the shell (row + mapping). A later sign-in starts a new
  request. The admin can instead **Block**, which keeps the row as `blocked`,
  so the same Google account can't ask again.

System config `Auth:SignUp`:

| Value | Meaning |
|---|---|
| `approval` | **default** — new accounts wait for an admin |
| `open` | new accounts are active immediately (a private LAN instance) |
| `closed` | no new accounts at all; the admin adds users |

Plus the optional `Auth:AllowedEmailDomains`: sign-ins outside these domains
are refused outright, without even a pending request (spam guard).

**Upgrade:** existing users are `active`, so nothing changes for them. From
the upgrade on, new sign-ins are `pending`. That closes the open door on
production without locking anyone out.

**Bootstrap:**

- Setup with a local user: that user is the admin, as today.
- Google-only setup: if no admin exists, the **first** sign-in becomes an
  active admin, with no approval (there's nobody to approve).
- Recovery: `tools/set-admin <userId|username>` for an operator with shell
  access.

## Quotas

- **A per-user quota**, set at approval and editable in Users. It covers the
  personal workspace (DB + files) **and every space the user owns**. A
  space's storage is billed to its owner, so sharing doesn't multiply
  anyone's allowance.
- **Defaults** come from the Files limits:
  - `Files:DefaultUserQuota`
  - `0` means unlimited
- **Enforcement** is where the Files backend already enforces context
  limits, now with the owner's quota as the ceiling.
- **Warning:** the user gets a message at 90%.
- **Admin view:** Users shows used / quota. That's the one number an admin
  sees about a person's storage, never what's in it.

## Messages

Fishbowl gets a small **system inbox**. It carries notifications from the
system to a person, not chat between people.

- **Storage:** a `messages` table in `system.db`, with columns:
  - `id`, `recipient_id`, `kind`
  - `subject_type` / `subject_id` (what it's about)
  - `data` JSON: IDs and numbers only, and the display text is rendered
    client-side
  - `created_at`, `read_at`, `done_at`

  Actionable messages resolve: once another admin approves Ada, the request
  is marked done for **every** admin.
- **Kinds at start:**

  | Kind | To |
  |---|---|
  | `user.pending` | all admins; action: approve / reject / block |
  | `user.approved` | the user |
  | `quota.warning` | the user (at 90%) |
  | `space.added` | a user who was added to a space |
  | `app.update` | a sandboxed app on your desktop has an update to confirm |

  Later: digest, reminders' in-app twin, space invitations to accept.
- **UI:**
  - an envelope icon with an unread badge in the nav toolbar, on every page
  - a **Messages** tile on the desktop (with the same badge)
  - The list shows each message with its actions inline, and read/done state
    syncs across devices.
  - A pending user sees none of it, only the waiting page.
- **Delivery elsewhere:** if the recipient has a linked bot channel, the
  message is also sent there by the existing `IBotClient` fan-out (Discord
  DM today). The DM carries a subject line only, never content. There is no
  e-mail and no SMTP.
- **API:**
  - `GET /api/v1/messages` (`?unread=1`)
  - `POST /api/v1/messages/{id}/read`
  - actions go through their own endpoints (e.g. `POST /api/v1/admin/users/{id}/approve`); the message is only the pointer

  Cookie-only for now.

## The admin as apps on the desktop

The admin tools are **built-in apps** in the desktop registry, flagged
`requires: "admin"`. They appear as tiles, in the burger and in Ctrl/⌘ K
**only for admins**. For everyone else they don't exist (no dead links).
Routes `#/admin/...` are personal-workspace only; in a space they show the
"Open in Personal" notice.

- **Users**
  - The list shows:
    - pending requests first
    - name, e-mail, sign-in methods, admin badge
    - state (pending / active / disabled / blocked)
    - storage used / quota
    - created, last sign-in
  - Actions:
    - **Approve** (with quota), **Reject**, **Block**
    - **Add local user:** username + temporary password, shown once, created
      active
    - **Reset password** (existing endpoint)
    - **Quota**
    - **Make admin / Remove admin** (last-admin guard)
    - **Disable / Enable:** blocks sign-in, ends live sessions, revokes the
      user's keys, keeps the data
    - **Delete:** "archive first" is checked by default (a ZIP to
      `archive/users/`), the same machinery and retention as archived
      spaces. Spaces the user solely owns must be handed over or deleted
      first; the dialog lists them.
    - **Import folder** (existing endpoint)
- **System settings**
  - Rendered from `ConfigSchema.Editable`, grouped by prefix:
    - Sign-in: Google, `Auth:*`
    - Integrations: Discord
    - Digest
    - Logging
    - Files & quotas
    - Archive
    - Apps: `Apps:Install`, `Apps:Trusted`, `Apps:AllowedOrigins`,
      `Apps:StoreOwners`
  - Secrets are write-only (shown as "set", replaceable).
  - Keys that need a restart say so after saving.
  - New keys join `ConfigSchema.Editable` with validation, so the app grows
    without new UI code.
- **System**
  - version, data size by context type
  - embedding model status, the scheduler's last tick
  - archived spaces and users (Download / Restore / Delete, days left)
  - the admin log

## Server

- **System schema** next version:
  - `users` gains:
    - `state` (`pending`|`active`|`disabled`|`blocked`, default
      `active` for existing rows)
    - `quota_bytes` (null = default)
    - `approved_by`, `approved_at`, `last_sign_in_at`
  - new tables:
    - `messages`
    - `admin_audit`: `id`, `actor_id`, `action`, `target_type`,
      `target_id`, `at`
- **Sign-in paths** (Google `OnTicketReceived`, local login) apply, in
  order:
  1. the domain allow-list
  2. blocked / disabled
  3. admin bootstrap
  4. the `Auth:SignUp` policy (new account → `pending` + `user.pending`
     message)

  A pending or disabled principal carries the state as a claim. The cookie
  validation re-checks it, so approval and disabling take effect at the next
  request.
- **Gate:** one middleware keeps a pending principal to the waiting page,
  `/api/v1/me` and sign-out. `DatabaseFactory` refuses to create a context
  DB for a non-active user, as a second wall.
- **API** under `/api/v1/admin`, extending `AdminApi` with:
  - `GET users`
  - `POST users/{id}/approve|reject|block`
  - `PATCH users/{id}` (quota, admin, disabled)
  - `DELETE users/{id}`
  - `audit`, `archive`, `system`

  All of it `IsCookieAdminAsync`, and every write goes through one
  `RecordAdminActionAsync`.

## Phases

- **A1 — Close the door**, first, independent of Files and Desktop. It is
  the fix for production:
  - pending state + waiting page + the gate
  - `Auth:SignUp` / `Auth:AllowedEmailDomains`
  - admin bootstrap for Google-only installs, `tools/set-admin`
  - `messages` with `user.pending` / `user.approved`, the envelope + list
  - the Users app with approve / reject / block
  - the audit table

  Quota at approval is stored now and enforced once the Files data-lifecycle
  phase lands.
- **A2 — Manage (done):**
  - rest of Users (add local, reset, quota, admin, disable)
  - System settings app
  - bot fan-out for messages
  - `quota.warning` (moved to the Files data-lifecycle phase, which
    enforces quotas)
- **A3 — Lifecycle:**
  - delete with archive
  - archive + System app, shared with the space-archive work from Files
- The admin apps become desktop tiles when **Desktop D1** lands. Until then
  they sit in the settings nav, admins only.

## Tests

- **Sign-up** under each policy:
  - a pending user reaches no API (403 `pending`) and no context DB is
    created
  - approve activates
  - reject lets the user ask again; block doesn't
  - the domain allow-list refuses without a request
- **Admins:**
  - Google-only bootstrap
  - the last-admin guard
  - disabling ends a live session at its next request
- **Messages:**
  - `user.pending` goes to every admin and resolves for all on approve
  - a user sees only their own messages
  - Bearer 403
- **Access:**
  - Bearer 403 on every admin route
  - a non-admin gets 403 and sees no admin tile or nav entry (UI test)
  - an invariant test walks the admin routes and asserts that no note,
    file or secret field appears in any response
- **Audit:** exactly one row per admin write, IDs only.
- **Quota:** a space's usage counts against its owner.

## Open question

- **Messages between people** (a note to a space's members, "look at
  this"): out of scope for now, or wanted? The table is shaped so a
  user-to-user kind could be added later without a migration.
