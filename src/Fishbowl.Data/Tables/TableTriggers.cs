using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Apps;
using Fishbowl.Core.Desktop;
using Fishbowl.Core.Files;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Tables;
using Jint;
using Jint.Native;
using Jint.Runtime;
using Jint.Runtime.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Data.Tables;

// Triggers (space-apps spec, phase 5): JavaScript beside a space's app code,
// `.apps/<app>/triggers/<table>.js`, run in Jint inside the Fishbowl process
// around every row write of that table — whoever writes (an app, an agent,
// the Tables app, curl): this decorates ITableRepository, so REST and MCP
// alike pass through it.
//
//   beforeInsert(row, ctx)        may change row (return it, or change it in
//   beforeUpdate(row, old, ctx)   place) or refuse: throw → the writer gets
//   beforeDelete(old, ctx)        the message (400 trigger_refused);
//                                 beforeDelete returning false skips the
//                                 delete (e.g. after archiving the row)
//   afterInsert(row, null, ctx)   after the write; a failure goes to the
//   afterUpdate(row, old, ctx)    space's error store, the write stands
//   afterDelete(null, old, ctx)
//
// `ctx` is the table API with the writer's role (get, query, count, insert,
// update, remove — through this decorator, so triggers chain, at most
// MaxDepth deep), plus ctx.user { id, canWrite, canDesign }, ctx.table and
// ctx.log(message) (into the error store). Every script run is bounded:
// time, statements, memory, recursion, the stack and what single built-ins
// build (TriggerSandbox); Jint gets no .NET, no files, no network. Scripts
// run only for a write that passes the role, table, row and own-row checks.
// Several apps may each have a trigger for a table: they run in folder
// order, each seeing what the one before left.
public sealed class TriggeringTableRepository : ITableRepository
{
    public const int MaxDepth = 3;
    public const int MaxScriptBytes = 256 * 1024;

    private static readonly AsyncLocal<int> Depth = new();

    private readonly TableRepository _inner;
    private readonly IFileService _files;
    private readonly IAppErrorRepository _errors;
    private readonly ILogger<TriggeringTableRepository> _logger;

    public TriggeringTableRepository(TableRepository inner, IFileService files, IAppErrorRepository errors,
        ILogger<TriggeringTableRepository>? logger = null)
    {
        _inner = inner;
        _files = files;
        _errors = errors;
        _logger = logger ?? NullLogger<TriggeringTableRepository>.Instance;
    }

    // ------------------------------------------------------------ writes --

    public async Task<JsonElement> InsertAsync(ContextRef ctx, TableActor actor, string table, JsonElement values, CancellationToken ct = default)
    {
        var scripts = await ScriptsAsync(ctx, table, ct);
        if (scripts.Count == 0) return await _inner.InsertAsync(ctx, actor, table, values, ct);
        await _inner.CheckWriteAsync(ctx, actor, table, null, null, ct);
        using var _ = Enter();
        foreach (var s in scripts) values = Before(s, "beforeInsert", ctx, actor, table, values, null) ?? values;
        var row = await _inner.InsertAsync(ctx, actor, table, values, ct);
        foreach (var s in scripts) await AfterAsync(s, "afterInsert", ctx, actor, table, row, null, ct);
        return row;
    }

    public async Task<JsonElement> UpdateAsync(ContextRef ctx, TableActor actor, string table, string id, JsonElement values, long? rowVersion, CancellationToken ct = default)
    {
        var scripts = await ScriptsAsync(ctx, table, ct);
        if (scripts.Count == 0) return await _inner.UpdateAsync(ctx, actor, table, id, values, rowVersion, ct);
        var old = (await _inner.CheckWriteAsync(ctx, actor, table, id, rowVersion, ct))!.Value;
        using var _ = Enter();
        foreach (var s in scripts) values = Before(s, "beforeUpdate", ctx, actor, table, values, old) ?? values;
        var row = await _inner.UpdateAsync(ctx, actor, table, id, values, rowVersion, ct);
        foreach (var s in scripts) await AfterAsync(s, "afterUpdate", ctx, actor, table, row, old, ct);
        return row;
    }

    public async Task DeleteAsync(ContextRef ctx, TableActor actor, string table, string id, CancellationToken ct = default)
    {
        var scripts = await ScriptsAsync(ctx, table, ct);
        if (scripts.Count == 0) { await _inner.DeleteAsync(ctx, actor, table, id, ct); return; }
        var old = (await _inner.CheckWriteAsync(ctx, actor, table, id, null, ct))!.Value;
        using var _ = Enter();
        foreach (var s in scripts)
            if (BeforeDelete(s, ctx, actor, table, old)) return;      // a trigger kept the row
        await _inner.DeleteAsync(ctx, actor, table, id, ct);
        foreach (var s in scripts) await AfterAsync(s, "afterDelete", ctx, actor, table, null, old, ct);
    }

