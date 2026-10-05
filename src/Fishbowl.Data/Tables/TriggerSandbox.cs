using System.Buffers;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Jint;
using Jint.Native;
using Jint.Native.Json;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interop;

namespace Fishbowl.Data.Tables;

// The box a trigger script runs in. Jint's own limits — time, statements,
// memory, recursion — are checked between statements; what one built-in
// call does on its own is bounded here: the .NET stack (Jint's stack guard,
// on a thread of its own with room to spare), regular expressions (the same
// time limit), arrays, the strings a call builds (repeat, padStart, padEnd,
// join, concat, replace, String.raw, JSON.stringify), and no typed arrays,
// Intl.ListFormat, eval or Function(). Values cross into .NET without
// recursion, at most MaxValueDepth deep. What stays open: one expression
// that joins a large string many times over (a template or + chain with
// hundreds of terms) builds it before the next memory check — a hard
// bound for that needs the script out of process.
public static class TriggerSandbox
{
    public static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(1);
    public const int StatementLimit = 50_000;
    public const long MemoryLimit = 32L * 1024 * 1024;
    public const int RecursionLimit = 64;
    public const uint MaxArraySize = 100_000;
    // What one call may build: half of what a script may allocate.
    public const int MaxStringLength = (int)(MemoryLimit / sizeof(char) / 2);
    public const int MaxValueDepth = 64;
    public const int StackSize = 16 * 1024 * 1024;

    // Raw memory a trigger has no use for, and no size limit of Jint's covers.
    private static readonly string[] Removed =
    {
        "ArrayBuffer", "SharedArrayBuffer", "DataView", "Atomics",
        "Int8Array", "Uint8Array", "Uint8ClampedArray", "Int16Array", "Uint16Array", "Int32Array", "Uint32Array",
        "Float16Array", "Float32Array", "Float64Array", "BigInt64Array", "BigUint64Array",
    };

    public static Engine NewEngine()
    {
        var engine = new Engine(Configure);
        foreach (var name in Removed) engine.Global.Delete(name);
        CapStrings(engine);
        return engine;
    }

    public static void Configure(Options o)
    {
        o.Strict()
            .TimeoutInterval(TimeLimit)
            .MaxStatements(StatementLimit)
            .LimitMemory(MemoryLimit)
            .LimitRecursion(RecursionLimit)
            .RegexTimeoutInterval(TimeLimit)
            .MaxArraySize(MaxArraySize)
            .MaxJsonParseDepth(MaxValueDepth)
            .DisableStringCompilation();
        // Deep recursion inside a built-in (JSON.stringify, join, flat, a
        // chain of proxies…) is a RangeError, not a crashed process.
        o.Constraints.StackOverflowGuard = true;
    }

    // Runs `body` on a thread of its own with a generous stack: the stack
    // guard keeps headroom on any thread, this keeps it far from the end.
    public static void OnOwnStack(Action body) => OnOwnStack(() => { body(); return 0; });

