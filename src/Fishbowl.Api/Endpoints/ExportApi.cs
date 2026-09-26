using System.Globalization;
using System.IO.Compression;
using System.Security.Claims;
using Fishbowl.Core;
using Fishbowl.Core.Files;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Repositories;
using Fishbowl.Data;
using Fishbowl.Data.Files;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;

namespace Fishbowl.Api.Endpoints;

// CONCEPT.md § User Data Export:
//   "At any time, a user can download their complete .db file from
//    settings. This is a valid SQLite database readable with any SQLite
//    client. [...] This is a first-class feature, not an afterthought."
//
// Files phase 3 (spec § Operational touch points) makes it two independent
// choices plus a combination, each a ZIP streamed straight to the response —
// never built in memory or in a temp ZIP, ZIP64 on its own:
//   /export/db     the workspace DB (SQLite online backup — safe under writes)
//   /export/files  files/ minus trash and in-flight upload temps
//   /export/all    both, offered only while files are below Export:CombinedMaxBytes
//   /export/info   the sizes the dialog shows first
// Cookie-only: Bearer tokens are scoped to agent/MCP workflows, not wholesale
// data exfil. The space variants in SpacesApi.cs are owner-only (CanDeleteSpace
// — walking off with a whole space is a terminal action).
public static class ExportApi
{
    public const string KindDb = "db";
    public const string KindFiles = "files";
    public const string KindAll = "all";

    public static RouteGroupBuilder MapExportApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/export");

        group.MapGet("/info", async (ClaimsPrincipal user, DatabaseFactory dbFactory, ISystemRepository system, CancellationToken ct) =>
        {
            var (ctx, error) = Personal(user);
            return error ?? Results.Ok(await InfoAsync(dbFactory, system, ctx!.Value, ct));
        })
        .WithName("ExportUserInfo")
        .WithSummary("Sizes of the personal export choices, and whether the combined ZIP is offered. Cookie-auth only.");

        foreach (var kind in new[] { KindDb, KindFiles, KindAll })
        {
            group.MapGet("/" + kind, async (HttpContext http, ClaimsPrincipal user, DatabaseFactory dbFactory,
                ISystemRepository system, CancellationToken ct) =>
            {
                var (ctx, error) = Personal(user);
                if (error is not null) return error;
                return await ZipAsync(http, dbFactory, system, ctx!.Value, kind, $"fishbowl-{ctx.Value.Id}", ct);
            })
            .WithName(kind switch
            {
                KindDb => "ExportUserDatabase",
                KindFiles => "ExportUserFiles",
                _ => "ExportUserAll",
            })
            .WithSummary(kind switch
            {
                KindDb => "Downloads the personal SQLite database as a ZIP. Cookie-auth only.",
                KindFiles => "Downloads every personal file (no trash) as a streamed ZIP. Cookie-auth only.",
                _ => "Downloads database and files in one streamed ZIP (only below Export:CombinedMaxBytes). Cookie-auth only.",
            });
        }

        return group.RequireAuthorization();
    }

    private static (ContextRef? Ctx, IResult? Error) Personal(ClaimsPrincipal user)
    {
        if (user.Identity?.AuthenticationType == McpContextClaims.BearerScheme)
            return (null, Results.Forbid());
        ContextRef ctx;
        try { ctx = McpContextClaims.Resolve(user); }
        catch (InvalidOperationException) { return (null, Results.Unauthorized()); }
        // Space exports live on the space-nested path and are role-gated
        // there. A cookie caller landing here is always a personal export.
        return ctx.Type != ContextType.User ? (null, Results.Forbid()) : (ctx, null);
    }

    private static string DbFileName(ContextRef ctx)
        => ctx.Type == ContextType.Space ? DatabaseFactory.SpaceDbFileName : DatabaseFactory.PersonalDbFileName;

    private static long FilesBytes(DatabaseFactory dbFactory, ContextRef ctx)
    {
        var files = Path.Combine(dbFactory.ResolveContextFolder(ctx), "files");
        return DiskFileStore.Measure(files).Bytes - DiskFileStore.Measure(Path.Combine(files, FileNameRules.TrashFolder)).Bytes;
    }

    private static async Task<long> CombinedMaxAsync(ISystemRepository system, CancellationToken ct)
    {
        var v = await system.GetConfigAsync(DataLifecycleLimits.ExportCombinedMaxBytesKey, ct);
        return long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) && x >= 0
            ? x : DataLifecycleLimits.DefaultExportCombinedMaxBytes;
    }

    internal static async Task<ExportInfo> InfoAsync(DatabaseFactory dbFactory, ISystemRepository system, ContextRef ctx, CancellationToken ct)
    {
        // Opening the context makes sure the DB exists (and is migrated).
        using (dbFactory.CreateContextConnection(ctx)) { }
        var dbPath = Path.Combine(dbFactory.ResolveContextFolder(ctx), DbFileName(ctx));
        var dbBytes = File.Exists(dbPath) ? new FileInfo(dbPath).Length : 0;
        var filesBytes = FilesBytes(dbFactory, ctx);
        var max = await CombinedMaxAsync(system, ct);
        return new ExportInfo(dbBytes, filesBytes, max, max == 0 || filesBytes <= max);
    }

    // Streams one export ZIP. Entries go to the response one after another;
    // ZipArchive writes synchronously, so this request is allowed to.
    internal static async Task<IResult> ZipAsync(HttpContext http, DatabaseFactory dbFactory, ISystemRepository system,
        ContextRef ctx, string kind, string baseName, CancellationToken ct)
    {
        if (kind == KindAll)
        {
            var max = await CombinedMaxAsync(system, ct);
            if (max > 0 && FilesBytes(dbFactory, ctx) > max)
            {
                var refusal = new
                {
                    error = "too_large",
                    field = "combined",
                    message = "Too many files for one ZIP — download the database and the files separately.",
                };
                return Results.Json(refusal, statusCode: StatusCodes.Status413PayloadTooLarge);
            }
        }

        var bodyControl = http.Features.Get<IHttpBodyControlFeature>();
        if (bodyControl is not null) bodyControl.AllowSynchronousIO = true;
        http.Response.Headers.CacheControl = "no-store";

        var filename = $"{baseName}-{DateTime.UtcNow:yyyyMMdd}-{kind}.zip";
        return Results.Stream(stream =>
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
            if (kind is KindDb or KindAll)
            {
                using var conn = (SqliteConnection)dbFactory.CreateContextConnection(ctx);
                ZipExport.AddDatabase(zip, DbFileName(ctx), conn, ct);
            }
            if (kind is KindFiles or KindAll)
                ZipExport.AddFolder(zip, Path.Combine(dbFactory.ResolveContextFolder(ctx), "files"),
                    kind == KindAll ? "files" : "", ZipExport.SkipForExport, ct);
            return Task.CompletedTask;
        }, "application/zip", filename);
    }
}
