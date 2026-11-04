using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Core.Stage;
using MxU.Slides.Windows.Views;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>A stage layout in the editor's list, with its picture (sample data filled in).</summary>
public sealed partial class StageLayoutRow(ConfidenceLayout layout, RenderScene preview) : ObservableObject
{
    public ConfidenceLayout Layout { get; } = layout;

    public string Name => Layout.Name;

    /// <summary>"3 screens" when stage screens show it.</summary>
    [ObservableProperty]
    public partial string Detail { get; set; } = "";

    [ObservableProperty]
    public partial RenderScene Preview { get; set; } = preview;

    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>
/// The stage (confidence) layout editor: make a layout from a starter or copy one, rename or delete
/// it; place and size its boxes on the canvas (current and next slide, chords, notes, the clock,
/// timers and the service-tracking timers, the stage message, video countdown…), style each box,
/// snap to the canvas, other boxes and a safe margin, and see it with sample words (or what's live
/// now). The layout is the library's own copy: every change shows on the stage screens straight
/// away and is saved a moment after the changes stop.
/// <para>
/// Port of the Mac's confidence layout editing: its slide editor with a confidence-layout host
/// (SlideEditorModel's <c>.confidenceLayout</c> host over ConfidenceLayoutEditing.swift's mirror),
/// LibraryView's "New Layout" from a template, and the inspector's "Linked To" text section
/// (<see cref="StageLinkViewModel"/>). The boxes as cards, safe margins and slide notes are
/// Windows'. Multi-view tile layouts (MultiViewTiles) open read-only.
/// </para>
/// </summary>
public sealed partial class StageLayoutEditorViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel main;
    private readonly DispatcherTimer saveTimer;
    private SlideCanvasEditor? canvas;
    private bool syncingSelection;
    private bool dirty;

