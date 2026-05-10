namespace Fishbowl.Core.Models;

// One row per (user, channel_type) — `channel_id` is the platform-side id
// the dispatcher needs to deliver the message. For Discord this is the
// numeric DM channel id; for Telegram it'd be the chat id; for web push
// the subscription endpoint. `Enabled` lets a user mute a channel without
// unlinking it (CONCEPT.md § Notification Channels).
public class NotificationChannel
{
    public string Id { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string ChannelType { get; set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; }
}
