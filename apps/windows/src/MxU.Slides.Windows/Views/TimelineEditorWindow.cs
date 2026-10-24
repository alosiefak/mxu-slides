using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The timeline editor: the open song's lanes (slides, media, overlays, alerts, video inputs,
/// music, actions) and its stems' track rows, with transport, zoom and the song's timecode,
/// transition and section map. Sized for one laptop screen; it follows whichever song the
/// main window has open. The lanes themselves are <see cref="TimelineLanesView"/>; all the
/// state and edits are <see cref="TimelineEditorViewModel"/>. Port of the Mac's timeline card
/// (PresentationTimelineStrip and PresentationTimelineLanes in Sources/PresentationTimelineView.swift).
/// </summary>
public sealed partial class TimelineEditorWindow : Window
{
    private static TimelineEditorWindow? open;

    /// <summary>The width of the lane names on the left.</summary>
    public const double LabelWidth = 156;

    private readonly MainViewModel model;
    private readonly StackPanel labels = new() { Width = LabelWidth, VerticalAlignment = VerticalAlignment.Top };
    private readonly ScrollViewer scroller = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly ScrollBar horizontal = new() { Orientation = Orientation.Horizontal, Height = 10, Margin = new Thickness(LabelWidth + 8, 2, 0, 0) };

