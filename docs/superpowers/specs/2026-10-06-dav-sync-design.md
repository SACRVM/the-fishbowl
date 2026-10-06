# Calendar and contacts sync — Fishbowl as a CalDAV/CardDAV server

Status: **direction and model changes decided (2026-10-06), not yet built.**
Fishbowl is the server; phones, Macs and mail clients sync with it over the standard
protocols. Brings sync back after `docs/slimming-plan.md` removed the empty
`Fishbowl.Sync` project and its unused `ISyncProvider` contract — not as
provider clients (that was the never-built part) but as a server. Files keep
their own decision (no WebDAV; a sync client, `2026-09-26-files-app-design.md`).

## Problem

Calendar, contacts and todos live in Fishbowl, but people read and write them
on their phones: Apple Calendar and Contacts, Google Calendar on Android,
Reminders, Thunderbird. Today the only bridges are a vCard/CSV import and
export for contacts. An event made in Fishbowl never reaches the phone, and
one made on the phone never reaches Fishbowl.

## Goal

Add a workspace on a device — server address, username, that workspace's
device password — and its calendar, task list and address book appear there,
two-way, live. Each workspace (the personal one, every space) is its own
account on the device. Fishbowl stays the source of truth
("a calendar that belongs to you"); no Google or Apple account sits in
between. Moving in from Google or iCloud is a one-time import.

## Decisions

**Compatibility leads.** Where the standards hold something Fishbowl's models
can't, the models grow (recurrence exceptions, full recurrence rules, several
alarms, the contact and todo fields phones edit) — the raw round trip below
only carries what no Fishbowl screen shows.

### Fishbowl is the server; the devices do the syncing

- Fishbowl speaks **CalDAV** (RFC 4791) and **CardDAV** (RFC 6352) on top of
  WebDAV, with **sync-collection** (RFC 6578) for "what changed since",
  ETags with `If-Match` / `If-None-Match` for every write, the calendarserver
  `getctag` for older clients, and discovery by `/.well-known/caldav` and
  `/.well-known/carddav` (RFC 6764) plus `current-user-principal` (RFC 5397).
- The clients do the hard part — change tracking on their side, retries,
  conflicts (a stale `If-Match` gets 412; the client fetches and tries again).
  Fishbowl never merges two edits.
- **Who connects how:** iPhone and Mac natively (Settings → Calendar /
  Contacts → Accounts → Other → CalDAV / CardDAV); Android through DAVx⁵
  (open source), after which Google Calendar and the phone's contacts show the
  collections; Thunderbird natively; Google Calendar on the web only as a
  read-only subscription (the ICS feed, below).
- **Not built:** clients for Google's APIs or iCloud (two-way sync engines,
  OAuth verification, stored third-party passwords); CalDAV scheduling (RFC
  6638 — Fishbowl sends no invitations); free/busy; WebDAV for files.

### What syncs

| Fishbowl | Protocol | On the device |
| --- | --- | --- |
| Events | CalDAV `VEVENT` | Calendar (Apple, Google via DAVx⁵, Thunderbird) |
| Todos | CalDAV `VTODO` | Reminders (non-iCloud accounts), Tasks.org via DAVx⁵, Thunderbird |
| Contacts (people and organisations) | CardDAV vCard 3.0 | Contacts |
| Birthdays (from contacts) | CalDAV `VEVENT`, read-only | Calendar — its own list |

Notes, files, tables and messages do not sync (no standard to sync with, or
their own path).

### One account per workspace

- **A device account is one workspace**, because a device password is a key,
  and every key belongs to exactly one workspace (the personal one or one
  space) — sync doesn't soften that. Whoever wants several spaces on a phone
  adds one account per space, each with its own device password. Joining a
  space adds nothing by itself; leaving one ends its device passwords (a space
  key whose owner is no longer a member is refused — as today).
- **What an account shows:** a calendar, a task list (a separate collection:
  Apple shows VTODO collections as Reminders lists), a read-only "Birthdays"
  calendar and an address book — named after the workspace ("Personal" or
  the space's name), a space's in its colour (`calendar-color`). A Reader's
  space account is **read-only** (`current-user-privilege-set` without write;
  writes answer 403).
- **Paths are keyed by ids, never slugs** (a space can be renamed), and a key
  reaches only its own: `<ws>` is `user-<userId>` or `space-<spaceId>` —
  principal `/dav/<ws>/principal/`, calendars `/dav/<ws>/calendars/events/`,
  `…/tasks/`, `…/birthdays/`, address book `/dav/<ws>/addressbooks/contacts/`.
  A resource is `<name>.ics` / `<name>.vcf`, where the name is the client's
  choice on PUT and the item's `uid` for items made in Fishbowl.
### Device passwords

- CalDAV and CardDAV clients log in with HTTP **Basic** — no OAuth, no
  cookies. The password is a **device password**: an ordinary API key
  (`fb_live_…`, hashed, revocable on the API keys page) of **one workspace**,
  like every key, with a new scope **`sync:dav`** that opens only the `/dav`
  routes of that workspace (and nothing on REST or MCP). A space's device
  password is trimmed to the owner's role there like any space key (a Reader's
  loses writing). The username is the account's; a mismatch is refused.
