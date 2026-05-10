using Fishbowl.Bot.Discord;
using Fishbowl.Bot.Discord.Commands;
using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Core.Search;
using Xunit;

namespace Fishbowl.Bot.Discord.Tests.Commands;

public class SearchCommandHandlerTests
{
    // Test double — keeps the unit test independent of HybridSearchService's
    // model dependency. Real ranking quality is covered in
    // Fishbowl.Data.Tests.Search; here we just verify the bot's
    // titles-only contract.
    private sealed class StubSearch : ISearchService
    {
        public IReadOnlyList<MemorySearchResult> NextHits { get; set; } = Array.Empty<MemorySearchResult>();
        public bool Degraded { get; set; }

        public Task<HybridSearchResult> HybridSearchAsync(
            ContextRef ctx, string query, int limit, bool includePending,
            IReadOnlyCollection<string>? tags = null, string match = "any",
            CancellationToken ct = default)
            => Task.FromResult(new HybridSearchResult(NextHits, Degraded));
    }

    [Fact]
    public async Task NotLinked_RepliesNotLinked()
    {
        using var fx = new BotTestFixture();
        var ct = TestContext.Current.CancellationToken;

        var handler = new SearchCommandHandler(
            new DiscordUserResolver(fx.System), new StubSearch());

        var reply = await handler.HandleAsync(
            new SlashCommandContext("9999", "alice", "ch1",
                new Dictionary<string, string?> { ["query"] = "hello" }),
            ct);

        Assert.Contains("don't recognise", reply.Message);
    }

    [Fact]
    public async Task NoHits_RepliesNoMatches()
    {
        using var fx = new BotTestFixture();
        var ct = TestContext.Current.CancellationToken;
        var alice = await fx.SeedUserAsync(ct: ct);
        await fx.System.CreateUserMappingAsync(alice, "discord", "9999", ct);

        var handler = new SearchCommandHandler(
            new DiscordUserResolver(fx.System), new StubSearch());

        var reply = await handler.HandleAsync(
            new SlashCommandContext("9999", "alice", "ch1",
                new Dictionary<string, string?> { ["query"] = "xyz" }),
            ct);

        Assert.Contains("No matches", reply.Message);
    }

    [Fact]
    public async Task WithHits_ReturnsTitlesOnly_NotContent()
    {
        using var fx = new BotTestFixture();
        var ct = TestContext.Current.CancellationToken;
        var alice = await fx.SeedUserAsync(ct: ct);
        await fx.System.CreateUserMappingAsync(alice, "discord", "9999", ct);

        const string sensitive = "tokens_dont_leak_through_chat";
        var stub = new StubSearch
        {
            NextHits = new[]
            {
                new MemorySearchResult(
                    new Note { Id = "01HABCXYZ", Title = "Found Title", Content = sensitive, Tags = new List<string>() },
                    Score: 0.9),
            }
        };

        var handler = new SearchCommandHandler(
            new DiscordUserResolver(fx.System), stub);

        var reply = await handler.HandleAsync(
            new SlashCommandContext("9999", "alice", "ch1",
                new Dictionary<string, string?> { ["query"] = "anything" }),
            ct);

        Assert.Contains("Found Title", reply.Message);
        Assert.Contains("01HABCXYZ", reply.Message);
        Assert.DoesNotContain(sensitive, reply.Message);
    }

    [Fact]
    public async Task DegradedMode_AppendsWarmupHint()
    {
        using var fx = new BotTestFixture();
        var ct = TestContext.Current.CancellationToken;
        var alice = await fx.SeedUserAsync(ct: ct);
        await fx.System.CreateUserMappingAsync(alice, "discord", "9999", ct);

        var stub = new StubSearch { Degraded = true };

        var handler = new SearchCommandHandler(
            new DiscordUserResolver(fx.System), stub);

        var reply = await handler.HandleAsync(
            new SlashCommandContext("9999", "alice", "ch1",
                new Dictionary<string, string?> { ["query"] = "xyz" }),
            ct);

        Assert.Contains("warming up", reply.Message);
    }
}
