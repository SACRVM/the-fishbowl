# The Fishbowl — Project Concept & Specification

> *"Your memory lives here. You don't."*

**The Fishbowl is your memory. And your assistant.**

Interact with it from the web, Discord, Telegram, WhatsApp — wherever humans and machines meet naturally. It sends you reminders. Helps you store information, ideas, and data. Keeps your passwords safe. It is your calendar, your address book, your notebook, and your password manager — all in one place, used the way you already communicate.

But with one important difference: **you own your data.** The Fishbowl can run on your own machine, on a home server, or in the cloud. It is open source, self-hostable, and extendable — moddable like a game, built for a community. Your data is a single file you can take anywhere, at any time, no questions asked.

---

**Yes — tools like this exist. That is not the point.**

Free to start. Useful enough to depend on. And then, slowly, the price goes up. Features disappear behind paywalls. Exports get restricted. Your own notes, your own calendar, your own passwords — held just out of reach until you pay. This is not an accident. It is a business model.

We think it has to stop.

The Fishbowl is built on a simple principle: a tool that earns your trust does not need to hold your data hostage to keep you. If you want to leave, you leave — with everything, instantly, completely. The only reason to stay is because it is genuinely useful.

That is the only acceptable reason.

---

## The Vision

We forget things. All of us. Not because we are careless — but because modern life throws more at us than any human brain was built to hold. A conversation at 9am plants an idea. By noon it is gone. A password written on a sticky note. An appointment confirmed via chat that never made it to the calendar. A document scanned and saved somewhere — but where, exactly?

The tools that were supposed to fix this have become part of the problem. Not because they are badly built — but because they made the wrong bet. They assumed that if they built a good enough app, you would change your behaviour to use it. Open the app. File the note. Switch contexts. Maintain the system.

Most people do not. Most people forget. That is why they needed the tool in the first place.

---

**The Fishbowl makes a different bet.**

Instead of asking you to change your habits, it meets you where you already are. The interface is the conversation you are already having — a message sent, a reminder received, a question answered. No context switch. No app to open. No system to maintain. Just a natural exchange with something that actually remembers.

Behind that conversation is a complete system — calendar, notes, contacts, tasks, documents, passwords — all connected, all searchable, all yours. The web interface exists for the moments that need more than a chat message: editing a long note, viewing a month of events, safely reading a stored password. But the default is always the conversation.

---

**Everything connected. Everything findable.**

The Fishbowl does not just search for words. It searches for meaning. Ask *"what was that thing about the venue last month"* and it understands what you mean — even if you cannot remember the exact phrase you used. A small AI model runs entirely on your own machine, reads your notes, and finds what is relevant. No data leaves. No query is logged. No one is watching.

Every piece of information can be tagged, linked, and traced back to its origin. A task knows which note created it. A reminder knows which event it belongs to. Nothing floats in isolation.

---

**You are not alone.**

Bring your friends. Bring your team. Bring your family. The Fishbowl is not just a personal tool — it is a shared space for the people you work and live with. Invite them in and build together: a shared calendar that everyone can see and contribute to, a collective knowledge base that grows with every conversation, shared to-do lists and tasks that everyone can check off, reminders that reach the right person at the right moment.

The grocery list your partner can update from their phone. The project board your team works from every morning. The family calendar that finally has everyone on the same page. The shared document folder your band, your club, your community actually uses.

Each group gets its own **Space** — separate from your personal data, but part of the same system. You can belong to multiple Spaces, switch between contexts naturally, and always know what is yours and what is shared. The same simplicity, the same search, the same chat interface — just more people in the bowl.

---

**And it grows without becoming complicated.**

Start with notes and a calendar. Add your team when you are ready. Build small custom tools when you need them — a structured list, a tracker, a booking flow — without writing a line of code. Let AI agents read your data and act on your behalf. Connect the tools you already use. Build the workflows that match the way *you* think.

Each step adds capability. None of them add complexity to the steps before. The person who uses The Fishbowl as a simple note-taker and reminder bot never sees the machinery underneath. It is there if they want it. Invisible if they do not.

---

**Named for the goldfish.**

Because we forget. Because the bowl does not. Because your memory should live somewhere safe, somewhere quiet, somewhere entirely your own — waiting patiently for the moment you need it.

*Your memory lives here.*

---

## Core Philosophy

**The Fishbowl is the source of truth.** External services (Google Calendar, iCal, Outlook) are sync targets — mirrors, not sources. The user owns their data absolutely. Every user's data lives in a single SQLite file that can be downloaded at any time and opened with standard tooling.

**The chat client is the primary interface.** People live in Discord, WhatsApp, Telegram — not in another app with another notification. The Fishbowl meets the user where they are. A companion web UI exists for anything that cannot be done comfortably in chat (rich editing, viewing lists, managing secrets).

**We are the truth. External services are mirrors, not masters. On conflict — we win.**

---

## UI Design Manifesto

> *"Simple for 99%. Powerful for 1%. Never the other way around."*

The Fishbowl has exactly two UI layers. No middle ground.

**Layer 1 — The Default (99%)**
Dropdowns. Toggles. Smart defaults. Auto-suggestions. Works without reading a single line of documentation. A goldfish can use it.

**Layer 2 — The Code (1%)**
Raw config. Script editor. Rules file. Full power, zero hand-holding. If you need it, you know why.

There is no Layer 1.5. If the simple UI cannot express it — drop straight to code. No wizard that generates XML nobody can read. No "advanced settings panel" that is secretly just Layer 2 with a friendlier font. Honest, clean, no pretending.

The Layer 2 escape hatch is always visible but never pushed — small, unobtrusive, there for those who need it, invisible to those who don't.

**What we never build:**

- Settings pages with 40 options nobody understands
- Wizards that hide complexity behind 8 steps
- "Advanced" panels that are just the same UI with more fields
- Tooltips explaining what a button does that should be self-evident

If a UI element needs a tooltip to be understood — the UI element is wrong.

---

## Core Features

The personal foundation of The Fishbowl. Everything else builds on top of this.

### Notes
Rich Markdown notes with tags, pins, and full-text search. Write freely — The Fishbowl organises as you go, suggests tags based on content, and finds anything you have ever written using meaning, not just keywords. A `::secret` block inside any note encrypts that section — invisible to search, invisible to AI, visible only to you after unlocking.

### Calendar
A calendar that belongs to you. Events, reminders, recurring schedules — all stored locally, syncing outward to Google Calendar or iCal if you want, but never depending on them. When something is coming up, The Fishbowl reminds you in whatever chat client you use.

### Contacts
Not an address book. A living record of the people who matter — with linked notes, linked events, and context that builds over time. Find people by what you remember about them, not just their name.

