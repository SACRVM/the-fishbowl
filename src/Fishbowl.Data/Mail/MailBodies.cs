using System.IO.Compression;
using System.Text;

namespace Fishbowl.Data.Mail;

/// <summary>
/// A mail's HTML as stored (`mail_messages.body_html_z`, user schema v25):
/// UTF-8, zlib-compressed — about a seventh of the text. Only the reader of
/// one message unpacks it; search never does (mail_fts indexes the text part).
/// </summary>
public static class MailBodies
{
    public static byte[]? Pack(string? html)
    {
        if (string.IsNullOrEmpty(html)) return null;
        using var buffer = new MemoryStream();
        using (var z = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(Encoding.UTF8.GetBytes(html));
        return buffer.ToArray();
    }

    public static string? Unpack(byte[]? packed)
    {
        if (packed is not { Length: > 0 }) return null;
        using var z = new ZLibStream(new MemoryStream(packed), CompressionMode.Decompress);
        using var reader = new StreamReader(z, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
