using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.Outputs;
using MxU.Slides.Windows.Rendering;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The welcome guide: on a new library's first launch, and again from Help › Welcome guide…. Five
/// steps: welcome (or restore a backup from another PC), the audience and stage screens, a look for
/// new songs, bringing songs in (ProPresenter, PowerPoint, text), and where Help is. Port of the
/// Mac's OnboardingView (its content and screens steps, "Skip Setup", "Moving from another Mac?
/// Restore a library backup…"); the step rules are Core's OnboardingFlow.
/// </summary>
internal sealed class OnboardingWindow : Window
{
    private readonly MainViewModel model;
    private readonly OnboardingFlow flow;
    private readonly StackPanel chips = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
    private readonly ContentControl page = new();
    private readonly Button back;
    private readonly Button next;

    private OnboardingWindow(Window owner, MainViewModel model)
    {
        this.model = model;
        flow = new OnboardingFlow(firstRun: model.Library.IsNew);
        EditorKit.Dress(this, owner, "Welcome to MxU Slides", 700, 600);
        ResizeMode = ResizeMode.NoResize;

        var header = new StackPanel { Margin = new Thickness(28, 20, 28, 14) };
        header.Children.Add(new TextBlock { Text = "Welcome to MxU Slides", FontSize = 20, FontWeight = FontWeights.SemiBold });
        header.Children.Add(chips);

        var skip = EditorKit.Button("Skip Setup", Finish);
        back = EditorKit.Button("Back", () => Go(flow.Back));
        back.Margin = new Thickness(0, 0, 8, 0);
        next = EditorKit.Button("Continue", () =>
        {
            if (flow.IsLastStep) Finish();
            else Go(flow.Advance);
        }, accent: true);
        next.IsDefault = true;
        var footer = new DockPanel { Margin = new Thickness(28, 12, 28, 18) };
        DockPanel.SetDock(skip, Dock.Left);
        footer.Children.Add(skip);
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(back);
        right.Children.Add(next);
        footer.Children.Add(right);

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(new Border { BorderBrush = EditorKit.Res<Brush>("Border"), BorderThickness = new Thickness(0, 1, 0, 1), Padding = new Thickness(28, 18, 28, 18), Child = page });
        Content = root;
        ShowStep();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Finish();
        };
    }

    public static OnboardingWindow Open(Window owner, MainViewModel model)
    {
        var window = new OnboardingWindow(owner, model);
        Opened?.Invoke(window, EventArgs.Empty);
        window.ShowDialog();
        return window;
    }

    /// <summary>Raised with the guide just before it shows (the self-test pictures each step).</summary>
    internal static event EventHandler? Opened;

    /// <summary>Which step it's on (the self-test walks through them).</summary>
    internal OnboardingFlow Flow => flow;

    internal void Next() => Go(flow.Advance);

    private void Go(Action move)
    {
        move();
        ShowStep();
    }

    private void ShowStep()
    {
        chips.Children.Clear();
        foreach (var step in OnboardingFlow.Steps)
        {
            var on = step == flow.Current;
            var chip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 0) };
            chip.Children.Add(new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = on ? EditorKit.Res<Brush>("Accent") : EditorKit.Res<Brush>("Raised"),
                Child = new TextBlock { Text = ((int)step + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
            chip.Children.Add(new TextBlock { Text = OnboardingFlow.Title(step), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = on ? EditorKit.Res<Brush>("Text") : EditorKit.Res<Brush>("MutedText") });
            chips.Children.Add(chip);
        }
        back.Visibility = flow.CanGoBack ? Visibility.Visible : Visibility.Collapsed;
        next.Content = flow.IsLastStep ? "Get Started" : "Continue";
        page.Content = flow.Current switch
        {
            OnboardingFlow.Step.Screens => ScreensStep(),
            OnboardingFlow.Step.Look => LookStep(),
            OnboardingFlow.Step.Songs => SongsStep(),
            OnboardingFlow.Step.Help => HelpStep(),
            _ => WelcomeStep(),
        };
    }

    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };

    private static TextBlock Body(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = EditorKit.Res<Brush>("MutedText"), Margin = new Thickness(0, 0, 0, 12), FontSize = 13 };

    // MARK: The steps

    private UIElement WelcomeStep()
    {
        var panel = new StackPanel();
        panel.Children.Add(Heading("Songs, scripture and media on the room's screens"));
        panel.Children.Add(Body("MxU Slides runs the words, Bible passages, videos and announcements for a service: lyrics on the projector, "
            + "the next line on the stage screen, and a livestream if you want one. This short guide sets up the essentials. Everything here can be changed later."));
        panel.Children.Add(Body(model.Library.IsNew
            ? "Your library starts with a Getting Started service and a short welcome presentation that walks through the basics."
            : "Your library is already set up; this guide only changes what you choose."));
        var restore = EditorKit.Button("Moving from another PC? Restore a library backup…", () => BackupWindow.Open(this, model));
        restore.HorizontalAlignment = HorizontalAlignment.Left;
        restore.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(restore);
        return panel;
    }

    private UIElement ScreensStep()
    {
        var panel = new StackPanel();
        panel.Children.Add(Heading("Pick your screens"));
        var displays = Displays.All();
        foreach (var display in displays)
            panel.Children.Add(new TextBlock { Text = "▭  " + display.Label, Margin = new Thickness(0, 1, 0, 1) });
        panel.Children.Add(Body(displays.Count > 1
            ? "Give each display a job: Audience is what the room sees (the projector or TV); Stage is the confidence screen for the band and the speaker."
            : "Only one display is connected, so nothing leaves this PC yet: the outputs show in preview windows, so you can see what the room would see. "
              + "Connect a projector or TV any time and give it the Audience job here or in Screens & Settings."));
        if (model.Outputs is not { } outputs) return panel;
        var choices = new List<(string? Device, string Label)> { (null, displays.Count > 1 ? "Off / preview window" : "Preview window") };
        choices.AddRange(displays.Select(d => ((string?)d.DeviceName, d.Label)));
        foreach (var role in new[] { OutputRole.Audience, OutputRole.Stage })
        {
            var picker = new ComboBox { Width = 320 };
            foreach (var (device, label) in choices) picker.Items.Add(new ComboBoxItem { Content = label, Tag = device, IsSelected = device == outputs.AssignedDisplay(role) });
            if (picker.SelectedItem is null) picker.SelectedIndex = 0;
            picker.SelectionChanged += (_, _) =>
            {
                if (picker.SelectedItem is not ComboBoxItem { Content: string label, Tag: var tag }) return;
                var device = tag as string;
                var other = role == OutputRole.Audience ? OutputRole.Stage : OutputRole.Audience;
                if (device is not null && outputs.AssignedDisplay(other) == device) outputs.AssignDisplay(other, null, null); // one display, one job
                outputs.AssignDisplay(role, device, device is null ? null : label);
                outputs.Apply();
            };
            panel.Children.Add(EditorKit.Row(role == OutputRole.Audience ? "Audience" : "Stage", picker, labelWidth: 90));
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(EditorKit.Button("Identify Displays", OutputManager.Identify));
        var more = EditorKit.Button("More Screens, NDI, Edge Blending…", () => new ScreensWindow(outputs) { Owner = this }.ShowDialog());
        more.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(more);
        panel.Children.Add(buttons);
        return panel;
    }

    private UIElement LookStep()
    {
        var panel = new StackPanel();
        panel.Children.Add(Heading("Pick a look for new songs"));
        panel.Children.Add(Body("New songs are drawn with this theme: the font, colours and where the words sit. Each song can still have its own, and the themes can be changed later."));
        var grid = new WrapPanel();
        var sample = SampleSlide();
        foreach (var theme in model.Library.All<Theme>().Where(t => t.Slides is { Count: > 0 }).Take(8))
        {
            var chosen = model.Settings.DefaultThemeId == theme.Id;
            var tile = new StackPanel { Width = 146, Margin = new Thickness(0, 0, 10, 10) };
            var scene = SlideSceneBuilder.Scene(sample, theme, null);
            tile.Children.Add(new Border
            {
                BorderThickness = new Thickness(2), BorderBrush = chosen ? EditorKit.Res<Brush>("Accent") : Brushes.Transparent, CornerRadius = new CornerRadius(4),
                Child = new SceneView { StaticScene = scene, PlaysVideo = false, Width = 142, Height = 80, Media = model.Media },
            });
            tile.Children.Add(new TextBlock { Text = theme.Name, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 3, 0, 0), Foreground = chosen ? EditorKit.Res<Brush>("Text") : EditorKit.Res<Brush>("MutedText") });
            var button = new Button { Content = tile, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, ToolTip = $"Use {theme.Name} for new songs" };
            button.Click += (_, _) =>
            {
                model.Settings.DefaultThemeId = theme.Id;
                model.Settings.Save();
                ShowStep();
            };
            grid.Children.Add(button);
        }
        panel.Children.Add(grid);
        return panel;
    }

    /// <summary>Two lines of a hymn, to show each look with.</summary>
    private static Slide SampleSlide() => new()
    {
        Id = "onboarding-sample", Name = "", ThemeSlideName = "Lyrics",
        Objects = [new SlideObject { Id = "onboarding-sample-text", ObjectKind = SlideObjectKind.Text, Name = "Lyrics", Text = "Amazing grace how sweet the sound\nThat saved a wretch like me" }],
    };

    private UIElement SongsStep()
    {
        var panel = new StackPanel();
        panel.Children.Add(Heading("Bring your songs in"));
        panel.Children.Add(Body("Everything comes into this PC's library; the files you import from are left as they are. You can do this any time from Import in the toolbar."));
        var window = Owner as MainWindow;
        void Choice(string title, string detail, Action action)
        {
            var button = EditorKit.Button(title, action);
            button.HorizontalAlignment = HorizontalAlignment.Left;
            button.MinWidth = 300;
            panel.Children.Add(button);
            panel.Children.Add(new TextBlock { Text = detail, FontSize = 11, Foreground = EditorKit.Res<Brush>("MutedText"), Margin = new Thickness(2, 3, 0, 12), TextWrapping = TextWrapping.Wrap });
        }
        Choice("Import a ProPresenter Library…", "Presentations, playlists (as services), themes, media, messages and stage layouts from ProPresenter 7.", () =>
        {
            flow.Choose(OnboardingFlow.ContentChoice.ImportSongs);
            _ = ImportProPresenterAsync();
        });
        Choice("Import from OpenLP…", "Songs and custom slides from OpenLP, on this PC or from a copy of its data folder.", () =>
        {
            flow.Choose(OnboardingFlow.ContentChoice.ImportSongs);
            window?.ImportFromOpenLp(this);
        });
        Choice("Import PowerPoint or Lyrics Files…", ".pptx decks, SongSelect and ChordPro files, plain text lyrics, and .mxuslides from a Mac.", () =>
        {
            flow.Choose(OnboardingFlow.ContentChoice.ImportSongs);
            window?.ImportFromGuide(this);
        });
        Choice("Type or Paste a Song…", "Paste the words: verses and choruses are found by themselves.", () => window?.NewSongFromGuide(this));
        Choice("Start From Scratch", "Keep the Getting Started service and carry on.", () => Go(() => flow.Choose(OnboardingFlow.ContentChoice.StartFromScratch)));
        return panel;
    }

    private async Task ImportProPresenterAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose your ProPresenter folder (the one holding Libraries and Playlists)",
            InitialDirectory = ProImport.ProPresenterImporter.ProPresenterShowDirectory() ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(this) != true) return;
        var library = model.Library;
        var folder = dialog.FolderName;
        var scan = await Task.Factory.StartNew(() => new ProImport.ProWorkspaceImporter(library).Scan(folder), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        if (scan.Presentations + scan.Themes + scan.Playlists + scan.Media == 0)
        {
            model.Problem = "That folder doesn't look like a ProPresenter library: no presentations, themes, playlists or media were found.";
            return;
        }
        model.Notice = $"Importing {scan.Presentations} presentations and {scan.Playlists} playlists from ProPresenter…";
        await model.ImportProPresenterLibraryAsync(folder);
    }

    private UIElement HelpStep()
    {
        var panel = new StackPanel();
        panel.Children.Add(Heading("Where Help is"));
        panel.Children.Add(Body("The Help button at the top right of the main window opens this guide again, the list of keys, Diagnostics (is this PC ready?) and Report a Problem."));
        panel.Children.Add(EditorKit.Section("THE KEYS TO KNOW", top: 4));
        foreach (var (keys, what) in HelpMenu.KeysToKnow)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var key = new TextBlock { Text = keys, Width = 150, FontWeight = FontWeights.SemiBold };
            DockPanel.SetDock(key, Dock.Left);
            row.Children.Add(key);
            row.Children.Add(new TextBlock { Text = what, Foreground = EditorKit.Res<Brush>("MutedText") });
            panel.Children.Add(row);
        }
        return panel;
    }

    private void Finish()
    {
        OnboardingFlow.MarkDone(model.Library.Root);
        Log.Info($"Welcome guide finished at step {flow.Number} ({flow.Choice?.ToString() ?? "no content choice"})");
        Close();
    }
}
