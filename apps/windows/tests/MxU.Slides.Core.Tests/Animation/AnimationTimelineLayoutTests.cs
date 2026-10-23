using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;
using static MxU.Slides.Core.Tests.Animation.AnimationFixtures;

namespace MxU.Slides.Core.Tests.Animation;

// Port of SlideSceneTests/AnimationTimelineLayoutTests.swift.
public class AnimationTimelineLayoutTests
{
    private static List<SlideObject> SeededObjects() =>
    [
        Text("title", "Faith That Moves", [
            Step("t-in", AnimationTrigger.WithPrevious, AnimationKind.In, StepAnimation.Wipe, duration: 0.6),
            Step("t-morph", AnimationTrigger.OnClick, AnimationKind.Morph, duration: 0.8),
            Step("t-out", AnimationTrigger.OnDismiss, AnimationKind.Out, duration: 0.4),
        ], name: "Title"),
        new SlideObject
        {
            Id = "rule", ObjectKind = SlideObjectKind.Shape, Name = "Accent Rule", Text = "",
            AnimationSteps = [Step("r-in", AnimationTrigger.AfterPrevious, AnimationKind.In, StepAnimation.Draw, delay: 0.1, duration: 0.8)],
        },
        Text("p1", "one two three", [
            Step("p1-in", AnimationTrigger.OnClick, AnimationKind.In, StepAnimation.Move, duration: 0.5, ranges: [Range(0, 0, 3)]),
            Step("p1-out", AnimationTrigger.OnDismiss, AnimationKind.Out, duration: 0.4),
        ], name: "Point 1"),
        Text("plain", "hi", name: "No steps"),
        Text("ghosted", "x", [Step("h-in", AnimationTrigger.OnClick)], name: "Hidden", hidden: true),
    ];

    /// <summary>Applies trigger/delay deltas the way the editor writes them back.</summary>
    private static List<SlideObject> Applied(List<SlideObject> objects, IEnumerable<AnimationSequence.StepDelta> deltas)
    {
        var moved = objects.Select(MxuJson.Clone).ToList();
        foreach (var delta in deltas)
        {
            var step = moved[0].AnimationSteps!.First(s => s.Id == delta.StepId);
            step.Trigger = delta.Trigger;
            step.DelaySeconds = delta.DelaySeconds;
        }
        return moved;
    }

    private static AnimationTrigger TriggerOf(List<SlideObject> objects, string id) => objects[0].AnimationSteps!.First(s => s.Id == id).Trigger;

    [Fact]
    public void TimelineIsTheSentenceListDrawnAsGeometry()
    {
        var objects = SeededObjects();
        var timeline = AnimationTimelineLayout.TimelineOf(objects, null);

        var groups = AnimationSequence.Groups(objects, null);
        Assert.Equal(
            [SceneAnimationGroup.Auto, .. Enumerable.Range(0, groups.Click.Count).Select(SceneAnimationGroup.Click), SceneAnimationGroup.Exit],
            timeline.Columns.Select(c => c.Group));

        var preview = AnimationSequence.PreviewTimeline(objects, null);
        Assert.Equal(preview.Clicks, timeline.ClickTimes);
        Assert.Equal(preview.DismissAt, timeline.DismissAt);
        Assert.Equal(preview.Duration, timeline.Duration);
        for (var index = 0; index < preview.Clicks.Count; index++) Assert.Equal(preview.Clicks[index], timeline.Columns[index + 1].Start);
        Assert.Equal(preview.DismissAt, timeline.Columns[^1].Start);

        Assert.Equal(["title", "rule", "p1", "plain"], timeline.Rows.Select(r => r.ObjectId));

        var blockIds = timeline.Rows.SelectMany(r => r.Blocks).Select(b => b.StepId).ToHashSet();
        var entryIds = AnimationSequence.OrderedEntries(objects, null).Select(e => e.Step.Id).ToHashSet();
        Assert.True(blockIds.SetEquals(entryIds));

        var scheduled = AnimationSequence.SceneSteps(objects, null);
        foreach (var row in timeline.Rows)
        {
            foreach (var block in row.Blocks)
            {
                var scene = scheduled[row.ObjectId].First(s => s.Id == block.StepId);
                Assert.Equal(scene.Group, block.Group);
                Assert.Equal(scene.StartOffset, block.StartOffset);
                Assert.Equal(scene.Duration, block.Duration);
            }
        }

        Assert.True(timeline.Rows[1].Blocks[0].Linked);
        Assert.True(timeline.Rows[2].Blocks[0].Ranged);
        Assert.False(timeline.Rows[0].Blocks[0].Linked);
    }

