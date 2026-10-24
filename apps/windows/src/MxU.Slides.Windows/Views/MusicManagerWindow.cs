using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using MxU.Slides.Audio.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The music manager: the music playlists and every song on the left; the chosen playlist
/// on the right with its name, Repeat, Shuffle, crossfade and songs (add, reorder with ↑ ↓,
/// take out). Right-click a song to rename it, replace its file, add it to a playlist or
/// delete it. Port of the Mac's playlist editor (InspectorView's PlaylistEditor) and its
/// Audio module's playlist menus; the Mac shows these in a popover and the sidebar, Windows
/// in a window. Every change saves at once and a playing playlist picks it up.
/// </summary>
public sealed class MusicManagerWindow : Window
{
    private static MusicManagerWindow? openWindow;

    private readonly MainViewModel model;
    private readonly ListBox playlists = new() { Margin = new Thickness(0, 0, 0, 8), MinHeight = 120 };
    private readonly ListBox songs = new() { SelectionMode = SelectionMode.Extended };
    private readonly ContentControl detail = new();
    private string? selectedId;
    private bool refreshing;

    /// <summary>Opens the manager (or brings the open one forward), showing <paramref name="playlistId"/> when given.</summary>
    public static MusicManagerWindow Open(Window owner, MainViewModel model, string? playlistId = null)
    {
        var window = openWindow ?? new MusicManagerWindow(owner, model);
        if (playlistId is not null) window.Select(playlistId);
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        return window;
    }