    // The top bar
    private readonly Button play = new() { Width = 34, ToolTip = "Play the timeline on its own clock (Space)" };
    private readonly Button record = new() { Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(4, 0, 0, 0) };
    private readonly Button discard = new() { Content = "Discard take", Margin = new Thickness(4, 0, 0, 0), ToolTip = "Put the timeline back as it was before Record (Esc)" };
    private readonly Button cancelTransition = new() { Content = "Cancel transition", Margin = new Thickness(8, 0, 0, 0), ToolTip = "This song plays to its end and stops; nothing starts by itself" };
    private readonly TextBlock title = new() { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 260 };
    private readonly TextBlock readout = new() { FontFamily = new FontFamily("Consolas"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 12, 0) };
    private readonly TextBlock status = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 };
    private readonly Button undo = new() { Margin = new Thickness(0, 0, 4, 0), ToolTip = "Undo (Ctrl+Z)" };
    private readonly Button redo = new() { Margin = new Thickness(0, 0, 8, 0), ToolTip = "Redo (Ctrl+Y)" };
    private readonly Button songMenu = new() { Content = "Song ▾", ToolTip = "Timecode start, transition to the next item, section map, track outputs" };

    // The second bar: what's selected, zoom and row height
    private readonly DockPanel selectionBar = new() { LastChildFill = true };
    private readonly Slider zoom = new() { Width = 110, Minimum = TimelineZoom.SongRange.Lower, Maximum = TimelineZoom.SongRange.Upper, VerticalAlignment = VerticalAlignment.Center };
    private readonly Slider rows = new() { Width = 70, Minimum = TimelineEditorLayout.RowHeights.Lower, Maximum = TimelineEditorLayout.RowHeights.Upper, VerticalAlignment = VerticalAlignment.Center };
    private bool updating;
    private string? selectionKey;

    private TimelineEditorWindow(MainViewModel model)
    {
        this.model = model;
        ViewModel = new TimelineEditorViewModel(model);
        Lanes = new TimelineLanesView(ViewModel) { VerticalScroller = scroller };
        Playhead = new TimelinePlayheadLayer(ViewModel);

        Title = "Timeline";
        // One laptop screen: no taller or wider than the work area, never smaller than it needs.
        var area = SystemParameters.WorkArea;
        Width = Math.Min(1200, area.Width - 40);
        Height = Math.Min(660, area.Height - 40);
        MinWidth = 820;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)FindResource("BasePlane");
        ShowInTaskbar = false;

        Content = Build();
        ViewModel.LayoutChanged += OnLayoutChanged;
        ViewModel.StateChanged += UpdateBars;
        ViewModel.PlayheadMoved += UpdateReadout;
        ViewModel.Redraw += UpdateScrollBar;
        ViewModel.Message += text => model.Notice = text;
        ViewModel.EditActionsRequested += cue => TimelineActionsWindow.Edit(this, ViewModel, cue);
        ViewModel.ResolveStems = (plan, channels) => UnmatchedStemsWindow.Ask(this, plan, channels);
        Lanes.MenuRequested += ShowLaneMenu;
        PreviewKeyDown += OnPreviewKeyDown;
        Closed += (_, _) =>
        {
            ViewModel.Dispose();
            if (open == this) open = null;
        };
        OnLayoutChanged();
        UpdateBars();
    }

    public TimelineEditorViewModel ViewModel { get; }

    public TimelineLanesView Lanes { get; }

    public TimelinePlayheadLayer Playhead { get; }

    /// <summary>Opens the editor (or brings it to the front: there is one, following the open song).</summary>
    public static TimelineEditorWindow Open(Window owner, MainViewModel model)
    {
        if (open is { } existing)
        {
            existing.Activate();
            return existing;
        }
        open = new TimelineEditorWindow(model) { Owner = owner };
        open.Show();
        return open;
    }

    // MARK: Building the window

    private UIElement Build()
    {
        // Top bar: transport, the song, its status, undo and the song menu.
        var start = new Button { Content = "⏮", Width = 34, Margin = new Thickness(0, 0, 4, 0), ToolTip = "Go to the start of the song (Home)" };
        start.Click += (_, _) => ViewModel.GoToStart();
        play.Click += (_, _) => ViewModel.PlayOrPause();
        record.Click += (_, _) => ViewModel.RecordOrStop();
        discard.Click += (_, _) => ViewModel.DiscardTake();
        cancelTransition.Click += (_, _) => ViewModel.CancelTransition();
        undo.Click += (_, _) => ViewModel.Undo();
        redo.Click += (_, _) => ViewModel.Redo();
        songMenu.Click += (_, _) => ShowSongMenu();
        var top = new DockPanel { Margin = new Thickness(12, 10, 12, 4), LastChildFill = true };
        foreach (var left in new FrameworkElement[] { start, play, record, discard, title, readout })
        {
            DockPanel.SetDock(left, Dock.Left);
            top.Children.Add(left);
        }
        foreach (var right in new FrameworkElement[] { songMenu, redo, undo, cancelTransition })
        {
            DockPanel.SetDock(right, Dock.Right);
            top.Children.Add(right);
        }
        status.Style = (Style)FindResource("Muted");
        top.Children.Add(status);

        // Second bar: the selection's tools, then zoom and row height.
        zoom.ValueChanged += (_, e) =>
        {
            if (!updating) ViewModel.SetZoom(e.NewValue);
        };
        rows.ValueChanged += (_, e) =>
        {
            if (!updating) ViewModel.SetRowHeight(e.NewValue);
        };
        var zoomOut = Small("−", "Zoom out (Ctrl+wheel)", ViewModel.ZoomOut);
        var zoomIn = Small("+", "Zoom in (Ctrl+wheel)", ViewModel.ZoomIn);
        var fit = Small("Fit", "Fit the song to the width", ViewModel.ZoomToFit);
        var shorter = Small("−", "Shorter rows", () => ViewModel.SetRowHeight(TimelineZoom.ZoomedOut(ViewModel.RowHeight, TimelineEditorLayout.RowHeights)));
        var taller = Small("+", "Taller rows", () => ViewModel.SetRowHeight(TimelineZoom.ZoomedIn(ViewModel.RowHeight, TimelineEditorLayout.RowHeights)));
        var view = new StackPanel { Orientation = Orientation.Horizontal };
        view.Children.Add(Faint("Zoom"));
        view.Children.Add(zoomOut);
        view.Children.Add(zoom);
        view.Children.Add(zoomIn);
        view.Children.Add(fit);
        view.Children.Add(Faint("Rows", 10));
        view.Children.Add(shorter);
        view.Children.Add(rows);
        view.Children.Add(taller);
        var second = new DockPanel { Margin = new Thickness(12, 2, 12, 6), LastChildFill = true };
        DockPanel.SetDock(view, Dock.Right);
        second.Children.Add(view);
        second.Children.Add(selectionBar);

        // The lanes: names on the left, the drawn lanes (and the playhead over them) on the right.
        var lanesCell = new Grid();
        lanesCell.Children.Add(Lanes);
        lanesCell.Children.Add(Playhead);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelWidth + 8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(labels);
        Grid.SetColumn(lanesCell, 1);
        grid.Children.Add(lanesCell);
        scroller.Content = grid;

        horizontal.Scroll += (_, e) => ViewModel.ScrollTo(e.NewValue);
        var lanesPanel = new Border
        {
            Background = (Brush)FindResource("Card"),
            BorderBrush = (Brush)FindResource("Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 8, 8, 6),
            Margin = new Thickness(12, 0, 12, 12),
        };
        var lanesStack = new DockPanel();
        DockPanel.SetDock(horizontal, Dock.Bottom);
        lanesStack.Children.Add(horizontal);
        lanesStack.Children.Add(scroller);
        lanesPanel.Child = lanesStack;

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(second, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(second);
        root.Children.Add(lanesPanel);
        return root;
    }

    private Button Small(string text, string tip, Action click)
    {
        var button = new Button { Content = text, ToolTip = tip, Padding = new Thickness(6, 0, 6, 0), MinHeight = 22, Margin = new Thickness(3, 0, 3, 0), Style = (Style)FindResource("GhostButton") };
        button.Click += (_, _) => click();
        return button;
    }

    private TextBlock Faint(string text, double left = 0) => new()
    {
        Text = text, FontSize = 11, Foreground = (Brush)FindResource("FaintText"), VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(left, 0, 2, 0),
    };

    // MARK: Keeping the bars up to date

    private void OnLayoutChanged()
    {
        BuildLabels();
        UpdateScrollBar();
        UpdateBars();
    }

    private void UpdateScrollBar()
    {
        var vm = ViewModel;
        horizontal.Maximum = Math.Max(vm.ContentWidth - vm.Viewport, 0);
        horizontal.ViewportSize = Math.Max(vm.Viewport, 1);
        horizontal.LargeChange = Math.Max(vm.Viewport * 0.8, 1);
        horizontal.SmallChange = 40;
        horizontal.Value = vm.ScrollX;
        horizontal.Visibility = horizontal.Maximum > 0 ? Visibility.Visible : Visibility.Hidden;
    }

    private void UpdateReadout()
    {
        var vm = ViewModel;
        readout.Text = vm.HasSong
            ? $"{PresentationTimelines.PlayheadClock(vm.Playhead)}  {vm.PlayheadTimecode}"
            : "";
        status.Text = vm.Importing ? "Importing audio…" : vm.Status;
        var pending = vm.PendingTransition is not null;
        if ((cancelTransition.Visibility == Visibility.Visible) != pending) cancelTransition.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        UpdateScrollBar();
    }

    private void UpdateBars()
    {
        var vm = ViewModel;
        updating = true;
        try
        {
            Title = vm.HasSong ? $"Timeline — {vm.Title}" : "Timeline";
            title.Text = vm.Title;
            play.Content = vm.IsPlaying ? "⏸" : "▶";
            play.ToolTip = vm.IsPlaying ? "Pause the timeline where it is (what's on screen stays)" : "Play the timeline on its own clock (Space)";
            record.Content = vm.IsRecording ? "■ Stop recording" : "● Record";
            record.Foreground = vm.IsRecording ? (Brush)FindResource("Live") : (Brush)FindResource("Text");
            record.ToolTip = vm.IsRecording
                ? "Stop recording: the slide cues after here stay"
                : "Record: plays from the playhead and makes each slide you click a cue. It replaces only the cues it plays over.";
            discard.Visibility = vm.IsRecording ? Visibility.Visible : Visibility.Collapsed;
            foreach (var button in new[] { play, record }) button.IsEnabled = vm.HasSong;
            undo.Content = "↶";
            redo.Content = "↷";
            undo.IsEnabled = vm.CanUndo && vm.CanEdit;
            redo.IsEnabled = vm.CanRedo && vm.CanEdit;
            undo.ToolTip = vm.CanUndo ? $"{vm.UndoTitle} (Ctrl+Z)" : "Nothing to undo";
            redo.ToolTip = vm.CanRedo ? $"{vm.RedoTitle} (Ctrl+Y)" : "Nothing to redo";
            songMenu.IsEnabled = vm.HasSong;
            zoom.Value = vm.Zoom;
            rows.Value = vm.RowHeight;
        }
        finally
        {
            updating = false;
        }
        UpdateReadout();
        UpdateSelectionBar();
    }

    /// <summary>The second bar's left side: the range's tools, the selection's tools, or a hint. Port of the Mac's toolbar(_:pps:display:).</summary>
    private void UpdateSelectionBar()
    {
        var vm = ViewModel;
        var picked = vm.SelectedCues;
        var key = vm.Range is { } r ? $"range {r.Lower} {r.Upper}"
            : picked.Count > 0 ? string.Join(",", picked.Select(c => c.Id + "@" + c.At)) + (vm.CanEdit ? "" : "-")
            : vm.Selection.Count > 0 ? "folded" : "hint" + vm.CanEdit;
        if (key == selectionKey) return;
        selectionKey = key;
        selectionBar.Children.Clear();
        var bar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        selectionBar.Children.Add(bar);
        if (vm.Range is { } range)
        {
            bar.Children.Add(new TextBlock
            {
                Text = $"{TimelineEditing.Clock(range.Lower)}–{TimelineEditing.Clock(range.Upper)} · {TimelineEditing.Seconds(range.Upper - range.Lower)}",
                FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
            });
            if (vm.CanEdit)
            {
                bar.Children.Add(Small("Copy", "Copy everything in the range (Ctrl+C)", () => vm.Copy()));
                bar.Children.Add(Small("Ripple Delete", "Take the range out of every lane; everything after moves up (Shift+Delete)", vm.RippleDelete));
            }
            bar.Children.Add(Small("✕", "Clear the range", vm.ClearRange));
            return;
        }
        if (vm.Picked is { } cue)
        {
            var count = picked.Count == 0 ? 1 : picked.Count;
            bar.Children.Add(new TextBlock
            {
                Text = count > 1 ? $"{count} cues" : vm.Label(cue), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0), MaxWidth = 240, TextTrimming = TextTrimming.CharacterEllipsis,
            });
            if (count == 1 && !TimelineEditing.IsFolded(cue)) bar.Children.Add(TimeField(cue));
            if (!vm.CanEdit) return;
            bar.Children.Add(Small("«", "Nudge 1 second earlier (Shift+←)", () => vm.Nudge(-1)));
            bar.Children.Add(Small("‹", "Nudge 1 frame earlier (←)", () => vm.Nudge(-PresentationTimelines.Frame)));
            bar.Children.Add(Small("›", "Nudge 1 frame later (→)", () => vm.Nudge(PresentationTimelines.Frame)));
            bar.Children.Add(Small("»", "Nudge 1 second later (Shift+→)", () => vm.Nudge(1)));
            bar.Children.Add(Small("Remove", count > 1 ? "Remove these cues from the timeline (Delete)" : "Remove from the timeline (Delete)", vm.DeleteSelected));
            return;
        }
        bar.Children.Add(new TextBlock
        {
            Text = !vm.HasSong ? "Open a song in the main window."
                : !vm.CanEdit ? "Recording: click each slide as the song plays."
                : "+ beside a lane adds at the playhead. Drag cues to move them, a clip's edges to trim it, across a lane to pick a stretch.",
            Foreground = (Brush)FindResource("FaintText"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        });
    }

    /// <summary>The selected cue's time as timecode from the song's start; type a new one and press Enter to move it. Port of TimelineCueTimeField.</summary>
    private TextBox TimeField(TimelineCue cue)
    {
        string Shown() => PresentationTimelines.Timecode((int)Math.Round(cue.At * PresentationTimelines.FramesPerSecond, MidpointRounding.AwayFromZero));
        var field = new TextBox
        {
            Text = Shown(), Width = 112, FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 0, 6, 0), IsEnabled = ViewModel.CanEdit,
            ToolTip = "Time from the start of the song (hours:minutes:seconds:frames). Type a new one and press Enter to move the cue.",
        };
        field.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) field.Text = Shown();
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            if (PresentationTimelines.Frames(field.Text) is { } frames)
                ViewModel.SetCueTime(cue, frames / (double)PresentationTimelines.FramesPerSecond);
            else field.Text = Shown();
            Lanes.Focus();
        };
        return field;
    }

    // MARK: Keys

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var name = key switch
        {
            Key.Back => "Backspace",
            Key.Delete => "Delete",
            Key.Left => "Left",
            Key.Right => "Right",
            Key.Escape => "Escape",
            Key.Space => "Space",
            Key.Home => "Home",
            Key.B => "B",
            Key.C => "C",
            Key.V => "V",
            Key.Y => "Y",
            Key.Z => "Z",
            _ => null,
        };
        if (name is null) return;
        var modifiers = Keyboard.Modifiers;
        if (ViewModel.Key(name, modifiers.HasFlag(ModifierKeys.Control), modifiers.HasFlag(ModifierKeys.Shift), modifiers.HasFlag(ModifierKeys.Alt)))
            e.Handled = true;
    }
}
