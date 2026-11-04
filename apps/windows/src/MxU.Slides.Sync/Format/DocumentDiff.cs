using System.Text.Json.Nodes;
using MxU.Slides.Sync.Automerge;

namespace MxU.Slides.Sync.Format;

/// <summary>
/// Turns a Windows edit into the smallest Automerge edit that makes a document say the same: it
/// compares the document as it is (its Automerge tree) with the JSON Windows now has, and writes
/// only what differs. That's what lets Windows and the Mac edit one song at the same time: a
/// change to slide 3 on Windows touches only slide 3, so a Mac's change to slide 5 survives the
/// merge, exactly as it does between two Macs.
/// <list type="bullet">
/// <item><b>Records</b> (maps) are compared field by field. A field set to the same value isn't
/// written again; one Windows cleared is deleted; keys the schema doesn't know (from an older or
/// newer Mac) are never touched.</item>
/// <item><b>Lists of records with ids</b> (slides, objects, service items, sections…) are matched
/// by id, using the longest run of ids both versions share in the same order. A slide that was
/// added is inserted where it belongs, one that was removed is deleted, one that was moved is
/// removed and inserted again (as the Mac moves a slide), and every slide that stayed is compared
/// field by field.</item>
/// <item><b>Lists of values</b> (an arrangement's section ids…) keep the values both share in
/// order and insert or delete only the rest.</item>
/// <item>Other lists are compared position by position, as the Mac's encoder writes them.</item>
/// </list>
/// Strings are whole values on the Mac (it never uses Automerge's text objects), so a string that
/// changed is replaced. When both sides change the same field at once, Automerge picks the same
/// winner on every computer, as between two Macs.
/// </summary>
public sealed class DocumentDiff(MacSchema schema)
{
    /// <summary>Above this many element pairs, a list is compared position by position rather than matched (it would be slow).</summary>
    private const long MatchLimit = 4_000_000;

    private readonly MacEncoder encoder = new(schema);

    public static readonly DocumentDiff Shared = new(MacSchema.Shared);

    /// <summary>The edit that turns the document <paramref name="current"/> into <paramref name="target"/> (checked against the schema first).</summary>
    public AmEdit Edit(AmMap current, JsonObject target, string recordName)
    {
        SchemaCheck.Require(target, recordName, schema);
        var edit = new AmEdit();
        DiffRecord(edit, AmPath.Root, current, target, schema.Record(recordName));
        return edit;
    }

    private void DiffRecord(AmEdit edit, AmPath path, AmMap current, JsonObject target, MacRecord record)
    {
        foreach (var field in record.Fields)
        {
            var wanted = target.TryGetPropertyValue(field.Name, out var value) ? value : null;
            var have = current[field.Name];
            var haveValue = have is not null and not AmScalar { Kind: AmScalarKind.Null };
            if (wanted is null)
            {
                // Cleared on Windows. (A null the Mac left reads as absent already: leave it.)
                if (haveValue) edit.Delete(path, field.Name);
            }
            else if (!haveValue)
            {
                encoder.WriteAtKey(edit, path, field.Name, field.Type, wanted);
            }
            else if (ReferenceEquals(path, AmPath.Root) && WholeValueKeys.Contains(field.Name) && have is AmList whole && wanted is JsonArray array
                     && field.Type is MacListOf { Element: MacNamed named } && schema.Records.TryGetValue(named.Name, out var element))
            {
                var trial = new AmEdit();
                DiffAtKey(trial, path, field.Name, have, wanted, field.Type);
                if (trial.Count > 0) WriteWhole(edit, field.Name, whole, array, element);
            }
            else
            {
                DiffAtKey(edit, path, field.Name, have!, wanted, field.Type);
            }
        }
    }

    /// <summary>
    /// Root fields the Mac writes as one whole value when they change: it deletes the old list
    /// and writes a new one (upstream TypedDocument.swift, <c>wholeValueKeys</c>, from MxU's "make
    /// slides from notes" release). Two computers changing a song's arrangements at once then
    /// get one computer's arrangements, never a mixture of both (a section listed twice).
    /// </summary>
    internal static readonly IReadOnlySet<string> WholeValueKeys = new HashSet<string>(StringComparer.Ordinal) { "arrangements" };

