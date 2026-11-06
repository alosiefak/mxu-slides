using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Undo;

namespace MxU.Slides.Core.Library;

/// <summary>The library's side of undo: what the undo history reads and writes (Undo/UndoHistory.cs).</summary>
public sealed partial class LibraryStore
{
    /// <summary>
    /// Undo and redo for everything saved here. Each window makes a scope with
    /// <see cref="UndoHistory.NewScope"/>; saves made while it is current become its steps.
    /// </summary>
    public UndoHistory Undo { get; }

    /// <summary>A document as it is held now, as this app writes it, or null when there's none.</summary>
    internal byte[]? HeldJson(Type type, string id)
    {
        lock (gate)
            return documents[type].TryGetValue(id, out var doc) ? JsonSerializer.SerializeToUtf8Bytes(doc, type, MxuJson.Options) : null;
    }

    /// <summary>A document's JSON, read and written again the way this app writes it (unchanged if it can't be read).</summary>
    internal static byte[] Reencoded(Type type, byte[] json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, type, MxuJson.Options) is { } doc
                ? JsonSerializer.SerializeToUtf8Bytes(doc, type, MxuJson.Options)
                : json;
        }
        catch (JsonException)
        {
            return json;
        }
    }

    /// <summary>
    /// Puts a document back as <paramref name="json"/> says (null: moves it to Trash), for undo. It
    /// is written like any save and raises <see cref="Changed"/>, so whatever shows it updates.
    /// <para>
    /// The object the library holds is kept and its properties replaced, so a window holding the
    /// document (the open presentation, the selected service) sees the restored version at once.
    /// The objects inside it (slides, items) are new: re-find them by id after an undo.
    /// </para>
    /// </summary>
    internal void Restore(Type type, string id, byte[]? json)
    {
        if (json is null)
        {
            DeleteDocument(type, id);
            return;
        }
        if (JsonSerializer.Deserialize(json, type, MxuJson.Options) is not ILibraryDocument fresh || fresh.Id != id)
            throw new InvalidDataException($"The undo step's copy of {type.Name} {id} can't be read.");
        var path = DocumentPath(type, id);
        lock (gate)
        {
            var bucket = documents[type];
            ILibraryDocument doc = fresh;
            if (bucket.TryGetValue(id, out var held) && held.GetType() == fresh.GetType())
            {
                foreach (var property in WritableProperties(type)) property.SetValue(held, property.GetValue(fresh));
                doc = held;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicWrite(path, JsonSerializer.SerializeToUtf8Bytes(doc, type, MxuJson.Options));
            bucket[id] = doc;
        }
        Changed?.Invoke(type, id);
    }

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> writable = new();

    private static PropertyInfo[] WritableProperties(Type type) => writable.GetOrAdd(type, t =>
        [.. t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)]);
}
