using global::Discord;
using Fishbowl.Core.Util;

namespace Fishbowl.Bot.Discord.Commands;

// /help — static command list. Doesn't require linkage; `/link` is in the
// list so a fresh user can find their way in. In the linked user's language
// (ChatText); unlinked accounts read English.
public class HelpCommandHandler : ISlashCommandHandler
{
    public string Name => "help";

    private readonly DiscordUserResolver? _resolver;

    // The resolver picks the reply language; without one (tests) it's English.
    public HelpCommandHandler(DiscordUserResolver? resolver = null) => _resolver = resolver;

    public SlashCommandProperties Build()
    {
        var builder = new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription(ChatText.Get("cmd.help", null))
            .WithDescriptionLocalizations(ChatText.Localizations("cmd.help"));
        LinkCommandHandler.ApplyDmContext(builder);
        return builder.Build();
    }

    public async Task<SlashCommandReply> HandleAsync(SlashCommandContext ctx, CancellationToken ct)
    {
        var lang = _resolver is null ? null : await _resolver.LanguageOfAsync(ctx.DiscordUserId, ct);
        var lines = new[]
        {
            ChatText.Get("help.title", lang),
            ChatText.Get("help.link", lang),
            ChatText.Get("help.unlink", lang),
            ChatText.Get("help.remember", lang),
            ChatText.Get("help.search", lang),
            ChatText.Get("help.recent", lang),
            ChatText.Get("help.upcoming", lang),
            ChatText.Get("help.help", lang),
            string.Empty,
            ChatText.Get("help.secrets", lang),
        };
        return SlashCommandReply.Plain(string.Join("\n", lines));
    }
}
