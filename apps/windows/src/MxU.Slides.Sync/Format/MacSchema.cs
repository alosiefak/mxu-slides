using System.Text.Json;

namespace MxU.Slides.Sync.Format;

/// <summary>A field's type in the shared schema: a primitive, a list, or a named enum or record.</summary>
public abstract record MacType
{
    /// <summary>Parses the schema's spelling: "string", "int", "double", "bool", "list:X" or a type's name.</summary>
    public static MacType Parse(string text) => text switch
    {
        "string" => new MacPrimitive(MacPrimitiveKind.String),
        "int" => new MacPrimitive(MacPrimitiveKind.Int),
        "double" => new MacPrimitive(MacPrimitiveKind.Double),
        "bool" => new MacPrimitive(MacPrimitiveKind.Bool),
        _ when text.StartsWith("list:", StringComparison.Ordinal) => new MacListOf(Parse(text["list:".Length..])),
        _ => new MacNamed(text),
    };
}

public enum MacPrimitiveKind
{
    String,

    /// <summary>Swift's Int: 64 bits on the Mac. Windows' models hold 32 (C# int), so a bigger one can't come across.</summary>
    Int,
    Double,
    Bool,
}

public sealed record MacPrimitive(MacPrimitiveKind Kind) : MacType;

public sealed record MacListOf(MacType Element) : MacType;

/// <summary>An enum or a record, by name.</summary>
public sealed record MacNamed(string Name) : MacType;

/// <summary>One field of a record, in declaration order (the order Swift encodes them in).</summary>
public sealed record MacField(string Name, MacType Type, bool Optional);

/// <summary>A record type: a Swift struct, an Automerge map.</summary>
public sealed class MacRecord(string name, IReadOnlyList<MacField> fields)
{
    private readonly Dictionary<string, MacField> byName = fields.ToDictionary(f => f.Name, StringComparer.Ordinal);

    public string Name { get; } = name;
    public IReadOnlyList<MacField> Fields { get; } = fields;

    public MacField? Field(string name) => byName.GetValueOrDefault(name);

    /// <summary>True when the record has a string "id" field, which lets lists of it be matched element by element.</summary>
    public bool HasStringId => Field("id") is { Type: MacPrimitive { Kind: MacPrimitiveKind.String } };
}

/// <summary>
/// An enum type: a Swift <c>enum: String, Codable</c>. Its cases are closed: the Mac refuses a
/// whole document holding a value it doesn't know. Some enums read an old spelling as a current
/// case (<see cref="LegacyAliases"/>, from the schema's "legacyAliases").
/// </summary>
public sealed class MacEnum(string name, IReadOnlyList<string> cases, IReadOnlyDictionary<string, string> legacyAliases)
{
    private readonly HashSet<string> known = new(cases, StringComparer.Ordinal);

    public string Name { get; } = name;
    public IReadOnlyList<string> Cases { get; } = cases;
    public IReadOnlyDictionary<string, string> LegacyAliases { get; } = legacyAliases;

    public bool IsCase(string raw) => known.Contains(raw);

    /// <summary>The case <paramref name="raw"/> decodes to on the Mac (an alias becomes its current case), or null if the Mac would refuse it.</summary>
    public string? Decode(string raw) => known.Contains(raw) ? raw : LegacyAliases.GetValueOrDefault(raw);
}

/// <summary>
/// The shared document schema, packages/schema/documents.json: the same file the Mac's Swift
/// models (packages/schema/codegen.mjs) and Windows' C# models (tools/codegen-csharp.mjs) are
/// generated from, built into this assembly. Library sync reads it to decode and check documents
/// exactly as the Mac's generated Codable code does.
/// </summary>
public sealed class MacSchema
{
    private static readonly Lazy<MacSchema> SharedSchema = new(LoadEmbedded);

    private MacSchema(int version, Dictionary<string, MacRecord> records, Dictionary<string, MacEnum> enums)
    {
        Version = version;
        Records = records;
        Enums = enums;
    }

    /// <summary>The schema built into this assembly.</summary>
    public static MacSchema Shared => SharedSchema.Value;

    /// <summary>The schema's "schemaVersion" (it matches <c>SchemaInfo.SchemaVersion</c> in the generated C# models).</summary>
    public int Version { get; }

    public IReadOnlyDictionary<string, MacRecord> Records { get; }
    public IReadOnlyDictionary<string, MacEnum> Enums { get; }

    public MacRecord Record(string name) =>
        Records.TryGetValue(name, out var record) ? record : throw new KeyNotFoundException($"The schema has no record named {name}");

    public static MacSchema Parse(ReadOnlySpan<byte> utf8)
    {
        using var json = JsonDocument.Parse(utf8.ToArray());
        var root = json.RootElement;
        var records = new Dictionary<string, MacRecord>(StringComparer.Ordinal);
        var enums = new Dictionary<string, MacEnum>(StringComparer.Ordinal);
        foreach (var type in root.GetProperty("types").EnumerateObject())
        {
            var definition = type.Value;
            switch (definition.GetProperty("kind").GetString())
            {
                case "enum":
                    var aliases = definition.TryGetProperty("legacyAliases", out var legacy)
                        ? legacy.EnumerateObject().ToDictionary(a => a.Name, a => a.Value.GetString()!, StringComparer.Ordinal)
                        : new Dictionary<string, string>(StringComparer.Ordinal);
                    enums[type.Name] = new MacEnum(type.Name, definition.GetProperty("cases").EnumerateArray().Select(c => c.GetString()!).ToList(), aliases);
                    break;
                case "record":
                    records[type.Name] = new MacRecord(type.Name, definition.GetProperty("fields").EnumerateArray().Select(f => new MacField(
                        f.GetProperty("name").GetString()!,
                        MacType.Parse(f.GetProperty("type").GetString()!),
                        f.TryGetProperty("optional", out var optional) && optional.GetBoolean())).ToList());
                    break;
                default:
                    throw new FormatException($"Unknown schema kind for {type.Name}");
            }
        }
        return new MacSchema(root.GetProperty("schemaVersion").GetInt32(), records, enums);
    }

    private static MacSchema LoadEmbedded()
    {
        using var stream = typeof(MacSchema).Assembly.GetManifestResourceStream("documents.json")
            ?? throw new InvalidOperationException("The shared schema isn't built into MxU.Slides.Sync");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return Parse(copy.ToArray());
    }
}
