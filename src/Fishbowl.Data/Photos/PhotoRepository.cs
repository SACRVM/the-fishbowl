using System.Globalization;
using System.Text.Json;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Files;
using Fishbowl.Media;

namespace Fishbowl.Data.Photos;

/// <summary>
/// What the Photos app reads and changes. Photos themselves come and go with
/// their files (<see cref="PhotoIndexer"/>); here are the timeline, the
/// favourites, albums, deleting (the files go to the Files trash) and the
/// upload, which files a photo by the date it says it was taken.
/// </summary>
public sealed class PhotoRepository
{
    public const int MaxAlbumName = 200;
    public const int MaxIdsPerCall = 1000;

    private readonly DatabaseFactory _db;
    private readonly IFileService _files;
    private readonly PhotoIndexer _indexer;

    public PhotoRepository(DatabaseFactory db, IFileService files, PhotoIndexer indexer)
    {
        _db = db;
        _files = files;
        _indexer = indexer;
    }

    private sealed class PhotoRow
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Path { get; set; } = "";
        public string TakenAt { get; set; } = "";
        public long? TakenOffset { get; set; }
        public string Month { get; set; } = "";
        public long? Width { get; set; }
        public long? Height { get; set; }
        public double? Duration { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public string? Camera { get; set; }
        public long Favorite { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }
        public string Keywords { get; set; } = "[]";
        public long Thumb { get; set; }
        public string? CreatedBy { get; set; }
        public string CreatedAt { get; set; } = "";
        public string UpdatedAt { get; set; } = "";
    }

