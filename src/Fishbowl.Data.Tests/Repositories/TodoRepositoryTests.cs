using Xunit;
using Fishbowl.Data;
using Fishbowl.Core.Models;
using Fishbowl.Data.Repositories;
using Dapper;
using Microsoft.Data.Sqlite;
using System.IO;
using System.Threading.Tasks;

namespace Fishbowl.Tests.Repositories;

public class TodoRepositoryTests : IDisposable
{
    private readonly string _tempDbDir;
    private readonly DatabaseFactory _dbFactory;
    private readonly TodoRepository _repo;
    private const string TestUserId = "todo_repo_test";

    public TodoRepositoryTests()
    {
        _tempDbDir = Path.Combine(Path.GetTempPath(), "fishbowl_todo_repo_tests_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDbDir);
        _dbFactory = new DatabaseFactory(_tempDbDir);
        _repo = new TodoRepository(_dbFactory);
    }

    [Fact]
    public async Task CreateAndGet_Test()
    {
        // Arrange
        var todo = new TodoItem { Title = "Task 1", Description = "Desc" };

        // Act
        var id = await _repo.CreateAsync(TestUserId, todo, TestContext.Current.CancellationToken);
        var retrieved = await _repo.GetByIdAsync(TestUserId, id, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal("Task 1", retrieved.Title);
    }

    [Fact]
    public async Task GetAll_Filtering_Test()
    {
        // Arrange
        await _repo.CreateAsync(TestUserId, new TodoItem { Title = "Active Task" }, TestContext.Current.CancellationToken);
        await _repo.CreateAsync(TestUserId, new TodoItem { Title = "Completed Task", CompletedAt = DateTime.UtcNow }, TestContext.Current.CancellationToken);

        // Act
        var activeOnly = await _repo.GetAllAsync(TestUserId, includeCompleted: false, TestContext.Current.CancellationToken);
        var all = await _repo.GetAllAsync(TestUserId, includeCompleted: true, TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(activeOnly);
        Assert.Equal(2, all.Count());
    }

    [Fact]
    public async Task Create_RejectsOversizedTitle()
    {
        var todo = new TodoItem
        {
            Title = new string('x', Fishbowl.Core.Util.TodoLimits.MaxTitleLength + 1),
        };

        var ex = await Assert.ThrowsAsync<Fishbowl.Core.Util.ResourceValidationException>(() =>
            _repo.CreateAsync(TestUserId, todo, TestContext.Current.CancellationToken));
        Assert.Equal("todo", ex.Error.Resource);
        Assert.Equal("title", ex.Error.Field);
    }

    [Fact]
    public async Task Update_RejectsOversizedDescription()
    {
        var todo = new TodoItem { Title = "ok" };
        var id = await _repo.CreateAsync(TestUserId, todo, TestContext.Current.CancellationToken);

        var loaded = await _repo.GetByIdAsync(TestUserId, id, TestContext.Current.CancellationToken);
        Assert.NotNull(loaded);
        loaded!.Description = new string('d', Fishbowl.Core.Util.TodoLimits.MaxDescriptionLength + 1);

        await Assert.ThrowsAsync<Fishbowl.Core.Util.ResourceValidationException>(() =>
            _repo.UpdateAsync(TestUserId, loaded, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Position_AppendsOnCreate_KeptOnUpdateWithout_MovesWhenGiven_Test()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await _repo.CreateAsync(TestUserId, new TodoItem { Title = "A" }, ct);
        var b = await _repo.CreateAsync(TestUserId, new TodoItem { Title = "B" }, ct);
        var c = await _repo.CreateAsync(TestUserId, new TodoItem { Title = "C" }, ct);

        // Appended in order.
        var all = (await _repo.GetAllAsync(TestUserId, true, ct)).ToList();
        Assert.Equal(new[] { "A", "B", "C" }, all.Select(t => t.Title));
        Assert.True(all[0].Position < all[1].Position && all[1].Position < all[2].Position);

        // An update that doesn't carry a position (older clients, done/undone)
        // leaves it alone.
        var bItem = (await _repo.GetByIdAsync(TestUserId, b, ct))!;
        var bPos = bItem.Position;
        bItem.Position = null;
        bItem.CompletedAt = DateTime.UtcNow;
        await _repo.UpdateAsync(TestUserId, bItem, ct);
        Assert.Equal(bPos, (await _repo.GetByIdAsync(TestUserId, b, ct))!.Position);

        // A given position moves it: C between A and B.
        var cItem = (await _repo.GetByIdAsync(TestUserId, c, ct))!;
        cItem.Position = (all[0].Position + all[1].Position) / 2;
        await _repo.UpdateAsync(TestUserId, cItem, ct);
        all = (await _repo.GetAllAsync(TestUserId, true, ct)).ToList();
        Assert.Equal(new[] { "A", "C", "B" }, all.Select(t => t.Title));
        Assert.Equal(a, all[0].Id);
    }

    [Fact]
    public void UserV8_NumbersExistingTodosInCreationOrder_Test()
    {
        var userId = "todo_v8_upgrade";
        using (var db = _dbFactory.CreateConnection(userId))
        {
            // Rewind to a v7 DB: no position column, todos out of rowid order.
            db.Execute("DROP TABLE todos");
            db.Execute(@"CREATE TABLE todos (id TEXT PRIMARY KEY, title TEXT NOT NULL, description TEXT,
                due_at TEXT, reminder_at TEXT, source TEXT, created_by TEXT NOT NULL,
                created_at TEXT NOT NULL, updated_at TEXT NOT NULL, completed_at TEXT)");
            foreach (var (id, at) in new[] { ("t3", "2026-03-01"), ("t1", "2026-01-01"), ("t2", "2026-02-01") })
                db.Execute("INSERT INTO todos VALUES (@id, @id, NULL, NULL, NULL, NULL, 'u', @at, @at, NULL)", new { id, at });
            db.Execute("PRAGMA user_version = 7");
        }
        SqliteConnection.ClearAllPools();

        using var upgraded = new DatabaseFactory(_tempDbDir).CreateConnection(userId);
        var order = upgraded.Query<string>("SELECT id FROM todos ORDER BY position").ToList();
        Assert.Equal(new[] { "t1", "t2", "t3" }, order);
        Assert.Equal(8, upgraded.ExecuteScalar<long>("PRAGMA user_version"));
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
