# Slimming plan

The original `CONCEPT.md` described two products at once: a **data platform**
(auth, isolation, schema, search, agents) and an **interface platform** (form
builder, auto-generated UI, custom renderers, template sharing). Each is a year
of work. That is why parts of it lay fallow — two projects existed as names with
zero lines in them, and the Apps platform shipped its database half while its UI
half sat under "out of MVP" for a year.

The decision is to build **one** of those platforms. Fishbowl is the spine —
identity, isolated storage, agent access, notifications, search. The interface
vocabulary comes from the SACRVM APPKIT instead of being invented here.

**The test for every item below: does it remove something, or add something?**
Removal is the plan. Addition is the trap that produced this document.

> This file is the authority on what has been *dropped* versus merely *deferred*.
> `CONCEPT.md` still describes the larger vision; where the two disagree, this
> file wins. Do not reintroduce a subsystem because CONCEPT.md still names it.

---

## Baseline

Measured 2026-08-24, before any removals:

| | Lines |
|---|---|
| Production C# | 15,784 |
| Test C# | 14,664 |
| Frontend (components + lib + css + vendor) | 8,473 |

Tests are ~48% of the C# codebase; `Fishbowl.Host` alone is 1,800 production to
7,559 test lines (4.2×). **Cutting a feature cuts its tests disproportionately** —
expect removals to land larger than the feature list suggests.

The two largest files are `DatabaseFactory.cs` (1,136) and `Program.cs` (907),
and both are large mostly because of items in tier C below.

---

## Tier A — done

Removed 2026-08-24. 20 files changed, 11 insertions, 551 deletions, 14 files
deleted. Build clean, 641 tests pass.

- **`Fishbowl.Sync` and `Fishbowl.Scripting`** — two projects in the solution
  with zero `.cs` files. Removed from `Fishbowl.sln` and from
  `Fishbowl.Host.csproj`'s project references.
- **Plugin sideloading** — `PluginLoadContext`, `PluginLoader`, `FishbowlApi`,
  the `LoadPlugins` call in `Program.cs`, and the `Plugins:Path` setting.
  86 lines of production code guarded by 285 lines of tests, with **zero real
  consumers**: every implementation outside the contracts was a test fake.
- **Orphaned contracts** — `IFishbowlPlugin`, `IFishbowlApi`, `IScheduledJob`,
  `ISyncProvider`, `SyncResult`, `SyncSource`, `SyncTarget`.

**Kept deliberately:** `IBotClient` and `IncomingMessage`. That is a real
internal seam — both scheduler dispatchers fan out over
`GetServices<IBotClient>()`, and `DiscordBotClient` implements it in-tree.
Extension by adding a class and a DI registration, not by dropping a DLL.

---

## Tier B — replaced by the appkit

The kit supplies the component vocabulary, theming and app packaging that
Fishbowl would otherwise have to invent and maintain.

| Replaced | Lines |
|---|---|
| `js/components/` (20 components) | 5,547 |
| `js/lib/` (router, icons, context, dialog, globals) | 932 |
| `css/app.css` | 585 |
| `js/vendor/` (marked, purify — the kit ships these) | 1,409 |
| **Total** | **8,473** |

`js/views/` (3,897 lines) stays but gets rebuilt. `fb-md-editor.js` (2,092 of
the component total) is **not** in the core kit — see open question 6.

Mapping is mostly mechanical: 17 of the 20 `fb-*` components have a 1:1 `sac-*`
counterpart. `sac.scope` is a generalization of this project's `js/lib/context.js`,
with a configurable prefix so `#/space/<slug>/…` keeps working.

**The expensive part is not the rename.** Kit v2.0.0 moved chrome ownership into
the app: the `sac.toolbar` projection model is gone, and a view draws its own
`<sac-nav>`. Fishbowl's `fb.toolbar.set([...])` is exactly that removed model.
Views must move their chrome inward.

**Next step:** rebuild `fb-hub-view.js` (63 lines) against the vendored kit and
nothing else. That measures the chrome cost honestly before committing to 8,473
lines. If it feels good, the rest is legwork; if it drags, 63 lines were spent
instead of a quarter.

---

## Tier C — built for a deployment that does not exist

| Item | Weight | Reality |
|---|---|---|
| ACME / LettuceEncrypt | 56 references across `Program.cs`, `BootConfig.cs`, `ConfigAdminEndpoints.cs`, plus the whole port-binding branch | Production runs behind a reverse proxy that terminates TLS; ACME is off |
| Legacy migrations | 853 lines of tests (`SchemaV2`–`V5`, `LegacyLayout`, `TeamsFolder`) plus the migration paths inside `DatabaseFactory` | Protect upgrade paths from schema states that existed on essentially one machine |

Removing these makes the two worst files in the codebase readable again. ACME
removal also collapses the `restartRequired` dance in `/api/setup` and the
three-way port-binding logic in `Program.cs`.

---

## Tier D — real function, no face

These are **product decisions, not cleanup**. Listed for completeness; do not
treat them as scheduled work.