### Tasks & To-Dos
Simple, fast, always there. Type a task in chat and it is saved. Check it off in chat and it is done. Nothing more needed.

Tasks can be standalone — a quick to-do with no date, no context, just something to remember. Or they can be linked — created from a note, triggered by a calendar event, suggested by The Fishbowl when it notices something that looks like a to-do in your writing. Every task optionally has a due date, a reminder, and a source. Overdue tasks surface automatically. Done tasks stay in history.

The Fishbowl never throws tasks away. It just moves them out of the way.

### Documents & Attachments
Drop in a PDF, a photo, a scanned receipt. The Fishbowl reads it locally using OCR — no cloud service, no upload — and makes it searchable alongside your notes and calendar. Every file is attached to something: a note, a contact, an event.

### Secrets
Passwords and sensitive data stored with real encryption, derived from a Master Password that never leaves your device. The Fishbowl will confirm a secret exists and send you a secure link — it will never show credentials in a chat window under any circumstances.

### Search
One search bar for everything. Notes, events, contacts, tasks, documents — all results in one place, ranked by relevance. Powered by a local AI model that understands meaning, not just matching words.

---

## Spaces

The Fishbowl is personal by default — but you are not alone.

A **Space** is a self-contained workspace with its own data, completely separate from your private notes but working with exactly the same system. A Space can be shared — invite your team, your friends, your family — or it can stand alone, a clean namespace of its own (even an AI agent gets its own Space to remember in). "Shared" is just a property of a Space, not a different kind of thing.

Invite people into a Space and you get the grocery list your partner updates from their phone, the project board your team works from every morning, the shared calendar that finally has everyone on the same page. Everyone uses The Fishbowl the way they already do — through their chat client, through the web UI, through whatever feels natural. The Space just means everyone is working from the same truth.

You can belong to multiple Spaces. Switch between them with a single word in chat. Your personal notes stay personal. A Space's notes stay in that Space. Nothing leaks across.

---

## Apps

Personal and shared Spaces have a fixed structure — notes, calendar, contacts, tasks, documents. That structure is intentional and stable. It does not change.

But sometimes you need something different. A staff roster. A booking tracker. An inventory. A content calendar. Something with its own fields, its own rules, its own shape.

That is what Apps are for.

An App lives in its own database — completely separate from your personal data and your Spaces' data. It has its own schema, defined by you. Its own access rules. Its own workflows. It can belong to a single user or to a Space. It can be exported as a template — schema and configuration, no data — and shared with anyone else running The Fishbowl.

Create an App without code using the simple form builder. Define the fields you need, choose who can read and write, add a basic workflow. The Fishbowl generates the interface automatically. For the 1% who want more — drop down to a script, write a custom rule, build a custom renderer.

Apps are searchable alongside everything else. A task created by an App workflow lands in your task list. A reminder fired by an App reaches you in chat. Everything connects — but nothing bleeds into the fixed structure where it does not belong.

```
fishbowl-data/
  users/
    abc123.db              ← fixed schema: notes, events, contacts, tasks
  spaces/
    acme-team.db           ← fixed schema: same, shared
  apps/
    staff-roster.db        ← dynamic schema, owned by a user or space
    grocery-list.db        ← dynamic schema
    booking-tracker.db     ← dynamic schema
```

---

## Notifications

The Fishbowl delivers notifications to the user — not to a specific platform. Where the notification arrives depends entirely on what the user has configured in their settings.

A user can connect one or more notification channels: Discord, Telegram, WhatsApp, web push, email. Each can be enabled or disabled independently. When something sends a notification — a reminder, a trigger, a task update — The Fishbowl delivers it to all active channels simultaneously.

```
Settings → Notifications
  ✓ Discord DM       ← primary
  ✓ Web Push         ← browser notifications
  ✗ Telegram         ← not set up
  ✗ Email            ← disabled
```

Apps, triggers, and scripts never decide the delivery channel. They only say who should be notified and what the message is. The routing is always the user's choice.

---

## App Triggers

Triggers are the simplest possible automation — a reaction to something that happened. Before or after an entry is created, updated, or deleted, The Fishbowl can run a small piece of logic.

Six trigger points cover the vast majority of real-world workflows:

```
before:insert    ← validate, transform, or cancel
after:insert     ← notify, create tasks, sync

before:update    ← validate, check old values
after:update     ← react to field changes

before:delete    ← guard — prevent if conditions not met
after:delete     ← clean up, archive, notify
```

`before:*` triggers can throw an error to cancel the operation. `after:*` triggers cannot — the action has already happened.

### The Context Object

Every trigger receives the entry and a context object. The context provides the notification abstraction — no platform-specific code, no hardcoded channels.

```javascript
trigger('after:insert', (entry, ctx) => {
    ctx.owner              // the App owner
    ctx.space              // the space the App belongs to (if any)
    ctx.actor              // who triggered the action
    ctx.user('abc123')     // any specific user by ID

    // all of the above have .notify()
    ctx.owner.notify(`New entry added: ${entry.Name}`);
    ctx.actor.notify('Your entry was saved');
    ctx.space.notify('Roster updated');
});
```

`.notify()` routes to all channels the recipient has active. The trigger does not know and does not care which ones.

### What Triggers Can Do

```javascript
ctx.owner.notify(message)       // send notification
tasks.create(title, options)    // create a task
calendar.create(event)          // create a calendar event
app.get(appId, query)           // read another App
app.insert(appId, entry)        // write to another App
throw new Error(message)        // cancel operation (before:* only)
```

What triggers **cannot** do:
- Read or write secrets
- Make outbound HTTP calls — use Webhooks for that
- Trigger other triggers — no recursive chains

### Layer 1 — Simple Workflows

For the 99%, triggers are configured through a simple If→Then UI:

```
┌─────────────────────────────────────┐
│ When:    [Entry added          ▾]   │
│ If:      [Status] = [Active    ]   │
│ Then:    [Create task          ▾]   │
│          "Send welcome email"       │
│                                     │
│ + Add another action                │
│                          [Custom ›] │
└─────────────────────────────────────┘
```

### Layer 2 — Full Script

For the 1%, drop to code:

```javascript
trigger('before:delete', (entry, ctx) => {
    if (entry.Status === 'Active')
        throw new Error('Active entries cannot be deleted');
});

trigger('after:update', 'Status', (entry, old, ctx) => {
    if (old.Status !== 'Active' && entry.Status === 'Active') {
        calendar.create({
            title: `${entry.Name} starts`,
            date: entry.StartDate
        });
        ctx.owner.notify(`${entry.Name} is now active`);
    }
});
```

