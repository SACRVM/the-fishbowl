using System.Globalization;

namespace Fishbowl.Core.Util;

// Everything Fishbowl says in a chat, in the recipient's UI language
// (users.language — a Languages code; null/automatic → English, because a
// chat has no browser to ask). One table for the chat side of every
// sender: system-message subject lines (SystemMessageNotifier), the Discord
// bot's replies, event reminders and the daily digest. The web UI has its
// own tables (js/i18n/*.js); this is the server's, for text that never
// passes through a browser.
//
// Unknown keys fall back to English, then to the key itself, so a missing
// translation shows up as English rather than as nothing.
public static class ChatText
{
    private static readonly Dictionary<string, string> En = new(StringComparer.Ordinal)
    {
        // System-message subject lines — fixed per kind, never data.
        ["subject.user.pending"] = "Fishbowl: a new account is waiting for your approval.",
        ["subject.user.approved"] = "Fishbowl: your account is approved — you can start now.",
        ["subject.user.invited"] = "Fishbowl: someone joined through a space invitation.",
        ["subject.quota.warning"] = "Fishbowl: your storage is almost full.",
        ["subject.password.reset"] = "Fishbowl: an admin reset your password.",
        ["subject.password.reset-used"] = "Fishbowl: someone signed in with your reset password and chose a new one.",
        ["password.resetCode"] = "Your Fishbowl password was reset. Sign in with this temporary password and choose your own: {0}",
        ["subject.space.added"] = "Fishbowl: you were added to a space.",
        ["subject.app.update"] = "Fishbowl: an app on your desktop has an update to confirm.",
        ["subject.other"] = "Fishbowl: you have a new message.",

        // Discord bot — shared.
        ["bot.notLinked"] = "I don't recognise this Discord account yet. Open your Fishbowl notification settings to generate a link code, then run `/link <code>` here.",
        ["bot.unknownCommand"] = "Unknown command `/{0}`. Try `/help`.",
        ["bot.error"] = "Something went wrong on my side. Try again, or check the Fishbowl host logs.",

        // /help
        ["help.title"] = "**Fishbowl bot — commands**",
        ["help.link"] = "`/link <code>` — connect this Discord account to your Fishbowl",
        ["help.unlink"] = "`/unlink` — disconnect this Discord account",
        ["help.remember"] = "`/remember text:<...> [title:<...>]` — save a note",
        ["help.search"] = "`/search query:<...>` — hybrid search across your notes (titles only)",
        ["help.recent"] = "`/recent` — list your newest notes",
        ["help.upcoming"] = "`/upcoming [days:<n>]` — list your upcoming calendar events",
        ["help.help"] = "`/help` — show this message",
        ["help.secrets"] = "I never reveal secret content (`:::secret` blocks) in chat — open them in the web UI.",

        // /link, /unlink
        ["link.missingCode"] = "Please supply your link code: `/link <code>`.",
        ["link.already"] = "This Discord account is already linked to a Fishbowl. Unlink it from your Fishbowl notification settings before linking again.",
        ["link.badCode"] = "That code didn't work. Generate a fresh one in your Fishbowl notification settings (codes expire after 10 minutes).",
        ["link.done"] = "Linked. From here on you can `/remember`, `/search`, `/recent` — and I'll DM you reminders. Try `/help` for the full list.",
        ["unlink.notLinked"] = "This Discord account isn't linked to any Fishbowl. Nothing to do.",
        ["unlink.done"] = "Unlinked. I'll forget this Discord account. Run `/link <code>` with a fresh code anytime to reconnect.",

        // /remember, /search, /recent
        ["remember.empty"] = "Tell me what to remember: `/remember text:<...>`.",
        ["remember.saved"] = "Saved as **{0}**.",
        ["search.empty"] = "Search what? `/search query:<...>`.",
        ["search.none"] = "No matches for **{0}**.",
        ["search.noneDegraded"] = " (semantic search is still warming up — try again in a few minutes if this doesn't look right)",
        ["search.top"] = "Top {0} for **{1}**:",
        ["search.degraded"] = "_(semantic search warming up — ranking is FTS-only for now)_",
        ["recent.none"] = "No notes yet. `/remember text:<...>` is a fine way to start.",
        ["recent.title"] = "Most recent {0}:",

        // /upcoming
        ["upcoming.none.1"] = "Nothing on the calendar for the next day.",
        ["upcoming.none"] = "Nothing on the calendar for the next {0} days.",
        ["upcoming.title.1"] = "**Coming up in the next day** ({0}):",
        ["upcoming.title"] = "**Coming up in the next {0} days** ({1}):",
        ["upcoming.allDay"] = "{0} (all day)",
        ["upcoming.more"] = "…and {0} more.",

        // Event reminders
        ["reminder.line"] = "Reminder: **{0}** {1}.",
        ["reminder.onAllDay"] = "on {0} (all day)",
        ["reminder.now"] = "now",
        ["reminder.startedAgo"] = "started {0}m ago",
        ["reminder.lessThanMinute"] = "in less than a minute",
        ["reminder.inMinutes"] = "in {0}m",
        ["reminder.inHours"] = "in {0}h{1:D2}m",
        ["reminder.at"] = "at {0} UTC",
        ["reminder.location"] = "Location: {0}",

        // Daily digest
        ["digest.hello"] = "**Good morning — here's your day.**",
        ["digest.today"] = "📅 **Today:**",
        ["digest.allDay"] = "all day",
        ["digest.todos"] = "✅ **Due todos:**",
        ["digest.overdue"] = " (overdue)",

        // Slash-command descriptions (Discord shows them in the client's
        // language; names stay English).
        ["cmd.help"] = "List Fishbowl bot commands.",
        ["cmd.link"] = "Connect your Discord account to your Fishbowl.",
        ["cmd.link.code"] = "The link code shown in your Fishbowl notification settings.",
        ["cmd.unlink"] = "Disconnect this Discord account from your Fishbowl.",
        ["cmd.remember"] = "Save something to your Fishbowl.",
        ["cmd.remember.text"] = "What to remember (the body of the note).",
        ["cmd.remember.title"] = "Optional title (defaults to a short slice of the text).",
        ["cmd.search"] = "Search your Fishbowl notes.",
        ["cmd.search.query"] = "What to search for.",
        ["cmd.recent"] = "Show your newest Fishbowl notes.",
        ["cmd.upcoming"] = "Show your upcoming Fishbowl events.",
        ["cmd.upcoming.days"] = "How many days ahead to look (default {0}).",
    };

