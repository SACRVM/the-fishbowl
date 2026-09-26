using System.Security.Claims;
using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Files;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Data.Files;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace Fishbowl.Api.Endpoints;

// The Files app's REST surface (docs/superpowers/specs/2026-09-26-files-app-design.md
// § API surface). Personal under /api/v1/files, the space mirror under
// /api/v1/spaces/{slug}/files — the same handlers with a different
// ContextRef. Everything goes through IFileService; a FileStoreException
// maps 1:1 to its status with { error, rule, segment, field, message }.
//
// Scopes read:files / write:files gate Bearer keys (cookie users pass).
// Permanent delete, empty trash and cross-workspace transfer are
// cookie-only: an irreversible wipe is a human action.
//
// Content is served from Fishbowl's own origin, so every response carries
// nosniff and — unless it is an allow-listed inline type — attachment plus a
// sandbox CSP. Sniffed type decides, never the name.
public static class FilesApi
{
    public sealed record FolderRequest(string Path);
    public sealed record MoveRequest(string From, string To);
    public sealed record RestoreRequest(string? OnConflict);
    public sealed record TransferFrom(string Workspace, string[] Paths);
    public sealed record TransferTo(string Workspace, string? Folder);
    public sealed record TransferRequest(string Op, TransferFrom From, TransferTo To, string? OnConflict);

    private const string SandboxCsp = "default-src 'none'; img-src 'self' data:; media-src 'self'; style-src 'unsafe-inline'; sandbox";
    // The browser's own PDF viewer must be able to render the document, and
    // Chrome refuses to under a `sandbox` directive.
    private const string PdfCsp = "default-src 'none'; img-src 'self' data:; style-src 'unsafe-inline'; object-src 'self'";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapFilesApi(this IEndpointRouteBuilder routes)
    {
        Map(routes.MapGroup("/api/v1/files").RequireAuthorization(), personal: true);
        Map(routes.MapGroup("/api/v1/spaces/{slug}/files").RequireAuthorization(), personal: false);
        return routes;
    }

    private sealed record Target(ContextRef Ctx, string Actor);

    // Personal: the caller's own context (a Bearer key bound to a space or an
    // app has its own routes — 403 here). Space: membership via
    // SpacesApi.ResolveSpaceAsync; readonly members can't write.
    private static async Task<(Target? Target, IResult? Error)> ResolveAsync(HttpContext http, bool write, CancellationToken ct)
    {
        var user = http.User;
        var userId = user.FindFirst(McpContextClaims.UserId)?.Value;
        if (string.IsNullOrEmpty(userId)) return (null, Results.Unauthorized());

        if (http.Request.RouteValues["slug"] is not string slug)
        {
            ContextRef ctx;
            try { ctx = McpContextClaims.Resolve(user); }
            catch (InvalidOperationException) { return (null, Results.Unauthorized()); }
            if (ctx.Type != ContextType.User) return (null, Results.Forbid());
            return (new Target(ctx, userId), null);
        }

        var spaces = http.RequestServices.GetRequiredService<ISpaceRepository>();
        var resolved = await SpacesApi.ResolveSpaceAsync(slug, user, spaces, ct);
        if (resolved.Error is not null) return (null, resolved.Error);
        if (write && !resolved.Role!.Value.CanWrite()) return (null, Results.Forbid());
        return (new Target(ContextRef.Space(resolved.Space!.Id), userId), null);
    }

    private static bool IsBearer(ClaimsPrincipal user) => user.Identity?.AuthenticationType == McpContextClaims.BearerScheme;

    private static IResult Fail(FileStoreException ex) => Results.Json(new
    {
        error = ex.Code,
        rule = ex.Rule,
        segment = ex.Segment,
        field = ex.Field,
        message = ex.Message,
    }, statusCode: ex.Status);

    // Resolve the workspace, run the operation, map refusals.
    private static async Task<IResult> Run(HttpContext http, bool write, Func<Target, IFileService, CancellationToken, Task<IResult>> op)
    {
        var ct = http.RequestAborted;
        var (target, error) = await ResolveAsync(http, write, ct);
        if (error is not null) return error;
        try { return await op(target!, http.RequestServices.GetRequiredService<IFileService>(), ct); }
        catch (FileStoreException ex) { return Fail(ex); }
    }

