using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Fishbowl.Core.Models;

namespace Fishbowl.Core.Util;

/// <summary>
/// A mail's stored text marks where a file stood in it: a line of its own
/// holding <see cref="Mark"/> (U+FFFC, the object replacement character) for
/// each attachment with <see cref="MailAttachment.InText"/>, in their order —
/// "text, photo, text" as the iPhone writes it. The Mail app shows the file
/// there; everything else reads the text without the marks or with the file's
/// name in their place.
/// </summary>
public static partial class MailText
{
    public const char Mark = '\uFFFC';

    /// <summary>The text without its marks (search, the list's line, the model).</summary>
    public static string Plain(string text) =>
        text.Contains(Mark) ? Blank().Replace(Marks().Replace(text, "\n\n"), "\n\n").Trim() : text;

    /// <summary>The text with each mark replaced by its file's label (an agent,
    /// a quoted reply); a mark without a file goes.</summary>
    public static string Named(string text, IReadOnlyList<MailAttachment> attachments, Func<MailAttachment, string> label)
    {
        if (!text.Contains(Mark)) return text;
        var files = attachments.Where(a => a.InText).ToList();
        var n = 0;
        var named = Marks().Replace(text, _ => n < files.Count ? $"\n\n{label(files[n++])}\n\n" : "\n\n");
        return Blank().Replace(named, "\n\n").Trim();
    }

    [GeneratedRegex(@"\s*\uFFFC\s*")]
    private static partial Regex Marks();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex Blank();
}
