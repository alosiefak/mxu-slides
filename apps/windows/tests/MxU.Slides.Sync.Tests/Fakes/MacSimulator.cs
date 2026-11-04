using System.Text.Json.Nodes;
using MxU.Slides.Core.Model;
using MxU.Slides.Sync.Automerge;
using MxU.Slides.Sync.Format;

namespace MxU.Slides.Sync.Tests.Fakes;

/// <summary>
/// Plays the Mac's part, since a real Mac can't run here. It writes Automerge documents the way
/// automerge-swift's <c>AutomergeEncoder</c> does when <c>TypedDocument</c> creates or updates one
/// (a whole-value write with <c>cautiousWrite</c>), written independently of the app's own
/// encoder from the Swift source:
/// <list type="bullet">
/// <item>every present field is put, in schema order; a record reuses the map already at its key,
/// a list the list already there (AutomergeKeyedEncodingContainer, retrieveObjectId);</item>
/// <item>a list's records are written in place by position, appending maps as needed; its scalar
/// values are <i>inserted</i> at their position (AutomergeUnkeyedEncodingContainer);</item>
/// <item>then keys that weren't written are deleted, and a list's elements past the last one
/// written (postencodeCleanup);</item>
/// <item>a shape that doesn't match (a map where a list should be) throws, as the Mac's does.</item>
/// </list>
/// And it saves into a library folder the way <c>DocumentStore.save</c> does: merging the file on
/// disk first, then writing the whole document.
/// </summary>
internal static class MacSimulator
{
    private static readonly MacSchema Schema = MacSchema.Shared;

    /// <summary><c>TypedDocument(value)</c>: a new document, one change with the message "create".</summary>
    public static AmDocument Create(object model, MacKind kind, DateTimeOffset? time = null)
    {
        var document = AmDocument.Create();
        var edit = new AmEdit();
        Keyed(edit, AmPath.Root, current: null, MacDocuments.ToJson(model), Schema.Record(kind.RecordName));
        document.Apply(edit, "create", time ?? DateTimeOffset.UtcNow);
        return document;
    }

