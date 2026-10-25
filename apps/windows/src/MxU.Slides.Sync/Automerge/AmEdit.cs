using System.Buffers;
using System.Text.Json;

namespace MxU.Slides.Sync.Automerge;

/// <summary>
/// Where in a document an operation acts: the keys and list positions from the root, such as
/// <c>slides › 2 › objects › 0</c>. Each step is a map key or a list index. Immutable: <see cref="Then(string)"/>
/// makes a longer path.
/// </summary>
public sealed class AmPath
{
    public static readonly AmPath Root = new([]);

    private readonly object[] steps;

    private AmPath(object[] steps) => this.steps = steps;

    public IReadOnlyList<object> Steps => steps;

    public AmPath Then(string key) => new([.. steps, key]);

    public AmPath Then(int index) => new([.. steps, index]);

    public override string ToString() => steps.Length == 0 ? "(root)" : string.Join(" › ", steps);
}

/// <summary>The kinds of value an edit can write: what the Mac's encoder writes for the shared schema.</summary>
public readonly record struct AmWrite
{
    private AmWrite(AmScalarKind kind, string? text, long integer, double number, bool flag)
    {
        Kind = kind;
        Text = text;
        Integer = integer;
        Number = number;
        Flag = flag;
    }

    public AmScalarKind Kind { get; }
    public string? Text { get; }
    public long Integer { get; }
    public double Number { get; }
    public bool Flag { get; }

    public static AmWrite String(string value) => new(AmScalarKind.String, value, 0, 0, false);

    public static AmWrite Int(long value) => new(AmScalarKind.Int, null, value, 0, false);

    /// <summary>A double. NaN and the infinities are refused, as the Mac's encoder refuses them.</summary>
    public static AmWrite Double(double value) => double.IsFinite(value)
        ? new(AmScalarKind.F64, null, 0, value, false)
        : throw new ArgumentOutOfRangeException(nameof(value), "Automerge documents shared with the Mac can't hold NaN or infinity");

    public static AmWrite Bool(bool value) => new(AmScalarKind.Boolean, null, 0, 0, value);
}

/// <summary>The kinds of object an edit can make.</summary>
public enum AmObjectKind
{
    Map,
    List,
}

/// <summary>
/// One edit to an Automerge document: a list of operations that <see cref="AmDocument.Apply"/>
/// carries out in one transaction and commits as one change. The operations are applied in order,
/// so a path that names a list index means the list as the earlier operations left it.
/// </summary>
public sealed class AmEdit
{
    private readonly List<Action<Utf8JsonWriter>> ops = [];

    /// <summary>How many operations the edit holds.</summary>
    public int Count => ops.Count;

    /// <summary>Sets a map key.</summary>
    public AmEdit Put(AmPath obj, string key, AmWrite value) => Add("put", obj, w => { Key(w, key); Value(w, value); });

    /// <summary>Replaces a list element with a value.</summary>
    public AmEdit Put(AmPath obj, int index, AmWrite value) => Add("put", obj, w => { Key(w, index); Value(w, value); });

    /// <summary>Sets a map key to a new, empty map or list.</summary>
    public AmEdit PutObject(AmPath obj, string key, AmObjectKind kind) => Add("putObj", obj, w => { Key(w, key); Kind(w, kind); });

    /// <summary>Inserts a value into a list before <paramref name="index"/> (the length appends).</summary>
    public AmEdit Insert(AmPath obj, int index, AmWrite value) => Add("ins", obj, w => { w.WriteNumber("i", index); Value(w, value); });

    /// <summary>Inserts a new, empty map or list into a list before <paramref name="index"/>.</summary>
    public AmEdit InsertObject(AmPath obj, int index, AmObjectKind kind) => Add("insObj", obj, w => { w.WriteNumber("i", index); Kind(w, kind); });

    /// <summary>Removes a map key.</summary>
    public AmEdit Delete(AmPath obj, string key) => Add("del", obj, w => Key(w, key));

    /// <summary>Removes a list element.</summary>
    public AmEdit Delete(AmPath obj, int index) => Add("del", obj, w => Key(w, index));

    /// <summary>The edit as the JSON list the DLL reads (format in lib.rs, <c>Op</c>).</summary>
    public byte[] ToUtf8()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var op in ops) op(writer);
            writer.WriteEndArray();
        }
        return buffer.WrittenSpan.ToArray();
    }

    public override string ToString() => System.Text.Encoding.UTF8.GetString(ToUtf8());

    private AmEdit Add(string name, AmPath obj, Action<Utf8JsonWriter> rest)
    {
        ops.Add(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("o", name);
            writer.WriteStartArray("p");
            foreach (var step in obj.Steps)
            {
                if (step is int index) writer.WriteNumberValue(index);
                else writer.WriteStringValue((string)step);
            }
            writer.WriteEndArray();
            rest(writer);
            writer.WriteEndObject();
        });
        return this;
    }

    private static void Key(Utf8JsonWriter writer, string key) => writer.WriteString("k", key);

    private static void Key(Utf8JsonWriter writer, int index) => writer.WriteNumber("k", index);

    private static void Kind(Utf8JsonWriter writer, AmObjectKind kind) => writer.WriteString("t", kind == AmObjectKind.Map ? "map" : "list");

    private static void Value(Utf8JsonWriter writer, AmWrite value)
    {
        writer.WriteStartObject("v");
        switch (value.Kind)
        {
            case AmScalarKind.String: writer.WriteString("s", value.Text); break;
            case AmScalarKind.Int: writer.WriteNumber("i", value.Integer); break;
            case AmScalarKind.F64: writer.WriteNumber("f", value.Number); break;
            case AmScalarKind.Boolean: writer.WriteBoolean("b", value.Flag); break;
            default: throw new InvalidOperationException($"An edit can't write a {value.Kind}");
        }
        writer.WriteEndObject();
    }
}
