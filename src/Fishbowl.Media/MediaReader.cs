using System.Globalization;
using System.Text.RegularExpressions;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Exif.Makernotes;
using MetadataExtractor.Formats.Heif;
using MetadataExtractor.Formats.QuickTime;

namespace Fishbowl.Media;

/// <summary>What a photo or video file says about itself.</summary>
/// <param name="TakenAt">When it was taken, as the camera's wall-clock time
/// with its offset — or, when the file gives no offset, that wall-clock time
/// with offset zero and <paramref name="OffsetKnown"/> false.</param>
/// <param name="Width">Width as it is shown (after the orientation).</param>
/// <param name="ContentId">Apple's content identifier: a Live Photo's still
/// and movie carry the same one.</param>
/// <param name="BurstId">Apple's burst id: every frame of a burst carries it.</param>
public sealed record MediaInfo(
    MediaKind Kind,
    DateTimeOffset? TakenAt,
    bool OffsetKnown,
    int? Width,
    int? Height,
    double? DurationSeconds,
    double? Latitude,
    double? Longitude,
    string? Make,
    string? Model,
    string? ContentId,
    string? BurstId);

public static class MediaReader
{
    /// <summary>
    /// Reads what the file holds. Never throws for a file it can't read: what
    /// it can't find stays null (an unknown format, a damaged header).
    /// </summary>
    public static MediaInfo Read(Stream stream, string fileName)
    {
        var kind = MediaTypes.KindOf(fileName);
        IReadOnlyList<MetadataExtractor.Directory> dirs;
        try { dirs = ImageMetadataReader.ReadMetadata(stream); }
        catch (Exception) { return new MediaInfo(kind, null, false, null, null, null, null, null, null, null, null, null); }

        var (taken, offsetKnown) = kind == MediaKind.Video ? VideoDate(dirs) : PhotoDate(dirs);
        var (width, height) = Size(dirs, kind);
        var (lat, lon) = Place(dirs);
        var ifd0 = dirs.OfType<ExifIfd0Directory>().FirstOrDefault();
        var qt = dirs.OfType<QuickTimeMetadataHeaderDirectory>().FirstOrDefault();
        var apple = dirs.OfType<AppleMakernoteDirectory>().FirstOrDefault();

        return new MediaInfo(
            kind,
            taken,
            offsetKnown,
            width,
            height,
            kind == MediaKind.Video ? Duration(dirs) : null,
            lat,
            lon,
            Clean(ifd0?.GetString(ExifDirectoryBase.TagMake) ?? qt?.GetString(QuickTimeMetadataHeaderDirectory.TagMake)),
            Clean(ifd0?.GetString(ExifDirectoryBase.TagModel) ?? qt?.GetString(QuickTimeMetadataHeaderDirectory.TagModel)),
            Clean(apple?.GetString(AppleMakernoteDirectory.TagContentIdentifier)
                  ?? qt?.GetString(QuickTimeMetadataHeaderDirectory.TagContentIdentifier)),
            Clean(apple?.GetString(AppleMakernoteDirectory.TagBurstUuid)));
    }

    private static string? Clean(string? s)
    {
        s = s?.Trim().TrimEnd('\0').Trim();
        return string.IsNullOrEmpty(s) ? null : s.Length > 200 ? s[..200] : s;
    }

    // ───────────────────────────── date ─────────────────────────────

    // EXIF writes "2024:07:01 18:30:05" in the camera's wall-clock time;
    // OffsetTimeOriginal ("+02:00", EXIF 2.31) says which zone that was.
    private static (DateTimeOffset?, bool) PhotoDate(IReadOnlyList<MetadataExtractor.Directory> dirs)
    {
        foreach (var sub in dirs.OfType<ExifSubIfdDirectory>())
        {
            foreach (var (tag, offsetTag) in new[]
                     {
                         (ExifDirectoryBase.TagDateTimeOriginal, ExifDirectoryBase.TagTimeZoneOriginal),
                         (ExifDirectoryBase.TagDateTimeDigitized, ExifDirectoryBase.TagTimeZoneDigitized),
                     })
            {
                var wall = ExifWallClock(sub.GetString(tag));
                if (wall is null) continue;
                var offset = Offset(sub.GetString(offsetTag));
                return offset is { } o
                    ? (new DateTimeOffset(wall.Value, o), true)
                    : (new DateTimeOffset(wall.Value, TimeSpan.Zero), false);
            }
        }
        foreach (var ifd0 in dirs.OfType<ExifIfd0Directory>())
        {
            var wall = ExifWallClock(ifd0.GetString(ExifDirectoryBase.TagDateTime));
            if (wall is null) continue;
            var offset = Offset(ifd0.GetString(ExifDirectoryBase.TagTimeZone));
            return offset is { } o
                ? (new DateTimeOffset(wall.Value, o), true)
                : (new DateTimeOffset(wall.Value, TimeSpan.Zero), false);
        }
        return (null, false);
    }

