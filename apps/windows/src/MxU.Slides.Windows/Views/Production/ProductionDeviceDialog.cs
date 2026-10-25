using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using MxU.Slides.Core.Automation;
using MxU.Slides.Production.Devices;
using MxU.Slides.Production.Lighting;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views.Production;

/// <summary>
/// Adds or changes one production device: its name and address, and what its kind needs (a
/// password for OBS or vMix; for a DMX node, how it's reached and which universes it gets).
/// Saving applies at once; the device reconnects in the background and its light shows how
/// it went. Passwords go to the encrypted store, never into production.json.
/// </summary>
internal sealed class ProductionDeviceDialog : Window
{
    private readonly MainViewModel model;
    private readonly DeviceSettings device;
    private readonly TextBox name;
    private readonly TextBox host;
    private readonly TextBox port;
    private readonly TextBox? user;
    private readonly PasswordBox? password;
    private readonly CheckBox? forgetPassword;
    private readonly ComboBox? delivery;
    private readonly TextBox? universes;
    private readonly TextBox? priority;
    private readonly CheckBox enabled;

    private ProductionDeviceDialog(Window? owner, MainViewModel model, DeviceSettings device, bool isNew)
    {
        this.model = model;
        this.device = device;
        var kindName = DeviceSettings.KindName(device.Kind);
        EditorKit.Dress(this, owner, isNew ? $"Add {kindName}" : $"{device.Name} — {kindName}", 520, double.NaN);
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;

        var body = new StackPanel { Margin = new Thickness(18, 16, 18, 10) };
        body.Children.Add(new TextBlock { Text = isNew ? $"Add {kindName}" : device.Name, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        body.Children.Add(EditorKit.Muted(Help(device.Kind), 11, 12));

        name = new TextBox { Text = device.Name };
        body.Children.Add(EditorKit.Row("Name", name, 110));
        host = new TextBox { Text = device.Host, Tag = HostHint(device.Kind) };
        body.Children.Add(EditorKit.Row(device.IsDmx ? "Address" : "Address", host, 110));
        port = new TextBox
        {
            Text = device.Port?.ToString(CultureInfo.InvariantCulture) ?? "", Width = 80, HorizontalAlignment = HorizontalAlignment.Left,
            Tag = DeviceSettings.DefaultPort(device.Kind).ToString(CultureInfo.InvariantCulture),
            ToolTip = $"Empty: the usual port, {DeviceSettings.DefaultPort(device.Kind).ToString(CultureInfo.InvariantCulture)}",
        };
        body.Children.Add(EditorKit.Row("Port", port, 110));

        if (device.Kind == DeviceKind.Vmix)
        {
            user = new TextBox { Text = device.User ?? "", Tag = "Only if vMix's web controller asks for one" };
            body.Children.Add(EditorKit.Row("User name", user, 110));
        }
        if (device.TakesPassword)
        {
            password = new PasswordBox();
            body.Children.Add(EditorKit.Row("Password", password, 110));
            var stored = model.Production.Secrets.Contains(device.Id);
            body.Children.Add(EditorKit.Muted(stored
                ? "A password is stored (encrypted for your Windows account). Leave this empty to keep it."
                : device.Kind == DeviceKind.Obs
                    ? "OBS: Tools › WebSocket Server Settings › Show Connect Info has it. Empty if authentication is off."
                    : "vMix: Settings › Web Controller. Empty if it has no password.", 10.5, 4));
            if (stored)
            {
                forgetPassword = new CheckBox { Content = "Forget the stored password" };
                body.Children.Add(EditorKit.Row("", forgetPassword, 110));
            }
        }
        if (device.IsDmx)
        {
            var choices = device.Kind == DeviceKind.ArtNet
                ? new List<EditorChoice> { new("broadcast", "Broadcast to every node"), new("unicast", "Straight to this address") }
                : [new("multicast", "Multicast (239.255.x.y, any node listening)"), new("unicast", "Straight to this address")];
            delivery = EditorKit.Picker(choices, device.EffectiveDelivery.ToString().ToLowerInvariant(), _ => { }, minWidth: 260);
            body.Children.Add(EditorKit.Row("Send", delivery, 110));
            universes = new TextBox { Text = ChannelList.Format(device.EffectiveUniverses), Width = 120, HorizontalAlignment = HorizontalAlignment.Left, Tag = "1" };
            body.Children.Add(EditorKit.Row("Universes", universes, 110));
            body.Children.Add(EditorKit.Muted(device.Kind == DeviceKind.ArtNet
                ? "The show's universes this node gets, e.g. \"1\" or \"1-4\". Universe 1 goes out as Art-Net 0:0:0 (net:sub-net:universe), as most lighting software counts."
                : "The show's universes this node gets, e.g. \"1\" or \"1-4\": universe 1 is sACN universe 1.", 10.5, 6));
            if (device.Kind == DeviceKind.Sacn)
            {
                priority = new TextBox { Text = (device.Priority ?? 100).ToString(CultureInfo.InvariantCulture), Width = 60, HorizontalAlignment = HorizontalAlignment.Left };
                body.Children.Add(EditorKit.Row("Priority", priority, 110));
            }
        }
        enabled = new CheckBox { Content = "On (connect and send)", IsChecked = device.Enabled };
        body.Children.Add(EditorKit.Row("", enabled, 110));

        var status = model.Production.StatusOf(device.Id);
        if (!isNew)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(110, 6, 0, 0) };
            line.Children.Add(ProductionUi.Dot(status));
            line.Children.Add(ProductionUi.Line($"{ProductionUi.StateWord(status.State)}: {status.Detail}"));
            body.Children.Add(line);
        }

        var save = EditorKit.Button(isNew ? "Add" : "Save", () =>
        {
            if (Save()) DialogResult = true;
        }, accent: true);
        save.IsDefault = true;
        save.MinWidth = 90;
        var cancel = EditorKit.Button("Cancel", Close);
        cancel.IsCancel = true;
        cancel.MinWidth = 90;
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(18, 4, 18, 16) };
        footer.Children.Add(cancel);
        footer.Children.Add(save);
        var root = new StackPanel();
        root.Children.Add(body);
        root.Children.Add(footer);
        Content = root;
        Loaded += (_, _) => name.Focus();
    }

    /// <summary>Opens the dialog and waits; true when the device was saved.</summary>
    public static bool Edit(Window? owner, MainViewModel model, DeviceSettings device, bool isNew) =>
        Make(owner, model, device, isNew).ShowDialog() == true;

    /// <summary>The dialog, not yet shown (the self-test fills it in and pictures it).</summary>
    internal static ProductionDeviceDialog Make(Window? owner, MainViewModel model, DeviceSettings device, bool isNew) => new(owner, model, device, isNew);

    /// <summary>Types into the fields, as a person would (the self-test).</summary>
    internal void Fill(string host, int? port, string? passwordText = null, string? name = null)
    {
        if (name is not null) this.name.Text = name;
        this.host.Text = host;
        this.port.Text = port?.ToString(CultureInfo.InvariantCulture) ?? "";
        if (password is not null && passwordText is not null) password.Password = passwordText;
        if (delivery is not null && port is not null)
            delivery.SelectedItem = ((IEnumerable<EditorChoice>)delivery.ItemsSource).First(c => c.Id == "unicast");
    }

    /// <summary>Checks what was typed and saves it (the device reconnects); false, with a message, when something's missing.</summary>
    internal bool Save()
    {
        var changed = device.Copy();
        changed.Name = name.Text.Trim().Length > 0 ? name.Text.Trim() : DeviceSettings.New(device.Kind).Name;
        changed.Host = host.Text.Trim();
        changed.Port = int.TryParse(port.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p is > 0 and < 65536 ? p : null;
        changed.Enabled = enabled.IsChecked == true;
        if (user is not null) changed.User = user.Text.Trim().Length > 0 ? user.Text.Trim() : null;
        if (delivery?.SelectedItem is EditorChoice chosen) changed.Delivery = Enum.Parse<DmxDelivery>(chosen.Id, ignoreCase: true);
        if (universes is not null)
        {
            var list = ChannelList.Parse(universes.Text);
            changed.Universes = list.Count > 0 ? list : [1];
        }
        if (priority is not null)
            changed.Priority = int.TryParse(priority.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var level) ? Math.Clamp(level, 0, 200) : 100;
        if (!changed.IsDmx && changed.Host.Length == 0)
        {
            MessageBox.Show(this, "Type the device's address (an IP address such as 192.168.10.240, or the computer's name).", Title);
            return false;
        }
        if (changed.EffectiveDelivery == DmxDelivery.Unicast && changed.IsDmx && changed.Host.Length == 0)
        {
            MessageBox.Show(this, "Type the node's address, or choose to broadcast (Art-Net) or multicast (sACN).", Title);
            return false;
        }
        string? newPassword = null;
        if (forgetPassword?.IsChecked == true) newPassword = "";
        else if (password is not null && password.Password.Length > 0) newPassword = password.Password;
        model.Production.SaveDevice(changed, newPassword);
        return true;
    }

    private static string Help(DeviceKind kind) => kind switch
    {
        DeviceKind.Atem => "A Blackmagic ATEM switcher on this network (ATEM Mini, Television Studio, Constellation…). Its address is in ATEM Setup › Network. Up to about five programs can be connected to one at once.",
        DeviceKind.Obs => "OBS Studio 28 or later, with Tools › WebSocket Server Settings › Enable WebSocket server turned on. Use 127.0.0.1 when OBS runs on this PC.",
        DeviceKind.Vmix => "vMix with its web controller on (Settings › Web Controller). Use 127.0.0.1 when vMix runs on this PC.",
        DeviceKind.ArtNet => "A DMX node (or lighting desk) that takes Art-Net. Nothing is sent until a lighting step runs, so starting MxU Slides never takes the lights from a desk.",
        _ => "A DMX node (or lighting desk) that takes sACN (E1.31). Nothing is sent until a lighting step runs, so starting MxU Slides never takes the lights from a desk.",
    };

    private static string HostHint(DeviceKind kind) => kind switch
    {
        DeviceKind.Atem => "192.168.10.240",
        DeviceKind.Obs or DeviceKind.Vmix => "127.0.0.1 (this PC)",
        DeviceKind.ArtNet => "Empty: everyone (255.255.255.255), or e.g. 192.168.1.255",
        _ => "Only to send straight to one node",
    };
}
