using Fishbowl.Core.Models;

namespace Fishbowl.Core.Util;

// A contact's birthday as a yearly date (sync spec, 2026-10-06): Fishbowl's
// calendar and its live tile — and later the read-only birthday calendar on
// devices — take birthdays from contacts, never from stored events. People
// only (an organisation has none), archived ones left out. 29 February falls
// on 1 March in a common year, like the Contacts tile. The age is the one
// turned that day, when the birth year is a real one (Apple writes 1604 for
// "no year").
public static class Birthdays
{
    public sealed record Entry(string ContactId, string Name, string Date, int? Age);

    public static IReadOnlyList<Entry> Between(IEnumerable<Contact> contacts, DateOnly from, DateOnly toExclusive)
    {
        var list = new List<Entry>();
        if (toExclusive <= from) return list;
        foreach (var c in contacts)
        {
            if (c.Archived || c.Kind != ContactKinds.Person || !AllDayDates.TryParse(c.Birthday, out var born)) continue;
            for (var year = from.Year; year <= toExclusive.Year; year++)
            {
                var date = born.Month == 2 && born.Day == 29 && !DateTime.IsLeapYear(year)
                    ? new DateOnly(year, 3, 1)
                    : new DateOnly(year, born.Month, born.Day);
                if (date < from || date >= toExclusive) continue;
                int? age = born.Year > 1604 && year >= born.Year ? year - born.Year : null;
                list.Add(new Entry(c.Id, c.Name, AllDayDates.ToText(date), age));
            }
        }
        return list.OrderBy(e => e.Date, StringComparer.Ordinal).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
