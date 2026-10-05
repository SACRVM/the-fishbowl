using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fishbowl.Data.Tables;
using Jint;
using Jint.Native;
using Jint.Runtime;

namespace Fishbowl.Data.Tests.Tables;

// The box trigger scripts run in: what Jint's statement-by-statement limits
// don't cover — deep values crossing into .NET, a single built-in building a
// huge string or list, a runaway regex — is bounded, and the capped
// built-ins behave like Jint's own. Nothing here can take the test host
// down if a guard regresses: the deep value is 100 levels (harmless for a
// recursive walk too), the big strings stay in the tens of MB.
public class TriggerSandboxTests
{
    private static JsValue Eval(Engine engine, string code) => TriggerSandbox.OnOwnStack(() => engine.Evaluate(code));

    [Fact]
    public void ToJson_RefusesAValueNestedDeeperThanTheCap()
    {
        var engine = TriggerSandbox.NewEngine();
        var deep = Eval(engine, "(() => { let o = {}; for (let i = 0; i < 100; i++) o = { a: o }; return o; })()");
        var ex = Assert.Throws<TriggerValueException>(() => TriggerSandbox.ToJson(deep));
        Assert.Contains($"{TriggerSandbox.MaxValueDepth} levels", ex.Message);

        var fits = Eval(engine, $"(() => {{ let o = 1; for (let i = 0; i < {TriggerSandbox.MaxValueDepth}; i++) o = [o]; return o; }})()");
        Assert.Equal(new string('[', TriggerSandbox.MaxValueDepth) + "1" + new string(']', TriggerSandbox.MaxValueDepth),
            TriggerSandbox.ToJson(fits).GetRawText());
    }

