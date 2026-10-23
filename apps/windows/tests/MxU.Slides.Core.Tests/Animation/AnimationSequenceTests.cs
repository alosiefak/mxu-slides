using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using static MxU.Slides.Core.Tests.Animation.AnimationFixtures;

namespace MxU.Slides.Core.Tests.Animation;

// Port of SlideSceneTests/AnimationSequenceTests.swift (the ShowState cases are skipped: see the report).
public class AnimationSequenceTests
{
    private static SlideObject Object(string id, List<AnimationStep> steps, bool? hidden = null) => Text(id, "one two\nthree", steps, hidden: hidden);

    [Fact]
    public void DocumentOrderThenBuildOrderWins()
    {
        var objects = Objects(
            Object("a", [Step("a1", AnimationTrigger.OnClick), Step("a2", AnimationTrigger.WithPrevious)]),
            Object("b", [Step("b1", AnimationTrigger.OnClick)]));
        Assert.Equal(["a1", "a2", "b1"], AnimationSequence.OrderedEntries(objects, null).Select(e => e.Step.Id));
        // Listed first, unknown/dupes ignored, rest in document order.
        Assert.Equal(["b1", "a1", "a2"], AnimationSequence.OrderedEntries(objects, ["b1", "zzz", "b1"]).Select(e => e.Step.Id));
        var hiddenObjects = Objects([Object("h", [Step("h1", AnimationTrigger.OnClick)], hidden: true), .. objects]);
        Assert.DoesNotContain(AnimationSequence.OrderedEntries(hiddenObjects, null), e => e.ObjectId == "h");
    }

    [Fact]
    public void ClickGroupsAutoAndExit()
    {
        var objects = Objects(
            Object("bar", [Step("bar-in", AnimationTrigger.WithPrevious), Step("bar-out", AnimationTrigger.OnDismiss, AnimationKind.Out)]),
            Object("p1", [Step("p1", AnimationTrigger.OnClick)]),
            Object("p2", [Step("p2", AnimationTrigger.OnClick), Step("p2b", AnimationTrigger.AfterPrevious)]),
            Object("p3", [Step("p3", AnimationTrigger.OnClick), Step("p3-out", AnimationTrigger.OnDismiss, AnimationKind.Out), Step("tail", AnimationTrigger.WithPrevious)]));
        var g = AnimationSequence.Groups(objects, null);
        Assert.Equal(["bar-in"], g.Auto.Select(e => e.Step.Id)); // leading non-click steps run from the fire
        Assert.Equal([["p1"], ["p2", "p2b"], ["p3"]], g.Click.Select(c => c.Select(e => e.Step.Id).ToList()));
        Assert.Equal(["bar-out", "p3-out", "tail"], g.Exit.Select(e => e.Step.Id)); // chained-after-dismiss joins the exit group
        Assert.Equal(3, AnimationSequence.ClickCount(objects, null));
        Assert.True(AnimationSequence.HasAnimationSteps(objects));
        Assert.False(AnimationSequence.HasAnimationSteps([Object("x", [])]));
    }

    [Fact]
    public void ScheduleFoldsChainingIntoOffsets()
    {
        var objects = Objects(
            Object("a", [Step("a1", AnimationTrigger.OnClick, delay: 0.25, duration: 1)]),
            Object("b", [Step("b1", AnimationTrigger.WithPrevious, delay: 0.1, duration: 2)]),
            Object("c", [Step("c1", AnimationTrigger.AfterPrevious, delay: 0.5, duration: 1)]),
            Object("d", [Step("d1", AnimationTrigger.OnClick, duration: 3), Step("d2", AnimationTrigger.OnDismiss, AnimationKind.Out, delay: 1, duration: 0.4)]),
            Object("e", [Step("e1", AnimationTrigger.OnDismiss, AnimationKind.Out, duration: 2), Step("e2", AnimationTrigger.AfterPrevious, duration: 1)]));
        var steps = AnimationSequence.SceneSteps(objects, null);
        Assert.Equal(SceneAnimationGroup.Click(0), steps["a"][0].Group);
        Assert.Equal(0.25, steps["a"][0].StartOffset);
        Assert.Equal(0.35, steps["b"][0].StartOffset); // with previous = previous start + delay
        Assert.Equal(2.85, steps["c"][0].StartOffset); // after previous = previous end + delay
        Assert.Equal([SceneAnimationGroup.Click(1), SceneAnimationGroup.Exit], steps["d"].Select(s => s.Group));
        Assert.Equal(1, steps["d"][1].StartOffset); // each onDismiss keys off the dismiss instant
        Assert.Equal(0, steps["e"][0].StartOffset);
        Assert.Equal(2, steps["e"][1].StartOffset);
        Assert.Equal(3, AnimationSequence.ExitDuration(objects, null)); // e1 (2s) then e2 (1s)
        Assert.Equal(0, AnimationSequence.ExitDuration([objects[0]], null));
    }

