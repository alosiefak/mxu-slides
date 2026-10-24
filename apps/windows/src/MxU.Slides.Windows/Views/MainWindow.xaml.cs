using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.Outputs;
using MxU.Slides.Windows.Rendering;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel model;
    private OutputManager? outputs;
    private readonly DispatcherTimer settingsSave;

    public MainWindow(MainViewModel model)
    {
        this.model = model;
        // Created before the XAML loads: controls can raise change events while it does.
        settingsSave = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            settingsSave!.Stop();
            model.Settings.Save();
        }, Dispatcher);
        settingsSave.Stop();
        DataContext = model;
        InitializeComponent();
        model.AudiencePreview = AudiencePreview;
        model.StagePreview = StagePreview;
        AudiencePreview.Media = model.Media;
        StagePreview.Media = model.Media;
        model.Canvas = CanvasEditor;
        CanvasEditor.TextEditRequested += _ => Dispatcher.BeginInvoke(FocusInspectorText, DispatcherPriority.Loaded);
        RestorePlacement();
        Loaded += OnLoaded;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        VideoOutputs.Initialize();
        outputs = new OutputManager(model.Settings, this, model.Media);
        outputs.DeviceSinkFactory = device => VideoOutputs.Create(device, outputs.Config);
        model.Outputs = outputs;
        model.Program = new ProgramFeed(model.Media);
        model.AskYesNo = question => MessageBox.Show(this, question, "MxU Slides", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        outputs.StatusChanged += UpdateOutputStatus;
        if (model.Settings.OutputsOnAtLaunch) outputs.Apply();
        model.RenderOutputs(SceneTransition.Cut);
        UpdateOutputStatus();
        Log.Info($"Main window ready; {Displays.All().Count} display(s) connected");
    }

    private bool forceClose;

    /// <summary>Closes without the "something is still showing" question (self-test, shutdown).</summary>
    public void ForceClose()
    {
        forceClose = true;
        Close();
    }

    // MARK: Song timeline

    private void TimelineSlider_MouseDown(object sender, MouseButtonEventArgs e) => model.TimelineScrubbing = true;

    private void TimelineSlider_MouseUp(object sender, MouseButtonEventArgs e)
    {
        model.TimelineScrubbing = false;
        model.SeekTimeline(TimelineSlider.Value);
    }

    /// <summary>The ⋯ menu: the song's music, following timecode, undo and clear.</summary>
    private void TimelineMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        // The editor: the song's lanes, its music and stems, its transition and section map.
        var edit = new MenuItem { Header = "Edit timeline…", IsEnabled = model.CurrentPresentation is not null };
        edit.Click += (_, _) => TimelineEditorWindow.Open(this, model);
        menu.Items.Add(edit);
        menu.Items.Add(new Separator());
        var music = new MenuItem { Header = "Play this music with the timeline" };
        var songs = model.AudioTiles.Where(t => t.Item is not null).Select(t => t.Item!).ToList();
        foreach (var item in songs)
        {
            var choice = new MenuItem { Header = item.Name };
            choice.Click += (_, _) => model.SetTimelineSong(item);
            music.Items.Add(choice);
        }
        if (songs.Count == 0) music.Items.Add(new MenuItem { Header = "No music yet: import some in the Music tab", IsEnabled = false });
        menu.Items.Add(music);

        var current = model.CurrentTimeline;
        var follow = new MenuItem { Header = "Follow timecode", IsCheckable = true, IsChecked = current?.TimecodeEnabled == true };
        follow.Click += (_, _) => model.SetTimelineTimecode(follow.IsChecked);
        menu.Items.Add(follow);
        var start = new MenuItem { Header = $"Timecode start ({Core.Automation.PresentationTimelines.Timecode(current?.TimecodeStart ?? 3600 * 30)})…" };
        start.Click += (_, _) =>
        {
            var shown = Core.Automation.PresentationTimelines.Timecode(model.CurrentTimeline?.TimecodeStart ?? 3600 * 30);
            if (InputDialog.Ask(this, "Timecode start", "Where this song starts (hh:mm:ss:ff)", shown) is not { } typed) return;
            if (Core.Automation.PresentationTimelines.Frames(typed) is { } frames) model.SetTimelineTimecode(true, frames);
            else MessageBox.Show(this, "Type the start as hours:minutes:seconds:frames, for example 01:00:00:00.", "Timecode start");
        };
        menu.Items.Add(start);
        menu.Items.Add(new Separator());
        var undo = new MenuItem { Header = "Undo the last take", IsEnabled = model.CanUndoTimeline };
        undo.Click += (_, _) => model.UndoTimeline();
        menu.Items.Add(undo);
        var clear = new MenuItem { Header = "Clear this timeline", IsEnabled = current is not null };
        clear.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Remove this song's timeline? Its slides stay as they are.", "Clear the timeline", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                model.ClearTimeline();
        };
        menu.Items.Add(clear);
        menu.IsOpen = true;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!forceClose && outputs is not null && outputs.IsOnDisplay(OutputRole.Audience) && !model.Show.IsEmpty)
        {
            var answer = MessageBox.Show(this, "Something is still showing on the audience screen. Quit MxU Slides anyway?",
                "MxU Slides", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }
        if (model.IsBroadcasting)
        {
            // A recording needs a moment to finish its file: stop everything, then close for real.
            e.Cancel = true;
            if (!forceClose && MessageBox.Show(this, "A recording or stream is running. Stop it and quit?", "MxU Slides",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            IsEnabled = false;
            _ = FinishBroadcastsThenClose();
            return;
        }
        model.FlushPendingSave();
        SavePlacement();
        model.Settings.Save();
        model.StopApi();
        model.StopTimeline();
        model.Program?.Dispose();
        outputs?.CloseAll();
    }

    private async Task FinishBroadcastsThenClose()
    {
        await model.EndBroadcastsAsync();
        forceClose = true;
        Close();
    }

    private void RestorePlacement()
    {
        var s = model.Session;
        if (s.WindowWidth is > 400 && s.WindowHeight is > 300 && s.WindowLeft is { } left && s.WindowTop is { } top
            && left > SystemParameters.VirtualScreenLeft - 100 && top > SystemParameters.VirtualScreenTop - 100
            && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100
            && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
            Width = s.WindowWidth.Value;
            Height = s.WindowHeight.Value;
        }
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        model.Session.WindowLeft = bounds.Left;
        model.Session.WindowTop = bounds.Top;
        model.Session.WindowWidth = bounds.Width;
        model.Session.WindowHeight = bounds.Height;
        model.Session.WindowMaximized = WindowState == WindowState.Maximized;
        model.Session.Save();
    }

    public void ShowProblem(string message) => model.Problem = message;

    private void UpdateOutputStatus()
    {
        if (outputs is null) return;
        AudienceStatus.Text = $"Audience: {outputs.StatusText(OutputRole.Audience)}";
        StageStatus.Text = $"Stage: {outputs.StatusText(OutputRole.Stage)}";
        AudienceDot.Fill = outputs.IsShowing(OutputRole.Audience) ? (Brush)FindResource("Live") : (Brush)FindResource("FaintText");
        StageDot.Fill = outputs.IsShowing(OutputRole.Stage) ? (Brush)FindResource("Warn") : (Brush)FindResource("FaintText");
        model.RenderOutputs(SceneTransition.Cut, refreshOnly: true);
    }

    // MARK: Keyboard (same defaults as the Mac app)

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var typing = Keyboard.FocusedElement is TextBox or ComboBox { IsEditable: true };
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        switch (key)
        {
            case Key.F1: model.ClearAllCommand.Execute(null); e.Handled = true; return;
            case Key.F2: model.ClearSlideCommand.Execute(null); e.Handled = true; return;
            case Key.F3: model.ClearMediaCommand.Execute(null); e.Handled = true; return;
            case Key.F4: model.ClearOverlaysCommand.Execute(null); e.Handled = true; return;
            case Key.F5: model.ClearAudioCommand.Execute(null); e.Handled = true; return;
            case Key.F6: model.ClearAlertsCommand.Execute(null); e.Handled = true; return;
        }

        if (ctrl)
        {
            switch (key)
            {
                case Key.N when shift: model.NewPresentationCommand.Execute(null); e.Handled = true; return;
                case Key.N: NewSong_Click(this, e); e.Handled = true; return;
                case Key.I: Import_Click(this, e); e.Handled = true; return;
                case Key.E: model.IsEditing = !model.IsEditing; e.Handled = true; return;
                case Key.F: LibrarySearchBox.Focus(); LibrarySearchBox.SelectAll(); e.Handled = true; return;
            }
        }

        // While typing, or while the canvas has focus (arrows nudge, Delete removes), keys belong there.
        if (typing || Keyboard.FocusedElement is SlideCanvasEditor)
        {
            if (key == Key.Escape && typing) Keyboard.ClearFocus();
            return;
        }

        switch (key)
        {
            case Key.Enter when model.CommitGoTo():
                e.Handled = true;
                return;
            case Key.Escape when model.GoToBuffer.Length > 0:
                model.CancelGoTo();
                e.Handled = true;
                return;
            case Key.Right when Keyboard.Modifiers == ModifierKeys.Alt:
                model.NextSkippingBuildsCommand.Execute(null);
                e.Handled = true;
                return;
            case Key.Right or Key.Space or Key.Enter or Key.PageDown or Key.Down:
                model.NextCommand.Execute(null);
                e.Handled = true;
                return;
            case Key.Left or Key.PageUp or Key.Up:
                model.PreviousCommand.Execute(null);
                e.Handled = true;
                return;
        }

        // Digits build a slide number (Enter takes it live); letters are group hot keys (C = Chorus…).
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        if (key is >= Key.D0 and <= Key.D9 or >= Key.NumPad0 and <= Key.NumPad9)
        {
            var digit = (char)('0' + (key >= Key.NumPad0 ? key - Key.NumPad0 : key - Key.D0));
            model.AppendGoToDigit(digit);
            e.Handled = true;
        }
        else if (key is >= Key.A and <= Key.Z && model.FireHotKey(((char)('a' + (key - Key.A))).ToString()))
        {
            e.Handled = true;
        }
    }

    // MARK: Slides

    private static T? ContextOf<T>(object sender) where T : class => (sender as FrameworkElement)?.DataContext as T;

    private void SlideTile_Click(object sender, MouseButtonEventArgs e)
    {
        if (ContextOf<SlideTileViewModel>(sender) is { } tile) model.SlideClickedCommand.Execute(tile);
        Keyboard.ClearFocus();
        Focus();
    }

    private void SlideTile_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (ContextOf<SlideTileViewModel>(sender) is not { } tile) return;
        model.SelectedSlide = tile;
        var menu = new ContextMenu();
        menu.Items.Add(Item("Go live", () => model.GoLiveCommand.Execute(tile)));
        menu.Items.Add(Item("Edit this slide", () => { model.IsEditing = true; model.SelectedSlide = tile; }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Add a slide after", () => model.AddSlideCommand.Execute(null)));
        menu.Items.Add(Item("Duplicate", () => model.DuplicateSlideCommand.Execute(null)));
        menu.Items.Add(Item("Delete", () => model.DeleteSlideCommand.Execute(null)));
        if (model.SelectedMedia is { } media)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item($"Use \"{media.Name}\" as this slide's background", () => model.SetMediaAsSlideBackgroundCommand.Execute(media)));
        }
        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static MenuItem Item(string header, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    private void ThumbSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        settingsSave.Stop();
        settingsSave.Start();
    }

    private void Title_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) RenamePresentation_Click(sender, e);
    }

    // MARK: Songs and presentations

    private void NewSong_Click(object sender, RoutedEventArgs e) => OpenLyricsEditor(null);

    private void EditLyrics_Click(object sender, RoutedEventArgs e)
    {
        if (model.CurrentPresentation is { } p) OpenLyricsEditor(p);
    }

    private void OpenLyricsEditor(Presentation? existing)
    {
        model.FlushPendingSave();
        var editor = new LyricsEditorWindow(model, existing) { Owner = this };
        if (editor.ShowDialog() == true && editor.Result is { } song) model.SaveSong(song, isNew: existing is null);
    }

    private const string LyricsPatterns = "*.txt;*.cho;*.chopro;*.chordpro;*.crd;*.pro";
    private const string ProPresenterPatterns = "*.pro;*.probundle;*.proPlaylist;*.protheme";

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (model.IsImporting) return;
        var dialog = new OpenFileDialog
        {
            Title = "Import",
            Multiselect = true,
            Filter = $"Everything MxU Slides can import|*.pptx;{ProPresenterPatterns};{LyricsPatterns}|ProPresenter 7|{ProPresenterPatterns}"
                + $"|PowerPoint decks|*.pptx|Lyrics files|{LyricsPatterns}|All files|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        var decks = dialog.FileNames.Where(IsPowerPoint).ToList();
        var pro = dialog.FileNames.Where(f => !IsPowerPoint(f) && MainViewModel.IsProPresenterFile(f)).ToList();
        var lyrics = dialog.FileNames.Except(decks).Except(pro).ToList();
        if (lyrics.Count > 0) model.ImportLyricsFiles(lyrics);
        if (decks.Count > 0) await model.ImportPowerPointAsync(decks);
        if (pro.Count > 0) await model.ImportProPresenterAsync(pro);

        static bool IsPowerPoint(string path) => string.Equals(Path.GetExtension(path), ".pptx", StringComparison.OrdinalIgnoreCase);
    }

    private void LibraryList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (model.CurrentPresentation is { } p && model.SelectedLibraryEntry?.IsSong == true) OpenLyricsEditor(p);
    }

    private void RenamePresentation_Click(object sender, RoutedEventArgs e)
    {
        if (model.CurrentPresentation is not { } p) return;
        if (InputDialog.Ask(this, "Rename", "Name", p.Name) is { } name) model.RenamePresentation(p, name);
    }

    private void DeletePresentation_Click(object sender, RoutedEventArgs e)
    {
        if (model.CurrentPresentation is not { } p) return;
        if (MessageBox.Show(this, $"Move \"{p.Name}\" to the library's Trash folder?", "MxU Slides",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            model.DeletePresentationCommand.Execute(null);
    }

    private void Background_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        var media = model.MediaItems.ToList();
        if (media.Count == 0) menu.Items.Add(Item("Import images or videos first (Media, below)", () => { }, enabled: false));
        foreach (var tile in media.Take(30))
            menu.Items.Add(Item($"{tile.Name}  ·  {tile.Kind}", () => model.SetMediaAsPresentationBackgroundCommand.Execute(tile)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("No background", () => model.ClearPresentationBackgroundCommand.Execute(null)));
        menu.IsOpen = true;
    }

    private void LibraryFilter_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && int.TryParse(tag, out var filter) && IsLoaded) model.LibraryFilter = filter;
    }

    // MARK: Library → service drag, service reordering

    private Point dragStart;
    private object? dragCandidate;

    private void LibraryList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        dragStart = e.GetPosition(null);
        dragCandidate = (e.OriginalSource as FrameworkElement)?.DataContext as LibraryEntryViewModel;
    }

    private void LibraryList_PreviewMouseMove(object sender, MouseEventArgs e) => MaybeDrag(e, "mxu/presentation");

    private void ServiceList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        dragStart = e.GetPosition(null);
        dragCandidate = (e.OriginalSource as FrameworkElement)?.DataContext as ServiceItemViewModel;
    }

    private void ServiceList_PreviewMouseMove(object sender, MouseEventArgs e) => MaybeDrag(e, "mxu/service-item");

    private void MaybeDrag(MouseEventArgs e, string format)
    {
        if (e.LeftButton != MouseButtonState.Pressed || dragCandidate is null) return;
        var delta = e.GetPosition(null) - dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var payload = dragCandidate switch
        {
            LibraryEntryViewModel entry when format == "mxu/presentation" => entry.Presentation.Id,
            ServiceItemViewModel row when format == "mxu/service-item" => row.Item.Id,
            _ => null,
        };
        dragCandidate = null;
        if (payload is null) return;
        DragDrop.DoDragDrop(this, new DataObject(format, payload), DragDropEffects.Copy | DragDropEffects.Move);
    }

    private void ServiceList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("mxu/presentation") ? DragDropEffects.Copy
            : e.Data.GetDataPresent("mxu/service-item") ? DragDropEffects.Move
            : e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void ServiceList_Drop(object sender, DragEventArgs e)
    {
        var target = (e.OriginalSource as FrameworkElement)?.DataContext as ServiceItemViewModel;
        if (e.Data.GetData("mxu/presentation") is string presentationId && model.Library.Get<Presentation>(presentationId) is { } p)
        {
            model.SelectedServiceItem = target;
            model.Open(p, null, null);
            model.AddCurrentToServiceCommand.Execute(null);
        }
        else if (e.Data.GetData("mxu/service-item") is string itemId && model.SelectedService is { } service)
        {
            var index = target is null ? service.Items.Count - 1 : service.Items.FindIndex(i => i.Id == target.Item.Id);
            model.MoveServiceItemTo(itemId, index);
        }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            foreach (var media in model.ImportMediaFiles(files)) model.AddMediaToService(media);
        }
    }

    private void ServiceList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is ServiceItemViewModel row) model.FireServiceItemCommand.Execute(row);
    }

    private void ServiceGoLive_Click(object sender, RoutedEventArgs e) => model.FireServiceItemCommand.Execute(model.SelectedServiceItem);

    private void ServiceItemRename_Click(object sender, RoutedEventArgs e)
    {
        if (model.SelectedServiceItem is not { } row) return;
        if (InputDialog.Ask(this, "Rename", "Name in this service", row.Name) is { } name) model.RenameServiceItem(row, name);
    }

    private void ServiceMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        var service = model.SelectedService;
        menu.Items.Add(Item("New service", () => model.NewServiceCommand.Execute(null)));
        menu.Items.Add(Item("Rename service…", () =>
        {
            if (service is not null && InputDialog.Ask(this, "Rename service", "Name", service.Name) is { } name) model.RenameService(service, name);
        }, service is not null));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Move service to Trash", () =>
        {
            if (service is not null && MessageBox.Show(this, $"Move \"{service.Name}\" to the library's Trash folder?", "MxU Slides",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                model.DeleteServiceCommand.Execute(null);
        }, service is not null));
        menu.IsOpen = true;
    }

    // MARK: Canvas editing

    /// <summary>Enter in a number box applies it straight away.</summary>
    private void NumberBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }

    private void FocusInspectorText()
    {
        var box = FindDescendant<TextBox>(this, t => t.Name == "InspectorText" && t.IsVisible);
        if (box is null) return;
        box.Focus();
        box.SelectAll();
    }

    private static T? FindDescendant<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t && match(t)) return t;
            if (FindDescendant(child, match) is { } found) return found;
        }
        return null;
    }

    // MARK: Overlays

    private void OverlayTile_Click(object sender, MouseButtonEventArgs e)
    {
        if (ContextOf<OverlayTileViewModel>(sender) is { } tile) model.ToggleOverlayCommand.Execute(tile);
    }

    // MARK: Media

    private void InputTile_Click(object sender, MouseButtonEventArgs e)
    {
        if (ContextOf<InputTileViewModel>(sender) is { } tile) model.InputClickedCommand.Execute(tile);
    }

    private void VideoInputs_Click(object sender, RoutedEventArgs e)
    {
        new VideoInputsWindow(model.Inputs) { Owner = this }.ShowDialog();
        model.RefreshInputs();
    }

    private void AudioTile_Click(object sender, MouseButtonEventArgs e)
    {
        if (ContextOf<AudioTileViewModel>(sender) is { } tile) model.AudioTileClickedCommand.Execute(tile);
    }

    private void ImportAudio_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import music",
            Multiselect = true,
            Filter = "Music|*.mp3;*.m4a;*.aac;*.wav;*.flac;*.wma;*.aiff;*.aif|All files|*.*",
        };
        if (dialog.ShowDialog(this) == true) model.ImportAudioFiles(dialog.FileNames);
    }

    private void MediaTile_Click(object sender, MouseButtonEventArgs e)
    {
        if (ContextOf<MediaTileViewModel>(sender) is { } tile) model.MediaClickedCommand.Execute(tile);
    }

    private void MediaShow_Click(object sender, RoutedEventArgs e)
    {
        if (ContextOf<MediaTileViewModel>(sender) is { } tile) model.FireMedia(tile.Item);
    }

    private void MediaAsSongBackground_Click(object sender, RoutedEventArgs e) =>
        model.SetMediaAsPresentationBackgroundCommand.Execute(ContextOf<MediaTileViewModel>(sender));

    private void MediaAsSlideBackground_Click(object sender, RoutedEventArgs e) =>
        model.SetMediaAsSlideBackgroundCommand.Execute(ContextOf<MediaTileViewModel>(sender));

    private void MediaAddToService_Click(object sender, RoutedEventArgs e) =>
        model.AddMediaTileToServiceCommand.Execute(ContextOf<MediaTileViewModel>(sender));

    private void MediaToggleLoop_Click(object sender, RoutedEventArgs e) =>
        model.ToggleMediaLoopCommand.Execute(ContextOf<MediaTileViewModel>(sender));

    private void MediaToggleKind_Click(object sender, RoutedEventArgs e) =>
        model.ToggleMediaClassificationCommand.Execute(ContextOf<MediaTileViewModel>(sender));

    private void MediaRename_Click(object sender, RoutedEventArgs e)
    {
        if (ContextOf<MediaTileViewModel>(sender) is { } tile && InputDialog.Ask(this, "Rename", "Name", tile.Name) is { } name)
            model.RenameMedia(tile, name);
    }

    private void MediaDelete_Click(object sender, RoutedEventArgs e) =>
        model.DeleteMediaCommand.Execute(ContextOf<MediaTileViewModel>(sender));

    private void MediaBin_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void MediaBin_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) model.ImportMediaFiles(files);
    }

    // MARK: Timers and messages

    private void TimerName_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || ContextOf<TimerRowViewModel>(sender) is not { } row) return;
        var answer = InputDialog.Ask(this, "Timer",
            "Name, then a length in minutes or a time to count down to. For example:  Sermon, 35   or   Service starts, 10:30",
            $"{row.Name}, ");
        if (answer is null) return;
        var parts = answer.Split(',', 2, StringSplitOptions.TrimEntries);
        if (parts[0].Length > 0) model.RenameTimer(row, parts[0]);
        if (parts.Length < 2 || parts[1].Length == 0) return;
        if (TimeOnly.TryParse(parts[1], out var time) && parts[1].Contains(':'))
            model.SetTimerUntil(row, time.Hour, time.Minute);
        else if (double.TryParse(parts[1], out var minutes))
            model.SetTimerMinutes(row, minutes);
    }

    private void AlertText_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        model.ShowAlertCommand.Execute(null);
        e.Handled = true;
    }

    // MARK: Outputs and settings

    private void AudienceOutput_Click(object sender, RoutedEventArgs e) => ToggleOutput(OutputRole.Audience);

    private void StageOutput_Click(object sender, RoutedEventArgs e) => ToggleOutput(OutputRole.Stage);

    private void ToggleOutput(OutputRole role)
    {
        if (outputs is null) return;
        outputs.Toggle(role);
        model.RenderOutputs(SceneTransition.Cut);
        UpdateOutputStatus();
        Activate();
    }

    private void Broadcast_Click(object sender, RoutedEventArgs e) => new BroadcastWindow(model) { Owner = this }.Show();

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (outputs is null) return;
        new SettingsWindow(model, outputs) { Owner = this }.ShowDialog();
        model.Media.VideoVolume = model.Settings.VideoVolume;
        model.Media.ApplyVolume();
        model.RenderOutputs(SceneTransition.Cut);
        UpdateOutputStatus();
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => OpenFolder(AppPaths.LogDirectory);

    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open {path}: {ex.Message}");
        }
    }
}
