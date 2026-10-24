using System.Windows;
using System.Windows.Controls;
using MxU.Slides.Audio;
using MxU.Slides.Audio.Routing;
using MxU.Slides.Audio.Video;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Views;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// The Phase 5 sound checks: music follows its output's device, delay and "no device";
/// a video's sound plays through our engine and stays with the picture through a pause, a
/// seek and an output change, and still reaches recordings; the music manager makes, edits
/// and plays a playlist; pictures of the Music tab, the music manager and Settings › Sound.
/// On a PC with no sound output (CI) the device-only checks are skipped, not failed: the
/// engine keeps time without one, so the sync checks still run.
/// </summary>
public sealed partial class SelfTest
{
    private async Task SoundCheck()
    {
        var made = new List<string>();
        var routing = model.SoundRouting;
        var hasDevice = AudioDeviceList.DefaultOutputDevice() is not null;
        var outputsBefore = routing.Outputs.Entries.Select(o => o.Id).ToHashSet();
        try
        {
            await MusicRoutingCheck(made, hasDevice);
            await MusicManagerCheck(made);
            await VideoSoundCheck(made, hasDevice);
            await SoundSettingsPicture();
        }
        finally
        {
            model.ClearAudioCommand.Execute(null);
            model.ClearAllCommand.Execute(null);
            routing.SetMusicOutput(AudioMixInventory.DefaultOutputId);
            routing.SetVideoOutput(null);
            foreach (var output in routing.Outputs.Entries.Where(o => !outputsBefore.Contains(o.Id)).ToList()) routing.Outputs.Remove(output.Id);
            foreach (var path in made)
                try { File.Delete(path); }
                catch (IOException) { }
            model.Bin = ViewModels.BinTab.Media;
        }
    }

    // MARK: Music routing

    /// <summary>The music plays on the main mix's output: its delay, then "no device" (streams only), then back.</summary>
    private async Task MusicRoutingCheck(List<string> made, bool hasDevice)
    {
        var routing = model.SoundRouting;
        var tone = SoundTone(made, "routing", 30);
        if (tone is null) return;
        var output = routing.Outputs.Create("Self-test house");
        routing.Outputs.SetDelay(output.Id, 25);
        routing.SetMusicOutput(output.Id);
        model.Audio.Play(tone);
        var bus = await Until(() => model.Audio.PlayerForAudioItem(tone.Id), p => p is { IsPlaying: true }, 4000);
        Check("music plays on the output chosen for it (its 25 ms delay applied)", bus is { TargetKey: AudioMixInventory.MainId } && bus.Engine.OutputDelayMilliseconds == 25);
        if (hasDevice)
            Check("the music's output reached a device", await Until(() => bus?.Engine.OutputStatus.State, s => s == AudioOutputState.Playing, 4000) == AudioOutputState.Playing);
        else
            Skip("the music's output reached a device", "this PC has no sound output; the engine keeps time without one");

        routing.SetMusicOutput(AudioMixInventory.NoOutputId);
        Check("“No device” takes the music off the speakers (streams and recordings still get it)",
            await Until(() => bus?.Engine.HardwareOutputEnabled, on => on == false, 3000) == false && bus?.IsPlaying == true);
        routing.SetMusicOutput(AudioMixInventory.DefaultOutputId);
        Check("back on the default device", await Until(() => bus?.Engine.HardwareOutputEnabled, on => on == true, 3000) == true
            && bus?.Engine.OutputDelayMilliseconds == 0);
        report.AppendLine($"      music now: {model.SoundTokenTitle(routing.MusicOutputToken)}");
        model.Audio.Stop();
        routing.Outputs.Remove(output.Id);
    }

    // MARK: The music manager

