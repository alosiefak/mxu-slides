using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Views;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>An object in the overlay editor's Objects list.</summary>
public sealed partial class OverlayObjectRow(SlideObject o, bool isMatte) : ObservableObject
{
    public SlideObject Object { get; } = o;

    public string Id => Object.Id;

    [ObservableProperty]
    public partial bool IsMatte { get; set; } = isMatte;

    public string Label => ObjectMasks.DisplayName(Object) + (IsMatte ? "  (mask)" : "");

    public string Kind => ObjectMasks.FallbackName(Object);

    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>A text field in the overlay: the label the operator sees in the bin, and the words it starts with.</summary>
public sealed partial class OverlayFieldRow(SlideObject o, Action changed) : ObservableObject
{
    public SlideObject Object { get; } = o;

    public string Label
    {
        get => Object.Name;
        set
        {
            if (Object.Name == value) return;
            Object.Name = value;
            OnPropertyChanged();
            changed();
        }
    }

    public string Text
    {
        get => Object.Text;
        set
        {
            var normalized = value.Replace("\r\n", "\n");
            if (Object.Text == normalized) return;
            Object.Text = normalized;
            OnPropertyChanged();
            changed();
        }
    }

    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>
/// The overlay editor: pick an overlay (by pack), design its objects on the canvas with
/// the slide editor's canvas and inspector, name its fields, give its objects In and
/// Out animations, and preview it. Everything you change is the overlay itself: it
/// saves shortly after you stop, and an overlay that is up on the program updates
/// straight away (<see cref="ShowState.OverlayEdited"/>). Port of the Mac's overlay
/// editing, which opens an overlay in the slide editor as a one-slide composition
/// (SlideEditorModel's overlay host) from the library and the Overlays module.
/// </summary>
public sealed partial class OverlayEditorViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel main;
    private readonly Dictionary<string, Overlay> originals = new(StringComparer.Ordinal);
    private readonly DispatcherTimer previewClock;
    private SlideCanvasEditor? canvas;
    private bool syncingSelection;

    public OverlayEditorViewModel(MainViewModel main, string? overlayId)
    {
        this.main = main;
        main.LiveOverlaysChanged += OnLiveChanged;
        main.OverlayContentChanged += OnChangedElsewhere;
        main.Library.Changed += OnLibraryChanged;
        previewClock = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => PreviewTick(), Dispatcher.CurrentDispatcher);
        previewClock.Stop();
        var start = main.Library.Get<Overlay>(overlayId);
        RefreshPacks(start is null ? null : OverlayLibrary.PackOf(start));
        Open(start?.Id ?? Items.FirstOrDefault()?.Overlay.Id);
    }

    public void Dispose()
    {
        previewClock.Stop();
        main.LiveOverlaysChanged -= OnLiveChanged;
        main.OverlayContentChanged -= OnChangedElsewhere;
        main.Library.Changed -= OnLibraryChanged;
        Canvas = null;
        main.FlushOverlayEdits();
    }

    public LibraryStore Library => main.Library;

    /// <summary>The overlay being edited: the library's own copy, the one the bin and the program use.</summary>
    public Overlay? Current => SelectedItem?.Overlay;

    // MARK: Packs and overlays

    public ObservableCollection<string> Packs { get; } = [];

    [ObservableProperty]
    public partial string? SelectedPack { get; set; }

    partial void OnSelectedPackChanged(string? value)
    {
        RefreshItems();
        if (SelectedItem is null) SelectedItem = Items.FirstOrDefault();
    }

    public ObservableCollection<OverlayTileViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial OverlayTileViewModel? SelectedItem { get; set; }

    // The overlay on the canvas now. The list can be rebuilt (a rename re-sorts it) without reloading the canvas.
    private Overlay? loadedOverlay;
    private bool rebuildingItems;

    partial void OnSelectedItemChanged(OverlayTileViewModel? value)
    {
        if (rebuildingItems || ReferenceEquals(loadedOverlay, value?.Overlay)) return;
        main.FlushOverlayEdits();
        StopPreview();
        if (value?.Overlay is { } overlay) originals.TryAdd(overlay.Id, MxuJson.Clone(overlay));
        loadedOverlay = value?.Overlay;
        LoadCurrent();
    }