    /// <summary>
    /// Writes a whole-value field as a new list. Each record that was there before (matched by id)
    /// goes in as a copy of itself first, every key included, then changed to its new version, so
    /// fields only a newer Mac knows are kept.
    /// </summary>
    private void WriteWhole(AmEdit edit, string key, AmList old, JsonArray target, MacRecord element)
    {
        var before = new Dictionary<string, AmMap>(StringComparer.Ordinal);
        foreach (var item in old.Items)
            if (item is AmMap map && map["id"] is AmScalar { Kind: AmScalarKind.String, Text: { } id }) before.TryAdd(id, map);
        var list = AmPath.Root.Then(key);
        edit.PutObject(AmPath.Root, key, AmObjectKind.List);
        for (var j = 0; j < target.Count; j++)
        {
            var wanted = (JsonObject)target[j]!;
            edit.InsertObject(list, j, AmObjectKind.Map);
            if (JsonRead.String(wanted["id"]) is { } id && before.TryGetValue(id, out var was))
            {
                if (!AmCopy.Entries(edit, list.Then(j), was))
                    throw new MacFormatException($"{key} holds values Windows can't copy exactly; it wasn't rewritten, so nothing of it is lost");
                DiffRecord(edit, list.Then(j), was, wanted, element);
            }
            else
            {
                encoder.WriteRecordFields(edit, list.Then(j), wanted, element);
            }
        }
    }

    private void DiffAtKey(AmEdit edit, AmPath path, string key, AmNode have, JsonNode wanted, MacType type)
    {
        switch (encoder.Shape(type))
        {
            case MacEncoder.Kind.Scalar:
                if (!Same(have, wanted, type, inList: false)) edit.Put(path, key, encoder.Scalar(wanted, type));
                break;
            case MacEncoder.Kind.Record:
                if (have is AmMap map) DiffRecord(edit, path.Then(key), map, (JsonObject)wanted, schema.Record(((MacNamed)type).Name));
                else encoder.WriteAtKey(edit, path, key, type, wanted);
                break;
            case MacEncoder.Kind.List:
                if (have is AmList list) DiffList(edit, path.Then(key), list, (JsonArray)wanted, ((MacListOf)type).Element);
                else encoder.WriteAtKey(edit, path, key, type, wanted);
                break;
        }
    }

    private void DiffList(AmEdit edit, AmPath path, AmList current, JsonArray target, MacType element)
    {
        var shape = encoder.Shape(element);
        if ((long)current.Items.Count * target.Count <= MatchLimit)
        {
            if (shape == MacEncoder.Kind.Record && schema.Record(((MacNamed)element).Name).HasStringId
                && Ids(current) is { } haveIds && Ids(target) is { } wantIds)
            {
                var at = new Dictionary<string, int>(StringComparer.Ordinal);
                for (var i = 0; i < haveIds.Length; i++) at[haveIds[i]] = i;
                Matched(edit, path, current, target, element, (i, j) => haveIds[i] == wantIds[j],
                    movedFrom: j => at.TryGetValue(wantIds[j], out var i) ? i : null);
                return;
            }
            if (shape == MacEncoder.Kind.Scalar)
            {
                Matched(edit, path, current, target, element, (i, j) => Same(current.Items[i], target[j]!, element, inList: true));
                return;
            }
        }
        ByPosition(edit, path, current, target, element);
    }

    /// <summary>
    /// Keeps the longest run of elements both lists share in order (<paramref name="same"/> says
    /// which pairs match), deletes the other old ones, inserts the other new ones, then compares
    /// each kept record with its new version. A record that moved (<paramref name="movedFrom"/>
    /// finds where it was) is deleted and inserted again as a copy of itself, every key included,
    /// as the Mac's moveListElement copies it, then changed to its new version: so fields only a
    /// newer Mac knows travel with it.
    /// </summary>
    private void Matched(AmEdit edit, AmPath path, AmList current, JsonArray target, MacType element, Func<int, int, bool> same,
        Func<int, int?>? movedFrom = null)
    {
        var pairs = LongestCommonRun(current.Items.Count, target.Count, same);
        var kept = new HashSet<int>(pairs.Select(p => p.Old));
        // Delete from the end, so the earlier indexes still mean what they did.
        for (var i = current.Items.Count - 1; i >= 0; i--)
            if (!kept.Contains(i)) edit.Delete(path, i);
        // Now the kept elements sit in order at the front. Walking the target in order, each one is
        // either the next kept element (already at this position) or new (inserted here).
        var newToOld = pairs.ToDictionary(p => p.New, p => p.Old);
        for (var j = 0; j < target.Count; j++)
        {
            if (newToOld.ContainsKey(j)) continue;
            if (movedFrom?.Invoke(j) is { } was && current.Items[was] is AmMap moved)
            {
                // The positions before j are final now, so the copy and its changes can go in at once.
                edit.InsertObject(path, j, AmObjectKind.Map);
                if (!AmCopy.Entries(edit, path.Then(j), moved))
                    throw new MacFormatException("A moved item holds values Windows can't copy exactly; it wasn't moved, so nothing of it is lost");
                DiffRecord(edit, path.Then(j), moved, (JsonObject)target[j]!, schema.Record(((MacNamed)element).Name));
            }
            else
            {
                encoder.InsertAt(edit, path, j, element, target[j]!);
            }
        }
        if (encoder.Shape(element) == MacEncoder.Kind.Record)
        {
            var record = schema.Record(((MacNamed)element).Name);
            foreach (var (j, i) in newToOld.OrderBy(p => p.Key))
                if (current.Items[i] is AmMap map) DiffRecord(edit, path.Then(j), map, (JsonObject)target[j]!, record);
        }
    }