- **"Connect a device"** on the API keys page (beside "Connect an agent"):
  workspace and device name → the server address, username and password,
  shown once. One password per device and workspace, so a lost phone is
  revoked on its own. Disabling or deleting the account revokes them with the
  other keys.
- Failed Basic logins are throttled like sign-ins (`SignInThrottle`). The
  `/dav` routes require HTTPS outside Development (Caddy in production).
### Identity, change tracking, round trip

- Every event, todo and contact gets a **`uid`** column (user/space schema
  bump): the iCalendar `UID` / vCard `UID`. Items made in Fishbowl get
  `<ulid>@fishbowl`; items PUT by a device keep the device's. `ExternalId` /
  `ExternalSource` on events predate this, are read by nothing, and stay as
  they are.
- **Changes:** a `dav_changes` journal per context DB (`seq`, collection,
  item id, resource name, deleted) written in the same transaction as every
  write to events, todos or contacts — from the UI, the REST API, MCP, the
  trash or a device. The sync token is `<epoch>:<seq>` (the files journal's
  pattern); a token from another epoch or older than the compaction floor
  answers `valid-sync-token` (the client starts over). The ETag is the row's
  `updated_at` plus its `dav_raw` hash.
- **Never lose what a device sent.** A device's iCalendar or vCard holds more
  than Fishbowl models (several alarms, attendees, photos, IM handles, custom
  labels, Apple's extensions). Each item keeps the **last full text it had**
  (`dav_raw`). Serving it starts from that text and overwrites only the
  properties Fishbowl owns with the current values; a device's PUT updates the
  owned fields from the text and keeps the rest. An item that never came from
  a device is written from the model alone.
- **Deletes** from a device go to Fishbowl's trash like any delete (restorable;
  a restore reappears on devices). Something a table row links to can't be
  deleted (`still_linked`): the device gets 403 and, at its next sync, gets the
  item back.

### Events ↔ VEVENT

