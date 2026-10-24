using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MxU.Slides.Core.Media;

namespace MxU.Slides.Windows.Rendering;

/// <summary>
/// Live inputs (cameras, capture cards, NDI sources): each one's newest frame is copied into
/// a <see cref="WriteableBitmap"/> on every screen refresh. Every view that shows the input
/// draws that one bitmap, so WPF redraws them all when it changes and they stay in step.
/// Cue ids start with <c>input::</c>, as on the Mac.
/// </summary>
public sealed partial class MediaHub
{
    private sealed class LiveInput
    {
        public WriteableBitmap? Bitmap;
        public double LastFrame = double.NaN;
        public DateTime LastAsked = DateTime.UtcNow;
    }

    private readonly Dictionary<string, LiveInput> liveInputs = new(StringComparer.Ordinal);
    private bool liveHooked;

    /// <summary>The newest frame of an input by cue id (set by the app from its video input inventory).</summary>
    public Func<string, VideoFrame?>? LiveFrames { get; set; }

    /// <summary>The input's display name, for the "waiting for…" placeholder.</summary>
    public Func<string, string?>? LiveInputName { get; set; }

    public static bool IsLiveInput(string mediaId) => mediaId.StartsWith("input::", StringComparison.Ordinal);

    /// <summary>
    /// The picture for a live input, updated in place as frames arrive; null until its first frame
    /// (MediaReady is raised then, so views redraw with it).
    /// </summary>
    public BitmapSource? LiveInputPicture(string mediaId)
    {
        if (!liveInputs.TryGetValue(mediaId, out var input)) liveInputs[mediaId] = input = new LiveInput();
        input.LastAsked = DateTime.UtcNow;
        if (!liveHooked)
        {
            CompositionTarget.Rendering += OnLiveFrame;
            liveHooked = true;
        }
        return input.Bitmap;
    }

    private void OnLiveFrame(object? sender, EventArgs e)
    {
        var stale = new List<string>();
        foreach (var (id, input) in liveInputs)
        {
            // Inputs nothing has asked for in a while stop being copied.
            if (DateTime.UtcNow - input.LastAsked > TimeSpan.FromSeconds(10)) stale.Add(id);
            if (LiveFrames?.Invoke(id) is not { } frame || frame.HostTime == input.LastFrame || frame.Width <= 0 || frame.Height <= 0) continue;
            input.LastFrame = frame.HostTime;
            var fresh = input.Bitmap is null || input.Bitmap.PixelWidth != frame.Width || input.Bitmap.PixelHeight != frame.Height;
            if (fresh) input.Bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Pbgra32, null);
            if (MemoryMarshal.TryGetArray(frame.Pixels, out var segment) && segment.Array is { } array)
                input.Bitmap!.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), array, frame.Stride, segment.Offset);
            else
                input.Bitmap!.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels.ToArray(), frame.Stride, 0);
            if (fresh) MediaReady?.Invoke(id);
        }
        foreach (var id in stale) liveInputs.Remove(id);
        if (liveInputs.Count == 0)
        {
            CompositionTarget.Rendering -= OnLiveFrame;
            liveHooked = false;
        }
    }
}
