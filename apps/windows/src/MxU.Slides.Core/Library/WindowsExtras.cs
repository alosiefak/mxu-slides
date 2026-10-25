using System.Security.Cryptography;
using MxU.Slides.Core.Json;

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

    /// <summary>Saves (or replaces) an extra, with the fingerprint of the shared part it goes with.</summary>
    public void Save<T>(string kind, string ownerId, T value, string? basis = null)
    {
        var path = PathOf(kind, ownerId);
        lock (gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            LibraryStore.AtomicWrite(path, MxuJson.SerializeToUtf8(new WindowsExtra<T>(value, basis)));
        }
        Changed?.Invoke(kind, ownerId);
    }

    public void Delete(string kind, string ownerId)
    {
        var path = PathOf(kind, ownerId);
        lock (gate)
        {
            if (!File.Exists(path)) return;
            File.Delete(path);
        }
        Changed?.Invoke(kind, ownerId);
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
