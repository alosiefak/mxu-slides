using System.Windows.Media;
using System.Windows.Media.Imaging;
using MxU.Slides.Windows.Rendering;

namespace MxU.Slides.Windows.Tests.Rendering;

// The decoded-picture cache ages its oldest pictures out past its budget (MxU's grid tiles
// ageing out of the thumbnail caches).
public class ImageCacheTests
{
    private static BitmapSource Picture(int side)
    {
        var bitmap = BitmapSource.Create(side, side, 96, 96, PixelFormats.Bgra32, null, new byte[side * side * 4], side * 4);
        bitmap.Freeze();
        return bitmap;
    }

    [Fact]
    public void TheLeastRecentlyUsedPictureGoesFirst()
    {
        var cache = new ImageCache(budgetBytes: 3 * 10 * 10 * 4);
        cache["a"] = Picture(10);
        cache["b"] = Picture(10);
        cache["c"] = Picture(10);
        Assert.True(cache.TryGetValue("a", out _)); // "a" is used again, so "b" is now the oldest
        cache["d"] = Picture(10);
        Assert.False(cache.TryGetValue("b", out _));
        Assert.True(cache.TryGetValue("a", out _));
        Assert.Equal(3, cache.Count);
        Assert.Equal(3 * 400, cache.Used);
    }

    [Fact]
    public void ANewPictureBiggerThanTheBudgetStillStays()
    {
        var cache = new ImageCache(budgetBytes: 100);
        cache["small"] = Picture(2);
        cache["big"] = Picture(20);
        Assert.True(cache.TryGetValue("big", out _));
        Assert.False(cache.TryGetValue("small", out _));
        Assert.True(cache.Remove("big"));
        Assert.Equal(0, cache.Used);
    }
}
