using System.Security.Cryptography;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Undo;

namespace MxU.Slides.Core.Library;

/// <summary>
/// One Windows-only addition to a shared document: <see cref="Value"/> (the addition itself) and
/// <see cref="Basis"/>, a fingerprint of the part of the shared document it was made against.
/// </summary>
public sealed record WindowsExtra<T>(T Value, string? Basis);

/// <summary>
/// Things only the Windows app does, attached to shared documents: a transition the Mac hasn't
/// got, a camera cut or a lighting cue on a slide. They can't go in the documents themselves:
/// the shared format's lists of kinds (transitions, slide actions…) are fixed, and the Mac
/// refuses a whole document that names a kind it doesn't know. So they're kept beside the
/// library, in "Windows extras/&lt;kind&gt;/&lt;owner id&gt;.json", where the Mac never looks.
/// <para>
/// Each extra keeps a fingerprint (<see cref="Fingerprint"/>) of the shared part it goes with:
/// for a transition, the slide's own (Mac-readable) transition. If a Mac has since changed that
/// part, the fingerprints no longer match and <see cref="Get{T}"/> gives the extra up, so the
/// Mac's edit wins instead of being silently overridden.
/// </para>
/// Windows only: the Mac has no twin.
/// </summary>
public sealed class WindowsExtras
{
    public const string FolderName = "Windows extras";

    private readonly Lock gate = new();

    public WindowsExtras(string libraryRoot) => Root = Path.Combine(libraryRoot, FolderName);

    /// <summary>The folder the extras are kept in.</summary>
    public string Root { get; }

    /// <summary>The library's undo history: an extra saved with its document undoes with it (set by LibraryStore).</summary>
    internal UndoHistory? Undo { get; init; }

    /// <summary>An extra was saved or deleted: its kind and owner id.</summary>
    public event Action<string, string>? Changed;

    /// <summary>
    /// The extra of <paramref name="kind"/> for <paramref name="ownerId"/>, or null when there's none,
    /// it can't be read, or <paramref name="basis"/> is given and differs from the one it was saved
    /// with (the shared document changed under it, on a Mac say).
    /// </summary>
    public T? Get<T>(string kind, string ownerId, string? basis = null) where T : class
    {
        var path = PathOf(kind, ownerId);
        try
        {
            if (!File.Exists(path)) return null;
            var extra = MxuJson.Deserialize<WindowsExtra<T>>(File.ReadAllBytes(path));
            return basis is not null && extra.Basis is not null && extra.Basis != basis ? null : extra.Value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>The extra of <paramref name="kind"/> for <paramref name="ownerId"/> with the fingerprint it was saved with, whatever that is; null when there's none.</summary>
    public WindowsExtra<T>? Read<T>(string kind, string ownerId) where T : class
    {
        var path = PathOf(kind, ownerId);
        try
        {
            return File.Exists(path) ? MxuJson.Deserialize<WindowsExtra<T>>(File.ReadAllBytes(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Saves (or replaces) an extra, with the fingerprint of the shared part it goes with.</summary>
    public void Save<T>(string kind, string ownerId, T value, string? basis = null)
    {
        var path = PathOf(kind, ownerId);
        var bytes = MxuJson.SerializeToUtf8(new WindowsExtra<T>(value, basis));
        UndoCapture? undoing;
        lock (gate)
        {
            undoing = Undo?.Capture(new UndoTarget.Extra(kind, ownerId), path, documentType: null);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            LibraryStore.AtomicWrite(path, bytes);
        }
        UndoHistory.Record(undoing, bytes);
        Changed?.Invoke(kind, ownerId);
    }

    public void Delete(string kind, string ownerId)
    {
        var path = PathOf(kind, ownerId);
        UndoCapture? undoing;
        lock (gate)
        {
            if (!File.Exists(path)) return;
            undoing = Undo?.Capture(new UndoTarget.Extra(kind, ownerId), path, documentType: null);
            File.Delete(path);
        }
        UndoHistory.Record(undoing, null);
        Changed?.Invoke(kind, ownerId);
    }

    /// <summary>An extra's file as it is now, or null when there's none (for undo).</summary>
    internal byte[]? FileJson(string kind, string ownerId)
    {
        var path = PathOf(kind, ownerId);
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Puts an extra back as <paramref name="json"/> says (null: removes it), for undo.</summary>
    internal void Restore(string kind, string ownerId, byte[]? json)
    {
        var path = PathOf(kind, ownerId);
        lock (gate)
        {
            if (json is null)
            {
                if (!File.Exists(path)) return;
                File.Delete(path);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                LibraryStore.AtomicWrite(path, json);
            }
        }
        Changed?.Invoke(kind, ownerId);
    }

    /// <summary>
    /// Gives <paramref name="toOwner"/> a copy of every extra <paramref name="fromOwner"/> has, of every
    /// kind: a pasted or duplicated slide keeps its Windows-only transition and its production steps,
    /// a pasted object its kinetic build. The copy keeps its fingerprint, which still matches, as the
    /// copy's shared part is the same. Returns how many were copied.
    /// </summary>
    public int CopyOwner(string fromOwner, string toOwner)
    {
        if (fromOwner == toOwner || !Directory.Exists(Root)) return 0;
        var copied = 0;
        foreach (var folder in Directory.EnumerateDirectories(Root))
        {
            var kind = Unsafe(Path.GetFileName(folder));
            if (FileJson(kind, fromOwner) is not { } json) continue;
            var path = PathOf(kind, toOwner);
            UndoCapture? undoing;
            lock (gate)
            {
                undoing = Undo?.Capture(new UndoTarget.Extra(kind, toOwner), path, documentType: null);
                LibraryStore.AtomicWrite(path, json);
            }
            UndoHistory.Record(undoing, json);
            Changed?.Invoke(kind, toOwner);
            copied++;
        }
        return copied;
    }

    /// <summary>The owner ids that have an extra of <paramref name="kind"/>.</summary>
    public IReadOnlyList<string> Owners(string kind)
    {
        var folder = Path.Combine(Root, Safe(kind));
        if (!Directory.Exists(folder)) return [];
        return [.. Directory.EnumerateFiles(folder, "*.json").Select(f => Unsafe(Path.GetFileNameWithoutExtension(f)))];
    }

    /// <summary>A short fingerprint of a shared value (its JSON), to tell later whether it has changed.</summary>
    public static string Fingerprint<T>(T? value) =>
        Convert.ToHexString(SHA256.HashData(MxuJson.SerializeToUtf8(value, indented: false)))[..16];

    private string PathOf(string kind, string ownerId) => Path.Combine(Root, Safe(kind), Safe(ownerId) + ".json");

    // Ids are usually UUIDs, but a Mac id can hold characters a Windows file name can't: those are escaped.
    private static string Safe(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '%' ? $"%{(int)c:X2}" : c.ToString()));

    private static string Unsafe(string name)
    {
        var text = new System.Text.StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (name[i] == '%' && i + 2 < name.Length && int.TryParse(name.AsSpan(i + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out var code))
            {
                text.Append((char)code);
                i += 2;
            }
            else
            {
                text.Append(name[i]);
            }
        }
        return text.ToString();
    }
}
