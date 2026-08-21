# FISHBOWL — UI Appkit Handover Guide

**Audience:** the agent working in the `sacrvm-appkit` repo. Companion document to the `dream-tools` project's `dream-tools-guide.md` — read that one first. All paths below are relative to this repo root.

**Read this second, and read it as a diff.** Fishbowl's UI is not a parallel invention: it was deliberately adopted *from* DREAM TOOLS in April 2026 (`docs/superpowers/specs/2026-04-19-ui-foundation-design.md` §Why: *"The Dream Tools project ships a well-documented UI system … Adopting it as the base (not a 1:1 copy — adapted for Fishbowl's domain)"*). Same philosophy, same fonts, same component vocabulary, same `--accent: #3b82f6`.

Then it ran for four months inside a real multi-view application. **Several of the "known warts" in §7 of the dream-tools guide are already fixed here.** That is the value of this document: for every point where the two systems disagree, §7 below says which one should win and why.

**Live reference:** `dotnet run --project src/Fishbowl.Host` → https://localhost:7180. Requires Google OAuth or a local admin account; the UI is behind auth. Faster path to look at pixels: `docs/`-linked screenshots, or the Playwright fixture in `src/Fishbowl.Ui.Tests/`.

**Source of truth on disk:** everything under `src/Fishbowl.Data/Resources/`. These files are embedded resources in a .NET assembly, but they are plain, unprocessed, browser-ready files — copy them straight out.

---

## 1. Philosophy (identical to DREAM TOOLS — this is the shared foundation)

- **Zero dependencies, zero build.** Vanilla JS, Custom Elements, plain CSS, served as-is. No bundler, no transpiler, no `node_modules`.
- **Components are classic scripts** loaded with `<script src="..." defer>`, NOT ES modules. They self-register via `customElements.define()`. Load order is by `defer` document order.
- **Design tokens as CSS custom properties on `:root`**, inherited into Shadow DOM.
- **Inter (body) + Outfit (display).**

Where Fishbowl adds to the philosophy:

- **No dead links or buttons.** Every tile, nav entry and button corresponds to a working feature. No "Coming soon" placeholders, ever. This is a hard user rule, not a preference — it is why the hub has three tiles and not eight.
- **URL-first state.** Anything load-bearing about *what data is shown* lives in the hash path, never in a cookie or a JS variable. Two tabs can hold different states, reload preserves them, deep links work.
- **Vendored, not CDN.** `marked` and `purify` sit in `js/vendor/`; fonts are self-hosted woff2. The app must work with no outbound internet.
- **Disk file wins, else default.** Any UI file can be overridden by dropping a same-named file in `fishbowl-mods/`. No manifest, no whitelist, no registration. (Fishbowl-specific mechanism — see §8.)

---

## 2. What to take (exact file list)

All paths under `src/Fishbowl.Data/Resources/`.

| File | Lines/KB | What it is |
|---|---|---|
| `css/app.css` | 585 | Global stylesheet: `@font-face` block, all design tokens, global scrollbars, form-control baseline, tile grid, auth-card, vault modal |
| `fonts/*.woff2` | 180 KB total | Inter + Outfit, latin + latin-ext subsets, self-hosted (OFL 1.1) |
| `js/lib/globals.js` | 33 | The `window.fb` namespace + the toolbar projection API |
| `js/lib/router.js` | 80 | Hash router with prefix-stripping (see §5) |
| `js/lib/icons.js` | 87 | Icon registry, 48 icons, `register/get/has` |
| `js/lib/dialog.js` | 2 KB | `fb.dialog.confirm()` — promise wrapper over `<fb-dialog>` |
| `js/lib/context.js` | 3.7 KB | Workspace context derived from the URL hash (see §6) |
| `js/components/fb-*.js` | 20 files | The component library — full catalog in §4 |
| `index.html` | 68 | The SPA shell. Take as the appkit's app-shell template |

**Take selectively:** `js/lib/api.js` (fetch wrapper — the *pattern* is reusable, the endpoints are not), `js/lib/tags-registry.js` and `js/lib/vault.js` (Fishbowl domain), `js/views/*` (domain views; read `fb-hub-view.js` as the view template and ignore the rest).