    private static DateTime Parse(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static Photo ToPhoto(PhotoRow r) => new(
        r.Id, r.Kind, r.Path, Parse(r.TakenAt), (int?)r.TakenOffset, r.Month, (int?)r.Width, (int?)r.Height, r.Duration,
        r.Latitude, r.Longitude, r.Camera, r.Favorite != 0, r.Title, r.Description,
        JsonSerializer.Deserialize<string[]>(r.Keywords) ?? [], r.Thumb == PhotoThumbStates.Made, r.CreatedBy,
        Parse(r.CreatedAt), Parse(r.UpdatedAt));

    private static (string Where, object Args) Filter(PhotoFilter f)
    {
        var where = "p.gone_at IS NULL";
        if (f.Favorites) where += " AND p.favorite = 1";
        if (f.AlbumId is not null) where += " AND p.id IN (SELECT photo_id FROM photo_album_items WHERE album_id = @album)";
        return (where, new { album = f.AlbumId });
    }

    // ───────────────────────────── reading ─────────────────────────────

    /// <summary>The timeline's sections: every month with photos, newest first.</summary>
    public async Task<IReadOnlyList<PhotoMonth>> MonthsAsync(ContextRef ctx, PhotoFilter filter, CancellationToken ct = default)
    {
        var (where, args) = Filter(filter);
        using var db = _db.CreateContextConnection(ctx);
        var rows = await db.QueryAsync<(string Month, long Count)>(new CommandDefinition(
            $"SELECT p.month, COUNT(*) FROM photos p WHERE {where} GROUP BY p.month ORDER BY p.month DESC", args, cancellationToken: ct));
        return rows.Select(r => new PhotoMonth(r.Month, (int)r.Count)).ToList();
    }

    /// <summary>One month's photos, newest first.</summary>
    public async Task<IReadOnlyList<Photo>> MonthAsync(ContextRef ctx, PhotoFilter filter, string month, CancellationToken ct = default)
    {
        var (where, _) = Filter(filter);
        using var db = _db.CreateContextConnection(ctx);
        var rows = await db.QueryAsync<PhotoRow>(new CommandDefinition(
            $"SELECT p.* FROM photos p WHERE {where} AND p.month = @month ORDER BY p.taken_at DESC, p.id DESC",
            new { album = filter.AlbumId, month }, cancellationToken: ct));
        return rows.Select(ToPhoto).ToList();
    }

    public async Task<PhotoDetail?> GetAsync(ContextRef ctx, string id, CancellationToken ct = default)
    {
        using var db = _db.CreateContextConnection(ctx);
        var row = await db.QuerySingleOrDefaultAsync<PhotoRow>(new CommandDefinition(
            "SELECT * FROM photos WHERE id = @id AND gone_at IS NULL", new { id }, cancellationToken: ct));
        if (row is null) return null;
        var parts = (await db.QueryAsync<PhotoPart>(new CommandDefinition(
            "SELECT role, path, size, sha256 FROM photo_parts WHERE photo_id = @id ORDER BY role", new { id }, cancellationToken: ct))).ToList();
        var albums = (await db.QueryAsync<string>(new CommandDefinition(
            "SELECT album_id FROM photo_album_items WHERE photo_id = @id", new { id }, cancellationToken: ct))).ToList();
        return new PhotoDetail(ToPhoto(row), parts, albums);
    }

    // ───────────────────────────── favourites ─────────────────────────────

    public async Task<int> SetFavoriteAsync(ContextRef ctx, IReadOnlyList<string> ids, bool favorite, CancellationToken ct = default)
    {
        if (ids.Count == 0) return 0;
        using var db = _db.CreateContextConnection(ctx);
        return await db.ExecuteAsync(new CommandDefinition(
            "UPDATE photos SET favorite = @f, updated_at = @now WHERE id IN @ids AND gone_at IS NULL",
            new { f = favorite ? 1 : 0, ids = ids.Take(MaxIdsPerCall).ToArray(), now = Now() }, cancellationToken: ct));
    }

    // ───────────────────────────── deleting ─────────────────────────────

    /// <summary>
    /// Every part's file to the Files trash; the photo is gone at once and
    /// comes back with its albums if its file is restored there.
    /// </summary>
    public async Task<int> TrashAsync(ContextRef ctx, IReadOnlyList<string> ids, string actor, CancellationToken ct = default)
    {
        var done = 0;
        foreach (var id in ids.Distinct().Take(MaxIdsPerCall))
        {
            List<string> paths;
            using (var db = _db.CreateContextConnection(ctx))
                paths = db.Query<string>(@"SELECT pp.path FROM photo_parts pp JOIN photos p ON p.id = pp.photo_id
                                           WHERE pp.photo_id = @id AND p.gone_at IS NULL", new { id }).ToList();
            if (paths.Count == 0) continue;
            foreach (var path in paths)
            {
                try { await _files.TrashAsync(ctx, path, actor, ct); }
                catch (FileStoreException e) when (e.Status == 404) { }
            }
            using (var db = _db.CreateContextConnection(ctx))
                db.Execute("UPDATE photos SET gone_at = @now, updated_at = @now WHERE id = @id AND gone_at IS NULL", new { id, now = Now() });
            done++;
        }
        return done;
    }

    // ───────────────────────────── albums ─────────────────────────────

    private sealed class AlbumRow
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string? ParentId { get; set; }
        public string Kind { get; set; } = "";
        public double Position { get; set; }
        public long Count { get; set; }
        public string? CoverId { get; set; }
        public string CreatedAt { get; set; } = "";
        public string UpdatedAt { get; set; } = "";
    }

    public async Task<IReadOnlyList<PhotoAlbum>> AlbumsAsync(ContextRef ctx, CancellationToken ct = default)
    {
        using var db = _db.CreateContextConnection(ctx);
        // The cover is the album's newest photo.
        var rows = await db.QueryAsync<AlbumRow>(new CommandDefinition(@"
            SELECT a.id, a.name, a.parent_id, a.kind, a.position, a.created_at, a.updated_at,
                   (SELECT COUNT(*) FROM photo_album_items i JOIN photos p ON p.id = i.photo_id
                    WHERE i.album_id = a.id AND p.gone_at IS NULL) AS count,
                   (SELECT p.id FROM photo_album_items i JOIN photos p ON p.id = i.photo_id
                    WHERE i.album_id = a.id AND p.gone_at IS NULL ORDER BY p.taken_at DESC LIMIT 1) AS cover_id
            FROM photo_albums a ORDER BY a.position, a.name", cancellationToken: ct));
        return rows.Select(r => new PhotoAlbum(r.Id, r.Name, r.ParentId, r.Kind, r.Position, (int)r.Count, r.CoverId,
            Parse(r.CreatedAt), Parse(r.UpdatedAt))).ToList();
    }

    private static string CheckName(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) throw PhotoException.NameRequired();
        if (n.Length > MaxAlbumName) throw PhotoException.NameTooLong();
        return n;
    }

    public async Task<PhotoAlbum> CreateAlbumAsync(ContextRef ctx, string? name, string actor, CancellationToken ct = default)
    {
        var n = CheckName(name);
        var id = Ulid.NewUlid().ToString();
        var now = Now();
        using (var db = _db.CreateContextConnection(ctx))
        {
            var position = (db.ExecuteScalar<double?>("SELECT MAX(position) FROM photo_albums") ?? 0) + 1;
            await db.ExecuteAsync(new CommandDefinition(@"
                INSERT INTO photo_albums (id, name, parent_id, kind, position, created_by, created_at, updated_at)
                VALUES (@id, @n, NULL, 'album', @position, @actor, @now, @now)", new { id, n, position, actor, now }, cancellationToken: ct));
        }
        return (await AlbumsAsync(ctx, ct)).First(a => a.Id == id);
    }

    public async Task RenameAlbumAsync(ContextRef ctx, string id, string? name, CancellationToken ct = default)
    {
        var n = CheckName(name);
        using var db = _db.CreateContextConnection(ctx);
        if (await db.ExecuteAsync(new CommandDefinition("UPDATE photo_albums SET name = @n, updated_at = @now WHERE id = @id",
                new { n, id, now = Now() }, cancellationToken: ct)) == 0)
            throw PhotoException.AlbumNotFound();
    }

    /// <summary>Deletes the album; its photos stay in the library.</summary>
    public Task DeleteAlbumAsync(ContextRef ctx, string id, CancellationToken ct = default) =>
        _db.WithContextTransactionAsync(ctx, (db, tx, _) =>
        {
            var ids = db.Query<string>(@"
                WITH RECURSIVE sub(id) AS (SELECT @id UNION SELECT a.id FROM photo_albums a JOIN sub ON a.parent_id = sub.id)
                SELECT id FROM sub", new { id }, tx).ToArray();
            if (db.ExecuteScalar<long>("SELECT COUNT(*) FROM photo_albums WHERE id = @id", new { id }, tx) == 0)
                throw PhotoException.AlbumNotFound();
            db.Execute("DELETE FROM photo_album_items WHERE album_id IN @ids", new { ids }, tx);
            db.Execute("DELETE FROM photo_albums WHERE id IN @ids", new { ids }, tx);
            return Task.CompletedTask;
        }, ct);

    public Task<int> AddToAlbumAsync(ContextRef ctx, string albumId, IReadOnlyList<string> ids, CancellationToken ct = default) =>
        _db.WithContextTransactionAsync(ctx, (db, tx, _) =>
        {
            if (db.ExecuteScalar<long>("SELECT COUNT(*) FROM photo_albums WHERE id = @albumId", new { albumId }, tx) == 0)
                throw PhotoException.AlbumNotFound();
            var position = db.ExecuteScalar<double?>("SELECT MAX(position) FROM photo_album_items WHERE album_id = @albumId",
                new { albumId }, tx) ?? 0;
            var now = Now();
            var added = 0;
            foreach (var id in ids.Distinct().Take(MaxIdsPerCall))
                added += db.Execute(@"
                    INSERT OR IGNORE INTO photo_album_items (album_id, photo_id, position, added_at)
                    SELECT @albumId, id, @position, @now FROM photos WHERE id = @id AND gone_at IS NULL",
                    new { albumId, id, position = ++position, now }, tx);
            db.Execute("UPDATE photo_albums SET updated_at = @now WHERE id = @albumId", new { albumId, now }, tx);
            return Task.FromResult(added);
        }, ct);

    public Task<int> RemoveFromAlbumAsync(ContextRef ctx, string albumId, IReadOnlyList<string> ids, CancellationToken ct = default) =>
        _db.WithContextTransactionAsync(ctx, (db, tx, _) =>
        {
            if (db.ExecuteScalar<long>("SELECT COUNT(*) FROM photo_albums WHERE id = @albumId", new { albumId }, tx) == 0)
                throw PhotoException.AlbumNotFound();
            var removed = db.Execute("DELETE FROM photo_album_items WHERE album_id = @albumId AND photo_id IN @ids",
                new { albumId, ids = ids.Take(MaxIdsPerCall).ToArray() }, tx);
            db.Execute("UPDATE photo_albums SET updated_at = @now WHERE id = @albumId", new { albumId, now = Now() }, tx);
            return Task.FromResult(removed);
        }, ct);

    // ───────────────────────────── upload ─────────────────────────────

    /// <summary>
    /// Takes one uploaded photo or video: what it says it is decides where it
    /// goes — Photos/&lt;year&gt;/&lt;month&gt;/ by the day it was taken (the
    /// upload's day without one), a name that is taken gets " (2)". Then the
    /// indexer runs, so the photo is listed when this returns.
    /// </summary>
    public async Task<Photo?> UploadAsync(ContextRef ctx, string fileName, Stream body, long? contentLength, string actor,
        CancellationToken ct = default)
    {
        var name = Path.GetFileName(fileName.Replace('\\', '/'));
        if (!MediaTypes.IsMedia(name))
            throw new FileStoreException(400, "not_a_photo", "Only photos and videos go into the library.");
        if (contentLength is { } len) await _files.CheckRoomAsync(ctx, len, ct);

        // The file has to be read before its folder is known: it goes to a
        // temporary file in the cache first (outside quota), then through
        // the Files entry point like any upload.
        var tempDir = Path.Combine(_db.ResolveCacheFolder(ctx), "upload");
        Directory.CreateDirectory(tempDir);
        var temp = Path.Combine(tempDir, Ulid.NewUlid() + ".part");
        try
        {
            // Bounded like any upload, also when no length was sent.
            var max = (await _files.GetCapabilitiesAsync(ctx, ct)).MaxFileBytes;
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await body.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > max) throw FileStoreException.TooLarge("file", "That file is larger than this server takes.");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }

            MediaInfo info;
            await using (var read = File.OpenRead(temp))
                info = MediaReader.Read(read, name);
            var month = (info.TakenAt?.DateTime ?? DateTime.UtcNow).ToString("yyyy-MM", CultureInfo.InvariantCulture);
            var folder = PhotoLibrary.MonthFolder(month);

            var stem = Path.GetFileNameWithoutExtension(name);
            var ext = Path.GetExtension(name);
            for (var n = 1; ; n++)
            {
                var path = $"{folder}/{(n == 1 ? stem : $"{stem} ({n})")}{ext}";
                try
                {
                    await using var source = File.OpenRead(temp);
                    await _files.UploadAsync(ctx, path, source, source.Length, ifMatch: null, ifNoneMatchStar: true,
                        parents: true, actor, ct);
                    await _indexer.RunAsync(ctx, ct);
                    using var db = _db.CreateContextConnection(ctx);
                    var row = db.QuerySingleOrDefault<PhotoRow>(@"SELECT p.* FROM photos p JOIN photo_parts pp ON pp.photo_id = p.id
                                                                 WHERE pp.path = @path AND p.gone_at IS NULL", new { path });
                    return row is null ? null : ToPhoto(row);
                }
                catch (FileStoreException e) when (e.Status == 412 && n < 1000) { }
            }
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
        }
    }

    private static string Now() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
}
