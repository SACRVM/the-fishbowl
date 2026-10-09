using System.Diagnostics;
using Dapper;
using Fishbowl.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fishbowl.Data.Tests;

// Opening a DB must not write (the v3 tag reconcile took a write lock on
// every open), a migration that throws must not leave the file open, and the
// migrations that bump user_version after their own commit must survive a
// crash in between (re-running them must not fail with "duplicate column").
public class DatabaseOpenRobustnessTests : IDisposable
{
    private readonly string _dataDir;

    public DatabaseOpenRobustnessTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_open_robust_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);
    }

    [Fact]
    public void ContextOpen_WhileAnotherConnectionWrites_DoesNotWait()
    {
        var factory = new DatabaseFactory(_dataDir);
        var ctx = ContextRef.User("open_lock_user");
        using var writer = factory.CreateContextConnection(ctx);
        using var tx = writer.BeginTransaction();   // BEGIN IMMEDIATE: holds the write lock
        writer.Execute("UPDATE tags SET color = color", transaction: tx);

        var watch = Stopwatch.StartNew();
        using (var reader = factory.CreateContextConnection(ctx))
            Assert.Equal(Fishbowl.Data.DatabaseFactory.UserSchemaHead, reader.ExecuteScalar<long>("PRAGMA user_version"));
        // A write on open would wait for the lock (30 s) and then fail.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"open took {watch.Elapsed}");
        tx.Rollback();
    }

    [Fact]
    public void FailedMigration_ClosesTheFile()
    {
        var path = Path.Combine(_dataDir, "system.db");
        using (var seed = new SqliteConnection($"Data Source={path}"))
        {
            // A v8 system DB without its tables: v9's ALTER TABLE fails.
            seed.Open();
            seed.Execute("PRAGMA user_version = 8");
        }
        SqliteConnection.ClearAllPools();

        var factory = new DatabaseFactory(_dataDir);
        Assert.ThrowsAny<SqliteException>(() => factory.CreateSystemConnection());

        // Disposed, the connection went back to the pool, which lets go of
        // the file; a leaked one would keep it open (Windows: can't delete).
        SqliteConnection.ClearAllPools();
        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void UserV8_RerunAfterCrash_KeepsPositions()
    {
        var factory = new DatabaseFactory(_dataDir);
        var ctx = ContextRef.User("v8_rerun_user");
        using (var db = factory.CreateContextConnection(ctx))
        {
            db.Execute("INSERT INTO todos (id, title, created_by, created_at, updated_at, position) " +
                       "VALUES ('t1', 'a', 'u', '2026-01-01', '2026-01-01', 42.5)");
            // Committed, but the bump to 8 never happened.
            db.Execute("PRAGMA user_version = 7");
        }
        SqliteConnection.ClearAllPools();

        using var reopened = new DatabaseFactory(_dataDir).CreateContextConnection(ctx);
        Assert.Equal(Fishbowl.Data.DatabaseFactory.UserSchemaHead, reopened.ExecuteScalar<long>("PRAGMA user_version"));
        Assert.Equal(42.5, reopened.ExecuteScalar<double>("SELECT position FROM todos WHERE id = 't1'"));
    }

    [Theory]
    [InlineData(8)]   // v9: spaces.color, users.accent, users.date_format
    [InlineData(9)]   // v10: users.vault_auto_lock_minutes
    public void SystemMigration_RerunAfterCrash_Opens(int version)
    {
        var factory = new DatabaseFactory(_dataDir);
        using (var db = factory.CreateSystemConnection())
            db.Execute($"PRAGMA user_version = {version}");
        SqliteConnection.ClearAllPools();

        using var reopened = new DatabaseFactory(_dataDir).CreateSystemConnection();
        Assert.Equal(18, reopened.ExecuteScalar<long>("PRAGMA user_version"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch { }
    }
}
