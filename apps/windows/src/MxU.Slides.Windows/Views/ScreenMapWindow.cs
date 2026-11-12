using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Outputs;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The Screen Map: the room as one picture, with each screen where it really sits (drag a
/// screen to move it; it snaps to 10 pixels). Below it, the output preset's layers: place one
/// on every screen or across all of them, and add the preset's own custom layers (a lower-third
/// band across the side screens, a logo on the LED wall). New LED Wall… adds a wall seeded 1:1
/// into its processor's frame. The selected screen's own picture shows with rulers, and Test
/// card puts the alignment grid on it. Port of the Mac's ScreenMapEditor.swift and the Screen
/// Map page of ScreenConfigurationView.swift (OutputPicture.swift's rulers, simplified).
/// </summary>
internal sealed class ScreenMapWindow : Window
{
    private static readonly Brush ScreenFill = Frozen(Color.FromArgb(0x55, 0x3B, 0x82, 0xF6));
    private static readonly Brush ScreenEdge = Frozen(Color.FromRgb(0x60, 0xA5, 0xFA));
    private static readonly Brush PlacedFill = Frozen(Color.FromArgb(0x40, 0xFA, 0xCC, 0x15));
    private static readonly Brush PlacedEdge = Frozen(Color.FromRgb(0xFA, 0xCC, 0x15));
    private static readonly Brush RulerInk = Frozen(Color.FromRgb(0x9C, 0xA3, 0xAF));

    private readonly OutputManager outputs;
    private readonly MainViewModel? model;
    private readonly Canvas map = new() { ClipToBounds = true, Background = Brushes.Transparent };
    private readonly Canvas picture = new() { ClipToBounds = true };
    private readonly StackPanel layers = new();
    private readonly ComboBox presets = new() { MinWidth = 220 };
    private readonly TextBlock status = new() { Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox testCard = new() { Content = "Test card on this screen", VerticalAlignment = VerticalAlignment.Center };
    private Guid? selected;
    private (Guid Id, Point Start, SceneRect From)? dragging;
    private string? highlightedLayer;

    private OutputConfiguration Config => outputs.Config;

    private ScreenMapWindow(Window? owner, OutputManager outputs, MainViewModel? model)
    {
        this.outputs = outputs;
        this.model = model;
        EditorKit.Dress(this, owner, "Screen Map", 980, 760);

        var top = new StackPanel { Margin = new Thickness(18, 14, 18, 6) };
        top.Children.Add(new TextBlock { Text = "Screen Map", FontSize = 16, FontWeight = FontWeights.SemiBold });
        top.Children.Add(EditorKit.Muted("Where each screen sits in the room. A preset can place a layer anywhere on the map, and each screen draws its own part of it.", 12, 8));
        var buttons = new WrapPanel();
        buttons.Children.Add(EditorKit.Button("Lay out side by side", () => SetMap(ScreenMapEditing.FirstMap(Config)), "Start a map with every audience screen in a row"));
        buttons.Children.Add(EditorKit.Button("New LED Wall…", NewLedWall, "A screen of the wall's own pixel size, 1:1 in its processor's frame"));
        buttons.Children.Add(EditorKit.Button("Remove the map", () => SetMap(null), "Screens show what the preset routes to them, as before"));
        top.Children.Add(buttons);

        map.Height = 260;
        map.SizeChanged += (_, _) => Draw();
        map.MouseLeftButtonDown += MapDown;
        map.MouseMove += MapMove;
        map.MouseLeftButtonUp += (_, _) => EndDrag();
        var mapCard = new Border { Child = map, BorderBrush = RulerInk, BorderThickness = new Thickness(1), Margin = new Thickness(18, 0, 18, 8), Background = Frozen(Color.FromRgb(0x11, 0x14, 0x1B)) };

        var presetRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        presetRow.Children.Add(new TextBlock { Text = "Preset", Width = 70, VerticalAlignment = VerticalAlignment.Center });
        presets.SelectionChanged += (_, _) => DrawLayers();
        presetRow.Children.Add(presets);
        // The list of layers takes whatever height is left, and scrolls: with a fixed height the
        // "+ Custom layer" button under it fell off the bottom of the window.
        var addLayer = EditorKit.Button("+ Custom layer", AddLayer, "A layer of the preset's own, above Slide; fire media onto it from a media item's Layer menu");
        addLayer.HorizontalAlignment = HorizontalAlignment.Left;
        addLayer.Margin = new Thickness(0, 6, 0, 0);
        var left = new DockPanel();
        DockPanel.SetDock(presetRow, Dock.Top);
        DockPanel.SetDock(status, Dock.Bottom);
        DockPanel.SetDock(addLayer, Dock.Bottom);
        left.Children.Add(presetRow);
        left.Children.Add(status);
        left.Children.Add(addLayer);
        left.Children.Add(new ScrollViewer { Content = layers, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var right = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };
        right.Children.Add(new TextBlock { Text = "The output picture", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        picture.Width = 320;
        picture.Height = 200;
        right.Children.Add(new Border { Child = picture, BorderBrush = RulerInk, BorderThickness = new Thickness(1), Background = Frozen(Color.FromRgb(0x0B, 0x0D, 0x12)) });
        testCard.Click += (_, _) =>
        {
            if (selected is { } id) Config.SetTestPattern(id, testCard.IsChecked == true);
        };
        right.Children.Add(testCard);

        var lower = new Grid { Margin = new Thickness(18, 0, 18, 0) };
        lower.ColumnDefinitions.Add(new ColumnDefinition());
        lower.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(right, 1);
        lower.Children.Add(left);
        lower.Children.Add(right);

        var done = EditorKit.Button("Done", Close, accent: true);
        done.IsDefault = done.IsCancel = true;
        var footer = new DockPanel { Margin = new Thickness(18, 10, 18, 14), LastChildFill = false };
        DockPanel.SetDock(done, Dock.Right);
        footer.Children.Add(done);

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(mapCard, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(mapCard);
        root.Children.Add(footer);
        root.Children.Add(lower);
        Content = root;

        LoadPresets();
        selected = Config.ScreenMap?.Screens.Keys.Select(SwiftUuid.Parse).OfType<Guid>().FirstOrDefault();
        Loaded += (_, _) => Draw();
    }

    public static void Show(Window? owner, OutputManager outputs)
    {
        var model = Application.Current?.MainWindow?.DataContext as MainViewModel;
        new ScreenMapWindow(owner, outputs, model).ShowDialog();
    }

    /// <summary>For the self-test: the window open, without waiting for it to close.</summary>
    internal static ScreenMapWindow Open(Window? owner, OutputManager outputs, MainViewModel model)
    {
        var window = new ScreenMapWindow(owner, outputs, model);
        window.Show();
        return window;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // MARK: The map

    private void SetMap(ScreenMap? next)
    {
        Config.SetScreenMap(next);
        Draw();
    }

    /// <summary>How map pixels fit the canvas on screen: one scale, with room for the rulers.</summary>
    private (double Scale, double Left, double Top) Fit()
    {
        var bounds = Config.ScreenMap?.Bounds ?? new SceneRect(0, 0, 1920, 1080);
        var placed = PresetPlacements().Select(p => p.Rect);
        foreach (var rect in placed) bounds = ScreenGeometry.Union(bounds, rect);
        const double ruler = 18;
        var width = Math.Max(map.ActualWidth - ruler - 8, 50);
        var height = Math.Max(map.ActualHeight - ruler - 8, 50);
        var scale = Math.Min(width / Math.Max(bounds.Width, 1), height / Math.Max(bounds.Height, 1));
        return (scale, ruler - bounds.X * scale, ruler - bounds.Y * scale);
    }

    private void Draw()
    {
        map.Children.Clear();
        var screens = Config.ScreenMap?.Screens ?? new Dictionary<string, SceneRect>();
        if (screens.Count == 0)
        {
            map.Children.Add(new TextBlock
            {
                Text = "No map yet. Lay out side by side to start one.", Foreground = RulerInk, Margin = new Thickness(24),
            });
            DrawPicture();
            DrawLayers();
            return;
        }
        var (scale, left, top) = Fit();
        DrawRulers(scale, left, top);
        foreach (var (key, rect) in screens)
        {
            var id = SwiftUuid.Parse(key);
            var name = id is { } g ? Config.Screen(g)?.Name ?? "Screen" : "Screen";
            var box = new Border
            {
                Width = rect.Width * scale, Height = rect.Height * scale,
                Background = ScreenFill, BorderBrush = ScreenEdge, BorderThickness = new Thickness(id == selected ? 3 : 1),
                Child = new TextBlock
                {
                    Text = $"{name}\n{rect.Width:0}×{rect.Height:0}", Foreground = Brushes.White, FontSize = 11,
                    Margin = new Thickness(4), TextWrapping = TextWrapping.Wrap,
                },
                Tag = id, Cursor = Cursors.SizeAll,
                ToolTip = $"{name}: at {rect.X:0}, {rect.Y:0}. Drag to move it.",
            };
            Canvas.SetLeft(box, left + rect.X * scale);
            Canvas.SetTop(box, top + rect.Y * scale);
            map.Children.Add(box);
        }
        foreach (var placement in PresetPlacements())
        {
            var shape = new Rectangle
            {
                Width = placement.Rect.Width * scale, Height = placement.Rect.Height * scale,
                Fill = placement.LayerId == highlightedLayer ? PlacedFill : Brushes.Transparent,
                Stroke = PlacedEdge, StrokeThickness = placement.LayerId == highlightedLayer ? 2 : 1,
                StrokeDashArray = [4, 3], IsHitTestVisible = false,
            };
            Canvas.SetLeft(shape, left + placement.Rect.X * scale);
            Canvas.SetTop(shape, top + placement.Rect.Y * scale);
            map.Children.Add(shape);
        }
        DrawPicture();
        DrawLayers();
    }

    /// <summary>Rulers along the top and left, every 960 map pixels (half an HD screen).</summary>
    private void DrawRulers(double scale, double left, double top)
    {
        var bounds = Config.ScreenMap!.Bounds;
        for (var x = Math.Floor(bounds.X / 960) * 960; x <= bounds.Right; x += 960)
        {
            map.Children.Add(Tick(left + x * scale, 0, 1, 14));
            map.Children.Add(Label(x.ToString("0", CultureInfo.InvariantCulture), left + x * scale + 2, 0));
        }
        for (var y = Math.Floor(bounds.Y / 540) * 540; y <= bounds.Bottom; y += 540)
            map.Children.Add(Tick(0, top + y * scale, 14, 1));
    }

    private static Rectangle Tick(double x, double y, double w, double h)
    {
        var tick = new Rectangle { Width = w, Height = h, Fill = RulerInk, IsHitTestVisible = false };
        Canvas.SetLeft(tick, x);
        Canvas.SetTop(tick, y);
        return tick;
    }

    private static TextBlock Label(string text, double x, double y)
    {
        var label = new TextBlock { Text = text, FontSize = 9, Foreground = RulerInk, IsHitTestVisible = false };
        Canvas.SetLeft(label, x);
        Canvas.SetTop(label, y);
        return label;
    }

    private void MapDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject hit) return;
        var box = FindBox(hit);
        if (box?.Tag is not Guid id || Config.ScreenMap?.RectFor(id) is not { } rect) return;
        selected = id;
        dragging = (id, e.GetPosition(map), rect);
        map.CaptureMouse();
        Draw();
    }

    private static Border? FindBox(DependencyObject hit)
    {
        for (var node = hit; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is Border { Tag: Guid } border) return border;
        return null;
    }

    private void MapMove(object sender, MouseEventArgs e)
    {
        if (dragging is not { } drag || Config.ScreenMap is not { } current) return;
        var (scale, _, _) = Fit();
        var delta = e.GetPosition(map) - drag.Start;
        // Whole pixels, snapped to 10, so screens line up edge to edge.
        var x = Math.Round((drag.From.X + delta.X / scale) / 10) * 10;
        var y = Math.Round((drag.From.Y + delta.Y / scale) / 10) * 10;
        Config.SetScreenMap(current.Placing(drag.Id, drag.From with { X = x, Y = y }));
        Draw();
    }

    private void EndDrag()
    {
        dragging = null;
        map.ReleaseMouseCapture();
    }

    // MARK: The selected screen's picture

    /// <summary>The screen's canvas with rulers, and the part of each placed layer it draws.</summary>
    private void DrawPicture()
    {
        picture.Children.Clear();
        testCard.IsEnabled = selected is not null;
        if (selected is not { } id || Config.Screen(id) is not { } screen || Config.ScreenMap?.RectFor(id) is not { } rect) return;
        testCard.IsChecked = Config.TestPatternScreens.Contains(id);
        var scale = Math.Min((picture.Width - 20) / screen.Width, (picture.Height - 20) / screen.Height);
        var frame = new Rectangle { Width = screen.Width * scale, Height = screen.Height * scale, Stroke = ScreenEdge, Fill = ScreenFill };
        Canvas.SetLeft(frame, 18);
        Canvas.SetTop(frame, 18);
        picture.Children.Add(frame);
        for (var x = 0; x <= screen.Width; x += Math.Max(screen.Width / 4, 1))
        {
            picture.Children.Add(Tick(18 + x * scale, 4, 1, 12));
            picture.Children.Add(Label(x.ToString(CultureInfo.InvariantCulture), 20 + x * scale, 2));
        }
        for (var y = 0; y <= screen.Height; y += Math.Max(screen.Height / 4, 1))
            picture.Children.Add(Tick(4, 18 + y * scale, 12, 1));
        foreach (var group in PresetPlacements().GroupBy(p => p.LayerId))
        {
            var hits = ScreenMapResolver.Resolve([.. group.Select(p => new MapPlacement(p.Rect))], rect) ?? [];
            foreach (var hit in hits)
            {
                var piece = ScreenGeometry.Intersection(hit.Frame, new SceneRect(0, 0, 1, 1));
                if (piece is not { } p) continue;
                var shape = new Rectangle
                {
                    Width = p.Width * screen.Width * scale, Height = p.Height * screen.Height * scale,
                    Fill = group.Key == highlightedLayer ? PlacedFill : Brushes.Transparent, Stroke = PlacedEdge, StrokeDashArray = [3, 2],
                    ToolTip = LayerName(group.Key),
                };
                Canvas.SetLeft(shape, 18 + p.X * screen.Width * scale);
                Canvas.SetTop(shape, 18 + p.Y * screen.Height * scale);
                picture.Children.Add(shape);
            }
        }
    }

    // MARK: The preset's layers

    private void LoadPresets()
    {
        var all = model?.OutputPresetChoices() ?? [];
        presets.ItemsSource = all;
        presets.DisplayMemberPath = "Name";
        presets.SelectedItem = all.FirstOrDefault(c => c.Id == model?.ActiveOutputPresetId) ?? all.FirstOrDefault();
    }

    private OutputPreset? Preset() =>
        presets.SelectedItem is OutputPresetMenu.Choice { Id: { } id } && model is not null ? model.Library.Get<OutputPreset>(id) : null;

    private IEnumerable<(string LayerId, SceneRect Rect)> PresetPlacements() =>
        (Preset()?.Placements ?? []).Select(p => (p.LayerId, new SceneRect(p.X, p.Y, p.Width, p.Height)));

    private string LayerName(string id) => ScreenMapEditing.Layers(Preset()).FirstOrDefault(l => l.Id == id).Name ?? id;

    private void DrawLayers()
    {
        layers.Children.Clear();
        var preset = Preset();
        if (preset is null)
        {
            layers.Children.Add(EditorKit.Muted("Make an output preset first (Screens › presets): placements belong to a preset, so each preset can lay the room out its own way.", 12));
            return;
        }
        var hasMap = Config.ScreenMap is { Screens.Count: > 0 };
        foreach (var (id, name) in ScreenMapEditing.Layers(preset))
        {
            var custom = preset.Layers?.Any(l => l.Id == id) == true;
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var title = new TextBlock { Text = custom ? $"{name} (custom)" : name, Width = 170, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(title);
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(actions, Dock.Right);
            actions.Children.Add(Small("Each screen", () => Place(id, ScreenMapEditing.Start.OnePerScreen), hasMap));
            actions.Children.Add(Small("Across", () => Place(id, ScreenMapEditing.Start.AcrossScreens), hasMap));
            // Fit shows the whole layer inside each frame; Fill covers the frame and crops the spill.
            var placed = preset.Placements?.Any(p => p.LayerId == id) == true;
            var fills = ScreenMapEditing.Fills(preset, id);
            var fit = Small(fills ? "Fill" : "Fit", () => Edit(p => ScreenMapEditing.SetFill(p, id, !fills)), placed);
            fit.ToolTip = fills
                ? "The layer covers each frame, cropped where it spills over. Click to fit it whole inside instead."
                : "The layer shows whole inside each frame. Click to make it cover the frame (cropped) instead.";
            fit.MinWidth = 44;
            actions.Children.Add(fit);
            actions.Children.Add(Small("Clear", () => Place(id, ScreenMapEditing.Start.NotPlaced), true));
            if (custom) actions.Children.Add(Small("Remove", () => Edit(p => ScreenMapEditing.RemoveCustomLayer(p, id)), true));
            row.Children.Add(actions);
            var summary = ScreenMapEditing.Summary(preset, id);
            var tip = placed ? "Shows wherever it's placed on the map, whatever the preset routes." : "Not placed on the map: it shows on the screens the preset routes it to.";
            row.Children.Add(new TextBlock { Text = summary, ToolTip = tip, Foreground = RulerInk, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            row.MouseEnter += (_, _) => { highlightedLayer = id; Redraw(); };
            layers.Children.Add(row);
        }
    }

    // Redraws the map and picture only (rebuilding the rows under the mouse would lose the hover).
    private void Redraw()
    {
        var keep = layers.Children.Cast<UIElement>().ToList();
        Draw();
        layers.Children.Clear();
        foreach (var row in keep) layers.Children.Add(row);
    }

    private static Button Small(string text, Action click, bool enabled)
    {
        var button = EditorKit.Button(text, click);
        button.Padding = new Thickness(8, 2, 8, 2);
        button.Margin = new Thickness(4, 0, 0, 0);
        button.IsEnabled = enabled;
        return button;
    }

    private void Place(string layerId, ScreenMapEditing.Start start)
    {
        if (Config.ScreenMap is not { } current && start != ScreenMapEditing.Start.NotPlaced) return;
        Edit(preset => ScreenMapEditing.Place(preset, layerId, start, Config.ScreenMap ?? new ScreenMap()));
        highlightedLayer = layerId;
    }

    private void AddLayer() => Edit(preset => ScreenMapEditing.AddCustomLayer(preset, ScreenMapEditing.NextName(preset), "slide"));

    /// <summary>Saves an edit to the preset (fields the Mac reads too) and re-applies it when it's the active one.</summary>
    private void Edit(Action<OutputPreset> change)
    {
        if (model is null || Preset() is not { } stored) return;
        var preset = MxuJson.Clone(stored);
        change(preset);
        model.Library.Save(preset);
        if (model.ActiveOutputPresetId == preset.Id) model.SwitchOutputPreset(preset.Id);
        status.Text = $"Saved to {preset.Name}.";
        Draw();
    }

    // MARK: New LED Wall…

    private void NewLedWall()
    {
        var name = new TextBox { Text = "LED Wall", Width = 160 };
        var width = new TextBox { Text = "1536", Width = 70 };
        var height = new TextBox { Text = "864", Width = 70 };
        var dialog = new Window
        {
            Title = "New LED Wall", SizeToContent = SizeToContent.WidthAndHeight, Owner = this, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(EditorKit.Muted("The wall's own size in pixels (its processor's input is HD or 4K). It is seated to the right on the map.", 12));
        panel.Children.Add(EditorKit.Row("Name", name, 80));
        panel.Children.Add(EditorKit.Row("Width", width, 80));
        panel.Children.Add(EditorKit.Row("Height", height, 80));
        var ok = EditorKit.Button("Add", () => dialog.DialogResult = true, accent: true);
        ok.IsDefault = true;
        panel.Children.Add(ok);
        dialog.Content = panel;
        if (dialog.ShowDialog() != true) return;
        if (!int.TryParse(width.Text, out var w) || !int.TryParse(height.Text, out var h) || w < 16 || h < 16 || w > 8192 || h > 8192)
        {
            status.Text = "A wall is 16 to 8192 pixels each way.";
            return;
        }
        selected = ScreenMapEditing.NewLedWall(Config, name.Text.Trim().Length == 0 ? "LED Wall" : name.Text.Trim(), w, h);
        Draw();
    }
}
