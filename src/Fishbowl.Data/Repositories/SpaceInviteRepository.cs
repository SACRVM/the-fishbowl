using Dapper;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Data.Repositories;

public class SpaceInviteRepository : ISpaceInviteRepository
{
    private readonly DatabaseFactory _dbFactory;
    private readonly ILogger<SpaceInviteRepository> _logger;

    public SpaceInviteRepository(DatabaseFactory dbFactory, ILogger<SpaceInviteRepository>? logger = null)
    {
        _dbFactory = dbFactory;
        _logger = logger ?? NullLogger<SpaceInviteRepository>.Instance;
    }

    public async Task<(SpaceInvite Invite, string Token)> CreateAsync(
        string spaceId, SpaceRole role, string createdBy, TimeSpan validFor, CancellationToken ct = default)
    {
        if (role == SpaceRole.Owner) throw new ArgumentException("An invitation can't make an owner.", nameof(role));
        var now = DateTime.UtcNow;
        var invite = new SpaceInvite
        {
            Id = Ulid.NewUlid().ToString(),
            SpaceId = spaceId,
            Role = role.ToDbValue(),
            CreatedBy = createdBy,
            CreatedAt = now,
            ExpiresAt = now + validFor,
        };
        var token = SpaceInvites.NewToken();
        using var db = _dbFactory.CreateSystemConnection();
        await db.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO space_invites(id, space_id, token_hash, role, created_by, created_at, expires_at)
            VALUES (@Id, @SpaceId, @hash, @Role, @CreatedBy, @created, @expires)",
            new
            {
                invite.Id,
                invite.SpaceId,
                hash = SpaceInvites.Hash(token),
                invite.Role,
                invite.CreatedBy,
                created = invite.CreatedAt.ToString("o"),
                expires = invite.ExpiresAt.ToString("o"),
            }, cancellationToken: ct));
        _logger.LogInformation("Invitation {InviteId} to space {SpaceId} created by {UserId}", invite.Id, spaceId, createdBy);
        return (invite, token);
    }

    public async Task<IReadOnlyList<SpaceInvite>> ListOpenAsync(string spaceId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        var rows = await db.QueryAsync<SpaceInvite>(new CommandDefinition(@"
            SELECT * FROM space_invites
            WHERE space_id = @spaceId AND used_at IS NULL AND expires_at > @now
            ORDER BY created_at DESC",
            new { spaceId, now = DateTime.UtcNow.ToString("o") }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<bool> RevokeAsync(string spaceId, string inviteId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        return await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM space_invites WHERE id = @inviteId AND space_id = @spaceId AND used_at IS NULL",
            new { spaceId, inviteId }, cancellationToken: ct)) > 0;
    }

    public async Task<(SpaceInvite? Invite, InviteProblem Problem)> CheckAsync(string token, CancellationToken ct = default)
    {
        if (!SpaceInvites.LooksValid(token)) return (null, InviteProblem.Unknown);
        using var db = _dbFactory.CreateSystemConnection();
        var invite = await db.QuerySingleOrDefaultAsync<SpaceInvite>(new CommandDefinition(
            "SELECT * FROM space_invites WHERE token_hash = @hash",
            new { hash = SpaceInvites.Hash(token) }, cancellationToken: ct));
        return (invite, ProblemOf(invite, DateTime.UtcNow));
    }

    private static InviteProblem ProblemOf(SpaceInvite? invite, DateTime now) =>
        invite is null ? InviteProblem.Unknown
        : invite.UsedAt is not null ? InviteProblem.Used
        : invite.ExpiresAt.ToUniversalTime() <= now ? InviteProblem.Expired
        : InviteProblem.None;

    public async Task<InviteRedemption?> RedeemAsync(string token, string userId, CancellationToken ct = default)
    {
        if (!SpaceInvites.LooksValid(token)) return null;
        using var db = _dbFactory.CreateSystemConnection();
        using var tx = db.BeginTransaction();
        var now = DateTime.UtcNow;
        var invite = await db.QuerySingleOrDefaultAsync<SpaceInvite>(new CommandDefinition(
            "SELECT * FROM space_invites WHERE token_hash = @hash",
            new { hash = SpaceInvites.Hash(token) }, transaction: tx, cancellationToken: ct));
        if (ProblemOf(invite, now) != InviteProblem.None) return null;
        var space = await db.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM spaces WHERE id = @SpaceId", new { invite!.SpaceId }, transaction: tx, cancellationToken: ct));
        if (space == 0) return null;

        var role = SpaceRoleExtensions.FromDbValue(invite.Role);
        // The maker must still be allowed to hand out this role: removing or
        // demoting someone withdraws their open invitations.
        var maker = await db.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT role FROM space_members WHERE space_id = @SpaceId AND user_id = @CreatedBy",
            new { invite.SpaceId, invite.CreatedBy }, transaction: tx, cancellationToken: ct));
        if (SpaceRoleExtensions.TryParse(maker) is not { } makerRole || !makerRole.CanGrant(role)) return null;
        var current = await db.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT role FROM space_members WHERE space_id = @SpaceId AND user_id = @userId",
            new { invite.SpaceId, userId }, transaction: tx, cancellationToken: ct));
        var had = current is null ? (SpaceRole?)null : SpaceRoleExtensions.FromDbValue(current);
        if (had is null)
            await db.ExecuteAsync(new CommandDefinition(
                "INSERT INTO space_members(space_id, user_id, role, joined_at) VALUES (@SpaceId, @userId, @role, @now)",
                new { invite.SpaceId, userId, role = invite.Role, now = now.ToString("o") }, transaction: tx, cancellationToken: ct));
        else if (had < role)
            await db.ExecuteAsync(new CommandDefinition(
                "UPDATE space_members SET role = @role WHERE space_id = @SpaceId AND user_id = @userId",
                new { invite.SpaceId, userId, role = invite.Role }, transaction: tx, cancellationToken: ct));

        await db.ExecuteAsync(new CommandDefinition(
            "UPDATE space_invites SET used_by = @userId, used_at = @now WHERE id = @Id",
            new { invite.Id, userId, now = now.ToString("o") }, transaction: tx, cancellationToken: ct));
        tx.Commit();
        _logger.LogInformation("Invitation {InviteId} used by {UserId}", invite.Id, userId);
        return new InviteRedemption(invite.SpaceId, had is { } h && h > role ? h : role);
    }
}