    private IReadOnlyList<Overlay> AllOverlays() => Library.All<Overlay>();

    /// <summary>Shows an overlay (switching to its pack).</summary>
    public void Open(string? overlayId)
    {
        if (Library.Get<Overlay>(overlayId) is not { } overlay)
        {
            SelectedItem ??= Items.FirstOrDefault();
            return;
        }
        var pack = OverlayLibrary.PackOf(overlay);
        if (SelectedPack != pack)
        {
            RefreshPacks(pack);
        }
        RefreshItems();
        SelectedItem = Items.FirstOrDefault(t => t.Overlay.Id == overlay.Id) ?? Items.FirstOrDefault();
    }

    private void RefreshPacks(string? select)
    {
        var packs = OverlayLibrary.Packs(AllOverlays());
        if (!packs.SequenceEqual(Packs))
        {
            Packs.Clear();
            foreach (var p in packs) Packs.Add(p);
        }
        OnPropertyChanged(nameof(MovePacks));
        var wanted = select is not null && packs.Contains(select) ? select : SelectedPack is { } now && packs.Contains(now) ? now : packs.FirstOrDefault();
        if (SelectedPack != wanted) SelectedPack = wanted;
        else RefreshItems();
    }

    /// <summary>The selected pack's overlays, sorted like the bin (by category, then name).</summary>
    private void RefreshItems()
    {
        var overlays = SelectedPack is { } pack
            ? OverlayLibrary.InPack(AllOverlays(), pack).OrderBy(o => OverlayLibrary.CategoryOf(o), StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase).ToList()
            : [];
        // Same overlays as shown? Only refresh their names and thumbnails, so the selection (and the canvas) stay put.
        if (overlays.Count == Items.Count && overlays.Zip(Items).All(pair => ReferenceEquals(pair.First, pair.Second.Overlay)))
        {
            foreach (var tile in Items)
            {
                tile.Preview = OverlayTileViewModel.PreviewScene(tile.Overlay);
                tile.IsLive = main.IsOverlayLive(tile.Overlay.Id);
            }
            return;
        }
        var selectedId = loadedOverlay?.Id;
        rebuildingItems = true;
        try
        {
            Items.Clear();
            foreach (var overlay in overlays)
                Items.Add(new OverlayTileViewModel(overlay, OverlayTileViewModel.PreviewScene(overlay)) { IsLive = main.IsOverlayLive(overlay.Id) });
        }
        finally { rebuildingItems = false; }
        SelectedItem = Items.FirstOrDefault(t => t.Overlay.Id == selectedId) ?? (selectedId is null ? null : Items.FirstOrDefault());
    }

