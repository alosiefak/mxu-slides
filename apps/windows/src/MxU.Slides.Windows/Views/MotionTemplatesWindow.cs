using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.MotionTemplates;
using MxU.Slides.MotionTemplates.Export;
using MxU.Slides.MotionTemplates.Lottie;
using MxU.Slides.Windows.Rendering;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// Overlays › Motion templates…: the library of animated templates (Lottie files from After
/// Effects or LottieFiles, and the starters), each a tile that plays when you point at it. Pick
/// one and, on the right, it plays through (In, Hold, Out) over a stand-in camera shot; fill in
/// its fields (words, colours, pictures), choose how it plays and what it's for, then add it to
/// the library or put it straight up. <b>Export MP4…</b> renders it to a video file.
/// <para>
/// Opened on a use already in the library (the bin's "Edit motion template…"), the fields change
/// that use, and the program too if it's up.
/// </para>
/// Built in code, like the other editors (EditorKit). The work is MainViewModel.MotionTemplates.cs's.
/// Windows only: the Mac has no Lottie support.
/// </summary>
internal sealed class MotionTemplatesWindow : Window
{
    private const int PreviewWidth = 362;
    private const int PreviewHeight = 204;
    private const int TileWidth = 208;
    private const int TileHeight = 117;

    private readonly MainViewModel model;
    private readonly Overlay? editing;
    private readonly MediaItem? editingMedia;
    private readonly MotionPreviews previews;
    private readonly MotionPreviews.Preview preview;
    private readonly Dictionary<string, (Border Frame, MotionPreviews.Preview Preview, Image Still)> tiles = [];
    private readonly WrapPanel tileGrid = new();
    private readonly StackPanel filters = new() { Orientation = Orientation.Horizontal };
    private readonly Border previewFrame = new() { Width = PreviewWidth, Height = PreviewHeight, CornerRadius = new CornerRadius(6), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock title = new() { FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock subtitle = new() { FontSize = 11, Margin = new Thickness(0, 2, 0, 4), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock description = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 4) };
    private readonly StackPanel fields = new();
    private readonly ComboBox playbackBox = new() { MinWidth = 220 };
    private readonly ToggleButton asOverlay = new() { Content = "Lower third over the slides", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0) };
    private readonly ToggleButton asBackground = new() { Content = "Full-screen background", Padding = new Thickness(10, 4, 10, 4) };
    private readonly TextBox nameBox = new() { VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock notice = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock status = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ProgressBar progress = new() { Height = 6, Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 3, 12, 0) };
    private readonly Button add;
    private readonly Button live;
    private readonly Button export;
    private readonly Button cancel;
    private readonly StackPanel useRow = new() { Orientation = Orientation.Horizontal };
    private readonly DockPanel nameRow;
    private string filter = "All";
    private MotionTemplateInfo? template;
    private LottieDocument? lottie;
    private Dictionary<string, string> values = [];
    private MotionPlayback playback = MotionPlayback.Loop;
    private MotionUse use = MotionUse.Overlay;
    private bool nameTyped;
    private bool updating;
    private MotionTemplateJob? job;
    private Overlay? added;

