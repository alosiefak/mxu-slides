using System.Globalization;
using System.Text;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Library;

/// <summary>Which part of an item a search found the words in.</summary>
public enum SearchField
{
    Name,
    Number,
    Author,
    Tag,
    FileName,
    Lyrics,
}

/// <summary>One search result: the item, where it matched, and for lyrics the line it matched on.</summary>
public sealed record LibrarySearchHit(LibraryEntry Entry, SearchField Field, string? Snippet, int Tier);

/// <summary>
/// What a search is narrowed to: some kinds only (songs, presentations, media, music), favourites,
/// one tag, or one folder and what's inside it. Null or empty means no limit.
/// </summary>
public sealed record LibrarySearchFilters(
    IReadOnlySet<LibraryItemKind>? Kinds = null, bool FavoritesOnly = false, string? Tag = null, string? Folder = null)
{
    public static LibrarySearchFilters None { get; } = new();

    public bool Admits(LibraryEntry entry) =>
        (Kinds is not { Count: > 0 } kinds || kinds.Contains(entry.Kind))
        && (!FavoritesOnly || entry.Favorite)
        && (string.IsNullOrEmpty(Tag) || entry.Tags.Contains(Tag, StringComparer.OrdinalIgnoreCase))
        && (string.IsNullOrEmpty(Folder) || FolderPaths.IsUnder(entry.Folder, Folder));
}

/// <summary>
/// Searching the whole library as you type: songs by title, lyrics, CCLI number and author;
/// presentations by title and their words; media and music by name, file name and tag.
/// <para>
/// <b>Matching</b> is the Mac's (LibraryIndex.searchHits: SQLite full-text search with every
/// word a prefix): each word typed must begin a word somewhere in the item, so "amaz gr" finds
/// "Amazing Grace". Here capitals, accents and apostrophes don't matter either ("twas" finds
/// "'Twas", "jesus" finds "Jésus"), which SQLite's default tokenizer gets only partly right.
/// </para>
/// <para>
/// <b>Ranking</b> follows the Mac's tiers: an exact title first, then titles that start with what
/// was typed, then everything else; within a tier, what was used most recently first. Windows
/// splits "everything else" further, by where the words were found (title, then CCLI number,
/// author, tag, file name, then lyrics, a phrase before scattered words), since there's no bm25
/// score to order them. A typed CCLI number that matches exactly ranks with exact titles.
/// </para>
/// </summary>
public static class LibrarySearch
{
    /// <summary>The most results a search returns (the Mac's query stops at 100; Windows lists a few more).</summary>
    public const int Limit = 200;

    private const int SnippetLength = 72;

    /// <summary>The searchable parts of one item, worked out once (see <see cref="LibrarySearchIndex"/>).</summary>
    public sealed class Indexed
    {
        public required string Name { get; init; }
        public required string[] NameWords { get; init; }
        public string[] AuthorWords { get; init; } = [];
        public int? Number { get; init; }
        public string[] FileNameWords { get; init; } = [];
        /// <summary>The lyric lines as written, and the same lines normalised (for words and phrases).</summary>
        public IReadOnlyList<string> Lines { get; init; } = [];
        public IReadOnlyList<string> NormalizedLines { get; init; } = [];
        public string[] LyricWords { get; init; } = [];
    }