    [Fact]
    public void SchemaToEngineDefaults()
    {
        List<AnimationRange> ranges = [Range(0, 4, 3)];
        var inStep = AnimationSequence.SceneStep(Step("i", AnimationTrigger.OnClick, AnimationKind.In, ranges: ranges), SceneAnimationGroup.Click(0), 0);
        Assert.Equal(SceneAnimationKind.Enter, inStep.Kind);
        Assert.Equal(SceneAnimationRamp.EaseOut, inStep.Ramp); // In defaults to ease-out
        Assert.Equal([new SceneAnimationRange(0, 4, 3)], inStep.Ranges!);
        var outStep = AnimationSequence.SceneStep(Step("o", AnimationTrigger.OnDismiss, AnimationKind.Out), SceneAnimationGroup.Exit, 0);
        Assert.Equal(SceneAnimationKind.Exit, outStep.Kind);
        Assert.Equal(SceneAnimationRamp.EaseIn, outStep.Ramp); // Out defaults to ease-in
        Assert.True(outStep.TargetsWholeObject);
        var emph = AnimationSequence.SceneStep(Step("e", AnimationTrigger.OnClick, AnimationKind.Emphasis, ramp: AnimationRamp.None), SceneAnimationGroup.Click(0), 0);
        Assert.Equal(SceneAnimationRamp.None, emph.Ramp); // explicit ramp wins
        var blur = Step("b", AnimationTrigger.OnClick, AnimationKind.In, StepAnimation.Blur);
        blur.ColorHex = "#FF000080";
        var engineBlur = AnimationSequence.SceneStep(blur, SceneAnimationGroup.Click(0), 0);
        Assert.Equal(24, engineBlur.Amount); // blur radius default
        Assert.Equal(128.0 / 255, engineBlur.Color?.Alpha);

        var colorEmph = Step("c", AnimationTrigger.OnClick, AnimationKind.Emphasis, StepAnimation.Color);
        var engineColor = AnimationSequence.SceneStep(colorEmph, SceneAnimationGroup.Click(0), 0);
        Assert.Equal(ColorHex.Color("#FFD54FFF"), engineColor.Color);
        Assert.Equal(0.6, engineColor.Amount); // tint strength default

        var empty = AnimationSequence.SceneStep(Step("x", AnimationTrigger.OnClick, AnimationKind.In, ranges: []), SceneAnimationGroup.Auto, 0);
        Assert.Null(empty.Ranges);
    }

    [Fact]
    public void EndToEndThreePointsOnThreeClicks()
    {
        var lines = Enumerable.Range(0, 3).Select(line => Range(line, 0, 5)).ToList();
        var objects = Objects(Object("pts", lines.Select(r => Step($"l{r.Line}", AnimationTrigger.OnClick, AnimationKind.In, duration: 0.5, ranges: [r])).ToList()));
        var steps = AnimationSequence.SceneSteps(objects, null)["pts"];
        Assert.Equal(3, AnimationSequence.ClickCount(objects, null));
        var fired = new AnimationContext(0);
        Assert.Equal(3, AnimationTimeline.Visibility(steps, fired, 1).HiddenRanges.Count);
        var two = new AnimationContext(0, clickHostTimes: [1, 2]);
        Assert.Equal([2], AnimationTimeline.Visibility(steps, two, 3).HiddenRanges.Select(r => r.Line));
        var three = new AnimationContext(0, clickHostTimes: [1, 2, 3]);
        Assert.Empty(AnimationTimeline.Visibility(steps, three, 3.1).HiddenRanges);
    }