    private MotionTemplatesWindow(Window? owner, MainViewModel model, Overlay? editing, MediaItem? editingMedia)
    {
        this.model = model;
        this.editing = editing;
        this.editingMedia = editingMedia;
        var editingName = editing?.Name ?? editingMedia?.Name;
        EditorKit.Dress(this, owner, editingName is null ? "Motion templates" : $"Edit “{editingName}”", 1240, 820);
        previews = new MotionPreviews(model.MotionFonts);
        preview = previews.Add(PreviewWidth, PreviewHeight);
        add = EditorKit.Button("Add to library", () => _ = AddAsync(goLive: false), "Adds it, as filled in: a lower third goes in the Overlays bin, a background in the Media bin", accent: true);
        live = EditorKit.Button("Add and go live", () => _ = AddAsync(goLive: true), "Adds it and puts it on the program");
        export = EditorKit.Button("Export MP4…", ExportDialog, "Renders it, as filled in, to an H.264 video: a background, or a lower third over a colour for a vision mixer");
        cancel = EditorKit.Button("Cancel", () => job?.Cancel(), "Stops the render; nothing is added");
        cancel.Visibility = Visibility.Collapsed;
        nameRow = EditorKit.Row("Name", nameBox, 70);
        Content = Layout();
        AllowDrop = true;
        Drop += OnDrop;
        model.MotionTemplatesChanged += OnCatalogueChanged;
        model.LiveOverlaysChanged += RefreshButtons;
        Closed += (_, _) =>
        {
            model.MotionTemplatesChanged -= OnCatalogueChanged;
            model.LiveOverlaysChanged -= RefreshButtons;
            previews.Dispose();
            _ = model.FlushMotionUsesAsync();
        };
        nameBox.TextChanged += (_, _) =>
        {
            if (!updating) nameTyped = true;
        };
        asOverlay.Click += (_, _) => UseAs(MotionUse.Overlay);
        asBackground.Click += (_, _) => UseAs(MotionUse.Background);
        playbackBox.SelectionChanged += (_, _) =>
        {
            if (!updating && playbackBox.SelectedItem is ComboBoxItem { Tag: MotionPlayback chosen }) SetPlayback(chosen);
        };

        RebuildFilters();
        RebuildTiles();
        if (editing is not null || editingMedia is not null) OpenUse();
        else if (model.MotionStore.All().FirstOrDefault() is { } first) Pick(first.Id);
        if (model.MotionTemplateJob is { } running) _ = Follow(running);
    }

    /// <summary>Opens the window (or brings an open one forward); on a use, to change it.</summary>
    public static MotionTemplatesWindow Open(Window owner, MainViewModel model, Overlay? editing = null, MediaItem? editingMedia = null)
    {
        var window = new MotionTemplatesWindow(owner, model, editing, editingMedia);
        window.Show();
        return window;
    }

    // MARK: For the self-test

    public MotionTemplateInfo? Picked => template;

    public IReadOnlyDictionary<string, string> Values => values;

    public MotionPreviews.Preview Preview => preview;

    public MotionPreviews.Preview? TilePreview(string id) => tiles.TryGetValue(id, out var tile) ? tile.Preview : null;

    public string Notice => notice.Text;

    public MotionPlayback Playback => playback;

    public MotionUse Use => use;

    /// <summary>Points at a tile, as the mouse does: it plays.</summary>
    public void Hover(string id, bool over)
    {
        if (!tiles.TryGetValue(id, out var tile) || model.MotionStore.Get(id) is not { } info || model.MotionStore.Lottie(info.LottieHash) is not { } document) return;
        if (over)
        {
            tile.Preview.Image.Visibility = Visibility.Visible;
            previews.Play(tile.Preview, document, MotionTimeline.For(document, MotionPlayback.Loop));
        }
        else
        {
            previews.Stop(tile.Preview);
            tile.Preview.Image.Visibility = Visibility.Hidden;
        }
    }

    // MARK: Layout

