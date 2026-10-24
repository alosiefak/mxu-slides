using System.Windows;
using MxU.Slides.Audio;
using MxU.Slides.Audio.Control;
using MxU.Slides.Audio.Library;
using MxU.Slides.Audio.Routing;
using MxU.Slides.Audio.Video;
using MxU.Slides.Core.Threading;
using MxU.Slides.Windows.Infrastructure;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// Where sound goes on this PC (Screens &amp; Settings › Sound): the audio outputs, where the
/// music plays and where video sound plays, each to a device, a channel pair (or one channel)
/// and a delay; and the music's fades. The music is the Audio function's buses
/// (<see cref="Audio"/>, told where to play through <see cref="AudioController.RoutingFor"/>);
/// video sound is played by our own engine (<see cref="VideoSound"/>, fed by the video hub),
/// because WPF's video player can only use the Windows default device. Port of the
/// routing half of the Mac's AudioController (applyDefaultRoutingToMedia,
/// reapplyAllRouting, deviceListChanged).
/// </summary>
public sealed partial class MainViewModel
{
    // Moving a bus to another device waits for Windows to open it (up to a few seconds), so
    // that happens here, never on the UI thread, and never on .NET's shared pool.
    private static readonly WorkQueue SoundRoutingWork = new("MxU sound routing");

    private AudioRoutingTable? soundRouting;
    private VideoSound? videoSound;

    /// <summary>The outputs, mixes and devices; where music and video sound play.</summary>
    public AudioRoutingTable SoundRouting => soundRouting ??= MakeSoundRouting();

    /// <summary>Every playing video's sound.</summary>
    public VideoSound VideoSound => videoSound ??= MakeVideoSound();

    /// <summary>Raised on the UI thread after anything about where sound goes changed (an output, a choice, a device plugged in or out).</summary>
    public event Action? SoundRoutingChanged;

    /// <summary>From the constructor: route video sound through our engine and keep the routing saved.</summary>
    private void InitializeSound()
    {
        Settings.Sound ??= new SoundSettings();
        Media.Sound = VideoSound;
        SoundRouting.Changed += OnSoundRoutingChanged;
        ApplyVideoSoundRouting();
    }

    private AudioRoutingTable MakeSoundRouting()
    {
        var stored = Settings.Sound ?? new SoundSettings();
        var table = new AudioRoutingTable(new AudioOutputInventory(stored.Outputs), new AudioMixInventory(stored.Mixes), stored.MediaMixId);
        // A first run keeps its "Main Output" and "Main" mix, as the Mac does.
        if (stored.Outputs is not { Count: > 0 } || stored.Mixes is not { Count: > 0 }) SaveSoundRouting(table);
        return table;
    }

    private VideoSound MakeVideoSound()
    {
        var made = new VideoSound();
        made.OutputStatusChanged += status => Log.Info($"Video sound output: {status.Message}");
        return made;
    }

    /// <summary>The music: where each bus plays, whether it reaches a device, and the fades (from <see cref="Audio"/>'s first use).</summary>
    private void ConfigureMusic(AudioController controller)
    {
        var routing = SoundRouting;
        controller.RoutingFor = playlistId => routing.MusicRouting(playlistId);
        controller.MixLevels = key => new AudioMixLevel(1, false, routing.HardwareLive(key));
        controller.FadeEnabled = Settings.Sound.FadeEnabled;
        controller.FadeSeconds = Settings.Sound.FadeSeconds;
        controller.CrossfadeSeconds = Settings.Sound.CrossfadeSeconds;
        controller.PlayersChanged += RefreshMusicTracks;
        controller.TransportChanged += RefreshMusicTracks;
    }

