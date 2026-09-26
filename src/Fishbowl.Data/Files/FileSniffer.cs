using System.Text;

namespace Fishbowl.Data.Files;

// The effective type of a file, from its first bytes — never from the
// client's declaration (spec § Security rule 1). A small magic-number table,
// then a "valid UTF-8, no NUL" text test; the extension only breaks ties
// between text types. Markup that a browser would run (HTML, SVG, XML,
// JavaScript) is recognised so it is never served inline, whatever it is
// called. Anything unrecognised is application/octet-stream.
public static class FileSniffer
{
    public const string OctetStream = "application/octet-stream";
    public const string Pdf = "application/pdf";

    private static readonly Dictionary<string, string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [""] = "text/plain",
        [".txt"] = "text/plain",
        [".log"] = "text/plain",
        [".text"] = "text/plain",
        [".md"] = "text/markdown",
        [".markdown"] = "text/markdown",
        [".csv"] = "text/csv",
        [".tsv"] = "text/tab-separated-values",
        [".json"] = "application/json",
        [".yaml"] = "text/yaml",
        [".yml"] = "text/yaml",
        [".ini"] = "text/plain",
        [".toml"] = "text/plain",
        [".cfg"] = "text/plain",
        [".conf"] = "text/plain",
        [".cs"] = "text/plain",
        [".py"] = "text/plain",
        [".java"] = "text/plain",
        [".c"] = "text/plain",
        [".h"] = "text/plain",
        [".cpp"] = "text/plain",
        [".go"] = "text/plain",
        [".rs"] = "text/plain",
        [".ts"] = "text/plain",
        [".sh"] = "text/plain",
        [".ps1"] = "text/plain",
        [".sql"] = "text/plain",
        [".css"] = "text/plain",
        [".srt"] = "text/plain",
        [".vtt"] = "text/plain",
        // Markup and script a browser would execute: recognised, never inline.
        [".html"] = "text/html",
        [".htm"] = "text/html",
        [".xhtml"] = "text/html",
        [".svg"] = "image/svg+xml",
        [".xml"] = "application/xml",
        [".js"] = "text/javascript",
        [".mjs"] = "text/javascript",
    };

    // Types a browser may render inline from Fishbowl's origin without
    // running code: raster images, audio, video, plain text.
    private static readonly HashSet<string> InlineImages = new(StringComparer.Ordinal)
    {
        "image/png", "image/jpeg", "image/gif", "image/webp", "image/avif", "image/bmp", "image/x-icon",
    };

    private static readonly HashSet<string> TextServedPlain = new(StringComparer.Ordinal)
    {
        "text/plain", "text/markdown", "text/csv", "text/tab-separated-values", "application/json", "text/yaml",
    };

    public static bool IsInlineSafe(string mime)
        => InlineImages.Contains(mime) || mime.StartsWith("audio/", StringComparison.Ordinal)
        || mime.StartsWith("video/", StringComparison.Ordinal) || TextServedPlain.Contains(mime);

    // Text types go out inline as text/plain (Markdown is rendered by the
    // client, never by the browser).
    public static string InlineContentType(string mime)
        => TextServedPlain.Contains(mime) ? "text/plain; charset=utf-8" : mime;

    public static string Sniff(ReadOnlySpan<byte> head, string fileName)
    {
        if (head.Length == 0) return TypeForText(fileName, head);

        if (StartsWith(head, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return "image/png";
        if (StartsWith(head, [0xFF, 0xD8, 0xFF])) return "image/jpeg";
        if (StartsWith(head, "GIF87a"u8) || StartsWith(head, "GIF89a"u8)) return "image/gif";
        if (head.Length >= 12 && StartsWith(head, "RIFF"u8))
        {
            var form = head.Slice(8, 4);
            if (form.SequenceEqual("WEBP"u8)) return "image/webp";
            if (form.SequenceEqual("WAVE"u8)) return "audio/wav";
            if (form.SequenceEqual("AVI "u8)) return "video/x-msvideo";
        }
        if (head.Length >= 12 && head.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            var brand = Encoding.ASCII.GetString(head.Slice(8, 4));
            return brand switch
            {
                "avif" or "avis" => "image/avif",
                "heic" or "heix" or "mif1" or "msf1" => "image/heic",
                "M4A " or "M4B " => "audio/mp4",
                "qt  " => "video/quicktime",
                _ => "video/mp4",
            };
        }
        if (StartsWith(head, "BM"u8) && head.Length >= 14) return "image/bmp";
        if (StartsWith(head, [0x00, 0x00, 0x01, 0x00])) return "image/x-icon";
        if (StartsWith(head, "%PDF-"u8)) return Pdf;
        if (StartsWith(head, [0x50, 0x4B, 0x03, 0x04]) || StartsWith(head, [0x50, 0x4B, 0x05, 0x06])) return "application/zip";
        if (StartsWith(head, "ID3"u8) || head.Length >= 2 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0) return "audio/mpeg";
        if (StartsWith(head, "OggS"u8)) return "audio/ogg";
        if (StartsWith(head, "fLaC"u8)) return "audio/flac";
        if (StartsWith(head, [0x1A, 0x45, 0xDF, 0xA3])) return "video/webm";

        return IsText(head) ? TypeForText(fileName, head) : OctetStream;
    }

    private static string TypeForText(string fileName, ReadOnlySpan<byte> head)
    {
        // Markup is markup whatever the file is called.
        var start = Encoding.UTF8.GetString(head[..Math.Min(head.Length, 1024)]).TrimStart('﻿', ' ', '\t', '\r', '\n').ToLowerInvariant();
        if (start.StartsWith("<!doctype html", StringComparison.Ordinal) || start.StartsWith("<html", StringComparison.Ordinal)
            || start.Contains("<script", StringComparison.Ordinal))
            return "text/html";
        if (start.StartsWith("<svg", StringComparison.Ordinal) || start.StartsWith("<?xml", StringComparison.Ordinal) && start.Contains("<svg", StringComparison.Ordinal))
            return "image/svg+xml";

        var ext = Path.GetExtension(fileName);
        return TextExtensions.TryGetValue(ext, out var mime) ? mime : OctetStream;
    }

    // Valid UTF-8 without NUL. The last few bytes may be a cut multi-byte
    // sequence (the 4 KB window ends mid-character) — that still counts.
    private static bool IsText(ReadOnlySpan<byte> head)
    {
        if (head.IndexOf((byte)0) >= 0) return false;
        var i = 0;
        while (i < head.Length)
        {
            var b = head[i];
            int n = b < 0x80 ? 0 : (b & 0xE0) == 0xC0 ? 1 : (b & 0xF0) == 0xE0 ? 2 : (b & 0xF8) == 0xF0 ? 3 : -1;
            if (n < 0) return false;
            if (i + n >= head.Length) return head.Length >= FileLimitsSniff;   // cut at the window's end
            for (var k = 1; k <= n; k++)
                if ((head[i + k] & 0xC0) != 0x80) return false;
            if (b < 0x20 && b is not (byte)'\t' and not (byte)'\n' and not (byte)'\r' and not 0x0C) return false;
            i += n + 1;
        }
        return true;
    }

    private const int FileLimitsSniff = Fishbowl.Core.Files.FileLimits.SniffBytes;

    private static bool StartsWith(ReadOnlySpan<byte> head, ReadOnlySpan<byte> magic)
        => head.Length >= magic.Length && head[..magic.Length].SequenceEqual(magic);
}
