using Fishbowl.Bot.Discord;
using Fishbowl.Bot.Discord.Commands;
using Fishbowl.Core;
using Fishbowl.Core.Models;
using Xunit;

namespace Fishbowl.Bot.Discord.Tests.Commands;

public class RecentCommandHandlerTests
{
    [Fact]
    public async Task NotLinked_RepliesNotLinked()
    {
        using var fx = new BotTestFixture();
        var ct = TestContext.Current.CancellationToken;

        var handler = new RecentCommandHandler(new DiscordUserResolver(fx.System), fx.Notes);
        var reply = await handler.HandleAsync(
            new SlashCommandContext("9999", "alice", "ch1", new Dictionary<string, string?>()),
            ct);

        Assert.Contains("don't recognise", reply.Message);
    }

    [Fact]
    public async Task NoNotes_RepliesEmptyHint()
    {
        using var fx = new BotTestFixture();
        var ct = TestContext.Current.CancellationToken;
        var alice = await fx.SeedUserAsync(ct: ct);
        await fx.System.CreateUserMappingAsync(alice, "discord", "9999", ct);

        var handler = new RecentCommandHandler(new DiscordUserResolver(fx.System), fx.Notes);
        var reply = await handler.HandleAsync(
            new SlashCommandContext("9999", "alice", "ch1", new Dictionary<string, string?>()),
            ct);

        Assert.Contains("No notes yet", reply.Message);
    }

    [Fact]
    public async Task ListsTitlesOnly_NeverContent()
    {
        using var fx = new BotTestFixture();
        var ct = TestContext.Current.CancellationToken;
        var alice = await fx.SeedUserAsync(ct: ct);
        await fx.System.CreateUserMappingAsync(alice, "discord", "9999", ct);

        const string secretBody = "client_secret_xyz_should_not_leak";
        await fx.Notes.CreateAsync(
            ContextRef.User(alice), alice,
            new Note { Title = "Streaming Account", Content = secretBody, Tags = new List<string>() },
            NoteSource.Human, ct);

        var handler = new RecentCommandHandler(new DiscordUserResolver(fx.System), fx.Notes);
        var reply = await handler.HandleAsync(
            new SlashCommandContext("9999", "alice", "ch1", new Dictionary<string, string?>()),
            ct);

        Assert.Contains("Streaming Account", reply.Message);
        // The bot must never echo note content into chat — that's the
        // bedrock invariant ("never reveal secret content in chat", and
        // "titles only" everywhere else).
        Assert.DoesNotContain(secretBody, reply.Message);
    }
}
