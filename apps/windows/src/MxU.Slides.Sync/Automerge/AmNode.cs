using System.Globalization;
using System.Text.Json;

namespace MxU.Slides.Sync.Automerge;

/// <summary>
/// A value read out of an Automerge document, keeping its Automerge type. The Mac's decoder cares
/// about the type, not just the value: a whole-number field accepts an Int but not an F64, and a
/// string field accepts a string but not a text object. So this keeps what JSON would lose.
/// </summary>
public abstract record AmNode;

/// <summary>A map object (a record, or the document's root). Keys are in Automerge's order.</summary>
public sealed record AmMap(IReadOnlyDictionary<string, AmNode> Entries) : AmNode
{
    public AmNode? this[string key] => Entries.GetValueOrDefault(key);
}

/// <summary>A list object.</summary>
public sealed record AmList(IReadOnlyList<AmNode> Items) : AmNode;

/// <summary>A text object (collaborative text). The Mac's documents don't use them; they're read only to be refused.</summary>
public sealed record AmText(string Value) : AmNode;

/// <summary>The Automerge scalar types.</summary>
public enum AmScalarKind
{
    String,
    Int,
    Uint,
    F64,

    /// <summary>NaN or an infinity, which the Mac's encoder never writes.</summary>
    NonFinite,
    Boolean,
    Null,
    Counter,
    Timestamp,
    Bytes,

    /// <summary>A value from a newer Automerge than this one.</summary>
    Unknown,
}

/// <summary>A scalar value: <see cref="Text"/> for strings, <see cref="Integer"/> for Int, Counter and
/// Timestamp, <see cref="Unsigned"/> for Uint, <see cref="Double"/> for F64 and <see cref="Bool"/>.</summary>
public sealed record AmScalar(AmScalarKind Kind, string? Text = null, long Integer = 0, ulong Unsigned = 0, double Double = 0, bool Bool = false) : AmNode
{
    public static AmScalar Of(string value) => new(AmScalarKind.String, Text: value);

    public static AmScalar Of(long value) => new(AmScalarKind.Int, Integer: value);

    public static AmScalar Of(double value) => new(AmScalarKind.F64, Double: value);

    public static AmScalar Of(bool value) => new(AmScalarKind.Boolean, Bool: value);

    public override string ToString() => Kind switch
    {
        AmScalarKind.String => $"\"{Text}\"",
        AmScalarKind.Int => Integer.ToString(CultureInfo.InvariantCulture),
        AmScalarKind.Uint => Unsigned.ToString(CultureInfo.InvariantCulture) + "u",
        AmScalarKind.F64 => Double.ToString("R", CultureInfo.InvariantCulture),
        AmScalarKind.Boolean => Bool ? "true" : "false",
        AmScalarKind.Null => "null",
        _ => Kind.ToString(),
    };
}

/// <summary>Reads the tagged JSON tree <c>mxu_am_read</c> returns (its format is described in lib.rs).</summary>
internal static class AmTreeReader
{
    public static AmNode Parse(ReadOnlySpan<byte> utf8)
    {
        using var json = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions { MaxDepth = 1024 });
        return Node(json.RootElement);
    }

    private static AmNode Node(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new FormatException("Automerge tree: expected a tagged value");
        using var properties = element.EnumerateObject();
        if (!properties.MoveNext()) throw new FormatException("Automerge tree: empty tag");
        var tag = properties.Current;
        var value = tag.Value;
        switch (tag.Name)
        {
            case "m":
            case "tb":
                var entries = new Dictionary<string, AmNode>(StringComparer.Ordinal);
                foreach (var entry in value.EnumerateObject()) entries[entry.Name] = Node(entry.Value);
                return new AmMap(entries);
            case "l":
                return new AmList(value.EnumerateArray().Select(Node).ToList());
            case "t":
                return new AmText(value.GetString() ?? "");
            case "s":
                return AmScalar.Of(value.GetString() ?? "");
            case "i":
                return new AmScalar(AmScalarKind.Int, Integer: value.GetInt64());
            case "u":
                return new AmScalar(AmScalarKind.Uint, Unsigned: value.GetUInt64());
            case "f":
                return new AmScalar(AmScalarKind.F64, Double: value.GetDouble());
            case "fx":
                return new AmScalar(AmScalarKind.NonFinite, Text: value.GetString());
            case "b":
                return AmScalar.Of(value.GetBoolean());
            case "n":
                return new AmScalar(AmScalarKind.Null);
            case "c":
                return new AmScalar(AmScalarKind.Counter, Integer: value.GetInt64());
            case "ts":
                return new AmScalar(AmScalarKind.Timestamp, Integer: value.GetInt64());
            case "by":
                return new AmScalar(AmScalarKind.Bytes, Text: value.GetString());
            case "x":
                return new AmScalar(AmScalarKind.Unknown, Integer: value.GetInt64());
            default:
                throw new FormatException($"Automerge tree: unknown tag '{tag.Name}'");
        }
    }
}
