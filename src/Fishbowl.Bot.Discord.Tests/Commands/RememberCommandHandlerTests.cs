using Fishbowl.Bot.Discord;
using Fishbowl.Bot.Discord.Commands;
using Fishbowl.Core;
using Xunit;

namespace Fishbowl.Bot.Discord.Tests.Commands;

public class RememberCommandHandlerTests
{
    [Fact]
    public async Task NotLinked_RepliesNotLinked_AndNoNoteWritten()
    {
        using var fx = new BotTestFixture();
        var ct = TestContext.Current.CancellationToken;

        var handler = new RememberCommandHandler(new DiscordUserResolver(fx.System), fx.Notes);
        var ctx = new SlashCommandContext("9999", "alice", "ch1",
            new Dictionary<string, string?> { ["text"] = "hello" });

        var reply = await handler.HandleAsync(ctx, ct);

        Assert.Contains("don't recognise", reply.Message);
    }

    [Fact]
    public async Task LinkedUser_CreatesNoteWithDiscordSourceTag()
    {
        using var fx = new BotTestFixture();
        var ct = TestContext.Current.CancellationToken;
        var alice = await fx.SeedUserAsync(ct: ct);
        await fx.System.CreateUserMappingAsync(alice, "discord", "9999", ct);

        var handler = new RememberCommandHandler(new DiscordUserResolver(fx.System), fx.Notes);
        var ctx = new SlashCommandContext("9999", "alice", "ch1",
            new Dictionary<string, string?> { ["text"] = "remember the milk" });

        var reply = await handler.HandleAsync(ctx, ct);

        Assert.Contains("Saved", reply.Message);

        var notes = (await fx.Notes.GetAllAsync(ContextRef.User(alice), ct)).ToList();
        Assert.Single(notes);
        Assert.Equal("remember the milk", notes[0].Content);
        Assert.Contains("source:discord", notes[0].Tags);
        Assert.Equal(alice, notes[0].CreatedBy);
    }

    [Fact]
    public async Task ExplicitTitle_OverridesAutoTitle()
    {
        using var fx = new BotTestFixture();
        var ct = TestContext.Current.CancellationToken;
        var alice = await fx.SeedUserAsync(ct: ct);
        await fx.System.CreateUserMappingAsync(alice, "discord", "9999", ct);

        var handler = new RememberCommandHandler(new DiscordUserResolver(fx.System), fx.Notes);
        var ctx = new SlashCommandContext("9999", "alice", "ch1",
            new Dictionary<string, string?>
            {
                ["text"] = "very long body of the note that would be a poor title",
                ["title"] = "Shopping",
            });

        await handler.HandleAsync(ctx, ct);

        var notes = (await fx.Notes.GetAllAsync(ContextRef.User(alice), ct)).ToList();
        Assert.Equal("Shopping", notes[0].Title);
    }

    [Fact]
    public async Task EmptyText_RepliesUsage_NoNoteWritten()
    {
        using var fx = new BotTestFixture();
        var ct = TestContext.Current.CancellationToken;
        var alice = await fx.SeedUserAsync(ct: ct);
        await fx.System.CreateUserMappingAsync(alice, "discord", "9999", ct);

        var handler = new RememberCommandHandler(new DiscordUserResolver(fx.System), fx.Notes);
        var ctx = new SlashCommandContext("9999", "alice", "ch1",
            new Dictionary<string, string?> { ["text"] = "   " });

        var reply = await handler.HandleAsync(ctx, ct);

        Assert.Contains("Tell me what to remember", reply.Message);
        Assert.Empty(await fx.Notes.GetAllAsync(ContextRef.User(alice), ct));
    }
}
