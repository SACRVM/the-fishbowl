using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Apps;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Tables;
using Jint;
using Jint.Native;
using Jint.Runtime;
using Jint.Runtime.Interop;

namespace Fishbowl.Data.Tables;

// The `ctx` a space's scripts get in Jint (TriggerSandbox): the table API with
// the caller's role — get, query, count and, for triggers, insert, update and
// remove — plus ctx.user { id, canWrite, canDesign }, ctx.table (a trigger's
// table) and ctx.log(message). A tile.js (SpaceAppTiles) gets the reads only.
// Every call runs synchronously on the script's own thread (OnOwnStack); a
// refusal reaches the script as an exception it may catch.
internal static class ScriptCtx
{
    public static JsValue Build(Engine engine, ITableRepository tables, ContextRef ctx, TableActor actor,
        Action<string> log, string? table = null, bool writes = false)
    {
        var o = new JsObject(engine);
        JsValue Json(object? v) => v is null ? JsValue.Null : TriggerSandbox.ToJs(engine, JsonSerializer.SerializeToElement(v));
        JsonElement Arg(JsValue v) => v.IsUndefined() || v.IsNull() ? JsonDocument.Parse("{}").RootElement.Clone() : TriggerSandbox.ToJson(v);
        string Str(JsValue v, string what) => v.IsString() ? v.AsString() : throw new JavaScriptException($"{what} must be a string");
        void Fn(string name, Func<JsValue[], JsValue> body) =>
            o.Set(name, new ClrFunction(engine, name, (_, args) => body(args)));
        JsValue At(JsValue[] a, int i) => i < a.Length ? a[i] : JsValue.Undefined;
        T Run<T>(Task<T> t) => t.GetAwaiter().GetResult();
        void Wait(Task t) => t.GetAwaiter().GetResult();

        var user = new JsObject(engine);
        user.Set("id", actor.UserId);
        user.Set("canWrite", actor.CanWrite);
        user.Set("canDesign", actor.CanDesign);
        o.Set("user", user);
        if (table is not null) o.Set("table", table);

        Fn("get", a => Run(tables.GetAsync(ctx, Str(At(a, 0), "table"), Str(At(a, 1), "id"))) is { } row ? TriggerSandbox.ToJs(engine, row) : JsValue.Null);
        Fn("query", a => Json(Run(tables.QueryAsync(ctx, Str(At(a, 0), "table"), AppJsonParsers.ParseQuerySpec(Arg(At(a, 1)))))));
        Fn("count", a =>
        {
            var where = At(a, 1);
            var spec = where.IsUndefined() || where.IsNull() ? "{}" : $"{{\"where\":{TriggerSandbox.ToJson(where).GetRawText()}}}";
            using var doc = JsonDocument.Parse(spec);
            return Run(tables.CountAsync(ctx, Str(At(a, 0), "table"), AppJsonParsers.ParseQuerySpec(doc.RootElement)));
        });
        if (writes)
        {
            Fn("insert", a => TriggerSandbox.ToJs(engine, Run(tables.InsertAsync(ctx, actor, Str(At(a, 0), "table"), Arg(At(a, 1))))));
            Fn("update", a => TriggerSandbox.ToJs(engine, Run(tables.UpdateAsync(ctx, actor, Str(At(a, 0), "table"), Str(At(a, 1), "id"), Arg(At(a, 2)), null))));
            // A refusal (not_your_row, still_linked, a nested trigger's…) reaches
            // the script's caller like every other ctx call's.
            Fn("remove", a => { Wait(tables.DeleteAsync(ctx, actor, Str(At(a, 0), "table"), Str(At(a, 1), "id"))); return JsValue.Undefined; });
        }
        Fn("log", a => { log(At(a, 0).ToString()); return JsValue.Undefined; });
        return o;
    }
}
