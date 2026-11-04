using CommunityToolkit.Mvvm.ComponentModel;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.Views;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// The media bin's sorting, folders, favourites and tags, and picking several tiles (Ctrl+click,
/// Shift+click) for one action: add to the service, file into a folder, duplicate, favourite, tag,
/// copy the files out, delete. Folders and favourites are the media item's own shared fields, so a
/// Mac sees them too. Selection follows the Mac's ListMultiSelect (Core/Library/LibraryBrowse.cs).
/// </summary>
public sealed partial class MainViewModel
{
    public const string MediaList = "media";

    /// <summary>The bin's order. New media first unless chosen otherwise (the bin always opened that way).</summary>
    public LibrarySort MediaSortOrder
    {
        get => Settings.LibrarySorts is { } sorts && sorts.TryGetValue(MediaList, out var sort) ? sort : LibrarySort.DateAdded;
        set
        {
            if (value == MediaSortOrder) return;
            (Settings.LibrarySorts ??= [])[MediaList] = value;
            Settings.Save();
            OnPropertyChanged();
            RefreshMedia();
        }
    }

    /// <summary>Only media filed in this folder (and its subfolders); null for all.</summary>
    [ObservableProperty]
    public partial string? MediaFolder { get; set; }

    [ObservableProperty]
    public partial bool MediaFavoritesOnly { get; set; }

    partial void OnMediaFolderChanged(string? value) => RefreshMedia();

    partial void OnMediaFavoritesOnlyChanged(bool value) => RefreshMedia();

    /// <summary>
    /// The media the bin shows: matching the search (by name, file name or tag), in the chosen folder,
    /// favourites only when asked, in the chosen order. RefreshMedia (MainViewModel.Media.cs) asks here.
    /// </summary>
    private IEnumerable<MediaItem> MediaForBin(string query)
    {
        var words = Core.Library.LibrarySearch.Words(query);
        var items = Library.All<MediaItem>().Where(m =>
            (MediaFolder is null || FolderPaths.IsUnder(m.Folder ?? "", MediaFolder))
            && (!MediaFavoritesOnly || m.Favorite)
            && (words.Length == 0 || words.All(word => Core.Library.LibrarySearch.Words($"{m.Name} {Path.GetFileNameWithoutExtension(m.FileName)} {string.Join(' ', m.Tags)}").Any(w => w.StartsWith(word, StringComparison.Ordinal)))))
            .ToDictionary(m => m.Id);
        return MediaSortOrder.Ordered(items.Values.Select(Catalog.Entry)).Select(e => items[e.Id]);
    }

    /// <summary>The media folders in use or kept empty, for the bin's folder picker and "Move to Folder ›".</summary>
    public IReadOnlyList<string> MediaBinFolders() => LibraryFolders.Folders(Catalog.Media(), Settings.EmptyFolders(MediaList));

    public string? NewMediaFolder(string typed)
    {
        if (LibraryFolders.CleanName(typed) is not { } name) return null;
        var folders = (Settings.LibraryEmptyFolders ??= []);
        var kept = folders.GetValueOrDefault(MediaList) ?? [];
        if (!MediaBinFolders().Contains(name)) folders[MediaList] = [.. kept, name];
        Settings.Save();
        return name;
    }

    // MARK: Picking tiles

    private ListMultiSelect.State mediaPicks = ListMultiSelect.State.Empty;

    /// <summary>A click on a tile with Ctrl (add or remove one) or Shift (everything from the last one picked).</summary>
    public void PickMedia(MediaTileViewModel tile, ListMultiSelect.Gesture gesture)
    {
        mediaPicks = ListMultiSelect.Click(tile.Item.Id, gesture, MediaItems.Select(m => m.Item.Id).ToList(), mediaPicks);
        ShowMediaPicks();
    }

    public void ClearMediaPicks()
    {
        mediaPicks = ListMultiSelect.State.Empty;
        ShowMediaPicks();
    }

    /// <summary>After the bin redraws: the tiles still there stay picked.</summary>
    private void ShowMediaPicks()
    {
        var kept = mediaPicks.Selected.Where(id => MediaItems.Any(m => m.Item.Id == id)).ToHashSet(StringComparer.Ordinal);
        if (kept.Count != mediaPicks.Selected.Count) mediaPicks = new ListMultiSelect.State(kept, mediaPicks.Anchor);
        // One tile on its own is just the one last clicked: only a batch of several is outlined.
        foreach (var tile in MediaItems) tile.IsPicked = mediaPicks.Selected.Count > 1 && mediaPicks.Selected.Contains(tile.Item.Id);
    }

    /// <summary>What a right-click on <paramref name="clicked"/> acts on: every picked tile when it's one of them, else just it.</summary>
    public IReadOnlyList<MediaItem> MediaBatch(MediaTileViewModel clicked) =>
        [.. ListMultiSelect.Batch(clicked.Item.Id, mediaPicks.Selected, MediaItems.Select(m => m.Item.Id).ToList())
            .Select(id => Library.Get<MediaItem>(id)).OfType<MediaItem>()];

