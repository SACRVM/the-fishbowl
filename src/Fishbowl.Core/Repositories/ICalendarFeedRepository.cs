namespace Fishbowl.Core.Repositories;

/// <summary>
/// A workspace's calendar subscription links (sync spec, phase 1): a random
/// token in the URL (<c>/feeds/&lt;token&gt;.ics</c>), only its SHA-256 kept,
/// revocable. A link belongs to the person who made it, and works only while
/// they are active and — in a space — a member.
/// </summary>
public interface ICalendarFeedRepository
{
    /// <summary>A new link for the workspace; the token is returned once.</summary>
    Task<(CalendarFeed Feed, string Token)> CreateAsync(ContextRef ctx, string userId, CancellationToken ct = default);

    /// <summary>The caller's own links of the workspace, newest first.</summary>
    Task<IReadOnlyList<CalendarFeed>> ListAsync(ContextRef ctx, string userId, CancellationToken ct = default);

    /// <summary>Revokes one of the caller's links; false when there is no such link of theirs.</summary>
    Task<bool> DeleteAsync(ContextRef ctx, string userId, string id, CancellationToken ct = default);

    /// <summary>The link a token opens, or null.</summary>
    Task<CalendarFeed?> FindByTokenAsync(string token, CancellationToken ct = default);
}

public sealed record CalendarFeed(string Id, string ContextType, string ContextId, string CreatedBy, DateTime CreatedAt);
