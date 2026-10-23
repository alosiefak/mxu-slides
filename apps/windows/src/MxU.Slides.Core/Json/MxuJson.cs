using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MxU.Slides.Core.Json;

/// <summary>
/// The one JSON configuration every document goes through. Optional fields
/// that are null are omitted, matching how the Mac app encodes its Swift
/// models, so a document written here reads back identically there.
/// </summary>
public static class MxuJson
{
    public static readonly JsonSerializerOptions Options = Create(indented: true);

    public static readonly JsonSerializerOptions Compact = Create(indented: false);

    private static JsonSerializerOptions Create(bool indented) => new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = indented,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string Serialize<T>(T value, bool indented = true) =>
        JsonSerializer.Serialize(value, indented ? Options : Compact);

    public static byte[] SerializeToUtf8<T>(T value, bool indented = true) =>
        JsonSerializer.SerializeToUtf8Bytes(value, indented ? Options : Compact);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new JsonException($"Empty {typeof(T).Name} document");

    public static T Deserialize<T>(ReadOnlySpan<byte> utf8) =>
        JsonSerializer.Deserialize<T>(utf8, Options)
        ?? throw new JsonException($"Empty {typeof(T).Name} document");

    /// <summary>Deep copy through JSON, so the copy shares no lists or nested records.</summary>
    public static T Clone<T>(T value) => Deserialize<T>(SerializeToUtf8(value, indented: false));

    /// <summary>Structural equality: two values are equal when they encode to the same JSON.</summary>
    public static bool StructurallyEqual<T>(T? a, T? b)
    {
        if (a is null || b is null) return a is null && b is null;
        return SerializeToUtf8(a, indented: false).AsSpan().SequenceEqual(SerializeToUtf8(b, indented: false));
    }
}
