using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using MxU.Slides.Broadcast.Control;
using MxU.Slides.Broadcast.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// Stream &amp; Record: choose or edit a preset (record only, or stream to some destinations
/// and maybe record too), start and stop it, and watch each destination's health. Below,
/// the destinations themselves (YouTube, Facebook, any RTMP server) with their stream keys,
/// which are kept encrypted for this Windows user and never written into the library.
/// </summary>
public sealed class BroadcastWindow : Window
{
    private sealed record Choice<T>(T Value, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly MainViewModel model;
    private readonly ComboBox presetCombo = new() { MinWidth = 260 };
    private readonly StackPanel editor = new();
    private readonly StackPanel destinationRows = new();
    private readonly StackPanel statusLines = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button startStop = new() { MinWidth = 140, Padding = new Thickness(14, 6, 14, 6) };
    private readonly TextBlock caption = new() { FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private bool loading;

    public BroadcastWindow(MainViewModel model)
    {
        this.model = model;
        Title = "Stream & Record";
        Width = 760;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (System.Windows.Media.Brush)FindResource("BasePlane");

        var newPreset = new Button { Content = "+ New preset", Margin = new Thickness(8, 0, 0, 0) };
        newPreset.Click += (_, _) => NewPreset();
        var deletePreset = new Button { Content = "Delete", Margin = new Thickness(6, 0, 0, 0) };
        deletePreset.Click += (_, _) => DeletePreset();
        presetCombo.SelectionChanged += (_, _) =>
        {
            if (!loading) BuildEditor();
        };
        var presetRow = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        presetRow.Children.Add(Label("Preset"));
        DockPanel.SetDock(deletePreset, Dock.Right);
        DockPanel.SetDock(newPreset, Dock.Right);
        presetRow.Children.Add(deletePreset);
        presetRow.Children.Add(newPreset);
        presetRow.Children.Add(presetCombo);

        startStop.Click += (_, _) => StartOrStop();
        var openFolder = new Button { Content = "Open recordings folder", Margin = new Thickness(8, 0, 0, 0) };
        openFolder.Click += (_, _) => OpenRecordings();
        var controls = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(startStop, Dock.Left);
        DockPanel.SetDock(openFolder, Dock.Right);
        controls.Children.Add(startStop);
        controls.Children.Add(openFolder);
        caption.Margin = new Thickness(12, 0, 0, 0);
        controls.Children.Add(caption);

        var addYouTube = new Button { Content = "+ YouTube", Margin = new Thickness(0, 0, 6, 0) };
        addYouTube.Click += (_, _) => AddDestination("YouTube", "rtmps://a.rtmps.youtube.com/live2");
        var addFacebook = new Button { Content = "+ Facebook", Margin = new Thickness(0, 0, 6, 0) };
        addFacebook.Click += (_, _) => AddDestination("Facebook", "rtmps://live-api-s.facebook.com:443/rtmp/");
        var addOther = new Button { Content = "+ Other RTMP server" };
        addOther.Click += (_, _) => AddDestination("Stream server", "rtmp://");

        var done = new Button { Content = "Done", IsDefault = true, IsCancel = true, MinWidth = 90, Style = (Style)FindResource("AccentButton") };
        done.Click += (_, _) => Close();
        DockPanel.SetDock(done, Dock.Right);

        var body = new StackPanel { Margin = new Thickness(18) };
        body.Children.Add(Muted("Records the audience program (what the audience sees and hears from this app) and streams it live. "
            + "Recordings are MP4 files; a crash or power cut still leaves a file that plays."));
        body.Children.Add(Section("PRESET"));
        body.Children.Add(presetRow);
        body.Children.Add(editor);
        body.Children.Add(controls);
        body.Children.Add(statusLines);
        body.Children.Add(Section("DESTINATIONS"));
        body.Children.Add(Muted("Where streams go. Paste the server URL and stream key from the platform's live dashboard. "
            + "Keys are kept encrypted for your Windows user, never in the library."));
        body.Children.Add(destinationRows);
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), Children = { addYouTube, addFacebook, addOther } });