    internal static DateTime? ExifWallClock(string? s)
    {
        s = s?.Trim().TrimEnd('\0');
        if (string.IsNullOrEmpty(s) || s.StartsWith("0000", StringComparison.Ordinal)) return null;
        string[] formats = ["yyyy:MM:dd HH:mm:ss", "yyyy:MM:dd HH:mm:ss.FFF", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss"];
        return DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? DateTime.SpecifyKind(d, DateTimeKind.Unspecified)
            : null;
    }

    private static TimeSpan? Offset(string? s)
    {
        s = s?.Trim().TrimEnd('\0');
        if (string.IsNullOrEmpty(s)) return null;
        var m = Regex.Match(s, @"^([+-])(\d{2}):?(\d{2})$");
        if (!m.Success) return null;
        var span = new TimeSpan(int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), 0);
        if (span > TimeSpan.FromHours(14)) return null;
        return m.Groups[1].Value == "-" ? -span : span;
    }

    // An iPhone video says "2024-07-01T18:30:05+0200" in its metadata; any
    // QuickTime file has the movie's creation time in UTC as well.
    private static (DateTimeOffset?, bool) VideoDate(IReadOnlyList<MetadataExtractor.Directory> dirs)
    {
        var qt = dirs.OfType<QuickTimeMetadataHeaderDirectory>().FirstOrDefault();
        var text = qt?.GetString(QuickTimeMetadataHeaderDirectory.TagCreationDate)?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            string[] formats = ["yyyy-MM-ddTHH:mm:sszzzz", "yyyy-MM-ddTHH:mm:ss.FFFzzzz", "yyyy-MM-ddTHH:mm:sszzz", "yyyy-MM-ddTHH:mm:ss.FFFzzz"];
            if (DateTimeOffset.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                return (d, true);
            var wall = ExifWallClock(text);
            if (wall is not null) return (new DateTimeOffset(wall.Value, TimeSpan.Zero), false);
        }
        var movie = dirs.OfType<QuickTimeMovieHeaderDirectory>().FirstOrDefault();
        if (movie is not null && movie.TryGetDateTime(QuickTimeMovieHeaderDirectory.TagCreated, out var created)
            && created.Year > 1970)
            return (new DateTimeOffset(DateTime.SpecifyKind(created, DateTimeKind.Utc)), true);
        return (null, false);
    }

    private static double? Duration(IReadOnlyList<MetadataExtractor.Directory> dirs)
    {
        var movie = dirs.OfType<QuickTimeMovieHeaderDirectory>().FirstOrDefault();
        if (movie is null) return null;
        var raw = movie.GetObject(QuickTimeMovieHeaderDirectory.TagDuration);
        if (raw is TimeSpan span) return span.TotalSeconds > 0 ? Math.Round(span.TotalSeconds, 3) : null;
        if (movie.TryGetInt64(QuickTimeMovieHeaderDirectory.TagDuration, out var units)
            && movie.TryGetInt64(QuickTimeMovieHeaderDirectory.TagTimeScale, out var scale) && scale > 0)
            return Math.Round((double)units / scale, 3);
        return null;
    }

    // ───────────────────────────── size ─────────────────────────────

