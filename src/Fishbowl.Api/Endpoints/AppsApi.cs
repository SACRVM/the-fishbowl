using System.Security.Claims;
using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Apps;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Fishbowl.Api.Endpoints;

// REST mirror of the apps MCP tools. Routes split into three zones:
//   1) /api/v1/apps — cookie-only lifecycle (create/list/get/rename/delete)
//   2) /api/v1/apps/{ownerType}/{ownerId}/{slug}/keys — cookie-only key mgmt
//   3) /api/v1/apps/{ownerType}/{ownerId}/{slug}/{...} — bearer-or-cookie
//      data plane gated by `app:*` scopes
//
// `{ownerType}` ∈ {user, space}. `{ownerId}` is the user-id for user owners
// and the space-slug for space owners — operator-friendly URLs. The resolver
// translates slug → space-ULID before any repository call, matching the
// pattern in SpacesApi.cs / ResolveSpaceAsync.
public static class AppsApi
{
    private const string ApiKeyScheme = "ApiKey";

    public record CreateAppRequest(string Slug, string Name, OwnerRef Owner);
    public record OwnerRef(string Type, string Id);
    public record RenameAppRequest(string Name);
    public record DeleteAppRequest(string Confirm);
    public record CreateAppKeyRequest(string Name, List<string> Scopes);
    public record CreateAppKeyResponse(
        string Id, string Name, string KeyPrefix, List<string> Scopes,
        DateTime CreatedAt, string RawToken);
    public record AppKeyResponse(
        string Id, string Name, string KeyPrefix, List<string> Scopes,
        DateTime CreatedAt, DateTime? LastUsedAt);
    public record AppSummary(string Id, string Slug, string Name, string OwnerType, string OwnerId, DateTime CreatedAt);
    public record AlterTableRequest(string Operation,
        JsonElement? Column = null,
        string? From = null,
        string? To = null,
        string? Name = null);

    public static IEndpointRouteBuilder MapAppsApi(this IEndpointRouteBuilder routes)
    {
        MapLifecycle(routes);
        MapKeys(routes);
        MapData(routes);
        return routes;
    }

