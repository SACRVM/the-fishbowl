using Fishbowl.Bot.Discord;
using Fishbowl.Bot.Discord.Commands;
using Fishbowl.Core;
using Fishbowl.Core.Models;
using Xunit;

namespace Fishbowl.Bot.Discord.Tests.Commands;

public class UpcomingCommandHandlerTests
{
    private static SlashCommandContext Ctx(Dictionary<string, string?>? options = null)
        => new("9999", "alice", "ch1", options ?? new Dictionary<string, string?>());

    [Fact]
    public async Task NotLinked_RepliesNotLinked()
    {
        using var fx = new BotTestFixture();
        var ct = TestContext.Current.CancellationToken;

        var handler = new UpcomingCommandHandler(new DiscordUserResolver(fx.System), fx.Events);
        var reply = await handler.HandleAsync(Ctx(), ct);

        Assert.Contains("don't recognise", reply.Message);
    }

    [Fact]
    public async Task NoEvents_RepliesEmptyHint()
    {
        using var fx = new BotTestFixture();
        var ct = TestContext.Current.CancellationToken;
        var alice = await fx.SeedUserAsync(ct: ct);
        await fx.System.CreateUserMappingAsync(alice, "discord", "9999", ct);

        var handler = new UpcomingCommandHandler(new DiscordUserResolver(fx.System), fx.Events);
        var reply = await handler.HandleAsync(Ctx(), ct);

        Assert.Contains("Nothing on the calendar", reply.Message);
    }

    [Fact]
    public async Task ListsEventsWithinWindow_ChronologicallyWithTimestampMarkup()
    {
        using var fx = new BotTestFixture();
        var ct = TestContext.Current.CancellationToken;
        var alice = await fx.SeedUserAsync(ct: ct);
        await fx.System.CreateUserMappingAsync(alice, "discord", "9999", ct);

        var now = DateTime.UtcNow;
        await fx.Events.CreateAsync(alice, new Event
        {
            Title = "Dentist",
            StartAt = now.AddDays(2),
            Location = "Downtown",
        }, ct);
        await fx.Events.CreateAsync(alice, new Event
        {
            Title = "Far future",
            StartAt = now.AddDays(30), // outside default 7-day window
        }, ct);

        var handler = new UpcomingCommandHandler(new DiscordUserResolver(fx.System), fx.Events);
        var reply = await handler.HandleAsync(Ctx(), ct);

        Assert.Contains("Dentist", reply.Message);
        Assert.Contains("Downtown", reply.Message);
        Assert.Contains("<t:", reply.Message); // viewer-local rendering
        Assert.DoesNotContain("Far future", reply.Message);
    }

    [Fact]
    public async Task DaysOption_WidensWindow_AndRecurringOccurrencesAppear()
    {
        using var fx = new BotTestFixture();
        var ct = TestContext.Current.CancellationToken;
        var alice = await fx.SeedUserAsync(ct: ct);
        await fx.System.CreateUserMappingAsync(alice, "discord", "9999", ct);

        // Weekly series anchored 20 days ago — only expansion can put an
        // occurrence into the next 14 days.
        var now = DateTime.UtcNow;
        await fx.Events.CreateAsync(alice, new Event
        {
            Title = "Weekly sync",
            StartAt = now.AddDays(-20),
            RRule = "FREQ=WEEKLY",
        }, ct);

        var handler = new UpcomingCommandHandler(new DiscordUserResolver(fx.System), fx.Events);
        var reply = await handler.HandleAsync(
            Ctx(new Dictionary<string, string?> { ["days"] = "14" }), ct);

        Assert.Contains("Weekly sync", reply.Message);
        Assert.Contains("14 days", reply.Message);
        Assert.Contains("↻", reply.Message); // ↻ repeat marker
    }
}
