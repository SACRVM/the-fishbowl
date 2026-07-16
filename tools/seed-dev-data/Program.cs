using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;

// Dev utility: seeds a variety of notes, todos, and contacts into a
// context so the UI and MCP tools have real data to work against.
//
// Idempotent: every seeded row carries the `seed:dev` tag (notes) or
// the "[seed-dev]" name prefix (contacts/todos). Re-running is a no-op
// when the sentinel is already present — counts report the pre-existing
// state rather than re-inserting.
//
// Usage:
//   dotnet run --project tools/seed-dev-data -- \
//       [--data <path>] \
//       [--user <id>] \
//       [--context user|space] [--context-id <slug>] \
//       [--force]              (reseed even if sentinels already exist)
//
// Defaults:
//   --data fishbowl-data             (matches Fishbowl.Host's default)
//   --user <first user in system.db>
//   --context user                   (--context-id required when space)

var args_ = args;
var dataPath = GetArg("--data") ?? "fishbowl-data";
var userIdArg = GetArg("--user");
var contextArg = (GetArg("--context") ?? "user").ToLowerInvariant();
var contextIdArg = GetArg("--context-id");
var force = args_.Contains("--force");

if (!Directory.Exists(dataPath) || !File.Exists(Path.Combine(dataPath, "system.db")))
{
    Console.Error.WriteLine($"error: no system.db found at {Path.GetFullPath(dataPath)}/system.db — start the host at least once first.");
    return 2;
}

var factory = new DatabaseFactory(dataPath);

string userId;
if (!string.IsNullOrEmpty(userIdArg))
{
    userId = userIdArg;
}
else
{
    using var sys = factory.CreateSystemConnection();
    var first = sys.QueryFirstOrDefault<string>(
        "SELECT id FROM users ORDER BY created_at LIMIT 1");
    if (string.IsNullOrEmpty(first))
    {
        Console.Error.WriteLine("error: no users in system.db — log in via the web UI first, then retry.");
        return 3;
    }
    userId = first;
}

ContextRef ctx;
string contextDisplay;
if (contextArg == "user")
{
    ctx = ContextRef.User(userId);
    contextDisplay = $"user:{userId}";
}
else if (contextArg == "space")
{
    if (string.IsNullOrEmpty(contextIdArg))
    {
        Console.Error.WriteLine("error: --context space requires --context-id <slug>");
        return 5;
    }

    using var sys = factory.CreateSystemConnection();
    var spaceRow = sys.QueryFirstOrDefault<(string Id, string Slug)>(
        "SELECT id AS Id, slug AS Slug FROM spaces WHERE slug = @slug OR id = @slug",
        new { slug = contextIdArg });
    if (string.IsNullOrEmpty(spaceRow.Id))
    {
        Console.Error.WriteLine($"error: no space found with slug or id '{contextIdArg}'");
        return 6;
    }
    ctx = ContextRef.Space(spaceRow.Id);
    contextDisplay = $"space:{spaceRow.Slug}";
}
else
{
    Console.Error.WriteLine($"error: --context must be 'user' or 'space' (got '{contextArg}')");
    return 8;
}

const string NoteSentinelTag  = "seed:dev";
const string NamePrefix        = "[seed-dev] ";

// Check the existing seed footprint. If we see the sentinel and --force
// isn't set, report and bail — reseeds would duplicate rows because none
// of the repos have upsert-by-name semantics.
using (var probe = factory.CreateContextConnection(ctx))
{
    var existingNotes = probe.ExecuteScalar<long>(
        "SELECT COUNT(*) FROM notes WHERE tags LIKE '%' || @tag || '%'",
        new { tag = NoteSentinelTag });
    var existingContacts = probe.ExecuteScalar<long>(
        "SELECT COUNT(*) FROM contacts WHERE name LIKE @pfx || '%'",
        new { pfx = NamePrefix });
    var existingTodos = probe.ExecuteScalar<long>(
        "SELECT COUNT(*) FROM todos WHERE title LIKE @pfx || '%'",
        new { pfx = NamePrefix });
    var existingEvents = probe.ExecuteScalar<long>(
        "SELECT COUNT(*) FROM events WHERE title LIKE @pfx || '%'",
        new { pfx = NamePrefix });

    if (!force && (existingNotes + existingContacts + existingTodos + existingEvents) > 0)
    {
        Console.Error.WriteLine(
            $"# already seeded in {contextDisplay}: " +
            $"{existingNotes} notes, {existingContacts} contacts, " +
            $"{existingTodos} todos, {existingEvents} events. " +
            "Pass --force to add another batch.");
        Console.WriteLine(
            $"{{\"notes\":{existingNotes},\"contacts\":{existingContacts}," +
            $"\"todos\":{existingTodos},\"events\":{existingEvents},\"reseeded\":false}}");
        return 0;
    }
}