AI writes triggers on request — describe what you want in plain language, The Fishbowl generates the script, you activate it.

---


*The following sections are intended for developers building, extending, or self-hosting The Fishbowl.*

---



```
┌─────────────────────────────────────────────────────┐
│              Fishbowl Monolith (C# ASP.NET Core)    │
│                                                     │
│  ┌──────────┐  ┌──────────┐  ┌───────────────────┐ │
│  │ REST API │  │ Web UI   │  │  Sync Engine      │ │
│  │          │  │ (Vanilla)│  │  Google Cal/iCal  │ │
│  └──────────┘  └──────────┘  └───────────────────┘ │
│                                                     │
│  ┌──────────────────────────────────────────────┐   │
│  │           Core Services                      │   │
│  │  NoteService  │  CalendarService             │   │
│  │  SearchService│  ReminderScheduler           │   │
│  │  SecretService│  EmbeddingService            │   │
│  └──────────────────────────────────────────────┘   │
│                                                     │
│  ┌──────────────────────────────────────────────┐   │
│  │         User Database Factory                │   │
│  │         (one SQLite file per user)           │   │
│  └──────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────┘
          ↑                    ↑
   ┌──────────────┐    ┌──────────────┐
   │ Discord Bot  │    │ [Future]     │
   │ (DM-based)   │    │ Telegram Bot │
   │              │    │ WhatsApp     │
   └──────────────┘    └──────────────┘
```

---

## Technology Stack

| Layer | Technology | Reason |
|---|---|---|
| Backend | C# ASP.NET Core Minimal APIs (.NET 9) | Thin, no MVC overhead, JSON in/out |
| Database | SQLite (one file per user) | Portable, file-based, exportable |
| Full-text search | SQLite FTS5 (built-in) | Fast, no extra dependency |
| Semantic search | sqlite-vec extension | Vector storage inside SQLite file |
| Embeddings | all-MiniLM-L6-v2 (ONNX) | Local, free, 384-dim, CPU-capable |
| Tokenizer | Tokenizers.DotNet | HuggingFace-compatible |
| ORM | Dapper or raw SQL | Fine-grained control over SQLite |
| Web UI | Pure HTML + CSS + Vanilla JS (Web Components) | No framework, no build step, native platform |
| Discord client | Discord.Net | DM-based, user-installable app |
| Auth | OAuth2 (Discord, Google) | No password for general access |

---

## Data Model

### One SQLite File Per User

Each user gets a file at `data/users/{userId}.db`. The file is self-contained and portable.

Schema versioning is handled with `PRAGMA user_version`. Migrations run lazily on first open — no migration runner, no EF Core, just sequential version checks:

```csharp
var version = conn.ExecuteScalar<int>("PRAGMA user_version");
if (version < 2) ApplyV2(conn);
if (version < 3) ApplyV3(conn);
conn.Execute($"PRAGMA user_version = {CurrentVersion}");
```

### Core Tables

```sql
-- All content items (notes, ideas, journal entries)
CREATE TABLE notes (
    id          TEXT PRIMARY KEY,  -- ULID
    title       TEXT NOT NULL,
    content     TEXT,              -- Markdown (public part)
    content_secret BLOB,           -- AES-256 encrypted, nullable
    type        TEXT NOT NULL DEFAULT 'note', -- note | idea | journal | password
    tags        TEXT,              -- JSON array
    created_by  TEXT NOT NULL,     -- user_id — immutable after insert
    created_at  TEXT NOT NULL,
    updated_at  TEXT NOT NULL,
    pinned      INTEGER DEFAULT 0,
    archived    INTEGER DEFAULT 0
);

-- FTS5 virtual table for full-text search (public content only)
CREATE VIRTUAL TABLE notes_fts USING fts5(
    id UNINDEXED,
    title,
    content,
    tags,
    content='notes',
    content_rowid='rowid'
);

-- Vector embeddings for semantic search (via sqlite-vec)
CREATE VIRTUAL TABLE vec_notes USING vec0(
    id TEXT PRIMARY KEY,
    embedding FLOAT[384]
);

-- Calendar events
CREATE TABLE events (
    id              TEXT PRIMARY KEY,
    title           TEXT NOT NULL,
    description     TEXT,
    start_at        TEXT NOT NULL,  -- ISO 8601
    end_at          TEXT,
    all_day         INTEGER DEFAULT 0,
    rrule           TEXT,           -- iCal RRULE for recurring events
    location        TEXT,
    reminder_minutes INTEGER,       -- minutes before event to send reminder
    external_id     TEXT,           -- for sync tracking
    external_source TEXT,           -- 'google' | 'ical' | null
    created_by      TEXT NOT NULL,  -- user_id — immutable after insert
    created_at      TEXT NOT NULL,
    updated_at      TEXT NOT NULL
);

-- Sync state for external calendars
CREATE TABLE sync_sources (
    id          TEXT PRIMARY KEY,
    type        TEXT NOT NULL,  -- 'google' | 'ical_url'
    config      TEXT NOT NULL,  -- JSON (url, calendar_id, etc.)
    last_synced TEXT,
    enabled     INTEGER DEFAULT 1
);

-- Reminder delivery tracking
CREATE TABLE reminders (
    id            TEXT PRIMARY KEY,
    event_id      TEXT NOT NULL REFERENCES events(id),
    scheduled_at  TEXT NOT NULL,
    sent_at       TEXT,
    channel_type  TEXT NOT NULL,  -- 'discord' | 'telegram' | future clients
    channel_id    TEXT NOT NULL   -- platform-specific channel/chat ID
);
```

---

## Notes & Extended Markdown

Notes are written in standard Markdown with one extension: the `::secret` block.

### Secret Blocks

```markdown
# Streaming Account
Shared with family. Started 2019.

::secret
username: hello@example.com
password: hunter2
::end
```

Rules:
- `::secret` blocks are stripped before FTS5 indexing and before embedding generation. Secret content is **never searchable**.
- The `content_secret` field stores AES-256-GCM encrypted bytes.
- The encryption key is derived client-side from the user's Master Password using Argon2id.
- The server stores only the encrypted blob. The key never reaches the server.
- In chat clients, secrets are **never sent in plain text**. The bot always responds with a link to the web UI.

### Master Password

The Master Password is a separate credential from the login (OAuth). It is used exclusively for client-side key derivation. It is never transmitted. It is never stored. If lost, encrypted content cannot be recovered — this is by design and must be communicated clearly to the user at setup.

The web UI supports storing the Master Password in the browser's built-in password manager. This is explicitly opt-in and explained in plain language:

> *"Your Master Password stays on your device. We never see it. You can let your browser remember it for convenience — but anyone with access to your browser can then see your secrets."*