| Item | Lines | Finding |
|---|---|---|
| Contacts | 1,364 | Backend, REST, MCP tools and tests are complete. There is **no view in the SPA** — `js/views/` has calendar, hub, keys, notes, spaces, todos. Reachable only by an agent. |
| Spaces | 1,566 | Has a real use (per-agent namespace via `tools/init-space`), but `SpacesApi.cs` at 700 lines is largely a mirror of the personal routes. |

---

## Architecture decisions taken

Recorded so they stop being re-litigated.

1. **Fishbowl is a host, not a guest.** It will not become an app inside another
   desktop. It owns a server, real auth and real storage — exactly what the
   appkit's Tier-4 vision says a shell owns. As a guest it would force a second
   login and give away its only differentiator. A small companion app pointing
   *at* Fishbowl from another desktop remains fine.
2. **Apps are real code, not declarative manifests.** The form-builder /
   "Access clone" direction is dropped. Building against the kit is pleasant
   enough that a code path beats a configuration path.
3. **Apps are AI-generated against a fixed vocabulary.** The frame and the data
   layer are prescribed; the agent composes `sac-*` components. This is the same
   99/1 split `CONCEPT.md` already described — only the author of the 99% changed
   from a human at a click-builder to an agent.
4. **No iframes as the default.** Same-realm web components stay the model.
5. **No install-by-URL for foreign apps, for now.** Apps in Fishbowl are
   first-party or self-authored. With plugin sideloading gone (tier A), there is
   now **no path on which unvetted third-party code runs** — one trust model
   instead of two contradictory ones.
6. **Moddability as a product promise is dropped.** "Disk file wins" for every
   UI file contradicts the kit's "vendor `kit/` verbatim, never edit". Extension
   happens by writing an app, not by overriding a file. See open question 5 —
   the dev overlay is a separate matter.
7. **Extension is by addition, not substitution.** No plugin loader, no file
   overrides. Add a class, register it, or write an app.

---

## Open questions

1. **Does the fixed core stay privileged?** `CONCEPT.md` guarantees a fixed
   structure (notes, calendar, contacts, tasks) that "does not change", with Apps
   for everything else. If the app platform moves to the centre, that split gets
   re-litigated — the radical simplification is *notes are also just an app*, one
   mechanism instead of two. This is the biggest open decision and it gates
   tier D.
2. **Contacts: build a face, or drop it?** 1,364 lines that no human can reach.
   Depends entirely on question 1.
3. **Spaces: how much of the membership/role machinery is load-bearing?** The
   namespace use is real; the multi-user role model may not be.
4. **Storage capability shape.** `context.fs` is a four-method key→JSON store
   (`get/set/del/keys`) with no query. Fishbowl's Apps platform is a relational
   store with a validated query DSL. Implementing `sac.fs.backend` over it is
   easy but reduces a database to a bucket. The alternative is proposing a richer
   typed capability upstream — the point where Fishbowl contributes rather than
   consumes.
5. **How does the dev loop survive the moddability removal?** `fishbowl-mods/`
   currently holds junctions into `Fishbowl.Data/Resources` (documented in
   `Fishbowl.Host.csproj`'s `DefaultItemExcludesInProjectFolder` comment), so
   editing a file and pressing F5 beats a rebuild per line of CSS. The answer is
   probably a **dev-only** path rather than keeping the product promise — but it
   must exist before the disk tier is removed.
6. **`fb-md-editor` (2,092 lines): keep as an add-on, or drop?** Not in the core
   kit; the kit's own migration guide lists it as a planned optional module.
7. **Should the SPA get a strict CSP (`connect-src 'self'`)?** With apps as real
   code in the same realm, and with agents generating that code while reading
   content the user did not write, prompt injection becomes a code path. A CSP
   does not stop injected code from reading, but it removes the exfiltration
   channel. Cheap, no DX cost.
8. **Which schema versions must still upgrade cleanly?** Determines how much of
   tier C's migration weight can go.

---

## Asks for the appkit

Contributions back, in priority order.

1. **`context.host` as data, not DOM references.** It currently carries `nav`
   and `toolbar` elements the app assigns to its own `<sac-nav>`. DOM cannot
   cross a `postMessage` boundary, so this is the single thing preventing a
   future `sandboxed: true` mode from ever working. Everything else in the
   contract already survives isolation, because `context.fs` is async and
   JSON-only by design. **This is the time-critical one** — every app written
   against the DOM shape makes the change more expensive.
2. **A machine-readable component manifest.** Component APIs live in header
   comments today: excellent for a human, workable for an agent reading 37
   files, but a generated-app pipeline wants one file listing tag, attributes,
   events and slots. Cheap, changes no behaviour, and directly serves the
   generation use case the kit was aligned for.
3. **A capability handshake.** `context.fs` is either present or `null`. If the
   same app should run everywhere but gain a spine on a host with a real
   backend, the app needs to declare what it wants and the host what it can
   offer, with graceful degradation. Without it, "premium host" just means
   "incompatible".
