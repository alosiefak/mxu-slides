using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Infrastructure;

namespace MxU.Slides.Windows.ViewModels;

public sealed partial class MainViewModel
{
    public ObservableCollection<MediaTileViewModel> MediaItems { get; } = [];

    [ObservableProperty]
    public partial MediaTileViewModel? SelectedMedia { get; set; }

    [ObservableProperty]
    public partial string MediaSearch { get; set; } = "";

    partial void OnMediaSearchChanged(string value) => RefreshMedia();

    public void RefreshMedia()
    {
        var query = MediaSearch.Trim();
        var selectedId = SelectedMedia?.Item.Id;
        MediaItems.Clear();
        foreach (var item in Library.All<MediaItem>()
                     .Where(m => query.Length == 0 || m.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                     .OrderByDescending(m => m.ImportedAt ?? 0))
            MediaItems.Add(new MediaTileViewModel(item, MediaTileViewModel.PreviewScene(item)));
        SelectedMedia = MediaItems.FirstOrDefault(m => m.Item.Id == selectedId);
        MarkLiveTiles();
    }

    [RelayCommand]
    private void ImportMedia()
    {
        var extensions = string.Join(";", LibraryStore.SupportedMediaExtensions.Select(e => "*" + e));
        var dialog = new OpenFileDialog
        {
            Title = "Import images and videos",
            Multiselect = true,
            Filter = $"Images and videos|{extensions}|All files|*.*",
        };
        if (dialog.ShowDialog() == true) ImportMediaFiles(dialog.FileNames);
    }

    /// <summary>Copies files into the library (from the dialog or a drag and drop). Returns what was imported.</summary>
    public List<MediaItem> ImportMediaFiles(IEnumerable<string> paths)
    {
        var imported = new List<MediaItem>();
        var skipped = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                if (Library.ImportMedia(path) is { } item) imported.Add(item);
                else skipped.Add(Path.GetFileName(path));
            }
            catch (Exception ex)
            {
                Log.Error($"Importing {path} failed", ex);
                skipped.Add(Path.GetFileName(path));
            }
        }
        RefreshMedia();
        if (skipped.Count > 0) Problem = $"Couldn't import: {string.Join(", ", skipped)}";
        else if (imported.Count > 0) Notice = $"Imported {imported.Count} media file{(imported.Count == 1 ? "" : "s")}.";
        return imported;
    }

    [RelayCommand]
    private void MediaClicked(MediaTileViewModel? tile)
    {
        if (tile is null) return;
        SelectedMedia = tile;
        if (!IsEditing) FireMedia(tile.Item);
    }

    /// <summary>
    /// Fires media onto its layer: background loops on Background Media, images on Still Graphics,
    /// videos on Foreground Videos. From a service row (<paramref name="serviceItemId"/>), the row's
    /// output preset applies and the item's own auto-advance starts; fired any other way (the bin,
    /// an action), a media row's auto-advance stops, as on the Mac.
    /// </summary>
    public void FireMedia(MediaItem item, string? serviceItemId = null)
    {
        var cue = MediaCueRouting.Dropped(item);
        var layer = SlideSceneBuilder.LayerKindOf(cue.Layer);
        Actions.ApplyPresetSwitches(item, serviceItemId);
        Show.Fire(cue, layer);
        RenderOutputs(DefaultTransition());
        // Fired again (or soon after it ended), a video starts over rather than sit at its end.
        if (VideoRestarts.OnMediaFired(item)) RestartVideo(item.Id);
        Actions.MediaDidFire(item, layer);
        if (serviceItemId is null) AutoAdvance.CancelMedia();
        else AutoAdvance.MediaFired(item, serviceItemId);
        if (Show.Live is null)
        {
            LiveTitle = item.Name;
            LiveDetail = item.MediaKind == MediaKind.Video ? "Video" : "Image";
        }
        MarkLiveTiles();
    }

    [RelayCommand]
    private void SetMediaAsPresentationBackground(MediaTileViewModel? tile)
    {
        if ((tile ?? SelectedMedia) is not { } media || CurrentPresentation is not { } p) return;
        p.Background = MediaCueRouting.DroppedOnSlide(media.Item);
        SavePresentation(p);
        RebuildSlides();
        Notice = $"\"{media.Name}\" is now the background for every slide in {p.Name}.";
    }

    [RelayCommand]
    private void SetMediaAsSlideBackground(MediaTileViewModel? tile)
    {
        if ((tile ?? SelectedMedia) is not { } media || CurrentPresentation is not { } p || SelectedSlide is not { } slideTile) return;
        var slide = p.Slides.FirstOrDefault(s => s.Id == slideTile.Slide.Id);
        if (slide is null) return;
        slide.Background = MediaCueRouting.DroppedOnSlide(media.Item);
        SavePresentation(p);
        RebuildSlides();
    }

    [RelayCommand]
    private void ClearPresentationBackground()
    {
        if (CurrentPresentation is not { } p) return;
        p.Background = null;
        foreach (var section in p.Sections ?? []) section.Background = null;
        SavePresentation(p);
        RebuildSlides();
    }

    [RelayCommand]
    private void AddMediaTileToService(MediaTileViewModel? tile)
    {
        if ((tile ?? SelectedMedia) is { } media) AddMediaToService(media.Item);
    }

    [RelayCommand]
    private void ToggleMediaLoop(MediaTileViewModel? tile)
    {
        if ((tile ?? SelectedMedia) is not { } media) return;
        media.Item.Loops = !media.Item.Loops;
        Library.Save(media.Item);
    }

    /// <summary>Background media sits behind slides and stays across slides; foreground media is cleared by the next slide.</summary>
    [RelayCommand]
    private void ToggleMediaClassification(MediaTileViewModel? tile)
    {
        if ((tile ?? SelectedMedia) is not { } media) return;
        media.Item.Classification = media.Item.Classification == MediaClassification.Background
            ? MediaClassification.Foreground
            : MediaClassification.Background;
        Library.Save(media.Item);
        Notice = $"\"{media.Name}\" will play as {(media.Item.Classification == MediaClassification.Background ? "background media behind slides" : "foreground media")}.";
    }

    [RelayCommand]
    private void DeleteMedia(MediaTileViewModel? tile)
    {
        if ((tile ?? SelectedMedia) is not { } media) return;
        Library.Delete<MediaItem>(media.Item.Id);
        Notice = $"Moved \"{media.Name}\" to the library's Trash folder. The file itself is kept.";
    }

    public void RenameMedia(MediaTileViewModel tile, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        tile.Item.Name = name.Trim();
        Library.Save(tile.Item);
    }
}