- **Owned:** `SUMMARY` ↔ title, `DESCRIPTION`, `LOCATION`, `URL`,
  `CATEGORIES` ↔ tags (new on events), `STATUS` (confirmed / tentative /
  cancelled, new), `TRANSP` ↔ busy/free (new), `DTSTART`/`DTEND` (with `TZID`
  ↔ `timeZone`; `VALUE=DATE` ↔ `startDate`/`endDate`, exclusive end — already
  iCal's rule), `RRULE`/`RDATE`/`EXDATE`, every `VALARM`. Attendees and
  organizer stay in `dav_raw` (no scheduling).
- **Full recurrence.** A device can send any rule, so the server expands any
  rule: **Ical.Net** (MIT) replaces the subset parser
  (`Fishbowl.Core.Util.RRule` delegates to it, then goes) in range reads, the
  reminder dispatcher, the digest, `/upcoming` and the live tile — the server
  and the phone always agree on when something happens. The calendar editor
  keeps its presets and shows any other rule as "Custom" (kept as is).
- **Recurrence exceptions** (new): a phone that moves one occurrence sends a
  `RECURRENCE-ID` override, one that deletes one an `EXDATE`. A new
  `event_exceptions` table (event id, recurrence id as UTC instant or date,
  cancelled, the overridden fields) that every expansion honours. Fishbowl's
  own editor asks **"this occurrence / the whole series"** for a recurring
  event, writing the same exceptions.
- **Several alarms** (new): `event_alarms` (relative to start or end, or an
  absolute time) replaces the single `reminderMinutes` (migrated as one
  relative alarm). Fishbowl's reminder dispatcher sends each; the editor lists
  them (add / remove).
- **Alarms in spaces ring for every member** — spaces are for everyone. A
  space calendar serves its `VALARM`s (every member's device rings), and
  Fishbowl's own reminders for space events go to every member with a linked
  chat (lifting the "space-event reminders out of scope" line).
- **Time zones:** IANA `TZID`s map directly; Windows names (Outlook) through
  `TimeZoneInfo.TryConvertWindowsIdToIanaId`; floating times as the account's
  zone. Fishbowl serves `VTIMEZONE` blocks for the zones it uses.

### Birthdays

- Birthdays come from **contacts** (`birthday`, and `anniversary`-type dates
  below) — never stored as events. **Fishbowl's calendar shows them** among
  the events (a yearly all-day entry with the age where the year is known;
  opening one opens the contact); the Calendar live tile too.
- **Outward they are their own read-only calendar per workspace.** An iPhone
  builds its own birthday calendar from the synced contacts, so ours would
  double every birthday inside the main calendar; as a separate list it is
  switched off there in one tap, and on Android and Thunderbird — which build
  none — it is the only source. Writes to it answer 403; an edit belongs on
  the contact.

### Contacts ↔ vCard 3.0

- vCard **3.0** (what Apple's CardDAV speaks; DAVx⁵ takes it too).
  `ContactFormats` already writes and reads it for import/export; the same
  mapping serves CardDAV, extended by the raw round trip.
- **Owned:** `N`/`FN` ↔ names, `ORG` ↔ the organisation's name (a person's
  `organisationId`; on PUT matched by name in the workspace, else created — as
  the import does), `TITLE` ↔ role, `BDAY`, `EMAIL`/`TEL`/`ADR` with their
  labels, `URL`, `NOTE`, `CATEGORIES` ↔ tags, organisations as
  `X-ABShowAs:COMPANY` (+ `KIND:org`). Fishbowl's own fields (legal form, VAT
  ID, IBAN, customer number, …) travel as `X-FISHBOWL-*` properties so a
  round trip through a phone keeps them.
- **New fields**, the ones phones edit: `nickname`, `urls` (labelled list,
  replacing the single `website`), `ims` (labelled list — `IMPP` and Apple's
  social profiles), `related` (name + relation — Apple's related names), and
  `dates` (labelled list — anniversaries and other dates; `BDAY` stays
  `birthday`). The Contacts app gets the fields.
- **Photos:** a device's `PHOTO` is written into the workspace's Files
  (`Contacts/<contact id>.jpg`, sniffed and size-limited like any upload) and
  `photo` points there; Fishbowl serves the file as `PHOTO`. The Contacts app
  shows it.

### Todos ↔ VTODO

`SUMMARY`, `DESCRIPTION`, `DUE` ↔ `dueAt`, `STATUS:COMPLETED` +
`COMPLETED` ↔ `completedAt`, `VALARM` ↔ `reminderAt`, `X-APPLE-SORT-ORDER`
↔ `position` where a client sends it, and — new on todos — `PRIORITY`,
`URL`, and **subtasks** (`RELATED-TO;RELTYPE=PARENT` ↔ `parentId`, one level
like Reminders; the Todos app indents them). Everything else in `dav_raw`.

### Read-only feed and file import (no DAV client needed)

- **Subscription link per workspace:** `GET /feeds/<token>.ics` — the
  workspace's events as one iCalendar file, for Google Calendar on the web,
  Outlook, or anyone who only wants to look. The token is random, stored
  hashed, revocable, made in the Calendar app ("Subscribe link…"); no cookie,
  no key in the URL.
- **Import / export** of `.ics` files in the Calendar app's toolbar (both
  workspaces, `…/events/import?format=ics`, `…/events/export?format=ics`),
  through the same mapping — the way in from Google Takeout or iCloud's
  export, next to the vCard import contacts already have.

## Limits

A resource ≤ 1 MB (vCards with photos up to 5 MB); a multiget ≤ 200 hrefs; a
calendar-query range ≤ `EventLimits.MaxRangeDays`; a response page ≤ 500
items with a sync token for the rest; requests per device password rate
limited like other keys. Logs carry ids and resource names, never summaries,
names or addresses.

## Phases

1. **iCalendar foundation + feed + import/export.** The `uid` columns,
   Ical.Net in (it takes over recurrence expansion here), the VEVENT
   mapping (owned fields only, no raw yet), the birthdays in Fishbowl's
   calendar, ICS
   import/export in the Calendar app, the subscription link. Guards: golden
   files exported by Apple Calendar, Google Calendar and Thunderbird
   (all-day, timed with zone, recurring, with alarm) import to the expected
   events and export back to text those apps read.
2. **DAV foundation + CardDAV.** `sync:dav` device passwords (one workspace
   each) and "Connect a device", the Basic handler, discovery, principal, the
   address book, `PROPFIND`, `REPORT` (`addressbook-multiget`,
   `sync-collection`), `PUT`/`DELETE` with ETags, `dav_changes`, the raw round
   trip, the new contact fields and photos into Files. Verified by hand with an iPhone, a Mac, DAVx⁵ and Thunderbird; the
   protocol by request/response tests in `Fishbowl.Host.Tests`.
3. **CalDAV events.** Calendar collections, `calendar-query` (time range),
   `calendar-multiget`, `sync-collection`, `event_exceptions` and
   "this occurrence / the whole series" in the editor, `event_alarms` and
   space reminders for every member, the new event fields, time zones, the
   birthday calendars.
4. **CalDAV todos** — the task-list collections, priority, URL, subtasks.

## Decided on 2026-10-06

- The models grow for compatibility: full recurrence through Ical.Net,
  exceptions with "this occurrence / the whole series", several alarms, the
  new contact and todo fields, device photos into Files.
- Alarms in space calendars ring for every member.
- Birthdays show in Fishbowl's calendar and go out as their own read-only
  calendar.
- A device password belongs to one workspace, like every key: one account per
  workspace on the device.