    public StageLayoutEditorViewModel(MainViewModel main, string? layoutId)
    {
        this.main = main;
        saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Flush(), Dispatcher.CurrentDispatcher);
        saveTimer.Stop();
        main.Library.Changed += OnLibraryChanged;
        RefreshLayouts(layoutId);
    }

    public void Dispose()
    {
        Flush();
        main.Library.Changed -= OnLibraryChanged;
        Canvas = null;
    }

    public LibraryStore Library => main.Library;

    /// <summary>The layout being edited: the library's own copy.</summary>
    public ConfidenceLayout? Current => SelectedLayout?.Layout;

    // MARK: Saving

    // Undo: integration opens this window's own undo scope around Flush (wt\p7-editing's UndoScope),
    // so every save below lands in it; nothing else here needs to change.
    /// <summary>Saves the layout now rather than after the pause (the stage screens already show the change).</summary>
    public void Flush()
    {
        saveTimer.Stop();
        if (!dirty || Current is not { } layout) return;
        dirty = false;
        SaveNow(layout);
    }

    private bool savingHere;

    private void SaveNow(ConfidenceLayout layout)
    {
        savingHere = true;
        try
        {
            Library.Save(layout);
        }
        catch (Exception ex)
        {
            Infrastructure.Log.Error($"Saving the stage layout \"{layout.Name}\" failed", ex);
            Notice = $"Couldn't save \"{layout.Name}\": {ex.Message}";
        }
        finally { savingHere = false; }
    }

    /// <summary>The layout changed: redraw the canvas, its picture and the stage screens, and save shortly.</summary>
    private void Changed(bool lists = true)
    {
        if (Current is not { } layout) return;
        dirty = true;
        canvas?.Refresh();
        if (SelectedLayout is { } row) row.Preview = PreviewScene(layout);
        if (lists) RefreshObjects();
        OnPropertyChanged(nameof(OutsideSafeNote));
        main.StageLayoutEdited();
        saveTimer.Stop();
        saveTimer.Start();
    }

    private void OnLibraryChanged(Type type, string id)
    {
        if (type != typeof(ConfidenceLayout) || savingHere) return;
        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => OnLibraryChanged(type, id));
            return;
        }
        RefreshLayouts(Current?.Id);
    }

    // MARK: Layouts

    public ObservableCollection<StageLayoutRow> Layouts { get; } = [];

    [ObservableProperty]
    public partial StageLayoutRow? SelectedLayout { get; set; }

    private bool rebuilding;
    private ConfidenceLayout? loaded;

    partial void OnSelectedLayoutChanged(StageLayoutRow? value)
    {
        if (rebuilding || ReferenceEquals(loaded, value?.Layout)) return;
        Flush();
        loaded = value?.Layout;
        LoadCurrent();
    }

    private void RefreshLayouts(string? select)
    {
        var layouts = Library.All<ConfidenceLayout>().OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var uses = main.StageScreenRows();
        rebuilding = true;
        Layouts.Clear();
        foreach (var layout in layouts)
        {
            var screens = uses.Count(r => r.LayoutId == layout.Id);
            Layouts.Add(new StageLayoutRow(layout, PreviewScene(layout)) { Detail = screens == 0 ? "" : $"On {screens} stage screen{(screens == 1 ? "" : "s")}" });
        }
        rebuilding = false;
        var wanted = Layouts.FirstOrDefault(r => r.Layout.Id == select) ?? Layouts.FirstOrDefault();
        if (!ReferenceEquals(SelectedLayout, wanted)) SelectedLayout = wanted;
        else if (wanted is not null && !ReferenceEquals(loaded, wanted.Layout))
        {
            loaded = wanted.Layout;
            LoadCurrent();
        }
    }

    /// <summary>Shows a layout by id.</summary>
    public void Open(string? layoutId)
    {
        if (Layouts.FirstOrDefault(r => r.Layout.Id == layoutId) is { } row) SelectedLayout = row;
    }

    /// <summary>The starters a new layout can begin from (Blank, Classic, Current + Next…).</summary>
    public IReadOnlyList<ConfidenceLayoutTemplate> Templates => ConfidenceLayoutTemplates.All;

    public void NewLayout(ConfidenceLayoutTemplate template)
    {
        Flush();
        var layout = StageLayoutEditing.Create(template);
        SaveNow(layout);
        RefreshLayouts(layout.Id);
        Notice = $"“{layout.Name}”: choose it for a stage screen in the Stage panel.";
    }

    [RelayCommand]
    private void DuplicateLayout()
    {
        if (Current is not { } layout) return;
        Flush();
        var copy = StageLayoutEditing.Duplicate(layout);
        SaveNow(copy);
        RefreshLayouts(copy.Id);
    }

    public bool RenameLayout(string name)
    {
        if (Current is not { } layout || !StageLayoutEditing.Rename(layout, name)) return false;
        SaveNow(layout);
        RefreshLayouts(layout.Id);
        OnPropertyChanged(nameof(LayoutName));
        return true;
    }

    /// <summary>The stage screens showing the layout (asked about before deleting it).</summary>
    public int ScreensUsing => Current is { } layout ? main.StageScreenRows().Count(r => r.LayoutId == layout.Id) : 0;

    /// <summary>Moves the layout to the Trash; screens that showed it fall back to the built-in layout.</summary>
    public void DeleteLayout()
    {
        if (Current is not { } layout) return;
        saveTimer.Stop();
        dirty = false;
        var index = Layouts.IndexOf(SelectedLayout!);
        Library.Delete<ConfidenceLayout>(layout.Id);
        loaded = null;
        RefreshLayouts(Layouts.Where(r => r.Layout.Id != layout.Id).ElementAtOrDefault(Math.Max(0, index - 1))?.Layout.Id);
    }

    public string LayoutName
    {
        get => Current?.Name ?? "";
        set
        {
            if (!RenameLayout(value)) OnPropertyChanged();
        }
    }

    public bool HasLayout => Current is not null;

    /// <summary>True for a Mac multi-view layout (tiles): shown, not edited here.</summary>
    public bool IsMultiView => Current is { } layout && StageLayoutEditing.IsMultiView(layout);

    public bool CanEdit => HasLayout && !IsMultiView;

    // MARK: Canvas size

    public IReadOnlyList<Choice<(int Width, int Height)>> CanvasSizes { get; } =
        [.. SlideSceneBuilder.CanvasPresets.Select(p => new Choice<(int Width, int Height)>((p.Width, p.Height), p.Label))];

    public Choice<(int Width, int Height)>? CanvasSize
    {
        get
        {
            var size = Current is { } layout ? StageLayoutEditing.CanvasOf(layout) : SceneSize.Hd;
            return CanvasSizes.FirstOrDefault(c => c.Value.Width == (int)size.Width && c.Value.Height == (int)size.Height)
                ?? new Choice<(int Width, int Height)>(((int)size.Width, (int)size.Height), $"{size.Width:0}×{size.Height:0}");
        }
        set
        {
            if (value is null || Current is not { } layout || !StageLayoutEditing.SetCanvas(layout, value.Value.Width, value.Value.Height)) return;
            LoadCurrent();
            Changed();
        }
    }

    // MARK: The canvas

    /// <summary>The layout as a one-slide presentation the canvas edits (its objects are the layout's own; it carries the canvas size).</summary>
    public (Presentation Mirror, Slide Slide)? Mirror { get; private set; }

    private void LoadCurrent()
    {
        Mirror = Current is { } layout ? StageLayoutEditing.EditorMirror(layout) : null;
        canvas?.Load(Mirror?.Slide, null, Mirror?.Mirror);
        Inspector = null;
        Link = null;
        RefreshObjects();
        OnPropertyChanged(string.Empty);
    }

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
                canvas.SnapTargets = null;
            }
            canvas = value;
            if (canvas is null) return;
            canvas.SceneBuilder = _ => CanvasScene();
            canvas.SnapTargets = SnapTargets;
            canvas.SelectionChanged += OnCanvasSelectionChanged;
            canvas.Edited += OnCanvasEdited;
            canvas.DeleteRequested += DeleteSelectedObjects;
            canvas.DuplicateRequested += DuplicateSelectedObjects;
            canvas.Load(Mirror?.Slide, null, Mirror?.Mirror);
        }
    }

    /// <summary>0 sample words and times (so every box reads something), 1 what's live now.</summary>
    [ObservableProperty]
    public partial int DataIndex { get; set; }

    partial void OnDataIndexChanged(int value) => canvas?.Refresh();

    private RenderScene CanvasScene() => Current is { } layout
        ? DataIndex == 1 ? main.StageScene(layout) : PreviewScene(layout)
        : new RenderScene(SceneSize.Hd);

    /// <summary>A layout as the stage shows it, with the editor's sample data in every box.</summary>
    public static RenderScene PreviewScene(ConfidenceLayout layout)
    {
        var canvas = StageLayoutEditing.CanvasOf(layout);
        var scene = new RenderScene(canvas);
        var resolved = LinkedText.PreviewObjects(layout.Objects);
        SlideSceneBuilder.AddObjects(scene, resolved.Where(o => o.Hidden != true).ToList(), LayerKind.Slide, canvas, $"stage-{layout.Id}-");
        return scene;
    }

    /// <summary>Redraws (the stage's data changed: a timer ticked, a slide went live), when showing live data.</summary>
    public void LiveTick()
    {
        if (DataIndex == 1) canvas?.Refresh();
    }

    // MARK: Safe margins and snapping

    public IReadOnlyList<Choice<double>> SafeMargins { get; } = [.. StageLayoutEditing.SafeMargins.Select(m => new Choice<double>(m.Fraction, m.Label))];

    /// <summary>The safe margin drawn and snapped to (remembered while the app runs).</summary>
    public Choice<double>? SafeMargin
    {
        get => SafeMargins.FirstOrDefault(m => Math.Abs(m.Value - safeFraction) < 1e-6) ?? SafeMargins[0];
        set
        {
            if (value is null) return;
            safeFraction = value.Value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SafeArea));
            OnPropertyChanged(nameof(OutsideSafeNote));
        }
    }

    private static double safeFraction = 0.05;

    /// <summary>The safe area on the canvas, or null with no margin.</summary>
    public SceneRect? SafeArea => Current is { } layout && safeFraction > 0 ? StageLayoutEditing.SafeArea(StageLayoutEditing.CanvasOf(layout), safeFraction) : null;

    /// <summary>The canvas snaps to the safe area's edges as it does to other boxes'.</summary>
    private IEnumerable<SceneRect> SnapTargets() => SafeArea is { } safe ? [safe] : [];

    /// <summary>"2 boxes reach past the safe margin" (an older monitor may cut them off), or null.</summary>
    public string? OutsideSafeNote
    {
        get
        {
            if (SafeArea is not { } safe || Current is not { } layout) return null;
            var outside = StageLayoutEditing.OutsideSafeArea(layout.Objects, safe).Count;
            return outside == 0 ? null : $"{outside} box{(outside == 1 ? " reaches" : "es reach")} past the safe margin: some monitors cut the edges off.";
        }
    }

    // MARK: Selection and the inspectors

    [ObservableProperty]
    public partial ObjectInspectorViewModel? Inspector { get; set; }

    /// <summary>What the selected text box shows (and that source's settings).</summary>
    [ObservableProperty]
    public partial StageLinkViewModel? Link { get; set; }

    private List<SlideObject> SelectedObjects() =>
        Current is { } layout && canvas is not null ? layout.Objects.Where(o => canvas.Selection.Contains(o.Id)).ToList() : [];

    public bool HasSelection => canvas?.Selection.Count > 0;

    private void OnCanvasSelectionChanged()
    {
        var selected = SelectedObjects();
        var canvasSize = Current is { } layout ? StageLayoutEditing.CanvasOf(layout) : SceneSize.Hd;
        Inspector = selected.Count == 1 ? new ObjectInspectorViewModel(selected[0], null, null, canvasSize, InspectorChanged) { Siblings = Current?.Objects, Library = Library, ShowsTextSource = false } : null;
        Link = selected is [{ ObjectKind: SlideObjectKind.Text } text] ? new StageLinkViewModel(text, LinkChanged, TimerChoices) : null;
        syncingSelection = true;
        SelectedObjectRow = selected.Count == 1 ? ObjectRows.FirstOrDefault(r => r.Id == selected[0].Id) : null;
        syncingSelection = false;
        OnPropertyChanged(nameof(HasSelection));
    }

    /// <summary>The timers a box can show: the Timers panel's, then the service-tracking ones.</summary>
    private IReadOnlyList<Choice<string?>> TimerChoices()
    {
        var list = main.Timers.Timers.Where(t => !ServiceTrackingTimers.IsPermanent(t.Id)).Select(t => new Choice<string?>(t.Id, t.Name)).ToList();
        foreach (var subject in Enum.GetValues<ServiceTrackingTimers.Subject>())
            foreach (var face in ServiceTrackingTimers.Faces(subject))
                list.Add(new Choice<string?>(ServiceTrackingTimers.Id(subject, face), ServiceTrackingTimers.Name(subject, face)));
        return list;
    }

    private void OnCanvasEdited(bool final)
    {
        Inspector?.GeometryChanged();
        if (final) Changed(lists: false);
        else canvas?.InvalidateVisual();
    }

    private void InspectorChanged()
    {
        canvas?.Refresh();
        Link?.Refresh();
        Changed();
    }

    private void LinkChanged()
    {
        Inspector?.Refresh();
        Changed();
    }

    public void SelectObjects(IEnumerable<string> ids) => canvas?.Select(ids);

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
        if (objects.Count == ObjectRows.Count && objects.Zip(ObjectRows).All(pair => ReferenceEquals(pair.First, pair.Second.Object)))
        {
            foreach (var row in ObjectRows) row.Refresh();
            return;
        }
        syncingSelection = true;
        ObjectRows.Clear();
        foreach (var o in objects.AsEnumerable().Reverse()) ObjectRows.Add(new OverlayObjectRow(o, false));
        SelectedObjectRow = canvas?.Selection.Count == 1 ? ObjectRows.FirstOrDefault(r => canvas.Selection.Contains(r.Id)) : null;
        syncingSelection = false;
    }

    // MARK: Adding and arranging boxes

    /// <summary>The kinds of box the Add menu offers.</summary>
    public IReadOnlyList<StageBoxChoice> BoxChoices => StageLayoutEditing.Boxes;

    /// <summary>The service-tracking timers, for the Add menu's Service timer submenu.</summary>
    public IReadOnlyList<Choice<string?>> ServiceTimers => [.. TimerChoices().Where(c => c.Value is { } id && ServiceTrackingTimers.IsPermanent(id))];

    /// <summary>The Timers panel's timers, for the Add menu's Timer submenu (null: the first running one).</summary>
    public IReadOnlyList<Choice<string?>> PanelTimers => [new(null, "The first running timer"), .. TimerChoices().Where(c => c.Value is { } id && !ServiceTrackingTimers.IsPermanent(id))];

    /// <summary>Adds a box (a card with its words and caption) in the first free spot; returns its objects.</summary>
    public List<SlideObject> AddBox(StageBoxKind kind, string? timerId = null)
    {
        if (!CanEdit || Current is not { } layout) return [];
        var objects = StageLayoutEditing.MakeBox(kind, StageLayoutEditing.CanvasOf(layout), layout.Objects, timerId);
        layout.Objects.AddRange(objects);
        canvas?.Refresh();
        // The words are what you style; the card and caption come along when you drag them all.
        canvas?.Select(objects.Where(o => o.TextLink is not null || objects.Count == 1).Select(o => o.Id));
        Changed();
        return objects;
    }

    [RelayCommand]
    private void DeleteSelectedObjects()
    {
        if (!CanEdit || Current is not { } layout || SelectedObjects() is not { Count: > 0 } selected) return;
        CompositionObjects.Delete(new LayoutComposition(layout), selected);
        canvas?.Refresh();
        canvas?.Select([]);
        Changed();
    }

    [RelayCommand]
    private void DuplicateSelectedObjects()
    {
        if (!CanEdit || Current is not { } layout || canvas is null || SelectedObjects() is not { Count: > 0 } selected) return;
        var copies = CompositionObjects.Duplicate(new LayoutComposition(layout), selected, canvas.FrameOf);
        canvas.Refresh();
        canvas.Select(copies.Select(c => c.Id));
        Changed();
    }

    [RelayCommand]
    private void BringToFront() => Reorder(true);

    [RelayCommand]
    private void SendToBack() => Reorder(false);

    private void Reorder(bool toFront)
    {
        if (!CanEdit || Current is not { } layout || SelectedObjects() is not { Count: > 0 } selected) return;
        CompositionObjects.Reorder(new LayoutComposition(layout), selected, toFront);
        canvas?.Refresh();
        canvas?.Select(selected.Select(o => o.Id));
        Changed();
    }

    [RelayCommand]
    private void Align(string edge)
    {
        if (!CanEdit || canvas is null || Current is not { } layout || !Enum.TryParse<EditorGeometry.AlignEdge>(edge, out var alignEdge)) return;
        if (!CompositionObjects.Align(SelectedObjects(), alignEdge, StageLayoutEditing.CanvasOf(layout), canvas.FrameOf)) return;
        Inspector?.GeometryChanged();
        Changed(lists: false);
    }

    [ObservableProperty]
    public partial string? Notice { get; set; }

    /// <summary>A layout seen as a composition (its objects and play order), for the shared object edits.</summary>
    private sealed class LayoutComposition(ConfidenceLayout layout) : IAnimatedComposition
    {
        public List<SlideObject> Objects => layout.Objects;

        public List<string>? AnimationOrder
        {
            get => layout.AnimationOrder;
            set => layout.AnimationOrder = value;
        }
    }
}
