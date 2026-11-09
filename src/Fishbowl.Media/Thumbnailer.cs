using System.Diagnostics;
using ImageMagick;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Media;

/// <summary>The two previews of a photo, written as WebP.</summary>
/// <param name="Width">The picture's width as shown (after its orientation).</param>
public sealed record Thumbnails(int Width, int Height);

public sealed class ThumbnailException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Makes a photo's previews: a small one for the grid (its short edge
/// <see cref="SmallEdge"/>, so a square cell crops it without blur) and a
/// large one for the viewer (at most <see cref="LargeEdge"/> on its long
/// edge — the browser can't show HEIC or RAW, so this is what it sees).
/// A video's are made from a frame ffmpeg hands over, when the server has
/// ffmpeg. Metadata (GPS among it) never goes into a preview.
/// </summary>
public sealed class Thumbnailer
{
    public const int SmallEdge = 384;
    public const int SmallMaxLongEdge = 1536;
    public const int LargeEdge = 2048;
    private static readonly TimeSpan FfmpegTimeout = TimeSpan.FromSeconds(60);

    private readonly ILogger<Thumbnailer> _logger;
    private readonly Lazy<bool> _magick;
    private readonly Lazy<string?> _ffmpeg;

    public Thumbnailer(ILogger<Thumbnailer>? logger = null)
    {
        _logger = logger ?? NullLogger<Thumbnailer>.Instance;
        _magick = new Lazy<bool>(ProbeMagick);
        _ffmpeg = new Lazy<string?>(FindFfmpeg);
    }

    /// <summary>False where Magick.NET has no native library (osx-x64).</summary>
    public bool CanMakeImages => _magick.Value;

    public bool CanMakeVideos => _magick.Value && _ffmpeg.Value is not null;

    public bool CanMake(MediaKind kind) => kind == MediaKind.Video ? CanMakeVideos : kind != MediaKind.None && CanMakeImages;

    /// <summary>
    /// Writes both previews of <paramref name="source"/> (a file on disk)
    /// next to each other; throws <see cref="ThumbnailException"/> for a file
    /// that can't be read as a picture.
    /// </summary>
    public async Task<Thumbnails> MakeAsync(string source, MediaKind kind, string smallPath, string largePath, CancellationToken ct = default)
    {
        if (!CanMake(kind)) throw new ThumbnailException("No decoder for this kind of file on this server.");
        try
        {
            if (kind == MediaKind.Video)
            {
                var frame = await VideoFrameAsync(source, ct);
                using var image = new MagickImage(frame);
                return Write(image, smallPath, largePath);
            }
            using (var image = new MagickImage())
            {
                // Only the first frame of an animation, and never a whole
                // multi-page TIFF.
                image.Read(source, new MagickReadSettings { FrameIndex = 0, FrameCount = 1 });
                return Write(image, smallPath, largePath);
            }
        }
        catch (MagickException ex)
        {
            throw new ThumbnailException("The file can't be read as a picture.", ex);
        }
    }

    private static Thumbnails Write(MagickImage image, string smallPath, string largePath)
    {
        image.AutoOrient();
        image.Strip();
        var width = (int)image.Width;
        var height = (int)image.Height;
        Directory.CreateDirectory(Path.GetDirectoryName(smallPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(largePath)!);

        using (var large = (MagickImage)image.Clone())
        {
            if (Math.Max(width, height) > LargeEdge)
                large.Resize(new MagickGeometry(LargeEdge, LargeEdge) { Greater = true });
            large.Quality = 85;
            WriteAtomically(large, largePath);
        }
        using (var small = (MagickImage)image.Clone())
        {
            var shortEdge = Math.Min(width, height);
            if (shortEdge > SmallEdge)
            {
                var scale = (double)SmallEdge / shortEdge;
                var w = (uint)Math.Max(1, Math.Round(width * scale));
                var h = (uint)Math.Max(1, Math.Round(height * scale));
                // A panorama's long edge stays bounded.
                var longScale = Math.Max(w, h) > SmallMaxLongEdge ? (double)SmallMaxLongEdge / Math.Max(w, h) : 1;
                small.Thumbnail(new MagickGeometry((uint)Math.Max(1, w * longScale), (uint)Math.Max(1, h * longScale)) { IgnoreAspectRatio = true });
            }
            small.Quality = 78;
            WriteAtomically(small, smallPath);
        }
        return new Thumbnails(width, height);
    }

    private static void WriteAtomically(MagickImage image, string path)
    {
        var temp = path + ".part";
        image.Write(temp, MagickFormat.WebP);
        File.Move(temp, path, overwrite: true);
    }

    // ───────────────────────────── video ─────────────────────────────

    private async Task<byte[]> VideoFrameAsync(string source, CancellationToken ct)
    {
        var ffmpeg = _ffmpeg.Value ?? throw new ThumbnailException("ffmpeg isn't installed on this server.");
        // A frame a little in, so a fade from black doesn't make a black poster;
        // a clip shorter than that gives its first frame (-ss past the end
        // gives nothing, hence the second try).
        foreach (var at in new[] { "0.5", "0" })
        {
            var psi = new ProcessStartInfo(ffmpeg)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-ss", at, "-i", source,
                         "-frames:v", "1", "-f", "image2pipe", "-vcodec", "png", "-" })
                psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi) ?? throw new ThumbnailException("ffmpeg didn't start.");
            using var output = new MemoryStream();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(FfmpegTimeout);
            try
            {
                var copy = process.StandardOutput.BaseStream.CopyToAsync(output, timeout.Token);
                var drain = process.StandardError.ReadToEndAsync(timeout.Token);
                await Task.WhenAll(copy, drain);
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                if (ct.IsCancellationRequested) throw;
                throw new ThumbnailException("ffmpeg took too long.");
            }
            if (process.ExitCode == 0 && output.Length > 0) return output.ToArray();
        }
        throw new ThumbnailException("ffmpeg found no frame in the video.");
    }

    // ───────────────────────────── probes ─────────────────────────────

    private bool ProbeMagick()
    {
        try
        {
            // Large enough for a 200-megapixel panorama, small enough that a
            // crafted file claiming 100k × 100k pixels is refused up front.
            ResourceLimits.Width = 60_000;
            ResourceLimits.Height = 60_000;
            ResourceLimits.Area = 400_000_000;
            _ = MagickNET.Version;
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or BadImageFormatException or EntryPointNotFoundException)
        {
            _logger.LogWarning("Photo previews are off: Magick.NET has no native library for this platform ({Error})", ex.GetType().Name);
            return false;
        }
    }

    private static string? FindFfmpeg()
    {
        var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }
}