    [Fact]
    public void PresenceBarsFollowTheDefaultVerbTargets()
    {
        var objects = SeededObjects();
        var timeline = AnimationTimelineLayout.TimelineOf(objects, null);
        var preview = AnimationSequence.PreviewTimeline(objects, null);

        var title = timeline.Rows[0];
        Assert.Equal(0, title.PresenceStart);
        Assert.Equal(preview.DismissAt + 0.4, title.PresenceEnd);

        var p1 = timeline.Rows[2];
        Assert.Null(p1.PresenceStart); // a ranged In never hides the object
        Assert.Equal(preview.DismissAt + 0.4, p1.PresenceEnd);

        var plain = timeline.Rows[3];
        Assert.True(plain.PresenceStart is null && plain.PresenceEnd is null);

        Assert.True(title.Blocks[0].RampOut && !title.Blocks[0].RampIn);
        Assert.True(title.Blocks[1].RampIn && title.Blocks[1].RampOut);
        Assert.All(timeline.Columns, c => Assert.True(c.Duration > 0
            ? c.DisplayDuration == c.Duration
            : c.DisplayDuration >= AnimationTimelineLayout.MinimumColumnSeconds));
    }

    [Fact]
    public void MorphLabelIsBehaviorDriven()
    {
        var o = new SlideObject { Id = "o", ObjectKind = SlideObjectKind.Text, Name = "T", Text = "hi", X = 10, Y = 10, Width = 100, Height = 50 };
        var mover = Step("m", AnimationTrigger.OnClick, AnimationKind.Morph);
        var end = MxuJson.Clone(o);
        end.Y = 200;
        mover.ToObject = end;
        Assert.True(AnimationTimelineLayout.MorphMoves(mover, o)); // a moved end state reads Morph

        var styler = Step("s", AnimationTrigger.OnClick, AnimationKind.Morph);
        var recolored = MxuJson.Clone(o);
        recolored.Fill = new ObjectFill { FillKind = FillKind.Solid, ColorHex = "#FF0000FF" };
        styler.ToObject = recolored;
        Assert.False(AnimationTimelineLayout.MorphMoves(styler, o)); // a style-only end state reads Change

        var tilter = Step("t", AnimationTrigger.OnClick, AnimationKind.Morph);
        var tilted = MxuJson.Clone(o);
        tilted.Tilt = 30;
        tilter.ToObject = tilted;
        Assert.True(AnimationTimelineLayout.MorphMoves(tilter, o));

        Assert.False(AnimationTimelineLayout.MorphMoves(Step("x", AnimationTrigger.OnClick, AnimationKind.Morph), o));
        o.AnimationSteps = null;
        Assert.False(AnimationTimelineLayout.MorphMoves(Step("y", AnimationTrigger.OnClick, AnimationKind.In), o));
    }

    [Fact]
    public void PlacementIsTheMinimalTriggerDelayDelta()
    {
        List<SlideObject> objects = [Text("a", "hi", [
            Step("head", AnimationTrigger.OnClick, duration: 1),
            Step("follow", AnimationTrigger.AfterPrevious, delay: 0.1, duration: 0.5),
        ], name: "A")];

        var head = AnimationSequence.Placement("head", 0.75, objects, null);
        Assert.Equal(AnimationTrigger.OnClick, head?.Trigger);
        Assert.Equal(0.75, head?.DelaySeconds);

        var zero = AnimationSequence.Placement("head", 0, objects, null);
        Assert.True(zero?.Trigger == AnimationTrigger.OnClick && zero?.DelaySeconds is null);

        var follow = AnimationSequence.Placement("follow", 0.4, objects, null);
        Assert.Equal(AnimationTrigger.WithPrevious, follow?.Trigger);
        Assert.Equal(0.4, follow?.DelaySeconds);

        var moved = objects.Select(MxuJson.Clone).ToList();
        moved[0].AnimationSteps![0].DelaySeconds = 0.75;
        var overlap = AnimationSequence.Placement("follow", 0.5, moved, null);
        Assert.Equal(AnimationTrigger.WithPrevious, overlap?.Trigger);
        Assert.Equal(-0.25, overlap?.DelaySeconds);
    }

