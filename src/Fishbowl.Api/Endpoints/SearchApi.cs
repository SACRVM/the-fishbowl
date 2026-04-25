using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Security.Claims;
using Fishbowl.Core;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Search;

namespace Fishbowl.Api.Endpoints;

// Search-side administrative endpoints. Kept separate from NotesApi so
// re-index doesn't clutter the /api/v1/notes surface — different verb,
// different consumer (human hitting the settings UI, not MCP clients).
public static class SearchApi
{
    public static RouteGroupBuilder MapSearchApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/search");

        // CONCEPT.md § Search — "One search bar for everything. Powered by
        // a local AI model that understands meaning, not just matching
        // words." Today hits notes only via HybridSearchService; contacts
        // live on /api/v1/contacts/search for now until a unified ranker
        // exists. Response shape mirrors the MCP `search_memory` tool
        // (notes[] + degraded flag) so clients can swap transports with
        // minimal code change.
        group.MapGet("/", async (
            ClaimsPrincipal user,
            ISearchService search,
            string q, string[]? tag = null, string? match = null,
            int limit = 20, bool includePending = true,
            CancellationToken ct = default) =>
        {
            ContextRef ctx;
            try { ctx = McpContextClaims.Resolve(user); }
            catch (InvalidOperationException) { return Results.Unauthorized(); }

            // Clamp limit defensively so a mistaken `?limit=99999999` can't
            // tip over the candidate pool inside the ranker.
            limit = Math.Clamp(limit, 1, 100);

            var tags = tag is { Length: > 0 } ? tag : null;
            var matchMode = match == "all" ? "all" : "any";

            var result = await search.HybridSearchAsync(
                ctx, q ?? "", limit, includePending, tags, matchMode, ct);
            return Results.Ok(new
            {
                notes = result.Hits.Select(h => new
                {
                    id = h.Note.Id,
                    title = h.Note.Title,
                    content = h.Note.Content,
                    tags = h.Note.Tags,
                    createdAt = h.Note.CreatedAt,
                    updatedAt = h.Note.UpdatedAt,
                    pinned = h.Note.Pinned,
                    archived = h.Note.Archived,
                    score = h.Score,
                }).ToList(),
                degraded = result.Degraded,
            });
        })
        .WithName("SearchNotes")
        .WithSummary("Hybrid (semantic + FTS) search across notes in the resolved context.")
        .RequireScope("read:notes");

        // Bulk re-embed every note in the resolved context. Cookie-only —
        // Bearer callers shouldn't be able to trigger long-running
        // maintenance against a user's DB from outside the UI.
        //
        // The operation is synchronous: response comes back only after
        // every note is processed. At personal-memory scale (hundreds-to-
        // low-thousands) that's seconds, not minutes. Revisit with an
        // async/progress-polled design if vault sizes grow.
        group.MapPost("/reindex", async (
            ClaimsPrincipal user,
            INoteRepository repo,
            CancellationToken ct) =>
        {
            if (user.Identity?.AuthenticationType == McpContextClaims.BearerScheme)
                return Results.Forbid();

            ContextRef ctx;
            try { ctx = McpContextClaims.Resolve(user); }
            catch (InvalidOperationException) { return Results.Unauthorized(); }

            var result = await repo.ReEmbedAllAsync(ctx, ct);
            return Results.Ok(new
            {
                processed = result.Processed,
                failed = result.Failed,
            });
        })
        .WithName("ReindexSearch")
        .WithSummary("Re-embeds every note in the current context. Cookie-auth only.");

        return group;
    }
}
