using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MxU.Slides.Audio.Library;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.Infrastructure;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>One song in a playlist, as the Music tab and the music manager list it.</summary>
public sealed partial class MusicTrackViewModel : ObservableObject
{
    public required string PlaylistId { get; init; }
    public required string EntryId { get; init; }
    public required int Number { get; init; }
    public required string Name { get; init; }
    public required string Duration { get; init; }

    /// <summary>The song isn't in the library any more (it is skipped when the playlist plays).</summary>
    public bool IsMissing { get; init; }

    /// <summary>The track playing now.</summary>
    [ObservableProperty]
    public partial bool IsCurrent { get; set; }
}

/// <summary>
/// The music manager: making, naming and deleting playlists, adding, removing and reordering
/// their songs, Repeat, Shuffle and each playlist's crossfade; renaming, deleting and
/// replacing songs; and the Music tab's list of the playlist playing now. Port of the Mac's
/// Modules/AudioModule.swift (with InspectorView's PlaylistEditor) and LibraryView's playlist
/// commands ("Add to Playlist", "New Playlist…", "Replace Music File…"). The rules are
/// Core's <see cref="PlaylistLibrary"/>; each change is saved at once and the playlist, if it
/// is playing, picks it up (the Mac's playlistDocumentChanged).
/// </summary>
public sealed partial class MainViewModel
{
    private string? chosenPlaylistId;

    /// <summary>The songs of the playlist the Music tab shows: the one playing, else the last one chosen.</summary>
    public ObservableCollection<MusicTrackViewModel> MusicTracks { get; } = [];

    /// <summary>The playlist <see cref="MusicTracks"/> belongs to.</summary>
    [ObservableProperty]
    public partial Playlist? ShownPlaylist { get; set; }

    /// <summary>"Walk-in · 2 of 5 · Loop Playlist · Shuffle".</summary>
    [ObservableProperty]
    public partial string MusicTracksTitle { get; set; } = "";

    public IReadOnlyList<Playlist> MusicPlaylists => PlaylistLibrary.MusicPlaylists(Library);