Master Password protection is **optional**. Users who do not set one can still use all features except `::secret` blocks.

### Embedding Model

The `all-MiniLM-L6-v2` ONNX model (~90MB) and `tokenizer.json` are not embedded in the executable. They are downloaded on first start if not already present in `fishbowl-data/models/`.

```
fishbowl-data/
  models/
    all-MiniLM-L6-v2.onnx     ← downloaded once, ~90MB
    tokenizer.json             ← downloaded once, ~1MB
```

On first launch the Setup Wizard shows:

```
⬇ Downloading search model (90MB)...  [████████░░] 84%
  This is a one-time download. The model runs locally
  after this — no internet connection required for search.
```

If the download fails or the user skips it — FTS5 keyword search works immediately. Semantic search activates once the model is present. The two search modes are independent.

Model files are never re-downloaded if already present. Version is tracked in `system.db` — a future update can prompt a re-download if a better model becomes available.

---

Search uses a hybrid approach identical to the existing `VectorSearchService` pattern:

- **70% semantic similarity** (cosine similarity on MiniLM embeddings via sqlite-vec)
- **30% keyword matching** (FTS5 with fuzzy Levenshtein fallback)

Embeddings are generated at save time, stored in `vec_notes`. Queries are embedded on-the-fly and compared against stored vectors.

Secret content is excluded from both FTS5 and vector indexes. Searching for a password by its value is intentionally impossible.

### EmbeddingService

Reuse directly from EvaService:
- Model: `all-MiniLM-L6-v2` (ONNX, 384 dimensions)
- Tokenizer: HuggingFace `tokenizer.json` via `Tokenizers.DotNet`
- Runtime: `Microsoft.ML.OnnxRuntime` with `ORT_ENABLE_ALL` graph optimization
- Token limit: 128 (sufficient for notes)
- Normalization: L2 normalization for cosine similarity

---

## Discord Integration

### Bot Type
User-installable app (not server-based). Users add the bot to their account via OAuth2 install link. The bot operates exclusively in DMs.

### First Contact Flow
1. User installs bot via invite link
2. Bot sends welcome message and stores the DM channel ID
3. From this point, the bot can proactively message the user (reminders, etc.)

### Bot Capabilities

**Saving notes:**
```
User: "Remember: team meeting next thursday at 10am"
Bot:  "📅 Saved as a calendar event — Thursday March 26, 10:00
       [View & edit](https://fishbowl.app/events/xyz)"
```

**Searching:**
```
User: "what was that thing about the club"
Bot:  "Found 2 notes that might match:
       • Partner Club Notes (March 2025)
       • Club Venue Contacts
       [Open search results](https://fishbowl.app/search?q=...)"
```

**Reminders:**
```
Bot → User: "⏰ In 30 minutes: Team standup"
```

**Secrets:**
```
User: "what's my streaming password"
Bot:  "🔒 You have a secret entry for Streaming Account.
       I never show passwords in chat.
       [View securely](https://fishbowl.app/notes/abc#secret)"
```

**The bot never:**
- Sends secret content in plain text
- Stores conversation history
- Operates in server channels

---

## Calendar & Sync

### Internal Calendar
The Fishbowl has its own calendar. It is the source of truth. Events are stored in the `events` table with full RRULE support for recurring entries.

### Sync Targets (outbound)
- Google Calendar (OAuth, bidirectional where sensible — Fishbowl wins on conflict)
- iCal feed (outbound read-only URL)
- Future: Outlook, CalDAV

### Sync Sources (inbound, read-only import)
- iCal URL (e.g. import a public holiday calendar)
- Google Calendar import

External events imported from sync sources are marked with `external_source` and `external_id`. They are never treated as Fishbowl-native entries unless explicitly copied.

### Reminder Engine
A background service polls upcoming events and dispatches DM reminders via Discord at the configured interval (`reminder_minutes` per event). Delivery is tracked in the `reminders` table to prevent duplicates.

---

## Authentication

Login is via OAuth2 (Discord, Google — more can be added). No Fishbowl-specific account password exists for general access.

Session management: standard ASP.NET Core cookie auth with CSRF protection (`AntiForgery`) enabled for all state-mutating endpoints. API key requests bypass cookie auth entirely — Bearer token has no CSRF surface.

Master Password: separate, optional, client-side only — see above.

---

## Configuration

> *"Less is more."*

The Fishbowl has exactly two start parameters. Everything else lives in `system.db`, configured through the Setup Wizard and Settings UI.

```bash
fishbowl --port 8080 --data ./my-data
```

| Parameter | Default | Description |
|---|---|---|
| `--port` | `5000` | HTTP port to listen on |
| `--data` | `./fishbowl-data` | Path to data directory |

Double-click without parameters — it just works.

Everything else — OAuth credentials, Discord Bot Token, backup config, deployment mode, plugin settings — is configured through the web UI on first run and stored in `system.db`. No JSON files. No environment variables. No config the user has to find and hand-edit.

---

## Project Structure

### Solution Layout

```
Fishbowl.sln
  src/
    Fishbowl.Core/          ← Domain models, interfaces, no dependencies
    Fishbowl.Data/          ← SQLite, ResourceProvider, migrations
    Fishbowl.Search/        ← EmbeddingService, VectorSearch, FTS5
    Fishbowl.Api/           ← Minimal API endpoints, auth
    Fishbowl.Bot.Discord/   ← Discord.Net integration
    Fishbowl.Bot.Telegram/  ← future
    Fishbowl.Sync/          ← Google Cal, iCal engine
    Fishbowl.Scheduler/     ← Reminder engine, cron jobs
    Fishbowl.Scripting/     ← Jint sandbox, FishScript API
    Fishbowl.Host/          ← Entry point, DI composition, publish target
```

`Fishbowl.Host` is the only publish target. `PublishSingleFile=true` — all DLLs are embedded into a single executable. The user sees one file.

### Dependency Direction

```
Host
  ↓
Api, Bot.Discord, Sync, Scheduler, Scripting
  ↓
Data, Search
  ↓
Core
  ↓ (nothing)
```

`Core` knows no one. Nothing knows `Host` except the entry point. No circular dependencies. Ever.

### Plugin Interface

Plugins — including built-in ones like the Discord Bot — register through the same interface. The Discord Bot eats its own dog food.

```csharp
// Fishbowl.Core — the only thing plugins need to reference
public interface IFishbowlPlugin
{
    string Name    { get; }
    string Version { get; }
    void Register(IServiceCollection services, IFishbowlApi api);
}

public interface IFishbowlApi
{
    void AddBotClient(IBotClient client);
    void AddSyncProvider(ISyncProvider provider);
    void AddScheduledJob(IScheduledJob job);
}
```

