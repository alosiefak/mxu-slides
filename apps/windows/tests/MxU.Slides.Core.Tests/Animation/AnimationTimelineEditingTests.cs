using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Model;
using static MxU.Slides.Core.Tests.Animation.AnimationFixtures;
using Slot = MxU.Slides.Core.Animation.AnimationSequence.GroupSlot;

namespace MxU.Slides.Core.Tests.Animation;

// The Mac has no tests for its timeline gestures (SlideEditorModel+Animations is app code); these
// pin down what was ported, using the same AnimationSequence rules the outputs play by.
public class AnimationTimelineEditingTests
{
    private static Overlay Composition(params SlideObject[] objects) => new() { Id = "c", Name = "C", Objects = [.. objects] };

    private static double StartOf(IAnimatedComposition c, string id) => AnimationSequence.Schedule(c.Objects, c.AnimationOrder)[id].Start;

    private static AnimationStep StepOf(IAnimatedComposition c, string id) => AnimationEditing.Find(c, id)!.Value.Step;

    [Fact]
    public void TimesSnapToATwentieth()
    {
        Assert.Equal(0.35, AnimationTimelineEditing.Snap(0.3612));
        Assert.Equal(1.05, AnimationTimelineEditing.Snap(1.025));
    }

    [Fact]
    public void DraggingABlockMovesItAndLeavesTheRestWhereTheyWere()
    {
        var c = Composition(
            Text("a", "A", [Step("a1", AnimationTrigger.WithPrevious, duration: 0.5)]),
            Text("b", "B", [Step("b1", AnimationTrigger.AfterPrevious, duration: 0.5)]),
            Text("d", "D", [Step("d1", AnimationTrigger.AfterPrevious, duration: 0.5)]));
        Assert.Equal((0.5, 1.0), (StartOf(c, "b1"), StartOf(c, "d1")));

        Assert.True(AnimationTimelineEditing.PlaceStep(c, "b1", 0.2));
        Assert.Equal(0.2, StartOf(c, "b1"), 6);
        Assert.Equal(1.0, StartOf(c, "d1"), 6); // pinned: moving one block never shoves the rest
        Assert.Equal(0, StartOf(c, "a1"));
    }

    [Fact]
    public void StretchingABlockKeepsTheOthersPlaced()
    {
        var c = Composition(
            Text("a", "A", [Step("a1", AnimationTrigger.WithPrevious, duration: 0.5)]),
            Text("b", "B", [Step("b1", AnimationTrigger.AfterPrevious, duration: 0.5)]));
        Assert.True(AnimationTimelineEditing.ResizeStep(c, "a1", 1.02));
        Assert.Equal(1.0, StepOf(c, "a1").DurationSeconds);
        Assert.Equal(0.5, StartOf(c, "b1"), 6);
        AnimationTimelineEditing.ResizeStep(c, "a1", 0);
        Assert.Equal(0.05, StepOf(c, "a1").DurationSeconds); // never shorter than a twentieth
    }

    [Fact]
    public void TrimmingTheLeftEdgeStartsLaterAndEndsWhereItDid()
    {
        var c = Composition(Text("a", "A", [Step("a1", AnimationTrigger.WithPrevious, duration: 1)]));
        Assert.True(AnimationTimelineEditing.TrimStepStart(c, "a1", 0.4));
        Assert.Equal(0.4, StartOf(c, "a1"), 6);
        Assert.Equal(0.6, StepOf(c, "a1").DurationSeconds, 6);
        AnimationTimelineEditing.TrimStepStart(c, "a1", -2);
        Assert.Equal(0, StartOf(c, "a1"), 6); // a column's first step can't start before the column
        Assert.Equal(1.0, StepOf(c, "a1").DurationSeconds, 6);
    }

    [Fact]
    public void DraggingSeveralBlocksMovesThemTogether()
    {
        var c = Composition(
            Text("a", "A", [Step("a1", AnimationTrigger.WithPrevious, duration: 0.5)]),
            Text("b", "B", [Step("b1", AnimationTrigger.WithPrevious, delay: 0.5, duration: 0.5)]),
            Text("d", "D", [Step("d1", AnimationTrigger.WithPrevious, delay: 0.5, duration: 0.5)]));
        Assert.True(AnimationTimelineEditing.MoveSteps(c, new HashSet<string> { "b1", "d1" }, 0.3));
        Assert.Equal((0.8, 1.3), (Math.Round(StartOf(c, "b1"), 6), Math.Round(StartOf(c, "d1"), 6)));
        Assert.True(AnimationTimelineEditing.MoveSteps(c, new HashSet<string> { "b1", "d1" }, -5));
        Assert.Equal(0, StartOf(c, "b1"), 6); // stopped at the column's start
        Assert.False(AnimationTimelineEditing.MoveSteps(c, new HashSet<string>(), 1));
    }

    [Fact]
    public void DroppingABlockInAnotherColumnPlaysItThere()
    {
        var c = Composition(
            Text("a", "A", [Step("a1", AnimationTrigger.WithPrevious)]),
            Text("b", "B", [Step("b1", AnimationTrigger.OnClick)]));
        Assert.True(AnimationTimelineEditing.MoveStepInto(c, "a1", Slot.Click(0), 0.5));
        var groups = AnimationSequence.Groups(c.Objects, c.AnimationOrder);
        Assert.Empty(groups.Auto);
        Assert.Equal(["b1", "a1"], groups.Click[0].Select(e => e.Step.Id));
        Assert.Equal(0.5, StartOf(c, "a1"), 6);

        Assert.True(AnimationTimelineEditing.MoveStepInto(c, "a1", Slot.Click(7), 0)); // past the last click: a new one
        Assert.Equal(2, AnimationSequence.Groups(c.Objects, c.AnimationOrder).Click.Count);
    }

