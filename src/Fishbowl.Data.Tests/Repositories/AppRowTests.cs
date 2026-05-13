using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Apps;
using Fishbowl.Core.Util;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fishbowl.Data.Tests.Repositories;

// Apps platform — Phase B.3 row CRUD. Covers:
//  * Server fills id, created_at, last_modified, row_version, author
//  * Forbidden base columns rejected on insert/patch
//  * Title required; unknown columns rejected
//  * additional_data 256 KB cap (ResourceValidationException)
//  * Update bumps last_modified + row_version, ignores soft-deleted rows
//  * Soft delete + restore round-trips; hard delete removes the row
//  * Type coercion: bool → 0/1, DateTime → ISO-8601 string
public class AppRowTests : IDisposable
{
    private readonly string _dataDir;
    private readonly DatabaseFactory _factory;
    private readonly AppSchemaRepository _schema;
    private readonly AppRowRepository _rows;
    private readonly AppRef _appRef = AppRef.OfUser("u_alice", "app_rows");

    public AppRowTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_apps_b3_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);
        _factory = new DatabaseFactory(_dataDir);
        _schema = new AppSchemaRepository(_factory);
        _rows = new AppRowRepository(_factory);
    }

    private async Task SeedTransactionsTableAsync(CancellationToken ct = default)
    {
        await _schema.CreateTableAsync(_appRef, "transactions", new[]
        {
            new AppColumn("amount", AppColumnType.Real, Nullable: false),
            new AppColumn("counterparty", AppColumnType.Text),
            new AppColumn("category", AppColumnType.Text),
            new AppColumn("paid", AppColumnType.Boolean),
        }, ct);
    }

    private static IReadOnlyDictionary<string, object?> Fields(params (string key, object? value)[] pairs)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (k, v) in pairs) d[k] = v;
        return d;
    }

    [Fact]
    public async Task Insert_FillsBaseColumns_AndStampsAuthor()
    {
        await SeedTransactionsTableAsync(TestContext.Current.CancellationToken);

        var row = await _rows.InsertAsync(_appRef, "transactions", Fields(
            ("title", "Coffee"),
            ("amount", 4.5),
            ("counterparty", "cafe central"),
            ("category", "food"),
            ("paid", true)
        ), actorId: "u_alice", TestContext.Current.CancellationToken);

        Assert.NotNull(row);
        Assert.Equal("Coffee", row["title"]);
        Assert.Equal(4.5, (double)row["amount"]!);
        Assert.NotNull(row["id"]);
        Assert.Equal(26, ((string)row["id"]!).Length); // ULID
        Assert.NotNull(row["created_at"]);
        Assert.NotNull(row["last_modified"]);
        Assert.Equal(1L, (long)row["row_version"]!);
        Assert.Equal(0L, (long)row["is_deleted"]!);
        Assert.Null(row["deleted_at"]);
        Assert.Equal("u_alice", row["author"]);
        Assert.Equal(1L, (long)row["paid"]!); // bool coerced to 1
    }

    [Fact]
    public async Task Insert_RequiresTitle()
    {
        await SeedTransactionsTableAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _rows.InsertAsync(_appRef, "transactions",
                Fields(("amount", 1.0)), actorId: null, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("created_at")]
    [InlineData("last_modified")]
    [InlineData("row_version")]
    [InlineData("is_deleted")]
    [InlineData("deleted_at")]
    [InlineData("author")]
    public async Task Insert_RejectsForbiddenBaseColumn(string column)
    {
        await SeedTransactionsTableAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _rows.InsertAsync(_appRef, "transactions",
                Fields(("title", "x"), ("amount", 1.0), (column, "spoof")),
                actorId: "u_alice", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Insert_RejectsUnknownColumn()
    {
        await SeedTransactionsTableAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _rows.InsertAsync(_appRef, "transactions",
                Fields(("title", "x"), ("amount", 1.0), ("fakecol", "no")),
                actorId: null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Insert_TypeMismatch_Throws()
    {
        await SeedTransactionsTableAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _rows.InsertAsync(_appRef, "transactions",
                Fields(("title", "x"), ("amount", "not a number")),
                actorId: null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Insert_AdditionalDataMustBeString()
    {
        await SeedTransactionsTableAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ResourceValidationException>(() =>
            _rows.InsertAsync(_appRef, "transactions",
                Fields(("title", "x"), ("amount", 1.0), ("additional_data", 12345)),
                actorId: null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Insert_AdditionalDataExceeds256kb_Throws()
    {
        await SeedTransactionsTableAsync(TestContext.Current.CancellationToken);
        var huge = new string('x', 256 * 1024 + 1);
        var ex = await Assert.ThrowsAsync<ResourceValidationException>(() =>
            _rows.InsertAsync(_appRef, "transactions",
                Fields(("title", "x"), ("amount", 1.0), ("additional_data", huge)),
                actorId: null, TestContext.Current.CancellationToken));
        Assert.Equal("app_row", ex.Error.Resource);
        Assert.Equal("additional_data", ex.Error.Field);
    }

    [Fact]
    public async Task Get_ReturnsRow_AndExcludesSoftDeletedByDefault()
    {
        await SeedTransactionsTableAsync(TestContext.Current.CancellationToken);
        var row = await _rows.InsertAsync(_appRef, "transactions", Fields(
            ("title", "Lunch"), ("amount", 12.0)),
            actorId: null, TestContext.Current.CancellationToken);
        var id = (string)row["id"]!;

        var got = await _rows.GetAsync(_appRef, "transactions", id,
            ct: TestContext.Current.CancellationToken);
        Assert.NotNull(got);
        Assert.Equal("Lunch", got!["title"]);

        await _rows.DeleteAsync(_appRef, "transactions", id,
            hard: false, actorId: null, TestContext.Current.CancellationToken);

        var afterSoft = await _rows.GetAsync(_appRef, "transactions", id,
            ct: TestContext.Current.CancellationToken);
        Assert.Null(afterSoft);

        var withDeleted = await _rows.GetAsync(_appRef, "transactions", id,
            includeDeleted: true, TestContext.Current.CancellationToken);
        Assert.NotNull(withDeleted);
        Assert.Equal(1L, (long)withDeleted!["is_deleted"]!);
        Assert.NotNull(withDeleted["deleted_at"]);
    }

    [Fact]
    public async Task Update_BumpsRowVersionAndLastModified()
    {
        await SeedTransactionsTableAsync(TestContext.Current.CancellationToken);
        var inserted = await _rows.InsertAsync(_appRef, "transactions",
            Fields(("title", "old"), ("amount", 1.0)),
            actorId: null, TestContext.Current.CancellationToken);
        var id = (string)inserted["id"]!;
        var originalLm = (string)inserted["last_modified"]!;

        await Task.Delay(20, TestContext.Current.CancellationToken); // ensure clock tick

        var updated = await _rows.UpdateAsync(_appRef, "transactions", id,
            Fields(("title", "fresh"), ("amount", 7.5)),
            actorId: "u_alice", TestContext.Current.CancellationToken);

        Assert.Equal("fresh", updated["title"]);
        Assert.Equal(7.5, (double)updated["amount"]!);
        Assert.Equal(2L, (long)updated["row_version"]!);
        Assert.NotEqual(originalLm, (string)updated["last_modified"]!);
    }

    [Fact]
    public async Task Update_OnSoftDeletedRow_Throws()
    {
        await SeedTransactionsTableAsync(TestContext.Current.CancellationToken);
        var inserted = await _rows.InsertAsync(_appRef, "transactions",
            Fields(("title", "x"), ("amount", 1.0)),
            actorId: null, TestContext.Current.CancellationToken);
        var id = (string)inserted["id"]!;
        await _rows.DeleteAsync(_appRef, "transactions", id,
            hard: false, actorId: null, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _rows.UpdateAsync(_appRef, "transactions", id,
                Fields(("title", "y")), actorId: null,
                TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("row_version")]
    [InlineData("is_deleted")]
    [InlineData("author")]
    public async Task Update_RejectsForbiddenBaseColumn(string column)
    {
        await SeedTransactionsTableAsync(TestContext.Current.CancellationToken);
        var inserted = await _rows.InsertAsync(_appRef, "transactions",
            Fields(("title", "x"), ("amount", 1.0)),
            actorId: null, TestContext.Current.CancellationToken);
        var id = (string)inserted["id"]!;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _rows.UpdateAsync(_appRef, "transactions", id,
                Fields((column, "spoof")), actorId: null,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Delete_Soft_FlipsFlagsAndBumpsVersion()
    {
        await SeedTransactionsTableAsync(TestContext.Current.CancellationToken);
        var inserted = await _rows.InsertAsync(_appRef, "transactions",
            Fields(("title", "x"), ("amount", 1.0)),
            actorId: null, TestContext.Current.CancellationToken);
        var id = (string)inserted["id"]!;

        var ok = await _rows.DeleteAsync(_appRef, "transactions", id,
            hard: false, actorId: null, TestContext.Current.CancellationToken);
        Assert.True(ok);

        var row = await _rows.GetAsync(_appRef, "transactions", id,
            includeDeleted: true, TestContext.Current.CancellationToken);
        Assert.Equal(1L, (long)row!["is_deleted"]!);
        Assert.Equal(2L, (long)row["row_version"]!); // 1 (insert) + 1 (delete)
        Assert.NotNull(row["deleted_at"]);
    }

    [Fact]
    public async Task Delete_Hard_RemovesRow()
    {
        await SeedTransactionsTableAsync(TestContext.Current.CancellationToken);
        var inserted = await _rows.InsertAsync(_appRef, "transactions",
            Fields(("title", "x"), ("amount", 1.0)),
            actorId: null, TestContext.Current.CancellationToken);
        var id = (string)inserted["id"]!;

        var ok = await _rows.DeleteAsync(_appRef, "transactions", id,
            hard: true, actorId: null, TestContext.Current.CancellationToken);
        Assert.True(ok);

        var none = await _rows.GetAsync(_appRef, "transactions", id,
            includeDeleted: true, TestContext.Current.CancellationToken);
        Assert.Null(none);
    }

    [Fact]
    public async Task Restore_AfterSoftDelete_ClearsFlagsAndBumpsVersion()
    {
        await SeedTransactionsTableAsync(TestContext.Current.CancellationToken);
        var inserted = await _rows.InsertAsync(_appRef, "transactions",
            Fields(("title", "x"), ("amount", 1.0)),
            actorId: null, TestContext.Current.CancellationToken);
        var id = (string)inserted["id"]!;
        await _rows.DeleteAsync(_appRef, "transactions", id, hard: false,
            actorId: null, TestContext.Current.CancellationToken);

        var restored = await _rows.RestoreAsync(_appRef, "transactions", id,
            actorId: null, TestContext.Current.CancellationToken);
        Assert.True(restored);

        var row = await _rows.GetAsync(_appRef, "transactions", id,
            ct: TestContext.Current.CancellationToken);
        Assert.NotNull(row);
        Assert.Equal(0L, (long)row!["is_deleted"]!);
        Assert.Null(row["deleted_at"]);
        Assert.Equal(3L, (long)row["row_version"]!); // 1 insert + 1 delete + 1 restore
    }

    [Fact]
    public async Task Restore_OnActiveRow_NoOps()
    {
        await SeedTransactionsTableAsync(TestContext.Current.CancellationToken);
        var inserted = await _rows.InsertAsync(_appRef, "transactions",
            Fields(("title", "x"), ("amount", 1.0)),
            actorId: null, TestContext.Current.CancellationToken);
        var id = (string)inserted["id"]!;

        var ok = await _rows.RestoreAsync(_appRef, "transactions", id,
            actorId: null, TestContext.Current.CancellationToken);
        Assert.False(ok);
    }

    [Fact]
    public async Task Query_RoundTripsAgainstSeededRows()
    {
        await SeedTransactionsTableAsync(TestContext.Current.CancellationToken);
        await _rows.InsertAsync(_appRef, "transactions", Fields(
            ("title", "small"), ("amount", 2.0), ("category", "food")),
            actorId: null, TestContext.Current.CancellationToken);
        await _rows.InsertAsync(_appRef, "transactions", Fields(
            ("title", "big"), ("amount", 80.0), ("category", "fuel")),
            actorId: null, TestContext.Current.CancellationToken);

        var where = JsonDocument.Parse("""{ "amount": { "$gte": 10 } }""").RootElement;
        var hits = await _rows.QueryAsync(_appRef, "transactions",
            new QuerySpec(Where: where),
            TestContext.Current.CancellationToken);
        Assert.Single(hits);
        Assert.Equal("big", hits[0]["title"]);
    }

    [Fact]
    public async Task Query_HonoursIncludeDeleted()
    {
        await SeedTransactionsTableAsync(TestContext.Current.CancellationToken);
        var ins = await _rows.InsertAsync(_appRef, "transactions",
            Fields(("title", "x"), ("amount", 1.0)),
            actorId: null, TestContext.Current.CancellationToken);
        var id = (string)ins["id"]!;
        await _rows.DeleteAsync(_appRef, "transactions", id, hard: false,
            actorId: null, TestContext.Current.CancellationToken);

        var defaultHits = await _rows.QueryAsync(_appRef, "transactions",
            new QuerySpec(), TestContext.Current.CancellationToken);
        Assert.Empty(defaultHits);

        var withDeleted = await _rows.QueryAsync(_appRef, "transactions",
            new QuerySpec(IncludeDeleted: true), TestContext.Current.CancellationToken);
        Assert.Single(withDeleted);
    }

    [Fact]
    public async Task Count_HonoursDslFilters()
    {
        await SeedTransactionsTableAsync(TestContext.Current.CancellationToken);
        for (var i = 0; i < 5; i++)
            await _rows.InsertAsync(_appRef, "transactions",
                Fields(("title", $"t{i}"), ("amount", i * 1.0)),
                actorId: null, TestContext.Current.CancellationToken);

        var where = JsonDocument.Parse("""{ "amount": { "$gte": 2 } }""").RootElement;
        var n = await _rows.CountAsync(_appRef, "transactions",
            new QuerySpec(Where: where), TestContext.Current.CancellationToken);
        Assert.Equal(3, n); // amounts 2, 3, 4
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
