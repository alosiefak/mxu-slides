using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using MxU.Slides.Production.Devices;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views.Production;

/// <summary>
/// Settings › Production: the cameras, streaming software and lights this PC controls, each
/// with its live status light (green connected, amber connecting, red not answering, grey off).
/// Add, edit, turn off and remove devices here; the lighting looks and cue list open from
/// here too. Changes apply at once (a changed device reconnects in the background) and
/// belong to this PC, like Timecode in: Cancel doesn't undo them.
/// <para>Built in code like the other settings panels; the settings window holds it in one place.</para>
/// <para>
/// The rows are made again only when the devices change. A status light changing (which can
/// happen every few seconds: OBS changing scene, vMix's poll) only repaints that row's light
/// and words, so the row's buttons never vanish from under the mouse mid-click.
/// </para>
/// </summary>
public sealed class ProductionSettingsSection : UserControl
{
    private readonly StackPanel rows = new();
    private readonly Dictionary<string, (Ellipse Dot, TextBlock Words)> lights = [];
    private MainViewModel? model;

    public ProductionSettingsSection()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "PRODUCTION (WINDOWS)", Style = EditorKit.Res<Style>("SectionLabel"), Margin = new Thickness(0, 18, 0, 6) });
        panel.Children.Add(EditorKit.Muted(
            "The video switcher, streaming software and lights this PC controls. Slides, combos, schedule triggers and timeline "
            + "cues switch them with their Production steps. Devices belong to this PC; the looks and steps belong to the library.", 11, 8));
        panel.Children.Add(rows);
        var buttons = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        var add = EditorKit.Button("+ Add a device", () => { }, "A Blackmagic ATEM, OBS, vMix, or a DMX lighting node (Art-Net or sACN)");
        add.Click += (_, _) => AddMenu(add);
        buttons.Children.Add(add);
        buttons.Children.Add(EditorKit.Button("Lighting looks and cues…", () =>
        {
            if (model is not null) LightingWindow.Open(Window.GetWindow(this), model);
        }, "Build lighting looks with sliders, put them in a cue list, blackout and grand master"));
        panel.Children.Add(buttons);
        Content = panel;

        void OnChange(ProductionDevice device) => ProductionUi.OnUi(this, () => ShowStatus(device.Id));
        void OnDevices() => ProductionUi.OnUi(this, Rebuild);
        Loaded += (_, _) =>
        {
            model ??= Window.GetWindow(this)?.Owner?.DataContext as MainViewModel ?? Application.Current.MainWindow?.DataContext as MainViewModel;
            if (model is null) return;
            model.Production.StatusChanged += OnChange;
            model.Production.DevicesChanged += OnDevices;
            Rebuild();
        };
        Unloaded += (_, _) =>
        {
            if (model is null) return;
            model.Production.StatusChanged -= OnChange;
            model.Production.DevicesChanged -= OnDevices;
        };
    }

    /// <summary>The rows shown (the self-test checks the lights).</summary>
    internal IReadOnlyList<(string Name, DeviceStatus Status)> Shown { get; private set; } = [];

    private void AddMenu(FrameworkElement under)
    {
        if (model is null) return;
        var menu = new ContextMenu();
        foreach (var kind in Enum.GetValues<DeviceKind>())
        {
            var picked = kind;
            menu.Items.Add(EditorKit.Command(DeviceSettings.KindName(kind) + "…", () =>
                ProductionDeviceDialog.Edit(Window.GetWindow(this), model, DeviceSettings.New(picked), isNew: true)));
        }
        EditorKit.Open(menu, under);
    }

    internal void Rebuild()
    {
        if (model is null) return;
        rows.Children.Clear();
        lights.Clear();
        var setups = model.Production.Setups;
        if (setups.Count == 0)
            rows.Children.Add(EditorKit.Muted("No devices yet. Production steps for a device this PC hasn't got are skipped (and noted in the log).", 11, 6));
        foreach (var device in setups) rows.Children.Add(EditorKit.Card(Row(device, StatusOf(device))));
        Shown = [.. setups.Select(d => (d.Name, StatusOf(d)))];
    }

    private DeviceStatus StatusOf(DeviceSettings device) => device.Enabled ? model!.Production.StatusOf(device.Id) : DeviceStatus.Off;

    /// <summary>One device's light changed: repaint just its light and words.</summary>
    private void ShowStatus(string deviceId)
    {
        if (model is null || !lights.TryGetValue(deviceId, out var light)) return;
        var setups = model.Production.Setups;
        if (setups.FirstOrDefault(d => d.Id == deviceId) is not { } device) return;
        var status = StatusOf(device);
        ProductionUi.Paint(light.Dot, status);
        light.Words.Text = Words(status);
        Shown = [.. setups.Select(d => (d.Name, StatusOf(d)))];
    }

    private static string Words(DeviceStatus status) => $"{ProductionUi.StateWord(status.State)}: {status.Detail}";

    private DockPanel Row(DeviceSettings device, DeviceStatus status)
    {
        var row = new DockPanel();
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var on = new CheckBox { Content = "On", IsChecked = device.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), ToolTip = "Off: leave it alone (no connecting, no sending)" };
        on.Click += (_, _) =>
        {
            var changed = device.Copy();
            changed.Enabled = on.IsChecked == true;
            model!.Production.SaveDevice(changed);
        };
        right.Children.Add(on);
        right.Children.Add(EditorKit.Button("Edit…", () => ProductionDeviceDialog.Edit(Window.GetWindow(this), model!, device.Copy(), isNew: false)));
        right.Children.Add(EditorKit.Button("Remove", () =>
        {
            if (MessageBox.Show(Window.GetWindow(this)!, $"Remove \"{device.Name}\" from this PC? Production steps that use it are kept, and skipped until a device like it is added.",
                    "Remove device", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                model!.Production.RemoveDevice(device.Id);
        }));
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);

        var dot = ProductionUi.Dot(status, 10);
        DockPanel.SetDock(dot, Dock.Left);
        row.Children.Add(dot);
        var text = new StackPanel();
        var address = device.IsDmx
            ? $"{DeviceSettings.KindName(device.Kind)} · {DmxWhere(device)} · universe{(device.EffectiveUniverses.Count == 1 ? "" : "s")} {string.Join(", ", device.EffectiveUniverses)}"
            : $"{DeviceSettings.KindName(device.Kind)} · {device.Host}:{device.EffectivePort.ToString(CultureInfo.InvariantCulture)}";
        text.Children.Add(new TextBlock { Text = device.Name, FontWeight = FontWeights.SemiBold, Foreground = EditorKit.Res<Brush>("Text") });
        text.Children.Add(ProductionUi.Line(address));
        var words = ProductionUi.Line(Words(status), 10.5);
        text.Children.Add(words);
        row.Children.Add(text);
        lights[device.Id] = (dot, words);
        return row;
    }

    private static string DmxWhere(DeviceSettings device) => device.EffectiveDelivery switch
    {
        DmxDelivery.Broadcast => device.Host.Trim().Length > 0 ? $"broadcast to {device.Host}" : "broadcast",
        DmxDelivery.Multicast => "multicast",
        _ => $"to {device.Host}",
    };
}
