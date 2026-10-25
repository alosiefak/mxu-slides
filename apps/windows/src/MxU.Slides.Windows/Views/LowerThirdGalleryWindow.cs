using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Rendering;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The lower-thirds gallery: every design as a tile (hover one and it plays), filters by what it's
/// for and by look, and on the right the picked design playing on a loop, its fields to fill in,
/// the pack it goes into, and Add to library / Go live. Built in code, like the automation editors;
/// the state is in <see cref="LowerThirdGalleryViewModel"/>.
/// </summary>
public sealed class LowerThirdGalleryWindow : Window
{
    private readonly LowerThirdGalleryViewModel vm;
    private readonly WrapPanel tiles = new();
    private readonly StackPanel categories = new();
    private readonly StackPanel styles = new() { Orientation = Orientation.Horizontal };
    // The previews' own black edge is turned off: their frames paint what's behind (PaintBehind).
    private readonly SceneView preview = new() { PlaysVideo = false, Width = PreviewWidth, Height = PreviewWidth * 9 / 16, Letterbox = Brushes.Transparent };
    private readonly TextBlock title = new() { FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock subtitle = new() { FontSize = 11, Margin = new Thickness(0, 2, 0, 6) };
    private readonly TextBlock description = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 10) };
    private readonly StackPanel fields = new();
    private readonly StackPanel extras = new();
    private readonly ComboBox pack = new() { MinWidth = 160 };
    private readonly Button add;
    private readonly Button live;
    private readonly TextBlock notice = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 8, 0, 0) };
    private readonly DispatcherTimer loop;
    private readonly Dictionary<LowerThirdTemplate, Border> tileFor = [];

    // Each preview's frame, and the design in it: what's painted behind it follows the Backdrop choice.
    private readonly Dictionary<Border, Overlay> frames = [];
    private Border? previewFrame;

    private const double PreviewWidth = 362;

    // The views that loop a play-through, and when each one's current pass began.
    private readonly Dictionary<SceneView, (Overlay Overlay, double StartedAt)> playing = [];

    public LowerThirdGalleryViewModel Model => vm;

    private LowerThirdGalleryWindow(Window? owner, MainViewModel main)
    {
        vm = new LowerThirdGalleryViewModel(main);
        EditorKit.Dress(this, owner, "Lower-thirds gallery", 1240, 800);
        add = EditorKit.Button("Add to library", () => vm.AddToLibrary(), "Save this design, as filled in, into the pack below (it appears in the Overlays bin)", accent: true);
        live = EditorKit.Button("Go live", ToggleLive, "Put it up on the program (adding it to the library first)");
        Content = Layout();
        vm.PreviewChanged += OnPreviewChanged;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LowerThirdGalleryViewModel.Visible)) RebuildTiles();
            if (e.PropertyName is nameof(LowerThirdGalleryViewModel.Category) or nameof(LowerThirdGalleryViewModel.Style)) RebuildFilters();
            if (e.PropertyName is nameof(LowerThirdGalleryViewModel.Notice)) notice.Text = vm.Notice ?? "";
            if (e.PropertyName is nameof(LowerThirdGalleryViewModel.IsLive) or nameof(LowerThirdGalleryViewModel.Added)) RefreshButtons();
            if (e.PropertyName is nameof(LowerThirdGalleryViewModel.Fields)) RebuildFields();
            if (e.PropertyName is nameof(LowerThirdGalleryViewModel.Packs)) RefreshPacks();
            if (e.PropertyName is nameof(LowerThirdGalleryViewModel.TimerChoice) or nameof(LowerThirdGalleryViewModel.TimerChoices)) RefreshTimers();
        };
        main.LiveOverlaysChanged += OnLiveOverlaysChanged;
        loop = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => Loop(), Dispatcher);
        loop.Start();
        Closed += (_, _) =>
        {
            loop.Stop();
            vm.PreviewChanged -= OnPreviewChanged;
            main.LiveOverlaysChanged -= OnLiveOverlaysChanged;
            main.FlushOverlayEdits();
        };
        RebuildFilters();
        RebuildTiles();
        ShowSelected();
    }

    public static LowerThirdGalleryWindow Open(Window owner, MainViewModel main)
    {
        var window = new LowerThirdGalleryWindow(owner, main);
        window.Show();
        return window;
    }

    private void OnLiveOverlaysChanged()
    {
        vm.RefreshLive();
        RefreshButtons();
    }

    // MARK: Layout

    private UIElement Layout()
    {
        var grid = new Grid { Margin = new Thickness(14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(370) });

        var left = new StackPanel();
        left.Children.Add(EditorKit.Section("FOR", top: 0));
        left.Children.Add(categories);
        grid.Children.Add(left);

        var middle = new DockPanel();
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var behind = new ComboBox { Width = 190, ItemsSource = LowerThirdGalleryViewModel.Backdrops, SelectedIndex = 0,
            ToolTip = "What the previews draw behind the designs: a stand-in camera shot (or worship background, for full-screen designs), or what's on the program now" };
        behind.SelectionChanged += (_, _) =>
        {
            vm.Backdrop = behind.SelectedIndex;
            PaintBehind();
            foreach (var view in tiles.Children.OfType<Border>().Select(t => t.Tag).OfType<SceneView>()) Rest(view);
        };
        DockPanel.SetDock(behind, Dock.Right);
        top.Children.Add(behind);
        top.Children.Add(styles);
        DockPanel.SetDock(top, Dock.Top);
        middle.Children.Add(top);
        var hint = EditorKit.Muted("Hover a design to play it; click to pick it. Everything here is an ordinary overlay once added, so you can change it in the overlay editor.");
        DockPanel.SetDock(hint, Dock.Bottom);
        middle.Children.Add(hint);
        middle.Children.Add(new ScrollViewer { Content = tiles, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Grid.SetColumn(middle, 2);
        grid.Children.Add(middle);

        var right = new StackPanel();
        previewFrame = new Border { Child = preview, Background = Brushes.Black, CornerRadius = new CornerRadius(6), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left };
        right.Children.Add(previewFrame);
        title.Margin = new Thickness(0, 10, 0, 0);
        title.Foreground = EditorKit.Res<Brush>("Text");
        subtitle.Foreground = EditorKit.Res<Brush>("MutedText");
        description.Foreground = EditorKit.Res<Brush>("MutedText");
        notice.Foreground = EditorKit.Res<Brush>("Accent");
        right.Children.Add(title);
        right.Children.Add(subtitle);
        right.Children.Add(description);
        right.Children.Add(EditorKit.Section("FIELDS", top: 2));
        right.Children.Add(fields);
        right.Children.Add(extras);
        right.Children.Add(EditorKit.Section("ADD TO PACK"));
        RefreshPacks();
        pack.SelectionChanged += (_, _) =>
        {
            if (pack.SelectedItem is string chosen && chosen != vm.Pack) vm.Pack = chosen;
        };
        var newPack = EditorKit.Button("New…", () =>
        {
            if (InputDialog.Ask(this, "New pack", "A name for the new overlay pack:", "My Lower Thirds") is { } name) vm.NewPack(name);
        }, "Put it in a pack of its own");
        newPack.Margin = new Thickness(6, 0, 0, 0);
        DockPanel.SetDock(newPack, Dock.Right);
        var packRow = new DockPanel();
        packRow.Children.Add(newPack);
        packRow.Children.Add(pack);
        right.Children.Add(EditorKit.Row("Pack", packRow, 60));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(add);
        buttons.Children.Add(live);
        right.Children.Add(buttons);
        right.Children.Add(notice);
        var scroller = new ScrollViewer { Content = right, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 4, 0) };
        Grid.SetColumn(scroller, 4);
        grid.Children.Add(scroller);

        var done = EditorKit.Button("Done", Close, accent: true);
        done.IsCancel = true;
        done.MinWidth = 90;
        done.HorizontalAlignment = HorizontalAlignment.Right;
        done.Margin = new Thickness(0, 0, 14, 12);
        var outer = new DockPanel();
        DockPanel.SetDock(done, Dock.Bottom);
        outer.Children.Add(done);
        outer.Children.Add(grid);
        return outer;
    }

    private void RebuildFilters()
    {
        categories.Children.Clear();
        foreach (var category in vm.Categories)
        {
            var count = category == LowerThirdGalleryViewModel.AllCategories ? LowerThirdGallery.All.Count : LowerThirdGallery.All.Count(t => t.Category == category);
            var button = new ToggleButton
            {
                Content = $"{category}  ({count})", IsChecked = vm.Category == category, HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 3), Padding = new Thickness(10, 4, 10, 4),
            };
            button.Click += (_, _) =>
            {
                vm.Category = category;
                RebuildFilters(); // a click on the shelf already showing would otherwise un-check it
            };
            categories.Children.Add(button);
        }
        styles.Children.Clear();
        foreach (var style in vm.Styles)
        {
            var button = new ToggleButton { Content = style, IsChecked = vm.Style == style, Margin = new Thickness(0, 0, 4, 0), Padding = new Thickness(10, 3, 10, 3) };
            button.Click += (_, _) =>
            {
                vm.Style = style;
                RebuildFilters();
            };
            styles.Children.Add(button);
        }
    }

    // MARK: Tiles

    private void RebuildTiles()
    {
        foreach (var view in playing.Keys.Where(v => v != preview).ToList()) playing.Remove(view);
        foreach (var frame in frames.Keys.Where(f => f != previewFrame).ToList()) frames.Remove(frame);
        foreach (var view in resting.Keys.Where(v => v != preview).ToList()) resting.Remove(view);
        tiles.Children.Clear();
        tileFor.Clear();
        foreach (var template in vm.Visible)
        {
            var overlay = template.Make();
            var view = new SceneView { PlaysVideo = false, Width = 256, Height = 144, Letterbox = Brushes.Transparent };
            resting[view] = overlay;
            view.Present(vm.Still(overlay), SceneTransition.Cut);
            var label = new StackPanel { Margin = new Thickness(2, 5, 2, 0) };
            label.Children.Add(new TextBlock { Text = template.Name, Foreground = EditorKit.Res<Brush>("Text"), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
            label.Children.Add(new TextBlock { Text = $"{template.Category} · {template.StyleName}", Foreground = EditorKit.Res<Brush>("MutedText"), FontSize = 10 });
            var frame = new Border { Child = view, CornerRadius = new CornerRadius(4), ClipToBounds = true };
            frames[frame] = overlay;
            var stack = new StackPanel();
            stack.Children.Add(frame);
            stack.Children.Add(label);
            var tile = new Border
            {
                Child = stack, Padding = new Thickness(6), Margin = new Thickness(0, 0, 10, 10), CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(2), Cursor = Cursors.Hand, Background = EditorKit.Res<Brush>("Card"), ToolTip = template.Description,
                Tag = view,
            };
            tile.MouseEnter += (_, _) => Play(view, overlay);
            tile.MouseLeave += (_, _) => Rest(view);
            tile.MouseLeftButtonUp += (_, _) => Pick(template);
            tileFor[template] = tile;
            tiles.Children.Add(tile);
        }
        PaintBehind();
        MarkSelected();
    }

    private void MarkSelected()
    {
        foreach (var (template, tile) in tileFor)
            tile.BorderBrush = template == vm.Selected ? EditorKit.Res<Brush>("Accent") : Brushes.Transparent;
    }

    /// <summary>Picks a design: it plays on the right, with its fields ready to fill in.</summary>
    public void Pick(LowerThirdTemplate template)
    {
        vm.Select(template);
        MarkSelected();
        ShowSelected();
    }

    // MARK: The picked design

    private void ShowSelected()
    {
        if (vm.Selected is not { } template) return;
        title.Text = template.Name;
        subtitle.Text = $"{template.Category} · {template.StyleName} look";
        description.Text = template.Description;
        RebuildFields();
        RefreshButtons();
        if (vm.Working is { } overlay)
        {
            if (previewFrame is not null) frames[previewFrame] = overlay;
            resting[preview] = overlay;
            PaintBehind();
            Play(preview, overlay);
        }
    }

    /// <summary>The fields (and the song and timer helpers) for the design picked; made again when its fields change.</summary>
    private void RebuildFields()
    {
        fields.Children.Clear();
        foreach (var field in vm.Fields)
        {
            var multiline = field.Text.Length > 48 || field.Text.Contains('\n');
            var box = new TextBox
            {
                Text = field.Text, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                MinHeight = multiline ? 52 : 0, VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
            };
            box.TextChanged += (_, _) => field.Text = box.Text;
            // Words put in from elsewhere show in the box too. It compares without Windows' "\r\n",
            // so the box never rewrites itself under the caret while you type.
            field.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(OverlayFieldRow.Text) && box.Text.Replace("\r\n", "\n") != field.Text) box.Text = field.Text;
            };
            fields.Children.Add(EditorKit.Row(field.Label, box, 92));
        }
        extras.Children.Clear();
        timers = null;
        if (vm.IsCredits)
        {
            var fill = EditorKit.Button("Fill from the song", () => vm.FillFromSong(),
                "Take the title, writers and CCLI song number from the song that's up (or open)");
            fill.HorizontalAlignment = HorizontalAlignment.Left;
            extras.Children.Add(fill);
        }
        if (vm.IsCountdown)
        {
            var picker = new ComboBox { MinWidth = 200 };
            timers = picker;
            RefreshTimers();
            picker.SelectionChanged += (_, _) =>
            {
                if (picker.SelectedItem is string choice && choice != vm.TimerChoice) vm.TimerChoice = choice;
            };
            extras.Children.Add(EditorKit.Row("Timer", picker, 92));
            var start = EditorKit.Button("Start a 5-minute countdown", () => vm.StartCountdown(5),
                "Makes a countdown timer, starts it, and points these digits at it");
            start.HorizontalAlignment = HorizontalAlignment.Left;
            extras.Children.Add(start);
        }
    }

    // The countdown's timer picker, while a countdown design is picked.
    private ComboBox? timers;

    private void RefreshTimers()
    {
        if (timers is null) return;
        timers.ItemsSource = vm.TimerChoices;
        timers.SelectedItem = vm.TimerChoice;
    }

    private void RefreshPacks()
    {
        pack.ItemsSource = vm.Packs;
        pack.SelectedItem = vm.Pack;
    }

    private void RefreshButtons()
    {
        live.Content = vm.IsLive ? "Take down" : vm.Added is null ? "Add and go live" : "Go live";
        add.Content = vm.Added is null ? "Add to library" : "Add another copy";
        if (vm.IsLive)
        {
            live.Background = EditorKit.Res<Brush>("Live");
            live.Foreground = Brushes.White;
        }
        else
        {
            live.ClearValue(BackgroundProperty);
            live.ClearValue(ForegroundProperty);
        }
    }

    private void ToggleLive()
    {
        if (vm.IsLive) vm.TakeDown();
        else vm.GoLive();
        RefreshButtons();
    }

    /// <summary>A field changed: show the words settled straight away, and play it through again once typing pauses.</summary>
    private void OnPreviewChanged()
    {
        if (vm.Working is not { } overlay) return;
        resting[preview] = overlay;
        preview.Present(vm.Still(overlay), SceneTransition.Cut);
        playing[preview] = (overlay, ShowState.HostClock() - LowerThirdGalleryViewModel.Duration(overlay) - 0.8 + 1.5);
    }

    // MARK: What's behind

    /// <summary>
    /// Paints each preview's frame: a stand-in camera shot behind a lower third, a worship
    /// background behind a design that fills the screen (Views/StandInPictures.cs); plain black
    /// when the previews draw the live program themselves.
    /// </summary>
    private void PaintBehind()
    {
        foreach (var (frame, overlay) in frames)
            frame.Background = !vm.StandInBehind ? Brushes.Black
                : LowerThirdGalleryViewModel.FillsTheScreen(overlay) ? WorshipBrush : StageBrush;
    }

    private static readonly Brush StageBrush = Frozen(new ImageBrush(StandInPictures.Stage) { Stretch = Stretch.UniformToFill });
    private static readonly Brush WorshipBrush = Frozen(new ImageBrush(StandInPictures.Worship) { Stretch = Stretch.UniformToFill });

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    // MARK: Playing on a loop

    // The design each view shows when it isn't playing.
    private readonly Dictionary<SceneView, Overlay> resting = [];

    /// <summary>A tile the pointer left goes back to its design at rest (the picked design's preview keeps playing).</summary>
    private void Rest(SceneView view)
    {
        if (!resting.TryGetValue(view, out var overlay)) return;
        if (view == preview)
        {
            Play(preview, overlay);
            return;
        }
        playing.Remove(view);
        view.Present(vm.Still(overlay), SceneTransition.Cut);
    }

    private void Play(SceneView view, Overlay overlay)
    {
        var now = ShowState.HostClock();
        playing[view] = (overlay, now);
        view.Present(vm.Picture(overlay, now), SceneTransition.Cut);
    }

    /// <summary>Each looping view starts again a moment after its play-through ends.</summary>
    private void Loop()
    {
        var now = ShowState.HostClock();
        foreach (var (view, (overlay, startedAt)) in playing.ToList())
            if (now - startedAt > LowerThirdGalleryViewModel.Duration(overlay) + 0.8) Play(view, overlay);
    }
}
