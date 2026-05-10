using global::Discord;
using global::Discord.WebSocket;
using Fishbowl.Core.Models;
using Fishbowl.Core.Plugins;
using Fishbowl.Core.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Bot.Discord;

// IBotClient impl for Discord. Owns the SocketClient (set by the hosted
// service after login). SendAsync resolves the user's stored DM channel
// from notification_channels and posts; reminder fan-out and any other
// outbound DM goes through here.
//
// ReceiveAsync is intentionally an empty stream: this bot uses Discord's
// slash-command interaction model, not raw message events, so there's no
// loop for a plugin host to drive. Inbound dispatch happens inside
// DiscordBotHostedService → SlashCommandRouter, not over IBotClient.
public class DiscordBotClient : IBotClient
{
    public string Name => "discord";

    // Singleton lifetime: resolve scoped repos per call via the scope factory.
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<DiscordBotClient> _logger;
    private DiscordSocketClient? _socket;

    public DiscordBotClient(
        IServiceScopeFactory scopes,
        ILogger<DiscordBotClient>? logger = null)
    {
        _scopes = scopes;
        _logger = logger ?? NullLogger<DiscordBotClient>.Instance;
    }

    // Set by DiscordBotHostedService after a successful login. Calling
    // SendAsync before this is wired throws, but it shouldn't happen in
    // practice — reminder dispatch only starts after the bot connects.
    internal void Bind(DiscordSocketClient socket) => _socket = socket;

    public async Task SendAsync(string userId, string message, CancellationToken ct)
    {
        if (_socket is null)
        {
            _logger.LogWarning(
                "Discord SendAsync called for user {UserId} but bot is not connected — skipping", userId);
            return;
        }

        NotificationChannel? channel;
        using (var scope = _scopes.CreateScope())
        {
            var channels = scope.ServiceProvider.GetRequiredService<INotificationChannelRepository>();
            channel = await channels.GetAsync(userId, DiscordProvider.Name, ct);
        }

        if (channel is null || !channel.Enabled)
        {
            _logger.LogDebug("No active Discord channel for user {UserId} — skipping send", userId);
            return;
        }

        if (!ulong.TryParse(channel.ChannelId, out var channelSnowflake))
        {
            _logger.LogWarning(
                "Discord channel id for user {UserId} is not a valid snowflake — refusing to send", userId);
            return;
        }

        var dm = await _socket.GetChannelAsync(channelSnowflake) as IMessageChannel
                 ?? await ResolveDmFallbackAsync(channel.ChannelId);
        if (dm is null)
        {
            _logger.LogWarning("Could not resolve Discord DM channel {ChannelId} for user {UserId}",
                channel.ChannelId, userId);
            return;
        }

        await dm.SendMessageAsync(message);
    }

    // Discord may evict cold DM channels from the gateway cache. Fall back
    // to opening the DM via the user — we stored the DM channel id, but the
    // user id (the Discord user mapped to this Fishbowl user) lives in
    // user_mappings. For MVP we don't bother re-opening because slash-command
    // first contact creates the cached DM. Returning null lets the caller log
    // and skip — better than spamming an error if the cache hasn't filled yet.
    private Task<IMessageChannel?> ResolveDmFallbackAsync(string channelId)
        => Task.FromResult<IMessageChannel?>(null);

    public IAsyncEnumerable<IncomingMessage> ReceiveAsync(CancellationToken ct)
        => AsyncEnumerable.Empty<IncomingMessage>();

    private static class AsyncEnumerable
    {
        public static IAsyncEnumerable<T> Empty<T>() => EmptyImpl<T>.Instance;

        private sealed class EmptyImpl<T> : IAsyncEnumerable<T>, IAsyncEnumerator<T>
        {
            public static readonly EmptyImpl<T> Instance = new();
            public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
            public ValueTask<bool> MoveNextAsync() => new(false);
            public T Current => default!;
            public ValueTask DisposeAsync() => default;
        }
    }
}