var tagRepo     = new TagRepository(factory);
var noteRepo    = new NoteRepository(factory, tagRepo);
var todoRepo    = new TodoRepository(factory);
var contactRepo = new ContactRepository(factory);
var eventRepo   = new EventRepository(factory);

// ────────── Notes ──────────
//
// A corpus big enough to stress-test hybrid search. Notes are grouped by
// topic so re-reading the source gives a map of the search probes:
//
//   * SEMANTIC-WIN probes — pairs where the query wording doesn't overlap
//     the note wording, so the embedding has to carry the match.
//   * LEXICAL-WIN probes — notes full of specific identifiers (error codes,
//     product SKUs, function names) that FTS5 prefix-match nails and the
//     embedding smears.
//   * PARAPHRASE TWINS — two notes about the same concept using different
//     vocabulary; ranking should surface both for either phrasing.
//
// All notes share the seed:dev sentinel tag. A handful also carry
// review:pending so the `includePending` filter on /api/v1/search has
// something to flip between.
//
// Embedding reminder: this tool writes notes WITHOUT embeddings (the
// repo's IEmbeddingService dep is left null). Run `tools/reindex-dev`
// afterwards to populate vec_notes — otherwise search degrades to FTS-only.
Note N(string title, string content, params string[] tags) => new()
{
    Title   = NamePrefix + title,
    Content = content,
    Tags    = tags.Append(NoteSentinelTag).ToList(),
};

