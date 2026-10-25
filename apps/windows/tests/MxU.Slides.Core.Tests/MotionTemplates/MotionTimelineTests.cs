using MxU.Slides.Core.MotionTemplates;

namespace MxU.Slides.Core.Tests.MotionTemplates;

// Which frame a template shows (Phase 11): In, Hold and Out from the markers; loop, hold or
// once; Take Down jumping to the Out. All of it is worked out from two times, so it's tested
// without a clock. The sample is 30 fps: In 0–20, Hold 20–120, Out 120–150.
public sealed class MotionTimelineTests
{
    private static MotionTimeline Strap(MotionPlayback playback = MotionPlayback.Loop) =>
        MotionTimeline.For(LottieDocument.Parse(LottieSamples.Strap), playback);

    /// <summary>An animation of <paramref name="frames"/> frames at 30 fps with the markers given (name, start, length).</summary>
    private static MotionTimeline Made(double frames, MotionPlayback playback, params (string Name, double At, double Length)[] markers)
    {
        var builder = new LottieBuilder("Test", 1920, 1080, 30, frames);
        foreach (var (name, at, length) in markers) builder.Marker(name, at, length);
        builder.Layer(builder.ShapeLayer("Box", LottieBuilder.Transform(LottieBuilder.Static(0, 0, 0)), LottieBuilder.Group("Box", LottieBuilder.Rect(0, 0, 10, 10))));
        return MotionTimeline.For(builder.Document(), playback);
    }

    [Fact]
    public void TheMarkersMakeTheParts()
    {
        var s = Strap().Segments;
        Assert.Equal(new MotionSegments(0, 20, 20, 120, 120, 150, HasMarkers: true, HasOut: true), s);
        Assert.True(s.HoldMoves);
    }

    [Fact]
    public void LottieFilesNamesWorkToo()
    {
        var s = Made(100, MotionPlayback.Loop, ("intro", 0, 10), ("loop", 10, 60), ("outro", 70, 30)).Segments;
        Assert.Equal((0.0, 10.0, 10.0, 70.0, 70.0, 100.0), (s.InStart, s.InEnd, s.HoldStart, s.HoldEnd, s.OutStart, s.End));
    }

    [Fact]
    public void MarkersWithoutLengthsRunToTheNextOne()
    {
        // Just "In" and "Out" points, as some designers mark them: the In runs up to the Out, which runs to the end, and it holds on the In's last frame.
        var s = Made(90, MotionPlayback.Loop, ("In", 0, 0), ("Out", 60, 0)).Segments;
        Assert.Equal((0.0, 60.0, 60.0, 60.0, 60.0, 90.0, true), (s.InStart, s.InEnd, s.HoldStart, s.HoldEnd, s.OutStart, s.End, s.HasOut));
        Assert.False(s.HoldMoves);
    }

    [Fact]
    public void ItAnimatesInThenLoopsItsHold()
    {
        var t = Strap();
        Assert.Equal((0.0, false), t.FrameAt(0));
        Assert.Equal((15.0, false), t.FrameAt(0.5));
        Assert.Equal((20.0, false), t.FrameAt(20 / 30.0));
        Assert.Equal((80.0, false), t.FrameAt(80 / 30.0));
        // 130 frames in: 110 into the 100-frame hold, so 10 into its second pass.
        Assert.Equal(30.0, t.FrameAt(130 / 30.0).Frame, 6);
        Assert.False(t.FrameAt(3600).Finished);
    }

    [Fact]
    public void HoldLastStopsAtTheEndOfItsHold()
    {
        var t = Strap(MotionPlayback.HoldLast);
        Assert.Equal(119.0, t.FrameAt(10).Frame);
        Assert.Equal(119.0, t.FrameAt(3600).Frame);
    }

    [Fact]
    public void OncePlaysInHoldAndOutThenFinishes()
    {
        var t = Strap(MotionPlayback.Once);
        Assert.Equal(5.0, t.OnceSeconds);
        Assert.Equal((100.0, false), t.FrameAt(100 / 30.0));
        Assert.Equal(135.0, t.FrameAt(135 / 30.0).Frame, 6);
        Assert.Equal((149.0, true), t.FrameAt(5.0));
    }

