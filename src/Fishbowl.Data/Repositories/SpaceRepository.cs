using Dapper;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Util;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Data.Repositories;

public class SpaceRepository : ISpaceRepository
{
    private readonly DatabaseFactory _dbFactory;
    private readonly ILogger<SpaceRepository> _logger;

    public SpaceRepository(DatabaseFactory dbFactory, ILogger<SpaceRepository>? logger = null)
    {
        _dbFactory = dbFactory;
        _logger = logger ?? NullLogger<SpaceRepository>.Instance;
    }

    public async Task<Space> CreateAsync(string ownerUserId, string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Space name is required.", nameof(name));

        using var db = _dbFactory.CreateSystemConnection();
        using var tx = db.BeginTransaction();

        var baseSlug = SlugGenerator.FromName(name);
        var slug = SlugGenerator.DedupeAgainst(baseSlug, candidate =>
            db.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM spaces WHERE slug = @candidate",
                new { candidate }, transaction: tx) > 0);

        var space = new Space
        {
            Id = Ulid.NewUlid().ToString(),
            Slug = slug,
            Name = name.Trim(),
            CreatedBy = ownerUserId,
            CreatedAt = DateTime.UtcNow,
        };

        await db.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO spaces(id, slug, name, created_by, created_at)
            VALUES (@Id, @Slug, @Name, @CreatedBy, @CreatedAt)",
            new
            {
                space.Id,
                space.Slug,
                space.Name,
                space.CreatedBy,
                CreatedAt = space.CreatedAt.ToString("o"),
            }, transaction: tx, cancellationToken: ct));

        await db.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO space_members(space_id, user_id, role, joined_at)
            VALUES (@SpaceId, @UserId, @Role, @JoinedAt)",
            new
            {
                SpaceId = space.Id,
                UserId = ownerUserId,
                Role = SpaceRole.Owner.ToDbValue(),
                JoinedAt = space.CreatedAt.ToString("o"),
            }, transaction: tx, cancellationToken: ct));

        tx.Commit();
        _logger.LogInformation("Created space {SpaceId} slug={Slug}", space.Id, space.Slug);
        return space;
    }

    public async Task<IReadOnlyList<SpaceMembership>> ListByMemberAsync(string userId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        var rows = await db.QueryAsync<(string Id, string Slug, string Name, string CreatedBy, string CreatedAt, string Role, string? Color)>(
            new CommandDefinition(@"
                SELECT t.id AS Id, t.slug AS Slug, t.name AS Name,
                       t.created_by AS CreatedBy, t.created_at AS CreatedAt,
                       m.role AS Role, t.color AS Color
                FROM spaces t
                JOIN space_members m ON m.space_id = t.id
                WHERE m.user_id = @userId
                ORDER BY t.name",
                new { userId }, cancellationToken: ct));

        return rows.Select(r => new SpaceMembership(
            new Space
            {
                Id = r.Id,
                Slug = r.Slug,
                Name = r.Name,
                CreatedBy = r.CreatedBy,
                CreatedAt = DateTime.Parse(r.CreatedAt, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind),
                Color = r.Color,
            },
            SpaceRoleExtensions.FromDbValue(r.Role))).ToList();
    }

    public async Task<Space?> GetBySlugAsync(string slug, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        return await db.QuerySingleOrDefaultAsync<Space>(new CommandDefinition(
            "SELECT * FROM spaces WHERE slug = @slug",
            new { slug }, cancellationToken: ct));
    }

    public async Task<Space?> GetByIdAsync(string spaceId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        return await db.QuerySingleOrDefaultAsync<Space>(new CommandDefinition(
            "SELECT * FROM spaces WHERE id = @spaceId",
            new { spaceId }, cancellationToken: ct));
    }

    public async Task<SpaceRole?> GetMembershipAsync(string spaceId, string userId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        var role = await db.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT role FROM space_members WHERE space_id = @spaceId AND user_id = @userId",
            new { spaceId, userId }, cancellationToken: ct));
        return role is null ? null : SpaceRoleExtensions.FromDbValue(role);
    }

    public async Task<bool> SetColorAsync(string spaceId, string actingUserId, string? color, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        var role = await db.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT role FROM space_members WHERE space_id = @spaceId AND user_id = @actingUserId",
            new { spaceId, actingUserId }, cancellationToken: ct));
        if (role != SpaceRole.Owner.ToDbValue()) return false;

        await db.ExecuteAsync(new CommandDefinition(
            "UPDATE spaces SET color = @color WHERE id = @spaceId",
            new { spaceId, color }, cancellationToken: ct));
        return true;
    }

    public async Task<bool> SetAppMessageTextAsync(string spaceId, string actingUserId, bool on, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        var role = await db.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT role FROM space_members WHERE space_id = @spaceId AND user_id = @actingUserId",
            new { spaceId, actingUserId }, cancellationToken: ct));
        if (role != SpaceRole.Owner.ToDbValue()) return false;

        await db.ExecuteAsync(new CommandDefinition(
            "UPDATE spaces SET app_message_text = @on WHERE id = @spaceId",
            new { spaceId, on = on ? 1 : 0 }, cancellationToken: ct));
        return true;
    }

    public async Task<bool> DeleteAsync(string spaceId, string actingUserId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        using var tx = db.BeginTransaction();

        var role = await db.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT role FROM space_members WHERE space_id = @spaceId AND user_id = @actingUserId",
            new { spaceId, actingUserId }, transaction: tx, cancellationToken: ct));

        if (role != SpaceRole.Owner.ToDbValue()) return false;

        await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM space_members WHERE space_id = @spaceId; DELETE FROM space_invites WHERE space_id = @spaceId;",
            new { spaceId }, transaction: tx, cancellationToken: ct));

        var affected = await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM spaces WHERE id = @spaceId",
            new { spaceId }, transaction: tx, cancellationToken: ct));

        tx.Commit();
        _logger.LogInformation("Deleted space {SpaceId} by owner {UserId}", spaceId, actingUserId);
        return affected > 0;
    }

    private sealed record MemberDbRow(string UserId, string? Name, string? AvatarUrl, string Role, string JoinedAt);

    public async Task<IReadOnlyList<SpaceMemberRow>> ListMembersAsync(string spaceId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        var rows = await db.QueryAsync<MemberDbRow>(new CommandDefinition(@"
            SELECT m.user_id AS UserId, u.name AS Name, u.avatar_url AS AvatarUrl, m.role AS Role, m.joined_at AS JoinedAt
            FROM space_members m JOIN users u ON u.id = m.user_id
            WHERE m.space_id = @spaceId",
            new { spaceId }, cancellationToken: ct));
        return rows
            .Select(r => new SpaceMemberRow(r.UserId, r.Name, r.AvatarUrl, SpaceRoleExtensions.FromDbValue(r.Role),
                DateTime.Parse(r.JoinedAt, null, System.Globalization.DateTimeStyles.RoundtripKind)))
            .OrderByDescending(r => r.Role)
            .ThenBy(r => r.Name ?? "", StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<SpaceRole> AddMemberAsync(string spaceId, string userId, SpaceRole role, CancellationToken ct = default)
    {
        if (role == SpaceRole.Owner) throw new ArgumentException("A space has one owner.", nameof(role));
        using var db = _dbFactory.CreateSystemConnection();
        using var tx = db.BeginTransaction();
        var current = await db.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT role FROM space_members WHERE space_id = @spaceId AND user_id = @userId",
            new { spaceId, userId }, transaction: tx, cancellationToken: ct));
        var existing = current is null ? (SpaceRole?)null : SpaceRoleExtensions.FromDbValue(current);
        if (existing is { } had && had >= role) return had;
        await db.ExecuteAsync(new CommandDefinition(existing is null
                ? "INSERT INTO space_members(space_id, user_id, role, joined_at) VALUES (@spaceId, @userId, @role, @now)"
                : "UPDATE space_members SET role = @role WHERE space_id = @spaceId AND user_id = @userId",
            new { spaceId, userId, role = role.ToDbValue(), now = DateTime.UtcNow.ToString("o") },
            transaction: tx, cancellationToken: ct));
        tx.Commit();
        return role;
    }

    public async Task<bool> SetRoleAsync(string spaceId, string userId, SpaceRole role, CancellationToken ct = default)
    {
        if (role == SpaceRole.Owner) return false;
        using var db = _dbFactory.CreateSystemConnection();
        return await db.ExecuteAsync(new CommandDefinition(
            "UPDATE space_members SET role = @role WHERE space_id = @spaceId AND user_id = @userId AND role <> 'owner'",
            new { spaceId, userId, role = role.ToDbValue() }, cancellationToken: ct)) > 0;
    }

    public async Task<bool> RemoveMemberAsync(string spaceId, string userId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        return await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM space_members WHERE space_id = @spaceId AND user_id = @userId AND role <> 'owner'",
            new { spaceId, userId }, cancellationToken: ct)) > 0;
    }
}