---

## Modding

> *"As easy to mod as a community-friendly game."*

The Fishbowl follows one single override pattern everywhere — the same mental model applies to Web Components, CSS, scripts, templates, rules, and plugins:

**If the file exists on disk — use it. Otherwise use the default.**

```
fishbowl-mods/
  components/      ← override or add Web Components
  styles/          ← override or extend CSS
  scripts/         ← custom FishScripts
  templates/       ← custom Collection templates
  plugins/         ← DLL plugins, loaded automatically
```

No manifest file. No whitelist. No config. Drop a file in the right folder — it works. Remove it — the default comes back. Same as Quake, same as Minecraft, same as every mod-friendly game that built a community around it.

### DLL Plugins

Any `.dll` placed in `fishbowl-mods/plugins/` is loaded at startup via an isolated `AssemblyLoadContext` (ALC) to prevent dependency version conflicts (DLL Hell) with the host application. It must implement `IFishbowlPlugin`. No registration file needed.

```
fishbowl-mods/
  plugins/
    Fishbowl.Bot.WhatsApp.dll     ← loaded automatically
    MyCompany.CustomSync.dll      ← loaded automatically
```

**Security note:** Everything in `plugins/` runs with full trust. Only place DLLs there that you trust completely. This is self-hosted software — if you control the machine, you control the plugins.

### The One Rule for Mods

`usr_` prefix for custom Web Components. Everything else is free-named. The prefix prevents conflicts with system resources and makes it immediately clear what is stock and what is community.

```
fb-note-editor.js      ← system, do not touch
usr_dj-card.js         ← yours, do whatever you want
```

### Documentation for Modders

```
/docs/modding/
  web-components.md     ← how to write usr_ components
  fishscript-api.md     ← available FishScript APIs
  templates.md          ← Collection template format
  theming.md            ← CSS variables for themes
  plugins.md            ← IFishbowlPlugin interface
```

The built-in components are the living documentation — read the source, copy the pattern, ship your mod.

---

## Resource System & Modding Infrastructure

> **This is v0.1 blocking infrastructure. Without it, updates break silently and caching is unreliable.**

The Fishbowl ships as a single executable but is fully moddable. Every UI resource — HTML, CSS, JavaScript, Web Components — can be overridden by placing a file in the `fishbowl-mods/` directory next to the binary. Custom Collection renderers live in the user's database and travel with Template exports.

### Three Resource Sources, One Provider

```
Priority order (first match wins):
  1. fishbowl-mods/   ← user overrides + custom components
  2. Database         ← collection renderers, custom scripts
  3. Embedded in exe  ← system defaults, always present
```

```csharp
public class ResourceProvider
{
    // Resources are cached in memory. No hot-modding.
    // Mods dropped into the folder while the app is running are ignored until restart.
    private readonly MemoryCache _cache;

    public async Task<Resource> GetAsync(string path)
    {
        if (_cache.TryGetValue(path, out var cachedResource))
            return cachedResource;

        // 1. Disk
        var diskPath = Path.Combine("fishbowl-mods", path);
        if (File.Exists(diskPath))
        {
            return CacheAndReturn(path, Resource.FromDisk(diskPath));
        }

        // 2. Database (collection renderers)
        var dbResource = await _db.GetResourceAsync(path);
        if (dbResource != null)
        {
            return CacheAndReturn(path, Resource.FromDb(dbResource));
        }

        // 3. Embedded fallback
        return CacheAndReturn(path, Resource.FromEmbedded(path));
    }
}
```

One call. Three sources. Transparent to the caller.

### Folder Structure

```
fishbowl-mods/
  components/
    usr_dj-card.js           ← custom Web Components
    usr_booking-form.js
  styles/
    usr_theme.css            ← custom themes
  scripts/
    usr_weekly-report.js     ← custom FishScripts
  templates/
    usr_dj-roster.json       ← custom Collection templates
```

`usr_` prefix — immediately clear what is system and what is mod. No conflicts with system resources.

### Content-Hash Versioning

Every resource URL contains a content hash. URLs only change when content changes. Browsers cache aggressively — correctly.

```
/components/fb-note-editor.js   →  /components/fb-note-editor.a3f9c2.js
/styles/app.css                 →  /styles/app.css?v=7b2e1a
/mods/usr_dj-card.js            →  /mods/usr_dj-card.c4d8f1.js
```

Hash sources:

| Source | Hash Strategy | Invalidation |
|---|---|---|
| Embedded | SHA256 at build time | Never (new exe = new URL) |
| Disk mod | SHA256 at startup + FileWatcher | On file change |
| DB renderer | Content hash stored on write | On DB write |

### FileSystemWatcher

Disk mods are watched at runtime. File changes invalidate the hash immediately — no restart required.

```csharp
var watcher = new FileSystemWatcher("fishbowl-mods/");
watcher.Changed += (_, e) => _manifest.Invalidate(e.FullPath);
```

### URL Patching

Resources are patched on delivery. Static URLs in HTML, CSS, and JS are rewritten to their versioned equivalents before reaching the browser.

```csharp
public class ResourcePatcher
{
    // Rewrites src=, href=, url(), and static JS imports
    // Binary files are never patched
    // Dynamic URLs use fishbowl.asset() at runtime
}
```

For dynamic URLs in JavaScript:

```javascript
// Static import — patched at delivery time
import { something } from '/components/fb-base.js';

// Dynamic URL — resolved at runtime via helper
const url = fishbowl.asset('/components/fb-base.js');
```

### Asset Manifest

Injected into every HTML page on load:

```html
<script>
window.__fishbowl_assets = {
  "/components/fb-note-editor.js": "/components/fb-note-editor.a3f9c2.js",
  "/mods/usr_dj-card.js":          "/mods/usr_dj-card.7b2e1a.js"
};
window.fishbowl = {
  asset: (path) => window.__fishbowl_assets[path] ?? path
};
</script>
```

### HTTP Caching Rules

```
index.html          Cache-Control: no-cache
                    → browser always checks for updates
                    → server responds 304 if unchanged (fast, no download)
                    → new exe = new hashes = 200 = fresh assets loaded

/assets/**          Cache-Control: immutable, max-age=31536000
                    → hash in URL guarantees uniqueness
                    → cached forever, never re-requested
```

`index.html` is the single source of truth. It is always verified. Everything else is immutable. This means:

- Zero stale JS after an update
- Zero broken UI from mismatched versions
- Maximum cache efficiency — unchanged assets never re-downloaded

### HTTP/2

Enabled by default in ASP.NET Core. No extra configuration. HTTP/2 multiplexing means multiple assets load in parallel over a single connection — faster page loads, especially on first visit with many components.