    // MARK: Batch actions

    public void AddMediaItemsToService(IReadOnlyList<MediaItem> items)
    {
        foreach (var item in items) AddMediaToService(item);
        if (items.Count > 1) Notice = $"Added {items.Count} items to the service.";
    }

    public void MoveMediaToFolder(IReadOnlyList<MediaItem> items, string? folder)
    {
        using (BatchOfChanges())
            foreach (var item in items)
                if (Core.Library.LibraryBatch.File(item, folder)) Library.Save(item);
        if (folder is { Length: > 0 } && Settings.LibraryEmptyFolders?.GetValueOrDefault(MediaList) is { } kept)
        {
            Settings.LibraryEmptyFolders[MediaList] = [.. kept.Where(f => f != folder)];
            Settings.Save();
        }
        Notice = folder is { Length: > 0 } ? $"Moved {(items.Count == 1 ? "it" : items.Count + " items")} to {folder}." : $"Moved {(items.Count == 1 ? "it" : items.Count + " items")} out of {(items.Count == 1 ? "its folder" : "their folders")}.";
    }

    public void DuplicateMedia(IReadOnlyList<MediaItem> items)
    {
        using (BatchOfChanges())
            foreach (var item in items) Library.Save(Core.Library.LibraryBatch.Duplicate(item, DateTimeOffset.Now));
        Notice = items.Count == 1 ? $"Made a copy of “{items[0].Name}”." : $"Made {items.Count} copies.";
    }

    /// <summary>
    /// Deletes several, saying first where any of them is still used (the warning one item already
    /// gives, for all of them together). One item goes through the single delete as before.
    /// </summary>
    public async Task<bool> DeleteMediaItemsAsync(IReadOnlyList<MediaItem> items)
    {
        if (items.Count == 1) return await DeleteMediaAsync(items[0]);
        var ids = items.Select(i => i.Id).ToList();
        var uses = await OffUiThread(() =>
        {
            var documents = MediaUsage.Documents(Library);
            return ids.SelectMany(id => MediaUsage.Uses(id, documents)).DistinctBy(u => (u.Kind, u.Id)).ToList();
        });
        if (!LibraryDepthDialogs.ConfirmDeleteMedia(items.Select(i => i.Name).ToList(), MediaUsage.Groups(uses))) return false;
        using (BatchOfChanges())
            foreach (var item in items)
            {
                Library.Delete<MediaItem>(item.Id);
                Media.ForgetFile(item.Id);
            }
        ClearMediaPicks();
        RebuildSlides();
        RefreshServiceItems();
        Notice = $"Moved {items.Count} media items to the library's Trash folder. The files themselves are kept.";
        return true;
    }

    /// <summary>Copies the media files out to a folder, under their own file names (numbered when two share one).</summary>
    public async Task ExportMediaFilesAsync(IReadOnlyList<MediaItem> items, string folder)
    {
        var copies = items.Select(i => (Item: i, Path: Library.BlobPath(i))).ToList();
        var missing = copies.Where(c => c.Path is null).Select(c => c.Item.Name).ToList();
        var present = copies.Where(c => c.Path is not null).ToList();
        // Each file keeps its own name and extension; a clash (two "IMG_0001.jpg", or one already in the folder) is numbered.
        var taken = new HashSet<string>(Directory.Exists(folder) ? Directory.EnumerateFiles(folder).Select(f => Path.GetFileName(f)) : [], StringComparer.OrdinalIgnoreCase);
        var names = present.Select(c =>
        {
            var extension = Path.GetExtension(c.Item.FileName) is { Length: > 0 } e ? e : Path.GetExtension(c.Path!);
            var stem = Core.Library.LibraryBatch.SafeFileStem(Path.GetFileNameWithoutExtension(c.Item.FileName.Length > 0 ? c.Item.FileName : c.Item.Name));
            var name = stem + extension;
            for (var n = 2; taken.Contains(name); n++) name = $"{stem} {n}{extension}";
            taken.Add(name);
            return name;
        }).ToList();
        Notice = $"Copying {present.Count} file{(present.Count == 1 ? "" : "s")}…";
        var copied = await OffUiThread(() =>
        {
            var done = 0;
            for (var i = 0; i < present.Count; i++)
            {
                try
                {
                    File.Copy(present[i].Path!, Path.Combine(folder, names[i]), overwrite: false);
                    done++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warn($"Couldn't copy {present[i].Item.Name}: {ex.Message}");
                }
            }
            return done;
        });
        Notice = $"Copied {copied} file{(copied == 1 ? "" : "s")} to {folder}." + (missing.Count > 0 ? $" Not on this PC, left out: {string.Join(", ", missing)}." : "");
    }
}
