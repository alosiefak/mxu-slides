using MxU.Slides.Core.MotionTemplates;
using MxU.Slides.MotionTemplates.Lottie;

namespace MxU.Slides.MotionTemplates.Tests;

// Playing templates live (Phase 11). These drive the players by hand (no drawing thread, a
// clock of their own), so they test which picture is made when, not how fast.
public sealed class MotionPlayersTests
{
    private static MotionTimeline Timeline(MotionPlayback playback = MotionPlayback.Loop) =>
        MotionTimeline.For(StarterMotionTemplates.NameStrap(), playback);

    private static MotionPlayers Players() => new(LottieRendererTests.ShippedFonts, clock: () => 0, thread: false);

    [Fact]
    public void EachPictureIsTheFrameForItsMoment()
    {
        using var players = Players();
        var motion = players.Start("motion::a", StarterMotionTemplates.NameStrap(), Timeline(), 480, 270, upAt: 100);
        players.DrawNow(100.25);
        Assert.Equal((1, 15.0), (motion.Version, motion.ShownFrame));
        players.DrawNow(100.5);
        Assert.Equal((2, 30.0), (motion.Version, motion.ShownFrame));

        // The screen copies the newest picture once; asking again with the same version gets nothing.
        var version = 0;
        byte[]? copied = null;
        Assert.True(players.CopyLatest("motion::a", ref version, (frame, _) => copied = [.. frame.Pixels]));
        Assert.Equal(2, version);
        Assert.False(players.CopyLatest("motion::a", ref version, (_, _) => { }));
        using var renderer = new LottieRenderer(StarterMotionTemplates.NameStrap(), LottieRendererTests.ShippedFonts);
        using var expected = new LottieFrame(480, 270);
        renderer.Render(30, expected);
        Assert.Equal(expected.Pixels, copied);
    }

    [Fact]
    public void TheScreenCopiesOnlyWhatChanged()
    {
        using var players = Players();
        players.Start("motion::a", StarterMotionTemplates.NameStrap(), Timeline(), 480, 270, upAt: 0);
        var version = 0;
        PixelRect region = default;
        players.DrawNow(0.6);
        players.CopyLatest("motion::a", ref version, (_, r) => region = r);
        Assert.Equal(new PixelRect(0, 0, 480, 270), region); // the first picture: all of it
        players.DrawNow(0.65);
        players.CopyLatest("motion::a", ref version, (_, r) => region = r);
        // The lower third moves only near the bottom left: the top of the picture needn't be copied.
        Assert.False(region.IsEmpty);
        Assert.True(region.Y > 150 && region.X + region.Width < 300, $"copied {region}");
        // A screen that fell behind gets everything that changed since what it last copied.
        players.DrawNow(0.7);
        players.DrawNow(0.75);
        PixelRect behind = default;
        players.CopyLatest("motion::a", ref version, (_, r) => behind = r);
        Assert.True(behind.Width >= region.Width && behind.Y > 150, $"copied {behind}");
        Assert.Equal(4, version);
    }

    [Fact]
    public void HoldingStillMakesNoNewPictures()
    {
        using var players = Players();
        var motion = players.Start("motion::a", StarterMotionTemplates.NameStrap(), Timeline(), 480, 270, upAt: 0);
        players.DrawNow(1.0);
        var settled = motion.Version;
        for (var t = 1.1; t < 3.5; t += 1 / 60.0) players.DrawNow(t);
        Assert.Equal(settled, motion.Version);
        Assert.Equal(settled, motion.FramesDrawn);
    }

    [Fact]
    public void TakingItDownPlaysTheOutThenItsFinished()
    {
        using var players = Players();
        var finished = new List<string>();
        players.Finished += finished.Add;
        var motion = players.Start("motion::a", StarterMotionTemplates.NameStrap(), Timeline(), 480, 270, upAt: 0);
        players.DrawNow(2);
        Assert.True(players.TakeDown("motion::a", 10));
        Assert.False(players.TakeDown("motion::a", 11));
        players.DrawNow(10.4);
        Assert.Equal(222 + 24.0, motion.ShownFrame, 6);
        Assert.Empty(finished);
        players.DrawNow(10.8);
        Assert.True(motion.IsFinished);
        Assert.Equal(["motion::a"], finished);
        players.DrawNow(11);
        Assert.Single(finished);
    }