### Development vs Production

```bash
# Development: all files on disk, live reload
fishbowl --dev

# Production: all embedded, single exe
dotnet publish -c Release -p:PublishSingleFile=true
```

Same ResourceProvider. Same code. Different source priority. During development all system resources sit on disk alongside mods — fast iteration, no rebuild needed. In production everything is embedded.

### v0.1 Blocking Checklist

```
✓ ResourceProvider (three-source lookup)
✓ ResourceManifest (hash computation, all three sources)
✓ FileSystemWatcher (live disk mod invalidation)
✓ ResourcePatcher (URL rewriting in HTML / CSS / JS)
✓ Asset Manifest injection (window.__fishbowl_assets)
✓ fishbowl.asset() runtime helper
✓ HTTP headers: index.html → no-cache, assets → immutable
✓ HTTP/2 enabled
```

---

## Web UI

Pure HTML + CSS + Vanilla JavaScript. No framework. No build step. No bundler. The backend serves static files and exposes a JSON API — the frontend consumes it.

### Web Components

The UI is built entirely from native Web Components (`customElements.define`). Each component is a self-contained file:

```
/wwwroot/
  index.html
  app.css
  components/
    fb-note-editor.js      ← Markdown editor with ::secret block support
    fb-note-list.js        ← scrollable note list
    fb-calendar-view.js    ← month/week calendar
    fb-search-bar.js       ← search input with live results
    fb-secret-block.js     ← locked/unlocked secret display
    fb-event-form.js       ← create/edit calendar event
  lib/
    router.js              ← minimal hash-based client router
    api.js                 ← fetch wrapper for backend API
    crypto.js              ← client-side AES-256-GCM + Argon2id (WebCrypto API)
```

No Shadow DOM is required for layout components — use it only where style isolation genuinely matters (e.g. `fb-secret-block`).

### API Contract

The backend exposes a clean REST API. The frontend knows nothing about the server's internals.

```
GET    /api/v1/notes
POST   /api/v1/notes
GET    /api/v1/notes/:id
PUT    /api/v1/notes/:id
DELETE /api/v1/notes/:id

GET    /api/v1/events
POST   /api/v1/events
GET    /api/v1/events/:id
PUT    /api/v1/events/:id
DELETE /api/v1/events/:id

GET    /api/v1/search?q=...

GET    /api/v1/export/db      ← triggers SQLite file download
```

Design principle: functional over beautiful in v1. No transpilation, no npm, no node_modules. A browser and a text editor is all that is needed to work on the frontend.

---

## User Data Export

At any time, a user can download their complete `.db` file from settings. This is a valid SQLite database readable with any SQLite client. It contains everything except the ability to decrypt secrets without the Master Password (which the user holds).

This is a first-class feature, not an afterthought. It is how we earn trust.

---

## Self-Hosting Made Simple

The Fishbowl ships as a single self-contained binary. No runtime installation. No Docker. No dependencies. Download, double-click, done.

```
fishbowl-win-x64.exe       ← Windows
fishbowl-osx-arm64         ← Mac (Apple Silicon)
fishbowl-osx-x64           ← Mac (Intel)
fishbowl-linux-x64         ← Linux / Raspberry Pi (ARM build available)
```

Built with .NET 9 single-file publish (`PublishSingleFile=true`, `SelfContained=true`). Everything bundled — runtime, dependencies, static web assets.

### First Run

On first launch, the binary:
1. Creates a `fishbowl-data/` directory next to itself
2. Opens `http://localhost:5000` in the default browser
3. Shows a setup wizard: Discord Bot Token, OAuth credentials, admin account
4. Done. The bot connects to Discord and the web UI is live.

### The Raspberry Pi Sweet Spot

A Raspberry Pi is the ideal self-hosting device for The Fishbowl:

- €50-80 hardware, runs 24/7
- 5W power consumption — negligible electricity cost
- .NET 9 runs natively on ARM64
- Sits on the home network, invisible, always on
- Reminders fire at 3am if needed — nobody has to be awake

The Discord bot uses an outbound WebSocket connection to Discord's gateway — **no port forwarding required, no public IP, no firewall rules**. The Fishbowl reaches out to Discord; Discord reaches back into your DMs. It works behind any home router, any NAT, any ISP.

For users who want web UI access from outside their home network, Tailscale or a simple Cloudflare Tunnel covers that — but it is entirely optional and out of scope for the core product.

---

## Backup Strategy

One SQLite file per user is not just an architecture decision — it is a backup strategy.

### The Core Insight

```
cp fishbowl-data/users/abc123.db /mnt/nas/backups/
```

That is a complete, valid, restorable backup. No dump. No export wizard. No special tooling. Any file copy is a backup.

### Built-in Backup Options

The Fishbowl includes a backup scheduler configured through the Settings UI and stored in `system.db`:

- Destination path (NAS mount, external drive, Dropbox folder, any path)
- Schedule (default: 3am daily)
- Retention period (default: 30 days)


### SQLite Online Backup API

SQLite has a built-in online backup API that creates a consistent snapshot even while the database is being written to. No locking. No downtime. No corruption risk.

```csharp
// Safe hot backup — no lock, no downtime
using var source = new SqliteConnection($"Data Source={userDbPath}");
using var dest = new SqliteConnection($"Data Source={backupPath}");
source.Open();
dest.Open();
source.BackupDatabase(dest);
```

### Backup Destinations

| Destination | How |
|---|---|
| NAS (Synology, QNAP, etc.) | Mount as network share, point `Destination` at it |
| External USB drive | Mount path, same config |
| Dropbox / OneDrive folder | Point at the local sync folder |
| Rclone target | Call rclone as post-backup hook (S3, Backblaze, Google Drive...) |
| Another machine via rsync | Post-backup hook script |

The Fishbowl does not prescribe a destination — it writes files to a path. What happens to those files is the user's choice.

### Restore

Restoration is equally trivial:

```
cp /mnt/nas/fishbowl-backups/abc123.db fishbowl-data/users/abc123.db
```

Restart the service. Done. Full restore, no wizard, no support ticket.

### In-App Backup Status

The web UI settings page shows:
- Last successful backup timestamp
- Backup destination (masked for privacy)
- Manual "Back up now" button
- Warning if no backup has run in over 48 hours

### What Is Not Backed Up

The binary itself is not backed up — it can always be re-downloaded. Only the `fishbowl-data/` directory matters. Users are informed of this clearly in the setup wizard.

---

## Spaces — Technical Details

Users can belong to multiple Spaces. A Space has its own SQLite database — structurally identical to a user database. The same schema, the same services, the same search. Just shared (or solo).

