using System.Windows.Media;
using MxU.Slides.Audio.Video;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Windows.Rendering;

/// <summary>
/// A video that's on screen when its file is replaced (Replace everywhere, Locate, a transcode,
/// an edit made in the Blobs folder) plays the new file at once, from where it was, on every
/// output. The Mac reloads live media when its file changes (ServiceControls.reloadLiveMedia);
/// until Phase 8 a replaced video here kept playing the old file until it was fired again.
/// Sound our own engine plays (MediaHub.Sound.cs) moves to the new file too.
/// </summary>
public sealed partial class MediaHub
{
    /// <summary>Reopens every player showing <paramref name="mediaId"/> on the item's current file, at the same point. Returns how many.</summary>
    public int ReloadVideosInPlace(string mediaId)
    {
        if (resolve(mediaId) is not { Kind: MediaKind.Video } media) return 0;
        var reopened = 0;
        foreach (var (key, player) in players.Where(p => p.Value.MediaId == mediaId && !p.Value.Failed).ToList())
        {
            var mp = player.MediaPlayer;
            if (mp.Source?.LocalPath is { } current && string.Equals(Path.GetFullPath(current), Path.GetFullPath(media.Path), StringComparison.OrdinalIgnoreCase)) continue;
            var at = mp.Position;
            var playing = player.Playing;
            if (soundPlayers.ContainsKey(key) && sound is not null)
            {
                // The sound follows the picture to the new file, over the same trim and speed.
                var trim = TrimOf?.Invoke(mediaId);
                sound.Detach(key);
                sound.Attach(key, media.Path, new VideoSoundOptions(trim?.InPoint, trim?.OutPoint,
                    Math.Clamp(trim?.Rate ?? 1, Core.Media.PlaybackOptions.MinimumRate, Core.Media.PlaybackOptions.MaximumRate)));
            }
            void Resume(object? sender, EventArgs e)
            {
                mp.MediaOpened -= Resume;
                // Opening ran the usual start of a pass (to the in point: MediaHub.Trim.cs); go back to where it was.
                var length = mp.NaturalDuration.HasTimeSpan ? mp.NaturalDuration.TimeSpan : TimeSpan.MaxValue;
                mp.Position = at < length ? at : TimeSpan.Zero;
                SoundJumped(key, mp.Position);
            }
            player.Opened = false;
            mp.MediaOpened += Resume;
            mp.Open(new Uri(media.Path));
            if (playing) mp.Play();
            else mp.Pause();
            reopened++;
            Infrastructure.Log.Info($"Reloaded {key} in place on the new file of {media.Name}");
        }
        return reopened;
    }
}