    // A lambda taking only HttpContext would bind as a RequestDelegate and
    // drop its IResult; typed as a Func it is a route handler.
    private static Delegate H(Func<HttpContext, Task<IResult>> handler) => handler;

    private static FileConflictMode Conflict(string? value, FileConflictMode fallback) => value?.ToLowerInvariant() switch
    {
        "fail" => FileConflictMode.Fail,
        "rename" => FileConflictMode.Rename,
        "replace" => FileConflictMode.Replace,
        _ => fallback,
    };

    private static void Map(RouteGroupBuilder g, bool personal)
    {
        var tag = personal ? "" : "Space";

        g.MapGet("/capabilities", H(http => Run(http, false, async (t, files, ct) =>
            Results.Ok(await files.GetCapabilitiesAsync(t.Ctx, ct)))))
            .WithName($"Get{tag}FileCapabilities").WithSummary("Name rules, case sensitivity and limits of this server's file store.")
            .RequireScope(ScopeCatalog.ReadFiles);

        g.MapGet("/list", (HttpContext http, string? path, string? cursor, int? limit) => Run(http, false, async (t, files, ct) =>
            Results.Ok(await files.ListAsync(t.Ctx, path, cursor, limit, ct))))
            .WithName($"List{tag}Files").WithSummary("A folder's entries, folders first; reconciles the folder with the disk.")
            .RequireScope(ScopeCatalog.ReadFiles);

        g.MapGet("/stat", (HttpContext http, string? path) => Run(http, false, async (t, files, ct) =>
            Results.Ok(await files.StatAsync(t.Ctx, path ?? "", ct))))
            .WithName($"Stat{tag}File").RequireScope(ScopeCatalog.ReadFiles);

        g.MapGet("/content", (HttpContext http, string? path, int? inline) => Run(http, false, async (t, files, ct) =>
        {
            var h = await files.OpenReadAsync(t.Ctx, path ?? "", ct);
            var isPdf = h.Mime == FileSniffer.Pdf;
            var asInline = inline == 1 && (isPdf || FileSniffer.IsInlineSafe(h.Mime));
            var headers = http.Response.Headers;
            headers[HeaderNames.XContentTypeOptions] = "nosniff";
            headers[HeaderNames.ContentSecurityPolicy] = asInline && isPdf ? PdfCsp : SandboxCsp;
            var disposition = new ContentDispositionHeaderValue(asInline ? "inline" : "attachment");
            disposition.SetHttpFileName(h.Entry.Name);
            headers[HeaderNames.ContentDisposition] = disposition.ToString();
            var type = asInline ? (isPdf ? FileSniffer.Pdf : FileSniffer.InlineContentType(h.Mime))
                     : FileSniffer.IsInlineSafe(h.Mime) || isPdf ? h.Mime : FileSniffer.OctetStream;
            return Results.File(h.Stream, type, fileDownloadName: null, lastModified: h.Entry.Mtime,
                entityTag: new EntityTagHeaderValue($"\"{h.Entry.Etag}\""), enableRangeProcessing: true);
        }))
            .WithName($"Get{tag}FileContent").WithSummary("Streams a file (Range, ETag). Attachment unless ?inline=1 and the sniffed type is safe; PDFs open inline in their own tab.")
            .RequireScope(ScopeCatalog.ReadFiles);

        g.MapPut("/content", (HttpContext http, string? path, int? parents) => Run(http, true, async (t, files, ct) =>
        {
            // A custom header forces a CORS preflight: no cross-site form
            // can post a file, whatever the cookie's SameSite.
            if (http.Request.Headers["X-Fishbowl-Upload"] != "1")
                return Results.Json(new { error = "upload_header_required", message = "Uploads need the X-Fishbowl-Upload: 1 header." },
                    statusCode: StatusCodes.Status400BadRequest);
            var ifNoneMatch = http.Request.Headers.IfNoneMatch.ToString().Trim();
            var ifMatch = http.Request.Headers.IfMatch.ToString();
            // Kestrel's 30 MB default stays for every other route.
            var caps = await files.GetCapabilitiesAsync(t.Ctx, ct);
            if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = caps.MaxFileBytes + 1;
            var result = await files.UploadAsync(t.Ctx, path ?? "", http.Request.Body, http.Request.ContentLength,
                string.IsNullOrWhiteSpace(ifMatch) ? null : ifMatch, ifNoneMatch == "*", parents == 1, t.Actor, ct);
            http.Response.Headers.ETag = $"\"{result.Entry.Etag}\"";
            return result.Created ? Results.Created((string?)null, result.Entry) : Results.Ok(result.Entry);
        }))
            .WithName($"Upload{tag}File").WithSummary("Raw-body upload. Needs X-Fishbowl-Upload: 1 and If-None-Match: * (create) or If-Match (replace); else 428.")
            .RequireScope(ScopeCatalog.WriteFiles);

        g.MapPost("/folders", (HttpContext http, FolderRequest body) => Run(http, true, async (t, files, ct) =>
            Results.Created((string?)null, await files.CreateFolderAsync(t.Ctx, body.Path, t.Actor, ct))))
            .WithName($"Create{tag}Folder").RequireScope(ScopeCatalog.WriteFiles);

        g.MapPost("/move", (HttpContext http, MoveRequest body) => Run(http, true, async (t, files, ct) =>
            Results.Ok(await files.MoveAsync(t.Ctx, body.From, body.To, t.Actor, ct))))
            .WithName($"Move{tag}File").WithSummary("Rename or move within the workspace.").RequireScope(ScopeCatalog.WriteFiles);

        g.MapPost("/copy", (HttpContext http, MoveRequest body) => Run(http, true, async (t, files, ct) =>
            Results.Created((string?)null, await files.CopyAsync(t.Ctx, body.From, body.To, t.Actor, ct))))
            .WithName($"Copy{tag}File").RequireScope(ScopeCatalog.WriteFiles);

        g.MapDelete("", (HttpContext http, string? path, int? permanent) =>
        {
            if (permanent == 1 && IsBearer(http.User)) return Task.FromResult(Results.Forbid());
            return Run(http, true, async (t, files, ct) =>
            {
                if (permanent == 1)
                {
                    await files.DeletePermanentlyAsync(t.Ctx, path ?? "", t.Actor, ct);
                    return Results.NoContent();
                }
                return Results.Ok(await files.TrashAsync(t.Ctx, path ?? "", t.Actor, ct));
            });
        })
            .WithName($"Delete{tag}File").WithSummary("Moves to trash; ?permanent=1 deletes for good (cookie only).")
            .RequireScope(ScopeCatalog.WriteFiles);

        g.MapGet("/trash", H(http => Run(http, false, async (t, files, ct) =>
            Results.Ok(await files.ListTrashAsync(t.Ctx, ct)))))
            .WithName($"List{tag}Trash").RequireScope(ScopeCatalog.ReadFiles);

        // The body is optional (a sync client restores with a bare POST), and
        // a bound body parameter would make routing demand a JSON content type.
        g.MapPost("/trash/{id}/restore", (HttpContext http, string id) => Run(http, true, async (t, files, ct) =>
        {
            RestoreRequest? body = null;
            if (http.Request.HasJsonContentType() && http.Request.ContentLength is not 0)
                body = await http.Request.ReadFromJsonAsync<RestoreRequest>(ct);
            return Results.Ok(await files.RestoreAsync(t.Ctx, id, Conflict(body?.OnConflict, FileConflictMode.Fail), t.Actor, ct));
        }))
            .WithName($"Restore{tag}Trash").WithSummary("Back to its original path; onConflict fail (409) or rename.")
            .RequireScope(ScopeCatalog.WriteFiles);

        g.MapDelete("/trash/{id}", (HttpContext http, string id) =>
        {
            if (IsBearer(http.User)) return Task.FromResult(Results.Forbid());
            return Run(http, true, async (t, files, ct) =>
            {
                await files.PurgeTrashAsync(t.Ctx, id, ct);
                return Results.NoContent();
            });
        }).WithName($"Purge{tag}Trash").WithSummary("Deletes one trash entry for good. Cookie only.");

        g.MapDelete("/trash", H(http =>
        {
            if (IsBearer(http.User)) return Task.FromResult(Results.Forbid());
            return Run(http, true, async (t, files, ct) =>
                Results.Ok(new { purged = await files.EmptyTrashAsync(t.Ctx, ct) }));
        })).WithName($"Empty{tag}Trash").WithSummary("Empties the trash. Cookie only.");

        g.MapGet("/usage", H(http => Run(http, false, async (t, files, ct) =>
            Results.Ok(await files.GetUsageAsync(t.Ctx, ct)))))
            .WithName($"Get{tag}FileUsage").RequireScope(ScopeCatalog.ReadFiles);

        g.MapGet("/changes", (HttpContext http, string? cursor, int? limit) => Run(http, false, async (t, files, ct) =>
            Results.Ok(await files.GetChangesAsync(t.Ctx, cursor, limit, ct))))
            .WithName($"Get{tag}FileChanges").WithSummary("The change feed after a cursor; 410 when the cursor expired — take a snapshot.")
            .RequireScope(ScopeCatalog.ReadFiles);

        g.MapGet("/snapshot", H(http => Run(http, false, async (t, files, ct) =>
        {
            var (cursor, entries) = await files.SnapshotAsync(t.Ctx, ct);
            http.Response.ContentType = "application/x-ndjson";
            var body = http.Response.Body;
            var newline = new byte[] { (byte)'\n' };
            foreach (var e in entries)
            {
                await JsonSerializer.SerializeAsync(body, e, Json, ct);
                await body.WriteAsync(newline, ct);
            }
            await JsonSerializer.SerializeAsync(body, new { cursor }, Json, ct);
            await body.WriteAsync(newline, ct);
            return Results.Empty;
        })))
            .WithName($"Get{tag}FileSnapshot").WithSummary("NDJSON: one line per entry, then { cursor }.")
            .RequireScope(ScopeCatalog.ReadFiles);

        if (personal)
        {
            g.MapPost("/transfer", TransferAsync)
                .WithName("TransferFiles").WithSummary("Copy or move between workspaces (personal ↔ spaces). Cookie only.");
        }
    }