    /// <summary>Makes a playlist of two songs, plays it from the second, changes Repeat, Shuffle, order and crossfade, renames, replaces a file and deletes.</summary>
    private async Task MusicManagerCheck(List<string> made)
    {
        var first = SoundTone(made, "walk-in one", 20);
        var second = SoundTone(made, "walk-in two", 20);
        var longer = SoundTone(made, "walk-in replacement", 25, import: false);
        if (first is null || second is null) return;
        model.Bin = ViewModels.BinTab.Music;
        var playlist = model.NewPlaylist("Self-test walk-in", [first.Id, second.Id]);
        Check("a new playlist holds the songs it was made with", model.Library.Get<Playlist>(playlist.Id) is { Entries.Count: 2, PlaylistKind: PlaylistKind.Audio });
        Check("the playlist has a tile in the Music tab", model.AudioTiles.Any(t => t.Playlist?.Id == playlist.Id));
        var secondEntry = playlist.Entries[1].Id;
        model.PlayPlaylistFrom(playlist.Id, secondEntry);
        var bus = await Until(() => model.Audio.PlayerForPlaylist(playlist.Id), p => p is { IsPlaying: true }, 4000);
        Check("“play from here” starts the playlist at that song", bus?.CurrentEntryId == secondEntry);
        await Until(() => model.MusicTracks.FirstOrDefault(t => t.IsCurrent)?.EntryId, id => id == secondEntry, 2000);
        Check("the Music tab lists the playing playlist's songs, the playing one marked",
            model.MusicTracks.Count == 2 && model.MusicTracks.Single(t => t.IsCurrent).EntryId == secondEntry && model.MusicTracksTitle.Contains("2 of 2"));

        model.SetPlaylistRepeat(playlist.Id, PlaybackMode.LoopPlaylist);
        model.SetPlaylistShuffle(playlist.Id, true);
        model.SetPlaylistCrossfade(playlist.Id, 4);
        var stored = model.Library.Get<Playlist>(playlist.Id)!;
        Check("Repeat, Shuffle and the playlist's own crossfade are saved",
            stored is { PlaybackMode: PlaybackMode.LoopPlaylist, Shuffle: true, CrossfadeSeconds: 4 } && model.MusicTracksTitle.Contains("Loop Playlist") && model.MusicTracksTitle.Contains("Shuffle"));
        model.SetPlaylistShuffle(playlist.Id, false);
        model.MoveTrack(playlist.Id, secondEntry, -1);
        Check("a song moves up the playlist", model.Library.Get<Playlist>(playlist.Id)!.Entries[0].Id == secondEntry && model.MusicTracks[0].EntryId == secondEntry);
        model.MusicMuted = true;
        Check("“mute all music” silences the playing playlist (it plays on)", bus is { IsPlaying: true, Engine.IsMuted: true });
        model.MusicMuted = false;
        model.MusicFadeSeconds = 2;
        Check("the fade and crossfade settings reach the music and settings.json",
            model.Audio.FadeSeconds == 2 && model.Settings.Sound.FadeSeconds == 2 && model.MusicFadeLabel == "Fade 2s");
        await Step("41-music-bin", () => { }, settle: 600);

        var manager = MusicManagerWindow.Open(window, model, playlist.Id);
        await Delay(700);
        Save(manager, "42-music-manager");
        Check("the music manager opens on the playlist", manager.SelectedPlaylistId == playlist.Id);
        model.RenamePlaylist(playlist.Id, "Self-test offering");
        model.RenameSong(first.Id, "Self-test renamed song");
        await Delay(300);
        Check("renaming a playlist and a song saves them", model.Library.Get<Playlist>(playlist.Id)?.Name == "Self-test offering"
            && model.Library.Get<AudioItem>(first.Id)?.Name == "Self-test renamed song" && model.MusicTracks.Any(t => t.Name == "Self-test renamed song"));
        Save(manager, "42b-music-manager-renamed");
        manager.Close();

        if (longer is not null)
        {
            Check("Replace Music File points the song at the new file", model.ReplaceSongFile(first.Id, longer.FileName) is null
                && model.Library.Get<AudioItem>(first.Id) is { DurationSeconds: > 24 } replaced && replaced.Name == "Self-test renamed song");
        }
        model.RemoveFromPlaylist(playlist.Id, secondEntry);
        Check("taking a song out of the playlist leaves the other", model.Library.Get<Playlist>(playlist.Id)!.Entries.Count == 1);
        model.DeletePlaylist(playlist.Id);
        model.DeleteSong(second.Id);
        Check("deleting a playlist stops it; deleting a song removes its tile",
            model.Audio.PlayerForPlaylist(playlist.Id) is null && model.Library.Get<Playlist>(playlist.Id) is null && model.AudioTiles.All(t => t.Item?.Id != second.Id));
        model.DeleteSong(first.Id);
        model.MusicFadeSeconds = 1.5;
    }

