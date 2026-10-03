using System.Text;
using Fishbowl.Core.Files;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Tables;

namespace Fishbowl.Core.Desktop;

// The guide the server writes from the live space (space-apps spec, decision
// 23): what an agent needs to build or fix this space's own apps — the
// caller's role, how an app is laid out and runs, the context.space API,
// this space's tables, its apps and their latest errors. Markdown, short
// enough for a small agent; REST GET …/guide and the MCP tool space_guide.
public static class SpaceGuide
{
    public static async Task<string> BuildAsync(
        Space space, SpaceRole role, ContextRef ctx,
        ITableRepository tables, IFileService files, IAppErrorRepository errors, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var designer = role.CanDesign();
        sb.AppendLine($"# Building apps for the space \"{space.Name}\" (`{space.Slug}`)");
        sb.AppendLine();
        sb.AppendLine($"Your role here: **{role.ToDbValue()}**. " + (designer
            ? "You may write app code (`.apps/`) and change tables."
            : role.CanWrite()
                ? "You may write rows, not app code or tables — writing `.apps/` needs the Designer role."
                : "You may read; apps you'd build need the Designer role."));
        sb.AppendLine();

        sb.AppendLine("## How a space app works");
        sb.AppendLine("""
            - An app is a folder in the space's files: `.apps/<folder>/` (folder: lower-case letters, digits, `-`).
            - `app.json` in it: `{ "name": "Booking", "tag": "booking-app", "version": "1.0.0", "icon": "calendar", "description": "…", "entry": "app.js" }` — `tag` (a custom element name with a dash) is required, `entry` defaults to `app.js` (a `.js`/`.mjs` file in the folder).
            - The entry defines that custom element. Fishbowl calls `mount(context)` once it is on the page:
              ```js
              customElements.define("booking-app", class extends HTMLElement {
                async mount(context) {
                  const rows = await context.space.query("rooms", { where: { seats: { $gte: 10 } }, limit: 50 });
                  this.innerHTML = `<div class="card">${rows.length} rooms</div>`;
                }
              });
              ```
            - Every member of the space gets a tile; it opens in a window, in a sandboxed frame: no cookies, no network (`fetch` to other servers is blocked), no access to the page around it. Data only through `context.space`, as the person using the app — their role decides what works.
            - The frame has SACRVM APPKIT loaded: its CSS (`.card`, `.btn`, `.btn.primary`, `.toolbar`, `.empty-state`, `.muted`, the theme tokens like `var(--accent)`) and its components — `sac-section`, `sac-select`, `sac-number-field`, `sac-date-field`, `sac-time-field`, `sac-toggle`, `sac-chip`, `sac-chip-input`, `sac-menu`, `sac-tabs`, `sac-dialog`, `sac-calendar`, `sac-progress`, `sac-spinner`, `sac-icon`, `sac-tooltip`, `sac-copy-button`, `sac-status-banner`. Use those; don't load other libraries.
            - Scripts, styles, JSON, images and fonts in the folder are served next to the entry (relative URLs work); never HTML. The code isn't pinned: a change runs the next time someone opens the app.
            """);

        sb.AppendLine("## Writing the code");
        sb.AppendLine($"""
            Over the Files API with a key for this space that has `write:files` and `design:apps` (and your role must be Designer):
            ```
            PUT /api/v1/spaces/{space.Slug}/files/content?path=.apps/<folder>/app.js&parents=1
            X-Fishbowl-Upload: 1
            If-None-Match: *          (new file)   — or —   If-Match: "<etag>"   (replace; the etag from GET …/files/stat?path=…)
            <the file's bytes as the body>
            ```
            Then open the space's desktop, or ask `app_errors` (MCP) / `GET /api/v1/spaces/{space.Slug}/apps/errors` what went wrong.

            """);

        sb.AppendLine("## context.space");
        sb.AppendLine("""
            Every method returns a promise. A refusal rejects with `{ code, message }` (e.g. `not-your-row`, `version-conflict`, `forbidden`) and is also written to the error store.

            | Method | What |
            |---|---|
            | `info()` | `{ name, slug, role, … }` — the space and the user's role |
            | `members()` | `{ role, items: [{ userId, name, avatarUrl, role, you }] }` — `role` is the user's own |
            | `tables()` | the space's tables |
            | `describe(table)` | one table: columns, types, rules |
            | `query(table, { where, orderBy, limit, offset })` | rows; `where` is a MongoDB-style filter: `{ col: value }` or `{ col: { $eq $ne $lt $lte $gt $gte $in $like $isNull $isNotNull } }`, combined with `$and $or $not`; `orderBy: [{ field, direction: "asc" \| "desc" }]`; `limit` ≤ 500 |
            | `count(table, where)` | `{ count }` |
            | `aggregate(table, { fn, column, where, groupBy })` | count/sum/min/max/avg, one `groupBy` |
            | `search(q, table?)` | full-text + semantic search over searchable columns |
            | `get(table, id)` | one row |
            | `insert(table, values)` | a new row (`title` is required); returns it |
            | `update(table, id, values, rowVersion?)` | partial; with `rowVersion` it refuses a stale write |
            | `remove(table, id)` | to the space's trash |
            | `notes()`, `note(id)`, `todos(includeCompleted?)`, `events(from?, to?)`, `contacts()` | the space's built-ins, read-only |
            | `notify(to, text)` | a message from your app to members of this space — `to`: member ids (`userId` from `members().items`) or `"all"` (everyone but the sender); text ≤ 500 characters; 30 an hour per app; members can mute an app |
            | `error(message, detail?)` | your own entry in the error store, for the space's Designers |

            Every row has `id`, `title`, `author`, `created_at`, `last_modified`, `row_version` besides its columns.

            """);

        sb.AppendLine("## Triggers");
        sb.AppendLine("""
            JavaScript that runs on the server around every row write of a table — whoever writes (an app, an agent, the Tables app, the API). One file per table and app: `.apps/<folder>/triggers/<table>.js`, with any of these functions:
            ```js
            function beforeInsert(row, ctx) { row.number = 'N-' + (ctx.count('orders') + 1); return row; }  // change the row, or throw to refuse
            function beforeUpdate(row, old, ctx) { /* row = the changed columns */ }
            function beforeDelete(old, ctx) { ctx.update('orders', old.id, { archived: true }); return false; }  // false keeps the row
            function afterInsert(row, old, ctx) { ctx.insert('audit', { title: 'new ' + row.number }); }        // after the write
            function afterUpdate(row, old, ctx) {}
            function afterDelete(row, old, ctx) {}
            ```
            - A `throw new Error('…')` in a before-function refuses the write; the writer sees your message.
            - After-functions run once the write is saved; their errors go to the error store (kind `trigger`), the write stands.
            - `ctx`: `get(table, id)`, `query(table, { where, orderBy, limit })`, `count(table, where?)`, `insert(table, values)`, `update(table, id, values)`, `remove(table, id)` — with the writer's role, and through triggers again (at most 3 deep); `ctx.user` `{ id, canWrite, canDesign }`, `ctx.table`, `ctx.log(message)` into the error store.
            - Synchronous, plain JavaScript: no `fetch`, no files, no modules. Each run gets about 1 s, 50 000 statements and 32 MB.

            """);

        sb.AppendLine("## This space's tables");
        var defs = await tables.ListAsync(ctx, ct);
        if (defs.Count == 0)
            sb.AppendLine(designer
                ? "None yet. Create them over MCP (`table_create`) or REST (`POST /api/v1/spaces/" + space.Slug + "/tables`) — describe every column; people read it."
                : "None yet.");
        foreach (var d in defs)
        {
            // One level below this section.
            var table = TableJson.Markdown(d, await tables.CountRowsAsync(ctx, d.Name, ct)).TrimEnd();
            sb.AppendLine(table.StartsWith("## ", StringComparison.Ordinal) ? "#" + table : table);
            sb.AppendLine();
        }

        sb.AppendLine("## This space's apps");
        var apps = await SpaceAppCatalog.ListAsync(files, ctx, ct);
        if (apps.Count == 0) sb.AppendLine("None yet.");
        foreach (var (m, changed) in apps)
            sb.AppendLine($"- `.apps/{m.Folder}/` — **{m.Name}**{(m.Version is null ? "" : $" v{m.Version}")}, `<{m.Tag}>`, entry `{m.Entry}`{(m.Description is null ? "" : $" — {m.Description}")}");

        if (designer)
        {
            var recent = await errors.ListAsync(ctx, null, 10, ct);
            if (recent.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Latest errors");
                foreach (var e in recent)
                    sb.AppendLine($"- {e.At:yyyy-MM-dd HH:mm} UTC · `.apps/{e.App}` · {e.Kind}: {e.Message}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Rules");
        sb.AppendLine("""
            - No secrets in app code or rows: everyone in the space reads `.apps/` and the tables.
            - Build only from `context.space` and the kit; no other servers, no raw SQL.
            - Keep an app small and say what it does in `description`; describe tables and columns.
            - Members see an app's tile as soon as its `app.json` reads — test with a copy in a new folder first.
            """);
        return sb.ToString();
    }
}