        var footer = new DockPanel { Margin = new Thickness(18, 0, 18, 14), LastChildFill = false };
        footer.Children.Add(done);
        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body });
        Content = root;

        model.EnsureStreamPreset();
        LoadPresets(model.StreamPresets().FirstOrDefault()?.Id);
        BuildDestinations();
        model.BroadcastChanged += ShowStatus;
        Closed += (_, _) => model.BroadcastChanged -= ShowStatus;
        ShowStatus();
    }

    private StreamRecordPreset? Preset => (presetCombo.SelectedItem as Choice<StreamRecordPreset>)?.Value;

    private void LoadPresets(string? selectId)
    {
        loading = true;
        var choices = model.StreamPresets().Select(p => new Choice<StreamRecordPreset>(p, p.Name)).ToList();
        presetCombo.ItemsSource = choices;
        presetCombo.SelectedItem = choices.FirstOrDefault(c => c.Value.Id == selectId) ?? choices.FirstOrDefault();
        loading = false;
        BuildEditor();
    }

    private void NewPreset()
    {
        var preset = new StreamRecordPreset
        {
            Name = "Live stream", PresetKind = StreamPresetKind.Stream, Width = 1920, Height = 1080, FrameRate = 30,
            VideoBitrateKbps = 6000, RecordWhileStreaming = true,
            DestinationIds = [.. model.Library.All<StreamDestination>().Select(d => d.Id)],
        };
        model.Library.Save(preset);
        LoadPresets(preset.Id);
    }

    private void DeletePreset()
    {
        if (Preset is not { } preset) return;
        if (MessageBox.Show(this, $"Delete the preset \"{preset.Name}\"?", "Stream & Record", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        model.StopBroadcast(preset.Id);
        model.Library.Delete<StreamRecordPreset>(preset.Id);
        model.EnsureStreamPreset();
        LoadPresets(null);
    }

    /// <summary>The selected preset's settings; every change saves straight away.</summary>
    private void BuildEditor()
    {
        editor.Children.Clear();
        if (Preset is not { } preset) return;

        var name = new TextBox { Text = preset.Name };
        name.LostFocus += (_, _) =>
        {
            if (name.Text.Trim() is not { Length: > 0 } typed || typed == preset.Name) return;
            preset.Name = typed;
            Save(preset);
            LoadPresets(preset.Id);
        };
        editor.Children.Add(Row("Name", name));

        var kinds = new[]
        {
            new Choice<StreamPresetKind>(StreamPresetKind.RecordOnly, "Record only"),
            new Choice<StreamPresetKind>(StreamPresetKind.Stream, "Stream live (to the destinations ticked below)"),
        };
        var kind = new ComboBox { ItemsSource = kinds, SelectedItem = kinds.First(k => k.Value == preset.ResolvedKind()) };
        kind.SelectionChanged += (_, _) =>
        {
            preset.PresetKind = ((Choice<StreamPresetKind>)kind.SelectedItem).Value;
            Save(preset);
            BuildEditor();
        };
        editor.Children.Add(Row("What it does", kind));

        var sizes = new[] { new Choice<(int, int)>((1280, 720), "720p (1280×720)"), new Choice<(int, int)>((1920, 1080), "1080p (1920×1080)") };
        var size = new ComboBox
        {
            ItemsSource = sizes, Width = 170,
            SelectedItem = sizes.FirstOrDefault(s => s.Value == (preset.Width ?? 1920, preset.Height ?? 1080)) ?? sizes[1],
        };
        size.SelectionChanged += (_, _) =>
        {
            (preset.Width, preset.Height) = ((Choice<(int, int)>)size.SelectedItem).Value;
            Save(preset);
        };
        var rates = new[] { 24, 25, 30 }.Select(r => new Choice<int>(r, $"{r} fps")).ToList();
        var rate = new ComboBox
        {
            ItemsSource = rates, Width = 90, Margin = new Thickness(8, 0, 0, 0),
            SelectedItem = rates.FirstOrDefault(r => r.Value == preset.FrameRate) ?? rates[2],
        };
        rate.SelectionChanged += (_, _) =>
        {
            preset.FrameRate = ((Choice<int>)rate.SelectedItem).Value;
            Save(preset);
        };
        var bitrate = new TextBox { Width = 70, Margin = new Thickness(8, 0, 6, 0), Text = (preset.VideoBitrateKbps ?? 6000).ToString(CultureInfo.InvariantCulture) };
        bitrate.LostFocus += (_, _) =>
        {
            if (!int.TryParse(bitrate.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kbps)) return;
            preset.VideoBitrateKbps = Math.Clamp(kbps, 500, 50_000);
            Save(preset);
        };
        var quality = new StackPanel { Orientation = Orientation.Horizontal };
        quality.Children.Add(size);
        quality.Children.Add(rate);
        quality.Children.Add(bitrate);
        quality.Children.Add(new TextBlock { Text = "kbps", VerticalAlignment = VerticalAlignment.Center, Style = (Style)FindResource("Muted") });
        editor.Children.Add(Row("Picture", quality));
        editor.Children.Add(Muted("6000 kbps suits 1080p streams; recordings look better at 8000 or more. Check your upload speed: a stream needs about 1.5× its bitrate.", 120));

        if (preset.ResolvedKind() != StreamPresetKind.Stream) return;
        var record = new CheckBox { Content = "Also record while streaming", IsChecked = preset.RecordWhileStreaming == true };
        record.Click += (_, _) =>
        {
            preset.RecordWhileStreaming = record.IsChecked == true;
            Save(preset);
        };
        editor.Children.Add(Row("", record));
        var picks = new StackPanel();
        var destinations = model.Library.All<StreamDestination>().OrderBy(d => d.Name).ToList();
        foreach (var destination in destinations)
        {
            var pick = new CheckBox { Content = destination.Name, IsChecked = preset.DestinationIds?.Contains(destination.Id) == true, Margin = new Thickness(0, 0, 0, 3) };
            pick.Click += (_, _) =>
            {
                var ids = preset.DestinationIds ?? [];
                ids.Remove(destination.Id);
                if (pick.IsChecked == true) ids.Add(destination.Id);
                preset.DestinationIds = ids;
                Save(preset);
            };
            picks.Children.Add(pick);
        }
        if (destinations.Count == 0) picks.Children.Add(Muted("No destinations yet: add one below."));
        editor.Children.Add(Row("Stream to", picks));
    }

    private void Save(StreamRecordPreset preset) => model.Library.Save(preset);

    // MARK: Start / stop and status

    private bool PresetRunning(StreamRecordPreset preset) =>
        model.IsBroadcasting && model.Broadcast.RunningCount(preset.Id) > 0;

    private void StartOrStop()
    {
        if (Preset is not { } preset) return;
        if (PresetRunning(preset))
        {
            var what = preset.IsRecordOnly() ? "recording" : "stream";
            if (MessageBox.Show(this, $"Stop the {what} \"{preset.Name}\"?", "Stream & Record", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            model.StopBroadcast(preset.Id);
            return;
        }
        if (model.Broadcast.StartWouldReplace(preset.Id) is { } replacing
            && MessageBox.Show(this, $"This ends the stream \"{replacing}\" that's live now. Go ahead?", "Stream & Record", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;
        if (model.StartBroadcast(preset) is { } problem) MessageBox.Show(this, problem, "Stream & Record");
    }

    private void ShowStatus()
    {
        var preset = Preset;
        var running = preset is not null && PresetRunning(preset);
        startStop.Content = running ? "■ Stop" : preset?.IsRecordOnly() == true ? "● Start recording" : "● Go live";
        if (running) startStop.ClearValue(StyleProperty);
        else startStop.Style = (Style)FindResource("AccentButton");
        caption.Text = model.BroadcastCaption;
        caption.Foreground = model.BroadcastTint switch
        {
            BroadcastStatus.ChipTint.Red => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE5, 0x48, 0x4D)),
            BroadcastStatus.ChipTint.Orange => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF5, 0xA6, 0x23)),
            BroadcastStatus.ChipTint.Green => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3F, 0xB9, 0x50)),
            _ => (System.Windows.Media.Brush)FindResource("Text"),
        };
        statusLines.Children.Clear();
        if (!model.IsBroadcasting && model.Broadcast.Recording.Sessions.Count == 0) return;
        foreach (var session in model.Broadcast.Streaming.Live?.Sessions ?? [])
            statusLines.Children.Add(Muted(BroadcastStatus.HealthLine(session)));
        foreach (var session in model.Broadcast.Recording.Sessions)
        {
            var line = session.IsRecording
                ? BroadcastStatus.RecordingHealthLine(session)
                : $"{session.TargetName} — {session.Status.State.Kind}{(session.Status.State.Message is { } m ? $": {m}" : "")} · {Path.GetFileName(session.FilePath)}";
            statusLines.Children.Add(Muted(line));
        }
    }

    private void OpenRecordings()
    {
        var folder = model.Broadcast.Recording.RecordingsFolder;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
    }

    // MARK: Destinations

    private void BuildDestinations()
    {
        destinationRows.Children.Clear();
        foreach (var destination in model.Library.All<StreamDestination>().OrderBy(d => d.Name))
            destinationRows.Children.Add(DestinationRow(destination));
    }

    private void AddDestination(string name, string url)
    {
        var destination = new StreamDestination { Name = name, Url = url, Transport = TransportFor(url) };
        StreamDestinationLibrary.SaveDestination(model.Library, model.Broadcast.Keys, destination);
        if (Preset is { } preset && preset.ResolvedKind() == StreamPresetKind.Stream)
        {
            preset.DestinationIds = [.. preset.DestinationIds ?? [], destination.Id];
            Save(preset);
        }
        BuildDestinations();
        BuildEditor();
    }

    private UIElement DestinationRow(StreamDestination destination)
    {
        var hasKey = model.Broadcast.Keys.Contains(destination.Id);
        var name = new TextBox { Text = destination.Name, Width = 130, ToolTip = "Its name here" };
        var url = new TextBox { Text = destination.Url, Margin = new Thickness(6, 0, 6, 0), ToolTip = "Server URL (rtmp://, rtmps:// or an HLS https:// ingest)" };
        var key = new PasswordBox { Width = 150, ToolTip = hasKey ? "A key is saved. Paste a new one to replace it." : "Paste the stream key" };
        var keyHint = new TextBlock
        {
            Text = hasKey ? "key saved" : "no key yet", Width = 70, Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, Style = (Style)FindResource("Muted"), FontSize = 11,
        };
        var save = new Button { Content = "Save", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
        save.Click += (_, _) =>
        {
            destination.Name = name.Text.Trim() is { Length: > 0 } typed ? typed : destination.Name;
            destination.Url = url.Text.Trim();
            destination.Transport = TransportFor(destination.Url);
            // A null key leaves the saved one alone; a typed one replaces it.
            destination.StreamKey = key.Password.Length > 0 ? key.Password.Trim() : null;
            StreamDestinationLibrary.SaveDestination(model.Library, model.Broadcast.Keys, destination);
            destination.StreamKey = null;
            BuildDestinations();
            BuildEditor();
        };
        var delete = new Button { Content = "Remove", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
        delete.Click += (_, _) =>
        {
            if (MessageBox.Show(this, $"Remove the destination \"{destination.Name}\" and its stream key?", "Stream & Record", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            StreamDestinationLibrary.DeleteDestination(model.Library, model.Broadcast.Keys, destination.Id);
            BuildDestinations();
            BuildEditor();
        };
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        foreach (var right in new FrameworkElement[] { delete, save, keyHint, key })
        {
            DockPanel.SetDock(right, Dock.Right);
            row.Children.Add(right);
        }
        DockPanel.SetDock(name, Dock.Left);
        row.Children.Add(name);
        row.Children.Add(url);
        return row;
    }

    private static StreamTransport TransportFor(string url) =>
        url.StartsWith("rtmps://", StringComparison.OrdinalIgnoreCase) ? StreamTransport.Rtmps
        : url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? StreamTransport.Hls
        : url.StartsWith("srt://", StringComparison.OrdinalIgnoreCase) ? StreamTransport.Srt
        : StreamTransport.Rtmp;

    // MARK: Layout helpers

    private TextBlock Section(string text) => new() { Text = text, Style = (Style)FindResource("SectionLabel"), Margin = new Thickness(0, 16, 0, 6) };

    private TextBlock Label(string text)
    {
        var label = new TextBlock { Text = text, Width = 120, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(label, Dock.Left);
        return label;
    }

    private TextBlock Muted(string text, double indent = 0) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11, Style = (Style)FindResource("Muted"),
        Margin = new Thickness(indent, 0, 0, 6),
    };

    private DockPanel Row(string label, UIElement content)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        row.Children.Add(Label(label));
        row.Children.Add(content);
        return row;
    }
}
