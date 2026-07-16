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
            .WithDescription("Show your upcoming Fishbowl events.")
            .AddOption("days", ApplicationCommandOptionType.Integer,
                $"How many days ahead to look (default {DefaultDays}).",
                isRequired: false, minValue: 1, maxValue: MaxDays);
        LinkCommandHandler.ApplyDmContext(builder);
        return builder.Build();
    }

    public async Task<SlashCommandReply> HandleAsync(SlashCommandContext ctx, CancellationToken ct)
    {
        var userId = await _resolver.ResolveAsync(ctx.DiscordUserId, ct);
        if (string.IsNullOrEmpty(userId))
            return Replies.NotLinked;

        var days = DefaultDays;
        if (int.TryParse(ctx.Get("days"), out var parsed))
            days = Math.Clamp(parsed, 1, MaxDays);

        var from = DateTime.UtcNow;
        var found = (await _events.GetRangeAsync(
            ContextRef.User(userId), from, from.AddDays(days), ct)).ToList();

        if (found.Count == 0)
            return SlashCommandReply.Plain(
                $"Nothing on the calendar for the next {days} day{(days == 1 ? "" : "s")}.");

        var lines = new List<string>(Math.Min(found.Count, MaxLines) + 2)
        {
            $"**Coming up in the next {days} day{(days == 1 ? "" : "s")}** ({found.Count}):",
        };
        foreach (var ev in found.Take(MaxLines))
        {
            var unix = new DateTimeOffset(TimeUtil.AsUtc(ev.StartAt)).ToUnixTimeSeconds();
            var when = ev.AllDay ? $"<t:{unix}:D>" : $"<t:{unix}:f>";
            var repeat = string.IsNullOrEmpty(ev.RRule) ? "" : " ↻";
            var location = string.IsNullOrWhiteSpace(ev.Location) ? "" : $" — {Escape(ev.Location)}";
            lines.Add($"• {when}  **{Escape(ev.Title)}**{repeat}{location}");
        }
        if (found.Count > MaxLines)
            lines.Add($"…and {found.Count - MaxLines} more.");

        return SlashCommandReply.Plain(string.Join("\n", lines));
    }

    private static string Escape(string s)
        => s.Replace("`", "\\`").Replace("*", "\\*").Replace("_", "\\_");
}
