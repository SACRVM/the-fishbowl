using global::Discord;
using Fishbowl.Core;
using Fishbowl.Core.Search;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Bot.Discord.Commands;

// /search <query> — hybrid (semantic + FTS) over the user's notes. Replies
// with titles + a hint to open them on the web. Never inlines content: even
// non-secret notes can be long, and the user might be in a public DM context
// (e.g. screenshare). The link-out is deliberate.
public class SearchCommandHandler : ISlashCommandHandler
{
    public string Name => "search";

    private const int DefaultLimit = 5;

    private readonly DiscordUserResolver _resolver;
    private readonly ISearchService _search;
    private readonly ILogger<SearchCommandHandler> _logger;

    public SearchCommandHandler(
        DiscordUserResolver resolver,
        ISearchService search,
        ILogger<SearchCommandHandler>? logger = null)
    {
        _resolver = resolver;
        _search = search;
        _logger = logger ?? NullLogger<SearchCommandHandler>.Instance;
    }

    public SlashCommandProperties Build()
    {
        var builder = new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Search your Fishbowl notes.")
            .AddOption("query", ApplicationCommandOptionType.String,
                "What to search for.", isRequired: true);

        LinkCommandHandler.ApplyDmContext(builder);
        return builder.Build();
    }

    public async Task<SlashCommandReply> HandleAsync(SlashCommandContext ctx, CancellationToken ct)
    {
        var userId = await _resolver.ResolveAsync(ctx.DiscordUserId, ct);
        if (string.IsNullOrEmpty(userId))
            return Replies.NotLinked;

        var query = ctx.Get("query")?.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return SlashCommandReply.Plain("Search what? `/search query:<...>`.");

        var result = await _search.HybridSearchAsync(
            ContextRef.User(userId),
            query,
            limit: DefaultLimit,
            includePending: false,
            tags: null,
            match: "any",
            ct: ct);

        if (result.Hits.Count == 0)
        {
            var degraded = result.Degraded
                ? " (semantic search is still warming up — try again in a few minutes if this doesn't look right)"
                : string.Empty;
            return SlashCommandReply.Plain($"No matches for **{Escape(query)}**.{degraded}");
        }

        var lines = new List<string>(result.Hits.Count + 2)
        {
            $"Top {result.Hits.Count} for **{Escape(query)}**:",
        };
        foreach (var hit in result.Hits)
        {
            lines.Add($"• {Escape(hit.Note.Title)}  `{hit.Note.Id}`");
        }
        if (result.Degraded)
            lines.Add("_(semantic search warming up — ranking is FTS-only for now)_");

        return SlashCommandReply.Plain(string.Join("\n", lines));
    }

    // Escapes the markdown chars Discord rendering would mangle in a
    // user-supplied string. Cheap allowlist; the message is plain text either
    // way, so worst case we lose a backtick the user wrote literally.
    private static string Escape(string s)
        => s.Replace("`", "\\`").Replace("*", "\\*").Replace("_", "\\_");
}