    private static readonly Dictionary<string, string> De = new(StringComparer.Ordinal)
    {
        ["subject.user.pending"] = "Fishbowl: ein neues Konto wartet auf deine Freigabe.",
        ["subject.user.approved"] = "Fishbowl: dein Konto ist freigegeben — du kannst loslegen.",
        ["subject.user.invited"] = "Fishbowl: jemand ist über eine Space-Einladung dazugekommen.",
        ["subject.quota.warning"] = "Fishbowl: dein Speicher ist fast voll.",
        ["subject.password.reset"] = "Fishbowl: ein Admin hat dein Passwort zurückgesetzt.",
        ["subject.password.reset-used"] = "Fishbowl: jemand hat sich mit deinem Reset-Passwort angemeldet und ein neues gewählt.",
        ["password.resetCode"] = "Dein Fishbowl-Passwort wurde zurückgesetzt. Melde dich mit diesem vorläufigen Passwort an und wähl ein eigenes: {0}",
        ["subject.space.added"] = "Fishbowl: du wurdest zu einem Space hinzugefügt.",
        ["subject.app.update"] = "Fishbowl: eine App auf deinem Desktop hat ein Update, das du bestätigen musst.",
        ["subject.other"] = "Fishbowl: du hast eine neue Nachricht.",

        ["bot.notLinked"] = "Dieses Discord-Konto kenne ich noch nicht. Erzeuge in deinen Fishbowl-Benachrichtigungseinstellungen einen Code und schick hier `/link <code>`.",
        ["bot.unknownCommand"] = "Unbekannter Befehl `/{0}`. Versuch's mit `/help`.",
        ["bot.error"] = "Bei mir ist etwas schiefgegangen. Versuch es noch einmal oder schau in die Logs des Fishbowl-Hosts.",

        ["help.title"] = "**Fishbowl-Bot — Befehle**",
        ["help.link"] = "`/link <code>` — dieses Discord-Konto mit deinem Fishbowl verbinden",
        ["help.unlink"] = "`/unlink` — dieses Discord-Konto trennen",
        ["help.remember"] = "`/remember text:<...> [title:<...>]` — eine Notiz speichern",
        ["help.search"] = "`/search query:<...>` — deine Notizen durchsuchen (nur Titel)",
        ["help.recent"] = "`/recent` — deine neuesten Notizen",
        ["help.upcoming"] = "`/upcoming [days:<n>]` — deine nächsten Termine",
        ["help.help"] = "`/help` — diese Übersicht",
        ["help.secrets"] = "Geheime Inhalte (`:::secret`-Blöcke) zeige ich im Chat nie — öffne sie in der Web-Oberfläche.",

        ["link.missingCode"] = "Bitte gib deinen Code an: `/link <code>`.",
        ["link.already"] = "Dieses Discord-Konto ist schon mit einem Fishbowl verbunden. Trenne es in deinen Fishbowl-Benachrichtigungseinstellungen, bevor du es neu verbindest.",
        ["link.badCode"] = "Der Code hat nicht funktioniert. Erzeuge in deinen Fishbowl-Benachrichtigungseinstellungen einen neuen (Codes laufen nach 10 Minuten ab).",
        ["link.done"] = "Verbunden. Ab jetzt kannst du `/remember`, `/search` und `/recent` nutzen — und ich schicke dir Erinnerungen. `/help` zeigt alle Befehle.",
        ["unlink.notLinked"] = "Dieses Discord-Konto ist mit keinem Fishbowl verbunden. Nichts zu tun.",
        ["unlink.done"] = "Getrennt. Ich vergesse dieses Discord-Konto. Mit `/link <code>` und einem neuen Code kannst du es jederzeit wieder verbinden.",

        ["remember.empty"] = "Was soll ich mir merken? `/remember text:<...>`.",
        ["remember.saved"] = "Gespeichert als **{0}**.",
        ["search.empty"] = "Wonach suchen? `/search query:<...>`.",
        ["search.none"] = "Keine Treffer für **{0}**.",
        ["search.noneDegraded"] = " (die Bedeutungssuche lädt noch — versuch es in ein paar Minuten noch einmal, falls das nicht stimmt)",
        ["search.top"] = "Die {0} besten Treffer für **{1}**:",
        ["search.degraded"] = "_(die Bedeutungssuche lädt noch — sortiert wird vorerst nur nach Wörtern)_",
        ["recent.none"] = "Noch keine Notizen. `/remember text:<...>` ist ein guter Anfang.",
        ["recent.title"] = "Die neuesten {0}:",

        ["upcoming.none.1"] = "Für den nächsten Tag steht nichts im Kalender.",
        ["upcoming.none"] = "Für die nächsten {0} Tage steht nichts im Kalender.",
        ["upcoming.title.1"] = "**Am nächsten Tag** ({0}):",
        ["upcoming.title"] = "**In den nächsten {0} Tagen** ({1}):",
        ["upcoming.allDay"] = "{0} (ganztägig)",
        ["upcoming.more"] = "…und {0} weitere.",

        ["reminder.line"] = "Erinnerung: **{0}** {1}.",
        ["reminder.onAllDay"] = "am {0} (ganztägig)",
        ["reminder.now"] = "jetzt",
        ["reminder.startedAgo"] = "hat vor {0} min begonnen",
        ["reminder.lessThanMinute"] = "in weniger als einer Minute",
        ["reminder.inMinutes"] = "in {0} min",
        ["reminder.inHours"] = "in {0} h {1:D2} min",
        ["reminder.at"] = "am {0} UTC",
        ["reminder.location"] = "Ort: {0}",

        ["digest.hello"] = "**Guten Morgen — das ist dein Tag.**",
        ["digest.today"] = "📅 **Heute:**",
        ["digest.allDay"] = "ganztägig",
        ["digest.todos"] = "✅ **Fällige Aufgaben:**",
        ["digest.overdue"] = " (überfällig)",

        ["cmd.help"] = "Die Befehle des Fishbowl-Bots.",
        ["cmd.link"] = "Dein Discord-Konto mit deinem Fishbowl verbinden.",
        ["cmd.link.code"] = "Der Code aus deinen Fishbowl-Benachrichtigungseinstellungen.",
        ["cmd.unlink"] = "Dieses Discord-Konto von deinem Fishbowl trennen.",
        ["cmd.remember"] = "Etwas in deinem Fishbowl speichern.",
        ["cmd.remember.text"] = "Was du dir merken willst (der Text der Notiz).",
        ["cmd.remember.title"] = "Titel, optional (sonst ein kurzes Stück des Textes).",
        ["cmd.search"] = "Deine Fishbowl-Notizen durchsuchen.",
        ["cmd.search.query"] = "Wonach du suchst.",
        ["cmd.recent"] = "Deine neuesten Fishbowl-Notizen.",
        ["cmd.upcoming"] = "Deine nächsten Fishbowl-Termine.",
        ["cmd.upcoming.days"] = "Wie viele Tage voraus (Standard {0}).",
    };

