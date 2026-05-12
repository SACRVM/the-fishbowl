using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fishbowl.Data.Tests.Repositories;

// Covers the user_mappings side of SystemRepository: forward + reverse
// lookup, delete, and the invariants other code relies on (mappings are
// not shared across users; deleting one mapping doesn't strand others).
public class SystemRepositoryMappingTests : IDisposable
{
    private readonly string _dataDir;
    private readonly DatabaseFactory _factory;
    private readonly SystemRepository _repo;

    public SystemRepositoryMappingTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_sysmap_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);
        _factory = new DatabaseFactory(_dataDir);
        _repo = new SystemRepository(_factory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dataDir))
        {
            try { Directory.Delete(_dataDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task GetProviderIdForUserAsync_ReturnsMappedId()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repo.CreateUserAsync("u_alice", "Alice", "a@a", null, ct);
        await _repo.CreateUserMappingAsync("u_alice", "discord", "9999", ct);

        var providerId = await _repo.GetProviderIdForUserAsync("u_alice", "discord", ct);

        Assert.Equal("9999", providerId);
    }

    [Fact]
    public async Task GetProviderIdForUserAsync_ReturnsNullForUnmappedUser()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repo.CreateUserAsync("u_alice", "Alice", "a@a", null, ct);

        Assert.Null(await _repo.GetProviderIdForUserAsync("u_alice", "discord", ct));
    }

    [Fact]
    public async Task GetProviderIdForUserAsync_ScopedByProvider()
    {
        // A user can be mapped under multiple providers (e.g. google for login,
        // discord for notifications). Reverse lookup must not bleed between
        // them — a discord lookup never returns a google provider_id.
        var ct = TestContext.Current.CancellationToken;
        await _repo.CreateUserAsync("u_alice", "Alice", "a@a", null, ct);
        await _repo.CreateUserMappingAsync("u_alice", "google", "google-sub-12345", ct);
        await _repo.CreateUserMappingAsync("u_alice", "discord", "9999", ct);

        Assert.Equal("9999", await _repo.GetProviderIdForUserAsync("u_alice", "discord", ct));
        Assert.Equal("google-sub-12345", await _repo.GetProviderIdForUserAsync("u_alice", "google", ct));
    }

    [Fact]
    public async Task DeleteUserMappingAsync_OnlyRemovesNamedMapping()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repo.CreateUserAsync("u_alice", "Alice", "a@a", null, ct);
        await _repo.CreateUserMappingAsync("u_alice", "google", "google-sub-12345", ct);
        await _repo.CreateUserMappingAsync("u_alice", "discord", "9999", ct);

        var removed = await _repo.DeleteUserMappingAsync("discord", "9999", ct);

        Assert.True(removed);
        Assert.Null(await _repo.GetProviderIdForUserAsync("u_alice", "discord", ct));
        // Google mapping must survive — unlinking Discord doesn't kick the
        // user out of their Fishbowl account.
        Assert.Equal("google-sub-12345", await _repo.GetProviderIdForUserAsync("u_alice", "google", ct));
    }

    [Fact]
    public async Task DeleteUserMappingAsync_ReturnsFalseForUnknownMapping()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.False(await _repo.DeleteUserMappingAsync("discord", "nonexistent", ct));
    }
}
