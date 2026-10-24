using MxU.Slides.Core.Media;
using MxU.Slides.VideoIO.Capture;

namespace MxU.Slides.VideoIO.Tests.Capture;

/// <summary>Port of MediaEngineTests/LiveInputDelayTests.swift, plus Windows checks of buffer reuse.</summary>
public sealed class LiveInputDelayTests
{
    private static VideoFrame MakeFrame(byte fill) => TestSupport.Solid(4, 4, fill, fill, fill, fill);

    private static byte FirstByte(VideoFrame frame) => frame.Pixels.Span[0];

    [Fact]
    public void ZeroDelayPassesTheFrameThrough()
    {
        var delay = new LiveInputDelay();
        var frame = MakeFrame(7);
        Assert.Same(frame, delay.Delayed(frame, "cam"));
    }

    [Fact]
    public void DelayedPullAnswersTheOldestHeldFrame()
    {
        var delay = new LiveInputDelay();
        delay.SetDelay("cam", 2);
        Assert.Equal(10, FirstByte(delay.Delayed(MakeFrame(10), "cam")));
        Assert.Equal(10, FirstByte(delay.Delayed(MakeFrame(20), "cam")));
        Assert.Equal(10, FirstByte(delay.Delayed(MakeFrame(30), "cam")));
        Assert.Equal(20, FirstByte(delay.Delayed(MakeFrame(40), "cam")));
    }

    [Fact]
    public void SameInstanceRePullDoesNotAdvanceTheRing()
    {
        var delay = new LiveInputDelay();
        delay.SetDelay("cam", 1);
        var first = MakeFrame(10);
        _ = delay.Delayed(first, "cam");
        _ = delay.Delayed(first, "cam");
        _ = delay.Delayed(first, "cam");
        Assert.Equal(10, FirstByte(delay.Delayed(MakeFrame(20), "cam"))); // re-pulls of one source frame count once
    }

    [Fact]
    public void HeldFramesAreCopiesNotTheSourceBuffers()
    {
        var delay = new LiveInputDelay();
        delay.SetDelay("cam", 1);
        var first = MakeFrame(10);
        var held = delay.Delayed(first, "cam");
        Assert.NotSame(first, held);
        Assert.Equal(10, FirstByte(held));
    }

    [Fact]
    public void ClearDropsHeldFramesButKeepsTheSetting()
    {
        var delay = new LiveInputDelay();
        delay.SetDelay("cam", 2);
        _ = delay.Delayed(MakeFrame(10), "cam");
        delay.Clear("cam");
        Assert.Equal(2, delay.Delay("cam"));
        Assert.Equal(30, FirstByte(delay.Delayed(MakeFrame(30), "cam"))); // a restarted input refills from its first frame
    }

    [Fact]
    public void SetDelayClampsToTheCeiling()
    {
        var delay = new LiveInputDelay();
        delay.SetDelay("cam", 999);
        Assert.Equal(LiveInputDelay.MaxFrames, delay.Delay("cam"));
        delay.SetDelay("cam", -1);
        Assert.Equal(0, delay.Delay("cam"));
    }

    [Fact]
    public void AReturnedFrameIsNotOverwrittenByTheNextPull()
    {
        var delay = new LiveInputDelay();
        delay.SetDelay("cam", 1);
        VideoFrame? previous = null;
        for (byte fill = 1; fill < 40; fill++)
        {
            var shown = delay.Delayed(MakeFrame(fill), "cam");
            if (previous is not null) Assert.Equal(Math.Max(1, fill - 2), FirstByte(previous)); // still what it was when returned
            Assert.Equal(Math.Max(1, fill - 1), FirstByte(shown));
            previous = shown;
        }
    }

    [Fact]
    public void ASizeChangeStartsAFreshPool()
    {
        var delay = new LiveInputDelay();
        delay.SetDelay("cam", 1);
        _ = delay.Delayed(MakeFrame(1), "cam");
        _ = delay.Delayed(MakeFrame(2), "cam");
        var big = TestSupport.Solid(8, 8, 9, 9, 9);
        _ = delay.Delayed(big, "cam");
        var shown = delay.Delayed(TestSupport.Solid(8, 8, 5, 5, 5), "cam");
        Assert.Equal((8, 8), (shown.Width, shown.Height));
        Assert.Equal(9, FirstByte(shown));
    }

    [Fact]
    public void InputsAreDelayedIndependently()
    {
        var delay = new LiveInputDelay();
        delay.SetDelay("a", 1);
        var b = MakeFrame(50);
        Assert.Same(b, delay.Delayed(b, "b"));
        Assert.Equal(1, FirstByte(delay.Delayed(MakeFrame(1), "a")));
    }
}
