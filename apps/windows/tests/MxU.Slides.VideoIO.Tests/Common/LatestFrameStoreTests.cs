using MxU.Slides.VideoIO.Common;

namespace MxU.Slides.VideoIO.Tests.Common;

/// <summary>Windows-only: the latest-frame ring every input shares.</summary>
public sealed class LatestFrameStoreTests
{
    private static void Publish(LatestFrameStore store, byte fill, double time)
    {
        var buffer = store.Rent(16);
        Array.Fill(buffer, fill, 0, 16);
        store.Publish(2, 2, 8, time);
    }

    [Fact]
    public void ANewFrameNeverOverwritesTheNewestOne()
    {
        var store = new LatestFrameStore();
        Publish(store, 1, 0);
        var newest = store.Latest;
        Assert.NotNull(newest);
        var next = store.Rent(16);
        Array.Fill(next, (byte)2);
        Assert.Equal(1, newest.Pixels.Span[0]);
    }

    [Fact]
    public void AFrameSurvivesUntilTheRingComesRound()
    {
        var store = new LatestFrameStore();
        Publish(store, 1, 0);
        var held = store.Latest;
        Assert.NotNull(held);
        for (byte fill = 2; fill < LatestFrameStore.Depth + 1; fill++) Publish(store, fill, fill);
        Assert.Equal(1, held.Pixels.Span[0]); // three newer frames went into the other buffers
        Assert.Equal(LatestFrameStore.Depth, store.Count);
    }

    [Fact]
    public void CopyLatestHonoursTheDestinationStrideAndRefusesWhatDoesNotFit()
    {
        var store = new LatestFrameStore();
        Assert.False(store.CopyLatest(new byte[64], 16, out _, out _));
        Publish(store, 9, 0);
        var destination = new byte[32];
        Assert.True(store.CopyLatest(destination, 16, out var width, out var height));
        Assert.Equal((2, 2), (width, height));
        Assert.Equal(9, destination[0]);
        Assert.Equal(0, destination[8]); // padding between rows untouched
        Assert.Equal(9, destination[16]);
        Assert.False(store.CopyLatest(new byte[8], 8, out _, out _));
    }

    [Fact]
    public void FrameAgeAndOpaqueCopies()
    {
        var store = new LatestFrameStore();
        Assert.Null(store.FrameAge(5));
        var source = new byte[] { 10, 20, 30, 0, 40, 50, 60, 7 };
        var frame = store.PublishCopy(source, 8, 2, 1, forceOpaque: true, hostTime: 3);
        Assert.Equal(new byte[] { 10, 20, 30, 255, 40, 50, 60, 255 }, frame.Pixels.ToArray());
        Assert.Equal(2, store.FrameAge(5));
        store.Clear();
        Assert.Null(store.Latest);
    }

    [Fact]
    public void NearestScalingPicksTheNearestSourcePixel()
    {
        // 2×1 → 4×2: each source pixel becomes a 2×2 block.
        var source = new byte[] { 1, 1, 1, 1, 2, 2, 2, 2 };
        var destination = new byte[4 * 2 * 4];
        PixelCopy.ScaleNearest(source, 2, 1, 8, destination, 4, 2, 16);
        Assert.Equal(new byte[] { 1, 1, 2, 2, 1, 1, 2, 2 }, destination.Where((_, i) => i % 4 == 0).ToArray());
    }
}