    /// <summary>Compares position by position (as the Mac's encoder writes a list), then trims or extends the end.</summary>
    private void ByPosition(AmEdit edit, AmPath path, AmList current, JsonArray target, MacType element)
    {
        var shared = Math.Min(current.Items.Count, target.Count);
        for (var i = 0; i < shared; i++)
        {
            var have = current.Items[i];
            var wanted = target[i]!;
            switch (encoder.Shape(element))
            {
                case MacEncoder.Kind.Scalar:
                    if (!Same(have, wanted, element, inList: true)) encoder.ReplaceAt(edit, path, i, element, wanted);
                    break;
                case MacEncoder.Kind.Record when have is AmMap map:
                    DiffRecord(edit, path.Then(i), map, (JsonObject)wanted, schema.Record(((MacNamed)element).Name));
                    break;
                case MacEncoder.Kind.List when have is AmList list:
                    DiffList(edit, path.Then(i), list, (JsonArray)wanted, ((MacListOf)element).Element);
                    break;
                default:
                    encoder.ReplaceAt(edit, path, i, element, wanted);
                    break;
            }
        }
        for (var i = current.Items.Count - 1; i >= shared; i--) edit.Delete(path, i);
        for (var i = shared; i < target.Count; i++) encoder.InsertAt(edit, path, i, element, target[i]!);
    }

    /// <summary>The longest common subsequence of two lists, as (old index, new index) pairs in order.</summary>
    internal static List<(int Old, int New)> LongestCommonRun(int oldCount, int newCount, Func<int, int, bool> same)
    {
        // Matching ends first keeps the table small for the usual edit (one slide changed in the middle).
        var start = 0;
        while (start < oldCount && start < newCount && same(start, start)) start++;
        var endOld = oldCount;
        var endNew = newCount;
        while (endOld > start && endNew > start && same(endOld - 1, endNew - 1))
        {
            endOld--;
            endNew--;
        }
        var pairs = new List<(int, int)>();
        for (var i = 0; i < start; i++) pairs.Add((i, i));
        var rows = endOld - start;
        var columns = endNew - start;
        if (rows > 0 && columns > 0)
        {
            // lengths[i, j]: the longest common run of old[start+i..] and new[start+j..].
            var lengths = new int[rows + 1, columns + 1];
            for (var i = rows - 1; i >= 0; i--)
                for (var j = columns - 1; j >= 0; j--)
                    lengths[i, j] = same(start + i, start + j)
                        ? lengths[i + 1, j + 1] + 1
                        : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            for (int i = 0, j = 0; i < rows && j < columns;)
            {
                if (same(start + i, start + j))
                {
                    pairs.Add((start + i, start + j));
                    i++;
                    j++;
                }
                else if (lengths[i + 1, j] >= lengths[i, j + 1]) i++;
                else j++;
            }
        }
        for (var k = 0; k < oldCount - endOld; k++) pairs.Add((endOld + k, endNew + k));
        return pairs;
    }

    /// <summary>The ids of a list of records, or null if any is missing or two are the same (then ids can't match them up).</summary>
    private static string[]? Ids(AmList list)
    {
        var ids = new string[list.Items.Count];
        for (var i = 0; i < ids.Length; i++)
        {
            if (list.Items[i] is not AmMap map || map["id"] is not AmScalar { Kind: AmScalarKind.String, Text: { } id }) return null;
            ids[i] = id;
        }
        return ids.Distinct(StringComparer.Ordinal).Count() == ids.Length ? ids : null;
    }

