using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.Outputs;
using MxU.Slides.Windows.Rendering;
using AnimatedScene = MxU.Slides.Core.Animation.AnimatedScene;
using Point = System.Windows.Point;
using UnitPoint = MxU.Slides.Core.Scene.EditorGeometry.Point;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// Draws and edits a screen's output masks over its picture: black out a pillar,
/// the wings of a set, or show only a window cut into an LED wall. Every change
/// applies to the real outputs straight away and is saved with the screen setup.
/// Port of the Mac's OutputMaskEditor (MaskEditor.swift); the pen's rules are
/// <see cref="OutputMaskPen"/> in Core, the masks themselves <see cref="OutputMask"/>.
/// Open it with <see cref="Edit"/>.
/// </summary>
public sealed class MaskEditorWindow : Window
{
    private sealed record Choice<T>(T Value, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record MaskRow(Guid Id, string Name, string Detail);

    private enum PenMode { None, Drawing, Editing }

    private readonly OutputManager outputs;
    private readonly Guid screenId;
    private readonly Guid previewSliceId;
    private readonly IReadOnlyList<OutputMask> original;
    private readonly SceneView picture = new() { PlaysVideo = false, Letterbox = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)) };
    private readonly MaskSurface surface;
    private readonly OutputMaskPen pen = new();
    private readonly ListBox maskList = new() { MinHeight = 120, MaxHeight = 220 };
    private readonly StackPanel details = new();
    private readonly TextBlock hint = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleButton drawButton = new() { Content = "+ Draw a mask", Margin = new Thickness(0, 0, 6, 0) };
    private readonly ComboBox pictureCombo = new() { Width = 220 };
    private readonly DispatcherTimer pictureTimer;
    private readonly DispatcherTimer featherTimer;
    private PenMode mode;
    private Guid? selectedId;
    private bool loading;
    private (Guid Id, double Value)? pendingFeather;
    private BitmapSource? preview;

    private OutputConfiguration Config => outputs.Config;

    private PlaceholderScreen? Screen => Config.Screen(screenId);

    private IReadOnlyList<OutputMask> Masks => Config.Masks(screenId);

    private OutputMask? Selected => selectedId is { } id ? Masks.FirstOrDefault(m => m.Id == id) : null;

    /// <summary>
    /// Opens the mask editor for a screen (over its whole picture) and waits until it is
    /// closed. <paramref name="sliceId"/> picks which output's masks the darkened preview
    /// shows (default: the screen's own). Returns whether the screen's masks changed.
    /// </summary>
    public static bool Edit(Window owner, OutputManager outputs, Guid screenId, Guid? sliceId = null)
    {
        if (outputs.Config.Screen(screenId) is null) return false;
        var window = new MaskEditorWindow(outputs, screenId, sliceId) { Owner = owner };
        window.ShowDialog();
        return window.Changed;
    }

    /// <summary>Whether the screen's masks differ from when the window opened.</summary>
    public bool Changed => !original.SequenceEqual(Masks);

    public MaskEditorWindow(OutputManager outputs, Guid screenId, Guid? sliceId = null)
    {
        this.outputs = outputs;
        this.screenId = screenId;
        previewSliceId = sliceId is { } slice && Config.SliceInfo(slice)?.ScreenId == screenId ? slice : screenId;
        original = Masks.ToList();
        surface = new MaskSurface(this);

        Title = $"Masks: {Screen?.Name ?? "Screen"}";
        Width = 1120;
        Height = 720;
        MinWidth = 860;
        MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)FindResource("BasePlane");

        pen.Completed += OnPenCompleted;
        pen.Cancelled += () => SetMode(PenMode.None);

        drawButton.Click += (_, _) => SetMode(drawButton.IsChecked == true ? PenMode.Drawing : PenMode.None);
        var rectangle = new Button { Content = "+ Rectangle", Margin = new Thickness(0, 0, 12, 0), ToolTip = "Add a rectangle in the middle, then drag its corners into place" };
        rectangle.Click += (_, _) => AddMask(OutputMaskLibrary.Rectangle(0.4, 0.3, 0.2, 0.4)!);
        var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { drawButton, rectangle } };
        DockPanel.SetDock(buttons, Dock.Left);
        toolbar.Children.Add(buttons);
        hint.Style = (Style)FindResource("Muted");
        toolbar.Children.Add(hint);

        var canvas = new Grid { ClipToBounds = true, Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)) };
        canvas.Children.Add(picture);
        canvas.Children.Add(surface);
        var canvasFrame = new Border { BorderBrush = (Brush)FindResource("Border"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Child = canvas };
        var left = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        left.Children.Add(toolbar);
        left.Children.Add(canvasFrame);

        maskList.SelectionChanged += (_, _) =>
        {
            if (loading) return;
            Select((maskList.SelectedItem as MaskRow)?.Id);
        };
        maskList.ItemTemplate = RowTemplate();
        var side = new StackPanel { Margin = new Thickness(0, 0, 4, 0) };
        side.Children.Add(Section("MASKS"));
        side.Children.Add(maskList);
        side.Children.Add(details);
        var sideScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = side };

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        body.Children.Add(left);
        Grid.SetColumn(sideScroll, 2);
        body.Children.Add(sideScroll);

        pictureCombo.ItemsSource = new[]
        {
            new Choice<bool>(false, "Show what's on the screen now"),
            new Choice<bool>(true, "Show the alignment grid"),
        };
        pictureCombo.SelectedIndex = outputs.Picture(screenId) is null ? 1 : 0;
        pictureCombo.SelectionChanged += (_, _) => ShowPicture(force: true);
        // Not a default button: Enter closes the shape being drawn, not the window.
        var done = new Button { Content = "Done", MinWidth = 90, Style = (Style)FindResource("AccentButton") };
        done.Click += (_, _) => Close();
        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(done, Dock.Right);
        footer.Children.Add(done);
        footer.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                pictureCombo,
                new TextBlock
                {
                    Text = "Masks change the real outputs as you edit.", Style = (Style)FindResource("Muted"), FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
                },
            },
        });

        var header = new TextBlock
        {
            Text = "Black out what the projector shouldn't light (a pillar, the stage wings), or show only part of the picture. "
                + "Draw a shape over the screen's picture; feather softens its edge.",
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)FindResource("Muted"),
            Margin = new Thickness(0, 0, 0, 10),
        };
        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(footer);
        root.Children.Add(body);
        Content = root;

        // The picture follows what the screen shows (clocks, slides changing) while the editor is open.
        pictureTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => ShowPicture(force: false), Dispatcher);
        // The feather slider waits for a pause before redrawing the outputs' mask, so dragging it stays smooth.
        featherTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Normal, (_, _) => ApplyFeather(), Dispatcher);
        featherTimer.Stop();
        PreviewKeyDown += OnKey;
        Loaded += (_, _) =>
        {
            ShowPicture(force: true);
            pictureTimer.Start();
        };
        Closed += (_, _) =>
        {
            pictureTimer.Stop();
            ApplyFeather();
        };

        RefreshList(Masks.FirstOrDefault()?.Id);
        SetMode(PenMode.None);
    }

    // MARK: The picture

    private bool ShowsGrid => (pictureCombo.SelectedItem as Choice<bool>)?.Value ?? false;

    private void ShowPicture(bool force)
    {
        if (Screen is not { } screen) return;
        var live = ShowsGrid ? null : outputs.Picture(screenId);
        var scene = live ?? AnimatedScene.From(OutputConfiguration.AlignmentGrid(screen.Size));
        if (force || picture.Scene is null) picture.Present(scene, SceneTransition.Cut);
        else if (live is not null) picture.Update(scene);
    }

    // MARK: Masks list

    private DataTemplate RowTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(MarginProperty, new Thickness(2, 4, 2, 4));
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(MaskRow.Name)));
        name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        var detail = new FrameworkElementFactory(typeof(TextBlock));
        detail.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(MaskRow.Detail)));
        detail.SetValue(TextBlock.FontSizeProperty, 11.0);
        detail.SetValue(TextBlock.ForegroundProperty, FindResource("MutedText"));
        detail.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        panel.AppendChild(name);
        panel.AppendChild(detail);
        return new DataTemplate { VisualTree = panel };
    }

    private string SliceName(Guid id)
    {
        var slices = Config.Slices(screenId);
        var index = slices.ToList().FindIndex(s => s.Id == id);
        if (index < 0) return "";
        var named = slices[index].Name ?? Config.DeviceName(id);
        return named is null ? $"Output {index + 1}" : $"Output {index + 1} ({named})";
    }

    private string Summary(OutputMask mask) => string.Join(" · ", new[]
    {
        mask.Mode == OutputMaskMode.Out ? "Blacks out the inside" : "Shows only the inside",
        mask.AlwaysOn ? "always on" : "when a preset turns it on",
        mask.Feather > 0 ? $"feather {mask.Feather.ToString("0.00", CultureInfo.CurrentCulture)}" : null,
        Config.Slices(screenId).Count > 1 && mask.SliceIds is not null
            ? "on " + OutputMaskLibrary.CoversLabel(mask, id => SliceName(id) is { Length: > 0 } n ? n : null)
            : null,
    }.OfType<string>());

    private void RefreshList(Guid? select)
    {
        loading = true;
        var rows = Masks.Select(m => new MaskRow(m.Id, m.Name, Summary(m))).ToList();
        maskList.ItemsSource = rows;
        maskList.SelectedItem = rows.FirstOrDefault(r => r.Id == select);
        loading = false;
        if (select != selectedId || details.Children.Count == 0) Select((maskList.SelectedItem as MaskRow)?.Id);
        RebuildPreview();
    }

    /// <summary>Selects a mask (null for none), showing its settings.</summary>
    public void Select(Guid? id)
    {
        if (mode == PenMode.Editing && id != selectedId) SetMode(PenMode.None);
        selectedId = id is { } wanted && Masks.Any(m => m.Id == wanted) ? wanted : null;
        loading = true;
        maskList.SelectedItem = (maskList.ItemsSource as IEnumerable<MaskRow>)?.FirstOrDefault(r => r.Id == selectedId);
        loading = false;
        BuildDetails();
        surface.InvalidateVisual();
    }

    /// <summary>Replaces the screen's masks: the outputs redraw and the setup is saved.</summary>
    private void Save(IReadOnlyList<OutputMask> library, Guid? select)
    {
        try
        {
            Config.SetMasks(screenId, library);
        }
        catch (Exception ex)
        {
            Log.Error("Saving the output masks failed", ex);
            MessageBox.Show(this, $"Couldn't save the masks: {ex.Message}", "Masks", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        RefreshList(select);
    }

    private void Change(Guid id, Func<OutputMask, OutputMask> change) => Save(OutputMaskLibrary.Change(Masks, id, change), id);

    private void AddMask(string pathData)
    {
        var library = OutputMaskLibrary.Add(Masks, pathData);
        Save(library, library[^1].Id);
        // Straight into point editing, so the new shape can be adjusted.
        SetMode(PenMode.Editing);
    }

    private void DeleteSelected()
    {
        if (Selected is not { } mask) return;
        if (MessageBox.Show(this, $"Delete the mask \"{mask.Name}\"? The screen shows that area again straight away.", "Masks",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        SetMode(PenMode.None);
        var index = Masks.ToList().FindIndex(m => m.Id == mask.Id);
        var library = OutputMaskLibrary.Remove(Masks, mask.Id);
        Save(library, library.Count == 0 ? null : library[Math.Clamp(index, 0, library.Count - 1)].Id);
    }

    // MARK: The selected mask's settings

    private void BuildDetails()
    {
        details.Children.Clear();
        if (Selected is not { } mask)
        {
            details.Children.Add(Muted(Masks.Count == 0
                ? "No masks yet. Press “+ Draw a mask” and click around the area to cover, or add a rectangle."
                : "Pick a mask to change it."));
            return;
        }
        var id = mask.Id;
        details.Children.Add(Section("MASK"));

        var name = new TextBox { Text = mask.Name, Margin = new Thickness(0, 0, 0, 8) };
        void CommitName()
        {
            if (name.Text.Trim() is { Length: > 0 } text && text != Selected?.Name) Change(id, m => m with { Name = text });
        }
        name.LostFocus += (_, _) => CommitName();
        name.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            CommitName();
            e.Handled = true;
        };
        details.Children.Add(Labelled("Name", name));

        var modeCombo = new ComboBox
        {
            ItemsSource = new[]
            {
                new Choice<OutputMaskMode>(OutputMaskMode.Out, "Black out what's inside"),
                new Choice<OutputMaskMode>(OutputMaskMode.In, "Show only what's inside"),
            },
            ToolTip = "Black out darkens inside the shape; show only darkens everything else.",
        };
        modeCombo.SelectedIndex = mask.Mode == OutputMaskMode.Out ? 0 : 1;
        modeCombo.SelectionChanged += (_, _) =>
        {
            if (modeCombo.SelectedItem is Choice<OutputMaskMode> choice && choice.Value != Selected?.Mode) Change(id, m => m with { Mode = choice.Value });
        };
        details.Children.Add(Labelled("Does", modeCombo));

        var featherValue = new TextBlock { Width = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Style = (Style)FindResource("Muted") };
        var feather = new Slider
        {
            Minimum = 0, Maximum = OutputMaskLibrary.MaxFeather, SmallChange = 0.005, LargeChange = 0.025, Value = mask.Feather,
            VerticalAlignment = VerticalAlignment.Center, ToolTip = "Softens the edge, up to a quarter of the screen's height",
        };
        featherValue.Text = mask.Feather.ToString("0.00", CultureInfo.CurrentCulture);
        feather.ValueChanged += (_, e) =>
        {
            featherValue.Text = e.NewValue.ToString("0.00", CultureInfo.CurrentCulture);
            pendingFeather = (id, OutputMaskLibrary.ClampFeather(e.NewValue));
            featherTimer.Stop();
            featherTimer.Start();
        };
        var featherRow = new DockPanel();
        DockPanel.SetDock(featherValue, Dock.Right);
        featherRow.Children.Add(featherValue);
        featherRow.Children.Add(feather);
        details.Children.Add(Labelled("Feather", featherRow));

        var alwaysOn = new CheckBox
        {
            Content = "Always on", IsChecked = mask.AlwaysOn, Margin = new Thickness(0, 2, 0, 2),
            ToolTip = "On: part of the room, applied all the time. Off: only while an output preset switches it on.",
        };
        alwaysOn.Click += (_, _) =>
        {
            Change(id, m => m with { AlwaysOn = alwaysOn.IsChecked == true });
            BuildDetails();
        };
        details.Children.Add(alwaysOn);
        details.Children.Add(Muted(mask.AlwaysOn
            ? "Part of the room: it applies whatever else is going on."
            : "Off until an output preset switches it on (it shows dashed here)."));

        var slices = Config.Slices(screenId);
        if (slices.Count > 1)
        {
            var covers = new List<Choice<Guid?>> { new(null, "All outputs of this screen") };
            covers.AddRange(slices.Select(s => new Choice<Guid?>(s.Id, SliceName(s.Id))));
            if (mask.SliceIds is { Count: not 1 } several && several.Count > 0)
                covers.Add(new Choice<Guid?>(Guid.Empty, $"{several.Count} outputs (as imported)"));
            var coversCombo = new ComboBox { ItemsSource = covers, ToolTip = "Which of this screen's outputs the mask darkens" };
            coversCombo.SelectedItem = mask.SliceIds switch
            {
                null => covers[0],
                { Count: 1 } one => covers.FirstOrDefault(c => c.Value == one[0]) ?? covers[0],
                _ => covers[^1],
            };
            coversCombo.SelectionChanged += (_, _) =>
            {
                if (coversCombo.SelectedItem is not Choice<Guid?> choice || choice.Value == Guid.Empty) return;
                Change(id, m => OutputMaskLibrary.Covering(m, choice.Value));
            };
            details.Children.Add(Labelled("On", coversCombo));
        }

        var editPoints = new ToggleButton
        {
            Content = mode == PenMode.Editing ? "Done editing points" : "Edit points", IsChecked = mode == PenMode.Editing,
            Margin = new Thickness(0, 10, 6, 0), ToolTip = "Drag the mask's points and curve handles on the picture",
        };
        editPoints.Click += (_, _) => SetMode(editPoints.IsChecked == true ? PenMode.Editing : PenMode.None);
        var delete = new Button { Content = "Delete mask", Margin = new Thickness(0, 10, 0, 0) };
        delete.Click += (_, _) => DeleteSelected();
        details.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { editPoints, delete } });
    }

    private void ApplyFeather()
    {
        featherTimer.Stop();
        if (pendingFeather is not { } feather) return;
        pendingFeather = null;
        if (Masks.FirstOrDefault(m => m.Id == feather.Id) is { } mask && Math.Abs(mask.Feather - feather.Value) > 1e-9)
        {
            Config.SetMasks(screenId, OutputMaskLibrary.Change(Masks, feather.Id, m => m with { Feather = feather.Value }));
            // Keep the slider (and its focus) while it is dragged: refresh only the list text and the preview.
            loading = true;
            var rows = Masks.Select(m => new MaskRow(m.Id, m.Name, Summary(m))).ToList();
            maskList.ItemsSource = rows;
            maskList.SelectedItem = rows.FirstOrDefault(r => r.Id == selectedId);
            loading = false;
            RebuildPreview();
        }
    }

    private FrameworkElement Labelled(string label, FrameworkElement control)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var text = new TextBlock { Text = label, Width = 64, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(text, Dock.Left);
        row.Children.Add(text);
        row.Children.Add(control);
        return row;
    }

    private TextBlock Section(string text) => new() { Text = text, Style = (Style)FindResource("SectionLabel"), Margin = new Thickness(0, 10, 0, 6) };

    private TextBlock Muted(string text) => new()
    {
        Text = text, Style = (Style)FindResource("Muted"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6),
    };

    // MARK: Drawing and editing

    private void SetMode(PenMode next)
    {
        if (next == PenMode.Editing && (Selected is not { } mask || !pen.Edit(mask.PathData)))
        {
            if (Selected is not null)
                MessageBox.Show(this, "This mask's shape can't be edited point by point (it has more than one part). You can still rename, feather or delete it.",
                    "Masks", MessageBoxButton.OK, MessageBoxImage.Information);
            next = PenMode.None;
        }
        if (next == PenMode.Drawing) pen.Draw();
        mode = next;
        drawButton.IsChecked = mode == PenMode.Drawing;
        hint.Text = mode switch
        {
            PenMode.Drawing => "Click to place points, drag for curves. Click the first point, double-click or press Enter to close; Esc gives up.",
            PenMode.Editing => "Drag a square point to move it or a round handle to bend the curve. Right-click a point to remove it.",
            _ => Masks.Count == 0 ? "Press “+ Draw a mask” to start." : "Click a mask on the picture to pick it.",
        };
        surface.Cursor = mode == PenMode.Drawing ? Cursors.Cross : Cursors.Arrow;
        BuildDetails();
        surface.InvalidateVisual();
    }

    private void OnPenCompleted(IReadOnlyList<PathAnchor> anchors, bool closed)
    {
        if (OutputMaskLibrary.PathData(anchors, closed) is not { } path) return;
        if (mode == PenMode.Drawing)
        {
            SetMode(PenMode.None);
            AddMask(path);
        }
        else if (mode == PenMode.Editing && Selected is { } mask && mask.PathData != path)
        {
            Change(mask.Id, m => m with { PathData = path });
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        switch (e.Key)
        {
            case Key.Enter when mode == PenMode.Drawing:
                e.Handled = pen.PressReturn();
                break;
            case Key.Escape when mode == PenMode.Drawing:
                pen.PressEscape();
                e.Handled = true;
                break;
            case Key.Escape when mode == PenMode.Editing:
                SetMode(PenMode.None);
                e.Handled = true;
                break;
        }
        surface.InvalidateVisual();
    }

    // MARK: Driving it (the self-test uses these too)

    /// <summary>Starts drawing a new mask, as the "+ Draw a mask" button does.</summary>
    public void StartDrawing() => SetMode(PenMode.Drawing);

    /// <summary>A click on the picture at a point of the screen (0–1 across and down).</summary>
    public void ClickAt(double x, double y, int clickCount = 1)
    {
        surface.UpdateLayout();
        var view = surface.ViewPoint(new UnitPoint(x, y));
        surface.Press(view, clickCount);
        surface.Release();
    }

    /// <summary>Enter: closes the shape being drawn.</summary>
    public bool FinishDrawing() => mode == PenMode.Drawing && pen.PressReturn();

    // MARK: The darkened preview

    /// <summary>
    /// Shows the picture darkened where the mask raster will black it out, at up to 960
    /// pixels across: the same coverage the outputs multiply their picture by.
    /// </summary>
    private void RebuildPreview()
    {
        preview = null;
        if (Screen is not { } screen) return;
        try
        {
            if (Config.MaskCoverage(previewSliceId) is not { } coverage || coverage.Length != screen.Width * screen.Height) return;
            var step = Math.Max(1, (int)Math.Ceiling(screen.Width / 960.0));
            var w = Math.Max(1, screen.Width / step);
            var h = Math.Max(1, screen.Height / step);
            var pixels = new byte[w * h * 4];
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    pixels[(y * w + x) * 4 + 3] = (byte)((255 - coverage[y * step * screen.Width + x * step]) * 0.82);
            var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, w * 4);
            bitmap.Freeze();
            preview = bitmap;
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't draw the mask preview: {ex.Message}");
        }
        finally
        {
            surface.InvalidateVisual();
        }
    }

    // MARK: The drawing surface

    /// <summary>
    /// Sits over the picture: shows the darkened preview and every mask's outline, the
    /// pen's points and handles, and turns mouse gestures into pen actions.
    /// </summary>
    private sealed class MaskSurface : FrameworkElement
    {
        private static readonly Pen Under = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), 3));
        private static readonly Pen Line = Frozen(new Pen(Brushes.White, 1.2));
        private static readonly Pen Rubber = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)), 1));
        private static readonly Pen HandleLine = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF)), 0.8));
        private static readonly Pen AnchorEdge = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0xB3, 0, 0, 0)), 1));
        private static readonly Pen Outline = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)), 1));
        private static readonly Pen Dashed = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)), 1) { DashStyle = DashStyles.Dash });
        private static readonly Pen Chosen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)), 2));

        private readonly MaskEditorWindow editor;

        public MaskSurface(MaskEditorWindow editor)
        {
            this.editor = editor;
            Focusable = true;
            FocusVisualStyle = null;
        }

        private static T Frozen<T>(T f) where T : Freezable
        {
            f.Freeze();
            return f;
        }

        private SceneSize ScreenSize => editor.Screen?.Size ?? SceneSize.Hd;

        /// <summary>Where the screen's picture sits in the view (the picture letterboxes the same way).</summary>
        private Rect PictureRect()
        {
            var (scale, origin) = EditorGeometry.Letterbox(ScreenSize, new SceneSize(ActualWidth, ActualHeight));
            return new Rect(origin.X, origin.Y, ScreenSize.Width * scale, ScreenSize.Height * scale);
        }

        private SceneSize PictureSize => new(PictureRect().Width, PictureRect().Height);

        private UnitPoint UnitOf(Point view)
        {
            var rect = PictureRect();
            return OutputMaskPen.Unit(view.X - rect.X, view.Y - rect.Y, new SceneSize(rect.Width, rect.Height));
        }

        public Point ViewPoint(UnitPoint unit)
        {
            var rect = PictureRect();
            return new Point(rect.X + unit.X * rect.Width, rect.Y + unit.Y * rect.Height);
        }

        public void Press(Point view, int clickCount)
        {
            Focus();
            switch (editor.mode)
            {
                case PenMode.None:
                    editor.Select(MaskAt(view) ?? editor.selectedId);
                    break;
                default:
                    editor.pen.Press(UnitOf(view), PictureSize, clickCount);
                    break;
            }
            InvalidateVisual();
        }

        public void Release()
        {
            if (editor.mode != PenMode.None) editor.pen.Release();
            InvalidateVisual();
        }

        /// <summary>The topmost mask whose shape contains a view point.</summary>
        private Guid? MaskAt(Point view)
        {
            foreach (var mask in editor.Masks.Reverse())
                if (Geometry(mask.PathData) is { } shape && shape.FillContains(view)) return mask.Id;
            return null;
        }

        private Geometry? Geometry(string pathData)
        {
            try
            {
                var rect = PictureRect();
                var geometry = System.Windows.Media.Geometry.Parse("F1 " + pathData).Clone();
                geometry.Transform = new MatrixTransform(rect.Width, 0, 0, rect.Height, rect.X, rect.Y);
                return geometry;
            }
            catch (FormatException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            CaptureMouse();
            Press(e.GetPosition(this), e.ClickCount);
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (editor.mode == PenMode.None) return;
            var unit = UnitOf(e.GetPosition(this));
            if (editor.pen.DragInProgress) editor.pen.Drag(unit);
            else editor.pen.Move(unit);
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            ReleaseMouseCapture();
            Release();
        }

        protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonUp(e);
            if (editor.mode == PenMode.Editing && editor.pen.RemovePoint(UnitOf(e.GetPosition(this)), PictureSize))
                e.Handled = true;
            InvalidateVisual();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            editor.pen.Leave();
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            var rect = PictureRect();
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
            if (editor.preview is { } shade) dc.DrawImage(shade, rect);

            // Every mask's outline; the chosen one in blue, those waiting for a preset dashed.
            var editing = editor.mode == PenMode.Editing;
            foreach (var mask in editor.Masks)
            {
                if (editing && mask.Id == editor.selectedId) continue;
                if (Geometry(mask.PathData) is not { } shape) continue;
                dc.DrawGeometry(null, mask.Id == editor.selectedId ? Chosen : mask.AlwaysOn ? Outline : Dashed, shape);
            }
            if (editor.mode != PenMode.None) DrawPen(dc);
        }

        private void DrawPen(DrawingContext dc)
        {
            var pen = editor.pen;
            if (pen.Anchors.Count == 0) return;
            var figure = new StreamGeometry();
            using (var context = figure.Open())
            {
                context.BeginFigure(ViewPoint(pen.Anchors[0].Point), false, false);
                foreach (var segment in pen.Segments()) AddSegment(context, segment);
            }
            figure.Freeze();
            if (pen.RubberBand() is { } band)
            {
                var rubber = new StreamGeometry();
                using (var context = rubber.Open())
                {
                    context.BeginFigure(ViewPoint(band.From), false, false);
                    AddSegment(context, band);
                }
                dc.DrawGeometry(null, Rubber, rubber);
            }
            dc.DrawGeometry(null, Under, figure);
            dc.DrawGeometry(null, Line, figure);

            foreach (var anchor in pen.Anchors)
            {
                var at = ViewPoint(anchor.Point);
                foreach (var handle in new[] { anchor.HandleIn, anchor.HandleOut }.OfType<UnitPoint>())
                {
                    var h = ViewPoint(handle);
                    dc.DrawLine(HandleLine, at, h);
                    dc.DrawEllipse(Brushes.White, null, h, 3, 3);
                }
                dc.DrawRectangle(Brushes.White, AnchorEdge, new Rect(at.X - 3.5, at.Y - 3.5, 7, 7));
            }
        }

        private void AddSegment(StreamGeometryContext context, OutputMaskPen.Segment segment)
        {
            if (segment.IsLine) context.LineTo(ViewPoint(segment.To), true, false);
            else context.BezierTo(ViewPoint(segment.Control1!.Value), ViewPoint(segment.Control2!.Value), ViewPoint(segment.To), true, false);
        }
    }
}
