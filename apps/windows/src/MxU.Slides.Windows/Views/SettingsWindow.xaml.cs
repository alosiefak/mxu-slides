using System.Windows;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Threading;
using MxU.Slides.Audio;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Control;
using MxU.Slides.LocalApi;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.Outputs;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

public partial class SettingsWindow : Window
{
    private sealed record DisplayChoice(string? DeviceName, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record TransitionChoice(OutputTransition Value, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record TimecodeChoice(TimecodeFollower.Input Input, string Label)
    {
        public override string ToString() => Label;
    }

    private bool loadingTimecode = true;

    private sealed class MidiRow(MidiCommand command, int note) : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
    {
        private int note = note;

        public MidiCommand Command { get; } = command;
        public string Name => Command.DisplayName() + (Command.UsesVelocityIndex() ? "  (velocity = number)" : "");

        public int Note
        {
            get => note;
            set
            {
                if (SetProperty(ref note, Math.Clamp(value, 0, 127))) OnPropertyChanged(nameof(NoteName));
            }
        }

        public string NoteName => MidiCommandMap.NoteName(note);
    }

    private readonly MainViewModel model;
    private readonly OutputManager outputs;
    private readonly List<MidiRow> midiRows;

    public SettingsWindow(MainViewModel model, OutputManager outputs)
    {
        this.model = model;
        this.outputs = outputs;
        InitializeComponent();
        var s = model.Settings;
        LoadDisplays();
        OutputsAtLaunch.IsChecked = s.OutputsOnAtLaunch;
        PreventSleep.IsChecked = s.PreventSleep;

        TransitionCombo.ItemsSource = new[]
        {
            new TransitionChoice(OutputTransition.Dissolve, "Dissolve"),
            new TransitionChoice(OutputTransition.FadeThroughBlack, "Fade through black"),
            new TransitionChoice(OutputTransition.Cut, "Cut (instant)"),
        };
        TransitionCombo.SelectedItem = ((TransitionChoice[])TransitionCombo.ItemsSource).First(c => c.Value == s.Transition);
        TransitionSeconds.Value = s.TransitionSeconds;
        MediaTransitionCombo.ItemsSource = TransitionCombo.ItemsSource;
        MediaTransitionCombo.SelectedItem = ((TransitionChoice[])TransitionCombo.ItemsSource).First(c => c.Value == s.MediaTransition);
        MediaTransitionSeconds.Value = s.MediaTransitionSeconds;

        var themes = model.Library.All<Core.Model.Theme>();
        ThemeCombo.ItemsSource = themes;
        ThemeCombo.SelectedItem = themes.FirstOrDefault(t => t.Id == s.DefaultThemeId);
        LinesCombo.ItemsSource = new[] { 1, 2, 3, 4, 5, 6, 8 };
        LinesCombo.SelectedItem = s.LinesPerSlide;
        Volume.Value = s.VideoVolume;
        MidiEnabled.IsChecked = s.MidiInputEnabled;
        MidiChannel.ItemsSource = new[] { "All channels" }.Concat(Enumerable.Range(1, 16).Select(c => $"Channel {c}")).ToList();
        MidiChannel.SelectedIndex = s.MidiMap.Channel ?? 0;
        MidiDevices.Text = $"Connected: {model.MidiStatus}";
        midiRows = MidiCommands.All.Select(c => new MidiRow(c, s.MidiMap.NoteFor(c))).ToList();
        MidiMapRows.ItemsSource = midiRows;
        Loaded += async (_, _) =>
        {
            var ports = await MidiOutputService.ListAsync();
            var choices = new List<DisplayChoice> { new(null, ports.Count == 0 ? "No MIDI outputs connected" : "Nowhere") };
            choices.AddRange(ports.Select(p => new DisplayChoice(p.Id, p.Name)));
            MidiOutCombo.ItemsSource = choices;
            MidiOutCombo.SelectedItem = choices.FirstOrDefault(c => c.DeviceName == s.MidiOutputId) ?? choices[0];
            await LoadTimecodeAsync();
            await model.RefreshMidiDevicesAsync();
            BuildMidiDeviceRows();
        };
        model.MidiDevicesChanged += BuildMidiDeviceRows;
        Closed += (_, _) => model.MidiDevicesChanged -= BuildMidiDeviceRows;
        ApiEnabled.IsChecked = s.LocalApiEnabled;
        ApiPort.Text = s.LocalApiPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ApiConnect.Text = s.LocalApiEnabled
            ? $"{model.ApiStatus}. Remotes on the same Wi-Fi connect here with a key."
            : $"When on, remotes connect at {model.ApiAddress} with a key. Windows may ask to let MxU Slides through the firewall: allow Private networks.";
        NewKeyScope.ItemsSource = ApiScopes.All;
        NewKeyScope.SelectedItem = ApiScope.Control;
        LoadKeys();
        LibraryPath.Text = $"Your songs, services and media live in {s.LibraryPath}. Every day the first launch copies the documents into Backups (the last 14 days are kept).";
    }

    private void LoadDisplays()
    {
        var displays = Displays.All();
        var choices = new List<DisplayChoice> { new(null, displays.Count > 1 ? "Off / preview window" : "Preview window (one screen connected)") };
        choices.AddRange(displays.Select(d => new DisplayChoice(d.DeviceName, d.Label)));
        AudienceCombo.ItemsSource = choices;
        StageCombo.ItemsSource = choices;
        AudienceCombo.SelectedItem = choices.FirstOrDefault(c => c.DeviceName == outputs.AssignedDisplay(OutputRole.Audience)) ?? choices[0];
        StageCombo.SelectedItem = choices.FirstOrDefault(c => c.DeviceName == outputs.AssignedDisplay(OutputRole.Stage)) ?? choices[0];
    }

    // MARK: MIDI devices (apply straight away)

    private void BuildMidiDeviceRows()
    {
        MidiDeviceRows.Children.Clear();
        var (inputs, outputs) = model.MidiPorts;
        var destinations = outputs.Select(o => o.Id).ToHashSet();
        var sources = inputs.Select(i => i.Id).ToHashSet();
        if (model.MidiDevices.Count == 0)
        {
            MidiDeviceRows.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "No devices listed: MIDI control listens to every input.", Style = (Style)FindResource("Muted"), FontSize = 11,
            });
            return;
        }
        var directions = new[] { Core.Model.MIDIDeviceItemDirection.Both, Core.Model.MIDIDeviceItemDirection.Input, Core.Model.MIDIDeviceItemDirection.Output };
        var channels = new[] { "All channels" }.Concat(Enumerable.Range(1, 16).Select(c => $"Channel {c}")).ToList();
        foreach (var device in model.MidiDevices)
        {
            var id = device.Id;
            var enabled = new System.Windows.Controls.CheckBox { IsChecked = device.IsEnabled, VerticalAlignment = VerticalAlignment.Center, ToolTip = "On" };
            enabled.Click += (_, _) => model.UpdateMidiDevice(id, d => d.Enabled = enabled.IsChecked == true);
            var direction = new System.Windows.Controls.ComboBox
            {
                ItemsSource = directions.Select(Core.Control.ResolvedMidiDevice.DirectionName).ToList(),
                SelectedIndex = Array.IndexOf(directions, device.Direction), Width = 90, Margin = new Thickness(6, 0, 0, 0),
            };
            direction.SelectionChanged += (_, _) =>
            {
                if (direction.SelectedIndex >= 0) model.UpdateMidiDevice(id, d => d.Direction = directions[direction.SelectedIndex]);
            };
            var channel = new System.Windows.Controls.ComboBox
            {
                ItemsSource = channels, SelectedIndex = device.Device.Channel ?? 0, Width = 105, Margin = new Thickness(6, 0, 0, 0),
                ToolTip = "Listen on one channel, or all of them",
            };
            channel.SelectionChanged += (_, _) =>
            {
                if (channel.SelectedIndex >= 0) model.UpdateMidiDevice(id, d => d.Channel = channel.SelectedIndex == 0 ? null : channel.SelectedIndex);
            };
            var connected = device.IsConnected(destinations, sources);
            var status = new System.Windows.Controls.TextBlock
            {
                Text = connected ? "connected" : "not connected", Width = 85, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0), Style = (Style)FindResource("Muted"),
            };
            var remove = new System.Windows.Controls.Button { Content = "Remove", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 1, 8, 1) };
            remove.Click += (_, _) => model.RemoveMidiDevice(id);
            var row = new System.Windows.Controls.DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            foreach (var right in new FrameworkElement[] { remove, status, channel, direction })
            {
                System.Windows.Controls.DockPanel.SetDock(right, System.Windows.Controls.Dock.Right);
                row.Children.Add(right);
            }
            System.Windows.Controls.DockPanel.SetDock(enabled, System.Windows.Controls.Dock.Left);
            row.Children.Add(enabled);
            row.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = device.Device.Name, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            MidiDeviceRows.Children.Add(row);
        }
    }

    private async void AddMidiDevice_Click(object sender, RoutedEventArgs e)
    {
        await model.RefreshMidiDevicesAsync();
        var (inputs, outputs) = model.MidiPorts;
        var listed = model.MidiDevices.Select(d => d.Device.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = (UIElement)sender };
        foreach (var found in Core.Control.MidiDeviceResolver.Discovered(outputs, inputs).Where(f => !listed.Contains(f.Name)))
        {
            var item = new System.Windows.Controls.MenuItem { Header = $"{found.Name} ({Core.Control.ResolvedMidiDevice.DirectionName(found.Direction)})" };
            item.Click += (_, _) => model.AddMidiDevice(found.Name, found.Direction, found.Destination?.Id, found.Source?.Id);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "No other MIDI devices are connected", IsEnabled = false });
        menu.IsOpen = true;
    }

    // MARK: Timecode in (applies straight away; the follower saves its own settings)

    private async Task LoadTimecodeAsync()
    {
        var follower = model.Timecode;
        var choices = new List<TimecodeChoice> { new(new TimecodeFollower.Input.None(), "Off") };
        choices.AddRange(AudioDeviceList.InputDevices().Select(d => new TimecodeChoice(new TimecodeFollower.Input.Audio(d.Uid), $"LTC · {d.Name}")));
        choices.AddRange((await MidiInputService.ListAsync()).Select(p => new TimecodeChoice(new TimecodeFollower.Input.Midi(p.Id), $"MTC · {p.Name}")));
        var current = follower.CurrentInput;
        if (current is not TimecodeFollower.Input.None && choices.All(c => c.Input != current))
            choices.Add(new TimecodeChoice(current, "The input used before (not connected now)"));
        TimecodeInput.ItemsSource = choices;
        TimecodeInput.SelectedItem = choices.FirstOrDefault(c => c.Input == current) ?? choices[0];
        TimecodeChannel.ItemsSource = Enumerable.Range(1, 16).Select(c => $"Channel {c}").ToList();
        TimecodeChannel.SelectedIndex = Math.Clamp(follower.Channel, 0, 15);
        TimecodeFollows.IsChecked = follower.Follows;
        TimecodePlaysAudio.IsChecked = follower.PlaysAudio;
        TimecodeOffset.Text = follower.OffsetFrames.ToString(CultureInfo.InvariantCulture);
        loadingTimecode = false;
        TimecodeChannel.IsEnabled = current is TimecodeFollower.Input.Audio;
        var ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        ticker.Tick += (_, _) => ShowTimecodeReading();
        ticker.Start();
        Closed += (_, _) => ticker.Stop();
        ShowTimecodeReading();
    }

    private void ShowTimecodeReading()
    {
        var follower = model.Timecode;
        if (!follower.HasInput)
        {
            TimecodeReadout.Text = "";
            return;
        }
        var (word, tone) = TimecodeFollower.StatusWord(follower.Reading, follower.Follows, follower.NoTimecode);
        TimecodeReadout.Text = follower.Problem ?? $"{TimecodeFollower.LabelText(follower.Reading)}  {word}";
        TimecodeReadout.Foreground = tone switch
        {
            TimecodeStatusTone.Green => new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50)),
            TimecodeStatusTone.Red => new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)),
            _ => (Brush)FindResource("MutedText"),
        };
    }

    private void TimecodeInput_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (loadingTimecode || TimecodeInput.SelectedItem is not TimecodeChoice choice) return;
        model.Timecode.SetInput(choice.Input);
        TimecodeChannel.IsEnabled = choice.Input is TimecodeFollower.Input.Audio;
    }

    private void TimecodeChannel_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!loadingTimecode && TimecodeChannel.SelectedIndex >= 0) model.Timecode.SetChannel(TimecodeChannel.SelectedIndex);
    }

    private void TimecodeFollows_Click(object sender, RoutedEventArgs e) => model.Timecode.SetFollows(TimecodeFollows.IsChecked == true);

    private void TimecodePlaysAudio_Click(object sender, RoutedEventArgs e) => model.Timecode.SetPlaysAudio(TimecodePlaysAudio.IsChecked == true);

    private void TimecodeOffset_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!loadingTimecode && int.TryParse(TimecodeOffset.Text.Replace('−', '-'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var frames))
            model.Timecode.SetOffset(frames);
    }

    private void Identify_Click(object sender, RoutedEventArgs e) => OutputManager.Identify();

    private void Refresh_Click(object sender, RoutedEventArgs e) => LoadDisplays();

    /// <summary>The full Screens window. The two pickers above are a shortcut to its first Audience and Stage screens.</summary>
    private void Screens_Click(object sender, RoutedEventArgs e)
    {
        new ScreensWindow(outputs) { Owner = this }.ShowDialog();
        LoadDisplays();
    }

    private void TransitionSeconds_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        TransitionLabel.Text = $"{e.NewValue:0.0} seconds";

    private void MediaTransitionSeconds_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        MediaTransitionLabel.Text = $"{e.NewValue:0.0} seconds";

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = model.Settings;
        var audience = AudienceCombo.SelectedItem as DisplayChoice;
        var stage = StageCombo.SelectedItem as DisplayChoice;
        if (audience?.DeviceName is not null && audience.DeviceName == stage?.DeviceName)
        {
            MessageBox.Show(this, "Audience and Stage can't use the same display.", "MxU Slides", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var main = Displays.ForWindow(new System.Windows.Interop.WindowInteropHelper(Owner).Handle);
        if (audience?.DeviceName is { } chosen && chosen == main?.DeviceName
            && MessageBox.Show(this, "The audience output would cover this window's screen, and you'd lose sight of the controls. Use it anyway?",
                "MxU Slides", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        outputs.AssignDisplay(OutputRole.Audience, audience?.DeviceName, audience?.DeviceName is null ? null : audience.Label);
        outputs.AssignDisplay(OutputRole.Stage, stage?.DeviceName, stage?.DeviceName is null ? null : stage.Label);
        s.OutputsOnAtLaunch = OutputsAtLaunch.IsChecked == true;
        s.PreventSleep = PreventSleep.IsChecked == true;
        s.Transition = (TransitionCombo.SelectedItem as TransitionChoice)?.Value ?? OutputTransition.Dissolve;
        s.TransitionSeconds = Math.Round(TransitionSeconds.Value, 1);
        s.MediaTransition = (MediaTransitionCombo.SelectedItem as TransitionChoice)?.Value ?? OutputTransition.Dissolve;
        s.MediaTransitionSeconds = Math.Round(MediaTransitionSeconds.Value, 1);
        if (ThemeCombo.SelectedItem is Core.Model.Theme theme) s.DefaultThemeId = theme.Id;
        if (LinesCombo.SelectedItem is int lines) s.LinesPerSlide = lines;
        s.VideoVolume = Volume.Value;
        if (midiRows.GroupBy(r => r.Note).FirstOrDefault(g => g.Count() > 1) is { } clash)
        {
            MessageBox.Show(this, $"MIDI note {clash.Key} ({MidiCommandMap.NoteName(clash.Key)}) is used by more than one command: "
                + string.Join(", ", clash.Select(r => r.Command.DisplayName())) + ".", "MxU Slides", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!int.TryParse(ApiPort.Text.Trim(), out var port) || port is < 1024 or > 65535)
        {
            MessageBox.Show(this, "The API port must be a number from 1024 to 65535.", "MxU Slides", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        s.LocalApiEnabled = ApiEnabled.IsChecked == true;
        s.LocalApiPort = port;
        s.MidiInputEnabled = MidiEnabled.IsChecked == true;
        if (MidiOutCombo.SelectedItem is DisplayChoice midiOut) s.MidiOutputId = midiOut.DeviceName;
        s.MidiMap.Channel = MidiChannel.SelectedIndex <= 0 ? null : MidiChannel.SelectedIndex;
        foreach (var row in midiRows) s.MidiMap.SetNote(row.Note, row.Command);
        s.Save();
        model.ApplyMidiSettings();
        model.ApplyApiSettings();
        if (s.LocalApiEnabled && model.ApiStatus.StartsWith("Couldn't", StringComparison.Ordinal))
            MessageBox.Show(this, $"The Local API {model.ApiStatus.ToLowerInvariant()}", "MxU Slides", MessageBoxButton.OK, MessageBoxImage.Warning);

        // Outputs that now have a display move there; ones set to preview stay where they are.
        foreach (var role in Enum.GetValues<OutputRole>())
        {
            var device = outputs.AssignedDisplay(role);
            if (device is null && outputs.IsOnDisplay(role)) outputs.Hide(role);
        }
        outputs.Apply();
        DialogResult = true;
    }

    // MARK: Local API keys

    private void LoadKeys() => ApiKeyRows.ItemsSource = model.ApiTokens.All;

    private void CopyDefaultKey_Click(object sender, RoutedEventArgs e)
    {
        var secret = model.ApiTokens.EnsureDefaultKey(evenIfPopulated: true);
        LoadKeys();
        if (secret is null) return;
        Clipboard.SetText(secret);
        MessageBox.Show(this, "The default key (Control: runs the show) is on the clipboard.", "MxU Slides", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenApiDocs_Click(object sender, RoutedEventArgs e)
    {
        if (!model.Settings.LocalApiEnabled)
        {
            MessageBox.Show(this, "Turn the API on and press Save first.", "MxU Slides", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo($"{model.ApiAddress}/docs") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open the API docs: {ex.Message}");
        }
    }

    private void NewKey_Click(object sender, RoutedEventArgs e)
    {
        var name = NewKeyName.Text.Trim();
        if (name.Length == 0)
        {
            NewKeyName.Focus();
            return;
        }
        var (_, secret) = model.ApiTokens.Create(name, NewKeyScope.SelectedItem as ApiScope? ?? ApiScope.Control);
        NewKeyName.Text = "";
        LoadKeys();
        Clipboard.SetText(secret);
        MessageBox.Show(this, $"Key for \"{name}\" is on the clipboard:\n\n{secret}\n\nIt is shown only once. Keep it somewhere safe.",
            "MxU Slides", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RevokeKey_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string id || model.ApiTokens.All.FirstOrDefault(t => t.Id == id) is not { } token) return;
        if (MessageBox.Show(this, $"Revoke \"{token.Name}\"? Anything using it stops working straight away.", "MxU Slides",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        model.ApiTokens.Revoke(id);
        LoadKeys();
    }

    private async void ImportProPresenterLibrary_Click(object sender, RoutedEventArgs e)
    {
        var folder = ProImport.ProPresenterImporter.ProPresenterShowDirectory();
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose your ProPresenter folder (the one holding Libraries and Playlists)",
            InitialDirectory = folder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(this) != true) return;
        var scan = new ProImport.ProWorkspaceImporter(model.Library).Scan(dialog.FolderName);
        if (scan.Presentations + scan.Themes + scan.Playlists + scan.Media == 0)
        {
            MessageBox.Show(this, "That folder doesn't look like a ProPresenter library: no presentations, themes, playlists or media were found.",
                "MxU Slides", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this, $"Found {scan.Presentations} presentations, {scan.Playlists} playlists, {scan.Themes} themes, {scan.Media} media files, "
                + $"{scan.Props} props, {scan.Messages} messages and {scan.StageLayouts} stage layouts. Import them?",
                "MxU Slides", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        DialogResult = false;
        await model.ImportProPresenterLibraryAsync(dialog.FolderName);
    }

    private void OpenLibrary_Click(object sender, RoutedEventArgs e) => MainWindow.OpenFolder(model.Settings.LibraryPath);

    private void OpenBackups_Click(object sender, RoutedEventArgs e) => MainWindow.OpenFolder(model.Library.BackupDirectory);

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => MainWindow.OpenFolder(AppPaths.LogDirectory);
}
