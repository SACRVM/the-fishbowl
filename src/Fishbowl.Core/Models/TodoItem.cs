using System;

namespace Fishbowl.Core.Models;

public class TodoItem
{
    public string Id { get; set; } = string.Empty; // ULID
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime? DueAt { get; set; }
    public DateTime? ReminderAt { get; set; }
    public string? Source { get; set; } // Note ID or other source
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    // The user's order in the list (ascending). Null on create = append at
    // the end; null on update = keep the stored position.
    public double? Position { get; set; }
}
