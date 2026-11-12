using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Library;

/// <summary>
/// Favourites and tags. Media items and music already have shared fields for them
/// (<c>favorite</c> and <c>tags</c>, which the Mac shows and edits too), so those are kept there and
/// travel to a Mac. Presentations have no such field, and the shared format can't gain one (a Mac
/// refuses a document with a field it doesn't know, HOW-IT-WORKS section 41), so a song's
/// favourite and tags are a Windows extra beside it: kind <see cref="ExtraKind"/>, owner the
/// presentation's id. They have no fingerprint: nothing a Mac does to a song should drop the star
/// someone gave it here.
/// <para>
/// The extras are read once into memory (a library list of a thousand songs can't read a thousand
/// files every time it's drawn) and written through as they change. Windows only (the Mac's songs
/// have no favourites).
/// </para>
/// </summary>
public sealed class LibraryMarks
{
    public const string ExtraKind = "library-marks";

    /// <summary>The longest tag kept (longer ones are cut): a tag is a word or two, not a note.</summary>
    public const int MaxTagLength = 40;

    /// <summary>What a presentation's extra holds.</summary>
    public sealed record Marks(bool Favorite, List<string> Tags);

    private readonly LibraryStore store;
    private readonly Lock gate = new();
    private Dictionary<string, Marks>? presentations;

    public LibraryMarks(LibraryStore store) => this.store = store;

    /// <summary>A favourite or tags changed: the item's kind and id.</summary>
    public event Action<LibraryItemKind, string>? Changed;

    public bool IsFavorite(LibraryItemKind kind, string id) => kind switch
    {
        LibraryItemKind.Media => store.Get<MediaItem>(id)?.Favorite == true,
        LibraryItemKind.Audio => store.Get<AudioItem>(id)?.Favorite == true,
        _ => PresentationMarks(id)?.Favorite == true,
    };

    public IReadOnlyList<string> Tags(LibraryItemKind kind, string id) => kind switch
    {
        LibraryItemKind.Media => store.Get<MediaItem>(id)?.Tags ?? [],
        LibraryItemKind.Audio => store.Get<AudioItem>(id)?.Tags ?? [],
        _ => PresentationMarks(id)?.Tags ?? [],
    };

    public void SetFavorite(LibraryItemKind kind, string id, bool favorite) =>
        Update(kind, id, marks => marks with { Favorite = favorite });

    public void SetTags(LibraryItemKind kind, string id, IEnumerable<string> tags) =>
        Update(kind, id, marks => marks with { Tags = Clean(tags) });

    /// <summary>Adds (or, with <paramref name="add"/> false, takes off) one tag on each item: the batch "Tag ›" menu.</summary>
    public void Tag(IEnumerable<(LibraryItemKind Kind, string Id)> items, string tag, bool add)
    {
        if (Clean([tag]) is not [var clean]) return;
        foreach (var (kind, id) in items)
            Update(kind, id, marks => marks with
            {
                Tags = add ? Clean(marks.Tags.Append(clean)) : marks.Tags.Where(t => !string.Equals(t, clean, StringComparison.OrdinalIgnoreCase)).ToList(),
            });
    }

    /// <summary>Every tag in use on songs, presentations, media and music, A–Z (the first spelling of each wins).</summary>
    public IReadOnlyList<string> AllTags()
    {
        var all = store.All<MediaItem>().SelectMany(m => m.Tags)
            .Concat(store.All<AudioItem>().SelectMany(a => a.Tags))
            .Concat(LoadedPresentations().Values.SelectMany(m => m.Tags));
        return Clean(all).Order(LibrarySorts.NameOrder).ToList();
    }

    /// <summary>
    /// Tags as typed, tidied: trimmed, inner runs of spaces made one, cut to <see cref="MaxTagLength"/>,
    /// blanks dropped, and each kept once whatever its capitals ("Easter" and "easter" are one tag).
    /// </summary>
    public static List<string> Clean(IEnumerable<string> tags)
    {
        var kept = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in tags)
        {
            var tag = string.Join(' ', (raw ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (tag.Length > MaxTagLength) tag = tag[..MaxTagLength].TrimEnd();
            if (tag.Length > 0 && seen.Add(tag)) kept.Add(tag);
        }
        return kept;
    }

    /// <summary>Tags typed in one box, separated by commas: "Easter, Christmas ,  hymn".</summary>
    public static List<string> Parse(string typed) => Clean((typed ?? "").Split(','));

    /// <summary>A presentation was deleted or duplicated: its extra goes, or the copy gets the same marks.</summary>
    public void Forget(string presentationId)
    {
        lock (gate) LoadedPresentations().Remove(presentationId);
        store.Extras.Delete(ExtraKind, presentationId);
    }

    public void Copy(string fromPresentationId, string toPresentationId)
    {
        if (PresentationMarks(fromPresentationId) is { } marks)
            SavePresentationMarks(toPresentationId, marks with { Tags = [.. marks.Tags] });
    }

    private Marks? PresentationMarks(string id)
    {
        lock (gate) return LoadedPresentations().GetValueOrDefault(id);
    }

    private void Update(LibraryItemKind kind, string id, Func<Marks, Marks> change)
    {
        switch (kind)
        {
            case LibraryItemKind.Media when store.Get<MediaItem>(id) is { } media:
            {
                var next = change(new Marks(media.Favorite, [.. media.Tags]));
                if (next.Favorite == media.Favorite && next.Tags.SequenceEqual(media.Tags)) return;
                media.Favorite = next.Favorite;
                media.Tags = next.Tags;
                store.Save(media);
                break;
            }
            case LibraryItemKind.Audio when store.Get<AudioItem>(id) is { } audio:
            {
                var next = change(new Marks(audio.Favorite, [.. audio.Tags]));
                if (next.Favorite == audio.Favorite && next.Tags.SequenceEqual(audio.Tags)) return;
                audio.Favorite = next.Favorite;
                audio.Tags = next.Tags;
                store.Save(audio);
                break;
            }
            case LibraryItemKind.Song or LibraryItemKind.Presentation:
            {
                var old = PresentationMarks(id) ?? new Marks(false, []);
                var next = change(old);
                if (next.Favorite == old.Favorite && next.Tags.SequenceEqual(old.Tags)) return;
                SavePresentationMarks(id, next);
                break;
            }
            default:
                return;
        }
        Changed?.Invoke(kind, id);
    }

    private void SavePresentationMarks(string id, Marks marks)
    {
        lock (gate) LoadedPresentations()[id] = marks;
        // An unmarked song needs no file at all.
        if (!marks.Favorite && marks.Tags.Count == 0) store.Extras.Delete(ExtraKind, id);
        else store.Extras.Save(ExtraKind, id, marks);
    }

    /// <summary>Every song's marks, read from the extras folder the first time they're wanted.</summary>
    private Dictionary<string, Marks> LoadedPresentations()
    {
        lock (gate)
        {
            if (presentations is not null) return presentations;
            presentations = new Dictionary<string, Marks>(StringComparer.Ordinal);
            foreach (var id in store.Extras.Owners(ExtraKind))
                if (store.Extras.Get<Marks>(ExtraKind, id) is { } marks) presentations[id] = marks with { Tags = Clean(marks.Tags ?? []) };
            return presentations;
        }
    }

    /// <summary>Forgets what was read, so the next look reads the extras again (after a restore replaced them).</summary>
    public void Reload()
    {
        lock (gate) presentations = null;
    }
}
