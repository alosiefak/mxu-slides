using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Model;
using static MxU.Slides.Core.Tests.Animation.AnimationFixtures;

namespace MxU.Slides.Core.Tests.Animation;

// Port of SlideSceneTests/ExitSemanticsTests.swift: the engine-level cases, and the
// ShowState.slideStepsCompletedAt cases that auto-advance relies on.
public class ExitSemanticsTests
{
    private static List<SlideObject> ExitSlideObjects() =>
    [
        Text("o", "one\ntwo\nthree", [
            Step("c1", AnimationTrigger.OnClick, duration: 0.5), Step("c2", AnimationTrigger.OnClick, duration: 0.5),
            Step("c3", AnimationTrigger.OnClick, duration: 0.5),
            Step("x1", AnimationTrigger.OnDismiss, AnimationKind.Out, duration: 0.4),
        ], name: "Points"),
    ];

    [Fact]
    public void TheExitGroupIsTheFinalClick()
    {
        var objects = ExitSlideObjects();
        Assert.Equal(3, AnimationSequence.ClickCount(objects, null));
        Assert.True(AnimationSequence.HasExitGroup(objects, null));
        Assert.Equal(4, AnimationSequence.AdvanceCount(objects, null)); // clicks + the exit press
    }

    [Fact]
    public void SettledArrivalSkipsTheWithSlideCeremony()
    {
        List<SlideObject> objects = [Text("o", "hi", [Step("auto-in", AnimationTrigger.WithPrevious, duration: 2), Step("c1", AnimationTrigger.OnClick, duration: 0.5)], name: "T")];
        var context = new AnimationContext(100, autoSettled: true);
        var steps = AnimationSequence.SceneSteps(objects, null)["o"];
        Assert.Equal(AnimationStepPhase.Done, AnimationTimeline.Phase(steps.First(s => s.Id == "auto-in"), context, 100.01));
        Assert.Equal(AnimationStepPhase.Pending, AnimationTimeline.Phase(steps.First(s => s.Id == "c1"), context, 100.01));
    }

    [Fact]
    public void ExitAnchorPrefersTheFinalClickOverDismiss()
    {
        var context = new AnimationContext(0, dismissHostTime: 50, exitHostTime: 10);
        Assert.Equal(10, context.Start(SceneAnimationGroup.Exit)); // a played exit anchors the group
        var dismissed = new AnimationContext(0, dismissHostTime: 50);
        Assert.Equal(50, dismissed.Start(SceneAnimationGroup.Exit)); // no exit press = the dismiss anchors it
    }

    [Fact]
    public void StepsCompletedAtGatesOnEveryHumanPress()
    {
        var show = new Core.Scene.ShowState();
        show.Fire(new Slide { Id = "s", Name = "S", Objects = ExitSlideObjects() }, atHostTime: 100);
        Assert.Null(show.SlideStepsCompletedAt()); // clicks pending: never auto-click
        foreach (var t in new[] { 101.0, 102, 103 }) show.AdvanceStep(t);
        Assert.Null(show.SlideStepsCompletedAt()); // the exit press is a human step too
        show.AdvanceStep(104);
        Assert.Equal(104.4, show.SlideStepsCompletedAt()!.Value, 9); // exit press + the exit group's animations

        show.UnadvanceStep();
        Assert.Null(show.SlideStepsCompletedAt());
    }

    [Fact]
    public void StepsCompletedAtForClickOnlyAndPlainSlides()
    {
        var clickObject = Text("o", "a\nb", [Step("c1", AnimationTrigger.OnClick, duration: 1), Step("c2", AnimationTrigger.OnClick, duration: 2)], name: "T");
        var show = new Core.Scene.ShowState();
        show.Fire(new Slide { Id = "s", Name = "S", Objects = [clickObject] }, atHostTime: 10);
        show.AdvanceStep(11);
        Assert.Null(show.SlideStepsCompletedAt());
        show.AdvanceStep(12);
        Assert.Equal(14, show.SlideStepsCompletedAt()); // last click + that group's duration

        show.Fire(new Slide { Id = "p", Name = "P", Objects = [] }, atHostTime: 20);
        Assert.Equal(20, show.SlideStepsCompletedAt());
    }

    [Fact]
    public void StepsCompleteAfterTheExitPressPlusTheExitGroup()
    {
        // The arithmetic behind ShowState.slideStepsCompletedAt: exit press + the exit group's duration.
        var steps = AllSteps(ExitSlideObjects());
        Assert.Equal(0.4, AnimationTimeline.GroupDuration(SceneAnimationGroup.Exit, steps));
        var context = new AnimationContext(100, clickHostTimes: [101, 102, 103], exitHostTime: 104);
        Assert.False(AnimationTimeline.GroupCompleted(SceneAnimationGroup.Exit, steps, context, 104.3));
        Assert.True(AnimationTimeline.GroupCompleted(SceneAnimationGroup.Exit, steps, context, 104.4));
    }
}
