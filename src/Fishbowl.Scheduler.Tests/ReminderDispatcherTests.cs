using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Core.Plugins;
using Fishbowl.Core.Repositories;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fishbowl.Scheduler.Tests;

// Integration-shaped: real DB, real repos, fake IBotClient. The dispatcher
// is the moving piece under test — we want to verify it dedupes correctly
// across ticks and only fires when a channel is actually available, since
// those are the rules that protect against double-notifying and silent
// drops. Discord's SendAsync is replaced by a CountingBotClient that
// records what it was asked to deliver.
public class ReminderDispatcherTests : IDisposable
{
    private readonly string _dataDir;
    private readonly ServiceProvider _services;
    private readonly DatabaseFactory _factory;
    private readonly CountingBotClient _bot;
    private const string Alice = "u_alice";

    public ReminderDispatcherTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(),
            "fishbowl_dispatcher_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);
        _factory = new DatabaseFactory(_dataDir);
        _bot = new CountingBotClient(name: "discord");

        var sc = new ServiceCollection();
        sc.AddSingleton(_factory);
        sc.AddScoped<ISystemRepository, SystemRepository>();
        sc.AddScoped<IEventRepository, EventRepository>();
        sc.AddScoped<IReminderRepository, ReminderRepository>();
        sc.AddScoped<INotificationChannelRepository, NotificationChannelRepository>();
        sc.AddSingleton<IBotClient>(_bot);
        sc.AddSingleton<ReminderDispatcher>();
        _services = sc.BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dataDir))
        {
            try { Directory.Delete(_dataDir, recursive: true); } catch { }
        }
    }

    private async Task SeedUserAsync(string id, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var system = scope.ServiceProvider.GetRequiredService<ISystemRepository>();
        await system.CreateUserAsync(id, name: id, email: $"{id}@test", avatarUrl: null, ct);
    }

    private async Task<string> SeedEventAsync(
        string userId, DateTime startAt, int? reminderMinutes, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventRepository>();
        return await events.CreateAsync(userId,
            new Event { Title = "Test event", StartAt = startAt, ReminderMinutes = reminderMinutes }, ct);
    }

    private async Task SeedDiscordChannelAsync(string userId, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var channels = scope.ServiceProvider.GetRequiredService<INotificationChannelRepository>();
        await channels.UpsertAsync(userId, "discord", "dm-channel-1", ct);
    }

    [Fact]
    public async Task HappyPath_FiresOnceAndRecordsSent()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedUserAsync(Alice, ct);
        await SeedDiscordChannelAsync(Alice, ct);

        // Tick window centered on now; event in 30 min with 30-min reminder.
        var now = DateTime.UtcNow;
        var eventId = await SeedEventAsync(Alice, now.AddMinutes(30), reminderMinutes: 30, ct);

        var dispatcher = _services.GetRequiredService<ReminderDispatcher>();
        var fired = await dispatcher.RunTickAsync(now.AddMinutes(-1), now.AddMinutes(1), ct);

        Assert.Equal(1, fired);
        Assert.Single(_bot.Calls);
        Assert.Equal(Alice, _bot.Calls[0].UserId);
        Assert.Contains("Test event", _bot.Calls[0].Message);

        // Reminder row was persisted with sent_at.
        using var scope = _services.CreateScope();
        var reminders = scope.ServiceProvider.GetRequiredService<IReminderRepository>();
        var sent = await reminders.GetSentEventIdsAsync(
            ContextRef.User(Alice), new[] { eventId }, ct);
        Assert.Contains(eventId, sent);
    }

    [Fact]
    public async Task SecondTick_DoesNotDoubleFire()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedUserAsync(Alice, ct);
        await SeedDiscordChannelAsync(Alice, ct);

        var now = DateTime.UtcNow;
        await SeedEventAsync(Alice, now.AddMinutes(30), reminderMinutes: 30, ct);

        var dispatcher = _services.GetRequiredService<ReminderDispatcher>();
        await dispatcher.RunTickAsync(now.AddMinutes(-1), now.AddMinutes(1), ct);
        var secondFired = await dispatcher.RunTickAsync(now.AddMinutes(-1), now.AddMinutes(1), ct);

        Assert.Equal(0, secondFired);
        Assert.Single(_bot.Calls); // still only the first tick's call
    }

    [Fact]
    public async Task NoChannel_DoesNotFire_AndDoesNotLatch()
    {
        // Without a notification channel, sending is impossible. The
        // dispatcher must NOT record a sent row — otherwise once the user
        // links their account later, the original reminder is silently
        // marked "delivered" and never reaches them.
        var ct = TestContext.Current.CancellationToken;
        await SeedUserAsync(Alice, ct);
        // No SeedDiscordChannelAsync — Alice hasn't linked yet.

        var now = DateTime.UtcNow;
        var eventId = await SeedEventAsync(Alice, now.AddMinutes(30), reminderMinutes: 30, ct);

        var dispatcher = _services.GetRequiredService<ReminderDispatcher>();
        var fired = await dispatcher.RunTickAsync(now.AddMinutes(-1), now.AddMinutes(1), ct);

        Assert.Equal(0, fired);
        Assert.Empty(_bot.Calls);

        // No sent row → next tick after Alice links would still fire.
        using var scope = _services.CreateScope();
        var reminders = scope.ServiceProvider.GetRequiredService<IReminderRepository>();
        var sent = await reminders.GetSentEventIdsAsync(
            ContextRef.User(Alice), new[] { eventId }, ct);
        Assert.Empty(sent);
    }

    [Fact]
    public async Task EventOutsideWindow_DoesNotFire()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedUserAsync(Alice, ct);
        await SeedDiscordChannelAsync(Alice, ct);

        // Event 1 hour out, 15-min reminder → trigger 45 minutes from now.
        // Window covers only ±1 minute — too early to fire.
        var now = DateTime.UtcNow;
        await SeedEventAsync(Alice, now.AddHours(1), reminderMinutes: 15, ct);

        var dispatcher = _services.GetRequiredService<ReminderDispatcher>();
        var fired = await dispatcher.RunTickAsync(now.AddMinutes(-1), now.AddMinutes(1), ct);

        Assert.Equal(0, fired);
        Assert.Empty(_bot.Calls);
    }

    [Fact]
    public async Task IncludesLocationInMessageWhenSet()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedUserAsync(Alice, ct);
        await SeedDiscordChannelAsync(Alice, ct);

        var now = DateTime.UtcNow;

        // Inject directly with a location since SeedEventAsync doesn't take one.
        using (var scope = _services.CreateScope())
        {
            var events = scope.ServiceProvider.GetRequiredService<IEventRepository>();
            await events.CreateAsync(Alice, new Event
            {
                Title = "Standup",
                StartAt = now.AddMinutes(30),
                ReminderMinutes = 30,
                Location = "Zoom",
            }, ct);
        }

        var dispatcher = _services.GetRequiredService<ReminderDispatcher>();
        await dispatcher.RunTickAsync(now.AddMinutes(-1), now.AddMinutes(1), ct);

        Assert.Single(_bot.Calls);
        Assert.Contains("Standup", _bot.Calls[0].Message);
        Assert.Contains("Zoom", _bot.Calls[0].Message);
    }

    private sealed class CountingBotClient : IBotClient
    {
        public CountingBotClient(string name) { Name = name; }
        public string Name { get; }
        public List<(string UserId, string Message)> Calls { get; } = new();

        public Task SendAsync(string userId, string message, CancellationToken ct)
        {
            Calls.Add((userId, message));
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<IncomingMessage> ReceiveAsync(CancellationToken ct)
            => AsyncEnumerable.Empty<IncomingMessage>();

        private static class AsyncEnumerable
        {
            public static IAsyncEnumerable<T> Empty<T>() => EmptyImpl<T>.Instance;
            private sealed class EmptyImpl<T> : IAsyncEnumerable<T>, IAsyncEnumerator<T>
            {
                public static readonly EmptyImpl<T> Instance = new();
                public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken c = default) => this;
                public ValueTask<bool> MoveNextAsync() => new(false);
                public T Current => default!;
                public ValueTask DisposeAsync() => default;
            }
        }
    }
}