    // MARK: Video sound

    /// <summary>A foreground video with sound: our engine plays it, in step with the picture, through a pause, a seek and an output change.</summary>
    private async Task VideoSoundCheck(List<string> made, bool hasDevice)
    {
        var video = await VideoWithSound(made, seconds: 12);
        if (video is null) return;
        video.Classification = MediaClassification.Foreground;
        video.Loops = false;
        model.Library.Save(video);
        model.FireMedia(video);
        var key = $"live:live-media-videos:{video.Id}";
        var playing = await Until(() => model.Media.SoundStatus(key), s => s is { State: VideoSoundState.Playing } or { State: VideoSoundState.NoSound }, 15000);
        Check("a video's sound plays through the app's own engine (the picture player is muted)" + (playing?.Problem is { } why ? $" ({why})" : ""),
            model.Media.PlaysSoundItself(key) && playing is { State: VideoSoundState.Playing });
        if (playing is not { State: VideoSoundState.Playing }) return;
        LogVideoClock(key);

        var settled = await InSync(key, 5000);
        report.AppendLine($"      video sound vs picture: {Ms(settled?.SyncError)}, landed {settled?.Landings}x, output: {settled?.Output.Message}");
        Check("the video's sound stays with the picture (within 50 ms)", settled?.SyncError is { } error && Math.Abs(error) < 0.05);
        if (hasDevice) Check("the video's sound reaches a device", settled?.Output.State == AudioOutputState.Playing);
        else Skip("the video's sound reaches a device", "this PC has no sound output; the sound keeps time without one");
        await Step("40-video-sound", () => { }, settle: 300);

        model.Media.Pause(key);
        var paused = await Until(() => model.Media.SoundStatus(key), s => s?.State == VideoSoundState.Paused, 2000);
        var heldAt = paused?.Position;
        await Delay(500);
        Check("pausing the video pauses its sound", paused?.State == VideoSoundState.Paused && model.Media.SoundStatus(key)?.Position == heldAt);
        model.Media.Play(key);
        var resumed = await InSync(key, 4000);
        Check("playing again picks the sound up in step", resumed is { State: VideoSoundState.Playing, SyncError: { } afterPause } && Math.Abs(afterPause) < 0.06);

        var landings = resumed?.Landings ?? 0;
        model.Media.Seek(key, TimeSpan.FromSeconds(6));
        await Until(() => model.Media.SoundStatus(key), s => s?.Landings > landings, 3000);
        var sought = await InSync(key, 4000);
        report.AppendLine($"      after a seek to 6 s: sound at {sought?.Position:0.00} s, {Ms(sought?.SyncError)} from the picture");
        Check("a seek moves the sound with the picture", sought is { State: VideoSoundState.Playing, Position: > 5.9, SyncError: { } afterSeek } && Math.Abs(afterSeek) < 0.06);

        // Give video sound its own output, 100 ms late (a projector that shows the picture late).
        var routing = model.SoundRouting;
        var late = routing.Outputs.Create("Self-test projector speakers");
        routing.Outputs.SetDelay(late.Id, 100);
        routing.SetVideoOutput(late.Id);
        await Delay(600);
        Check("video sound can play on its own output, with its delay", model.VideoSound.Routing.DelayMs == 100 && routing.MediaMixId != AudioMixInventory.MainId
            && routing.MusicRouting(null).DelayMs == 0 && model.Media.SoundStatus(key)?.State == VideoSoundState.Playing);
        routing.SetVideoOutput(null);

        if (!hasDevice)
        {
            Skip("recording the program audio with a video's sound", "this PC has no sound output, so Windows has nothing to capture");
        }
        else if (model.Program is not null)
        {
            model.Media.Seek(key, TimeSpan.FromSeconds(1));
            var preset = new StreamRecordPreset
            {
                Name = "Self-test video sound recording", PresetKind = StreamPresetKind.RecordOnly, Width = 640, Height = 360, FrameRate = 30, VideoBitrateKbps = 1500,
            };
            model.Library.Save(preset);
            try
            {
                var (peak, file) = await RecordSoundAsync(preset);
                report.AppendLine($"      program-audio recording with the video: {file} peak {peak:0.000}");
                Check("a recording of the program audio has the video's sound in it", peak > 0.01);
            }
            finally
            {
                model.Library.Delete<StreamRecordPreset>(preset.Id);
            }
        }
        model.ClearMediaCommand.Execute(null);
        await Until(() => model.Media.SoundStatus(key), s => s is null, 4000);
        Check("clearing the video lets its sound go", model.Media.SoundStatus(key) is null);
    }