    private static void MapLifecycle(IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/apps").RequireAuthorization();

        group.MapPost("/", async (
            CreateAppRequest body,
            ClaimsPrincipal user,
            IAppRepository apps,
            ISpaceRepository spaces,
            CancellationToken ct) =>
        {
            if (RejectIfBearer(user) is { } reject) return reject;
            var userId = user.FindFirst(McpContextClaims.UserId)?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();
            if (body is null || string.IsNullOrWhiteSpace(body.Slug)
                || string.IsNullOrWhiteSpace(body.Name) || body.Owner is null)
                return Results.BadRequest(new { error = "slug, name, owner.{type,id} are required" });

            var (owner, error) = await ResolveOwnerForCreateAsync(body.Owner, userId, spaces, ct);
            if (error is not null) return error;

            try
            {
                var app = await apps.CreateAsync(owner!.Value, body.Slug.Trim(), body.Name.Trim(), userId, ct);
                return Results.Ok(new AppSummary(app.Id, app.Slug, app.Name,
                    body.Owner.Type, body.Owner.Id, app.CreatedAt));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        }).WithName("CreateApp");

        group.MapGet("/", async (
            ClaimsPrincipal user,
            IAppRepository apps,
            ISpaceRepository spaces,
            CancellationToken ct) =>
        {
            if (RejectIfBearer(user) is { } reject) return reject;
            var userId = user.FindFirst(McpContextClaims.UserId)?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            // Personal apps + apps from every space the user belongs to.
            var personal = await apps.ListByOwnerAsync(ContextRef.User(userId), ct);
            var rows = new List<AppSummary>(personal.Count);
            foreach (var a in personal)
                rows.Add(new AppSummary(a.Id, a.Slug, a.Name, "user", userId, a.CreatedAt));

            var memberships = await spaces.ListByMemberAsync(userId, ct);
            foreach (var m in memberships)
            {
                var spaceApps = await apps.ListByOwnerAsync(ContextRef.Space(m.Space.Id), ct);
                foreach (var a in spaceApps)
                    rows.Add(new AppSummary(a.Id, a.Slug, a.Name, "space", m.Space.Slug, a.CreatedAt));
            }
            return Results.Ok(rows);
        }).WithName("ListApps");

        group.MapGet("/{ownerType}/{ownerId}/{slug}", async (
            string ownerType, string ownerId, string slug,
            ClaimsPrincipal user, IAppRepository apps,
            ISpaceRepository spaces, CancellationToken ct) =>
        {
            var resolved = await ResolveAppAsync(ownerType, ownerId, slug, user, apps, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            var (app, owner) = (resolved.App!, resolved.Owner!.Value);
            return Results.Ok(new AppSummary(app.Id, app.Slug, app.Name,
                ownerType, ownerId, app.CreatedAt));
        }).WithName("GetApp");

        group.MapPatch("/{ownerType}/{ownerId}/{slug}", async (
            string ownerType, string ownerId, string slug,
            RenameAppRequest body,
            ClaimsPrincipal user, IAppRepository apps,
            ISpaceRepository spaces, CancellationToken ct) =>
        {
            if (RejectIfBearer(user) is { } reject) return reject;
            if (body is null || string.IsNullOrWhiteSpace(body.Name))
                return Results.BadRequest(new { error = "name is required" });
            var resolved = await ResolveAppAsync(ownerType, ownerId, slug, user, apps, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            if (resolved.SpaceRole is { } role && !role.CanWrite())
                return Results.Forbid();
            var ok = await apps.RenameAsync(resolved.Owner!.Value, resolved.App!.Id, body.Name.Trim(), ct);
            return ok ? Results.NoContent() : Results.NotFound();
        }).WithName("RenameApp");

        group.MapDelete("/{ownerType}/{ownerId}/{slug}", async (
            string ownerType, string ownerId, string slug,
            string? confirm,
            ClaimsPrincipal user, IAppRepository apps,
            ISpaceRepository spaces, DatabaseFactory dbFactory,
            CancellationToken ct) =>
        {
            if (RejectIfBearer(user) is { } reject) return reject;
            // DELETE bodies aren't inferred by minimal API; pass the slug
            // confirmation as ?confirm=<slug> instead. Same safety knob.
            if (!string.Equals(confirm, slug, StringComparison.Ordinal))
                return Results.BadRequest(new { error = "?confirm=<slug> must match the app slug" });
            var resolved = await ResolveAppAsync(ownerType, ownerId, slug, user, apps, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            // Space apps: only owner-level roles can delete. Personal apps:
            // owner check already done by ResolveAppAsync.
            if (resolved.SpaceRole is { } role && !role.CanDeleteSpace())
                return Results.Forbid();

            var appRef = new AppRef(ownerType, resolved.Owner!.Value.Id, resolved.App!.Id);
            var deleted = await apps.DeleteAsync(resolved.Owner.Value, resolved.App.Id, ct);
            if (!deleted) return Results.NotFound();

            // Drop the app's own .db folder. Folder layout is owner/<id>/apps/<appId>;
            // the registry row is gone above, so the file is now orphaned and safe to remove.
            var appFolder = System.IO.Path.GetDirectoryName(dbFactory.ResolveAppPath(appRef));
            if (!string.IsNullOrEmpty(appFolder) && Directory.Exists(appFolder))
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { Directory.Delete(appFolder, recursive: true); }
                catch { /* operator can clean up manually */ }
            }
            return Results.NoContent();
        }).WithName("DeleteApp");
    }

    private static void MapKeys(IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/apps/{ownerType}/{ownerId}/{slug}/keys")
            .RequireAuthorization();

        group.MapPost("/", async (
            string ownerType, string ownerId, string slug,
            CreateAppKeyRequest body,
            ClaimsPrincipal user,
            IAppRepository apps,
            ISpaceRepository spaces,
            IApiKeyRepository keys,
            CancellationToken ct) =>
        {
            if (RejectIfBearer(user) is { } reject) return reject;
            var userId = user.FindFirst(McpContextClaims.UserId)?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();
            if (body is null || string.IsNullOrWhiteSpace(body.Name)
                || body.Scopes is null || body.Scopes.Count == 0)
                return Results.BadRequest(new { error = "name + scopes required" });

            var unknown = ScopeCatalog.UnknownScopes(body.Scopes);
            if (unknown.Count > 0)
                return Results.BadRequest(new { error = "unknown scope(s)", unknown });

            var resolved = await ResolveAppAsync(ownerType, ownerId, slug, user, apps, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;

            var appRef = new AppRef(ownerType, resolved.Owner!.Value.Id, resolved.App!.Id);
            var issued = await keys.IssueAsync(userId, appRef, body.Name.Trim(), body.Scopes, ct);
            return Results.Ok(new CreateAppKeyResponse(
                issued.Record.Id, issued.Record.Name, issued.Record.KeyPrefix,
                issued.Record.Scopes, issued.Record.CreatedAt, issued.RawToken));
        }).WithName("CreateAppKey");

        group.MapGet("/", async (
            string ownerType, string ownerId, string slug,
            ClaimsPrincipal user, IAppRepository apps,
            ISpaceRepository spaces, IApiKeyRepository keys,
            DatabaseFactory dbFactory,
            CancellationToken ct) =>
        {
            if (RejectIfBearer(user) is { } reject) return reject;
            var userId = user.FindFirst(McpContextClaims.UserId)?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();
            var resolved = await ResolveAppAsync(ownerType, ownerId, slug, user, apps, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;

            // List from the user's own minted keys filtered by AppId. The api_keys
            // table is owner-keyed by user_id (the human who minted it), so we
            // can't list "every key for this app" without breaking the trust
            // boundary — but listing your own keys for this app is fine.
            var mine = await keys.ListByUserAsync(userId, ct);
            var rows = mine
                .Where(k => k.ContextType == "app" && k.ContextId == resolved.App!.Id)
                .Select(k => new AppKeyResponse(
                    k.Id, k.Name, k.KeyPrefix, k.Scopes, k.CreatedAt, k.LastUsedAt));
            return Results.Ok(rows);
        }).WithName("ListAppKeys");

        group.MapDelete("/{id}", async (
            string ownerType, string ownerId, string slug, string id,
            ClaimsPrincipal user, IApiKeyRepository keys,
            CancellationToken ct) =>
        {
            if (RejectIfBearer(user) is { } reject) return reject;
            var userId = user.FindFirst(McpContextClaims.UserId)?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();
            var ok = await keys.RevokeAsync(id, userId, ct);
            return ok ? Results.NoContent() : Results.NotFound();
        }).WithName("RevokeAppKey");
    }

    private static void MapData(IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/apps/{ownerType}/{ownerId}/{slug}")
            .RequireAuthorization();

        // ----- Tables / schema -----

        group.MapGet("/tables", async (
            string ownerType, string ownerId, string slug,
            ClaimsPrincipal user, IAppRepository apps, ISpaceRepository spaces,
            IAppSchemaRepository schema, CancellationToken ct) =>
        {
            var resolved = await ResolveAppAsync(ownerType, ownerId, slug, user, apps, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            var appRef = new AppRef(ownerType, resolved.Owner!.Value.Id, resolved.App!.Id);
            return Results.Ok(new { tables = await schema.ListTablesAsync(appRef, ct) });
        }).RequireScope(ScopeCatalog.AppAdmin);

        group.MapPost("/tables", async (
            string ownerType, string ownerId, string slug,
            JsonElement body,
            ClaimsPrincipal user, IAppRepository apps, ISpaceRepository spaces,
            IAppSchemaRepository schema, CancellationToken ct) =>
        {
            var resolved = await ResolveAppAsync(ownerType, ownerId, slug, user, apps, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            var name = AppJsonParsers.RequireString(body, "table");
            var cols = AppJsonParsers.ParseColumns(body, "columns");
            var appRef = new AppRef(ownerType, resolved.Owner!.Value.Id, resolved.App!.Id);
            await schema.CreateTableAsync(appRef, name, cols, ct);
            return Results.Ok(new { created = true, table = name });
        }).RequireScope(ScopeCatalog.AppAdmin);

        group.MapGet("/tables/{table}", async (
            string ownerType, string ownerId, string slug, string table,
            ClaimsPrincipal user, IAppRepository apps, ISpaceRepository spaces,
            IAppSchemaRepository schema, CancellationToken ct) =>
        {
            var resolved = await ResolveAppAsync(ownerType, ownerId, slug, user, apps, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            var appRef = new AppRef(ownerType, resolved.Owner!.Value.Id, resolved.App!.Id);
            var desc = await schema.DescribeTableAsync(appRef, table, ct);
            return desc is null ? Results.NotFound() : Results.Ok(desc);
        }).RequireScope(ScopeCatalog.AppAdmin);

        group.MapPatch("/tables/{table}", async (
            string ownerType, string ownerId, string slug, string table,
            AlterTableRequest body,
            ClaimsPrincipal user, IAppRepository apps, ISpaceRepository spaces,
            IAppSchemaRepository schema, CancellationToken ct) =>
        {
            var resolved = await ResolveAppAsync(ownerType, ownerId, slug, user, apps, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            var appRef = new AppRef(ownerType, resolved.Owner!.Value.Id, resolved.App!.Id);
            switch (body.Operation?.ToLowerInvariant())
            {
                case "add_column":
                    if (body.Column is null || body.Column.Value.ValueKind != JsonValueKind.Object)
                        return Results.BadRequest(new { error = "column required" });
                    var col = ParseAppColumn(body.Column.Value);
                    await schema.AddColumnAsync(appRef, table, col, ct);
                    return Results.NoContent();
                case "rename_column":
                    if (string.IsNullOrWhiteSpace(body.From) || string.IsNullOrWhiteSpace(body.To))
                        return Results.BadRequest(new { error = "from + to required" });
                    await schema.RenameColumnAsync(appRef, table, body.From!, body.To!, ct);
                    return Results.NoContent();
                case "drop_column":
                    if (string.IsNullOrWhiteSpace(body.Name))
                        return Results.BadRequest(new { error = "name required" });
                    await schema.DropColumnAsync(appRef, table, body.Name!, ct);
                    return Results.NoContent();
                default:
                    return Results.BadRequest(new { error = "operation must be add_column | rename_column | drop_column" });
            }
        }).RequireScope(ScopeCatalog.AppAdmin);

        group.MapDelete("/tables/{table}", async (
            string ownerType, string ownerId, string slug, string table,
            ClaimsPrincipal user, IAppRepository apps, ISpaceRepository spaces,
            IAppSchemaRepository schema, CancellationToken ct) =>
        {
            var resolved = await ResolveAppAsync(ownerType, ownerId, slug, user, apps, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            var appRef = new AppRef(ownerType, resolved.Owner!.Value.Id, resolved.App!.Id);
            await schema.DropTableAsync(appRef, table, ct);
            return Results.NoContent();
        }).RequireScope(ScopeCatalog.AppAdmin);

        group.MapPost("/tables/{table}/indexes", async (
            string ownerType, string ownerId, string slug, string table,
            JsonElement body,
            ClaimsPrincipal user, IAppRepository apps, ISpaceRepository spaces,
            IAppSchemaRepository schema, CancellationToken ct) =>
        {
            var resolved = await ResolveAppAsync(ownerType, ownerId, slug, user, apps, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            var appRef = new AppRef(ownerType, resolved.Owner!.Value.Id, resolved.App!.Id);
            var column = AppJsonParsers.RequireString(body, "column");
            await schema.CreateIndexAsync(appRef, table, column, ct);
            return Results.NoContent();
        }).RequireScope(ScopeCatalog.AppAdmin);

        // ----- Rows -----

        group.MapPost("/rows/{table}", async (
            string ownerType, string ownerId, string slug, string table,
            JsonElement body,
            ClaimsPrincipal user, IAppRepository apps, ISpaceRepository spaces,
            IAppRowRepository rows, CancellationToken ct) =>
        {
            var resolved = await ResolveAppAsync(ownerType, ownerId, slug, user, apps, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            var appRef = new AppRef(ownerType, resolved.Owner!.Value.Id, resolved.App!.Id);
            var fields = AppJsonParsers.ToDict(body);
            var actor = user.FindFirst(McpContextClaims.UserId)?.Value;
            var row = await rows.InsertAsync(appRef, table, fields, actor, ct);
            return Results.Ok(row);
        }).RequireScope(ScopeCatalog.AppWrite);

        group.MapGet("/rows/{table}/{id}", async (
            string ownerType, string ownerId, string slug, string table, string id,
            bool? includeDeleted,
            ClaimsPrincipal user, IAppRepository apps, ISpaceRepository spaces,
            IAppRowRepository rows, CancellationToken ct) =>
        {
            var resolved = await ResolveAppAsync(ownerType, ownerId, slug, user, apps, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            var appRef = new AppRef(ownerType, resolved.Owner!.Value.Id, resolved.App!.Id);
            var row = await rows.GetAsync(appRef, table, id, includeDeleted ?? false, ct);
            return row is null ? Results.NotFound() : Results.Ok(row);
        }).RequireScope(ScopeCatalog.AppRead);

        group.MapPatch("/rows/{table}/{id}", async (
            string ownerType, string ownerId, string slug, string table, string id,
            JsonElement body,
            ClaimsPrincipal user, IAppRepository apps, ISpaceRepository spaces,
            IAppRowRepository rows, CancellationToken ct) =>
        {
            var resolved = await ResolveAppAsync(ownerType, ownerId, slug, user, apps, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            var appRef = new AppRef(ownerType, resolved.Owner!.Value.Id, resolved.App!.Id);
            var patch = AppJsonParsers.ToDict(body);
            var actor = user.FindFirst(McpContextClaims.UserId)?.Value;
            var row = await rows.UpdateAsync(appRef, table, id, patch, actor, ct);
            return Results.Ok(row);
        }).RequireScope(ScopeCatalog.AppWrite);

        group.MapDelete("/rows/{table}/{id}", async (
            string ownerType, string ownerId, string slug, string table, string id,
            bool? hard,
            ClaimsPrincipal user, IAppRepository apps, ISpaceRepository spaces,
            IAppRowRepository rows, CancellationToken ct) =>
        {
            var resolved = await ResolveAppAsync(ownerType, ownerId, slug, user, apps, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            var appRef = new AppRef(ownerType, resolved.Owner!.Value.Id, resolved.App!.Id);
            var actor = user.FindFirst(McpContextClaims.UserId)?.Value;
            var ok = await rows.DeleteAsync(appRef, table, id, hard ?? false, actor, ct);
            return ok ? Results.NoContent() : Results.NotFound();
        }).RequireScope(ScopeCatalog.AppWrite);

        group.MapPost("/rows/{table}/{id}/restore", async (
            string ownerType, string ownerId, string slug, string table, string id,
            ClaimsPrincipal user, IAppRepository apps, ISpaceRepository spaces,
            IAppRowRepository rows, CancellationToken ct) =>
        {
            var resolved = await ResolveAppAsync(ownerType, ownerId, slug, user, apps, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            var appRef = new AppRef(ownerType, resolved.Owner!.Value.Id, resolved.App!.Id);
            var actor = user.FindFirst(McpContextClaims.UserId)?.Value;
            var ok = await rows.RestoreAsync(appRef, table, id, actor, ct);
            return ok ? Results.NoContent() : Results.NotFound();
        }).RequireScope(ScopeCatalog.AppWrite);

        // ----- Query / Count -----

        group.MapPost("/query/{table}", async (
            string ownerType, string ownerId, string slug, string table,
            JsonElement body,
            ClaimsPrincipal user, IAppRepository apps, ISpaceRepository spaces,
            IAppRowRepository rows, CancellationToken ct) =>
        {
            var resolved = await ResolveAppAsync(ownerType, ownerId, slug, user, apps, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            var appRef = new AppRef(ownerType, resolved.Owner!.Value.Id, resolved.App!.Id);
            var spec = AppJsonParsers.ParseQuerySpec(body);
            var hits = await rows.QueryAsync(appRef, table, spec, ct);
            return Results.Ok(new { rows = hits, count = hits.Count });
        }).RequireScope(ScopeCatalog.AppRead);

        group.MapPost("/count/{table}", async (
            string ownerType, string ownerId, string slug, string table,
            JsonElement body,
            ClaimsPrincipal user, IAppRepository apps, ISpaceRepository spaces,
            IAppRowRepository rows, CancellationToken ct) =>
        {
            var resolved = await ResolveAppAsync(ownerType, ownerId, slug, user, apps, spaces, ct);
            if (resolved.Error is not null) return resolved.Error;
            var appRef = new AppRef(ownerType, resolved.Owner!.Value.Id, resolved.App!.Id);
            var spec = AppJsonParsers.ParseQuerySpec(body);
            var n = await rows.CountAsync(appRef, table, spec, ct);
            return Results.Ok(new { count = n });
        }).RequireScope(ScopeCatalog.AppRead);
    }

    private static AppColumn ParseAppColumn(JsonElement col)
    {
        var name = AppJsonParsers.RequireString(col, "name");
        var type = AppColumnTypeExtensions.Parse(
            AppJsonParsers.RequireString(col, "type"));
        var nullable = AppJsonParsers.OptionalBool(col, "nullable", true);
        var unique = AppJsonParsers.OptionalBool(col, "unique", false);
        object? def = null;
        if (col.TryGetProperty("default", out var d) && d.ValueKind != JsonValueKind.Null)
            def = AppJsonParsers.ToClrValue(d);
        return new AppColumn(name, type, nullable, def, unique);
    }

    private record AppResolution(
        Fishbowl.Core.Models.App? App,
        ContextRef? Owner,
        SpaceRole? SpaceRole,
        IResult? Error);

    // Resolves {ownerType,ownerId,slug} → (App, owner-ContextRef) and runs
    // the same trust checks every data/lifecycle handler needs:
    //   * cookie principals: owner-user or space-member-of
    //   * Bearer principals: token's AppRef must agree with the URL triple
    //
    // The translation space-slug → space-ULID lives here so the rest of the API
    // can treat ContextRef.Space(id) uniformly.
    private static async Task<AppResolution> ResolveAppAsync(
        string ownerType, string ownerId, string slug,
        ClaimsPrincipal user,
        IAppRepository apps, ISpaceRepository spaces,
        CancellationToken ct)
    {
        var userId = user.FindFirst(McpContextClaims.UserId)?.Value;
        if (string.IsNullOrEmpty(userId))
            return new AppResolution(null, null, null, Results.Unauthorized());

        ContextRef owner;
        SpaceRole? spaceRole = null;

        if (string.Equals(ownerType, AppRef.OwnerTypeUser, StringComparison.Ordinal))
        {
            // Cookie principal must be the owning user; Bearer principal's
            // owner_id claim must match the URL user-id.
            if (user.Identity?.AuthenticationType != ApiKeyScheme && !string.Equals(userId, ownerId, StringComparison.Ordinal))
                return new AppResolution(null, null, null, Results.Forbid());
            owner = ContextRef.User(ownerId);
        }
        else if (string.Equals(ownerType, AppRef.OwnerTypeSpace, StringComparison.Ordinal))
        {
            var space = await spaces.GetBySlugAsync(ownerId, ct);
            if (space is null)
                return new AppResolution(null, null, null, Results.NotFound());

            if (user.Identity?.AuthenticationType == ApiKeyScheme)
            {
                // Bearer: owner_id claim must be this space's ULID (apps use ULID consistently).
                var claimedOwnerId = user.FindFirst(McpContextClaims.OwnerId)?.Value;
                if (!string.Equals(claimedOwnerId, space.Id, StringComparison.Ordinal))
                    return new AppResolution(null, null, null, Results.Forbid());
            }
            else
            {
                var role = await spaces.GetMembershipAsync(space.Id, userId, ct);
                if (role is null) return new AppResolution(null, null, null, Results.Forbid());
                spaceRole = role;
            }
            owner = ContextRef.Space(space.Id);
        }
        else
        {
            return new AppResolution(null, null, null,
                Results.BadRequest(new { error = "ownerType must be 'user' or 'space'" }));
        }

        var app = await apps.GetBySlugAsync(owner, slug, ct);
        if (app is null) return new AppResolution(null, owner, spaceRole, Results.NotFound());

        // Bearer: app-id claim must agree with the URL's resolved app.
        if (user.Identity?.AuthenticationType == ApiKeyScheme)
        {
            var claimedAppId = user.FindFirst(McpContextClaims.ContextId)?.Value;
            if (!string.Equals(claimedAppId, app.Id, StringComparison.Ordinal))
                return new AppResolution(app, owner, spaceRole, Results.Forbid());
        }

        return new AppResolution(app, owner, spaceRole, null);
    }

    private static async Task<(ContextRef? Owner, IResult? Error)> ResolveOwnerForCreateAsync(
        OwnerRef owner, string userId, ISpaceRepository spaces, CancellationToken ct)
    {
        if (string.Equals(owner.Type, AppRef.OwnerTypeUser, StringComparison.Ordinal))
        {
            // Personal apps always nest under the calling user. Refusing
            // cross-user creates is the same trust line ApiKeysApi draws.
            if (!string.Equals(owner.Id, userId, StringComparison.Ordinal))
                return (null, Results.Forbid());
            return (ContextRef.User(userId), null);
        }
        if (string.Equals(owner.Type, AppRef.OwnerTypeSpace, StringComparison.Ordinal))
        {
            var space = await spaces.GetBySlugAsync(owner.Id, ct);
            if (space is null) return (null, Results.NotFound());
            var role = await spaces.GetMembershipAsync(space.Id, userId, ct);
            if (role is null) return (null, Results.Forbid());
            // App creation = workspace-shaping. Members may create on behalf
            // of their space; readonly viewers may not.
            if (!role.Value.CanWrite()) return (null, Results.Forbid());
            return (ContextRef.Space(space.Id), null);
        }
        return (null, Results.BadRequest(new { error = "owner.type must be 'user' or 'space'" }));
    }

    private static IResult? RejectIfBearer(ClaimsPrincipal user)
        => user.Identity?.AuthenticationType == ApiKeyScheme
            ? Results.Forbid()
            : null;
}
