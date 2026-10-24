using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MxU.Slides.Audio;
using MxU.Slides.Audio.Control;
using MxU.Slides.Audio.Library;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.Infrastructure;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>A music file or playlist in the bin.</summary>
public sealed partial class AudioTileViewModel : ObservableObject
{
    public AudioTileViewModel(AudioItem item) { Item = item; Name = item.Name; Detail = Duration(item.DurationSeconds); }

    public AudioTileViewModel(Playlist playlist) { Playlist = playlist; Name = playlist.Name; Detail = $"Playlist · {playlist.Entries.Count} songs"; }

    public AudioItem? Item { get; }
    public Playlist? Playlist { get; }
    public string Name { get; }
    public string Detail { get; }

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    private static string Duration(double? seconds) => seconds is { } s && s > 0 ? TimeSpan.FromSeconds(s).ToString(s >= 3600 ? @"h\:mm\:ss" : @"m\:ss") : "Music";
}

/// <summary>
/// Music: audio files and playlists played by the audio engine (ported from the Mac),
/// out of this PC's sound output, with fades and crossfades. The Audio clear (F5) stops it.
/// </summary>
public sealed partial class MainViewModel
{
    private AudioController? audio;
    private DispatcherTimer? audioTicker;

    /// <summary>The Audio function (created on the UI thread, so its events come back to it).</summary>
    public AudioController Audio
    {
        get
        {
            if (audio is not null) return audio;
            audio = new AudioController(new LibraryAudioSource(Library), SynchronizationContext.Current);
            audio.PlayersChanged += UpdateNowPlaying;
            audio.TransportChanged += UpdateNowPlaying;
            audio.OutputStatusChanged += status => Log.Info($"Audio output: {status}");
            return audio;
        }
    }

    public ObservableCollection<AudioTileViewModel> AudioTiles { get; } = [];

    [ObservableProperty]
    public partial string NowPlaying { get; set; } = "";

    [ObservableProperty]
    public partial bool IsMusicPlaying { get; set; }

    public void RefreshAudio()
    {
        AudioTiles.Clear();
        foreach (var playlist in Library.All<Playlist>().Where(p => p.PlaylistKind == PlaylistKind.Audio).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
            AudioTiles.Add(new AudioTileViewModel(playlist));
        foreach (var item in Library.All<AudioItem>().OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
            AudioTiles.Add(new AudioTileViewModel(item));
        UpdateNowPlaying();
    }

    /// <summary>Plays an audio file; <paramref name="repeatOverride"/> true repeats it.</summary>
    private void PlayAudio(AudioItem item, bool? repeatOverride)
    {
        try { Audio.Play(item, repeatOverride); }
        catch (AudioEngineException ex) { Problem = $"Couldn't play \"{item.Name}\": {ex.Message}"; }
        UpdateNowPlaying();
    }

    /// <summary>Plays a playlist from an entry (null: its first).</summary>
    private void PlayAudioPlaylist(Playlist playlist, string? startAtEntryId, bool? repeatOverride)
    {
        try { Audio.Play(playlist, startAtEntryId, repeatOverride); }
        catch (AudioEngineException ex) { Problem = $"Couldn't play \"{playlist.Name}\": {ex.Message}"; }
        UpdateNowPlaying();
    }

    /// <summary>A click on a music tile: play it, or stop it if it's the one playing.</summary>
    [RelayCommand]
    private void AudioTileClicked(AudioTileViewModel? tile)
    {
        if (tile is null) return;
        if (tile.IsPlaying)
        {
            if (tile.Playlist is { } playing) Audio.StopPlaylist(playing.Id);
            else if (tile.Item is { } item) Audio.StopAudioItem(item.Id);
        }
        else if (tile.Playlist is { } playlist) PlayAudioPlaylist(playlist, null, null);
        else if (tile.Item is { } item) PlayAudio(item, null);
        UpdateNowPlaying();
    }

    [RelayCommand]
    private void MusicPlayPause() => Audio.Transport(AudioTransportAction.PlayPause);

    [RelayCommand]
    private void MusicNext() => Audio.Transport(AudioTransportAction.Next);

    [RelayCommand]
    private void MusicPrevious() => Audio.Transport(AudioTransportAction.Previous);

    [RelayCommand]
    private void MusicStop()
    {
        Audio.Stop();
        UpdateNowPlaying();
    }

    /// <summary>Copies music files into the library (same file twice is stored once).</summary>
    public void ImportAudioFiles(IEnumerable<string> paths)
    {
        var importer = new AudioImporter(Library);
        var imported = 0;
        foreach (var path in paths)
        {
            try
            {
                if (importer.Import(path) is not null) imported++;
            }
            catch (Exception ex)
            {
                Log.Warn($"Couldn't import {path}: {ex.Message}");
                Problem = $"Couldn't import {Path.GetFileName(path)}: {ex.Message}";
            }
        }
        RefreshAudio();
        if (imported > 0) Notice = imported == 1 ? "Imported 1 music file." : $"Imported {imported} music files.";
    }

    private void UpdateNowPlaying()
    {
        if (audio is null) return;
        var status = audio.Status();
        foreach (var tile in AudioTiles)
            tile.IsPlaying = status.Any(b => b.Playing && (tile.Playlist is { } p ? b.PlaylistId == p.Id : b.AudioItemId == tile.Item?.Id && b.PlaylistId is null));
        if (audio.NowPlaying is { } now)
        {
            IsMusicPlaying = now.IsPlaying;
            NowPlaying = $"{now.Name} · {Clock(now.Elapsed)} / {Clock(now.Duration)}";
            audioTicker ??= new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => UpdateNowPlaying(), Dispatcher.CurrentDispatcher);
            audioTicker.Start();
        }
        else
        {
            IsMusicPlaying = false;
            NowPlaying = "";
            audioTicker?.Stop();
        }
    }

    private static string Clock(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
}
