using MxU.Slides.Core.Animation;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>An overlay (lower third, ticker, bars…) in the bin.</summary>
public sealed partial class OverlayTileViewModel(Overlay overlay, RenderScene preview) : ObservableObject
{
    public Overlay Overlay { get; } = overlay;

    [ObservableProperty]
    public partial RenderScene Preview { get; set; } = preview;

    public string Name => Overlay.Name;

    /// <summary>The part after the pack name ("Lower Thirds"), or the whole folder.</summary>
    public string Category => Overlay.Folder?.Split('/').LastOrDefault() ?? "";

    [ObservableProperty]
    public partial bool IsLive { get; set; }

    public static RenderScene PreviewScene(Overlay overlay)
    {
        var scene = new RenderScene(SceneSize.Hd, new SceneColor(0.10, 0.10, 0.11));
        SlideSceneBuilder.AddObjects(scene, overlay.Objects, LayerKind.Overlays, SceneSize.Hd, $"bin-{overlay.Id}-");
        return scene;
    }
}

public sealed partial class MainViewModel
{
    public ObservableCollection<OverlayTileViewModel> Overlays { get; } = [];

    public ObservableCollection<string> OverlayPacks { get; } = [];

    [ObservableProperty]
    public partial string? SelectedOverlayPack { get; set; }

    partial void OnSelectedOverlayPackChanged(string? value) => RefreshOverlays();

    [ObservableProperty]
    public partial OverlayTileViewModel? SelectedOverlay { get; set; }

    public ObservableCollection<SlideTextFieldViewModel> OverlayTextFields { get; } = [];

    /// <summary>Which tab the bottom bin shows.</summary>
    [ObservableProperty]
    public partial BinTab Bin { get; set; }

    partial void OnBinChanged(BinTab value)
    {
        OnPropertyChanged(nameof(ShowingMedia));
        OnPropertyChanged(nameof(ShowingOverlays));
        OnPropertyChanged(nameof(ShowingMusic));
        OnPropertyChanged(nameof(ShowingCombos));
    }

    // One flag per tab, for the tab buttons (setting one true switches to it).
    public bool ShowingMedia { get => Bin == BinTab.Media; set { if (value) Bin = BinTab.Media; } }

    public bool ShowingOverlays
    {
        get => Bin == BinTab.Overlays;
        set
        {
            if (value) Bin = BinTab.Overlays;
            else if (Bin == BinTab.Overlays) Bin = BinTab.Media;
        }
    }

    public bool ShowingMusic { get => Bin == BinTab.Music; set { if (value) Bin = BinTab.Music; } }

    public bool ShowingCombos { get => Bin == BinTab.Combos; set { if (value) Bin = BinTab.Combos; } }

    partial void OnSelectedOverlayChanged(OverlayTileViewModel? value)
    {
        FlushOverlaySave();
        OverlayTextFields.Clear();
        if (value is null) return;
        var wiring = SlideSceneBuilder.MaskWiring(value.Overlay.Objects);
        foreach (var o in value.Overlay.Objects.Where(o => o.ObjectKind == SlideObjectKind.Text && o.TextLink is null && !wiring.Mattes.Contains(o.Id)))
            OverlayTextFields.Add(new SlideTextFieldViewModel(o, OverlayEdited));
    }

    public void RefreshOverlays()
    {
        var all = Library.All<Overlay>();
        var packs = all.Select(PackOf).Distinct().OrderBy(p => p == "Other" ? 1 : 0).ThenBy(p => p).ToList();
        if (!packs.SequenceEqual(OverlayPacks))
        {
            OverlayPacks.Clear();
            foreach (var p in packs) OverlayPacks.Add(p);
        }
        if (SelectedOverlayPack is null || !packs.Contains(SelectedOverlayPack))
        {
            SelectedOverlayPack = packs.FirstOrDefault(p => p == PackForTheme(CurrentPresentation?.ThemeId))
                ?? packs.FirstOrDefault(p => p == PackForTheme(Settings.DefaultThemeId))
                ?? packs.FirstOrDefault();
            return;
        }
        var selectedId = SelectedOverlay?.Overlay.Id;
        Overlays.Clear();
        foreach (var overlay in all.Where(o => PackOf(o) == SelectedOverlayPack)
                     .OrderBy(o => CategoryOrder(o.Folder))
                     .ThenBy(o => Array.IndexOf(StarterPacks.OverlayItems, o.Id.Split('.').LastOrDefault()) is var i and >= 0 ? i : 99))
            Overlays.Add(new OverlayTileViewModel(overlay, OverlayTileViewModel.PreviewScene(overlay)));
        SelectedOverlay = Overlays.FirstOrDefault(o => o.Overlay.Id == selectedId);
        MarkLiveOverlays();
    }

