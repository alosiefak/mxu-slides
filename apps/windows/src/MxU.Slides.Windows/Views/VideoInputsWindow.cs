using System.Windows;
using System.Windows.Controls;
using MxU.Slides.Core.Model;
using MxU.Slides.VideoIO.Capture;
using MxU.Slides.VideoIO.DeckLink;
using MxU.Slides.VideoIO.Ndi;
using MxU.Slides.Windows.Infrastructure;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// Sets up live inputs: give each a name ("Pulpit camera") and choose what feeds it — a
/// camera or capture card, an NDI source on the network, or a DeckLink input — plus a delay
/// to line its picture up with the sound. Port of the Mac's video inputs settings.
/// </summary>
public sealed class VideoInputsWindow : Window
{
    private sealed record Choice(string? Value, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly VideoInputInventory inventory;
    private readonly StackPanel rows = new();
    private IReadOnlyList<CameraDevice> cameras = [];
    private IReadOnlyList<string> ndiSources = [];
    private IReadOnlyList<DeckLinkDevice> deckLinks = [];

    public VideoInputsWindow(VideoInputInventory inventory)
    {
        this.inventory = inventory;
        Title = "Video inputs";
        Width = 820;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (System.Windows.Media.Brush)FindResource("BasePlane");

        var add = new Button { Content = "+ Add input", Margin = new Thickness(0, 0, 8, 0) };
        add.Click += (_, _) =>
        {
            inventory.Create($"Input {inventory.Entries.Count + 1}");
            Build();
        };
        var refresh = new Button { Content = "Look for devices again" };
        refresh.Click += async (_, _) => await FindDevicesAsync();
        var done = new Button { Content = "Done", IsDefault = true, IsCancel = true, MinWidth = 90, Style = (Style)FindResource("AccentButton") };
        done.Click += (_, _) => Close();
        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(done, Dock.Right);
        footer.Children.Add(done);
        footer.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { add, refresh } });

        var help = new TextBlock
        {
            Text = "Each input appears as a tile in the Media tab; click it to show it under the slides (the Video Input layer). "
                + "NDI sources need NDI Tools; DeckLink inputs need Blackmagic Desktop Video. Cameras may ask for permission in Windows Settings › Privacy › Camera.",
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)FindResource("Muted"),
            Margin = new Thickness(0, 0, 0, 10),
        };
        var layout = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(help, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(help);
        layout.Children.Add(footer);
        layout.Children.Add(new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = layout;
        Loaded += async (_, _) =>
        {
            Build();
            await FindDevicesAsync();
        };
    }

    private async Task FindDevicesAsync()
    {
        cameras = await CameraDevices.ListAsync();
        (ndiSources, deckLinks) = await Task.Run(() =>
        {
            IReadOnlyList<string> ndi = [];
            IReadOnlyList<DeckLinkDevice> cards = [];
            try
            {
                if (NdiLibrary.TryLoad(out var library, out _))
                {
                    using var finder = NdiFinder.Create(library);
                    ndi = finder?.CurrentSourceNames(2500) ?? [];
                }
            }
            catch (Exception ex) { Log.Warn($"Looking for NDI sources failed: {ex.Message}"); }
            try { cards = [.. DeckLinkRuntime.Devices().Where(d => d.SupportsCapture)]; }
            catch (Exception ex) { Log.Warn($"Looking for DeckLink inputs failed: {ex.Message}"); }
            return (ndi, cards);
        });
        Build();
    }

    private IReadOnlyList<Choice> Sources(VideoInputInventory.Entry entry)
    {
        var choices = new List<Choice> { new(null, "Choose…") };
        choices.AddRange(cameras.Select(c => new Choice($"camera|{c.Id}", $"Camera: {c.Name}")));
        choices.AddRange(ndiSources.Select(n => new Choice($"ndi|{n}", $"NDI: {n}")));
        choices.AddRange(deckLinks.Select(d => new Choice($"decklink|{d.PersistentId}", $"DeckLink: {d.Name}")));
        if (entry.IsAssigned && choices.All(c => c.Value != Key(entry.Kind, entry.SourceId)))
            choices.Add(new Choice(Key(entry.Kind, entry.SourceId), $"{entry.Kind}: {entry.SourceId} (not found right now)"));
        return choices;
    }

    private static string? Key(CaptureSourceKind? kind, string? source) => kind switch
    {
        CaptureSourceKind.Camera => $"camera|{source}",
        CaptureSourceKind.Ndi => $"ndi|{source}",
        CaptureSourceKind.Decklink => $"decklink|{source}",
        _ => null,
    };

    private void Build()
    {
        rows.Children.Clear();
        if (inventory.Entries.Count == 0)
            rows.Children.Add(new TextBlock { Text = "No inputs yet. Press “+ Add input”.", Style = (Style)FindResource("Muted") });
        foreach (var entry in inventory.Entries)
        {
            var id = entry.Id;
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            foreach (var width in new[] { 200.0, double.NaN, 120, 90 })
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = double.IsNaN(width) ? new GridLength(1, GridUnitType.Star) : new GridLength(width) });
            var name = new TextBox { Text = entry.Name, Margin = new Thickness(0, 0, 8, 0) };
            name.LostFocus += (_, _) =>
            {
                if (name.Text.Trim() is { Length: > 0 } text && text != entry.Name) inventory.Rename(id, text);
            };
            var source = new ComboBox { ItemsSource = Sources(entry), Margin = new Thickness(0, 0, 8, 0) };
            source.SelectedItem = source.Items.Cast<Choice>().FirstOrDefault(c => c.Value == Key(entry.Kind, entry.SourceId)) ?? source.Items[0];
            source.SelectionChanged += (_, _) =>
            {
                if (source.SelectedItem is not Choice { Value: { } value }) return;
                var parts = value.Split('|', 2);
                CaptureSourceKind kind = parts[0] switch { "ndi" => CaptureSourceKind.Ndi, "decklink" => CaptureSourceKind.Decklink, _ => CaptureSourceKind.Camera };
                inventory.AssignDevice(id, kind, parts[1]);
            };
            var delay = new ComboBox
            {
                ItemsSource = Enumerable.Range(0, 16).Select(f => new Choice(f.ToString(), f == 0 ? "No delay" : $"{f} frames")).ToList(),
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = "Hold the picture back so lips line up with the sound",
            };
            delay.SelectedIndex = Math.Clamp(entry.DelayFrames ?? 0, 0, 15);
            delay.SelectionChanged += (_, _) => inventory.SetDelay(id, delay.SelectedIndex);
            var remove = new Button { Content = "Remove" };
            remove.Click += (_, _) =>
            {
                inventory.Remove(id);
                Build();
            };
            Grid.SetColumn(source, 1);
            Grid.SetColumn(delay, 2);
            Grid.SetColumn(remove, 3);
            row.Children.Add(name);
            row.Children.Add(source);
            row.Children.Add(delay);
            row.Children.Add(remove);
            rows.Children.Add(row);
        }
    }
}
