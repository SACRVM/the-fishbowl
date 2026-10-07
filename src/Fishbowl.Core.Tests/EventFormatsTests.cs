using Fishbowl.Core.Models;
using Fishbowl.Core.Util;
using Xunit;

namespace Fishbowl.Core.Tests;

// Events as iCalendar (EventFormats): a round trip keeps every field Fishbowl
// owns, and files in the shapes Apple Calendar, Google Calendar and Outlook
// write read into the right events — zones, all-day dates, rules, alarms,
// floating times; overridden occurrences wait for the exceptions model.
public class EventFormatsTests
{
    private static DateTime Utc(int y, int mo, int d, int h = 0, int mi = 0) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    [Fact]
    public void RoundTrip_KeepsEveryOwnedField()
    {
        var events = new[]
        {
            new Event
            {
                Id = "01JTEST000000000000000000A", Uid = "team@example.com", Title = "Team", Description = "Agenda\nline 2",
                Location = "Room 1", StartAt = Utc(2026, 10, 12, 16), EndAt = Utc(2026, 10, 12, 17),
                TimeZone = "Europe/Berlin", RRule = "FREQ=WEEKLY;BYDAY=MO", ReminderMinutes = 15,
                CreatedAt = Utc(2026, 10, 1), UpdatedAt = Utc(2026, 10, 2),
            },
            new Event
            {
                Id = "01JTEST000000000000000000B", Title = "Holiday", AllDay = true,
                StartDate = "2026-12-24", EndDate = "2026-12-27", StartAt = Utc(2026, 12, 24), EndAt = Utc(2026, 12, 27),
            },
            new Event { Id = "01JTEST000000000000000000C", Title = "Call", StartAt = Utc(2026, 11, 3, 9, 30) },
        };

        var text = EventFormats.ToICalendar(events, "Personal");
        Assert.Contains("X-WR-CALNAME:Personal", text);
        Assert.Contains("BEGIN:VTIMEZONE", text);
        Assert.Contains("DTSTART;TZID=Europe/Berlin:20261012T180000", text);
        Assert.Contains("DTSTART;VALUE=DATE:20261224", text);
        Assert.Contains("DTEND;VALUE=DATE:20261227", text);

        var back = EventFormats.ParseICalendar(text);
        Assert.Equal(0, back.Skipped);
        Assert.Equal(3, back.Events.Count);

        var team = back.Events.Single(e => e.Title == "Team");
        Assert.Equal("team@example.com", team.Uid);
        Assert.Equal("Agenda\nline 2", team.Description);
        Assert.Equal("Room 1", team.Location);
        Assert.Equal(Utc(2026, 10, 12, 16), team.StartAt);
        Assert.Equal(Utc(2026, 10, 12, 17), team.EndAt);
        Assert.Equal("Europe/Berlin", team.TimeZone);
        Assert.Equal("FREQ=WEEKLY;BYDAY=MO", team.RRule);
        Assert.Equal(15, team.ReminderMinutes);

        var holiday = back.Events.Single(e => e.Title == "Holiday");
        Assert.True(holiday.AllDay);
        Assert.Equal("2026-12-24", holiday.StartDate);
        Assert.Equal("2026-12-27", holiday.EndDate);
        Assert.Equal("01JTEST000000000000000000B@fishbowl", holiday.Uid);

        var call = back.Events.Single(e => e.Title == "Call");
        Assert.Equal(Utc(2026, 11, 3, 9, 30), call.StartAt);
        Assert.Null(call.EndAt);
        Assert.Null(call.TimeZone);
        Assert.Null(call.ReminderMinutes);
    }