var notes = new List<Note>
{
    // ── Smoke-test baseline (kept from v1 of this tool) ──
    N("Welcome to the Fishbowl",
      "This is a seeded note so the editor has something to open.\n" +
      "Tags are regular user tags plus the `seed:dev` sentinel.",
      "welcome"),
    N("Meeting notes — venue walkthrough",
      "- stage left is the load-in door\n- power drops at both sides\n- sound check from 4pm",
      "work", "venue"),
    N("Groceries",
      "- eggs\n- oat milk\n- tomatoes\n- something for pasta",
      "personal", "shopping"),
    N("Reading list",
      "Anything about distributed consensus — the usual suspects.\n" +
      "Tag for quick filter.",
      "reading"),
    N("A note with a secret block",
      "Public bit first.\n\n::secret\nsecretuser: alice\nsecretpass: hunter2\n::end\n\nPublic tail.",
      "auth"),

    // ── Programming / distributed systems ──
    // Probes: "how do servers agree on a value?" → Raft note (semantic).
    //         "CrashLoopBackOff" → k8s note (lexical).
    //         "avoid thundering herd" → jitter note (semantic paraphrase).
    N("Raft consensus — leader election basics",
      "Each node starts as follower. Election timeout → candidate → asks peers for votes. " +
      "Majority quorum wins the term. Heartbeats from the leader reset timeouts. Split votes back off randomly.",
      "tech", "distributed-systems"),
    N("Paxos is the grandparent",
      "Raft was designed to be Paxos-but-understandable. Same impossibility result — FLP — same " +
      "quorum math. Multi-Paxos and Raft make equivalent progress guarantees; Raft just names the roles.",
      "tech", "distributed-systems"),
    N("Rust ownership in one paragraph",
      "Every value has exactly one owner. When the owner goes out of scope, Drop runs. " +
      "Borrows are either shared (&T, many) or exclusive (&mut T, one). The borrow checker rejects " +
      "anything that would let you observe a value after it moved or was freed.",
      "tech", "rust"),
    N("Memory safety without a garbage collector",
      "Linear types, RAII, compile-time lifetime tracking. No stop-the-world pauses, " +
      "no finalizer surprises. Cost: you pay for correctness at build time instead of runtime.",
      "tech", "rust", "languages"),
    N("Kubernetes CrashLoopBackOff triage",
      "`kubectl describe pod <name>` → look at Events.\n" +
      "`kubectl logs <pod> --previous` for the last container's stderr.\n" +
      "Most common causes: missing ConfigMap/Secret, failing readiness probe, OOMKilled (check limits).",
      "tech", "kubernetes", "ops"),
    N("Exponential backoff with jitter",
      "Don't retry in lockstep — every client hammering the service at t=2, 4, 8… is a DDoS. " +
      "Add random jitter so the retry wall spreads out. AWS SDK defaults to full jitter: sleep = random(0, min(cap, base*2^n)).",
      "tech", "reliability"),
    N("Python asyncio — event loop gotchas",
      "Blocking calls inside a coroutine freeze the loop. `asyncio.run()` owns the loop — don't nest. " +
      "Use `asyncio.to_thread()` for sync libs. `gather` surfaces the first exception; prefer `as_completed` for resilience.",
      "tech", "python"),
    N("ACME HTTP-01 challenge flow",
      "ACME client requests a cert. CA returns a token; client serves it at `/.well-known/acme-challenge/<token>`. " +
      "CA fetches it over HTTP-01, verifies, issues cert. DNS-01 uses a TXT record instead — needed for wildcards.",
      "tech", "tls", "lets-encrypt"),

    // ── Databases ──
    // Probes: "why is my LIKE query slow" → B-tree/GIN note (semantic).
    //         "PRAGMA journal_mode=WAL" → SQLite WAL note (lexical).
    N("SQLite WAL mode in practice",
      "`PRAGMA journal_mode=WAL;` — writers don't block readers, readers don't block writers. " +
      "WAL file grows until a checkpoint; `PRAGMA wal_autocheckpoint=1000` is the default threshold. " +
      "Backup via the online backup API is safe under concurrent writes.",
      "tech", "sqlite", "databases"),
    N("Postgres EXPLAIN ANALYZE cheatsheet",
      "`EXPLAIN ANALYZE SELECT …` — top line is the chosen plan, nested lines are children. " +
      "Watch for Seq Scan on big tables, bad row estimates (planner misjudged selectivity), " +
      "and high loops × cost on nested loops. `BUFFERS` adds cache hit stats.",
      "tech", "postgres", "databases"),
    N("B-tree vs GIN indexes",
      "B-tree: ordered, great for equality and range. Useless for full-text or array containment. " +
      "GIN: inverted index, built for `@>`, `?`, and tsvector. LIKE with a leading `%` skips the " +
      "B-tree — that's why those queries crawl.",
      "tech", "postgres", "databases"),
    N("Transaction isolation — READ COMMITTED default",
      "Dirty reads impossible, non-repeatable reads possible, phantom reads possible. " +
      "REPEATABLE READ escalates by snapshotting rows; SERIALIZABLE adds predicate locks. " +
      "Most apps are fine on the default — the surprises live at the boundary of two transactions.",
      "tech", "databases"),
    N("sqlite-vec storage layout notes",
      "`vec0` virtual table stores FLOAT[N] as raw little-endian f32 blobs. `MATCH` queries take " +
      "a blob of the same shape and return `distance` (cosine by default on L2-normalised vectors). " +
      "INSERT OR REPLACE doesn't behave like a real replace on vec0 — use DELETE + INSERT.",
      "tech", "sqlite", "search"),

    // ── Git / dev workflow ──
    N("git rebase vs merge — when each fits",
      "Rebase keeps history linear; rewrite only local branches, never shared ones. Merge preserves " +
      "context of when branches diverged — useful on long-lived feature lines. Team convention beats ideology.",
      "tech", "git"),
    N("git reflog saves your bacon",
      "Nothing is actually gone for ~90 days. `git reflog` → find the SHA (e.g. `HEAD@{12}`) → " +
      "`git reset --hard <sha>` or `git checkout -b rescue <sha>`. Also works after a bad rebase.",
      "tech", "git"),
    N("Conventional commits — the why",
      "`type(scope): subject` isn't bureaucracy — `feat:` and `fix:` drive changelog generation and " +
      "semver bumps. Breaking changes use `!` or a `BREAKING CHANGE:` footer. Everything else is prose.",
      "tech", "git", "conventions"),
    N("Pre-commit hooks — lefthook over husky",
      "Lefthook is a single Go binary, no node_modules. Same mental model as husky: hook config in " +
      "`lefthook.yml`, one command per hook. Fast enough to run on every file without annoying anyone.",
      "tech", "git", "tooling"),

    // ── Cooking ──
    // Probes: "how to make soup base" → stock note (semantic).
    //         "umami stock" → dashi note (paraphrase).
    N("Ramen broth — dashi base",
      "Kombu cold-steeps overnight, then pull at 60°C. Add katsuobushi, steep 10 min off heat, strain. " +
      "Don't boil kombu — it turns bitter and slimy. That's the foundation layer; tare goes on top.",
      "cooking", "japanese"),
    N("Building a chicken stock",
      "Roast the bones at 200°C until deeply brown. Cold water start, aromatics after the first " +
      "skim. Low simmer 4 hours — a rolling boil emulsifies fat into a cloudy mess. Strain, reduce, freeze flat.",
      "cooking", "fundamentals"),
    N("Sourdough hydration at 75%",
      "For every 500g flour, 375g water. Autolyse 45 min before adding starter and salt. Bulk 4h at 24°C " +
      "with 3 sets of stretch-and-folds. Cold retard overnight. Scoring depth matters more than pattern.",
      "cooking", "baking"),
    N("Pasta water — salty as the sea",
      "10g salt per litre is the floor, 20g is better. The starch slurry picks up that salt and coats " +
      "the sauce. Under-salted pasta water is the most common fix I make on other people's cooking.",
      "cooking", "pasta"),
    N("Paella — bomba rice, nothing else",
      "Bomba absorbs ~3× its volume without collapsing. Calasparra is the runner-up. Don't stir once " +
      "the rice is in — the socarrat crust at the bottom is the whole point. SKU: Brillante Bomba 1kg.",
      "cooking", "spanish"),

    // ── Music / audio ──
    // Probes: "pre-show audio setup" ↔ "sound check" (paraphrase twins).
    //         "Radial JDI" → DI box note (lexical).
    N("Sound check checklist",
      "1) Line check every channel. 2) Monitor mix per performer. 3) FOH EQ sweep — kill the 200Hz mud. " +
      "4) Ring out wedges. 5) A/B the vocal with a playlist track. If there's time, walk the room mid-set.",
      "music", "audio", "live"),
    N("Pre-show audio setup — mic placement and gain",
      "Stage volume first, house second. SM57 an inch off the grille on guitar cabs, angled in. " +
      "Kick mic inside the shell, pointing at the beater. Set gain where the meter peaks at -6 dBFS during a loud hit.",
      "music", "audio", "live"),
    N("Mastering loudness — LUFS targets",
      "Spotify: -14 LUFS integrated, 1 dB true peak ceiling. Apple Music: -16 LUFS. YouTube: -14. " +
      "Mix for dynamics, not the target — normalisation bumps quiet tracks up, nothing to gain by pre-smashing.",
      "music", "mixing"),
    N("DI box — Radial JDI when it matters",
      "Passive, Jensen transformer, zero phantom-power fuss. Clean DI for bass, acoustic pickups, keys. " +
      "Active (JDV) only when you need the extra headroom or a hi-Z pedal chain. PN: R800-1010.",
      "music", "gear"),
    N("In-ear monitors — Shure PSM300 setup",
      "Scan for open UHF block, set TX/RX to matching channel. Ambient mic on the belt pack avoids " +
      "the isolated-bubble feeling. Always have a wired backup cable in the pelican.",
      "music", "gear"),

    // ── Travel ──
    // Probes: "fix my body clock" → jet lag note (semantic).
    //         "JR Pass activation" → Japan Rail note (lexical).
    N("Japan Rail Pass — activation rules",
      "Buy the exchange order before you land; swap at any JR ticket office within 3 months. " +
      "Activation date is chosen at exchange — pick it for when you'll actually ride long distance. " +
      "7/14/21-day validity starts the activation date, not first use.",
      "travel", "japan"),
    N("Lisbon — what I keep recommending",
      "Alfama for the miradouros early in the morning before tour groups. Time Out Market for a fast " +
      "lunch spread. Belém for pastel de nata at the original Pastéis de Belém — queue is worth it.",
      "travel", "portugal"),
    N("Backpack essentials — one-bag target",
      "Target total 7kg including bag. Packing cubes matter more than zip-off trousers. " +
      "One warm layer, one rain shell, three tee rotation. Charger brick + USB-C, not a bag of adapters.",
      "travel", "packing"),
    N("Jet lag recovery — light timing",
      "Fix the body clock by timing bright light: morning sun on arrival day pulls you earlier, " +
      "evening exposure pushes you later. Melatonin 0.5mg an hour before target bedtime for the first three nights.",
      "travel", "health"),

    // ── Books / reading ──
    N("The Making of Prince of Persia — Jordan Mechner",
      "Journals from 1985–93. Half about animation and rotoscoping his brother, half about anxiety " +
      "and shipping a game nobody asked for. The most honest thing I've read about creative work.",
      "reading", "books"),
    N("Zen and the Art of Motorcycle Maintenance",
      "Quality as the thing you recognise before you can define. The metaphysics chapters drag; " +
      "the father-son road trip and the workshop scenes don't. Still a foundational book for me.",
      "reading", "books"),
    N("Rilke — Letters to a Young Poet",
      "Ten short letters. 'Live the questions now.' Keep returning to letter four on solitude.",
      "reading", "poetry"),
    N("Ted Chiang — Stories of Your Life and Others",
      "If you liked Arrival, read 'Hell Is the Absence of God' next. Tower of Babylon for the craft, " +
      "Understand for the cognition-as-horror setup. Best working short story writer in English.",
      "reading", "sci-fi"),

    // ── Health / fitness ──
    N("Squat — the three cues that fix most issues",
      "1) Chest up — stop folding at the hip before the knee. 2) Knees tracking toes — no cave-in. " +
      "3) Drive the floor away, don't stand up. Depth fixes itself once those three lock in.",
      "health", "fitness"),
    N("Shoulder rehab — band pull-aparts",
      "3 × 15 with a light resistance band, elbows locked, squeeze the shoulder blades. " +
      "Daily, not just on gym days. My physio called this 'tooth-brushing for posture' and she was right.",
      "health", "rehab"),
    N("Sleep reset — fix insomnia in a week",
      "Same wake time every morning regardless of how the night went. No caffeine after noon. " +
      "Bedroom at 17–19°C. Screens off 30 min before lights-out. Boring by design — that's the point.",
      "health", "sleep"),
    N("Morning routine — water then coffee",
      "500ml water on waking clears overnight dehydration. Coffee 60–90 min later lets adenosine clear " +
      "and sidesteps the afternoon crash. Not a hack, just how the biology actually works.",
      "health", "routine"),

    // ── Personal / reflection ──
    N("Weekly gratitude check-in",
      "Sunday evening: three things that went well, one thing I'd do differently, one person to thank. " +
      "Five minutes max or it becomes journaling and I quit the habit.",
      "personal", "reflection"),
    N("Therapy session — running notes",
      "Pattern: I frame decisions as binary when they're not. Prompt to try: 'what's the third option?'. " +
      "Homework — notice the framing in real time, write one example a day.",
      "personal", "therapy"),
    N("Gift ideas — mum's birthday",
      "She's been rereading Donna Leon. Venice walking guide or the newest Brunetti in hardback. " +
      "Plant-wise: the ceramic Haws watering can she pointed at last time we were at the garden centre.",
      "personal", "gifts"),
    N("Friendship dinner — rotating hosts",
      "Every 6 weeks, four of us, hosted on rotation. Host picks cuisine and one conversation prompt. " +
      "No phones on the table. Works because the bar is low and the cadence is sacred.",
      "personal", "friends"),

    // ── Work / meetings ──
    N("Standup — blockers first",
      "Reverse the usual order: blockers → today → yesterday. Surfaces problems before everyone's " +
      "tuned out. Cap at 15 min; anything longer is a working session in disguise.",
      "work", "process"),
    N("1:1 template with manager",
      "1) What's on your mind? 2) What's blocking you? 3) What do you want feedback on? " +
      "4) Career / growth thread (every 3rd one). Agenda lives in a shared doc — we both own it.",
      "work", "process"),
    N("Q2 learning OKRs — draft",
      "O: level up on distributed systems foundations. KR1: finish Designing Data-Intensive Applications. " +
      "KR2: ship a Raft toy implementation. KR3: write a blog post explaining FLP impossibility in plain terms.",
      "work", "okrs"),
    N("Salary benchmarking resources",
      "Levels.fyi for FAANG-adjacent and public comp bands. Glassdoor for regional medians. " +
      "Kununu for DE-specific. Remember: posted ranges are anchors, not ceilings.",
      "work", "career"),
    N("Running retro — stop/start/continue",
      "Simplest retro format that doesn't devolve into venting. Timebox each column to 5 min silent " +
      "writing, then dot-vote. Top 2 items become action items with an owner and a date.",
      "work", "process"),

    // ── Admin / finance ──
    N("German income tax — key deadlines",
      "Without a Steuerberater: 31 Juli for the previous year. With one: end of February two years on. " +
      "ELSTER online is the path. Keep receipts for home office and work-related training.",
      "admin", "taxes"),
    N("Insurance policy reference",
      "Policy number TK-2024-8843 — private health, Techniker Krankenkasse. Contact block: " +
      "serviceteam, +49 800 285 85 85. Next review: November. Don't rely on memory — they keep raising premiums.",
      "admin", "insurance"),
    N("Bank account switch procedure",
      "New account first. Standing orders + direct debits pulled from the old bank's 30-day statement. " +
      "Migrate salary last — that's what clocks the 30 days at the new bank for their welcome bonus.",
      "admin", "banking"),
    N("Invoice template — VAT notes",
      "VAT ID on every invoice (DE123456789). For reverse charge intra-EU B2B, line: " +
      "'Steuerschuldnerschaft des Leistungsempfängers / Reverse charge'. Keep PDFs for 10 years.",
      "admin", "freelance"),

    // ── Identifier-heavy (lexical-win bait) ──
    N("Linux ENOBUFS — socket buffer exhaustion",
      "errno 105. Kernel ran out of space in the per-socket send/recv queue. Usually a downstream " +
      "consumer not draining fast enough. `sysctl net.core.rmem_max` to raise the ceiling; fix the real cause too.",
      "tech", "linux", "errors"),
    N("ULID reference entry",
      "Example ULID: 01HZABCDEFGHJKMNPQRSTVWXYZ — 26 chars, Crockford base32, lexicographically sortable. " +
      "First 10 chars are the timestamp, last 16 are random. We use these instead of UUIDv4 for all IDs.",
      "tech", "conventions"),
    N("Release checklist — v2.4.0-rc3",
      "Cut tag `v2.4.0-rc3`. Smoke test on staging. Bump CHANGELOG. Announce in #releases. " +
      "If no blockers in 72h, retag as v2.4.0 and publish.",
      "work", "releases"),

    // ── Markdown spec coverage ──
    //
    // One note per CommonMark / GFM feature so the editor can be
    // eyeball-tested end-to-end. Tagged spec:commonmark or spec:gfm
    // (plus a category subtag) — filter the notes view to those and
    // you get the whole suite grouped.
    // References:
    //   https://spec.commonmark.org/0.31.2/
    //   https://github.github.com/gfm/
    N("spec: ATX headings h1–h6",
      """
      # H1 heading
      ## H2 heading
      ### H3 heading
      #### H4 heading
      ##### H5 heading
      ###### H6 heading

      Trailing hashes are optional:
      ## Heading with trailing hashes ##

      At least one space after the hash is required:
      #not a heading (should render as a paragraph)
      """,
      "spec:commonmark", "spec:headings"),

    N("spec: setext headings",
      """
      Setext H1 uses `=`
      ==================

      Setext H2 uses `-`
      ------------------

      Either underline accepts any length.
      """,
      "spec:commonmark", "spec:headings"),

    N("spec: paragraphs, hard breaks, escapes",
      """
      This is one paragraph. Wrapped lines without a blank separator
      are part of the same paragraph.

      Trailing-space hard breaks are not exercised here because editors
      routinely strip trailing whitespace. The GFM backslash form is
      tested instead:\
      Like so — the line above ends with a literal backslash.

      Escapes: \*not emphasis\*, \[not a link\], literal backslash: \\.

      HTML entities pass through: &amp;, &copy;, &hellip;.
      """,
      "spec:commonmark"),

    N("spec: emphasis and strong",
      """
      *single-star italic* and _single-underscore italic_.

      **double-star bold** and __double-underscore bold__.

      ***both at once*** and ___both at once___.

      Nested: **bold with _italic inside_ bold**.

      Mid-word emphasis should NOT fire:
      - snake_case_ident stays literal
      - x*y*z stays literal (letters around the star)

      Inside a `code span *asterisks* are literal`.
      """,
      "spec:commonmark", "spec:inline"),

    N("spec: strikethrough (GFM)",
      """
      ~~single-line strike~~

      Mixed: ~~strike with **bold** inside~~ and **bold with ~~strike~~ inside**.

      Should not fire mid-word: ~not~single~tilde~.
      """,
      "spec:gfm", "spec:inline"),

    N("spec: unordered and ordered lists",
      """
      Unordered with `-`:
      - alpha
      - bravo
      - charlie

      Unordered with `*`:
      * one
      * two

      Ordered:
      1. first
      2. second
      3. third

      Custom start (CommonMark respects the first number, then increments):
      7. seven
      8. eight

      Nested:
      - outer
        - inner
          - deepest
      - back to outer
      """,
      "spec:commonmark", "spec:lists"),

    N("spec: task lists (GFM)",
      """
      - [ ] open task
      - [x] closed task
      - [X] uppercase X also closed
      - [ ] parent
        - [x] sub-one
        - [ ] sub-two
      """,
      "spec:gfm", "spec:lists"),

    N("spec: blockquotes",
      """
      > A plain blockquote.
      > Second line in the same quote.

      > Nested:
      > > inner quote
      > > > deeper still

      > Quote with a heading:
      > ### inside a quote
      >
      > And a list inside:
      > - one
      > - two
      """,
      "spec:commonmark"),

    N("spec: fenced code blocks",
      """
      Plain fence:

      ```
      plain fence, no language hint
      ```

      With language hint:

      ```python
      def hello(name: str) -> str:
          return f"hi {name}"
      ```

      Tilde fence:

      ~~~js
      console.log("tilde fence");
      ~~~
      """,
      "spec:commonmark", "spec:code"),

    N("spec: indented code blocks",
      """
      A paragraph first.

          def legacy():
              return "four-space indent means code"

      Back to prose after a blank line.
      """,
      "spec:commonmark", "spec:code"),

    N("spec: inline code spans",
      """
      Single backtick: `code`.

      Double-backtick wraps embedded backticks: ``inline `code` with backticks``.

      Inside a span, `*asterisks* and [brackets](url)` stay literal.
      """,
      "spec:commonmark", "spec:code"),

    N("spec: links and autolinks",
      """
      Inline link: [CommonMark](https://spec.commonmark.org/).

      Titled link: [GFM spec](https://github.github.com/gfm/ "GitHub Flavored Markdown").

      Autolink (angle-wrapped): <https://example.org/>.

      Bare URL (GFM autolink): https://example.org/path.

      Email autolink: <hello@example.com>.

      Reference link: [Raft paper][raft] — repeated: [the paper][raft].

      [raft]: https://raft.github.io/raft.pdf "In Search of an Understandable Consensus Algorithm"
      """,
      "spec:commonmark", "spec:links"),

    N("spec: images",
      """
      Inline image: ![alt text](https://example.org/img.png).

      Titled image: ![logo](https://example.org/logo.svg "Site logo").

      Reference image: ![banner][banner].

      [banner]: https://example.org/banner.png
      """,
      "spec:commonmark", "spec:images"),

    N("spec: horizontal rules (three forms)",
      """
      Above first rule.

      ---

      Between dash and star.

      ***

      Between star and underscore.

      ___

      Below underscore rule.
      """,
      "spec:commonmark"),

    N("spec: tables (GFM)",
      """
      | left     | center   | right    |
      | :---     | :---:    | ---:     |
      | a        | b        | c        |
      | longer   | data     |       42 |
      | `code`   | **bold** | *italic* |

      Without outer pipes:

      col1 | col2
      -----|-----
      x    | y
      """,
      "spec:gfm", "spec:tables"),

    N("spec: raw HTML (escaped, not parsed)",
      """
      Fishbowl policy: inline HTML is always escaped, never parsed as live
      DOM. Reasons: content fidelity (DOMPurify-stripped tags would silently
      vanish from the source), and defense-in-depth (self-hosted memory
      should never execute arbitrary HTML from notes).

      All of the following should render as literal text, not markup:

      <em>this will show as "<em>this will show as …"</em>
      <strong>same for strong</strong>
      <script>alert(1)</script>
      <iframe src="https://evil.example/"></iframe>
      <img src=x onerror=alert(1) />

      <div class="custom">
        <p>block-level HTML is literal text too</p>
      </div>
      """,
      "spec:commonmark", "spec:html"),

    N("spec: kitchen sink",
      """
      # Kitchen sink

      Paragraph with **bold**, *italic*, ~~strike~~, `code`, and a [link](https://example.org/).

      ## A table with inline formatting

      | feature  | state    |
      | :---     | :---:    |
      | bold     | **yes**  |
      | italic   | *yes*    |
      | strike   | ~~yes~~  |

      ## A list with mixed content

      1. Ordered item with `inline code`.
      2. Ordered item with a [link](https://example.org/).
         - Nested unordered child.
         - [ ] nested task.
      3. Third item.

      ## A quote with a code block inside

      > The only way to discover the limits of the possible
      > is to go beyond them into the impossible.
      >
      > ```python
      > def beyond():
      >     return float("inf")
      > ```

      ## A secret block inside markdown

      Before the block.

      ::secret
      apikey: fb_live_REDACTED_FOR_SEED
      passphrase: correct-horse-battery-staple
      ::end

      After the block.

      ---

      See <https://spec.commonmark.org/0.31.2/> for the authoritative spec.
      """,
      "spec:commonmark", "spec:gfm", "spec:kitchen-sink"),

    // ── review:pending probes (for includePending filter) ──
    //
    // These simulate notes captured by an MCP client that haven't been
    // reviewed by the human yet — /api/v1/search?includePending=true
    // surfaces them, default (false) hides them.
    N("Captured from chat — need to verify",
      "User said the staging DB migration takes ~20 min under load. Want to confirm against the " +
      "last production run before quoting this number anywhere.",
      "captured", "review:pending"),
    N("Idea fragment — context switcher shortcut",
      "What if ⌘K opened the context switcher with a text filter? Today it's click-only. " +
      "Needs a spec before I build anything.",
      "ideas", "ui", "review:pending"),
};
var noteCount = 0;
foreach (var n in notes) { await noteRepo.CreateAsync(ctx, userId, n); noteCount++; }