    private static async Task<IResult> TransferAsync(HttpContext http, TransferRequest body)
    {
        var user = http.User;
        if (IsBearer(user)) return Results.Forbid();
        var userId = user.FindFirst(McpContextClaims.UserId)?.Value;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();
        var ct = http.RequestAborted;
        var move = body.Op?.ToLowerInvariant() switch
        {
            "copy" => false,
            "move" => true,
            _ => (bool?)null,
        };
        if (move is null || body.From?.Paths is not { Length: > 0 } || body.To is null)
            return Results.Json(new { error = "invalid_request", message = "Needs op (copy|move), from.paths and to." }, statusCode: 400);

        var spaces = http.RequestServices.GetRequiredService<ISpaceRepository>();
        async Task<(ContextRef? Ctx, IResult? Error)> Workspace(string? ws, bool write)
        {
            if (ws == "personal") return (ContextRef.User(userId), null);
            if (ws is null || !ws.StartsWith("space:", StringComparison.Ordinal))
                return (null, Results.Json(new { error = "invalid_workspace", message = "Workspace is \"personal\" or \"space:<slug>\"." }, statusCode: 400));
            var resolved = await SpacesApi.ResolveSpaceAsync(ws["space:".Length..], user, spaces, ct);
            if (resolved.Error is not null) return (null, resolved.Error);
            if (write && !resolved.Role!.Value.CanWrite()) return (null, Results.Forbid());
            return (ContextRef.Space(resolved.Space!.Id), null);
        }

        // Moving takes from the source, so it needs write there too.
        var (from, fromError) = await Workspace(body.From.Workspace, write: move.Value);
        if (fromError is not null) return fromError;
        var (to, toError) = await Workspace(body.To.Workspace, write: true);
        if (toError is not null) return toError;

        try
        {
            var files = http.RequestServices.GetRequiredService<IFileService>();
            var results = await files.TransferAsync(from!.Value, body.From.Paths, to!.Value, body.To.Folder ?? "",
                move.Value, Conflict(body.OnConflict, FileConflictMode.Fail), userId, ct);
            return Results.Ok(new { results });
        }
        catch (FileStoreException ex) { return Fail(ex); }
    }
}
