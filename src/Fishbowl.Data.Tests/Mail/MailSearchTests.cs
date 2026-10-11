using Dapper;
using Fishbowl.Core.Models;
using Fishbowl.Core.Search;
using Fishbowl.Data.Mail;
using Fishbowl.Data.Repositories;
using Fishbowl.Data.Search;
using Fishbowl.Data.Tables;

namespace Fishbowl.Tests.Mail;

// Mail found by meaning (decision 15, schema v27): vectors made after the
// fact, kept in step with deletes and restores, blended with the full text,
// and made again when the model changes. The model is a fake that knows two
// topics — a text is "about" one when it carries one of its words.
public class MailSearchTests : MailFixture
{
    private MailRepository WithModel(TopicEmbedder model) => new(Factory, Credentials, embeddings: model);

    [Fact]
    public async Task Search_FindsMailByMeaning_AndRanksWordsAndMeaningFirst()
    {
        var acc = await AddAccountAsync();
        var s = Connector.For(acc.Id);
        s.AddMessage("inbox", "<a@x>", "Ihre Sendung", text: "wird morgen zugestellt");      // meaning only
        s.AddMessage("inbox", "<b@x>", "Stromrechnung März", text: "84,20 Euro");           // neither
        s.AddMessage("inbox", "<c@x>", "Paket für dich", text: "liegt beim Nachbarn");      // words and meaning
        await SyncAsync(acc);
        var repo = WithModel(new TopicEmbedder());
        Assert.Equal(3, await repo.EmbedPendingAsync(Ctx, 100, Ct));

        var found = await repo.SearchThreadsAsync(Ctx, new MailListQuery { Text = "Paket", Limit = 10 }, Ct);

        Assert.False(found.Degraded);
        Assert.Equal(["Paket für dich", "Ihre Sendung"], found.Threads.Select(t => t.Subject));
        // The list's filters hold: nothing of it is archived; both states find it all.
        Assert.Empty((await repo.SearchThreadsAsync(Ctx, new MailListQuery { Text = "Paket", Archived = true }, Ct)).Threads);
        Assert.Equal(2, (await repo.SearchThreadsAsync(Ctx, new MailListQuery { Text = "Paket", AnyState = true }, Ct)).Threads.Count);
    }

    [Fact]
    public async Task Search_WithoutAModel_IsTheFullTextAndSaysSo()
    {
        var acc = await AddAccountAsync();
        var s = Connector.For(acc.Id);
        s.AddMessage("inbox", "<a@x>", "Ihre Sendung", text: "wird morgen zugestellt");
        s.AddMessage("inbox", "<c@x>", "Paket für dich", text: "liegt beim Nachbarn");
        await SyncAsync(acc);
        var repo = WithModel(new TopicEmbedder { Ready = false });

        var found = await repo.SearchThreadsAsync(Ctx, new MailListQuery { Text = "Paket" }, Ct);

        Assert.True(found.Degraded);
        Assert.Equal(["Paket für dich"], found.Threads.Select(t => t.Subject));
    }

