using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using MxU.Slides.Audio;
using MxU.Slides.Audio.Library;
using MxU.Slides.Audio.Routing;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// Settings › Sound: where the music plays, where video sound plays, this PC's audio outputs
/// (each a device, a channel pair or one channel, and a playout delay) and the music's
/// fades. Port of the Mac's AudioOutputsSection and AudioOutputDetail (SettingsPanes.swift),
/// with the two "plays on" choices the Mac makes in its mixer (the main mix's output and the
/// media mix), and the fade and crossfade settings from the Audio module's header. Every
/// change applies and saves at once; Cancel doesn't undo them, as with Timecode in.
/// <para>Built in code like the other editors; the settings window holds it in one place.</para>
/// </summary>
public sealed class SoundSettingsPanel : UserControl
{
    private const int DelayStep = 5;

    private readonly StackPanel root = new();
    private readonly TextBlock videoNow = EditorKit.Muted("", 11, 0);
    private readonly DispatcherTimer statusTimer;
    private MainViewModel? model;
    private string? expandedOutputId;
    private bool building;

    public SoundSettingsPanel()
    {
        Content = root;
        statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => ShowVideoNow(), Dispatcher);
        Loaded += (_, _) =>
        {
            model ??= Window.GetWindow(this)?.Owner?.DataContext as MainViewModel ?? Application.Current.MainWindow?.DataContext as MainViewModel;
            if (model is null) return;
            model.SoundRoutingChanged += Rebuild;
            Rebuild();
            statusTimer.Start();
        };
        Unloaded += (_, _) =>
        {
            statusTimer.Stop();
            if (model is not null) model.SoundRoutingChanged -= Rebuild;
        };
    }

    /// <summary>The self-test: opens one output's details.</summary>
    internal void Expand(string outputId)
    {
        expandedOutputId = outputId;
        Rebuild();
    }

    private AudioRoutingTable Routing => model!.SoundRouting;

    private void Rebuild()
    {
        if (model is null || building) return;
        building = true;
        try
        {
            root.Children.Clear();
            root.Children.Add(PlaysOnRow("Music plays on", Routing.MusicOutputToken, music: true,
                "Every playlist, song and timeline's music. “No device” still records and streams it."));
            root.Children.Add(PlaysOnRow("Video sound on", Routing.VideoOutputToken, music: false,
                "The sound of foreground videos. Their pictures stay in step with it on any device."));
            videoNow.Margin = new Thickness(120, 0, 0, 10);
            root.Children.Add(videoNow);
            ShowVideoNow();

            root.Children.Add(EditorKit.Section("AUDIO OUTPUTS", 8));
            root.Children.Add(EditorKit.Muted("A named place sound goes: a device, a pair of its channels (or one channel), and how much to hold it back to line up with a late screen.", 11));
            var devices = Routing.Devices;
            foreach (var output in Routing.Outputs.Entries) root.Children.Add(OutputCard(output, devices));
            var add = EditorKit.Button("Add Output…", () => { }, "An output on any device, a pair of its channels, or one per channel");
            add.HorizontalAlignment = HorizontalAlignment.Left;
            add.Click += (_, _) => EditorKit.Open(AddMenu(devices), add);
            root.Children.Add(add);

            root.Children.Add(EditorKit.Section("MUSIC FADES", 14));
            root.Children.Add(FadeRow());
            root.Children.Add(CrossfadeRow());
            root.Children.Add(EditorKit.Muted("These apply straight away and belong to this PC. A playlist can choose its own crossfade (Music › Manage music).", 11, 0));
        }
        finally
        {
            building = false;
        }
    }

    private void ShowVideoNow() => videoNow.Text = model is null ? "" : $"Now: {model.VideoSoundSummary()}";

    // MARK: Where music and video sound play

    private sealed record Choice(string? Token, string Title)
    {
        public override string ToString() => Title;
    }

    private DockPanel PlaysOnRow(string label, string? token, bool music, string tooltip)
    {
        var choices = new List<Choice>();
        if (!music) choices.Add(new Choice(null, model!.SoundTokenTitle(null)));
        choices.Add(new Choice(AudioMixInventory.DefaultOutputId, model!.SoundTokenTitle(AudioMixInventory.DefaultOutputId)));
        choices.AddRange(Routing.Outputs.Entries.Select(o => new Choice(o.Id, model.SoundTokenTitle(o.Id, video: !music))));
        choices.Add(new Choice(AudioMixInventory.NoOutputId, model.SoundTokenTitle(AudioMixInventory.NoOutputId, video: !music)));
        if (token is not null && choices.All(c => c.Token != token)) choices.Add(new Choice(token, model.SoundTokenTitle(token, video: !music)));
        var box = new ComboBox { ItemsSource = choices, MinWidth = 300, ToolTip = tooltip };
        box.SelectedItem = choices.FirstOrDefault(c => c.Token == token) ?? choices[0];
        box.SelectionChanged += (_, _) =>
        {
            if (building || box.SelectedItem is not Choice picked) return;
            if (music) Routing.SetMusicOutput(picked.Token!);
            else Routing.SetVideoOutput(picked.Token);
        };
        return EditorKit.Row(label, box, 120, 8);
    }

    // MARK: Outputs

    private Border OutputCard(AudioOutputEntry output, IReadOnlyList<AudioOutputDevice> devices)
    {
        var id = output.Id;
        var expanded = expandedOutputId == id;
        var header = new DockPanel();
        var edit = EditorKit.Ghost(expanded ? "Done" : "Edit", expanded ? "Close" : "Change its device, channels or delay", () =>
        {
            expandedOutputId = expanded ? null : id;
            Rebuild();
        });
        DockPanel.SetDock(edit, Dock.Right);
        header.Children.Add(edit);
        if (AudioOutputWords.ShowsNotConnected(output, devices))
        {
            var missing = new TextBlock { Text = AudioOutputWords.NotConnected, FontSize = 11, Foreground = EditorKit.Res<Brush>("Warn"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
            DockPanel.SetDock(missing, Dock.Right);
            header.Children.Add(missing);
        }
        header.Children.Add(EditorKit.Icon("audio", 14));
        var words = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
        words.Children.Add(new TextBlock { Text = output.Name, FontSize = 13 });
        words.Children.Add(EditorKit.Faint(AudioOutputWords.Subtitle(output, devices), 11));
        header.Children.Add(words);
        var body = new StackPanel { Children = { header } };
        if (expanded) body.Children.Add(OutputDetail(output, devices));
        return EditorKit.Card(body);
    }

    private StackPanel OutputDetail(AudioOutputEntry output, IReadOnlyList<AudioOutputDevice> devices)
    {
        var id = output.Id;
        var detail = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        detail.Children.Add(EditorKit.Row("Name", EditorKit.CommitBox(output.Name, typed =>
        {
            Routing.Outputs.Rename(id, typed);
            return Routing.Outputs.Entry(id)?.Name ?? typed;
        }), 110));

        // The device: the system default, none, or one of this PC's (and the one it names, if unplugged).
        var deviceChoices = new List<Choice> { new(null, "System default"), new(AudioOutputInventory.NoDeviceUid, "No device (streams and recordings only)") };
        deviceChoices.AddRange(devices.Select(d => new Choice(d.Uid, d.ChannelCount > 2 ? $"{d.Name} ({d.ChannelCount} channels)" : d.Name)));
        if (output.DeviceUid is { } uid && !output.IsDeviceless && devices.All(d => d.Uid != uid)) deviceChoices.Add(new Choice(uid, "Device (not connected)"));
        var device = new ComboBox { ItemsSource = deviceChoices, MinWidth = 300 };
        device.SelectedItem = deviceChoices.FirstOrDefault(c => c.Token == output.DeviceUid) ?? deviceChoices[0];
        device.SelectionChanged += (_, _) =>
        {
            if (building || device.SelectedItem is not Choice picked) return;
            Routing.Outputs.AssignDevice(id, picked.Token, 0);
        };
        detail.Children.Add(EditorKit.Row("Device", device, 110));

        // Channels, on an interface with more than two: each pair, then each channel on its own (mono).
        if (devices.FirstOrDefault(d => d.Uid == output.DeviceUid) is { ChannelCount: > 2 } wide)
        {
            var channelChoices = new List<Choice>();
            for (var offset = 0; offset + 1 < wide.ChannelCount; offset += 2) channelChoices.Add(new Choice($"pair:{offset}", $"{offset + 1}-{offset + 2}"));
            for (var channel = 0; channel < wide.ChannelCount; channel++) channelChoices.Add(new Choice($"mono:{channel}", $"{channel + 1} (mono)"));
            var current = output.MonoChannel is { } mono ? $"mono:{mono}" : $"pair:{output.ChannelOffset}";
            var channels = new ComboBox { ItemsSource = channelChoices, MinWidth = 140, HorizontalAlignment = HorizontalAlignment.Left, MaxDropDownHeight = 360 };
            channels.SelectedItem = channelChoices.FirstOrDefault(c => c.Token == current) ?? channelChoices[0];
            channels.SelectionChanged += (_, _) =>
            {
                if (building || channels.SelectedItem is not Choice picked) return;
                var parts = picked.Token!.Split(':');
                var number = int.Parse(parts[1], CultureInfo.InvariantCulture);
                if (parts[0] == "mono") Routing.Outputs.AssignDevice(id, wide.Uid, 0, number);
                else Routing.Outputs.AssignDevice(id, wide.Uid, number);
            };
            detail.Children.Add(EditorKit.Row("Channels", channels, 110));
        }

        detail.Children.Add(EditorKit.Row("Playout delay", DelayBox(id, output.DelayMs ?? 0), 110));
        detail.Children.Add(EditorKit.Muted("Holds this output's sound back to match slower rooms or devices (0–2000 ms).", 11));

        var remove = EditorKit.Button("Remove Output…", () => Remove(output));
        remove.HorizontalAlignment = HorizontalAlignment.Left;
        remove.IsEnabled = Routing.Outputs.Entries.Count > 1;
        detail.Children.Add(remove);
        return detail;
    }

    // A delay in milliseconds, typed or stepped in fives.
    private StackPanel DelayBox(string id, int value)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var current = value;
        TextBox? box = null;
        void Store(int ms)
        {
            Routing.Outputs.SetDelay(id, ms);
            current = Routing.Outputs.Entry(id)?.DelayMs ?? 0;
            if (box is not null) box.Text = current.ToString(CultureInfo.InvariantCulture);
        }
        box = EditorKit.CommitBox(value.ToString(CultureInfo.InvariantCulture), typed =>
        {
            if (int.TryParse(typed.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var ms)) Store(ms);
            return current.ToString(CultureInfo.InvariantCulture);
        }, width: 64);
        box.TextAlignment = TextAlignment.Right;
        panel.Children.Add(box);
        panel.Children.Add(new TextBlock { Text = "ms", Foreground = EditorKit.Res<Brush>("MutedText"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) });
        panel.Children.Add(EditorKit.Ghost("−", "5 ms less", () => Store(current - DelayStep), 13));
        panel.Children.Add(EditorKit.Ghost("+", "5 ms more", () => Store(current + DelayStep), 13));
        return panel;
    }

    private void Remove(AudioOutputEntry output)
    {
        var owner = Window.GetWindow(this);
        var answer = owner is null ? MessageBoxResult.Yes
            : MessageBox.Show(owner, "Mixes sending here drop the send.", $"Remove “{output.Name}”?", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        // Whatever played there goes back to the system default (rather than to an output that's gone).
        if (Routing.MusicOutputToken == output.Id) Routing.SetMusicOutput(AudioMixInventory.DefaultOutputId);
        if (Routing.VideoOutputToken == output.Id) Routing.SetVideoOutput(AudioMixInventory.DefaultOutputId);
        expandedOutputId = null;
        Routing.Outputs.Remove(output.Id);
    }

    private ContextMenu AddMenu(IReadOnlyList<AudioOutputDevice> devices)
    {
        var menu = new ContextMenu();
        menu.Items.Add(EditorKit.Command("Empty Output — assign a device later", () => Open(Routing.Outputs.Create())));
        foreach (var device in devices)
            menu.Items.Add(EditorKit.Command(device.Name, () =>
            {
                var made = Routing.Outputs.Create(device.Name);
                Routing.Outputs.AssignDevice(made.Id, device.Uid, 0);
                Open(made);
            }));
        var wide = devices.Where(d => d.ChannelCount > 2).ToList();
        if (wide.Count > 0)
        {
            menu.Items.Add(EditorKit.Separator());
            foreach (var device in wide)
                menu.Items.Add(EditorKit.Command($"An Output for Every Channel of {device.Name} ({device.ChannelCount})", () => Routing.Outputs.AddOutputPerChannel(device)));
        }
        return menu;
    }

    private void Open(AudioOutputEntry made)
    {
        expandedOutputId = made.Id;
        Rebuild();
    }

    // MARK: Fades

    private DockPanel FadeRow()
    {
        var on = new CheckBox { Content = "Fade music in and out on play, pause and stop", IsChecked = model!.MusicFadeEnabled, VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock { Width = 44, VerticalAlignment = VerticalAlignment.Center, Foreground = EditorKit.Res<Brush>("MutedText"), TextAlignment = TextAlignment.Right };
        var slider = new Slider { Minimum = 0, Maximum = 5, TickFrequency = 0.25, IsSnapToTickEnabled = true, Width = 150, Value = model.MusicFadeSeconds, VerticalAlignment = VerticalAlignment.Center, IsEnabled = model.MusicFadeEnabled };
        label.Text = PlaylistCrossfade.Label(slider.Value);
        on.Click += (_, _) =>
        {
            model.MusicFadeEnabled = on.IsChecked == true;
            slider.IsEnabled = model.MusicFadeEnabled;
        };
        slider.ValueChanged += (_, e) =>
        {
            label.Text = PlaylistCrossfade.Label(e.NewValue);
            if (!building) model.MusicFadeSeconds = e.NewValue;
        };
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(label, Dock.Right);
        DockPanel.SetDock(slider, Dock.Right);
        row.Children.Add(label);
        row.Children.Add(slider);
        row.Children.Add(on);
        return row;
    }

    private DockPanel CrossfadeRow()
    {
        var label = new TextBlock { Width = 44, VerticalAlignment = VerticalAlignment.Center, Foreground = EditorKit.Res<Brush>("MutedText"), TextAlignment = TextAlignment.Right };
        var slider = new Slider { Minimum = 0, Maximum = PlaylistCrossfade.MaximumSeconds, TickFrequency = 0.5, IsSnapToTickEnabled = true, Width = 150, Value = model!.MusicCrossfadeSeconds, VerticalAlignment = VerticalAlignment.Center };
        label.Text = PlaylistCrossfade.Label(slider.Value);
        slider.ValueChanged += (_, e) =>
        {
            label.Text = PlaylistCrossfade.Label(e.NewValue);
            if (!building) model.MusicCrossfadeSeconds = e.NewValue;
        };
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(label, Dock.Right);
        DockPanel.SetDock(slider, Dock.Right);
        row.Children.Add(label);
        row.Children.Add(slider);
        row.Children.Add(new TextBlock { Text = "Crossfade between songs", VerticalAlignment = VerticalAlignment.Center, ToolTip = "For playlists that don't choose their own" });
        return row;
    }
}
