namespace Fishbowl.Core.Files;

// A space's own apps live as files in a hidden folder of its tree,
// `files/.apps/<app>/` (space-apps spec, "Where things live"). Every member
// reads it; writing, deleting, moving or copying into or out of it needs the
// Designer role (or a key with `design:apps`), and the folder itself can't be
// renamed or deleted. The personal workspace has no `.apps`.
public static class AppsFolder
{
    public const string Name = ".apps";

    /// <summary>Is the wire path `.apps` or anything under it?</summary>
    public static bool Contains(string? path)
    {
        var p = (path ?? "").Trim('/');
        return p.Equals(Name, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(Name + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Is the wire path the `.apps` folder itself?</summary>
    public static bool IsRoot(string? path) => (path ?? "").Trim('/').Equals(Name, StringComparison.OrdinalIgnoreCase);

    public static FileStoreException SpaceOnly() =>
        new(400, "apps_space_only", "App code (.apps) lives in spaces only.");

    public static FileStoreException DesignOnly() =>
        new(403, "apps_design_only", "Changing app code (.apps) needs the Designer role in this space.");

    public static FileStoreException Fixed() =>
        new(409, "apps_folder_fixed", "The .apps folder itself can't be renamed, moved or deleted.");
}