    /// <summary><c>TypedDocument.update { $0 = value }</c>: the whole value written over the document as it is.</summary>
    public static void Update(AmDocument document, object model, MacKind kind, DateTimeOffset? time = null)
    {
        var edit = new AmEdit();
        Keyed(edit, AmPath.Root, document.Read(), MacDocuments.ToJson(model), Schema.Record(kind.RecordName));
        document.Apply(edit, message: null, time ?? DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// <c>TypedDocument.updateSlideList</c>: what the Mac does when slides are added, removed or
    /// edited in the grid. Removing deletes the list elements, adding inserts new ones, and editing
    /// slides that stay in place writes just those slides; anything else is a whole-value write.
    /// </summary>
    public static void UpdateSlideList(AmDocument document, Presentation next, DateTimeOffset? time = null)
    {
        var kind = MacKind.ForWindowsType(typeof(Presentation))!;
        var current = Read<Presentation>(document, kind).Slides;
        var slides = AmPath.Root.Then("slides");
        var nextIds = next.Slides.Select(s => s.Id).ToList();
        var currentIds = current.Select(s => s.Id).ToList();
        var edit = new AmEdit();
        var slideRecord = Schema.Record("Slide");
        if (nextIds.All(currentIds.Contains) && nextIds.Count < currentIds.Count
            && currentIds.Where(nextIds.Contains).SequenceEqual(nextIds))
        {
            for (var i = currentIds.Count - 1; i >= 0; i--)
                if (!nextIds.Contains(currentIds[i])) edit.Delete(slides, i);
        }
        else if (currentIds.All(nextIds.Contains) && nextIds.Count > currentIds.Count
                 && nextIds.Where(currentIds.Contains).SequenceEqual(currentIds))
        {
            for (var i = 0; i < next.Slides.Count; i++)
            {
                if (currentIds.Contains(nextIds[i])) continue;
                edit.InsertObject(slides, i, AmObjectKind.Map);
                Keyed(edit, slides.Then(i), null, MacDocuments.ToJson(next.Slides[i]), slideRecord);
            }
        }
        else if (currentIds.SequenceEqual(nextIds))
        {
            var tree = (AmList)document.Read()["slides"]!;
            for (var i = 0; i < next.Slides.Count; i++)
                if (!Core.Json.MxuJson.StructurallyEqual(current[i], next.Slides[i]))
                    Keyed(edit, slides.Then(i), (AmMap)tree.Items[i], MacDocuments.ToJson(next.Slides[i]), slideRecord);
        }
        else
        {
            Update(document, next, kind, time);
            return;
        }
        document.Apply(edit, message: null, time ?? DateTimeOffset.UtcNow);
    }

    /// <summary>Reads a document the way the Mac does (the app's port of the decoder; see MacDecoderTests).</summary>
    public static JsonObject Read(AmDocument document, MacKind kind) =>
        MacDecoder.Shared.Decode(document.Read(), kind.RecordName) is { Value: { } value } ? value : throw new InvalidOperationException("the Mac couldn't read it");

    public static T Read<T>(AmDocument document, MacKind kind) => (T)MacDocuments.ToModel(Read(document, kind), kind);

    /// <summary><c>DocumentStore.save</c>: merge what's on disk, then write the whole document to "&lt;kind&gt;/&lt;id&gt;.automerge".</summary>
    public static void Save(AmDocument document, string libraryFolder, MacKind kind, string id)
    {
        var file = Path.Combine(libraryFolder, kind.DirectoryName, id + ".automerge");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        if (File.Exists(file))
        {
            using var disk = AmDocument.Load(File.ReadAllBytes(file));
            document.Merge(disk);
        }
        File.WriteAllBytes(file, document.Save());
    }

    /// <summary>Loads "&lt;kind&gt;/&lt;id&gt;.automerge" from a library folder.</summary>
    public static AmDocument Open(string libraryFolder, MacKind kind, string id) =>
        AmDocument.Load(File.ReadAllBytes(Path.Combine(libraryFolder, kind.DirectoryName, id + ".automerge")));

    /// <summary>The empty library folder <c>DocumentStore(rootURL:)</c> makes: a folder per kind.</summary>
    public static void MakeLibrary(string folder)
    {
        foreach (var kind in MacKind.All) Directory.CreateDirectory(Path.Combine(folder, kind.DirectoryName));
        Directory.CreateDirectory(Path.Combine(folder, "blobs"));
    }

    private static void Keyed(AmEdit edit, AmPath path, AmMap? current, JsonObject value, MacRecord record)
    {
        var written = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in record.Fields)
        {
            if (!value.TryGetPropertyValue(field.Name, out var fieldValue) || fieldValue is null) continue;
            written.Add(field.Name);
            var existing = current?[field.Name];
            switch (field.Type)
            {
                case MacListOf list:
                    if (existing is not null and not AmList) throw new InvalidOperationException($"MismatchedSchema at {path} › {field.Name}");
                    if (existing is null) edit.PutObject(path, field.Name, AmObjectKind.List);
                    Unkeyed(edit, path.Then(field.Name), existing as AmList, (JsonArray)fieldValue, list.Element);
                    break;
                case MacNamed named when Schema.Records.ContainsKey(named.Name):
                    if (existing is not null and not AmMap) throw new InvalidOperationException($"MismatchedSchema at {path} › {field.Name}");
                    if (existing is null) edit.PutObject(path, field.Name, AmObjectKind.Map);
                    Keyed(edit, path.Then(field.Name), existing as AmMap, (JsonObject)fieldValue, Schema.Record(named.Name));
                    break;
                default:
                    edit.Put(path, field.Name, Scalar(fieldValue, field.Type));
                    break;
            }
        }
        if (current is null) return;
        foreach (var key in current.Entries.Keys)
            if (!written.Contains(key)) edit.Delete(path, key);
    }

    private static void Unkeyed(AmEdit edit, AmPath path, AmList? current, JsonArray value, MacType element)
    {
        // What sits at each position as the encoder goes (it inserts scalars, so positions shift).
        var items = current?.Items.Cast<AmNode?>().ToList() ?? [];
        for (var i = 0; i < value.Count; i++)
        {
            var item = value[i]!;
            switch (element)
            {
                case MacNamed named when Schema.Records.ContainsKey(named.Name):
                    if (i < items.Count && items[i] is AmMap map)
                    {
                        Keyed(edit, path.Then(i), map, (JsonObject)item, Schema.Record(named.Name));
                    }
                    else if (i == items.Count)
                    {
                        edit.InsertObject(path, i, AmObjectKind.Map);
                        items.Insert(i, null);
                        Keyed(edit, path.Then(i), null, (JsonObject)item, Schema.Record(named.Name));
                    }
                    else
                    {
                        throw new InvalidOperationException($"MismatchedSchema at {path} › {i}");
                    }
                    break;
                case MacListOf:
                    throw new NotSupportedException("the schema has no lists of lists");
                default:
                    edit.Insert(path, i, Scalar(item, element));
                    items.Insert(i, null);
                    break;
            }
        }
        for (var last = items.Count - 1; last >= value.Count; last--) edit.Delete(path, last);
    }

    private static AmWrite Scalar(JsonNode value, MacType type)
    {
        var element = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(value);
        return type switch
        {
            MacPrimitive { Kind: MacPrimitiveKind.Int } => AmWrite.Int(element.GetInt64()),
            MacPrimitive { Kind: MacPrimitiveKind.Double } => AmWrite.Double(element.GetDouble()),
            MacPrimitive { Kind: MacPrimitiveKind.Bool } => AmWrite.Bool(element.GetBoolean()),
            _ => AmWrite.String(element.GetString()!),
        };
    }
}
