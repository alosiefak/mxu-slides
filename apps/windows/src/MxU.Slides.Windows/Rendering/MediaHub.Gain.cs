namespace MxU.Slides.Windows.Rendering;

/// <summary>
/// A video's own volume: a media item can play quieter than the rest (a loud walk-in video), set in
/// the media editor and kept as a Windows extra (Core/Library/MediaLookExtras.cs). It multiplies the
/// video sound's level (Settings › Sound), wherever the sound plays: our own engine, or the video
/// player when the engine can't read the file. Windows-only: the Mac has no per-item volume.
/// </summary>
public sealed partial class MediaHub
{
    /// <summary>A media item's own level, 0…1, by media id (set by the app from the library); null plays everything at 1.</summary>
    public Func<string, double>? GainOf { get; set; }

    private double Gain(string mediaId) => GainOf?.Invoke(mediaId) is { } gain && double.IsFinite(gain) ? Math.Clamp(gain, 0, 1) : 1;

    /// <summary>
    /// One player's speed, now and after every Play (the media editor's preview, whose players play
    /// the whole file and take the speed as it's changed).
    /// </summary>
    public void SetRate(string playbackKey, double rate)
    {
        if (!players.TryGetValue(playbackKey, out var player)) return;
        PassOf(player).Rate = Math.Clamp(rate, Core.Media.PlaybackOptions.MinimumRate, Core.Media.PlaybackOptions.MaximumRate);
        ApplyRate(player);
    }

    /// <summary>Applies the items' levels again to every playing video (a volume changed while it plays).</summary>
    public void RefreshGains()
    {
        foreach (var (key, player) in players)
        {
            var gain = Gain(player.MediaId);
            if (soundPlayers.ContainsKey(key)) sound?.SetGain(key, gain);
            else if (!player.MediaPlayer.IsMuted) player.MediaPlayer.Volume = VideoVolume * gain;
        }
    }
}
