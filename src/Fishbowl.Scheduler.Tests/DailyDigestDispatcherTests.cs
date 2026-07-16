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

// Integration-shaped like ReminderDispatcherTests: real DB, real repos,
// fake IBotClient. The rules under test are the opt-in gate, the send-slot
// hour, the once-per-day latch, and the "no channel → no latch" retry
// semantics.
public class DailyDigestDispatcherTests : IDisposable
{
    private readonly string _dataDir;
    private readonly ServiceProvider _services;
    private readonly DatabaseFactory _factory;
    private readonly CountingBotClient _bot;
    private const string Alice = "u_alice";

    public DailyDigestDispatcherTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(),
            "fishbowl_digest_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);
        _factory = new DatabaseFactory(_dataDir);
        _bot = new CountingBotClient(name: "discord");

        var sc = new ServiceCollection();
        sc.AddSingleton(_factory);
        sc.AddScoped<ISystemRepository, SystemRepository>();
        sc.AddScoped<IEventRepository, EventRepository>();
        sc.AddScoped<ITodoRepository, TodoRepository>();
        sc.AddScoped<INotificationChannelRepository, NotificationChannelRepository>();
        sc.AddSingleton<IBotClient>(_bot);
        sc.AddSingleton<DailyDigestDispatcher>();
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

    private async Task SeedUserAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var system = scope.ServiceProvider.GetRequiredService<ISystemRepository>();
        await system.CreateUserAsync(Alice, name: Alice, email: $"{Alice}@test", avatarUrl: null, ct);
    }

    private async Task SeedChannelAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var channels = scope.ServiceProvider.GetRequiredService<INotificationChannelRepository>();
        await channels.UpsertAsync(Alice, "discord", "dm-channel-1", ct);
    }

    private async Task SetConfigAsync(string key, string value, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var system = scope.ServiceProvider.GetRequiredService<ISystemRepository>();
        await system.SetConfigAsync(key, value, ct);
    }

    private async Task SeedTodayEventAsync(string title, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventRepository>();
        await events.CreateAsync(Alice, new Event
        {
            Title = title,
            StartAt = DateTime.UtcNow, // "now" is always inside today's local day window
        }, ct);
    }

    [Fact]
    public async Task DisabledByDefault_SendsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedUserAsync(ct);
        await SeedChannelAsync(ct);
        await SeedTodayEventAsync("Standup", ct);

        var dispatcher = _services.GetRequiredService<DailyDigestDispatcher>();
        var sent = await dispatcher.RunTickAsync(DateTime.Now, ct);

        Assert.Equal(0, sent);
        Assert.Empty(_bot.Calls);
    }

    [Fact]
    public async Task Enabled_BeforeSendHour_SendsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedUserAsync(ct);
        await SeedChannelAsync(ct);
        await SeedTodayEventAsync("Standup", ct);
        await SetConfigAsync("Digest:Enabled", "true", ct);
        await SetConfigAsync("Digest:Hour", "23", ct);

        var dispatcher = _services.GetRequiredService<DailyDigestDispatcher>();
        var beforeSlot = DateTime.Now.Date.AddHours(22);
        var sent = await dispatcher.RunTickAsync(beforeSlot, ct);

        Assert.Equal(0, sent);
        Assert.Empty(_bot.Calls);
    }

    [Fact]
    public async Task Enabled_AfterSendHour_SendsOnce_AndLatchesForTheDay()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedUserAsync(ct);
        await SeedChannelAsync(ct);
        await SeedTodayEventAsync("Standup", ct);
        await SetConfigAsync("Digest:Enabled", "true", ct);
        await SetConfigAsync("Digest:Hour", "0", ct);

        var dispatcher = _services.GetRequiredService<DailyDigestDispatcher>();
        var first = await dispatcher.RunTickAsync(DateTime.Now, ct);
        var second = await dispatcher.RunTickAsync(DateTime.Now, ct);

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        Assert.Single(_bot.Calls);
        Assert.Equal(Alice, _bot.Calls[0].UserId);
        Assert.Contains("Standup", _bot.Calls[0].Message);
    }

    [Fact]
    public async Task IncludesOverdueTodos_AndMarksThem()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedUserAsync(ct);
        await SeedChannelAsync(ct);
        await SetConfigAsync("Digest:Enabled", "true", ct);
        await SetConfigAsync("Digest:Hour", "0", ct);

        using (var scope = _services.CreateScope())
        {
            var todos = scope.ServiceProvider.GetRequiredService<ITodoRepository>();
            await todos.CreateAsync(Alice, new TodoItem
            {
                Title = "Pay rent",
                DueAt = DateTime.UtcNow.AddDays(-2),
            }, ct);
        }

        var dispatcher = _services.GetRequiredService<DailyDigestDispatcher>();
        var sent = await dispatcher.RunTickAsync(DateTime.Now, ct);

        Assert.Equal(1, sent);
        Assert.Contains("Pay rent", _bot.Calls[0].Message);
        Assert.Contains("overdue", _bot.Calls[0].Message);
    }

    [Fact]
    public async Task NoChannel_DoesNotLatch_SoLinkingLaterStillDelivers()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedUserAsync(ct);
        await SeedTodayEventAsync("Standup", ct);
        await SetConfigAsync("Digest:Enabled", "true", ct);
        await SetConfigAsync("Digest:Hour", "0", ct);

        var dispatcher = _services.GetRequiredService<DailyDigestDispatcher>();
        var withoutChannel = await dispatcher.RunTickAsync(DateTime.Now, ct);
        Assert.Equal(0, withoutChannel);
        Assert.Empty(_bot.Calls);

        // User links Discord mid-day → same-day digest still arrives.
        await SeedChannelAsync(ct);
        var afterLinking = await dispatcher.RunTickAsync(DateTime.Now, ct);
        Assert.Equal(1, afterLinking);
        Assert.Single(_bot.Calls);
    }

    [Fact]
    public async Task EmptyDay_StaysQuiet_ButLatches()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedUserAsync(ct);
        await SeedChannelAsync(ct);
        await SetConfigAsync("Digest:Enabled", "true", ct);
        await SetConfigAsync("Digest:Hour", "0", ct);

        var dispatcher = _services.GetRequiredService<DailyDigestDispatcher>();
        var sent = await dispatcher.RunTickAsync(DateTime.Now, ct);

        // Nothing to say → no message, and the day is latched so we don't
        // re-evaluate every minute.
        Assert.Equal(0, sent);
        Assert.Empty(_bot.Calls);

        await SeedTodayEventAsync("Added later", ct);
        var again = await dispatcher.RunTickAsync(DateTime.Now, ct);
        Assert.Equal(0, again);
        Assert.Empty(_bot.Calls);
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
            => Empty<IncomingMessage>();

        private static IAsyncEnumerable<T> Empty<T>() => EmptyImpl<T>.Instance;

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
