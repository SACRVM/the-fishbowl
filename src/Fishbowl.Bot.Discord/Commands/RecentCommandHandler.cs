using global::Discord;
using Fishbowl.Core;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Util;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Bot.Discord.Commands;

// /recent — newest 5 notes. Same content rule as /search: titles only.
public class RecentCommandHandler : ISlashCommandHandler
{
    public string Name => "recent";

    private const int Limit = 5;

    private readonly DiscordUserResolver _resolver;
    private readonly INoteRepository _notes;
    private readonly ILogger<RecentCommandHandler> _logger;

    public RecentCommandHandler(
        DiscordUserResolver resolver,
        INoteRepository notes,
        ILogger<RecentCommandHandler>? logger = null)
    {
        _resolver = resolver;
        _notes = notes;
        _logger = logger ?? NullLogger<RecentCommandHandler>.Instance;
    }

    public SlashCommandProperties Build()
    {
        var builder = new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription(ChatText.Get("cmd.recent", null))
            .WithDescriptionLocalizations(ChatText.Localizations("cmd.recent"));
        LinkCommandHandler.ApplyDmContext(builder);
        return builder.Build();
    }

    public async Task<SlashCommandReply> HandleAsync(SlashCommandContext ctx, CancellationToken ct)
    {
        var userId = await _resolver.ResolveAsync(ctx.DiscordUserId, ct);
        if (string.IsNullOrEmpty(userId))
            return Replies.NotLinked;
        var lang = await _resolver.LanguageAsync(userId, ct);

        var notes = await _notes.GetAllAsync(
            ContextRef.User(userId),
            tags: null,
            match: "any",
            limit: Limit,
            offset: 0,
            ct);

        var hits = notes.ToList();
        if (hits.Count == 0)
            return Replies.Say(lang, "recent.none");

        var lines = new List<string>(hits.Count + 1) { ChatText.Get("recent.title", lang, hits.Count) };
        foreach (var note in hits)
            lines.Add($"• {note.Title.Replace("`", "\\`")}  `{note.Id}`");

        return SlashCommandReply.Plain(string.Join("\n", lines));
    }
}