    [Fact]
    public void OnceFinishesByItself()
    {
        using var players = Players();
        var finished = new List<string>();
        players.Finished += finished.Add;
        players.Start("motion::a", StarterMotionTemplates.NameStrap(), Timeline(MotionPlayback.Once), 240, 135, upAt: 0);
        players.DrawNow(4.4);
        Assert.Empty(finished);
        players.DrawNow(4.5);
        Assert.Equal(["motion::a"], finished);
    }

    [Fact]
    public void NewFieldValuesShowWithoutStartingAgain()
    {
        using var players = Players();
        var motion = players.Start("motion::a", StarterMotionTemplates.NameStrap(), Timeline(), 480, 270, upAt: 0);
        players.DrawNow(2);
        var before = motion.Version;
        var edited = MotionFields.Apply(StarterMotionTemplates.NameStrap(), new Dictionary<string, string> { ["text:L0"] = "Deacon Musa Bello" });
        players.Update("motion::a", edited);
        // Still holding (no frame change), but the new words are drawn at once, at the same moment of the hold.
        players.DrawNow(2.01);
        Assert.Equal(before + 1, motion.Version);
        Assert.Equal(120.6, motion.ShownFrame, 6);
        Assert.Same(edited, motion.Document);
        var version = 0;
        byte[]? shown = null;
        players.CopyLatest("motion::a", ref version, (f, _) => shown = [.. f.Pixels]);
        using var renderer = new LottieRenderer(edited, LottieRendererTests.ShippedFonts);
        using var expected = new LottieFrame(480, 270);
        renderer.Render(motion.ShownFrame, expected);
        Assert.Equal(expected.Pixels, shown);
    }

    [Fact]
    public void ChangingThePlaybackRedrawsFromTheSameMoment()
    {
        using var players = Players();
        var motion = players.Start("motion::a", StarterMotionTemplates.NameStrap(), Timeline(), 240, 135, upAt: 0);
        players.DrawNow(1);
        players.Update("motion::a", Timeline(MotionPlayback.HoldLast));
        players.DrawNow(1.01);
        Assert.Equal(MotionPlayback.HoldLast, motion.Timeline.Playback);
        Assert.Equal(2, motion.Version);
    }

    [Fact]
    public void StoppingLetsItGo()
    {
        using var players = Players();
        players.Start("motion::a", StarterMotionTemplates.NameStrap(), Timeline(), 240, 135, upAt: 0);
        players.Start("motion::b", StarterMotionTemplates.ScriptureBar(), MotionTimeline.For(StarterMotionTemplates.ScriptureBar(), MotionPlayback.Loop), 240, 135, upAt: 0);
        players.Stop("motion::a");
        Assert.Null(players["motion::a"]);
        Assert.Equal(["motion::b"], players.Playing.Select(m => m.Id));
        var version = 0;
        Assert.False(players.CopyLatest("motion::a", ref version, (_, _) => { }));
        players.DrawNow(1);
        Assert.Equal(1, players["motion::b"]!.Version);
    }

    [Fact]
    public void TheDrawingThreadFollowsTheScreensRequests()
    {
        var now = 0.0;
        using var players = new MotionPlayers(LottieRendererTests.ShippedFonts, clock: () => Volatile.Read(ref now));
        var motion = players.Start("motion::a", StarterMotionTemplates.NameStrap(), Timeline(), 240, 135, upAt: 0);
        Volatile.Write(ref now, 0.3);
        players.Request(0.3);
        Assert.True(SpinWait.SpinUntil(() => motion.ShownFrame == 18.0, TimeSpan.FromSeconds(10)), $"shown {motion.ShownFrame}");
    }
}
