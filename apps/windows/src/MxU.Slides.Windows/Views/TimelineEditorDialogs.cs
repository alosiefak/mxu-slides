using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using MxU.Slides.Audio;
using MxU.Slides.Audio.Timeline;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>Shared look for the timeline editor's small windows (dark, sized to their content, owned by the editor).</summary>
internal static class TimelineDialog
{
    public static Window Make(Window owner, string title, double width)
    {
        var window = new Window
        {
            Title = title, Owner = owner, Width = width, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Background = (Brush)Application.Current.FindResource("BasePlane"),
        };
        return window;
    }

    public static TextBlock Heading(string text) => new() { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };

    public static TextBlock Muted(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 10),
        Style = (Style)Application.Current.FindResource("Muted"),
    };

    public static TextBlock Warning(string text) => new()
    {
        Text = "⚠ " + text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 4, 0, 8),
        Foreground = (Brush)Application.Current.FindResource("Warn"),
    };

    public static Button Button(string text, Action click, bool accent = false, string? tip = null)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), ToolTip = tip, MinWidth = accent ? 90 : 0 };
        if (accent) button.Style = (Style)Application.Current.FindResource("AccentButton");
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>A Done button on the right, closing the window.</summary>
    public static DockPanel Footer(Window window, params UIElement[] left)
    {
        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false };
        var done = Button("Done", window.Close, accent: true);
        done.IsDefault = true;
        done.IsCancel = true;
        done.Margin = new Thickness(0);
        DockPanel.SetDock(done, Dock.Right);
        footer.Children.Add(done);
        foreach (var element in left)
        {
            DockPanel.SetDock(element, Dock.Left);
            footer.Children.Add(element);
        }
        return footer;
    }

    public static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var child in children) row.Children.Add(child);
        return row;
    }
}

/// <summary>
/// Where the song starts in the service's timecode, whether it plays from timecode, and
/// what its start overlaps. Port of PresentationTimelineStartPopover.
/// </summary>
public static class TimecodeStartWindow
{
    /// <summary>Shows it as a dialog.</summary>
    public static void Edit(Window owner, TimelineEditorViewModel vm) => Make(owner, vm).ShowDialog();