    [Fact]
    public void LinkToggleNeverMovesTheBlock()
    {
        List<SlideObject> objects = [Text("a", "hi", [
            Step("head", AnimationTrigger.OnClick, duration: 1),
            Step("follow", AnimationTrigger.AfterPrevious, delay: 0.2, duration: 0.5),
        ], name: "A")];
        var before = AnimationSequence.Schedule(objects, null);

        var unlink = AnimationSequence.LinkToggle("follow", objects, null)!.Value;
        Assert.Equal(AnimationTrigger.WithPrevious, unlink.Trigger);
        Assert.Equal(1.2, unlink.DelaySeconds);
        var unlinked = Applied(objects, [new("follow", unlink.Trigger, unlink.DelaySeconds)]);
        var after = AnimationSequence.Schedule(unlinked, null);
        Assert.Equal(before["follow"].Start, after["follow"].Start); // the block never visually moves

        var relink = AnimationSequence.LinkToggle("follow", unlinked, null);
        Assert.Equal(AnimationTrigger.AfterPrevious, relink?.Trigger);
        Assert.Equal(0.2, relink!.Value.DelaySeconds!.Value, 1e-9);

        Assert.Null(AnimationSequence.LinkToggle("head", objects, null));
    }

    [Fact]
    public void MovingOneCardNeverShovesTheRest()
    {
        List<SlideObject> objects = [Text("a", "hi", [
            Step("head", AnimationTrigger.OnClick, duration: 1),
            Step("follow", AnimationTrigger.AfterPrevious, delay: 0.1, duration: 0.5),
            Step("tail", AnimationTrigger.WithPrevious, delay: 2, duration: 0.3),
        ], name: "A")];
        var before = AnimationSequence.Schedule(objects, null);
        Assert.Equal(1.1, before["follow"].Start);

        var head = AnimationSequence.Placement("head", 0.75, objects, null)!.Value;
        var moved = Applied(objects, [new("head", head.Trigger, head.DelaySeconds)]);
        moved = Applied(moved, AnimationSequence.Pins(moved, null, before, new HashSet<string> { "head" }));
        var after = AnimationSequence.Schedule(moved, null);
        Assert.Equal(0.75, after["head"].Start);
        Assert.Equal(before["follow"].Start, after["follow"].Start); // the chained follower stays put
        Assert.Equal(before["tail"].Start, after["tail"].Start);

        Assert.Equal(AnimationTrigger.AfterPrevious, moved[0].AnimationSteps![1].Trigger);
        Assert.Equal(AnimationTrigger.WithPrevious, moved[0].AnimationSteps![2].Trigger);
    }

    [Fact]
    public void GroupMoveStaysRigidAcrossUnselectedAnchors()
    {
        List<SlideObject> objects = [Text("a", "hi", [
            Step("head", AnimationTrigger.OnClick, delay: 0.35, duration: 0.3),
            Step("b", AnimationTrigger.WithPrevious, delay: -0.2, duration: 0.3),
            Step("c", AnimationTrigger.AfterPrevious, delay: -0.2, duration: 0.35),
            Step("d", AnimationTrigger.WithPrevious, delay: -0.05, duration: 0.3),
        ], name: "A")];
        var before = AnimationSequence.Schedule(objects, null);
        HashSet<string> selected = ["head", "b", "d"];
        const double delta = 0.3;

        var targets = before.ToDictionary(kv => kv.Key, kv => selected.Contains(kv.Key) ? kv.Value.Start + delta : kv.Value.Start);
        var moved = Applied(objects, AnimationSequence.Pins(objects, null, targets));
        var after = AnimationSequence.Schedule(moved, null);
        foreach (var id in selected) Assert.Equal(delta, after[id].Start - before[id].Start, 1e-9); // shifts by exactly the drag delta
        Assert.Equal(before["c"].Start, after["c"].Start, 1e-9); // the unselected step plants

        Assert.Equal(
            [AnimationTrigger.OnClick, AnimationTrigger.WithPrevious, AnimationTrigger.AfterPrevious, AnimationTrigger.WithPrevious],
            moved[0].AnimationSteps!.Select(s => s.Trigger));
    }

