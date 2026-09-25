using System.Text;

namespace Fishbowl.Core.Util;

public static class TagPalette
{
    public static readonly string[] Slots =
    {
        "blue", "orange", "red", "green", "purple",
        "pink", "yellow", "teal", "gray", "indigo",
    };

    // The kit's --palette-* names double as the accent choices for spaces
    // and a user's personal accent.
    public static bool IsSlot(string? name) => name is not null && Array.IndexOf(Slots, name) >= 0;

    public static string DefaultFor(string name)
    {
        // 32-bit FNV-1a. Mirrored byte-for-byte in tags-registry.js so a tag's
        // unsaved default color matches what the backend would assign on first
        // EnsureExistsAsync — prevents a chip flicker between client/server hash.
        uint hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(name))
        {
            hash = unchecked((hash ^ b) * 16777619u);
        }
        return Slots[(int)(hash % (uint)Slots.Length)];
    }
}
