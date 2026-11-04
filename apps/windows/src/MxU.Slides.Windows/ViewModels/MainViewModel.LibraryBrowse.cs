using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.Views;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// The library list (Views/LibraryPanel.xaml): folders you can go into, sorting, search across the
/// whole library, favourites and tags. The rules are Core's (LibraryBrowse.cs, LibrarySearch.cs,
/// LibraryMarks.cs: ports of the Mac's LibraryViewOptions, ListMultiSelect and LibraryBrowseLogic);
/// this works out the rows to show and keeps them fresh. Batch actions are
/// MainViewModel.LibraryBatch.cs.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The name the sort and the kept-empty folders are saved under, as the Mac's "library.sort.presentations".</summary>
    public const string PresentationsList = "presentations";

    private LibraryCatalog? catalog;

    /// <summary>
    /// The library as the browser sees it. Made on first use; its file dates and last-used times are
    /// read on the library's own background thread, and the list sorts again when they land.
    /// </summary>
    public LibraryCatalog Catalog
    {
        get
        {
            if (catalog is not null) return catalog;
            catalog = new LibraryCatalog(Library);
            catalog.Dates.Loaded += () => OnUi(() => RefreshLibrary());
            catalog.Marks.Changed += (kind, _) => OnUi(() =>
            {
                if (kind is LibraryItemKind.Media) RefreshMedia();
                else if (kind is LibraryItemKind.Audio) RefreshAudio();
                else RefreshLibrary();
            });
            var usage = catalog.Usage;
            _ = catalog.Dates.LoadAsync();
            LibraryChores.Queue.Async(() =>
            {
                usage.Load();
                OnUi(() => RefreshLibrary());
            });
            return catalog;
        }
    }

    private static void OnUi(Action action)
    {
        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess()) dispatcher.BeginInvoke(action);
        else action();
    }

    // MARK: The list

    /// <summary>The rows the list shows: folders, then songs and presentations (or search results: those and media).</summary>
    public ObservableCollection<object> LibraryRows { get; } = [];

    /// <summary>The songs and presentations among the rows (what the rest of the app looks for).</summary>
    public ObservableCollection<LibraryEntryViewModel> LibraryItems { get; } = [];

    public ObservableCollection<LibraryCrumb> LibraryCrumbs { get; } = [];

    [ObservableProperty]
    public partial string LibrarySearch { get; set; } = "";

    /// <summary>0 = everything, 1 = songs, 2 = presentations, 3 = media and music (search only).</summary>
    [ObservableProperty]
    public partial int LibraryFilter { get; set; }

    [ObservableProperty]
    public partial bool LibraryFavoritesOnly { get; set; }

    /// <summary>Only items with this tag; null for any.</summary>
    [ObservableProperty]
    public partial string? LibraryTag { get; set; }

    [ObservableProperty]
    public partial LibraryEntryViewModel? SelectedLibraryEntry { get; set; }

    /// <summary>The folder the list is in (empty at the top).</summary>
    public IReadOnlyList<string> LibraryPath { get; private set; } = [];

    /// <summary>True while the list shows everything in one go, folders or not ("All songs and presentations").</summary>
    [ObservableProperty]
    public partial bool LibraryShowsAll { get; set; }

    /// <summary>"12 results" or "Hymns · 8 items", under the search box.</summary>
    [ObservableProperty]
    public partial string LibraryStatus { get; set; } = "";

    public LibrarySort LibrarySortOrder
    {
        get => Settings.SortFor(PresentationsList);
        set
        {
            if (value == LibrarySortOrder) return;
            (Settings.LibrarySorts ??= [])[PresentationsList] = value;
            Settings.Save();
            OnPropertyChanged();
            RefreshLibrary();
        }
    }

    public bool IsLibrarySearching => LibrarySearch.Trim().Length > 0;

    /// <summary>True when a filter (favourites, a tag) is narrowing the list, so it shows everything that passes, folders or not.</summary>
    public bool IsLibraryFiltered => LibraryFavoritesOnly || LibraryTag is not null;

    partial void OnLibrarySearchChanged(string value) => RefreshLibrary();

    partial void OnLibraryFilterChanged(int value) => RefreshLibrary();

    partial void OnLibraryFavoritesOnlyChanged(bool value) => RefreshLibrary();

    partial void OnLibraryTagChanged(string? value) => RefreshLibrary();

    partial void OnLibraryShowsAllChanged(bool value) => RefreshLibrary();

    private bool suppressLibrarySelection;

    partial void OnSelectedLibraryEntryChanged(LibraryEntryViewModel? value)
    {
        if (value is null || suppressLibrarySelection) return;
        SelectedServiceItem = null;
        Open(value.Presentation, null, null);
    }

    /// <summary>Goes into a folder (or, with an empty path, back to the top).</summary>
    public void OpenLibraryFolder(IReadOnlyList<string> path)
    {
        LibraryPath = path;
        LibraryShowsAll = false;
        RefreshLibrary();
    }

    /// <summary>The kinds a search looks through, from the All / Songs / Presentations / Media choice.</summary>
    private IReadOnlySet<LibraryItemKind>? SearchKinds() => LibraryFilter switch
    {
        1 => new HashSet<LibraryItemKind> { LibraryItemKind.Song },
        2 => new HashSet<LibraryItemKind> { LibraryItemKind.Presentation },
        3 => new HashSet<LibraryItemKind> { LibraryItemKind.Media, LibraryItemKind.Audio },
        _ => null,
    };

    private bool AdmitsKind(LibraryEntry entry) => LibraryFilter switch
    {
        1 => entry.Kind == LibraryItemKind.Song,
        2 => entry.Kind == LibraryItemKind.Presentation,
        3 => false,
        _ => true,
    };

    /// <summary>
    /// Rebuilds the rows. Searching: the best matches across the library. Filtered by a favourite or
    /// tag, or showing all: every song and presentation that passes, sorted. Otherwise: the folders
    /// inside this one (with how many are in each), then what's filed here, sorted.
    /// </summary>
    public void RefreshLibrary()
    {
        var selectedId = SelectedLibraryEntry?.Presentation.Id;
        var rows = new List<object>();
        var query = LibrarySearch.Trim();
        var presentations = Library.All<Presentation>().ToDictionary(p => p.Id);
        if (query.Length > 0)
        {
            var hits = Catalog.Search(query, new LibrarySearchFilters(SearchKinds(), LibraryFavoritesOnly, LibraryTag));
            foreach (var hit in hits)
            {
                if (hit.Entry.IsPresentation && presentations.TryGetValue(hit.Entry.Id, out var p))
                    rows.Add(new LibraryEntryViewModel(p) { Entry = hit.Entry, Snippet = hit.Snippet, MatchNote = MatchNote(hit, p), ShowsFolder = true });
                else if (!hit.Entry.IsPresentation)
                    rows.Add(new LibraryMediaHitViewModel(hit.Entry, MediaDetail(hit.Entry), hit.Field));
            }
            LibraryStatus = hits.Count == 0 ? "Nothing matches." : hits.Count == 1 ? "1 result" : $"{hits.Count} results{(hits.Count >= Core.Library.LibrarySearch.Limit ? " (the best)" : "")}";
        }
        else
        {
            var entries = Catalog.Presentations().Where(AdmitsKind).ToList();
            if (LibraryFavoritesOnly) entries = entries.Where(e => e.Favorite).ToList();
            if (LibraryTag is { } tag) entries = entries.Where(e => e.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)).ToList();
            var sort = LibrarySortOrder;
            var flat = LibraryShowsAll || IsLibraryFiltered;
            if (flat)
            {
                foreach (var entry in sort.Ordered(entries)) rows.Add(new LibraryEntryViewModel(presentations[entry.Id]) { Entry = entry, ShowsFolder = true });
                LibraryStatus = entries.Count == 1 ? "1 item" : $"{entries.Count} items";
            }
            else
            {
                // A folder that's gone (renamed on another PC, its last item deleted) takes the list back up to the top.
                var folders = LibraryFolders.Folders(entries, Settings.EmptyFolders(PresentationsList));
                if (LibraryPath.Count > 0 && !folders.Any(f => FolderPaths.IsUnder(f, string.Join("/", LibraryPath)))) LibraryPath = [];
                if (LibraryPath.Count == 0 && folders.Count > 0) rows.Add(new LibraryAllRowViewModel(entries.Count));
                foreach (var (name, count) in LibraryFolders.ChildFolders(folders, entries.Select(e => e.Folder), LibraryPath))
                    rows.Add(new LibraryFolderRowViewModel(name, count, [.. LibraryPath, name]));
                var here = sort.Ordered(LibraryFolders.ItemsAt(entries, LibraryPath)).ToList();
                foreach (var entry in here) rows.Add(new LibraryEntryViewModel(presentations[entry.Id]) { Entry = entry });
                LibraryStatus = LibraryPath.Count == 0 ? "" : here.Count == 1 ? "1 item here" : $"{here.Count} items here";
            }
        }

        UpdateCrumbs();
        suppressLibrarySelection = true;
        LibraryRows.Clear();
        foreach (var row in rows) LibraryRows.Add(row);
        LibraryItems.Clear();
        foreach (var entry in rows.OfType<LibraryEntryViewModel>()) LibraryItems.Add(entry);
        SelectedLibraryEntry = selectedId is null ? null : LibraryItems.FirstOrDefault(e => e.Presentation.Id == selectedId);
        suppressLibrarySelection = false;
        OnPropertyChanged(nameof(IsLibrarySearching));
        OnPropertyChanged(nameof(IsLibraryFiltered));
        LibraryRowsChanged?.Invoke();
    }

    /// <summary>The rows were rebuilt (the panel puts back the rows picked for a batch).</summary>
    public event Action? LibraryRowsChanged;

    private void UpdateCrumbs()
    {
        LibraryCrumbs.Clear();
        if (IsLibrarySearching) return;
        if (LibraryShowsAll || IsLibraryFiltered)
        {
            LibraryCrumbs.Add(new LibraryCrumb("Library", []));
            LibraryCrumbs.Add(new LibraryCrumb(IsLibraryFiltered ? "Filtered" : "Everything", []));
            return;
        }
        if (LibraryPath.Count == 0) return;
        LibraryCrumbs.Add(new LibraryCrumb("Library", []));
        for (var i = 0; i < LibraryPath.Count; i++) LibraryCrumbs.Add(new LibraryCrumb(LibraryPath[i], [.. LibraryPath.Take(i + 1)]));
    }

    private static string? MatchNote(LibrarySearchHit hit, Presentation p) => hit.Field switch
    {
        SearchField.Number => $"CCLI {p.Ccli?.SongNumber}",
        SearchField.Author => null, // the author is on the line already
        SearchField.Tag => "tag: " + string.Join(", ", hit.Entry.Tags),
        _ => null,
    };

    private string MediaDetail(LibraryEntry entry)
    {
        if (entry.Kind == LibraryItemKind.Audio) return "Music";
        return Library.Get<MediaItem>(entry.Id) is { } item
            ? MediaUsage.Detail(item.MediaKind, item.PixelWidth, item.PixelHeight, item.DurationSeconds, Library.BlobPath(item) is null)
            : "Media";
    }

    /// <summary>Every tag used anywhere, for the filter and the Tag › menus.</summary>
    public IReadOnlyList<string> LibraryTags() => Catalog.Marks.AllTags();

    /// <summary>The library's folders, for "Move to Folder ›".</summary>
    public IReadOnlyList<FolderNode> PresentationFolderTree() =>
        LibraryFolders.Tree(LibraryFolders.Folders(Catalog.Presentations(), Settings.EmptyFolders(PresentationsList)));

    // MARK: A search result that's media

    /// <summary>Shows a picture, video or song a search found: in its bin, picked out.</summary>
    public void ShowLibraryHit(LibraryMediaHitViewModel hit)
    {
        if (hit.IsAudio)
        {
            ShowingMusic = true;
            return;
        }
        ShowingMedia = true;
        MediaSearch = hit.Name;
        SelectedMedia = MediaItems.FirstOrDefault(m => m.Item.Id == hit.Entry.Id);
        Notice = $"“{hit.Name}” is in the media bin below. Clear the media search to see everything again.";
    }

    // MARK: When things were last used

    private HashSet<string> liveIds = new(StringComparer.Ordinal);

    /// <summary>
    /// Called whenever what's live changes (MarkLiveTiles): a presentation or media item that has
    /// just gone on screen counts as used now, for the Recently Used sort (the Mac's markUsed on fire).
    /// </summary>
    private void NoteLiveUse()
    {
        var now = new HashSet<string>(StringComparer.Ordinal);
        if (Show.Live?.Presentation?.Id is { } presentation) now.Add(presentation);
        foreach (var media in Show.LiveMedia.Values) if (media.MediaId is { Length: > 0 } id) now.Add(id);
        foreach (var id in now.Except(liveIds)) Catalog.Usage.Touch(id);
        liveIds = now;
    }

    // MARK: Many changes at once

    private int libraryBatchDepth;
    private readonly HashSet<Type> deferredLibraryKinds = [];

    /// <summary>
    /// While a batch saves many documents (a restore, moving fifty songs), each save would redraw
    /// the lists. Inside one of these, changes are only noted, and each list redraws once at the end.
    /// </summary>
    public IDisposable BatchOfChanges()
    {
        libraryBatchDepth++;
        return new Ending(() =>
        {
            // The batch's saves may have come from another thread: their notes are queued on the UI
            // thread ahead of this, so ending it after them lets them land in the batch first.
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (--libraryBatchDepth > 0) return;
                var kinds = deferredLibraryKinds.ToList();
                deferredLibraryKinds.Clear();
                foreach (var kind in kinds) OnLibraryChanged(kind, "");
            }, System.Windows.Threading.DispatcherPriority.Background);
        });
    }

    /// <summary>Called first by OnLibraryChanged (on the UI thread): true when a batch is running and the change was noted for its end.</summary>
    private bool DeferLibraryChange(Type type)
    {
        if (libraryBatchDepth == 0) return false;
        deferredLibraryKinds.Add(type);
        return true;
    }

    private sealed class Ending(Action end) : IDisposable
    {
        private Action? end = end;

        public void Dispose()
        {
            end?.Invoke();
            end = null;
        }
    }
}