    [Fact]
    public void UnclickedOutPlaysAtDismissAndConsumedOutDoesNotReplay()
    {
        var objects = Objects(Object("t", [
            Step("in", AnimationTrigger.WithPrevious, duration: 0.3),
            Step("out", AnimationTrigger.OnClick, AnimationKind.Out, duration: 0.4),
        ]));
        var steps = AllSteps(objects);
        var @out = steps.First(s => s.Id == "out");

        var dismissed = new AnimationContext(0, dismissHostTime: 10);
        Assert.False(AnimationTimeline.Phase(@out, dismissed, 9).HasStarted);
        Assert.True(AnimationTimeline.Phase(@out, dismissed, 10.2).HasStarted);
        Assert.True(AnimationTimeline.Visibility(steps, dismissed, 10.5).HidesWholeObject);

        var clicked = new AnimationContext(0, clickHostTimes: [5], dismissHostTime: 20);
        Assert.True(AnimationTimeline.Visibility(steps, clicked, 5.5).HidesWholeObject);
        Assert.True(AnimationTimeline.Phase(@out, clicked, 20.2).IsDone); // played once, at its click

        var gated = Objects(Object("g", [
            Step("g.in", AnimationTrigger.OnClick),
            Step("g.out", AnimationTrigger.OnClick, AnimationKind.Out, duration: 0.4),
        ]));
        Assert.True(AnimationTimeline.Visibility(AllSteps(gated), dismissed, 10.2).HidesWholeObject);
    }

    [Fact]
    public void ExitDurationCountsUnconsumedClickOuts()
    {
        var objects = Objects(Object("t", [
            Step("in", AnimationTrigger.WithPrevious, duration: 0.3),
            Step("out", AnimationTrigger.OnClick, AnimationKind.Out, duration: 0.4),
            Step("tail", AnimationTrigger.OnDismiss, AnimationKind.Out, duration: 0.2),
        ]));
        Assert.Equal(0.4, AnimationSequence.ExitDuration(objects, null, consumedClicks: 0)); // the un-clicked out extends the leave
        Assert.Equal(0.2, AnimationSequence.ExitDuration(objects, null, consumedClicks: 1)); // consumed = already played
    }

    private static Slide PointsSlide()
    {
        var lines = Enumerable.Range(0, 3).Select(line => Range(line, 0, 5)).ToList();
        var o = Text("pts", "one\ntwo\nthree", lines.Select(r => Step($"l{r.Line}", AnimationTrigger.OnClick, duration: 0.5, ranges: [r])).ToList(), name: "Points");
        return new Slide { Id = "s1", Name = "Points", Objects = [o] };
    }

    [Fact]
    public void PreviewTimelineWalksEveryGroupAndFreezesOnScrub()
    {
        var slide = PointsSlide();
        slide.Objects[0].AnimationSteps!.Add(Step("out", AnimationTrigger.OnDismiss, AnimationKind.Out, duration: 1));
        var timeline = AnimationSequence.PreviewTimeline(slide.Objects, slide.AnimationOrder);

        Assert.All(timeline.Clicks.Zip([0, 0.9, 1.8]), pair => Assert.Equal(pair.Second, pair.First, 1e-9));
        Assert.Equal(2.3 + AnimationSequence.PreviewExitHold, timeline.DismissAt, 1e-9);
        Assert.Equal(timeline.DismissAt + 1, timeline.Duration, 1e-9);

        var frozen = AnimationSequence.PreviewContext(slide.Objects, null, time: 1.0);
        Assert.Equal(1.0, frozen.FrozenHostTime);
        var item = SlideItems(slide, null, frozen).First(i => i.Id == "pts");
        Assert.Equal(frozen, item.AnimationContext);
        Assert.Equal(4, item.AnimationSteps.Count);
        var vis = AnimationTimeline.Visibility(item.AnimationSteps, frozen, 999);
        Assert.Equal([2], vis.HiddenRanges.Select(r => r.Line)); // frozen clock ignores the render clock

        Assert.Null(SlideItems(slide, null).First(i => i.Id == "pts").AnimationContext);
    }

    [Fact]
    public void AuthoringWarningsFlagImpossibleOrder()
    {
        var objects = Objects(
            Object("a", [Step("a-out", AnimationTrigger.OnDismiss, AnimationKind.Out), Step("a-in", AnimationTrigger.OnClick), Step("a-in2", AnimationTrigger.OnClick)]),
            Object("b", [Step("b-emph", AnimationTrigger.OnClick, AnimationKind.Emphasis)]),
            Object("c", [Step("c-emph", AnimationTrigger.OnClick, AnimationKind.Emphasis), Step("c-in", AnimationTrigger.WithPrevious)]));
        var warnings = AnimationSequence.Warnings(objects, null);
        Assert.True(warnings.ContainsKey("a-out")); // Out before its In
        Assert.False(warnings.ContainsKey("a-in"));
        Assert.True(warnings.ContainsKey("a-in2")); // second In while shown
        Assert.False(warnings.ContainsKey("b-emph"));
        Assert.True(warnings.ContainsKey("c-emph")); // emphasis before the In

        var ordered = AnimationSequence.Warnings(objects, ["a-in", "a-out"]);
        Assert.False(ordered.ContainsKey("a-out"));
        Assert.False(ordered.ContainsKey("a-in"));
    }

