using System.Security.Claims;
using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Search;

namespace Fishbowl.Mcp.Tools;

// Hybrid (semantic + FTS) search over notes in the resolved context.
// HybridSearchService handles the blending; this tool is a thin adapter
// that parses MCP args, invokes the search, and shapes the response.
// Secrets are already stripped by the service.
public class SearchMemoryTool : IMcpTool
{
    private readonly ISearchService _search;

    public SearchMemoryTool(ISearchService search) { _search = search; }

    public string Name => "search_memory";
    public string Description =>
        "Hybrid (semantic + keyword) search over notes in the current context. " +
        "Returns matching notes ranked by relevance with secrets stripped.";
    public string RequiredScope => "read:notes";
    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            query = new { type = "string", description = "Search term" },
            limit = new { type = "integer", @default = 10, description = "Max hits to return" },
            include_pending = new { type = "boolean", @default = true, description = "Include review:pending notes" },
            tags = new
            {
                type = "array",
                items = new { type = "string" },
                description = "Restrict hits to notes carrying these tags",
            },
            match = new
            {
                type = "string",
                @enum = new[] { "any", "all" },
                @default = "any",
                description = "With multiple tags: 'any' (default) keeps notes with at least one; 'all' requires every tag",
            },
        },
        required = new[] { "query" },
    };

    public async Task<object> InvokeAsync(
        ContextRef ctx, string actor, JsonElement arguments, ClaimsPrincipal principal, CancellationToken ct)
    {
        var query = arguments.GetProperty("query").GetString() ?? string.Empty;
        var limit = arguments.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number
            ? Math.Clamp(l.GetInt32(), 1, 100) : 10;
        var includePending = !arguments.TryGetProperty("include_pending", out var p)
            || p.ValueKind == JsonValueKind.Null
            || p.GetBoolean();

        IReadOnlyCollection<string>? tags = null;
        if (arguments.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
        {
            tags = tagsEl.EnumerateArray()
                .Select(e => e.GetString() ?? string.Empty)
                .Where(s => s.Length > 0)
                .ToList();
            if (tags.Count == 0) tags = null;
        }
        var match = arguments.TryGetProperty("match", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString() ?? "any" : "any";

        var result = await _search.HybridSearchAsync(ctx, query, limit, includePending, tags, match, ct);

        // Surface the degraded flag so MCP clients can reason about partial
        // ranking (e.g. when the embedding model is still downloading on
        // first run). Notes come through already stripped of secret blocks.
        return new
        {
            notes = result.Hits.Select(h => new
            {
                id = h.Note.Id,
                title = h.Note.Title,
                content = h.Note.Content,
                tags = h.Note.Tags,
                created_at = h.Note.CreatedAt,
                updated_at = h.Note.UpdatedAt,
                score = h.Score,
            }).ToList(),
            degraded = result.Degraded,
        };
    }
}