    [Fact]
    public void ToJson_WritesPlainData_AsJsonStringifyWould()
    {
        var engine = TriggerSandbox.NewEngine();
        var value = Eval(engine, """
            ({ title: 'T', n: 1.5, big: 1e21, nan: NaN, yes: true, none: null, skip: undefined, f() {},
               list: [1, undefined, () => 1, 'x'], when: new Date(Date.UTC(2026, 9, 4, 12, 30)), nested: { 2: 'b', 1: 'a' } })
            """);
        var json = TriggerSandbox.ToJson(value);
        Assert.Equal("T", json.GetProperty("title").GetString());
        Assert.Equal(1.5, json.GetProperty("n").GetDouble());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("nan").ValueKind);
        Assert.False(json.TryGetProperty("skip", out _));
        Assert.False(json.TryGetProperty("f", out _));
        Assert.Equal("[1,null,null,\"x\"]", json.GetProperty("list").GetRawText());
        Assert.Equal("2026-10-04T12:30:00.000Z", json.GetProperty("when").GetString());
        Assert.Equal("{\"1\":\"a\",\"2\":\"b\"}", json.GetProperty("nested").GetRawText());
        Assert.Throws<TriggerValueException>(() => TriggerSandbox.ToJson(Eval(engine, "({ n: 1n })")));
    }

    [Fact]
    public void KeepNumbers_KeepsWhatTheScriptLeftExactly()
    {
        using var before = JsonDocument.Parse("""{ "big": 9007199254740993, "n": 1, "deep": { "k": 12345678901234567890, "l": [ 9007199254740995 ] } }""");
        // As JavaScript hands them back: doubles.
        using var after = JsonDocument.Parse("""{ "big": 9007199254740992, "n": 2, "deep": { "k": 12345678901234567000, "l": [ 9007199254740996 ] }, "x": 3 }""");
        var kept = TriggerSandbox.KeepNumbers(before.RootElement, after.RootElement);
        Assert.Equal("9007199254740993", kept.GetProperty("big").GetRawText());
        Assert.Equal(2, kept.GetProperty("n").GetInt32());   // changed by the script
        Assert.Equal("12345678901234567890", kept.GetProperty("deep").GetProperty("k").GetRawText());
        Assert.Equal("[9007199254740995]", kept.GetProperty("deep").GetProperty("l").GetRawText());
        Assert.Equal(3, kept.GetProperty("x").GetInt32());
    }

    [Fact]
    public void Engine_GuardsTheStack_AndHasNoEvalOrRawMemory()
    {
        var options = new Options();
        TriggerSandbox.Configure(options);
        Assert.True(options.Constraints.StackOverflowGuard);
        Assert.Equal(TriggerSandbox.TimeLimit, options.Constraints.RegexTimeout);
        Assert.Equal(TriggerSandbox.MaxArraySize, options.Constraints.MaxArraySize);
        var engine = TriggerSandbox.NewEngine();
        Assert.Equal("undefined|undefined|undefined", Eval(engine, "typeof ArrayBuffer + '|' + typeof Uint8Array + '|' + typeof SharedArrayBuffer").AsString());
        Assert.Throws<JavaScriptException>(() => Eval(engine, "eval('1')"));
        Assert.Throws<JavaScriptException>(() => Eval(engine, "new Function('return 1')()"));
    }

    [Theory]
    [InlineData("'x'.repeat(2e7)")]
    [InlineData("'x'.padStart(2e7)")]
    [InlineData("'x'.padEnd(2e7, 'ab')")]
    [InlineData("Array(100).fill('x'.repeat(2e5)).join('')")]
    [InlineData("Array(100).join('x'.repeat(2e5))")]
    [InlineData("String(Array(100).fill('x'.repeat(2e5)))")]
    [InlineData("Array(100).fill('x'.repeat(2e5)).toLocaleString()")]
    [InlineData("'x'.concat(...Array(100).fill('x'.repeat(2e5)))")]
    [InlineData("'x'.repeat(2e5).replaceAll('', 'yyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyy')")]
    [InlineData("'x'.repeat(2e5).replace(/x/g, '$&$&$&$&$&$&$&$&$&$&$&$&$&$&$&$&$&$&$&$&$&$&$&$&$&$&$&$&$&$&$&$&$&$&$&$&$&$&$&$&$&$&$&$&$&$&$&$&$&$&')")]
    [InlineData("'x'.repeat(1000).replace(/x/g, () => 'y'.repeat(2e5))")]
    [InlineData("/x/g[Symbol.replace]('x'.repeat(1000), 'y'.repeat(2e5))")]
    [InlineData("String.raw({ raw: Array(100).fill('x'.repeat(2e5)) })")]
    [InlineData("JSON.stringify(Array(100).fill('x'.repeat(2e5)))")]
    [InlineData("new Array(2e5).length")]
    public void BuiltIns_ThatBuildBigStringsOrLists_StopAtTheCap(string expression)
    {
        var engine = TriggerSandbox.NewEngine();
        var ex = Assert.Throws<MemoryLimitExceededException>(() => Eval(engine, expression + ".length"));
        Assert.NotNull(ex.Message);
    }

    [Fact]
    public void ARunawayRegex_StopsAtTheTimeLimit()
    {
        var engine = TriggerSandbox.NewEngine();
        var watch = Stopwatch.StartNew();
        Assert.Throws<RegexMatchTimeoutException>(() => Eval(engine, "/^(a+)+$/.test('a'.repeat(40) + 'b')"));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }

    // The capped built-ins are the same functions to a script: same results,
    // same errors as Jint's own.
    [Theory]
    [InlineData("'ab'.repeat(3)")]
    [InlineData("'a'.repeat(-1)")]
    [InlineData("String.prototype.repeat.call(null, 2)")]
    [InlineData("'abc'.padStart(6, '12') + 'abc'.padEnd(2)")]
    [InlineData("'a'.concat(1, null, undefined, {})")]
    [InlineData("'aXbXc'.replaceAll('X', '$&$&') + 'abc'.replace('b', '$`$\\'$$$0')")]
    [InlineData("'abcdefghijkl'.replace(/(a)(b)(c)(d)(e)(f)(g)(h)(i)(j)(k)/, '$11-$10-$1-$01-$00')")]
    [InlineData("'abc'.replace(/(?<x>b)/, '[$<x>][$<y>]') + 'abc'.replace(/b/, '[$<x>]')")]
    [InlineData("'aaa'.replace(/a/g, (m, p) => p) + 'abc'.replaceAll('', '-')")]
    [InlineData("'abc'.replaceAll(/b/, 'X')")]
    [InlineData("String.raw`a${1}b${2}c` + String.raw({ raw: 'abc' }, '-', '+')")]
    [InlineData("[1, [2, [3]], null, undefined].join('-') + String([1, 2]) + `${[3]}`")]
    [InlineData("(() => { const a = [1]; a.push(a); return String(a); })()")]
    [InlineData("JSON.stringify({ a: [1, 'x', null, undefined, () => 1], b: undefined, c: new Date(0), d: { toJSON(k) { return 'key:' + k; } } })")]
    [InlineData("JSON.stringify({ a: { b: [] }, c: {} }, null, 2) + JSON.stringify([1, [2]], null, '--')")]
    [InlineData("JSON.stringify({ a: 1, b: 2, 1: 3 }, ['b', 1, new String('a')]) + JSON.stringify({ a: 1 }, (k, v) => typeof v === 'number' ? v * 2 : v)")]
    [InlineData("JSON.stringify('\\ud800\\udc00\\ud800 é\\n\\u0001') + JSON.stringify([new Number(1), Object('x'), new Boolean(false)])")]
    [InlineData("JSON.stringify(new Proxy([1, 2], {})) + JSON.stringify(JSON.rawJSON('1e1000'))")]
    [InlineData("JSON.stringify(1n)")]
    [InlineData("(() => { const a = {}; a.a = a; return JSON.stringify(a); })()")]
    [InlineData("String(JSON.stringify(undefined)) + JSON.stringify(NaN) + JSON.stringify(-0)")]
    public void CappedBuiltIns_BehaveLikeJints(string expression)
    {
        var code = $"(() => {{ try {{ return String({expression}); }} catch (e) {{ return 'ERR ' + e.name + ': ' + e.message; }} }})()";
        var plain = new Engine(o => o.Strict()).Evaluate(code).AsString();
        Assert.Equal(plain, Eval(TriggerSandbox.NewEngine(), code).AsString());
    }

    [Fact]
    public void OnOwnStack_RethrowsTheOriginalException()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => TriggerSandbox.OnOwnStack<int>(() => throw new InvalidOperationException("inner")));
        Assert.Equal("inner", ex.Message);
    }
}
