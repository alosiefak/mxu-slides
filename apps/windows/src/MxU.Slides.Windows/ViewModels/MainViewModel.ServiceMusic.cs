using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>One song in a playlist row's card ("Play the playlist starting here").</summary>
public sealed partial class MusicCardTrackViewModel(string entryId, string name, string? length) : ObservableObject
{
    public string EntryId { get; } = entryId;
    public string Name { get; } = name;
    public string? Length { get; } = length;

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }
}

/// <summary>
/// Firing service rows: a presentation's first slide, a media item, or a row's music. Music
/// rows (one song, or a playlist such as the walk-in music a ProPresenter playlist imports as)
/// play through the Music tab's audio engine, and show a card in the middle of the window with
/// their songs, as the Mac's AudioFireView and PlaylistFireView do.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// Fires one service row: a presentation's first slide, a media item, or its music. False
    /// when the row has nothing to fire (an empty presentation, missing media, a header).
    /// </summary>
    private bool FireRow(ServiceItem item)
    {
        switch (item.ItemKind)
        {
            case ServiceItemKind.Presentation when Library.Get<Presentation>(item.RefId) is { } p:
                var arrangementId = item.ArrangementId ?? p.DefaultArrangementId;
                if (SlideSceneBuilder.ArrangedSlides(p, arrangementId).Count == 0) return false;
                Fire(p, arrangementId, 0, item.Id);
                return true;
            case ServiceItemKind.Media when Library.Get<MediaItem>(item.RefId) is { } media:
                FireMediaRow(item, media);
                return true;
            case ServiceItemKind.Audio or ServiceItemKind.Playlist:
                return PlayServiceMusic(item);
            default:
                return false;
        }
    }

    /// <summary>A media row: its media goes up with the row's output preset, and its video's auto-advance starts.</summary>
    private void FireMediaRow(ServiceItem item, MediaItem media)
    {
        // The service has moved on from the slide on screen: its timer would otherwise still fire.
        AutoAdvance.Cancel();
        Live = new LiveContext(item.Id, Live?.PresentationId ?? "", null, 0);
        FireMedia(media, item.Id);
        NoteServiceItemFired(item.Id);
        MarkLiveTiles();
    }

    public static bool IsMusicRow(ServiceItem item) => item.ItemKind is ServiceItemKind.Audio or ServiceItemKind.Playlist;

    /// <summary>The row's music is playing (its row and card show LIVE).</summary>
    private bool IsMusicLive(ServiceItem item) => item.ItemKind switch
    {
        ServiceItemKind.Audio => audio?.Players.Any(p => p.SingleItemId == item.RefId) == true,
        ServiceItemKind.Playlist => audio?.Players.Any(p => p.PlaylistId == item.RefId) == true,
        _ => false,
    };

    /// <summary>
    /// Plays a music row: one song, or a playlist from <paramref name="startAtEntryId"/> (else its
    /// first song). Arriving again on music that's already playing leaves it playing (going back
    /// past the walk-in music with Previous mustn't restart it). False when its music is missing.
    /// </summary>
    private bool PlayServiceMusic(ServiceItem item, string? startAtEntryId = null)
    {
        var playing = IsMusicLive(item);
        switch (item.ItemKind)
        {
            case ServiceItemKind.Audio when Library.Get<AudioItem>(item.RefId) is { } song:
                if (!playing) PlayAudio(song, null);
                break;
            case ServiceItemKind.Playlist when Library.Get<Playlist>(item.RefId) is { } playlist:
                if (!playing || startAtEntryId is not null) PlayAudioPlaylist(playlist, startAtEntryId, null);
                break;
            default:
                Notice = $"\"{item.Name}\" has no music in the library.";
                return false;
        }
        // The service moves on to the music: the slide on screen stays, but its timer stops.
        AutoAdvance.Cancel();
        Live = new LiveContext(item.Id, Live?.PresentationId ?? "", null, 0);
        NoteServiceItemFired(item.Id);
        MarkLiveTiles();
        RefreshMusicCard();
        return true;
    }

    // MARK: The music card (a music row is selected)

    [ObservableProperty]
    public partial bool MusicCardVisible { get; set; }

    [ObservableProperty]
    public partial string MusicCardTitle { get; set; } = "";

    [ObservableProperty]
    public partial string MusicCardDetail { get; set; } = "";

    [ObservableProperty]
    public partial bool MusicCardIsLive { get; set; }

    [ObservableProperty]
    public partial bool MusicCardIsPlaylist { get; set; }

    /// <summary>"1:23 / 3:45" while it plays.</summary>
    [ObservableProperty]
    public partial string MusicCardTime { get; set; } = "";

    [ObservableProperty]
    public partial double MusicCardProgress { get; set; }

    public ObservableCollection<MusicCardTrackViewModel> MusicCardTracks { get; } = [];

    private string? musicCardItemId;

    /// <summary>Play (or Stop, while it plays) on the card; double-clicking the row and Next play it too.</summary>
    [RelayCommand]
    private void MusicCardPlayStop()
    {
        if (SelectedServiceItem?.Item is not { } item || !IsMusicRow(item)) return;
        if (IsMusicLive(item))
        {
            if (item.ItemKind == ServiceItemKind.Playlist) Audio.StopPlaylist(item.RefId);
            else Audio.StopAudioItem(item.RefId);
            UpdateNowPlaying();
            MarkLiveTiles();
        }
        else
        {
            PlayServiceMusic(item);
        }
        RefreshMusicCard();
    }

    /// <summary>"Play the playlist starting here."</summary>
    [RelayCommand]
    private void MusicCardPlayTrack(MusicCardTrackViewModel? track)
    {
        if (track is null || SelectedServiceItem?.Item is not { ItemKind: ServiceItemKind.Playlist } item) return;
        PlayServiceMusic(item, track.EntryId);
    }

    /// <summary>Shows the card for the selected row when it's music, and keeps its live state and time current.</summary>
    private void RefreshMusicCard()
    {
        if (SelectedServiceItem?.Item is not { } item || !IsMusicRow(item))
        {
            MusicCardVisible = false;
            musicCardItemId = null;
            return;
        }
        MusicCardVisible = true;
        MusicCardIsPlaylist = item.ItemKind == ServiceItemKind.Playlist;
        var playlist = MusicCardIsPlaylist ? Library.Get<Playlist>(item.RefId) : null;
        var song = MusicCardIsPlaylist ? null : Library.Get<AudioItem>(item.RefId);
        MusicCardTitle = playlist?.Name ?? song?.Name ?? item.Name;
        MusicCardDetail = playlist is not null ? $"Playlist · {playlist.Entries.Count} song{(playlist.Entries.Count == 1 ? "" : "s")}"
            : song is not null ? $"Music · {Length(song.DurationSeconds) ?? "–:––"}" : "Missing from the library";
        if (musicCardItemId != item.Id)
        {
            musicCardItemId = item.Id;
            MusicCardTracks.Clear();
            foreach (var entry in playlist?.Entries ?? [])
            {
                var name = entry.RefKind == PlaylistRefKind.Audio ? Library.Get<AudioItem>(entry.RefId)?.Name : Library.Get<MediaItem>(entry.RefId)?.Name;
                var seconds = entry.RefKind == PlaylistRefKind.Audio ? Library.Get<AudioItem>(entry.RefId)?.DurationSeconds : Library.Get<MediaItem>(entry.RefId)?.DurationSeconds;
                MusicCardTracks.Add(new MusicCardTrackViewModel(entry.Id, name ?? entry.RefId, Length(seconds)));
            }
        }
        MusicCardIsLive = IsMusicLive(item);
        var player = audio is null ? null
            : MusicCardIsPlaylist ? audio.PlayerForPlaylist(item.RefId) : audio.PlayerForAudioItem(item.RefId);
        foreach (var track in MusicCardTracks) track.IsCurrent = player?.CurrentEntryId == track.EntryId;
        MusicCardTime = player is { Duration: > 0 } p ? $"{Clock(p.Elapsed)} / {Clock(p.Duration)}" : "";
        MusicCardProgress = player is { Duration: > 0 } q ? Math.Clamp(q.Elapsed / q.Duration, 0, 1) : 0;
    }

    private static string? Length(double? seconds) => seconds is { } s && s > 0 ? Clock(s) : null;
}