// ────────── Todos ──────────
var now = DateTime.UtcNow;
var todos = new[]
{
    new TodoItem { Title = NamePrefix + "Ship the contacts UI" },
    new TodoItem { Title = NamePrefix + "Wire /api/v1/search into notes-view",
                   DueAt = now.AddDays(2) },
    new TodoItem { Title = NamePrefix + "Add settings download button",
                   DueAt = now.AddDays(7) },
    new TodoItem { Title = NamePrefix + "Closed — already done",
                   CompletedAt = now.AddDays(-1) },
};
var todoCount = 0;
foreach (var t in todos) { await todoRepo.CreateAsync(ctx, userId, t); todoCount++; }

// ────────── Contacts ──────────
var contacts = new[]
{
    new Contact
    {
        Name  = NamePrefix + "Alice Example",
        Email = "alice@studio.example",
        Phone = "+49-30-111-222",
        Notes = "Venue sound engineer. Met at Q1 show. Prefers morning calls.",
    },
    new Contact
    {
        Name  = NamePrefix + "Bob Partner",
        Email = "bob@partner.example",
        Phone = "+1-555-0100",
        Notes = "Backup label contact. Responds fast on email, slow on WhatsApp.",
    },
    new Contact
    {
        Name  = NamePrefix + "Carol Caterer",
        Phone = "+33-1-40-55",
        Notes = "Event catering — the one with the great paella.",
    },
    new Contact
    {
        Name     = NamePrefix + "Dave Old-friend (archived)",
        Email    = "dave@archive.example",
        Notes    = "Not in touch anymore. Kept for history.",
        Archived = true,
    },
};
var contactCount = 0;
foreach (var c in contacts) { await contactRepo.CreateAsync(ctx, userId, c); contactCount++; }

