using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using MxU.Slides.Core.Automation;
using MxU.Slides.LocalApi;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views.Follow;

/// <summary>
/// Make This the Backup (the Mac's pairing sheet): the computers running MxU Slides on this network
/// (found by DNS-SD, as a Mac's Bonjour, so Macs show too), or one typed by its address; the code
/// it shows; and how to follow it. Start Backing Up asks the booth for a key with the code.
/// </summary>
public sealed class PairBackupWindow : Window
{
    private readonly MainViewModel model;
    private readonly BonjourBrowser browser;
    private readonly ComboBox computers = new() { MinWidth = 280 };
    private readonly TextBox address = new() { MinWidth = 280, ToolTip = "Or type its address and port, e.g. 192.168.1.20:6980" };
    private readonly TextBox code = new() { Width = 90, FontSize = 16, MaxLength = 4 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };
    private bool mirrors = true;
    private bool automatic = true;
    private IReadOnlyList<BonjourBrowser.Peer> peers = [];

    private PairBackupWindow(Window owner, MainViewModel model)
    {
        this.model = model;
        EditorKit.Dress(this, owner, "Make this the backup", 560, 420);
        SizeToContent = SizeToContent.Height;
        browser = new BonjourBrowser(model.Computer.Id);
        browser.Changed += found => Dispatcher.BeginInvoke(() => ShowPeers(found));
        Content = Layout();
        Loaded += (_, _) =>
        {
            try
            {
                browser.Start();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException)
            {
                status.Text = $"Couldn't look on the network ({ex.Message}). Type the other computer's address instead.";
            }
        };
        Closed += (_, _) => browser.Dispose();
    }

    public static PairBackupWindow Open(Window owner, MainViewModel model)
    {
        var window = new PairBackupWindow(owner, model);
        window.Show();
        return window;
    }

    private UIElement Layout()
    {
        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock { Text = "Make this the backup", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        root.Children.Add(EditorKit.Muted(
            "This computer shows what the main computer shows, or stays ready with clear screens. It answers its own triggers only after it takes over.", 11, 10));
        root.Children.Add(EditorKit.Muted("Looking for MxU Slides on this network… The other computer needs MxU Slides open with its API on.", 11, 6));
        computers.Items.Add("(none found yet)");
        computers.SelectedIndex = 0;
        root.Children.Add(EditorKit.Row("Computer", computers, 140));
        root.Children.Add(EditorKit.Row("Or its address", address, 140));
        root.Children.Add(EditorKit.Row("Code it shows", code, 140));
        root.Children.Add(EditorKit.Row("While it runs", EditorKit.Picker(
            [new EditorChoice("live", "Show the same thing live"), new EditorChoice("ready", "Stay ready, show nothing")], "live", id => mirrors = id == "live"), 140));
        root.Children.Add(EditorKit.Row("If it stops", EditorKit.Picker(
            [new EditorChoice("auto", "Take over after 3 seconds"), new EditorChoice("wait", "Wait for Take Over")], "auto", id => automatic = id == "auto"), 140));
        root.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = EditorKit.Button("Cancel", Close);
        cancel.Margin = new Thickness(0, 0, 8, 0);
        buttons.Children.Add(cancel);
        buttons.Children.Add(EditorKit.Button("Start Backing Up", () => _ = StartAsync(), accent: true));
        root.Children.Add(buttons);
        return root;
    }

    private void ShowPeers(IReadOnlyList<BonjourBrowser.Peer> found)
    {
        var picked = computers.SelectedIndex > 0 && computers.SelectedIndex - 1 < peers.Count ? peers[computers.SelectedIndex - 1].StationHexId : null;
        peers = found;
        computers.Items.Clear();
        computers.Items.Add(found.Count == 0 ? "(none found yet)" : "Pick a computer");
        foreach (var peer in found) computers.Items.Add(peer.StationName is { Length: > 0 } station && station != peer.Name ? $"{peer.Name} ({station})" : peer.Name);
        var again = found.ToList().FindIndex(p => p.StationHexId == picked);
        computers.SelectedIndex = again >= 0 ? again + 1 : found.Count == 1 ? 1 : 0;
    }

    /// <summary>Pairs with the computer picked (or typed). For the self-test too.</summary>
    public async Task<string?> StartAsync(string? typedAddress = null, string? typedCode = null)
    {
        if (typedAddress is not null) address.Text = typedAddress;
        if (typedCode is not null) code.Text = typedCode;
        string host, station, name;
        int port;
        if (address.Text.Trim() is { Length: > 0 } typed)
        {
            var colon = typed.LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(typed[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port))
                return Say("Type the address with its port, e.g. 192.168.1.20:6980.");
            (host, station, name) = (typed[..colon].Trim('[', ']'), "", typed[..colon]);
        }
        else if (computers.SelectedIndex > 0 && computers.SelectedIndex - 1 < peers.Count)
        {
            var peer = peers[computers.SelectedIndex - 1];
            (host, port, station, name) = (peer.Host, peer.Port, peer.StationHexId, peer.StationName ?? peer.Name);
        }
        else
        {
            return Say("Pick the computer to back up, or type its address.");
        }
        if (code.Text.Trim().Length != 4) return Say($"Type the four-digit code {name} shows in its Settings › Backup.");
        status.Text = $"Pairing with {name}…";
        // The name it goes by: what it calls itself, once its ping answers.
        if (station.Length == 0 && await Ping(host, port) is { } info)
            (station, name) = (info.StationHexId ?? "", info.StationName ?? info.Name);
        var problem = await model.Follow.PairAsync(host, port, station, name, code.Text, automatic, mirrors);
        if (problem is not null) return Say(problem);
        Close();
        return null;
    }

    private static async Task<ApiServerInfo?> Ping(string host, int port)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var json = await http.GetByteArrayAsync(new LocalApiClient.Endpoint(host, port, "").Http("/v1/ping"));
            return System.Text.Json.JsonSerializer.Deserialize<ApiServerInfo>(json, ApiJson.Options);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private string Say(string text)
    {
        status.Text = text;
        return text;
    }
}
