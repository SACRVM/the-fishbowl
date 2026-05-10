using Fishbowl.Bot.Discord;
using Fishbowl.Bot.Discord.Commands;
using Xunit;

namespace Fishbowl.Bot.Discord.Tests.Commands;

public class LinkCommandHandlerTests
{
    [Fact]
    public async Task HappyPath_RedeemsCode_AndCreatesMappingAndChannel()
    {
        using var fx = new BotTestFixture();
        var ct = TestContext.Current.CancellationToken;
        var alice = await fx.SeedUserAsync(ct: ct);
        var (code, _) = await fx.Links.CreateCodeAsync(alice, ct);

        var handler = new LinkCommandHandler(fx.Links, fx.System, fx.Channels);
        var ctx = new SlashCommandContext(
            DiscordUserId: "9999",
            DiscordUsername: "alice#0",
            DiscordChannelId: "1234",
            Options: new Dictionary<string, string?> { ["code"] = code });

        var reply = await handler.HandleAsync(ctx, ct);

        Assert.Contains("Linked", reply.Message);

        // Mapping was written so future commands can resolve discord→fishbowl.
        var mapped = await fx.System.GetUserIdByMappingAsync("discord", "9999", ct);
        Assert.Equal(alice, mapped);

        // Notification channel was upserted with the DM channel id.
        var channel = await fx.Channels.GetAsync(alice, "discord", ct);
        Assert.NotNull(channel);
        Assert.Equal("1234", channel!.ChannelId);
        Assert.True(channel.Enabled);
    }

    [Fact]
    public async Task BadCode_RepliesGenericFailure()
    {
        using var fx = new BotTestFixture();
        var ct = TestContext.Current.CancellationToken;

        var handler = new LinkCommandHandler(fx.Links, fx.System, fx.Channels);
        var ctx = new SlashCommandContext(
            "9999", "alice", "1234",
            new Dictionary<string, string?> { ["code"] = "FAKECODE" });

        var reply = await handler.HandleAsync(ctx, ct);

        Assert.Contains("didn't work", reply.Message);
        Assert.Null(await fx.System.GetUserIdByMappingAsync("discord", "9999", ct));
    }

    [Fact]
    public async Task ExpiredCode_RepliesGenericFailure_NotDifferentiated()
    {
        using var fx = new BotTestFixture();
        var ct = TestContext.Current.CancellationToken;
        var alice = await fx.SeedUserAsync(ct: ct);
        var (code, _) = await fx.Links.CreateCodeAsync(alice, ct);

        // Force the row to "expired" past.
        using (var db = fx.Factory.CreateSystemConnection())
        {
            Dapper.SqlMapper.Execute(db,
                "UPDATE discord_link_codes SET expires_at = @past WHERE code = @code",
                new { past = DateTime.UtcNow.AddMinutes(-1).ToString("o"), code });
        }

        var handler = new LinkCommandHandler(fx.Links, fx.System, fx.Channels);
        var ctx = new SlashCommandContext("9999", "alice", "1234",
            new Dictionary<string, string?> { ["code"] = code });

        var reply = await handler.HandleAsync(ctx, ct);

        // Same wording as bad code — we deliberately don't differentiate so
        // someone shoulder-surfing can't enumerate.
        Assert.Contains("didn't work", reply.Message);
    }

    [Fact]
    public async Task AlreadyLinkedDiscordAccount_RejectsRelink()
    {
        using var fx = new BotTestFixture();
        var ct = TestContext.Current.CancellationToken;
        var alice = await fx.SeedUserAsync(ct: ct);
        await fx.System.CreateUserMappingAsync(alice, "discord", "9999", ct);

        var (code, _) = await fx.Links.CreateCodeAsync(alice, ct);

        var handler = new LinkCommandHandler(fx.Links, fx.System, fx.Channels);
        var ctx = new SlashCommandContext("9999", "alice", "1234",
            new Dictionary<string, string?> { ["code"] = code });

        var reply = await handler.HandleAsync(ctx, ct);

        Assert.Contains("already linked", reply.Message, StringComparison.OrdinalIgnoreCase);
        // The code must NOT have been redeemed — still usable for the actual flow.
        Assert.NotNull(await fx.Links.RedeemAsync(code, ct));
    }

    [Fact]
    public async Task MissingCodeOption_RepliesUsageHint()
    {
        using var fx = new BotTestFixture();
        var ct = TestContext.Current.CancellationToken;

        var handler = new LinkCommandHandler(fx.Links, fx.System, fx.Channels);
        var ctx = new SlashCommandContext("9999", "alice", "1234",
            new Dictionary<string, string?>());

        var reply = await handler.HandleAsync(ctx, ct);
        Assert.Contains("link code", reply.Message);
    }
}
