using global::Discord;
using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Util;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Bot.Discord.Commands;

// /remember <text> [title] — quick-save a note from chat. CONCEPT.md:
// "Remember: team meeting next thursday at 10am" — the bot is the user's
// chat-side capture button.
public class RememberCommandHandler : ISlashCommandHandler
{
    public string Name => "remember";

    private const int MaxTitleLength = 80;

    private readonly DiscordUserResolver _resolver;
    private readonly INoteRepository _notes;
    private readonly ILogger<RememberCommandHandler> _logger;

    public RememberCommandHandler(
        DiscordUserResolver resolver,
        INoteRepository notes,
        ILogger<RememberCommandHandler>? logger = null)
    {
        _resolver = resolver;
        _notes = notes;
        _logger = logger ?? NullLogger<RememberCommandHandler>.Instance;
    }

    public SlashCommandProperties Build()
    {
        var builder = new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription(ChatText.Get("cmd.remember", null))
            .WithDescriptionLocalizations(ChatText.Localizations("cmd.remember"))
            .AddOption("text", ApplicationCommandOptionType.String,
                ChatText.Get("cmd.remember.text", null),
                isRequired: true,
                descriptionLocalizations: ChatText.Localizations("cmd.remember.text"))
            .AddOption("title", ApplicationCommandOptionType.String,
                ChatText.Get("cmd.remember.title", null),
                isRequired: false,
                descriptionLocalizations: ChatText.Localizations("cmd.remember.title"));

        LinkCommandHandler.ApplyDmContext(builder);
        return builder.Build();
    }

    public async Task<SlashCommandReply> HandleAsync(SlashCommandContext ctx, CancellationToken ct)
    {
        var (user, refusal) = await _resolver.ResolveActiveAsync(ctx.DiscordUserId, ct);
        if (user is null)
            return refusal!;
        var (userId, lang) = user;

        var text = ctx.Get("text")?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return Replies.Say(lang, "remember.empty");

        var title = ctx.Get("title")?.Trim();
        if (string.IsNullOrWhiteSpace(title))
            title = TitleFrom(text);

        var note = new Note
        {
            Title = title,
            Content = text,
            Tags = new List<string> { "source:discord" },
            CreatedBy = userId,
        };

        var id = await _notes.CreateAsync(ContextRef.User(userId), userId, note, NoteSource.Human, ct);
        _logger.LogInformation("Discord /remember → note {NoteId} for user {UserId}", id, userId);

        return Replies.Say(lang, "remember.saved", Trim(title, 80));
    }

    private static string TitleFrom(string text)
    {
        // First newline or sentence break, capped to MaxTitleLength so we don't
        // dump a paragraph as a title.
        var firstBreak = text.AsSpan().IndexOfAny('\n', '\r', '.');
        var slice = firstBreak > 0 ? text[..firstBreak].Trim() : text.Trim();
        return Trim(slice, MaxTitleLength);
    }

    private static string Trim(string s, int max)
        => s.Length <= max ? s : s[..(max - 1)] + "…";
}
