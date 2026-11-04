using System.Text.Json.Nodes;
using MxU.Slides.Sync.Automerge;

namespace MxU.Slides.Sync.Format;

/// <summary>
/// What decoding found. <see cref="Value"/> is the document as JSON in Windows' shape (the same
/// field names and values as the C# models), or null when the Mac's own decoder would refuse it:
/// then <see cref="Errors"/> says why. <see cref="UnknownKeys"/> are keys the schema doesn't name;
/// the Mac's decoder skips them and so does this (an older Mac wrote a field that was later
/// retired, or a newer Mac added one). They stay in the Automerge document untouched.
/// </summary>
public sealed record MacDecoded(JsonObject? Value, IReadOnlyList<string> Errors, IReadOnlyList<string> UnknownKeys)
{
    public bool Ok => Value is not null;
}

/// <summary>
/// Reads an Automerge document into JSON with the Mac's decoding rules: automerge-swift's
/// <c>AutomergeDecoder</c> driving the schema's generated Swift <c>Codable</c> code. The rules, field
/// by field (from AutomergeKeyedDecodingContainer, AutomergeUnkeyedDecodingContainer and
/// AutomergeSingleValueDecodingContainer):
/// <list type="bullet">
/// <item>A record must be a map; a list must be a list.</item>
/// <item>A required field that's missing fails the document. An optional one that's missing or
/// null is simply absent.</item>
/// <item>A string must be a string value (not a text object), a bool a boolean.</item>
/// <item>A whole number may be an Int or a Uint (never an F64).</item>
/// <item>A double in a record may be an F64 or an Int; a double inside a list must be an F64.</item>
/// <item>An enum is a string (a text object is read as its string) that names one of the enum's
/// cases, or one of its legacy aliases (read as the current case). Anything else fails the whole
/// document: the cases are closed.</item>
/// <item>Keys the record doesn't have are skipped.</item>
/// </list>
/// Windows adds one rule of its own: its models hold whole numbers in 32 bits, so a bigger one
/// fails the document here (the Mac would read it).
/// </summary>
public sealed class MacDecoder(MacSchema schema)
{
    public static readonly MacDecoder Shared = new(MacSchema.Shared);

    public MacDecoded Decode(AmMap root, string recordName)
    {
        var context = new Context();
        var value = DecodeRecord(root, schema.Record(recordName), "", context);
        return new MacDecoded(context.Errors.Count == 0 ? value : null, context.Errors, context.UnknownKeys);
    }

    private sealed class Context
    {
        public readonly List<string> Errors = [];
        public readonly List<string> UnknownKeys = [];
    }

    private static string At(string path, string key) => path.Length == 0 ? key : $"{path}.{key}";

    private JsonObject? DecodeRecord(AmNode node, MacRecord record, string path, Context context)
    {
        if (node is not AmMap map)
        {
            context.Errors.Add($"{Where(path)} should be a {record.Name} (a map) but is {Describe(node)}");
            return null;
        }
        var result = new JsonObject();
        foreach (var field in record.Fields)
        {
            var fieldPath = At(path, field.Name);
            var value = map[field.Name];
            if (value is null || value is AmScalar { Kind: AmScalarKind.Null })
            {
                // decodeIfPresent reads a missing key and a null alike as nil; a required field needs a value.
                if (!field.Optional) context.Errors.Add($"{fieldPath} is missing");
                continue;
            }
            if (DecodeValue(value, field.Type, inList: false, fieldPath, context) is { } decoded) result[field.Name] = decoded;
        }
        foreach (var key in map.Entries.Keys)
            if (record.Field(key) is null) context.UnknownKeys.Add(At(path, key));
        return result;
    }

    private JsonNode? DecodeValue(AmNode node, MacType type, bool inList, string path, Context context)
    {
        switch (type)
        {
            case MacPrimitive primitive:
                return DecodePrimitive(node, primitive.Kind, inList, path, context);
            case MacListOf list:
                if (node is not AmList items)
                {
                    context.Errors.Add($"{path} should be a list but is {Describe(node)}");
                    return null;
                }
                var array = new JsonArray();
                for (var i = 0; i < items.Items.Count; i++)
                {
                    var element = DecodeValue(items.Items[i], list.Element, inList: true, $"{path}[{i}]", context);
                    // A failed element already recorded its error; keep positions honest with a placeholder.
                    array.Add(element ?? JsonValue.Create(0));
                }
                return array;
            case MacNamed named when schema.Enums.TryGetValue(named.Name, out var enumType):
                // An enum decodes through a single-value container, which reads a text object as its string.
                var raw = node switch
                {
                    AmScalar { Kind: AmScalarKind.String } s => s.Text,
                    AmText text => text.Value,
                    _ => null,
                };
                if (raw is null)
                {
                    context.Errors.Add($"{path} should be a {enumType.Name} (a string) but is {Describe(node)}");
                    return null;
                }
                if (enumType.Decode(raw) is not { } current)
                {
                    context.Errors.Add($"{path} is \"{raw}\", which isn't a {enumType.Name} this version knows");
                    return null;
                }
                return JsonValue.Create(current);
            case MacNamed named:
                return DecodeRecord(node, schema.Record(named.Name), path, context);
            default:
                throw new InvalidOperationException($"Unknown schema type {type}");
        }
    }

    private static JsonNode? DecodePrimitive(AmNode node, MacPrimitiveKind kind, bool inList, string path, Context context)
    {
        var scalar = node as AmScalar;
        switch (kind)
        {
            case MacPrimitiveKind.String when scalar is { Kind: AmScalarKind.String }:
                return JsonValue.Create(scalar.Text!);
            case MacPrimitiveKind.Bool when scalar is { Kind: AmScalarKind.Boolean }:
                return JsonValue.Create(scalar.Bool);
            case MacPrimitiveKind.Int when scalar is { Kind: AmScalarKind.Int or AmScalarKind.Uint }:
                var wide = scalar.Kind == AmScalarKind.Int ? (decimal)scalar.Integer : scalar.Unsigned;
                if (wide is < int.MinValue or > int.MaxValue)
                {
                    context.Errors.Add($"{path} is {wide}, too big for Windows (it holds whole numbers up to {int.MaxValue:N0})");
                    return null;
                }
                return JsonValue.Create((int)wide);
            case MacPrimitiveKind.Double when scalar is { Kind: AmScalarKind.F64 }:
                return JsonValue.Create(scalar.Double);
            case MacPrimitiveKind.Double when !inList && scalar is { Kind: AmScalarKind.Int }:
                // A record's double reads an Int too (decodeLosslessStringConvertible); a list's doesn't.
                return JsonValue.Create((double)scalar.Integer);
            default:
                context.Errors.Add($"{path} should be a {kind.ToString().ToLowerInvariant()} but is {Describe(node)}");
                return null;
        }
    }

    private static string Where(string path) => path.Length == 0 ? "The document" : path;

    private static string Describe(AmNode node) => node switch
    {
        AmMap => "a map",
        AmList => "a list",
        AmText => "a text object",
        AmScalar s => $"{Article(s.Kind)} {s.Kind} ({s})",
        _ => "something else",
    };

    private static string Article(AmScalarKind kind) => kind is AmScalarKind.Int or AmScalarKind.Uint or AmScalarKind.Unknown ? "an" : "a";
}
