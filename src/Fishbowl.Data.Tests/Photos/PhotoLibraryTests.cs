using System.Diagnostics;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Files;
using Fishbowl.Data.Files;
using Fishbowl.Data.Photos;
using Fishbowl.Data.Repositories;
using Fishbowl.Media;
using ImageMagick;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fishbowl.Data.Tests.Photos;

// The Photos library below HTTP: files in Photos/ through the real
// FileService, the indexer following the journal, the repository and the
// previews (spec 2026-10-30-photos-design).
public class PhotoLibraryTests : IDisposable
{
    private readonly string _dataDir;
    private readonly DatabaseFactory _db;
    private readonly SystemRepository _system;
    private readonly FileService _files;
    private readonly PhotoIndexer _indexer;
    private readonly PhotoRepository _photos;
    private readonly ContextRef _me = ContextRef.User("photo_user");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public PhotoLibraryTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_photos_" + Path.GetRandomFileName());
        _db = new DatabaseFactory(_dataDir);
        _system = new SystemRepository(_db);
        _files = new FileService(_db, _system);
        _system.SetConfigAsync(FileLimits.MinFreeBytesKey, "0").GetAwaiter().GetResult();
        _indexer = new PhotoIndexer(_db, _files, _system);
        _photos = new PhotoRepository(_db, _files, _indexer);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static byte[] Jpeg(string taken = "2024:07:01 18:30:05", string? offset = "+02:00", string color = "SteelBlue")
    {
        using var image = new MagickImage(new MagickColor(color), 120, 80);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Make, "Apple");
        exif.SetValue(ExifTag.Model, "iPhone 15");
        exif.SetValue(ExifTag.DateTimeOriginal, taken);
        if (offset is not null) exif.SetValue(ExifTag.OffsetTimeOriginal, offset);
        image.SetProfile(exif);
        return image.ToByteArray(MagickFormat.Jpeg);
    }

    private Task Put(string path, byte[] bytes) =>
        _files.UploadAsync(_me, path, new MemoryStream(bytes), bytes.Length, null, true, true, "photo_user", Ct);

    private async Task<List<Photo>> All()
    {
        await _indexer.RunAsync(_me, Ct);
        var list = new List<Photo>();
        foreach (var m in await _photos.MonthsAsync(_me, PhotoFilter.All, Ct))
            list.AddRange(await _photos.MonthAsync(_me, PhotoFilter.All, m.Month, Ct));
        return list;
    }

    private List<(string Role, string Path)> Parts(string photoId)
    {
        using var db = _db.CreateContextConnection(_me);
        return db.Query<(string, string)>("SELECT role, path FROM photo_parts WHERE photo_id = @photoId ORDER BY role", new { photoId }).ToList();
    }

    private string? Mp4(double seconds)
    {
        if (!new Thumbnailer().CanMakeVideos) return null;
        var path = Path.Combine(_dataDir, Guid.NewGuid().ToString("N") + ".mp4");
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=size=160x90:rate=10",
                     "-t", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p", path })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return path;
    }

    // ───── what is a photo ─────

    [Fact]
    public async Task AFileInPhotos_IsAPhoto_WithWhatItSays()
    {
        await Put("Photos/2024/07/IMG_0001.JPG", Jpeg());

        var photo = Assert.Single(await All());
        Assert.Equal("Photos/2024/07/IMG_0001.JPG", photo.Path);
        Assert.Equal(PhotoKinds.Photo, photo.Kind);
        Assert.Equal("2024-07", photo.Month);
        Assert.Equal(new DateTime(2024, 7, 1, 16, 30, 5, DateTimeKind.Utc), photo.TakenAt);
        Assert.Equal(120, photo.TakenOffset);
        Assert.Equal("Apple iPhone 15", photo.Camera);
        Assert.Equal(120, photo.Width);
    }

    [Fact]
    public async Task OutsidePhotos_InADotFolder_OrNotAPicture_IsNoPhoto()
    {
        await Put("Elsewhere/IMG_0001.JPG", Jpeg());
        await Put("Photos/.import/IMG_0002.JPG", Jpeg());
        await Put("Photos/2024/notes.txt", "hello"u8.ToArray());
        await Put("Photos/2024/IMG_0003.json", "{}"u8.ToArray());

        Assert.Empty(await All());
    }

    [Fact]
    public async Task TheMonthIsTheCamerasLocalDate()
    {
        // Late on New Year's Eve in Berlin is already next year in UTC — and
        // still December where it was taken.
        await Put("Photos/a.jpg", Jpeg("2023:12:31 23:30:00", "+01:00"));
        await Put("Photos/b.jpg", Jpeg("2024:01:01 00:30:00", null));

        var photos = await All();
        Assert.Equal("2023-12", photos.Single(p => p.Path.EndsWith("a.jpg")).Month);
        var b = photos.Single(p => p.Path.EndsWith("b.jpg"));
        Assert.Equal("2024-01", b.Month);
        Assert.Null(b.TakenOffset);
    }

    // ───── following the files ─────

    [Fact]
    public async Task AMove_KeepsThePhoto_AFolderMoveToo()
    {
        await Put("Photos/2024/07/IMG_0001.JPG", Jpeg());
        var id = Assert.Single(await All()).Id;

        await _files.MoveAsync(_me, "Photos/2024/07/IMG_0001.JPG", "Photos/2024/07/Beach.jpg", "photo_user", Ct);
        Assert.Equal("Photos/2024/07/Beach.jpg", Assert.Single(await All()).Path);

        await _files.MoveAsync(_me, "Photos/2024", "Photos/Holidays", "photo_user", Ct);
        var moved = Assert.Single(await All());
        Assert.Equal(id, moved.Id);
        Assert.Equal("Photos/Holidays/07/Beach.jpg", moved.Path);
        Assert.Equal("Photos/Holidays/07/Beach.jpg", Assert.Single(Parts(id)).Path);
    }

    [Fact]
    public async Task Trashed_IsGone_AndComesBackWithItsAlbum_WhenRestored()
    {
        await Put("Photos/2024/07/IMG_0001.JPG", Jpeg());
        var id = Assert.Single(await All()).Id;
        var album = await _photos.CreateAlbumAsync(_me, "Summer", "photo_user", Ct);
        await _photos.AddToAlbumAsync(_me, album.Id, [id], Ct);
        await _photos.SetFavoriteAsync(_me, [id], true, Ct);

        var trash = await _files.TrashAsync(_me, "Photos/2024/07/IMG_0001.JPG", "photo_user", Ct);
        Assert.Empty(await All());
        Assert.Equal(0, (await _photos.AlbumsAsync(_me, Ct)).Single().Count);

        await _files.RestoreAsync(_me, trash.Id, FileConflictMode.Fail, "photo_user", Ct);
        var back = Assert.Single(await All());
        Assert.Equal(id, back.Id);
        Assert.True(back.Favorite);
        Assert.Equal(1, (await _photos.AlbumsAsync(_me, Ct)).Single().Count);
    }

    [Fact]
    public async Task MovedOutOfPhotos_IsGone_AndBackIn_IsTheSamePhoto()
    {
        await Put("Photos/IMG_0001.JPG", Jpeg());
        var id = Assert.Single(await All()).Id;

        await _files.MoveAsync(_me, "Photos/IMG_0001.JPG", "Elsewhere.jpg", "photo_user", Ct);
        Assert.Empty(await All());
        await _files.MoveAsync(_me, "Elsewhere.jpg", "Photos/IMG_0001.JPG", "photo_user", Ct);
        Assert.Equal(id, Assert.Single(await All()).Id);
    }

    [Fact]
    public async Task AFolderTrashed_TakesItsPhotos()
    {
        await Put("Photos/2024/a.jpg", Jpeg());
        await Put("Photos/2024/b.jpg", Jpeg(color: "Orange"));
        await Put("Photos/2025/c.jpg", Jpeg("2025:01:02 10:00:00"));
        Assert.Equal(3, (await All()).Count);

        await _files.TrashAsync(_me, "Photos/2024", "photo_user", Ct);
        Assert.Equal("Photos/2025/c.jpg", Assert.Single(await All()).Path);
    }

    [Fact]
    public async Task ALostJournal_MeansAFullScan_NothingTwice()
    {
        await Put("Photos/a.jpg", Jpeg());
        var id = Assert.Single(await All()).Id;
        using (var db = _db.CreateContextConnection(_me))
            db.Execute("UPDATE photo_state SET cursor = 'another-epoch:5'");
        await Put("Photos/b.jpg", Jpeg(color: "Orange"));

        var photos = await All();
        Assert.Equal(2, photos.Count);
        Assert.Contains(photos, p => p.Id == id);
    }

    // ───── one photo, several files ─────

    [Fact]
    public async Task AnEditedCopy_IsThePhotosShownPart()
    {
        await Put("Photos/2024/07/IMG_0002.jpeg", Jpeg());
        await Put("Photos/2024/07/IMG_0002_edited.jpeg", Jpeg(color: "Orange"));

        var photo = Assert.Single(await All());
        Assert.Equal("Photos/2024/07/IMG_0002_edited.jpeg", photo.Path);
        Assert.Equal([("edited", "Photos/2024/07/IMG_0002_edited.jpeg"), ("original", "Photos/2024/07/IMG_0002.jpeg")], Parts(photo.Id));

        // The edit goes: the original is shown again.
        await _files.TrashAsync(_me, "Photos/2024/07/IMG_0002_edited.jpeg", "photo_user", Ct);
        Assert.Equal("Photos/2024/07/IMG_0002.jpeg", Assert.Single(await All()).Path);
    }

    [Fact]
    public async Task ARawBesideItsJpeg_IsOnePhoto_WhicheverCameFirst()
    {
        await Put("Photos/r/DSC_0001.DNG", Jpeg());
        await Put("Photos/r/DSC_0001.JPG", Jpeg());

        var photo = Assert.Single(await All());
        Assert.Equal("Photos/r/DSC_0001.JPG", photo.Path);
        Assert.Equal([("original", "Photos/r/DSC_0001.JPG"), ("raw", "Photos/r/DSC_0001.DNG")], Parts(photo.Id));
    }

    [Fact]
    public async Task ALivePhoto_IsTheStillWithItsMovie_WhicheverCameFirst()
    {
        var clip = Mp4(2);
        Assert.SkipWhen(clip is null, "ffmpeg isn't installed");
        await Put("Photos/l/IMG_0005.MOV", File.ReadAllBytes(clip!));
        await Put("Photos/l/IMG_0005.HEIC", Jpeg());
        // A long video beside a photo of the same name stays a video.
        await Put("Photos/l/IMG_0006.JPG", Jpeg());
        await Put("Photos/l/IMG_0006.MP4", File.ReadAllBytes(Mp4(8)!));

        var photos = await All();
        var live = photos.Single(p => p.Kind == PhotoKinds.Live);
        Assert.Equal("Photos/l/IMG_0005.HEIC", live.Path);
        Assert.Equal([("live", "Photos/l/IMG_0005.MOV"), ("original", "Photos/l/IMG_0005.HEIC")], Parts(live.Id));
        Assert.Contains(photos, p => p.Kind == PhotoKinds.Video && p.Path == "Photos/l/IMG_0006.MP4");
        Assert.Equal(3, photos.Count);
    }

    // ───── the app's own ─────

    [Fact]
    public async Task Favourites_AndAlbums_FilterTheTimeline()
    {
        await Put("Photos/a.jpg", Jpeg());
        await Put("Photos/b.jpg", Jpeg("2024:06:01 12:00:00", color: "Orange"));
        var photos = await All();
        var a = photos.Single(p => p.Path.EndsWith("a.jpg")).Id;
        var b = photos.Single(p => p.Path.EndsWith("b.jpg")).Id;

        await _photos.SetFavoriteAsync(_me, [b], true, Ct);
        Assert.Equal([new PhotoMonth("2024-06", 1)], await _photos.MonthsAsync(_me, new PhotoFilter(Favorites: true), Ct));

        var album = await _photos.CreateAlbumAsync(_me, "  Summer  ", "photo_user", Ct);
        Assert.Equal("Summer", album.Name);
        Assert.Equal(1, await _photos.AddToAlbumAsync(_me, album.Id, [a, a, "nope"], Ct));
        Assert.Equal(a, Assert.Single(await _photos.MonthAsync(_me, new PhotoFilter(AlbumId: album.Id), "2024-07", Ct)).Id);
        var listed = Assert.Single(await _photos.AlbumsAsync(_me, Ct));
        Assert.Equal((1, a), (listed.Count, listed.CoverId));

        await _photos.RemoveFromAlbumAsync(_me, album.Id, [a], Ct);
        Assert.Empty(await _photos.MonthsAsync(_me, new PhotoFilter(AlbumId: album.Id), Ct));
        await _photos.RenameAlbumAsync(_me, album.Id, "Sommer", Ct);
        await _photos.DeleteAlbumAsync(_me, album.Id, Ct);
        Assert.Empty(await _photos.AlbumsAsync(_me, Ct));
        Assert.Equal(2, (await All()).Count);
        await Assert.ThrowsAsync<PhotoException>(() => _photos.CreateAlbumAsync(_me, " ", "photo_user", Ct));
    }

    [Fact]
    public async Task Deleting_SendsItsFilesToTheTrash()
    {
        await Put("Photos/2024/07/IMG_0002.jpeg", Jpeg());
        await Put("Photos/2024/07/IMG_0002_edited.jpeg", Jpeg(color: "Orange"));
        var id = Assert.Single(await All()).Id;

        Assert.Equal(1, await _photos.TrashAsync(_me, [id], "photo_user", Ct));

        Assert.Empty(await All());
        var trashed = (await _files.ListTrashAsync(_me, Ct)).Select(t => t.OriginalPath).OrderBy(p => p, StringComparer.Ordinal).ToList();
        Assert.Equal(["Photos/2024/07/IMG_0002.jpeg", "Photos/2024/07/IMG_0002_edited.jpeg"], trashed);
    }

    [Fact]
    public async Task AnUpload_GoesToTheMonthItWasTaken_ANameTakenGetsANumber()
    {
        var first = await _photos.UploadAsync(_me, "IMG_0001.JPG", new MemoryStream(Jpeg()), null, "photo_user", Ct);
        var second = await _photos.UploadAsync(_me, @"C:\fakepath\IMG_0001.JPG", new MemoryStream(Jpeg(color: "Orange")), null, "photo_user", Ct);

        Assert.Equal("Photos/2024/07/IMG_0001.JPG", first!.Path);
        Assert.Equal("Photos/2024/07/IMG_0001 (2).JPG", second!.Path);
        var refused = await Assert.ThrowsAsync<FileStoreException>(() =>
            _photos.UploadAsync(_me, "notes.txt", new MemoryStream("x"u8.ToArray()), 1, "photo_user", Ct));
        Assert.Equal("not_a_photo", refused.Code);
        Assert.False(Directory.Exists(Path.Combine(_db.ResolveCacheFolder(_me), "upload"))
                     && Directory.EnumerateFiles(Path.Combine(_db.ResolveCacheFolder(_me), "upload")).Any());
    }

    [Fact]
    public async Task Previews_AreMadeInTheCache_NotInTheWorkspace()
    {
        await Put("Photos/a.jpg", Jpeg());
        await Put("Photos/broken.jpg", "not a picture"u8.ToArray());
        var photos = await All();
        var previews = new PhotoPreviews(_db, _files, new Thumbnailer());

        Assert.Equal(2, await previews.RunAsync(_me, 10, Ct));
        Assert.Equal(0, await previews.RunAsync(_me, 10, Ct));

        var a = (await All()).Single(p => p.Path == "Photos/a.jpg");
        Assert.True(a.Thumb);
        var small = PhotoPreviews.PathOf(_db, _me, a.Id, PhotoPreviews.Size.Small);
        Assert.True(File.Exists(small));
        Assert.False(small.StartsWith(_db.ResolveContextFolder(_me), StringComparison.Ordinal));
        Assert.False((await All()).Single(p => p.Path == "Photos/broken.jpg").Thumb);
    }
}
