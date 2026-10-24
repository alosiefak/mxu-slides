using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The Music tab (see MusicBin.xaml): click a tile to play or stop it, right-click for the
/// song or playlist commands, double-click a song in the list to play the playlist from
/// there. Port of the Mac's Audio module (Modules/AudioModule.swift) laid out for the
/// Windows bin: the Mac lists playlists down the sidebar and opens one to see its songs.
/// </summary>
public partial class MusicBin : UserControl
{
    public MusicBin()
    {
        InitializeComponent();
    }

    private MainViewModel? Model => DataContext as MainViewModel;

    private Window Owner => Window.GetWindow(this)!;

    private static T? ContextOf<T>(object sender) where T : class => (sender as FrameworkElement)?.DataContext as T;

    private void Tile_Click(object sender, MouseButtonEventArgs e)
    {
        if (Model is not { } model || ContextOf<AudioTileViewModel>(sender) is not { } tile) return;
        if (tile.Playlist is { } playlist) model.ShowPlaylistTracks(playlist.Id);
        model.AudioTileClickedCommand.Execute(tile);
    }

    private void Tile_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (Model is not { } model || ContextOf<AudioTileViewModel>(sender) is not { } tile) return;
        var menu = tile.Playlist is { } playlist
            ? MusicMenus.ForPlaylist(Owner, model, model.Library.Get<Core.Model.Playlist>(playlist.Id) ?? playlist, tile.IsPlaying,
                () => MusicManagerWindow.Open(Owner, model, playlist.Id))
            : MusicMenus.ForSong(Owner, model, tile.Item!, tile.IsPlaying);
        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void NewPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        if (MusicMenus.Ask(Owner, "New playlist", "Name the playlist:", Core.Library.PlaylistLibrary.UntitledMusicName) is not { } name) return;
        var made = model.NewPlaylist(name);
        // Straight into the editor to add its songs, as the Mac opens its playlist popover.
        MusicManagerWindow.Open(Owner, model, made.Id);
    }

    private void Manage_Click(object sender, RoutedEventArgs e)
    {
        if (Model is { } model) MusicManagerWindow.Open(Owner, model, model.ShownPlaylist?.Id);
    }

    private void Fade_Click(object sender, RoutedEventArgs e)
    {
        if (Model is { } model) MusicMenus.ShowFadePopup(FadeButton, model);
    }

    private void Crossfade_Click(object sender, RoutedEventArgs e)
    {
        if (Model is { } model) MusicMenus.ShowCrossfadePopup(CrossfadeButton, model);
    }

    private void Track_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Model is { } model && ContextOf<MusicTrackViewModel>(sender) is { IsMissing: false } track) model.PlayPlaylistFrom(track.PlaylistId, track.EntryId);
    }

    private void Track_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (Model is not { } model || ContextOf<MusicTrackViewModel>(sender) is not { } track) return;
        var menu = TrackMenu(model, track);
        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>A song in a playlist: play from here, move it, take it out (the Mac's track list menu).</summary>
    internal static ContextMenu TrackMenu(MainViewModel model, MusicTrackViewModel track)
    {
        var menu = new ContextMenu();
        menu.Items.Add(EditorKit.Command("Play from Here", () => model.PlayPlaylistFrom(track.PlaylistId, track.EntryId), !track.IsMissing));
        menu.Items.Add(EditorKit.Command("Move Up", () => model.MoveTrack(track.PlaylistId, track.EntryId, -1)));
        menu.Items.Add(EditorKit.Command("Move Down", () => model.MoveTrack(track.PlaylistId, track.EntryId, +1)));
        menu.Items.Add(EditorKit.Separator());
        menu.Items.Add(EditorKit.Command("Remove from Playlist", () => model.RemoveFromPlaylist(track.PlaylistId, track.EntryId)));
        return menu;
    }
}
