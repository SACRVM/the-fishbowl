using Fishbowl.Bot.Discord;
using Fishbowl.Bot.Discord.Commands;
using Xunit;

namespace Fishbowl.Bot.Discord.Tests.Commands;

public class UnlinkCommandHandlerTests
{
    [Fact]
    public async Task HappyPath_RemovesMapping_AndDisablesChannel()
    {
        using var fx = new BotTestFixture();
        var ct = TestContext.Current.CancellationToken;

        var alice = await fx.SeedUserAsync(ct: ct);
        await fx.System.CreateUserMappingAsync(alice, "discord", "9999", ct);
        await fx.Channels.UpsertAsync(alice, "discord", "dm-channel-1", ct);

        var handler = new UnlinkCommandHandler(fx.System, fx.Channels);
        var ctx = new SlashCommandContext(
            DiscordUserId: "9999",
            DiscordUsername: "alice",
            DiscordChannelId: "dm-channel-1",
            Options: new Dictionary<string, string?>());

        var reply = await handler.HandleAsync(ctx, ct);

        Assert.Contains("Unlinked", reply.Message);
        Assert.Null(await fx.System.GetUserIdByMappingAsync("discord", "9999", ct));
        Assert.Null(await fx.Channels.GetAsync(alice, "discord", ct));
    }

    [Fact]
    public async Task NotLinked_RepliesPolitely_NoStateChange()
    {
        using var fx = new BotTestFixture();
        var ct = TestContext.Current.CancellationToken;

        var handler = new UnlinkCommandHandler(fx.System, fx.Channels);
        var ctx = new SlashCommandContext("9999", "stranger", "dm-1",
            new Dictionary<string, string?>());

        var reply = await handler.HandleAsync(ctx, ct);

        // The unlinked case isn't an error — the user might be confused or
        // just exploring. Reply should be friendly, not accusatory.
        Assert.Contains("isn't linked", reply.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await fx.System.GetUserIdByMappingAsync("discord", "9999", ct));
    }

    [Fact]
    public async Task DoesNotAffectOtherUsersMappings()
    {
        using var fx = new BotTestFixture();
        var ct = TestContext.Current.CancellationToken;

        var alice = await fx.SeedUserAsync("u_alice", ct);
        var bob = await fx.SeedUserAsync("u_bob", ct);
        await fx.System.CreateUserMappingAsync(alice, "discord", "9999", ct);
        await fx.System.CreateUserMappingAsync(bob, "discord", "1111", ct);
        await fx.Channels.UpsertAsync(alice, "discord", "dm-a", ct);
        await fx.Channels.UpsertAsync(bob, "discord", "dm-b", ct);

        var handler = new UnlinkCommandHandler(fx.System, fx.Channels);
        var ctx = new SlashCommandContext("9999", "alice", "dm-a", new Dictionary<string, string?>());
        await handler.HandleAsync(ctx, ct);

        Assert.Null(await fx.System.GetUserIdByMappingAsync("discord", "9999", ct));
        // Bob's mapping and channel must survive Alice's unlink.
        Assert.Equal(bob, await fx.System.GetUserIdByMappingAsync("discord", "1111", ct));
        Assert.NotNull(await fx.Channels.GetAsync(bob, "discord", ct));
    }

    [Fact]
    public async Task RunTwice_SecondCallReportsAlreadyUnlinked()
    {
        using var fx = new BotTestFixture();
        var ct = TestContext.Current.CancellationToken;

        var alice = await fx.SeedUserAsync(ct: ct);
        await fx.System.CreateUserMappingAsync(alice, "discord", "9999", ct);

        var handler = new UnlinkCommandHandler(fx.System, fx.Channels);
        var ctx = new SlashCommandContext("9999", "alice", "dm-1", new Dictionary<string, string?>());

        var first = await handler.HandleAsync(ctx, ct);
        var second = await handler.HandleAsync(ctx, ct);

        Assert.Contains("Unlinked", first.Message);
        Assert.Contains("isn't linked", second.Message, StringComparison.OrdinalIgnoreCase);
    }
}
