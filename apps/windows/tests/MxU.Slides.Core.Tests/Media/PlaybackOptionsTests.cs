using MxU.Slides.Core.Media;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Media;

// Ported from MediaEngine/Tests/PlaybackOptionsTests.swift.
public class PlaybackOptionsTests
{
    private const double Duration = 10;
    private const double Frame = 1.0 / 30;

    [Fact]
    public void ResolvedClampsAndFallsBack()
    {
        Assert.Equal((0.0, Duration, 1.0), new PlaybackOptions().Resolved(Duration, Frame));
        var trimmed = new PlaybackOptions(2, 8).Resolved(Duration, Frame);
        Assert.Equal((2.0, 8.0), (trimmed.Start, trimmed.End));
        var outside = new PlaybackOptions(-3, 99).Resolved(Duration, Frame);
        Assert.Equal((0.0, Duration), (outside.Start, outside.End));
        var backwards = new PlaybackOptions(8, 2).Resolved(Duration, Frame);
        Assert.Equal((0.0, Duration), (backwards.Start, backwards.End));
        var sliver = new PlaybackOptions(5, 5.01).Resolved(Duration, Frame);
        Assert.Equal((0.0, Duration), (sliver.Start, sliver.End));
        Assert.Equal(PlaybackOptions.MaximumRate, new PlaybackOptions(Rate: 100).Resolved(Duration, Frame).Rate);
        Assert.Equal(PlaybackOptions.MinimumRate, new PlaybackOptions(Rate: 0).Resolved(Duration, Frame).Rate);
    }

    [Fact]
    public void EffectiveWallClockDuration() =>
        Assert.Equal(3, new PlaybackOptions(2, 8, 2).EffectiveWallClockDuration(Duration, Frame), 0.001);

    [Fact]
    public void AMediaItemsTrimAndSpeedBecomeItsOptions()
    {
        var item = new MediaItem { Id = "m", Name = "Walk-in", InPoint = 1.5, OutPoint = 9, PlayRate = 0.5 };
        Assert.Equal(new PlaybackOptions(1.5, 9, 0.5), PlaybackOptions.Of(item));
        Assert.Null(PlaybackOptions.Of(new MediaItem { Id = "plain", Name = "Plain" })); // plays whole, at normal speed
    }

    [Fact]
    public void APassEndsAtTheOutPointHalfAFrameEarly()
    {
        Assert.False(PlaybackOptions.PassIsOver(7.9, 8, Frame));
        Assert.True(PlaybackOptions.PassIsOver(8 - Frame / 2, 8, Frame));
        Assert.True(PlaybackOptions.PassIsOver(8.2, 8, Frame));
    }
}