// ────────── Events ──────────
var weekStart = DateTime.UtcNow.Date.AddDays(-(int)DateTime.UtcNow.DayOfWeek);
var events = new[]
{
    new Event
    {
        Title    = NamePrefix + "Team sync",
        StartAt  = weekStart.AddDays(1).AddHours(10),
        EndAt    = weekStart.AddDays(1).AddHours(10).AddMinutes(30),
        RRule    = "FREQ=WEEKLY;BYDAY=MO",
        Location = "Meet link",
        ReminderMinutes = 10,
    },
    new Event
    {
        Title   = NamePrefix + "Venue walkthrough",
        StartAt = weekStart.AddDays(3).AddHours(16),
        EndAt   = weekStart.AddDays(3).AddHours(17),
        Location = "Alpine lodge",
    },
    new Event
    {
        Title       = NamePrefix + "Offsite",
        StartAt     = weekStart.AddDays(10).AddHours(9),
        EndAt       = weekStart.AddDays(11).AddHours(17),
        AllDay      = true,
        Description = "Two-day offsite — agenda in the shared doc.",
    },
};
var eventCount = 0;
foreach (var e in events) { await eventRepo.CreateAsync(ctx, userId, e); eventCount++; }

Console.Error.WriteLine(
    $"# seeded in {contextDisplay}: {noteCount} notes, {contactCount} contacts, " +
    $"{todoCount} todos, {eventCount} events");
Console.Error.WriteLine(
    "# notes were written WITHOUT embeddings — run `dotnet run --project tools/reindex-dev" +
    (contextArg == "space" ? $" -- --context space --context-id {contextIdArg}" : "") +
    "` to populate vec_notes so hybrid search leaves FTS-only mode.");
Console.WriteLine(
    $"{{\"notes\":{noteCount},\"contacts\":{contactCount}," +
    $"\"todos\":{todoCount},\"events\":{eventCount},\"reseeded\":true}}");
return 0;

string? GetArg(string flag)
{
    var i = Array.IndexOf(args_, flag);
    if (i < 0 || i + 1 >= args_.Length) return null;
    return args_[i + 1];
}