    [Fact]
    public void RehomedClickLandsAfterTheTargetsContentAndDisappears()
    {
        List<SlideObject> objects = [Text("a", "hi", [
            Step("a1", AnimationTrigger.WithPrevious, duration: 0.45),
            Step("a2", AnimationTrigger.WithPrevious, delay: 0.4, duration: 0.4),
            Step("head", AnimationTrigger.OnClick, delay: 0.35, duration: 0.3),
            Step("follow", AnimationTrigger.WithPrevious, delay: -0.2, duration: 0.3),
        ], name: "A")];
        var move = AnimationSequence.RehomeGroup(AnimationSequence.GroupSlot.Click(0), AnimationSequence.GroupSlot.Auto, objects, null);
        Assert.NotNull(move);
        var moved = Applied(objects, move.Deltas);
        var groups = AnimationSequence.Groups(moved, move.Order);
        Assert.Empty(groups.Click); // the vacated click disappears
        Assert.Equal(["a1", "a2", "head", "follow"], groups.Auto.Select(e => e.Step.Id));
        var after = AnimationSequence.Schedule(moved, move.Order);
        Assert.Equal(0.8, after["head"].Start, 1e-9);
        Assert.Equal(0.6, after["follow"].Start, 1e-9);
        Assert.Equal(AnimationTrigger.WithPrevious, TriggerOf(moved, "head"));
        Assert.Equal(AnimationTrigger.WithPrevious, TriggerOf(moved, "follow"));
    }

    [Fact]
    public void RehomedClickBecomesTheExitGroup()
    {
        List<SlideObject> objects = [Text("a", "hi", [
            Step("head", AnimationTrigger.OnClick, delay: 0.35, duration: 0.3),
            Step("follow", AnimationTrigger.WithPrevious, delay: 0.15, duration: 0.3),
        ], name: "A")];
        var move = AnimationSequence.RehomeGroup(AnimationSequence.GroupSlot.Click(0), AnimationSequence.GroupSlot.Exit, objects, null);
        Assert.NotNull(move);
        var moved = Applied(objects, move.Deltas);
        var groups = AnimationSequence.Groups(moved, move.Order);
        Assert.Empty(groups.Click);
        Assert.Equal(["head", "follow"], groups.Exit.Select(e => e.Step.Id));
        Assert.Equal(AnimationTrigger.OnDismiss, TriggerOf(moved, "head"));
        Assert.Equal(0.15, AnimationSequence.Schedule(moved, move.Order)["follow"].Start, 1e-9); // internal timing survives
    }

    [Fact]
    public void RehomeRefusesEmptyOrSameColumn()
    {
        List<SlideObject> objects = [Text("a", "hi", [Step("head", AnimationTrigger.OnClick, duration: 0.3)], name: "A")];
        Assert.Null(AnimationSequence.RehomeGroup(AnimationSequence.GroupSlot.Click(0), AnimationSequence.GroupSlot.Click(0), objects, null));
        Assert.Null(AnimationSequence.RehomeGroup(AnimationSequence.GroupSlot.Auto, AnimationSequence.GroupSlot.Exit, objects, null));
        Assert.Null(AnimationSequence.RehomeGroup(AnimationSequence.GroupSlot.Click(3), AnimationSequence.GroupSlot.Auto, objects, null));
    }

