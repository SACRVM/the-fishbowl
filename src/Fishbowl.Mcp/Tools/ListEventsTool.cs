using System.Security.Claims;
using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Repositories;

namespace Fishbowl.Mcp.Tools;

// CONCEPT.md § Calendar + § Chat interface — "Your AI assistant, your
// data, your hardware." Lets an agent answer "what's on this week" or
// "am I free Friday" against the real calendar. Read-only on purpose:
// agents writing to calendars have sharper consequences than remembering
// a note (double-bookings, invites, wrong attendees) — if we add write
// it'll be a separate, deliberate tool.
public class ListEventsTool : IMcpTool
{
    private readonly IEventRepository _events;

    public ListEventsTool(IEventRepository events) { _events = events; }

    public string Name => "list_events";
    public string Description =>
        "Lists calendar events in the current context. Pass `upcoming_days` for a quick next-N-days view, or `from`+`to` (ISO-8601) for an exact range.";
    public string RequiredScope => "read:events";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            upcoming_days = new
            {
                type = "integer",
                description = "Shortcut: list events starting in the next N days. Ignored when `from`+`to` are both set.",
            },
            from = new
            {
                type = "string",
                description = "ISO-8601 range lower bound (inclusive).",
            },
            to = new
            {
                type = "string",
                description = "ISO-8601 range upper bound (exclusive). Must pair with `from`.",
            },
            limit = new
            {
                type = "integer",
                @default = 50,
                description = "Maximum number of events to return (1–500).",
            },
        },
    };

    public async Task<object> InvokeAsync(
        ContextRef ctx, string actor, JsonElement arguments, ClaimsPrincipal principal, CancellationToken ct)
    {
        var limit = arguments.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number
            ? Math.Clamp(l.GetInt32(), 1, 500) : 50;

        DateTime? from = TryDate(arguments, "from");
        DateTime? to = TryDate(arguments, "to");

        IEnumerable<Fishbowl.Core.Models.Event> found;
        if (from is not null && to is not null)
        {
            found = await _events.GetRangeAsync(ctx, from.Value, to.Value, ct);
        }
        else if (arguments.TryGetProperty("upcoming_days", out var dN) && dN.ValueKind == JsonValueKind.Number)
        {
            var days = Math.Clamp(dN.GetInt32(), 1, 365);
            var start = DateTime.UtcNow;
            found = await _events.GetRangeAsync(ctx, start, start.AddDays(days), ct);
        }
        else
        {
            found = await _events.GetAllAsync(ctx, ct);
        }

        var rows = found.Take(limit).Select(e => new
        {
            id = e.Id,
            title = e.Title,
            description = e.Description,
            startAt = e.StartAt,
            endAt = e.EndAt,
            allDay = e.AllDay,
            rrule = e.RRule,
            location = e.Location,
            reminderMinutes = e.ReminderMinutes,
        }).ToList();

        return new { events = rows, count = rows.Count };
    }

    private static DateTime? TryDate(JsonElement args, string prop)
    {
        if (!args.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.String)
            return null;
        return DateTime.TryParse(v.GetString(),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind | System.Globalization.DateTimeStyles.AssumeUniversal,
            out var dt) ? dt : null;
    }
}