    public static T OnOwnStack<T>(Func<T> body)
    {
        T result = default!;
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try { result = body(); }
            catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
        }, StackSize)
        { IsBackground = true, Name = "Fishbowl trigger" };
        thread.Start();
        thread.Join();
        error?.Throw();
        return result;
    }

    // ------------------------------------------------------------ values --

    public static JsValue ToJs(Engine engine, JsonElement value) => new JsonParser(engine).Parse(value.GetRawText());

    // A value a script made, as JSON — plain data: objects, arrays, text,
    // numbers, true/false/null; a Date becomes its ISO text; undefined,
    // functions and symbols are left out like JSON.stringify leaves them.
    // Walked with a stack of its own, never recursively.
    public static JsonElement ToJson(JsValue value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            MaxDepth = MaxValueDepth + 1,
        }))
        {
            var stack = new Stack<Frame>();
            Write(w, stack, Left(value) ? JsValue.Null : value);
            while (stack.Count > 0)
            {
                var f = stack.Peek();
                if (f.Next >= f.Count)
                {
                    if (f.Keys is null) w.WriteEndArray(); else w.WriteEndObject();
                    stack.Pop();
                    continue;
                }
                var i = f.Next++;
                if (f.Keys is null)
                {
                    var item = f.Object.Get(i);
                    Write(w, stack, Left(item) ? JsValue.Null : item);
                }
                else
                {
                    var key = f.Keys[i];
                    var own = f.Object.GetOwnProperty(key);
                    if (own == PropertyDescriptor.Undefined || !own.Enumerable) continue;
                    var item = f.Object.Get(key);
                    if (Left(item)) continue;
                    w.WritePropertyName(key.ToString());
                    Write(w, stack, item);
                }
                if (w.BytesPending + w.BytesCommitted > MaxStringLength)
                    throw new TriggerValueException($"it built a value larger than {MaxStringLength / (1024 * 1024)} MB");
            }
        }
        using var doc = JsonDocument.Parse(buffer.WrittenMemory, new JsonDocumentOptions { MaxDepth = MaxValueDepth + 1 });
        return doc.RootElement.Clone();
    }

    private sealed class Frame(ObjectInstance o, List<JsValue>? keys, int count)
    {
        public ObjectInstance Object { get; } = o;
        public List<JsValue>? Keys { get; } = keys;   // null: an array
        public int Count { get; } = count;
        public int Next { get; set; }
    }

    private static readonly double MinDateMs = DateTimeOffset.MinValue.ToUnixTimeMilliseconds();
    private static readonly double MaxDateMs = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    // What JSON.stringify leaves out of an object (and writes as null in an array).
    private static bool Left(JsValue v) => v.IsUndefined() || v.IsSymbol() || v.IsCallable();

    private static void Write(Utf8JsonWriter w, Stack<Frame> stack, JsValue v)
    {
        if (v.IsNull()) w.WriteNullValue();
        else if (v.IsBoolean()) w.WriteBooleanValue(v.AsBoolean());
        else if (v.IsNumber())
        {
            var n = v.AsNumber();
            if (double.IsFinite(n)) w.WriteNumberValue(n); else w.WriteNullValue();
        }
        else if (v.IsString()) w.WriteStringValue(v.AsString());
        else if (v.IsBigInt()) throw new TriggerValueException("a BigInt can't be stored — use a number or text");
        else if (v is JsDate date)
        {
            var ms = date.DateValue;
            if (double.IsNaN(ms)) w.WriteNullValue();
            else if (ms < MinDateMs || ms > MaxDateMs) throw new TriggerValueException("a date outside the years 1 to 9999 can't be stored");
            else w.WriteStringValue(DateTimeOffset.FromUnixTimeMilliseconds((long)ms).UtcDateTime
                .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        }
        else if (v is ObjectInstance o)
        {
            if (stack.Count >= MaxValueDepth)
                throw new TriggerValueException($"it built a value nested more than {MaxValueDepth} levels deep");
            if (o.IsArray())
            {
                var length = TypeConverter.ToLength(o.Get("length"));
                if (length > MaxArraySize)
                    throw new TriggerValueException($"it built a list of more than {MaxArraySize} items");
                w.WriteStartArray();
                stack.Push(new Frame(o, null, (int)length));
            }
            else
            {
                var keys = o.GetOwnPropertyKeys(Types.String);
                w.WriteStartObject();
                stack.Push(new Frame(o, keys, keys.Count));
            }
        }
        else w.WriteNullValue();
    }

    // Numbers a script left as they were stay exactly as they came: a whole
    // number beyond 2^53 survives a trip through JavaScript's doubles only
    // this way. Both sides are parsed JSON, at most MaxValueDepth deep.
    public static JsonElement KeepNumbers(JsonElement before, JsonElement after)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            MaxDepth = MaxValueDepth + 1,
        }))
            Keep(w, before, after);
        using var doc = JsonDocument.Parse(buffer.WrittenMemory, new JsonDocumentOptions { MaxDepth = MaxValueDepth + 1 });
        return doc.RootElement.Clone();
    }

    private static void Keep(Utf8JsonWriter w, JsonElement before, JsonElement after)
    {
        switch (after.ValueKind)
        {
            case JsonValueKind.Number when before.ValueKind == JsonValueKind.Number
                && before.TryGetDouble(out var b) && after.TryGetDouble(out var a) && a == b:
                w.WriteRawValue(before.GetRawText(), skipInputValidation: true);
                break;
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (var p in after.EnumerateObject())
                {
                    w.WritePropertyName(p.Name);
                    Keep(w, before.ValueKind == JsonValueKind.Object && before.TryGetProperty(p.Name, out var bp) ? bp : default, p.Value);
                }
                w.WriteEndObject();
                break;
            case JsonValueKind.Array:
                w.WriteStartArray();
                var i = 0;
                foreach (var item in after.EnumerateArray())
                {
                    Keep(w, before.ValueKind == JsonValueKind.Array && i < before.GetArrayLength() ? before[i] : default, item);
                    i++;
                }
                w.WriteEndArray();
                break;
            default:
                after.WriteTo(w);
                break;
        }
    }

    // ----------------------------------------------------------- strings --

    private static void CapStrings(Engine engine)
    {
        var stringProto = engine.Evaluate("String.prototype").AsObject();
        var arrayProto = engine.Evaluate("Array.prototype").AsObject();
        var regexpProto = engine.Evaluate("RegExp.prototype").AsObject();

        Swap(engine, stringProto, "repeat", 1, (original, self, a) =>
        {
            if (self.IsNullish()) return engine.Call(original, self, a);
            var s = TypeConverter.ToString(self);
            var n = TypeConverter.ToIntegerOrInfinity(At(a, 0));
            if (n > 0 && !double.IsInfinity(n) && s.Length * n > MaxStringLength) throw TooLong();
            return engine.Call(original, s, new JsValue[] { n });
        });
        foreach (var pad in new[] { "padStart", "padEnd" })
            Swap(engine, stringProto, pad, 1, (original, self, a) =>
            {
                if (self.IsNullish()) return engine.Call(original, self, a);
                var s = TypeConverter.ToString(self);
                var max = TypeConverter.ToLength(At(a, 0));
                if (max > (ulong)Math.Max(s.Length, MaxStringLength)) throw TooLong();
                return engine.Call(original, s, new JsValue[] { max, At(a, 1) });
            });
        Swap(engine, stringProto, "concat", 1, (original, self, a) =>
        {
            if (self.IsNullish()) return engine.Call(original, self, a);
            var sb = new StringBuilder(TypeConverter.ToString(self));
            foreach (var arg in a) Append(sb, TypeConverter.ToString(arg));
            return sb.ToString();
        });
        // A string pattern is replaced here; a RegExp hands the work to its
        // [Symbol.replace], capped below.
        foreach (var replace in new[] { "replace", "replaceAll" })
            Swap(engine, stringProto, replace, 2, (original, self, a) =>
            {
                var search = At(a, 0);
                if (self.IsNullish() || search.IsObject()) return engine.Call(original, self, a);
                var s = TypeConverter.ToString(self);
                return engine.Call(original, s, new[] { search, Replacer(engine, s.Length, At(a, 1)) });
            });
        var symbolReplace = engine.Evaluate("Symbol.replace");
        Swap(engine, regexpProto, symbolReplace, 2, (original, self, a) =>
        {
            var s = TypeConverter.ToString(At(a, 0));
            return engine.Call(original, self, new[] { (JsValue)s, Replacer(engine, s.Length, At(a, 1)) });
        });
        // ToObject, as Object(v) — null and undefined go to the original
        // built-in first, for its TypeError.
        var objectCtor = engine.Evaluate("Object");
        ObjectInstance ToObject(JsValue v) =>
            v as ObjectInstance ?? engine.Call(objectCtor, JsValue.Undefined, new[] { v }).AsObject();

        Swap(engine, engine.Evaluate("String").AsObject(), "raw", 1, (original, self, a) =>
        {
            if (At(a, 0).IsNullish()) return engine.Call(original, self, a);
            var rawValue = ToObject(At(a, 0)).Get("raw");
            if (rawValue.IsNullish()) return engine.Call(original, self, a);
            var raw = ToObject(rawValue);
            var count = Count(raw);
            var sb = new StringBuilder();
            for (ulong i = 0; i < count; i++)
            {
                Append(sb, TypeConverter.ToString(raw.Get(i)));
                if (i + 1 < count && (int)i + 1 < a.Length) Append(sb, TypeConverter.ToString(a[(int)i + 1]));
            }
            return sb.ToString();
        });

        // join is what toString and every array-to-text conversion use.
        var joining = new HashSet<ObjectInstance>(ReferenceEqualityComparer.Instance);
        JsValue Join(ObjectInstance o, string separator, Func<JsValue, string> text)
        {
            var count = Count(o);
            if (!joining.Add(o)) return "";   // a list inside itself, as browsers do
            try
            {
                var sb = new StringBuilder();
                for (ulong i = 0; i < count; i++)
                {
                    if (i > 0) Append(sb, separator);
                    var item = o.Get(i);
                    if (!item.IsNullish()) Append(sb, text(item));
                }
                return sb.ToString();
            }
            finally { joining.Remove(o); }
        }
        Swap(engine, arrayProto, "join", 1, (original, self, a) =>
        {
            if (self.IsNullish()) return engine.Call(original, self, a);
            var o = ToObject(self);
            var separator = At(a, 0);
            return Join(o, separator.IsUndefined() ? "," : TypeConverter.ToString(separator), TypeConverter.ToString);
        });
        Swap(engine, arrayProto, "toLocaleString", 0, (original, self, a) => self.IsNullish()
            ? engine.Call(original, self, a)
            : Join(ToObject(self), ",", item => TypeConverter.ToString(engine.Call(ToObject(item).Get("toLocaleString"), item, Array.Empty<JsValue>()))));

        // Jint's own serializer builds up to half a billion characters
        // before it checks; this one stops at the cap.
        var builtins = new Builtins(engine.Evaluate("Boolean.prototype.valueOf"), engine.Evaluate("BigInt.prototype.valueOf"),
            engine.Evaluate("Array.isArray"), engine.Evaluate("RangeError"));
        Swap(engine, engine.Evaluate("JSON").AsObject(), "stringify", 3, (_, _, a) =>
            new Stringifier(engine, ToObject, builtins).Run(At(a, 0), At(a, 1), At(a, 2)));

        // Intl.ListFormat joins a list like join does, uncapped.
        if (engine.Evaluate("typeof Intl === 'object' ? Intl : undefined") is ObjectInstance intl) intl.Delete("ListFormat");
    }

    // JSON.stringify by the book (ECMA-262 §25.5.2) into a capped builder.
    // Number, String, Boolean and BigInt objects and JSON.rawJSON values are
    // told apart by Jint's type names (TriggerSandboxTests pins them).
    private sealed record Builtins(JsValue BooleanValueOf, JsValue BigIntValueOf, JsValue IsArray, JsValue RangeError);

    private sealed class Stringifier(Engine engine, Func<JsValue, ObjectInstance> toObject, Builtins builtins)
    {
        private readonly StringBuilder _sb = new();
        private readonly HashSet<ObjectInstance> _stack = new(ReferenceEqualityComparer.Instance);
        private JsValue _replacer = JsValue.Undefined;
        private List<string>? _keys;
        private string _gap = "";
        private string _indent = "";

        public JsValue Run(JsValue value, JsValue replacer, JsValue space)
        {
            if (replacer is ObjectInstance r)
            {
                if (r.IsCallable()) _replacer = r;
                else if (IsArray(r))
                {
                    _keys = new List<string>();
                    var count = Count(r);
                    for (ulong k = 0; k < count; k++)
                    {
                        var v = r.Get(k);
                        var key = v.IsString() || v.IsNumber() || Kind(v) is "StringInstance" or "NumberInstance" ? TypeConverter.ToString(v) : null;
                        if (key is not null && !_keys.Contains(key)) _keys.Add(key);
                    }
                }
            }
            if (Kind(space) == "NumberInstance") space = TypeConverter.ToNumber(space);
            else if (Kind(space) == "StringInstance") space = TypeConverter.ToString(space);
            if (space.IsNumber())
            {
                var n = Math.Min(10, TypeConverter.ToIntegerOrInfinity(space));
                _gap = n < 1 ? "" : new string(' ', (int)n);
            }
            else if (space.IsString()) _gap = space.AsString() is var s && s.Length > 10 ? s[..10] : s;

            var wrapper = new JsObject(engine);
            wrapper.Set("", value);
            return Property("", wrapper) ? _sb.ToString() : JsValue.Undefined;
        }

        private static string? Kind(JsValue v) => v is ObjectInstance o ? o.GetType().Name : null;

        // IsArray sees through a proxy.
        private bool IsArray(ObjectInstance o) =>
            o.IsArray() || (Kind(o) == "JsProxy" && engine.Call(builtins.IsArray, JsValue.Undefined, new JsValue[] { o }).AsBoolean());

        // SerializeJSONProperty: false = undefined (nothing written).
        private bool Property(string key, ObjectInstance holder)
        {
            var value = holder.Get(key);
            if (value is ObjectInstance || value.IsBigInt())
            {
                var toJson = toObject(value).Get("toJSON");
                if (toJson.IsCallable()) value = engine.Call(toJson, value, new JsValue[] { key });
            }
            if (!_replacer.IsUndefined()) value = engine.Call(_replacer, holder, new JsValue[] { key, value });
            switch (Kind(value))
            {
                case "NumberInstance": value = TypeConverter.ToNumber(value); break;
                case "StringInstance": value = TypeConverter.ToString(value); break;
                case "BooleanInstance": value = engine.Call(builtins.BooleanValueOf, value, Array.Empty<JsValue>()); break;
                case "BigIntInstance": value = engine.Call(builtins.BigIntValueOf, value, Array.Empty<JsValue>()); break;
                case "JsRawJson": Append(TypeConverter.ToString(value.AsObject().Get("rawJSON"))); return true;
            }
            if (value.IsNull()) Append("null");
            else if (value.IsBoolean()) Append(value.AsBoolean() ? "true" : "false");
            else if (value.IsString()) Quote(value.AsString());
            else if (value.IsNumber()) Append(double.IsFinite(value.AsNumber()) ? TypeConverter.ToString(value) : "null");
            else if (value.IsBigInt()) throw new JavaScriptException(engine.Intrinsics.TypeError, "Do not know how to serialize a BigInt");
            else if (value is ObjectInstance o && !o.IsCallable()) Nested(o);
            else return false;
            return true;
        }

        private void Nested(ObjectInstance o)
        {
            if (!_stack.Add(o)) throw new JavaScriptException(engine.Intrinsics.TypeError, "Cyclic reference detected.");
            if (!System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
                throw new JavaScriptException(engine.Call(builtins.RangeError, JsValue.Undefined, new JsValue[] { "Maximum call stack size exceeded" }));
            var outer = _indent;
            _indent += _gap;
            if (IsArray(o))
            {
                var count = Count(o);
                Append("[");
                for (ulong i = 0; i < count; i++)
                {
                    if (i > 0) Append(",");
                    if (_gap.Length > 0) { Append("\n"); Append(_indent); }
                    if (!Property(i.ToString(CultureInfo.InvariantCulture), o)) Append("null");
                }
                if (count > 0 && _gap.Length > 0) { Append("\n"); Append(outer); }
                Append("]");
            }
            else
            {
                var keys = _keys;
                if (keys is null)
                {
                    keys = new List<string>();
                    foreach (var k in o.GetOwnPropertyKeys(Types.String))
                        if (o.GetOwnProperty(k) is var d && d != PropertyDescriptor.Undefined && d.Enumerable) keys.Add(k.ToString());
                }
                Append("{");
                var any = false;
                foreach (var key in keys)
                {
                    var mark = _sb.Length;
                    if (any) Append(",");
                    if (_gap.Length > 0) { Append("\n"); Append(_indent); }
                    Quote(key);
                    Append(_gap.Length > 0 ? ": " : ":");
                    if (Property(key, o)) any = true;
                    else _sb.Length = mark;
                }
                if (any && _gap.Length > 0) { Append("\n"); Append(outer); }
                Append("}");
            }
            _indent = outer;
            _stack.Remove(o);
        }

        // QuoteJSONString: lone surrogates and control characters as \uXXXX.
        private void Quote(string s)
        {
            Append("\"");
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                switch (c)
                {
                    case '\b': Append("\\b"); break;
                    case '\t': Append("\\t"); break;
                    case '\n': Append("\\n"); break;
                    case '\f': Append("\\f"); break;
                    case '\r': Append("\\r"); break;
                    case '"': Append("\\\""); break;
                    case '\\': Append("\\\\"); break;
                    default:
                        var lone = (char.IsHighSurrogate(c) && !(i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])))
                            || (char.IsLowSurrogate(c) && !(i > 0 && char.IsHighSurrogate(s[i - 1])));
                        if (c < 0x20 || lone) Append("\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                        {
                            if (_sb.Length >= MaxStringLength) throw TooLong();
                            _sb.Append(c);
                        }
                        break;
                }
            }
            Append("\"");
        }

        private void Append(string s) => TriggerSandbox.Append(_sb, s);
    }

    // replace's second argument as a function that counts what it adds —
    // a template string ($&, $1, $<name>…) is filled in here.
    private static JsValue Replacer(Engine engine, int length, JsValue replacement)
    {
        long total = length;
        string Counted(string s)
        {
            total += s.Length;
            if (total > MaxStringLength) throw TooLong();
            return s;
        }
        if (replacement.IsCallable())
            return new ClrFunction(engine, "", (_, a) => Counted(TypeConverter.ToString(engine.Call(replacement, JsValue.Undefined, a))), 1);
        var template = TypeConverter.ToString(replacement);
        return new ClrFunction(engine, "", (_, a) => Counted(template.Contains('$') ? Substitute(template, a) : template), 1);
    }

    // GetSubstitution (ECMA-262 §22.1.3.19.1) over replace's function
    // arguments: matched, captures…, position, string[, groups].
    private static string Substitute(string template, JsValue[] a)
    {
        var groups = a.Length > 0 && a[^1].IsObject() ? a[^1] : JsValue.Undefined;
        var captures = a.Length - (groups.IsUndefined() ? 3 : 4);
        var matched = TypeConverter.ToString(At(a, 0));
        var position = (int)Math.Min(TypeConverter.ToIntegerOrInfinity(At(a, captures + 1)), int.MaxValue);
        var str = TypeConverter.ToString(At(a, captures + 2));
        string Capture(int n) => At(a, n) is { } c && !c.IsUndefined() ? TypeConverter.ToString(c) : "";

        var sb = new StringBuilder();
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c != '$' || i + 1 >= template.Length) { sb.Append(c); continue; }
            var next = template[i + 1];
            switch (next)
            {
                case '$': sb.Append('$'); i++; break;
                case '&': sb.Append(matched); i++; break;
                case '`': sb.Append(str, 0, Math.Clamp(position, 0, str.Length)); i++; break;
                case '\'':
                    var tail = Math.Clamp(position + matched.Length, 0, str.Length);
                    sb.Append(str, tail, str.Length - tail);
                    i++;
                    break;
                case >= '0' and <= '9':
                    var one = next - '0';
                    var two = i + 2 < template.Length && char.IsAsciiDigit(template[i + 2]) ? one * 10 + (template[i + 2] - '0') : -1;
                    if (two >= 1 && two <= captures) { sb.Append(Capture(two)); i += 2; }
                    else if (one >= 1 && one <= captures) { sb.Append(Capture(one)); i++; }
                    else sb.Append('$');
                    break;
                case '<':
                    var close = groups.IsUndefined() ? -1 : template.IndexOf('>', i + 2);
                    if (close < 0) { sb.Append("$<"); i++; break; }
                    var value = groups.AsObject().Get(template[(i + 2)..close]);
                    if (!value.IsUndefined()) sb.Append(TypeConverter.ToString(value));
                    i = close;
                    break;
                default: sb.Append('$'); break;
            }
        }
        return sb.ToString();
    }

    private static ulong Count(ObjectInstance o)
    {
        var count = TypeConverter.ToLength(o.Get("length"));
        if (count > MaxArraySize) throw new MemoryLimitExceededException($"A list longer than {MaxArraySize} items can't be turned into text here.");
        return count;
    }

    private static void Append(StringBuilder sb, string s)
    {
        if ((long)sb.Length + s.Length > MaxStringLength) throw TooLong();
        sb.Append(s);
    }

    private static MemoryLimitExceededException TooLong() =>
        new($"A string longer than {MaxStringLength} characters can't be built here.");

    private static JsValue At(JsValue[] a, int i) => i >= 0 && i < a.Length ? a[i] : JsValue.Undefined;

    private static bool IsNullish(this JsValue v) => v.IsNull() || v.IsUndefined();

    private static void Swap(Engine engine, ObjectInstance target, JsValue key, int length, Func<JsValue, JsValue, JsValue[], JsValue> body)
    {
        var original = target.Get(key);
        var name = key.IsSymbol() ? "[Symbol.replace]" : key.ToString();
        target.DefineOwnProperty(key, new PropertyDescriptor(
            new ClrFunction(engine, name, (self, a) => body(original, self, a), length, PropertyFlag.Configurable),
            writable: true, enumerable: false, configurable: true));
    }
}

// A value a trigger made that can't become a row: nested too deep, too
// large, a BigInt.
public sealed class TriggerValueException(string message) : Exception(message);
