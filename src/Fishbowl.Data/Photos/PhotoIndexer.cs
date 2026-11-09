using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Files;
using Fishbowl.Core.Repositories;
using Fishbowl.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Data.Photos;

/// <summary>
/// Keeps a workspace's photos in step with its Photos/ folder (spec decision
/// 3): it reads the files journal from where it last stopped and turns what
/// changed into photos. A new file becomes a photo, or a part of one — a Live
/// Photo's movie beside its still (Apple's content identifier, else the same
/// name and a clip of a few seconds), a RAW beside its JPEG, an
/// "_edited" copy beside its original. A file moved keeps its photo; a file
/// that left the library (the trash, a move out) leaves its photo "gone",
/// and the photo comes back with its albums when the file does. A journal it
/// can no longer read (another epoch, compacted) means one full scan.
/// </summary>
public sealed class PhotoIndexer
{
    public const int PageSize = 500;
    // A Live Photo's movie is about three seconds.
    private const double LiveMovieMaxSeconds = 6;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);

    private readonly DatabaseFactory _db;
    private readonly IFileService _files;
    private readonly ISystemRepository _system;
    private readonly ILogger<PhotoIndexer> _logger;

    public PhotoIndexer(DatabaseFactory db, IFileService files, ISystemRepository system, ILogger<PhotoIndexer>? logger = null)
    {
        _db = db;
        _files = files;
        _system = system;
        _logger = logger ?? NullLogger<PhotoIndexer>.Instance;
    }

    /// <summary>Brings the photos up to date; how many files it looked at.</summary>
    public async Task<int> RunAsync(ContextRef ctx, CancellationToken ct = default)
    {
        var gate = Locks.GetOrAdd($"{ctx.Type}:{ctx.Id}", _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var seen = 0;
            var cursor = ReadCursor(ctx);
            if (cursor is null) seen += await ScanAsync(ctx, ct);
            else
            {
                while (true)
                {
                    FileChangesPage page;
                    try { page = await _files.GetChangesAsync(ctx, cursor, PageSize, reconcile: false, ct); }
                    catch (FileStoreException e) when (e.Code == "cursor_expired")
                    {
                        seen += await ScanAsync(ctx, ct);
                        break;
                    }
                    foreach (var change in page.Changes)
                    {
                        ct.ThrowIfCancellationRequested();
                        seen += await ApplyAsync(ctx, change, ct);
                    }
                    if (page.Cursor != cursor) SaveCursor(ctx, page.Cursor);
                    cursor = page.Cursor;
                    if (!page.More) break;
                }
            }
            await PurgeGoneAsync(ctx, ct);
            return seen;
        }
        finally { gate.Release(); }
    }

    // ───────────────────────────── journal ─────────────────────────────

    private static bool FolderInLibrary(string path) => path == PhotoLibrary.Folder || PhotoLibrary.InFolder(path);

    private async Task<int> ApplyAsync(ContextRef ctx, FileChange c, CancellationToken ct)
    {
        if (c.Kind == FileKinds.Folder)
        {
            switch (c.Op)
            {
                case "move" when c.OldPath is { } old:
                    var oldIn = FolderInLibrary(old);
                    var newIn = FolderInLibrary(c.Path);
                    if (oldIn && newIn) await RePrefixAsync(ctx, old, c.Path, ct);
                    else if (oldIn) await GoneUnderAsync(ctx, old, ct);
                    else if (newIn) return await ScanFolderAsync(ctx, c.Path, ct);
                    return 0;
                case "delete" when FolderInLibrary(c.Path):
                    await GoneUnderAsync(ctx, c.Path, ct);
                    return 0;
                case "create" when FolderInLibrary(c.Path):
                    // A folder restored from the trash or copied in brings its
                    // files as their own entries; one moved in on the disk may not.
                    return await ScanFolderAsync(ctx, c.Path, ct);
                default:
                    return 0;
            }
        }

        switch (c.Op)
        {
            case "create" or "modify" when PhotoLibrary.Holds(c.Path):
                await UpsertAsync(ctx, c.Path, c.Sha256, c.Size, c.Actor, ct);
                return 1;
            case "move" when c.OldPath is { } old:
                var had = PhotoLibrary.Holds(old);
                var has = PhotoLibrary.Holds(c.Path);
                if (had && has) await MovePartAsync(ctx, old, c.Path, c.Sha256, c.Size, c.Actor, ct);
                else if (had) await GoneAsync(ctx, old, ct);
                else if (has) await UpsertAsync(ctx, c.Path, c.Sha256, c.Size, c.Actor, ct);
                return had || has ? 1 : 0;
            case "delete" when PhotoLibrary.Holds(c.Path):
                await GoneAsync(ctx, c.Path, ct);
                return 1;
            default:
                return 0;
        }
    }

    // ───────────────────────────── full scan ─────────────────────────────

    private async Task<int> ScanAsync(ContextRef ctx, CancellationToken ct)
    {
        bool hasLibrary;
        try { hasLibrary = (await _files.StatAsync(ctx, PhotoLibrary.Folder, ct)).Kind == FileKinds.Folder; }
        catch (FileStoreException e) when (e.Status == 404) { hasLibrary = false; }

        if (!hasLibrary)
        {
            // Nothing to read: start at the head, and whatever photos are
            // still listed lost their files.
            var head = await _files.GetChangesAsync(ctx, null, null, reconcile: false, ct);
            await GoneUnderAsync(ctx, PhotoLibrary.Folder, ct);
            SaveCursor(ctx, head.Cursor);
            return 0;
        }

        var (cursor, entries) = await _files.SnapshotAsync(ctx, ct);
        var present = entries.Where(e => e.Kind == FileKinds.File && PhotoLibrary.Holds(e.Path))
            .ToDictionary(e => e.Path, StringComparer.Ordinal);
        List<string> known;
        using (var db = _db.CreateContextConnection(ctx))
            known = db.Query<string>("SELECT path FROM photo_parts").ToList();
        foreach (var path in known.Where(p => !present.ContainsKey(p)))
            await GoneAsync(ctx, path, ct);
        var knownSet = known.ToHashSet(StringComparer.Ordinal);
        var seen = 0;
        // Oldest folder first is the order an export was made in; within a
        // folder, names keep a Live Photo's still and movie side by side.
        foreach (var entry in present.Values.OrderBy(e => e.Path, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (knownSet.Contains(entry.Path))
            {
                using var db = _db.CreateContextConnection(ctx);
                var gone = db.ExecuteScalar<string?>(
                    "SELECT p.gone_at FROM photo_parts pp JOIN photos p ON p.id = pp.photo_id WHERE pp.path = @path", new { path = entry.Path });
                if (gone is null) continue;
            }
            await UpsertAsync(ctx, entry.Path, entry.Sha256, entry.Size, null, ct);
            seen++;
        }
        SaveCursor(ctx, cursor);
        return seen;
    }

    private async Task<int> ScanFolderAsync(ContextRef ctx, string folder, CancellationToken ct)
    {
        var seen = 0;
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            string? next = null;
            do
            {
                FileListPage page;
                try { page = await _files.ListAsync(ctx, current, next, 1000, ct); }
                catch (FileStoreException e) when (e.Status is 404 or 400) { break; }
                foreach (var entry in page.Entries)
                {
                    if (entry.Kind == FileKinds.Folder && FolderInLibrary(entry.Path)) pending.Push(entry.Path);
                    else if (entry.Kind == FileKinds.File && PhotoLibrary.Holds(entry.Path))
                    {
                        await UpsertAsync(ctx, entry.Path, entry.Sha256, entry.Size, entry.CreatedBy, ct);
                        seen++;
                    }
                }
                next = page.Next;
            } while (next is not null);
        }
        return seen;
    }

    // ───────────────────────────── a file came ─────────────────────────────

    private sealed class PartRow
    {
        public string PhotoId { get; set; } = "";
        public string Role { get; set; } = "";
        public string Path { get; set; } = "";
        public long? Size { get; set; }
        public string? Sha256 { get; set; }
        public string? GoneAt { get; set; }
        public string PhotoPath { get; set; } = "";
        public string Kind { get; set; } = "";
        public string? ContentId { get; set; }
        public double? Duration { get; set; }
    }

    private const string PartSelect = @"
        SELECT pp.photo_id, pp.role, pp.path, pp.size, pp.sha256, p.gone_at, p.path AS photo_path, p.kind,
               p.content_id, p.duration
        FROM photo_parts pp JOIN photos p ON p.id = pp.photo_id";

    private async Task UpsertAsync(ContextRef ctx, string path, string? sha, long? size, string? actor, CancellationToken ct)
    {
        // Already a part: the file changed (a replace) or was seen again.
        PartRow? existing;
        using (var db = _db.CreateContextConnection(ctx))
            existing = db.QuerySingleOrDefault<PartRow>(PartSelect + " WHERE pp.path = @path", new { path });
        if (existing is not null)
        {
            var sameFile = existing.Sha256 is null || sha is null ? existing.Size == size || size is null : existing.Sha256 == sha;
            if (existing.GoneAt is not null && !sameFile)
            {
                // Another file took the place of a gone photo's: that photo is over.
                await DeletePhotosAsync(ctx, [existing.PhotoId], ct);
            }
            else
            {
                var changed = !sameFile;
                Media? info = changed && (existing.Role == PhotoRoles.Original || existing.PhotoPath == path)
                    ? await ReadAsync(ctx, path, ct) : null;
                await _db.WithContextTransactionAsync(ctx, (db, tx, _) =>
                {
                    db.Execute("UPDATE photo_parts SET size = coalesce(@size, size), sha256 = coalesce(@sha, sha256) WHERE path = @path",
                        new { size, sha, path }, tx);
                    db.Execute(@"UPDATE photos SET gone_at = NULL, updated_at = @now,
                                     thumb = CASE WHEN @changed AND path = @path THEN 0 ELSE thumb END
                                 WHERE id = @id",
                        new { id = existing.PhotoId, now = Now(), changed, path }, tx);
                    if (info is not null && existing.Role == PhotoRoles.Original) WriteMetadata(db, tx, existing.PhotoId, info.Value, overwrite: true);
                    return Task.CompletedTask;
                }, ct);
                return;
            }
        }

        // The same bytes as a gone photo's part: it comes back where it lies now.
        if (sha is not null)
        {
            PartRow? twin;
            using (var db = _db.CreateContextConnection(ctx))
                twin = db.QueryFirstOrDefault<PartRow>(PartSelect + " WHERE pp.sha256 = @sha AND p.gone_at IS NOT NULL", new { sha });
            if (twin is not null)
            {
                await _db.WithContextTransactionAsync(ctx, (db, tx, _) =>
                {
                    db.Execute("UPDATE photo_parts SET path = @path WHERE photo_id = @id AND role = @role",
                        new { path, id = twin.PhotoId, role = twin.Role }, tx);
                    db.Execute(@"UPDATE photos SET gone_at = NULL, updated_at = @now,
                                     path = CASE WHEN path = @old THEN @path ELSE path END
                                 WHERE id = @id",
                        new { id = twin.PhotoId, now = Now(), old = twin.Path, path }, tx);
                    return Task.CompletedTask;
                }, ct);
                return;
            }
        }

        var read = await ReadAsync(ctx, path, ct);
        if (read is not { } media) return;   // vanished before it could be read
        var kind = MediaTypes.KindOf(path);
        var target = FindPartner(ctx, path, kind, media);

        await _db.WithContextTransactionAsync(ctx, (db, tx, _) =>
        {
            if (target is { } t)
            {
                Attach(db, tx, t, path, kind, size, sha, media);
                return Task.CompletedTask;
            }
            var id = Ulid.NewUlid().ToString();
            var now = Now();
            var (takenAt, offset, month) = Taken(media);
            db.Execute(@"
                INSERT INTO photos (id, kind, path, taken_at, taken_offset, month, width, height, duration, latitude, longitude,
                                    camera, favorite, keywords, content_id, burst_id, thumb, created_by, created_at, updated_at)
                VALUES (@id, @kind, @path, @takenAt, @offset, @month, @width, @height, @duration, @lat, @lon,
                        @camera, 0, '[]', @contentId, @burstId, 0, @actor, @now, @now)",
                new
                {
                    id,
                    kind = kind == MediaKind.Video ? PhotoKinds.Video : PhotoKinds.Photo,
                    path,
                    takenAt,
                    offset,
                    month,
                    width = media.Info.Width,
                    height = media.Info.Height,
                    duration = media.Info.DurationSeconds,
                    lat = media.Info.Latitude,
                    lon = media.Info.Longitude,
                    camera = Camera(media.Info),
                    contentId = media.Info.ContentId,
                    burstId = media.Info.BurstId,
                    actor,
                    now,
                }, tx);
            db.Execute("INSERT INTO photo_parts (photo_id, role, path, size, sha256) VALUES (@id, @role, @path, @size, @sha)",
                new { id, role = PhotoRoles.Original, path, size, sha }, tx);
            return Task.CompletedTask;
        }, ct);
    }

    // What the file says, with the time it was last changed as the date of a
    // file that says none.
    private readonly record struct Media(MediaInfo Info, DateTime Mtime);

    private async Task<Media?> ReadAsync(ContextRef ctx, string path, CancellationToken ct)
    {
        try
        {
            var handle = await _files.OpenReadAsync(ctx, path, ct);
            await using var stream = handle.Stream;
            var info = MediaReader.Read(stream, path);
            return new Media(info, handle.Entry.Mtime ?? DateTime.UtcNow);
        }
        catch (FileStoreException e) when (e.Status is 404 or 400 or 423) { return null; }
    }

    private static (string TakenAt, int? Offset, string Month) Taken(Media m)
    {
        if (m.Info.TakenAt is { } t)
        {
            var utc = m.Info.OffsetKnown ? t.UtcDateTime : DateTime.SpecifyKind(t.DateTime, DateTimeKind.Utc);
            return (utc.ToString("o", CultureInfo.InvariantCulture),
                m.Info.OffsetKnown ? (int)t.Offset.TotalMinutes : null,
                t.DateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture));
        }
        var mtime = DateTime.SpecifyKind(m.Mtime, DateTimeKind.Utc);
        return (mtime.ToString("o", CultureInfo.InvariantCulture), null, mtime.ToString("yyyy-MM", CultureInfo.InvariantCulture));
    }

    private static string? Camera(MediaInfo i)
    {
        if (i.Model is null) return i.Make;
        if (i.Make is null || i.Model.StartsWith(i.Make, StringComparison.OrdinalIgnoreCase)) return i.Model;
        return $"{i.Make} {i.Model}";
    }

    // A photo's date, size, place and camera from a file: overwrite = its
    // original (what it says counts), else only what the photo lacks.
    private static void WriteMetadata(IDbConnection db, IDbTransaction tx, string id, Media m, bool overwrite)
    {
        var (takenAt, offset, month) = Taken(m);
        var known = m.Info.TakenAt is not null;
        db.Execute(overwrite
            ? @"UPDATE photos SET
                    taken_at = CASE WHEN @known THEN @takenAt ELSE taken_at END,
                    taken_offset = CASE WHEN @known THEN @offset ELSE taken_offset END,
                    month = CASE WHEN @known THEN @month ELSE month END,
                    width = coalesce(@width, width), height = coalesce(@height, height),
                    latitude = coalesce(@lat, latitude), longitude = coalesce(@lon, longitude),
                    camera = coalesce(@camera, camera), content_id = coalesce(content_id, @contentId),
                    burst_id = coalesce(burst_id, @burstId)
                WHERE id = @id"
            : @"UPDATE photos SET
                    latitude = coalesce(latitude, @lat), longitude = coalesce(longitude, @lon),
                    camera = coalesce(camera, @camera), content_id = coalesce(content_id, @contentId)
                WHERE id = @id",
            new
            {
                id,
                known,
                takenAt,
                offset,
                month,
                width = m.Info.Width,
                height = m.Info.Height,
                lat = m.Info.Latitude,
                lon = m.Info.Longitude,
                camera = Camera(m.Info),
                contentId = m.Info.ContentId,
                burstId = m.Info.BurstId,
            }, tx);
    }

    // ───────────────────────────── pairing ─────────────────────────────

    private enum Join { LiveMovie, StillOfLive, Raw, StillOfRaw, Edited, OriginalOfEdited }

    private readonly record struct Partner(string PhotoId, Join How, string? MovedPartPath);

    // "IMG_0001" for IMG_0001.HEIC, IMG_0001.MOV and IMG_0001_edited.jpeg.
    private static string Stem(string path, out bool edited)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        edited = name.EndsWith("_edited", StringComparison.OrdinalIgnoreCase);
        return edited ? name[..^"_edited".Length] : name;
    }

    private Partner? FindPartner(ContextRef ctx, string path, MediaKind kind, Media media)
    {
        var folder = path[..path.LastIndexOf('/')];
        var stem = Stem(path, out var edited);
        using var db = _db.CreateContextConnection(ctx);

        // Every part beside it with the same stem, grouped by photo.
        var lo = folder + "/" + stem;
        var siblings = db.Query<PartRow>(PartSelect + " WHERE pp.path >= @lo AND pp.path < @hi AND p.gone_at IS NULL",
                new { lo, hi = lo + "￿" })
            .Where(p => p.Path[..p.Path.LastIndexOf('/')] == folder
                        && string.Equals(Stem(p.Path, out _), stem, StringComparison.OrdinalIgnoreCase))
            .ToList();
        // A Live Photo's halves by Apple's content identifier, wherever they lie.
        if (media.Info.ContentId is { } cid)
            siblings.AddRange(db.Query<PartRow>(PartSelect + " WHERE p.content_id = @cid AND p.gone_at IS NULL", new { cid })
                .Where(p => siblings.All(s => s.Path != p.Path)));

        foreach (var group in siblings.GroupBy(p => p.PhotoId))
        {
            var parts = group.ToList();
            var roles = parts.ToDictionary(p => p.Role, p => p.Path);
            var first = parts[0];
            var originalKind = roles.TryGetValue(PhotoRoles.Original, out var op) ? MediaTypes.KindOf(op) : MediaKind.None;
            var sameContent = media.Info.ContentId is not null && first.ContentId == media.Info.ContentId;
            var originalEdited = op is not null && Stem(op, out var oe) is not null && oe;

            switch (kind)
            {
                case MediaKind.Video when !roles.ContainsKey(PhotoRoles.Live)
                                          && originalKind is MediaKind.Image or MediaKind.Raw
                                          && (sameContent || (first.ContentId is null && media.Info.ContentId is null
                                              && media.Info.DurationSeconds is <= LiveMovieMaxSeconds)):
                    return new Partner(first.PhotoId, Join.LiveMovie, null);
                case MediaKind.Raw when originalKind == MediaKind.Image && !roles.ContainsKey(PhotoRoles.Raw):
                    return new Partner(first.PhotoId, Join.Raw, null);
                case MediaKind.Image when edited && originalKind != MediaKind.None && !originalEdited
                                          && !roles.ContainsKey(PhotoRoles.Edited):
                    return new Partner(first.PhotoId, Join.Edited, null);
                case MediaKind.Image when !edited && originalKind == MediaKind.Video && !roles.ContainsKey(PhotoRoles.Live)
                                          && (sameContent || (first.ContentId is null && media.Info.ContentId is null
                                              && first.Duration is <= LiveMovieMaxSeconds)):
                    return new Partner(first.PhotoId, Join.StillOfLive, op);
                case MediaKind.Image when !edited && originalKind == MediaKind.Raw && !roles.ContainsKey(PhotoRoles.Raw):
                    return new Partner(first.PhotoId, Join.StillOfRaw, op);
                case MediaKind.Image when !edited && originalEdited && !roles.ContainsKey(PhotoRoles.Edited):
                    return new Partner(first.PhotoId, Join.OriginalOfEdited, op);
            }
        }
        return null;
    }

    private static void Attach(IDbConnection db, IDbTransaction tx, Partner t, string path, MediaKind kind, long? size, string? sha, Media media)
    {
        void Part(string role) => db.Execute(
            "INSERT INTO photo_parts (photo_id, role, path, size, sha256) VALUES (@id, @role, @path, @size, @sha)",
            new { id = t.PhotoId, role, path, size, sha }, tx);
        void MoveRole(string to) => db.Execute(
            "UPDATE photo_parts SET role = @to WHERE photo_id = @id AND role = @from",
            new { to, id = t.PhotoId, from = PhotoRoles.Original }, tx);
        void Show(string shown) => db.Execute(
            "UPDATE photos SET path = @shown, thumb = 0, updated_at = @now WHERE id = @id",
            new { shown, id = t.PhotoId, now = Now() }, tx);
        bool HasEdit() => db.ExecuteScalar<long>("SELECT COUNT(*) FROM photo_parts WHERE photo_id = @id AND role = @role",
            new { id = t.PhotoId, role = PhotoRoles.Edited }, tx) > 0;

        switch (t.How)
        {
            case Join.LiveMovie:
                Part(PhotoRoles.Live);
                db.Execute("UPDATE photos SET kind = @kind, duration = coalesce(duration, @d), updated_at = @now WHERE id = @id",
                    new { kind = PhotoKinds.Live, d = media.Info.DurationSeconds, id = t.PhotoId, now = Now() }, tx);
                WriteMetadata(db, tx, t.PhotoId, media, overwrite: false);
                break;
            case Join.Raw:
                Part(PhotoRoles.Raw);
                WriteMetadata(db, tx, t.PhotoId, media, overwrite: false);
                break;
            case Join.Edited:
                Part(PhotoRoles.Edited);
                Show(path);
                break;
            case Join.StillOfLive:
                // The movie came first: the still is the photo, the movie its live part.
                MoveRole(PhotoRoles.Live);
                Part(PhotoRoles.Original);
                db.Execute("UPDATE photos SET kind = @kind, updated_at = @now WHERE id = @id",
                    new { kind = PhotoKinds.Live, id = t.PhotoId, now = Now() }, tx);
                if (!HasEdit()) Show(path);
                WriteMetadata(db, tx, t.PhotoId, media, overwrite: true);
                break;
            case Join.StillOfRaw:
                MoveRole(PhotoRoles.Raw);
                Part(PhotoRoles.Original);
                if (!HasEdit()) Show(path);
                WriteMetadata(db, tx, t.PhotoId, media, overwrite: true);
                break;
            case Join.OriginalOfEdited:
                MoveRole(PhotoRoles.Edited);
                Part(PhotoRoles.Original);
                WriteMetadata(db, tx, t.PhotoId, media, overwrite: true);
                break;
        }
    }

    // ───────────────────────────── a file moved or went ─────────────────────────────

    private async Task MovePartAsync(ContextRef ctx, string from, string to, string? sha, long? size, string? actor, CancellationToken ct)
    {
        bool known;
        using (var db = _db.CreateContextConnection(ctx))
            known = db.ExecuteScalar<long>("SELECT COUNT(*) FROM photo_parts WHERE path = @from", new { from }) > 0;
        if (!known)
        {
            await UpsertAsync(ctx, to, sha, size, actor, ct);
            return;
        }
        await ClearPlaceAsync(ctx, to, ct);
        await _db.WithContextTransactionAsync(ctx, (db, tx, _) =>
        {
            db.Execute("UPDATE photo_parts SET path = @to WHERE path = @from", new { from, to }, tx);
            db.Execute("UPDATE photos SET path = @to, updated_at = @now WHERE path = @from", new { from, to, now = Now() }, tx);
            return Task.CompletedTask;
        }, ct);
    }

    // A gone photo whose part still holds a path that a living file takes now.
    private async Task ClearPlaceAsync(ContextRef ctx, string path, CancellationToken ct)
    {
        string? stale;
        using (var db = _db.CreateContextConnection(ctx))
            stale = db.ExecuteScalar<string?>(
                "SELECT pp.photo_id FROM photo_parts pp JOIN photos p ON p.id = pp.photo_id WHERE pp.path = @path AND p.gone_at IS NOT NULL",
                new { path });
        if (stale is not null) await DeletePhotosAsync(ctx, [stale], ct);
    }

    private Task RePrefixAsync(ContextRef ctx, string from, string to, CancellationToken ct) =>
        _db.WithContextTransactionAsync(ctx, (db, tx, _) =>
        {
            var args = new { lo = from + "/", hi = from + "/￿", to = to + "/", n = from.Length + 2, now = Now() };
            db.Execute("UPDATE photo_parts SET path = @to || substr(path, @n) WHERE path >= @lo AND path < @hi", args, tx);
            db.Execute("UPDATE photos SET path = @to || substr(path, @n), updated_at = @now WHERE path >= @lo AND path < @hi", args, tx);
            return Task.CompletedTask;
        }, ct);

    private async Task GoneUnderAsync(ContextRef ctx, string folder, CancellationToken ct)
    {
        List<string> paths;
        using (var db = _db.CreateContextConnection(ctx))
            paths = db.Query<string>("SELECT path FROM photo_parts WHERE path >= @lo AND path < @hi",
                new { lo = folder + "/", hi = folder + "/￿" }).ToList();
        foreach (var p in paths) await GoneAsync(ctx, p, ct);
    }

    // A part's file left the library. The original (or the part it shows)
    // takes the photo with it, kept as gone; an edit, a movie or a RAW is just
    // no longer part of it.
    private Task GoneAsync(ContextRef ctx, string path, CancellationToken ct) =>
        _db.WithContextTransactionAsync(ctx, (db, tx, _) =>
        {
            var part = db.QuerySingleOrDefault<PartRow>(PartSelect + " WHERE pp.path = @path", new { path }, tx);
            if (part is null || part.GoneAt is not null) return Task.CompletedTask;
            var now = Now();
            if (part.Role == PhotoRoles.Original)
            {
                db.Execute("UPDATE photos SET gone_at = @now, updated_at = @now WHERE id = @id", new { id = part.PhotoId, now }, tx);
                return Task.CompletedTask;
            }
            db.Execute("DELETE FROM photo_parts WHERE photo_id = @id AND role = @role", new { id = part.PhotoId, role = part.Role }, tx);
            if (part.Role == PhotoRoles.Live)
                db.Execute("UPDATE photos SET kind = @kind, updated_at = @now WHERE id = @id",
                    new { kind = PhotoKinds.Photo, id = part.PhotoId, now }, tx);
            if (part.PhotoPath == path)
                db.Execute(@"UPDATE photos SET thumb = 0, updated_at = @now,
                                 path = (SELECT path FROM photo_parts WHERE photo_id = @id AND role = @original)
                             WHERE id = @id", new { id = part.PhotoId, original = PhotoRoles.Original, now }, tx);
            return Task.CompletedTask;
        }, ct);

    // ───────────────────────────── for good ─────────────────────────────

    /// <summary>Photos gone longer than the files trash keeps anything go for good.</summary>
    private async Task PurgeGoneAsync(ContextRef ctx, CancellationToken ct)
    {
        var raw = await _system.GetConfigAsync(FileLimits.TrashRetentionDaysKey, ct);
        var days = long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var d) ? d : FileLimits.DefaultTrashRetentionDays;
        if (days <= 0) return;
        List<string> ids;
        using (var db = _db.CreateContextConnection(ctx))
            ids = db.Query<string>("SELECT id FROM photos WHERE gone_at IS NOT NULL AND gone_at < @cutoff",
                new { cutoff = DateTime.UtcNow.AddDays(-days).ToString("o", CultureInfo.InvariantCulture) }).ToList();
        if (ids.Count > 0) await DeletePhotosAsync(ctx, ids, ct);
    }

    internal async Task DeletePhotosAsync(ContextRef ctx, IReadOnlyList<string> ids, CancellationToken ct)
    {
        // An array: Dapper would hand a List<string> to the JSON tags handler.
        var list = ids.ToArray();
        await _db.WithContextTransactionAsync(ctx, (db, tx, _) =>
        {
            foreach (var table in new[] { "photo_album_items", "photo_parts" })
                db.Execute($"DELETE FROM {table} WHERE photo_id IN @list", new { list }, tx);
            db.Execute("DELETE FROM photos WHERE id IN @list", new { list }, tx);
            return Task.CompletedTask;
        }, ct);
        foreach (var id in ids) PhotoPreviews.Forget(_db, ctx, id);
    }

    // ───────────────────────────── state ─────────────────────────────

    private string? ReadCursor(ContextRef ctx)
    {
        using var db = _db.CreateContextConnection(ctx);
        return db.ExecuteScalar<string?>("SELECT cursor FROM photo_state WHERE id = 1");
    }

    private void SaveCursor(ContextRef ctx, string cursor)
    {
        using var db = _db.CreateContextConnection(ctx);
        db.Execute("INSERT INTO photo_state (id, cursor) VALUES (1, @cursor) ON CONFLICT(id) DO UPDATE SET cursor = excluded.cursor",
            new { cursor });
    }

    private static string Now() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
}
