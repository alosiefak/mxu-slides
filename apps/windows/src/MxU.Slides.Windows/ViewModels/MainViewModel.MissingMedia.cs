using MxU.Slides.Broadcast.Library;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.Views;
using SceneTransition = MxU.Slides.Windows.Rendering.SceneTransition;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// Media whose file isn't on this PC, and putting a file back. A library carried over from
/// another computer, a file deleted from the Blobs folder or a presentation imported without
/// its videos leaves media with nothing to play; until now that quietly drew nothing. Now:
/// <list type="bullet">
/// <item>media tiles, slide tiles and service rows say "file missing";</item>
/// <item><b>Locate</b> scans a folder for exact copies (by content, so names don't matter) or
/// takes any file you choose; a same-name file the scan finds is offered, never used by itself;</item>
/// <item><b>Replace everywhere</b> gives an item a new file, listing first everywhere it's used;</item>
/// <item>deleting media that's still used says where, first;</item>
/// <item>a video flagged at import can be transcoded to H.264 (<b>Transcode now</b>).</item>
/// </list>
/// Every use points at the item's id, not its file, so one change fixes every slide. Port of
/// the Mac's LocateMissingMediaView, ReplaceMediaFileFlow and the relinking in its AppModel; the
/// rules are in Core (MediaRelink.cs, MediaUsage.cs).
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Chooses a picture or video from disk (an Open dialog; the self-test gives a path).</summary>
    public Func<string, string?> ChooseMediaFile { get; set; } = LibraryDialogs.ChooseMediaFile;

    /// <summary>"Replace “X” everywhere?" with where it's used; true to go on.</summary>
    public Func<string, IReadOnlyList<MediaUsage.Group>, bool> ConfirmReplaceEverywhere { get; set; } = LibraryDialogs.ConfirmReplaceEverywhere;

    /// <summary>"This file is already in your library" (as another item): true is Replace Anyway.</summary>
    public Func<MediaItem, string, bool> ConfirmSharedFile { get; set; } = LibraryDialogs.ConfirmSharedFile;

    /// <summary>"Delete “X”? Used in N places": true to delete.</summary>
    public Func<string, IReadOnlyList<MediaUsage.Group>, bool> ConfirmDeleteUsedMedia { get; set; } = LibraryDialogs.ConfirmDeleteUsedMedia;

    /// <summary>Tells the operator which of the old file's settings now apply to the new one.</summary>
    public Action<string, IReadOnlyList<string>> ShowCarriedSettings { get; set; } = LibraryDialogs.ShowCarriedSettings;

    // MARK: Badges

    /// <summary>Marks the media tiles whose file is missing or that were flagged (one look at the Blobs folder for all of them).</summary>
    private void MarkMediaFiles()
    {
        var missing = MissingMedia.MissingIds(Library, MediaItems.Select(t => t.Item));
        foreach (var tile in MediaItems) tile.ShowFileState(missing.Contains(tile.Item.Id));
    }

    /// <summary>
    /// Marks the slide tiles that show media with no file here: the slide's own (a background,
    /// a picture on it, a "fire media" action) and the presentation's or section's background
    /// it sits on. A deleted item a slide still uses counts too.
    /// </summary>
    private void MarkMissingMediaTiles()
    {
        if (CurrentPresentation is not { } presentation || Slides.Count == 0) return;
        var shared = new HashSet<string>(StringComparer.Ordinal);
        if (presentation.Background?.MediaId is { Length: > 0 } deckBackground) shared.Add(deckBackground);
        var perSlide = Slides.ToDictionary(t => t, t =>
        {
            var ids = MediaReferences.Ids(t.Slide);
            ids.UnionWith(shared);
            if (presentation.Sections?.FirstOrDefault(s => s.Id == t.Slide.SectionId)?.Background?.MediaId is { Length: > 0 } section) ids.Add(section);
            ids.RemoveWhere(id => id.Contains("::", StringComparison.Ordinal));
            return ids;
        });
        var used = perSlide.Values.SelectMany(i => i).ToHashSet(StringComparer.Ordinal);
        var items = used.Select(Library.Get<MediaItem>).OfType<MediaItem>().ToList();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in MissingMedia.MissingIds(Library, items)) names[id] = Library.Get<MediaItem>(id)!.Name;
        foreach (var id in MissingMedia.UnknownIds(Library, used)) names[id] = "an item no longer in the library";
        foreach (var (tile, ids) in perSlide)
        {
            var gone = ids.Where(names.ContainsKey).Select(id => names[id]).Order(MediaUsage.NameOrder).ToList();
            tile.MissingMedia = gone.Count == 0 ? null
                : $"Media file missing on this PC: {string.Join(", ", gone)}. Right-click it in Media › Locate missing file…";
        }
    }

    /// <summary>A service row's note when its media has no file here, or its presentation shows some that hasn't.</summary>
    private string? MissingMediaNote(ServiceItem item)
    {
        switch (item.ItemKind)
        {
            case ServiceItemKind.Media when Library.Get<MediaItem>(item.RefId) is { } media:
                return MissingMedia.IsFileMissing(Library, media) ? "File missing" : null;
            case ServiceItemKind.Presentation when Library.Get<Presentation>(item.RefId) is { } presentation:
                var count = MissingMedia.MissingCount(Library, MediaReferences.Ids(presentation));
                return count == 0 ? null : count == 1 ? "1 media file missing" : $"{count} media files missing";
            default:
                return null;
        }
    }

    /// <summary>Redraws everything that shows media after an item got its file back or a new one.</summary>
    private void MediaFilesChanged(IEnumerable<string> ids)
    {
        foreach (var id in ids) Media.ForgetFile(id);
        Library.Blobs.NoteDirectoryChanged();
        RefreshMedia();
        RebuildSlides();
        RefreshServiceItems();
        RenderOutputs(SceneTransition.Cut, refreshOnly: true);
    }

    // MARK: Locate

    /// <summary>What's missing: items with no file, and deleted items still used somewhere (read off the UI thread: it opens the Trash).</summary>
    public Task<MissingMedia.Targets> FindMissingMediaAsync() => OffUiThread(() => MissingMedia.Find(Library));

    /// <summary>Scans a folder for exact copies of missing files and relinks them. Returns what it did and the same-name files it found.</summary>
    public async Task<(int Relinked, int Restored, List<MissingMedia.NameMatch> ByName)> LocateMissingMediaAsync(string folder)
    {
        Notice = $"Looking for missing media in {folder}…";
        var before = await FindMissingMediaAsync();
        var result = await OffUiThread(() => MissingMedia.Locate(Library, folder));
        MediaFilesChanged(before.Missing.Concat(before.Tombstones).Select(m => m.Id));
        var total = result.Relinked + result.Restored;
        Notice = total == 0 ? "No exact copies of the missing files in that folder." : $"Relinked {Count(total, "item")}.";
        return result;
    }

    /// <summary>
    /// Gives a missing item (or a deleted one still used) the file the operator chose. Returns
    /// the settings carried over from the old file, or null when the file can't be used.
    /// </summary>
    public async Task<IReadOnlyList<string>?> UseFileForAsync(MediaItem item, bool isTombstone, string path)
    {
        var carried = await OffUiThread(() => isTombstone
            ? MediaRelink.RestoreTombstone(Library, item, path)
            : MediaRelink.ReplaceFile(Library, item.Id, path));
        if (carried is null) Problem = $"Couldn't use {Path.GetFileName(path)}: Windows can't read it as a picture or video.";
        MediaFilesChanged([item.Id]);
        return carried;
    }

    // MARK: Replace everywhere

    /// <summary>
    /// "Replace media file…": lists where the item is used (none: straight to choosing a
    /// file), then takes the new file. If another item already has that file, asks first, as
    /// the two would show the same picture. Port of ReplaceMediaFileFlow.
    /// </summary>
    public async Task<bool> ReplaceMediaFileEverywhereAsync(MediaItem item)
    {
        var groups = MediaUsage.Groups(await OffUiThread(() => MediaUsage.Uses(Library, item.Id)));
        if (groups.Count > 0 && !ConfirmReplaceEverywhere(item.Name, groups)) return false;
        if (ChooseMediaFile($"Replace “{item.Name}” everywhere") is not { } path) return false;
        var hash = await OffUiThread(() => TryHash(path));
        if (hash is not null && MediaRelink.HoldingFile(Library, hash, item.Id) is { } other && !ConfirmSharedFile(other, item.Name)) return false;
        Notice = $"Replacing the file of “{item.Name}”…";
        var carried = await OffUiThread(() => MediaRelink.ReplaceFile(Library, item.Id, path));
        MediaFilesChanged([item.Id]);
        if (carried is null)
        {
            Problem = $"Couldn't use {Path.GetFileName(path)}: Windows can't read it as a picture or video.";
            return false;
        }
        Notice = $"“{item.Name}” now uses {Path.GetFileName(path)}" + (groups.Count > 0 ? $", in all {Count(groups.Sum(g => g.Uses.Count), "place")} it's used." : ".");
        if (carried.Count > 0) ShowCarriedSettings(item.Name, carried);
        return true;
    }

    // MARK: Deleting

    /// <summary>
    /// Moves a media item to the Trash, first saying where it's used if anywhere: each of those
    /// spots then shows a missing-media warning until it's restored (Locate) or replaced.
    /// </summary>
    public async Task<bool> DeleteMediaAsync(MediaItem item)
    {
        var groups = MediaUsage.Groups(await OffUiThread(() => MediaUsage.Uses(Library, item.Id)));
        if (groups.Count > 0 && !ConfirmDeleteUsedMedia(item.Name, groups)) return false;
        Library.Delete<MediaItem>(item.Id);
        Media.ForgetFile(item.Id);
        RefreshMedia();
        RebuildSlides();
        RefreshServiceItems();
        Notice = groups.Count == 0
            ? $"Moved \"{item.Name}\" to the library's Trash folder. The file itself is kept."
            : $"Moved \"{item.Name}\" to the library's Trash folder. Where it was used now says it's missing: Media › Locate missing file… brings it back.";
        return true;
    }

    // MARK: Transcoding

    private int transcodes;

    /// <summary>True while a transcode runs (one at a time: they use every core).</summary>
    public bool IsTranscoding => transcodes > 0;

    /// <summary>Makes an H.264 copy of a flagged video and points the item at it. Returns the item's new status.</summary>
    public async Task<MediaFileStatus> TranscodeMediaAsync(MediaItem item)
    {
        if (IsTranscoding) return item.FileStatus;
        transcodes++;
        try
        {
            Notice = $"Transcoding “{item.Name}”…";
            var name = item.Name;
            var progress = new Progress<double>(p => Notice = $"Transcoding “{name}”: {p:P0}…");
            RefreshMediaSoon();
            var status = await OffUiThread(() => new TranscodeQueue(Library).Transcode(item.Id, progress));
            MediaFilesChanged([item.Id]);
            var detail = Library.Get<MediaItem>(item.Id)?.StatusDetail ?? "";
            if (status == MediaFileStatus.Ready) Notice = $"“{name}” is ready: it now plays from an H.264 copy.";
            else
            {
                Notice = null;
                Problem = $"Couldn't transcode “{name}”: {detail}";
            }
            return status;
        }
        finally
        {
            transcodes--;
        }
    }

    /// <summary>The item says "transcoding" as soon as the work starts (the save comes from the worker thread).</summary>
    private void RefreshMediaSoon() =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(RefreshMedia, System.Windows.Threading.DispatcherPriority.Background);
}
