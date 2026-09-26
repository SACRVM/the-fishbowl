using Fishbowl.Core;
using Fishbowl.Core.Files;
using Fishbowl.Core.Repositories;
using Fishbowl.Data;
using Fishbowl.Data.Files;
using Fishbowl.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fishbowl.Scheduler.Tests;

// The daily Files tick finds contexts by their files/ folder on disk — so a
// cold-imported folder is maintained too — and skips everything else.
public class FilesMaintenanceServiceTests : IDisposable
{
    private readonly string _dataDir;
    private readonly ServiceProvider _services;
    private readonly DatabaseFactory _factory;

    public FilesMaintenanceServiceTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_files_tick_" + Path.GetRandomFileName());
        _factory = new DatabaseFactory(_dataDir);
        var sc = new ServiceCollection();
        sc.AddSingleton(_factory);
        sc.AddScoped<ISystemRepository, SystemRepository>();
        sc.AddScoped<IFileService, FileService>();
        sc.AddSingleton<FilesMaintenanceService>();
        _services = sc.BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task RunOnce_MaintainsEveryContextWithFiles_AndSweepsStaleParts()
    {
        var ct = TestContext.Current.CancellationToken;
        using (_factory.CreateContextConnection(ContextRef.User("no_files"))) { }
        var withFiles = Path.Combine(_factory.ResolveContextFolder(ContextRef.User("has_files")), "files");
        var spaceFiles = Path.Combine(_factory.ResolveContextFolder(ContextRef.Space(Ulid.NewUlid().ToString())), "files");
        Directory.CreateDirectory(withFiles);
        Directory.CreateDirectory(spaceFiles);
        var stale = Path.Combine(withFiles, ".~fb-old.part");
        File.WriteAllText(stale, "x");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-1));

        var done = await _services.GetRequiredService<FilesMaintenanceService>().RunOnceAsync(ct);

        Assert.Equal(2, done);
        Assert.False(File.Exists(stale));
    }
}
