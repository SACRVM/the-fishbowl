using global::Discord;
using Fishbowl.Core;
using Fishbowl.Core.Search;
using Fishbowl.Core.Util;
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
            .WithDescription(ChatText.Get("cmd.search", null))
            .WithDescriptionLocalizations(ChatText.Localizations("cmd.search"))
            .AddOption("query", ApplicationCommandOptionType.String,
                ChatText.Get("cmd.search.query", null), isRequired: true,
                descriptionLocalizations: ChatText.Localizations("cmd.search.query"));

        LinkCommandHandler.ApplyDmContext(builder);
        return builder.Build();
    }

    public async Task<SlashCommandReply> HandleAsync(SlashCommandContext ctx, CancellationToken ct)
    {
        var (user, refusal) = await _resolver.ResolveActiveAsync(ctx.DiscordUserId, ct);
        if (user is null)
            return refusal!;
        var (userId, lang) = user;

        var query = ctx.Get("query")?.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return Replies.Say(lang, "search.empty");

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
            var degraded = result.Degraded ? ChatText.Get("search.noneDegraded", lang) : string.Empty;
            return SlashCommandReply.Plain(ChatText.Get("search.none", lang, Escape(query)) + degraded);
        }

        var lines = new List<string>(result.Hits.Count + 2)
        {
            ChatText.Get("search.top", lang, result.Hits.Count, Escape(query)),
        };
        foreach (var hit in result.Hits)
        {
            lines.Add($"• {Escape(hit.Note.Title)}  `{hit.Note.Id}`");
        }
        if (result.Degraded)
            lines.Add(ChatText.Get("search.degraded", lang));

        return SlashCommandReply.Plain(string.Join("\n", lines));
    }

    // Escapes the markdown chars Discord rendering would mangle in a
    // user-supplied string. Cheap allowlist; the message is plain text either
    // way, so worst case we lose a backtick the user wrote literally.
    private static string Escape(string s)
        => s.Replace("`", "\\`").Replace("*", "\\*").Replace("_", "\\_");
}
