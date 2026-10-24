using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MxU.Slides.Core.Outputs;
using MxU.Slides.VideoIO.DeckLink;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.Outputs;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The Screens window: add screens, choose what each shows (audience or stage) and what
/// carries it (a display, NDI, a DeckLink card), split it across projectors (mirror,
/// grouped, edge blend, LED wall), and correct each output (keystone, colour, blend
/// ramps). Every change applies live and is saved. Port of the Mac's
/// ScreenConfigurationView and OutputsSection, using Core's ScreenEditing rules.
/// </summary>
public partial class ScreensWindow : Window
{
    private sealed record ScreenRow(Guid Id, string Title, string Detail);

    private sealed record Choice<T>(T Value, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly OutputManager outputs;
    private OutputConfiguration Config => outputs.Config;
    private Guid? screenId;
    private Guid? sliceId;
    private bool loading;
    private IReadOnlyList<DeckLinkDevice> deckLinks = [];

    public ScreensWindow(OutputManager outputs)
    {
        this.outputs = outputs;
        InitializeComponent();
        RoleCombo.ItemsSource = new[]
        {
            new Choice<ScreenRole>(ScreenRole.Audience, "The audience program (slides, media, overlays)"),
            new Choice<ScreenRole>(ScreenRole.Confidence, "The stage display (confidence monitor)"),
        };
        ScaleCombo.ItemsSource = new[]
        {
            new Choice<OutputScaleMode>(OutputScaleMode.Fit, "Show everything (bars where the shapes differ)"),
            new Choice<OutputScaleMode>(OutputScaleMode.Fill, "Fill the screen (trim the edges)"),
        };
        SizePresets.ItemsSource = ScreenEditing.AddScreenSizes.Select(s => new Choice<ScreenSizeChoice>(s, s.Label)).ToList();
        FpsCombo.ItemsSource = ScreenEditing.FrameRates.Select(r => new Choice<int>(r, $"{r} fps")).ToList();
        CountCombo.ItemsSource = Enumerable.Range(ScreenEditing.MinOutputCount, ScreenEditing.MaxOutputCount - ScreenEditing.MinOutputCount + 1).ToList();
        DelayCombo.ItemsSource = Enumerable.Range(0, OutputConfiguration.MaxVideoDelayFrames + 1)
            .Select(f => new Choice<int>(f, f == 0 ? "None" : $"{f} frame{(f == 1 ? "" : "s")}")).ToList();
        Config.ConfigurationChanged += OnConfigurationChanged;
        Closed += (_, _) => Config.ConfigurationChanged -= OnConfigurationChanged;
        Loaded += async (_, _) =>
        {
            // DeckLink discovery talks to the driver; keep it off the UI thread.
            deckLinks = await Task.Run(() =>
            {
                try { return DeckLinkRuntime.Devices().Where(d => d.SupportsPlayback).ToList(); }
                catch (Exception ex)
                {
                    Log.Warn($"DeckLink discovery failed: {ex.Message}");
                    return new List<DeckLinkDevice>();
                }
            });
            if (screenId is not null) ShowSlices();
            Footer.Text = deckLinks.Count > 0 ? $"{deckLinks.Count} DeckLink output{(deckLinks.Count == 1 ? "" : "s")} found." : "";
        };
        RefreshList(Config.Screens.FirstOrDefault()?.Id);
    }

    private void OnConfigurationChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (!loading) RefreshList(screenId);
    });

    // MARK: Screen list

    private void RefreshList(Guid? select)
    {
        loading = true;
        var rows = Config.Screens.Select(s => new ScreenRow(s.Id, s.Name, Summary(s))).ToList();
        ScreenList.ItemsSource = rows;
        ScreenList.SelectedItem = rows.FirstOrDefault(r => r.Id == select) ?? rows.FirstOrDefault();
        loading = false;
        ShowScreen((ScreenList.SelectedItem as ScreenRow)?.Id);
    }

    /// <summary>"Audience program · 1920×1080 · On EPSON PJ, NDI" — what it shows, its size, where it goes.</summary>
    private string Summary(PlaceholderScreen screen)
    {
        var shows = Config.Role(screen.Id) == ScreenRole.Confidence ? "Stage display" : "Audience program";
        var carriers = Config.Slices(screen.Id).Select(slice => Config.Device(slice.Id) is { } device
                ? OutputDevice.Kind(device) switch
                {
                    OutputDeviceKind.Ndi => $"NDI \"{OutputDevice.Value(device)}\"",
                    OutputDeviceKind.DeckLink => Config.DeviceName(slice.Id) ?? "DeckLink",
                    _ => Config.Displays.Any(d => d.Id == device)
                        ? Config.Displays.First(d => d.Id == device).Name
                        : $"{Config.DeviceName(slice.Id) ?? "a display"} (not connected)",
                }
                : null)
            .OfType<string>().Distinct().ToList();
        var goes = carriers.Count == 0 ? "not on any output yet: use its Preview window" : $"on {string.Join(", ", carriers)}";
        return $"{shows} · {screen.Width}×{screen.Height} · {goes}";
    }

    private void ScreenList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!loading) ShowScreen((ScreenList.SelectedItem as ScreenRow)?.Id);
    }

    private void AddScreen_Click(object sender, RoutedEventArgs e)
    {
        if (ScreenEditing.AddScreen(Config) is { } screen) RefreshList(screen.Id);
    }

    private void RemoveScreen_Click(object sender, RoutedEventArgs e)
    {
        if (screenId is not { } id || Config.Screen(id) is not { } screen) return;
        if (MessageBox.Show(this, $"Remove the screen \"{screen.Name}\"? Its outputs stop straight away.", "MxU Slides",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Config.RemoveScreen(id);
        outputs.Apply();
        RefreshList(Config.Screens.FirstOrDefault()?.Id);
    }

    private void Identify_Click(object sender, RoutedEventArgs e) => OutputManager.Identify();

    private void Done_Click(object sender, RoutedEventArgs e) => Close();

    // MARK: Screen details

    private void ShowScreen(Guid? id)
    {
        screenId = id;
        Details.IsEnabled = id is not null;
        RemoveButton.IsEnabled = id is not null;
        if (id is not { } sid || Config.Screen(sid) is not { } screen) return;
        loading = true;
        ScreenName.Text = screen.Name;
        ScreenWidth.Text = screen.Width.ToString(CultureInfo.InvariantCulture);
        ScreenHeight.Text = screen.Height.ToString(CultureInfo.InvariantCulture);
        SizePresets.SelectedItem = null;
        FpsCombo.SelectedItem = FpsCombo.Items.Cast<Choice<int>>().FirstOrDefault(c => c.Value == screen.FramesPerSecond);
        RoleCombo.SelectedItem = RoleCombo.Items.Cast<Choice<ScreenRole>>().First(c => c.Value == Config.Role(sid));
        ScaleCombo.SelectedItem = ScaleCombo.Items.Cast<Choice<OutputScaleMode>>().First(c => c.Value == Config.ScaleMode(sid));
        ScreenStatus.Text = Summary(screen);
        var layout = Config.Layout(sid);
        LayoutCombo.ItemsSource = ScreenEditing.LayoutChoices(layout).Select(k => new Choice<OutputLayoutKind>(k, k.DisplayName())).ToList();
        LayoutCombo.SelectedItem = LayoutCombo.Items.Cast<Choice<OutputLayoutKind>>().FirstOrDefault(c => c.Value == layout);
        var multi = layout is OutputLayoutKind.Mirror or OutputLayoutKind.Grouped or OutputLayoutKind.EdgeBlend;
        CountCombo.IsEnabled = multi;
        CountCombo.SelectedItem = multi ? ScreenEditing.OutputCount(Config, sid) : null;
        var blend = layout == OutputLayoutKind.EdgeBlend;
        OverlapSlider.Visibility = OverlapLabel.Visibility = OverlapValue.Visibility = blend ? Visibility.Visible : Visibility.Collapsed;
        if (blend && ScreenEditing.Seams(Config, sid).FirstOrDefault() is { } seam)
        {
            OverlapSlider.Value = seam.Overlap;
            OverlapValue.Text = $"{ScreenEditing.OverlapPixels(screen, seam)} px";
        }
        LayoutHelp.Text = ScreenEditing.LayoutHelp(layout);
        TestPattern.IsChecked = Config.TestPatternScreens.Contains(sid);
        loading = false;
        if (sliceId is null || Config.SliceInfo(sliceId.Value)?.ScreenId != sid) sliceId = sid;
        ShowSlices();
    }

    private void Identity_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Identity_Changed(sender, e);
    }

    private void Identity_Changed(object sender, RoutedEventArgs e)
    {
        if (loading || screenId is not { } id) return;
        if (!ScreenEditing.CommitIdentity(Config, id, ScreenName.Text, ScreenWidth.Text, ScreenHeight.Text))
        {
            ShowScreen(id);
            return;
        }
        outputs.Apply();
    }

    private void SizePreset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading || screenId is not { } id || SizePresets.SelectedItem is not Choice<ScreenSizeChoice> size) return;
        ScreenWidth.Text = size.Value.Width.ToString(CultureInfo.InvariantCulture);
        ScreenHeight.Text = size.Value.Height.ToString(CultureInfo.InvariantCulture);
        if (ScreenEditing.CommitIdentity(Config, id, ScreenName.Text, ScreenWidth.Text, ScreenHeight.Text)) outputs.Apply();
    }

    private void Fps_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading || screenId is not { } id || FpsCombo.SelectedItem is not Choice<int> rate) return;
        ScreenEditing.SetFrameRate(Config, id, rate.Value);
        outputs.Apply();
    }

    private void Role_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading || screenId is not { } id || RoleCombo.SelectedItem is not Choice<ScreenRole> role) return;
        Config.SetRole(id, role.Value);
        outputs.Apply();
    }

    private void Scale_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading || screenId is not { } id || ScaleCombo.SelectedItem is not Choice<OutputScaleMode> mode) return;
        Config.SetScaleMode(id, mode.Value);
    }

    // MARK: Layout

    private void Layout_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading || screenId is not { } id || LayoutCombo.SelectedItem is not Choice<OutputLayoutKind> kind) return;
        ScreenEditing.ChooseLayout(Config, id, kind.Value);
        outputs.Apply();
        sliceId = id;
        ShowScreen(id);
    }

    private void Count_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading || screenId is not { } id || CountCombo.SelectedItem is not int count) return;
        ScreenEditing.SetOutputCount(Config, id, count);
        outputs.Apply();
        ShowScreen(id);
    }

    private void Overlap_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (loading || screenId is not { } id) return;
        ScreenEditing.SetOverlap(Config, id, e.NewValue);
        if (Config.Screen(id) is { } screen && ScreenEditing.Seams(Config, id).FirstOrDefault() is { } seam)
            OverlapValue.Text = $"{ScreenEditing.OverlapPixels(screen, seam)} px";
        outputs.Apply();
    }

    // MARK: Outputs (slices) and what carries them

    private IReadOnlyList<Choice<string?>> DeviceChoices(Guid screen, Guid slice)
    {
        var name = Config.Screen(screen)?.Name ?? "MxU Slides";
        var sliceName = Config.SliceInfo(slice)?.Name;
        var choices = new List<Choice<string?>> { new(null, "Nothing (use Preview window)") };
        choices.AddRange(Displays.All().Select(d => new Choice<string?>(OutputDevice.Display(d.DeviceName), $"Display: {d.Label}")));
        var ndiName = sliceName is null ? $"MxU Slides {name}" : $"MxU Slides {name} {sliceName}";
        choices.Add(new Choice<string?>(OutputDevice.Ndi(ndiName), $"NDI source \"{ndiName}\""));
        choices.AddRange(deckLinks.Select(d => new Choice<string?>(OutputDevice.DeckLink(d.PersistentId.ToString(CultureInfo.InvariantCulture)), $"DeckLink: {d.Name}")));
        // Keep a saved device visible even when it isn't here right now (e.g. a projector unplugged).
        if (Config.Device(slice) is { } current && choices.All(c => c.Value != current))
            choices.Add(new Choice<string?>(current, $"{Config.DeviceName(slice) ?? OutputDevice.Value(current)} (not connected)"));
        return choices;
    }

    private void ShowSlices()
    {
        SliceRows.Children.Clear();
        if (screenId is not { } sid) return;
        var layout = Config.Layout(sid);
        var slices = layout is OutputLayoutKind.Grouped or OutputLayoutKind.EdgeBlend ? ScreenEditing.TilesByPosition(Config, sid) : Config.Slices(sid);
        foreach (var slice in slices)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var selected = slice.Id == sliceId;
            var title = new TextBlock
            {
                Text = ScreenEditing.SliceTitle(sid, slice, layout),
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
            };
            var devices = new ComboBox { ItemsSource = DeviceChoices(sid, slice.Id), Margin = new Thickness(0, 0, 8, 0) };
            devices.SelectedItem = devices.Items.Cast<Choice<string?>>().FirstOrDefault(c => c.Value == Config.Device(slice.Id)) ?? devices.Items[0];
            var id = slice.Id;
            devices.SelectionChanged += (_, _) =>
            {
                if (devices.SelectedItem is not Choice<string?> choice) return;
                Config.AssignDevice(id, choice.Value, choice.Value is null ? null : choice.Label);
                outputs.Apply();
                ScreenStatus.Text = Config.Screen(sid) is { } s ? Summary(s) : "";
            };
            var correct = new Button { Content = selected ? "Correcting" : "Correct", Padding = new Thickness(10, 2, 10, 2) };
            if (selected) correct.Style = (Style)FindResource("AccentButton");
            correct.Click += (_, _) =>
            {
                sliceId = id;
                ShowSlices();
            };
            Grid.SetColumn(devices, 1);
            Grid.SetColumn(correct, 2);
            row.Children.Add(title);
            row.Children.Add(devices);
            row.Children.Add(correct);
            SliceRows.Children.Add(row);
        }
        ShowCorrections();
    }

    // MARK: Corrections (for the selected output)

    private void ShowCorrections()
    {
        CorrectionRows.Children.Clear();
        if (sliceId is not { } id || screenId is not { } sid) return;
        var slice = Config.Slices(sid).FirstOrDefault(s => s.Id == id);
        CorrectionsTitle.Text = slice is null ? "CORRECTIONS" : $"CORRECTIONS — {ScreenEditing.SliceTitle(sid, slice, Config.Layout(sid)).ToUpperInvariant()}";
        CorrectionsHelp.Text = "Fix the picture for this output's projector or TV: keystone by pulling corners in, colour to match neighbouring "
            + "projectors, and edge blends where projectors overlap. Changes show straight away.";
        DelayCombo.SelectedItem = DelayCombo.Items.Cast<Choice<int>>().FirstOrDefault(c => c.Value == Config.VideoDelay(id));

        var a = Config.Adjustments(id);
        Section("Colour");
        Slider("Brightness", -1, 1, a.Brightness ?? 0, v => Set(x => x with { Brightness = v }));
        Slider("Contrast", -1, 1, a.Contrast ?? 0, v => Set(x => x with { Contrast = v }));
        Slider("Gamma", -0.9, 2, a.Gamma ?? 0, v => Set(x => x with { Gamma = v }));
        Slider("Black level", -0.5, 0.5, a.BlackLevel ?? 0, v => Set(x => x with { BlackLevel = v }));
        Slider("Red", -1, 1, a.RedLevel ?? 0, v => Set(x => x with { RedLevel = v }));
        Slider("Green", -1, 1, a.GreenLevel ?? 0, v => Set(x => x with { GreenLevel = v }));
        Slider("Blue", -1, 1, a.BlueLevel ?? 0, v => Set(x => x with { BlueLevel = v }));

        Section("Shape");
        Slider("Rotate", -180, 180, a.RotationDegrees ?? 0, v => Set(x => x with { RotationDegrees = v }), "0°");
        var pixels = Config.SliceInfo(id) is { } info ? (info.Width, info.Height) : (1920, 1080);
        Corner("Top-left corner", a.TopLeft, pixels, p => Set(x => x with { TopLeft = p }));
        Corner("Top-right corner", a.TopRight, pixels, p => Set(x => x with { TopRight = p }));
        Corner("Bottom-left corner", a.BottomLeft, pixels, p => Set(x => x with { BottomLeft = p }));
        Corner("Bottom-right corner", a.BottomRight, pixels, p => Set(x => x with { BottomRight = p }));

        Section("Edge blend (fraction of the output)");
        Slider("Left", 0, 0.5, a.BlendLeft?.Width ?? 0, v => Set(x => x with { BlendLeft = Blend(x.BlendLeft, v) }), "0.00");
        Slider("Right", 0, 0.5, a.BlendRight?.Width ?? 0, v => Set(x => x with { BlendRight = Blend(x.BlendRight, v) }), "0.00");
        Slider("Top", 0, 0.5, a.BlendTop?.Width ?? 0, v => Set(x => x with { BlendTop = Blend(x.BlendTop, v) }), "0.00");
        Slider("Bottom", 0, 0.5, a.BlendBottom?.Width ?? 0, v => Set(x => x with { BlendBottom = Blend(x.BlendBottom, v) }), "0.00");
    }

    private static OutputEdgeBlend? Blend(OutputEdgeBlend? current, double width) =>
        width <= 0.0005 ? null : (current ?? new OutputEdgeBlend(width)) with { Width = width };

    private void Set(Func<OutputAdjustments, OutputAdjustments> change)
    {
        if (sliceId is not { } id) return;
        Config.SetAdjustments(id, change(Config.Adjustments(id)));
    }

    private void Section(string title) => CorrectionRows.Children.Add(new TextBlock
    {
        Text = title,
        Style = (Style)FindResource("Muted"),
        Margin = new Thickness(0, 8, 0, 2),
        FontWeight = FontWeights.SemiBold,
    });

    private void Slider(string label, double min, double max, double value, Action<double> changed, string format = "+0.00;-0.00;0")
    {
        var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
        var name = new TextBlock { Text = label, Width = 150, VerticalAlignment = VerticalAlignment.Center };
        var readout = new TextBlock { Width = 60, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, Style = (Style)FindResource("Muted") };
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, VerticalAlignment = VerticalAlignment.Center };
        readout.Text = value.ToString(format, CultureInfo.CurrentCulture);
        slider.ValueChanged += (_, e) =>
        {
            readout.Text = e.NewValue.ToString(format, CultureInfo.CurrentCulture);
            changed(e.NewValue);
        };
        slider.MouseDoubleClick += (_, _) => slider.Value = min <= 0 && max >= 0 ? 0 : min;
        slider.ToolTip = "Double-click to reset";
        DockPanel.SetDock(name, Dock.Left);
        DockPanel.SetDock(readout, Dock.Right);
        row.Children.Add(name);
        row.Children.Add(readout);
        row.Children.Add(slider);
        CorrectionRows.Children.Add(row);
    }

    /// <summary>A corner's keystone offset in output pixels: how far it is pulled across and down.</summary>
    private void Corner(string label, OutputPoint? current, (int Width, int Height) size, Action<OutputPoint?> changed)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
        row.Children.Add(new TextBlock { Text = label, Width = 150, VerticalAlignment = VerticalAlignment.Center });
        var x = new TextBox { Width = 60, Text = (current?.X ?? 0).ToString("0", CultureInfo.CurrentCulture) };
        var y = new TextBox { Width = 60, Text = (current?.Y ?? 0).ToString("0", CultureInfo.CurrentCulture), Margin = new Thickness(6, 0, 0, 0) };
        void Commit()
        {
            if (!double.TryParse(x.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var dx)) dx = 0;
            if (!double.TryParse(y.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var dy)) dy = 0;
            dx = Math.Clamp(dx, -size.Width, size.Width);
            dy = Math.Clamp(dy, -size.Height, size.Height);
            changed(dx == 0 && dy == 0 ? null : new OutputPoint(dx, dy));
        }
        foreach (var box in new[] { x, y })
        {
            box.LostFocus += (_, _) => Commit();
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) Commit();
            };
        }
        row.Children.Add(new TextBlock { Text = "→", Style = (Style)FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        row.Children.Add(x);
        row.Children.Add(new TextBlock { Text = " ↓", Style = (Style)FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
        row.Children.Add(y);
        row.Children.Add(new TextBlock { Text = "px", Style = (Style)FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
        CorrectionRows.Children.Add(row);
    }

    private void ResetCorrections_Click(object sender, RoutedEventArgs e)
    {
        if (sliceId is not { } id) return;
        Config.SetAdjustments(id, new OutputAdjustments());
        ShowCorrections();
    }

    private void TestPattern_Click(object sender, RoutedEventArgs e)
    {
        if (screenId is { } id) Config.SetTestPattern(id, TestPattern.IsChecked == true);
    }

    private void Delay_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading || sliceId is not { } id || DelayCombo.SelectedItem is not Choice<int> frames) return;
        Config.SetVideoDelay(id, frames.Value);
    }
}
