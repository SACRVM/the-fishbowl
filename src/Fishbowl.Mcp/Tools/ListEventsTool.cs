using System.Security.Claims;
using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Util;

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
            ? Math.Clamp(McpArgs.Int(l, "limit"), 1, 500) : 50;

        DateTime? from = TryDate(arguments, "from");
        DateTime? to = TryDate(arguments, "to");
        if ((from is null) != (to is null))
            throw new ArgumentException("`from` and `to` go together: pass both or neither.");

        IEnumerable<Fishbowl.Core.Models.Event> found;
        if (from is not null && to is not null)
        {
            if (EventLimits.CheckRange(from.Value, to.Value) is { } bad)
                throw new ArgumentException(bad.Message);
            found = await _events.GetRangeAsync(ctx, from.Value, to.Value, ct);
        }
        else if (arguments.TryGetProperty("upcoming_days", out var dN) && dN.ValueKind == JsonValueKind.Number)
        {
            var days = Math.Clamp(McpArgs.Int(dN, "upcoming_days"), 1, 365);
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
            // All-day events are dates (end exclusive); startAt/endAt then
            // hold T00:00Z anchors, not moments.
            startDate = e.StartDate,
            endDate = e.EndDate,
            rrule = e.RRule,
            location = e.Location,
            reminderMinutes = e.ReminderMinutes,
        }).ToList();

        return new { events = rows, count = rows.Count };
    }

    // An ISO-8601 string as a UTC instant; one without an offset counts as
    // UTC. (RoundtripKind can't be combined with AssumeUniversal: TryParse
    // throws for the style itself, whatever the input.)
    private static DateTime? TryDate(JsonElement args, string prop)
    {
        if (!args.TryGetProperty(prop, out var v) || v.ValueKind == JsonValueKind.Null)
            return null;
        if (v.ValueKind == JsonValueKind.String && DateTime.TryParse(v.GetString(),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var dt))
            return dt;
        throw new ArgumentException($"`{prop}` must be an ISO-8601 date-time, e.g. 2026-10-04T00:00:00Z.");
    }
}
