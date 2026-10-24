using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;
using MxU.Slides.Audio.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The music commands the Music tab and the music manager share: a song's and a playlist's
/// right-click menus (the Mac's LibraryView "Add to Playlist", "New Playlist…", "Replace Music
/// File…", Rename, Delete, and AudioModule's playlist menu: Repeat, Shuffle, Crossfade), and
/// the fade and crossfade popups from the Mac's Audio module header.
/// </summary>
internal static class MusicMenus
{
    private const string Check = "✓  ";

    /// <summary>A song's menu: play, add to a playlist (or a new one), rename, replace its file, delete.</summary>
    public static ContextMenu ForSong(Window owner, MainViewModel model, AudioItem song, bool playing, Action? played = null)
    {
        var menu = new ContextMenu();
        menu.Items.Add(EditorKit.Command(playing ? "Stop" : "Play", () =>
        {
            if (playing) model.Audio.StopAudioItem(song.Id);
            else model.Audio.Play(song);
            played?.Invoke();
        }));
        var add = Submenu("Add to Playlist");
        foreach (var playlist in model.MusicPlaylists)
        {
            var id = playlist.Id;
            add.Items.Add(EditorKit.Command(playlist.Name, () => model.AddToPlaylist(id, [song.Id])));
        }
        if (add.Items.Count > 0) add.Items.Add(EditorKit.Separator());
        add.Items.Add(EditorKit.Command("New Playlist…", () =>
        {
            if (Ask(owner, "New playlist", "Name the playlist:", Core.Library.PlaylistLibrary.UntitledMusicName) is { } name)
                model.NewPlaylist(name, [song.Id]);
        }));
        menu.Items.Add(add);
        menu.Items.Add(EditorKit.Separator());
        menu.Items.Add(EditorKit.Command("Rename…", () =>
        {
            if (Ask(owner, "Rename song", "Name:", song.Name) is { } name) model.RenameSong(song.Id, name);
        }));
        menu.Items.Add(EditorKit.Command("Replace Music File…", () => ReplaceFile(owner, model, song)));
        menu.Items.Add(EditorKit.Separator());
        menu.Items.Add(EditorKit.Command("Delete", () =>
        {
            if (Confirm(owner, $"Delete “{song.Name}”?", "It goes to the library's Trash folder. Playlists keep its place and skip it.")) model.DeleteSong(song.Id);
        }));
        return menu;
    }

    /// <summary>A playlist's menu: play or stop, show its songs, edit, Repeat, Shuffle, Crossfade, rename, delete.</summary>
    public static ContextMenu ForPlaylist(Window owner, MainViewModel model, Playlist playlist, bool playing, Action edit)
    {
        var id = playlist.Id;
        var menu = new ContextMenu();
        menu.Items.Add(EditorKit.Command(playing ? "Stop" : "Play", () =>
        {
            if (playing) model.Audio.StopPlaylist(id);
            else model.PlayPlaylistFrom(id, null);
        }));
        menu.Items.Add(EditorKit.Command("Show Songs", () => model.ShowPlaylistTracks(id)));
        menu.Items.Add(EditorKit.Command("Edit Playlist…", edit));
        menu.Items.Add(EditorKit.Separator());
        foreach (var item in PlaybackItems(model, playlist)) menu.Items.Add(item);
        menu.Items.Add(EditorKit.Separator());
        menu.Items.Add(EditorKit.Command("Rename…", () =>
        {
            if (Ask(owner, "Rename playlist", "Name:", playlist.Name) is { } name) model.RenamePlaylist(id, name);
        }));
        menu.Items.Add(EditorKit.Command("Delete", () =>
        {
            if (Confirm(owner, $"Delete “{playlist.Name}”?", "It goes to the library's Trash folder. Its songs stay in the library.")) model.DeletePlaylist(id);
        }));
        return menu;
    }

    /// <summary>Repeat ›, Shuffle and Crossfade › for a playlist (the Mac's Playback section).</summary>
    public static IEnumerable<object> PlaybackItems(MainViewModel model, Playlist playlist)
    {
        var id = playlist.Id;
        var repeat = Submenu($"Repeat: {MainViewModel.RepeatName(playlist.PlaybackMode)}");
        foreach (var mode in new[] { PlaybackMode.PlayAll, PlaybackMode.LoopPlaylist, PlaybackMode.LoopSingle })
            repeat.Items.Add(EditorKit.Command((playlist.PlaybackMode == mode ? Check : "") + MainViewModel.RepeatName(mode), () => model.SetPlaylistRepeat(id, mode)));
        yield return repeat;
        var shuffled = playlist.Shuffle == true;
        yield return EditorKit.Command((shuffled ? Check : "") + "Shuffle", () => model.SetPlaylistShuffle(id, !shuffled));
        var crossfade = Submenu($"Crossfade: {CrossfadeName(model, playlist.CrossfadeSeconds)}");
        foreach (var (title, seconds) in CrossfadeChoices(model, playlist.CrossfadeSeconds))
            crossfade.Items.Add(EditorKit.Command((playlist.CrossfadeSeconds == seconds ? Check : "") + title, () => model.SetPlaylistCrossfade(id, seconds)));
        yield return crossfade;
    }

