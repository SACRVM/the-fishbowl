using Fishbowl.Bot.Discord;
using Fishbowl.Bot.Discord.Commands;
using Fishbowl.Core.Models;
using Fishbowl.Core.Util;
using Xunit;

namespace Fishbowl.Bot.Discord.Tests.Commands;

// The bot speaks the linked user's UI language (users.language) from the
// shared chat table (ChatText); null/automatic and unlinked accounts get
// English. Command names stay English.
public class LanguageTests
{
    private static SlashCommandContext Ctx(Dictionary<string, string?>? options = null)
        => new("9999", "alice", "ch1", options ?? new Dictionary<string, string?>());

    private static async Task<string> LinkedAsync(BotTestFixture fx, string? language, CancellationToken ct)
    {
        var alice = await fx.SeedUserAsync(ct: ct);
        await fx.System.CreateUserMappingAsync(alice, "discord", "9999", ct);
        if (language is not null) await fx.System.SetLanguageAsync(alice, language, ct);
        return alice;
    }

    [Fact]
    public async Task GermanUser_GetsGermanReplies()
    {
        using var fx = new BotTestFixture();
        var ct = TestContext.Current.CancellationToken;
        await LinkedAsync(fx, "de", ct);
        var resolver = new DiscordUserResolver(fx.System);

        var help = await new HelpCommandHandler(resolver).HandleAsync(Ctx(), ct);
        Assert.StartsWith("**Fishbowl-Bot — Befehle**", help.Message);
        Assert.Contains("`/remember text:<...> [title:<...>]` — eine Notiz speichern", help.Message);

        var recent = await new RecentCommandHandler(resolver, fx.Notes).HandleAsync(Ctx(), ct);
        Assert.Equal("Noch keine Notizen. `/remember text:<...>` ist ein guter Anfang.", recent.Message);

        var upcoming = await new UpcomingCommandHandler(resolver, fx.Events).HandleAsync(Ctx(), ct);
        Assert.Equal("Für die nächsten 7 Tage steht nichts im Kalender.", upcoming.Message);

        var saved = await new RememberCommandHandler(resolver, fx.Notes).HandleAsync(
            Ctx(new() { ["text"] = "Milch kaufen" }), ct);
        Assert.Equal("Gespeichert als **Milch kaufen**.", saved.Message);

        var router = new SlashCommandRouter(Array.Empty<ISlashCommandHandler>(), resolver: resolver);
        var unknown = await router.DispatchAsync("nope", Ctx(), ct);
        Assert.Equal("Unbekannter Befehl `/nope`. Versuch's mit `/help`.", unknown.Message);
    }

    [Fact]
    public async Task AllDayEvent_ShowsTheGermanDay()
    {
        using var fx = new BotTestFixture();
        var ct = TestContext.Current.CancellationToken;
        var alice = await LinkedAsync(fx, "de", ct);
        var day = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2);
        await fx.Events.CreateAsync(alice, new Event
        {
            Title = "Feiertag",
            AllDay = true,
            StartDate = day.ToString("yyyy-MM-dd"),
            EndDate = day.AddDays(1).ToString("yyyy-MM-dd"),
            StartAt = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
        }, ct);

        var reply = await new UpcomingCommandHandler(new DiscordUserResolver(fx.System), fx.Events).HandleAsync(Ctx(), ct);

        Assert.Contains("**In den nächsten 7 Tagen** (1):", reply.Message);
        Assert.Contains($"{ChatText.Day(day, "de")} (ganztägig)", reply.Message);
        Assert.Contains("**Feiertag**", reply.Message);
    }

    [Fact]
    public async Task AutomaticAndUnlinked_AreEnglish()
    {
        using var fx = new BotTestFixture();
        var ct = TestContext.Current.CancellationToken;
        var resolver = new DiscordUserResolver(fx.System);

        // Unlinked: the bot can't know a language.
        var unlinkedHelp = await new HelpCommandHandler(resolver).HandleAsync(Ctx(), ct);
        Assert.StartsWith("**Fishbowl bot — commands**", unlinkedHelp.Message);
        var notLinked = await new RecentCommandHandler(resolver, fx.Notes).HandleAsync(Ctx(), ct);
        Assert.Contains("don't recognise", notLinked.Message);

        // Linked, language automatic (null).
        await LinkedAsync(fx, null, ct);
        var recent = await new RecentCommandHandler(resolver, fx.Notes).HandleAsync(Ctx(), ct);
        Assert.Equal("No notes yet. `/remember text:<...>` is a fine way to start.", recent.Message);
    }

    [Fact]
    public async Task Link_AnswersInTheLinkedUsersLanguage_AndUnlinkSaysGoodbyeInIt()
    {
        using var fx = new BotTestFixture();
        var ct = TestContext.Current.CancellationToken;
        var alice = await fx.SeedUserAsync(ct: ct);
        await fx.System.SetLanguageAsync(alice, "de", ct);
        var (code, _) = await fx.Links.CreateCodeAsync(alice, ct);

        var link = new LinkCommandHandler(fx.Links, fx.System, fx.Channels);
        var linked = await link.HandleAsync(Ctx(new() { ["code"] = code }), ct);
        Assert.StartsWith("Verbunden.", linked.Message);

        var unlinked = await new UnlinkCommandHandler(fx.System, fx.Channels).HandleAsync(Ctx(), ct);
        Assert.StartsWith("Getrennt.", unlinked.Message);
    }

    [Fact]
    public void CommandDescriptions_HaveGermanLocalizations_NamesStayEnglish()
    {
        var help = new HelpCommandHandler().Build();
        Assert.Equal("help", help.Name.Value);
        Assert.Equal("Die Befehle des Fishbowl-Bots.", help.DescriptionLocalizations["de"]);
        Assert.True(help.NameLocalizations is null || help.NameLocalizations.Count == 0);
    }

    [Fact]
    public void EveryEnglishChatText_HasAGermanEntry()
    {
        foreach (var key in ChatText.Keys)
            Assert.True(ChatText.Localizations(key).ContainsKey("de"), $"no German text for {key}");
    }
}