    private void OnLibraryChanged(Type type, string id)
    {
        if (type != typeof(Overlay)) return;
        // Imports save from a background thread; the lists belong to the UI thread.
        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => OnLibraryChanged(type, id));
            return;
        }
        RefreshPacks(SelectedPack);
        // The overlay being edited was deleted elsewhere (or swapped for a fresh copy): follow the library.
        if (Current is { } current && !ReferenceEquals(Library.Get<Overlay>(current.Id), current))
        {
            var replacement = Library.Get<Overlay>(current.Id);
            SelectedItem = replacement is null ? Items.FirstOrDefault() : Items.FirstOrDefault(t => ReferenceEquals(t.Overlay, replacement));
        }
    }

    [RelayCommand]
    private void NewOverlay()
    {
        var overlay = OverlayLibrary.Create(SelectedPack ?? OverlayLibrary.NoPack);
        main.SaveOverlayNow(overlay);
        Open(overlay.Id);
    }

    [RelayCommand]
    private void DuplicateOverlay()
    {
        if (Current is not { } overlay) return;
        main.FlushOverlayEdits();
        var copy = OverlayLibrary.Duplicate(overlay);
        main.SaveOverlayNow(copy);
        Open(copy.Id);
    }

    /// <summary>Renames the overlay; false for a blank name.</summary>
    public bool Rename(string name)
    {
        if (Current is not { } overlay || !OverlayLibrary.Rename(overlay, name)) return false;
        main.SaveOverlayNow(overlay);
        OnPropertyChanged(nameof(OverlayName));
        RefreshItems();
        return true;
    }

    /// <summary>Moves the overlay to the library's Trash (taking it off the program first).</summary>
    public void Delete()
    {
        if (Current is not { } overlay) return;
        var index = Items.IndexOf(SelectedItem!);
        main.DeleteOverlay(overlay);
        RefreshPacks(SelectedPack);
        SelectedItem = Items.Count == 0 ? null : Items[Math.Clamp(index, 0, Items.Count - 1)];
    }

    /// <summary>A new pack, made with one untitled overlay in it (a pack exists while something is filed in it).</summary>
    public bool NewPack(string name)
    {
        if (OverlayLibrary.ValidPackName(name) is not { } pack) return false;
        var overlay = OverlayLibrary.Create(pack);
        main.SaveOverlayNow(overlay);
        RefreshPacks(pack);
        Open(overlay.Id);
        return true;
    }

    public bool RenamePack(string name)
    {
        if (SelectedPack is not { } pack || OverlayLibrary.ValidPackName(name) is not { } newName) return false;
        main.FlushOverlayEdits();
        var changed = OverlayLibrary.RenamePack(AllOverlays(), pack, newName);
        foreach (var overlay in changed) main.SaveOverlayNow(overlay);
        RefreshPacks(newName);
        return changed.Count > 0;
    }

    /// <summary>The overlays a pack deletion would move to the Trash.</summary>
    public IReadOnlyList<Overlay> PackContents => SelectedPack is { } pack ? OverlayLibrary.InPack(AllOverlays(), pack) : [];

    public void DeletePack()
    {
        foreach (var overlay in PackContents) main.DeleteOverlay(overlay);
        RefreshPacks(null);
    }

    // MARK: Which tab the right-hand side shows

    /// <summary>0 the selected object, 1 its animations, 2 the overlay's own settings and fields.</summary>
    [ObservableProperty]
    public partial int Tab { get; set; }

    partial void OnTabChanged(int value)
    {
        OnPropertyChanged(nameof(ShowingObjectTab));
        OnPropertyChanged(nameof(ShowingAnimationTab));
        OnPropertyChanged(nameof(ShowingOverlayTab));
    }

    // One flag per tab for its button; pressing the open tab's button again keeps it open.
    public bool ShowingObjectTab { get => Tab == 0; set { if (value) Tab = 0; else OnPropertyChanged(); } }

    public bool ShowingAnimationTab { get => Tab == 1; set { if (value) Tab = 1; else OnPropertyChanged(); } }

    public bool ShowingOverlayTab { get => Tab == 2; set { if (value) Tab = 2; else OnPropertyChanged(); } }

    // MARK: The overlay's own settings

    public string OverlayName
    {
        get => Current?.Name ?? "";
        set
        {
            if (!Rename(value)) OnPropertyChanged();
        }
    }

    public bool HasOverlay => Current is not null;

    public bool IsStarter => Current is { } overlay && OverlayLibrary.IsStarter(overlay);

    /// <summary>The packs this overlay could move to (all of them, plus unfiled).</summary>
    public IReadOnlyList<string> MovePacks => [.. Packs.Where(p => p != OverlayLibrary.NoPack), OverlayLibrary.NoPack];

    public string? OverlayPack
    {
        get => Current is { } overlay ? OverlayLibrary.PackOf(overlay) : null;
        set
        {
            if (Current is not { } overlay || value is null || !OverlayLibrary.MoveToPack(overlay, value)) return;
            main.SaveOverlayNow(overlay);
            RefreshPacks(value);
            Open(overlay.Id);
        }
    }

    public string Category
    {
        get => Current is { } overlay ? OverlayLibrary.CategoryOf(overlay) : "";
        set
        {
            if (Current is not { } overlay) return;
            var folder = OverlayLibrary.FolderFor(OverlayLibrary.PackOf(overlay), value);
            if (folder == overlay.Folder) return;
            overlay.Folder = folder;
            overlay.FolderId = null;
            main.SaveOverlayNow(overlay);
            RefreshItems();
        }
    }

    /// <summary>The layers an overlay can sit on, top first (the Mac's Layer menu).</summary>
    public IReadOnlyList<Choice<LayerKind>> Layers { get; } =
        [.. Enumerable.Reverse(LayerKinds.All).Select(layer => new Choice<LayerKind>(layer, layer.DisplayName()))];

    public Choice<LayerKind>? Layer
    {
        get => Layers.FirstOrDefault(l => l.Value == (LayerKinds.Parse(Current?.Layer) ?? LayerKind.Overlays));
        set
        {
            if (Current is not { } overlay || value is null) return;
            var raw = value.Value == LayerKind.Overlays ? null : value.Value.Raw();
            if (overlay.Layer == raw) return;
            overlay.Layer = raw;
            Changed(Parts.None);
        }
    }

    /// <summary>Puts the overlay back the way it was when this editor first showed it.</summary>
    [RelayCommand]
    private void Revert()
    {
        if (Current is not { } overlay || !originals.TryGetValue(overlay.Id, out var original)) return;
        var copy = MxuJson.Clone(original);
        overlay.Name = copy.Name;
        overlay.Objects = copy.Objects;
        overlay.Folder = copy.Folder;
        overlay.FolderId = copy.FolderId;
        overlay.Layer = copy.Layer;
        overlay.AnimationOrder = copy.AnimationOrder;
        overlay.BuildOrder = copy.BuildOrder;
        main.SaveOverlayNow(overlay);
        RefreshPacks(OverlayLibrary.PackOf(overlay));
        LoadCurrent();
    }

    // MARK: Loading an overlay onto the canvas

    /// <summary>A one-slide stand-in for the overlay that the canvas edits; it shares the overlay's object list.</summary>
    public Slide? Mirror { get; private set; }

    private void LoadCurrent()
    {
        Mirror = Current is { } overlay ? new Slide { Id = overlay.Id, Name = overlay.Name, Objects = overlay.Objects, AnimationOrder = overlay.AnimationOrder } : null;
        canvas?.Load(Mirror, null, null);
        Inspector = null;
        StepEditor = null;
        RefreshAll();
        OnPropertyChanged(string.Empty);
    }

    private void RefreshAll()
    {
        RefreshObjects();
        RefreshFields();
        RefreshSteps();
        OnLiveChanged();
        OnPropertyChanged(nameof(PreviewDuration));
        OnPropertyChanged(nameof(PlayheadText));
    }

    /// <summary>What needs re-reading after a change.</summary>
    [Flags]
    private enum Parts { None = 0, Objects = 1, Fields = 2, Steps = 4, All = Objects | Fields | Steps }

    /// <summary>
    /// The overlay changed: redraw the canvas, update the bin and the program (if it's up),
    /// save it shortly, and re-read the lists that may show the change.
    /// </summary>
    private void Changed(Parts parts = Parts.All)
    {
        if (Current is not { } overlay) return;
        if (!ReferenceEquals(Mirror?.Objects, overlay.Objects)) LoadCurrent();
        canvas?.Refresh();
        changing = true;
        try { main.OverlayChanged(overlay); }
        finally { changing = false; }
        if (SelectedItem is { } tile) tile.Preview = OverlayTileViewModel.PreviewScene(overlay);
        if (parts.HasFlag(Parts.Objects)) RefreshObjects();
        if (parts.HasFlag(Parts.Fields)) RefreshFields();
        if (parts.HasFlag(Parts.Steps)) RefreshSteps();
        OnPropertyChanged(nameof(PreviewDuration));
        OnPropertyChanged(nameof(PlayheadText));
    }

    private bool changing;

    /// <summary>The overlay was changed outside the editor (its fields typed in the bin): show the new words.</summary>
    private void OnChangedElsewhere(Overlay overlay)
    {
        if (changing || !ReferenceEquals(overlay, Current)) return;
        canvas?.Refresh();
        Inspector?.Refresh();
        RefreshFields();
        RefreshSteps();
    }

    // MARK: The canvas

    /// <summary>Set by the window: the canvas reports selection and edits back here.</summary>
    public SlideCanvasEditor? Canvas
    {
        get => canvas;
        set
        {
            if (canvas is not null)
            {
                canvas.SelectionChanged -= OnCanvasSelectionChanged;
                canvas.Edited -= OnCanvasEdited;
                canvas.DeleteRequested -= DeleteSelectedObjects;
                canvas.DuplicateRequested -= DuplicateSelectedObjects;
                canvas.SceneBuilder = null;
            }
            canvas = value;
            if (canvas is null) return;
            canvas.SceneBuilder = _ => CanvasScene();
            canvas.SelectionChanged += OnCanvasSelectionChanged;
            canvas.Edited += OnCanvasEdited;
            canvas.DeleteRequested += DeleteSelectedObjects;
            canvas.DuplicateRequested += DuplicateSelectedObjects;
            canvas.Load(Mirror, null, null);
        }
    }

    /// <summary>What the canvas shows behind the overlay: 0 dark grey, 1 black, 2 the live program.</summary>
    [ObservableProperty]
    public partial int Backdrop { get; set; }

    partial void OnBackdropChanged(int value) => canvas?.Refresh();

    /// <summary>Show masks (mattes) as pale blue shapes on the canvas; outputs never draw them.</summary>
    [ObservableProperty]
    public partial bool ShowMattes { get; set; } = true;

    partial void OnShowMattesChanged(bool value) => canvas?.Refresh();

    private static readonly SceneColor DarkGrey = new(0.10, 0.10, 0.11);

    private RenderScene BackdropScene()
    {
        if (Backdrop == 2 && Current is { } overlay) return OverlayPreview.Without(main.Show.Scene(), overlay);
        return OverlayPreview.Plain(SceneSize.Hd, Backdrop == 1 ? SceneColor.Black : DarkGrey);
    }

    private RenderScene CanvasScene() =>
        Current is { } overlay ? OverlayPreview.Still(overlay, BackdropScene(), ShowMattes) : OverlayPreview.Plain(SceneSize.Hd, DarkGrey);

    [ObservableProperty]
    public partial ObjectInspectorViewModel? Inspector { get; set; }

    /// <summary>The selected objects, in the overlay's order.</summary>
    private List<SlideObject> SelectedObjects() =>
        Current is { } overlay && canvas is not null ? overlay.Objects.Where(o => canvas.Selection.Contains(o.Id)).ToList() : [];

    public bool HasSelection => canvas?.Selection.Count > 0;

    private void OnCanvasSelectionChanged()
    {
        var selected = SelectedObjects();
        Inspector = selected.Count == 1 && Current is { } overlay
            ? new ObjectInspectorViewModel(selected[0], null, null, SceneSize.Hd, InspectorChanged) { Siblings = overlay.Objects }
            : null;
        syncingSelection = true;
        SelectedObjectRow = selected.Count == 1 ? ObjectRows.FirstOrDefault(r => r.Id == selected[0].Id) : null;
        syncingSelection = false;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(GalleryTarget));
    }

    private void OnCanvasEdited(bool final)
    {
        Inspector?.GeometryChanged();
        if (final) Changed(Parts.None);
    }

    private void InspectorChanged()
    {
        canvas?.Refresh();
        Changed(Parts.All);
    }

    /// <summary>Selects objects on the canvas (the Objects list and the self-test use this).</summary>
    public void SelectObjects(IEnumerable<string> ids) => canvas?.Select(ids);

    // MARK: Objects

    public ObservableCollection<OverlayObjectRow> ObjectRows { get; } = [];

    [ObservableProperty]
    public partial OverlayObjectRow? SelectedObjectRow { get; set; }

    partial void OnSelectedObjectRowChanged(OverlayObjectRow? value)
    {
        if (syncingSelection || value is null) return;
        SelectObjects([value.Id]);
    }

    private void RefreshObjects()
    {
        var objects = Current?.Objects ?? [];
        var mattes = SlideSceneBuilder.MaskWiring(objects).Mattes;
        if (objects.Count == ObjectRows.Count && objects.Zip(ObjectRows).All(pair => ReferenceEquals(pair.First, pair.Second.Object)))
        {
            foreach (var row in ObjectRows)
            {
                row.IsMatte = mattes.Contains(row.Id);
                row.Refresh();
            }
            return;
        }
        syncingSelection = true;
        ObjectRows.Clear();
        // Top of the stack first, as layer lists usually read.
        foreach (var o in objects.AsEnumerable().Reverse()) ObjectRows.Add(new OverlayObjectRow(o, mattes.Contains(o.Id)));
        SelectedObjectRow = canvas?.Selection.Count == 1 ? ObjectRows.FirstOrDefault(r => canvas.Selection.Contains(r.Id)) : null;
        syncingSelection = false;
    }

    private void AddObject(SlideObject o)
    {
        if (Current is not { } overlay) return;
        overlay.Objects.Add(o);
        AfterStructureChange([o.Id]);
    }

    private void AfterStructureChange(IEnumerable<string> select)
    {
        canvas?.Refresh();
        canvas?.Select(select);
        Changed(Parts.All);
    }

    /// <summary>A text field, placed where a lower third's words usually go.</summary>
    [RelayCommand]
    private void AddText()
    {
        var count = Current?.Objects.Count(o => o.ObjectKind == SlideObjectKind.Text) ?? 0;
        AddObject(new SlideObject
        {
            Id = LibraryStore.NewId(), ObjectKind = SlideObjectKind.Text, Name = count == 0 ? "Text" : $"Text {count + 1}", Text = "Text",
            X = 160, Y = 840 - Math.Min(count, 4) * 110, Width = 1000, Height = 110,
            TextStyle = new TextStyle
            {
                FontSize = 60, HorizontalAlignment = TextHorizontalAlignment.Left, VerticalAlignment = TextVerticalAlignment.Middle,
                ColorHex = "#FFFFFFFF", AutoShrink = true,
            },
        });
    }

    /// <summary>A plate to put text on.</summary>
    [RelayCommand]
    private void AddBox() => AddObject(new SlideObject
    {
        Id = LibraryStore.NewId(), ObjectKind = SlideObjectKind.Shape, Name = "Plate", Text = "",
        X = 120, Y = 800, Width = 1100, Height = 190, ShapeKind = ShapeKind.RoundedRectangle, CornerRadius = 12,
        Fill = new ObjectFill { FillKind = FillKind.Solid, ColorHex = "#0F172AE6" },
    });

    [RelayCommand]
    private void AddCircle() => AddObject(new SlideObject
    {
        Id = LibraryStore.NewId(), ObjectKind = SlideObjectKind.Shape, Name = "Circle", Text = "",
        X = 1640, Y = 80, Width = 200, Height = 200, ShapeKind = ShapeKind.Ellipse,
        Fill = new ObjectFill { FillKind = FillKind.Solid, ColorHex = "#FFFFFFFF" },
    });

    /// <summary>The image or video selected in the main window's Media bin, as a picture box.</summary>
    [RelayCommand]
    private void AddMedia()
    {
        if (main.SelectedMedia is not { } media)
        {
            Notice = "Pick an image or video in the main window's Media bin first, then press Media.";
            return;
        }
        var w = 640.0;
        var h = media.Item is { PixelWidth: > 0 and var pw, PixelHeight: > 0 and var ph } ? w * ph / pw : 360;
        AddObject(new SlideObject
        {
            Id = LibraryStore.NewId(), ObjectKind = SlideObjectKind.Shape, Name = media.Name, Text = "",
            X = 1920 - w - 80, Y = 80, Width = w, Height = h, ShapeKind = ShapeKind.Rectangle,
            Fill = new ObjectFill { FillKind = FillKind.Media, MediaId = media.Item.Id, MediaScaleMode = MediaScaleMode.Fit, Loops = media.Item.Loops },
        });
    }

    /// <summary>A plain-language message for the window's status line.</summary>
    [ObservableProperty]
    public partial string? Notice { get; set; }

    [RelayCommand]
    private void DeleteSelectedObjects()
    {
        if (Current is not { } overlay || SelectedObjects() is not { Count: > 0 } selected) return;
        var stepIds = selected.SelectMany(o => o.AnimationSteps ?? []).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var o in selected) overlay.Objects.Remove(o);
        // Leave no dangling steps in the play order, and no masks naming an object that's gone.
        if (overlay.AnimationOrder is { } order)
        {
            var kept = order.Where(id => !stepIds.Contains(id)).ToList();
            overlay.AnimationOrder = kept.Count == 0 ? null : kept;
        }
        var gone = selected.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var o in overlay.Objects.Where(o => o.MaskObjectId is { } m && gone.Contains(m))) ObjectMasks.SetMask(o, null);
        AfterStructureChange([]);
    }

    /// <summary>
    /// Copies the selection 40 units down and right. The copies' steps get new ids and play
    /// just after the originals', so they land in the same group (going up, or coming down).
    /// </summary>
    [RelayCommand]
    private void DuplicateSelectedObjects()
    {
        if (Current is not { } overlay || canvas is null || SelectedObjects() is not { Count: > 0 } selected) return;
        var copies = new List<SlideObject>();
        var order = overlay.AnimationOrder is { Count: > 0 } existing ? existing.ToList() : null;
        foreach (var o in selected)
        {
            var frame = canvas.FrameOf(o);
            var copy = MxuJson.Clone(o);
            copy.Id = LibraryStore.NewId();
            copy.Name = string.IsNullOrWhiteSpace(o.Name) ? o.Name : $"{o.Name} Copy";
            copy.X = frame.X + 40;
            copy.Y = frame.Y + 40;
            copy.Width = frame.Width;
            copy.Height = frame.Height;
            foreach (var step in copy.AnimationSteps ?? [])
            {
                var originalId = step.Id;
                step.Id = Guid.NewGuid().ToString().ToUpperInvariant();
                if (order is not null && order.IndexOf(originalId) is >= 0 and var at) order.Insert(at + 1, step.Id);
            }
            copies.Add(copy);
        }
        overlay.Objects.AddRange(copies);
        if (order is not null) overlay.AnimationOrder = order;
        AfterStructureChange(copies.Select(c => c.Id));
    }

    [RelayCommand]
    private void BringToFront() => Reorder(toFront: true);

    [RelayCommand]
    private void SendToBack() => Reorder(toFront: false);

    private void Reorder(bool toFront)
    {
        if (Current is not { } overlay || SelectedObjects() is not { Count: > 0 } selected) return;
        foreach (var o in selected) overlay.Objects.Remove(o);
        if (toFront) overlay.Objects.AddRange(selected);
        else overlay.Objects.InsertRange(0, selected);
        AfterStructureChange(selected.Select(o => o.Id));
    }

    /// <summary>Aligns the selection to each other (two or more) or to the canvas (one).</summary>
    [RelayCommand]
    private void Align(string edge)
    {
        if (canvas is null || SelectedObjects() is not { Count: > 0 } selected || !Enum.TryParse<EditorGeometry.AlignEdge>(edge, out var alignEdge)) return;
        var units = selected.Select(o => new EditorGeometry.AlignUnit([o.Id], canvas.FrameOf(o))).ToList();
        var reference = units.Count == 1
            ? new SceneRect(canvas.Canvas)
            : units.Skip(1).Aggregate(units[0].Frame, (a, u) =>
            {
                var x = Math.Min(a.X, u.Frame.X);
                var y = Math.Min(a.Y, u.Frame.Y);
                return new SceneRect(x, y, Math.Max(a.Right, u.Frame.Right) - x, Math.Max(a.Bottom, u.Frame.Bottom) - y);
            });
        var deltas = EditorGeometry.AlignDeltas(units, alignEdge, reference);
        if (deltas.Count == 0) return;
        foreach (var o in selected.Where(o => deltas.ContainsKey(o.Id)))
        {
            var f = canvas.FrameOf(o);
            var d = deltas[o.Id];
            o.X = f.X + d.Dx;
            o.Y = f.Y + d.Dy;
            o.Width = f.Width;
            o.Height = f.Height;
        }
        Inspector?.GeometryChanged();
        Changed(Parts.None);
    }

    // MARK: Fields

    /// <summary>The text boxes the operator types into (not mattes, not clocks or other linked text).</summary>
    public ObservableCollection<OverlayFieldRow> Fields { get; } = [];

    private void RefreshFields()
    {
        var objects = Current?.Objects ?? [];
        var mattes = SlideSceneBuilder.MaskWiring(objects).Mattes;
        var typed = objects.Where(o => o.ObjectKind == SlideObjectKind.Text && o.TextLink is null && !mattes.Contains(o.Id)).ToList();
        if (typed.Count == Fields.Count && typed.Zip(Fields).All(pair => ReferenceEquals(pair.First, pair.Second.Object)))
        {
            foreach (var field in Fields) field.Refresh();
            return;
        }
        Fields.Clear();
        foreach (var o in typed) Fields.Add(new OverlayFieldRow(o, () => FieldChanged()));
    }

    private void FieldChanged()
    {
        Inspector?.Refresh();
        Changed(Parts.Objects | Parts.Steps);
    }

    // MARK: Live

    [ObservableProperty]
    public partial bool IsLive { get; set; }

    public string LiveButtonText => IsLive ? "Take down" : "Go live";

    partial void OnIsLiveChanged(bool value) => OnPropertyChanged(nameof(LiveButtonText));

    private void OnLiveChanged()
    {
        IsLive = Current is { } overlay && main.IsOverlayLive(overlay.Id);
        foreach (var tile in Items) tile.IsLive = main.IsOverlayLive(tile.Overlay.Id);
    }

    /// <summary>Puts the overlay up on the program (playing its In), or takes it down (playing its Out).</summary>
    [RelayCommand]
    private void ToggleLive()
    {
        if (Current is not { } overlay) return;
        main.SetOverlayLive(overlay, up: !main.IsOverlayLive(overlay.Id));
        OnLiveChanged();
    }

    // MARK: Preview

    private double previewStartedAt;

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    /// <summary>How long the play-through takes (in, a hold, out), in seconds.</summary>
    public double PreviewDuration => Current is { } overlay ? Math.Max(0.1, OverlayPreview.Duration(overlay)) : 0.1;

    private double playhead;

    /// <summary>Where the preview is (seconds); setting it scrubs: the canvas freezes the overlay at that moment.</summary>
    public double Playhead
    {
        get => playhead;
        set
        {
            if (Math.Abs(playhead - value) < 1e-6) return;
            playhead = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PlayheadText));
            if (!ticking) ScrubTo(value);
        }
    }

    public string PlayheadText => $"{playhead:0.0}s / {PreviewDuration:0.0}s";

    private bool ticking;

    /// <summary>Plays the overlay going up, a short hold and coming down, on the canvas.</summary>
    [RelayCommand]
    private void PlayPreview()
    {
        if (Current is not { } overlay || canvas is null) return;
        previewStartedAt = ShowState.HostClock();
        canvas.ShowPreview(OverlayPreview.PlayThrough(overlay, SceneSize.Hd, previewStartedAt, null, BackdropScene()));
        IsPlaying = true;
        previewClock.Start();
    }

    [RelayCommand]
    private void StopPreview()
    {
        previewClock.Stop();
        IsPlaying = false;
        canvas?.EndPreview();
        // Back to the start, without the slider's move starting a scrub.
        ticking = true;
        Playhead = 0;
        ticking = false;
    }

    private void PreviewTick()
    {
        if (canvas is not { IsPreviewing: true })
        {
            previewClock.Stop();
            IsPlaying = false;
            return;
        }
        var t = ShowState.HostClock() - previewStartedAt;
        ticking = true;
        Playhead = Math.Min(t, PreviewDuration);
        ticking = false;
        // Hold the last frame (taken down) for a moment, then go back to editing.
        if (t > PreviewDuration + 0.6) StopPreview();
    }

    private void ScrubTo(double seconds)
    {
        if (Current is not { } overlay || canvas is null) return;
        previewClock.Stop();
        IsPlaying = false;
        var start = ShowState.HostClock();
        canvas.ShowPreview(OverlayPreview.PlayThrough(overlay, SceneSize.Hd, start, Math.Clamp(seconds, 0, PreviewDuration), BackdropScene()));
    }
}