    /// <summary>"Use default (3s)", "Off", the presets, and a custom value the playlist already has (the Mac's crossfadeOverridePicker).</summary>
    public static List<(string Title, double? Seconds)> CrossfadeChoices(MainViewModel model, double? current)
    {
        var choices = new List<(string, double?)> { ($"Use default ({PlaylistCrossfade.Label(model.MusicCrossfadeSeconds)})", null), ("Off", 0) };
        choices.AddRange(PlaylistCrossfade.Presets.Select(s => (PlaylistCrossfade.Label(s), (double?)s)));
        if (current is > 0 and var custom && !PlaylistCrossfade.Presets.Contains(custom)) choices.Add((PlaylistCrossfade.Label(custom), custom));
        return choices;
    }

    public static string CrossfadeName(MainViewModel model, double? seconds) =>
        seconds is { } own ? PlaylistCrossfade.Label(own) : $"default ({PlaylistCrossfade.Label(model.MusicCrossfadeSeconds)})";

    public static void ReplaceFile(Window owner, MainViewModel model, AudioItem song)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Replace the file “{song.Name}” plays",
            Filter = "Music|" + string.Join(";", AudioImporter.SupportedExtensions.Select(e => "*" + e)) + "|All files|*.*",
        };
        if (dialog.ShowDialog(owner) != true) return;
        if (model.ReplaceSongFile(song.Id, dialog.FileName) is { } problem)
            MessageBox.Show(owner, problem, "Couldn't replace the music file", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // MARK: The fade and crossfade popups

    /// <summary>"Fade on play, pause, and stop": on or off, and how long (0–5 s).</summary>
    public static void ShowFadePopup(FrameworkElement under, MainViewModel model)
    {
        var on = new CheckBox { Content = "Fade in and out", IsChecked = model.MusicFadeEnabled, Margin = new Thickness(0, 0, 0, 8) };
        var (slider, row) = SliderRow(0, 5, 0.25, model.MusicFadeSeconds);
        slider.IsEnabled = model.MusicFadeEnabled;
        on.Click += (_, _) =>
        {
            model.MusicFadeEnabled = on.IsChecked == true;
            slider.IsEnabled = model.MusicFadeEnabled;
        };
        slider.ValueChanged += (_, e) => model.MusicFadeSeconds = e.NewValue;
        Popup(under, "Fade on play, pause, and stop", on, row);
    }

    /// <summary>"Crossfade between songs": the room's crossfade, and each playing playlist's own.</summary>
    public static void ShowCrossfadePopup(FrameworkElement under, MainViewModel model)
    {
        var (slider, row) = SliderRow(0, PlaylistCrossfade.MaximumSeconds, 0.5, model.MusicCrossfadeSeconds);
        slider.ValueChanged += (_, e) => model.MusicCrossfadeSeconds = e.NewValue;
        var parts = new List<UIElement> { row };
        foreach (var player in model.Audio.Players.Where(p => p.PlaylistId is not null))
        {
            if (model.Library.Get<Playlist>(player.PlaylistId) is not { } playlist) continue;
            var choices = CrossfadeChoices(model, playlist.CrossfadeSeconds);
            var picker = new ComboBox { ItemsSource = choices.Select(c => c.Title).ToList(), MinWidth = 120, Margin = new Thickness(8, 0, 0, 0) };
            picker.SelectedIndex = Math.Max(0, choices.FindIndex(c => c.Seconds == playlist.CrossfadeSeconds));
            var id = playlist.Id;
            picker.SelectionChanged += (_, _) =>
            {
                if (picker.SelectedIndex >= 0) model.SetPlaylistCrossfade(id, choices[picker.SelectedIndex].Seconds);
            };
            var line = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            DockPanel.SetDock(picker, Dock.Right);
            line.Children.Add(picker);
            line.Children.Add(new TextBlock
            {
                Text = playlist.CrossfadeSeconds is { } own ? $"{playlist.Name} overrides: {PlaylistCrossfade.Label(own)}" : $"{playlist.Name} follows this",
                VerticalAlignment = VerticalAlignment.Center, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
            });
            parts.Add(line);
        }
        Popup(under, "Crossfade between songs", [.. parts]);
    }

    private static (Slider Slider, DockPanel Row) SliderRow(double minimum, double maximum, double step, double value)
    {
        var slider = new Slider { Minimum = minimum, Maximum = maximum, TickFrequency = step, IsSnapToTickEnabled = true, Width = 160, Value = value, VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock { Text = PlaylistCrossfade.Label(value), Width = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = EditorKit.Res<Brush>("MutedText") };
        slider.ValueChanged += (_, e) => label.Text = PlaylistCrossfade.Label(e.NewValue);
        var row = new DockPanel();
        DockPanel.SetDock(label, Dock.Right);
        row.Children.Add(label);
        row.Children.Add(slider);
        return (slider, row);
    }

    private static void Popup(FrameworkElement under, string title, params UIElement[] content)
    {
        var panel = new StackPanel { Margin = new Thickness(12, 10, 12, 10), MinWidth = 240 };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 12, Margin = new Thickness(0, 0, 0, 8) });
        foreach (var part in content) panel.Children.Add(part);
        var popup = new Popup
        {
            Child = new Border { Child = panel, Background = EditorKit.Res<Brush>("Card"), BorderBrush = EditorKit.Res<Brush>("Border"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) },
            PlacementTarget = under, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
        };
        popup.IsOpen = true;
    }

    // MARK: Small dialogs

    private static MenuItem Submenu(string title) => new() { Header = title, Style = (Style)EditorKit.Styles["CascadingMenuItem"] };

    public static string? Ask(Window owner, string title, string prompt, string initial) =>
        InputDialog.Ask(owner, title, prompt, initial) is { } typed && typed.Trim().Length > 0 ? typed.Trim() : null;

    public static bool Confirm(Window owner, string question, string detail) =>
        MessageBox.Show(owner, detail, question, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
}
