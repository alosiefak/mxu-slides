using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MxU.Slides.Audio.Library;
using MxU.Slides.Audio.Timeline;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

// The timeline editor's left column (lane names, "+" menus, track rows) and its menus:
// a card's right-click menu with the clip commands, the song menu, and the add menus.
public sealed partial class TimelineEditorWindow
{
    // MARK: The lane names on the left

    /// <summary>One name per row, at exactly the row's height, so they line up with the drawn lanes. Port of the Mac's label VStack.</summary>
    private void BuildLabels()
    {
        var vm = ViewModel;
        labels.Children.Clear();
        labels.Margin = new Thickness(0, TimelineEditorLayout.LanesTop, 0, 0);
        foreach (var row in vm.Layout.Rows) labels.Children.Add(LabelFor(row));
        if (vm.HasSong && vm.CanEdit && (vm.HiddenLanes.Count > 0 || !vm.ShowsTracks))
        {
            var add = new Button
            {
                Content = "+ Add a layer", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0),
                Style = (Style)FindResource("GhostButton"), FontSize = 11, ToolTip = "Add a layer: overlays, alerts, video and more",
            };
            add.Click += (_, _) =>
            {
                var menu = Menu(add);
                foreach (var lane in vm.HiddenLanes) menu.Items.Add(Item(lane.Title(), () => vm.AddLane(lane)));
                if (!vm.ShowsTracks) menu.Items.Add(Item("Tracks", vm.AddTracks));
                menu.IsOpen = true;
            };
            labels.Children.Add(add);
        }
    }

    private FrameworkElement LabelFor(TimelineRow row)
    {
        var vm = ViewModel;
        var panel = new DockPanel { Height = row.Height, Margin = new Thickness(0, 0, 0, TimelineEditorLayout.RowGap), LastChildFill = true, Background = Brushes.Transparent };
        switch (row.Kind)
        {
            case TimelineRowKind.Lane when row.Lane is { } lane:
                if (vm.CanEdit) DockRight(panel, PlusButton($"Add to {lane.Title()} at the playhead", menu => FillAddMenu(menu, lane)));
                panel.Children.Add(LaneName(lane.Title()));
                break;
            case TimelineRowKind.Tracks:
            {
                if (vm.CanEdit) DockRight(panel, PlusButton(
                    "Import Audio: a song's files at the playhead, each on the track its name matches (new tracks made as needed). New Empty Track: a row to drop one file onto.",
                    FillTracksMenu));
                var stems = TimelineTracks.Stems(vm.Cues).Count;
                var fold = new Button
                {
                    Content = (vm.TracksExpanded ? "▾ " : "▸ ") + (stems == 0 ? "Tracks" : $"Tracks · {stems}"),
                    Style = (Style)FindResource("GhostButton"), FontSize = 11, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left,
                    HorizontalContentAlignment = HorizontalAlignment.Left, Foreground = (Brush)FindResource("MutedText"),
                    ToolTip = vm.TracksExpanded ? "Fold this song's tracks into one row" : "Show this song's tracks",
                };
                fold.Click += (_, _) => vm.TracksExpanded = !vm.TracksExpanded;
                panel.Children.Add(fold);
                break;
            }
            case TimelineRowKind.Track when row.ChannelId is { } id:
            {
                var channel = vm.TrackRows.FirstOrDefault(c => c.Id == id);
                var grid = new Grid { Height = row.Height, Margin = panel.Margin, Background = Brushes.Transparent };
                var line = new DockPanel { LastChildFill = true, Margin = new Thickness(11, 0, 0, 0) };
                var toggles = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                toggles.Children.Add(Toggle("M", vm.IsMuted(id), Color.FromRgb(0xFF, 0x95, 0x00),
                    vm.IsMuted(id) ? "Unmute this track" : "Mute this track", () => vm.ToggleMute(id)));
                toggles.Children.Add(Toggle("S", vm.IsSoloed(id), Color.FromRgb(0xFF, 0xCC, 0x00),
                    vm.IsSoloed(id) ? "Stop soloing" : "Solo: hear only soloed tracks (click and guide stay)", () => vm.ToggleSolo(id)));
                DockRight(line, toggles);
                var name = LaneName(channel?.Name ?? "Track");
                name.ToolTip = channel?.Name;
                line.Children.Add(name);
                grid.Children.Add(line);
                // Drag the bottom edge to size this track; double-click it to match the others.
                var resize = new Thumb { Height = 5, VerticalAlignment = VerticalAlignment.Bottom, Cursor = Cursors.SizeNS, Opacity = 0, ToolTip = "Drag to size this track; double-click to match the others" };
                var startHeight = row.Height;
                resize.DragStarted += (_, _) => startHeight = vm.TrackHeight(id);
                resize.DragDelta += (_, e) =>
                {
                    startHeight += e.VerticalChange;
                    vm.SetTrackScale(id, Math.Max(startHeight, TimelineEditorLayout.RowHeights.Lower) / Math.Max(vm.RowHeight, 1));
                };
                resize.MouseDoubleClick += (_, _) => vm.SetTrackScale(id, null);
                grid.Children.Add(resize);
                if (vm.CanEdit)
                {
                    grid.ContextMenu = new ContextMenu();
                    grid.ContextMenuOpening += (_, _) => FillTrackMenu(grid.ContextMenu, id);
                }
                return grid;
            }
        }
        return panel;
    }

    private TextBlock LaneName(string text) => new()
    {
        Text = text, FontSize = 11, Foreground = (Brush)FindResource("MutedText"), VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private static void DockRight(DockPanel panel, FrameworkElement right)
    {
        DockPanel.SetDock(right, Dock.Right);
        panel.Children.Add(right);
    }

    private Button PlusButton(string tip, Action<ContextMenu> fill)
    {
        var button = new Button
        {
            Content = "+", ToolTip = tip, Style = (Style)FindResource("GhostButton"), Padding = new Thickness(5, 0, 5, 0), MinHeight = 18,
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Click += (_, _) =>
        {
            var menu = Menu(button);
            fill(menu);
            if (menu.Items.Count == 0) menu.Items.Add(Item("Nothing to add yet", null));
            menu.IsOpen = true;
        };
        return button;
    }

    private Button Toggle(string letter, bool on, Color tint, string tip, Action click)
    {
        var button = new Button
        {
            Content = letter, ToolTip = tip, FontSize = 9, FontWeight = FontWeights.Bold, Width = 18, MinHeight = 15, Height = 15,
            Padding = new Thickness(0), Margin = new Thickness(2, 0, 0, 0),
            Background = on ? new SolidColorBrush(tint) : (Brush)FindResource("Raised"),
            Foreground = on ? Brushes.Black : (Brush)FindResource("MutedText"),
        };
        button.Click += (_, _) => click();
        return button;
    }

    private static ContextMenu Menu(UIElement target) => new() { PlacementTarget = target, Placement = PlacementMode.Bottom };

    private static MenuItem Item(string header, Action? click, bool enabled = true, bool? check = null)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled && click is not null };
        if (check is { } on)
        {
            item.IsCheckable = true;
            item.IsChecked = on;
            if (on) item.Header = header + "  ✓";
        }
        if (click is not null) item.Click += (_, _) => click();
        return item;
    }

    private static MenuItem Sub(string header, bool enabled = true) => new() { Header = header, IsEnabled = enabled };

    // MARK: Add menus (the "+" beside each lane: adds at the playhead)

    private void FillAddMenu(ItemsControl menu, TimelineLane lane)
    {
        var vm = ViewModel;
        var library = model.Library;
        switch (lane)
        {
            case TimelineLane.Slide:
                foreach (var (occurrence, label) in vm.SlideChoices()) menu.Items.Add(Item(label, () => vm.AddSlide(occurrence)));
                break;
            case TimelineLane.LoopingVideos:
            case TimelineLane.StillGraphics:
            case TimelineLane.Videos:
                foreach (var media in vm.MediaChoices(lane)) menu.Items.Add(Item(media.Name, () => vm.AddMedia(media)));
                if (menu.Items.Count == 0) menu.Items.Add(Item("No media for this layer in the library yet", null));
                break;
            case TimelineLane.Overlays:
                foreach (var overlay in library.All<Overlay>()) menu.Items.Add(Item(overlay.Name, () => vm.AddOverlay(overlay)));
                break;
            case TimelineLane.Alerts:
                foreach (var alert in library.All<AlertPreset>()) menu.Items.Add(Item(alert.Name, () => vm.AddAlert(alert)));
                break;
            case TimelineLane.VideoInput:
                foreach (var input in model.Inputs.Entries) menu.Items.Add(Item(input.Name, () => vm.AddLiveInput(input.Id)));
                if (menu.Items.Count == 0) menu.Items.Add(Item("No video inputs set up", null));
                break;
            case TimelineLane.Audio:
                foreach (var item in vm.MusicChoices()) menu.Items.Add(Item(item.Name, () => vm.AddMusic(item)));
                if (menu.Items.Count > 0) menu.Items.Add(new Separator());
                menu.Items.Add(Item("Import Music Files…", () =>
                {
                    if (PickAudioFiles() is { } files) vm.ImportMusic(files, vm.InsertionTime);
                }));
                break;
            case TimelineLane.Actions:
                TimelineActionsWindow.FillAddActions(menu, model, action => vm.AddActions([action]));
                break;
        }
    }

    private string[]? PickAudioFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Title = "Choose audio files",
            Filter = "Audio|" + string.Join(";", AudioImporter.SupportedExtensions.Select(e => "*" + e)) + "|All files|*.*",
        };
        return dialog.ShowDialog(this) == true ? dialog.FileNames : null;
    }

    private void FillTracksMenu(ContextMenu menu)
    {
        var vm = ViewModel;
        menu.Items.Add(Item("Import Audio Files…", () =>
        {
            if (PickAudioFiles() is { } files) vm.ImportStems(files, vm.InsertionTime);
        }));
        menu.Items.Add(Item("Import a Song's Folder…", () =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a song's folder: each file goes on the track its name matches" };
            if (dialog.ShowDialog(this) == true) vm.ImportStems([dialog.FolderName], vm.InsertionTime);
        }));
        menu.Items.Add(Item("New Empty Track…", () =>
        {
            if (InputDialog.Ask(this, "New track", "Track name", "") is { Length: > 0 } name) vm.MakeTrack(name);
        }));
        var sets = vm.TrackSetChoices();
        if (sets.Count > 0)
        {
            var add = Sub("Add a Track Set");
            foreach (var set in sets) add.Items.Add(Item($"{set.Name} ({set.Stems.Count} stems)", () => vm.AddTrackSet(set)));
            menu.Items.Add(add);
        }
        if (vm.AddableTracks.Count > 0)
        {
            var existing = Sub("Add an Existing Track");
            foreach (var channel in vm.AddableTracks) existing.Items.Add(Item(channel.Name, () => vm.AddTrackRow(channel.Id)));
            menu.Items.Add(existing);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(vm.SongMap is { } map ? $"Section Map ({map.Sections.Count})…" : "Section Map…", () => SongMapWindow.Edit(this, vm)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Set Up Outputs…", () => TrackOutputsWindow.Edit(this, model)));
    }

    /// <summary>A track row's right-click menu. Port of trackRowMenu.</summary>
    private void FillTrackMenu(ContextMenu menu, string id)
    {
        var vm = ViewModel;
        menu.Items.Clear();
        var known = vm.Channels.FirstOrDefault(c => c.Id == id);
        var hasStems = TimelineTracks.Stems(vm.Cues).Any(s => s.TrackChannelId() == id);
        if (known is not null)
        {
            menu.Items.Add(Item("Rename…", () =>
            {
                if (InputDialog.Ask(this, "Rename track", "Track name", known.Name) is { } name) vm.RenameChannel(id, name);
            }));
            var role = Sub($"Role: {known.TrackRole()}");
            foreach (var each in Enum.GetValues<TimelineTracks.Role>())
                role.Items.Add(Item(each.ToString(), () => vm.SetChannelRole(id, each), check: known.TrackRole() == each));
            menu.Items.Add(role);
            menu.Items.Add(Item("Mono", () => vm.SetChannelMono(id, known.Mono != true), check: known.Mono == true));
        }
        if (hasStems)
        {
            var move = Sub("Move Stems To");
            foreach (var other in vm.Channels.Where(c => c.Id != id)) move.Items.Add(Item(other.Name, () => vm.MoveStems(id, other.Id)));
            if (move.Items.Count == 0) move.IsEnabled = false;
            menu.Items.Add(move);
        }
        else if (vm.CanRemoveTrackRow(id))
        {
            menu.Items.Add(Item("Remove from This Song", () => vm.RemoveTrackRow(id)));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Track Outputs…", () => TrackOutputsWindow.Edit(this, model)));
    }

    // MARK: The song menu

    private void ShowSongMenu()
    {
        var vm = ViewModel;
        var menu = Menu(songMenu);
        menu.Placement = PlacementMode.Bottom;
        menu.Items.Add(Item("Timecode Start…", () => TimecodeStartWindow.Edit(this, vm), vm.HasSong));
        menu.Items.Add(Item("This song plays from timecode", () => vm.SetFollowsTimecode(vm.Timeline?.TimecodeEnabled != true), vm.CanEdit,
            check: vm.Timeline?.TimecodeEnabled == true));
        menu.Items.Add(new Separator());
        if (vm.ServiceItem is not null)
        {
            var current = vm.Transition;
            var kind = SongTransitions.KindOf(current);
            var transition = Sub($"Transition to the Next Item: {TimelineEditing.Summary(current)}");
            foreach (var option in Enum.GetValues<SongTransitions.Kind>())
                transition.Items.Add(Item(option.Title(), () => vm.SetTransition(TimelineEditing.Choosing(option, current)), check: kind == option));
            if (kind != SongTransitions.Kind.Stop)
            {
                transition.Items.Add(new Separator());
                var length = Sub("Length");
                length.Items.Add(Item(kind == SongTransitions.Kind.Overlap ? "Automatic (by sections, up to 5 s)" : "Automatic (5 s)",
                    () => vm.SetTransition(new SongTransition { Kind = kind.Raw() }), check: current?.Seconds is null));
                length.Items.Add(new Separator());
                foreach (var seconds in TimelineEditing.TransitionLengths)
                    length.Items.Add(Item(TimelineEditing.Seconds(seconds), () => vm.SetTransition(new SongTransition { Kind = kind.Raw(), Seconds = seconds }),
                        check: current?.Seconds == seconds));
                transition.Items.Add(length);
            }
            menu.Items.Add(transition);
        }
        else
        {
            menu.Items.Add(Item("Transition to the next item: open the song from a service", null));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(vm.SongMap is { } map ? $"Section Map ({map.Sections.Count})…" : "Section Map…", () => SongMapWindow.Edit(this, vm), vm.HasSong));
        menu.Items.Add(Item("Track Outputs and Interface…", () => TrackOutputsWindow.Edit(this, model)));
        menu.IsOpen = true;
    }

    // MARK: A card's right-click menu

    /// <summary>The menu for a card (or, with no card, for an empty stretch of lane). Port of the card's contextMenu and clipMenu.</summary>
    private void ShowLaneMenu(TimelineCard? card, TimelineRow? row, double time)
    {
        var vm = ViewModel;
        if (!vm.HasSong) return;
        var menu = new ContextMenu { PlacementTarget = Lanes, Placement = PlacementMode.MousePoint };
        if (card is null)
        {
            menu.Items.Add(Item("Paste at Playhead (Ctrl+V)", vm.Paste, vm.CanEdit && vm.HasClipboard));
            if (vm.Range is not null && vm.CanEdit)
            {
                menu.Items.Add(Item("Copy the Range (Ctrl+C)", () => vm.Copy()));
                menu.Items.Add(Item("Ripple Delete the Range (Shift+Delete)", vm.RippleDelete));
            }
            menu.Items.Add(Item("Move the Playhead Here", () => vm.Seek(time)));
            menu.IsOpen = true;
            return;
        }
        var cue = card.Cue;
        if (!vm.Selection.Contains(cue.Id)) vm.Select([cue.Id]);
        menu.Items.Add(Item("Go Here", () => vm.Seek(cue.At)));
        if (vm.CanEdit)
        {
            if (cue.Kind() == TimelineLane.Actions) menu.Items.Add(Item("Edit Actions…", () => TimelineActionsWindow.Edit(this, vm, cue)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Nudge 1 Frame Earlier", () => vm.Move(cue, TimelineBlockEdit.Mode.Move, -PresentationTimelines.Frame, "Nudge Cue")));
            menu.Items.Add(Item("Nudge 1 Frame Later", () => vm.Move(cue, TimelineBlockEdit.Mode.Move, PresentationTimelines.Frame, "Nudge Cue")));
            if (TimelineClips.IsClip(cue) || TimelineEditing.IsFolded(cue)) AddClipItems(menu, cue);
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Remove from Timeline", () => vm.Delete(cue)));
            if (cue.TrackChannelId() is not null && !TimelineEditing.IsFolded(cue)) menu.Items.Add(Item("Remove These Stems", () => vm.RemoveStems(cue)));
        }
        menu.IsOpen = true;
    }

    private void AddClipItems(ContextMenu menu, TimelineCue cue)
    {
        var vm = ViewModel;
        var cues = vm.Cues;
        var folded = TimelineEditing.IsFolded(cue);
        var reach = TimelineEditing.Reach(cue, cues);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Split at Playhead (Ctrl+B)", () => vm.Split(reach)));
        menu.Items.Add(Item("Copy (Ctrl+C)", () => vm.Copy(reach)));
        menu.Items.Add(Item("Paste at Playhead (Ctrl+V)", vm.Paste, vm.HasClipboard));
        if (!folded)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(FadeMenu(cue, @out: false));
            menu.Items.Add(FadeMenu(cue, @out: true));
            var overlapping = TimelineClips.Overlapping(cue, cues);
            foreach (var other in overlapping)
            {
                var (left, right) = cue.At <= other.At ? (cue, other) : (other, cue);
                if (TimelineClips.PlacementNamed(right.FadeIn?.Placement) is { } placement)
                {
                    var crossfade = Sub($"Crossfade: {placement.Title()}");
                    foreach (var choice in Enum.GetValues<TimelineClips.Placement>())
                    {
                        var possible = choice == placement || vm.CrossfadeAtCutPreview(left, right, choice) is not null;
                        crossfade.Items.Add(Item(choice.Title(), () => vm.PlaceCrossfade(cue, other, choice), possible, check: choice == placement));
                    }
                    crossfade.Items.Add(new Separator());
                    crossfade.Items.Add(Item("Remove Crossfade", () => vm.RemoveCrossfade(cue, other)));
                    menu.Items.Add(crossfade);
                }
                else
                {
                    var crossfade = Sub(overlapping.Count > 1 ? $"Crossfade with the Clip at {TimelineEditing.Clock(other.At)}" : "Crossfade");
                    FillShapes(crossfade, @out: false, null, shape => vm.Crossfade(cue, other, shape));
                    menu.Items.Add(crossfade);
                }
            }
            foreach (var other in TimelineClips.Butted(cue, cues))
            {
                var (left, right) = cue.At <= other.At ? (cue, other) : (other, cue);
                if (vm.CrossfadeAtCutPreview(left, right) is { } made)
                {
                    var atCut = Sub($"Crossfade at the Cut ({made.Placement.Title()})");
                    FillShapes(atCut, @out: false, null, shape => vm.CrossfadeAtCut(left, right, shape));
                    menu.Items.Add(atCut);
                }
                else
                {
                    menu.Items.Add(Item("Crossfade at the Cut: no audio past either clip's edge", null));
                }
            }
            var edges = cue.EdgeFades ?? true;
            menu.Items.Add(Item("Edge Fades (5 ms)", () => vm.SetEdgeFades(cue, !edges), check: edges));
        }
        if (vm.Range is not null)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Ripple Delete Range (Shift+Delete)", vm.RippleDelete));
        }
    }

    private MenuItem FadeMenu(TimelineCue cue, bool @out)
    {
        var vm = ViewModel;
        var name = @out ? "Fade Out" : "Fade In";
        if ((@out ? cue.FadeOut : cue.FadeIn) is { } fade)
        {
            var shape = TimelineClips.ShapeNamed(fade.Shape);
            var menu = Sub($"{name}: {shape.Title()}, {TimelineEditing.Seconds(fade.Seconds)}");
            FillShapes(menu, @out, shape, chosen => vm.SetFade(cue, @out, chosen));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item($"Remove {name}", () => vm.SetFade(cue, @out, null)));
            return menu;
        }
        var add = Sub($"Add {name}");
        FillShapes(add, @out, null, chosen => vm.SetFade(cue, @out, chosen));
        return add;
    }

    /// <summary>The fade shapes, each with a small picture of its curve. Port of ClipFadeShapeItems.</summary>
    private static void FillShapes(MenuItem menu, bool @out, TimelineClips.Shape? current, Action<TimelineClips.Shape> choose)
    {
        foreach (var shape in TimelineClips.Shapes)
        {
            var points = ClipFadeGeometry.ShapePicture(shape, @out);
            var picture = new System.Windows.Shapes.Polyline
            {
                Points = [.. points.Select(p => new Point(p.X, p.Y))], Stroke = (Brush)Application.Current.FindResource("Text"),
                StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round, Width = 22, Height = 14, Margin = new Thickness(0, 0, 8, 0),
            };
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(picture);
            header.Children.Add(new TextBlock { Text = shape == current ? $"{shape.Title()}  ✓" : shape.Title(), VerticalAlignment = VerticalAlignment.Center });
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => choose(shape);
            menu.Items.Add(item);
        }
    }
}