    [Fact]
    public void NegativeDelayOverlapsThePreviousStep()
    {
        var objects = Objects(
            Object("frame", [Step("draw", AnimationTrigger.OnClick, duration: 1.2)]),
            Object("title", [Step("type", AnimationTrigger.AfterPrevious, delay: -0.3, duration: 0.8)]),
            Object("z", [Step("early", AnimationTrigger.WithPrevious, delay: -5, duration: 0.5)]));
        var steps = AnimationSequence.SceneSteps(objects, null);
        Assert.Equal(0.9, steps["title"][0].StartOffset, 1e-9); // starts 0.3s before the draw ends
        Assert.Equal(0, steps["z"][0].StartOffset); // never before the group instant
    }

    [Fact]
    public void MorphStepsRenderTheirEndStateOntoEveryItemOfTheObject()
    {
        var o = new SlideObject
        {
            Id = "box", ObjectKind = SlideObjectKind.Shape, Name = "Box", Text = "Hi",
            X = 100, Y = 100, Width = 200, Height = 100,
            Fill = new ObjectFill { FillKind = FillKind.Solid, ColorHex = "#FF0000FF" },
        };
        var end = MxuJson.Clone(o);
        end.X = 600;
        end.Width = 300;
        end.ShapeKind = ShapeKind.Ellipse;
        end.Fill = new ObjectFill { FillKind = FillKind.Solid, ColorHex = "#0000FFFF" };
        end.AnimationSteps = [new AnimationStep { Id = "junk", Kind = AnimationKind.In, Animation = StepAnimation.Fade, Trigger = AnimationTrigger.OnClick, DurationSeconds = 1 }];
        var morph = new AnimationStep { Id = "m", Kind = AnimationKind.Morph, Animation = StepAnimation.Fade, Trigger = AnimationTrigger.OnClick, DurationSeconds = 1, ToObject = end };
        var from = MxuJson.Clone(o);
        from.X = -400;
        var enter = new AnimationStep { Id = "in", Kind = AnimationKind.In, Animation = StepAnimation.Move, Trigger = AnimationTrigger.OnClick, DurationSeconds = 1, FromObject = from };
        o.AnimationSteps = [enter, morph];
        var steps = AnimationSequence.SceneSteps([o], null)["box"];
        var items = AnimatedItems.For(o, SlideSceneBuilder.RenderItems(o, null), steps);
        Assert.Equal(2, items.Count); // shape + its ::text
        foreach (var item in items)
        {
            var inStep = item.AnimationSteps.First(s => s.Id == "in");
            var mStep = item.AnimationSteps.First(s => s.Id == "m");
            Assert.Equal(-400, inStep.FromItem?.Render.Frame.X);
            Assert.Equal(600, mStep.ToItem?.Render.Frame.X);
            Assert.Equal(item.Id.EndsWith("::text"), mStep.ToItem!.Id.EndsWith("::text")); // matched by suffix
            Assert.Empty(mStep.ToItem.AnimationSteps); // the end state's own animationSteps are ignored
        }

        var bare = new AnimationStep { Id = "m2", Kind = AnimationKind.Morph, Animation = StepAnimation.Fade, Trigger = AnimationTrigger.OnClick, DurationSeconds = 1 };
        Assert.Equal("No end state yet", AnimationSequence.Warnings([Text("o", "x", [bare])], null)["m2"]);
    }

