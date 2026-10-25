using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MxU.Slides.Core.MotionTemplates;
using MxU.Slides.Core.Scene;
using MxU.Slides.MotionTemplates.Lottie;

namespace MxU.Slides.Windows.Rendering;

/// <summary>
/// Motion templates playing live (Phase 11), drawn like live inputs: each one is a picture id
/// (<c>motion::…</c>) whose <see cref="WriteableBitmap"/> is updated in place on every screen
/// refresh. Every view showing it draws that one bitmap, so the projector, the preview and the
/// recording all show the same frame. The pixels come from <see cref="Motions"/>, which draws
/// them on a thread of its own; here they're only copied, and only the part that changed.
/// <para>
/// A full-screen template is a video in the library (what a Mac plays). While its animation
/// plays live here, that video's id <see cref="StandIn">stands in</see> for the animation: the
/// views draw the live picture where they would have played the video.
/// </para>
/// </summary>
public sealed partial class MediaHub
{
    private sealed class MotionPicture
    {
        public WriteableBitmap? Bitmap;
        public int Version;
        public int Copied;
        public DateTime LastAsked = DateTime.UtcNow;
    }

    private readonly Dictionary<string, MotionPicture> motionPictures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> motionStandIns = new(StringComparer.Ordinal);
    private bool motionHooked;

    /// <summary>The live animations (set by the app).</summary>
    public MotionPlayers? Motions { get; set; }

    public static bool IsMotion(string mediaId) => mediaId.StartsWith(MotionTemplateStore.PicturePrefix, StringComparison.Ordinal);

    /// <summary>The live animation a library item plays as instead of itself right now (a full-screen template's video), or null.</summary>
    public string? MotionStandIn(string mediaId) => motionStandIns.GetValueOrDefault(mediaId);

    /// <summary>
    /// Has <paramref name="mediaId"/> drawn as the live animation <paramref name="pictureId"/> (null:
    /// as itself again). Views showing the item redraw at once.
    /// </summary>
    public void StandIn(string mediaId, string? pictureId)
    {
        if (pictureId is null)
        {
            if (!motionStandIns.Remove(mediaId)) return;
        }
        else
        {
            if (motionStandIns.TryGetValue(mediaId, out var current) && current == pictureId) return;
            motionStandIns[mediaId] = pictureId;
            MotionPictureFor(pictureId);
        }
        MediaReady?.Invoke(mediaId);
    }

    /// <summary>The picture of a live animation, updated in place; null until its first frame (MediaReady is raised then).</summary>
    public BitmapSource? MotionPictureFor(string pictureId)
    {
        if (!motionPictures.TryGetValue(pictureId, out var picture)) motionPictures[pictureId] = picture = new MotionPicture();
        picture.LastAsked = DateTime.UtcNow;
        if (!motionHooked)
        {
            CompositionTarget.Rendering += OnMotionFrame;
            motionHooked = true;
        }
        return picture.Bitmap;
    }

    /// <summary>How many new pictures of an animation have been copied for the screens (the self-test's speed report).</summary>
    public int MotionFramesShown(string pictureId) => motionPictures.TryGetValue(pictureId, out var p) ? p.Copied : 0;

    /// <summary>The time spent copying animations' pictures for the screens, in all (the self-test's speed report).</summary>
    public TimeSpan MotionCopyTime { get; private set; }

    private DateTime lastRendering;

    private void OnMotionFrame(object? sender, EventArgs e)
    {
        var players = Motions;
        var now = DateTime.UtcNow;
        // Ask for the frames of the picture after this one: about one screen refresh from now.
        var refresh = lastRendering == default ? 1 / 60.0 : Math.Clamp((now - lastRendering).TotalSeconds, 1 / 240.0, 1 / 20.0);
        lastRendering = now;
        players?.Request(ShowState.HostClock() + refresh);

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        List<string>? gone = null;
        foreach (var (id, picture) in motionPictures)
        {
            if (players?[id] is null)
            {
                // Stopped (taken down, cleared): let its picture go once nothing has asked for it for a moment.
                if (now - picture.LastAsked > TimeSpan.FromSeconds(2)) (gone ??= []).Add(id);
                continue;
            }
            var fresh = false;
            var copied = players.CopyLatest(id, ref picture.Version, (frame, changed) =>
            {
                if (picture.Bitmap is null || picture.Bitmap.PixelWidth != frame.Width || picture.Bitmap.PixelHeight != frame.Height)
                {
                    picture.Bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Pbgra32, null);
                    changed = new PixelRect(0, 0, frame.Width, frame.Height);
                    fresh = true;
                }
                if (changed.IsEmpty) return;
                // Only the part that changed: WPF then only sends that part to the graphics card too.
                picture.Bitmap.WritePixels(new Int32Rect(changed.X, changed.Y, changed.Width, changed.Height), frame.Pixels, frame.Stride,
                    changed.Y * frame.Stride + changed.X * 4);
            });
            if (copied) picture.Copied++;
            if (fresh)
            {
                MediaReady?.Invoke(id);
                foreach (var owner in motionStandIns.Where(s => s.Value == id).Select(s => s.Key).ToList()) MediaReady?.Invoke(owner);
            }
        }
        MotionCopyTime += System.Diagnostics.Stopwatch.GetElapsedTime(started);
        foreach (var id in gone ?? []) motionPictures.Remove(id);
        if (motionPictures.Count == 0 && (players is null || players.Playing.Count == 0))
        {
            CompositionTarget.Rendering -= OnMotionFrame;
            motionHooked = false;
            lastRendering = default;
        }
    }
}