    [Fact]
    public async Task Vectors_AreMadeNewestFirst_GoWithADelete_AndComeBackAfterARestore()
    {
        var acc = await AddAccountAsync();
        var s = Connector.For(acc.Id);
        s.AddMessage("inbox", "<old@x>", "Oldest", date: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        s.AddMessage("inbox", "<mid@x>", "Middle", date: new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));
        s.AddMessage("inbox", "<new@x>", "Newest", date: new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        await SyncAsync(acc);
        var repo = WithModel(new TopicEmbedder());

        Assert.Equal(2, await repo.EmbedPendingAsync(Ctx, 2, Ct));
        Assert.Equal(["Middle", "Newest"], Subjects("SELECT subject FROM mail_messages WHERE embedded = 1 ORDER BY subject"));
        Assert.Equal(2, Scalar("SELECT COUNT(*) FROM vec_mail"));

        var newest = await ByKeyAsync("new@x");
        await Repo.DeleteMessagesAsync(Ctx, [newest.Id], tombstone: true, deletedBy: null, Ct);
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM vec_mail"));

        var item = Assert.Single(await Trash.ListAsync(Ctx, Ct), t => t.Kind == TrashKinds.Mail);
        await Trash.RestoreAsync(Ctx, item.Id, Ct);
        // Back without its vector: the backfill makes it again (and the oldest).
        Assert.Equal(0, Scalar($"SELECT embedded FROM mail_messages WHERE id = '{newest.Id}'"));
        Assert.Equal(2, await repo.EmbedPendingAsync(Ctx, 10, Ct));
        Assert.Equal(3, Scalar("SELECT COUNT(*) FROM vec_mail"));
        Assert.Equal(0, await repo.EmbedPendingAsync(Ctx, 10, Ct));
    }

    [Fact]
    public async Task Backfill_RecordsTheModel_AndEmbedsAgainWhenItChanges()
    {
        var acc = await AddAccountAsync();
        var s = Connector.For(acc.Id);
        s.AddMessage("inbox", "<a@x>", "Ihre Sendung");
        s.AddMessage("inbox", "<b@x>", "Paket");
        await SyncAsync(acc);

        // Not ready: nothing happens, nothing is recorded.
        Assert.Equal(0, await Backfill(new TopicEmbedder { Ready = false }).RunAsync(Ctx, 100, Ct));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM search_meta"));

        Assert.Equal(2, await Backfill(new TopicEmbedder { Name = "m1" }).RunAsync(Ctx, 100, Ct));
        Assert.Equal(0, await Backfill(new TopicEmbedder { Name = "m1" }).RunAsync(Ctx, 100, Ct));
        Assert.Equal(["m1"], Subjects("SELECT value FROM search_meta WHERE key = 'embedding_model'"));

        // Another model: every vector is made again with it.
        Assert.Equal(2, await Backfill(new TopicEmbedder { Name = "m2" }).RunAsync(Ctx, 100, Ct));
        Assert.Equal(["m2"], Subjects("SELECT value FROM search_meta WHERE key = 'embedding_model'"));
        Assert.Equal(2, Scalar("SELECT COUNT(*) FROM vec_mail"));
    }

    [Fact]
    public void EmbeddingText_IsTheSubjectSenderAndStart_WithoutQuotesOrLinks()
    {
        var text = MailRepository.EmbeddingText("Re: Termin", "Ada",
            "Passt mir gut.\nDetails: https://example.com/x?y=1 bitte ansehen\n\n> alte Zeile\n" +
            "Am 10.10.2026 um 12:00 schrieb Bob <bob@example.com>:\nZitierter Text");

        Assert.Equal("Re: Termin\nAda\nPasst mir gut. Details: bitte ansehen", text);
        Assert.True(MailRepository.EmbeddingText("S", null, new string('x', 5000)).Length <= MailRepository.EmbeddedChars + 2);
    }

    private VectorBackfill Backfill(TopicEmbedder model) =>
        new(Factory, model, new NoteRepository(Factory, new TagRepository(Factory), model),
            new TableRepository(Factory, new SpaceRepository(Factory), model), WithModel(model));

    private List<string> Subjects(string sql)
    {
        using var db = Factory.CreateContextConnection(Ctx);
        return db.Query<string>(sql).ToList();
    }

    /// <summary>Two topics, one direction each; anything else a third. A
    /// query and a text about the same topic are the same vector.</summary>
    private sealed class TopicEmbedder : IEmbeddingService
    {
        public bool Ready { get; init; } = true;
        public string Name { get; init; } = "topics";
        public int Dimensions => 384;
        public string Model => Name;

        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        {
            if (!Ready) throw new EmbeddingUnavailableException("not ready");
            var t = text.ToLowerInvariant();
            var vec = new float[Dimensions];
            vec[t.Contains("paket") || t.Contains("sendung") || t.Contains("zugestellt") ? 0
                : t.Contains("rechnung") ? 1 : 2] = 1f;
            return Task.FromResult(vec);
        }
    }
}
