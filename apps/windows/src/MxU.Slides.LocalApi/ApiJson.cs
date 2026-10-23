using System.Buffers;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace MxU.Slides.LocalApi;

/// <summary>
/// The API's JSON, encoded like the Mac's APIJSON encoder: sorted keys, ISO 8601
/// dates to the second, null optionals left out. Free-form JSON (JSONValue.swift on
/// the Mac) is a <see cref="JsonNode"/>; <see cref="Encoded"/> writes it with sorted keys.
/// </summary>
public static class ApiJson
{
    /// <summary>
    /// Also decodes request bodies. Like Swift's decoder, an optional field may be absent, a
    /// field marked [JsonRequired] (non-optional on the Mac) may not, and null is refused for
    /// any non-nullable one.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        RespectNullableAnnotations = true,
        Converters = { new Iso8601DateConverter(), new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { SortProperties } },
    };

    /// <summary>The value as JSON, or <c>{}</c> when it can't be encoded (a NaN, say), as the Mac falls back.</summary>
    public static byte[] Encode<T>(T value)
    {
        try
        {
            return JsonSerializer.SerializeToUtf8Bytes(value, Options);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return "{}"u8.ToArray();
        }
    }

    /// <summary>A schema reference into the OpenAPI components: <c>{"$ref":"#/components/schemas/Name"}</c>.</summary>
    public static JsonObject Ref(string name) => new() { ["$ref"] = $"#/components/schemas/{name}" };

    public static byte[] Encoded(JsonNode? node)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            WriteSorted(writer, node);
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteSorted(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var (key, value) in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(key);
                    WriteSorted(writer, value);
                }
                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var item in array) WriteSorted(writer, item);
                writer.WriteEndArray();
                break;
            default:
                node.WriteTo(writer);
                break;
        }
    }

    private static void SortProperties(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object || info.Properties.Count < 2) return;
        var sorted = info.Properties.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
        info.Properties.Clear();
        foreach (var property in sorted) info.Properties.Add(property);
    }

    /// <summary>Swift's <c>.iso8601</c> strategy: UTC, whole seconds, e.g. 1970-01-01T00:00:00Z.</summary>
    private sealed class Iso8601DateConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            DateTimeOffset.Parse(reader.GetString() ?? "", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
    }
}
