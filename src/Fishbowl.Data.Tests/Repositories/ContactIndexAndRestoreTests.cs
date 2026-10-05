using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Core.Tables;
using Fishbowl.Core.Util;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fishbowl.Tests.Repositories;

// The contact search index follows an organisation's name (rename, merge),
// trash restore rebuilds the same index a write does, a restored person
// whose organisation is gone comes back without it, and a merge over the
// limits is refused before anything is written.
public class ContactIndexAndRestoreTests : IDisposable
{
    private readonly string _dir;
    private readonly DatabaseFactory _factory;
    private readonly ContactRepository _contacts;
    private readonly TrashRepository _trash;
    private static readonly ContextRef Ctx = ContextRef.User("contact_index_user");

    public ContactIndexAndRestoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fishbowl_contact_index_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
        _factory = new DatabaseFactory(_dir);
        _contacts = new ContactRepository(_factory);
        _trash = new TrashRepository(_factory, new NoteRepository(_factory, new TagRepository(_factory), embeddings: null));
    }

    private Task<string> OrgAsync(string name, CancellationToken ct)
        => _contacts.CreateAsync(Ctx, "u", new Contact { Kind = ContactKinds.Organisation, Name = name }, ct);

    private Task<string> PersonAsync(string first, string? orgId, CancellationToken ct, Action<Contact>? more = null)
    {
        var c = new Contact { FirstName = first, LastName = "Example", OrganisationId = orgId };
        more?.Invoke(c);
        return _contacts.CreateAsync(Ctx, "u", c, ct);
    }

    private async Task<List<string>> FindAsync(string q, CancellationToken ct)
        => (await _contacts.SearchAsync(Ctx, q, 50, ct)).Select(c => c.Id).ToList();

    [Fact]
    public async Task RenamingAnOrganisation_ReindexesItsPeople()
    {
        var ct = TestContext.Current.CancellationToken;
        var org = await OrgAsync("Acme", ct);
        var ada = await PersonAsync("Ada", org, ct);

        var loaded = (await _contacts.GetByIdAsync(Ctx, org, ct))!;
        loaded.Name = "Globex";
        await _contacts.UpdateAsync(Ctx, loaded, ct);

        Assert.Contains(ada, await FindAsync("globex", ct));
        Assert.DoesNotContain(ada, await FindAsync("acme", ct));
    }

    [Fact]
    public async Task MergingOrganisations_ReindexesThePeopleThatMoved()
    {
        var ct = TestContext.Current.CancellationToken;
        var keep = await OrgAsync("Initech", ct);
        var dup = await OrgAsync("Initrode", ct);
        var bob = await PersonAsync("Bob", dup, ct);

        await _contacts.MergeAsync(Ctx, keep, dup, "u", ct);

        Assert.Equal(keep, (await _contacts.GetByIdAsync(Ctx, bob, ct))!.OrganisationId);
        Assert.Contains(bob, await FindAsync("initech", ct));
        Assert.DoesNotContain(bob, await FindAsync("initrode", ct));
    }

    [Fact]
    public async Task Merge_OverTheLimits_IsRefused_AndWritesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var keep = await PersonAsync("Cy", null, ct, c => c.Notes = new string('a', ContactLimits.MaxNotesLength - 10));
        var dup = await PersonAsync("Cy", null, ct, c => c.Notes = new string('b', ContactLimits.MaxNotesLength - 10));

        var ex = await Assert.ThrowsAsync<TableException>(() => _contacts.MergeAsync(Ctx, keep, dup, "u", ct));

        Assert.Equal("merge_too_large", ex.Code);
        Assert.Equal(400, ex.Status);
        Assert.NotNull(await _contacts.GetByIdAsync(Ctx, dup, ct));
        Assert.Equal(ContactLimits.MaxNotesLength - 10, (await _contacts.GetByIdAsync(Ctx, keep, ct))!.Notes!.Length);
        Assert.Empty(await _trash.ListAsync(Ctx, ct));
    }

    [Fact]
    public async Task Restore_RebuildsTheFullSearchRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var org = await OrgAsync("Umbrella", ct);
        var dee = await PersonAsync("Dee", org, ct, c =>
        {
            c.Role = "Chemist";
            c.Emails = new() { new() { Value = "dee@first.test" }, new() { Value = "dee@second.test" } };
            c.Tags = new() { "lab" };
        });
        await _contacts.DeleteAsync(Ctx, dee, ct);
        var item = (await _trash.ListAsync(Ctx, ct)).Single();

        var (result, _) = await _trash.RestoreAsync(Ctx, item.Id, ct);

        Assert.Equal(TrashRestore.Restored, result);
        foreach (var q in new[] { "second.test", "chemist", "lab", "umbrella" })
            Assert.Contains(dee, await FindAsync(q, ct));
    }

    [Fact]
    public async Task Restore_PersonWhoseOrganisationWentAfterThem_ComesBackWithoutIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var org = await OrgAsync("Hooli", ct);
        var eve = await PersonAsync("Eve", org, ct);
        await _contacts.DeleteAsync(Ctx, eve, ct);
        await _contacts.DeleteAsync(Ctx, org, ct);
        var item = (await _trash.ListAsync(Ctx, ct)).Single(t => t.ItemId == eve);

        var (result, _) = await _trash.RestoreAsync(Ctx, item.Id, ct);

        Assert.Equal(TrashRestore.Restored, result);
        var back = (await _contacts.GetByIdAsync(Ctx, eve, ct))!;
        Assert.Null(back.OrganisationId);
        back.Role = "CEO";
        Assert.True(await _contacts.UpdateAsync(Ctx, back, ct));   // editable again
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }
}