    private UIElement Layout()
    {
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(EditorKit.Button("Import Lottie…", ImportDialog, "Adds Lottie animations (.json from Bodymovin, or .lottie from LottieFiles) to the library", accent: editing is null && editingMedia is null));
        buttons.Children.Add(EditorKit.Button("After Effects…", () => AfterEffectsWindow.Open(this, model), "Render a composition with After Effects, changing its text and colours first"));
        buttons.Children.Add(EditorKit.Button("How to make one…", () => LottieGuideWindow.Open(this), "Exporting a Lottie from After Effects, so it plays live with transparency"));
        DockPanel.SetDock(buttons, Dock.Right);
        top.Children.Add(buttons);
        top.Children.Add(filters);

        var middle = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        middle.Children.Add(top);
        var hint = EditorKit.Muted("Point at a template to play it; click to pick it. Drop .json or .lottie files here to import them.");
        DockPanel.SetDock(hint, Dock.Bottom);
        middle.Children.Add(hint);
        middle.Children.Add(new ScrollViewer { Content = tileGrid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });

        var right = new StackPanel();
        preview.Image.Width = PreviewWidth;
        preview.Image.Height = PreviewHeight;
        previewFrame.Child = preview.Image;
        right.Children.Add(previewFrame);
        title.Foreground = EditorKit.Res<Brush>("Text");
        subtitle.Foreground = EditorKit.Res<Brush>("MutedText");
        description.Foreground = EditorKit.Res<Brush>("MutedText");
        notice.Foreground = EditorKit.Res<Brush>("Accent");
        right.Children.Add(title);
        right.Children.Add(subtitle);
        right.Children.Add(description);
        right.Children.Add(EditorKit.Section("FIELDS", top: 4));
        right.Children.Add(fields);
        right.Children.Add(EditorKit.Section("PLAYS"));
        right.Children.Add(playbackBox);
        var useSection = EditorKit.Section("USE AS");
        right.Children.Add(useSection);
        useRow.Children.Add(asOverlay);
        useRow.Children.Add(asBackground);
        right.Children.Add(useRow);
        right.Children.Add(EditorKit.Section("ADD"));
        right.Children.Add(nameRow);
        var actions = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        actions.Children.Add(add);
        actions.Children.Add(live);
        actions.Children.Add(export);
        foreach (var b in new[] { add, live, export }) b.Margin = new Thickness(0, 0, 6, 6);
        right.Children.Add(actions);
        right.Children.Add(notice);
        if (editing is not null || editingMedia is not null)
        {
            useSection.Visibility = Visibility.Collapsed;
            useRow.Visibility = Visibility.Collapsed;
            nameRow.Visibility = Visibility.Collapsed;
            add.Visibility = Visibility.Collapsed;
            live.Content = editingMedia is not null ? "Show it now" : "Go live";
            live.ToolTip = editingMedia is not null ? "Puts it behind whatever is live" : "Puts it up on the program, or takes it down";
        }
        var scroller = new ScrollViewer { Content = right, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 4, 0) };

        var grid = new Grid { Margin = new Thickness(16, 14, 16, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(380) });
        grid.Children.Add(middle);
        Grid.SetColumn(scroller, 2);
        grid.Children.Add(scroller);

