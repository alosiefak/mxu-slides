using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using MxU.Slides.Audio;
using MxU.Slides.Broadcast.Control;
using MxU.Slides.Broadcast.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// Stream &amp; Record: choose or edit a preset (record only, or stream to some destinations
/// and maybe record too), start and stop it, and watch each destination's health. A preset says
/// what the picture is (the audience program, one screen such as the stage display, or a video
/// from the library to replay), what the sound is (the program, the Music tab's mix, or a sound
/// input like the mixing desk, with a delay to line it up), and where recordings are filed.
/// Below, the destinations themselves (YouTube, Facebook, any RTMP server) with their stream
/// keys, which are kept encrypted for this Windows user and never written into the library.
/// Port of the Mac's StreamRecordPane (preset detail) and StreamingView.
/// </summary>
public sealed class BroadcastWindow : Window
{
    private sealed record Choice<T>(T Value, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>Where a preset's picture comes from: a screen (null = the program), a library video, or the newest recording.</summary>
    private sealed record PictureChoice(StreamSourceKind Kind, string? ScreenId);

    /// <summary>Where a preset's sound comes from: "program", "mix" (id), "entry" (an input's id) or "device" (a sound device not yet used).</summary>
    private sealed record SoundChoice(string Kind, string? Id = null, string? Name = null);

    private readonly MainViewModel model;
    private readonly ComboBox presetCombo = new() { MinWidth = 260 };
    private readonly StackPanel editor = new();
    private readonly StackPanel destinationRows = new();
    private readonly StackPanel statusLines = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock preview = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button startStop = new() { MinWidth = 140, Padding = new Thickness(14, 6, 14, 6) };
    private readonly Button openCopies = new() { Content = "Open copies folder", Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock caption = new() { FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private bool loading;

    public BroadcastWindow(MainViewModel model)
    {
        this.model = model;
        Title = "Stream & Record";
        // Fits a 1366×768 laptop screen; the content scrolls.
        Width = Math.Min(780, SystemParameters.WorkArea.Width - 40);
        Height = Math.Min(820, SystemParameters.WorkArea.Height - 40);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)FindResource("BasePlane");

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
        openCopies.Click += (_, _) => OpenCopies();
        var controls = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(startStop, Dock.Left);
        DockPanel.SetDock(openCopies, Dock.Right);
        controls.Children.Add(startStop);
        controls.Children.Add(openCopies);
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
        body.Children.Add(Muted("Records or streams what the audience sees (or one particular screen, such as the stage display), "
            + "with this app's sound or a mixing desk's. Recordings are MP4 files filed into the Media tab; a crash or power cut still leaves a file that plays."));
        body.Children.Add(Section("PRESET"));
        body.Children.Add(presetRow);
        body.Children.Add(editor);
        body.Children.Add(preview);
        body.Children.Add(controls);
        body.Children.Add(statusLines);
        body.Children.Add(Section("DESTINATIONS"));
        body.Children.Add(Muted("Where streams go. Paste the server URL and stream key from the platform's live dashboard. "
            + "Keys are kept encrypted for your Windows user, never in the library. (SRT isn't available on Windows yet.)"));
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

    /// <summary>Shows a preset (for the self-test, which pictures each kind).</summary>
    public void Select(string presetId) => LoadPresets(presetId);

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
        if (Preset is not { } preset)
        {
            ShowPreview();
            return;
        }
        var streams = preset.ResolvedKind() == StreamPresetKind.Stream;
        var sourceKind = preset.SourceKind ?? StreamSourceKind.Screen;
        // Record-only presets record a screen; only streams can replay a file.
        if (!streams && sourceKind != StreamSourceKind.Screen)
        {
            preset.SourceKind = null;
            sourceKind = StreamSourceKind.Screen;
            Save(preset);
        }

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

        AddPictureRows(preset, streams, sourceKind);
        AddQualityRows(preset);
        if (sourceKind == StreamSourceKind.Screen) AddSoundRows(preset);
        else editor.Children.Add(Row("Sound", Muted("From the video file.")));
        // A replay isn't recorded again (as on the Mac).
        if (sourceKind == StreamSourceKind.Screen) AddRecordingRows(preset, streams);
        if (streams) AddDestinationPicks(preset);
        ShowPreview();
        ShowStatus();
    }

    // MARK: Picture

    private void AddPictureRows(StreamRecordPreset preset, bool streams, StreamSourceKind sourceKind)
    {
        editor.Children.Add(Section("PICTURE"));
        var choices = new List<Choice<PictureChoice>>
        {
            new(new PictureChoice(StreamSourceKind.Screen, null), "The audience program (what the audience sees)"),
        };
        foreach (var (id, screenName, isStage) in model.BroadcastScreens())
            choices.Add(new(new PictureChoice(StreamSourceKind.Screen, id), $"Screen: {screenName}{(isStage ? " · stage display" : "")}"));
        if (!string.IsNullOrEmpty(preset.CanvasScreenId) && choices.All(c => c.Value.ScreenId != preset.CanvasScreenId))
            choices.Add(new(new PictureChoice(StreamSourceKind.Screen, preset.CanvasScreenId), "A screen that's no longer set up (records the program)"));
        if (streams)
        {
            choices.Add(new(new PictureChoice(StreamSourceKind.MediaItem, null), "A video from the library (replay)"));
            choices.Add(new(new PictureChoice(StreamSourceKind.LatestRecording, null), "The newest recording (replay a recorded service)"));
        }
        var current = sourceKind == StreamSourceKind.Screen
            ? choices.FirstOrDefault(c => c.Value.Kind == StreamSourceKind.Screen && c.Value.ScreenId == NullIfEmpty(preset.CanvasScreenId))
            : choices.FirstOrDefault(c => c.Value.Kind == sourceKind);
        var picture = new ComboBox { ItemsSource = choices, SelectedItem = current ?? choices[0] };
        picture.SelectionChanged += (_, _) =>
        {
            var chosen = ((Choice<PictureChoice>)picture.SelectedItem).Value;
            preset.SourceKind = chosen.Kind == StreamSourceKind.Screen ? null : chosen.Kind;
            if (chosen.Kind == StreamSourceKind.Screen) preset.CanvasScreenId = chosen.ScreenId;
            Save(preset);
            BuildEditor();
        };
        editor.Children.Add(Row("Picture from", picture));

        switch (sourceKind)
        {
            case StreamSourceKind.MediaItem:
                var videos = model.BroadcastVideos().Select(v => new Choice<string>(v.Id, v.Name)).ToList();
                if (videos.Count == 0)
                {
                    editor.Children.Add(Row("Video", Muted("No videos in the library yet: import one in the Media tab.")));
                    break;
                }
                var video = new ComboBox { ItemsSource = videos, SelectedItem = videos.FirstOrDefault(v => v.Value == preset.SourceMediaId) };
                video.SelectionChanged += (_, _) =>
                {
                    if (video.SelectedItem is not Choice<string> chosen) return;
                    preset.SourceMediaId = chosen.Value;
                    Save(preset);
                    ShowPreview();
                };
                editor.Children.Add(Row("Video", video));
                break;
            case StreamSourceKind.LatestRecording:
                var folders = model.MediaFolders().Select(f => new Choice<string?>(f == RecordingLibrary.DefaultFolder ? null : f,
                    f == RecordingLibrary.DefaultFolder ? $"{f} (default)" : f)).ToList();
                var folder = new ComboBox { ItemsSource = folders, Width = 220, SelectedItem = folders.FirstOrDefault(f => f.Value == preset.SourceFolder) ?? folders[0] };
                folder.SelectionChanged += (_, _) =>
                {
                    preset.SourceFolder = ((Choice<string?>)folder.SelectedItem).Value;
                    Save(preset);
                    ShowPreview();
                };
                var ages = new[] { 0, 6, 12, 24, 48, 168 }.Select(h => new Choice<int>(h, h switch { 0 => "any age", < 24 => $"{h} hours", 24 => "24 hours", _ => $"{h / 24} days" })).ToList();
                var age = new ComboBox { ItemsSource = ages, Width = 110, Margin = new Thickness(8, 0, 0, 0), SelectedItem = ages.FirstOrDefault(a => a.Value == (preset.SourceMaxAgeHours ?? 0)) ?? ages[0] };
                age.SelectionChanged += (_, _) =>
                {
                    var hours = ((Choice<int>)age.SelectedItem).Value;
                    preset.SourceMaxAgeHours = hours == 0 ? null : hours;
                    Save(preset);
                    ShowPreview();
                };
                editor.Children.Add(Row("From the folder", new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children = { folder, new TextBlock { Text = "newer than", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }, age },
                }));
                editor.Children.Add(Muted("When it goes live, the newest finished recording in that folder plays out, then the stream ends by itself.", 120));
                break;
        }
    }

    private void AddQualityRows(StreamRecordPreset preset)
    {
        var sizes = new[]
        {
            new Choice<(int, int)>((1280, 720), "720p (1280×720)"),
            new Choice<(int, int)>((1920, 1080), "1080p (1920×1080)"),
            new Choice<(int, int)>((2560, 1440), "1440p (2560×1440)"),
            new Choice<(int, int)>((3840, 2160), "4K (3840×2160)"),
            new Choice<(int, int)>((1080, 1920), "Vertical 1080 (1080×1920)"),
        }.ToList();
        var currentSize = (preset.Width ?? 1920, preset.Height ?? 1080);
        if (sizes.All(s => s.Value != currentSize)) sizes.Add(new(currentSize, $"{currentSize.Item1}×{currentSize.Item2}"));
        var size = new ComboBox { ItemsSource = sizes, Width = 180, SelectedItem = sizes.First(s => s.Value == currentSize) };
        size.SelectionChanged += (_, _) =>
        {
            (preset.Width, preset.Height) = ((Choice<(int, int)>)size.SelectedItem).Value;
            Save(preset);
            ShowPreview();
        };
        var rates = new[] { 24, 25, 30, 50, 60 }.Select(r => new Choice<int>(r, $"{r} fps")).ToList();
        var rate = new ComboBox
        {
            ItemsSource = rates, Width = 80, Margin = new Thickness(8, 0, 0, 0),
            SelectedItem = rates.FirstOrDefault(r => r.Value == (preset.FrameRate ?? 30)) ?? rates[2],
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
        editor.Children.Add(Row("Quality", quality));
        editor.Children.Add(Muted("6000 kbps suits 1080p streams; recordings look better at 8000 or more, and 60 fps wants about half as much again. "
            + "Check your upload speed: a stream needs about 1.5× its bitrate.", 120));
    }

    // MARK: Sound

    private void AddSoundRows(StreamRecordPreset preset)
    {
        editor.Children.Add(Section("SOUND"));
        var choices = new List<Choice<SoundChoice>> { new(new SoundChoice("program"), "Program audio (everything this app plays: videos and music)") };
        foreach (var (id, mixName) in AudioMixFeeds.Known) choices.Add(new(new SoundChoice("mix", id), $"{mixName}: only the Music tab"));
        var entries = model.AudioInputs.Entries.Where(e => e.IsAssigned).ToList();
        foreach (var entry in entries) choices.Add(new(new SoundChoice("entry", entry.Id), $"Sound input: {entry.Name} ({AudioInputEntry.ChannelsLabel(entry.Channels)})"));
        var devices = SafeInputDevices();
        foreach (var device in devices.Where(d => entries.All(e => e.Uid != d.Uid)))
            choices.Add(new(new SoundChoice("device", device.Uid, device.Name), $"Sound input: {device.Name}"));

        var current = !string.IsNullOrEmpty(preset.AudioMixId) ? choices.FirstOrDefault(c => c.Value.Kind == "mix" && c.Value.Id == preset.AudioMixId)
            : !string.IsNullOrEmpty(preset.AudioInputId) ? choices.FirstOrDefault(c => c.Value.Kind == "entry" && c.Value.Id == preset.AudioInputId)
            : !string.IsNullOrEmpty(preset.AudioInputUid) ? choices.FirstOrDefault(c => c.Value.Kind == "device" && c.Value.Id == preset.AudioInputUid)
            : choices[0];
        if (current is null)
        {
            // Set up on another computer (or a device that's gone): say so, and keep it until something else is chosen.
            current = new Choice<SoundChoice>(new SoundChoice("missing"), $"{model.AudioSourceTitle(preset)} — not on this PC (uses program audio)");
            choices.Add(current);
        }
        var sound = new ComboBox { ItemsSource = choices, SelectedItem = current };
        sound.SelectionChanged += (_, _) =>
        {
            var chosen = ((Choice<SoundChoice>)sound.SelectedItem).Value;
            var includeProgram = preset.AudioIncludesProgram == true;
            switch (chosen.Kind)
            {
                case "program":
                    StreamAudioSource.Program.ApplyTo(preset, includeProgram);
                    break;
                case "mix":
                    StreamAudioSource.Mix(chosen.Id!).ApplyTo(preset, includeProgram);
                    break;
                case "entry":
                    StreamAudioSource.Input(chosen.Id!).ApplyTo(preset, includeProgram);
                    break;
                case "device":
                    var made = model.AudioInputs.Ensure(chosen.Id!, chosen.Name ?? "Sound input", InputChannelSelection.Default);
                    StreamAudioSource.Input(made.Id).ApplyTo(preset, includeProgram);
                    break;
                default:
                    return;
            }
            Save(preset);
            BuildEditor();
        };
        editor.Children.Add(Row("Sound from", sound));

        if (!string.IsNullOrEmpty(preset.AudioInputId) && model.AudioInputs.Entry(preset.AudioInputId) is { Uid: { } uid } input)
        {
            var channelCount = devices.FirstOrDefault(d => d.Uid == uid)?.ChannelCount ?? 2;
            var channelChoices = ChannelChoices(channelCount, input.Channels);
            var channels = new ComboBox { ItemsSource = channelChoices, Width = 200, SelectedItem = channelChoices.FirstOrDefault(c => c.Value == input.Channels) };
            channels.SelectionChanged += (_, _) =>
            {
                if (channels.SelectedItem is not Choice<InputChannelSelection> picked) return;
                var entry = model.AudioInputs.Ensure(uid, input.Name, picked.Value);
                StreamAudioSource.Input(entry.Id).ApplyTo(preset, preset.AudioIncludesProgram == true);
                Save(preset);
                BuildEditor();
            };
            var connected = devices.Any(d => d.Uid == uid);
            editor.Children.Add(Row("Channels", new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    channels,
                    new TextBlock
                    {
                        Text = connected ? $"of {channelCount} on this device" : "the device isn't connected now",
                        Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Style = (Style)FindResource("Muted"),
                    },
                },
            }));
        }

        if (!string.IsNullOrEmpty(preset.AudioMixId) || !string.IsNullOrEmpty(preset.AudioInputId) || !string.IsNullOrEmpty(preset.AudioInputUid))
        {
            var addProgram = new CheckBox { Content = "Add the program audio too (videos and music played here)", IsChecked = preset.AudioIncludesProgram == true };
            addProgram.Click += (_, _) =>
            {
                preset.AudioIncludesProgram = addProgram.IsChecked == true ? true : null;
                Save(preset);
            };
            editor.Children.Add(Row("", addProgram));
        }

        var delay = new TextBox { Width = 60, Text = (preset.AudioDelayMs ?? 0).ToString(CultureInfo.InvariantCulture) };
        delay.LostFocus += (_, _) =>
        {
            if (!int.TryParse(delay.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)) return;
            ms = Math.Clamp(ms, 0, SessionAudioRequest.MaxDelayMs);
            preset.AudioDelayMs = ms == 0 ? null : ms;
            delay.Text = ms.ToString(CultureInfo.InvariantCulture);
            Save(preset);
        };
        editor.Children.Add(Row("Delay the sound", new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { delay, new TextBlock { Text = "ms (0–2000)", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Style = (Style)FindResource("Muted") } },
        }));
        editor.Children.Add(Muted("If lips move before the words are heard, hold the sound back: cameras and LED walls often run 2–5 frames (60–160 ms) late. "
            + "A sound input that can't be opened (unplugged, or blocked in Windows Settings › Privacy › Microphone) falls back to program audio.", 120));
    }

    private static List<Choice<InputChannelSelection>> ChannelChoices(int channelCount, InputChannelSelection current)
    {
        var choices = new List<Choice<InputChannelSelection>>();
        for (var offset = 0; offset + 1 < Math.Max(2, channelCount); offset += 2)
            choices.Add(new(new InputChannelSelection.StereoPair(offset), $"Channels {offset + 1}–{offset + 2} (stereo)"));
        for (var channel = 0; channel < Math.Max(1, channelCount); channel++)
            choices.Add(new(new InputChannelSelection.Mono(channel), $"Channel {channel + 1} (mono)"));
        if (choices.All(c => c.Value != current)) choices.Insert(0, new(current, AudioInputEntry.ChannelsLabel(current)));
        return choices;
    }

    private static IReadOnlyList<AudioInputDevice> SafeInputDevices()
    {
        try { return AudioDeviceList.InputDevices(); }
        catch (Exception) { return []; }
    }

    // MARK: Recording

    private void AddRecordingRows(StreamRecordPreset preset, bool streams)
    {
        editor.Children.Add(Section("RECORDING"));
        if (streams)
        {
            var record = new CheckBox { Content = "Also record while streaming", IsChecked = preset.RecordWhileStreaming == true };
            record.Click += (_, _) =>
            {
                preset.RecordWhileStreaming = record.IsChecked == true ? true : null;
                Save(preset);
                BuildEditor();
            };
            editor.Children.Add(Row("", record));
            if (preset.RecordWhileStreaming != true) return;
        }

        var currentFolder = preset.RecordLibraryFolder ?? RecordingLibrary.DefaultFolder;
        var folders = model.MediaFolders().ToList();
        if (!folders.Contains(currentFolder)) folders.Add(currentFolder);
        // Pick from the library's folders, or type a new one.
        var folder = new ComboBox { ItemsSource = folders, IsEditable = true, Width = 220, SelectedItem = currentFolder };
        void SaveFolder()
        {
            var typed = folder.Text.Trim().Replace('\\', '/').Trim('/');
            preset.RecordLibraryFolder = typed.Length == 0 || typed == RecordingLibrary.DefaultFolder ? null : typed;
            Save(preset);
        }
        folder.LostFocus += (_, _) => SaveFolder();
        folder.SelectionChanged += (_, _) =>
        {
            if (folder.SelectedItem is string picked) folder.Text = picked;
            SaveFolder();
        };
        editor.Children.Add(Row("Save to", new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { folder, new TextBlock { Text = "folder in the Media tab", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Style = (Style)FindResource("Muted") } },
        }));

        var copies = new StackPanel { Orientation = Orientation.Horizontal };
        if (preset.RecordExportFolder is { Length: > 0 } export)
        {
            copies.Children.Add(new TextBlock { Text = export, MaxWidth = 300, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = export });
            var change = new Button { Content = "Change…", Margin = new Thickness(8, 0, 0, 0) };
            change.Click += (_, _) => ChooseExportFolder(preset);
            var none = new Button { Content = "None", Margin = new Thickness(6, 0, 0, 0) };
            none.Click += (_, _) =>
            {
                preset.RecordExportFolder = null;
                Save(preset);
                BuildEditor();
            };
            copies.Children.Add(change);
            copies.Children.Add(none);
        }
        else
        {
            var choose = new Button { Content = "Choose folder…" };
            choose.Click += (_, _) => ChooseExportFolder(preset);
            var videos = new Button { Content = @"Videos\MxU Slides", Margin = new Thickness(6, 0, 0, 0), ToolTip = "A copy of each recording goes here too" };
            videos.Click += (_, _) =>
            {
                preset.RecordExportFolder = RecordingController.DefaultRecordingsFolder;
                Save(preset);
                BuildEditor();
            };
            copies.Children.Add(choose);
            copies.Children.Add(videos);
        }
        editor.Children.Add(Row("Also copy to", copies));
        editor.Children.Add(Muted("A copy can also go to an ordinary folder, for uploading later or a synced folder like OneDrive or Dropbox.", 120));
    }

    private void ChooseExportFolder(StreamRecordPreset preset)
    {
        var dialog = new OpenFolderDialog { Title = "Also copy recordings to", InitialDirectory = preset.RecordExportFolder ?? RecordingController.DefaultRecordingsFolder };
        if (dialog.ShowDialog(this) != true) return;
        preset.RecordExportFolder = dialog.FolderName;
        Save(preset);
        BuildEditor();
    }

    private void AddDestinationPicks(StreamRecordPreset preset)
    {
        editor.Children.Add(Section("STREAM TO"));
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
                ShowPreview();
            };
            picks.Children.Add(pick);
        }
        if (destinations.Count == 0) picks.Children.Add(Muted("No destinations yet: add one below."));
        editor.Children.Add(Row("Destinations", picks));
    }

    private void Save(StreamRecordPreset preset) => model.Library.Save(preset);

    /// <summary>The line under the editor: what a replay will play, or why the preset won't go live.</summary>
    private void ShowPreview()
    {
        var result = Preset is { } preset ? model.Broadcast.CapturePreview(preset) : null;
        preview.Text = result?.Text ?? "";
        preview.Visibility = result is null ? Visibility.Collapsed : Visibility.Visible;
        preview.Foreground = result?.IsProblem == true ? new SolidColorBrush(Color.FromRgb(0xF5, 0xA6, 0x23)) : (Brush)FindResource("Text");
    }

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
        openCopies.Visibility = preset?.RecordExportFolder is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        caption.Text = model.BroadcastCaption;
        caption.Foreground = model.BroadcastTint switch
        {
            BroadcastStatus.ChipTint.Red => new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)),
            BroadcastStatus.ChipTint.Orange => new SolidColorBrush(Color.FromRgb(0xF5, 0xA6, 0x23)),
            BroadcastStatus.ChipTint.Green => new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50)),
            _ => (Brush)FindResource("Text"),
        };
        statusLines.Children.Clear();
        if (model.Broadcast.Streaming.Live is { } live)
        {
            if (live.FileSource is not null) statusLines.Children.Add(Muted($"Replaying {Path.GetFileName(live.FileSource.Path)} · ends by itself when the video does"));
            foreach (var session in live.Sessions) statusLines.Children.Add(Muted(BroadcastStatus.HealthLine(session)));
        }
        foreach (var session in model.Broadcast.Recording.Sessions)
        {
            var screen = session.TargetId == CaptureSources.ProgramTarget ? "" : $" · {model.BroadcastScreenName(session.TargetId) ?? "screen"}";
            var line = session.IsRecording
                ? BroadcastStatus.RecordingHealthLine(session) + screen
                : $"{session.TargetName} — {session.Status.State.Kind}{(session.Status.State.Message is { } m ? $": {m}" : "")} · {Path.GetFileName(session.FilePath)}";
            statusLines.Children.Add(Muted(line));
        }
        if (model.LastFiledRecording is { } filed && !model.IsBroadcasting)
            statusLines.Children.Add(Muted($"Last recording: “{filed.Name}” is in the Media tab ({filed.Folder ?? "no folder"})."));
    }

    private void OpenCopies()
    {
        if (Preset?.RecordExportFolder is not { Length: > 0 } folder) return;
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

    private static string? NullIfEmpty(string? text) => string.IsNullOrEmpty(text) ? null : text;

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
