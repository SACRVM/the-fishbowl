using global::Discord;
using global::Discord.WebSocket;
using Fishbowl.Bot.Discord.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fishbowl.Bot.Discord;

// Owns the DiscordSocketClient for the host's lifetime. No-ops when the
// bot token is unset (fresh install before the operator configures Discord)
// so the rest of the host boots normally. On Ready the service registers
// slash commands globally and starts dispatching invocations to the
// SlashCommandRouter — one DI scope per invocation so scoped repositories
// resolve correctly.
public class DiscordBotHostedService : IHostedService, IAsyncDisposable
{
    // IOptionsMonitor not IOptions: the options callback reads
    // ConfigurationCache, which ConfigurationInitializer (also a hosted
    // service) populates in its own StartAsync. By the time we reach
    // StartAsync below the cache is fresh, but resolving IOptions.Value
    // in the ctor would freeze a stale (empty) snapshot. Monitor.CurrentValue
    // re-evaluates on each call.
    private readonly IOptionsMonitor<DiscordBotOptions> _options;
    private readonly DiscordBotClient _bot;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<DiscordBotHostedService> _logger;
    private DiscordSocketClient? _client;
    private bool _commandsRegistered;

    public DiscordBotHostedService(
        IOptionsMonitor<DiscordBotOptions> options,
        DiscordBotClient bot,
        IServiceScopeFactory scopes,
        ILogger<DiscordBotHostedService>? logger = null)
    {
        _options = options;
        _bot = bot;
        _scopes = scopes;
        _logger = logger ?? NullLogger<DiscordBotHostedService>.Instance;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var token = _options.CurrentValue.Token;
        if (string.IsNullOrWhiteSpace(token) ||
            string.Equals(token, "placeholder", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Discord bot token not configured — Discord integration disabled. " +
                "Set Discord:BotToken in system_config to enable.");
            return;
        }

        var config = new DiscordSocketConfig
        {
            // Slash commands need no privileged intents at all. We'd add
            // GatewayIntents.Guilds if we ever needed guild metadata, but the
            // user-install + DM-only model doesn't.
            GatewayIntents = GatewayIntents.None,
            LogLevel = LogSeverity.Info,
            AlwaysDownloadUsers = false,
            MessageCacheSize = 0,
        };

        _client = new DiscordSocketClient(config);
        _client.Log += OnDiscordLogAsync;
        _client.Ready += OnReadyAsync;
        _client.SlashCommandExecuted += OnSlashCommandAsync;

        _bot.Bind(_client);

        // Best-effort prune so the table doesn't grow on a cold deploy. Done
        // before login so a slow Discord handshake doesn't gate it.
        try
        {
            using var scope = _scopes.CreateScope();
            var links = scope.ServiceProvider.GetService<Fishbowl.Core.Repositories.IDiscordLinkRepository>();
            if (links is not null)
                await links.PruneExpiredAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to prune Discord link codes on startup (non-fatal)");
        }

        try
        {
            await _client.LoginAsync(TokenType.Bot, token);
            await _client.StartAsync();
            _logger.LogInformation("Discord bot logging in...");
        }
        catch (Exception ex)
        {
            // Bad token is a common first-install state. Log loudly but don't
            // crash the host — operator can fix Discord:BotToken and restart.
            _logger.LogError(ex, "Discord bot failed to start. Check Discord:BotToken in system_config.");
            await DisposeClientAsync();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await DisposeClientAsync();
    }

    public ValueTask DisposeAsync() => new(DisposeClientAsync());

    private async Task OnReadyAsync()
    {
        if (_client is null) return;

        // Idempotent. Discord caches Ready emissions on reconnect; we only
        // overwrite the global command set on the first one to avoid wasting
        // an upsert per gateway re-handshake.
        if (_commandsRegistered) return;

        try
        {
            using var scope = _scopes.CreateScope();
            var handlers = scope.ServiceProvider.GetServices<ISlashCommandHandler>().ToList();
            var commands = handlers.Select(h => h.Build()).ToArray();

            await _client.BulkOverwriteGlobalApplicationCommandsAsync(commands);
            _commandsRegistered = true;
            _logger.LogInformation(
                "Registered {Count} Discord slash commands: {Names}",
                commands.Length,
                string.Join(", ", handlers.Select(h => "/" + h.Name)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register Discord slash commands");
        }
    }

    private async Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        // DM-only invariant. User-installable apps can be invoked in guilds
        // too; we hard-reject everything outside a DM/private channel
        // regardless of the command's declared contexts.
        if (command.Channel is not (IDMChannel or IGroupChannel))
        {
            await SafeRespondAsync(command, "I only work in DMs.", ephemeral: true);
            return;
        }

        var options = command.Data.Options
            .ToDictionary(
                o => o.Name,
                o => o.Value?.ToString(),
                StringComparer.OrdinalIgnoreCase);

        var ctx = new SlashCommandContext(
            DiscordUserId: command.User.Id.ToString(),
            DiscordUsername: command.User.Username,
            DiscordChannelId: command.Channel.Id.ToString(),
            Options: options);

        // Defer immediately so we don't trip the 3-second interaction
        // deadline on slow paths (search hits the embedding model).
        try
        {
            await command.DeferAsync(ephemeral: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to defer Discord interaction {Name}", command.Data.Name);
        }

        try
        {
            using var scope = _scopes.CreateScope();
            var handlers = scope.ServiceProvider.GetServices<ISlashCommandHandler>();
            var loggerFactory = scope.ServiceProvider.GetService<ILoggerFactory>();
            var routerLogger = loggerFactory?.CreateLogger<SlashCommandRouter>();
            var router = new SlashCommandRouter(handlers, routerLogger);

            var reply = await router.DispatchAsync(command.Data.Name, ctx, CancellationToken.None);
            await command.FollowupAsync(reply.Message, ephemeral: reply.Ephemeral);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Discord slash command {Name} threw", command.Data.Name);
            await SafeFollowupAsync(command, "Something went wrong on my side. Try again.", ephemeral: true);
        }
    }

    private static async Task SafeRespondAsync(SocketSlashCommand command, string text, bool ephemeral)
    {
        try
        {
            await command.RespondAsync(text, ephemeral: ephemeral);
        }
        catch
        {
            // If the interaction was already responded/deferred, retry as followup.
            try { await command.FollowupAsync(text, ephemeral: ephemeral); }
            catch { /* swallow — at this point the user just sees nothing */ }
        }
    }

    private static async Task SafeFollowupAsync(SocketSlashCommand command, string text, bool ephemeral)
    {
        try { await command.FollowupAsync(text, ephemeral: ephemeral); }
        catch { /* swallow */ }
    }

    private Task OnDiscordLogAsync(LogMessage msg)
    {
        var level = msg.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Debug,
            LogSeverity.Debug => LogLevel.Trace,
            _ => LogLevel.Information,
        };
        _logger.Log(level, msg.Exception, "[Discord] {Source}: {Message}", msg.Source, msg.Message);
        return Task.CompletedTask;
    }

    private async Task DisposeClientAsync()
    {
        if (_client is null) return;
        try { await _client.LogoutAsync(); } catch { /* shutting down */ }
        try { await _client.StopAsync(); } catch { /* shutting down */ }
        await _client.DisposeAsync();
        _client = null;
    }
}
