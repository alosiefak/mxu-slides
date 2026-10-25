using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Rendering;
using MxU.Slides.Windows.Views;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>A design in the theme editor's list: its picture (with sample words), its name and group.</summary>
public sealed partial class DesignRowViewModel(Slide design, RenderScene preview) : ObservableObject
{
    public Slide Design { get; } = design;

    public string Name => Design.Name;

    public string Folder => Design.Folder ?? "";

    [ObservableProperty]
    public partial RenderScene Preview { get; set; } = preview;

    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>A text placeholder of the design: the box a slide's words go into (matched by name, then in order).</summary>
public sealed partial class PlaceholderRowViewModel(SlideObject o, Action changed) : ObservableObject
{
    public SlideObject Object { get; } = o;

    public string Name
    {
        get => Object.Name;
        set
        {
            var trimmed = value.Trim();
            if (trimmed.Length == 0 || trimmed == Object.Name) return;
            Object.Name = trimmed;
            OnPropertyChanged();
            changed();
        }
    }

    /// <summary>The words the design shows (a sample; each slide puts its own words here).</summary>
    public string Sample
    {
        get => Object.Text;
        set
        {
            var normalized = value.Replace("\r\n", "\n");
            if (normalized == Object.Text) return;
            Object.Text = normalized;
            OnPropertyChanged();
            changed();
        }
    }

    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>
/// The theme editor: pick a theme, make, copy, rename or delete themes; pick one of its designs
/// ("Lyrics", "Lower Third"…) and make, copy, rename, file, reorder or delete designs; design one on
/// the canvas with the slide editor's canvas and inspector (objects, text styles, background,
/// placeholders, effects), animate it in the shared Animation tab, and preview it as a real slide
/// with sample words, playing its builds. The theme is the library's own copy: changes are drawn at
/// once, saved a moment after they stop, and every slide using the theme follows.
/// <para>
/// Port of the Mac's theme editing: its slide editor with a theme host (SlideEditorModel's
/// <c>.theme</c> host, <c>isThemeEditor</c>, "Add Category"), the theme-wide settings, and
/// ThemeExplorerView's design pictures. The worship looks' kinetic builds (Windows extras) are kept
/// working: a placeholder's build is saved again against its steps when you change them here, and
/// a kinetic preset or "one step per line" on a placeholder makes a build (Core/Library/ThemeKinetics.cs).
/// </para>
/// </summary>
public sealed partial class ThemeEditorViewModel : ObservableObject, IDisposable, IAnimationHost
{
    private readonly MainViewModel main;
    private readonly KineticExtras kinetic;
    private readonly DispatcherTimer saveTimer;
    private readonly DispatcherTimer previewClock;
    private SlideCanvasEditor? canvas;
    private bool syncingSelection;
    private bool dirty;

    /// <summary>The kinetic builds this editor keeps up to date: placeholders (by id) that had one, or were given one here.</summary>
    private readonly Dictionary<string, KineticBuild> builds = new(StringComparer.Ordinal);

