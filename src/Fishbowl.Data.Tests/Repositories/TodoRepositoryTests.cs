using Xunit;
using Fishbowl.Data;
using Fishbowl.Core.Models;
using Fishbowl.Data.Repositories;
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

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDbDir))
        {
            try { Directory.Delete(_tempDbDir, true); } catch { }
        }
    }
}