    private static Dictionary<string, string>? TableFor(string? language) => language switch
    {
        "de" => De,
        _ => null,
    };

    /// <summary>The text for <paramref name="key"/> in <paramref name="language"/>,
    /// with <paramref name="args"/> filled in (string.Format, invariant culture).</summary>
    public static string Get(string key, string? language, params object?[] args)
    {
        var table = TableFor(language);
        var text = table is not null && table.TryGetValue(key, out var t) ? t
            : En.TryGetValue(key, out var e) ? e
            : key;
        return args.Length == 0 ? text : string.Format(CultureInfo.InvariantCulture, text, args);
    }

    /// <summary>Every key the table knows (English is complete).</summary>
    public static IEnumerable<string> Keys => En.Keys;

    /// <summary>Whether the table knows <paramref name="key"/> (in English).</summary>
    public static bool Has(string key) => En.ContainsKey(key);

    /// <summary>Every language's text for a key — Discord's description
    /// localizations. English (the default) is left out.</summary>
    public static IDictionary<string, string> Localizations(string key, params object?[] args)
    {
        var result = new Dictionary<string, string>();
        foreach (var code in Languages.Codes)
        {
            if (code == "en" || TableFor(code) is not { } table || !table.ContainsKey(key)) continue;
            result[code] = Get(key, code, args);
        }
        return result;
    }

    /// <summary>A calendar day as a person reads it: "Thu 1 Oct 2026",
    /// "Do., 1. Okt. 2026". German names are spelled out here rather than
    /// taken from the de-DE culture, whose abbreviations differ between ICU
    /// and Windows NLS ("Sa" vs "Sa.", "Okt" vs "Okt.").</summary>
    public static string Day(DateOnly day, string? language) => language == "de"
        ? $"{DeWeekdays[(int)day.DayOfWeek]}, {day.Day}. {DeMonths[day.Month - 1]} {day.Year}"
        : day.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture);

    private static readonly string[] DeWeekdays = { "So.", "Mo.", "Di.", "Mi.", "Do.", "Fr.", "Sa." };
    private static readonly string[] DeMonths =
        { "Jan.", "Feb.", "März", "Apr.", "Mai", "Juni", "Juli", "Aug.", "Sept.", "Okt.", "Nov.", "Dez." };
}
