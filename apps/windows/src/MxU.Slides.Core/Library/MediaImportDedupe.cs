using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Library;

/// <summary>
/// Spots, before a file is imported, that the library already has it: the very same file
/// (same hash), or a different file under the same name. The app then asks whether to use the
/// item already there or add a new copy (numbered "Background 2", like Finder). Media and
/// music never match each other. Port of the Mac's MediaImportDedupe.
/// </summary>
public sealed class MediaImportDedupe
{
    public enum Kind
    {
        Media,
        Audio,
    }

    public abstract record Match
    {
        public sealed record SameFile(string Id) : Match;

        public sealed record SameName(string Id, string Name) : Match;

        public sealed record New : Match;
    }

    private readonly Dictionary<Kind, Dictionary<string, string>> byHash = [];
    private readonly Dictionary<Kind, Dictionary<string, (string Id, string Name)>> byName = [];

    public MediaImportDedupe(IEnumerable<MediaItem> media, IEnumerable<AudioItem> audio)
    {
        // Sorted by id, so the same library always answers the same way.
        foreach (var item in media.OrderBy(m => m.Id, StringComparer.Ordinal)) Note(item.Id, item.FileHash, item.Name, Kind.Media);
        foreach (var item in audio.OrderBy(a => a.Id, StringComparer.Ordinal)) Note(item.Id, item.FileHash, item.Name, Kind.Audio);
    }

    public Match Find(string hash, string name, Kind kind)
    {
        if (byHash.TryGetValue(kind, out var hashes) && hashes.TryGetValue(hash, out var id)) return new Match.SameFile(id);
        if (byName.TryGetValue(kind, out var names) && names.TryGetValue(Key(name), out var named)) return new Match.SameName(named.Id, named.Name);
        return new Match.New();
    }

    /// <summary>Remembers an item (one already there, or one this import just made); the first of each hash and name wins.</summary>
    public void Note(string id, string hash, string name, Kind kind)
    {
        if (!byHash.TryGetValue(kind, out var hashes)) byHash[kind] = hashes = new(StringComparer.Ordinal);
        hashes.TryAdd(hash, id);
        if (!byName.TryGetValue(kind, out var names)) byName[kind] = names = new(StringComparer.Ordinal);
        names.TryAdd(Key(name), (id, name));
    }

    /// <summary>"Background" → "Background 2" (or the next number free); "Background 2" counts on to "Background 3".</summary>
    public string AvailableName(string name, Kind kind)
    {
        var trimmed = TrimSpaces(name);
        var stem = trimmed;
        var number = 2;
        var space = trimmed.LastIndexOf(' ');
        if (space >= 0 && int.TryParse(trimmed[(space + 1)..], out var n) && n >= 1)
        {
            stem = trimmed[..space];
            number = n + 1;
        }
        var names = byName.GetValueOrDefault(kind);
        while (names?.ContainsKey(Key($"{stem} {number}")) == true) number++;
        return $"{stem} {number}";
    }

    private static string Key(string name) => TrimSpaces(name).ToLowerInvariant();

    /// <summary>Trims spaces and tabs but not line breaks, as Swift's <c>.whitespaces</c> does.</summary>
    private static string TrimSpaces(string text)
    {
        var (start, end) = (0, text.Length);
        while (start < end && IsSpace(text[start])) start++;
        while (end > start && IsSpace(text[end - 1])) end--;
        return text[start..end];
    }

    private static bool IsSpace(char c) => c == '\t' || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.SpaceSeparator;
}
