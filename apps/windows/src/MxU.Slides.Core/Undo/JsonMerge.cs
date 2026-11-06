using System.Text.Json;
using System.Text.Json.Nodes;
using MxU.Slides.Core.Json;

namespace MxU.Slides.Core.Undo;

/// <summary>
/// A three-way merge of JSON, which is how undo moves a document back without losing anyone
/// else's later edit. Undoing a step means "make the change from the step's after (the basis) to
/// its before (wanted)", applied to the document as it is now (current):
/// <list type="bullet">
/// <item>a part nobody touched since the step simply goes back;</item>
/// <item>a part the step didn't change keeps whatever it is now;</item>
/// <item>a part both changed keeps the newer value (current): a later edit wins.</item>
/// </list>
/// Objects merge key by key. Lists of objects with ids (slides, objects, service items) merge
/// element by element, by id, so undoing "Delete Slide" puts that slide back without undoing a
/// slide someone else added meanwhile. Any other list (an arrangement's section ids) is one value.
/// <para>
/// The Mac gets the same effect from Automerge, whose undo reverts only the objects a change
/// touched (SubtreeRevert.swift). Where a slide that's put back goes follows the Mac's list rules:
/// back at its old place, clamped if the list shrank (ListRemoval.restore), and an order put back
/// ranks what it knows and leaves the rest after it, in their order (ListOrder.apply).
/// </para>
/// </summary>
public static class JsonMerge
{
    /// <summary>
    /// The change from <paramref name="basis"/> to <paramref name="wanted"/>, applied to
    /// <paramref name="current"/>. Null stands for "doesn't exist" (a deleted document).
    /// </summary>
    public static byte[]? Apply(byte[]? basis, byte[]? current, byte[]? wanted)
    {
        if (UndoBytes.Same(current, basis)) return wanted;
        if (UndoBytes.Same(wanted, basis) || UndoBytes.Same(current, wanted)) return current;
        // Made or deleted on one side and edited on the other: the later edit wins.
        if (basis is null || current is null || wanted is null) return current;
        if (Parse(basis) is not { } b || Parse(current) is not { } c || Parse(wanted) is not { } w) return current;
        return Merge(b, c, w) is { } merged ? JsonSerializer.SerializeToUtf8Bytes(merged, MxuJson.Options) : null;
    }

    /// <summary>The same three-way merge on parsed JSON (null: absent).</summary>
    public static JsonNode? Merge(JsonNode? basis, JsonNode? current, JsonNode? wanted)
    {
        if (JsonNode.DeepEquals(current, basis)) return wanted?.DeepClone();
        if (JsonNode.DeepEquals(wanted, basis) || JsonNode.DeepEquals(current, wanted)) return current?.DeepClone();
        if (basis is JsonObject bo && current is JsonObject co && wanted is JsonObject wo) return MergeObjects(bo, co, wo);
        if (basis is JsonArray ba && current is JsonArray ca && wanted is JsonArray wa && IsIdList(ba) && IsIdList(ca) && IsIdList(wa))
            return MergeIdLists(ba, ca, wa);
        // Both changed the same value (or one made it and the other dropped it): the newer edit stands.
        return current?.DeepClone();
    }

    /// <summary>Two JSON texts say the same thing (ignoring layout).</summary>
    public static bool SameJson(byte[]? a, byte[]? b)
    {
        if (UndoBytes.Same(a, b)) return true;
        if (a is null || b is null) return false;
        return JsonNode.DeepEquals(Parse(a), Parse(b));
    }

    private static JsonNode? Parse(byte[] json)
    {
        try
        {
            return JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonObject MergeObjects(JsonObject basis, JsonObject current, JsonObject wanted)
    {
        var result = new JsonObject();
        // Current's key order first, then keys only the wanted side has: the model writes a fixed order anyway.
        var keys = current.Select(p => p.Key).Concat(wanted.Select(p => p.Key)).Concat(basis.Select(p => p.Key)).Distinct();
        foreach (var key in keys)
        {
            if (Merge(basis[key], current[key], wanted[key]) is { } merged) result[key] = merged;
        }
        return result;
    }

    // MARK: Lists of things with ids

    private static string? IdOf(JsonNode? node) =>
        node is JsonObject o && o["id"] is JsonValue v && v.TryGetValue<string>(out var id) ? id : null;

    /// <summary>A list whose every element is an object with its own, unique id.</summary>
    private static bool IsIdList(JsonArray list)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in list)
        {
            if (IdOf(element) is not { } id || !seen.Add(id)) return false;
        }
        return true;
    }

    private static Dictionary<string, JsonNode> ById(JsonArray list) =>
        list.ToDictionary(n => IdOf(n)!, n => n!, StringComparer.Ordinal);

    private static JsonArray MergeIdLists(JsonArray basis, JsonArray current, JsonArray wanted)
    {
        var b = ById(basis);
        var c = ById(current);
        var w = ById(wanted);
        var currentIds = current.Select(n => IdOf(n)!).ToList();
        var wantedIds = wanted.Select(n => IdOf(n)!).ToList();

        // Which elements stay, and what each holds: the same merge, element by element, with
        // "absent" for an element one side hasn't got. So an element the step removed comes back
        // unless someone deleted it since, and one it added goes unless someone edited it since.
        var kept = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var id in currentIds.Concat(wantedIds).Concat(b.Keys).Distinct())
        {
            if (Merge(b.GetValueOrDefault(id), c.GetValueOrDefault(id), w.GetValueOrDefault(id)) is { } element)
                kept[id] = element;
        }

        // The order. When the step itself reordered things (a slide moved) and nobody has
        // reordered since, its old order is put back, with anything it doesn't know after it
        // (ListOrder.apply). Otherwise the order as it is now stands, and each element coming back
        // goes in at its old place, clamped if the list shrank (ListRemoval.restore).
        var basisIds = basis.Select(n => IdOf(n)!).ToList();
        var stepReordered = !basisIds.Where(w.ContainsKey).SequenceEqual(wantedIds.Where(b.ContainsKey));
        var currentReordered = !currentIds.Where(b.ContainsKey).SequenceEqual(basisIds.Where(c.ContainsKey));
        List<string> order;
        if (stepReordered && !currentReordered)
        {
            order = ListOrder(wantedIds, [.. currentIds.Concat(wantedIds).Where(kept.ContainsKey).Distinct()]);
        }
        else
        {
            order = [.. currentIds.Where(kept.ContainsKey)];
            for (var index = 0; index < wantedIds.Count; index++)
            {
                var id = wantedIds[index];
                if (kept.ContainsKey(id) && !order.Contains(id)) order.Insert(Math.Min(index, order.Count), id);
            }
        }
        return [.. order.Select(id => kept[id].DeepClone())];
    }

    /// <summary>
    /// <paramref name="items"/> ranked by their place in <paramref name="order"/>; ones it doesn't
    /// name go after, in their own order. A port of the Mac's ListOrder.apply.
    /// </summary>
    public static List<string> ListOrder(IReadOnlyList<string> order, IReadOnlyList<string> items)
    {
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < order.Count; i++) rank.TryAdd(order[i], i);
        return [.. items.Select((id, offset) => (id, offset))
            .OrderBy(e => rank.GetValueOrDefault(e.id, int.MaxValue))
            .ThenBy(e => e.offset)
            .Select(e => e.id)];
    }
}
