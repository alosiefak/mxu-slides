using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using static MxU.Slides.Core.Tests.Animation.AnimationFixtures;

namespace MxU.Slides.Core.Tests.Scene;

/// <summary>
/// The live show's click, exit and deferred-clear bookkeeping. Ports of the Mac's
/// ShowState cases in AnimationSequenceTests and ExitSemanticsTests.
/// </summary>
public class ShowStateAnimationTests
{
    private static Slide PointsSlide() => new()
    {
        Id = "s1", Name = "Points",
        Objects = [Text("pts", "one\ntwo\nthree",
            [.. Enumerable.Range(0, 3).Select(line => Step($"l{line}", AnimationTrigger.OnClick, duration: 0.5, ranges: [Range(line, 0, 5)]))])],
    };

    private static Slide ExitSlide() => new()
    {
        Id = "s1", Name = "Points",
        Objects = [Text("o", "one\ntwo\nthree", [
            Step("c1", AnimationTrigger.OnClick, duration: 0.5), Step("c2", AnimationTrigger.OnClick, duration: 0.5),
            Step("c3", AnimationTrigger.OnClick, duration: 0.5),
            Step("x1", AnimationTrigger.OnDismiss, AnimationKind.Out, duration: 0.4),
        ])],
    };

    private static Overlay LowerThird() => new()
    {
        Id = "lt", Name = "Lower Third",
        Objects = [new SlideObject
        {
            Id = "plate", ObjectKind = SlideObjectKind.Shape, Name = "Plate", Text = "",
            AnimationSteps = [Step("in", AnimationTrigger.WithPrevious, duration: 0.6), Step("out", AnimationTrigger.OnDismiss, AnimationKind.Out, duration: 0.4)],
        }],
    };

    private static List<AnimatedItem> Layer(ShowState show, LayerKind kind) => show.AnimatedScene()[kind].Items;

    [Fact]
    public void ThreeClicksRevealThreePointsThenTheFourthIsFree()
    {
        var show = new ShowState();
        show.Fire(PointsSlide(), atHostTime: 100);
        Assert.Equal(3, show.SlideClickCount);
        Assert.Equal(0, show.SlideAnimationStep?.Consumed);
        Assert.True(show.AdvanceStep(101));
        Assert.True(show.AdvanceStep(102));
        Assert.True(show.AdvanceStep(103));
        Assert.Equal(3, show.SlideAnimationStep?.Consumed);
        Assert.False(show.AdvanceStep(104)); // the 4th press moves to the next slide
        Assert.Equal(new AnimationContext(100, [101, 102, 103]), show.SlideAnimationContext);
        Assert.Contains(Layer(show, LayerKind.Slide), i => i.AnimationContext?.ClickHostTimes.SequenceEqual([101.0, 102, 103]) == true && i.AnimationSteps.Count == 3);

        Assert.True(show.UnadvanceStep());
        Assert.Equal(2, show.SlideAnimationStep?.Consumed);

        show.Fire(PointsSlide(), atHostTime: 200);
        Assert.Equal(0, show.SlideAnimationStep?.Consumed);

        show.Fire(new Slide { Id = "plain", Name = "Plain", Objects = [Text("t", "hello")] }, atHostTime: 300);
        Assert.False(show.AdvanceStep(301));
        Assert.False(show.UnadvanceStep());
        Assert.Null(show.SlideAnimationContext);
        Assert.Null(show.SlideAnimationStep);
    }

    [Fact]
    public void OverlayWithTimedOutDisappearsUnaidedAndClearAllCuts()
    {
        var show = new ShowState();
        show.Fire(LowerThird(), atHostTime: 10);

        show.DismissOverlay("lt", atHostTime: 20);
        Assert.Single(show.LiveOverlays);
        Assert.True(show.IsLeaving("lt"));
        Assert.True(show.HasPendingExits);
        Assert.Equal(new AnimationContext(10, dismissHostTime: 20), Layer(show, LayerKind.Overlays).First().AnimationContext);

        Assert.False(show.SweepFinishedExits(20.3));
        Assert.Single(show.LiveOverlays);
        Assert.True(show.SweepFinishedExits(20.4));
        Assert.Empty(show.LiveOverlays);
        Assert.False(show.HasPendingExits);

        show.Fire(LowerThird(), atHostTime: 30);
        show.Clear(ShowFunction.Overlays, atHostTime: 31);
        Assert.Single(show.LiveOverlays); // a timed clear plays the Out first
        show.Fire(LowerThird(), atHostTime: 32);
        Assert.False(show.IsLeaving("lt"));

        show.Clear(LayerKind.Overlays, atHostTime: 40);
        Assert.Single(show.LiveOverlays);
        show.ClearAll();
        Assert.Empty(show.LiveOverlays);
        Assert.False(show.HasPendingExits);

        show.Fire(LowerThird(), atHostTime: 50);
        show.DismissOverlay("lt");
        Assert.Empty(show.LiveOverlays);
        show.Fire(new Overlay { Id = "plain", Name = "Plain", Objects = [Text("x", "x")] }, atHostTime: 60);
        show.DismissOverlay("plain", atHostTime: 61);
        Assert.Empty(show.LiveOverlays);
    }

