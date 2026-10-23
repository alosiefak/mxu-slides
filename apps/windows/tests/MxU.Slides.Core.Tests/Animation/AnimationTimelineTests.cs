using MxU.Slides.Core.Animation;

namespace MxU.Slides.Core.Tests.Animation;

// Port of RenderEngineTests/AnimationTimelineTests.swift.
public class AnimationTimelineTests
{
    private static SceneAnimationStep Step(
        string id, SceneAnimationKind kind, SceneAnimationGroup group, double start = 0, double duration = 1,
        SceneAnimationRamp ramp = SceneAnimationRamp.None, List<SceneAnimationRange>? ranges = null, bool placeholder = false) => new()
    {
        Id = id, Kind = kind, Animation = SceneStepAnimation.Fade, Group = group, StartOffset = start,
        Duration = duration, Ramp = ramp, Ranges = ranges, PlaceholderUnderline = placeholder,
    };

    [Fact]
    public void PhaseFollowsGroupStartAndOffset()
    {
        var s = Step("a", SceneAnimationKind.Enter, SceneAnimationGroup.Click(0), start: 0.5, duration: 2);
        var ctx = new AnimationContext(100, clickHostTimes: [110]);
        Assert.Equal(AnimationStepPhase.Pending, AnimationTimeline.Phase(s, ctx, 105)); // click not consumed
        Assert.Equal(AnimationStepPhase.Pending, AnimationTimeline.Phase(s, new AnimationContext(100), 200));
        Assert.Equal(AnimationStepPhase.Pending, AnimationTimeline.Phase(s, ctx, 110.4)); // offset not elapsed
        Assert.Equal(AnimationStepPhase.Running(0.5), AnimationTimeline.Phase(s, ctx, 111.5));
        Assert.Equal(AnimationStepPhase.Done, AnimationTimeline.Phase(s, ctx, 112.5));
        Assert.Equal(AnimationStepPhase.Done, AnimationTimeline.Phase(s, null, 0)); // no context = settled
    }

    [Fact]
    public void RampsEaseWithoutOvershoot()
    {
        Assert.Equal(0.25, SceneAnimationRamp.None.Apply(0.25));
        Assert.Equal(0.25, SceneAnimationRamp.EaseIn.Apply(0.5));
        Assert.Equal(0.75, SceneAnimationRamp.EaseOut.Apply(0.5));
        Assert.Equal(0.5, SceneAnimationRamp.EaseInOut.Apply(0.5));
        Assert.Equal(1, SceneAnimationRamp.EaseInOut.Apply(1.5));
        Assert.Equal(0, SceneAnimationRamp.EaseIn.Apply(-1));
        var s = Step("a", SceneAnimationKind.Enter, SceneAnimationGroup.Auto, duration: 2, ramp: SceneAnimationRamp.EaseOut);
        Assert.Equal(AnimationStepPhase.Running(0.75), AnimationTimeline.Phase(s, new AnimationContext(0), 1));
    }

    [Fact]
    public void WholeObjectVisibilityGates()
    {
        var enter = Step("in", SceneAnimationKind.Enter, SceneAnimationGroup.Click(0));
        var exit = Step("out", SceneAnimationKind.Exit, SceneAnimationGroup.Click(1));
        SceneAnimationStep[] steps = [enter, exit];
        var ctx0 = new AnimationContext(0);
        Assert.True(AnimationTimeline.Visibility(steps, ctx0, 5).HidesWholeObject); // In pending = hidden
        var ctx1 = new AnimationContext(0, clickHostTimes: [10]);
        Assert.False(AnimationTimeline.Visibility(steps, ctx1, 10.2).HidesWholeObject); // In running = shown
        Assert.False(AnimationTimeline.Visibility(steps, ctx1, 20).HidesWholeObject); // settled between
        var ctx2 = new AnimationContext(0, clickHostTimes: [10, 30]);
        Assert.False(AnimationTimeline.Visibility(steps, ctx2, 30.5).HidesWholeObject); // Out running = still shown
        Assert.True(AnimationTimeline.Visibility(steps, ctx2, 31.5).HidesWholeObject); // Out done = hidden
        Assert.False(AnimationTimeline.Visibility(steps, null, 0).HidesWholeObject); // editor: settled shows

        Assert.False(AnimationTimeline.Visibility([exit], ctx0, 5).HidesWholeObject);

        var pulse = Step("p", SceneAnimationKind.Emphasis, SceneAnimationGroup.Click(0));
        Assert.False(AnimationTimeline.Visibility([pulse], ctx0, 5).HidesWholeObject);
    }

    [Fact]
    public void RangeVisibilityAndPlaceholders()
    {
        var r1 = new SceneAnimationRange(0, 0, 4);
        var r2 = new SceneAnimationRange(1, 2, 3);
        var s1 = Step("a", SceneAnimationKind.Enter, SceneAnimationGroup.Click(0), ranges: [r1], placeholder: true);
        var s2 = Step("b", SceneAnimationKind.Enter, SceneAnimationGroup.Click(1), ranges: [r2]);
        var ctx = new AnimationContext(0, clickHostTimes: [10]);
        var v = AnimationTimeline.Visibility([s1, s2], ctx, 10.5);
        Assert.False(v.HidesWholeObject);
        Assert.Equal([r2], v.HiddenRanges);
        Assert.Empty(v.PlaceholderRanges);
        var before = AnimationTimeline.Visibility([s1, s2], new AnimationContext(0), 1);
        Assert.Equal([r1, r2], before.HiddenRanges);
        Assert.Equal([r1], before.PlaceholderRanges); // fill-in-the-blank keeps its underline while hidden
    }

    [Fact]
    public void GroupCompletionAndDuration()
    {
        var a = Step("a", SceneAnimationKind.Exit, SceneAnimationGroup.Exit, start: 0, duration: 1);
        var b = Step("b", SceneAnimationKind.Exit, SceneAnimationGroup.Exit, start: 0.5, duration: 1);
        SceneAnimationStep[] steps = [a, b, Step("c", SceneAnimationKind.Enter, SceneAnimationGroup.Auto)];
        Assert.Equal(1.5, AnimationTimeline.GroupDuration(SceneAnimationGroup.Exit, steps));
        Assert.Equal(0, AnimationTimeline.GroupDuration(SceneAnimationGroup.Click(0), steps));
        Assert.True(AnimationTimeline.GroupCompleted(SceneAnimationGroup.Click(0), steps, null, 0)); // empty group = complete
        var notDismissed = new AnimationContext(0);
        Assert.False(AnimationTimeline.GroupCompleted(SceneAnimationGroup.Exit, steps, notDismissed, 100));
        var dismissed = new AnimationContext(0, dismissHostTime: 50);
        Assert.False(AnimationTimeline.GroupCompleted(SceneAnimationGroup.Exit, steps, dismissed, 51.2));
        Assert.True(AnimationTimeline.GroupCompleted(SceneAnimationGroup.Exit, steps, dismissed, 51.5));
    }
}
