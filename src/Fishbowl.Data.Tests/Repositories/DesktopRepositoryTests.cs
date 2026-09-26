using Fishbowl.Core;
using Fishbowl.Core.Desktop;
using Fishbowl.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fishbowl.Data.Tests.Repositories;

// desktop_tiles + desktop_apps (user/space schema v10).
public class DesktopRepositoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fishbowl_desktop_repo_" + Path.GetRandomFileName());
    private readonly DesktopRepository _repo;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly ContextRef Ctx = ContextRef.User("desk_user");

    public DesktopRepositoryTests()
    {
        _repo = new DesktopRepository(new DatabaseFactory(_dir));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static DesktopApp App(string id, string mode = AppModes.Sandboxed) => new(
        id, "https://o.github.io/app.json", "{\"id\":\"" + id + "\"}", "https://o.github.io", "https://o.github.io/app.js",
        mode == AppModes.Sandboxed ? "sha256-47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=" : null,
        "1.0.0", mode, new[] { "files" }, DateTime.UtcNow, DateTime.UtcNow);

    [Fact]
    public async Task Apps_RoundTrip_InsertOnce_Update_Delete()
    {
        Assert.True(await _repo.InsertAppAsync(Ctx, App("a"), Ct));
        Assert.False(await _repo.InsertAppAsync(Ctx, App("a"), Ct));

        var got = await _repo.GetAppAsync(Ctx, "a", Ct);
        Assert.NotNull(got);
        Assert.Equal(new[] { "files" }, got!.Granted);
        Assert.Equal(AppModes.Sandboxed, got.Mode);

        Assert.True(await _repo.UpdateAppAsync(Ctx, got with { Mode = AppModes.Trusted, EntryIntegrity = null, Granted = Array.Empty<string>() }, Ct));
        var updated = await _repo.GetAppAsync(Ctx, "a", Ct);
        Assert.Equal(AppModes.Trusted, updated!.Mode);
        Assert.Null(updated.EntryIntegrity);
        Assert.Empty(updated.Granted);

        await _repo.UpsertTileAsync(Ctx, new DesktopTile("app:a", 1, "wide", null, false), Ct);
        Assert.True(await _repo.DeleteAppAsync(Ctx, "a", Ct));
        Assert.Empty(await _repo.ListAppsAsync(Ctx, Ct));
        Assert.Empty(await _repo.ListTilesAsync(Ctx, Ct));   // its tile goes with it
        Assert.False(await _repo.DeleteAppAsync(Ctx, "a", Ct));
    }

    [Fact]
    public async Task Tiles_UpsertReplaces_AndSortByPosition()
    {
        await _repo.UpsertTileAsync(Ctx, new DesktopTile("builtin:notes", 2, null, null, false), Ct);
        await _repo.UpsertTileAsync(Ctx, new DesktopTile("builtin:todos", 1, "large", "teal", false), Ct);
        await _repo.UpsertTileAsync(Ctx, new DesktopTile("builtin:calendar", null, null, null, true), Ct);
        await _repo.UpsertTileAsync(Ctx, new DesktopTile("builtin:notes", 3, "wide", null, false), Ct);

        var tiles = await _repo.ListTilesAsync(Ctx, Ct);
        Assert.Equal(new[] { "builtin:todos", "builtin:notes", "builtin:calendar" }, tiles.Select(t => t.Key));
        Assert.Equal("wide", tiles[1].Size);
        Assert.True(tiles[2].Hidden);
    }
}