    [Fact]
    public void SlideClearDefersWhileItsOutPlaysAndClicksFreezeMeanwhile()
    {
        var slide = PointsSlide();
        slide.Objects[0].AnimationSteps!.Add(Step("out", AnimationTrigger.OnDismiss, AnimationKind.Out, duration: 1));
        var show = new ShowState();
        show.Fire(slide, atHostTime: 0);
        show.AdvanceStep(1);
        show.Clear(ShowFunction.Slides, atHostTime: 5);
        Assert.NotNull(show.Live); // still showing, playing its Out
        Assert.False(show.AdvanceStep(5.5)); // no clicks while leaving
        Assert.Equal(5, show.SlideAnimationContext?.DismissHostTime);
        Assert.False(show.SweepFinishedExits(5.9));
        Assert.True(show.SweepFinishedExits(6));
        Assert.Null(show.Live);
        Assert.Equal("s1", show.Last?.Slide.Id);

        show.Fire(slide, atHostTime: 10);
        show.Clear(LayerKind.Slide, atHostTime: 11);
        Assert.NotNull(show.Live);
        show.Fire(PointsSlide(), atHostTime: 12);
        Assert.Null(show.SlideAnimationContext?.DismissHostTime);
    }

    [Fact]
    public void TheExitGroupIsTheFinalClick()
    {
        var show = new ShowState();
        show.Fire(ExitSlide(), atHostTime: 100);
        Assert.Equal(3, show.SlideClickCount);
        Assert.True(show.SlideHasExitGroup);
        Assert.Equal(4, show.SlideAdvanceCount);
        foreach (var t in new[] { 101.0, 102, 103 }) Assert.True(show.AdvanceStep(t));
        Assert.True(show.AdvanceStep(104));
        Assert.NotNull(show.Live); // the exit press never removes the cue
        Assert.Equal(4, show.SlideAnimationStep?.Consumed);
        Assert.Equal(104, show.SlideAnimationContext?.ExitHostTime);
        Assert.False(show.HasPendingExits);
        Assert.False(show.SweepFinishedExits(1_000));
        Assert.NotNull(show.Live);
        Assert.False(show.AdvanceStep(104.1)); // a second press mid-exit moves on
    }

    [Fact]
    public void BackUnplaysTheExitBeforeUnconsumingClicks()
    {
        var show = new ShowState();
        show.Fire(ExitSlide(), atHostTime: 100);
        foreach (var t in new[] { 101.0, 102, 103, 104 }) show.AdvanceStep(t);
        Assert.True(show.UnadvanceStep());
        Assert.Null(show.SlideAnimationContext?.ExitHostTime);
        Assert.Equal(3, show.SlideAnimationContext?.ClickHostTimes.Count);
        Assert.True(show.UnadvanceStep());
        Assert.Equal(2, show.SlideAnimationContext?.ClickHostTimes.Count);
    }

    [Fact]
    public void ClearAfterTheExitPlayedRemovesImmediately()
    {
        var show = new ShowState();
        show.Fire(ExitSlide(), atHostTime: 100);
        foreach (var t in new[] { 101.0, 102, 103, 104 }) show.AdvanceStep(t);
        show.Clear(ShowFunction.Slides, atHostTime: 104.2);
        Assert.Null(show.Live);
        Assert.Equal("s1", show.Last?.Slide.Id);
    }

    [Fact]
    public void DirectFiresAlwaysCut()
    {
        var show = new ShowState();
        show.Fire(ExitSlide(), atHostTime: 100);
        foreach (var t in new[] { 101.0, 102, 103, 104 }) show.AdvanceStep(t);
        show.Fire(new Slide { Id = "s2", Name = "Next", Objects = [] }, atHostTime: 104.1);
        Assert.Equal("s2", show.Live?.Slide.Id);
        Assert.Null(show.SlideAnimationStep);
    }

    [Fact]
    public void TheRendererKeepsDrawingOnlyWhileSomethingMoves()
    {
        var show = new ShowState();
        show.Fire(new Slide
        {
            Id = "s", Name = "S",
            Objects = [Text("o", "hi", [Step("in", AnimationTrigger.WithPrevious, duration: 1), Step("c", AnimationTrigger.OnClick, duration: 0.5)])],
        }, atHostTime: 0);
        var scene = show.AnimatedScene();
        Assert.True(AnimationTimeline.IsAnimating(scene, 0.5)); // the With Slide In is playing
        Assert.False(AnimationTimeline.IsAnimating(scene, 2)); // settled; the click group waits for a press
        show.AdvanceStep(3);
        Assert.True(AnimationTimeline.IsAnimating(show.AnimatedScene(), 3.2));
        Assert.False(AnimationTimeline.IsAnimating(show.AnimatedScene(), 3.6));
    }

    [Fact]
    public void TheStillSceneShowsEverythingAtRest()
    {
        var show = new ShowState();
        show.Fire(PointsSlide(), atHostTime: 0);
        var still = show.Scene();
        Assert.Contains(still[LayerKind.Slide].Items, i => i.Content is ItemContent.Text { Styled.String: "one\ntwo\nthree" });
    }
}