    [Fact]
    public void SetMovesToAnotherClickKeepingItsSpacing()
    {
        List<SlideObject> objects = [Text("a", "hi", [
            Step("h1", AnimationTrigger.OnClick, delay: 0.3, duration: 0.3),
            Step("f1", AnimationTrigger.WithPrevious, delay: 0.1, duration: 0.3),
            Step("g1", AnimationTrigger.WithPrevious, delay: 0.1, duration: 0.3),
            Step("h2", AnimationTrigger.OnClick, delay: 0.2, duration: 0.3),
        ], name: "A")];
        var move = AnimationSequence.RehomeSteps(new HashSet<string> { "f1", "g1" }, AnimationSequence.GroupSlot.Click(1), 0.5, objects, null);
        Assert.NotNull(move);
        var moved = Applied(objects, move.Deltas);
        var groups = AnimationSequence.Groups(moved, move.Order);
        Assert.Equal(2, groups.Click.Count);
        Assert.Equal(["h1"], groups.Click[0].Select(e => e.Step.Id));
        Assert.Equal(["h2", "f1", "g1"], groups.Click[1].Select(e => e.Step.Id));
        var after = AnimationSequence.Schedule(moved, move.Order);
        Assert.Equal(0.5, after["f1"].Start, 1e-9); // the packet's first lands at the drop
        Assert.Equal(0.6, after["g1"].Start, 1e-9); // internal spacing survives
        Assert.Equal(0.3, after["h1"].Start, 1e-9); // the vacated column stays planted
        Assert.Equal(0.2, after["h2"].Start, 1e-9);
    }

    [Fact]
    public void SetPastTheEdgeBecomesANewFinalClick()
    {
        List<SlideObject> objects = [Text("a", "hi", [
            Step("h1", AnimationTrigger.OnClick, delay: 0.35, duration: 0.3),
            Step("f1", AnimationTrigger.WithPrevious, delay: 0.15, duration: 0.3),
            Step("h2", AnimationTrigger.OnClick, duration: 0.3),
        ], name: "A")];
        var move = AnimationSequence.RehomeSteps(new HashSet<string> { "h1", "f1" }, AnimationSequence.GroupSlot.Click(2), 0, objects, null);
        Assert.NotNull(move);
        var moved = Applied(objects, move.Deltas);
        var groups = AnimationSequence.Groups(moved, move.Order);
        Assert.Equal(2, groups.Click.Count); // the vacated click disappears; the packet is the new final click
        Assert.Equal(["h2"], groups.Click[0].Select(e => e.Step.Id));
        Assert.Equal(["h1", "f1"], groups.Click[1].Select(e => e.Step.Id));
        var after = AnimationSequence.Schedule(moved, move.Order);
        Assert.Equal(0, after["h1"].Start, 1e-9);
        Assert.Equal(0.15, after["f1"].Start, 1e-9); // internal spacing survives
        Assert.Equal(AnimationTrigger.OnClick, TriggerOf(moved, "h1"));
    }

    [Fact]
    public void DisplacedHeadDemotesWhenTheSetLandsFirst()
    {
        List<SlideObject> objects = [Text("a", "hi", [
            Step("x", AnimationTrigger.WithPrevious, duration: 0.3),
            Step("h1", AnimationTrigger.OnClick, delay: 0.5, duration: 0.3),
        ], name: "A")];
        var move = AnimationSequence.RehomeSteps(new HashSet<string> { "x" }, AnimationSequence.GroupSlot.Click(0), 0, objects, null);
        Assert.NotNull(move);
        var moved = Applied(objects, move.Deltas);
        var groups = AnimationSequence.Groups(moved, move.Order);
        Assert.Single(groups.Click); // landing first hands the group trigger over, never splits the click
        Assert.Equal(["x", "h1"], groups.Click[0].Select(e => e.Step.Id));
        Assert.Equal(AnimationTrigger.OnClick, TriggerOf(moved, "x"));
        Assert.Equal(AnimationTrigger.WithPrevious, TriggerOf(moved, "h1"));
        var after = AnimationSequence.Schedule(moved, move.Order);
        Assert.Equal(0, after["x"].Start, 1e-9);
        Assert.Equal(0.5, after["h1"].Start, 1e-9); // the displaced head keeps its spot
    }

