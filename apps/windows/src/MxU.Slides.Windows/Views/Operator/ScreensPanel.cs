using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MxU.Slides.Core.Outputs;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The Screens panel: every screen with a Live/Off switch of its own (the toolbar's Audience and
/// Stage buttons switch all of one job at once), what carries it, the output preset menu and a
/// way into the full Screens window. Port of Sources/Modules/OutputsModule.swift; on the Mac the
/// switches are for displays shown directly, which Windows doesn't have, so here they are per screen.
/// </summary>
public sealed class ScreensPanel : UserControl
{
    private readonly StackPanel rows = new();
    private readonly Button presets;
    private MainViewModel? model;

    public ScreensPanel()
    {
        presets = OperatorKit.Chip(null, OutputPresetMenu.DefaultName, "Output Preset: which layers reach each screen", chip =>
        {
            if (model is not null) OperatorKit.Open(PresetMenu(model), chip);
        });
        presets.MaxWidth = 170;
        var settings = OperatorKit.IconButton("", "Screens: sizes, displays, NDI, DeckLink, looks, corrections", OpenScreens);
        var panel = new StackPanel();
        panel.Children.Add(OperatorKit.Header("SCREENS", presets, settings));
        panel.Children.Add(rows);
        Content = panel;
        DataContextChanged += (_, _) => Attach(OperatorKit.Model(this));
    }

    private void Attach(MainViewModel? next)
    {
        if (model is not null) model.OutputsChanged -= Rebuild;
        model = next;
        if (model is not null) model.OutputsChanged += Rebuild;
        Rebuild();
    }

    /// <summary>The look switcher (also the live header's chip): no preset, then every preset, the active one ticked.</summary>
    internal static ContextMenu PresetMenu(MainViewModel model) => OperatorKit.Menu(model.OutputPresetChoices().Select(choice =>
        (object)OperatorKit.Choice(choice.Name, OutputPresetMenu.IsActive(choice, model.ActiveOutputPresetId), () => model.SwitchOutputPreset(choice.Id))));

    private void OpenScreens()
    {
        if (model?.Outputs is not { } outputs || Window.GetWindow(this) is not { } owner) return;
        new ScreensWindow(outputs) { Owner = owner }.ShowDialog();
        model.RenderOutputs(Rendering.SceneTransition.Cut);
        Rebuild();
    }

    /// <summary>Each row's screen name and whether it shows Live (the self-test reads these).</summary>
    internal List<(string Name, bool Live)> Rows { get; } = [];

    internal void Rebuild()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Rebuild);
            return;
        }
        rows.Children.Clear();
        Rows.Clear();
        if (model is null) return;
        OperatorKit.SetChipText(presets, OutputPresetMenu.ChipName(model.ActiveOutputPresetName));
        presets.ToolTip = OutputPresetMenu.ChipHelp(model.ActiveOutputPresetName);
        var screens = model.ScreenRows();
        if (screens.Count == 0) rows.Children.Add(EditorKit.Muted("No screens yet. Add them in Screens & Settings.", 11, 2));
        foreach (var screen in screens)
        {
            Rows.Add((screen.Name, screen.IsLive));
            var id = screen.Id;
            var live = screen.IsLive;
            var toggle = OperatorKit.SmallButton(live ? "Live" : "Off", live ? $"Take {screen.Name} off air" : $"Send output to {screen.Name}",
                () => model.SetScreenLive(id, !live), live ? OperatorKit.Green : EditorKit.Res<Brush>("FaintText"), live ? OperatorKit.Green : null);
            toggle.MinWidth = 46;
            var name = new TextBlock
            {
                Text = screen.Name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = live ? OperatorKit.Green : EditorKit.Res<Brush>("Text"),
            };
            var caption = new TextBlock
            {
                Text = $"{(screen.Role == ScreenRole.Confidence ? "Stage" : "Audience")} · {screen.Carrier}", FontSize = 10, Margin = new Thickness(8, 1, 6, 0),
                VerticalAlignment = VerticalAlignment.Center, Foreground = EditorKit.Res<Brush>("FaintText"), TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var line = new DockPanel { Margin = new Thickness(8, 3, 6, 3) };
            DockPanel.SetDock(toggle, Dock.Right);
            line.Children.Add(toggle);
            line.Children.Add(name);
            line.Children.Add(caption);
            rows.Children.Add(OperatorKit.RowCard(line, live ? OperatorKit.GreenWash : null));
        }
    }
}
