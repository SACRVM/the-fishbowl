using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Core.Plugins;
using Fishbowl.Core.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Scheduler;

// Polls every minute for event reminders that have crossed their trigger
// time and routes them through the registered IBotClient plugins. Personal
// (user) contexts only for MVP — space-event reminders need a deliberate
// "who's on the to-line" design call that we don't have yet.
//
// Idempotency: each fire writes a `reminders` row with sent_at set. The
// next tick filters those out via `IReminderRepository.GetSentTriggersAsync`
// — keyed on (event_id, trigger time) so each occurrence of a recurring
// event fires exactly once. Restart-during-window or accidental
// double-tick can't double-notify, even though the dispatcher itself
// holds no in-memory "already fired" set.
//
// Catch-up: at startup the dispatcher looks at the past 6h of triggers so
// a downtime shorter than that backfills automatically. Older triggers are
// skipped via the `notAncient` guard (events older than 24h are also out).
public class ReminderDispatcher : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CatchUpWindow = TimeSpan.FromHours(6);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NotAncientWindow = TimeSpan.FromDays(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ReminderDispatcher> _logger;
    private DateTime _lastTickUtc;

    public ReminderDispatcher(
        IServiceScopeFactory scopes,
        ILogger<ReminderDispatcher>? logger = null)
    {
        _scopes = scopes;
        _logger = logger ?? NullLogger<ReminderDispatcher>.Instance;
        // First tick covers the catch-up window so a restart doesn't drop
        // notifications that became due during the downtime.
        _lastTickUtc = DateTime.UtcNow - CatchUpWindow;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Brief settle period so ConfigurationInitializer + Discord login
        // finish before we start enumerating users. Doesn't matter for
        // correctness (the bot's no-op path handles "not connected"), but
        // keeps the first-tick logs cleaner.
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        _logger.LogInformation(
            "Reminder dispatcher started — tick interval {Interval}, catch-up window {Window}",
            TickInterval, CatchUpWindow);

        while (!stoppingToken.IsCancellationRequested)
        {
            var tickStart = DateTime.UtcNow;
            try
            {
                var fired = await RunTickAsync(_lastTickUtc, tickStart, stoppingToken);
                if (fired > 0)
                    _logger.LogInformation("Reminder tick fired {Count} notifications", fired);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reminder dispatcher tick failed — will retry next interval");
            }

            _lastTickUtc = tickStart;

            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    // Visible for tests: runs one pass with explicit window endpoints so
    // assertions don't depend on wall-clock.
    public async Task<int> RunTickAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var system = sp.GetRequiredService<ISystemRepository>();
        var bots = sp.GetServices<IBotClient>().ToList();
        if (bots.Count == 0)
        {
            // No bot plugins → nothing to dispatch through. Still useful to
            // run the enumeration silently so an operator can plug in a bot
            // later without restarting; cost is ~1 SELECT per user per
            // minute which is negligible.
            return 0;
        }

        var userIds = await system.ListUserIdsAsync(ct);
        var totalFired = 0;

        foreach (var userId in userIds)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                totalFired += await ProcessUserAsync(sp, bots, userId, fromUtc, toUtc, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reminder processing failed for user {UserId}", userId);
            }
        }

        return totalFired;
    }

    private async Task<int> ProcessUserAsync(
        IServiceProvider sp, IReadOnlyList<IBotClient> bots, string userId,
        DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var events = sp.GetRequiredService<IEventRepository>();
        var reminders = sp.GetRequiredService<IReminderRepository>();
        var channels = sp.GetRequiredService<INotificationChannelRepository>();
        var ctx = ContextRef.User(userId);

        var notAncient = toUtc - NotAncientWindow;
        var due = await events.ListDueRemindersAsync(ctx, fromUtc, toUtc, notAncient, ct);
        if (due.Count == 0) return 0;

        // Latch is (event_id, trigger) — recurring events come back as one
        // entry per due occurrence under the same event_id, and each
        // occurrence deserves its own notification.
        var alreadySent = await reminders.GetSentTriggersAsync(ctx, due.Select(e => e.Id), ct);
        var toFire = due.Where(e => !alreadySent.Contains((e.Id, TriggerOf(e)))).ToList();
        if (toFire.Count == 0) return 0;

        var fired = 0;
        foreach (var ev in toFire)
        {
            if (ct.IsCancellationRequested) break;

            // Walk registered bots in registration order. First bot that has
            // an enabled channel for this user wins — we don't fan out to
            // every platform yet. (When we do, the "sent" latch needs to be
            // per (event, platform) instead of per event.)
            foreach (var bot in bots)
            {
                var channel = await channels.GetAsync(userId, bot.Name, ct);
                if (channel is null || !channel.Enabled) continue;

                var message = FormatReminderMessage(ev, toUtc);
                try
                {
                    await bot.SendAsync(userId, message, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Bot {Platform} threw while sending reminder for event {EventId} to {UserId}",
                        bot.Name, ev.Id, userId);
                    continue; // try next platform
                }

                await reminders.RecordSentAsync(ctx, new Reminder
                {
                    Id = Ulid.NewUlid().ToString(),
                    EventId = ev.Id,
                    ScheduledAt = TriggerOf(ev),
                    SentAt = toUtc,
                    ChannelType = bot.Name,
                    ChannelId = channel.ChannelId,
                }, ct);

                _logger.LogInformation(
                    "Sent reminder for event {EventId} to {UserId} via {Platform}",
                    ev.Id, userId, bot.Name);
                fired++;
                break;
            }
        }

        return fired;
    }

    // The latch identity for one occurrence. ListDueRemindersAsync returns
    // UTC-normalized StartAt values (per-occurrence for recurring events),
    // so this is deterministic across ticks and restarts.
    internal static DateTime TriggerOf(Event ev)
        => ev.StartAt.AddMinutes(-(ev.ReminderMinutes ?? 0));

    // Plain-text, deliberately short. Discord renders **bold**; other
    // platforms either render or pass through the asterisks. No PII risk —
    // titles can contain anything the user typed, but they own that data
    // and chose to set a reminder on it.
    internal static string FormatReminderMessage(Event ev, DateTime now)
    {
        var when = ev.StartAt - now;
        string whenText;
        if (when.TotalSeconds <= 30) whenText = "now";
        else if (when.TotalMinutes < 1) whenText = "in less than a minute";
        else if (when.TotalMinutes < 60) whenText = $"in {(int)when.TotalMinutes}m";
        else if (when.TotalHours < 24) whenText = $"in {(int)when.TotalHours}h{when.Minutes:D2}m";
        else whenText = $"at {ev.StartAt:yyyy-MM-dd HH:mm} UTC";

        var msg = $"Reminder: **{ev.Title}** {whenText}.";
        if (!string.IsNullOrWhiteSpace(ev.Location))
            msg += $"\nLocation: {ev.Location}";
        return msg;
    }
}