    private MusicManagerWindow(Window owner, MainViewModel model)
    {
        this.model = model;
        openWindow = this;
        EditorKit.Dress(this, owner, "Music", 980, 640);

        var title = new TextBlock { Text = "Music", FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var newPlaylist = EditorKit.Button("+ Playlist", NewPlaylist, "Make a music playlist");
        var import = EditorKit.Button("Import music…", Import, "Copy music files into the library");
        import.Margin = new Thickness(0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { newPlaylist, import } };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);
        header.Children.Add(title);

        playlists.SelectionChanged += (_, _) =>
        {
            if (refreshing || playlists.SelectedItem is not ListBoxItem { Tag: string id }) return;
            selectedId = id;
            model.ShowPlaylistTracks(id);
            ShowDetail();
        };
        songs.MouseDoubleClick += (_, _) => AddSelectedSongs();
        songs.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) AddSelectedSongs();
        };

        var left = new DockPanel();
        var playlistsLabel = EditorKit.Section("PLAYLISTS", 0);
        var songsLabel = EditorKit.Section("SONGS", 8);
        var songsHint = EditorKit.Muted("Double-click a song to add it to the playlist. Right-click for more.", 11);
        foreach (var top in new UIElement[] { header, playlistsLabel, playlists, songsLabel, songsHint }) DockPanel.SetDock(top, Dock.Top);
        left.Children.Add(header);
        left.Children.Add(playlistsLabel);
        left.Children.Add(playlists);
        left.Children.Add(songsLabel);
        left.Children.Add(songsHint);
        left.Children.Add(songs);
        var leftPanel = new Border { Style = EditorKit.Res<Style>("Panel"), Child = left, Margin = new Thickness(0, 0, 10, 0) };

        var rightPanel = new Border
        {
            Style = EditorKit.Res<Style>("Panel"), Padding = new Thickness(0),
            Child = new ScrollViewer { Content = new Border { Padding = new Thickness(16, 14, 16, 14), Child = detail }, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        };

        var fade = EditorKit.Button(model.MusicFadeLabel, () => { }, "Fade on play, pause and stop");
        fade.Click += (_, _) => MusicMenus.ShowFadePopup(fade, model);
        var crossfade = EditorKit.Button(model.MusicCrossfadeLabel, () => { }, "Crossfade between songs");
        crossfade.Click += (_, _) => MusicMenus.ShowCrossfadePopup(crossfade, model);
        void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.MusicFadeLabel)) fade.Content = model.MusicFadeLabel;
            if (e.PropertyName == nameof(MainViewModel.MusicCrossfadeLabel)) crossfade.Content = model.MusicCrossfadeLabel;
        }
        model.PropertyChanged += OnModelChanged;
        var done = EditorKit.Button("Done", Close, accent: true);
        done.IsCancel = true;
        done.MinWidth = 90;
        done.Margin = new Thickness(0);
        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(done, Dock.Right);
        footer.Children.Add(done);
        footer.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { fade, crossfade } });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330), MinWidth = 260 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(rightPanel, 1);
        grid.Children.Add(leftPanel);
        grid.Children.Add(rightPanel);
        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(grid);
        Content = new Border { Padding = new Thickness(12), Child = root };

        model.Library.Changed += OnLibraryChanged;
        model.Audio.TransportChanged += OnTransport;
        Closed += (_, _) =>
        {
            model.Library.Changed -= OnLibraryChanged;
            model.Audio.TransportChanged -= OnTransport;
            model.PropertyChanged -= OnModelChanged;
            if (openWindow == this) openWindow = null;
        };
        selectedId = model.ShownPlaylist?.Id ?? model.MusicPlaylists.FirstOrDefault()?.Id;
        Refresh();
    }

    // MARK: Keeping up

    private void OnLibraryChanged(Type type, string id)
    {
        if (type != typeof(Playlist) && type != typeof(AudioItem)) return;
        Dispatcher.BeginInvoke(Refresh);
    }

    // The song playing moved on: only the marks change, so the detail is redrawn without the boxes being typed in.
    private string? shownCurrent;

    private void OnTransport()
    {
        var current = model.Audio.PlayerForPlaylist(selectedId ?? "")?.CurrentEntryId;
        if (current == shownCurrent) return;
        shownCurrent = current;
        ShowDetail();
    }

    private void Select(string playlistId)
    {
        selectedId = playlistId;
        Refresh();
    }

    /// <summary>Redraws both lists and the chosen playlist.</summary>
    internal void Refresh()
    {
        refreshing = true;
        try
        {
            playlists.Items.Clear();
            var all = model.MusicPlaylists;
            if (selectedId is not null && all.All(p => p.Id != selectedId)) selectedId = all.FirstOrDefault()?.Id;
            foreach (var playlist in all)
            {
                var item = new ListBoxItem { Tag = playlist.Id, Content = PlaylistRow(playlist), Padding = new Thickness(6, 3, 6, 3) };
                var id = playlist.Id;
                item.MouseRightButtonUp += (_, e) =>
                {
                    if (model.Library.Get<Playlist>(id) is not { } current) return;
                    var menu = MusicMenus.ForPlaylist(this, model, current, model.Audio.PlayerForPlaylist(id) is not null, () => Select(id));
                    menu.PlacementTarget = item;
                    menu.IsOpen = true;
                    e.Handled = true;
                };
                playlists.Items.Add(item);
                if (id == selectedId) playlists.SelectedItem = item;
            }
            if (all.Count == 0) playlists.Items.Add(new ListBoxItem { Content = EditorKit.Muted("No playlists yet. Press + Playlist.", 11, 0), IsEnabled = false });

            songs.Items.Clear();
            foreach (var song in model.Songs)
            {
                var item = new ListBoxItem { Tag = song.Id, Content = SongRow(song), Padding = new Thickness(6, 2, 6, 2) };
                var id = song.Id;
                item.MouseRightButtonUp += (_, e) =>
                {
                    if (model.Library.Get<AudioItem>(id) is not { } current) return;
                    var menu = MusicMenus.ForSong(this, model, current, model.Audio.PlayerForAudioItem(id) is { PlaylistId: null });
                    menu.PlacementTarget = item;
                    menu.IsOpen = true;
                    e.Handled = true;
                };
                songs.Items.Add(item);
            }
        }
        finally
        {
            refreshing = false;
        }
        ShowDetail();
    }

    private DockPanel PlaylistRow(Playlist playlist)
    {
        var row = new DockPanel();
        var playing = model.Audio.PlayerForPlaylist(playlist.Id) is { IsPlaying: true };
        var count = EditorKit.Faint(playlist.Entries.Count == 1 ? "1 song" : $"{playlist.Entries.Count} songs", 11);
        DockPanel.SetDock(count, Dock.Right);
        row.Children.Add(count);
        var icon = EditorKit.Icon("audio", 13, playing ? EditorKit.Res<Brush>("Good") : null);
        if (playing) icon.Text = "\uE768";
        row.Children.Add(icon);
        row.Children.Add(new TextBlock { Text = playlist.Name, Margin = new Thickness(8, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private DockPanel SongRow(AudioItem song)
    {
        var row = new DockPanel();
        var length = EditorKit.Faint(song.DurationSeconds is { } s ? TimeSpan.FromSeconds(s).ToString(s >= 3600 ? @"h\:mm\:ss" : @"m\:ss") : "", 11);
        DockPanel.SetDock(length, Dock.Right);
        row.Children.Add(length);
        row.Children.Add(new TextBlock { Text = song.Name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0) });
        return row;
    }

    // MARK: The chosen playlist (right)

    private void ShowDetail()
    {
        if (model.Library.Get<Playlist>(selectedId) is not { } playlist)
        {
            detail.Content = EditorKit.Muted("Choose a playlist on the left, or press + Playlist to make one.", 12);
            return;
        }
        var id = playlist.Id;
        var panel = new StackPanel();
        panel.Children.Add(EditorKit.Row("Name", EditorKit.CommitBox(playlist.Name, typed =>
        {
            model.RenamePlaylist(id, typed);
            return model.Library.Get<Playlist>(id)?.Name ?? typed;
        })));

        var repeats = new[] { PlaybackMode.PlayAll, PlaybackMode.LoopPlaylist, PlaybackMode.LoopSingle };
        var repeat = new ComboBox { ItemsSource = repeats.Select(MainViewModel.RepeatName).ToList(), SelectedIndex = Array.IndexOf(repeats, playlist.PlaybackMode), MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Left };
        repeat.SelectionChanged += (_, _) =>
        {
            if (repeat.SelectedIndex >= 0) model.SetPlaylistRepeat(id, repeats[repeat.SelectedIndex]);
        };
        panel.Children.Add(EditorKit.Row("Repeat", repeat));

        var shuffle = new CheckBox { Content = "Play the songs in a random order", IsChecked = playlist.Shuffle == true };
        shuffle.Click += (_, _) => model.SetPlaylistShuffle(id, shuffle.IsChecked == true);
        panel.Children.Add(EditorKit.Row("Shuffle", shuffle));

        var choices = MusicMenus.CrossfadeChoices(model, playlist.CrossfadeSeconds);
        var crossfade = new ComboBox { ItemsSource = choices.Select(c => c.Title).ToList(), MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Left };
        crossfade.SelectedIndex = Math.Max(0, choices.FindIndex(c => c.Seconds == playlist.CrossfadeSeconds));
        crossfade.SelectionChanged += (_, _) =>
        {
            if (crossfade.SelectedIndex >= 0) model.SetPlaylistCrossfade(id, choices[crossfade.SelectedIndex].Seconds);
        };
        panel.Children.Add(EditorKit.Row("Crossfade", crossfade));

        var player = model.Audio.PlayerForPlaylist(id);
        var play = EditorKit.Button(player is null ? "Play" : "Play from the Start", () => model.PlayPlaylistFrom(id, null), accent: true);
        var stop = EditorKit.Button("Stop", () => model.Audio.StopPlaylist(id), "Stops it, with the fade");
        stop.IsEnabled = player is not null;
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(96, 2, 0, 0), Children = { play, stop } });

        var add = EditorKit.Button("+ Add Song", () => { }, "Add a song from the library");
        add.Click += (_, _) => EditorKit.Open(AddSongMenu(id), add);
        add.Margin = new Thickness(0);
        var songsHeader = new DockPanel { Margin = new Thickness(0, 16, 0, 6) };
        DockPanel.SetDock(add, Dock.Right);
        songsHeader.Children.Add(add);
        songsHeader.Children.Add(new TextBlock { Text = $"SONGS ({playlist.Entries.Count})", Style = EditorKit.Res<Style>("SectionLabel"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0) });
        panel.Children.Add(songsHeader);

        var rows = model.TrackRows(playlist);
        var current = player?.CurrentEntryId;
        shownCurrent = current;
        if (rows.Count == 0)
            panel.Children.Add(EditorKit.Muted("Press + Add Song, double-click a song on the left, or right-click a song in the Music tab and choose Add to Playlist.", 12));
        foreach (var track in rows) panel.Children.Add(TrackRow(track, track.EntryId == current, rows.Count));
        detail.Content = panel;
    }

    private Border TrackRow(MusicTrackViewModel track, bool current, int count)
    {
        var row = new DockPanel();
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var up = EditorKit.Ghost("↑", "Earlier", () => model.MoveTrack(track.PlaylistId, track.EntryId, -1));
        up.IsEnabled = track.Number > 1;
        var down = EditorKit.Ghost("↓", "Later", () => model.MoveTrack(track.PlaylistId, track.EntryId, +1));
        down.IsEnabled = track.Number < count;
        actions.Children.Add(EditorKit.Faint(track.Duration, 11));
        actions.Children.Add(up);
        actions.Children.Add(down);
        actions.Children.Add(EditorKit.Ghost("✕", "Take it out of the playlist", () => model.RemoveFromPlaylist(track.PlaylistId, track.EntryId)));
        DockPanel.SetDock(actions, Dock.Right);
        row.Children.Add(actions);
        row.Children.Add(new TextBlock
        {
            Text = current ? "" : $"{track.Number}", FontFamily = current ? EditorKit.IconFont : FontFamily, Width = 24, FontSize = current ? 10 : 11,
            Foreground = current ? EditorKit.Res<Brush>("Good") : EditorKit.Res<Brush>("FaintText"), VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(new TextBlock
        {
            Text = track.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = track.IsMissing ? EditorKit.Res<Brush>("Warn") : EditorKit.Res<Brush>("Text"),
        });
        var card = new Border
        {
            Child = row, Padding = new Thickness(8, 3, 4, 3), CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 0, 2),
            Background = current ? new SolidColorBrush(Color.FromArgb(0x22, 0x7E, 0xD9, 0x57)) : Brushes.Transparent,
            ToolTip = track.IsMissing ? "This song isn't in the library any more; the playlist skips it." : "Double-click to play the playlist from here",
        };
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2 && !track.IsMissing) model.PlayPlaylistFrom(track.PlaylistId, track.EntryId);
        };
        card.MouseRightButtonUp += (_, e) =>
        {
            var menu = MusicBin.TrackMenu(model, track);
            menu.PlacementTarget = card;
            menu.IsOpen = true;
            e.Handled = true;
        };
        return card;
    }

    private ContextMenu AddSongMenu(string playlistId)
    {
        var menu = new ContextMenu { MaxHeight = 480 };
        var all = model.Songs;
        if (all.Count == 0) menu.Items.Add(EditorKit.Command("Import music first (Import music…)", () => { }, enabled: false));
        foreach (var song in all)
        {
            var id = song.Id;
            menu.Items.Add(EditorKit.Command(song.Name, () => model.AddToPlaylist(playlistId, [id])));
        }
        return menu;
    }

    // MARK: Commands

    private void NewPlaylist()
    {
        if (MusicMenus.Ask(this, "New playlist", "Name the playlist:", Core.Library.PlaylistLibrary.UntitledMusicName) is not { } name) return;
        Select(model.NewPlaylist(name).Id);
    }

    private void AddSelectedSongs()
    {
        if (selectedId is null) return;
        var ids = songs.SelectedItems.Cast<ListBoxItem>().Select(i => (string)i.Tag).ToList();
        if (ids.Count > 0) model.AddToPlaylist(selectedId, ids);
    }

    private void Import()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import music", Multiselect = true,
            Filter = "Music|" + string.Join(";", AudioImporter.SupportedExtensions.Select(e => "*" + e)) + "|All files|*.*",
        };
        if (dialog.ShowDialog(this) == true) model.ImportAudioFiles(dialog.FileNames);
    }

    /// <summary>The self-test's view: which playlist is shown and its rows.</summary>
    internal string? SelectedPlaylistId => selectedId;
}
