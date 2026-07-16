using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Core.Plugins;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Util;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Scheduler;

// Sends one "here's your day" DM per user per day: today's events (recurring
// occurrences included — GetRangeAsync expands them) plus due/overdue todos.
// Personal contexts only, same recipient rules as ReminderDispatcher.
//
// Opt-in via system config (system.db):
//   Digest:Enabled — "true" to enable; anything else (or absent) = off.
//   Digest:Hour    — server-local hour 0–23 to send at (default 7).
//
// Idempotency: after a successful send (or an intentionally skipped empty
// day) the per-user config key `Digest:LastSent:<userId>` is stamped with
// the local date, so restarts don't double-post and downtime across the
// send slot still catches up later the same day. When no notification
// channel exists the latch is NOT written — a user who links Discord at
// noon still gets that day's digest on the next tick.
public class DailyDigestDispatcher : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);

    private const string EnabledKey = "Digest:Enabled";
    private const string HourKey = "Digest:Hour";
    private const int DefaultHour = 7;

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<DailyDigestDispatcher> _logger;

    public DailyDigestDispatcher(
        IServiceScopeFactory scopes,
        ILogger<DailyDigestDispatcher>? logger = null)
    {
        _scopes = scopes;
        _logger = logger ?? NullLogger<DailyDigestDispatcher>.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        _logger.LogInformation("Daily digest dispatcher started — tick interval {Interval}", TickInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var sent = await RunTickAsync(DateTime.Now, stoppingToken);
                if (sent > 0)
                    _logger.LogInformation("Daily digest tick sent {Count} digests", sent);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Daily digest tick failed — will retry next interval");
            }

            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    // Visible for tests: `nowLocal` is the server-local clock so assertions
    // don't depend on wall time. Returns the number of digests delivered.
    public async Task<int> RunTickAsync(DateTime nowLocal, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var system = sp.GetRequiredService<ISystemRepository>();
        var bots = sp.GetServices<IBotClient>().ToList();
        if (bots.Count == 0) return 0;

        if (!string.Equals(await system.GetConfigAsync(EnabledKey, ct), "true", StringComparison.OrdinalIgnoreCase))
            return 0;

        var hour = DefaultHour;
        if (int.TryParse(await system.GetConfigAsync(HourKey, ct), out var parsed))
            hour = Math.Clamp(parsed, 0, 23);
        if (nowLocal < nowLocal.Date.AddHours(hour))
            return 0; // slot not reached yet today

        var todayStamp = nowLocal.ToString("yyyy-MM-dd");
        var sent = 0;
        foreach (var userId in await system.ListUserIdsAsync(ct))
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var latchKey = $"Digest:LastSent:{userId}";
                if (await system.GetConfigAsync(latchKey, ct) == todayStamp) continue;

                var delivered = await TrySendDigestAsync(sp, bots, userId, nowLocal, ct);
                if (delivered is null) continue; // no channel — retry next tick, no latch

                await system.SetConfigAsync(latchKey, todayStamp, ct);
                if (delivered.Value) sent++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Daily digest failed for user {UserId}", userId);
            }
        }

        return sent;
    }

    // null → no enabled channel (don't latch); false → nothing to say today
    // (latch, but nothing was sent); true → digest delivered.
    private async Task<bool?> TrySendDigestAsync(
        IServiceProvider sp, IReadOnlyList<IBotClient> bots, string userId,
        DateTime nowLocal, CancellationToken ct)
    {
        var channels = sp.GetRequiredService<INotificationChannelRepository>();

        IBotClient? target = null;
        foreach (var bot in bots)
        {
            var channel = await channels.GetAsync(userId, bot.Name, ct);
            if (channel is not null && channel.Enabled) { target = bot; break; }
        }
        if (target is null) return null;

        var ctx = ContextRef.User(userId);
        var dayStartUtc = DateTime.SpecifyKind(nowLocal.Date, DateTimeKind.Local).ToUniversalTime();
        var dayEndUtc = dayStartUtc.AddDays(1);

        var events = sp.GetRequiredService<IEventRepository>();
        var todos = sp.GetRequiredService<ITodoRepository>();

        var todaysEvents = (await events.GetRangeAsync(ctx, dayStartUtc, dayEndUtc, ct)).ToList();
        var dueTodos = (await todos.GetAllAsync(ctx, includeCompleted: false, ct))
            .Where(t => t.DueAt is DateTime due && TimeUtil.AsUtc(due) < dayEndUtc)
            .OrderBy(t => t.DueAt)
            .ToList();

        if (todaysEvents.Count == 0 && dueTodos.Count == 0)
            return false; // an empty digest is noise — stay quiet, latch the day

        var message = FormatDigestMessage(todaysEvents, dueTodos, dayStartUtc);
        try
        {
            await target.SendAsync(userId, message, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Bot {Platform} threw while sending daily digest to {UserId}", target.Name, userId);
            return null; // treat like "no channel" — retry next tick
        }

        _logger.LogInformation("Sent daily digest to {UserId} via {Platform}", userId, target.Name);
        return true;
    }

    // <t:…> markup renders in the viewer's local timezone on Discord; other
    // platforms see the raw token, which is still unambiguous. Titles only
    // plus time/location — same content minimalism as the bot commands.
    internal static string FormatDigestMessage(
        IReadOnlyList<Event> events, IReadOnlyList<TodoItem> todos, DateTime dayStartUtc)
    {
        var lines = new List<string> { "**Good morning — here's your day.**" };

        if (events.Count > 0)
        {
            lines.Add("");
            lines.Add("📅 **Today:**");
            foreach (var ev in events)
            {
                var unix = new DateTimeOffset(TimeUtil.AsUtc(ev.StartAt)).ToUnixTimeSeconds();
                var when = ev.AllDay ? "all day" : $"<t:{unix}:t>";
                var location = string.IsNullOrWhiteSpace(ev.Location) ? "" : $" — {ev.Location}";
                lines.Add($"• {when}  **{ev.Title}**{location}");
            }
        }

        if (todos.Count > 0)
        {
            lines.Add("");
            lines.Add("✅ **Due todos:**");
            foreach (var todo in todos)
            {
                var overdue = todo.DueAt is DateTime due && TimeUtil.AsUtc(due) < dayStartUtc
                    ? " (overdue)"
                    : "";
                lines.Add($"• **{todo.Title}**{overdue}");
            }
        }

        return string.Join("\n", lines);
    }
}
