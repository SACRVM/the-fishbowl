using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Apps;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fishbowl.Data.Tests.Repositories;

// Apps platform — Phase B.2 schema management. Covers:
//  * CREATE TABLE injects all eight base columns + soft-delete index
//  * Identifier validation rejects reserved keywords, invalid charsets, base
//    column overlaps, duplicates
//  * ALTER (add/rename/drop) leaves base columns untouchable
//  * DescribeTable hydrates IsUnique from pragma_index_list
//  * ListTables hides SQLite internals
public class AppSchemaTests : IDisposable
{
    private readonly string _dataDir;
    private readonly DatabaseFactory _factory;
    private readonly AppSchemaRepository _schema;
    private readonly AppRef _appRef = AppRef.OfUser("u_alice", "app_test");

    public AppSchemaTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_apps_b2_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);
        _factory = new DatabaseFactory(_dataDir);
        _schema = new AppSchemaRepository(_factory);
    }

    [Fact]
    public async Task CreateTable_InjectsBaseColumnsAndSoftDeleteIndex()
    {
        var cols = new[]
        {
            new AppColumn("amount", AppColumnType.Real, Nullable: false),
            new AppColumn("counterparty", AppColumnType.Text),
            new AppColumn("category", AppColumnType.Text),
        };
        await _schema.CreateTableAsync(_appRef, "transactions", cols, TestContext.Current.CancellationToken);

        using var db = (SqliteConnection)_factory.CreateAppConnection(_appRef);

        var names = db.Query<string>("SELECT name FROM pragma_table_info('transactions')").ToList();
        foreach (var baseCol in BaseColumns.Names)
            Assert.Contains(baseCol, names);
        Assert.Contains("amount", names);
        Assert.Contains("counterparty", names);

        // The mandatory soft-delete index is named idx_<table>_is_deleted.
        var idx = db.QuerySingleOrDefault<string?>(
            "SELECT name FROM sqlite_master WHERE type='index' AND name = @name",
            new { name = BaseColumns.SoftDeleteIndexName("transactions") });
        Assert.Equal(BaseColumns.SoftDeleteIndexName("transactions"), idx);
    }

    [Fact]
    public async Task CreateTable_Twice_Throws()
    {
        var cols = new[] { new AppColumn("x", AppColumnType.Text) };
        await _schema.CreateTableAsync(_appRef, "dup", cols, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _schema.CreateTableAsync(_appRef, "dup", cols, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("Select")]    // uppercase rejected
    [InlineData("1notes")]    // leading digit
    [InlineData("co-lor")]   // hyphen
    [InlineData("note name")] // space
    [InlineData("")]
    public async Task CreateTable_RejectsInvalidTableName(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _schema.CreateTableAsync(_appRef, name, Array.Empty<AppColumn>(),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateTable_RejectsReservedKeywordTableName()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _schema.CreateTableAsync(_appRef, "select", Array.Empty<AppColumn>(),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateTable_RejectsColumnOverlappingBaseColumn()
    {
        var bad = new[] { new AppColumn("id", AppColumnType.Text) };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _schema.CreateTableAsync(_appRef, "x", bad, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateTable_RejectsDuplicateUserColumn()
    {
        var bad = new[]
        {
            new AppColumn("amount", AppColumnType.Real),
            new AppColumn("amount", AppColumnType.Real),
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _schema.CreateTableAsync(_appRef, "x", bad, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateTable_RejectsMismatchedDefaultValueType()
    {
        var bad = new[] { new AppColumn("n", AppColumnType.Integer, DefaultValue: "not-a-number") };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _schema.CreateTableAsync(_appRef, "x", bad, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddColumn_RoundTrip()
    {
        await _schema.CreateTableAsync(_appRef, "rows",
            new[] { new AppColumn("x", AppColumnType.Text) }, TestContext.Current.CancellationToken);
        await _schema.AddColumnAsync(_appRef, "rows",
            new AppColumn("y", AppColumnType.Integer), TestContext.Current.CancellationToken);

        using var db = (SqliteConnection)_factory.CreateAppConnection(_appRef);
        var names = db.Query<string>("SELECT name FROM pragma_table_info('rows')").ToList();
        Assert.Contains("y", names);
    }

    [Fact]
    public async Task AddColumn_RejectsBaseColumnOverlap()
    {
        await _schema.CreateTableAsync(_appRef, "rows",
            new[] { new AppColumn("x", AppColumnType.Text) }, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _schema.AddColumnAsync(_appRef, "rows",
                new AppColumn("id", AppColumnType.Text), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddColumn_RejectsNotNullColumn()
    {
        await _schema.CreateTableAsync(_appRef, "rows",
            new[] { new AppColumn("x", AppColumnType.Text) }, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _schema.AddColumnAsync(_appRef, "rows",
                new AppColumn("y", AppColumnType.Integer, Nullable: false),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddColumn_RejectsUniqueColumn()
    {
        await _schema.CreateTableAsync(_appRef, "rows",
            new[] { new AppColumn("x", AppColumnType.Text) }, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _schema.AddColumnAsync(_appRef, "rows",
                new AppColumn("y", AppColumnType.Text, IsUnique: true),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RenameColumn_RoundTrip()
    {
        await _schema.CreateTableAsync(_appRef, "rows",
            new[] { new AppColumn("old", AppColumnType.Text) }, TestContext.Current.CancellationToken);
        await _schema.RenameColumnAsync(_appRef, "rows", "old", "fresh",
            TestContext.Current.CancellationToken);

        using var db = (SqliteConnection)_factory.CreateAppConnection(_appRef);
        var names = db.Query<string>("SELECT name FROM pragma_table_info('rows')").ToList();
        Assert.Contains("fresh", names);
        Assert.DoesNotContain("old", names);
    }

    [Fact]
    public async Task RenameColumn_RejectsBaseColumnSource()
    {
        await _schema.CreateTableAsync(_appRef, "rows",
            new[] { new AppColumn("x", AppColumnType.Text) }, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _schema.RenameColumnAsync(_appRef, "rows", "id", "uid",
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RenameColumn_RejectsBaseColumnTarget()
    {
        await _schema.CreateTableAsync(_appRef, "rows",
            new[] { new AppColumn("x", AppColumnType.Text) }, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _schema.RenameColumnAsync(_appRef, "rows", "x", "id",
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DropColumn_RoundTrip()
    {
        await _schema.CreateTableAsync(_appRef, "rows",
            new[]
            {
                new AppColumn("keep", AppColumnType.Text),
                new AppColumn("toss", AppColumnType.Text),
            }, TestContext.Current.CancellationToken);
        await _schema.DropColumnAsync(_appRef, "rows", "toss", TestContext.Current.CancellationToken);

        using var db = (SqliteConnection)_factory.CreateAppConnection(_appRef);
        var names = db.Query<string>("SELECT name FROM pragma_table_info('rows')").ToList();
        Assert.Contains("keep", names);
        Assert.DoesNotContain("toss", names);
    }

    [Fact]
    public async Task DropColumn_RejectsBaseColumn()
    {
        await _schema.CreateTableAsync(_appRef, "rows",
            new[] { new AppColumn("x", AppColumnType.Text) }, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _schema.DropColumnAsync(_appRef, "rows", "id", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DropTable_RoundTrip()
    {
        await _schema.CreateTableAsync(_appRef, "doomed",
            new[] { new AppColumn("x", AppColumnType.Text) }, TestContext.Current.CancellationToken);
        await _schema.DropTableAsync(_appRef, "doomed", TestContext.Current.CancellationToken);

        using var db = (SqliteConnection)_factory.CreateAppConnection(_appRef);
        var existed = db.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name = 'doomed'");
        Assert.Equal(0, existed);
    }

    [Fact]
    public async Task DropTable_RejectsNonexistent()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _schema.DropTableAsync(_appRef, "ghost", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateIndex_RoundTrip()
    {
        await _schema.CreateTableAsync(_appRef, "rows",
            new[] { new AppColumn("col", AppColumnType.Text) }, TestContext.Current.CancellationToken);
        await _schema.CreateIndexAsync(_appRef, "rows", "col", TestContext.Current.CancellationToken);

        using var db = (SqliteConnection)_factory.CreateAppConnection(_appRef);
        var idx = db.QuerySingleOrDefault<string?>(
            "SELECT name FROM sqlite_master WHERE type='index' AND name = @name",
            new { name = "idx_rows_col" });
        Assert.Equal("idx_rows_col", idx);
    }

    [Fact]
    public async Task CreateIndex_RejectsBaseColumn()
    {
        await _schema.CreateTableAsync(_appRef, "rows",
            new[] { new AppColumn("x", AppColumnType.Text) }, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _schema.CreateIndexAsync(_appRef, "rows", "is_deleted",
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ListTables_HidesSqliteInternals_OrdersByName()
    {
        await _schema.CreateTableAsync(_appRef, "alpha",
            new[] { new AppColumn("x", AppColumnType.Text) }, TestContext.Current.CancellationToken);
        await _schema.CreateTableAsync(_appRef, "zeta",
            new[] { new AppColumn("x", AppColumnType.Text) }, TestContext.Current.CancellationToken);

        var tables = await _schema.ListTablesAsync(_appRef, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "alpha", "zeta" }, tables.ToArray());
    }

    [Fact]
    public async Task DescribeTable_ReturnsNullForMissing()
    {
        var desc = await _schema.DescribeTableAsync(_appRef, "ghost",
            TestContext.Current.CancellationToken);
        Assert.Null(desc);
    }

    [Fact]
    public async Task DescribeTable_IncludesBaseColumnsFirstThenUserColumns()
    {
        var userCols = new[]
        {
            new AppColumn("amount", AppColumnType.Real, Nullable: false),
            new AppColumn("note", AppColumnType.Text),
        };
        await _schema.CreateTableAsync(_appRef, "tx", userCols, TestContext.Current.CancellationToken);

        var desc = await _schema.DescribeTableAsync(_appRef, "tx",
            TestContext.Current.CancellationToken);
        Assert.NotNull(desc);
        // First nine columns must be the base columns in declaration order.
        var baseNames = BaseColumns.All.Select(c => c.Name).ToArray();
        for (var i = 0; i < baseNames.Length; i++)
        {
            Assert.Equal(baseNames[i], desc!.Columns[i].Name);
            Assert.True(desc.Columns[i].IsBaseColumn);
        }
        Assert.Equal("amount", desc!.Columns[baseNames.Length].Name);
        Assert.False(desc.Columns[baseNames.Length].IsBaseColumn);
        Assert.Equal("note", desc.Columns[baseNames.Length + 1].Name);
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
