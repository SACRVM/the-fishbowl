using Fishbowl.Core.Desktop;

namespace Fishbowl.Core.Repositories;

// The desktop of one workspace (user or space DB, schema v10): how its tiles
// are arranged and which apps are installed into it.
public interface IDesktopRepository
{
    Task<IReadOnlyList<DesktopTile>> ListTilesAsync(ContextRef ctx, CancellationToken ct = default);
    Task UpsertTileAsync(ContextRef ctx, DesktopTile tile, CancellationToken ct = default);

    Task<IReadOnlyList<DesktopApp>> ListAppsAsync(ContextRef ctx, CancellationToken ct = default);
    Task<DesktopApp?> GetAppAsync(ContextRef ctx, string id, CancellationToken ct = default);
    // False when an app with that id is already installed.
    Task<bool> InsertAppAsync(ContextRef ctx, DesktopApp app, CancellationToken ct = default);
    Task<bool> UpdateAppAsync(ContextRef ctx, DesktopApp app, CancellationToken ct = default);
    // Removes the app and its tile row. False when it wasn't installed.
    Task<bool> DeleteAppAsync(ContextRef ctx, string id, CancellationToken ct = default);
}
