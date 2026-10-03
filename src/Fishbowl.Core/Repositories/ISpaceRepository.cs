using Fishbowl.Core.Models;

namespace Fishbowl.Core.Repositories;

public record SpaceMembership(Space Space, SpaceRole Role);

// One member of a space as the other members see them: a name and a
// picture, never an e-mail address.
public record SpaceMemberRow(string UserId, string? Name, string? AvatarUrl, SpaceRole Role, DateTime JoinedAt);

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

    // Owner-only. `color` is a TagPalette slot or null (default). Returns
    // false if the user isn't the owner. Callers validate the slot.
    Task<bool> SetColorAsync(string spaceId, string actingUserId, string? color, CancellationToken ct = default);

    // Owner only, like the colour: false if the actor isn't the owner.
    Task<bool> SetAppMessageTextAsync(string spaceId, string actingUserId, bool on, CancellationToken ct = default);

    // Everyone in the space, owner first, then by role (highest first) and name.
    Task<IReadOnlyList<SpaceMemberRow>> ListMembersAsync(string spaceId, CancellationToken ct = default);

    // Adds a member, or raises an existing member to `role` — never lowers
    // one and never touches the owner. Returns the role the user now has.
    Task<SpaceRole> AddMemberAsync(string spaceId, string userId, SpaceRole role, CancellationToken ct = default);

    // Changes a member's role. False when the user isn't a member or is the
    // owner (a space has exactly one; ownership doesn't move this way).
    // Callers check who may hand out which role (SpaceRole.CanGrant).
    Task<bool> SetRoleAsync(string spaceId, string userId, SpaceRole role, CancellationToken ct = default);

    // Removes a member. False when the user isn't one or is the owner.
    Task<bool> RemoveMemberAsync(string spaceId, string userId, CancellationToken ct = default);
}
