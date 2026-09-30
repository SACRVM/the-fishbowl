using System.Security.Claims;
using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Apps;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Tables;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Fishbowl.Api.Endpoints;

// A space's own tables over REST (space-apps spec, phase 3) — the same
// surface the MCP table_* tools and, later, space apps use. Everyone in the
// space reads; Members write rows (only their own in an own-rows table);
// Designers and up change the schema. Bearer keys need read:/write:/
// design:tables on top of their owner's role. Refusals are TableExceptions
// (TableErrorsMiddleware turns them into { error, message, … }).
//
//   GET    /api/v1/spaces/<slug>/tables                   list
//   POST   /api/v1/spaces/<slug>/tables                   create { name, description?, ownRows?, columns: [...] }
//   GET    /api/v1/spaces/<slug>/tables/{table}           describe (?format=markdown)
//   PATCH  /api/v1/spaces/<slug>/tables/{table}           alter { description?, ownRows?, addColumns?, dropColumns?, renameColumns?, updateColumns? }
//   DELETE /api/v1/spaces/<slug>/tables/{table}           to the trash
//   GET    …/{table}/rows?limit=&offset=                  rows, newest first
//   POST   …/{table}/rows                                 insert
//   GET|PATCH|DELETE …/{table}/rows/{id}                  (PATCH: partial, ?rowVersion=)
//   POST   …/{table}/query     { where?, orderBy?, limit?, offset? }
//   POST   …/{table}/count     { where? }
//   POST   …/{table}/aggregate { fn, column?, groupBy?, where? }
public static class TablesApi
{
    public static IEndpointRouteBuilder MapTablesApi(this IEndpointRouteBuilder routes)
    {
        var g = routes.MapGroup("/api/v1/spaces/{slug}/tables");

        g.MapGet("/", async (string slug, ClaimsPrincipal user, ISpaceRepository spaces, ITableRepository tables, CancellationToken ct) =>
        {
            var (ctx, _, err) = await ResolveAsync(slug, user, spaces, ct);
            if (err is not null) return err;
            var list = new List<object>();
            foreach (var t in await tables.ListAsync(ctx, ct))
                list.Add(TableJson.Describe(t, await tables.CountRowsAsync(ctx, t.Name, ct)));
            return Results.Ok(list);
        }).RequireScope(ScopeCatalog.ReadTables).WithSummary("The space's tables with their columns and row counts.");

        g.MapGet("/search", async (string slug, string? q, string? table, int? limit, ClaimsPrincipal user, ISpaceRepository spaces, ITableRepository tables, CancellationToken ct) =>
        {
            var (ctx, _, err) = await ResolveAsync(slug, user, spaces, ct);
            if (err is not null) return err;
            var (hits, degraded) = await tables.SearchAsync(ctx, q ?? "", table, limit ?? 20, ct);
            return Results.Ok(new { hits = hits.Select(h => new { table = h.Table, row = h.Row, score = h.Score }), degraded });
        }).RequireScope(ScopeCatalog.ReadTables).WithSummary("Hybrid search over rows of tables with searchable columns (?q=, ?table=, ?limit=).");

        g.MapPost("/", async (string slug, JsonElement body, ClaimsPrincipal user, ISpaceRepository spaces, ITableRepository tables, CancellationToken ct) =>
        {
            var (ctx, actor, err) = await ResolveAsync(slug, user, spaces, ct);
            if (err is not null) return err;
            var def = new TableDef(
                Str(body, "name") ?? "", Str(body, "description"), Bool(body, "ownRows"),
                body.TryGetProperty("columns", out var cols) ? TableJson.ParseColumns(cols) : new List<ColumnDef>(),
                DateTime.UtcNow, null);
            var created = await tables.CreateAsync(ctx, actor, def, ct);
            return Results.Created($"/api/v1/spaces/{slug}/tables/{created.Name}", TableJson.Describe(created, 0));
        }).RequireScope(ScopeCatalog.DesignTables).WithSummary("Create a table (Designer and up).");

        g.MapGet("/{table}", async (string slug, string table, string? format, ClaimsPrincipal user, ISpaceRepository spaces, ITableRepository tables, CancellationToken ct) =>
        {
            var (ctx, _, err) = await ResolveAsync(slug, user, spaces, ct);
            if (err is not null) return err;
            var def = await tables.DescribeAsync(ctx, table, ct);
            if (def is null) return ApiErrors.NotFound("table_missing", $"There is no table '{table}' in this space.", new { table });
            var rows = await tables.CountRowsAsync(ctx, table, ct);
            return format == "markdown" ? Results.Text(TableJson.Markdown(def, rows), "text/markdown") : Results.Ok(TableJson.Describe(def, rows));
        }).RequireScope(ScopeCatalog.ReadTables).WithSummary("One table's definition (?format=markdown for a short readable block).");

        g.MapPatch("/{table}", async (string slug, string table, JsonElement body, ClaimsPrincipal user, ISpaceRepository spaces, ITableRepository tables, CancellationToken ct) =>
        {
            var (ctx, actor, err) = await ResolveAsync(slug, user, spaces, ct);
            if (err is not null) return err;
            var altered = await tables.AlterAsync(ctx, actor, table, TableJson.ParseChange(body), ct);
            return Results.Ok(TableJson.Describe(altered, await tables.CountRowsAsync(ctx, table, ct)));
        }).RequireScope(ScopeCatalog.DesignTables).WithSummary("Change a table: description, own rows, add/drop/rename/update columns (Designer and up). Dropping a column is final.");

        g.MapDelete("/{table}", async (string slug, string table, ClaimsPrincipal user, ISpaceRepository spaces, ITableRepository tables, CancellationToken ct) =>
        {
            var (ctx, actor, err) = await ResolveAsync(slug, user, spaces, ct);
            if (err is not null) return err;
            await tables.DropAsync(ctx, actor, table, ct);
            return Results.NoContent();
        }).RequireScope(ScopeCatalog.DesignTables).WithSummary("Move a table with its rows to the trash (Designer and up).");

        g.MapGet("/{table}/rows", async (string slug, string table, int? limit, int? offset, ClaimsPrincipal user, ISpaceRepository spaces, ITableRepository tables, CancellationToken ct) =>
        {
            var (ctx, _, err) = await ResolveAsync(slug, user, spaces, ct);
            if (err is not null) return err;
            var spec = new QuerySpec(OrderBy: new[] { new OrderByClause("created_at", "desc") }, Limit: limit, Offset: offset);
            return Results.Ok(await tables.QueryAsync(ctx, table, spec, ct));
        }).RequireScope(ScopeCatalog.ReadTables).WithSummary("A table's rows, newest first.");

        g.MapPost("/{table}/rows", async (string slug, string table, JsonElement body, ClaimsPrincipal user, ISpaceRepository spaces, ITableRepository tables, CancellationToken ct) =>
        {
            var (ctx, actor, err) = await ResolveAsync(slug, user, spaces, ct);
            if (err is not null) return err;
            var row = await tables.InsertAsync(ctx, actor, table, body, ct);
            return Results.Created($"/api/v1/spaces/{slug}/tables/{table}/rows/{row.GetProperty("id").GetString()}", row);
        }).RequireScope(ScopeCatalog.WriteTables).WithSummary("Add a row (Member and up).");

        g.MapGet("/{table}/rows/{id}", async (string slug, string table, string id, ClaimsPrincipal user, ISpaceRepository spaces, ITableRepository tables, CancellationToken ct) =>
        {
            var (ctx, _, err) = await ResolveAsync(slug, user, spaces, ct);
            if (err is not null) return err;
            var row = await tables.GetAsync(ctx, table, id, ct);
            return row is null ? ApiErrors.NotFound("row_missing", $"Table '{table}' has no row '{id}'.", new { table, id }) : Results.Ok(row);
        }).RequireScope(ScopeCatalog.ReadTables).WithSummary("One row.");

        g.MapPatch("/{table}/rows/{id}", async (string slug, string table, string id, long? rowVersion, JsonElement body, ClaimsPrincipal user, ISpaceRepository spaces, ITableRepository tables, CancellationToken ct) =>
        {
            var (ctx, actor, err) = await ResolveAsync(slug, user, spaces, ct);
            if (err is not null) return err;
            return Results.Ok(await tables.UpdateAsync(ctx, actor, table, id, body, rowVersion, ct));
        }).RequireScope(ScopeCatalog.WriteTables).WithSummary("Change some of a row's values (?rowVersion= guards against lost updates).");

        g.MapDelete("/{table}/rows/{id}", async (string slug, string table, string id, ClaimsPrincipal user, ISpaceRepository spaces, ITableRepository tables, CancellationToken ct) =>
        {
            var (ctx, actor, err) = await ResolveAsync(slug, user, spaces, ct);
            if (err is not null) return err;
            await tables.DeleteAsync(ctx, actor, table, id, ct);
            return Results.NoContent();
        }).RequireScope(ScopeCatalog.WriteTables).WithSummary("Move a row to the trash — refused while something links to it.");

        g.MapPost("/{table}/query", async (string slug, string table, JsonElement body, ClaimsPrincipal user, ISpaceRepository spaces, ITableRepository tables, CancellationToken ct) =>
        {
            var (ctx, _, err) = await ResolveAsync(slug, user, spaces, ct);
            if (err is not null) return err;
            return Results.Ok(await tables.QueryAsync(ctx, table, ParseSpec(body), ct));
        }).RequireScope(ScopeCatalog.ReadTables).WithSummary("Rows matching a filter ({ where, orderBy, limit, offset }; the MongoDB-style DSL).");

        g.MapPost("/{table}/count", async (string slug, string table, JsonElement body, ClaimsPrincipal user, ISpaceRepository spaces, ITableRepository tables, CancellationToken ct) =>
        {
            var (ctx, _, err) = await ResolveAsync(slug, user, spaces, ct);
            if (err is not null) return err;
            return Results.Ok(new { count = await tables.CountAsync(ctx, table, ParseSpec(body), ct) });
        }).RequireScope(ScopeCatalog.ReadTables).WithSummary("How many rows match a filter.");

        g.MapPost("/{table}/aggregate", async (string slug, string table, JsonElement body, ClaimsPrincipal user, ISpaceRepository spaces, ITableRepository tables, CancellationToken ct) =>
        {
            var (ctx, _, err) = await ResolveAsync(slug, user, spaces, ct);
            if (err is not null) return err;
            JsonElement? where = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("where", out var w) ? w : null;
            var result = await tables.AggregateAsync(ctx, table, Str(body, "fn") ?? "count", Str(body, "column"), Str(body, "groupBy"), where, ct);
            return Results.Ok(result.Select(r => new { group = r.Group, value = r.Value }));
        }).RequireScope(ScopeCatalog.ReadTables).WithSummary("count / sum / min / max / avg, optionally grouped by one column.");

        return routes;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    internal static QuerySpec ParseSpec(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object ? AppJsonParsers.ParseQuerySpec(body) : new QuerySpec();

    // The space by id and the caller's role in it, as a TableActor.
    internal static TableActor ActorFor(string userId, SpaceRole role) =>
        new(userId, role.CanWrite(), role.CanDesign());

    private static async Task<(ContextRef Ctx, TableActor Actor, IResult? Error)> ResolveAsync(
        string slug, ClaimsPrincipal user, ISpaceRepository spaces, CancellationToken ct)
    {
        var r = await SpacesApi.ResolveSpaceAsync(slug, user, spaces, ct);
        if (r.Error is not null) return (default, null!, r.Error);
        var userId = user.FindFirst(McpContextClaims.UserId)!.Value;
        return (ContextRef.Space(r.Space!.Id), ActorFor(userId, r.Role!.Value), null);
    }
}
