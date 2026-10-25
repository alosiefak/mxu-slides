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
/// slide layers' room default transitions. Port of the Mac's LivePanel.swift (screenPicker and
/// Open in Window), OutputPresetChip.swift and the header's transition chips (TransitionChip in
/// PerformanceHUD.swift).
/// </summary>
public sealed class LiveToolbar : UserControl
{
    private readonly Button target;
    private readonly Button preset;
    private readonly Button media;
    private readonly Button slides;
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
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, Children = { media, slides } };
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
        }
    }

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