    public ThemeEditorViewModel(MainViewModel main, string? themeId, string? designName = null)
    {
        this.main = main;
        kinetic = new KineticExtras(main.Library.Extras);
        saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) => Flush(), Dispatcher.CurrentDispatcher);
        saveTimer.Stop();
        previewClock = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => PreviewTick(), Dispatcher.CurrentDispatcher);
        previewClock.Stop();
        Animation = new AnimationTabViewModel(this);
        main.Library.Changed += OnLibraryChanged;
        RefreshThemes();
        Open(themeId ?? Themes.FirstOrDefault()?.Id, designName);
    }

    public void Dispose()
    {
        previewClock.Stop();
        Flush();
        main.Library.Changed -= OnLibraryChanged;
        Canvas = null;
    }

    public LibraryStore Library => main.Library;

    /// <summary>The theme being edited: the library's own copy.</summary>
    public Theme? Current { get; private set; }

    /// <summary>The design on the canvas (one of the theme's slides).</summary>
    public Slide? Design => SelectedDesign?.Design;

    // MARK: Saving

    // Undo: integration opens this window's own undo scope around Flush (wt\p7-editing's UndoScope),
    // so every save below lands in it; nothing else here needs to change.
    /// <summary>Saves the theme (then the kinetic builds against its new steps) now, rather than after the pause.</summary>
    public void Flush()
    {
        saveTimer.Stop();
        if (!dirty || Current is not { } theme) return;
        dirty = false;
        if (!main.SaveThemeNow(theme)) return;
        SaveBuilds(theme);
    }

    /// <summary>
    /// Each build this editor keeps is saved against its placeholder's steps as they are now (so a
    /// step re-timed here keeps the build); a placeholder that lost its Ins loses its build.
    /// </summary>
    private void SaveBuilds(Theme theme)
    {
        foreach (var (id, build) in builds.ToList())
        {
            var placeholder = (theme.Slides ?? []).SelectMany(d => d.Objects).FirstOrDefault(o => o.Id == id);
            if (placeholder is null) continue;
            if (placeholder.AnimationSteps?.Any(s => s.Kind == AnimationKind.In) != true)
            {
                kinetic.Remove(placeholder);
                builds.Remove(id);
                continue;
            }
            kinetic.Save(placeholder, build);
        }
    }

    /// <summary>The theme changed: redraw the canvas, the design's picture and the sample, and save shortly.</summary>
    private void Changed(bool lists = true)
    {
        if (Current is null) return;
        dirty = true;
        canvas?.Refresh();
        if (SelectedDesign is { } row) row.Preview = ThemePickerWindow.DesignScene(Current, row.Design);
        if (lists)
        {
            RefreshObjects();
            RefreshPlaceholders();
        }
        OnPropertyChanged(nameof(SampleScene));
        OnPropertyChanged(nameof(PreviewDuration));
        OnPropertyChanged(nameof(KineticNote));
        saveTimer.Stop();
        saveTimer.Start();
    }

    private bool savingHere;

    private void OnLibraryChanged(Type type, string id)
    {
        if (type != typeof(Theme)) return;
        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => OnLibraryChanged(type, id));
            return;
        }
        if (savingHere) return;
        RefreshThemes();
        // The theme being edited was replaced (an import) or deleted elsewhere: follow the library.
        if (Current is { } current && !ReferenceEquals(Library.Get<Theme>(current.Id), current))
            Open(Library.Get<Theme>(current.Id)?.Id ?? Themes.FirstOrDefault()?.Id, Design?.Name);
    }

    // MARK: Themes

    public ObservableCollection<ThemeChoice> Themes { get; } = [];

    private void RefreshThemes()
    {
        var all = Library.All<Theme>().OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).Select(t => new ThemeChoice(t.Id, t.Name)).ToList();
        if (all.SequenceEqual(Themes)) return;
        var keep = Current?.Id;
        syncingTheme = true;
        Themes.Clear();
        foreach (var choice in all) Themes.Add(choice);
        SelectedTheme = Themes.FirstOrDefault(t => t.Id == keep);
        syncingTheme = false;
    }

    private bool syncingTheme;

    [ObservableProperty]
    public partial ThemeChoice? SelectedTheme { get; set; }

    partial void OnSelectedThemeChanged(ThemeChoice? value)
    {
        if (syncingTheme || value is null || value.Id == Current?.Id) return;
        Open(value.Id, null);
    }

    /// <summary>Shows a theme (and one of its designs, by name, when given).</summary>
    public void Open(string? themeId, string? designName)
    {
        Flush();
        StopPreview();
        Current = Library.Get<Theme>(themeId);
        builds.Clear();
        foreach (var placeholder in (Current?.Slides ?? []).SelectMany(d => d.Objects).Where(o => o.ObjectKind == SlideObjectKind.Text))
            if (kinetic.For(placeholder) is { } build) builds[placeholder.Id] = build;
        syncingTheme = true;
        SelectedTheme = Themes.FirstOrDefault(t => t.Id == Current?.Id);
        syncingTheme = false;
        RefreshDesigns(select: designName);
        OnPropertyChanged(string.Empty);
    }

    public bool HasTheme => Current is not null;

    public bool IsStarter => Current is { } theme && ThemeLibrary.IsStarter(theme);

    [RelayCommand]
    private void NewTheme()
    {
        var theme = ThemeLibrary.Create();
        Flush();
        savingHere = true;
        try { main.SaveThemeNow(theme); }
        finally { savingHere = false; }
        RefreshThemes();
        Open(theme.Id, null);
        Notice = "A new theme with four designs. Pick one on the left to design it.";
    }

    /// <summary>A copy of the theme (fresh ids inside), its kinetic builds and design transitions with it.</summary>
    [RelayCommand]
    private void DuplicateTheme()
    {
        if (Current is not { } theme) return;
        Flush();
        var (copy, ids) = ThemeLibrary.Duplicate(theme);
        savingHere = true;
        try
        {
            if (!main.SaveThemeNow(copy)) return;
            var originals = (theme.Slides ?? []).SelectMany(d => d.Objects).ToDictionary(o => o.Id);
            foreach (var placeholder in (copy.Slides ?? []).SelectMany(d => d.Objects))
                if (ids.Objects.FirstOrDefault(p => p.Value == placeholder.Id).Key is { } from && originals.TryGetValue(from, out var original))
                    kinetic.Copied(original, placeholder);
            foreach (var design in theme.Slides ?? [])
                if (ids.Designs.TryGetValue(design.Id, out var copyId) && copy.Slides?.FirstOrDefault(d => d.Id == copyId) is { } copied)
                    main.TransitionExtras.Copied(design, copied);
        }
        finally { savingHere = false; }
        RefreshThemes();
        Open(copy.Id, Design?.Name);
        Notice = $"“{copy.Name}” is your own copy: change it as you like.";
    }

    /// <summary>Renames the theme; false for a blank name.</summary>
    public bool RenameTheme(string name)
    {
        if (Current is not { } theme || !ThemeLibrary.Rename(theme, name)) return false;
        Flush();
        savingHere = true;
        try { main.SaveThemeNow(theme); }
        finally { savingHere = false; }
        RefreshThemes();
        main.RefreshThemes();
        OnPropertyChanged(nameof(ThemeName));
        return true;
    }

    /// <summary>Who uses the theme (asked about before deleting it).</summary>
    public List<ThemeLibrary.ThemeUse> ThemeUses => Current is { } theme ? ThemeLibrary.Uses(Library.All<Presentation>(), theme.Id) : [];

    /// <summary>Moves the theme to the library's Trash (slides that used it then show with no theme).</summary>
    public void DeleteTheme()
    {
        if (Current is not { } theme) return;
        saveTimer.Stop();
        dirty = false;
        var index = Themes.ToList().FindIndex(t => t.Id == theme.Id);
        Library.Delete<Theme>(theme.Id);
        RefreshThemes();
        Open(Themes.ElementAtOrDefault(Math.Clamp(index, 0, Math.Max(0, Themes.Count - 1)))?.Id, null);
    }

    // MARK: The theme's own settings (its defaults, used where a design says nothing)

    public string ThemeName
    {
        get => Current?.Name ?? "";
        set
        {
            if (!RenameTheme(value)) OnPropertyChanged();
        }
    }

    public IReadOnlyList<string> FontFamilies { get; } = FontMap.InstalledFamilies();

    public IReadOnlyList<string> FontChoices => Current is { } theme && !FontFamilies.Contains(theme.FontFamily) ? [theme.FontFamily, .. FontFamilies] : FontFamilies;

    public string ThemeFont
    {
        get => Current?.FontFamily ?? "";
        set
        {
            if (Current is not { } theme || string.IsNullOrWhiteSpace(value) || value == theme.FontFamily) return;
            theme.FontFamily = value.Trim();
            Changed();
        }
    }

    public string ThemeFontSize
    {
        get => Current is { } theme ? theme.FontSize.ToString("0.#", CultureInfo.CurrentCulture) : "";
        set
        {
            if (Current is not { } theme || !double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var size) || size <= 0) return;
            theme.FontSize = Math.Clamp(size, 4, 1000);
            Changed();
        }
    }

    public string ThemeTextColor
    {
        get => Current?.TextColorHex ?? "#FFFFFFFF";
        set
        {
            if (Current is not { } theme || string.IsNullOrEmpty(value) || value == theme.TextColorHex) return;
            theme.TextColorHex = value;
            Changed();
        }
    }

    public string ThemeBackgroundColor
    {
        get => Current?.BackgroundColorHex ?? "#000000FF";
        set
        {
            if (Current is not { } theme || string.IsNullOrEmpty(value) || value == theme.BackgroundColorHex) return;
            theme.BackgroundColorHex = value;
            Changed();
        }
    }

    // MARK: Designs

    public ObservableCollection<DesignRowViewModel> Designs { get; } = [];

    [ObservableProperty]
    public partial DesignRowViewModel? SelectedDesign { get; set; }

    private bool rebuildingDesigns;

    partial void OnSelectedDesignChanged(DesignRowViewModel? value)
    {
        if (rebuildingDesigns) return;
        StopPreview();
        LoadDesign();
    }

    private void RefreshDesigns(string? select = null)
    {
        var keep = select ?? Design?.Name;
        rebuildingDesigns = true;
        Designs.Clear();
        if (Current is { } theme)
            foreach (var design in theme.Slides ?? [])
                Designs.Add(new DesignRowViewModel(design, ThemePickerWindow.DesignScene(theme, design)));
        rebuildingDesigns = false;
        SelectedDesign = Designs.FirstOrDefault(d => string.Equals(d.Name, keep, StringComparison.OrdinalIgnoreCase)) ?? Designs.FirstOrDefault();
        if (SelectedDesign is null) LoadDesign();
    }

    [RelayCommand]
    private void AddDesign()
    {
        if (Current is not { } theme) return;
        var design = ThemeLibrary.AddDesign(theme, Design?.Id);
        Changed(lists: false);
        RefreshDesigns(design.Name);
    }

    [RelayCommand]
    private void DuplicateDesign()
    {
        if (Current is not { } theme || Design is not { } design) return;
        if (ThemeLibrary.DuplicateDesign(theme, design.Id) is not { } copy) return;
        // Kinetic builds follow the copied placeholders (saved once the copy is saved).
        foreach (var (from, to) in copy.ObjectIds)
            if (builds.TryGetValue(from, out var build)) builds[to] = build;
        main.TransitionExtras.Copied(design, copy.Design);
        Changed(lists: false);
        RefreshDesigns(copy.Design.Name);
    }

    /// <summary>The slides that use the selected design by name (asked about when it's renamed).</summary>
    public int DesignUseCount => Current is { } theme && Design is { } design ? ThemeLibrary.DesignUses(Library.All<Presentation>(), theme.Id, design.Name).Count : 0;

    /// <summary>Renames the design; with <paramref name="renameUses"/>, the slides that name it follow. False for a blank name.</summary>
    public bool RenameDesign(string name, bool renameUses)
    {
        if (Current is not { } theme || Design is not { } design) return false;
        var old = design.Name;
        if (!ThemeLibrary.RenameDesign(theme, design.Id, name)) return false;
        if (renameUses)
            foreach (var presentation in ThemeLibrary.RenameDesignUses(Library.All<Presentation>(), theme.Id, old, design.Name))
                main.SavePresentation(presentation);
        Changed(lists: false);
        RefreshDesigns(design.Name);
        OnPropertyChanged(nameof(DesignName));
        return true;
    }

    public bool CanDeleteDesign => Current?.Slides is { Count: > 1 };

    [RelayCommand]
    private void DeleteDesign()
    {
        if (Current is not { } theme || Design is not { } design) return;
        var index = Designs.IndexOf(SelectedDesign!);
        if (!ThemeLibrary.DeleteDesign(theme, design.Id))
        {
            Notice = "A theme keeps at least one design.";
            return;
        }
        Changed(lists: false);
        RefreshDesigns(theme.Slides!.ElementAtOrDefault(Math.Clamp(index, 0, theme.Slides!.Count - 1))?.Name);
    }

    [RelayCommand]
    private void MoveDesign(string by)
    {
        if (Current is not { } theme || Design is not { } design) return;
        if (!ThemeLibrary.MoveDesign(theme, design.Id, by == "up" ? -1 : +1)) return;
        Changed(lists: false);
        RefreshDesigns(design.Name);
    }

    public string DesignName
    {
        get => Design?.Name ?? "";
        set
        {
            if (!RenameDesign(value, renameUses: false)) OnPropertyChanged();
        }
    }

    /// <summary>The group the design is filed in ("Lower Thirds"), or "" for none.</summary>
    public string DesignFolder
    {
        get => Design?.Folder ?? "";
        set
        {
            if (Current is not { } theme || Design is not { } design || !ThemeLibrary.SetDesignFolder(theme, design.Id, value)) return;
            Changed(lists: false);
            RefreshDesigns(design.Name);
        }
    }

    /// <summary>The design's background colour (none: the theme's).</summary>
    public string DesignBackgroundColor
    {
        get => Design?.BackgroundFill is { FillKind: FillKind.Solid } fill ? fill.ColorHex ?? "" : "";
        set
        {
            if (Design is not { } design) return;
            design.BackgroundFill = string.IsNullOrEmpty(value) || value == "#00000000" ? null : new ObjectFill { FillKind = FillKind.Solid, ColorHex = value };
            Changed();
        }
    }

    // MARK: Placeholders

    public ObservableCollection<PlaceholderRowViewModel> Placeholders { get; } = [];

    private void RefreshPlaceholders()
    {
        var texts = Design?.Objects.Where(o => o.ObjectKind == SlideObjectKind.Text && o.TextLink is null).ToList() ?? [];
        if (texts.Count == Placeholders.Count && texts.Zip(Placeholders).All(p => ReferenceEquals(p.First, p.Second.Object)))
        {
            foreach (var row in Placeholders) row.Refresh();
            return;
        }
        Placeholders.Clear();
        foreach (var o in texts) Placeholders.Add(new PlaceholderRowViewModel(o, () => { Inspector?.Refresh(); Changed(); }));
    }

    /// <summary>A new text placeholder, for designs with more than one place for words (a title and a body).</summary>
    [RelayCommand]
    private void AddPlaceholder()
    {
        if (Design is not { } design || RefusedWhilePosing()) return;
        var text = CompositionObjects.Text(design, y: 480, fontSize: 72);
        text.Name = ThemeLibrary.UniqueDesignName("Body", design.Objects.Select(o => o.Name));
        text.Text = "Sample text";
        AddObject(text);
    }

    // MARK: Kinetic builds (Windows extras)

    private SlideObject? SelectedText => SelectedObjects() is [{ ObjectKind: SlideObjectKind.Text } text] ? text : null;

    /// <summary>The selected placeholder's kinetic build in words, or null when it has none.</summary>
    public string? KineticNote => SelectedText is { } text && builds.TryGetValue(text.Id, out var build)
        ? $"Kinetic build (Windows): {ThemeKinetics.Describe(build)} On a Mac the words arrive together."
        : null;

    public bool HasKinetic => KineticNote is not null;

    [RelayCommand]
    private void RemoveKinetic()
    {
        if (SelectedText is not { } text || !builds.Remove(text.Id)) return;
        kinetic.Remove(text);
        Changed();
        OnPropertyChanged(nameof(HasKinetic));
        Notice = "The build is gone: the words play the theme's own steps, as on a Mac.";
    }

    // MARK: The canvas

    /// <summary>The design as the canvas edits it: its own object list (or, while posing a step, that step's pose in its object's place).</summary>
    public Slide? Mirror { get; private set; }

    private string? posedStepId;

    private void LoadDesign()
    {
        posedStepId = null;
        Mirror = Design is { } design ? new Slide { Id = design.Id, Name = design.Name, Objects = design.Objects, AnimationOrder = design.AnimationOrder, BackgroundFill = design.BackgroundFill } : null;
        canvas?.Load(Mirror, null, null);
        Inspector = null;
        Animation.SelectedStepRow = null;
        RefreshObjects();
        RefreshPlaceholders();
        Animation.Reload();
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

    /// <summary>
    /// The design drawn as itself: its objects over the theme's background, with the theme's font and
    /// colour where they say nothing (a theme with no designs of its own, so nothing is composed in).
    /// While posing a step, a faint copy shows where the posed object rests.
    /// </summary>
    private RenderScene CanvasScene()
    {
        if (Current is not { } theme || Mirror is null) return new RenderScene(SceneSize.Hd, new SceneColor(0.1, 0.1, 0.11));
        var defaults = new Theme
        {
            Id = theme.Id, Name = theme.Name, FontFamily = theme.FontFamily, FontSize = theme.FontSize,
            TextColorHex = theme.TextColorHex, BackgroundColorHex = theme.BackgroundColorHex,
        };
        List<SlideObject> objects = [.. Mirror.Objects];
        if (posedStepId is not null && Design is { } design && AnimationEditing.Find(design, posedStepId)?.ObjectId is { } posed
            && design.Objects.FirstOrDefault(o => o.Id == posed) is { } rest)
        {
            var faint = Core.Json.MxuJson.Clone(rest);
            faint.Id = rest.Id + ".resting";
            faint.Opacity = Math.Clamp((rest.Opacity ?? 1) * 0.3, 0, 1);
            faint.AnimationSteps = null;
            objects.Insert(0, faint);
        }
        var slide = new Slide { Id = Mirror.Id, Name = Mirror.Name, Objects = objects, BackgroundFill = Design?.BackgroundFill, Background = Design?.Background };
        return SlideSceneBuilder.Scene(slide, defaults, null);
    }

    [ObservableProperty]
    public partial ObjectInspectorViewModel? Inspector { get; set; }

    private List<SlideObject> SelectedObjects() =>
        Design is { } design && canvas is not null ? design.Objects.Where(o => canvas.Selection.Contains(o.Id)).ToList() : [];

    private List<SlideObject> CanvasSelected() =>
        Mirror is { } mirror && canvas is not null ? mirror.Objects.Where(o => canvas.Selection.Contains(o.Id)).ToList() : [];

    public bool HasSelection => canvas?.Selection.Count > 0;

    private void OnCanvasSelectionChanged()
    {
        var selected = CanvasSelected();
        Inspector = selected.Count == 1 && Current is { } theme
            ? new ObjectInspectorViewModel(selected[0], null, theme, SceneSize.Hd, InspectorChanged) { Siblings = Mirror?.Objects, Library = Library }
            : null;
        syncingSelection = true;
        SelectedObjectRow = selected.Count == 1 ? ObjectRows.FirstOrDefault(r => r.Id == selected[0].Id) : null;
        syncingSelection = false;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(KineticNote));
        OnPropertyChanged(nameof(HasKinetic));
        Animation.SelectionChanged();
    }

    private void OnCanvasEdited(bool final)
    {
        Inspector?.GeometryChanged();
        if (!final) return;
        if (posedStepId is not null) Animation.PoseEdited();
        Changed(lists: false);
    }

    private void InspectorChanged()
    {
        canvas?.Refresh();
        Changed();
        if (posedStepId is not null) Animation.PoseEdited();
        foreach (var row in Placeholders) row.Refresh();
    }

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
        var objects = Design?.Objects ?? [];
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
        foreach (var o in objects.AsEnumerable().Reverse()) ObjectRows.Add(new OverlayObjectRow(o, mattes.Contains(o.Id)));
        SelectedObjectRow = canvas?.Selection.Count == 1 ? ObjectRows.FirstOrDefault(r => canvas.Selection.Contains(r.Id)) : null;
        syncingSelection = false;
    }

    private bool RefusedWhilePosing()
    {
        if (posedStepId is null) return false;
        Notice = "You're posing a step. Press Done posing first, then add, copy or delete objects.";
        return true;
    }

    private void AddObject(SlideObject o)
    {
        if (RefusedWhilePosing() || Design is not { } design) return;
        design.Objects.Add(o);
        AfterStructureChange([o.Id]);
    }

    private void AfterStructureChange(IEnumerable<string> select)
    {
        canvas?.Refresh();
        canvas?.Select(select);
        Changed();
        Animation.Reload();
    }

    [RelayCommand]
    private void AddText() => AddPlaceholder();

    [RelayCommand]
    private void AddBox()
    {
        AddObject(CompositionObjects.Box());
    }

    [RelayCommand]
    private void AddCircle() => AddObject(CompositionObjects.Circle());

    [RelayCommand]
    private void AddMedia()
    {
        if (main.SelectedMedia is not { } media)
        {
            Notice = "Pick an image or video in the main window's Media bin first, then press Media.";
            return;
        }
        AddObject(CompositionObjects.Media(media.Item, SceneSize.Hd));
    }

    [RelayCommand]
    private void DeleteSelectedObjects()
    {
        if (RefusedWhilePosing() || Design is not { } design || SelectedObjects() is not { Count: > 0 } selected) return;
        CompositionObjects.Delete(design, selected);
        AfterStructureChange([]);
    }

    [RelayCommand]
    private void DuplicateSelectedObjects()
    {
        if (RefusedWhilePosing() || Design is not { } design || canvas is null || SelectedObjects() is not { Count: > 0 } selected) return;
        var copies = CompositionObjects.Duplicate(design, selected, canvas.FrameOf);
        AfterStructureChange(copies.Select(c => c.Id));
    }

    [RelayCommand]
    private void BringToFront() => Reorder(toFront: true);

    [RelayCommand]
    private void SendToBack() => Reorder(toFront: false);

    private void Reorder(bool toFront)
    {
        if (Design is not { } design || SelectedObjects() is not { Count: > 0 } selected) return;
        CompositionObjects.Reorder(design, selected, toFront);
        AfterStructureChange(selected.Select(o => o.Id));
    }

    [RelayCommand]
    private void Align(string edge)
    {
        if (canvas is null || !Enum.TryParse<EditorGeometry.AlignEdge>(edge, out var alignEdge)) return;
        if (!CompositionObjects.Align(CanvasSelected(), alignEdge, SceneSize.Hd, canvas.FrameOf)) return;
        Inspector?.GeometryChanged();
        Changed(lists: false);
    }

    [ObservableProperty]
    public partial string? Notice { get; set; }

    // MARK: Which tab the right-hand side shows

    /// <summary>0 the object, 1 animation, 2 the design, 3 the theme.</summary>
    [ObservableProperty]
    public partial int Tab { get; set; }

    partial void OnTabChanged(int value)
    {
        OnPropertyChanged(nameof(ShowingObjectTab));
        OnPropertyChanged(nameof(ShowingAnimationTab));
        OnPropertyChanged(nameof(ShowingDesignTab));
        OnPropertyChanged(nameof(ShowingThemeTab));
    }

    public bool ShowingObjectTab { get => Tab == 0; set { if (value) Tab = 0; else OnPropertyChanged(); } }

    public bool ShowingAnimationTab { get => Tab == 1; set { if (value) Tab = 1; else OnPropertyChanged(); } }

    public bool ShowingDesignTab { get => Tab == 2; set { if (value) Tab = 2; else OnPropertyChanged(); } }

    public bool ShowingThemeTab { get => Tab == 3; set { if (value) Tab = 3; else OnPropertyChanged(); } }

    // MARK: The preview: the design as a slide with sample words

    /// <summary>The words the preview puts in the design's first placeholder.</summary>
    [ObservableProperty]
    public partial string SampleText { get; set; } = DesignPreview.SampleLyrics;

    partial void OnSampleTextChanged(string value)
    {
        OnPropertyChanged(nameof(SampleScene));
        OnPropertyChanged(nameof(PreviewDuration));
    }

    private Slide? SampleSlide => Current is { } theme && Design is { } design ? DesignPreview.SampleSlide(design, theme.Id, SampleText.Replace("\r\n", "\n")) : null;

    /// <summary>The design as a real slide shows it, with the sample words, at rest (the Design tab's picture).</summary>
    public AnimatedScene? SampleScene => Current is { } theme && SampleSlide is { } sample
        ? DesignPreview.PlayThrough(sample, theme, SceneSize.Hd, 0, pager: TextLayout.Pager, still: true)
        : null;

    public double PreviewDuration => Current is { } theme && SampleSlide is { } sample ? Math.Max(0.1, DesignPreview.Duration(sample, theme, SceneSize.Hd, TextLayout.Pager)) : 0.1;

    private double previewStartedAt;
    private bool ticking;
    private double playhead;

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

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

    /// <summary>Plays the sample slide through: its Ins, each click in turn, a hold, then its Outs.</summary>
    [RelayCommand]
    private void PlayPreview() => PlayPreviewFrom(0);

    public void PlayPreviewFrom(double seconds)
    {
        if (Current is not { } theme || canvas is null || SampleSlide is not { } sample) return;
        if (posedStepId is not null) Animation.Posing = false;
        previewStartedAt = ShowState.HostClock() - Math.Max(seconds, 0);
        canvas.ShowPreview(DesignPreview.PlayThrough(sample, theme, SceneSize.Hd, previewStartedAt, pager: TextLayout.Pager));
        IsPlaying = true;
        previewClock.Start();
    }

    [RelayCommand]
    private void StopPreview()
    {
        previewClock.Stop();
        IsPlaying = false;
        canvas?.EndPreview();
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
        if (t > PreviewDuration + 0.6) StopPreview();
    }

    private void ScrubTo(double seconds)
    {
        if (Current is not { } theme || canvas is null || SampleSlide is not { } sample) return;
        previewClock.Stop();
        IsPlaying = false;
        canvas.ShowPreview(DesignPreview.PlayThrough(sample, theme, SceneSize.Hd, ShowState.HostClock(), Math.Clamp(seconds, 0, PreviewDuration), TextLayout.Pager));
    }

    // MARK: The Animation tab (IAnimationHost)

    public AnimationTabViewModel Animation { get; }

    IAnimatedComposition? IAnimationHost.Composition => Design;

    IReadOnlyList<SlideObject> IAnimationHost.SelectedObjects => SelectedObjects();

    void IAnimationHost.AnimationChanged(bool objects) => Changed(lists: objects);

    bool IAnimationHost.IsOverlay => false;

    void IAnimationHost.Notify(string message) => Notice = message;

    void IAnimationHost.ScrubPreview(double seconds) => Playhead = Math.Clamp(seconds, 0, PreviewDuration);

    /// <summary>The canvas edits a step's pose instead of the design's objects (see the overlay editor's Pose).</summary>
    public void Pose(string? stepId)
    {
        if (Design is not { } design) return;
        StopPreview();
        var posed = stepId is null ? null : AnimationTimelineEditing.PosedObjects(design, stepId);
        posedStepId = posed is null ? null : stepId;
        Mirror = new Slide { Id = design.Id, Name = design.Name, Objects = posed ?? design.Objects, AnimationOrder = design.AnimationOrder, BackgroundFill = design.BackgroundFill };
        var keep = canvas?.Selection.ToList() ?? [];
        canvas?.Load(Mirror, null, null);
        canvas?.Select(posedStepId is not null && AnimationEditing.Find(design, posedStepId) is { } found ? [found.ObjectId] : keep);
    }

    /// <summary>
    /// A preset on objects of the design. A kinetic preset on a text placeholder becomes whole-text
    /// steps in the theme and a kinetic build beside it (the placeholder's sample words aren't the
    /// slides' words); everything else is put on as the Mac puts presets on.
    /// </summary>
    List<string> IAnimationHost.ApplyPreset(PresetChoice choice, List<string> objectIds, AnimationPresetApplication.Mode mode)
    {
        if (Design is not { } design) return [];
        if (choice.KineticRecipe is not { } recipe) return choice.Apply(design, objectIds, mode);
        var added = new List<string>();
        var others = new List<string>();
        foreach (var id in objectIds)
        {
            if (design.Objects.FirstOrDefault(o => o.Id == id) is not { ObjectKind: SlideObjectKind.Text } text)
            {
                others.Add(id);
                continue;
            }
            var (ids, build) = ThemeKinetics.Apply(design, text, recipe, mode);
            builds[text.Id] = build;
            added.AddRange(ids);
        }
        if (others.Count > 0) added.AddRange(choice.Apply(design, others, mode));
        OnPropertyChanged(nameof(KineticNote));
        OnPropertyChanged(nameof(HasKinetic));
        return added;
    }

    /// <summary>One step per line or word on a placeholder: a kinetic build over every slide's words (Ins only).</summary>
    List<string> IAnimationHost.GenerateSteps(SlideObject text, AnimationRanges.Generator by, AnimationKind kind)
    {
        if (Design is not { } design) return [];
        if (kind != AnimationKind.In) Notice = "A theme's words build in one piece after another as they arrive: this makes an In.";
        var split = by == AnimationRanges.Generator.Word ? KineticSplit.Words : KineticSplit.Lines;
        var (ids, build) = ThemeKinetics.PerPiece(design, text, split);
        builds[text.Id] = build;
        OnPropertyChanged(nameof(KineticNote));
        OnPropertyChanged(nameof(HasKinetic));
        Notice ??= $"Each {(split == KineticSplit.Words ? "word" : "line")} of every slide's words fades in after the one before (Windows; on a Mac they arrive together).";
        return ids;
    }
}