        var footer = new DockPanel { Margin = new Thickness(16, 10, 16, 14) };
        var done = EditorKit.Button("Done", Close, accent: false);
        done.IsCancel = true;
        done.MinWidth = 90;
        var footerButtons = new StackPanel { Orientation = Orientation.Horizontal };
        footerButtons.Children.Add(cancel);
        footerButtons.Children.Add(done);
        DockPanel.SetDock(footerButtons, Dock.Right);
        footer.Children.Add(footerButtons);
        var progressColumn = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        progressColumn.Children.Add(status);
        progressColumn.Children.Add(progress);
        footer.Children.Add(progressColumn);

        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(grid);
        return root;
    }

    private void RebuildFilters()
    {
        filters.Children.Clear();
        foreach (var name in new[] { "All", "Lower thirds", "Backgrounds" })
        {
            var button = new ToggleButton { Content = name, IsChecked = filter == name, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0) };
            button.Click += (_, _) =>
            {
                filter = name;
                RebuildFilters();
                RebuildTiles();
            };
            filters.Children.Add(button);
        }
    }

    private void OnCatalogueChanged()
    {
        RebuildTiles();
        if (editing is not null || editingMedia is not null) return;
        // The picked template was removed (or none was picked yet, before the starters arrived): pick the first.
        if ((template is null || model.MotionStore.Get(template.Id) is null) && model.MotionStore.All().FirstOrDefault() is { } first) Pick(first.Id);
    }

    private void RebuildTiles()
    {
        foreach (var (_, tile) in tiles) previews.Stop(tile.Preview);
        tiles.Clear();
        tileGrid.Children.Clear();
        foreach (var info in model.MotionStore.All().Where(t => filter switch
                 {
                     "Lower thirds" => t.SuggestedUse == MotionUse.Overlay,
                     "Backgrounds" => t.SuggestedUse == MotionUse.Background,
                     _ => true,
                 }))
        {
            var still = new Image { Stretch = Stretch.Uniform, Width = TileWidth, Height = TileHeight };
            var moving = previews.Add(TileWidth, TileHeight);
            moving.Image.Width = TileWidth;
            moving.Image.Height = TileHeight;
            moving.Image.Visibility = Visibility.Hidden;
            var picture = new Grid { Width = TileWidth, Height = TileHeight };
            if (info.SuggestedUse == MotionUse.Overlay)
                picture.Children.Add(new Image { Source = StandInPictures.Stage, Stretch = Stretch.UniformToFill, Opacity = 0.8 });
            picture.Children.Add(still);
            picture.Children.Add(moving.Image);
            var label = new StackPanel { Margin = new Thickness(6, 4, 6, 4) };
            label.Children.Add(new TextBlock { Text = info.Name, FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = EditorKit.Res<Brush>("Text") });
            label.Children.Add(EditorKit.Faint((info.Starter ? "Starter · " : "") + (info.SuggestedUse == MotionUse.Background ? "Background" : "Lower third")
                + $" · {info.Seconds:0.#} s"));
            var stack = new StackPanel();
            stack.Children.Add(new Border { Child = picture, Background = Brushes.Black, CornerRadius = new CornerRadius(4, 4, 0, 0), ClipToBounds = true });
            stack.Children.Add(label);
            var frame = new Border
            {
                Child = stack, Width = TileWidth + 4, Margin = new Thickness(0, 0, 10, 10), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent, Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)), Cursor = Cursors.Hand, ToolTip = info.Description ?? info.SourceFileName,
            };
            var id = info.Id;
            frame.MouseEnter += (_, _) => Hover(id, over: true);
            frame.MouseLeave += (_, _) => Hover(id, over: false);
            frame.MouseLeftButtonUp += (_, _) => Pick(id);
            if (!info.Starter)
            {
                var menu = new ContextMenu();
                menu.Items.Add(EditorKit.Command("Rename…", () =>
                {
                    if (InputDialog.Ask(this, "Rename template", "A name for this template:", info.Name) is { } renamed) model.MotionStore.Rename(id, renamed);
                    OnCatalogueChanged();
                }));
                menu.Items.Add(EditorKit.Command("Remove from the library", () => model.RemoveMotionTemplate(id)));
                frame.ContextMenu = menu;
            }
            tiles[id] = (frame, moving, still);
            tileGrid.Children.Add(frame);
            _ = LoadStill(info, still);
        }
        MarkPicked();
    }

    /// <summary>Draws a tile's still (the settled design) off the UI thread.</summary>
    private async Task LoadStill(MotionTemplateInfo info, Image target)
    {
        var bytes = await Task.Factory.StartNew(() =>
        {
            if (model.MotionStore.Lottie(info.LottieHash) is not { } document) return null;
            try { return LottieStills.Png(document, MotionTimeline.For(document, MotionPlayback.Loop).SettledFrame, TileWidth * 2, TileHeight * 2, model.MotionFonts); }
            catch (LottieFormatException) { return null; }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        if (bytes is null) return;
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = new MemoryStream(bytes);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        target.Source = image;
    }

    private void MarkPicked()
    {
        foreach (var (id, tile) in tiles) tile.Frame.BorderBrush = template?.Id == id ? EditorKit.Res<Brush>("Accent") : Brushes.Transparent;
    }

    // MARK: Picking and filling in

    /// <summary>Picks a template: its preview, its fields (at their defaults), how it plays and what it's for.</summary>
    public void Pick(string templateId)
    {
        if (model.MotionStore.Get(templateId) is not { } info || model.MotionStore.Lottie(info.LottieHash) is not { } document) return;
        template = info;
        lottie = document;
        values = [];
        var segments = MotionTimeline.SegmentsOf(document);
        use = info.SuggestedUse;
        playback = segments.HasMarkers ? MotionPlayback.Loop : use == MotionUse.Background ? MotionPlayback.Loop : MotionPlayback.HoldLast;
        added = null;
        ShowTemplate();
        notice.Text = "";
    }

    /// <summary>Opens on the use being edited: its template, values and playback.</summary>
    private void OpenUse()
    {
        var current = editing is not null ? model.MotionUseOf(editing) : editingMedia is not null ? model.MotionUseOf(editingMedia) : null;
        if (current is null || model.MotionStore.Lottie(current.LottieHash) is not { } document)
        {
            notice.Text = "This isn't a motion template any more (it may have been changed on a Mac), so it shows as it is.";
            return;
        }
        lottie = document;
        template = model.MotionStore.Get(current.TemplateId) ?? new MotionTemplateInfo
        {
            Id = current.TemplateId ?? "", Name = document.Name, LottieHash = current.LottieHash, Width = document.Width, Height = document.Height,
            FrameRate = document.FrameRate, Seconds = document.Seconds, SuggestedUse = current.Use,
        };
        values = new(current.Values);
        playback = current.Playback;
        use = current.Use;
        ShowTemplate();
    }

    private void ShowTemplate()
    {
        if (template is null || lottie is null) return;
        updating = true;
        try
        {
            title.Text = editing?.Name ?? editingMedia?.Name ?? template.Name;
            var segments = MotionTimeline.SegmentsOf(lottie);
            var fieldList = MotionFields.Discover(lottie);
            subtitle.Text = $"{lottie.Width:0} × {lottie.Height:0} · {lottie.FrameRate:0.##} fps · {lottie.Seconds:0.#} s · "
                            + (segments.HasMarkers ? (segments.HasOut ? "In, Hold and Out markers" : "In and Hold markers") : "no markers: plays as one piece")
                            + $" · {fieldList.Count} field{(fieldList.Count == 1 ? "" : "s")}";
            description.Text = template.Description ?? (template.SourceFileName is { } file ? $"Imported from {file}." : "");
            if (!nameTyped) nameBox.Text = SuggestedName();
            RebuildFields(fieldList);
            RebuildPlayback(segments);
            asOverlay.IsChecked = use == MotionUse.Overlay;
            asBackground.IsChecked = use == MotionUse.Background;
            previewFrame.Background = use == MotionUse.Overlay ? StageBrush : Brushes.Black;
        }
        finally
        {
            updating = false;
        }
        MarkPicked();
        RefreshButtons();
        PlayPreview();
    }

    private static readonly Brush StageBrush = Frozen(new ImageBrush(StandInPictures.Stage) { Stretch = Stretch.UniformToFill });

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    /// <summary>A name for the new use: its first words ("Pastor Grace Okafor"), else the template's name.</summary>
    private string SuggestedName()
    {
        if (lottie is null || template is null) return "";
        var first = MotionFields.Discover(lottie).FirstOrDefault(f => f.Kind == MotionFieldKind.Text);
        var words = first is null ? null : values.TryGetValue(first.Id, out var typed) ? typed : first.Default;
        words = words?.Split('\n')[0].Trim();
        return words is { Length: > 0 } ? $"{template.Name} · {words}" : template.Name;
    }

    private void RebuildFields(IReadOnlyList<MotionField> list)
    {
        fields.Children.Clear();
        if (list.Count == 0) fields.Children.Add(EditorKit.Muted("This template has no text, colours or pictures to change."));
        foreach (var field in list)
        {
            var value = values.TryGetValue(field.Id, out var v) ? v : field.Default;
            UIElement control;
            switch (field.Kind)
            {
                case MotionFieldKind.Text:
                    var box = new TextBox { Text = value, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 76, ToolTip = field.Where };
                    box.TextChanged += (_, _) =>
                    {
                        if (!updating) SetValue(field.Id, box.Text.Replace("\r\n", "\n"));
                    };
                    control = box;
                    break;
                case MotionFieldKind.Color:
                    var picker = new ColorPicker { Hex = value + "FF", ToolTip = $"Used in {field.Where}" };
                    DependencyPropertyDescriptor.FromProperty(ColorPicker.HexProperty, typeof(ColorPicker)).AddValueChanged(picker, (_, _) =>
                    {
                        if (!updating && MotionFields.NormalHex(picker.Hex) is { } hex) SetValue(field.Id, hex);
                    });
                    control = picker;
                    break;
                default:
                    var row = new DockPanel();
                    var label = EditorKit.Faint(value.Length == 0 ? "The template's own picture" : model.Library.Get<MediaItem>(value)?.Name ?? "A picture");
                    var choose = EditorKit.Button("Choose…", () => _ = ChoosePicture(field.Id, label), "Use a picture of your own (it's added to the media library)");
                    DockPanel.SetDock(choose, Dock.Right);
                    row.Children.Add(choose);
                    row.Children.Add(label);
                    control = row;
                    break;
            }
            fields.Children.Add(EditorKit.Row(field.Label, control, 120));
        }
    }

    private async Task ChoosePicture(string fieldId, TextBlock label)
    {
        if (LibraryDialogs.ChooseMediaFile("Choose a picture for the template") is not { } path || LibraryStore.MediaKindOf(path) != MediaKind.Image) return;
        var item = await Task.Factory.StartNew(() => MediaImporting.AddMedia(model.Library, path, null, MediaClassification.Foreground),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        if (item is null)
        {
            notice.Text = "That picture couldn't be read.";
            return;
        }
        item.Folder = MainViewModel.MotionStillsFolder;
        model.Library.Save(item);
        label.Text = item.Name;
        SetValue(fieldId, item.Id);
    }

    private void RebuildPlayback(MotionSegments segments)
    {
        playbackBox.Items.Clear();
        (MotionPlayback Playback, string Text)[] choices = segments.HasMarkers
            ? [(MotionPlayback.Loop, "In, then hold (looping the Hold) until taken down"), (MotionPlayback.HoldLast, "In, then stay on the end of the Hold"),
               (MotionPlayback.Once, "In, Hold, Out once, then take itself down")]
            : [(MotionPlayback.Loop, "Loop until taken down"), (MotionPlayback.HoldLast, "Play once and stay on the last frame"),
               (MotionPlayback.Once, "Play once, then take itself down")];
        foreach (var (choice, text) in choices)
            playbackBox.Items.Add(new ComboBoxItem { Content = text, Tag = choice, IsSelected = choice == playback });
    }

    /// <summary>Changes one field, as typing into it does: the preview (and a use being edited, live) follow at once.</summary>
    public void SetValue(string fieldId, string value)
    {
        if (lottie is null || template is null) return;
        var field = MotionFields.Discover(lottie).FirstOrDefault(f => f.Id == fieldId);
        if (field is null) return;
        if (value == field.Default && field.Kind != MotionFieldKind.Image) values.Remove(fieldId);
        else values[fieldId] = value;
        if (!nameTyped)
        {
            updating = true;
            nameBox.Text = SuggestedName();
            updating = false;
        }
        var filled = model.FilledIn(template.LottieHash, lottie, values);
        previews.Update(preview, filled);
        ChangeUse();
    }

    public void SetPlayback(MotionPlayback chosen)
    {
        playback = chosen;
        ChangeUse();
        PlayPreview();
    }

    public void UseAs(MotionUse chosen)
    {
        use = chosen;
        asOverlay.IsChecked = use == MotionUse.Overlay;
        asBackground.IsChecked = use == MotionUse.Background;
        previewFrame.Background = use == MotionUse.Overlay ? StageBrush : Brushes.Black;
        RefreshButtons();
    }

    /// <summary>Editing a use: the new values or playback go to it (and to the program if it's up).</summary>
    private void ChangeUse()
    {
        if (editing is not null && model.MotionUseOf(editing) is { } current)
            model.ChangeMotionUse(editing, current with { Values = new(values), Playback = playback });
        else if (editingMedia is not null && model.MotionUseOf(editingMedia) is { } currentMedia)
            model.ChangeMotionUse(editingMedia, currentMedia with { Values = new(values), Playback = playback });
    }

    private void PlayPreview()
    {
        if (lottie is null || template is null) return;
        previews.Play(preview, model.FilledIn(template.LottieHash, lottie, values), MotionTimeline.For(lottie, playback));
    }

    // MARK: Adding, going live, exporting

    private void RefreshButtons()
    {
        var busy = job is not null;
        add.IsEnabled = template is not null && !busy;
        export.IsEnabled = template is not null && !busy;
        if (editing is not null)
        {
            live.Content = model.IsOverlayLive(editing.Id) ? "Take down" : "Go live";
            live.IsEnabled = true;
        }
        else if (editingMedia is null)
        {
            live.Content = use == MotionUse.Overlay ? "Add and go live" : "Add and show it";
            live.IsEnabled = template is not null && !busy;
        }
    }

    /// <summary>Adds the template as filled in (a lower third or a background), and with <paramref name="goLive"/> puts it up. Returns what was added.</summary>
    public async Task<object?> AddAsync(bool goLive)
    {
        if (editing is not null)
        {
            await model.FlushMotionUsesAsync();
            model.SetMotionOverlayLive(editing, !model.IsOverlayLive(editing.Id));
            RefreshButtons();
            return editing;
        }
        if (editingMedia is not null)
        {
            model.FireMedia(editingMedia);
            return editingMedia;
        }
        if (template is null) return null;
        var name = nameBox.Text.Trim() is { Length: > 0 } typed ? typed : SuggestedName();
        if (use == MotionUse.Overlay)
        {
            add.IsEnabled = false;
            var overlay = await model.AddMotionOverlayAsync(template, new(values), playback, name);
            RefreshButtons();
            if (overlay is null) return null;
            added = overlay;
            if (goLive) model.SetMotionOverlayLive(overlay, up: true);
            notice.Text = goLive ? $"“{overlay.Name}” is up. Its fields are in the Overlays bin; type in them to change it live." : $"Added “{overlay.Name}” to the Overlays bin.";
            return overlay;
        }
        var started = model.AddMotionBackground(template, new(values), playback, name, model.Settings.MotionExportColor ?? "#000000", BackgroundSizeOverride);
        if (started is null)
        {
            notice.Text = "Another render is going on; try again when it has finished.";
            return null;
        }
        var item = await Follow(started);
        if (item is not null && goLive) model.FireMedia(item);
        if (item is not null) notice.Text = goLive ? $"“{item.Name}” is up as the background." : $"Added “{item.Name}” to the Media bin.";
        return item;
    }

    /// <summary>The self-test makes small background videos.</summary>
    public (int Width, int Height)? BackgroundSizeOverride { get; set; }

    /// <summary>Shows a job's progress in the footer until it's done; returns what it made.</summary>
    private async Task<MediaItem?> Follow(MotionTemplateJob running)
    {
        job = running;
        RefreshButtons();
        cancel.Visibility = Visibility.Visible;
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
            var made = await running.Done;
            status.Text = running.Succeeded ? "Done." : running.IsCancelled ? "Stopped. Nothing was added." : $"Couldn't do it: {running.Failure}";
            return made;
        }
        finally
        {
            running.Progressed -= Show;
            job = null;
            cancel.Visibility = Visibility.Collapsed;
            progress.Visibility = Visibility.Collapsed;
            RefreshButtons();
        }
    }

    /// <summary>Starts rendering the template, as filled in, to an MP4 (the self-test calls this directly). Null when another render is running.</summary>
    public MotionTemplateJob? Export(MotionExportOptions options, bool addToMedia, bool background, string? copyTo, string? name = null)
    {
        if (template is null || lottie is null) return null;
        var filled = model.FilledIn(template.LottieHash, lottie, values);
        var started = model.ExportMotion(name ?? (nameBox.Text.Trim() is { Length: > 0 } n ? n : SuggestedName()), filled, MotionTimeline.For(lottie, playback),
            options, copyTo, addToMedia, background);
        if (started is not null) _ = Follow(started);
        return started;
    }

    private void ExportDialog()
    {
        if (template is null || lottie is null) return;
        if (MotionExportDialog.Ask(this, model, MotionTimeline.SegmentsOf(lottie).HasMarkers, use) is not { } answer) return;
        model.Settings.MotionExportColor = answer.Options.BackgroundColor;
        model.Settings.Save();
        if (Export(answer.Options, answer.AddToMedia, answer.Background, answer.CopyTo) is null)
            notice.Text = "Another render is going on; try again when it has finished.";
    }

    // MARK: Importing

    private void ImportDialog()
    {
        var dialog = new OpenFileDialog { Title = "Import Lottie animations", Filter = "Lottie animations|*.json;*.lottie|All files|*.*", Multiselect = true };
        if (dialog.ShowDialog(this) == true) _ = ImportAsync(dialog.FileNames);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Where(LottieFiles.IsLottieFile).ToList() is { Count: > 0 } lotties)
            _ = ImportAsync(lotties);
    }

    /// <summary>Imports Lottie files and picks the first one imported.</summary>
    public async Task<IReadOnlyList<MotionTemplateInfo>> ImportAsync(IEnumerable<string> paths)
    {
        var results = await model.ImportMotionTemplatesAsync(paths);
        var imported = results.Select(r => r.Template).OfType<MotionTemplateInfo>().ToList();
        RebuildTiles();
        if (imported.FirstOrDefault() is { } first && editing is null && editingMedia is null) Pick(first.Id);
        notice.Text = results.FirstOrDefault(r => r.Problem is not null) is { Problem: { } problem, File: var file }
            ? $"Couldn't import {Path.GetFileName(file)}: {problem}"
            : imported.Count > 0 ? $"Imported {string.Join(", ", imported.Select(t => $"“{t.Name}”"))}." : "";
        return imported;
    }
}