    private static (int?, int?) Size(IReadOnlyList<MetadataExtractor.Directory> dirs, MediaKind kind)
    {
        if (kind == MediaKind.Video)
        {
            foreach (var track in dirs.OfType<QuickTimeTrackHeaderDirectory>())
            {
                if (!track.TryGetDouble(QuickTimeTrackHeaderDirectory.TagWidth, out var w) || w <= 0) continue;
                if (!track.TryGetDouble(QuickTimeTrackHeaderDirectory.TagHeight, out var h) || h <= 0) continue;
                var turned = track.TryGetDouble(QuickTimeTrackHeaderDirectory.TagRotation, out var r)
                    && Math.Abs(Math.Abs(r % 180) - 90) < 1;
                return turned ? ((int)h, (int)w) : ((int)w, (int)h);
            }
            return (null, null);
        }

        int? width = null, height = null;
        var heic = dirs.OfType<HeicImagePropertiesDirectory>()
            .Where(d => d.ContainsTag(HeicImagePropertiesDirectory.TagImageWidth))
            .OrderByDescending(d => d.TryGetInt32(HeicImagePropertiesDirectory.TagImageWidth, out var w) ? w : 0)
            .FirstOrDefault();
        if (heic is not null
            && heic.TryGetInt32(HeicImagePropertiesDirectory.TagImageWidth, out var hw)
            && heic.TryGetInt32(HeicImagePropertiesDirectory.TagImageHeight, out var hh))
        {
            (width, height) = (hw, hh);
        }
        else
        {
            foreach (var dir in dirs)
            {
                var (wt, ht) = dir switch
                {
                    ExifSubIfdDirectory => (ExifDirectoryBase.TagExifImageWidth, ExifDirectoryBase.TagExifImageHeight),
                    MetadataExtractor.Formats.Jpeg.JpegDirectory => (MetadataExtractor.Formats.Jpeg.JpegDirectory.TagImageWidth, MetadataExtractor.Formats.Jpeg.JpegDirectory.TagImageHeight),
                    MetadataExtractor.Formats.Png.PngDirectory => (MetadataExtractor.Formats.Png.PngDirectory.TagImageWidth, MetadataExtractor.Formats.Png.PngDirectory.TagImageHeight),
                    MetadataExtractor.Formats.WebP.WebPDirectory => (MetadataExtractor.Formats.WebP.WebPDirectory.TagImageWidth, MetadataExtractor.Formats.WebP.WebPDirectory.TagImageHeight),
                    MetadataExtractor.Formats.Gif.GifHeaderDirectory => (MetadataExtractor.Formats.Gif.GifHeaderDirectory.TagImageWidth, MetadataExtractor.Formats.Gif.GifHeaderDirectory.TagImageHeight),
                    _ => (-1, -1),
                };
                if (wt < 0) continue;
                if (dir.TryGetInt32(wt, out var w) && dir.TryGetInt32(ht, out var h) && w > 0 && h > 0)
                {
                    (width, height) = (w, h);
                    break;
                }
            }
        }
        if (width is null) return (null, null);

        // EXIF orientations 5–8 turn the picture by a quarter.
        var ifd0 = dirs.OfType<ExifIfd0Directory>().FirstOrDefault();
        var turnedImage = ifd0 is not null && ifd0.TryGetInt32(ExifDirectoryBase.TagOrientation, out var o) && o is >= 5 and <= 8;
        return turnedImage ? (height, width) : (width, height);
    }

    // ───────────────────────────── place ─────────────────────────────

    private static (double?, double?) Place(IReadOnlyList<MetadataExtractor.Directory> dirs)
    {
        var gps = dirs.OfType<GpsDirectory>().FirstOrDefault();
        if (gps is not null)
        {
            var geo = gps.GetGeoLocation();
            if (geo is { } g && !g.IsZero && Plausible(g.Latitude, g.Longitude))
                return (Math.Round(g.Latitude, 6), Math.Round(g.Longitude, 6));
        }
        var qt = dirs.OfType<QuickTimeMetadataHeaderDirectory>().FirstOrDefault();
        if (Iso6709(qt?.GetString(QuickTimeMetadataHeaderDirectory.TagGpsLocation)) is { } p)
            return (p.Lat, p.Lon);
        return (null, null);
    }

    // "+52.5163+013.3777+034.000/" — latitude, longitude, an optional height.
    internal static (double Lat, double Lon)? Iso6709(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = Regex.Match(s.Trim(), @"^([+-]\d+(?:\.\d+)?)([+-]\d+(?:\.\d+)?)");
        if (!m.Success) return null;
        if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)) return null;
        if (!double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)) return null;
        if (!Plausible(lat, lon) || (lat == 0 && lon == 0)) return null;
        return (Math.Round(lat, 6), Math.Round(lon, 6));
    }

    private static bool Plausible(double lat, double lon) =>
        double.IsFinite(lat) && double.IsFinite(lon) && Math.Abs(lat) <= 90 && Math.Abs(lon) <= 180;
}