    [Fact]
    public void SetLeavingItsHeadPromotesTheSurvivors()
    {
        List<SlideObject> objects = [Text("a", "hi", [
            Step("h", AnimationTrigger.OnClick, delay: 0.3, duration: 0.3),
            Step("f", AnimationTrigger.WithPrevious, delay: 0.1, duration: 0.3),
            Step("g", AnimationTrigger.WithPrevious, delay: 0.1, duration: 0.3),
        ], name: "A")];
        var move = AnimationSequence.RehomeSteps(new HashSet<string> { "h", "f" }, AnimationSequence.GroupSlot.Exit, 0.2, objects, null);
        Assert.NotNull(move);
        var moved = Applied(objects, move.Deltas);
        var groups = AnimationSequence.Groups(moved, move.Order);
        Assert.Single(groups.Click); // the survivor keeps the click alive
        Assert.Equal(["g"], groups.Click[0].Select(e => e.Step.Id));
        Assert.Equal(["h", "f"], groups.Exit.Select(e => e.Step.Id));
        Assert.Equal(AnimationTrigger.OnClick, TriggerOf(moved, "g"));
        Assert.Equal(AnimationTrigger.OnDismiss, TriggerOf(moved, "h"));
        var after = AnimationSequence.Schedule(moved, move.Order);
        Assert.Equal(0.5, after["g"].Start, 1e-9); // the survivor keeps its spot
        Assert.Equal(0.2, after["h"].Start, 1e-9);
        Assert.Equal(0.3, after["f"].Start, 1e-9); // packet spacing survives
    }

    [Fact]
    public void SetDroppedOnADividerBirthsAClickBetween()
    {
        List<SlideObject> objects = [Text("a", "hi", [
            Step("h1", AnimationTrigger.OnClick, delay: 0.3, duration: 0.3),
            Step("f1", AnimationTrigger.WithPrevious, delay: 0.1, duration: 0.3),
            Step("h2", AnimationTrigger.OnClick, delay: 0.2, duration: 0.3),
        ], name: "A")];
        var move = AnimationSequence.RehomeSteps(new HashSet<string> { "f1" }, AnimationSequence.GroupSlot.NewClick(1), 0, objects, null);
        Assert.NotNull(move);
        var moved = Applied(objects, move.Deltas);
        var groups = AnimationSequence.Groups(moved, move.Order);
        Assert.Equal(3, groups.Click.Count); // a brand-new click lands between the neighbors
        Assert.Equal(["h1"], groups.Click[0].Select(e => e.Step.Id));
        Assert.Equal(["f1"], groups.Click[1].Select(e => e.Step.Id));
        Assert.Equal(["h2"], groups.Click[2].Select(e => e.Step.Id));
        Assert.Equal(AnimationTrigger.OnClick, TriggerOf(moved, "f1"));
        var after = AnimationSequence.Schedule(moved, move.Order);
        Assert.Equal(0, after["f1"].Start, 1e-9); // the new click's head starts at its instant
        Assert.Equal(0.3, after["h1"].Start, 1e-9); // the neighbors keep their spots
        Assert.Equal(0.2, after["h2"].Start, 1e-9);
    }

    [Fact]
    public void PinPreservesTheTriggerItRexpresses()
    {
        List<SlideObject> objects = [Text("a", "hi", [
            Step("head", AnimationTrigger.OnClick, duration: 1),
            Step("after", AnimationTrigger.AfterPrevious, duration: 0.5),
        ], name: "A")];
        var pinned = AnimationSequence.Pin("after", 1.4, objects, null);
        Assert.Equal(AnimationTrigger.AfterPrevious, pinned?.Trigger);
        Assert.Equal(0.4, pinned!.Value.DelaySeconds!.Value, 1e-9);

        var headPin = AnimationSequence.Pin("head", -0.5, objects, null);
        Assert.Equal(AnimationTrigger.OnClick, headPin?.Trigger);
        Assert.Null(headPin?.DelaySeconds); // clamped to 0 = the absent default
    }
}
