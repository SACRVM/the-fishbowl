# Desktop and external apps — design

Status: **decided** (2026-09-26), see Decisions at the end. Related:
`2026-09-26-admin-design.md` (roles, approval, messages). Builds on the Files app
(`2026-09-26-files-app-design.md`) and takes its model from
[`SACRVM/sacrvm-desktop`](https://github.com/SACRVM/sacrvm-desktop), the kit's
reference host.

## Problem

`#/` is a static hub: three hand-written tiles (Notes, Todos, Calendar), no
personalisation, no way to add anything. Fishbowl already owns the parts a
desktop needs — a server, real accounts, workspaces, and (with Files) a real
file store — but nothing lets an app someone else wrote run on it and use
them.

`sacrvm-desktop` shows the shape: paste a repo URL, the desktop reads the
app's `app.json` (data only), you confirm, the app appears as a tile and
runs through the kit's app contract (`sac.apps`, `context.fs`,
`context.files`, `context.identity`, `context.host`). What it lacks is
exactly what Fishbowl has: storage that isn't one browser's `localStorage`,
an identity that is an account, and files that follow you.

## Goals

1. A **modern desktop** at `#/`: tiles for the built-in apps and installed
   ones, sized and coloured by the user, reorderable, with live badges, and a
   Ctrl/⌘ K palette that opens any app and runs common actions.
2. **Install an external app by URL**, the kit's way (`sac.apps.inspect` →
   confirm → `sac.apps.add`), and run it through the unchanged kit contract,
   so an app written for `sacrvm-desktop` runs here too, and the other way
   round.
3. Installed apps **benefit from Fishbowl's backend**: `context.files` opens
   and saves in the user's Files, `context.fs` is stored on the server,
   `context.identity` is the real account (read-only).
4. **A sandboxed app never gets the account.** It sees what the user hands
   it: a picked file, its own folder, a name. It never sees notes, secrets,
   other apps' data, or the session.
5. **Your own apps integrate fully.** An app the user trusts (typically one
   they wrote) can run *trusted*, same-realm, with no sandbox, exactly like
   on `sacrvm-desktop`. The user chooses per install; the default is the
   sandbox.

## Non-goals

- Server-side code from apps. No plugin loader comes back (slimming plan tier
  A, decision 7). Apps are client-side web components; the server only
  stores records and serves bytes.
- Trusted (same-realm) apps on a **space** desktop. The code would run with
  every member's session, so an owner could read members' personal data
  through it. Space apps are always sandboxed.
- Sideloading a local folder as an app. A self-authored app is published
  (GitHub Pages or any HTTPS origin with CORS) and installed like any other.
- Widgets that render app code on the desktop itself. Badges come from the
  host, never from a foreign app.

## What changes in the architecture decisions

