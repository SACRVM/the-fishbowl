using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Fishbowl.Mail.Text;

/// <summary>
/// Dependency-free HTML to plain-text conversion tuned for mail bodies read by agents:
/// drops script/style/head, turns block elements into line breaks, keeps link targets,
/// decodes entities and collapses runs of whitespace.
/// </summary>
public static partial class HtmlToText
{
    public static string Convert(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        var s = html;
        s = DropBlocks().Replace(s, string.Empty);
        s = Comments().Replace(s, string.Empty);

        // Links: <a href="X">text</a>  ->  text (X)   (skip when text already is the url)
        s = Anchors().Replace(s, m =>
        {
            var href = WebUtility.HtmlDecode(m.Groups["href"].Value).Trim();
            var inner = StripTags().Replace(m.Groups["inner"].Value, string.Empty).Trim();
            inner = WebUtility.HtmlDecode(inner);
            if (href.Length == 0 || href.StartsWith('#') || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                return inner;
            if (inner.Length == 0 || string.Equals(inner, href, StringComparison.OrdinalIgnoreCase))
                return href;
            return $"{inner} ({href})";
        });

        s = LineBreaks().Replace(s, "\n");
        s = ListItems().Replace(s, "\n- ");
        s = TableCells().Replace(s, "\t");
        s = BlockTags().Replace(s, "\n\n");
        s = StripTags().Replace(s, string.Empty);
        s = WebUtility.HtmlDecode(s);
        s = s.Replace(' ', ' ');

        return Normalize(s);
    }

    /// <summary>Collapse horizontal whitespace, trim lines, cap blank runs at one empty line.</summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        var blank = 0;
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = HorizontalWs().Replace(raw, " ").Trim();
            if (line.Length == 0)
            {
                if (++blank > 1) continue;
            }
            else blank = 0;
            sb.Append(line).Append('\n');
        }
        return sb.ToString().Trim();
    }

    public static (string Text, bool Truncated) Truncate(string text, int maxChars)
    {
        if (maxChars <= 0 || text.Length <= maxChars) return (text, false);
        return (text[..maxChars] + "\n\n[… truncated, " + (text.Length - maxChars) + " more characters]", true);
    }

    [GeneratedRegex(@"<(script|style|head|title|noscript)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DropBlocks();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comments();

    [GeneratedRegex(@"<a\b[^>]*?href\s*=\s*[""'](?<href>[^""']*)[""'][^>]*>(?<inner>.*?)</a\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Anchors();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreaks();

    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItems();

    [GeneratedRegex(@"</t[dh]\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex TableCells();

    [GeneratedRegex(@"</?(p|div|tr|table|h[1-6]|ul|ol|blockquote|pre|section|article|header|footer|hr)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTags();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex StripTags();

    [GeneratedRegex(@"[ \t\f\v]+")]
    private static partial Regex HorizontalWs();
}
