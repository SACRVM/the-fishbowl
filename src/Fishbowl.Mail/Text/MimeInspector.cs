using System.Text;
using Fishbowl.Mail.Models;
using MimeKit;
using MimeKit.IO;
using MimeKit.Utils;

namespace Fishbowl.Mail.Text;

/// <summary>
/// Walks a parsed message the way IMAP numbers body sections (RFC 3501 §6.4.5), so part ids
/// shown to agents match BODY[section] and nothing in the tree is hidden: message/rfc822,
/// message/delivery-status and text/rfc822-headers parts are listed like any other part.
/// </summary>
public static class MimeInspector
{
    public sealed record Node(string? Part, int Depth, MimeEntity Entity);

    /// <summary>
    /// Every entity in document order. A multipart that is the body of a message has no section
    /// number of its own (Part = null); every other entity has one, e.g. "1", "2.1", "3.1.2".
    /// </summary>
    public static IReadOnlyList<Node> Walk(MimeMessage msg)
    {
        var nodes = new List<Node>();
        WalkBody(msg.Body, "", 0, nodes);
        return nodes;
    }

    private static void WalkBody(MimeEntity? body, string prefix, int depth, List<Node> nodes)
    {
        if (body is null) return;
        if (body is Multipart mp)
        {
            nodes.Add(new Node(null, depth, mp));
            for (var i = 0; i < mp.Count; i++) WalkChild(mp[i], Join(prefix, i + 1), depth + 1, nodes);
        }
        else WalkChild(body, Join(prefix, 1), depth, nodes);
    }

    private static void WalkChild(MimeEntity entity, string id, int depth, List<Node> nodes)
    {
        nodes.Add(new Node(id, depth, entity));
        if (entity is Multipart mp)
            for (var i = 0; i < mp.Count; i++) WalkChild(mp[i], $"{id}.{i + 1}", depth + 1, nodes);
        // text/rfc822-headers carries only a header block; its Content-Type describes a body that is not there.
        else if (entity is MessagePart { Message: { } inner } and not TextRfc822Headers)
            WalkBody(inner.Body, id, depth + 1, nodes);
    }

    private static string Join(string prefix, int n) => prefix.Length == 0 ? n.ToString() : $"{prefix}.{n}";

    public static IReadOnlyList<MimePartInfo> Describe(MimeMessage msg)
        => Walk(msg).Select(n => new MimePartInfo(
            n.Part,
            n.Entity.ContentType.MimeType,
            n.Depth,
            SizeOf(n.Entity),
            FileNameOf(n.Entity),
            n.Entity.ContentDisposition?.Disposition)).ToList();

    public static MimeEntity Find(MimeMessage msg, string part)
        => Walk(msg).FirstOrDefault(n => n.Part == part.Trim())?.Entity
           ?? throw new MailException($"Message has no part '{part}'. List the message parts to see their ids.");

    public static string? FileNameOf(MimeEntity e)
        => (e as MimePart)?.FileName ?? e.ContentDisposition?.FileName ?? e.ContentType.Name;

    /// <summary>Encoded size of a leaf, serialized size of an embedded message, null for containers.</summary>
    private static long? SizeOf(MimeEntity e)
    {
        if (e is MimePart { Content.Stream: { CanSeek: true } s }) return s.Length;
        if (e is MessagePart { Message: { } m })
        {
            using var ms = new MeasuringStream();
            m.WriteTo(ms);
            return ms.Length;
        }
        return null;
    }

    /// <summary>
    /// Readable content of one part: text parts decoded with their charset, embedded messages and
    /// header blocks serialized as-is, other leaves decoded as UTF-8. Binary parts should be saved instead.
    /// </summary>
    public static string ReadText(MimeEntity e)
    {
        switch (e)
        {
            case TextPart tp:
                return tp.Text;
            case MessagePart { Message: { } m }:
                return Serialize(m);
            case MimePart { Content: not null } p:
                using (var ms = new MemoryStream())
                {
                    p.Content.DecodeTo(ms);
                    return Encoding.UTF8.GetString(ms.ToArray());
                }
            default:
                throw new MailException($"Part of type {e.ContentType.MimeType} is a container; read its children instead.");
        }
    }