    [Fact]
    public void TheLinkFlipsWithAndAfterWithoutMoving()
    {
        var c = Composition(
            Text("a", "A", [Step("a1", AnimationTrigger.WithPrevious, duration: 1)]),
            Text("b", "B", [Step("b1", AnimationTrigger.WithPrevious, delay: 0.25, duration: 1)]));
        Assert.True(AnimationTimelineEditing.ToggleLink(c, "b1"));
        Assert.Equal(AnimationTrigger.AfterPrevious, StepOf(c, "b1").Trigger);
        Assert.Equal(-0.75, StepOf(c, "b1").DelaySeconds!.Value, 6);
        Assert.Equal(0.25, StartOf(c, "b1"), 6);
        Assert.False(AnimationTimelineEditing.ToggleLink(c, "a1")); // nothing before it to link to
    }

    [Fact]
    public void ACopyOfAStepPlaysJustAfterIt()
    {
        var c = Composition(Text("a", "A", [Step("a1", AnimationTrigger.WithPrevious), Step("a2", AnimationTrigger.OnClick)]));
        var copy = AnimationTimelineEditing.DuplicateStep(c, "a1")!;
        Assert.Equal(["a1", copy, "a2"], c.AnimationOrder);
        Assert.Equal(["a1", copy, "a2"], c.Objects[0].AnimationSteps!.Select(s => s.Id));
        Assert.Null(AnimationTimelineEditing.DuplicateStep(c, "missing"));
    }

    [Fact]
    public void ColumnsAreDeletedAndMovedWhole()
    {
        var c = Composition(
            Text("a", "A", [Step("a1", AnimationTrigger.OnClick)]),
            Text("b", "B", [Step("b1", AnimationTrigger.OnClick)]),
            Text("d", "D", [Step("d1", AnimationTrigger.WithPrevious)]));
        Assert.True(AnimationTimelineEditing.MoveColumn(c, Slot.Click(1), Slot.NewClick(0)));
        Assert.Equal(["b1", "d1"], AnimationSequence.Groups(c.Objects, c.AnimationOrder).Click[0].Select(e => e.Step.Id));
        Assert.True(AnimationTimelineEditing.DeleteColumn(c, Slot.Click(0)));
        Assert.Null(c.Objects[1].AnimationSteps);
        Assert.Equal(["a1"], AnimationSequence.Groups(c.Objects, c.AnimationOrder).Click.Single().Select(e => e.Step.Id));
        Assert.False(AnimationTimelineEditing.DeleteColumn(c, Slot.Exit)); // nothing there
    }

    [Fact]
    public void AMorphStartsFromTheObjectsLookAfterItsEarlierMorphs()
    {
        var box = new SlideObject { Id = "box", ObjectKind = SlideObjectKind.Shape, Name = "Box", Text = "", X = 0, Y = 0, Width = 100, Height = 100 };
        var c = Composition(box);
        var first = AnimationTimelineEditing.AddMorph(c, ["box"]).Single();
        var step = StepOf(c, first);
        Assert.Equal((AnimationKind.Morph, AnimationTrigger.OnClick, 1.0), (step.Kind, step.Trigger, step.DurationSeconds));
        Assert.Equal(0, step.ToObject!.X); // nothing changes until it's posed
        Assert.Null(step.ToObject.AnimationSteps);

        // Pose it: the canvas edits the end state through the posed objects.
        var posed = AnimationTimelineEditing.PosedObjects(c, first)!;
        Assert.Same(step.ToObject, posed[0]);
        Assert.Equal("box", posed[0].Id); // the canvas picks it by the object's id
        posed[0].X = 500;
        Assert.Equal(500, StepOf(c, first).ToObject!.X);
        Assert.Equal(0, box.X); // the object itself rests where it was

        var second = AnimationTimelineEditing.AddMorph(c, ["box"]).Single();
        Assert.Equal(500, StepOf(c, second).ToObject!.X); // the next morph starts where the first ended
        Assert.Equal(500, AnimationTimelineEditing.MorphBaseline(c, "box", second)!.X);
        Assert.Equal(0, AnimationTimelineEditing.MorphBaseline(c, "box", first)!.X);

        Assert.True(AnimationTimelineEditing.ResetPose(c, second));
        Assert.Equal(500, StepOf(c, second).ToObject!.X); // reset: back to how it looks before the step
    }

    [Fact]
    public void AnInCanEnterFromWhereYouPlaceIt()
    {
        var box = new SlideObject
        {
            Id = "box", ObjectKind = SlideObjectKind.Shape, Name = "Box", Text = "", X = 100, Y = 100, Width = 100, Height = 100,
            AnimationSteps = [Step("in", AnimationTrigger.OnClick, animation: StepAnimation.Move)],
        };
        var c = Composition(box);
        Assert.Null(AnimationTimelineEditing.Pose(c, "in"));
        Assert.True(AnimationTimelineEditing.SetCustomStart(c, "in", true));
        var start = AnimationTimelineEditing.Pose(c, "in")!;
        Assert.Equal(100, start.X);
        AnimationTimelineEditing.PosedObjects(c, "in")![0].X = -300; // parked off the left edge
        Assert.Equal(-300, StepOf(c, "in").FromObject!.X);
        Assert.Equal("Arrives from its custom start", AnimationEditing.Sentence(StepOf(c, "in")));

        Assert.True(AnimationTimelineEditing.StartFromEdge(c, "in", AnimationEdge.Top));
        Assert.Null(StepOf(c, "in").FromObject);
        Assert.Equal(AnimationEdge.Top, StepOf(c, "in").Edge);
        Assert.False(AnimationTimelineEditing.SetCustomStart(c, "missing", true));
    }
}
