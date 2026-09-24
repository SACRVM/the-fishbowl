# APPKIT — Mobile / responsive foundation (work order)

**Audience:** the agent working in `SACRVM/sacrvm-appkit`.
**From:** `the-fishbowl`, 2026-09-24, against kit **v2.4.0** (`9e2c230`).
**Goal in one line:** an app built only from the kit is usable on a phone
(360–430 px wide, touch, portrait) without writing a single media query of its own.

---

## Why this is kit work, not app work

The Fishbowl SPA is unusable on a phone — there is no `@media` rule anywhere
in its ~10k lines of UI. But `docs/slimming-plan.md` (Tier B) replaces that
whole UI with the kit, so fixing `fb-*` in place would be thrown away. The
kit has the same gap, and every consumer inherits it:

| Where | Finding |
|---|---|
| `kit/css/ui.css` | Exactly one breakpoint (`max-width: 768px`, launcher tiles only). |
| `ui.css` §12/§13 | `.app-page`, `.app-stage`, `#app-root`, `.center-shell` all size with `100vh`/`100vw` — wrong on mobile browsers whose toolbar slides in and out. `.app-page` also sets `user-select: none` on everything. |
| `.sidebar` / `<sac-sidebar>` | Fixed 260 px / 220 px rail, never collapses. On a 375 px phone it eats 60–70 % of the width. |
| `<sac-split>` | No stacked or single-panel mode. List+detail — the most common app layout, the one Fishbowl's notes/todos use — has no phone form. |
| `<sac-nav>` | The `toolbar` slot never overflows into a menu; per-view buttons overflow the 50 px ribbon. (The 300 px burger panel is a good base — keep it.) |
| `<sac-window>` | Drag/resize is a desktop metaphor; no phone behaviour. |
| `<sac-dialog>`, `<sac-menu>`, `<sac-command-palette>`, `<sac-calendar>` | Fixed or minimum widths not checked below 400 px. |
| Hover | Several affordances hang on `:hover` / `mouseenter` / `pointerenter` (`sac-tooltip`, `sac-dialog`, `sac-toast`, `sac-chip-input`, `sac-command-palette`); touch has no hover. |
| Templates | No `viewport-fit=cover`, no `env(safe-area-inset-*)` handling (notch, home indicator). |
| Tap targets | Not sized for touch (`pointer: coarse`); inputs below 16 px make iOS zoom on focus. |

---

## Decisions to confirm with the user BEFORE implementing

Befehlsgehorsam — these are design calls. Put them to the user with the
recommendation, record the answers in `CLAUDE.md` → *Decisions*.

1. **Breakpoints.** Recommendation: one page-level breakpoint, **`compact` ≤ 768 px**
   (matches the existing launcher rule), optionally a second **`narrow` ≤ 480 px**
   only where a component really needs it. Custom properties cannot be used in
   media queries, so the values are fixed and documented, not tokens.
2. **Page queries vs. container queries.** Recommendation: page layout
   (`.main-layout`, rail, nav) reacts to `@media`; **components react to their own
   container** (`container-type: inline-size` + `@container`), because they also live
   in `sac-window`s and `sac-split` panels that are narrow on a wide screen.
3. **Rail on compact:** off-canvas drawer opened from the `sac-nav` burger
   (recommended — one burger, not two) vs. its own toggle button.
4. **List/detail on compact:** one panel at a time with a back affordance
   (recommended) vs. vertical stacking.
5. **Dialog on compact:** bottom sheet (recommended) vs. full-screen.
6. **Version:** all of this is additive/visual, no API removed → **MINOR (2.5.0)**
   is the recommendation. If any existing attribute changes meaning, MAJOR +
   `MIGRATION.md`.

---

## Work packages

Keep the kit's rules: zero dependencies, no build step, no raw colours, Shadow-DOM
components carry their own queries (a light-DOM `@media` does not pierce the shadow
root — same caveat as reduced motion in §14).

### 1. Foundation — `ui.css`
- New section **"Responsive"**: document the breakpoint(s) from decision 1 and the
  container-query convention from decision 2.
- `100vh` → `100dvh` everywhere a full height is meant, with the `100vh` line kept
  in front as fallback. `100vw` → `100%` (avoids the scrollbar-width overflow).
- Safe areas: `.main-layout` / `#app-root` top padding becomes
  `calc(50px + env(safe-area-inset-top))`; bottom/side insets on the scrolling
  regions and on anything fixed to the bottom (toasts, sheets).
- **Touch sizing** under `@media (pointer: coarse)`: minimum 44 × 44 px hit area for
  `.btn`, `.nav-icon-btn`, list rows, toggles, chips, segmented controls. Hit area,
  not necessarily visual size.
- Inputs, selects, textareas: `font-size: max(16px, 1rem)` on coarse pointers (kills
  iOS focus zoom).
- `.app-page`'s `user-select: none` stays, but make sure every text-bearing content
  region (`.app-scroll`, editors, logs) re-enables selection.
- `@media (hover: none)`: anything that is only *revealed* on hover (row actions,
  copy buttons, hidden icons) is shown permanently.

