using Fishbowl.Core.Models;

namespace Fishbowl.Core.Repositories;

public interface INotificationChannelRepository
{
    Task<IEnumerable<NotificationChannel>> ListForUserAsync(string userId, CancellationToken ct = default);
    Task<NotificationChannel?> GetAsync(string userId, string channelType, CancellationToken ct = default);

    // Returns the channel id of the user this channel_id is currently linked
    // to, or null. Used by the bot to look up "which Fishbowl user owns this
    // Discord DM" via the user_mappings table side, but kept here so the bot
    // doesn't need to depend on ISystemRepository for two related lookups.
    Task<string?> FindUserByChannelAsync(string channelType, string channelId, CancellationToken ct = default);

    Task<NotificationChannel> UpsertAsync(string userId, string channelType, string channelId, CancellationToken ct = default);
    Task<bool> SetEnabledAsync(string userId, string channelType, bool enabled, CancellationToken ct = default);
    Task<bool> RemoveAsync(string userId, string channelType, CancellationToken ct = default);
}
