using System.Text.Json;
using Fishbowl.Core.Tables;

namespace Fishbowl.Core.Desktop;

// What a custom app shows on its desktop tile — the built-in live tiles'
// model (desktop-live.js): two status lines and a list. A space app's
// .apps/<folder>/tile.js computes it on the server, as the viewer and
// read-only (ISpaceAppTiles); any app may also hand one over while it runs
// (context.tile.set, in the browser). Whatever a script returns is cut to
// size here — text only, never markup.
public static class AppTiles
{
    public const string ScriptName = "tile.js";
    public const int MaxItems = 30;
    public const int MaxSub = 3;
    public const int MaxText = 200;

    /// <summary>
    /// The model a script returned, cleaned: <c>main</c> and <c>empty</c>
    /// (text), <c>sub</c> (text or a list of up to <see cref="MaxSub"/>),
    /// <c>items</c> (up to <see cref="MaxItems"/> rows of lead · text · tail —
    /// absent means the app has no list). Numbers and booleans become text;
    /// anything else is dropped. Null when nothing is left to show.
    /// </summary>
    public static AppTile? Clean(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        var main = Text(value, "main");
        var empty = Text(value, "empty");
        List<string>? sub = null;
        if (value.TryGetProperty("sub", out var s))
        {
            sub = s.ValueKind == JsonValueKind.Array
                ? s.EnumerateArray().Select(TextOf).OfType<string>().Take(MaxSub).ToList()
                : TextOf(s) is { } one ? [one] : null;
            if (sub is { Count: 0 }) sub = null;
        }
        List<AppTileItem>? items = null;
        if (value.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            items = list.EnumerateArray()
                .Where(i => i.ValueKind == JsonValueKind.Object)
                .Select(i => new AppTileItem(Text(i, "lead"), Text(i, "text") ?? "", Text(i, "tail")))
                .Where(i => i.Text.Length > 0 || i.Lead is not null)
                .Take(MaxItems)
                .ToList();
        }
        return main is null && sub is null && items is null ? null : new AppTile(main, sub, items, empty);
    }

    private static string? Text(JsonElement o, string name) => o.TryGetProperty(name, out var v) ? TextOf(v) : null;

    private static string? TextOf(JsonElement v)
    {
        var s = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length <= MaxText ? s : s[..MaxText];
    }
}

public sealed record AppTileItem(string? Lead, string Text, string? Tail);

public sealed record AppTile(string? Main, IReadOnlyList<string>? Sub, IReadOnlyList<AppTileItem>? Items, string? Empty);

/// <summary>Runs a space's .apps/&lt;folder&gt;/tile.js scripts for its desktop.</summary>
public interface ISpaceAppTiles
{
    /// <summary>
    /// Every space app's tile that has a tile.js, by folder — run as the
    /// viewer, read-only. A script that fails or returns nothing has no entry
    /// (a failure goes to the error store, kind <see cref="AppErrors.Tile"/>).
    /// </summary>
    Task<IReadOnlyDictionary<string, AppTile>> RenderAsync(ContextRef ctx, TableActor actor, CancellationToken ct = default);

    /// <summary>Whether the app folder has a tile.js.</summary>
    Task<bool> HasTileAsync(ContextRef ctx, string folder, CancellationToken ct = default);
}