**Do NOT take (Fishbowl domain, stays behind):** `js/views/fb-notes-view.js`, `fb-todos-view.js`, `fb-calendar-view.js`, `fb-spaces-settings-view.js`, `fb-keys-settings-view.js`, `js/lib/vault.js`, `js/lib/tags-registry.js`, `login.html`, `setup.html`, and the `.fb-vault-*` block at the bottom of `app.css`.

**Judgement call — `fb-md-editor.js` (88 KB, 2092 lines):** an Obsidian-style live-preview markdown editor with no mode switch (the caret line shows raw source, every other line renders). Technically excellent and heavily used, but it is a *product* in its own right and it drags in `marked` + `purify`. Recommendation: leave it out of the core kit, offer it as an optional add-on module. The pattern worth documenting regardless: **the canonical document is the concatenated `textContent` of the line divs — every transformation preserves `textContent` exactly, markers get wrapped in inline elements rather than synthesized or deleted.** That invariant is what makes the round-trip trivial.

---

## 3. Design tokens — the evolved set

`css/app.css` `:root`, lines 77–122. **This is a superset of the dream-tools token set and should be the merge base.**

```css
color-scheme: dark;         /* native selects, date pickers, autofill, scrollbars follow */

/* Slate background family — bg/panel/glass share one hue */
--bg:           #0b0f1a;    /* was #0a0a0a in dream-tools */
--panel:        #171b24;    /* was #1e1e1e */
--accent:       #3b82f6;    /* unchanged — pro blue */
--accent-edit:  #a855f7;    /* unchanged — purple, edit modes */
--accent-warm:  #f97316;    /* NEW — orange: shared/unsaved/attention state */
--danger:       #ef4444;    /* NEW */
--border:       rgba(255,255,255,0.08);
--text:         #f8fafc;
--text-muted:   #94a3b8;    /* was #64748b — raised to pass WCAG AA on --panel (6.7:1) */
--text-dim:     #64748b;    /* NEW — the old muted value, demoted to tertiary */
--glass:        rgba(15,23,42,0.7);
--bg-dark:      #000000;

/* Motion + elevation rails — use instead of ad-hoc cubic-beziers/shadows */
--ease-bounce:  cubic-bezier(0.175, 0.885, 0.32, 1.275);
--ease-smooth:  cubic-bezier(0.4, 0, 0.2, 1);
--shadow-1:     0 2px 8px rgba(0,0,0,0.35);
--shadow-2:     0 8px 24px rgba(0,0,0,0.45);

/* Tag palette — 10 slots, names mirror the backend's TagPalette.Slots */
--tag-blue #3b82f6 · --tag-orange #f97316 · --tag-red #ef4444 · --tag-green #10b981
--tag-purple #8b5cf6 · --tag-pink #ec4899 · --tag-yellow #eab308 · --tag-teal #14b8a6
--tag-gray #64748b · --tag-indigo #6366f1
```

Three decisions worth carrying over verbatim, each with its reason:

1. **The slate move.** `--bg`/`--panel`/`--glass` share one hue. With dream-tools' near-blacks (`#0a0a0a` / `#1e1e1e`), a `--glass` surface of `rgba(15,23,42,…)` reads as a *different material* floating above the page. Sharing the hue makes a panel read as *raised*. This is the single highest-leverage colour change in the set.
2. **Two-step secondary text.** One muted grey has to carry both "readable secondary information" and "de-emphasized chrome", and it can't do both. `--text-muted` is the AA-compliant carrier; `--text-dim` is strictly tertiary and must never be the only carrier of information.
3. **`color-scheme: dark` on `:root`.** One line. Without it, every native `<select>` popup, date-picker popover, autofill highlight and default scrollbar renders light-mode white inside a dark app. dream-tools does not set it.

