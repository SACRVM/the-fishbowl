using System.Diagnostics;
using ImageMagick;

namespace Fishbowl.Media.Tests;

public sealed class MediaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fb-media-" + Guid.NewGuid().ToString("N"));

    public MediaTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // A 1200 × 800 JPEG the camera held upright: stored landscape, EXIF
    // orientation 6 (turn a quarter clockwise), so it shows as 800 × 1200.
    private string CameraJpeg(string name = "IMG_0001.JPG", int orientation = 6)
    {
        using var image = new MagickImage(MagickColors.SteelBlue, 1200, 800);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Make, "Apple");
        exif.SetValue(ExifTag.Model, "iPhone 15");
        exif.SetValue(ExifTag.Orientation, (ushort)orientation);
        exif.SetValue(ExifTag.DateTimeOriginal, "2024:07:01 18:30:05");
        exif.SetValue(ExifTag.OffsetTimeOriginal, "+02:00");
        exif.SetValue(ExifTag.GPSLatitudeRef, "N");
        exif.SetValue(ExifTag.GPSLatitude, [new Rational(52, 1), new Rational(31, 1), new Rational(0, 1)]);
        exif.SetValue(ExifTag.GPSLongitudeRef, "E");
        exif.SetValue(ExifTag.GPSLongitude, [new Rational(13, 1), new Rational(24, 1), new Rational(0, 1)]);
        image.SetProfile(exif);
        // Magick writes the image's own orientation over the profile's.
        image.Orientation = (Orientation)orientation;
        var path = Path.Combine(_dir, name);
        image.Write(path, MagickFormat.Jpeg);
        return path;
    }

    [Fact]
    public void Kinds_FollowTheExtension_InAnyCase()
    {
        Assert.Equal(MediaKind.Image, MediaTypes.KindOf("Photos/2024/07/IMG_0001.HEIC"));
        Assert.Equal(MediaKind.Video, MediaTypes.KindOf("a/IMG_0001.MOV"));
        Assert.Equal(MediaKind.Raw, MediaTypes.KindOf("a/b.dng"));
        Assert.Equal(MediaKind.None, MediaTypes.KindOf("a/IMG_0001.xmp"));
        Assert.Equal(MediaKind.None, MediaTypes.KindOf("a/IMG_0001.json"));
    }

    [Fact]
    public void Reader_TakesDatePlaceCameraAndShownSize_FromExif()
    {
        var path = CameraJpeg();
        using var stream = File.OpenRead(path);
        var info = MediaReader.Read(stream, path);

        Assert.Equal(MediaKind.Image, info.Kind);
        Assert.True(info.OffsetKnown);
        Assert.Equal(new DateTimeOffset(2024, 7, 1, 18, 30, 5, TimeSpan.FromHours(2)), info.TakenAt);
        Assert.Equal(800, info.Width);
        Assert.Equal(1200, info.Height);
        Assert.Equal(52.516667, info.Latitude!.Value, 5);
        Assert.Equal(13.4, info.Longitude!.Value, 5);
        Assert.Equal("Apple", info.Make);
        Assert.Equal("iPhone 15", info.Model);
    }

    [Fact]
    public void Reader_WithoutAnOffset_KeepsTheWallClockAndSaysSo()
    {
        using var image = new MagickImage(MagickColors.White, 40, 30);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.DateTimeOriginal, "2019:12:24 20:15:00");
        image.SetProfile(exif);
        var path = Path.Combine(_dir, "old.jpg");
        image.Write(path, MagickFormat.Jpeg);

        using var stream = File.OpenRead(path);
        var info = MediaReader.Read(stream, path);
        Assert.False(info.OffsetKnown);
        Assert.Equal(new DateTime(2019, 12, 24, 20, 15, 0), info.TakenAt!.Value.DateTime);
        Assert.Null(info.Latitude);
    }

    [Fact]
    public void Reader_OnAFileItCantRead_GivesNothingAndDoesntThrow()
    {
        var path = Path.Combine(_dir, "broken.jpg");
        File.WriteAllBytes(path, [0xFF, 0xD8, 0x00, 0x01, 0x02]);
        using var stream = File.OpenRead(path);
        var info = MediaReader.Read(stream, path);
        Assert.Equal(MediaKind.Image, info.Kind);
        Assert.Null(info.TakenAt);
    }

    [Theory]
    [InlineData("+52.5163+013.3777+034.000/", 52.5163, 13.3777)]
    [InlineData("-33.8688+151.2093/", -33.8688, 151.2093)]
    public void Iso6709_ReadsAVideosPlace(string text, double lat, double lon)
    {
        var p = MediaReader.Iso6709(text);
        Assert.NotNull(p);
        Assert.Equal(lat, p!.Value.Lat, 4);
        Assert.Equal(lon, p.Value.Lon, 4);
    }

    [Fact]
    public async Task Thumbnailer_TurnsThePicture_AndMakesBothSizes()
    {
        var thumbs = new Thumbnailer();
        Assert.True(thumbs.CanMakeImages);
        var path = CameraJpeg();
        var small = Path.Combine(_dir, "t", "s.webp");
        var large = Path.Combine(_dir, "t", "l.webp");

        var result = await thumbs.MakeAsync(path, MediaKind.Image, small, large, TestContext.Current.CancellationToken);

        Assert.Equal(800, result.Width);
        Assert.Equal(1200, result.Height);
        using var s = new MagickImage(small);
        Assert.Equal(MagickFormat.WebP, s.Format);
        Assert.Equal((uint)Thumbnailer.SmallEdge, Math.Min(s.Width, s.Height));
        Assert.True(s.Height > s.Width, "upright");
        Assert.Null(s.GetExifProfile());
        using var l = new MagickImage(large);
        Assert.Equal(800u, l.Width);
        Assert.Equal(1200u, l.Height);
    }

    [Fact]
    public async Task Thumbnailer_RefusesWhatIsntAPicture()
    {
        var path = Path.Combine(_dir, "not.jpg");
        File.WriteAllText(path, "hello");
        var thumbs = new Thumbnailer();
        await Assert.ThrowsAsync<ThumbnailException>(() => thumbs.MakeAsync(path, MediaKind.Image,
            Path.Combine(_dir, "s.webp"), Path.Combine(_dir, "l.webp"), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(_dir, "s.webp")));
    }

    [Fact]
    public async Task Video_GivesItsLengthDateAndAPoster_WhenFfmpegIsThere()
    {
        var thumbs = new Thumbnailer();
        Assert.SkipUnless(thumbs.CanMakeVideos, "ffmpeg isn't installed");
        var path = Path.Combine(_dir, "clip.mp4");
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=size=640x360:rate=10",
                     "-t", "2", "-pix_fmt", "yuv420p", "-metadata", "creation_time=2024-07-01T16:30:05Z", path })
            psi.ArgumentList.Add(a);
        using (var p = Process.Start(psi)!)
            await p.WaitForExitAsync(TestContext.Current.CancellationToken);

        using (var stream = File.OpenRead(path))
        {
            var info = MediaReader.Read(stream, path);
            Assert.Equal(MediaKind.Video, info.Kind);
            Assert.Equal(new DateTimeOffset(2024, 7, 1, 16, 30, 5, TimeSpan.Zero), info.TakenAt);
            Assert.Equal(2.0, info.DurationSeconds!.Value, 1);
            Assert.Equal(640, info.Width);
            Assert.Equal(360, info.Height);
        }

        var result = await thumbs.MakeAsync(path, MediaKind.Video, Path.Combine(_dir, "v-s.webp"), Path.Combine(_dir, "v-l.webp"),
            TestContext.Current.CancellationToken);
        Assert.Equal(640, result.Width);
        Assert.True(File.Exists(Path.Combine(_dir, "v-s.webp")));
    }
}
