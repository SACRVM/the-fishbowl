using global::Discord;

namespace Fishbowl.Bot.Discord.Commands;

// /help — static command list. Doesn't require linkage; `/link` is in the
// list so a fresh user can find their way in.
public class HelpCommandHandler : ISlashCommandHandler
{
    public string Name => "help";

    public SlashCommandProperties Build()
    {
        var builder = new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("List Fishbowl bot commands.");
        LinkCommandHandler.ApplyDmContext(builder);
        return builder.Build();
    }

    public Task<SlashCommandReply> HandleAsync(SlashCommandContext ctx, CancellationToken ct)
    {
        var lines = new[]
        {
            "**Fishbowl bot — commands**",
            "`/link <code>` — connect this Discord account to your Fishbowl",
            "`/remember text:<...> [title:<...>]` — save a note",
            "`/search query:<...>` — hybrid search across your notes (titles only)",
            "`/recent` — list your newest notes",
            "`/help` — show this message",
            string.Empty,
            "I never reveal secret content (`::secret` blocks) in chat — open them in the web UI.",
        };
        return Task.FromResult(SlashCommandReply.Plain(string.Join("\n", lines)));
    }
}