    /// <summary>The window, not yet shown (the self-test pictures it).</summary>
    public static Window Make(Window owner, TimelineEditorViewModel vm)
    {
        var window = TimelineDialog.Make(owner, "Timecode Start", 380);
        var body = new StackPanel { Margin = new Thickness(18) };
        window.Content = new Border { Child = body };
        var text = new TextBox { Width = 140, FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 0, 8, 0), Tag = "HH:MM:SS:FF" };
        text.Text = PresentationTimelines.Timecode(vm.Timeline?.TimecodeStart ?? vm.SuggestedStart(0));
        void Build()
        {
            // The text box is kept between builds (it holds what's typed): take it out of its old row first.
            if (text.Parent is Panel row) row.Children.Remove(text);
            body.Children.Clear();
            body.Children.Add(TimelineDialog.Heading("Timecode Start"));
            var typed = PresentationTimelines.Frames(text.Text);
            int? ordered = vm.ByOrder ? vm.TimecodeStart : null;
            if (vm.ByOrder)
            {
                var minutes = (ordered ?? 0) / PresentationTimelines.FramesPerSecond / 60;
                body.Children.Add(TimelineDialog.Row(
                    new TextBlock { Text = $"{minutes / 60:00}:{minutes % 60:00}", FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = "· by order in this service", VerticalAlignment = VerticalAlignment.Center, Style = (Style)Application.Current.FindResource("Muted") }));
                body.Children.Add(TimelineDialog.Row(TimelineDialog.Button("Use saved times", () =>
                {
                    vm.SetByOrder(false);
                    Build();
                }, tip: "Start times for this service: by each song's saved time")));
            }
            var set = TimelineDialog.Button("Set", () => Set(typed), tip: "Use this start");
            set.IsEnabled = typed is not null && vm.CanEdit;
            text.IsEnabled = vm.CanEdit;
            body.Children.Add(TimelineDialog.Row(text, set));
            body.Children.Add(TimelineDialog.Muted("Frames at 30 fps. Give each song in the service its own start, so incoming timecode says which song is playing."));
            var follows = new CheckBox { Content = "This song plays from timecode", IsChecked = vm.Timeline?.TimecodeEnabled == true, IsEnabled = vm.CanEdit };
            follows.Click += (_, _) => vm.SetFollowsTimecode(follows.IsChecked == true);
            body.Children.Add(follows);
            var start = ordered ?? typed ?? vm.Timeline?.TimecodeStart ?? 0;
            var clashes = vm.Clashes(start);
            if (clashes.Count > 0)
            {
                body.Children.Add(TimelineDialog.Warning("Overlaps " + string.Join(", ",
                    clashes.Select(c => $"{c.Name} ({PresentationTimelines.Timecode(c.Window.Start)})"))));
                if (!vm.ByOrder && vm.WindowAt(start) is { } mine)
                {
                    var free = PresentationTimelines.SuggestedStart(mine.Length, vm.OtherWindows().Select(o => o.Window), mine.Id);
                    body.Children.Add(TimelineDialog.Row(
                        TimelineDialog.Button($"Use {PresentationTimelines.Timecode(free)}", () => Set(free)),
                        TimelineDialog.Button("Use service order", () =>
                        {
                            vm.SetByOrder(true);
                            Build();
                        }, tip: "Start times for this service: by service order (01:00, 02:00, … in run order)")));
                }
            }
            body.Children.Add(TimelineDialog.Footer(window));
        }
        void Set(int? frames)
        {
            if (frames is not { } value) return;
            text.Text = PresentationTimelines.Timecode(value);
            vm.SetTimecodeStart(value);
            Build();
        }
        text.TextChanged += (_, _) =>
        {
            // Only the warnings follow the typing; the box keeps its focus.
            if (text.IsKeyboardFocused) window.Dispatcher.BeginInvoke(() =>
            {
                var caret = text.CaretIndex;
                Build();
                text.Focus();
                text.CaretIndex = Math.Min(caret, text.Text.Length);
            });
        };
        text.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) Set(PresentationTimelines.Frames(text.Text));
        };
        Build();
        return window;
    }
}

/// <summary>
/// The song's section map: where each section is in its recording, which slide section it
/// plays, markers added at the playhead. Every arrangement then lays its sections' audio out
/// in its order, the slides riding with them. Port of SongMapSheet (Sources/SongMapEditing.swift).
/// </summary>
public static class SongMapWindow
{
    /// <summary>Shows it as a dialog.</summary>
    public static void Edit(Window owner, TimelineEditorViewModel vm) => Make(owner, vm).ShowDialog();