    // Waits until the sound is playing and close to the picture (or the time runs out), then says how it is.
    private async Task<VideoSoundStatus?> InSync(string key, int milliseconds) =>
        await Until(() => model.Media.SoundStatus(key), s => s is { State: VideoSoundState.Playing, SyncError: { } e } && Math.Abs(e) < 0.03, milliseconds);

    // A few readings of WPF's video clock, so the report shows how smoothly it moves.
    private void LogVideoClock(string key)
    {
        if (model.Media.Transport(key) is not { } first) return;
        var start = ShowState.HostClock();
        var steps = new List<string>();
        var last = first.Position.TotalSeconds;
        var lastHost = start;
        while (ShowState.HostClock() - start < 0.2)
        {
            Thread.Sleep(15);
            if (model.Media.Transport(key) is not { } now) break;
            var host = ShowState.HostClock();
            steps.Add($"{(now.Position.TotalSeconds - last) * 1000:0}/{(host - lastHost) * 1000:0}");
            last = now.Position.TotalSeconds;
            lastHost = host;
        }
        report.AppendLine($"      WPF video clock, moved/elapsed ms: {string.Join(" ", steps)}");
    }

    private static string Ms(double? seconds) => seconds is { } s ? $"{s * 1000:+0;-0;0} ms" : "unknown";

    // MARK: Settings › Sound