    private static IDisposable Enter()
    {
        if (Depth.Value >= MaxDepth)
            throw new TableException("trigger_depth", $"Triggers called each other more than {MaxDepth} deep — stopped.", 400);
        Depth.Value++;
        return new Leave();
    }

    private sealed class Leave : IDisposable
    {
        public void Dispose() => Depth.Value--;
    }

    // ----------------------------------------------------------- scripts --

    private sealed record Script(string App, string Path, string Code);

    // Every app's trigger for this table, in folder order. Only spaces have
    // .apps; a missing folder or file just means no trigger.
    private async Task<IReadOnlyList<Script>> ScriptsAsync(ContextRef ctx, string table, CancellationToken ct)
    {
        // The table-name rule, not the SQL identifier one: a table may be
        // called "order" or "group" (its rows live in t_<name>).
        if (ctx.Type != ContextType.Space || !TableNames.IsValid(table)) return Array.Empty<Script>();
        FileListPage apps;
        try { apps = await _files.ListAsync(ctx, AppsFolder.Name, null, 500, ct); }
        catch (FileStoreException) { return Array.Empty<Script>(); }

        var list = new List<Script>();
        foreach (var dir in apps.Entries.Where(e => e.Kind == "folder" && SpaceApps.IsFolder(e.Name)).OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            var path = $"{AppsFolder.Name}/{dir.Name}/triggers/{table}.js";
            try
            {
                var h = await _files.OpenReadAsync(ctx, path, ct);
                await using var stream = h.Stream;
                if (h.Entry.Size > MaxScriptBytes)
                {
                    await _errors.AddAsync(ctx, dir.Name, AppErrors.Trigger, $"triggers/{table}.js is larger than {MaxScriptBytes / 1024} KB — not run.", null, null, ct);
                    continue;
                }
                using var reader = new StreamReader(stream);
                list.Add(new Script(dir.Name, path, await reader.ReadToEndAsync(ct)));
            }
            catch (FileStoreException) { /* no trigger for this table */ }
        }
        return list;
    }

    private Engine NewEngine(Script s, ContextRef ctx, TableActor actor, string table)
    {
        var engine = TriggerSandbox.NewEngine();
        engine.SetValue("ctx", Ctx(engine, s, ctx, actor, table));
        engine.Execute(s.Code, s.Path);
        return engine;
    }

    private JsValue? Function(Engine engine, string name)
    {
        var f = engine.GetValue(name);
        return f.IsUndefined() || f is not Jint.Native.Function.Function ? null : f;
    }

    // before*: the row as the script left it, or null when it has no such
    // function. beforeUpdate gets the row as it is (`old`), beforeInsert not.
    // Numbers the script didn't change keep their exact text.
    private JsonElement? Before(Script s, string fn, ContextRef ctx, TableActor actor, string table, JsonElement values, JsonElement? old)
    {
        try
        {
            return TriggerSandbox.OnOwnStack(() =>
            {
                var engine = NewEngine(s, ctx, actor, table);
                if (Function(engine, fn) is not { } f) return (JsonElement?)null;
                var row = ToJs(engine, values);
                var result = fn == "beforeUpdate"
                    ? engine.Invoke(f, row, old is { } o ? ToJs(engine, o) : JsValue.Null, engine.GetValue("ctx"))
                    : engine.Invoke(f, row, engine.GetValue("ctx"));
                var back = result.IsObject() ? result : row;
                return TriggerSandbox.KeepNumbers(values, ToJson(back));
            });
        }
        catch (TableException) { throw; }
        catch (JavaScriptException ex)
        {
            throw new TableException("trigger_refused", ex.Message, 400, new { app = s.App, trigger = fn });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Broken(s, fn, ex);
        }
    }

    // beforeDelete: true = the trigger kept the row (returned false).
    private bool BeforeDelete(Script s, ContextRef ctx, TableActor actor, string table, JsonElement old)
    {
        try
        {
            return TriggerSandbox.OnOwnStack(() =>
            {
                var engine = NewEngine(s, ctx, actor, table);
                if (Function(engine, "beforeDelete") is not { } f) return false;
                var result = engine.Invoke(f, ToJs(engine, old), engine.GetValue("ctx"));
                return result.IsBoolean() && !result.AsBoolean();
            });
        }
        catch (TableException) { throw; }
        catch (JavaScriptException ex)
        {
            throw new TableException("trigger_refused", ex.Message, 400, new { app = s.App, trigger = "beforeDelete" });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Broken(s, "beforeDelete", ex);
        }
    }

