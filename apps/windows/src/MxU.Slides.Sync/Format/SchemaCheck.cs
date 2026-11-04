using System.Text.Json;
using System.Text.Json.Nodes;

namespace MxU.Slides.Sync.Format;

/// <summary>A document Windows was about to write breaks the shared schema, so it wasn't written.</summary>
public sealed class SchemaViolationException(string recordName, IReadOnlyList<string> problems)
    : Exception($"A {recordName} that the Mac couldn't read: {string.Join("; ", problems.Take(5))}")
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

/// <summary>
/// Checks a document against the shared schema before Windows writes it into the Mac's library:
/// every field is one the schema names, with the right kind of value; every required field is
/// there; every enum value is one of its cases. A document that fails is never written: the Mac's
/// decoder would refuse the whole document, and the church would lose it on the Mac.
/// </summary>
public static class SchemaCheck
{
    public static IReadOnlyList<string> Problems(JsonObject value, string recordName, MacSchema? schema = null)
    {
        schema ??= MacSchema.Shared;
        var problems = new List<string>();
        CheckRecord(value, schema.Record(recordName), "", schema, problems);
        return problems;
    }

    /// <summary>Throws <see cref="SchemaViolationException"/> unless <paramref name="value"/> fits the schema.</summary>
    public static void Require(JsonObject value, string recordName, MacSchema? schema = null)
    {
        var problems = Problems(value, recordName, schema);
        if (problems.Count > 0) throw new SchemaViolationException(recordName, problems);
    }

    private static string At(string path, string key) => path.Length == 0 ? key : $"{path}.{key}";

    private static void CheckRecord(JsonNode? node, MacRecord record, string path, MacSchema schema, List<string> problems)
    {
        if (node is not JsonObject obj)
        {
            problems.Add($"{(path.Length == 0 ? "the document" : path)} should be a {record.Name} object");
            return;
        }
        foreach (var (key, _) in obj)
            if (record.Field(key) is null) problems.Add($"{At(path, key)} isn't a field the Mac knows");
        foreach (var field in record.Fields)
        {
            var present = obj.TryGetPropertyValue(field.Name, out var value) && value is not null;
            if (!present)
            {
                if (!field.Optional) problems.Add($"{At(path, field.Name)} is missing");
                continue;
            }
            CheckValue(value, field.Type, At(path, field.Name), schema, problems);
        }
    }

    private static void CheckValue(JsonNode? node, MacType type, string path, MacSchema schema, List<string> problems)
    {
        switch (type)
        {
            case MacPrimitive primitive:
                if (!JsonRead.Fits(node, primitive.Kind)) problems.Add($"{path} should be a {primitive.Kind.ToString().ToLowerInvariant()}");
                break;
            case MacListOf list:
                if (node is not JsonArray array)
                {
                    problems.Add($"{path} should be a list");
                    break;
                }
                for (var i = 0; i < array.Count; i++) CheckValue(array[i], list.Element, $"{path}[{i}]", schema, problems);
                break;
            case MacNamed named when schema.Enums.TryGetValue(named.Name, out var enumType):
                if (JsonRead.String(node) is not { } raw) problems.Add($"{path} should be a {enumType.Name}");
                // Only a current case: a legacy alias is read by the Mac, but never written by it.
                else if (!enumType.IsCase(raw)) problems.Add($"{path} is \"{raw}\", which isn't a {enumType.Name} the Mac knows");
                break;
            case MacNamed named:
                CheckRecord(node, schema.Record(named.Name), path, schema, problems);
                break;
        }
    }
}

/// <summary>Reads JSON values the same way whether they came from a file or were made in code.</summary>
internal static class JsonRead
{
    public static JsonElement Element(JsonNode node) => node.Deserialize<JsonElement>();

    public static string? String(JsonNode? node) =>
        node is JsonValue && Element(node) is { ValueKind: JsonValueKind.String } element ? element.GetString() : null;

    public static bool Fits(JsonNode? node, MacPrimitiveKind kind)
    {
        if (node is not JsonValue) return false;
        var element = Element(node);
        return kind switch
        {
            MacPrimitiveKind.String => element.ValueKind == JsonValueKind.String,
            MacPrimitiveKind.Bool => element.ValueKind is JsonValueKind.True or JsonValueKind.False,
            MacPrimitiveKind.Int => element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out _),
            MacPrimitiveKind.Double => element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var d) && double.IsFinite(d),
            _ => false,
        };
    }

    public static long Integer(JsonNode node) => Element(node).GetInt64();

    public static double Double(JsonNode node) => Element(node).GetDouble();

    public static bool Bool(JsonNode node) => Element(node).GetBoolean();
}