    /// <summary>The window, not yet shown (the self-test pictures it).</summary>
    public static Window Make(Window owner, TimelineEditorViewModel vm)
    {
        var window = TimelineDialog.Make(owner, "Section Map", 520);
        var body = new StackPanel { Margin = new Thickness(18) };
        window.Content = new Border { Child = body };
        void Build()
        {
            body.Children.Clear();
            body.Children.Add(TimelineDialog.Heading("Section Map"));
            body.Children.Add(TimelineDialog.Muted("Where each section is in this song's recording. Every arrangement plays its sections' audio in its order; the slides ride with their section."));
            if (vm.SongMap is { } map)
            {
                var slideSections = vm.Song?.Sections ?? [];
                var list = new StackPanel();
                foreach (var section in map.Sections) list.Children.Add(SectionRow(section, slideSections));
                if (map.Sections.Count == 0) list.Children.Add(TimelineDialog.Muted("No markers yet: put the playhead where a section starts, then Add Marker."));
                body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
                var missing = vm.MissingSections();
                if (missing.Count > 0)
                    body.Children.Add(TimelineDialog.Warning($"{string.Join(", ", missing)} {(missing.Count == 1 ? "has" : "have")} no audio in this song's tracks"));
                var remove = TimelineDialog.Button("Remove Section Map", () =>
                {
                    vm.RemoveSongMap();
                    Build();
                }, tip: "This song's timelines stay as they are now");
                body.Children.Add(TimelineDialog.Footer(window,
                    TimelineDialog.Button("Add Marker at Playhead", () =>
                    {
                        vm.AddMarker();
                        Build();
                    }, tip: "A new section starting where the playhead is in the recording"),
                    remove));
            }
            else
            {
                body.Children.Add(TimelineDialog.Muted("This song has no section map. Make one from this timeline's audio, then add a marker where each section starts."));
                var make = TimelineDialog.Button("Make Section Map", () =>
                {
                    vm.MakeSongMap();
                    Build();
                }, tip: "Needs the song's stems or a music clip on the timeline");
                make.IsEnabled = vm.CanMakeSongMap && vm.CanEdit;
                body.Children.Add(TimelineDialog.Footer(window, make));
            }
        }
        FrameworkElement SectionRow(AudioSection section, IReadOnlyList<PresentationSection> slideSections)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6), LastChildFill = true };
            var clock = new TextBlock { Text = TimelineEditing.Clock(section.Start), Width = 44, FontFamily = new FontFamily("Consolas"), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 6, 0) };
            var earlier = TimelineDialog.Button("‹", () =>
            {
                vm.NudgeSection(section.Id, -PresentationTimelines.Frame);
                Build();
            }, tip: "A frame earlier");
            var later = TimelineDialog.Button("›", () =>
            {
                vm.NudgeSection(section.Id, PresentationTimelines.Frame);
                Build();
            }, tip: "A frame later");
            earlier.Margin = later.Margin = new Thickness(0, 0, 4, 0);
            var name = new TextBox { Text = section.Name, Margin = new Thickness(4, 0, 6, 0) };
            void Rename()
            {
                if (name.Text.Trim() is { Length: > 0 } typed && typed != section.Name) vm.EditSection(section.Id, s => s.Name = typed);
            }
            name.LostFocus += (_, _) => Rename();
            name.KeyDown += (_, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter) Rename();
            };
            var choices = new List<(string? Id, string Name)> { (null, "No slides") };
            choices.AddRange(slideSections.Select(s => ((string?)s.Id, s.Name)));
            var picker = new ComboBox
            {
                Width = 130, ItemsSource = choices.Select(c => c.Name).ToList(), SelectedIndex = Math.Max(choices.FindIndex(c => c.Id == section.SectionId), 0),
                ToolTip = "The slide section this audio plays", Margin = new Thickness(0, 0, 6, 0),
            };
            picker.SelectionChanged += (_, _) =>
            {
                if (picker.SelectedIndex < 0) return;
                var id = choices[picker.SelectedIndex].Id;
                if (id != section.SectionId) vm.EditSection(section.Id, s => s.SectionId = id);
            };
            var remove = TimelineDialog.Button("−", () =>
            {
                vm.RemoveMarker(section.Id);
                Build();
            }, tip: "Remove this marker");
            remove.Margin = new Thickness(0);
            foreach (var left in new FrameworkElement[] { clock, earlier, later })
            {
                DockPanel.SetDock(left, Dock.Left);
                row.Children.Add(left);
            }
            foreach (var right in new FrameworkElement[] { remove, picker })
            {
                DockPanel.SetDock(right, Dock.Right);
                row.Children.Add(right);
            }
            row.Children.Add(name);
            return row;
        }
        Build();
        return window;
    }
}

/// <summary>
/// "N files match no track": for each dropped file no track's name matches, a new track (named
/// as suggested, or typed) or one of the church's tracks. Port of UnmatchedStemsSheet.
/// </summary>
public static class UnmatchedStemsWindow
{
    private static bool remember = true;