/// <summary>Export MP4…: the video's size, frame rate, the colour behind it, how long it holds, and where it goes.</summary>
internal static class MotionExportDialog
{
    public sealed record Answer(MotionExportOptions Options, bool AddToMedia, bool Background, string? CopyTo);

    public static Answer? Ask(Window owner, MainViewModel model, bool hasMarkers, MotionUse use)
    {
        var window = new Window();
        EditorKit.Dress(window, owner, "Export MP4", 460, double.NaN);
        window.SizeToContent = SizeToContent.Height;
        window.ResizeMode = ResizeMode.NoResize;
        var size = new ComboBox { ItemsSource = new[] { "1920 × 1080", "1280 × 720", "3840 × 2160" }, SelectedIndex = 0 };
        var rate = new ComboBox { ItemsSource = new[] { "30 fps", "60 fps" }, SelectedIndex = 0 };
        var colour = new ColorPicker { Hex = (model.Settings.MotionExportColor ?? (use == MotionUse.Overlay ? MotionExportOptions.KeyGreen : "#000000")) + "FF" };
        var colours = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (name, hex) in new[] { ("Black", "#000000"), ("Key green", MotionExportOptions.KeyGreen), ("Key blue", "#0047BB") })
            colours.Children.Add(EditorKit.Button(name, () => colour.Hex = hex + "FF", $"Behind it: {hex}"));
        var hold = new TextBox { Text = "5", Width = 60, HorizontalAlignment = HorizontalAlignment.Left };
        var toMedia = new CheckBox { Content = "Add it to the Media bin", IsChecked = true, Margin = new Thickness(0, 4, 0, 2) };
        var asBackground = new CheckBox { Content = "…as a looping background", IsChecked = use == MotionUse.Background, Margin = new Thickness(18, 0, 0, 2) };
        var toFile = new CheckBox { Content = "Save a copy as a file…", IsChecked = false, Margin = new Thickness(0, 2, 0, 2) };

