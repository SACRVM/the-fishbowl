using System.Net;
using global::Discord;
using global::Discord.Net;
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
    internal void Bind(DiscordSocketClient? socket) => _socket = socket;

    public async Task SendAsync(string userId, string message, CancellationToken ct)
    {
        if (_socket is null)
        {
            _logger.LogWarning(
                "Discord SendAsync called for user {UserId} but bot is not connected", userId);
            throw new BotDeliveryException("Discord isn't connected.");
        }

        using var scope = _scopes.CreateScope();
        var channels = scope.ServiceProvider.GetRequiredService<INotificationChannelRepository>();
        var channel = await channels.GetAsync(userId, DiscordProvider.Name, ct);

        if (channel is null || !channel.Enabled)
        {
            _logger.LogDebug("No active Discord channel for user {UserId}", userId);
            throw new BotDeliveryException("No Discord channel is linked for this user.");
        }

        var text = DiscordText.Fit(message);
        ulong? refusedId = null;
        if (ulong.TryParse(channel.ChannelId, out var channelSnowflake))
        {
            // A stored channel the bot can't reach (403/404 — e.g. a DM with a
            // friend that an older /link stored) isn't the end: the fallback
            // below opens the bot's own DM and re-caches it.
            try
            {
                if (await _socket.GetChannelAsync(channelSnowflake) is IMessageChannel stored)
                {
                    await stored.SendMessageAsync(text);
                    return;
                }
            }
            catch (HttpException ex) when (IsUnreachable(ex))
            {
                _logger.LogInformation(
                    "Stored Discord channel for user {UserId} refused ({Status}) — reopening the DM",
                    userId, ex.HttpCode);
                refusedId = channelSnowflake;
            }
        }
        else
        {
            _logger.LogWarning(
                "Discord channel id for user {UserId} is not a valid snowflake — will try DM re-open", userId);
        }

        var dm = await ResolveDmFallbackAsync(userId, channels, scope.ServiceProvider, ct);
        if (dm is null || dm.Id == refusedId)
        {
            // The bot's DM is the channel that just refused: trying again changes nothing.
            _logger.LogWarning("Could not resolve Discord DM channel for user {UserId}", userId);
            throw new BotDeliveryException("The Discord DM couldn't be opened.");
        }

        await dm.SendMessageAsync(text);
    }

    private static bool IsUnreachable(HttpException ex)
        => ex.HttpCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound;

    // Discord evicts cold DM channels from the gateway cache surprisingly
    // often. Fallback path: take the user's mapped Discord user id from
    // user_mappings, ask the socket for that user, and open a fresh DM. The
    // returned channel id is then upserted back into notification_channels so
    // subsequent sends hit the cache directly. Failing this fallback is
    // logged and skipped — a stuck reminder is preferable to spamming the
    // gateway with retries when the user's Discord account is unreachable.
    private async Task<IMessageChannel?> ResolveDmFallbackAsync(
        string userId,
        INotificationChannelRepository channels,
        IServiceProvider scopedServices,
        CancellationToken ct)
    {
        if (_socket is null) return null;

        var system = scopedServices.GetService<ISystemRepository>();
        if (system is null) return null;

        var discordUserIdStr = await system.GetProviderIdForUserAsync(userId, DiscordProvider.Name, ct);
        if (string.IsNullOrEmpty(discordUserIdStr))
        {
            _logger.LogDebug("No discord user mapping for {UserId} — cannot reopen DM", userId);
            return null;
        }

        if (!ulong.TryParse(discordUserIdStr, out var discordUserId))
        {
            _logger.LogWarning("Mapped discord user id for {UserId} is not numeric", userId);
            return null;
        }

        IUser? user = _socket.GetUser(discordUserId);
        if (user is null)
        {
            // The socket might just not have seen this user yet on a cold
            // connect. Discord.Net's REST client hits the API and returns
            // a usable IUser; trying both forms covers the gap.
            try
            {
                user = await _socket.Rest.GetUserAsync(discordUserId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "REST GetUser fallback failed for {UserId}", userId);
            }
        }

        if (user is null)
        {
            _logger.LogInformation(
                "Discord user not reachable for Fishbowl user {UserId} (gateway+REST both missed)", userId);
            return null;
        }

        IDMChannel dmChannel;
        try
        {
            dmChannel = await user.CreateDMChannelAsync();
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex,
                "CreateDMChannelAsync failed for {UserId} — user may have DMs disabled", userId);
            return null;
        }

        // Refresh the cached channel id so the next send hits the fast path.
        try
        {
            await channels.UpsertAsync(userId, DiscordProvider.Name, dmChannel.Id.ToString(), ct);
        }
        catch (Exception ex)
        {
            // Non-fatal — sending the message still works, we just rebuild
            // the same channel id on the next send.
            _logger.LogDebug(ex, "Failed to refresh cached DM channel for {UserId}", userId);
        }

        return dmChannel;
    }

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