```
fishbowl-data/
  users/
    abc123.db        ← fixed schema, private data only
    def456.db        ← fixed schema, another user
  spaces/
    acme-team.db     ← fixed schema, shared by members
    project-x.db     ← fixed schema, selected members only
  apps/
    staff-roster.db  ← dynamic schema, owned by user or space
    grocery-list.db  ← dynamic schema
```

### Space Membership

Stored in `system.db`. Four roles — nothing more complex is needed:

```sql
CREATE TABLE spaces (
    id           TEXT PRIMARY KEY,
    name         TEXT NOT NULL,
    slug         TEXT NOT NULL UNIQUE,  -- 'acme-team', 'project-x'
    created_at   TEXT NOT NULL
);

CREATE TABLE space_members (
    space_id   TEXT NOT NULL REFERENCES spaces(id),
    user_id    TEXT NOT NULL REFERENCES users(id),
    role       TEXT NOT NULL CHECK(role IN ('owner','admin','member','readonly')),
    joined_at  TEXT NOT NULL,
    PRIMARY KEY (space_id, user_id)
);
```

| Role | Can read | Can write | Can invite | Can delete space |
|---|---|---|---|---|
| readonly | ✓ | ✗ | ✗ | ✗ |
| member | ✓ | ✓ | ✗ | ✗ |
| admin | ✓ | ✓ | ✓ | ✗ |
| owner | ✓ | ✓ | ✓ | ✓ |

### Context Switching

The `UserDatabaseFactory` becomes a `ContextDatabaseFactory`. One extra parameter — personal or space context. Same code, same migrations, same search engine.

```csharp
public Task<IDbConnection> GetConnectionAsync(string contextId, ContextType type)
// type = ContextType.User | ContextType.Space
```

### Collection Security Rules

Each collection has an optional `.rules` file stored in the database. This is the sole source of truth for access control on that collection — no separate permission tables, no role matrices.

```javascript
// Default rules — applies when no rules file exists
rules {
  read:   member()
  write:  member()
  delete: creator() || admin()
}
```

Users who want nothing more than this never touch rules. Users who want granular control can go as deep as they like.

**Built-in rule functions:**

```javascript
// Identity
member()           // is a space member
admin()            // is space admin or owner
owner()            // is space owner
creator()          // created this specific entry
owner_of(entry)    // entry.created_by === current_user
authenticated()    // is logged in at all

// Field-level conditions
entry.FieldName == "value"
entry.Balance >= 0
```

**Example — Staff Roster with financial privacy:**

```javascript
rules {
  read:   member()
  write:  member()
  delete: creator() || admin()

  field("Salary") {
    read:  admin() || creator()   // only admin and creator see pay
    write: admin()
  }
}
```

**Example — read-only archive:**

```javascript
rules {
  read:   member()
  write:  admin()
  delete: admin()
}
```

**Example — fully locked internal collection:**

```javascript
rules {
  read:   admin()
  write:  admin()
  delete: admin()
  deny:   !admin()
}
```

Rules are stored in `collection_rules` inside the collection's database — versionable, exportable with templates, AI-generatable on request, handwriteable by nerds, entirely ignorable by everyone else.

**AI-assisted rule generation:**

```
User: "only admins should see the Salary field"

Bot:  "Updated rules for Staff Roster:

      field("Salary") {
        read:  admin() || creator()
        write: admin()
      }

      [Activate]?"
```

### In the Discord Bot

```
@fishbowl note for the-bassline: new event saturday
```

Or with auto-prompt when context is ambiguous:

```
Bot: "Save to which context?"
     🐟 Personal
     🏢 Acme Team
     🎸 Project X
```

Spaces are a v0.1 architecture decision — retrofitting this later would be painful. The schema and factory pattern must exist from day one even if the UI for managing Spaces comes in v0.2.

---

## Multi-User Architecture

Multi-user is not an afterthought — it is baked in from day one.

### User Provisioning

When a user authenticates for the first time (via OAuth), the system automatically provisions a SQLite database for them:

```csharp
public class UserDatabaseFactory
{
    private readonly string _basePath;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public async Task<IDbConnection> GetConnectionAsync(Guid userId)
    {
        var path = Path.Combine(_basePath, "users", $"{userId}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var conn = new SqliteConnection($"Data Source={path}");
        await conn.OpenAsync();
        await MigrateAsync(conn);
        return conn;
    }

    private async Task MigrateAsync(IDbConnection conn)
    {
        var version = await conn.ExecuteScalarAsync<int>("PRAGMA user_version");
        if (version < 1) await ApplyV1Async(conn);
        if (version < 2) await ApplyV2Async(conn);
        // ... lazy, per-connection, always forward-only
        await conn.ExecuteAsync($"PRAGMA user_version = {CurrentVersion}");
    }
}
```

### Global Database

A single small global SQLite file (`global.db`) exists alongside the user databases. It contains only:

```sql
-- User registry
CREATE TABLE users (
    id           TEXT PRIMARY KEY,  -- ULID
    display_name TEXT NOT NULL,
    email        TEXT,
    created_at   TEXT NOT NULL,
    last_seen_at TEXT
);

-- OAuth provider identities
CREATE TABLE identities (
    id          TEXT PRIMARY KEY,
    user_id     TEXT NOT NULL REFERENCES users(id),
    provider    TEXT NOT NULL,  -- 'discord' | 'google'
    provider_id TEXT NOT NULL,
    UNIQUE(provider, provider_id)
);

-- Discord bot DM channel mapping
CREATE TABLE discord_channels (
    user_id    TEXT PRIMARY KEY REFERENCES users(id),
    channel_id TEXT NOT NULL
);
```

Nothing sensitive lives in the global database. All user content lives exclusively in the user's own SQLite file.

### Isolation Guarantee

Each user's database is completely isolated. A bug affecting one user's data cannot affect another's. A user can delete their account and the entire `.db` file is deleted — no residual data anywhere.

---

## Deployment Modes

The Fishbowl supports three deployment modes out of the box. The same binary runs in all three.

### Mode 1 — Self-Hosted (Single Owner)

The user runs their own instance. They are the only user, they control the server, their data never leaves their machine. Configured via Setup Wizard on first run — stored in `system.db`.

### Mode 2 — Self-Hosted (Family / Team)

The user runs an instance for a small group — family, friends, a team. Registration is invite-only. Configured via Setup Wizard and Settings UI — stored in `system.db`.

### Mode 3 — Managed Service

The operator (e.g. thefishbowl.app) hosts a public instance. Open registration, optional quota management, abuse prevention, future billing hooks. All configured through the admin Settings UI — stored in `system.db`.