    private static string[]? Ids(JsonArray list)
    {
        var ids = new string[list.Count];
        for (var i = 0; i < ids.Length; i++)
        {
            if (list[i] is not JsonObject obj || !obj.TryGetPropertyValue("id", out var id) || JsonRead.String(id) is not { } text) return null;
            ids[i] = text;
        }
        return ids.Distinct(StringComparer.Ordinal).Count() == ids.Length ? ids : null;
    }

    /// <summary>
    /// True when the document already holds <paramref name="wanted"/> as the Mac's decoder would
    /// read it: no need to write it again. An enum stored under a legacy alias counts as its
    /// current case; a record's whole-number double stored as an Int counts as that double.
    /// </summary>
    private bool Same(AmNode have, JsonNode wanted, MacType type, bool inList)
    {
        if (have is not AmScalar scalar)
            return type is MacNamed named && schema.Enums.TryGetValue(named.Name, out var textEnum) && have is AmText text
                && textEnum.Decode(text.Value) == JsonRead.String(wanted);
        switch (type)
        {
            case MacPrimitive { Kind: MacPrimitiveKind.String }:
                return scalar.Kind == AmScalarKind.String && scalar.Text == JsonRead.String(wanted);
            case MacPrimitive { Kind: MacPrimitiveKind.Bool }:
                return scalar.Kind == AmScalarKind.Boolean && scalar.Bool == JsonRead.Bool(wanted);
            case MacPrimitive { Kind: MacPrimitiveKind.Int }:
                var whole = JsonRead.Integer(wanted);
                return (scalar.Kind == AmScalarKind.Int && scalar.Integer == whole)
                    || (scalar.Kind == AmScalarKind.Uint && whole >= 0 && scalar.Unsigned == (ulong)whole);
            case MacPrimitive { Kind: MacPrimitiveKind.Double }:
                var number = JsonRead.Double(wanted);
                return (scalar.Kind == AmScalarKind.F64 && scalar.Double.Equals(number))
                    || (!inList && scalar.Kind == AmScalarKind.Int && ((double)scalar.Integer).Equals(number));
            case MacNamed named when schema.Enums.TryGetValue(named.Name, out var enumType):
                return scalar.Kind == AmScalarKind.String && scalar.Text is { } raw && enumType.Decode(raw) == JsonRead.String(wanted);
            default:
                return false;
        }
    }
}

/// <summary>
/// Copies an existing part of a document, every key in it (the schema's and any other), with each
/// value's Automerge type kept. Only what the Mac's encoder writes can be copied: maps, lists,
/// strings, Ints, F64s and booleans. Anything else returns false, so the caller can refuse rather
/// than copy it wrongly.
/// </summary>
internal static class AmCopy
{
    public static bool Entries(AmEdit edit, AmPath obj, AmMap source)
    {
        foreach (var (key, value) in source.Entries)
        {
            switch (value)
            {
                case AmMap map:
                    edit.PutObject(obj, key, AmObjectKind.Map);
                    if (!Entries(edit, obj.Then(key), map)) return false;
                    break;
                case AmList list:
                    edit.PutObject(obj, key, AmObjectKind.List);
                    if (!Items(edit, obj.Then(key), list)) return false;
                    break;
                case AmScalar scalar when Write(scalar) is { } write:
                    edit.Put(obj, key, write);
                    break;
                default:
                    return false;
            }
        }
        return true;
    }

    private static bool Items(AmEdit edit, AmPath list, AmList source)
    {
        for (var i = 0; i < source.Items.Count; i++)
        {
            switch (source.Items[i])
            {
                case AmMap map:
                    edit.InsertObject(list, i, AmObjectKind.Map);
                    if (!Entries(edit, list.Then(i), map)) return false;
                    break;
                case AmList inner:
                    edit.InsertObject(list, i, AmObjectKind.List);
                    if (!Items(edit, list.Then(i), inner)) return false;
                    break;
                case AmScalar scalar when Write(scalar) is { } write:
                    edit.Insert(list, i, write);
                    break;
                default:
                    return false;
            }
        }
        return true;
    }

    private static AmWrite? Write(AmScalar scalar) => scalar.Kind switch
    {
        AmScalarKind.String => AmWrite.String(scalar.Text!),
        AmScalarKind.Int => AmWrite.Int(scalar.Integer),
        AmScalarKind.F64 => AmWrite.Double(scalar.Double),
        AmScalarKind.Boolean => AmWrite.Bool(scalar.Bool),
        _ => null,
    };
}
