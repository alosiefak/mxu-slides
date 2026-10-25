using MxU.Slides.Core.Model;

namespace MxU.Slides.Windows.Rendering;

/// <summary>
/// Swapping the file under a video that's playing. Normally a replaced video keeps playing its
/// old file until it's fired again (Phase 5's Replace everywhere works that way, see
/// docs/phase5/library.md). A re-rendered motion background is different: you asked for its new
/// look on the screens, so its players reopen on the new file straight away, from the start of
/// the loop.
/// </summary>
public sealed partial class MediaHub
{
    /// <summary>
    /// Reopens every player showing <paramref name="mediaId"/> on the item's current file. Only
    /// silent players: one whose sound runs through the app's engine would leave that sound on
    /// the old file, so it keeps the old file until fired again. Returns how many were reopened.
    /// </summary>
    public int ReloadSilentVideos(string mediaId)
    {
        if (resolve(mediaId) is not { Kind: MediaKind.Video } media) return 0;
        var reopened = 0;
        foreach (var (key, player) in players.Where(p => p.Value.MediaId == mediaId && !p.Value.Failed && !soundPlayers.ContainsKey(p.Key)).ToList())
        {
            var mp = player.MediaPlayer;
            player.Opened = false;
            // Opening raises MediaOpened again, which starts the pass from the in point (MediaHub.Trim.cs).
            mp.Open(new Uri(media.Path));
            if (player.Playing) mp.Play();
            reopened++;
            Infrastructure.Log.Info($"Reopened {key} on the replaced file of {media.Name}");
        }
        return reopened;
    }
}