    public static bool IsTextual(MimeEntity e)
        => e is TextPart or MessagePart or MessageDeliveryStatus
           || e.ContentType.MediaType.Equals("text", StringComparison.OrdinalIgnoreCase)
           || e.ContentType.MediaType.Equals("message", StringComparison.OrdinalIgnoreCase);

    public static async Task WriteContentAsync(MimeEntity e, Stream target, CancellationToken ct)
    {
        switch (e)
        {
            case MimePart { Content: not null } p:
                await p.Content.DecodeToAsync(target, ct).ConfigureAwait(false);
                break;
            case MessagePart { Message: { } m }:
                await m.WriteToAsync(target, ct).ConfigureAwait(false);
                break;
            default:
                throw new MailException($"Part of type {e.ContentType.MimeType} has no content of its own.");
        }
    }

    private static string Serialize(MimeMessage m)
    {
        using var ms = new MemoryStream();
        m.WriteTo(ms);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // ---------- DSN (RFC 3464) ----------

    /// <summary>
    /// Extracts the machine-readable part of a delivery status notification plus the headers of the
    /// returned original. Null when the message carries no message/delivery-status part.
    /// </summary>
    public static BounceInfo? ParseBounce(MimeMessage msg)
    {
        var nodes = Walk(msg);
        var dsnIndex = nodes.ToList().FindIndex(n => n.Entity is MessageDeliveryStatus);
        if (dsnIndex < 0) return null;
        var dsn = (MessageDeliveryStatus)nodes[dsnIndex].Entity;

        var groups = dsn.StatusGroups;
        var perMessage = groups.Count > 0 ? groups[0] : new HeaderList();
        var recipients = groups.Skip(1).Select(g => new BounceRecipient(
            StripType(g["Final-Recipient"]),
            StripType(g["Original-Recipient"]),
            Clean(g["Action"]),
            Clean(g["Status"]),
            StripType(g["Diagnostic-Code"]),
            StripType(g["Remote-MTA"]))).ToList();

        // The returned original follows the status part inside the report.
        var original = nodes.Skip(dsnIndex + 1).Select(n => n.Entity).FirstOrDefault(IsOriginal);

        return new BounceInfo(
            StripType(perMessage["Reporting-MTA"]),
            Clean(perMessage["Arrival-Date"]),
            recipients,
            original is null ? null : ReadOriginal(original));
    }

    private static bool IsOriginal(MimeEntity e) => e.ContentType.MimeType.ToLowerInvariant() is
        "message/rfc822" or "message/global" or "text/rfc822-headers" or "message/global-headers";

    private static OriginalHeaders? ReadOriginal(MimeEntity e)
    {
        HeaderList headers;
        if (e is MessagePart { Message: { } m }) headers = m.Headers;
        else if (e is MimePart { Content: not null } p)
        {
            using var ms = new MemoryStream();
            p.Content.DecodeTo(ms);
            ms.Position = 0;
            headers = HeaderList.Load(ms);
        }
        else return null;

        DateTimeOffset? date = DateUtils.TryParse(headers["Date"] ?? "", out var d) ? d : null;
        return new OriginalHeaders(
            Decode(headers, HeaderId.From),
            Decode(headers, HeaderId.To),
            Decode(headers, HeaderId.Subject),
            date,
            Clean(headers["Message-Id"]),
            e.ContentType.MimeType.EndsWith("headers", StringComparison.OrdinalIgnoreCase));
    }

    private static string? Decode(HeaderList h, HeaderId id)
    {
        var header = h.FirstOrDefault(x => x.Id == id);
        if (header is null) return null;
        return Clean(Rfc2047.DecodeText(header.RawValue));
    }

    /// <summary>Unfolds and trims a header value; empty becomes null.</summary>
    private static string? Clean(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        return string.Join(' ', v.Split((char[])['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Drops the "rfc822;" / "smtp;" / "dns;" type prefix of DSN fields.</summary>
    internal static string? StripType(string? v)
    {
        var c = Clean(v);
        if (c is null) return null;
        var semi = c.IndexOf(';');
        return semi > 0 && semi < 16 && !c[..semi].Contains(' ') ? c[(semi + 1)..].Trim() : c;
    }
}
