using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Outputs;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The chips above the live preview: which screen or stage layout the preview shows, a button to
/// pop the preview out into its own window, the output preset ("look") switcher, and the media and
/// slide layers' room default transitions, each with a small slider for its length beside it (Phase 8).
/// Port of the Mac's LivePanel.swift (screenPicker and Open in Window), OutputPresetChip.swift and
/// the header's transition chips (TransitionChip in PerformanceHUD.swift).
/// </summary>
public sealed class LiveToolbar : UserControl
{
    private readonly Button target;
    private readonly Button preset;
    private readonly Button media;
    private readonly Button slides;
    private readonly (Slider Slider, TextBlock Label) mediaLength;
    private readonly (Slider Slider, TextBlock Label) slidesLength;
    private MainViewModel? model;
    private bool watchingWindow;

    public LiveToolbar()
    {
        target = OperatorKit.Chip("", "Preview", "Which screen or layout this preview shows: each screen shows its own look and layers", chip =>
        {
            if (model is not null) OperatorKit.Open(TargetMenu(model), chip);
        });
        target.MaxWidth = 150;
        var popOut = OperatorKit.IconButton("", "Open this preview in its own window (or another screen's)", () => { }, 12);
        popOut.Click += (_, _) =>
        {
            if (model is not null) OperatorKit.Open(PopOutMenu(model), popOut);
        };
        preset = OperatorKit.Chip("", OutputPresetMenu.DefaultName, "Output Preset", chip =>
        {
            if (model is null) return;
            var menu = ScreensPanel.PresetMenu(model);
            menu.Items.Add(EditorKit.Separator());
            menu.Items.Add(EditorKit.Command("Edit Screens and Looks…", OpenScreens));
            OperatorKit.Open(menu, chip);
        });
        preset.MaxWidth = 150;
        preset.Margin = new Thickness(0);
        media = OperatorKit.Chip("", "Media", "Media transition", chip => OpenTransition(chip, media: true));
        slides = OperatorKit.Chip("", "Slides", "Slide transition", chip => OpenTransition(chip, media: false));
        slides.Margin = new Thickness(0);

        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(preset, Dock.Right);
        top.Children.Add(preset);
        top.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { target, popOut } });
        mediaLength = LengthSlider(media: true);
        slidesLength = LengthSlider(media: false);
        slides.Margin = new Thickness(0, 0, 4, 0);
        var bottom = new WrapPanel
        {
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 0), Children = { media, mediaLength.Slider, mediaLength.Label } },
                new StackPanel { Orientation = Orientation.Horizontal, Children = { slides, slidesLength.Slider, slidesLength.Label } },
            },
        };
        Content = new StackPanel { Children = { top, bottom } };
        DataContextChanged += (_, _) => Attach(OperatorKit.Model(this));
        // Settings › Transitions changes the same room defaults: read them again when the window comes back to the front.
        Loaded += (_, _) =>
        {
            if (watchingWindow || Window.GetWindow(this) is not { } window) return;
            watchingWindow = true;
            window.Activated += (_, _) => Refresh();
        };
    }

    private void Attach(MainViewModel? next)
    {
        if (model is not null)
        {
            model.OutputsChanged -= Refresh;
            model.TransitionsChanged -= Refresh;
            model.StageLayoutsChanged -= Refresh;
        }
        model = next;
        if (model is not null)
        {
            model.OutputsChanged += Refresh;
            model.TransitionsChanged += Refresh;
            model.StageLayoutsChanged += Refresh;
        }
        Refresh();
    }

    /// <summary>The chips' labels (the self-test reads these).</summary>
    internal string TargetText { get; private set; } = "";
    internal string PresetText { get; private set; } = "";
    internal string MediaText { get; private set; } = "";
    internal string SlidesText { get; private set; } = "";

    private void Refresh()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Refresh);
            return;
        }
        if (model is null) return;
        TargetText = model.PreviewTarget?.Name ?? "No Screens";
        OperatorKit.SetChipText(target, TargetText);
        PresetText = OutputPresetMenu.ChipName(model.ActiveOutputPresetName);
        OperatorKit.SetChipText(preset, PresetText);
        preset.ToolTip = OutputPresetMenu.ChipHelp(model.ActiveOutputPresetName);
        foreach (var (chip, isMedia) in new[] { (media, true), (slides, false) })
        {
            // Any kind the Windows app plays, since Phase 6 (Views/Transitions).
            var choice = model.RoomTransitionChoice(isMedia);
            var text = WindowsTransitions.ChipCaption(choice);
            OperatorKit.SetChipText(chip, text);
            chip.ToolTip = $"{(isMedia ? "Backgrounds and videos" : "Slides")}: {text}: this layer's default; items with their own always win";
            if (isMedia) MediaText = text;
            else SlidesText = text;
            var (slider, label) = isMedia ? mediaLength : slidesLength;
            var shows = WindowsTransitions.HasDuration(choice.Kind);
            slider.Visibility = label.Visibility = shows ? Visibility.Visible : Visibility.Collapsed;
            showingLength = true;
            slider.Value = Math.Clamp(choice.DurationSeconds, slider.Minimum, slider.Maximum);
            showingLength = false;
            label.Text = TransitionEdit.Label(choice.DurationSeconds);
            slider.IsEnabled = !model.IsVolunteer;
        }
    }

    // MARK: The inline length sliders (the Mac's TransitionChip slider)

    private bool showingLength;

    /// <summary>
    /// A small slider beside a chip for its length (0.1 to 3 s, in tenths), saved when it's let go,
    /// as the Mac's mini slider does. Longer lengths are typed in the chip's popover.
    /// </summary>
    private (Slider Slider, TextBlock Label) LengthSlider(bool media)
    {
        var slider = new Slider
        {
            Minimum = TransitionEdit.DurationStep, Maximum = TransitionEdit.SliderMaximum, Width = 64, VerticalAlignment = VerticalAlignment.Center,
            SmallChange = TransitionEdit.DurationStep, LargeChange = 0.5, IsSnapToTickEnabled = true, TickFrequency = TransitionEdit.DurationStep,
            Margin = new Thickness(0, 0, 4, 0), Opacity = 0.75, ToolTip = media ? "How long backgrounds and videos take to change" : "How long slides take to change",
        };
        var label = new TextBlock { Width = 34, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center, Foreground = EditorKit.Res<Brush>("MutedText") };
        slider.ValueChanged += (_, _) =>
        {
            label.Text = TransitionEdit.Label(slider.Value);
            // A click on the track (not a drag) changes it straight away; a drag saves when let go.
            if (!showingLength && !dragging) SaveLength(media, slider.Value);
        };
        slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => dragging = true));
        slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            dragging = false;
            SaveLength(media, slider.Value);
        }));
        return (slider, label);
    }

    private bool dragging;

    private void SaveLength(bool media, double seconds)
    {
        if (model is null || !model.Permit(Core.Control.OperatorAction.ChangeTransitions)) return;
        var choice = model.RoomTransitionChoice(media);
        var rounded = Math.Round(seconds / TransitionEdit.DurationStep) * TransitionEdit.DurationStep;
        if (Math.Abs(choice.DurationSeconds - rounded) < 0.001) return;
        model.SetRoomTransition(media, choice with { DurationSeconds = rounded });
    }

    /// <summary>The length slider beside a chip (the self-test moves it).</summary>
    internal Slider LengthSliderFor(bool media) => media ? mediaLength.Slider : slidesLength.Slider;

    private static ContextMenu TargetMenu(MainViewModel model)
    {
        var (screens, layouts) = model.PreviewChoices();
        var current = model.PreviewTarget?.Id;
        List<object> items = [new MenuItem { Header = "SCREENS", IsEnabled = false, Style = (Style)EditorKit.Styles["CascadingMenuItem"], FontSize = 10 }];
        items.AddRange(screens.Select(s => (object)OperatorKit.Choice(s.Name, s.Id == current, () => model.SetPreviewTarget(s.Id))));
        if (layouts.Count > 0)
        {
            items.Add(EditorKit.Separator());
            items.Add(new MenuItem { Header = "LAYOUTS", IsEnabled = false, Style = (Style)EditorKit.Styles["CascadingMenuItem"], FontSize = 10 });
            items.AddRange(layouts.Select(l => (object)OperatorKit.Choice(l.Name, l.Id == current, () => model.SetPreviewTarget(l.Id))));
        }
        return OperatorKit.Menu(items);
    }

    private ContextMenu PopOutMenu(MainViewModel model)
    {
        var owner = Window.GetWindow(this);
        var (screens, layouts) = model.PreviewChoices();
        List<object> items = [EditorKit.Command("Open in Window", () => model.OpenPreviewWindow(null, owner))];
        if (screens.Count + layouts.Count > 1)
        {
            List<object> others = [.. screens.Select(s => (object)EditorKit.Command(s.Name, () => model.OpenPreviewWindow(s.Id, owner)))];
            if (layouts.Count > 0)
            {
                others.Add(EditorKit.Separator());
                others.AddRange(layouts.Select(l => (object)EditorKit.Command(l.Name, () => model.OpenPreviewWindow(l.Id, owner))));
            }
            items.Add(OperatorKit.Submenu("Open Preview Window", others));
        }
        return OperatorKit.Menu(items);
    }

    private void OpenScreens()
    {
        if (model?.Outputs is not { } outputs || Window.GetWindow(this) is not { } owner) return;
        new ScreensWindow(outputs) { Owner = owner }.ShowDialog();
        model.RenderOutputs(Rendering.SceneTransition.Cut);
    }

    /// <summary>Opens a chip's menu or popover as a click would (the self-test pictures them).</summary>
    internal ContextMenu? OpenTargetMenu() => Opened(model is null ? null : TargetMenu(model), target);

    internal ContextMenu? OpenPresetMenu() => Opened(model is null ? null : ScreensPanel.PresetMenu(model), preset);

    internal Popup? OpenTransitionPopover(bool forMedia) => OpenTransition(forMedia ? media : slides, forMedia);

    private static ContextMenu? Opened(ContextMenu? menu, FrameworkElement under)
    {
        if (menu is not null) OperatorKit.Open(menu, under);
        return menu;
    }

    /// <summary>
    /// The chip's popover: every kind as a playing tile, the length for any but a cut, and the
    /// direction, softness or colour where the kind has one. Each change saves at once.
    /// </summary>
    private Popup? OpenTransition(Button chip, bool media) => Transitions.RoomTransitionPopover.Open(chip, model, media);
}
