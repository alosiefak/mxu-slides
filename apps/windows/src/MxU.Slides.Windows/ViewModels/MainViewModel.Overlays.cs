using MxU.Slides.Core.Animation;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Infrastructure;

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
        var packs = OverlayLibrary.Packs(all);
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

    private static string PackOf(Overlay overlay) => OverlayLibrary.PackOf(overlay);

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
        LiveOverlaysChanged?.Invoke();
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
        FlushOverlaySave();
        var copy = OverlayLibrary.Duplicate(tile.Overlay);
        Library.Save(copy);
        RefreshOverlays();
        SelectedOverlay = Overlays.FirstOrDefault(o => o.Overlay.Id == copy.Id);
    }

    /// <summary>Opens the overlay editor on the selected overlay (or the first of the pack).</summary>
    [RelayCommand]
    private void EditOverlays()
    {
        if (System.Windows.Application.Current?.MainWindow is not { } owner) return;
        Views.OverlayEditorWindow.Open(owner, this, SelectedOverlay?.Overlay.Id);
    }

    // Typing in an overlay's fields updates its thumbnail at once, the live output if it is up, and the file shortly after.

    private DispatcherTimer? overlaySaveTimer;
    private Overlay? pendingOverlaySave;

    private void OverlayEdited()
    {
        if (SelectedOverlay is not { } tile) return;
        OverlayChanged(tile.Overlay);
    }

    /// <summary>
    /// An overlay was changed (by its fields in the bin, or in the overlay editor): its
    /// thumbnail updates at once, the program too if it is up (without replaying its
    /// entrance), and the file is saved shortly after the changes stop.
    /// </summary>
    public void OverlayChanged(Overlay overlay)
    {
        foreach (var tile in Overlays.Where(t => t.Overlay.Id == overlay.Id))
            tile.Preview = OverlayTileViewModel.PreviewScene(overlay);
        OverlayContentChanged?.Invoke(overlay);
        if (IsOverlayLive(overlay.Id))
        {
            Show.OverlayEdited(overlay);
            RenderOutputs(Rendering.SceneTransition.Cut, refreshOnly: true);
        }
        if (pendingOverlaySave is { } other && other.Id != overlay.Id) FlushOverlaySave();
        pendingOverlaySave = overlay;
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
        catch (Exception ex)
        {
            Log.Error($"Saving the overlay \"{overlay.Name}\" failed", ex);
            Problem = $"Couldn't save the overlay \"{overlay.Name}\": {ex.Message}";
        }
        finally { suppressOverlayRefresh = false; }
    }

    /// <summary>Saves any overlay edit still waiting for its pause (before closing the editor, or quitting).</summary>
    public void FlushOverlayEdits() => FlushOverlaySave();

    private bool suppressOverlayRefresh;

    // MARK: For the overlay editor

    public bool IsOverlayLive(string id) => Show.LiveOverlays.Any(o => o.Id == id);

    /// <summary>Raised after an overlay's content changed (typed in the bin, or edited), so an open editor can redraw it.</summary>
    public event Action<Overlay>? OverlayContentChanged;

    /// <summary>Raised when overlays go up or come down, so the editor's Go Live button can follow.</summary>
    public event Action? LiveOverlaysChanged;

    /// <summary>Puts an overlay up (playing its In) or takes it down (playing its Out), as clicking its tile does.</summary>
    public void SetOverlayLive(Overlay overlay, bool up)
    {
        FlushOverlaySave();
        SetOverlay(overlay, up);
    }

    /// <summary>Saves a new, renamed or re-filed overlay straight away and refreshes the bin.</summary>
    public void SaveOverlayNow(Overlay overlay)
    {
        if (pendingOverlaySave?.Id == overlay.Id) pendingOverlaySave = null;
        FlushOverlaySave();
        Library.Save(overlay);
        if (IsOverlayLive(overlay.Id))
        {
            Show.OverlayEdited(overlay);
            RenderOutputs(Rendering.SceneTransition.Cut, refreshOnly: true);
        }
    }

    /// <summary>Moves an overlay to the library's Trash, taking it off the program first if it is up.</summary>
    public void DeleteOverlay(Overlay overlay)
    {
        if (pendingOverlaySave?.Id == overlay.Id) pendingOverlaySave = null;
        FlushOverlaySave();
        if (IsOverlayLive(overlay.Id))
        {
            Show.DismissOverlay(overlay.Id);
            RenderOutputs(DefaultTransition());
            MarkLiveOverlays();
        }
        Library.Delete<Overlay>(overlay.Id);
    }
}

/// <summary>The bottom bin's tabs.</summary>
public enum BinTab { Media, Overlays, Music, Combos }
