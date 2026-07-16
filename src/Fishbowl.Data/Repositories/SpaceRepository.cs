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
        var rows = await db.QueryAsync<(string Id, string Slug, string Name, string CreatedBy, string CreatedAt, string Role)>(
            new CommandDefinition(@"
                SELECT t.id AS Id, t.slug AS Slug, t.name AS Name,
                       t.created_by AS CreatedBy, t.created_at AS CreatedAt,
                       m.role AS Role
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

    public async Task<bool> DeleteAsync(string spaceId, string actingUserId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        using var tx = db.BeginTransaction();

        var role = await db.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT role FROM space_members WHERE space_id = @spaceId AND user_id = @actingUserId",
            new { spaceId, actingUserId }, transaction: tx, cancellationToken: ct));

        if (role != SpaceRole.Owner.ToDbValue()) return false;

        await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM space_members WHERE space_id = @spaceId",
            new { spaceId }, transaction: tx, cancellationToken: ct));

        var affected = await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM spaces WHERE id = @spaceId",
            new { spaceId }, transaction: tx, cancellationToken: ct));

        tx.Commit();
        _logger.LogInformation("Deleted space {SpaceId} by owner {UserId}", spaceId, actingUserId);
        return affected > 0;
    }
}
