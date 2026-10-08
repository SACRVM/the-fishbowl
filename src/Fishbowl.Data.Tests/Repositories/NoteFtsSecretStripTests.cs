using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fishbowl.Data.Tests.Repositories;

// notes_fts holds no secret text: a `:::secret` block stored in the clear (a
// key's write, a note from before the vault, a block behind a no-break
// space) must not make its note findable by the secret. Every path that
// feeds notes_fts — create, update, reindex, trash restore and the v20
// migration for rows indexed before — indexes the stripped content.
public class NoteFtsSecretStripTests : IDisposable
{
    private const string Secret = "hunter2fts";
    private readonly string _dataDir;
    private readonly DatabaseFactory _factory;
    private readonly NoteRepository _notes;
    private readonly ContextRef _ctx = ContextRef.User("fts_strip_user");

    public NoteFtsSecretStripTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_fts_strip_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);
        _factory = new DatabaseFactory(_dataDir);
        _notes = new NoteRepository(_factory, new TagRepository(_factory));
    }

    private static Note WithSecret(string title) => new()
    {
        Title = title,
        Content = $"public words\n:::secret\u00A0label\npw={Secret}\n:::end\ntail",
    };

    private long Matches(string term)
    {
        using var db = _factory.CreateContextConnection(_ctx);
        return db.ExecuteScalar<long>("SELECT COUNT(*) FROM notes_fts WHERE notes_fts MATCH @term", new { term });
    }

    private string? IndexedContent(string id)
    {
        using var db = _factory.CreateContextConnection(_ctx);
        return db.ExecuteScalar<string?>(
            "SELECT content FROM notes_fts WHERE rowid = (SELECT rowid FROM notes WHERE id = @id)", new { id });
    }

    [Fact]
    public async Task CreateAndUpdate_IndexStrippedContent()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = await _notes.CreateAsync(_ctx, "u", WithSecret("created"), ct);
        Assert.Equal(0, Matches(Secret));
        Assert.Equal(1, Matches("public"));
        Assert.DoesNotContain(Secret, IndexedContent(id));

        var note = (await _notes.GetByIdAsync(_ctx, id, ct))!;
        note.Content = $"other words\n::secret\n{Secret}2\n::end";
        await _notes.UpdateAsync(_ctx, note, ct);
        Assert.Equal(0, Matches(Secret + "2"));
        Assert.Equal(1, Matches("other"));
    }

    [Fact]
    public async Task ReindexAndTrashRestore_IndexStrippedContent()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = await _notes.CreateAsync(_ctx, "u", WithSecret("restored"), ct);

        await _notes.ReindexAsync(_ctx, id, ct);
        Assert.Equal(0, Matches(Secret));

        await _notes.DeleteAsync(_ctx, id, ct);
        var trash = new TrashRepository(_factory, _notes);
        var item = Assert.Single(await trash.ListAsync(_ctx, ct));
        var (result, _) = await trash.RestoreAsync(_ctx, item.Id, ct);
        Assert.Equal(TrashRestore.Restored, result);
        Assert.Equal(0, Matches(Secret));
        Assert.Equal(1, Matches("public"));
    }

    // Rows indexed before the strip get indexed again by the v20 migration.
    [Fact]
    public async Task UserV20_StripsRowsIndexedRaw()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = await _notes.CreateAsync(_ctx, "u", WithSecret("old"), ct);
        var plain = await _notes.CreateAsync(_ctx, "u", new Note { Title = "plain", Content = "nothing hidden" }, ct);
        using (var db = _factory.CreateContextConnection(_ctx))
        {
            // What a v19 DB holds: the raw content in the index.
            db.Execute("UPDATE notes_fts SET content = (SELECT content FROM notes WHERE id = @id) " +
                       "WHERE rowid = (SELECT rowid FROM notes WHERE id = @id)", new { id });
            Assert.Equal(1, db.ExecuteScalar<long>("SELECT COUNT(*) FROM notes_fts WHERE notes_fts MATCH @Secret", new { Secret }));
            db.Execute("PRAGMA user_version = 19");
        }
        SqliteConnection.ClearAllPools();

        using (var db = new DatabaseFactory(_dataDir).CreateContextConnection(_ctx))
            Assert.Equal(23, db.ExecuteScalar<long>("PRAGMA user_version"));
        Assert.Equal(0, Matches(Secret));
        Assert.Equal("nothing hidden", IndexedContent(plain));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch { }
    }
}
