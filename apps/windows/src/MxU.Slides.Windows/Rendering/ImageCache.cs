using System.Windows.Media.Imaging;

namespace MxU.Slides.Windows.Rendering;

/// <summary>
/// Decoded pictures kept for reuse, up to a memory budget: when a new one would go over it, the
/// pictures used longest ago are let go (they decode again if they come back). A big library's
/// grid used to keep every tile's picture for the whole session; MxU's grid tiles now age out of
/// their caches the same way (ThumbnailStore.swift: every image costs its bytes, 384 MB in all).
/// UI thread only, like the hub that owns it.
/// </summary>
public sealed class ImageCache(long budgetBytes)
{
    public const long DefaultBudget = 384L * 1024 * 1024;

    private readonly Dictionary<string, LinkedListNode<(string Key, BitmapSource Image, long Cost)>> entries = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, BitmapSource Image, long Cost)> recency = new();

    public long Budget { get; } = budgetBytes;
    public long Used { get; private set; }
    public int Count => entries.Count;
    public IEnumerable<string> Keys => entries.Keys;

    /// <summary>What a picture holds in memory: four bytes a pixel.</summary>
    public static long CostOf(BitmapSource image) => (long)image.PixelWidth * image.PixelHeight * 4;

    public bool TryGetValue(string key, out BitmapSource image)
    {
        if (entries.TryGetValue(key, out var node))
        {
            recency.Remove(node);
            recency.AddFirst(node);
            image = node.Value.Image;
            return true;
        }
        image = null!;
        return false;
    }

    public BitmapSource this[string key]
    {
        set => Add(key, value, CostOf(value));
    }

    public void Add(string key, BitmapSource image, long cost)
    {
        Remove(key);
        var node = recency.AddFirst((key, image, cost));
        entries[key] = node;
        Used += cost;
        // The newest picture always stays, even one bigger than the whole budget.
        while (Used > Budget && recency.Last is { } oldest && oldest != node) Remove(oldest.Value.Key);
    }

    public bool Remove(string key)
    {
        if (!entries.Remove(key, out var node)) return false;
        recency.Remove(node);
        Used -= node.Value.Cost;
        return true;
    }
}
