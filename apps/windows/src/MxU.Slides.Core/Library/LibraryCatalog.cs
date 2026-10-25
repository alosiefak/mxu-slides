using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Library;

/// <summary>
/// The library as the browser lists it: every song, presentation, media item and piece of music as
/// a <see cref="LibraryEntry"/> (name, folder, dates, last use, favourite and tags), and searching
/// across all of them. It puts together what's kept in different places: the documents
/// (<see cref="LibraryStore"/>), their file dates (<see cref="LibraryDates"/>), when each was last
/// used (<see cref="LibraryUsage"/>) and the favourites and tags (<see cref="LibraryMarks"/>).
/// The Mac reads all of this from one SQLite index; Windows' documents are already in memory, so
/// an entry is cheap to make each time the list is drawn.
/// </summary>
public sealed class LibraryCatalog
{
    public LibraryCatalog(LibraryStore store, LibraryDates? dates = null, LibraryUsage? usage = null, LibraryMarks? marks = null)
    {
        Store = store;
        Dates = dates ?? new LibraryDates(store);
        Usage = usage ?? new LibraryUsage(store.Root);
        Marks = marks ?? new LibraryMarks(store);
        Index = new LibrarySearchIndex(store);
    }

    public LibraryStore Store { get; }
    public LibraryDates Dates { get; }
    public LibraryUsage Usage { get; }
    public LibraryMarks Marks { get; }
    public LibrarySearchIndex Index { get; }

    public LibraryEntry Entry(Presentation p)
    {
        var kind = LibraryEntry.KindOf(p);
        return new LibraryEntry(p.Id, kind, p.Name, p.Folder ?? "",
            Dates.UpdatedAt(typeof(Presentation), p.Id), Dates.AddedAt(typeof(Presentation), p.Id), Usage.LastUsed(p.Id),
            Marks.IsFavorite(kind, p.Id), Marks.Tags(kind, p.Id));
    }

    public LibraryEntry Entry(MediaItem m) => new(m.Id, LibraryItemKind.Media, m.Name, m.Folder ?? "",
        Dates.UpdatedAt(typeof(MediaItem), m.Id), Dates.AddedAt(m), Usage.LastUsed(m.Id), m.Favorite, m.Tags);

    public LibraryEntry Entry(AudioItem a) => new(a.Id, LibraryItemKind.Audio, a.Name, a.Folder ?? "",
        Dates.UpdatedAt(typeof(AudioItem), a.Id), Dates.AddedAt(typeof(AudioItem), a.Id), Usage.LastUsed(a.Id), a.Favorite, a.Tags);

    public IReadOnlyList<LibraryEntry> Presentations() => [.. Store.All<Presentation>().Select(Entry)];

    public IReadOnlyList<LibraryEntry> Media() => [.. Store.All<MediaItem>().Select(Entry)];

    public IReadOnlyList<LibraryEntry> Audio() => [.. Store.All<AudioItem>().Select(Entry)];

    /// <summary>
    /// Searches every song, presentation, media item and piece of music (as many kinds as
    /// <paramref name="filters"/> lets through). See <see cref="LibrarySearch"/> for the rules.
    /// </summary>
    public IReadOnlyList<LibrarySearchHit> Search(string query, LibrarySearchFilters? filters = null, int limit = LibrarySearch.Limit)
    {
        if (LibrarySearch.Words(query).Length == 0) return [];
        var kinds = filters?.Kinds;
        bool Wants(params LibraryItemKind[] wanted) => kinds is not { Count: > 0 } || wanted.Any(kinds.Contains);
        var items = new List<(LibraryEntry, LibrarySearch.Indexed)>();
        if (Wants(LibraryItemKind.Song, LibraryItemKind.Presentation))
            items.AddRange(Store.All<Presentation>().Select(p => (Entry(p), Index.For(p))));
        if (Wants(LibraryItemKind.Media))
            items.AddRange(Store.All<MediaItem>().Select(m => (Entry(m), Index.For(m))));
        if (Wants(LibraryItemKind.Audio))
            items.AddRange(Store.All<AudioItem>().Select(a => (Entry(a), Index.For(a))));
        return LibrarySearch.Search(query, items, filters, limit);
    }
}