        var form = new StackPanel { Margin = new Thickness(16) };
        form.Children.Add(EditorKit.Muted("H.264 has no transparency, so the template is drawn over a colour: black for a background, or a key colour for a vision mixer to key out."));
        form.Children.Add(EditorKit.Row("Size", size, 110));
        form.Children.Add(EditorKit.Row("Frame rate", rate, 110));
        form.Children.Add(EditorKit.Row("Behind it", colour, 110));
        form.Children.Add(new Border { Child = colours, Margin = new Thickness(110, 0, 0, 8) });
        if (hasMarkers) form.Children.Add(EditorKit.Row("Hold for (s)", hold, 110));
        form.Children.Add(toMedia);
        form.Children.Add(asBackground);
        form.Children.Add(toFile);
        Answer? answer = null;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = EditorKit.Button("Cancel", window.Close);
        cancel.IsCancel = true;
        var ok = EditorKit.Button("Render", () =>
        {
            var (w, h) = size.SelectedIndex switch { 1 => (1280, 720), 2 => (3840, 2160), _ => (1920, 1080) };
            string? copyTo = null;
            if (toFile.IsChecked == true)
            {
                var save = new SaveFileDialog { Title = "Save the video as", Filter = "MP4 video|*.mp4", FileName = "Motion template.mp4" };
                if (save.ShowDialog(window) != true) return;
                copyTo = save.FileName;
            }
            if (toMedia.IsChecked != true && copyTo is null) return;
            var holdSeconds = double.TryParse(hold.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var s) ? s : 5;
            answer = new Answer(new MotionExportOptions(w, h, rate.SelectedIndex == 1 ? 60 : 30, MotionFields.NormalHex(colour.Hex) ?? "#000000", holdSeconds).Normalized(),
                toMedia.IsChecked == true, asBackground.IsChecked == true, copyTo);
            window.DialogResult = true;
        }, accent: true);
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        form.Children.Add(buttons);
        window.Content = form;
        window.ShowDialog();
        return answer;
    }
}