    private async Task AfterAsync(Script s, string fn, ContextRef ctx, TableActor actor, string table, JsonElement? row, JsonElement? old, CancellationToken ct)
    {
        try
        {
            TriggerSandbox.OnOwnStack(() =>
            {
                var engine = NewEngine(s, ctx, actor, table);
                if (Function(engine, fn) is not { } f) return;
                engine.Invoke(f, row is null ? JsValue.Null : ToJs(engine, row.Value), old is null ? JsValue.Null : ToJs(engine, old.Value), engine.GetValue("ctx"));
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The write stands; its Designers see why the after-step didn't.
            var message = ex is JavaScriptException js ? js.Message : ex is TableException te ? te.Message : Limit(ex) ?? "the trigger failed";
            await _errors.AddAsync(ctx, s.App, AppErrors.Trigger, $"{fn} on {table}: {message}", (ex as JavaScriptException)?.JavaScriptStackTrace, null, ct);
        }
    }

    // A script that can't run (syntax, a limit): the writer is refused, the
    // Designers see it in the error store.
    private TableException Broken(Script s, string fn, Exception ex)
    {
        var why = Limit(ex) ?? ex.Message;
        _logger.LogDebug(ex, "Trigger {Path} {Fn} failed", s.Path, fn);
        return new TableException("trigger_failed", $"The trigger {s.Path} ({fn}) couldn't run: {why}", 400, new { app = s.App, trigger = fn });
    }

    // Why a script stopped, when a sandbox limit stopped it (shared with tile.js).
    internal static string? Limit(Exception ex) => ex switch
    {
        System.Text.RegularExpressions.RegexMatchTimeoutException => $"a regular expression ran longer than {TriggerSandbox.TimeLimit.TotalSeconds:0.#} s",
        TimeoutException => $"it ran longer than {TriggerSandbox.TimeLimit.TotalSeconds:0.#} s",
        StatementsCountOverflowException => $"it ran more than {TriggerSandbox.StatementLimit} statements",
        MemoryLimitExceededException => "it used too much memory",
        RecursionDepthOverflowException or InsufficientExecutionStackException => "it recursed too deep",
        TriggerValueException => ex.Message,
        _ => null,
    };

    private static JsValue ToJs(Engine engine, JsonElement value) => TriggerSandbox.ToJs(engine, value);

    private static JsonElement ToJson(JsValue value) => TriggerSandbox.ToJson(value);

    // ---------------------------------------------------------------- ctx --

    private JsValue Ctx(Engine engine, Script s, ContextRef ctx, TableActor actor, string table) =>
        ScriptCtx.Build(engine, this, ctx, actor,
            // A log line that can't be stored never breaks the script.
            message => _errors.AddAsync(ctx, s.App, AppErrors.Trigger, message, null, actor.UserId).ContinueWith(_ => true).GetAwaiter().GetResult(),
            table, writes: true);

    // ------------------------------------------------------- pass-through --

    public Task<IReadOnlyList<TableDef>> ListAsync(ContextRef ctx, CancellationToken ct = default) => _inner.ListAsync(ctx, ct);
    public Task<TableDef?> DescribeAsync(ContextRef ctx, string table, CancellationToken ct = default) => _inner.DescribeAsync(ctx, table, ct);
    public Task<long> CountRowsAsync(ContextRef ctx, string table, CancellationToken ct = default) => _inner.CountRowsAsync(ctx, table, ct);
    public Task<TableDef> CreateAsync(ContextRef ctx, TableActor actor, TableDef def, CancellationToken ct = default) => _inner.CreateAsync(ctx, actor, def, ct);
    public Task<TableDef> AlterAsync(ContextRef ctx, TableActor actor, string table, TableChange change, CancellationToken ct = default) => _inner.AlterAsync(ctx, actor, table, change, ct);
    public Task DropAsync(ContextRef ctx, TableActor actor, string table, CancellationToken ct = default) => _inner.DropAsync(ctx, actor, table, ct);
    public Task<JsonElement?> GetAsync(ContextRef ctx, string table, string id, CancellationToken ct = default) => _inner.GetAsync(ctx, table, id, ct);
    public Task<IReadOnlyList<JsonElement>> QueryAsync(ContextRef ctx, string table, QuerySpec spec, CancellationToken ct = default) => _inner.QueryAsync(ctx, table, spec, ct);
    public Task<long> CountAsync(ContextRef ctx, string table, QuerySpec spec, CancellationToken ct = default) => _inner.CountAsync(ctx, table, spec, ct);
    public Task<(IReadOnlyList<RowSearchHit> Hits, bool Degraded)> SearchAsync(ContextRef ctx, string query, string? table, int limit, CancellationToken ct = default) => _inner.SearchAsync(ctx, query, table, limit, ct);
    public Task ReindexAsync(ContextRef ctx, string table, string? id = null, CancellationToken ct = default) => _inner.ReindexAsync(ctx, table, id, ct);
    public Task<IReadOnlyList<AggregateResult>> AggregateAsync(ContextRef ctx, string table, string fn, string? column, string? groupBy, JsonElement? where, CancellationToken ct = default) => _inner.AggregateAsync(ctx, table, fn, column, groupBy, where, ct);
}
