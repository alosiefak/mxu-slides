using System.Text.Json;
using System.Text.Json.Serialization;

namespace MxU.Slides.Core.Outputs;

// JSON converters that make the output types encode exactly as the Mac's
// Swift Codable does, whichever JsonSerializerOptions the caller uses (MxuJson,
// or the WPF app's own settings options). They are attached with attributes on
// the types and properties, so nothing has to be registered.

/// <summary>Swift's UUID encoding: the uppercase "D" form ("E621E1F8-C36C-495A-…"). Reads either case.</summary>
public static class SwiftUuid
{
    public static string Format(Guid id) => id.ToString("D").ToUpperInvariant();

    public static Guid? Parse(string? text) => Guid.TryParse(text, out var id) ? id : null;
}

public sealed class SwiftUuidJsonConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        SwiftUuid.Parse(reader.GetString()) ?? throw new JsonException($"Not a UUID: {reader.GetString()}");

    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) =>
        writer.WriteStringValue(SwiftUuid.Format(value));
}

public sealed class SwiftUuidListJsonConverter : JsonConverter<IReadOnlyList<Guid>>
{
    public override IReadOnlyList<Guid> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Expected an array of UUIDs");
        var ids = new List<Guid>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            ids.Add(SwiftUuid.Parse(reader.GetString()) ?? throw new JsonException($"Not a UUID: {reader.GetString()}"));
        return ids;
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<Guid> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var id in value) writer.WriteStringValue(SwiftUuid.Format(id));
        writer.WriteEndArray();
    }
}

/// <summary>CoreGraphics encodes a CGPoint as a two-number array, <c>[x, y]</c>, not an object.</summary>
public sealed class OutputPointJsonConverter : JsonConverter<OutputPoint>
{
    public override OutputPoint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Expected [x, y]");
        reader.Read();
        var x = reader.GetDouble();
        reader.Read();
        var y = reader.GetDouble();
        if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray) throw new JsonException("Expected [x, y]");
        return new OutputPoint(x, y);
    }

    public override void Write(Utf8JsonWriter writer, OutputPoint value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        writer.WriteEndArray();
    }
}

/// <summary>A mask's mode as the Mac writes it: "out" or "in".</summary>
public sealed class OutputMaskModeJsonConverter : JsonConverter<OutputMaskMode>
{
    public override OutputMaskMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString() switch
        {
            "out" => OutputMaskMode.Out,
            "in" => OutputMaskMode.In,
            var raw => throw new JsonException($"Unknown mask mode: {raw}"),
        };

    public override void Write(Utf8JsonWriter writer, OutputMaskMode value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value == OutputMaskMode.In ? "in" : "out");
}