    // The shape Apple Calendar exports: VTIMEZONE + TZID, a weekly rule, an alarm.
    private const string AppleExport = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Apple Inc.//macOS 15.0//EN
        CALSCALE:GREGORIAN
        BEGIN:VTIMEZONE
        TZID:Europe/Berlin
        BEGIN:DAYLIGHT
        TZOFFSETFROM:+0100
        RRULE:FREQ=YEARLY;BYMONTH=3;BYDAY=-1SU
        DTSTART:19810329T020000
        TZNAME:CEST
        TZOFFSETTO:+0200
        END:DAYLIGHT
        BEGIN:STANDARD
        TZOFFSETFROM:+0200
        RRULE:FREQ=YEARLY;BYMONTH=10;BYDAY=-1SU
        DTSTART:19961027T030000
        TZNAME:CET
        TZOFFSETTO:+0100
        END:STANDARD
        END:VTIMEZONE
        BEGIN:VEVENT
        UID:6F1E3C2A-0D4B-4A5E-9F71-2B8C1A9E0D11
        DTSTAMP:20261001T080000Z
        SUMMARY:Choir
        LOCATION:Church hall
        DTSTART;TZID=Europe/Berlin:20261012T180000
        DTEND;TZID=Europe/Berlin:20261012T193000
        RRULE:FREQ=WEEKLY;BYDAY=MO
        BEGIN:VALARM
        ACTION:DISPLAY
        DESCRIPTION:Reminder
        TRIGGER:-PT30M
        X-WR-ALARMUID:0B1F
        END:VALARM
        END:VEVENT
        END:VCALENDAR
        """;

    [Fact]
    public void Import_AppleShape()
    {
        var parsed = EventFormats.ParseICalendar(AppleExport.Replace("\n", "\r\n"));
        var e = Assert.Single(parsed.Events);
        Assert.Equal("6F1E3C2A-0D4B-4A5E-9F71-2B8C1A9E0D11", e.Uid);
        Assert.Equal("Choir", e.Title);
        Assert.Equal("Church hall", e.Location);
        Assert.Equal(Utc(2026, 10, 12, 16), e.StartAt);   // 18:00 CEST
        Assert.Equal(Utc(2026, 10, 12, 17, 30), e.EndAt);
        Assert.Equal("Europe/Berlin", e.TimeZone);
        Assert.Equal("FREQ=WEEKLY;BYDAY=MO", e.RRule);
        Assert.Equal(30, e.ReminderMinutes);
        Assert.False(e.AllDay);
    }

    // The shape Google Calendar exports: UTC times, all-day dates, a series
    // with UNTIL and one moved occurrence (RECURRENCE-ID).
    private const string GoogleExport = """
        BEGIN:VCALENDAR
        PRODID:-//Google Inc//Google Calendar 70.9054//EN
        VERSION:2.0
        CALSCALE:GREGORIAN
        X-WR-CALNAME:me@example.com
        X-WR-TIMEZONE:Europe/Berlin
        BEGIN:VEVENT
        DTSTART;VALUE=DATE:20261224
        DTEND;VALUE=DATE:20261227
        DTSTAMP:20261001T080000Z
        UID:holiday-1@google.com
        SUMMARY:Christmas break
        END:VEVENT
        BEGIN:VEVENT
        DTSTART:20261005T070000Z
        DTEND:20261005T073000Z
        RRULE:FREQ=DAILY;UNTIL=20261030T070000Z
        DTSTAMP:20261001T080000Z
        UID:standup@google.com
        SUMMARY:Standup
        END:VEVENT
        BEGIN:VEVENT
        DTSTART:20261008T090000Z
        DTEND:20261008T093000Z
        DTSTAMP:20261001T080000Z
        UID:standup@google.com
        RECURRENCE-ID:20261008T070000Z
        SUMMARY:Standup (moved)
        END:VEVENT
        END:VCALENDAR
        """;

    [Fact]
    public void Import_GoogleShape_OverridesWaitForThePhaseWithExceptions()
    {
        var parsed = EventFormats.ParseICalendar(GoogleExport.Replace("\n", "\r\n"));
        Assert.Equal(1, parsed.Skipped);   // the moved occurrence
        Assert.Equal(2, parsed.Events.Count);

        var holiday = parsed.Events.Single(e => e.Uid == "holiday-1@google.com");
        Assert.True(holiday.AllDay);
        Assert.Equal("2026-12-24", holiday.StartDate);
        Assert.Equal("2026-12-27", holiday.EndDate);

        var standup = parsed.Events.Single(e => e.Uid == "standup@google.com");
        Assert.Equal(Utc(2026, 10, 5, 7), standup.StartAt);
        Assert.Equal(Utc(2026, 10, 5, 7, 30), standup.EndAt);
        Assert.Null(standup.TimeZone);
        Assert.True(RRule.TryParse(standup.RRule, out var spec));
        Assert.Equal(Utc(2026, 10, 30, 7), spec.Until);
    }

    [Fact]
    public void Import_FloatingTimes_AreTheImportersZone()
    {
        const string text = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:x\r\nBEGIN:VEVENT\r\nUID:f1\r\nDTSTAMP:20261001T000000Z\r\n"
            + "SUMMARY:Lunch\r\nDTSTART:20261102T120000\r\nDURATION:PT1H\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var e = Assert.Single(EventFormats.ParseICalendar(text, "America/New_York").Events);
        Assert.Equal(Utc(2026, 11, 2, 17), e.StartAt);   // 12:00 EST
        Assert.Equal(Utc(2026, 11, 2, 18), e.EndAt);
        Assert.Equal("America/New_York", e.TimeZone);
    }

    [Fact]
    public void Import_NotICalendar_IsAFormatError()
    {
        Assert.Throws<FormatException>(() => EventFormats.ParseICalendar("BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Ada\r\nEND:VCARD\r\n"));
        Assert.Throws<FormatException>(() => EventFormats.ParseICalendar("just some text"));
    }
}
