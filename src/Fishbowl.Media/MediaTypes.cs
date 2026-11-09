namespace Fishbowl.Media;

// What a file in a photo library is, by its name. The Photos app takes only
// these; everything else in the folder (a sidecar, a note) is not a photo.
public enum MediaKind { None, Image, Raw, Video }

public static class MediaTypes
{
    private static readonly Dictionary<string, MediaKind> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = MediaKind.Image,
        [".jpeg"] = MediaKind.Image,
        [".png"] = MediaKind.Image,
        [".gif"] = MediaKind.Image,
        [".webp"] = MediaKind.Image,
        [".heic"] = MediaKind.Image,
        [".heif"] = MediaKind.Image,
        [".avif"] = MediaKind.Image,
        [".tif"] = MediaKind.Image,
        [".tiff"] = MediaKind.Image,
        [".bmp"] = MediaKind.Image,
        [".dng"] = MediaKind.Raw,
        [".cr2"] = MediaKind.Raw,
        [".cr3"] = MediaKind.Raw,
        [".nef"] = MediaKind.Raw,
        [".arw"] = MediaKind.Raw,
        [".orf"] = MediaKind.Raw,
        [".rw2"] = MediaKind.Raw,
        [".raf"] = MediaKind.Raw,
        [".mov"] = MediaKind.Video,
        [".mp4"] = MediaKind.Video,
        [".m4v"] = MediaKind.Video,
        [".3gp"] = MediaKind.Video,
    };

    public static MediaKind KindOf(string path) =>
        Kinds.TryGetValue(Path.GetExtension(path), out var kind) ? kind : MediaKind.None;

    public static bool IsMedia(string path) => KindOf(path) != MediaKind.None;

    /// <summary>The extensions the app takes, for an upload picker.</summary>
    public static IReadOnlyCollection<string> Extensions => Kinds.Keys;
}
