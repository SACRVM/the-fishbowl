using System.Text.Json;
using Fishbowl.Core.Files;
using Fishbowl.Core.Repositories;

namespace Fishbowl.Core.Desktop;

// A space's own apps as they are on disk (SpaceApps): every .apps/<folder>/
// whose app.json reads. One reader for the desktop, the frame and the guide.
public static class SpaceAppCatalog
{
    /// <summary>The apps in a space's .apps — those whose app.json reads, by name.</summary>
    public static async Task<IReadOnlyList<(SpaceAppManifest Manifest, DateTime? Changed)>> ListAsync(
        IFileService files, ContextRef ctx, CancellationToken ct, IAppErrorRepository? errors = null)
    {
        FileListPage page;
        try { page = await files.ListAsync(ctx, AppsFolder.Name, null, 500, ct); }
        catch (FileStoreException) { return Array.Empty<(SpaceAppManifest, DateTime?)>(); }

        var apps = new List<(SpaceAppManifest, DateTime?)>();
        foreach (var dir in page.Entries.Where(e => e.Kind == "folder" && SpaceApps.IsFolder(e.Name)))
        {
            var m = await ReadAsync(files, ctx, dir.Name, ct, errors);
            if (m is not null) apps.Add(m.Value);
        }
        return apps.OrderBy(a => a.Item1.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // A folder without app.json is no app (nothing to report); one whose
    // app.json doesn't read is a broken app — into the error store with why.
    public static async Task<(SpaceAppManifest Manifest, DateTime? Changed)?> ReadAsync(
        IFileService files, ContextRef ctx, string folder, CancellationToken ct, IAppErrorRepository? errors = null)
    {
        string? problem;
        try
        {
            var h = await files.OpenReadAsync(ctx, $"{AppsFolder.Name}/{folder}/{SpaceApps.ManifestFile}", ct);
            await using var stream = h.Stream;
            if (h.Entry.Size > SpaceApps.MaxManifestBytes) problem = $"app.json is larger than {SpaceApps.MaxManifestBytes / 1024} KB.";
            else
            {
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var m = SpaceApps.Parse(folder, doc.RootElement, out problem);
                if (m is not null) return (m, h.Entry.Mtime);
            }
        }
        catch (FileStoreException) { return null; }
        catch (JsonException ex) { problem = $"app.json isn't valid JSON: {ex.Message}"; }

        if (errors is not null && problem is not null)
            await errors.AddAsync(ctx, folder, AppErrors.Manifest, problem, null, null, ct);
        return null;
    }
}