In managed mode, an admin interface becomes available for user management and monitoring. The admin interface is out of scope for v0.1 but the mode flag must exist from the start.

### Licence

The Fishbowl is published under the **AGPL-3.0** licence. This means:

- Anyone can self-host for free
- Anyone can modify the code freely
- Any party offering the Fishbowl as a hosted service must publish their modifications

This is the Bitwarden model. It protects the open-source nature of the project while allowing a sustainable managed-service business on top.

---

## Open API & Agent Integration

The Fishbowl is not a closed silo. Every user can generate personal API keys and expose their own data to any AI agent, automation, or external tool they trust. **Secrets are structurally excluded — no scope exists for them, no exception is possible.**

### API Keys

Users generate API keys in Settings. Each key has a defined set of scopes:

```
read:notes         read:calendar      read:contacts
write:notes        write:calendar     write:contacts
read:tasks         write:tasks
read:attachments   write:attachments
read:tags

secrets            ← THIS SCOPE DOES NOT EXIST
```

Keys can be named ("My Claude setup", "Home automation"), revoked individually, and optionally restricted to a list of IP addresses. The key is shown once at creation — after that it is hashed and never retrievable again, like GitHub tokens.

### REST API

The public REST API is identical to the internal API used by the web UI — no second-class citizen. Full CRUD on all resources, consistent JSON responses, OpenAPI spec published at `/api/openapi.json`.

```
Authorization: Bearer fb_live_abc123...

GET  /api/v1/notes?tag=Q4-Report
GET  /api/v1/calendar/upcoming?days=7
GET  /api/v1/contacts/search?q=Alice
POST /api/v1/notes
```

### MCP Server (Model Context Protocol)

The Fishbowl exposes an MCP server endpoint. Any MCP-compatible AI tool — Claude, Cursor, or custom agents — can connect directly to a user's Fishbowl instance using their API key.

```json
{
  "mcpServers": {
    "fishbowl": {
      "url": "http://localhost:5000/mcp",
      "apiKey": "fb_live_abc123..."
    }
  }
}
```

**What an AI agent can do via MCP:**

```
"What do I have on this week?"
  → reads calendar events for next 7 days

"Find my notes about the tax documents"
  → semantic search across notes and attachments

"Add a task: call the client"
  → creates task with no date

"Who is Alice?"
  → returns contact with linked notes and events
```

**What an AI agent can never do via MCP:**

- Read, list, or acknowledge the existence of `::secret` blocks
- Access encrypted content in any form
- The MCP server strips all secret-adjacent data before it ever reaches the response serializer — not as a policy check, but as a structural absence

### The Vision

When the MCP server is live, Claude can be connected directly to your own Fishbowl instance running on your Raspberry Pi at home. "Hey Claude, what's on this week?" answered from your actual calendar. "Find my notes about the project from last month" found via semantic search across your real notes. Your AI assistant, your data, your hardware. No cloud middleman.

---

## Roadmap

```
v0.1  — Foundation
        Notes + Markdown, Calendar + Reminders
        Discord Bot (DM-based, user-installable)
        Web UI (vanilla JS + Web Components)
        SQLite per user, lazy migrations
        Discord + Google OAuth
        Self-hosted binary (Windows / Mac / Linux / Raspberry Pi)
        User data export (SQLite download)

v0.2  — Secrets Polish
        Master Password change / reset flow
        TOTP as optional session gate for secret access
        Browser password manager integration guide
        Backup scheduler (NAS, local path, rclone hook)

v0.3  — Knowledge Expansion
        Contacts (with linked notes, events, tasks)
        Tasks / To-Dos (standalone + linked)
        Attachments + Documents (PDF, images, Office blobs)
        OCR indexing (Tesseract, local, no API)
        AI-suggested tagging on ingest
        Systemwide tags connecting all content types

v0.4  — Openness
        Public REST API (OpenAPI spec)
        User API key management (scoped, revocable)
        Google Calendar sync (bidirectional, Fishbowl wins conflicts)
        iCal feed (outbound) + iCal URL import (inbound)
        Admin interface (user management, quota, managed mode)

v0.5  — Agent Ready
        MCP server endpoint
        Claude / Cursor / custom agent integration
        Webhook support (outbound events for automation)
        Telegram bot (second chat client)

v0.6  — Apps
        App database (dynamic schema, separate from user/space DB)
        Custom field definitions, Views (Table / Kanban / List)
        Simple If→Then Workflows
        User + Space ownership per App
        App Security Rules (Firebase-style .rules file)
        Creator-owns-delete enforced everywhere
        App Permissions (iOS-style prompts, secrets structurally excluded)

v0.7  — Scripting
        Jint JavaScript Sandbox (pure C#, no Node)
        FishScript API (ctx.notify, tasks, calendar, app, schedule)
        AI-assisted Script Generation via Claude
        Scheduled scripts (cron-style)
        AI task detection in free-text notes

v0.8  — Templates & Community
        Template Export (schema + static data, no user rows)
        Template Import with Permission Prompt
        Public Template Directory
        Community sharing via URL

v0.9  — Full Controllability
        MCP extended to Collections + Scripts + Tasks
        Webhook triggers (inbound + outbound)
        External agents can read / write / control Collections
        n8n / Make compatible via webhooks

v1.0  — Public Launch
        Self-hosting polished and documented
        Managed service (thefishbowl.app)
        AGPL-3.0 public release
        Mobile-optimised web UI
```

---

## MVP Scope (v0.1)

The following is in scope for the initial version:

- Multi-user from day one — registration via OAuth, one SQLite file per user provisioned automatically
- SQLite per-user architecture with lazy schema versioning
- Note CRUD with Markdown support
- `::secret` block full support — Master Password setup in Setup Wizard (optional step), client-side AES-256-GCM encryption via WebCrypto, web-UI-only viewing
- FTS5 full-text search
- Basic semantic search (MiniLM embeddings + sqlite-vec)
- Discord bot: save notes via DM, search via DM, receive reminders
- Web UI: note list, note editor, basic search, user settings
- Calendar: create events, set reminders, receive Discord DM reminders
- Discord OAuth login + Google OAuth login
- User data export (SQLite file download)

The following is explicitly **out of scope** for v0.1:

- Google Calendar sync
- iCal sync
- WhatsApp / Telegram clients
- Master Password UI and client-side encryption (architecture is ready, UI deferred)
- Admin interface
- Mobile web optimization
- Billing / quota management

---

## Project Name

**The Fishbowl**

Named for the goldfish — the user who forgets. The bowl remembers everything so the fish doesn't have to.

Logo: a goldfish in a glass bowl. Simple, iconic, warm.

Tagline: *"Your memory lives here."*
