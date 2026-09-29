namespace Fishbowl.Core.Models;

// One deleted thing in a workspace's trash (user/space schema v13): what it
// was, its title, who deleted it and when. The row's snapshot stays in the
// DB and never goes over the wire — restore puts it back.
public class TrashItem
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? DeletedBy { get; set; }
    public DateTime DeletedAt { get; set; }
}

public static class TrashKinds
{
    public const string Note = "note";
    public const string Todo = "todo";
    public const string Event = "event";
    public const string Contact = "contact";
}

// Restore works or doesn't: the item's id is taken again, or a unique value
// or a link it needs is gone, and the snapshot stays in the trash.
public enum TrashRestore { Restored, NotFound, Conflict }
