using global::Discord;
using Fishbowl.Core;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Util;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Bot.Discord.Commands;

// /upcoming [days] — chronological events for the next N days (default 7).
// Recurring series come back pre-expanded from GetRangeAsync, so each
// occurrence shows on its own line. Times use Discord's <t:…> markup so
// the client renders them in the *viewer's* timezone — the bot never has
// to guess where the user is. Titles + time + location only, same
// content-minimalism rule as /search and /recent.
public class UpcomingCommandHandler : ISlashCommandHandler
{
    public string Name => "upcoming";

    private const int DefaultDays = 7;
    private const int MaxDays = 60;
    // Discord caps messages at 2000 chars; 15 event lines stays well under.
    private const int MaxLines = 15;

    private readonly DiscordUserResolver _resolver;
    private readonly IEventRepository _events;
    private readonly ILogger<UpcomingCommandHandler> _logger;

    public UpcomingCommandHandler(
        DiscordUserResolver resolver,
        IEventRepository events,
        ILogger<UpcomingCommandHandler>? logger = null)
    {
        _resolver = resolver;
        _events = events;
        _logger = logger ?? NullLogger<UpcomingCommandHandler>.Instance;
    }

    public SlashCommandProperties Build()
    {
        var builder = new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription(ChatText.Get("cmd.upcoming", null))
            .WithDescriptionLocalizations(ChatText.Localizations("cmd.upcoming"))
            .AddOption("days", ApplicationCommandOptionType.Integer,
                ChatText.Get("cmd.upcoming.days", null, DefaultDays),
                isRequired: false, minValue: 1, maxValue: MaxDays,
                descriptionLocalizations: ChatText.Localizations("cmd.upcoming.days", DefaultDays));
        LinkCommandHandler.ApplyDmContext(builder);
        return builder.Build();
    }

    public async Task<SlashCommandReply> HandleAsync(SlashCommandContext ctx, CancellationToken ct)
    {
        var userId = await _resolver.ResolveAsync(ctx.DiscordUserId, ct);
        if (string.IsNullOrEmpty(userId))
            return Replies.NotLinked;
        var lang = await _resolver.LanguageAsync(userId, ct);

        var days = DefaultDays;
        if (int.TryParse(ctx.Get("days"), out var parsed))
            days = Math.Clamp(parsed, 1, MaxDays);

        var from = DateTime.UtcNow;
        // All-day events are dates; the range read pads them a day each side.
        var fromDay = DateOnly.FromDateTime(from);
        var found = (await _events.GetRangeAsync(
            ContextRef.User(userId), from, from.AddDays(days), ct))
            .Where(e => !e.AllDay || AllDayDates.Overlaps(e, fromDay, fromDay.AddDays(days)))
            .ToList();

        if (found.Count == 0)
            return days == 1 ? Replies.Say(lang, "upcoming.none.1") : Replies.Say(lang, "upcoming.none", days);

        var lines = new List<string>(Math.Min(found.Count, MaxLines) + 2)
        {
            days == 1
                ? ChatText.Get("upcoming.title.1", lang, found.Count)
                : ChatText.Get("upcoming.title", lang, days, found.Count),
        };
        foreach (var ev in found.Take(MaxLines))
        {
            var unix = new DateTimeOffset(TimeUtil.AsUtc(ev.StartAt)).ToUnixTimeSeconds();
            // A date, not a moment: <t:…> would shift it into the viewer's zone.
            var when = ev.AllDay ? ChatText.Get("upcoming.allDay", lang, DayText(ev.StartDate, lang)) : $"<t:{unix}:f>";
            var repeat = string.IsNullOrEmpty(ev.RRule) ? "" : " ↻";
            var location = string.IsNullOrWhiteSpace(ev.Location) ? "" : $" — {Escape(ev.Location)}";
            lines.Add($"• {when}  **{Escape(ev.Title)}**{repeat}{location}");
        }
        if (found.Count > MaxLines)
            lines.Add(ChatText.Get("upcoming.more", lang, found.Count - MaxLines));

        return SlashCommandReply.Plain(string.Join("\n", lines));
    }

    private static string DayText(string? date, string? lang)
        => AllDayDates.TryParse(date, out var d) ? ChatText.Day(d, lang) : date ?? "";

    private static string Escape(string s)
        => s.Replace("`", "\\`").Replace("*", "\\*").Replace("_", "\\_");
}
