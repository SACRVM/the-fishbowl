using System.Text.Json;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Desktop;
using Fishbowl.Core.Repositories;

namespace Fishbowl.Data.Repositories;

public class DesktopRepository : IDesktopRepository
{
    private readonly DatabaseFactory _dbFactory;

    public DesktopRepository(DatabaseFactory dbFactory)
    {
        _dbFactory = dbFactory;
    }

    private sealed class TileRow
    {
        public string Key { get; set; } = "";
        public double? Position { get; set; }
        public string? Size { get; set; }
        public string? Color { get; set; }
        public long Hidden { get; set; }
    }

    private sealed class AppRow
    {
        public string Id { get; set; } = "";
        public string ManifestUrl { get; set; } = "";
        public string Manifest { get; set; } = "";
        public string Origin { get; set; } = "";
        public string EntryUrl { get; set; } = "";
        public string? EntryIntegrity { get; set; }
        public string? Version { get; set; }
        public string Mode { get; set; } = "";
        public string Granted { get; set; } = "[]";
        public string InstalledAt { get; set; } = "";
        public string UpdatedAt { get; set; } = "";
    }

    private static DesktopApp ToApp(AppRow r) => new(
        r.Id, r.ManifestUrl, r.Manifest, r.Origin, r.EntryUrl, r.EntryIntegrity, r.Version, r.Mode,
        JsonSerializer.Deserialize<string[]>(r.Granted) ?? Array.Empty<string>(),
        DateTime.Parse(r.InstalledAt, null, System.Globalization.DateTimeStyles.RoundtripKind),
        DateTime.Parse(r.UpdatedAt, null, System.Globalization.DateTimeStyles.RoundtripKind));

    private static object Params(DesktopApp a) => new
    {
        a.Id,
        a.ManifestUrl,
        a.Manifest,
        a.Origin,
        a.EntryUrl,
        a.EntryIntegrity,
        a.Version,
        a.Mode,
        Granted = JsonSerializer.Serialize(a.Granted),
        InstalledAt = a.InstalledAt.ToUniversalTime().ToString("o"),
        UpdatedAt = a.UpdatedAt.ToUniversalTime().ToString("o"),
    };

    public async Task<IReadOnlyList<DesktopTile>> ListTilesAsync(ContextRef ctx, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateContextConnection(ctx);
        var rows = await db.QueryAsync<TileRow>(new CommandDefinition(
            "SELECT key, position, size, color, hidden FROM desktop_tiles ORDER BY position IS NULL, position, key",
            cancellationToken: ct));
        return rows.Select(r => new DesktopTile(r.Key, r.Position, r.Size, r.Color, r.Hidden != 0)).ToList();
    }

    public async Task UpsertTileAsync(ContextRef ctx, DesktopTile tile, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateContextConnection(ctx);
        await db.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO desktop_tiles (key, position, size, color, hidden)
            VALUES (@Key, @Position, @Size, @Color, @Hidden)
            ON CONFLICT(key) DO UPDATE SET
                position = excluded.position, size = excluded.size,
                color = excluded.color, hidden = excluded.hidden",
            new { tile.Key, tile.Position, tile.Size, tile.Color, Hidden = tile.Hidden ? 1 : 0 },
            cancellationToken: ct));
    }

    public async Task<IReadOnlyList<DesktopApp>> ListAppsAsync(ContextRef ctx, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateContextConnection(ctx);
        var rows = await db.QueryAsync<AppRow>(new CommandDefinition(
            "SELECT * FROM desktop_apps ORDER BY installed_at, id", cancellationToken: ct));
        return rows.Select(ToApp).ToList();
    }

    public async Task<DesktopApp?> GetAppAsync(ContextRef ctx, string id, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateContextConnection(ctx);
        var row = await db.QuerySingleOrDefaultAsync<AppRow>(new CommandDefinition(
            "SELECT * FROM desktop_apps WHERE id = @id", new { id }, cancellationToken: ct));
        return row is null ? null : ToApp(row);
    }

    public async Task<bool> InsertAppAsync(ContextRef ctx, DesktopApp app, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateContextConnection(ctx);
        var n = await db.ExecuteAsync(new CommandDefinition(@"
            INSERT OR IGNORE INTO desktop_apps
                (id, manifest_url, manifest, origin, entry_url, entry_integrity, version, mode, granted, installed_at, updated_at)
            VALUES
                (@Id, @ManifestUrl, @Manifest, @Origin, @EntryUrl, @EntryIntegrity, @Version, @Mode, @Granted, @InstalledAt, @UpdatedAt)",
            Params(app), cancellationToken: ct));
        return n == 1;
    }

    public async Task<bool> UpdateAppAsync(ContextRef ctx, DesktopApp app, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateContextConnection(ctx);
        var n = await db.ExecuteAsync(new CommandDefinition(@"
            UPDATE desktop_apps SET
                manifest_url = @ManifestUrl, manifest = @Manifest, origin = @Origin, entry_url = @EntryUrl,
                entry_integrity = @EntryIntegrity, version = @Version, mode = @Mode, granted = @Granted,
                updated_at = @UpdatedAt
            WHERE id = @Id",
            Params(app), cancellationToken: ct));
        return n == 1;
    }

    public async Task<bool> DeleteAppAsync(ContextRef ctx, string id, CancellationToken ct = default)
    {
        return await _dbFactory.WithContextTransactionAsync(ctx, async (db, tx, token) =>
        {
            await db.ExecuteAsync(new CommandDefinition(
                "DELETE FROM desktop_tiles WHERE key = @key", new { key = "app:" + id }, transaction: tx, cancellationToken: token));
            var n = await db.ExecuteAsync(new CommandDefinition(
                "DELETE FROM desktop_apps WHERE id = @id", new { id }, transaction: tx, cancellationToken: token));
            return n == 1;
        }, ct);
    }
}
