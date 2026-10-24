using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Views;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>The Edit-mode canvas: selection, the object inspector and object commands.</summary>
public sealed partial class MainViewModel
{
    private SlideCanvasEditor? canvas;

    /// <summary>Set by the main window; the canvas reports selection and edits back here.</summary>
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
            }
            canvas = value;
            if (canvas is null) return;
            canvas.SelectionChanged += OnCanvasSelectionChanged;
            canvas.Edited += OnCanvasEdited;
            canvas.DeleteRequested += DeleteSelectedObjects;
            canvas.DuplicateRequested += DuplicateSelectedObjects;
            LoadCanvas();
        }
    }

    [ObservableProperty]
    public partial ObjectInspectorViewModel? Inspector { get; set; }

    public bool HasCanvasSelection => canvas?.Selection.Count > 0;

    private void LoadCanvas() => canvas?.Load(IsEditing ? EditedSlide : null, EditedSlideTheme, CurrentPresentation);

    private List<SlideObject> SelectedObjects() =>
        EditedSlide is { } slide && canvas is not null ? slide.Objects.Where(o => canvas.Selection.Contains(o.Id)).ToList() : [];

    private void OnCanvasSelectionChanged()
    {
        var selected = SelectedObjects();
        if (selected.Count == 1 && EditedSlide is { } slide)
        {
            var theme = EditedSlideTheme;
            var template = SlideSceneBuilder.ThemeSlide(slide, theme);
            var placeholder = SlideSceneBuilder.PlaceholderAssignments(slide.Objects, template).GetValueOrDefault(selected[0].Id);
            Inspector = new ObjectInspectorViewModel(selected[0], placeholder, theme, SlideSceneBuilder.CanvasSizeFor(CurrentPresentation), InspectorChanged);
        }
        else
        {
            Inspector = null;
        }
        OnPropertyChanged(nameof(HasCanvasSelection));
    }

    private void OnCanvasEdited(bool final)
    {
        Inspector?.GeometryChanged();
        if (final) SlideEdited();
    }

    private void InspectorChanged()
    {
        canvas?.Refresh();
        SlideEdited();
        // Keep the text boxes list in step when the inspector edits text.
        foreach (var field in SlideTextFields) field.Refresh();
    }

    /// <summary>Re-binds the canvas after the slide grid is rebuilt or the selected slide changes.</summary>
    partial void OnSelectedSlideChanged(SlideTileViewModel? value) => LoadCanvas();

    partial void OnIsEditingChanged(bool oldValue, bool newValue) => LoadCanvas();

    // MARK: Adding objects

    private void AddObject(SlideObject o)
    {
        if (EditedSlide is not { } slide) return;
        slide.Objects.Add(o);
        AfterStructureChange([o.Id]);
    }

    private void AfterStructureChange(IEnumerable<string> select)
    {
        canvas?.Refresh();
        canvas?.Select(select);
        RefreshSlideFields();
        SlideEdited();
    }

    private void RefreshSlideFields()
    {
        SlideTextFields.Clear();
        if (EditedSlide is not { } slide) return;
        foreach (var o in slide.Objects.Where(o => o.ObjectKind == SlideObjectKind.Text && o.TextLink is null))
            SlideTextFields.Add(new SlideTextFieldViewModel(o, SlideEdited));
    }

    [RelayCommand]
    private void AddTextBox() => AddObject(new SlideObject
    {
        Id = LibraryStore.NewId(), ObjectKind = SlideObjectKind.Text, Name = "Text", Text = "Text",
        X = 360, Y = 440, Width = 1200, Height = 200,
        TextStyle = new TextStyle { FontSize = 72, HorizontalAlignment = TextHorizontalAlignment.Center, VerticalAlignment = TextVerticalAlignment.Middle },
    });

    [RelayCommand]
    private void AddBox() => AddObject(new SlideObject
    {
        Id = LibraryStore.NewId(), ObjectKind = SlideObjectKind.Shape, Name = "Box", Text = "",
        X = 660, Y = 340, Width = 600, Height = 400, ShapeKind = ShapeKind.RoundedRectangle, CornerRadius = 16,
        Fill = new ObjectFill { FillKind = FillKind.Solid, ColorHex = "#3B82F6FF" },
    });

    [RelayCommand]
    private void AddCircle() => AddObject(new SlideObject
    {
        Id = LibraryStore.NewId(), ObjectKind = SlideObjectKind.Shape, Name = "Circle", Text = "",
        X = 760, Y = 340, Width = 400, Height = 400, ShapeKind = ShapeKind.Ellipse,
        Fill = new ObjectFill { FillKind = FillKind.Solid, ColorHex = "#FFFFFFFF" },
    });

    /// <summary>Places the media selected in the bin on the slide as a picture/video box.</summary>
    [RelayCommand]
    private void AddMediaObject()
    {
        if (SelectedMedia is not { } media)
        {
            Notice = "Pick an image or video in the Media bin first, then press + Media.";
            return;
        }
        var w = 960.0;
        var h = media.Item is { PixelWidth: > 0 and var pw, PixelHeight: > 0 and var ph } ? w * ph / pw : 540;
        AddObject(new SlideObject
        {
            Id = LibraryStore.NewId(), ObjectKind = SlideObjectKind.Shape, Name = media.Name, Text = "",
            X = (1920 - w) / 2, Y = (1080 - h) / 2, Width = w, Height = h, ShapeKind = ShapeKind.Rectangle,
            Fill = new ObjectFill { FillKind = FillKind.Media, MediaId = media.Item.Id, MediaScaleMode = MediaScaleMode.Fit, Loops = media.Item.Loops },
        });
    }

    // MARK: Editing the selection

    [RelayCommand]
    private void DeleteSelectedObjects()
    {
        if (EditedSlide is not { } slide || SelectedObjects() is not { Count: > 0 } selected) return;
        foreach (var o in selected) slide.Objects.Remove(o);
        AfterStructureChange([]);
    }

    [RelayCommand]
    private void DuplicateSelectedObjects()
    {
        if (EditedSlide is not { } slide || SelectedObjects() is not { Count: > 0 } selected || canvas is null) return;
        var copies = new List<SlideObject>();
        foreach (var o in selected)
        {
            var frame = canvas.FrameOf(o);
            var copy = MxuJson.Clone(o);
            copy.Id = LibraryStore.NewId();
            copy.X = frame.X + 40;
            copy.Y = frame.Y + 40;
            copy.Width = frame.Width;
            copy.Height = frame.Height;
            copies.Add(copy);
        }
        slide.Objects.AddRange(copies);
        AfterStructureChange(copies.Select(c => c.Id));
    }

    [RelayCommand]
    private void BringToFront() => Reorder(toFront: true);

    [RelayCommand]
    private void SendToBack() => Reorder(toFront: false);

    private void Reorder(bool toFront)
    {
        if (EditedSlide is not { } slide || SelectedObjects() is not { Count: > 0 } selected) return;
        foreach (var o in selected) slide.Objects.Remove(o);
        if (toFront) slide.Objects.AddRange(selected);
        else slide.Objects.InsertRange(0, selected);
        AfterStructureChange(selected.Select(o => o.Id));
    }

    /// <summary>Gives selected text back its theme position and style.</summary>
    [RelayCommand]
    private void ResetToTheme()
    {
        var selected = SelectedObjects().Where(o => o.ObjectKind == SlideObjectKind.Text).ToList();
        if (selected.Count == 0 && EditedSlide is { } slide) selected = slide.Objects.Where(o => o.ObjectKind == SlideObjectKind.Text).ToList();
        foreach (var o in selected)
        {
            o.TextStyle = null;
            o.X = o.Y = o.Width = o.Height = null;
        }
        AfterStructureChange(selected.Select(o => o.Id));
    }

    /// <summary>Aligns the selection to each other (two or more) or to the slide (one).</summary>
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
        ApplyDeltas(selected, EditorGeometry.AlignDeltas(units, alignEdge, reference));
    }

    [RelayCommand]
    private void Distribute(string axis)
    {
        if (canvas is null || SelectedObjects() is not { Count: >= 3 } selected) return;
        var units = selected.Select(o => new EditorGeometry.AlignUnit([o.Id], canvas.FrameOf(o))).ToList();
        ApplyDeltas(selected, EditorGeometry.DistributeDeltas(units, axis == "Horizontal"));
    }

    private void ApplyDeltas(List<SlideObject> selected, Dictionary<string, EditorGeometry.Vector> deltas)
    {
        if (canvas is null || deltas.Count == 0) return;
        foreach (var o in selected.Where(o => deltas.ContainsKey(o.Id)))
        {
            var f = canvas.FrameOf(o);
            var d = deltas[o.Id];
            o.X = f.X + d.Dx;
            o.Y = f.Y + d.Dy;
            o.Width = f.Width;
            o.Height = f.Height;
        }
        canvas.Refresh();
        Inspector?.GeometryChanged();
        SlideEdited();
    }

    // MARK: Slide background colour

    public string SlideBackgroundColor
    {
        get => EditedSlide?.BackgroundFill is { FillKind: FillKind.Solid } fill ? fill.ColorHex ?? "" : "";
        set
        {
            if (EditedSlide is not { } slide) return;
            slide.BackgroundFill = string.IsNullOrEmpty(value) || value == "#00000000" ? null : new ObjectFill { FillKind = FillKind.Solid, ColorHex = value };
            canvas?.Refresh();
            SlideEdited();
        }
    }
}
