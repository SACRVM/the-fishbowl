using Fishbowl.Media;

namespace Fishbowl.Data.Photos;

// The Photos app (docs/superpowers/specs/2026-10-30-photos-design.md). A
// photo is one or more files under the workspace's Photos/ folder — its
// parts — plus what files can't hold (favourite, albums, title). The disk
// stays the truth: the indexer follows the files journal.

public static class PhotoLibrary
{
    /// <summary>The library's folder in a workspace's Files.</summary>
    public const string Folder = "Photos";

    /// <summary>
    /// A file of the library: below Photos/, not in a dot folder or a dot
    /// file (Photos/.import/ holds the import's sidecars), and a photo or
    /// video by its name.
    /// </summary>
    public static bool Holds(string path) => InFolder(path) && MediaTypes.IsMedia(path);

    /// <summary>Below Photos/ and in no dot segment.</summary>
    public static bool InFolder(string path)
    {
        if (!path.StartsWith(Folder + "/", StringComparison.Ordinal)) return false;
        foreach (var segment in path[(Folder.Length + 1)..].Split('/'))
            if (segment.Length == 0 || segment[0] == '.') return false;
        return true;
    }

    /// <summary>Photos/2024/07 for a photo taken in July 2024.</summary>
    public static string MonthFolder(string month) => $"{Folder}/{month[..4]}/{month[5..7]}";
}

public static class PhotoKinds
{
    public const string Photo = "photo";
    public const string Video = "video";
    public const string Live = "live";
}

public static class PhotoRoles
{
    public const string Original = "original";
    public const string Edited = "edited";
    public const string Live = "live";
    public const string Raw = "raw";
}

public static class PhotoThumbStates
{
    public const int ToMake = 0;
    public const int Made = 1;
    public const int Failed = 2;
}

public sealed record Photo(
    string Id,
    string Kind,
    string Path,
    DateTime TakenAt,
    int? TakenOffset,
    string Month,
    int? Width,
    int? Height,
    double? Duration,
    double? Latitude,
    double? Longitude,
    string? Camera,
    bool Favorite,
    string? Title,
    string? Description,
    IReadOnlyList<string> Keywords,
    bool Thumb,
    string? CreatedBy,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record PhotoPart(string Role, string Path, long? Size, string? Sha256);

public sealed record PhotoDetail(Photo Photo, IReadOnlyList<PhotoPart> Parts, IReadOnlyList<string> AlbumIds);

public sealed record PhotoMonth(string Month, int Count);

public sealed record PhotoAlbum(
    string Id,
    string Name,
    string? ParentId,
    string Kind,
    double Position,
    int Count,
    string? CoverId,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>Which photos a list shows: the library, the favourites, or one album.</summary>
public sealed record PhotoFilter(bool Favorites = false, string? AlbumId = null)
{
    public static readonly PhotoFilter All = new();
}

public sealed class PhotoException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;

    public static PhotoException NotFound() => new(404, "not_found", "There is no such photo.");
    public static PhotoException AlbumNotFound() => new(404, "album_not_found", "There is no such album.");
    public static PhotoException NameRequired() => new(400, "name_required", "An album needs a name.");
    public static PhotoException NameTooLong() => new(400, "name_too_long", "An album's name is at most 200 characters.");
}