**Semantic accents.** `--accent` is the app identity and stays the per-app override point (dream-tools' one-variable-retheme trick — keep it). `--accent-warm` in Fishbowl means *"you are touching shared or unsaved state"*: the space-context switcher tint, the unsaved-draft hint, the pinned marker. `--danger` is destructive/overdue. Keep these three roles distinct in the kit — semantic colour that is separate from the brand accent is what lets a view encode state in colour without fighting the identity.

**Fonts.** Self-hosted woff2, latin + latin-ext subsets, `font-display: swap`, per-weight `@font-face` (Inter 400/500/600, Outfit 600/700/800) with explicit `unicode-range`. 180 KB total. **Recommendation for the appkit: adopt this over the Google Fonts CDN.** It removes a third-party request from every page load, works offline, and is a straight copy of `app.css` lines 12–75 plus the four font files.

---

## 4. Component catalog

20 components, all Shadow DOM (`mode: 'open'`), all classic deferred scripts, all prefixed `fb-`. Header comments in each file document attributes/events/methods — they are accurate; trust them.

### Chrome

- **`<fb-nav app-name="NOTES">`** (`fb-nav.js`, 662 lines) — fixed 50px glassmorphic ribbon + 300px slide-out panel. **The nav list is computed from `fb.router.routes()`**, re-rendered on `fb:route-registered` and on `hashchange`. No hardcoded page list, no path-matching helper. Two slots: `toolbar` (right-aligned) and `context` (a dedicated slot that `renderToolbar()` never touches, so a persistent control survives per-view toolbar repaints). The menu button is a three-span burger that morphs to an X, with `aria-expanded` kept in sync. Layout contract: content below needs `padding-top: 50px` (`#app-root` does it).
- **`<fb-footer>`** — version string + optional GitHub link. The link only renders when `fb.system?.githubUrl` is set — a live demonstration of the no-dead-links rule.
- **`<fb-window title width height top left open>`** (`fb-window.js`) — draggable/resizable glassmorphic floating window. **This is DreamWindow, ported and debugged.** The header comment names the three fixes: *event listener loss, drag/resize coordinates, scroll bleeding*. Methods `open() close() toggle() bringToFront()` (z-index walk over all `fb-window`s, base 10000); events `open`/`close` with `detail.window`. Content is light-DOM children.
- **`<fb-icon name="note">`** — inline SVG from the `fb.icons` registry (48 icons: brand, content, security, calendar, contacts, system, chrome, status). Each entry is the inner-path string of a 24×24 stroke icon; size via `--icon-size` (default 24px), colour via `currentColor`. `fb.icons.register(name, pathString)` adds or overrides.

### Controls

- **`<fb-section title="FILTERS">`** — sidebar group separator, uppercase title + thin border.
- **`<fb-toggle label checked>`** — switch. `change` → `detail` = boolean. Property `.checked`.
- **`<fb-slider label min max step value suffix labels>`** — **all seven attributes are observed** (dream-tools' `dream-slider` reads all but `value` once at `connectedCallback`; that wart is fixed here). `input` on drag, `change` on release.
- **`<fb-segmented-control value="all">`** with `<button data-value="…">` children — `change` → `detail` = string. Property `.value`.
- **`<fb-collapsible max-height="82px" more-label less-label expanded>`** — clamps slotted content, and *only when it actually overflows* draws a separator line with a small hanging tab plus a gradient fade at the clipped edge. `fb:collapse-toggle` event, `.expanded` property. No dream-tools equivalent; the overflow-measurement + fade-vs-border layering is fiddly enough that having it in the kit is worth it.
- **`<fb-status-banner kind message open>`** — inline, non-modal status strip that sits where you put it (typically atop a form panel), hidden until something happens, never auto-dismisses. `show(text, kind)` / `hide()`; kinds `error | info | warn | success`. No dream-tools equivalent. **This is the missing half of a form system** — dream-tools can style an input but has nowhere to say what went wrong.

### Feedback and output

- **`<fb-dialog>`** + **`fb.dialog.confirm({title, message, buttons})`** — modal confirm. Buttons are `{action, label, kind: "default"|"destructive", armAfterMs}`. Escape closes with `null`; Tab/Shift-Tab is a focus trap. **`armAfterMs` is the interesting part:** a destructive button waits N ms before taking focus so a reflexive Enter can't confirm it — and the timer is *cancelled* if the pointer enters any other button first, so it never steals focus from an actively-interacting user. The promise wrapper resolves with the action string or `null`; callers treat `null` as cancel. No dream-tools equivalent (it has a `.floating-menu` CSS recipe only).
- **`<fb-loader>`** — full-screen blocking overlay, two concentric spinning rings (blue + orange, matching the logo mark). `show(title, subtitle)` / `hide()`.
- **`<fb-log>`** — timestamped entries; `add(text, level)`, `clear()`, `copy()`.
- **`<fb-terminal>`** — darker monospace variant; `append(text, level)`, `clear()`, `copy()`.
- **`<fb-hud position="top-right">`** — absolute overlay readout, auto-hides when empty via MutationObserver.

### Domain-flavoured (take the pattern, not the wiring)

- **`<fb-tag-chip name color removable selected clickable>`** — coloured pill. `color` is a *palette slot name* ("blue", "orange"), resolved to `var(--tag-<slot>)`, background is a tinted `color-mix()` so one token per slot serves both fill and tint. `tag-remove` event. **Generalize this as the kit's chip/pill component** — the slot-name indirection is the good idea: persisted data stores a name, not a hex, so re-theming doesn't rewrite stored records.
- **`<fb-tag-input>`** — combobox: chips + text input + filtered dropdown; Tab/Enter/comma commit, Esc closes, Backspace-on-empty removes last chip, Arrow keys move highlight. Creating an unknown entry opens a 10-swatch picker. Property `.value` → `string[]` (returns a copy). Excellent keyboard model; the kit wants a generalized version.
- **`<fb-tag-manage-dialog>`**, **`<fb-context-switcher>`** — Fishbowl-specific. Read `fb-tag-manage-dialog` for one thing only: it uses `<fb-window>` as its shell instead of reinventing chrome. That is the right instinct and worth stating as a rule in the kit.

### CSS-only patterns in `app.css` (no custom element — decide their fate in the kit)

`.btn` (+ `.primary` with lift+glow hover on `--ease-bounce`, `.danger`) · `.tile` + `.grid` · `.glass` · `.orb` · `.tool-layout` / `.tool-sidebar` / `.tool-main` · `.auth-shell` / `.auth-card` · `.fb-logo-mark` · global `select` baseline · global scrollbars.

**The form-control baseline is the part dream-tools is missing.** `app.css` lines 186–209 give every light-DOM `<select>` a dark field with a custom inline-SVG chevron (`appearance: none` kills the native arrow, so we paint our own), and set `accent-color: var(--accent)` on checkboxes and radios. There is a documented trap: **view-specific classes may add sizing but must never re-declare the `background` shorthand — that wipes the chevron image.** Carry the rule into the kit's docs.

---

## 5. The app-shell pattern

`index.html` is 68 lines and is the whole shell. Take it as the appkit's SPA template.

```html
<head><link rel="stylesheet" href="/css/app.css"></head>
<body>
  <!-- lib: globals → icons → router → context → api …  (defer preserves order) -->
  <!-- vendor: marked, purify (only if the md editor ships) -->
  <!-- components: fb-*.js -->
  <!-- views: each view script self-registers a route at load time -->
  <fb-nav>
    <fb-context-switcher slot="context"></fb-context-switcher>
  </fb-nav>
  <div id="app-root"></div>
  <script defer>
    window.addEventListener("DOMContentLoaded", () => fb.router.mount("#app-root"));
  </script>
</body>
```

**The router** (`js/lib/router.js`, 80 lines):

```js
fb.router.register("#/notes", "fb-notes-view", { label: "Notes", icon: "note" });
fb.router.routes()          // → [{hash, tag, label, icon}]  — this is what fb-nav renders
fb.router.current()         // raw hash
fb.router.currentResource() // hash with any /space/SLUG prefix stripped
fb.router.navigate(hash)
fb.router.mount(selector)
```

On `hashchange` it sets `rootElement.innerHTML = "<tag></tag>"`. That's the entire mount mechanism — views are custom elements, so `connectedCallback` is the lifecycle hook and `disconnectedCallback` is where listeners get removed.

Three details worth stealing verbatim:

1. **`register()` fires a `fb:route-registered` event.** Nav components are constructed before view scripts run, so their first render sees an empty route map. The event is what makes a self-registering view list work at all.
2. **Prefix stripping.** `resourceHash()` turns `#/space/SLUG/notes` into `#/notes` for lookup, so one route registration serves both a personal and a scoped workspace. Generalize as: *the router matches on the resource, not the full path.*
3. **The router clears the toolbar between view swaps** (`fb.toolbar.clear()` at the top of `render()`), so an outgoing view never has to clean up after itself.

**Toolbar projection** (`js/lib/globals.js`) — the replacement for dream-tools' inline-styled `slot="toolbar"` buttons:

```js
fb.toolbar.set([{ icon: "plus", title: "New note", onClick: fn, active: false }]);
```

`<fb-nav>` registers itself as the renderer on connect (`fb.toolbar._nav = this`) and unregisters on disconnect. Views declare *what* actions they have; the ribbon owns *how* they look. No repeated `height: 32px` inline styles anywhere.

**View template** — read `js/views/fb-hub-view.js` (63 lines) for the canonical shape: `connectedCallback` renders, subscribes to `fb:context-changed`; `disconnectedCallback` unsubscribes; last two lines are `customElements.define(...)` + `fb.router.register(...)`.

**Light DOM vs Shadow DOM — the one rule that matters:** **views use light DOM, components use Shadow DOM.** Views need `app.css` to apply (they are composed from global classes: `.grid`, `.tile`, `.btn`); components need style isolation so a view's CSS can't reach in. dream-tools puts everything in Shadow DOM because it has no view layer. The kit needs both halves of this rule stated explicitly, or consumers will guess wrong.

**Caveat that bites:** the global `*` scrollbar rules in `app.css` do **not** pierce Shadow DOM. Any component with its own scrollable region must duplicate them. This is noted in the CSS; keep the note.

---

## 6. Workspace context (Fishbowl-only, but the pattern generalizes)

`js/lib/context.js` derives an active workspace from the hash — `#/notes` → personal, `#/space/SLUG/notes` → that space — exposes `get() / endpoint(path) / hashFor(path) / set({type, slug})`, and emits `fb:context-changed` on every switch. The API wrapper routes every call through `context.endpoint(path)`, so one client library hits either backend shape with no mirrored code.

For the appkit this is worth generalizing as **"scoped workspace"**: a URL-derived scope that (a) rewrites data-layer URLs, (b) rewrites navigation hrefs via `hashFor()` so links stay inside the active scope, and (c) tints its switcher with `--accent-warm` so operating on shared state is visually unmissable. Any multi-tenant or multi-project consumer of the kit will need exactly this.

---

## 7. Where the two systems disagree — merge decisions

This is the section to act on. "Fishbowl" in the last column means: the Fishbowl version already solves a wart that the dream-tools guide lists as open.

| # | Topic | DREAM TOOLS | Fishbowl | Take |
|---|---|---|---|---|
| 1 | Token sources | **three** diverging sets (`ui.css`, hub inline, nav Shadow DOM) | one `:root` in `app.css` | **Fishbowl** — fixes dream wart §7.2 |
| 2 | Ground colours | `#0a0a0a` / `#1e1e1e` near-black | `#0b0f1a` / `#171b24` shared-hue slate | **Fishbowl** — see §3.1 |
| 3 | Secondary text | one `--text-muted: #64748b` | two-step, AA-checked | **Fishbowl** |
| 4 | Motion / elevation | ad-hoc cubic-beziers and shadows per file | `--ease-bounce/-smooth`, `--shadow-1/-2` | **Fishbowl** |
| 5 | Fonts | Google Fonts CDN | self-hosted woff2, subset, per-weight | **Fishbowl** — also resolves the kit's open "self-host?" question |
| 6 | Native form controls | inputs styled, selects/pickers left light | `color-scheme: dark` + global select chevron + `accent-color` | **Fishbowl** |
| 7 | Nav entries | hardcoded tool list + `isActive()` + `getBasePath()` | computed from `fb.router.routes()` | **Fishbowl** — fixes dream wart §7.1, the kit's top requirement |
| 8 | Toolbar | `slot="toolbar"` + repeated inline styles | `fb.toolbar.set([...])` + auto-clear on route change | **Fishbowl** — fixes dream wart §7.5 |
| 9 | Slider reactivity | reads `min/max/step/label` once | all 7 attributes observed | **Fishbowl** — fixes dream wart §7.4 |
| 10 | Floating window | DreamWindow | FbWindow — listener loss, drag/resize coords, scroll bleed fixed | **Fishbowl** |
| 11 | Confirm dialogs | none (`.floating-menu` CSS only) | `<fb-dialog>` + promise wrapper + `armAfterMs` | **Fishbowl** (new capability) |
| 12 | Form feedback | none | `<fb-status-banner>` | **Fishbowl** (new capability) |
| 13 | Chips / pills | none | `<fb-tag-chip>` + 10-slot palette + `<fb-tag-input>` | **Fishbowl**, generalized |
| 14 | Progressive disclosure | none | `<fb-collapsible>` | **Fishbowl** (new capability) |
| 15 | Markdown | `help-loader.js` + `marked` via CDN | vendored `marked` + `purify`, plus a live-preview editor | **Split** — take Fishbowl's vendoring, keep dream's help-loader, treat the editor as an optional module |
| 16 | Scene graph / tree list | `dream-scene-graph` + `dream-scene-item` | none | **DREAM TOOLS** |
| 17 | Synchronized pan/zoom | `pan-zoom.js` | none | **DREAM TOOLS** |
| 18 | Launcher deep links | `?tool=` + lazy `dream-window` overlays | none (hub tiles are plain routes) | **DREAM TOOLS** |
| 19 | Per-app `--accent` override | yes, the retheme mechanism | not used (single app) | **DREAM TOOLS** — keep the mechanism, add Fishbowl's semantic `--accent-warm` / `--danger` alongside |
| 20 | Routing | multi-page, static hosting | hash SPA router, prefix-stripping, URL-first state | **Fishbowl** for app consumers; keep dream's multi-page path viable for static tool pages |
| 21 | Tag prefix | `dream-*` | `fb-*` | **Neither** — the kit picks its own (`sac-*`?), and the rename is mechanical either way |

**Net:** the appkit's core should be Fishbowl's token set, shell, router, nav and form layer, plus DREAM TOOLS' scene-graph, pan-zoom, launcher-overlay pattern and per-app accent retheme. Neither project is a superset of the other, but the overlap is genuine — same DNA, four months apart.

---

## 8. Fishbowl's own warts (fix during consolidation — do not port faithfully)

1. **`.tile` hardcodes `rgba(30,41,59,0.7)` and `rgba(255,255,255,0.1)`** instead of tokens (`app.css` 284–287) — the exact wart dream-tools has on its hub. Both projects made it. Tokenize it in the kit.
2. **`.glass` uses `rgba(15,23,42,0.85)` while `--glass` is `rgba(15,23,42,0.7)`.** Two glass opacities, one name. Pick one.
3. **`.tile`'s `transition` uses a literal `cubic-bezier(0.175, 0.885, 0.32, 1.275)`** (line 295) while the icon transition on the very next rule uses `var(--ease-bounce)` — the same curve, written twice. Half-migrated to the motion rails.
4. **`.fb-vault-*` fallbacks are stale:** `var(--panel, #1e1e1e)` is the *old dream-tools* panel colour, and `var(--accent-warm, #f59e0b)` is amber where the real token is `#f97316` orange. Leftovers from the port. Don't carry the block over at all (it's domain), but learn the lesson: fallback values in `var()` rot silently.
5. **No light theme.** Everything assumes dark. `color-scheme: dark` is hardcoded on `:root`. If the kit wants a light mode, that is net-new design work in both projects — the token *structure* supports it, the token *values* do not.
6. **`<select>`'s background-shorthand trap** (§4) is a documented landmine rather than a fixed one. In the kit, paint the chevron with a pseudo-element or a background-*image*-only declaration so a consumer can't wipe it.
7. **`fb-md-editor.js` is 2092 lines in one file.** If it ships in the kit, it needs splitting.
8. **The kit's own open question, unchanged:** Fishbowl never needed per-app accent overrides, so that mechanism is *untested* here. dream-tools is the reference for it.

---

## 9. Quick orientation for the first hour

1. Read `dream-tools-guide.md` fully.
2. Read this file's §7 table — that's the merge plan in one screen.
3. Open `src/Fishbowl.Data/Resources/css/app.css`. Lines 77–122 are the tokens, 186–209 the form baseline, 349–378 the buttons.
4. Open `src/Fishbowl.Data/Resources/index.html` (68 lines) — the whole shell.
5. Open `js/lib/router.js` (80 lines) + `js/lib/globals.js` (33 lines) — the whole app framework. It really is 113 lines.
6. Open `js/views/fb-hub-view.js` (64 lines) — the view template.
7. Skim the header comment of every `js/components/fb-*.js`. They are accurate and they *are* the API documentation.

Total: under 400 lines of reading to have the entire system in your head. That is the property both projects should keep.

---

## 10. Cross-repo etiquette

This guide lives in `the-fishbowl` and is maintained here. Changes to the Fishbowl UI that affect the kit should be reflected in this file in the same commit. Work that needs to happen *in* Fishbowl on behalf of the appkit goes through `gh issue create --repo SACRVM/the-fishbowl` — the appkit agent should not edit this repo directly, and vice versa.
