using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.Rendering;
using MxU.Slides.Windows.ViewModels;
using MxU.Slides.Windows.Views.Transitions;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The media editor window: a preview of the item as the screen will show it (its effects, fit,
/// zoom and crop), a scrub bar with trim handles for a video, and its settings on the right. One is
/// open at a time; <see cref="Open"/> brings it forward on the item asked for. The logic is in
/// <see cref="MediaEditorViewModel"/>; this file lays the window out and opens the other editors
/// (transition, auto advance, actions). Built in code, like the automation editors.
/// </summary>
public sealed class MediaEditorWindow : Window
{
    private static MediaEditorWindow? open;

    private readonly MainViewModel main;
    private readonly SceneView preview = new() { PlaysVideo = true, PlaybackScope = MediaEditorViewModel.PreviewScope };
    private readonly TrimScrubBar scrub = new() { Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly EffectsPanel effects = new();

    private MediaEditorWindow(Window? owner, MainViewModel main, MediaItem item)
    {
        this.main = main;
        Model = new MediaEditorViewModel(main, item);
        DataContext = Model;
        EditorKit.Dress(this, owner, Model.Title, 1180, 760);
        SetBinding(TitleProperty, new Binding(nameof(MediaEditorViewModel.Title)));

        preview.Media = Model.PreviewHub;
        preview.IsAudible = _ => Model.Listen;
        Model.PreviewChanged += ShowPreview;
        Model.PreviewHub.MediaReady += _ => ShowPreview();
        Model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MediaEditorViewModel.Duration) or nameof(MediaEditorViewModel.Position) or nameof(MediaEditorViewModel.Trim) or "")
            {
                scrub.Duration = Model.Duration;
                scrub.Position = Model.Position;
                scrub.Trim = Model.Trim;
            }
        };
        scrub.Seeked += (seconds, _) => Model.SeekTo(seconds);
        scrub.Trimmed += Model.TrimmedTo;
        effects.DataContext = Model.Effects;

        var grid = new Grid { Margin = new Thickness(12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 420 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(370) });
        grid.Children.Add(PreviewArea());
        var settings = new ScrollViewer { Content = Settings(), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 6, 0) };
        Grid.SetColumn(settings, 2);
        grid.Children.Add(settings);

        var done = EditorKit.Button("Done", Close, accent: true);
        done.MinWidth = 90;
        var footer = new DockPanel { Margin = new Thickness(12, 0, 12, 12) };
        DockPanel.SetDock(done, Dock.Right);
        footer.Children.Add(done);
        footer.Children.Add(EditorKit.Muted("Changes save as you make them, and show on screen straight away if this item is live.", 11, 0));
        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(grid);
        Content = root;

        Loaded += (_, _) => ShowPreview();
        Closed += (_, _) =>
        {
            Model.Dispose();
            preview.Present((Core.Animation.AnimatedScene?)null, SceneTransition.Cut);
            if (ReferenceEquals(open, this)) open = null;
        };
    }

    public MediaEditorViewModel Model { get; }

    /// <summary>The preview (the self-test pictures it).</summary>
    internal SceneView Preview => preview;

    internal TrimScrubBar Scrub => scrub;

    internal EffectsPanel EffectsPanel => effects;

    /// <summary>Opens the media editor on <paramref name="item"/> (closing the one open on another item first). Doesn't block.</summary>
    public static MediaEditorWindow? Open(Window? owner, MainViewModel main, MediaItem item)
    {
        if (main.Library.Get<MediaItem>(item.Id) is not { } stored) return null;
        if (open is { } existing)
        {
            if (existing.Model.Item.Id == stored.Id)
            {
                if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
                existing.Activate();
                return existing;
            }
            existing.Close();
        }
        var window = new MediaEditorWindow(owner, main, stored);
        open = window;
        window.Show();
        return window;
    }

    private void ShowPreview()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(ShowPreview);
            return;
        }
        var scene = Model.PreviewScene();
        if (preview.Scene is null) preview.Present(scene, SceneTransition.Cut);
        else preview.Update(scene);
    }

    // MARK: Left: the preview and its transport

    private UIElement PreviewArea()
    {
        var frame = new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = preview };
        var play = new Button { Width = 36, ToolTip = "Play or pause the preview (inside the trim, it loops back to the in point)" };
        var glyph = new TextBlock { FontFamily = EditorKit.IconFont, Text = "" };
        play.Content = glyph;
        play.SetBinding(Button.CommandProperty, new Binding(nameof(MediaEditorViewModel.TogglePlayCommand)));
        Model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MediaEditorViewModel.IsPlaying)) glyph.Text = Model.IsPlaying ? "" : "";
        };
        var time = new TextBlock { Width = 112, VerticalAlignment = VerticalAlignment.Center, Foreground = EditorKit.Res<Brush>("MutedText"), FontSize = 11 };
        time.SetBinding(TextBlock.TextProperty, new Binding(nameof(MediaEditorViewModel.TimeText)));
        var listen = new ToggleButton { Content = "Listen", Margin = new Thickness(6, 0, 0, 0), ToolTip = "Hear the preview (off, so a preview during a service never reaches the room)" };
        listen.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(MediaEditorViewModel.Listen)));
        var transport = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(play, Dock.Left);
        DockPanel.SetDock(listen, Dock.Right);
        DockPanel.SetDock(time, Dock.Right);
        transport.Children.Add(play);
        transport.Children.Add(listen);
        transport.Children.Add(time);
        transport.Children.Add(scrub);
        transport.Visibility = Model.IsTimeBased ? Visibility.Visible : Visibility.Collapsed;

        var hint = EditorKit.Muted(Model.IsTimeBased
            ? "Drag the yellow handles to trim, or click the bar to move the playhead and press Set."
            : "The picture as the screen shows it, with its fit, zoom, crop and effects.", 11, 0);
        hint.Margin = new Thickness(0, 6, 0, 0);
        var panel = new DockPanel();
        var bottom = new StackPanel { Children = { transport, hint } };
        DockPanel.SetDock(bottom, Dock.Bottom);
        panel.Children.Add(bottom);
        panel.Children.Add(frame);
        return panel;
    }

    // MARK: Right: the settings

    private StackPanel Settings()
    {
        var panel = new StackPanel();
        panel.Children.Add(EditorKit.Section("NAME", 0));
        panel.Children.Add(Bound(EditorKit.CommitBox(Model.Name, typed =>
        {
            Model.Name = typed;
            return Model.Name;
        })));

        panel.Children.Add(EditorKit.Section("BEHAVIOUR"));
        var kind = new ComboBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Background media sits behind slides and stays across them; foreground media is cleared by the next slide" };
        kind.Items.Add("Background");
        kind.Items.Add("Foreground");
        kind.SetBinding(Selector.SelectedIndexProperty, new Binding(nameof(MediaEditorViewModel.ClassificationIndex)));
        panel.Children.Add(EditorKit.Row("Plays as", kind));
        if (Model.IsVideo)
        {
            var loops = new CheckBox { Content = "Loops", VerticalAlignment = VerticalAlignment.Center };
            loops.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(MediaEditorViewModel.Loops)));
            panel.Children.Add(EditorKit.Row("", loops));
            var rate = new ComboBox { Width = 120, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = Model.RateChoices };
            rate.SetBinding(Selector.SelectedItemProperty, new Binding(nameof(MediaEditorViewModel.PlayRate)));
            panel.Children.Add(EditorKit.Row("Play rate", rate));
            panel.Children.Add(EditorKit.Row("Volume", SliderRow(nameof(MediaEditorViewModel.Volume), 0, 100, "%",
                "This video's own volume, on top of the video sound's level in Settings › Sound (Windows only)")));
        }
        panel.Children.Add(LinkButton(nameof(MediaEditorViewModel.TransitionText), () =>
        {
            TransitionPickerWindow.ForMedia(this, main, Model.Item)?.ShowDialog();
            Model.SettingsChanged();
        }));
        panel.Children.Add(LinkButton(nameof(MediaEditorViewModel.AutoAdvanceText), () =>
        {
            AutoAdvanceWindow.ForMedia(this, main, Model.Item)?.ShowDialog();
            Model.SettingsChanged();
        }));
        panel.Children.Add(LinkButton(nameof(MediaEditorViewModel.ActionsText), () =>
        {
            SlideActionsWindow.EditMedia(this, main, Model.Item);
            Model.SettingsChanged();
        }));

        if (Model.IsVideo)
        {
            panel.Children.Add(EditorKit.Section("TRIM"));
            panel.Children.Add(TrimRow("In point", nameof(MediaEditorViewModel.InPointText), Model.SetInPointCommand, Model.ClearInPointCommand));
            panel.Children.Add(TrimRow("Out point", nameof(MediaEditorViewModel.OutPointText), Model.SetOutPointCommand, Model.ClearOutPointCommand));
            var plays = new TextBlock { Foreground = EditorKit.Res<Brush>("MutedText"), FontSize = 11, Margin = new Thickness(96, 0, 0, 4) };
            plays.SetBinding(TextBlock.TextProperty, new Binding(nameof(MediaEditorViewModel.PlaysText)));
            panel.Children.Add(plays);
        }

        panel.Children.Add(EditorKit.Section("LOOK"));
        var fit = new ComboBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        fit.Items.Add("Fill the screen");
        fit.Items.Add("Fit (whole picture)");
        fit.Items.Add("Stretch");
        fit.SetBinding(Selector.SelectedIndexProperty, new Binding(nameof(MediaEditorViewModel.FitIndex)));
        panel.Children.Add(EditorKit.Row("Fit", fit));
        panel.Children.Add(EditorKit.Row("Zoom", SliderRow(nameof(MediaEditorViewModel.Zoom), 10, 400, "%", "Bigger or smaller than the screen, about its middle")));
        panel.Children.Add(EditorKit.Row("Crop left", SliderRow(nameof(MediaEditorViewModel.CropLeft), 0, 45, "%", null)));
        panel.Children.Add(EditorKit.Row("Crop top", SliderRow(nameof(MediaEditorViewModel.CropTop), 0, 45, "%", null)));
        panel.Children.Add(EditorKit.Row("Crop right", SliderRow(nameof(MediaEditorViewModel.CropRight), 0, 45, "%", null)));
        panel.Children.Add(EditorKit.Row("Crop bottom", SliderRow(nameof(MediaEditorViewModel.CropBottom), 0, 45, "%", null)));
        var summary = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = EditorKit.Res<Brush>("MutedText"), Margin = new Thickness(0, 2, 0, 4) };
        summary.SetBinding(TextBlock.TextProperty, new Binding(nameof(MediaEditorViewModel.LookSummary)));
        panel.Children.Add(summary);
        var reset = EditorKit.Button("Reset the look", () => Model.ResetLookCommand.Execute(null), "Fill the screen, whole, at full volume: as a Mac shows it");
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(reset);

        panel.Children.Add(effects);

        panel.Children.Add(EditorKit.Section("FILE"));
        panel.Children.Add(Info("Kind", Model.FileKind));
        panel.Children.Add(Info("Original name", Model.FileName));
        if (Model.Added.Length > 0) panel.Children.Add(Info("Added", Model.Added));
        if (Model.Dimensions.Length > 0) panel.Children.Add(Info("Dimensions", Model.Dimensions));
        if (Model.Length.Length > 0) panel.Children.Add(Info("Duration", Model.Length));
        panel.Children.Add(Info("Status", Model.Status));
        var buttons = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        buttons.Children.Add(EditorKit.Button("Replace file everywhere…", async () =>
        {
            Model.Flush();
            await main.ReplaceMediaFileEverywhereAsync(Model.Item);
        }, "Swaps this item's file everywhere it's used"));
        var show = EditorKit.Button("Show in Explorer", () =>
        {
            if (Model.FilePath is { } path) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        });
        show.IsEnabled = Model.FilePath is not null;
        buttons.Children.Add(show);
        panel.Children.Add(buttons);
        return panel;
    }

    private static TextBox Bound(TextBox box) => box;

    private static DockPanel Info(string label, string value) =>
        EditorKit.Row(label, new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }, bottom: 3);

    private Button LinkButton(string textProperty, Action click)
    {
        var button = new Button { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(96, 0, 0, 6), Padding = new Thickness(10, 3, 10, 3) };
        button.SetBinding(ContentProperty, new Binding(textProperty));
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>A slider with its number beside it ("75 %"), both bound to one of the model's numbers.</summary>
    private DockPanel SliderRow(string property, double min, double max, string unit, string? tooltip)
    {
        var slider = new Slider { Minimum = min, Maximum = max, VerticalAlignment = VerticalAlignment.Center, ToolTip = tooltip, SmallChange = 1, LargeChange = 10 };
        slider.SetBinding(RangeBase.ValueProperty, new Binding(property));
        var value = new TextBlock { Width = 48, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = EditorKit.Res<Brush>("MutedText") };
        value.SetBinding(TextBlock.TextProperty, new Binding(property) { StringFormat = "{0:0} " + unit });
        DockPanel.SetDock(value, Dock.Right);
        return new DockPanel { Children = { value, slider } };
    }

    private DockPanel TrimRow(string label, string property, System.Windows.Input.ICommand set, System.Windows.Input.ICommand clear)
    {
        var box = new TextBox { Width = 72, TextAlignment = TextAlignment.Right, VerticalContentAlignment = VerticalAlignment.Center, Tag = "—",
            ToolTip = "Type a time like 1:05.5, or clear it to use the " + (label == "In point" ? "start" : "end") };
        box.SetBinding(TextBox.TextProperty, new Binding(property) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
        box.KeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
        };
        var setButton = new Button { Content = "Set", Margin = new Thickness(6, 0, 4, 0), Command = set, ToolTip = "Set it at the preview's playhead" };
        var clearButton = EditorKit.Ghost("✕", "Clear it", () => clear.Execute(null));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { box, setButton, clearButton } };
        return EditorKit.Row(label, row);
    }
}
