using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MxU.Slides.Core.Animation;
using MxU.Slides.Windows.Infrastructure;

namespace MxU.Slides.Windows.Rendering;

/// <summary>
/// The trails of every Echo and Ghost Trails effect on screen. Each frame, the first view to
/// draw an item takes a small picture of it (<see cref="RenderTargetBitmap"/>, at most
/// <see cref="MaxSide"/> pixels across), advances its <see cref="EffectTrail"/>, and puts the
/// trail in a bitmap. Other views showing the same item in the same frame (the projector, its
/// preview, the program feed) reuse that bitmap, so they show the same trail and the work is
/// done once. Trails nobody has drawn for a few seconds are dropped.
/// <para>
/// Everything here runs on the UI thread, where WPF draws. The picture is small on purpose:
/// trails are soft anyway, and a full-size copy every frame would slow the operator screen
/// (see HOW-IT-WORKS, the program feed). The live item itself is still drawn at full size.
/// </para>
/// </summary>
internal static class TrailStore
{
    /// <summary>The trail picture's longest side, in pixels.</summary>
    public const int MaxSide = 384;

    // WPF frames seen so far. Views draw one after another within a frame, several milliseconds
    // apart, so "the same frame" is counted, not timed: the first view to draw an item in a frame
    // advances its trail, and the rest reuse it. (Timing it advanced the trail once per view, which
    // blew the ghost's smoke away three times too fast on a quick PC.)
    private static long frame;
    private static bool counting;

    private static void CountFrames()
    {
        if (counting) return;
        counting = true;
        CompositionTarget.Rendering += (_, _) => frame++;
    }

    private const double Forget = 5;

    private sealed class Entry
    {
        public readonly EffectTrail History = new();
        public readonly DrawingVisual Snapshot = new();
        public readonly VisualBrush Source = new() { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        public RenderTargetBitmap? Fresh;
        public WriteableBitmap? Bitmap;
        public ImageBrush? Brush;
        public uint[] FreshPixels = [];
        public uint[] TrailPixels = [];
        public long UpdatedInFrame = -1;
        public double LastUsed;
        public Trail Result = Trail.None;
    }

    /// <summary>What to combine with the live picture this frame: the trail brush, how much of it is kept, whether there is one yet.</summary>
    public readonly record struct Trail(Brush? Brush, double Keep, bool Has)
    {
        public static readonly Trail None = new(null, 0, false);
    }

    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal);
    private static double lastSweep;

    /// <summary>
    /// The trail for one effect pass of one item, advanced to <paramref name="now"/> with a picture of
    /// <paramref name="source"/> (the item's drawing, below the pass) inside <paramref name="region"/>
    /// (canvas units). A fault is logged and the item shows without its trail; it never stops the frame.
    /// </summary>
    public static Trail Update(string key, Visual source, Rect region, AnimationEffect effect, double now)
    {
        if (!Entries.TryGetValue(key, out var entry)) Entries[key] = entry = new Entry();
        entry.LastUsed = now;
        CountFrames();
        if (entry.UpdatedInFrame == frame) return entry.Result;
        entry.UpdatedInFrame = frame;
        try
        {
            var scale = Math.Min(1, MaxSide / Math.Max(1, Math.Max(region.Width, region.Height)));
            var width = Math.Max(1, (int)Math.Ceiling(region.Width * scale));
            var height = Math.Max(1, (int)Math.Ceiling(region.Height * scale));
            if (entry.Fresh is null || entry.Fresh.PixelWidth != width || entry.Fresh.PixelHeight != height)
            {
                entry.Fresh = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                entry.Bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);
                entry.Brush = new ImageBrush(entry.Bitmap) { Stretch = Stretch.Fill };
                entry.FreshPixels = new uint[width * height];
                entry.TrailPixels = new uint[width * height];
            }
            // The item's picture, squeezed into the small bitmap.
            entry.Source.Visual = source;
            entry.Source.Viewbox = region;
            using (var dc = entry.Snapshot.RenderOpen())
                dc.DrawRectangle(entry.Source, null, new Rect(0, 0, width, height));
            entry.Fresh.Clear();
            entry.Fresh.Render(entry.Snapshot);
            entry.Fresh.CopyPixels(entry.FreshPixels, width * 4, 0);

            var keep = entry.History.Advance(entry.FreshPixels, width, height, now, effect, scale, entry.TrailPixels);
            entry.Bitmap!.WritePixels(new Int32Rect(0, 0, width, height), entry.TrailPixels, width * 4, 0);
            entry.Result = new Trail(entry.Brush, keep ?? 0, keep is not null);
        }
        catch (Exception ex)
        {
            Log.Warn($"An echo or ghost trail couldn't be drawn; showing the item without it: {ex.Message}");
            entry.History.Reset();
            entry.Result = Trail.None;
        }
        Sweep(now);
        return entry.Result;
    }

    /// <summary>How many trails are kept (for tests and the self-test).</summary>
    public static int Count => Entries.Count;

    private static void Sweep(double now)
    {
        if (now - lastSweep < 1) return;
        lastSweep = now;
        foreach (var stale in Entries.Where(pair => now - pair.Value.LastUsed > Forget).Select(pair => pair.Key).ToList())
            Entries.Remove(stale);
    }
}
