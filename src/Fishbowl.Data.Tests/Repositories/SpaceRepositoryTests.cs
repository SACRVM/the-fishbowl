using Dapper;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fishbowl.Data.Tests.Repositories;

public class SpaceRepositoryTests : IDisposable
{
    private readonly string _dataDir;
    private readonly DatabaseFactory _factory;
    private readonly SpaceRepository _repo;
    private const string OwnerId = "u_owner";
    private const string OtherId = "u_other";

    public SpaceRepositoryTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_spacerepo_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);
        _factory = new DatabaseFactory(_dataDir);
        _repo = new SpaceRepository(_factory);

        using var db = _factory.CreateSystemConnection();
        var now = DateTime.UtcNow.ToString("o");
        db.Execute(
            "INSERT OR IGNORE INTO users(id, name, email, created_at) VALUES (@id, @n, @e, @now)",
            new[]
            {
                new { id = OwnerId, n = "Owner", e = "o@o", now },
                new { id = OtherId, n = "Other", e = "x@x", now },
            });
    }

    [Fact]
    public async Task CreateAsync_GeneratesSlugAndAddsOwnerMembership()
    {
        var space = await _repo.CreateAsync(OwnerId, "Fishbowl Dev",
            TestContext.Current.CancellationToken);

        Assert.Equal("fishbowl-dev", space.Slug);
        Assert.Equal("Fishbowl Dev", space.Name);
        Assert.Equal(OwnerId, space.CreatedBy);
        Assert.NotEqual(string.Empty, space.Id);

        var role = await _repo.GetMembershipAsync(space.Id, OwnerId,
            TestContext.Current.CancellationToken);
        Assert.Equal(SpaceRole.Owner, role);
    }

    [Fact]
    public async Task CreateAsync_SlugCollision_AppendsSuffix()
    {
        await _repo.CreateAsync(OwnerId, "Backlog", TestContext.Current.CancellationToken);
        var s2 = await _repo.CreateAsync(OwnerId, "Backlog",
            TestContext.Current.CancellationToken);
        Assert.Equal("backlog-2", s2.Slug);
    }

    [Fact]
    public async Task CreateAsync_EmptyName_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _repo.CreateAsync(OwnerId, "   ", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ListByMemberAsync_ReturnsOnlyMySpaces()
    {
        var mine = await _repo.CreateAsync(OwnerId, "Mine",
            TestContext.Current.CancellationToken);
        _ = await _repo.CreateAsync(OtherId, "Theirs",
            TestContext.Current.CancellationToken);

        var spaces = await _repo.ListByMemberAsync(OwnerId,
            TestContext.Current.CancellationToken);
        Assert.Single(spaces);
        Assert.Equal(mine.Id, spaces[0].Space.Id);
        Assert.Equal(SpaceRole.Owner, spaces[0].Role);
    }

    [Fact]
    public async Task GetBySlugAsync_ReturnsNullForUnknown()
    {
        var s = await _repo.GetBySlugAsync("does-not-exist",
            TestContext.Current.CancellationToken);
        Assert.Null(s);
    }

    [Fact]
    public async Task GetMembershipAsync_NonMember_ReturnsNull()
    {
        var space = await _repo.CreateAsync(OwnerId, "Private",
            TestContext.Current.CancellationToken);
        var role = await _repo.GetMembershipAsync(space.Id, OtherId,
            TestContext.Current.CancellationToken);
        Assert.Null(role);
    }

    [Fact]
    public async Task DeleteAsync_Owner_Succeeds()
    {
        var space = await _repo.CreateAsync(OwnerId, "Doomed",
            TestContext.Current.CancellationToken);
        var ok = await _repo.DeleteAsync(space.Id, OwnerId,
            TestContext.Current.CancellationToken);
        Assert.True(ok);

        var refetched = await _repo.GetByIdAsync(space.Id,
            TestContext.Current.CancellationToken);
        Assert.Null(refetched);
    }

    [Fact]
    public async Task DeleteAsync_NonOwner_ReturnsFalse()
    {
        var space = await _repo.CreateAsync(OwnerId, "Locked",
            TestContext.Current.CancellationToken);
        var ok = await _repo.DeleteAsync(space.Id, OtherId,
            TestContext.Current.CancellationToken);
        Assert.False(ok);

        var refetched = await _repo.GetByIdAsync(space.Id,
            TestContext.Current.CancellationToken);
        Assert.NotNull(refetched);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dataDir))
        {
            try { Directory.Delete(_dataDir, true); } catch { }
        }
    }
}
