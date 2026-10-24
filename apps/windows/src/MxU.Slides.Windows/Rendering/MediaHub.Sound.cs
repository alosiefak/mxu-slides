using System.Windows.Threading;
using MxU.Slides.Audio.Video;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Windows.Rendering;

/// <summary>
/// Video sound. WPF's MediaPlayer always plays a video's sound on the Windows default device,
/// so a video with sound plays its picture here with the player muted, and its sound through
/// our own engine (<see cref="VideoSound"/>): on the video sound's device, channels and delay
/// (Settings › Sound), like the Mac's MediaEngine with setAudioOutputDevice and
/// setAudioOutputDelay. To keep the two together the hub reads each player's clock 25 times
/// a second (and at once on a pause, play, seek, restart or loop) and hands it to the sound,
/// which holds, lands or steers to match (<see cref="VideoClockFollower"/>).
/// <para>
/// A file whose sound our engine can't read (no audio track, or a codec only the video
/// player has) is given back to the video player, which then plays it as before.
/// </para>
/// </summary>
public sealed partial class MediaHub
{
    private static readonly TimeSpan SoundLook = TimeSpan.FromMilliseconds(40);

    private readonly Dictionary<string, Player> soundPlayers = new(StringComparer.Ordinal);
    private VideoSound? sound;
    private DispatcherTimer? soundClock;

    /// <summary>Plays audible videos' sound; null = the video players play it themselves (the Windows default device).</summary>
    public VideoSound? Sound
    {
        get => sound;
        set
        {
            sound = value;
            ApplySoundVolume();
        }
    }

    /// <summary>
    /// Called by <see cref="Video"/> for a player that should be heard: its sound is played by
    /// <see cref="Sound"/>, and the caller mutes the picture player. False when there is no
    /// <see cref="Sound"/> (the player then plays its own sound, as before).
    /// </summary>
    /// <param name="inPoint">The cue's trim start (seconds into the file), when it has one.</param>
    /// <param name="outPoint">Its trim end: no sound is played past it.</param>
    /// <param name="rate">Its play rate (the sound keeps its pitch).</param>
    private bool TakeSound(string playbackKey, Player player, string path, double? inPoint = null, double? outPoint = null, double rate = 1)
    {
        if (sound is null) return false;
        sound.Attach(playbackKey, path, new VideoSoundOptions(inPoint, outPoint, rate));
        soundPlayers[playbackKey] = player;
        soundClock ??= new DispatcherTimer(SoundLook, DispatcherPriority.Normal, (_, _) => LookAtVideoClocks(), dispatcher);
        soundClock.Start();
        return true;
    }

    /// <summary>A pause or a play: tell the sound now rather than at the next look.</summary>
    private void SoundTransportChanged(string playbackKey) => LookAtVideoClock(playbackKey);

    /// <summary>A seek, a restart or a loop (to <paramref name="to"/>, when known): the sound lands again once the picture moves from there.</summary>
    private void SoundJumped(string playbackKey, TimeSpan? to = null)
    {
        if (!soundPlayers.ContainsKey(playbackKey)) return;
        sound?.Discontinuity(playbackKey, to?.TotalSeconds);
        LookAtVideoClock(playbackKey);
    }

    /// <summary>The player closed: its sound goes (with a short fade).</summary>
    private void ReleaseSound(string playbackKey)
    {
        if (!soundPlayers.Remove(playbackKey)) return;
        sound?.Detach(playbackKey);
        if (soundPlayers.Count == 0) soundClock?.Stop();
    }

    private void ApplySoundVolume()
    {
        if (sound is not null) sound.Volume = (float)Math.Clamp(VideoVolume, 0, 1);
    }

    /// <summary>How a playing video's sound is doing (diagnostics, the self-test).</summary>
    public VideoSoundStatus? SoundStatus(string playbackKey) => soundPlayers.ContainsKey(playbackKey) ? sound?.Status(playbackKey) : null;

    /// <summary>True while our engine (not the video player) plays this video's sound.</summary>
    public bool PlaysSoundItself(string playbackKey) => soundPlayers.ContainsKey(playbackKey);

    private void LookAtVideoClocks()
    {
        foreach (var key in soundPlayers.Keys.ToList()) LookAtVideoClock(key);
    }

    private void LookAtVideoClock(string playbackKey)
    {
        if (sound is null || !soundPlayers.TryGetValue(playbackKey, out var player)) return;
        if (sound.HasNoSound(playbackKey))
        {
            GiveSoundBack(playbackKey, player);
            return;
        }
        double position, rate;
        try
        {
            position = player.MediaPlayer.Position.TotalSeconds;
            rate = player.MediaPlayer.SpeedRatio;
        }
        catch (InvalidOperationException)
        {
            return;
        }
        // Heard only while it plays and something shows it (a video let go fades out with its picture).
        var playing = player.Playing && player.Opened && !player.Failed && player.References > 0;
        sound.Follow(playbackKey, new VideoClockSample(position, ShowState.HostClock(), playing, rate));
    }

    // Our engine can't read this file's sound: let the video player have it, on the default device.
    private void GiveSoundBack(string playbackKey, Player player)
    {
        ReleaseSound(playbackKey);
        player.MediaPlayer.IsMuted = false;
        player.MediaPlayer.Volume = VideoVolume;
    }
}
