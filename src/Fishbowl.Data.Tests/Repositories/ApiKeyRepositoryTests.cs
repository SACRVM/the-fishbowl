using Dapper;
using Fishbowl.Core;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fishbowl.Data.Tests.Repositories;

public class ApiKeyRepositoryTests : IDisposable
{
    private readonly string _dataDir;
    private readonly DatabaseFactory _factory;
    private readonly ApiKeyRepository _repo;
    private const string Alice = "u_alice";
    private const string Bob = "u_bob";

    public ApiKeyRepositoryTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_apikey_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);
        _factory = new DatabaseFactory(_dataDir);
        _repo = new ApiKeyRepository(_factory);

        using var db = _factory.CreateSystemConnection();
        var now = DateTime.UtcNow.ToString("o");
        db.Execute(
            "INSERT OR IGNORE INTO users(id, name, email, created_at) VALUES (@id, @n, @e, @now)",
            new[]
            {
                new { id = Alice, n = "Alice", e = "a@a", now },
                new { id = Bob,   n = "Bob",   e = "b@b", now },
            });
    }

    [Fact]
    public async Task IssueAsync_ReturnsRawTokenInCorrectFormat()
    {
        var issued = await _repo.IssueAsync(
            Alice, ContextRef.User(Alice), "test key", new[] { "read:notes" },
            TestContext.Current.CancellationToken);

        Assert.StartsWith("fb_live_", issued.RawToken);
        Assert.Equal(issued.RawToken.Substring(0, 12), issued.Record.KeyPrefix);
        Assert.NotEqual(issued.RawToken, issued.Record.KeyHash);
        Assert.Equal(Alice, issued.Record.UserId);
        Assert.Equal("user", issued.Record.ContextType);
        Assert.Equal(Alice, issued.Record.ContextId);
        Assert.Equal("test key", issued.Record.Name);
        Assert.Single(issued.Record.Scopes, "read:notes");
        Assert.Null(issued.Record.RevokedAt);
        Assert.Null(issued.Record.LastUsedAt);
    }

    [Fact]
    public async Task IssueAsync_SpaceContext_StoresSpaceType()
    {
        var issued = await _repo.IssueAsync(
            Alice, ContextRef.Space("fishbowl-dev"), "space key", new[] { "read:notes", "write:notes" },
            TestContext.Current.CancellationToken);

        Assert.Equal("space", issued.Record.ContextType);
        Assert.Equal("fishbowl-dev", issued.Record.ContextId);
        Assert.Equal(2, issued.Record.Scopes.Count);
    }

    [Fact]
    public async Task IssueAsync_NeverRepeatsTokens()
    {
        var a = await _repo.IssueAsync(Alice, ContextRef.User(Alice), "a", new[] { "read:notes" },
            TestContext.Current.CancellationToken);
        var b = await _repo.IssueAsync(Alice, ContextRef.User(Alice), "b", new[] { "read:notes" },
            TestContext.Current.CancellationToken);

        Assert.NotEqual(a.RawToken, b.RawToken);
        Assert.NotEqual(a.Record.Id, b.Record.Id);
    }

    [Fact]
    public async Task LookupAsync_ValidToken_ReturnsKey()
    {
        var issued = await _repo.IssueAsync(
            Alice, ContextRef.User(Alice), "valid", new[] { "read:notes" },
            TestContext.Current.CancellationToken);

        var found = await _repo.LookupAsync(issued.RawToken, TestContext.Current.CancellationToken);
        Assert.NotNull(found);
        Assert.Equal(issued.Record.Id, found!.Id);
    }

    [Fact]
    public async Task LookupAsync_UnknownToken_ReturnsNull()
    {
        await _repo.IssueAsync(Alice, ContextRef.User(Alice), "a", new[] { "read:notes" },
            TestContext.Current.CancellationToken);

        var found = await _repo.LookupAsync("fb_live_NOT_A_REAL_TOKEN_xyz",
            TestContext.Current.CancellationToken);
        Assert.Null(found);
    }

    [Fact]
    public async Task LookupAsync_MalformedToken_ReturnsNull()
    {
        Assert.Null(await _repo.LookupAsync("", TestContext.Current.CancellationToken));
        Assert.Null(await _repo.LookupAsync("not-a-token", TestContext.Current.CancellationToken));
        Assert.Null(await _repo.LookupAsync("fb_test_abcdef", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LookupAsync_RevokedToken_ReturnsNull()
    {
        var issued = await _repo.IssueAsync(
            Alice, ContextRef.User(Alice), "doomed", new[] { "read:notes" },
            TestContext.Current.CancellationToken);

        await _repo.RevokeAsync(issued.Record.Id, Alice, TestContext.Current.CancellationToken);

        var found = await _repo.LookupAsync(issued.RawToken, TestContext.Current.CancellationToken);
        Assert.Null(found);
    }

    [Fact]
    public async Task RevokeAsync_OnlyKeyOwnerCanRevoke()
    {
        var issued = await _repo.IssueAsync(
            Alice, ContextRef.User(Alice), "alices", new[] { "read:notes" },
            TestContext.Current.CancellationToken);

        var bobTried = await _repo.RevokeAsync(issued.Record.Id, Bob,
            TestContext.Current.CancellationToken);
        Assert.False(bobTried);

        // Still live — lookup succeeds.
        var stillFound = await _repo.LookupAsync(issued.RawToken, TestContext.Current.CancellationToken);
        Assert.NotNull(stillFound);

        var aliceDid = await _repo.RevokeAsync(issued.Record.Id, Alice,
            TestContext.Current.CancellationToken);
        Assert.True(aliceDid);
    }

    [Fact]
    public async Task ListByUserAsync_ReturnsOnlyLiveKeysForUser()
    {
        var a1 = await _repo.IssueAsync(Alice, ContextRef.User(Alice), "a1", new[] { "read:notes" },
            TestContext.Current.CancellationToken);
        await _repo.IssueAsync(Alice, ContextRef.User(Alice), "a2", new[] { "read:notes" },
            TestContext.Current.CancellationToken);
        await _repo.IssueAsync(Bob, ContextRef.User(Bob), "b1", new[] { "read:notes" },
            TestContext.Current.CancellationToken);

        await _repo.RevokeAsync(a1.Record.Id, Alice, TestContext.Current.CancellationToken);

        var alices = await _repo.ListByUserAsync(Alice, TestContext.Current.CancellationToken);
        Assert.Single(alices);
        Assert.Equal("a2", alices[0].Name);
    }

    [Fact]
    public async Task IssueAsync_RejectsEmptyScopeList()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _repo.IssueAsync(Alice, ContextRef.User(Alice), "no-scopes",
                Array.Empty<string>(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IssueAsync_RejectsUnknownScope()
    {
        // Defense in depth: the API endpoint validates too, but a typo
        // through any other caller (mint-dev-key, future automation) must
        // still be caught at the repository layer rather than minting a
        // dead token.
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            _repo.IssueAsync(Alice, ContextRef.User(Alice), "typo",
                new[] { "read:nots" }, TestContext.Current.CancellationToken));
        Assert.Contains("read:nots", ex.Message);
    }

    [Fact]
    public async Task IssueAsync_RejectsMixedValidAndUnknown()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            _repo.IssueAsync(Alice, ContextRef.User(Alice), "mixed",
                new[] { "read:notes", "delete:everything" },
                TestContext.Current.CancellationToken));
        Assert.Contains("delete:everything", ex.Message);
    }

    [Fact]
    public async Task TouchLastUsedAsync_UpdatesTimestamp()
    {
        var issued = await _repo.IssueAsync(
            Alice, ContextRef.User(Alice), "touch", new[] { "read:notes" },
            TestContext.Current.CancellationToken);

        Assert.Null(issued.Record.LastUsedAt);

        await _repo.TouchLastUsedAsync(issued.Record.Id, TestContext.Current.CancellationToken);

        var after = (await _repo.ListByUserAsync(Alice, TestContext.Current.CancellationToken))
            .Single(k => k.Id == issued.Record.Id);
        Assert.NotNull(after.LastUsedAt);
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