    private async Task SoundSettingsPicture()
    {
        if (model.Outputs is null) return;
        var routing = model.SoundRouting;
        var foldback = routing.Outputs.Create("Self-test foldback");
        routing.Outputs.SetDelay(foldback.Id, 40);
        var settings = new SettingsWindow(model, model.Outputs) { Owner = window };
        settings.Show();
        await Delay(600);
        settings.SoundPanel.Expand(foldback.Id);
        settings.SoundPanel.BringIntoView();
        await Delay(500);
        Save(settings, "43-sound-settings");
        var shown = Descendants<TextBlock>(settings.SoundPanel).Select(t => t.Text).ToList();
        Check("Settings › Sound lists the outputs and where music and video sound play",
            shown.Contains("Self-test foldback") && shown.Any(t => t.Contains("40 ms delay")) && shown.Contains("Music plays on") && shown.Contains("Video sound on"));
        settings.Close();
        routing.Outputs.Remove(foldback.Id);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    // MARK: Making sound to test with

    /// <summary>A generated tone, imported as a song (or just written, with <paramref name="import"/> false: then Path is the file and the item is a stand-in).</summary>
    private AudioItem? SoundTone(List<string> made, string name, double seconds, bool import = true)
    {
        var path = Path.Combine(Path.GetTempPath(), $"MxU self-test {name} {Guid.NewGuid():N}.wav");
        made.Add(path);
        // Each tone its own pitch: the library stores a file once by its content, so two identical tones would be one song.
        WriteSoundTone(path, seconds, 300 + 20 * made.Count);
        if (!import) return new AudioItem { Id = "", Name = name, FileHash = "", FileName = path, Tags = [] };
        model.ImportAudioFiles([path]);
        var song = model.Library.All<AudioItem>().FirstOrDefault(a => a.FileName == Path.GetFileName(path));
        if (song is null) Check($"a test tone ({name}) imports", false);
        return song;
    }

    /// <summary>An MP4 made by Windows' own encoder from a starter background with a tone as its sound, imported.</summary>
    private async Task<MediaItem?> VideoWithSound(List<string> made, double seconds)
    {
        string path;
        try
        {
            var still = model.MediaItems.FirstOrDefault(m => !m.IsVideo);
            if (still is null || model.Library.BlobPath(still.Item) is not { } image) return null;
            var tone = Path.Combine(Path.GetTempPath(), $"MxU self-test video tone {Guid.NewGuid():N}.wav");
            made.Add(tone);
            WriteTone(tone, seconds);
            path = Path.Combine(Path.GetTempPath(), $"MxU self-test video with sound {Guid.NewGuid():N}.mp4");
            made.Add(path);
            var composition = new global::Windows.Media.Editing.MediaComposition();
            composition.Clips.Add(await global::Windows.Media.Editing.MediaClip.CreateFromImageFileAsync(
                await global::Windows.Storage.StorageFile.GetFileFromPathAsync(image), TimeSpan.FromSeconds(seconds)));
            composition.BackgroundAudioTracks.Add(await global::Windows.Media.Editing.BackgroundAudioTrack.CreateFromFileAsync(
                await global::Windows.Storage.StorageFile.GetFileFromPathAsync(tone)));
            var folder = await global::Windows.Storage.StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)!);
            var target = await folder.CreateFileAsync(Path.GetFileName(path), global::Windows.Storage.CreationCollisionOption.ReplaceExisting);
            var profile = global::Windows.Media.MediaProperties.MediaEncodingProfile.CreateMp4(global::Windows.Media.MediaProperties.VideoEncodingQuality.HD720p);
            var result = await composition.RenderToFileAsync(target, global::Windows.Media.Editing.MediaTrimmingPreference.Fast, profile);
            if (result != global::Windows.Media.Transcoding.TranscodeFailureReason.None)
            {
                report.AppendLine($"NOTE  couldn't make a test video with sound ({result}); video sound not checked");
                return null;
            }
        }
        catch (Exception ex)
        {
            report.AppendLine($"NOTE  couldn't make a test video with sound ({ex.Message}); video sound not checked");
            return null;
        }
        var video = model.ImportMediaFiles([path]).FirstOrDefault();
        Check("a video with sound imports", video is { MediaKind: MediaKind.Video });
        return video;
    }

    /// <summary>A 16-bit stereo WAV of a sine at <paramref name="hertz"/>.</summary>
    private static void WriteSoundTone(string path, double seconds, double hertz)
    {
        const int rate = 48000;
        var frames = (int)(rate * seconds);
        using var file = new BinaryWriter(File.Create(path));
        file.Write("RIFF"u8);
        file.Write(36 + frames * 4);
        file.Write("WAVEfmt "u8);
        file.Write(16);
        file.Write((short)1);
        file.Write((short)2);
        file.Write(rate);
        file.Write(rate * 4);
        file.Write((short)4);
        file.Write((short)16);
        file.Write("data"u8);
        file.Write(frames * 4);
        for (var i = 0; i < frames; i++)
        {
            var sample = (short)(Math.Sin(2 * Math.PI * hertz * i / rate) * 1200);
            file.Write(sample);
            file.Write(sample);
        }
    }

    /// <summary>Polls <paramref name="read"/> every 50 ms until <paramref name="done"/> (or the time is up); returns the last reading.</summary>
    private static async Task<T> Until<T>(Func<T> read, Func<T, bool> done, int milliseconds)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        var value = read();
        while (!done(value) && DateTime.UtcNow < deadline)
        {
            await Delay(50);
            value = read();
        }
        return value;
    }
}
