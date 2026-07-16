using Fishbowl.Core.Models;

namespace Fishbowl.Core.Repositories;

public record SpaceMembership(Space Space, SpaceRole Role);

public interface ISpaceRepository
{
    // Creates a space owned by the given user. Slug is derived from `name` and
    // disambiguated against existing spaces. The creator is inserted as the
    // sole owner member.
    Task<Space> CreateAsync(string ownerUserId, string name, CancellationToken ct = default);

    // Spaces the user belongs to, with their role in each. Ordered by space name.
    Task<IReadOnlyList<SpaceMembership>> ListByMemberAsync(string userId, CancellationToken ct = default);

    Task<Space?> GetBySlugAsync(string slug, CancellationToken ct = default);
    Task<Space?> GetByIdAsync(string spaceId, CancellationToken ct = default);

    // Null if the user isn't a member of the given space.
    Task<SpaceRole?> GetMembershipAsync(string spaceId, string userId, CancellationToken ct = default);

    // Owner-only. Returns true on success, false if the user isn't the owner
    // or the space doesn't exist. Leaves the .db file in place — callers can
    // keep the data for recovery or delete it themselves.
    Task<bool> DeleteAsync(string spaceId, string actingUserId, CancellationToken ct = default);
}
