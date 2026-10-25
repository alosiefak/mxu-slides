using System.Text.Json.Nodes;
using MxU.Slides.Sync.Automerge;

namespace MxU.Slides.Sync.Format;

/// <summary>
/// Writes a value into an Automerge document as the Mac's encoder (automerge-swift's
/// <c>AutomergeEncoder</c> over the schema's generated Swift <c>Codable</c> code) would:
/// <list type="bullet">
/// <item>a record becomes a map, its fields set in the schema's order; an optional field that's
/// absent isn't written at all (Swift's <c>encodeIfPresent</c>);</item>
/// <item>a list becomes a list, its elements inserted in order;</item>
/// <item>a string is a string value (never a text object), a whole number an Int, a double an F64,
/// a bool a boolean, and an enum its case's name as a string.</item>
/// </list>
/// These are exactly the types the Mac checks for before it next writes the document
/// (<c>cautiousWrite</c>), so it can go on editing what Windows wrote.
/// </summary>
public sealed class MacEncoder(MacSchema schema)
{
    public static readonly MacEncoder Shared = new(MacSchema.Shared);

    internal MacSchema Schema => schema;

    /// <summary>The edit that fills an empty document with <paramref name="value"/> (checked against the schema first).</summary>
    public AmEdit NewDocument(JsonObject value, string recordName)
    {
        SchemaCheck.Require(value, recordName, schema);
        var edit = new AmEdit();
        WriteRecordFields(edit, AmPath.Root, value, schema.Record(recordName));
        return edit;
    }

    /// <summary>Sets every present field of <paramref name="value"/> on the (empty) map at <paramref name="obj"/>.</summary>
    internal void WriteRecordFields(AmEdit edit, AmPath obj, JsonObject value, MacRecord record)
    {
        foreach (var field in record.Fields)
            if (value.TryGetPropertyValue(field.Name, out var fieldValue) && fieldValue is not null)
                WriteAtKey(edit, obj, field.Name, field.Type, fieldValue);
    }

    /// <summary>Sets <paramref name="key"/> on the map at <paramref name="obj"/> to a new copy of <paramref name="value"/>.</summary>
    internal void WriteAtKey(AmEdit edit, AmPath obj, string key, MacType type, JsonNode value)
    {
        switch (Shape(type))
        {
            case Kind.Scalar:
                edit.Put(obj, key, Scalar(value, type));
                break;
            case Kind.Record:
                edit.PutObject(obj, key, AmObjectKind.Map);
                WriteRecordFields(edit, obj.Then(key), (JsonObject)value, schema.Record(((MacNamed)type).Name));
                break;
            case Kind.List:
                edit.PutObject(obj, key, AmObjectKind.List);
                WriteListItems(edit, obj.Then(key), (JsonArray)value, ((MacListOf)type).Element);
                break;
        }
    }

    /// <summary>Inserts a new copy of <paramref name="value"/> into the list at <paramref name="list"/>, before <paramref name="index"/>.</summary>
    internal void InsertAt(AmEdit edit, AmPath list, int index, MacType type, JsonNode value)
    {
        switch (Shape(type))
        {
            case Kind.Scalar:
                edit.Insert(list, index, Scalar(value, type));
                break;
            case Kind.Record:
                edit.InsertObject(list, index, AmObjectKind.Map);
                WriteRecordFields(edit, list.Then(index), (JsonObject)value, schema.Record(((MacNamed)type).Name));
                break;
            case Kind.List:
                edit.InsertObject(list, index, AmObjectKind.List);
                WriteListItems(edit, list.Then(index), (JsonArray)value, ((MacListOf)type).Element);
                break;
        }
    }

    /// <summary>Replaces the list element at <paramref name="index"/> with a new copy of <paramref name="value"/>.</summary>
    internal void ReplaceAt(AmEdit edit, AmPath list, int index, MacType type, JsonNode value)
    {
        switch (Shape(type))
        {
            case Kind.Scalar:
                edit.Put(list, index, Scalar(value, type));
                break;
            case Kind.Record:
                edit.Delete(list, index);
                InsertAt(edit, list, index, type, value);
                break;
            case Kind.List:
                edit.Delete(list, index);
                InsertAt(edit, list, index, type, value);
                break;
        }
    }

    private void WriteListItems(AmEdit edit, AmPath list, JsonArray items, MacType element)
    {
        for (var i = 0; i < items.Count; i++) InsertAt(edit, list, i, element, items[i]!);
    }

    internal enum Kind
    {
        Scalar,
        Record,
        List,
    }

    internal Kind Shape(MacType type) => type switch
    {
        MacPrimitive => Kind.Scalar,
        MacListOf => Kind.List,
        MacNamed named when schema.Enums.ContainsKey(named.Name) => Kind.Scalar,
        MacNamed => Kind.Record,
        _ => throw new InvalidOperationException($"Unknown schema type {type}"),
    };

    /// <summary>The Automerge value the Mac writes for a JSON scalar of this schema type.</summary>
    internal AmWrite Scalar(JsonNode value, MacType type) => type switch
    {
        MacPrimitive { Kind: MacPrimitiveKind.String } => AmWrite.String(JsonRead.String(value) ?? throw Wrong(type)),
        MacPrimitive { Kind: MacPrimitiveKind.Int } => AmWrite.Int(JsonRead.Integer(value)),
        MacPrimitive { Kind: MacPrimitiveKind.Double } => AmWrite.Double(JsonRead.Double(value)),
        MacPrimitive { Kind: MacPrimitiveKind.Bool } => AmWrite.Bool(JsonRead.Bool(value)),
        MacNamed named when schema.Enums.TryGetValue(named.Name, out var enumType) =>
            JsonRead.String(value) is { } raw && enumType.IsCase(raw)
                ? AmWrite.String(raw)
                : throw new SchemaViolationException(named.Name, [$"\"{value}\" isn't a {named.Name} the Mac knows"]),
        _ => throw Wrong(type),
    };

    private static InvalidOperationException Wrong(MacType type) => new($"Not a value of type {type}");
}