    /// <summary>Asks, as a dialog; null when cancelled.</summary>
    public static TimelineTracks.StemResolution? Ask(Window owner, TimelineTracks.StemPlan plan, IReadOnlyList<TrackChannel> channels)
    {
        TimelineTracks.StemResolution? result = null;
        var window = Make(owner, plan, channels, resolution => result = resolution);
        return window.ShowDialog() == true ? result : null;
    }

    /// <summary>The window, not yet shown; <paramref name="import"/> gets the answer when Import is pressed.</summary>
    public static Window Make(Window owner, TimelineTracks.StemPlan plan, IReadOnlyList<TrackChannel> channels, Action<TimelineTracks.StemResolution> import)
    {
        var window = TimelineDialog.Make(owner, "Import Audio", 560);
        var body = new StackPanel { Margin = new Thickness(18) };
        window.Content = new Border { Child = body };
        body.Children.Add(TimelineDialog.Heading(plan.Unmatched.Count == 1 ? "1 file matches no track" : $"{plan.Unmatched.Count} files match no track"));
        var matched = plan.Assignments.Keys.Select(Path.GetFileNameWithoutExtension).OfType<string>().Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        if (matched.Count is > 0 and <= 3)
            body.Children.Add(TimelineDialog.Muted($"{string.Join(", ", matched)} {(matched.Count == 1 ? "goes on its track" : "go on their tracks")}."));
        else if (matched.Count > 3)
            body.Children.Add(TimelineDialog.Muted($"The other {matched.Count} go on their tracks."));

        const string New = "new";
        var picks = new Dictionary<string, string>();
        var names = new Dictionary<string, string>();
        var combos = new Dictionary<string, ComboBox>();
        var taken = plan.Assignments.Values.ToHashSet();
        var grid = new StackPanel();
        void Fill(string fileName)
        {
            // A track picked for one file (or already matched) isn't offered to another.
            var combo = combos[fileName];
            var pick = picks.GetValueOrDefault(fileName, New);
            var others = picks.Where(p => p.Key != fileName).Select(p => p.Value).ToHashSet();
            var options = new List<(string Id, string Name)> { (New, "New Track") };
            options.AddRange(channels.Where(c => (!taken.Contains(c.Id) && !others.Contains(c.Id)) || c.Id == pick).Select(c => (c.Id, c.Name)));
            combo.Tag = options;
            combo.ItemsSource = options.Select(o => o.Name).ToList();
            combo.SelectedIndex = Math.Max(options.FindIndex(o => o.Id == pick), 0);
        }
        foreach (var file in plan.Unmatched)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6), LastChildFill = false };
            var label = new TextBlock
            {
                Text = Path.GetFileNameWithoutExtension(file.FileName), Width = 160, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = file.FileName,
            };
            var combo = new ComboBox { Width = 170, Margin = new Thickness(8, 0, 8, 0) };
            var name = new TextBox { Width = 160, Text = file.Proposed.Name };
            combos[file.FileName] = combo;
            name.TextChanged += (_, _) => names[file.FileName] = name.Text;
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.Tag is not List<(string Id, string Name)> options || combo.SelectedIndex < 0) return;
                var id = options[combo.SelectedIndex].Id;
                if (picks.GetValueOrDefault(file.FileName, New) == id) return;
                picks[file.FileName] = id;
                name.Visibility = id == New ? Visibility.Visible : Visibility.Hidden;
                foreach (var other in plan.Unmatched.Where(o => o.FileName != file.FileName)) Fill(other.FileName);
            };
            foreach (var part in new FrameworkElement[] { label, combo, name })
            {
                DockPanel.SetDock(part, Dock.Left);
                row.Children.Add(part);
            }
            grid.Children.Add(row);
        }
        foreach (var file in plan.Unmatched) Fill(file.FileName);
        body.Children.Add(new ScrollViewer { Content = grid, MaxHeight = 340, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var rememberBox = new CheckBox
        {
            Content = "Remember for next time", IsChecked = remember, Margin = new Thickness(0, 8, 0, 0),
            ToolTip = "Next time you import a file with the same name, it goes on the track you picked.",
        };
        body.Children.Add(rememberBox);
        var importButton = TimelineDialog.Button("Import", () =>
        {
            remember = rememberBox.IsChecked == true;
            var choices = new Dictionary<string, TimelineTracks.StemChoice>();
            foreach (var file in plan.Unmatched)
            {
                var pick = picks.GetValueOrDefault(file.FileName, New);
                choices[file.FileName] = pick == New
                    ? new TimelineTracks.StemChoice.New(names.GetValueOrDefault(file.FileName) ?? file.Proposed.Name)
                    : new TimelineTracks.StemChoice.Existing(pick);
            }
            import(TimelineTracks.Resolve(plan, choices, remember));
            window.DialogResult = true;
        }, accent: true);
        importButton.IsDefault = true;
        var cancel = TimelineDialog.Button("Cancel", () => window.DialogResult = false);
        cancel.IsCancel = true;
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        footer.Children.Add(cancel);
        footer.Children.Add(importButton);
        body.Children.Add(footer);
        return window;
    }
}

