using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Core.Util;
using Fishbowl.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fishbowl.Data.Tests.Repositories;

// System tags can't be forged by a human write: a reserved name is matched
// as it will be stored (normalised), and no tag can be renamed into one. A
// tag TagName can't take is refused as invalid input, not thrown as a crash.
public class NoteSystemTagForgeryTests : IDisposable
{
    private readonly string _dataDir;
    private readonly DatabaseFactory _factory;
    private readonly TagRepository _tags;
    private readonly NoteRepository _notes;
    private readonly ContextRef _ctx = ContextRef.User("tag_forgery_user");

    public NoteSystemTagForgeryTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_tag_forgery_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);
        _factory = new DatabaseFactory(_dataDir);
        _tags = new TagRepository(_factory);
        _notes = new NoteRepository(_factory, _tags);
    }

    [Fact]
    public async Task HumanWrite_ReservedNameInAnyCase_IsStripped()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = await _notes.CreateAsync(_ctx, "u",
            new Note { Title = "t", Tags = new() { "Source:MCP", " Review:Pending ", "Work" } }, NoteSource.Human, ct);
        var stored = (await _notes.GetByIdAsync(_ctx, id, ct))!;
        Assert.Equal(new[] { "work" }, stored.Tags);

        stored.Tags = new() { "SOURCE:mcp", "work" };
        await _notes.UpdateAsync(_ctx, stored, NoteSource.Human, ct);
        Assert.Equal(new[] { "work" }, (await _notes.GetByIdAsync(_ctx, id, ct))!.Tags);
    }

    [Fact]
    public async Task McpWrite_StillGetsItsSourceTags()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = await _notes.CreateAsync(_ctx, "u", new Note { Title = "t", Tags = new() { "Ideas" } }, NoteSource.Mcp, ct);
        var tags = (await _notes.GetByIdAsync(_ctx, id, ct))!.Tags;
        Assert.Contains("source:mcp", tags);
        Assert.Contains("review:pending", tags);
        Assert.Contains("ideas", tags);
    }

    [Theory]
    [InlineData("source:mcp")]
    [InlineData("Review:Pending")]
    public async Task Rename_IntoSystemTag_IsRefused(string target)
    {
        var ct = TestContext.Current.CancellationToken;
        var id = await _notes.CreateAsync(_ctx, "u", new Note { Title = "t", Tags = new() { "foo" } }, ct);
        await Assert.ThrowsAsync<ArgumentException>(() => _tags.RenameAsync(_ctx, "foo", target, ct));
        Assert.Equal(new[] { "foo" }, (await _notes.GetByIdAsync(_ctx, id, ct))!.Tags);
    }

    [Theory]
    [InlineData("two words")]
    [InlineData("a-tag-that-is-far-too-long-for-the-tag-rule-of-fifty-characters")]
    public async Task Write_TagTagNameCantTake_IsInvalidInput(string tag)
    {
        var ct = TestContext.Current.CancellationToken;
        var ex = await Assert.ThrowsAsync<ResourceValidationException>(() =>
            _notes.CreateAsync(_ctx, "u", new Note { Title = "t", Tags = new() { tag } }, ct));
        Assert.Equal(ResourceValidationKind.Invalid, ex.Error.Kind);
        Assert.Equal("tags", ex.Error.Field);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch { }
    }
}