### 2. Layout primitives
- **`.sidebar` + `<sac-sidebar>`** below `compact`: off-canvas drawer (decision 3),
  scrim behind it, closes on item tap / scrim tap / Escape / swipe-back, focus trapped
  while open. Expose it: `sidebar.open()` / `close()` / `toggle()` + an `open`
  attribute, and a `sac:sidebar-toggle` event so `sac-nav`'s burger can drive it.
- **`<sac-split>`**: new attribute, e.g. `collapse="compact"` (or a px value).
  When collapsed, show one panel at a time (decision 4): the attribute
  `show="start|end"` picks it, reflected; a `sac:split-back` affordance returns to
  `start`. Nothing re-renders — keep the existing "panel content survives" promise
  (hide via attribute/CSS, never re-parent). Divider hidden while collapsed.
- **`.main-layout`, `.app-scroll`, `.center-shell`, `.center-card`**: padding shrinks
  on compact (the 3 rem / 2.5 rem card padding is too much on a phone).

### 3. Chrome — `<sac-nav>`
- Ribbon on compact: brand collapses to icon (+ app-name if it fits), `context` slot
  stays visible but may truncate with ellipsis.
- **Toolbar overflow:** when the `toolbar` slot plus host toolbar do not fit, the
  overflowing buttons move into a trailing "…" `<sac-menu>` (keep the order, keep
  the icons, use the button's `title`/`aria-label` as the menu label). Must work on
  resize, not only on first render — use a `ResizeObserver`, not a breakpoint.
- Burger panel: `min(300px, 85vw)`; on compact it also hosts / opens the app's rail
  (decision 3).

### 4. Overlays
- **`<sac-dialog>`** on compact: bottom sheet (decision 5), full width, max height
  `85dvh`, scrollable body, buttons full-width and stacked, safe-area bottom inset.
- **`<sac-window>`** on compact: always opens maximized; drag and resize disabled;
  the traffic lights stay usable.
- **`<sac-menu>`, `<sac-tooltip>`, `<sac-command-palette>`, `<sac-calendar>`,
  `<sac-date-field>`, `<sac-color-picker>`**: widths use `min(<current>, 100vw - 16px)`;
  the existing 8 px viewport clamp must hold at 360 px. Command palette on compact:
  full-width, top-anchored.
- **`<sac-tooltip>`** on touch: long-press shows, next tap anywhere hides; a
  tooltip must never be the only way to reach information.
- **`<sac-toast>`**: bottom-anchored full-width on compact, above the safe-area
  inset; "pause on hover" gets a touch equivalent (pause while touched).

### 5. Components audit
Go through **every** `kit/js/components/sac-*.js` at 360 px width, coarse pointer:
nothing overflows horizontally, nothing needs hover, every interactive part has a
44 px hit area. Fix what fails; list anything left as-is (with the reason) in the
style guide. `sac-scene-graph` and pan-zoom: confirm pinch-zoom and one-finger pan
work (`touch-action` set, pointer events not mouse events).

### 6. Templates & demo
- All templates in `kit/templates/`: `viewport-fit=cover` in the viewport meta,
  and a `theme-color` meta derived from the surface seed.
- `app-shell.html`, `tool-page.html`, `shell.html`, `launcher.html`,
  `app-fullscreen/`, `app-dialog/`: each must work at 360 px using the new
  primitives. `tool-page.html` (sidebar + viewport) is the reference case for the
  drawer; add a list/detail example using `sac-split collapse`.
- `demo/` must be usable end-to-end on a phone.

### 7. Style guide & docs
- New **Patterns → Responsive** section: breakpoints, container-query convention,
  drawer, list/detail, sheet, toolbar overflow, touch rules — with live examples.
- Every component section: the new attributes/events in its table, and a note on its
  compact behaviour.
- A width toggle in the style guide (e.g. "phone 375 / tablet 768 / full") that
  renders the demos in a constrained frame — *Truth in Preview* applies to phones too.
- Update each component's header comment (the API lives there).

---

## Acceptance

- At **360, 375, 390 and 430 px** (portrait) and **768 px**, with touch emulation:
  style guide, demo and every template — no horizontal page scroll, no clipped
  controls, every action reachable without hover. Check with Playwright
  (Chromium is available) using `isMobile: true, hasTouch: true`; also sanity-check
  once in an iOS Safari–sized viewport (dvh + safe-area).
- Desktop (≥ 1024 px) looks and behaves **exactly as in v2.4.0** — this release must
  not change the desktop experience.
- Keyboard and reduced-motion behaviour unchanged; the drawer and the sheet are
  focus-trapped and close on Escape.
- No raw colours introduced; light and dark theme both checked on phone width.
- Released via `/release` (version per decision 6), with the usual re-vendor notice
  to all consumer repos.

## Out of scope

- Fishbowl's views (calendar month grid vs. agenda, notes editor) — rebuilt in
  `the-fishbowl` on top of this release, starting with the Tier-B pilot
  (`fb-hub-view` + notes list/detail).
- `fb-md-editor` touch behaviour — not a kit component.
- PWA / offline / install prompts.

## Feedback to Fishbowl

When released, reply via Firepit inbox (or an issue on `SACRVM/the-fishbowl`) with
the version, the final breakpoint values, and the new attributes (`sac-split
collapse/show`, sidebar drawer API, dialog sheet) so the Fishbowl pilot can build
against them.