    private static string PackOf(Overlay overlay) => overlay.Folder?.Split('/').FirstOrDefault() is { Length: > 0 } pack ? pack : "Other";

    private static int CategoryOrder(string? folder) => folder?.Split('/').LastOrDefault() switch
    {
        "Lower Thirds" => 0,
        "Side Thirds" => 1,
        "Full Screen" => 2,
        _ => 3,
    };

    private static string? PackForTheme(string? themeId) =>
        StarterPacks.All.Where(p => p.ThemeId() == themeId).Select(p => p.Title()).FirstOrDefault();

    private void MarkLiveOverlays()
    {
        foreach (var tile in Overlays) tile.IsLive = Show.LiveOverlays.Any(o => o.Id == tile.Overlay.Id);
    }

    /// <summary>Puts an overlay up, or takes it down if it is already showing.</summary>
    [RelayCommand]
    private void ToggleOverlay(OverlayTileViewModel? tile)
    {
        if (tile is null) return;
        SelectedOverlay = tile;
        FlushOverlaySave();
        SetOverlay(tile.Overlay, up: !Show.LiveOverlays.Any(o => o.Id == tile.Overlay.Id));
    }

    /// <summary>
    /// Puts an overlay up or takes it down. One with its own In/Out animations plays
    /// them (a second "down" while it leaves cuts it); one without dissolves like a slide.
    /// </summary>
    private void SetOverlay(Overlay overlay, bool up)
    {
        var now = ShowState.HostClock();
        if (up) Show.Fire(overlay, now);
        else Show.DismissOverlay(overlay.Id, now);
        if (AnimationSequence.HasAnimationSteps(overlay.Objects)) RenderOutputs(Rendering.SceneTransition.Cut, refreshOnly: true);
        else RenderOutputs(DefaultTransition());
        MarkLiveOverlays();
    }

    [RelayCommand]
    private void DuplicateOverlay()
    {
        if (SelectedOverlay is not { } tile) return;
        var copy = MxuJson.Clone(tile.Overlay);
        copy.Id = LibraryStore.NewId();
        copy.Name = $"{tile.Overlay.Name} (copy)";
        Library.Save(copy);
        RefreshOverlays();
        SelectedOverlay = Overlays.FirstOrDefault(o => o.Overlay.Id == copy.Id);
    }

    // Typing in an overlay's fields updates its thumbnail at once, the live output if it is up, and the file shortly after.

    private DispatcherTimer? overlaySaveTimer;
    private Overlay? pendingOverlaySave;

    private void OverlayEdited()
    {
        if (SelectedOverlay is not { } tile) return;
        tile.Preview = OverlayTileViewModel.PreviewScene(tile.Overlay);
        if (Show.LiveOverlays.Any(o => o.Id == tile.Overlay.Id))
        {
            Show.OverlayEdited(tile.Overlay);
            RenderOutputs(Rendering.SceneTransition.Cut, refreshOnly: true);
        }
        pendingOverlaySave = tile.Overlay;
        overlaySaveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) => FlushOverlaySave(), Dispatcher.CurrentDispatcher);
        overlaySaveTimer.Stop();
        overlaySaveTimer.Start();
    }

    private void FlushOverlaySave()
    {
        overlaySaveTimer?.Stop();
        if (pendingOverlaySave is not { } overlay) return;
        pendingOverlaySave = null;
        suppressOverlayRefresh = true;
        try { Library.Save(overlay); }
        finally { suppressOverlayRefresh = false; }
    }

    private bool suppressOverlayRefresh;
}

/// <summary>The bottom bin's tabs.</summary>
public enum BinTab { Media, Overlays, Music, Combos }
