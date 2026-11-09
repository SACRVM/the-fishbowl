using System.Globalization;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Files;
using Fishbowl.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Data.Photos;

/// <summary>
/// Makes the photos' previews in the background (spec decision 5), the
/// newest first, so the timeline fills from the top. They live in the
/// workspace's cache folder (<see cref="DatabaseFactory.ResolveCacheFolder"/>),
/// keyed by the photo's id: a move keeps them, a replaced file makes them
/// again (the indexer sets thumb back to 0).
/// </summary>
public sealed class PhotoPreviews
{
    private readonly DatabaseFactory _db;
    private readonly IFileService _files;
    private readonly Thumbnailer _thumbs;
    private readonly ILogger<PhotoPreviews> _logger;

    public PhotoPreviews(DatabaseFactory db, IFileService files, Thumbnailer thumbs, ILogger<PhotoPreviews>? logger = null)
    {
        _db = db;
        _files = files;
        _thumbs = thumbs;
        _logger = logger ?? NullLogger<PhotoPreviews>.Instance;
    }

    public enum Size { Small, Large }

    public static string PathOf(DatabaseFactory db, ContextRef ctx, string photoId, Size size)
    {
        var bucket = photoId.Length >= 2 ? photoId[^2..] : "00";
        return Path.Combine(db.ResolveCacheFolder(ctx), "photos", bucket, $"{photoId}-{(size == Size.Small ? "s" : "l")}.webp");
    }

    /// <summary>Removes a photo's previews (it went for good).</summary>
    public static void Forget(DatabaseFactory db, ContextRef ctx, string photoId)
    {
        foreach (var size in new[] { Size.Small, Size.Large })
        {
            try { File.Delete(PathOf(db, ctx, photoId, size)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class Todo
    {
        public string Id { get; set; } = "";
        public string Path { get; set; } = "";
    }

    /// <summary>Makes up to <paramref name="max"/> photos' previews; how many it tried.</summary>
    public async Task<int> RunAsync(ContextRef ctx, int max, CancellationToken ct = default)
    {
        if (!_thumbs.CanMakeImages) return 0;
        List<Todo> todo;
        using (var db = _db.CreateContextConnection(ctx))
            todo = db.Query<Todo>(@"
                SELECT id, path FROM photos
                WHERE thumb = 0 AND gone_at IS NULL AND (kind <> 'video' OR @videos)
                ORDER BY taken_at DESC LIMIT @max", new { videos = _thumbs.CanMakeVideos, max }).ToList();

        foreach (var t in todo)
        {
            ct.ThrowIfCancellationRequested();
            await MakeAsync(ctx, t.Id, t.Path, ct);
        }
        return todo.Count;
    }

    /// <summary>One photo's previews now (an upload shows at once).</summary>
    public async Task MakeNowAsync(ContextRef ctx, string id, CancellationToken ct = default)
    {
        if (!_thumbs.CanMakeImages) return;
        Todo? t;
        using (var db = _db.CreateContextConnection(ctx))
            t = db.QuerySingleOrDefault<Todo>("SELECT id, path FROM photos WHERE id = @id AND thumb = 0 AND gone_at IS NULL", new { id });
        if (t is not null && _thumbs.CanMake(MediaTypes.KindOf(t.Path))) await MakeAsync(ctx, t.Id, t.Path, ct);
    }

    private async Task MakeAsync(ContextRef ctx, string id, string path, CancellationToken ct)
    {
        string local;
        try { local = await _files.LocalPathAsync(ctx, path, ct); }
        catch (FileStoreException)
        {
            // The file isn't there (the indexer hasn't seen it go yet).
            return;
        }
        var state = PhotoThumbStates.Made;
        Thumbnails? made = null;
        try
        {
            made = await _thumbs.MakeAsync(local, MediaTypes.KindOf(path),
                PathOf(_db, ctx, id, Size.Small), PathOf(_db, ctx, id, Size.Large), ct);
        }
        catch (ThumbnailException ex)
        {
            state = PhotoThumbStates.Failed;
            _logger.LogInformation("No preview for photo {PhotoId}: {Reason}", id, ex.Message);
        }
        using var db = _db.CreateContextConnection(ctx);
        // The size as shown counts for the grid's layout; a video keeps the
        // size its file says.
        db.Execute(@"UPDATE photos SET thumb = @state,
                         width = CASE WHEN @w IS NOT NULL AND kind <> 'video' THEN @w ELSE coalesce(width, @w) END,
                         height = CASE WHEN @h IS NOT NULL AND kind <> 'video' THEN @h ELSE coalesce(height, @h) END,
                         updated_at = @now
                     WHERE id = @id AND path = @path",
            new { state, w = made?.Width, h = made?.Height, id, path, now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) });
    }
}