    [Fact]
    public void GroupedMoveTakesTheTriggerOfItsNewNeighbourhood()
    {
        var objects = Objects(
            Object("a", [Step("a1", AnimationTrigger.OnClick)]),
            Object("b", [Step("b1", AnimationTrigger.OnClick)]),
            Object("c", [Step("c1", AnimationTrigger.OnClick), Step("c-out", AnimationTrigger.OnDismiss, AnimationKind.Out)]));

        var into = AnimationSequence.GroupedMove(objects, null, "b1", AnimationSequence.GroupSlot.Click(0), 1)!;
        Assert.Equal(["a1", "b1", "c1", "c-out"], into.Order);
        Assert.Equal(AnimationTrigger.WithPrevious, into.Triggers["b1"]);

        var first = AnimationSequence.GroupedMove(objects, null, "c1", AnimationSequence.GroupSlot.Click(0), 0)!;
        Assert.Equal(["c1", "a1", "b1", "c-out"], first.Order);
        Assert.Equal(AnimationTrigger.OnClick, first.Triggers["c1"]);
        Assert.Equal(AnimationTrigger.WithPrevious, first.Triggers["a1"]);

        var exit = AnimationSequence.GroupedMove(objects, null, "a1", AnimationSequence.GroupSlot.Exit, 0)!;
        Assert.Equal(["b1", "c1", "a1", "c-out"], exit.Order);
        Assert.Equal(AnimationTrigger.OnDismiss, exit.Triggers["a1"]);
        Assert.Equal(AnimationTrigger.WithPrevious, exit.Triggers["c-out"]);

        var auto = AnimationSequence.GroupedMove(objects, null, "b1", AnimationSequence.GroupSlot.Auto, 0)!;
        Assert.Equal("b1", auto.Order[0]);
        Assert.Equal(AnimationTrigger.WithPrevious, auto.Triggers["b1"]);

        Assert.Equal(AnimationSequence.PreviewTimeline(objects, null).Clicks[1], AnimationSequence.PreviewStart("b1", objects, null));
    }

    [Fact]
    public void RemovingAGroupsFirstStepPromotesItsFollower()
    {
        var objects = Objects(
            Object("a", [Step("a1", AnimationTrigger.OnClick), Step("a2", AnimationTrigger.WithPrevious)]),
            Object("b", [Step("b1", AnimationTrigger.OnClick), Step("b2", AnimationTrigger.AfterPrevious)]),
            Object("c", [Step("c1", AnimationTrigger.OnDismiss, AnimationKind.Out), Step("c2", AnimationTrigger.WithPrevious, AnimationKind.Out)]));
        Assert.Equal("a2", AnimationSequence.Promotion("a1", objects, null)?.StepId);
        Assert.Equal(AnimationTrigger.OnClick, AnimationSequence.Promotion("a1", objects, null)?.Trigger);
        Assert.Null(AnimationSequence.Promotion("a2", objects, null)); // not a first step
        Assert.Equal("b2", AnimationSequence.Promotion("b1", objects, null)?.StepId);
        Assert.Equal(AnimationTrigger.OnDismiss, AnimationSequence.Promotion("c1", objects, null)?.Trigger);
        Assert.Null(AnimationSequence.Promotion("b2", objects, null)); // next is a click already
    }

    [Fact]
    public void UpcomingRevealTextReadsTheComingClickGroup()
    {
        var points = Text("p", "One\nTwo\nThree", Enumerable.Range(0, 3).Select(line => new AnimationStep
        {
            Id = $"s{line}", Kind = AnimationKind.In, Animation = StepAnimation.Fade, Trigger = AnimationTrigger.OnClick, DurationSeconds = 0.5,
            Ranges = [Range(line, 0, 3 + (line == 2 ? 2 : 0))],
        }).ToList(), name: "Points");
        var shape = new SlideObject
        {
            Id = "sh", ObjectKind = SlideObjectKind.Shape, Name = "Accent Rule", Text = "",
            AnimationSteps =
            [
                new() { Id = "shape-in", Kind = AnimationKind.In, Animation = StepAnimation.Wipe, Trigger = AnimationTrigger.OnClick, DurationSeconds = 0.5 },
                new() { Id = "shape-out", Kind = AnimationKind.Out, Animation = StepAnimation.Wipe, Trigger = AnimationTrigger.OnClick, DurationSeconds = 0.3 },
            ],
        };
        var objects = Objects(points, shape);
        List<string> order = ["s0", "s1", "s2", "shape-in", "shape-out"];
        Assert.Equal("One", AnimationSequence.UpcomingRevealText(objects, order, 0));
        Assert.Equal("Three", AnimationSequence.UpcomingRevealText(objects, order, 2));
        Assert.Equal("Accent Rule", AnimationSequence.UpcomingRevealText(objects, order, 3)); // a textless shape reads by name
        Assert.Null(AnimationSequence.UpcomingRevealText(objects, order, 4)); // an Out-only click reveals nothing
        Assert.Null(AnimationSequence.UpcomingRevealText(objects, order, 5));
        Assert.Null(AnimationSequence.UpcomingRevealText([], null, 0));
    }
}
