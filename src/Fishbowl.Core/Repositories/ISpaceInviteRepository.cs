using Fishbowl.Core.Models;

namespace Fishbowl.Core.Repositories;

public interface ISpaceInviteRepository
{
    // Stores a new invitation and returns it with its token (the only time
    // the token exists outside the link).
    Task<(SpaceInvite Invite, string Token)> CreateAsync(
        string spaceId, SpaceRole role, string createdBy, TimeSpan validFor, CancellationToken ct = default);

    // The space's invitations that can still be used, newest first.
    Task<IReadOnlyList<SpaceInvite>> ListOpenAsync(string spaceId, CancellationToken ct = default);

    Task<bool> RevokeAsync(string spaceId, string inviteId, CancellationToken ct = default);

    // What's wrong with a token, if anything, and the invitation behind it.
    Task<(SpaceInvite? Invite, InviteProblem Problem)> CheckAsync(string token, CancellationToken ct = default);

    // Uses the invitation for `userId` in one transaction: the user joins the
    // space with its role (an existing member keeps a higher role), the
    // invitation is spent. Null (and nothing changes) when it can't be used.
    Task<InviteRedemption?> RedeemAsync(string token, string userId, CancellationToken ct = default);
}
