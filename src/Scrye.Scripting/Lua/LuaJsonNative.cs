using System.Globalization;
using System.Text;
using System.Text.Json;
using KeraLua;
using NativeLua = KeraLua.Lua;

namespace Scrye.Scripting.Lua;

/// <summary>
/// The <c>scrye.json</c> codec for the native-Lua runtime (plugin API 1.6) — the
/// stack-based twin of <c>LuaJson</c> (MoonSharp), with the same stated shape rules:
/// a table whose keys are exactly the integers 1..n encodes as a JSON array; any other
/// table encodes as an object with string keys; an EMPTY table encodes as <c>{}</c>.
/// Decoding: objects → tables, arrays → 1-based lists. Functions, userdata and coroutines
/// are not data; encoding one throws (the binding turns that into <c>nil, err</c>).
/// Depth is capped so a cyclic table errors as "too deep" instead of overflowing.
///
/// <para>Lua 5.4's integer subtype makes the number rules crisper than MoonSharp's
/// integral-double dance, with the same wire result: integers (and integral doubles up to
/// 2^53) write without a decimal point, so <c>"42"</c> round-trips as <c>"42"</c> — room
/// coordinates as keys depend on this. Decode pushes whole JSON numbers as Lua
/// <b>integers</b>: MoonSharp had only doubles, but on 5.4 an integer is what a plugin
/// author expects to feed <c>string.format("%d", …)</c>, and <c>42 == 42.0</c> holds
/// either way.</para>
/// </summary>
internal static class LuaJsonNative
{
    private const int MaxDepth = 64;

    /// <summary>Encode the value at <paramref name="index"/> (absolute) as JSON text.
    /// Throws <see cref="InvalidOperationException"/> with an author-readable message on
    /// unencodable values. Leaves the stack as it found it.</summary>
    public static string Encode(NativeLua l, int index)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            WriteValue(writer, l, index, 0);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteValue(Utf8JsonWriter w, NativeLua l, int index, int depth)
    {
        if (depth > MaxDepth)
            throw new InvalidOperationException($"json.encode: nesting deeper than {MaxDepth} (cyclic table?)");

        switch (l.Type(index))
        {
            case LuaType.Nil:
            case LuaType.None:
                w.WriteNullValue();
                break;
            case LuaType.Boolean:
                w.WriteBooleanValue(l.ToBoolean(index));
                break;
            case LuaType.Number:
                if (l.IsInteger(index))
                {
                    w.WriteNumberValue(l.ToInteger(index));
                }
                else
                {
                    double d = l.ToNumber(index);
                    if (!double.IsFinite(d))
                        throw new InvalidOperationException("json.encode: NaN/Infinity is not valid JSON");
                    // Integral doubles write without a decimal point (MoonSharp parity).
                    if (Math.Floor(d) == d && Math.Abs(d) <= 9007199254740992d)
                        w.WriteNumberValue((long)d);
                    else
                        w.WriteNumberValue(d);
                }
                break;
            case LuaType.String:
                w.WriteStringValue(l.ToString(index, false));
                break;
            case LuaType.Table:
                WriteTable(w, l, index, depth);
                break;
            default:
                throw new InvalidOperationException(
                    $"json.encode: cannot encode a {TypeName(l.Type(index))}");
        }
    }

    private static void WriteTable(Utf8JsonWriter w, NativeLua l, int index, int depth)
    {
        l.CheckStack(6, "json.encode");

        // Array iff the key set is exactly {1..n}. Lua 5.4 normalizes integral float keys
        // (t[1.0] IS t[1]), so "key is an integer" is the whole test. Empty is an object
        // (see class remarks / LuaJson).
        long count = 0;
        bool allIntKeys = true;
        l.PushNil();
        while (l.Next(index))
        {
            count++;
            if (!(l.Type(-2) == LuaType.Number && l.IsInteger(-2))) allIntKeys = false;
            l.Pop(1);   // drop value, keep key for next
        }

        bool isArray = count > 0 && allIntKeys && l.RawLen(index) == count;
        if (isArray)
        {
            w.WriteStartArray();
            for (long i = 1; i <= count; i++)
            {
                l.RawGetInteger(index, i);
                WriteValue(w, l, l.GetTop(), depth + 1);
                l.Pop(1);
            }
            w.WriteEndArray();
            return;
        }

        w.WriteStartObject();
        l.PushNil();
        while (l.Next(index))
        {
            int keyIdx = l.GetTop() - 1;
            string key = l.Type(keyIdx) switch
            {
                LuaType.String => l.ToString(keyIdx, false),
                // Never lua_tostring a live iteration key — read the number out instead.
                LuaType.Number => l.IsInteger(keyIdx)
                    ? l.ToInteger(keyIdx).ToString(CultureInfo.InvariantCulture)
                    : l.ToNumber(keyIdx).ToString(CultureInfo.InvariantCulture),
                _ => throw new InvalidOperationException(
                    $"json.encode: table key of type {TypeName(l.Type(keyIdx))} cannot be a JSON object key"),
            };
            w.WritePropertyName(key);
            WriteValue(w, l, l.GetTop(), depth + 1);
            l.Pop(1);   // drop value, keep key
        }
        w.WriteEndObject();
    }

    private static string TypeName(LuaType t) => t.ToString().ToLowerInvariant();

    /// <summary>Decode JSON text and push the resulting Lua value. Throws
    /// <see cref="JsonException"/> on malformed input (the binding turns that into
    /// <c>nil, err</c>). Same permissiveness as <c>scrye.data</c> files: comments and
    /// trailing commas are tolerated.</summary>
    public static void Decode(NativeLua l, string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        PushElement(l, doc.RootElement);
    }

    private static void PushElement(NativeLua l, JsonElement e)
    {
        l.CheckStack(4, "json.decode");
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                l.NewTable();
                foreach (JsonProperty p in e.EnumerateObject())
                {
                    PushElement(l, p.Value);
                    l.SetField(-2, p.Name);
                }
                break;
            case JsonValueKind.Array:
            {
                l.NewTable();
                long i = 1;
                foreach (JsonElement item in e.EnumerateArray())
                {
                    PushElement(l, item);
                    l.RawSetInteger(-2, i++);
                }
                break;
            }
            case JsonValueKind.String:
                l.PushString(e.GetString() ?? "");
                break;
            case JsonValueKind.Number:
                if (e.TryGetInt64(out long n)) l.PushInteger(n);
                else l.PushNumber(e.GetDouble());
                break;
            case JsonValueKind.True:
                l.PushBoolean(true);
                break;
            case JsonValueKind.False:
                l.PushBoolean(false);
                break;
            default:
                l.PushNil();   // null / undefined
                break;
        }
    }
}
