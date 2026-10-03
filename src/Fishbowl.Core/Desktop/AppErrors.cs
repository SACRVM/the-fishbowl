namespace Fishbowl.Core.Desktop;

// A space's error store for its own apps (space-apps spec, decision 19): what
// broke, so a Designer — or their agent — can see why an app doesn't work.
// The server writes what it sees (an app.json that doesn't read); the
// browser writes what happens in the frame (a script that doesn't load, a
// refused call, what the app reports itself). Only the newest Keep stay.
public static class AppErrors
{
    public const int Keep = 500;
    public const int MaxMessage = 1000;
    public const int MaxDetail = 4000;

    // The server's: an app.json that doesn't read.
    public const string Manifest = "manifest";
    // The browser's.
    public const string Load = "load";          // the entry didn't load or define its element
    public const string Runtime = "runtime";    // an uncaught error in the frame (kit issue #39)
    public const string Call = "call";          // a context.space call the server refused
    public const string Reported = "reported";  // the app's own context.space.error(…)

    public static readonly IReadOnlySet<string> FromBrowser = new HashSet<string> { Load, Runtime, Call, Reported };

    public static string Clip(string? s, int max) => s is null ? "" : s.Length <= max ? s : s[..max];
}

public sealed record AppError(string Id, DateTime At, string App, string Kind, string Message, string? Detail, string? UserId);