    [Fact]
    public void TakingItDownJumpsToTheOutAndFinishesWhenItsOver()
    {
        var t = Strap();
        Assert.Equal(1.0, t.TakeDownSeconds);
        Assert.Equal((120.0, false), t.FrameAt(60, secondsDown: 0));
        Assert.Equal(135.0, t.FrameAt(60.5, secondsDown: 0.5).Frame, 6);
        Assert.Equal((149.0, true), t.FrameAt(61, secondsDown: 1));
        // Even part-way through its In, a take-down goes straight to the Out.
        Assert.Equal((120.0, false), t.FrameAt(0.2, secondsDown: 0));
    }

    [Fact]
    public void WithoutMarkersItIsOnePiece()
    {
        var loop = Made(90, MotionPlayback.Loop);
        Assert.False(loop.Segments.HasMarkers);
        Assert.Equal(30.0, loop.FrameAt(4).Frame, 6); // 120 frames in, a 90-frame loop
        var hold = Made(90, MotionPlayback.HoldLast);
        Assert.Equal(89.0, hold.FrameAt(10).Frame);
        var once = Made(90, MotionPlayback.Once);
        Assert.Equal(3.0, once.OnceSeconds);
        Assert.Equal((89.0, true), once.FrameAt(3));
        // Taken down, it keeps moving while it fades, and is gone after the fade.
        Assert.Equal(MotionTimeline.FadeSeconds, loop.TakeDownSeconds);
        Assert.Equal((31.0, false), loop.FrameAt(31 / 30.0, secondsDown: 0.1));
        Assert.True(loop.FrameAt(2, secondsDown: MotionTimeline.FadeSeconds).Finished);
    }

    [Fact]
    public void TheSettledFrameIsTheStartOfTheHold()
    {
        Assert.Equal(20.0, Strap().SettledFrame);
        Assert.Equal(89.0, Made(90, MotionPlayback.HoldLast).SettledFrame);
        Assert.Equal(45.0, Made(90, MotionPlayback.Loop).SettledFrame);
        Assert.Equal(48.0, MotionTimeline.For(StarterMotionTemplates.NameStrap(), MotionPlayback.Loop).SettledFrame);
    }

    [Fact]
    public void APlayThroughForAVideoIsInThenTheHoldThenOut()
    {
        var frames = Strap().PlayThrough(holdSeconds: 2, frameRate: 30);
        Assert.Equal(20 + 60 + 30, frames.Count);
        Assert.Equal([0.0, 1, 2], frames.Take(3));
        Assert.Equal(20.0, frames[20]);
        Assert.Equal(120.0, frames[80]);
        Assert.Equal(149.0, frames[^1]);
        // At 60 frames a second, each animation frame is shown for two video frames' worth of time (Skottie fills in between).
        var sixty = Strap().PlayThrough(holdSeconds: 0, frameRate: 60);
        Assert.Equal(40 + 60, sixty.Count);
        Assert.Equal(0.5, sixty[1]);
        // Without markers: the whole animation, as many times as asked.
        Assert.Equal(180, Made(90, MotionPlayback.Loop).PlayThrough(0, 30, passes: 2).Count);
    }

    [Fact]
    public void TheTitleCardLoopsSeamlessly()
    {
        // Every keyframed position in it ends where it starts, so frame 240 is frame 0.
        var card = StarterMotionTemplates.TitleCard();
        foreach (var layer in card.Root["layers"]!.AsArray())
        {
            if (layer!["ks"]!["p"]!["a"]!.GetValue<int>() != 1) continue;
            var keys = layer["ks"]!["p"]!["k"]!.AsArray();
            Assert.Equal(keys[0]!["s"]!.ToJsonString(), keys[^1]!["s"]!.ToJsonString());
            Assert.Equal(240, keys[^1]!["t"]!.GetValue<double>());
        }
        Assert.False(MotionTimeline.SegmentsOf(card).HasMarkers);
    }
}
