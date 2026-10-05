using Dapper;
using Fishbowl.Bot.Discord;
using Fishbowl.Bot.Discord.Commands;
using Fishbowl.Core;
using Fishbowl.Core.Util;
using Xunit;

namespace Fishbowl.Bot.Discord.Tests.Commands;

// The bot acts only for active accounts (a disabled/blocked one keeps its
// mapping), stores the bot's own DM as the reminder channel, and keeps one
// Discord account per Fishbowl user.
public class AccountStateAndRelinkTests
{
    private static SlashCommandContext Ctx(string discordId, Dictionary<string, string?>? options = null,
        string channelId = "1234", Func<CancellationToken, Task<string>>? openBotDm = null)
        => new(discordId, "someone", channelId, options ?? new Dictionary<string, string?>(), openBotDm);

    private static void SetState(BotTestFixture fx, string userId, string state)
    {
        using var db = fx.Factory.CreateSystemConnection();
        db.Execute("UPDATE users SET state = @state WHERE id = @userId", new { state, userId });
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("blocked")]
    public async Task InactiveAccount_CommandsRefuse_AndWriteNothing(string state)
    {
        using var fx = new BotTestFixture();
        var ct = TestContext.Current.CancellationToken;
        var alice = await fx.SeedUserAsync(ct: ct);
        await fx.System.CreateUserMappingAsync(alice, DiscordProvider.Name, "9999", ct);
        SetState(fx, alice, state);
        var resolver = new DiscordUserResolver(fx.System);
        var inactive = ChatText.Get("bot.inactive", null);

        var remember = await new RememberCommandHandler(resolver, fx.Notes).HandleAsync(
            Ctx("9999", new() { ["text"] = "secret plans" }), ct);
        var recent = await new RecentCommandHandler(resolver, fx.Notes).HandleAsync(Ctx("9999"), ct);
        var upcoming = await new UpcomingCommandHandler(resolver, fx.Events).HandleAsync(Ctx("9999"), ct);

        Assert.Equal(inactive, remember.Message);
        Assert.Equal(inactive, recent.Message);
        Assert.Equal(inactive, upcoming.Message);

        // Back to active (so the context DB opens) — nothing was written meanwhile.
        SetState(fx, alice, "active");
        Assert.Empty(await fx.Notes.GetAllAsync(ContextRef.User(alice), null, "any", 10, 0, ct));
    }

    [Fact]
    public async Task Link_CodeOfAnAccountDisabledSince_LinksNothing()
    {
        using var fx = new BotTestFixture();
        var ct = TestContext.Current.CancellationToken;
        var alice = await fx.SeedUserAsync(ct: ct);
        var (code, _) = await fx.Links.CreateCodeAsync(alice, ct);
        SetState(fx, alice, "disabled");

        var reply = await new LinkCommandHandler(fx.Links, fx.System, fx.Channels)
            .HandleAsync(Ctx("9999", new() { ["code"] = code }), ct);

        Assert.Equal(ChatText.Get("bot.inactive", null), reply.Message);
        Assert.Null(await fx.System.GetUserIdByMappingAsync(DiscordProvider.Name, "9999", ct));
        Assert.Null(await fx.Channels.GetAsync(alice, DiscordProvider.Name, ct));
    }

    [Fact]
    public async Task Link_FromADmWithAFriend_StoresTheBotsOwnDm()
    {
        using var fx = new BotTestFixture();
        var ct = TestContext.Current.CancellationToken;
        var alice = await fx.SeedUserAsync(ct: ct);
        var (code, _) = await fx.Links.CreateCodeAsync(alice, ct);

        var reply = await new LinkCommandHandler(fx.Links, fx.System, fx.Channels).HandleAsync(
            Ctx("9999", new() { ["code"] = code }, channelId: "friend-dm",
                openBotDm: _ => Task.FromResult("5555")),
            ct);

        Assert.Equal(ChatText.Get("link.done", null), reply.Message);
        Assert.Equal("5555", (await fx.Channels.GetAsync(alice, DiscordProvider.Name, ct))!.ChannelId);
    }

    [Fact]
    public async Task Link_SecondDiscordAccount_ReplacesTheFirst()
    {
        using var fx = new BotTestFixture();
        var ct = TestContext.Current.CancellationToken;
        var alice = await fx.SeedUserAsync(ct: ct);
        var handler = new LinkCommandHandler(fx.Links, fx.System, fx.Channels);

        var (first, _) = await fx.Links.CreateCodeAsync(alice, ct);
        await handler.HandleAsync(Ctx("1111", new() { ["code"] = first }, channelId: "dm-1"), ct);
        var (second, _) = await fx.Links.CreateCodeAsync(alice, ct);
        await handler.HandleAsync(Ctx("2222", new() { ["code"] = second }, channelId: "dm-2"), ct);

        Assert.Null(await fx.System.GetUserIdByMappingAsync(DiscordProvider.Name, "1111", ct));
        Assert.Equal(alice, await fx.System.GetUserIdByMappingAsync(DiscordProvider.Name, "2222", ct));
        Assert.Equal("2222", await fx.System.GetProviderIdForUserAsync(alice, DiscordProvider.Name, ct));
        Assert.Equal("dm-2", (await fx.Channels.GetAsync(alice, DiscordProvider.Name, ct))!.ChannelId);

        // /unlink from the replaced account no longer touches the new link.
        var unlink = await new UnlinkCommandHandler(fx.System, fx.Channels).HandleAsync(Ctx("1111"), ct);
        Assert.Equal(ChatText.Get("unlink.notLinked", null), unlink.Message);
        Assert.NotNull(await fx.Channels.GetAsync(alice, DiscordProvider.Name, ct));
    }
}
