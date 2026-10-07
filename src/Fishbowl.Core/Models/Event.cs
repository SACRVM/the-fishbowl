using System;

namespace Fishbowl.Core.Models;

public class Event
{
    public string Id { get; set; } = string.Empty; // ULID
    // The iCalendar / vCard UID (user/space schema v21): "<id>@fishbowl" for
    // what Fishbowl makes, the source's own for what an import or a synced
    // device brings. Set once, never changed by an update.
    public string? Uid { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime StartAt { get; set; }
    public DateTime? EndAt { get; set; }
    public bool AllDay { get; set; }
    // All-day only (user schema v12): the calendar dates, `YYYY-MM-DD`, end
    // exclusive. StartAt/EndAt then hold `<date>T00:00:00Z` anchors — see
    // Fishbowl.Core.Util.AllDayDates. Null on timed events.
    public string? StartDate { get; set; }
    public string? EndDate { get; set; }
    public string? RRule { get; set; } // iCal RRULE
    // IANA zone the event was written in (the browser's). A recurring series
    // expands in its wall-clock time; null = expand in UTC.
    public string? TimeZone { get; set; }
    public string? Location { get; set; }
    public int? ReminderMinutes { get; set; }
    public string? ExternalId { get; set; }
    public string? ExternalSource { get; set; } // 'google' | 'ical'
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // True only on expanded RRULE occurrences returned by range reads and
    // reminder queries — never persisted. Instances share the master's Id;
    // clients editing an instance are editing the whole series.
    public bool IsRecurringInstance { get; set; }
}
