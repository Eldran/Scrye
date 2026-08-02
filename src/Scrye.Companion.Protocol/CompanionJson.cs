using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scrye.Companion.Protocol;

/// <summary>
/// The one JSON configuration both ends use. Kept here rather than at each call site so the
/// desktop server and every client cannot drift on casing or enum form.
/// </summary>
public static class CompanionJson
{
    /// <summary>camelCase properties, enums as strings, nulls omitted.
    ///
    /// <para>Enums go over the wire as names, not numbers: the client is JavaScript, and
    /// <c>"permissionDenied"</c> survives a protocol version skew in a way that <c>1</c>
    /// does not. Nulls are omitted because <see cref="OutputSpanDto.Link"/> is null on the
    /// overwhelming majority of spans and repeating <c>"link":null</c> across a combat
    /// burst is pure waste (§3.1).</para></summary>
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return o;
    }

    public static string Serialize<T>(T message) => JsonSerializer.Serialize(message, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>Read only the <c>type</c> discriminator, so a receiver can dispatch to the
    /// right concrete type without deserializing twice. Returns null when the payload is not
    /// an object or carries no type.</summary>
    public static string? PeekType(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return doc.RootElement.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
