using Xunit;
using Fishbowl.Data;
using Fishbowl.Core.Models;
using Fishbowl.Data.Repositories;
using Microsoft.Data.Sqlite;
using System.IO;
using System.Threading.Tasks;
using Dapper;

namespace Fishbowl.Tests.Repositories;

public class NoteRepositoryTests : IDisposable
{
    private readonly string _tempDbDir;
    private readonly DatabaseFactory _dbFactory;
    private readonly NoteRepository _repo;
    private const string TestUserId = "user_repo_test";

    public NoteRepositoryTests()
    {
        _tempDbDir = Path.Combine(Path.GetTempPath(), "fishbowl_repo_tests_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDbDir);
        _dbFactory = new DatabaseFactory(_tempDbDir);
        var tagRepo = new TagRepository(_dbFactory);
        _repo = new NoteRepository(_dbFactory, tagRepo);
    }

    [Fact]
    public async Task Create_SetsMetadataAndStores_Test()
    {
        // Arrange
        var note = new Note
        {
            Title = "Test Note",
            Content = "Hello World",
            Tags = new List<string> { "tag1", "tag2" }
        };

        // Act
        var id = await _repo.CreateAsync(TestUserId, note, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(id);
        Assert.Equal(id, note.Id);
        Assert.Equal(TestUserId, note.CreatedBy);

        var retrieved = await _repo.GetByIdAsync(TestUserId, id, TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);
        Assert.Equal("Test Note", retrieved.Title);
        Assert.Equal(2, retrieved.Tags.Count);
        Assert.Contains("tag1", retrieved.Tags);
    }

    [Fact]
    public async Task Update_ModifiesData_Test()
    {
        // Arrange
        var note = new Note { Title = "Original", Content = "Old" };
        var id = await _repo.CreateAsync(TestUserId, note, TestContext.Current.CancellationToken);

        // Act
        note.Title = "Updated";
        note.Tags.Add("new-tag");
        var success = await _repo.UpdateAsync(TestUserId, note, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(success);
        var retrieved = await _repo.GetByIdAsync(TestUserId, id, TestContext.Current.CancellationToken);
        Assert.Equal("Updated", retrieved!.Title);
        Assert.Contains("new-tag", retrieved.Tags);
    }

    [Fact]
    public async Task Delete_RemovesFromDb_Test()
    {
        // Arrange
        var note = new Note { Title = "To Delete" };
        var id = await _repo.CreateAsync(TestUserId, note, TestContext.Current.CancellationToken);

        // Act
        var success = await _repo.DeleteAsync(TestUserId, id, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(success);
        var retrieved = await _repo.GetByIdAsync(TestUserId, id, TestContext.Current.CancellationToken);
        Assert.Null(retrieved);
    }

    [Fact]
    public async Task GetAll_ReturnsAllNotesForUser_Test()
    {
        // Arrange
        await _repo.CreateAsync(TestUserId, new Note { Title = "Note 1" }, TestContext.Current.CancellationToken);
        await _repo.CreateAsync(TestUserId, new Note { Title = "Note 2" }, TestContext.Current.CancellationToken);
        await _repo.CreateAsync("other_user", new Note { Title = "Other Note" }, TestContext.Current.CancellationToken);

        // Act
        var notes = await _repo.GetAllAsync(TestUserId, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, notes.Count());
    }

    [Fact]
    public async Task Create_PopulatesFts_Test()
    {
        var note = new Note { Title = "Pineapple recipe", Content = "Peel and slice" };
        await _repo.CreateAsync(TestUserId, note, TestContext.Current.CancellationToken);

        using var db = _dbFactory.CreateConnection(TestUserId);
        var hit = await db.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM notes_fts WHERE notes_fts MATCH 'pineapple'");

        Assert.Equal(1, hit);
    }

    [Fact]
    public async Task Update_UpdatesFts_Test()
    {
        var note = new Note { Title = "Old title", Content = "old content" };
        var id = await _repo.CreateAsync(TestUserId, note, TestContext.Current.CancellationToken);

        note.Title = "New title";
        note.Content = "quinoa salad";
        await _repo.UpdateAsync(TestUserId, note, TestContext.Current.CancellationToken);

        using var db = _dbFactory.CreateConnection(TestUserId);
        var newHit = await db.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM notes_fts WHERE notes_fts MATCH 'quinoa'");
        var oldHit = await db.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM notes_fts WHERE notes_fts MATCH 'old'");

        Assert.Equal(1, newHit);
        Assert.Equal(0, oldHit);
    }

    [Fact]
    public async Task Delete_RemovesFromFts_Test()
    {
        var note = new Note { Title = "Ephemeral", Content = "gone tomorrow" };
        var id = await _repo.CreateAsync(TestUserId, note, TestContext.Current.CancellationToken);

        await _repo.DeleteAsync(TestUserId, id, TestContext.Current.CancellationToken);

        using var db = _dbFactory.CreateConnection(TestUserId);
        var hit = await db.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM notes_fts WHERE notes_fts MATCH 'ephemeral'");

        Assert.Equal(0, hit);
    }

    [Fact]
    public async Task RoundTrip_AllFields_Test()
    {
        var original = new Note
        {
            Title = "Full note",
            Content = "Multiline\n\ncontent.",
            Type = "journal",
            Tags = new List<string> { "alpha", "beta", "gamma" },
            Pinned = true,
            Archived = false
        };

        var id = await _repo.CreateAsync(TestUserId, original, TestContext.Current.CancellationToken);
        var retrieved = await _repo.GetByIdAsync(TestUserId, id, TestContext.Current.CancellationToken);

        Assert.NotNull(retrieved);
        Assert.Equal(original.Title, retrieved.Title);
        Assert.Equal(original.Content, retrieved.Content);
        Assert.Equal(original.Type, retrieved.Type);
        Assert.Equal(original.Tags, retrieved.Tags);
        Assert.True(retrieved.Pinned);
        Assert.False(retrieved.Archived);
        Assert.Equal(TestUserId, retrieved.CreatedBy);
        Assert.True(retrieved.CreatedAt > DateTime.UtcNow.AddSeconds(-10));
    }

    [Fact]
    public async Task Create_RejectsNoteAboveContentLimit()
    {
        var note = new Note
        {
            Title = "ok",
            Content = new string('x', Fishbowl.Core.Util.NoteLimits.MaxContentLength + 1),
        };

        var ex = await Assert.ThrowsAsync<Fishbowl.Core.Util.NoteValidationException>(() =>
            _repo.CreateAsync(TestUserId, note, TestContext.Current.CancellationToken));
        Assert.Equal("content", ex.Error.Field);
    }

    [Fact]
    public async Task Update_RejectsOversizedContentSecret()
    {
        var note = new Note { Title = "ok", Content = "small" };
        var id = await _repo.CreateAsync(TestUserId, note, TestContext.Current.CancellationToken);

        var loaded = await _repo.GetByIdAsync(TestUserId, id, TestContext.Current.CancellationToken);
        Assert.NotNull(loaded);
        loaded!.ContentSecret = new byte[Fishbowl.Core.Util.NoteLimits.MaxContentSecretBytes + 1];

        await Assert.ThrowsAsync<Fishbowl.Core.Util.NoteValidationException>(() =>
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
