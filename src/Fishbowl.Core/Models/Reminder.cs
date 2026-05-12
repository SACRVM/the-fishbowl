namespace Fishbowl.Core.Models;

// A reminder dispatch record. Lives in the per-context DB; rows are
// created the moment the scheduler decides to fire (idempotency latch),
// so duplicate ticks against the same trigger don't double-notify. The
// channel_type/channel_id columns capture *where it actually got sent*
// rather than where it ought to be sent — notification_channels is the
// live source of truth for the latter, which can change between schedule
// and send.
public class Reminder
{
    public string Id { get; set; } = string.Empty;     // ULID
    public string EventId { get; set; } = string.Empty;
    public DateTime ScheduledAt { get; set; }          // trigger time (start_at - reminder_minutes)
    public DateTime? SentAt { get; set; }              // null while pending
    public string ChannelType { get; set; } = string.Empty; // bot.Name (e.g. "discord")
    public string ChannelId { get; set; } = string.Empty;   // DM channel snapshot at send time
}
