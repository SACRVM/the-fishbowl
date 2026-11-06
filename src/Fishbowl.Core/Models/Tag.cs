using System;

namespace Fishbowl.Core.Models;

public class Tag
{
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    /// <summary>Notes carrying the tag.</summary>
    public int UsageCount { get; set; }
    /// <summary>Mail conversations carrying the tag (on any of their messages).</summary>
    public int MailCount { get; set; }
    /// <summary>Mail rules that add the tag.</summary>
    public int RuleCount { get; set; }
    /// <summary>Todos carrying the tag (user/space schema v31).</summary>
    public int TodoCount { get; set; }
    /// <summary>What the tag means — one line, for people and agents (v31).</summary>
    public string? Description { get; set; }

    // Protection flags — see SystemTags for semantics.
    public bool IsSystem { get; set; }
    public bool UserAssignable { get; set; } = true;
    public bool UserRemovable { get; set; } = true;
}
