using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using System.Security.Claims;
using Fishbowl.Core;
using Fishbowl.Core.Mcp;
using Fishbowl.Data;

namespace Fishbowl.Api.Endpoints;

// CONCEPT.md § User Data Export:
//   "At any time, a user can download their complete .db file from
//    settings. This is a valid SQLite database readable with any SQLite
//    client. [...] This is a first-class feature, not an afterthought."
//
// The download uses SQLite's online backup API — safe even while the
// source DB is being written to. Cookie-only: Bearer tokens are scoped
// to agent/MCP workflows, not wholesale data exfil. Space variant in
// SpacesApi.cs restricts further to owner (see CanDeleteSpace — a full
// DB copy is a terminal action, not a member-level read).
public static class ExportApi
{
    public static RouteGroupBuilder MapExportApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/export");

        group.MapGet("/db", async (
            ClaimsPrincipal user,
            DatabaseFactory dbFactory,
            CancellationToken ct) =>
        {
            if (user.Identity?.AuthenticationType == McpContextClaims.BearerScheme)
                return Results.Forbid();

            ContextRef ctx;
            try { ctx = McpContextClaims.Resolve(user); }
            catch (InvalidOperationException) { return Results.Unauthorized(); }

            // Space exports live on the space-nested path and are role-gated
            // there. A cookie caller landing here is always a personal export.
            if (ctx.Type != ContextType.User) return Results.Forbid();

            var bytes = await BackupContextAsync(dbFactory, ctx, ct);
            var filename = $"fishbowl-{ctx.Id}-{DateTime.UtcNow:yyyyMMdd}.db";
            return Results.File(bytes, "application/vnd.sqlite3", filename);
        })
        .WithName("ExportUserDatabase")
        .WithSummary("Downloads the authenticated user's SQLite database. Cookie-auth only.");

        return group.RequireAuthorization();
    }

    // Uses SQLite's online backup API — safe under concurrent writes. The
    // destination is a temp file so we never touch the live DB file on
    // disk directly; the bytes are buffered into memory for the response
    // because a personal DB is small (typically < 20 MB even for heavy
    // notebooks) and streaming from a temp file requires response-
    // completion cleanup hooks that add more failure modes than they save.
    //
    // `Pooling=False` on the destination connection is load-bearing on
    // Windows. Microsoft.Data.Sqlite pools connection handles by default;
    // a `using` block returns the connection to the pool rather than
    // closing the underlying file, which leaves an exclusive lock on the
    // temp file and makes `File.ReadAllBytesAsync` throw IOException.
    // The source connection stays pooled (it's a live user DB).
    internal static async Task<byte[]> BackupContextAsync(
        DatabaseFactory dbFactory, ContextRef ctx, CancellationToken ct)
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            using (var source = (SqliteConnection)dbFactory.CreateContextConnection(ctx))
            using (var dest = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = tempPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString()))
            {
                dest.Open();
                source.BackupDatabase(dest);
                dest.Close();
            }
            return await File.ReadAllBytesAsync(tempPath, ct);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* best effort */ }
        }
    }
}
