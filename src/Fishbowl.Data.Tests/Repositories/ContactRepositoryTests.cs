using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fishbowl.Tests.Repositories;

public class ContactRepositoryTests : IDisposable
{
    private readonly string _tempDbDir;
    private readonly DatabaseFactory _dbFactory;
    private readonly ContactRepository _repo;
    private const string TestUserId = "contact_repo_user";

    public ContactRepositoryTests()
    {
        _tempDbDir = Path.Combine(Path.GetTempPath(),
            "fishbowl_contact_repo_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDbDir);
        _dbFactory = new DatabaseFactory(_tempDbDir);
        _repo = new ContactRepository(_dbFactory);
    }

    [Fact]
    public async Task Create_SetsMetadataAndStoresAllFields()
    {
        var contact = new Contact
        {
            Name = "Alice Example",
            Email = "alice@example.com",
            Phone = "+1-555-0100",
            Notes = "Met at Q4 conference",
        };

        var id = await _repo.CreateAsync(TestUserId, contact,
            TestContext.Current.CancellationToken);

        Assert.NotNull(id);
        Assert.Equal(id, contact.Id);
        Assert.Equal(TestUserId, contact.CreatedBy);
        Assert.NotEqual(default, contact.CreatedAt);

        var retrieved = await _repo.GetByIdAsync(TestUserId, id,
            TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);
        Assert.Equal("Alice Example", retrieved!.Name);
        Assert.Equal("alice@example.com", retrieved.Email);
        Assert.Equal("+1-555-0100", retrieved.Phone);
        Assert.Equal("Met at Q4 conference", retrieved.Notes);
        Assert.False(retrieved.Archived);
    }

    [Fact]
    public async Task Create_RejectsEmptyName()
    {
        var contact = new Contact { Name = "   " };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _repo.CreateAsync(TestUserId, contact, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Update_PersistsChangedFields()
    {
        var contact = new Contact { Name = "Bob", Email = "bob@old.com" };
        var id = await _repo.CreateAsync(TestUserId, contact,
            TestContext.Current.CancellationToken);

        contact.Email = "bob@new.com";
        contact.Notes = "Phone upgraded";
        var ok = await _repo.UpdateAsync(TestUserId, contact,
            TestContext.Current.CancellationToken);
        Assert.True(ok);

        var fresh = await _repo.GetByIdAsync(TestUserId, id,
            TestContext.Current.CancellationToken);
        Assert.Equal("bob@new.com", fresh!.Email);
        Assert.Equal("Phone upgraded", fresh.Notes);
    }

    [Fact]
    public async Task Update_ReturnsFalse_ForMissingId()
    {
        var contact = new Contact { Id = "01HZ_GHOST", Name = "ghost" };
        var ok = await _repo.UpdateAsync(TestUserId, contact,
            TestContext.Current.CancellationToken);
        Assert.False(ok);
    }

    [Fact]
    public async Task Delete_RemovesRow_AndStripsFts()
    {
        // Bare single-token marker — FTS5's default tokenizer treats `-`
        // as the NOT operator inside MATCH queries, so any hyphenated
        // placeholder would break the search.
        const string marker = "zxqvsearchmarker";
        var contact = new Contact { Name = "Temp", Notes = marker };
        var id = await _repo.CreateAsync(TestUserId, contact,
            TestContext.Current.CancellationToken);

        var ok = await _repo.DeleteAsync(TestUserId, id,
            TestContext.Current.CancellationToken);
        Assert.True(ok);

        var gone = await _repo.GetByIdAsync(TestUserId, id,
            TestContext.Current.CancellationToken);
        Assert.Null(gone);

        // FTS row must also be gone — otherwise a future search endpoint
        // would surface phantom hits pointing at a deleted contact.
        using var db = _dbFactory.CreateContextConnection(ContextRef.User(TestUserId));
        var ftsHits = (await db.QueryAsync<string>(
            $"SELECT name FROM contacts_fts WHERE contacts_fts MATCH '{marker}'")).ToList();
        Assert.Empty(ftsHits);
    }

    [Fact]
    public async Task GetAll_HidesArchived_ByDefault()
    {
        await _repo.CreateAsync(TestUserId, new Contact { Name = "Active A" },
            TestContext.Current.CancellationToken);
        var shelved = new Contact { Name = "Shelved", Archived = true };
        await _repo.CreateAsync(TestUserId, shelved,
            TestContext.Current.CancellationToken);

        var @default = (await _repo.GetAllAsync(TestUserId, ct: TestContext.Current.CancellationToken)).ToList();
        Assert.Single(@default);
        Assert.Equal("Active A", @default[0].Name);

        var all = (await _repo.GetAllAsync(TestUserId, includeArchived: true,
            ct: TestContext.Current.CancellationToken)).ToList();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task GetAll_OrdersByLowercaseName()
    {
        await _repo.CreateAsync(TestUserId, new Contact { Name = "bernd" },
            TestContext.Current.CancellationToken);
        await _repo.CreateAsync(TestUserId, new Contact { Name = "Alice" },
            TestContext.Current.CancellationToken);
        await _repo.CreateAsync(TestUserId, new Contact { Name = "Charlie" },
            TestContext.Current.CancellationToken);

        var list = (await _repo.GetAllAsync(TestUserId,
            ct: TestContext.Current.CancellationToken)).ToList();
        Assert.Equal("Alice",   list[0].Name);
        Assert.Equal("bernd",   list[1].Name);
        Assert.Equal("Charlie", list[2].Name);
    }

    [Fact]
    public async Task Update_SyncsFtsRow_SoOldTermsNoLongerMatch()
    {
        // Single-token markers — see Delete test for why hyphens break MATCH.
        var contact = new Contact { Name = "oldname", Notes = "oldbodymarker" };
        var id = await _repo.CreateAsync(TestUserId, contact,
            TestContext.Current.CancellationToken);

        contact.Name = "newname";
        contact.Notes = "newbodymarker";
        await _repo.UpdateAsync(TestUserId, contact,
            TestContext.Current.CancellationToken);

        using var db = _dbFactory.CreateContextConnection(ContextRef.User(TestUserId));
        var oldHits = (await db.QueryAsync<string>(
            "SELECT name FROM contacts_fts WHERE contacts_fts MATCH 'oldbodymarker'")).ToList();
        Assert.Empty(oldHits);

        var newHits = (await db.QueryAsync<string>(
            "SELECT name FROM contacts_fts WHERE contacts_fts MATCH 'newbodymarker'")).ToList();
        Assert.Contains("newname", newHits);
    }

    [Fact]
    public async Task Search_EmptyQuery_ReturnsEmpty()
    {
        await _repo.CreateAsync(TestUserId, new Contact { Name = "Anyone" },
            TestContext.Current.CancellationToken);

        var hits = await _repo.SearchAsync(ContextRef.User(TestUserId), "   ",
            ct: TestContext.Current.CancellationToken);
        Assert.Empty(hits);
    }

    [Fact]
    public async Task Search_MatchesNameEmailPhoneNotes()
    {
        await _repo.CreateAsync(TestUserId, new Contact
        {
            Name = "Alice",
            Email = "alice@studio.example",
            Phone = "+49-30-1234",
            Notes = "Met at the venue sound check",
        }, TestContext.Current.CancellationToken);

        // Each field separately resolves the same contact — this proves the
        // FTS5 virtual table is indexing all four columns, not just name.
        foreach (var query in new[] { "alice", "studio", "1234", "venue" })
        {
            var hits = (await _repo.SearchAsync(ContextRef.User(TestUserId), query,
                ct: TestContext.Current.CancellationToken)).ToList();
            Assert.True(hits.Count == 1, $"expected 1 hit for '{query}', got {hits.Count}");
            Assert.Equal("Alice", hits[0].Name);
        }
    }

    [Fact]
    public async Task Search_HyphenatedQuery_DoesNotParseAsNot()
    {
        // Bare `-` in an FTS5 MATCH query is the NOT operator. The repo's
        // tokenizer must strip hyphens to prefix tokens before querying —
        // same rule the existing HybridSearchService uses.
        await _repo.CreateAsync(TestUserId, new Contact
        {
            Name = "Bob Partner",
            Notes = "studio-in-charge",
        }, TestContext.Current.CancellationToken);

        var hits = (await _repo.SearchAsync(ContextRef.User(TestUserId),
            "studio-in-charge",
            ct: TestContext.Current.CancellationToken)).ToList();

        Assert.Single(hits);
        Assert.Equal("Bob Partner", hits[0].Name);
    }

    [Fact]
    public async Task Search_ExcludesArchivedContacts()
    {
        await _repo.CreateAsync(TestUserId,
            new Contact { Name = "Active Alice", Email = "a@a" },
            TestContext.Current.CancellationToken);
        await _repo.CreateAsync(TestUserId,
            new Contact { Name = "Archived Alice", Email = "a@a", Archived = true },
            TestContext.Current.CancellationToken);

        var hits = (await _repo.SearchAsync(ContextRef.User(TestUserId), "alice",
            ct: TestContext.Current.CancellationToken)).ToList();
        Assert.Single(hits);
        Assert.Equal("Active Alice", hits[0].Name);
    }

    [Fact]
    public async Task Search_RespectsLimit()
    {
        for (var i = 0; i < 5; i++)
        {
            await _repo.CreateAsync(TestUserId,
                new Contact { Name = $"Person {i}", Notes = "matching marker" },
                TestContext.Current.CancellationToken);
        }

        var hits = (await _repo.SearchAsync(ContextRef.User(TestUserId), "marker",
            limit: 2, ct: TestContext.Current.CancellationToken)).ToList();
        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public async Task TeamContext_IsolatedFromPersonal()
    {
        // Same in-memory factory, different ContextRef → different files.
        const string teamSlug = "01J_TEAM_ID";
        await _repo.CreateAsync(ContextRef.User(TestUserId), TestUserId,
            new Contact { Name = "personal-only" }, TestContext.Current.CancellationToken);
        await _repo.CreateAsync(ContextRef.Team(teamSlug), TestUserId,
            new Contact { Name = "team-only" }, TestContext.Current.CancellationToken);

        var personal = (await _repo.GetAllAsync(ContextRef.User(TestUserId),
            ct: TestContext.Current.CancellationToken)).ToList();
        var team = (await _repo.GetAllAsync(ContextRef.Team(teamSlug),
            ct: TestContext.Current.CancellationToken)).ToList();

        Assert.Single(personal);
        Assert.Equal("personal-only", personal[0].Name);
        Assert.Single(team);
        Assert.Equal("team-only", team[0].Name);
    }

    [Fact]
    public async Task Create_RejectsContactWithOversizedNotes()
    {
        var contact = new Contact
        {
            Name = "ok",
            Notes = new string('x', Fishbowl.Core.Util.ContactLimits.MaxNotesLength + 1),
        };

        var ex = await Assert.ThrowsAsync<Fishbowl.Core.Util.ResourceValidationException>(() =>
            _repo.CreateAsync(TestUserId, contact, TestContext.Current.CancellationToken));
        Assert.Equal("contact", ex.Error.Resource);
        Assert.Equal("notes", ex.Error.Field);
    }

    [Fact]
    public async Task Update_RejectsOversizedName()
    {
        var contact = new Contact { Name = "ok" };
        var id = await _repo.CreateAsync(TestUserId, contact, TestContext.Current.CancellationToken);

        var loaded = await _repo.GetByIdAsync(TestUserId, id, TestContext.Current.CancellationToken);
        Assert.NotNull(loaded);
        loaded!.Name = new string('n', Fishbowl.Core.Util.ContactLimits.MaxNameLength + 1);

        await Assert.ThrowsAsync<Fishbowl.Core.Util.ResourceValidationException>(() =>
            _repo.UpdateAsync(TestUserId, loaded, TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDbDir))
        {
            try { Directory.Delete(_tempDbDir, true); } catch { }
        }
    }
}