    /// <summary>Mutes all music (the Audio module's speaker button). The songs play on, silently, and streams hear them muted too.</summary>
    public bool MusicMuted
    {
        get => audio?.IsMuted ?? false;
        set
        {
            Audio.IsMuted = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MusicMuteGlyph));
        }
    }

    /// <summary>A speaker, or a crossed-out one while muted.</summary>
    public string MusicMuteGlyph => MusicMuted ? "" : "";

    public IReadOnlyList<AudioItem> Songs => Library.All<AudioItem>();

    /// <summary>Shows a playlist's songs in the Music tab (when nothing else is playing).</summary>
    public void ShowPlaylistTracks(string? playlistId)
    {
        chosenPlaylistId = playlistId;
        RefreshMusicTracks();
    }

    /// <summary>Redraws the track list: which playlist, its songs and the one playing now.</summary>
    public void RefreshMusicTracks()
    {
        var player = audio?.Players.FirstOrDefault(p => p.PlaylistId is not null && p.IsPlaying) ?? audio?.Players.FirstOrDefault(p => p.PlaylistId is not null);
        var playlist = Library.Get<Playlist>(player?.PlaylistId ?? chosenPlaylistId);
        if (playlist is null || !PlaylistLibrary.IsMusic(playlist))
        {
            if (ShownPlaylist is not null || MusicTracks.Count > 0)
            {
                ShownPlaylist = null;
                MusicTracks.Clear();
            }
            MusicTracksTitle = "";
            return;
        }
        var current = player?.PlaylistId == playlist.Id ? player.CurrentEntryId : null;
        var same = ShownPlaylist?.Id == playlist.Id && MusicTracks.Select(t => (t.EntryId, t.Name)).SequenceEqual(playlist.Entries.Select(e => (e.Id, SongName(e))));
        if (!same)
        {
            ShownPlaylist = playlist;
            MusicTracks.Clear();
            foreach (var row in TrackRows(playlist)) MusicTracks.Add(row);
        }
        foreach (var track in MusicTracks) track.IsCurrent = track.EntryId == current;
        var parts = new List<string> { playlist.Name };
        if (player?.PlaylistId == playlist.Id && player.PassPosition is { } pass) parts.Add($"{pass.Index} of {pass.Count}");
        else parts.Add(playlist.Entries.Count == 1 ? "1 song" : $"{playlist.Entries.Count} songs");
        if (playlist.PlaybackMode != PlaybackMode.PlayAll) parts.Add(RepeatName(playlist.PlaybackMode));
        if (playlist.Shuffle == true) parts.Add("Shuffle");
        MusicTracksTitle = string.Join(" · ", parts);
    }

    /// <summary>A playlist's songs as rows (numbered, with their length; a missing song says so).</summary>
    public List<MusicTrackViewModel> TrackRows(Playlist playlist) =>
    [
        .. playlist.Entries.Select((entry, index) =>
        {
            var song = entry.RefKind == PlaylistRefKind.Audio ? Library.Get<AudioItem>(entry.RefId) : null;
            return new MusicTrackViewModel
            {
                PlaylistId = playlist.Id, EntryId = entry.Id, Number = index + 1, Name = SongName(entry),
                Duration = song?.DurationSeconds is { } seconds ? Clock(seconds) : "", IsMissing = song is null,
            };
        }),
    ];

    private string SongName(PlaylistEntry entry) =>
        (entry.RefKind == PlaylistRefKind.Audio ? Library.Get<AudioItem>(entry.RefId)?.Name : null) ?? "Missing song";

    /// <summary>"Off", "Loop Playlist", "Loop Single" (the Mac's Repeat menu).</summary>
    public static string RepeatName(PlaybackMode mode) => mode switch
    {
        PlaybackMode.LoopPlaylist => "Loop Playlist",
        PlaybackMode.LoopSingle => "Loop Single",
        _ => "Off",
    };

    // MARK: Playlists

    /// <summary>Makes a music playlist (with songs in it, for "Add to Playlist › New Playlist…").</summary>
    public Playlist NewPlaylist(string? name = null, IEnumerable<string>? songIds = null)
    {
        var playlist = PlaylistLibrary.Create(PlaylistKind.Audio, name);
        foreach (var id in songIds ?? []) PlaylistLibrary.Add(playlist, id, PlaylistRefKind.Audio);
        Library.Save(playlist);
        ShowPlaylistTracks(playlist.Id);
        return playlist;
    }

    public bool RenamePlaylist(string id, string name) => ChangePlaylist(id, p => PlaylistLibrary.Rename(p, name));

    /// <summary>Deletes a playlist (to the library's Trash), stopping it first if it plays.</summary>
    public void DeletePlaylist(string id)
    {
        if (audio?.PlayerForPlaylist(id) is not null) audio.StopPlaylist(id);
        Library.Delete<Playlist>(id);
        if (chosenPlaylistId == id) chosenPlaylistId = null;
        RefreshMusicTracks();
    }

    public void AddToPlaylist(string playlistId, IEnumerable<string> songIds)
    {
        var ids = songIds.ToList();
        ChangePlaylist(playlistId, p => ids.Select(id => PlaylistLibrary.Add(p, id, PlaylistRefKind.Audio)).Count(e => e is not null) > 0);
    }

    public void InsertIntoPlaylist(string playlistId, string songId, int index) =>
        ChangePlaylist(playlistId, p => PlaylistLibrary.Insert(p, songId, PlaylistRefKind.Audio, index) is not null);

    public void RemoveFromPlaylist(string playlistId, string entryId) => ChangePlaylist(playlistId, p => PlaylistLibrary.Remove(p, entryId));

    /// <summary>Up (−1) or down (+1) one place.</summary>
    public void MoveTrack(string playlistId, string entryId, int delta) => ChangePlaylist(playlistId, p => PlaylistLibrary.MoveBy(p, entryId, delta));

    public void MoveTrackBefore(string playlistId, string entryId, string? beforeEntryId) =>
        ChangePlaylist(playlistId, p => PlaylistLibrary.MoveBefore(p, entryId, beforeEntryId));

    public void SetPlaylistRepeat(string id, PlaybackMode mode) => ChangePlaylist(id, p => PlaylistLibrary.SetRepeat(p, mode));

    public void SetPlaylistShuffle(string id, bool on) => ChangePlaylist(id, p => PlaylistLibrary.SetShuffle(p, on));

    /// <summary>The playlist's own crossfade, or null to follow the room's.</summary>
    public void SetPlaylistCrossfade(string id, double? seconds) => ChangePlaylist(id, p => PlaylistLibrary.SetCrossfade(p, seconds));

    /// <summary>Plays a playlist from one of its songs ("Play the playlist starting here").</summary>
    public void PlayPlaylistFrom(string playlistId, string? entryId)
    {
        if (Library.Get<Playlist>(playlistId) is not { } playlist) return;
        PlayAudioPlaylist(playlist, entryId, null);
        ShowPlaylistTracks(playlistId);
    }

    // Saves a changed copy, then lets a playing playlist pick the change up.
    private bool ChangePlaylist(string id, Func<Playlist, bool> change)
    {
        if (PlaylistLibrary.Update(Library, id, change) is null) return false;
        audio?.PlaylistDocumentChanged();
        RefreshMusicTracks();
        return true;
    }

    // MARK: Songs

    public bool RenameSong(string id, string name)
    {
        if (Library.Get<AudioItem>(id) is not { } song || name.Trim() is not { Length: > 0 } trimmed || trimmed == song.Name) return false;
        var copy = Core.Json.MxuJson.Clone(song);
        copy.Name = trimmed;
        Library.Save(copy);
        RefreshMusicTracks();
        return true;
    }

    /// <summary>Deletes a song (to the library's Trash), stopping it first if it plays on its own. Playlists keep their place for it, as on the Mac (it is skipped).</summary>
    public void DeleteSong(string id)
    {
        if (audio?.PlayerForAudioItem(id) is { PlaylistId: null }) audio.StopAudioItem(id);
        Library.Delete<AudioItem>(id);
        RefreshMusicTracks();
    }

    /// <summary>"Replace Music File…": the song plays the new file from now on. Returns why it couldn't, or null.</summary>
    public string? ReplaceSongFile(string id, string path)
    {
        if (Library.Get<AudioItem>(id) is not { } song) return "That song isn't in the library any more.";
        try
        {
            new AudioImporter(Library).ReplaceFile(song, path);
            Notice = $"“{song.Name}” now plays {Path.GetFileName(path)}.";
            RefreshMusicTracks();
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Log.Warn($"Couldn't replace the file of {song.Name}: {ex.Message}");
            return ex.Message;
        }
    }
}
