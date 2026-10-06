using Fishbowl.Core;
using Fishbowl.Core.Desktop;
using Fishbowl.Core.Files;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Tables;
using Jint;
using Jint.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Data.Tables;

// A space app's tile (AppTiles): `.apps/<folder>/tile.js` defines
//
//   function tile(ctx) { return { main, sub, items: [{ lead, text, tail }], empty }; }
//
// and the desktop shows what it returns, fresh on every load — run in the
// trigger sandbox (TriggerSandbox: time, statements, memory, no .NET, files or
// network) as the viewer, READ-ONLY: ctx.get, ctx.query, ctx.count, ctx.user,
// ctx.log (into the error store, kind "tile"). A script that fails, hits a
// limit or returns nothing leaves its tile plain; why goes to the error store.
public sealed class SpaceAppTiles : ISpaceAppTiles
{
    private readonly ITableRepository _tables;
    private readonly IFileService _files;
    private readonly IAppErrorRepository _errors;
    private readonly ILogger<SpaceAppTiles> _logger;

    public SpaceAppTiles(ITableRepository tables, IFileService files, IAppErrorRepository errors, ILogger<SpaceAppTiles>? logger = null)
    {
        _tables = tables;
        _files = files;
        _errors = errors;
        _logger = logger ?? NullLogger<SpaceAppTiles>.Instance;
    }

    private static string PathOf(string folder) => $"{AppsFolder.Name}/{folder}/{AppTiles.ScriptName}";

    public async Task<bool> HasTileAsync(ContextRef ctx, string folder, CancellationToken ct = default)
    {
        if (ctx.Type != ContextType.Space || !SpaceApps.IsFolder(folder)) return false;
        try { return (await _files.StatAsync(ctx, PathOf(folder), ct)).Kind == "file"; }
        catch (FileStoreException) { return false; }
    }

    public async Task<IReadOnlyDictionary<string, AppTile>> RenderAsync(ContextRef ctx, TableActor actor, CancellationToken ct = default)
    {
        var tiles = new Dictionary<string, AppTile>(StringComparer.Ordinal);
        if (ctx.Type != ContextType.Space) return tiles;
        FileListPage apps;
        try { apps = await _files.ListAsync(ctx, AppsFolder.Name, null, 500, ct); }
        catch (FileStoreException) { return tiles; }

        foreach (var dir in apps.Entries.Where(e => e.Kind == "folder" && SpaceApps.IsFolder(e.Name)).OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            var path = PathOf(dir.Name);
            string code;
            try
            {
                var h = await _files.OpenReadAsync(ctx, path, ct);
                await using var stream = h.Stream;
                if (h.Entry.Size > TriggeringTableRepository.MaxScriptBytes)
                {
                    await _errors.AddAsync(ctx, dir.Name, AppErrors.Tile, $"{AppTiles.ScriptName} is larger than {TriggeringTableRepository.MaxScriptBytes / 1024} KB — not run.", null, null, ct);
                    continue;
                }
                using var reader = new StreamReader(stream);
                code = await reader.ReadToEndAsync(ct);
            }
            catch (FileStoreException) { continue; }   // no tile.js: a plain tile

            try
            {
                var value = TriggerSandbox.OnOwnStack(() =>
                {
                    var engine = TriggerSandbox.NewEngine();
                    engine.SetValue("ctx", ScriptCtx.Build(engine, _tables, ctx, actor,
                        // A log line that can't be stored never breaks the script.
                        message => _errors.AddAsync(ctx, dir.Name, AppErrors.Tile, message, null, actor.UserId).ContinueWith(_ => true).GetAwaiter().GetResult()));
                    engine.Execute(code, path);
                    if (engine.GetValue("tile") is not Jint.Native.Function.Function f) return (System.Text.Json.JsonElement?)null;
                    var result = engine.Invoke(f, engine.GetValue("ctx"));
                    return result.IsNull() || result.IsUndefined() ? null : TriggerSandbox.ToJson(result);
                });
                if (value is { } v && AppTiles.Clean(v) is { } tile) tiles[dir.Name] = tile;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var why = ex is JavaScriptException js ? js.Message
                    : ex is TableException te ? te.Message
                    : TriggeringTableRepository.Limit(ex) ?? "it couldn't run";
                _logger.LogDebug(ex, "Tile {Path} failed", path);
                await _errors.AddAsync(ctx, dir.Name, AppErrors.Tile, $"{AppTiles.ScriptName}: {why}", (ex as JavaScriptException)?.JavaScriptStackTrace, actor.UserId, ct);
            }
        }
        return tiles;
    }
}