/// <summary>
/// Where this computer plays the stems: the audio interface, how many of its outputs go to
/// the sound board (each family then gets its own), and each family's output. Port of
/// TracksSetupGuide (Sources/TracksSetupView.swift), writing <see cref="TrackOutputSettings"/>.
/// </summary>
public static class TrackOutputsWindow
{
    /// <summary>Shows it as a dialog.</summary>
    public static void Edit(Window owner, MainViewModel model) => Make(owner, model).ShowDialog();

    /// <summary>The window, not yet shown (the self-test pictures it).</summary>
    public static Window Make(Window owner, MainViewModel model)
    {
        var window = TimelineDialog.Make(owner, "Tracks: Outputs", 560);
        var body = new StackPanel { Margin = new Thickness(18) };
        window.Content = new Border { Child = body };
        var settings = model.TrackOutputs;
        var devices = AudioDeviceList.OutputDevices();
        void Save()
        {
            model.SaveTrackOutputs();
            Build();
        }
        AudioOutputDevice? Device() => settings.DeviceUid is { } uid ? devices.FirstOrDefault(d => d.Uid == uid) : null;
        int Channels() => Device()?.ChannelCount ?? AudioDeviceList.DefaultOutputDevice()?.ChannelCount ?? 2;
        void Build()
        {
            body.Children.Clear();
            body.Children.Add(TimelineDialog.Heading("Set Up Tracks"));
            body.Children.Add(TimelineDialog.Muted("Where this computer plays a song's stems: click and guide to the band's ears, the music to the house or to its own outputs on your sound board."));

            var interfaces = new List<(string? Uid, string Name)> { (null, "Windows default output") };
            interfaces.AddRange(devices.Select(d => ((string?)d.Uid, $"{d.Name} ({d.ChannelCount} outputs)")));
            var picker = new ComboBox
            {
                Width = 300, ItemsSource = interfaces.Select(i => i.Name).ToList(), SelectedIndex = Math.Max(interfaces.FindIndex(i => i.Uid == settings.DeviceUid), 0),
            };
            picker.SelectionChanged += (_, _) =>
            {
                if (picker.SelectedIndex < 0) return;
                settings.DeviceUid = interfaces[picker.SelectedIndex].Uid;
                TrackRouting.Lay(settings, Math.Min(settings.Outputs, Channels()), settings.Start, Channels());
                Save();
            };
            body.Children.Add(Labelled("Audio interface", picker));

            var most = Math.Min(TrackRouting.MaxOutputs, Channels());
            var count = new ComboBox { Width = 64, ItemsSource = Enumerable.Range(0, most + 1).ToList(), SelectedItem = Math.Min(settings.Outputs, most) };
            count.SelectionChanged += (_, _) =>
            {
                if (count.SelectedItem is int value && value != settings.Outputs)
                {
                    TrackRouting.Lay(settings, value, settings.Start, Channels());
                    Save();
                }
            };
            var row = TimelineDialog.Row(count);
            if (settings.Outputs > 0)
            {
                var start = new ComboBox
                {
                    Width = 64, Margin = new Thickness(8, 0, 0, 0),
                    ItemsSource = Enumerable.Range(1, Math.Max(Channels() - settings.Outputs + 1, 1)).ToList(), SelectedItem = settings.Start + 1,
                };
                start.SelectionChanged += (_, _) =>
                {
                    if (start.SelectedItem is int first && first - 1 != settings.Start)
                    {
                        TrackRouting.Lay(settings, settings.Outputs, first - 1, Channels());
                        Save();
                    }
                };
                row.Children.Add(new TextBlock { Text = "starting at", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(start);
            }
            body.Children.Add(Labelled("Outputs to your sound board", row));
            body.Children.Add(TimelineDialog.Muted(TrackRouting.Explanation(settings.Outputs, Device()?.Name)));

            var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var channels = Channels();
            foreach (var family in TimelineTracks.Families)
            {
                var (channel, stereo) = TrackRouting.Current(settings, family);
                var index = grid.RowDefinitions.Count;
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var name = new TextBlock { Text = family.Name(), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 10, 4) };
                var last = channels - (stereo ? 1 : 0);
                var outputs = new List<(int? Channel, string Name)> { (null, "Not yet") };
                for (var c = 0; c < Math.Max(last, 0); c++) outputs.Add((c, stereo ? $"Outputs {c + 1}–{c + 2}" : $"Output {c + 1}"));
                var output = new ComboBox
                {
                    ItemsSource = outputs.Select(o => o.Name).ToList(), SelectedIndex = Math.Max(outputs.FindIndex(o => o.Channel == channel), 0),
                    Margin = new Thickness(0, 0, 8, 4),
                };
                output.SelectionChanged += (_, _) =>
                {
                    if (output.SelectedIndex < 0) return;
                    TrackRouting.Assign(settings, family, outputs[output.SelectedIndex].Channel, stereo);
                    Save();
                };
                var width = new ComboBox { ItemsSource = new[] { "Mono", "Stereo" }, SelectedIndex = stereo ? 1 : 0, Width = 90, Margin = new Thickness(0, 0, 0, 4) };
                width.SelectionChanged += (_, _) =>
                {
                    var wantStereo = width.SelectedIndex == 1;
                    var moved = channel is { } c && wantStereo ? Math.Min(c, Math.Max(channels - 2, 0)) : channel;
                    TrackRouting.Assign(settings, family, moved, wantStereo);
                    Save();
                };
                Grid.SetRow(name, index);
                Grid.SetRow(output, index);
                Grid.SetColumn(output, 1);
                Grid.SetRow(width, index);
                Grid.SetColumn(width, 2);
                grid.Children.Add(name);
                grid.Children.Add(output);
                grid.Children.Add(width);
            }
            body.Children.Add(new ScrollViewer { Content = grid, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            var house = new CheckBox { Content = "The music stems also play on outputs 1–2 (the house)", IsChecked = settings.MusicOnMain };
            house.Click += (_, _) =>
            {
                settings.MusicOnMain = house.IsChecked == true;
                Save();
            };
            body.Children.Add(house);
            body.Children.Add(TimelineDialog.Footer(window));
        }
        Build();
        return window;
    }

    private static DockPanel Labelled(string label, UIElement content)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var text = new TextBlock { Text = label, Width = 196, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(text, Dock.Left);
        row.Children.Add(text);
        row.Children.Add(content);
        return row;
    }
}

/// <summary>
/// The actions an actions cue runs, with an Add menu of the steps that need no further
/// settings. Port of the Mac's AddActionMenuItems (Sources/ActionEditors.swift) and a simple
/// stand-in for its SlideActionsSheet (the full action editor comes with the automation editors).
/// </summary>
public static class TimelineActionsWindow
{
    /// <summary>Shows it as a dialog (nothing while recording).</summary>
    public static void Edit(Window owner, TimelineEditorViewModel vm, TimelineCue cue)
    {
        if (vm.CanEdit) Make(owner, vm, cue).ShowDialog();
    }

    /// <summary>
    /// The window, not yet shown; the cue's actions are saved when it closes. It uses the same
    /// step editor as slides and combos (every kind of step, its settings and its delay).
    /// </summary>
    public static Window Make(Window owner, TimelineEditorViewModel vm, TimelineCue cue)
    {
        var model = vm.Main;
        var window = TimelineDialog.Make(owner, "Timeline Actions", 560);
        var body = new StackPanel { Margin = new Thickness(18) };
        window.Content = new Border { Child = body };
        body.Children.Add(TimelineDialog.Heading("Timeline Actions"));
        body.Children.Add(TimelineDialog.Muted($"Run together at {TimelineEditing.Clock(cue.At)} when the timeline plays past them (not when you jump past)."));
        var editor = new ActionListEditor(model.ActionChoices, emptyText: "No actions yet.");
        editor.Load(vm.Cues.FirstOrDefault(c => c.Id == cue.Id)?.Actions ?? []);
        IReadOnlyList<SlideAction>? edited = null;
        editor.Changed += list => edited = list;
        // Windows only: the cue's production steps (cameras, OBS, vMix, lights), kept beside the library and saved at once.
        Production.ProductionStepsSection.Attach(editor, model, cue.Id);
        body.Children.Add(new ScrollViewer { Content = editor, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        body.Children.Add(TimelineDialog.Footer(window));
        window.Closed += (_, _) =>
        {
            if (edited is not null) vm.SetActions(cue.Id, [.. edited]);
        };
        return window;
    }

    private static string? EntryName(MainViewModel model, string id)
    {
        var library = model.Library;
        return library.Get<Presentation>(id)?.Name ?? library.Get<MediaItem>(id)?.Name ?? library.Get<Overlay>(id)?.Name
            ?? library.Get<AlertPreset>(id)?.Name ?? library.Get<ActionCombo>(id)?.Name ?? library.Get<OutputPreset>(id)?.Name
            ?? library.Get<StreamRecordPreset>(id)?.Name ?? library.Get<ConfidenceLayout>(id)?.Name ?? library.Get<Playlist>(id)?.Name
            ?? library.Get<AudioItem>(id)?.Name ?? model.Inputs.EntryFor(id)?.Name;
    }

    /// <summary>The actions an actions cue can run, ready made (each needs no further settings).</summary>
    public static void FillAddActions(ItemsControl menu, MainViewModel model, Action<SlideAction> add)
    {
        var library = model.Library;
        SlideAction Make(SlideActionKind kind, Action<SlideAction>? fill = null)
        {
            var action = new SlideAction { Id = TimelineEditing.NewId(), Kind = kind };
            fill?.Invoke(action);
            return action;
        }
        MenuItem Item(string header, Func<SlideAction> make)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => add(make());
            return item;
        }
        MenuItem Sub(string header, IEnumerable<MenuItem> items)
        {
            var sub = new MenuItem { Header = header };
            foreach (var item in items) sub.Items.Add(item);
            if (sub.Items.Count == 0) sub.IsEnabled = false;
            return sub;
        }

        var presets = library.All<OutputPreset>();
        if (presets.Count > 0) menu.Items.Add(Sub("Output Preset", presets.Select(p => Item(p.Name, () => Make(SlideActionKind.SwitchOutputPreset, a => a.PresetId = p.Id)))));

        var clear = Sub("Clear", []);
        clear.IsEnabled = true;
        clear.Items.Add(Item("Clear All", () => Make(SlideActionKind.ClearAll)));
        clear.Items.Add(new Separator());
        foreach (var layer in LayerKinds.All.Reverse()) clear.Items.Add(Item(layer.DisplayName(), () => Make(SlideActionKind.ClearLayer, a => a.Layer = layer.Raw())));
        clear.Items.Add(new Separator());
        clear.Items.Add(Item("Clear Music", () => Make(SlideActionKind.ClearAudio)));
        menu.Items.Add(clear);

        menu.Items.Add(Sub("Fire Media", library.All<MediaItem>().Select(m => Item(m.Name, () => Make(SlideActionKind.FireMedia, a => a.MediaId = m.Id)))));

        var music = Sub("Music", []);
        music.IsEnabled = true;
        music.Items.Add(Sub("Playlist", library.All<Playlist>().Where(p => p.PlaylistKind == PlaylistKind.Audio)
            .Select(p => Item(p.Name, () => Make(SlideActionKind.FireAudioPlaylist, a => a.PlaylistId = p.Id)))));
        music.Items.Add(Sub("File", library.All<AudioItem>().Select(i => Item(i.Name, () => Make(SlideActionKind.FireAudio, a => a.AudioItemId = i.Id)))));
        menu.Items.Add(music);

        var alerts = library.All<AlertPreset>();
        var alert = Sub("Alert", alerts.Select(a => Item(a.Name, () => Make(SlideActionKind.FireAlert, x => x.AlertId = a.Id))));
        alert.IsEnabled = true;
        if (alerts.Count > 0) alert.Items.Add(new Separator());
        alert.Items.Add(Item("Dismiss Alert", () => Make(SlideActionKind.DismissAlert)));
        menu.Items.Add(alert);

        var overlays = library.All<Overlay>();
        var overlay = Sub("Overlay", overlays.Select(o => Item(o.Name, () => Make(SlideActionKind.FireOverlay, a => a.OverlayId = o.Id))));
        overlay.IsEnabled = true;
        if (overlays.Count > 0) overlay.Items.Add(new Separator());
        overlay.Items.Add(Item("Take Down Overlays", () => Make(SlideActionKind.DismissOverlay)));
        menu.Items.Add(overlay);

        var inputs = model.Inputs.Entries;
        if (inputs.Count > 0) menu.Items.Add(Sub("Live Input", inputs.Select(i => Item(i.Name, () => Make(SlideActionKind.FireLiveInput, a => a.LiveInputId = i.Id)))));

        var layouts = Sub("Confidence Layout", library.All<ConfidenceLayout>().Select(l => Item(l.Name, () => Make(SlideActionKind.SetConfidenceLayout, a => a.ConfidenceLayoutId = l.Id))));
        layouts.IsEnabled = true;
        layouts.Items.Insert(0, Item("Built-in Layout", () => Make(SlideActionKind.SetConfidenceLayout)));
        menu.Items.Add(layouts);

        var timers = model.TimerRows.Select(t => (t.Id, t.Name)).ToList();
        if (timers.Count > 0)
        {
            var timer = Sub("Timer", []);
            timer.IsEnabled = true;
            foreach (var (label, kind) in new[] { ("Start", SlideActionKind.TimerStart), ("Pause", SlideActionKind.TimerPause), ("Reset", SlideActionKind.TimerReset) })
                timer.Items.Add(Sub(label, timers.Select(t => Item(t.Name, () => Make(kind, a => a.TimerId = t.Id)))));
            menu.Items.Add(timer);
        }

        var combos = library.All<ActionCombo>();
        if (combos.Count > 0) menu.Items.Add(Sub("Action Combo", combos.Select(c => Item(c.Name, () => Make(SlideActionKind.FireCombo, a => a.ComboId = c.Id)))));

        var capturePresets = library.All<StreamRecordPreset>();
        var capture = Sub("Capture", []);
        capture.IsEnabled = true;
        if (capturePresets.Count > 0)
        {
            capture.Items.Add(Sub("Start", capturePresets.Select(p => Item(p.Name, () => Make(SlideActionKind.CaptureStart, a => a.CapturePresetId = p.Id)))));
            capture.Items.Add(Sub("Stop", capturePresets.Select(p => Item(p.Name, () => Make(SlideActionKind.CaptureStop, a => a.CapturePresetId = p.Id)))));
            capture.Items.Add(new Separator());
        }
        capture.Items.Add(Item("Stop All Capture", () => Make(SlideActionKind.CaptureStop)));
        menu.Items.Add(capture);
    }
}