`docs/slimming-plan.md` decision 5 ("No install-by-URL for foreign apps, for
now") is **superseded** by this design. Decision 4 ("No iframes as the
default") **stays true for first-party and trusted code**: Notes, Todos,
Calendar, Files, anything Fishbowl ships, and apps the user installs as
trusted all run same-realm. A sandboxed install is the one exception. That's
the "`sandboxed: true` mode" the slimming plan's first ask to the appkit
prepared for. The kit has since made `context.host` plain data, so the
contract already survives a `postMessage` boundary.

New decision 9 (to record on acceptance): *apps install by URL in one of two
modes, chosen per install: **sandboxed** (the default, host-granted
capabilities, pinned) or **trusted** (same-realm, acts as the user,
auto-updating, personal desktops only).* Two trust models, both explicit,
and the user picks which one an app gets.

## The desktop (phase D1, no foreign code yet)

**Tiles.** One registry drives the grid, the burger list and the palette:
the built-in apps (Notes, Todos, Calendar, Files once it ships, Messages, and
the admin-only Users / System settings / System) plus installed apps. Rendered with the kit's `<sac-launcher>` if it covers href
tiles and a tile menu; otherwise with the hub recipe (`.grid` / `.tile`) as
today, and the gaps are filed upstream.

- **Size** per tile: medium / wide / large (the manifest's `tile` field is
  the default, the user's choice wins).
- **Colour** per tile: a kit palette slot via the same swatch dialog as the
  space colour. The tile colour seeds the app it opens (`opts.accent`, the
  kit's "tile colour = app highlight").
- **Order** by drag (`sac.sortable`), stored as a position like todos.
- **Hide** a built-in tile (and bring it back from the tile menu of the
  install tile). Hidden means off the desktop, never disabled: the route
  still works.
- **Per workspace.** Each context has its own desktop. Personal is yours; a
  space's desktop is shared by its members, and only its owner arranges it.
  Stored server-side, so it roams across devices. That's the difference
  from `sacrvm-desktop`, which keeps its list in one browser.

**Live badges**, computed by the host from existing APIs, never by app
code: Todos shows open and due-today counts, Calendar the next event's time
(through `fb.format`). Refreshed on `#/` entry and on
`sac:scope-changed`.

**Palette** (`<sac-command-palette>` + `sac.commands`, Ctrl/⌘ K anywhere):

- **Apps:** open any tile.
- **Create:** new note, new todo, new event.
- **Go:** each settings page.
- **Workspace:** switch to Personal or any space.
- **Search:** notes search as you type, results open the note.

A search button in the nav toolbar makes it discoverable; its tooltip names
the key.

**Phone:** tiles collapse to rows, as the kit's hub recipe already does. The
palette is reachable from the toolbar button.

Nothing in D1 mentions installing. The install tile, Settings → Apps and
the palette's install command arrive with D2 (no dead buttons).

## Installing (phase D2)

**Flow**, the same as `sacrvm-desktop` and deliberately split into reading
and running:

1. The dashed install tile → "Install app" dialog. Paste
   `github.com/owner/repo` or a manifest or folder URL. `?install=<url>`
   (and `#/space/<slug>/?install=`) opens the same dialog pre-filled, which
   is how you hand someone an app.
2. `sac.apps.inspect(url)` fetches `app.json` in the browser (credentials
   omitted). The server never fetches foreign URLs, so there is no SSRF
   surface.
3. The browser also fetches the entry script and computes its **SHA-256**.
   That becomes the pin (`sha256-…`, SRI format).
4. The confirm step shows:
   - name, icon, version, description
   - the **origin**, prominently
   - what the app asks for (see Capabilities)
   - where it will live: Personal, or this space for all its members
   - **the mode**, a two-way choice with the sandbox preselected:
     - *Sandboxed — recommended for apps by others.* It gets only what you
       hand it.
     - *Trusted — for your own apps.* It runs as you, with access to
       everything you can reach, secrets included while the vault is
       unlocked, and it updates itself.

     Trusted is offered only on the personal desktop, and only when the
     admin policy allows it (`Apps:Trusted`). Choosing it needs a second,
     explicit confirm that names the origin again.
5. On yes, the record goes to the server. A sandboxed app's code is only
   ever loaded inside its frame, and only when it's opened.

**Updates depend on the mode:**

- **Sandboxed: pinned.** Opening the app re-inspects the manifest in the
  background, as `sacrvm-desktop` does, but a new entry hash **doesn't run
  silently**.
  - The tile shows "Update available · v1.3". Confirming re-pins, and any
    new capabilities the update asks for are listed.
  - Until then the pinned script runs. If the origin no longer serves those
    bytes, the app says it can't start ("the author changed it — update or
    remove").
  - A compromised author account therefore can't push new code onto a
    desktop that holds files.
- **Trusted: automatic.** No pin; the origin's current code runs, exactly
  like `sacrvm-desktop`. It's your own app, so your next deploy is simply
  there. The manifest snapshot still refreshes on open, for the tile.

**Switching modes** is in the tile menu and Settings → Apps. Sandboxed to
trusted asks the same explicit confirm; trusted to sandboxed re-pins the
current code and shows the capability grants.

**Trusted apps in the runtime.** `sac.apps.register` + `open`, unchanged
kit behaviour, in Fishbowl's own realm:

- `context.fs` and `context.files` are the same Fishbowl-backed providers
  the bridge uses, installed with `sac.fs.backend` and `sac.files.use`. A
  trusted app's data therefore lands in `Apps/<name>/` too.
- `context.identity` is `sac.identity.use(...)`, fed from `/api/v1/me`.
- It may also call `fb.api` directly. That's what "trusted" means.
- The main page CSP allows the user's trusted origins in `script-src` and
  `connect-src`; the list is computed per response from the user's trusted
  installs.

**Remove** forgets the record and says how much the app stored in its
folder, like `sacrvm-desktop`'s drawer. A second button deletes the folder.
Nothing is deleted by default.

**Policy** (system config via `set-config` / the admin page):

| Key | Default | Meaning |
|---|---|---|
| `Apps:Install` | `everyone` | `everyone` \| `admins` \| `off` — who may install into personal desktops (space desktops: always the owner, and only if not `off`) |
| `Apps:Trusted` | `everyone` | `everyone` \| `admins` \| `off` — who may choose the trusted mode (personal desktops only, always) |
| `Apps:AllowedOrigins` | *(empty = any)* | optional allow-list of app origins |
| `Apps:StoreOwners` | `SACRVM` | GitHub owners the App Store tab lists (phase D4) |
| `Apps:StoreTopic` | `sacrvm-app` | the repo topic that marks an app |

## Isolation (sandboxed mode)

A sandboxed app runs in `<iframe sandbox="allow-scripts allow-forms
allow-popups allow-downloads">`, **never** with `allow-same-origin`. Its
document therefore has an opaque origin:

- no Fishbowl cookies
- no `fb.api`
- no parent DOM
- no `localStorage` shared with Fishbowl (the kit's storage already
  tolerates a throwing `localStorage`)

**The frame document** is served by Fishbowl at `/apps/frame/<ctx>/<appId>`.
The navigation request carries the session, so the server finds the install
record, but the resulting document is opaque. It contains:

- the kit (`kit/css/ui.css`, `kit/js/all.js`, from Fishbowl, same-origin
  static files)
- the guest half of the bridge
- one `<script src="<entry>" integrity="<pin>" crossorigin="anonymous">`
  (GitHub Pages sends `Access-Control-Allow-Origin: *`, so SRI works)

It also sets a **per-app CSP**, built from the record:

```
default-src 'none';
script-src 'self' <app origin>;   style-src 'self' 'unsafe-inline' <app origin>;
img-src 'self' data: blob: <app origin>;   font-src 'self' <app origin>;
connect-src <declared connect origins, else 'none'>;
frame-ancestors 'self';   form-action 'none';   base-uri 'none'
```

**The bridge.** It speaks `postMessage` and checks
`event.source === frame.contentWindow`. The host keeps one channel per
frame. The guest half builds a context of the kit's own shape and calls
`el.mount(context)`, so the app can't tell it isn't same-realm:

| Context member | Across the bridge |
|---|---|
| `appId`, `manifest`, `params`, `route`, `onRoute` | data + events; the host maps `#/apps/<id>/<route>` to and from the frame |
| `host` (`name`, `icon`, `href`, `nav`, `toolbar`) | plain data. `href`s navigate the top window through the host; `toolbar[].onClick` become action ids the host runs |
| `deepLink.set` | host `replaceState` on the top URL |
| `theme` get/set/onChange, `lang`, regional (date format) | the host pushes changes; `set` is a request |
| `setDirty` | the host arms `beforeunload` and asks before leaving the app |
| `fs` | RPC to the app's folder (Capabilities) |
| `files` | RPC; the picker is drawn by the host (Capabilities) |
| `identity` | data (Capabilities) |

Blobs and Files cross by structured clone. Every call is size-checked
against the Files limits and answered with a typed error, never a hang. The
kit's palette chord inside the frame is forwarded, so Ctrl/⌘ K works
everywhere.

**Where it shows.** A `kind: "view"` app takes the stage at `#/apps/<id>`
(and `#/space/<slug>/apps/<id>`) through a Fishbowl view that holds the
frame. Per the kit contract, the app draws its own chrome and renders
`context.host`, so Fishbowl's global nav is hidden on that route. The host
package carries:

- ⌂ back to the desktop
- the app list in the burger
- the palette button, the workspace chip and the avatar in the ribbon

A `kind: "window"` app opens in a `<sac-window>` holding the frame.

This bridge is **kit work** (see Asks). Fishbowl consumes it, the same way
it consumed the date fields, instead of growing an `fb-*` runtime.

## Capabilities

The manifest declares, the confirm dialog shows, and the host enforces per
call. Nothing is granted that wasn't asked for and shown.

| Capability | Default | What the app gets |
|---|---|---|
| `fs` (own storage) | always | `context.fs` backed by the folder `Apps/<app name>/` in the workspace's Files. Each path is a file (JSON values as `.json`; Blobs as themselves), `watch` rides the change feed. As the Files spec already decides, **nothing an app stores is hidden from its owner**: the folder is visible in Files and counts against the workspace quota. This answers slimming-plan open question 4 for foreign apps: the drawer is a folder. |
| `files` (the user's files) | asked | `context.files.open/save`. **The picker is Fishbowl's, drawn in the top window**, over the workspace's Files (Files' own browser component), with the device one click away, like the kit's virtual provider. The app receives only the bytes the user picked. A `handle` is an opaque host-side token (context, file id, etag), so Save overwrites the same file with an If-Match conflict check. A read-only space member gets open but no save. |
| `identity` | asked | `{ id, name, avatar }`, read-only. `id` is **per-app pseudonymous** (a hash of user id and app id), so two apps can't correlate the same person. |
| `connect: [origins]` | asked | network access to exactly those origins (frame CSP). Without it the app can't send anything anywhere, which is what makes handing it a file safe. |
| `opens: [mime/ext]` | shown | registers the app as an "Open with…" target in Files (phase D3). |

Secrets are unreachable by construction: the vault is never bridged, and
secret blocks live in notes, which no capability exposes.

## Server

- **Schema.** User and space DB get `desktop_tiles` (built-in and installed
  alike) and `desktop_apps`. The version is the next free one after the
  Files backend's v9.
  - `desktop_tiles`: `key`, `position` REAL, `size`, `color`, `hidden`.
  - `desktop_apps`:
    - identity: `id` (the manifest's), `manifest_url`, `manifest` JSON,
      `origin`
    - pinning: `entry_url`, `entry_integrity`, `version`
    - consent: `granted` JSON
    - timestamps: `installed_at`, `updated_at`
- **API** `/api/v1/desktop` with a space mirror
  `/api/v1/spaces/<slug>/desktop`.
  - Endpoints:
    - `GET` returns tiles and apps.
    - `PUT /tiles/<key>` stores size, colour, position and hidden.
    - `POST /apps` installs, with a body of manifest + integrity + grants. The
      server validates the shape, the https origin, the policy and the
      allow-list, **and never fetches anything**.
    - `PATCH /apps/<id>` updates the pin or grants.
    - `DELETE /apps/<id>` takes `?purgeData=true`.
  - Access:
    - All of it is **cookie-only**; Bearer gets 403, because an agent
      installing code is a sharper action than an agent writing a note.
    - In a space, reads are for members and writes for the owner.
- **Frame endpoint** `/apps/frame/<ctx>/<appId>`: cookie-auth on the
  navigation, 404 for unknown or foreign-context apps, per-app CSP, and
  `X-Content-Type-Options`. It adds nothing else.
- **Main SPA CSP.** Add `frame-src 'self'` and take slimming-plan open
  question 7 (`connect-src 'self'`) with it. Sandboxed code never runs in the
  main realm. The only additions are the user's own trusted origins, and
  they appear only for that user.
- `desktop_apps` gains `mode` (`sandboxed` | `trusted`). The server rejects
  `trusted` in a space context and when the policy forbids it.
  `entry_integrity` stays null for trusted apps.

## Phases

- **D1 — Desktop.** *Done 2026-09-26; moved onto `<sac-launcher>` with square tiles (kit 2.18 host-owned layout, sacrvm-appkit #25) the same day.* Registry-driven tiles for the
  built-ins, size/colour/order/hide stored per workspace, badges, the
  palette. Small and useful on its own, with no foreign code. As built:
  - `js/lib/desktop.js` (`fb.desktop`) holds the registry and fills the
    palette. The desktop itself is `fb-hub-view` on the kit's hub recipe;
    `<sac-launcher>` didn't fit (localStorage layout, no tile menu, no
    drag, no targeted badge update, appkit #16).
  - Tile colour tints the tile only. It doesn't seed the opened app's
    accent: Fishbowl's accent is per user and per space, not per app.
  - The palette lists note *titles*. Full-text search as you type needs an
    async result source the kit palette doesn't have yet.
  - Its commands navigate through `sac.scope.hashFor`. The routes register
    with `palette: false`, because the kit palette would navigate a route
    out of the active space.
  - Inside the notes editor, Ctrl/⌘K is the editor's own link shortcut.
    The nav's search button opens the palette from anywhere.
- **D2 — Apps.** Install flow, pinning and updates, the frame and bridge,
  `fs` (needs Files backend phase 1), `identity`, theme/lang/regional,
  Settings → Apps (installed list, grants, storage, update, remove), install
  policy. **Blocked on** the kit's isolated runtime.
  - **Server side done (2026-09-26):**
    - user/space schema v10 (`desktop_tiles`, `desktop_apps`)
    - `AppManifestValidator` and `FrameCsp`
    - the policy keys
    - `/api/v1/desktop` + the space mirror, including tiles, so D1's
      storage is ready too
    - the frame endpoint, and `purgeData`

    Open:
    - the guest bridge `/js/lib/app-guest.js` (the frame references it)
    - the install UI, the tiles UI and Settings → Apps
    - the main SPA's `frame-src 'self'` CSP
    - trusted-mode loading and its per-user `script-src`
- **D3 — Files for apps.** The host picker over Files (needs the Files UI
  MVP), save-back handles, "Open with…" from Files via `opens`.
- **D4 — Reach.**
  - An App Store tab: GitHub topic search, owners from `Apps:StoreOwners`.
    Its entries go through the same inspect and confirm steps.
  - Space apps installed by owners for members.
  - Optionally, a manifest `data` declaration that provisions an
    Apps-platform `app.db` (tables + MCP) behind an app-scoped Bearer the
    bridge holds for the app. That's structured storage for apps that
    outgrow a folder.

## Asks for the appkit

Filed upstream as issues when D2 starts, the same way as the date fields.

1. **Isolated app runtime** — `sac.apps` kind option `isolated: true`
   (host half) and a guest runtime for the frame document. It covers:
   - context over `postMessage`, with `host.toolbar` callbacks as action ids
   - Blob transfer
   - route sync and palette-chord forwarding
   - an error contract

   The contract is already data-only; this is the runtime around it.
2. **Manifest capabilities** — `permissions` (`files`, `identity`),
   `connect`, `opens` in the manifest schema. That's the capability
   handshake (slimming plan's third ask): apps check what they got and
   degrade.
3. **Pinned installs** — `sac.apps.inspect` returns the entry's SRI hash,
   and `add` and the loader honour an `integrity` field.
4. **`<sac-launcher>`** — href tiles for non-kit routes, a per-tile menu
   slot, drag reorder, badges updatable after render.
5. **`sac.files` remote provider** — a provider whose picker is the host's
   file browser and whose handles are opaque host tokens. It may already fit
   `{ kind, open, save }`; the ask is the documented pattern plus the
   guest-side proxy.
6. `sac.dialog.prompt()` — already requested for Files; the install dialog
   needs it too.

## Tests

- **Unit:** manifest validation (https only, required fields, tag shape,
  allow-list, policy), CSP builder, grants enforcement, pseudonymous id.
- **API:**
  - install, list, update and remove in both contexts
  - space owner vs member vs readonly
  - Bearer 403
  - the frame endpoint: 404 across contexts; CSP matches the record
- **Playwright** (`Fishbowl.Ui.Tests`), with a fixture app served from a
  second loopback port with CORS:
  - install → tile → open → the app mounts with the right context
  - **sandbox proof:**
    - `fetch('/api/v1/notes')` from the frame gets 401
    - `parent.document` throws
    - an undeclared `connect` is blocked
  - `context.fs` writes appear in Files under `Apps/<name>/`
  - pick a file → the bytes arrive; save → the file changes in Files
  - a changed entry hash shows "Update available", and the old pin keeps
    running
  - the trusted mode:
    - the app mounts same-realm and its code updates without asking
    - the mode can't be chosen in a space or when `Apps:Trusted=off`
    - the second confirm is required
  - the palette opens an app and creates a note
  - tile size, colour and order survive a reload and another browser
    context

## Decisions (2026-09-26)

1. **Both modes, the user decides per install; the sandbox is the default.**
   Trusted is for one's own apps and personal desktops only.
2. **Updates:**
   - trusted apps update automatically
   - sandboxed apps are pinned and update on confirm
3. **Who installs:**
   - The workspace's owner: you on your personal desktop (only you see
     those apps), a space's owner in the space.
   - Sharing a space shares its apps.
   - `Apps:Install` / `Apps:Trusted` are instance-wide limits the admin
     sets (`2026-09-26-admin-design.md`), default `everyone`.
4. **Network for sandboxed apps:** none unless the manifest declares its
   servers (`connect`). The list is shown at install and enforced by the
   frame's CSP.
5. Admin tools (Users, System settings, System) are built-in desktop apps
   flagged `requires: "admin"`, and a **Messages** tile carries the system
   inbox. Both come from the admin spec.
