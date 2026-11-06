using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.MotionTemplates;
using MxU.Slides.MotionTemplates.AfterEffects;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// Motion templates › After Effects…: render a composition with After Effects and put the video in
/// the Media bin, optionally changing its text and colours first. Pick an .aep; <b>Read
/// compositions</b> has After Effects open a copy and list them, with their text layers and
/// colours; fill in the ones to change; <b>Render</b> runs aerender on a changed copy, with
/// progress and Cancel. The user's .aep is never changed (MotionTemplates/AfterEffects).
/// <para>
/// When After Effects isn't on this PC (or is somewhere unusual), it says so and offers to find
/// aerender.exe by hand. Beside it is the guide to the Lottie route, which plays live with
/// transparency; a rendered video can't.
/// </para>
/// </summary>
internal sealed class AfterEffectsWindow : Window
{
    private readonly MainViewModel model;
    private readonly TextBlock installLine = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock projectLine = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ComboBox compositions = new() { IsEditable = true, MinWidth = 260, ToolTip = "The composition to render (type its name if After Effects can't list them)" };
    private readonly StackPanel fields = new();
    private readonly TextBox nameBox = new();
    private readonly ToggleButton asBackground = new() { Content = "Looping background", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0) };
    private readonly ToggleButton asForeground = new() { Content = "Video that plays once", Padding = new Thickness(10, 4, 10, 4) };
    private readonly TextBox outputModule = new() { ToolTip = "After Effects' name for the output module template. H.264 needs After Effects 2023 or later; older ones can use Lossless (the app can transcode it)." };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressBar progress = new() { Height = 6, Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
    private readonly Button read;
    private readonly Button render;
    private readonly Button cancel;
    private AfterEffectsInstall? install;
    private string? project;
    private AeProject? listing;
    private CancellationTokenSource? reading;
    private MotionTemplateJob? job;
    private readonly Dictionary<AeText, string> words = [];
    private readonly Dictionary<AeColour, string> colours = [];

    private AfterEffectsWindow(Window? owner, MainViewModel model)
    {
        this.model = model;
        EditorKit.Dress(this, owner, "Render with After Effects", 1040, 760);
        read = EditorKit.Button("Read compositions", () => _ = ReadAsync(), "After Effects opens a copy of the project and lists its compositions, text layers and colours (it may take a minute to start)");
        render = EditorKit.Button("Render", Render, "aerender renders the composition from a copy (with your changes) and the video goes in the Media bin", accent: true);
        cancel = EditorKit.Button("Cancel", () =>
        {
            reading?.Cancel();
            job?.Cancel();
        }, "Stops After Effects; nothing is added");
        cancel.Visibility = Visibility.Collapsed;
        outputModule.Text = model.Settings.AfterEffectsOutputModule ?? Aerender.H264;
        asBackground.IsChecked = true;
        asBackground.Click += (_, _) => (asBackground.IsChecked, asForeground.IsChecked) = (true, false);
        asForeground.Click += (_, _) => (asBackground.IsChecked, asForeground.IsChecked) = (false, true);
        compositions.SelectionChanged += (_, _) => ShowFields();
        compositions.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => RefreshButtons()));
        Content = Layout();
        Closed += (_, _) => reading?.Cancel();
        install = model.AfterEffects;
        ShowInstall();
        if (model.MotionTemplateJob is { } running) _ = Follow(running);
    }

    public static AfterEffectsWindow Open(Window? owner, MainViewModel model)
    {
        var window = new AfterEffectsWindow(owner, model);
        window.Show();
        return window;
    }

    // For the self-test.
    public AfterEffectsInstall? Install => install;

    public AeProject? Listing => listing;

    public string Status => status.Text;

    public string InstallText => installLine.Text;

    private UIElement Layout()
    {
        var left = new StackPanel();
        left.Children.Add(EditorKit.Section("AFTER EFFECTS", top: 0));
        left.Children.Add(installLine);
        var locate = EditorKit.Button("Locate aerender.exe…", Locate, "If After Effects is installed somewhere unusual, find its Support Files\\aerender.exe");
        locate.HorizontalAlignment = HorizontalAlignment.Left;
        locate.Margin = new Thickness(0, 6, 0, 0);
        left.Children.Add(locate);

        left.Children.Add(EditorKit.Section("PROJECT"));
        var choose = EditorKit.Button("Choose .aep…", ChooseProject, "The After Effects project to render from. It's copied; your file is never changed.");
        var projectRow = new DockPanel();
        DockPanel.SetDock(choose, Dock.Left);
        projectRow.Children.Add(choose);
        projectRow.Children.Add(projectLine);
        projectLine.VerticalAlignment = VerticalAlignment.Center;
        left.Children.Add(projectRow);
        var readRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        readRow.Children.Add(read);
        left.Children.Add(readRow);
        left.Children.Add(EditorKit.Row("Composition", compositions, 100));

        left.Children.Add(EditorKit.Section("CHANGE BEFORE RENDERING"));
        left.Children.Add(fields);

        left.Children.Add(EditorKit.Section("INTO THE MEDIA BIN"));
        left.Children.Add(EditorKit.Row("Name", nameBox, 100));
        var useRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(100, 0, 0, 6) };
        useRow.Children.Add(asBackground);
        useRow.Children.Add(asForeground);
        left.Children.Add(useRow);
        left.Children.Add(EditorKit.Row("Output module", outputModule, 100));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        actions.Children.Add(render);
        actions.Children.Add(cancel);
        left.Children.Add(actions);
        left.Children.Add(status);
        left.Children.Add(progress);
        status.Foreground = EditorKit.Res<Brush>("Accent");

        var guide = new StackPanel();
        guide.Children.Add(EditorKit.Section("THE LOTTIE ROUTE: LIVE, WITH TRANSPARENCY", top: 0));
        guide.Children.Add(LottieGuideWindow.Steps());
        var guideBox = new Border
        {
            Child = new ScrollViewer { Content = guide, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 6, 0) },
            Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)), CornerRadius = new CornerRadius(6), Padding = new Thickness(14, 12, 8, 12),
        };

        var grid = new Grid { Margin = new Thickness(16, 14, 16, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(380) });
        grid.Children.Add(new ScrollViewer { Content = left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 6, 0) });
        Grid.SetColumn(guideBox, 2);
        grid.Children.Add(guideBox);

        var done = EditorKit.Button("Done", Close);
        done.IsCancel = true;
        done.MinWidth = 90;
        done.HorizontalAlignment = HorizontalAlignment.Right;
        done.Margin = new Thickness(0, 10, 16, 14);
        var root = new DockPanel();
        DockPanel.SetDock(done, Dock.Bottom);
        root.Children.Add(done);
        root.Children.Add(grid);
        return root;
    }

    private void ShowInstall()
    {
        installLine.Text = install is null
            ? "After Effects isn't installed on this PC (no Adobe After Effects folder with aerender.exe under Program Files). You can still import Lottie files exported on another PC: see the guide."
            : $"{install.Title}, in {install.Folder}." + (install.AfterFx is null ? $" {ExtendScriptsHelp.NoAfterFx}." : "");
        installLine.Foreground = EditorKit.Res<Brush>(install is null ? "MutedText" : "Text");
        RefreshButtons();
    }

    private void RefreshButtons()
    {
        var busy = job is not null || reading is not null;
        read.IsEnabled = install?.AfterFx is not null && project is not null && !busy;
        render.IsEnabled = install is not null && project is not null && Composition.Length > 0 && !busy;
        cancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private string Composition => (compositions.SelectedItem as AeComposition)?.Name ?? compositions.Text.Trim();

    private void Locate()
    {
        var dialog = new OpenFileDialog { Title = "Find aerender.exe (in After Effects' Support Files folder)", Filter = "aerender|aerender.exe|Programs|*.exe" };
        if (dialog.ShowDialog(this) != true) return;
        if (model.ChooseAfterEffects(dialog.FileName) is { } chosen) install = chosen;
        else status.Text = "That isn't aerender.exe.";
        ShowInstall();
    }

    private void ChooseProject()
    {
        var dialog = new OpenFileDialog { Title = "Choose an After Effects project", Filter = "After Effects projects|*.aep|All files|*.*" };
        if (dialog.ShowDialog(this) == true) UseProject(dialog.FileName);
    }

    /// <summary>Uses <paramref name="path"/> as the project (the self-test calls this directly).</summary>
    public void UseProject(string path)
    {
        project = path;
        projectLine.Text = " " + Path.GetFileName(path);
        projectLine.ToolTip = path;
        listing = null;
        compositions.ItemsSource = null;
        compositions.Text = "";
        fields.Children.Clear();
        if (nameBox.Text.Length == 0) nameBox.Text = Path.GetFileNameWithoutExtension(path);
        RefreshButtons();
    }

    /// <summary>Has After Effects list the project's compositions (from a copy).</summary>
    public async Task ReadAsync()
    {
        if (install is null || project is null) return;
        reading = new CancellationTokenSource();
        RefreshButtons();
        status.Text = "After Effects is opening a copy of the project… (the first time, it has to start, which can take a minute)";
        progress.Visibility = Visibility.Visible;
        progress.IsIndeterminate = true;
        try
        {
            listing = await model.ReadAfterEffectsProjectAsync(install, project, reading.Token);
            compositions.ItemsSource = listing.Compositions;
            compositions.DisplayMemberPath = nameof(AeComposition.Name);
            compositions.SelectedIndex = listing.Compositions.Count > 0 ? 0 : -1;
            status.Text = listing.Compositions.Count == 0 ? "The project has no compositions." : $"Found {listing.Compositions.Count} composition{(listing.Compositions.Count == 1 ? "" : "s")}.";
        }
        catch (OperationCanceledException)
        {
            status.Text = "Stopped.";
        }
        catch (IOException ex)
        {
            status.Text = $"After Effects couldn't read it: {ex.Message}";
        }
        finally
        {
            reading = null;
            progress.Visibility = Visibility.Collapsed;
            progress.IsIndeterminate = false;
            RefreshButtons();
        }
    }

    private void ShowFields()
    {
        fields.Children.Clear();
        words.Clear();
        colours.Clear();
        RefreshButtons();
        if (compositions.SelectedItem is not AeComposition comp)
        {
            fields.Children.Add(EditorKit.Muted("Read the compositions to change their text and colours, or type a composition's name to render it as it is."));
            return;
        }
        if (nameBox.Text.Length == 0 || nameBox.Text == Path.GetFileNameWithoutExtension(project)) nameBox.Text = comp.Name;
        fields.Children.Add(EditorKit.Faint($"{comp.Width:0} × {comp.Height:0} · {comp.FrameRate:0.##} fps · {comp.Duration:0.#} s"));
        foreach (var text in comp.Texts)
        {
            var box = new TextBox { Text = text.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 70 };
            box.TextChanged += (_, _) =>
            {
                if (box.Text.Replace("\r\n", "\n") == text.Text) words.Remove(text);
                else words[text] = box.Text.Replace("\r\n", "\n");
            };
            fields.Children.Add(EditorKit.Row(text.Name, box, 140));
        }
        foreach (var colour in comp.Colours)
        {
            var picker = new ColorPicker { Hex = colour.Hex + "FF" };
            DependencyPropertyDescriptor.FromProperty(ColorPicker.HexProperty, typeof(ColorPicker)).AddValueChanged(picker, (_, _) =>
            {
                if (MotionFields.NormalHex(picker.Hex) is not { } hex || hex == colour.Hex) colours.Remove(colour);
                else colours[colour] = hex;
            });
            fields.Children.Add(EditorKit.Row($"{colour.LayerName}: {colour.Name}", picker, 140));
        }
        if (comp.Texts.Count == 0 && comp.Colours.Count == 0) fields.Children.Add(EditorKit.Muted("No text layers or colours to change in this composition."));
    }

    /// <summary>The changes typed in, for the chosen composition.</summary>
    public IReadOnlyList<AeChange> Changes()
    {
        var comp = Composition;
        return [.. words.Select(w => AeChange.Words(comp, w.Key.Layer, w.Value)), .. colours.Select(c => AeChange.Colour(comp, c.Key, c.Value))];
    }

    /// <summary>Puts new words in a text layer, as typing in its box does (the self-test).</summary>
    public void SetWords(int layer, string text)
    {
        if ((compositions.SelectedItem as AeComposition)?.Texts.FirstOrDefault(t => t.Layer == layer) is { } found) words[found] = text;
    }

    private void Render() => StartRender();

    /// <summary>Starts the render; null when it can't start (another render is going on).</summary>
    public MotionTemplateJob? StartRender()
    {
        if (install is null || project is null || Composition.Length == 0) return null;
        var module = outputModule.Text.Trim() is { Length: > 0 } m ? m : Aerender.H264;
        if (module != (model.Settings.AfterEffectsOutputModule ?? Aerender.H264))
        {
            model.Settings.AfterEffectsOutputModule = module;
            model.Settings.Save();
        }
        var name = nameBox.Text.Trim() is { Length: > 0 } n ? n : Composition;
        var started = model.RenderAfterEffects(install, project, Composition, Changes(), name, background: asBackground.IsChecked == true);
        if (started is null)
        {
            status.Text = "Another render is going on; try again when it has finished.";
            return null;
        }
        _ = Follow(started);
        return started;
    }

    private async Task Follow(MotionTemplateJob running)
    {
        job = running;
        RefreshButtons();
        progress.Visibility = Visibility.Visible;
        void Show()
        {
            status.Text = running.Status;
            progress.IsIndeterminate = running.Fraction is null;
            progress.Value = running.Fraction ?? 0;
        }
        running.Progressed += Show;
        Show();
        try
        {
            var item = await running.Done;
            status.Text = item is not null ? $"Rendered “{item.Name}”: it's in the Media bin." : running.IsCancelled ? "Stopped. Nothing was added." : $"It didn't render: {running.Failure}";
        }
        finally
        {
            running.Progressed -= Show;
            job = null;
            progress.Visibility = Visibility.Collapsed;
            RefreshButtons();
        }
    }
}
