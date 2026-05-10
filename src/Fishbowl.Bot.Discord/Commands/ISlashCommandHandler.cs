using global::Discord;

namespace Fishbowl.Bot.Discord.Commands;

// One handler per top-level slash command. Implementations declare the
// command shape (Build) and the dispatch logic (HandleAsync). The router
// uses Build at startup to register commands with Discord, and HandleAsync
// per invocation. HandleAsync takes a plain SlashCommandContext so tests
// can drive it without the Discord runtime.
public interface ISlashCommandHandler
{
    string Name { get; }
    SlashCommandProperties Build();
    Task<SlashCommandReply> HandleAsync(SlashCommandContext ctx, CancellationToken ct);
}
