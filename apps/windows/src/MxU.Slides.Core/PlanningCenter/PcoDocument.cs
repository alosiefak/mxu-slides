using System.Text.Json;
using System.Text.Json.Nodes;

namespace MxU.Slides.Core.PlanningCenter;

/// <summary>
/// One JSON:API resource, the unit Planning Center answers in:
/// <c>{"type":"Plan","id":"123","attributes":{…},"relationships":{"song":{"data":{"type":"Song","id":"9"}}}}</c>.
/// Attributes are kept as JSON and read through the typed helpers, so a field Planning Center adds
/// or leaves out never breaks the import.
/// </summary>
public sealed record PcoResource(string Type, string Id, JsonObject Attributes, IReadOnlyDictionary<string, IReadOnlyList<PcoRef>> Relationships)
{
    /// <summary>The first resource a relationship points at ("song" → Song 9), or null.</summary>
    public PcoRef? One(string relationship) =>
        Relationships.TryGetValue(relationship, out var refs) && refs.Count > 0 ? refs[0] : null;

    public IReadOnlyList<PcoRef> Many(string relationship) =>
        Relationships.TryGetValue(relationship, out var refs) ? refs : [];

    public string? Text(string name) =>
        Attributes[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>A text attribute with surrounding blanks removed; null when missing or empty.</summary>
    public string? Trimmed(string name) => Text(name)?.Trim() is { Length: > 0 } text ? text : null;

    public double? Number(string name)
    {
        if (Attributes[name] is not JsonValue value) return null;
        if (value.TryGetValue<double>(out var number)) return number;
        // Some numbers (a CCLI number, a length) arrive as strings in older records.
        return value.TryGetValue<string>(out var text) && double.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    public int? Integer(string name) => Number(name) is { } n && n is >= int.MinValue and <= int.MaxValue ? (int)Math.Round(n) : null;

    public bool Flag(string name) => Attributes[name] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    public List<string> Texts(string name) =>
        Attributes[name] is JsonArray array
            ? [.. array.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).OfType<string>()]
            : [];

    public DateTimeOffset? Time(string name) =>
        DateTimeOffset.TryParse(Text(name), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var time) ? time : null;
}

/// <summary>A pointer from one resource to another: its type and id.</summary>
public sealed record PcoRef(string Type, string Id);

/// <summary>
/// One answer from Planning Center: the resources asked for (<see cref="Data"/>), the ones pulled in
/// alongside with <c>include=</c> (<see cref="Included"/>), and the link to the next page, if any.
/// </summary>
public sealed class PcoDocument
{
    public List<PcoResource> Data { get; } = [];
    public List<PcoResource> Included { get; } = [];

    /// <summary>The next page's address (links.next), or null on the last page.</summary>
    public string? Next { get; private set; }

    /// <summary>meta.total_count when Planning Center sends it.</summary>
    public int? TotalCount { get; private set; }

    /// <summary>An included resource by its type and id, or null.</summary>
    public PcoResource? Find(PcoRef? reference) =>
        reference is null ? null : Included.FirstOrDefault(r => r.Type == reference.Type && r.Id == reference.Id)
            ?? Data.FirstOrDefault(r => r.Type == reference.Type && r.Id == reference.Id);

    /// <summary>Adds a later page's resources to this one (paging).</summary>
    public void Append(PcoDocument page)
    {
        Data.AddRange(page.Data);
        foreach (var resource in page.Included)
            if (!Included.Any(r => r.Type == resource.Type && r.Id == resource.Id)) Included.Add(resource);
        Next = page.Next;
    }

    /// <summary>Reads a JSON:API document. Throws <see cref="JsonException"/> when it isn't one.</summary>
    public static PcoDocument Parse(ReadOnlySpan<byte> json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new JsonException("Not a JSON object.");
        var document = new PcoDocument();
        switch (root["data"])
        {
            case JsonArray many:
                document.Data.AddRange(many.OfType<JsonObject>().Select(Resource).OfType<PcoResource>());
                break;
            case JsonObject one when Resource(one) is { } resource:
                document.Data.Add(resource);
                break;
            case null:
                throw new JsonException("No \"data\" in the answer.");
        }
        if (root["included"] is JsonArray included)
            document.Included.AddRange(included.OfType<JsonObject>().Select(Resource).OfType<PcoResource>());
        if (root["links"] is JsonObject links && links["next"] is JsonValue next && next.TryGetValue<string>(out var url) && url.Length > 0)
            document.Next = url;
        if (root["meta"] is JsonObject meta && meta["total_count"] is JsonValue total && total.TryGetValue<int>(out var count))
            document.TotalCount = count;
        return document;
    }

    private static PcoResource? Resource(JsonObject node)
    {
        if (node["type"]?.GetValueKind() != JsonValueKind.String || node["id"] is not JsonValue idValue) return null;
        var type = node["type"]!.GetValue<string>();
        // Ids are strings in JSON:API, but be forgiving of a number.
        var id = idValue.TryGetValue<string>(out var text) ? text : idValue.ToJsonString();
        var attributes = node["attributes"] as JsonObject ?? [];
        var relationships = new Dictionary<string, IReadOnlyList<PcoRef>>();
        if (node["relationships"] is JsonObject related)
        {
            foreach (var (name, value) in related)
            {
                var data = (value as JsonObject)?["data"];
                relationships[name] = data switch
                {
                    JsonObject single when Ref(single) is { } r => [r],
                    JsonArray list => [.. list.OfType<JsonObject>().Select(Ref).OfType<PcoRef>()],
                    _ => [],
                };
            }
        }
        return new PcoResource(type, id, attributes, relationships);
    }

    private static PcoRef? Ref(JsonObject node) =>
        node["type"] is JsonValue type && type.TryGetValue<string>(out var t) && node["id"] is JsonValue id
            ? new PcoRef(t, id.TryGetValue<string>(out var s) ? s : id.ToJsonString())
            : null;
}
