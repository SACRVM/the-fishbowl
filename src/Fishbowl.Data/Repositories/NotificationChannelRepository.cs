using Dapper;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Data.Repositories;

public class NotificationChannelRepository : INotificationChannelRepository
{
    private readonly DatabaseFactory _dbFactory;
    private readonly ILogger<NotificationChannelRepository> _logger;

    public NotificationChannelRepository(
        DatabaseFactory dbFactory,
        ILogger<NotificationChannelRepository>? logger = null)
    {
        _dbFactory = dbFactory;
        _logger = logger ?? NullLogger<NotificationChannelRepository>.Instance;
    }

    public async Task<IEnumerable<NotificationChannel>> ListForUserAsync(string userId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        return await db.QueryAsync<NotificationChannel>(new CommandDefinition(
            "SELECT * FROM notification_channels WHERE user_id = @userId ORDER BY channel_type",
            new { userId }, cancellationToken: ct));
    }

    public async Task<NotificationChannel?> GetAsync(string userId, string channelType, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        return await db.QuerySingleOrDefaultAsync<NotificationChannel>(new CommandDefinition(
            "SELECT * FROM notification_channels WHERE user_id = @userId AND channel_type = @channelType",
            new { userId, channelType }, cancellationToken: ct));
    }

    public async Task<string?> FindUserByChannelAsync(string channelType, string channelId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        return await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT user_id FROM notification_channels WHERE channel_type = @channelType AND channel_id = @channelId",
            new { channelType, channelId }, cancellationToken: ct));
    }

    public async Task<NotificationChannel> UpsertAsync(string userId, string channelType, string channelId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        var existing = await db.QuerySingleOrDefaultAsync<NotificationChannel>(new CommandDefinition(
            "SELECT * FROM notification_channels WHERE user_id = @userId AND channel_type = @channelType",
            new { userId, channelType }, cancellationToken: ct));

        if (existing is not null)
        {
            await db.ExecuteAsync(new CommandDefinition(@"
                UPDATE notification_channels
                SET channel_id = @channelId, enabled = 1
                WHERE user_id = @userId AND channel_type = @channelType",
                new { userId, channelType, channelId }, cancellationToken: ct));

            existing.ChannelId = channelId;
            existing.Enabled = true;
            return existing;
        }

        var row = new NotificationChannel
        {
            Id = Ulid.NewUlid().ToString(),
            UserId = userId,
            ChannelType = channelType,
            ChannelId = channelId,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };

        await db.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO notification_channels (id, user_id, channel_type, channel_id, enabled, created_at)
            VALUES (@Id, @UserId, @ChannelType, @ChannelId, 1, @CreatedAt)",
            new
            {
                row.Id,
                row.UserId,
                row.ChannelType,
                row.ChannelId,
                CreatedAt = row.CreatedAt.ToString("o"),
            }, cancellationToken: ct));

        _logger.LogInformation("Linked notification channel {Type} for user {UserId}", channelType, userId);
        return row;
    }

    public async Task<bool> SetEnabledAsync(string userId, string channelType, bool enabled, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        var affected = await db.ExecuteAsync(new CommandDefinition(@"
            UPDATE notification_channels SET enabled = @enabled
            WHERE user_id = @userId AND channel_type = @channelType",
            new { userId, channelType, enabled = enabled ? 1 : 0 }, cancellationToken: ct));
        return affected > 0;
    }

    public async Task<bool> RemoveAsync(string userId, string channelType, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        var affected = await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM notification_channels WHERE user_id = @userId AND channel_type = @channelType",
            new { userId, channelType }, cancellationToken: ct));
        if (affected > 0)
            _logger.LogInformation("Removed notification channel {Type} for user {UserId}", channelType, userId);
        return affected > 0;
    }
}
