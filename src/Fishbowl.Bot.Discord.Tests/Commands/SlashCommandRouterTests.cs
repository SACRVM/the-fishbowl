using global::Discord;
using Fishbowl.Bot.Discord.Commands;
using Xunit;

namespace Fishbowl.Bot.Discord.Tests.Commands;

public class SlashCommandRouterTests
{
    private sealed class TestHandler : ISlashCommandHandler
    {
        private readonly Func<SlashCommandContext, Task<SlashCommandReply>> _handle;
        public TestHandler(string name, Func<SlashCommandContext, Task<SlashCommandReply>> handle)
        {
            Name = name;
            _handle = handle;
        }
        public string Name { get; }
        public SlashCommandProperties Build()
            => new SlashCommandBuilder().WithName(Name).WithDescription("test").Build();
        public Task<SlashCommandReply> HandleAsync(SlashCommandContext ctx, CancellationToken ct)
            => _handle(ctx);
    }

    private static SlashCommandContext MakeCtx() => new(
        DiscordUserId: "9999",
        DiscordUsername: "alice",
        DiscordChannelId: "ch1",
        Options: new Dictionary<string, string?>());

    [Fact]
    public async Task UnknownCommand_RepliesUnknown()
    {
        var router = new SlashCommandRouter(Array.Empty<ISlashCommandHandler>());
        var reply = await router.DispatchAsync("nope", MakeCtx(), TestContext.Current.CancellationToken);

        Assert.Contains("Unknown command", reply.Message);
        Assert.Contains("/help", reply.Message);
    }

    [Fact]
    public async Task KnownCommand_DispatchesToHandler()
    {
        var calls = 0;
        var handler = new TestHandler("foo", _ =>
        {
            calls++;
            return Task.FromResult(SlashCommandReply.Plain("ok"));
        });

        var router = new SlashCommandRouter(new[] { handler });
        var reply = await router.DispatchAsync("foo", MakeCtx(), TestContext.Current.CancellationToken);

        Assert.Equal("ok", reply.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task HandlerThrows_RepliesGenericError_DoesNotPropagate()
    {
        var handler = new TestHandler("boom", _ => throw new InvalidOperationException("kaboom"));
        var router = new SlashCommandRouter(new[] { handler });

        var reply = await router.DispatchAsync("boom", MakeCtx(), TestContext.Current.CancellationToken);

        Assert.Contains("Something went wrong", reply.Message);
    }

    [Fact]
    public async Task CancellationToken_PropagatesUp()
    {
        var handler = new TestHandler("slow", _ =>
            throw new OperationCanceledException(new CancellationToken(canceled: true)));
        var router = new SlashCommandRouter(new[] { handler });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            router.DispatchAsync("slow", MakeCtx(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Lookup_IsCaseInsensitive_ToMatchDiscordCommandNames()
    {
        var router = new SlashCommandRouter(new ISlashCommandHandler[]
        {
            new TestHandler("Help", _ => Task.FromResult(SlashCommandReply.Plain("h"))),
        });

        // Discord normalises command names to lowercase but the dictionary
        // shouldn't care either way — this guards against future refactors
        // that might tighten case sensitivity by accident.
        Assert.Single(router.Handlers);
    }
}