    public static Indexed Index(Presentation presentation)
    {
        var lines = presentation.Slides
            .SelectMany(s => s.Objects)
            .Where(o => o.Text.Length > 0)
            .SelectMany(o => o.Text.Split('\n'))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var normalized = lines.Select(Normalize).ToList();
        return new Indexed
        {
            Name = Normalize(presentation.Name),
            NameWords = Words(presentation.Name),
            AuthorWords = Words(presentation.Ccli?.Author ?? ""),
            Number = presentation.Ccli?.SongNumber,
            Lines = lines,
            NormalizedLines = normalized,
            LyricWords = normalized.SelectMany(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Distinct(StringComparer.Ordinal).ToArray(),
        };
    }

    public static Indexed Index(string name, string fileName) => new()
    {
        Name = Normalize(name),
        NameWords = Words(name),
        FileNameWords = Words(Path.GetFileNameWithoutExtension(fileName)),
    };

    /// <summary>
    /// The items that match <paramref name="query"/>, best first. An empty query matches nothing (the
    /// browser shows the folders instead), as on the Mac.
    /// </summary>
    public static IReadOnlyList<LibrarySearchHit> Search(string query, IEnumerable<(LibraryEntry Entry, Indexed Index)> items,
        LibrarySearchFilters? filters = null, int limit = Limit)
    {
        var tokens = Words(query);
        if (tokens.Length == 0) return [];
        var whole = string.Join(' ', tokens);
        var digits = query.Trim().All(char.IsAsciiDigit) && int.TryParse(query.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : (int?)null;
        filters ??= LibrarySearchFilters.None;
        var hits = new List<LibrarySearchHit>();
        foreach (var (entry, index) in items)
        {
            if (!filters.Admits(entry)) continue;
            var tags = entry.Tags.SelectMany(Words).ToArray();
            if (Rank(tokens, whole, digits, index, tags) is not { } rank) continue;
            hits.Add(new LibrarySearchHit(entry, rank.Field, rank.Field == SearchField.Lyrics ? Snippet(tokens, whole, index) : null, rank.Tier));
        }
        return hits
            .OrderBy(h => h.Tier)
            .ThenByDescending(h => h.Entry.LastUsedAt ?? DateTimeOffset.MinValue)
            .ThenBy(h => h.Entry.Name, LibrarySorts.NameOrder)
            .ThenBy(h => h.Entry.Id, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }

    /// <summary>Where an item matched and how well (lower is better), or null when some word typed isn't in it.</summary>
    private static (SearchField Field, int Tier)? Rank(string[] tokens, string whole, int? digits, Indexed index, string[] tags)
    {
        if (digits is { } number && index.Number == number) return (SearchField.Number, 1);
        if (index.Name == whole) return (SearchField.Name, 0);
        if (index.Name.StartsWith(whole, StringComparison.Ordinal)) return (SearchField.Name, 2);
        if (AllIn(tokens, index.NameWords)) return (SearchField.Name, 3);
        if (AllIn(tokens, index.AuthorWords)) return (SearchField.Author, 4);
        if (AllIn(tokens, tags)) return (SearchField.Tag, 5);
        if (AllIn(tokens, index.FileNameWords)) return (SearchField.FileName, 6);
        // The words of a phrase standing together in one line beat the same words scattered about.
        if (index.NormalizedLines.Any(line => (" " + line).Contains(" " + whole, StringComparison.Ordinal))) return (SearchField.Lyrics, 7);
        if (AllIn(tokens, index.LyricWords)) return (SearchField.Lyrics, 8);
        // Every word is somewhere, but spread over the title, the author, the words…
        var everything = index.NameWords.Concat(index.AuthorWords).Concat(tags).Concat(index.FileNameWords).Concat(index.LyricWords).ToArray();
        if (AllIn(tokens, everything))
        {
            var field = tokens.Any(t => Begins(t, index.LyricWords)) ? SearchField.Lyrics : SearchField.Name;
            return (field, 9);
        }
        return null;
    }

    private static bool AllIn(string[] tokens, string[] words) => words.Length > 0 && tokens.All(t => Begins(t, words));

    private static bool Begins(string token, string[] words)
    {
        foreach (var word in words)
            if (word.StartsWith(token, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// The lyric line the search matched, as written, cut down around the match with "…" when it's
    /// long (the Mac shows an FTS snippet, eight words around the match).
    /// </summary>
    private static string? Snippet(string[] tokens, string whole, Indexed index)
    {
        var at = -1;
        for (var i = 0; i < index.NormalizedLines.Count && at < 0; i++)
            if ((" " + index.NormalizedLines[i]).Contains(" " + whole, StringComparison.Ordinal)) at = i;
        for (var i = 0; i < index.NormalizedLines.Count && at < 0; i++)
            if (Begins(tokens[0], index.NormalizedLines[i].Split(' '))) at = i;
        if (at < 0) return null;
        var line = index.Lines[at];
        if (line.Length <= SnippetLength) return line;
        // Centre the cut on where the first word typed starts in the original line (accents and all).
        var start = FindWordStart(line, tokens[0]);
        var from = Math.Clamp(start - SnippetLength / 3, 0, line.Length - SnippetLength);
        var cut = line.Substring(from, SnippetLength).Trim();
        return (from > 0 ? "…" : "") + cut + (from + SnippetLength < line.Length ? "…" : "");
    }

    private static int FindWordStart(string line, string token)
    {
        for (var i = 0; i < line.Length; i++)
            if ((i == 0 || !char.IsLetterOrDigit(line[i - 1])) && Normalize(line[i..]).StartsWith(token, StringComparison.Ordinal)) return i;
        return 0;
    }

    /// <summary>
    /// Text as search compares it: lower case, accents taken off ("é" is "e"), apostrophes dropped
    /// ("'Twas" is "twas"), and anything that isn't a letter or a digit a single space.
    /// </summary>
    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var space = true;
        foreach (var c in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark) continue;
            if (c is '\'' or '’' or '‘' or 'ʼ') continue;
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
                space = false;
            }
            else if (!space)
            {
                builder.Append(' ');
                space = true;
            }
        }
        return builder.ToString().TrimEnd();
    }

    public static string[] Words(string text) => Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>
/// The search's working copy of every item's words, made the first time each is searched and
/// thrown away when it's saved (<see cref="LibraryStore.Changed"/>), so typing in the search box
/// never re-reads a thousand songs' lyrics. The Mac keeps the same thing as an SQLite table.
/// </summary>
public sealed class LibrarySearchIndex
{
    private readonly LibraryStore store;
    private readonly Lock gate = new();
    private readonly Dictionary<string, LibrarySearch.Indexed> cache = new(StringComparer.Ordinal);

    public LibrarySearchIndex(LibraryStore store)
    {
        this.store = store;
        store.Changed += (type, id) =>
        {
            lock (gate) cache.Remove(Key(type, id));
        };
    }

    public LibrarySearch.Indexed For(Presentation presentation) =>
        Get(typeof(Presentation), presentation.Id, () => LibrarySearch.Index(presentation));

    public LibrarySearch.Indexed For(MediaItem item) => Get(typeof(MediaItem), item.Id, () => LibrarySearch.Index(item.Name, item.FileName));

    public LibrarySearch.Indexed For(AudioItem item) => Get(typeof(AudioItem), item.Id, () => LibrarySearch.Index(item.Name, item.FileName));

    /// <summary>
    /// Forgets one item (or, with no id, everything): for edits made to a document in place before
    /// it's saved, which is how the editors work.
    /// </summary>
    public void Forget(string? id = null)
    {
        lock (gate)
        {
            if (id is null) cache.Clear();
            else foreach (var key in cache.Keys.Where(k => k.EndsWith("/" + id, StringComparison.Ordinal)).ToList()) cache.Remove(key);
        }
    }

    private LibrarySearch.Indexed Get(Type type, string id, Func<LibrarySearch.Indexed> make)
    {
        var key = Key(type, id);
        lock (gate)
        {
            if (cache.TryGetValue(key, out var found)) return found;
        }
        var made = make();
        lock (gate) cache[key] = made;
        return made;
    }

    private static string Key(Type type, string id) => type.Name + "/" + id;

    /// <summary>The store this index reads (for <see cref="LibraryCatalog"/>).</summary>
    internal LibraryStore Store => store;
}