    // An output, a mix, the media mix or the devices changed (any thread).
    private void OnSoundRoutingChanged()
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(OnSoundRoutingChanged);
            return;
        }
        SaveSoundRouting(SoundRouting);
        if (audio is { } controller)
            SoundRoutingWork.Async(() =>
            {
                controller.ReapplyAllRouting();
                controller.ReapplyAllMixLevels();
            });
        ApplyVideoSoundRouting();
        SoundRoutingChanged?.Invoke();
    }

    private void ApplyVideoSoundRouting()
    {
        var routing = SoundRouting;
        VideoSound.SetRouting(routing.MediaRouting(), routing.HardwareLive(routing.MediaMixId));
    }

    private void SaveSoundRouting(AudioRoutingTable table)
    {
        Settings.Sound.Outputs = [.. table.Outputs.Entries];
        Settings.Sound.Mixes = [.. table.Mixes.Entries];
        Settings.Sound.MediaMixId = table.MediaMixId == AudioMixInventory.MainId ? null : table.MediaMixId;
        Settings.Save();
    }

    // MARK: The music's fades (the Mac's Audio module header and Settings › Sound)

    /// <summary>Fade music in and out when it starts, pauses and stops.</summary>
    public bool MusicFadeEnabled
    {
        get => Settings.Sound.FadeEnabled;
        set
        {
            if (Settings.Sound.FadeEnabled == value) return;
            Settings.Sound.FadeEnabled = value;
            Audio.FadeEnabled = value;
            SaveMusicFades();
        }
    }

    /// <summary>How long those fades take (0–5 s, in quarter seconds, as on the Mac).</summary>
    public double MusicFadeSeconds
    {
        get => Settings.Sound.FadeSeconds;
        set
        {
            var seconds = Math.Round(Math.Clamp(value, 0, 5) * 4) / 4;
            if (Settings.Sound.FadeSeconds == seconds) return;
            Settings.Sound.FadeSeconds = seconds;
            Audio.FadeSeconds = seconds;
            SaveMusicFades();
        }
    }

    /// <summary>The room's crossfade between songs (0–12 s, in half seconds; 0 = cut).</summary>
    public double MusicCrossfadeSeconds
    {
        get => Settings.Sound.CrossfadeSeconds;
        set
        {
            var seconds = Math.Round(Math.Clamp(value, 0, PlaylistCrossfade.MaximumSeconds) * 2) / 2;
            if (Settings.Sound.CrossfadeSeconds == seconds) return;
            Settings.Sound.CrossfadeSeconds = seconds;
            Audio.CrossfadeSeconds = seconds;
            SaveMusicFades();
        }
    }

    /// <summary>"Fade 1.5s" / "Fade off", for the Music tab's header.</summary>
    public string MusicFadeLabel => MusicFadeEnabled ? $"Fade {PlaylistCrossfade.Label(MusicFadeSeconds)}" : "Fade off";

    public string MusicCrossfadeLabel => MusicCrossfadeSeconds > 0 ? $"Crossfade {PlaylistCrossfade.Label(MusicCrossfadeSeconds)}" : "Crossfade off";

    private void SaveMusicFades()
    {
        Settings.Save();
        OnPropertyChanged(nameof(MusicFadeEnabled));
        OnPropertyChanged(nameof(MusicFadeSeconds));
        OnPropertyChanged(nameof(MusicCrossfadeSeconds));
        OnPropertyChanged(nameof(MusicFadeLabel));
        OnPropertyChanged(nameof(MusicCrossfadeLabel));
    }

    // MARK: Words for the Sound settings

    /// <summary>"Speakers (Realtek)", "Focusrite USB 3-4 · 20 ms delay"… for a place music or video sound can play.</summary>
    /// <param name="video">For video sound, whose "no device" is silent everywhere: it isn't part of a mix the streams can take (as on the Mac, which turns the video volume down to 0).</param>
    public string SoundTokenTitle(string? token, bool video = false) => token switch
    {
        null => "Same as the music",
        AudioMixInventory.DefaultOutputId => "System default",
        AudioMixInventory.NoOutputId => video ? "No device (silent)" : "No device (streams and recordings only)",
        _ when SoundRouting.Outputs.Entry(token) is { } output => $"{output.Name} — {AudioOutputWords.Subtitle(output, SoundRouting.Devices)}",
        _ => "An output that was removed",
    };

    /// <summary>Where the video sound is going now and how it is doing (for the settings and the self-test).</summary>
    public string VideoSoundSummary()
    {
        var keys = VideoSound.Keys;
        if (keys.Count == 0) return "No video is playing sound.";
        var status = VideoSound.Status(keys[0]);
        return status is null ? "" : $"{status.State}{(status.SyncError is { } error ? $", {error * 1000:+0;-0;0} ms from the picture" : "")} · {status.Output.Message}";
    }
}
