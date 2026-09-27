using Fishbowl.Core.Util;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Bot.Discord.Commands;

// Single point of dispatch — keeps the hosted service thin and lets tests
// run command flow without a real socket. The router owns no state besides
// the handler lookup; per-invocation deps come through the handlers (which
// resolve from the DI scope created by the hosted service per command).
public class SlashCommandRouter
{
    private readonly Dictionary<string, ISlashCommandHandler> _handlers;
    private readonly ILogger<SlashCommandRouter> _logger;
    private readonly DiscordUserResolver? _resolver;

    // The resolver only picks the language of the router's own replies
    // (unknown command, failure); without one they're English.
    public SlashCommandRouter(
        IEnumerable<ISlashCommandHandler> handlers,
        ILogger<SlashCommandRouter>? logger = null,
        DiscordUserResolver? resolver = null)
    {
        _handlers = handlers.ToDictionary(h => h.Name, StringComparer.OrdinalIgnoreCase);
        _logger = logger ?? NullLogger<SlashCommandRouter>.Instance;
        _resolver = resolver;
    }

    public IEnumerable<ISlashCommandHandler> Handlers => _handlers.Values;

    public async Task<SlashCommandReply> DispatchAsync(
        string commandName,
        SlashCommandContext ctx,
        CancellationToken ct)
    {
        if (!_handlers.TryGetValue(commandName, out var handler))
        {
            _logger.LogWarning("Unknown slash command: {Command}", commandName);
            return SlashCommandReply.Plain(
                ChatText.Get("bot.unknownCommand", await LanguageAsync(ctx, ct), commandName));
        }

        try
        {
            return await handler.HandleAsync(ctx, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Slash command {Command} failed", commandName);
            return SlashCommandReply.Plain(ChatText.Get("bot.error", await LanguageAsync(ctx, ct)));
        }
    }

    // Never lets a lookup failure turn an error reply into another error.
    private async Task<string?> LanguageAsync(SlashCommandContext ctx, CancellationToken ct)
    {
        if (_resolver is null) return null;
        try { return await _resolver.LanguageOfAsync(ctx.DiscordUserId, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }
}
