# Spaces as sources in the personal Calendar and Contacts

Status: **direction decided (2026-10-07), not yet built.**
The personal Calendar and Contacts can also show the user's spaces. Each
space is a source with its colour, switched on and off. Nothing is copied: a
space's entries are read from that space while the view shows them.

## Problem

Someone in three spaces has four calendars and four address books. To see a
week, they switch workspace four times, and a family birthday sits in a
different place from a club meeting. The personal workspace is where a person
lives, but it only shows what is personal.

## Goal

In the personal Calendar, the month shows the person's own events and those
of every space they switched on, each in its space's colour. A space's
entries are read-only there; a click shows them and offers to open them in
their space. Contacts work the same way. One place to look, and the data
stays where it belongs.

## Decisions

1. **A view, not a copy.** For each source that is on, the personal Calendar
   loads the range on screen from that space over the routes that already
   exist: `/api/v1/spaces/<slug>/events?from&to` and `…/events/birthdays`.
   Contacts loads `/api/v1/spaces/<slug>/contacts`. The view merges the
   results in memory and sorts them by start or by name.
   - Nothing is stored in the personal workspace, so nothing goes stale.
   - The space's checks hold on every read: someone who leaves a space, or
     whose role changes, sees exactly what the space now allows.
   - The visible range is already bounded (a month, ≤ 400 days at the
     server), so there is no "only current events" window.
   - A server fan-out endpoint can come later, if N requests per month ever
     shows.
2. **Only in the personal workspace.** Inside a space, Calendar and Contacts
   show that space alone, as today. The sources menu exists only in the
   personal Calendar and Contacts.
3. **Sources are switched per user, on the server.** The setting follows the
   person to every device.
   - Stored in a new system schema column `users.view_sources` (v19), JSON
     `{ "calendar": [...], "contacts": [...] }`. Each list holds `"personal"`
     and space **ids** — ids, because a slug can change.
   - Written through `PATCH /api/v1/me` (partial, cookie only, like the other
     settings).
   - The server keeps only `"personal"` and spaces the person is a member of,
     on write and on read. Leaving a space drops it.
   - Default: `["personal"]` for both. A space starts off, so nobody's
     calendar fills up unasked.
   - "Personal" can be switched off too, to see one space alone in the
     personal Calendar.
4. **Colour = source.**
   - Personal entries use the person's accent (`--accent`).
   - A space's entries use its colour (`spaces.color`, a kit palette slot,
     via `fb.accents.cssVar`).
   - A space without a colour gets a neutral grey (`--text-muted`), so it never
     looks like personal. The sources menu suggests giving it a colour.
   - This is the calendar's only colour rule. Event types stay one colour
     (decided earlier on 2026-10-07).
5. **The sources menu is the legend, in the nav toolbar.** It sits next to
   Import / Export, because it changes the whole view: an icon button opening
   a `<sac-menu>`.
   - One item per source: Personal first, then the spaces in the switcher's
     order, each with its colour on its icon (as in the workspace switcher)
     and a ✓ while on.
   - A pick toggles that source and closes the menu, like the other
     checkable kit menus (tile size, theme).
   - `fb.toolbar` items gain a `menu` shape for this (`{ icon, title, menu:
     () => items, onSelect }`), rendered as a `<sac-menu>` in the toolbar
     slot. The kit folds it into the phone's "…" like the account menu.
6. **Read-only here, edited in its space.** A space's entry opens in the
   personal view read-only, whatever the person's role there.
   - The editor shows the source as a chip in the space's colour, plus
     **Open in <space>**, which switches to that space and opens the entry
     (`fb.desktop.go` gains a target workspace; the Calendar takes an `open`
     intent like Contacts).
   - Nothing writes across workspaces from the personal view. New entries
     (+, double-click) go to personal; only personal contacts can be marked
     and deleted. Import, export, duplicates and merge act on personal alone.
7. **Birthdays follow the contacts' source.** The personal Calendar shows the
   birthdays of the contacts in every calendar source that is on: personal,
   and the spaces whose calendar source is on. Each birthday is in its
   source's colour; its card's Go to contact opens the contact in its
   space.
8. **What stays per workspace.**
   - Export, import, subscription links and device sync (DAV, one workspace
     per device account, decided 2026-10-06).
   - Reminders and the daily digest. Space reminders are the sync spec's
     phase 3.
   - The merge happens only on screen.
9. **Desktop tiles follow the view.** The personal Calendar and Contacts live
   tiles show what the apps show: the same sources and colours, with the lead
   in the source's colour.
10. **No foreign sources.** External iCal feeds and vCard URLs are not
    supported: Fishbowl is the truth, and things come in by import (and later
    by device sync). Should that change, a foreign feed would be a cached copy
    the server refreshes, with an SSRF guard — a different mechanism from
    spaces.

## Limits

- A space switched on costs one request per range change (and one for its
  birthdays). Spaces are few; the requests run in parallel.
- A space that fails to answer (deleted meanwhile, network) leaves its
  entries out and marks its menu item. The rest of the calendar shows.

## Phases

1. **Calendar:**
   - `users.view_sources` with its `PATCH /api/v1/me` validation;
   - the sources menu;
   - fan-out loading with colours;
   - space entries read-only, with Open in <space>;
   - the birthdays of the sources;
   - `SpaceSourcesTests`: two coloured spaces, switched on and off,
     persisting across a reload; a space event read-only with Open in
     <space>; leaving a space drops it.
2. **Contacts:** the same for the list and the editor (space contacts
   read-only, not markable).
3. **Tiles:** the personal Calendar and Contacts tiles follow the sources.

## Decided on 2026-10-07

- Spaces are shown live in the personal Calendar and Contacts, never copied.
- The sources setting is per user, on the server, switched in a menu in the
  nav toolbar; the feature exists only in the personal workspace.
- A space's entries are read-only in the personal view; they are edited in
  their space.
- External feeds are not supported for now; Fishbowl is the truth.
- No colour per desktop tile (removed the same day); colour means a source.
