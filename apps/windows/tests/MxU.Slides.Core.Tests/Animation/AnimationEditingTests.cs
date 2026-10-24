using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Model;
using static MxU.Slides.Core.Tests.Animation.AnimationFixtures;
using Slot = MxU.Slides.Core.Animation.AnimationSequence.GroupSlot;

namespace MxU.Slides.Core.Tests.Animation;

// The Mac has no tests for SlideEditorModel+Animations (it lives in the app target); these pin
// down the behaviour ported from it, using the same AnimationSequence rules the outputs play by.
public class AnimationEditingTests
{
    private static Overlay Composition(params SlideObject[] objects) => new() { Id = "ov", Name = "Overlay", Objects = [.. objects] };

    private static AnimationTrigger TriggerOf(IAnimatedComposition c, string stepId) => AnimationEditing.Find(c, stepId)!.Value.Step.Trigger;

    [Fact]
    public void FreshStepsStartWithTheMacsSettings()
    {
        var slide = AnimationEditing.FreshStep(AnimationKind.In, StepAnimation.Move, AnimationTrigger.OnClick);
        Assert.Equal(0.5, slide.DurationSeconds);
        Assert.Equal(AnimationEdge.Left, slide.Edge);
        Assert.Equal(0.95, AnimationEditing.FreshStep(AnimationKind.Out, StepAnimation.Scale, AnimationTrigger.OnDismiss).FromScale);
        Assert.Equal(AnimationEdge.Left, AnimationEditing.FreshStep(AnimationKind.In, StepAnimation.Wipe, AnimationTrigger.OnClick).Edge);
        var nudge = AnimationEditing.FreshStep(AnimationKind.Emphasis, StepAnimation.Move, AnimationTrigger.OnClick);
        Assert.Null(nudge.Edge);
        Assert.Equal((0.0, -20.0), (nudge.OffsetX, nudge.OffsetY));
        Assert.Null(AnimationEditing.FreshStep(AnimationKind.Emphasis, StepAnimation.Scale, AnimationTrigger.OnClick).FromScale);
        Assert.Equal(1.0, AnimationEditing.FreshStep(AnimationKind.Morph, StepAnimation.Fade, AnimationTrigger.OnClick).DurationSeconds);
        Assert.NotEqual(slide.Id, AnimationEditing.FreshStep(AnimationKind.In, StepAnimation.Move, AnimationTrigger.OnClick).Id);
    }

    [Fact]
    public void AddingToSeveralObjectsPlaysThemTogetherInDocumentOrder()
    {
        var c = Composition(Text("a", "A"), Text("b", "B"), Text("c", "C"));
        var ids = AnimationEditing.AddStep(c, ["c", "a"], AnimationKind.In, StepAnimation.Fade, AnimationTrigger.OnClick);
        Assert.Equal(2, ids.Count);
        Assert.Equal(ids[0], c.Objects[0].AnimationSteps!.Single().Id); // a comes first on the canvas
        Assert.Equal(AnimationTrigger.OnClick, TriggerOf(c, ids[0]));
        Assert.Equal(AnimationTrigger.WithPrevious, TriggerOf(c, ids[1]));
        Assert.Null(c.Objects[1].AnimationSteps);
        Assert.Single(AnimationSequence.Groups(c.Objects, c.AnimationOrder).Click); // one click brings both

        var chained = AnimationEditing.AddStep(c, ["a", "b"], AnimationKind.Out, StepAnimation.Fade, AnimationTrigger.AfterPrevious);
        Assert.All(chained, id => Assert.Equal(AnimationTrigger.AfterPrevious, TriggerOf(c, id))); // a chaining trigger is kept for all
    }

    [Fact]
    public void NewStepsJoinAnExplicitOrderAtTheEnd()
    {
        var c = Composition(Text("a", "A", [Step("a1", AnimationTrigger.WithPrevious)]), Text("b", "B"));
        AnimationEditing.AddStep(c, ["b"], AnimationKind.In, StepAnimation.Fade, AnimationTrigger.OnClick);
        Assert.Null(c.AnimationOrder); // document order stays implicit
        c.AnimationOrder = ["a1"];
        var added = AnimationEditing.AddStep(c, ["a"], AnimationKind.Out, StepAnimation.Fade, AnimationTrigger.OnDismiss);
        Assert.Equal(["a1", added[0]], c.AnimationOrder);
    }

    [Fact]
    public void QuickAddUsesClicksAndTheExitOrTheGroupYouChose()
    {
        var c = Composition(Text("a", "A"), Text("b", "B"));
        var inId = AnimationEditing.QuickAdd(c, ["a"], AnimationKind.In, StepAnimation.Fade).Single();
        Assert.Equal(AnimationTrigger.OnClick, TriggerOf(c, inId));
        var outId = AnimationEditing.QuickAdd(c, ["a"], AnimationKind.Out, StepAnimation.Fade).Single();
        Assert.Equal(AnimationTrigger.OnDismiss, TriggerOf(c, outId));
        Assert.Empty(AnimationEditing.QuickAdd(c, ["a"], AnimationKind.Morph, StepAnimation.Fade)); // morphs need the canvas

        // Into "when it goes up": the first leads with With, later ones join it.
        var overlay = Composition(Text("a", "A"), Text("b", "B"), Text("c", "C"));
        var first = AnimationEditing.QuickAdd(overlay, ["c"], AnimationKind.In, StepAnimation.Fade, Slot.Auto).Single();
        var exit = AnimationEditing.QuickAdd(overlay, ["c"], AnimationKind.Out, StepAnimation.Fade, Slot.Exit).Single();
        var second = AnimationEditing.QuickAdd(overlay, ["a"], AnimationKind.In, StepAnimation.Move, Slot.Auto).Single();
        var groups = AnimationSequence.Groups(overlay.Objects, overlay.AnimationOrder);
        Assert.Equal([first, second], groups.Auto.Select(e => e.Step.Id));
        Assert.Equal([exit], groups.Exit.Select(e => e.Step.Id));
        Assert.Empty(groups.Click);
        Assert.Equal(AnimationTrigger.WithPrevious, TriggerOf(overlay, first));
        Assert.Equal(AnimationTrigger.WithPrevious, TriggerOf(overlay, second));
        Assert.Equal(AnimationTrigger.OnDismiss, TriggerOf(overlay, exit));
    }

    [Fact]
    public void RemovingALeadingStepHandsItsTriggerOn()
    {
        var c = Composition(
            Text("a", "A", [Step("a1", AnimationTrigger.OnClick)]),
            Text("b", "B", [Step("b1", AnimationTrigger.AfterPrevious)]));
        Assert.True(AnimationEditing.Remove(c, "a1"));
        Assert.Null(c.Objects[0].AnimationSteps); // the last step goes, and so does the list
        Assert.Equal(AnimationTrigger.OnClick, TriggerOf(c, "b1")); // b still waits for the click
        Assert.False(AnimationEditing.Remove(c, "a1"));

        c.AnimationOrder = ["b1"];
        AnimationEditing.Remove(c, "b1");
        Assert.Null(c.AnimationOrder);
    }

    [Fact]
    public void ReorderingAndMovingBetweenGroups()
    {
        var c = Composition(
            Text("a", "A", [Step("a1", AnimationTrigger.WithPrevious), Step("a2", AnimationTrigger.OnDismiss, AnimationKind.Out)]),
            Text("b", "B", [Step("b1", AnimationTrigger.AfterPrevious), Step("b2", AnimationTrigger.WithPrevious, AnimationKind.Out)]));
        // Document order: a1, a2, b1, b2 — so b1 (After) trails the exit.
        Assert.Equal(["a2", "b1", "b2"], AnimationSequence.Groups(c.Objects, c.AnimationOrder).Exit.Select(e => e.Step.Id));

        Assert.True(AnimationEditing.MoveBefore(c, "b1", "a2"));
        Assert.Equal(["a1", "b1", "a2", "b2"], c.AnimationOrder);
        Assert.False(AnimationEditing.MoveBefore(c, "b1", "a2")); // already there
        Assert.True(AnimationEditing.MoveBefore(c, "a1", null));
        Assert.Equal(["b1", "a2", "b2", "a1"], c.AnimationOrder);

        // Into the exit at the front: it takes On Exit and the old leader joins it.
        Assert.True(AnimationEditing.MoveIntoGroup(c, "a1", Slot.Exit, 0));
        var groups = AnimationSequence.Groups(c.Objects, c.AnimationOrder);
        Assert.Equal(["b1"], groups.Auto.Select(e => e.Step.Id));
        Assert.Equal(["a1", "a2", "b2"], groups.Exit.Select(e => e.Step.Id));
        Assert.Equal(AnimationTrigger.OnDismiss, TriggerOf(c, "a1"));
        Assert.Equal(AnimationTrigger.WithPrevious, TriggerOf(c, "a2"));
        Assert.False(AnimationEditing.MoveIntoGroup(c, "missing", Slot.Auto, 0));
    }

    [Fact]
    public void ReplacingAnEffectKeepsTimingAndDropsOldSettings()
    {
        var wipe = Step("s", AnimationTrigger.AfterPrevious, AnimationKind.In, StepAnimation.Wipe, delay: 0.3, duration: 0.8);
        wipe.SoftEdge = 40;
        wipe.Edge = AnimationEdge.Bottom;
        wipe.FromObject = new SlideObject { Id = "pose", ObjectKind = SlideObjectKind.Shape, Name = "", Text = "" };
        var c = Composition(Text("a", "A", [wipe]));
        Assert.True(AnimationEditing.ReplaceEffect(c, "s", AnimationKind.Out, StepAnimation.Scale));
        Assert.Equal((AnimationKind.Out, StepAnimation.Scale), (wipe.Kind, wipe.Animation));
        Assert.Equal((AnimationTrigger.AfterPrevious, 0.3, 0.8), (wipe.Trigger, wipe.DelaySeconds, wipe.DurationSeconds));
        Assert.Null(wipe.SoftEdge);
        Assert.Null(wipe.Edge);
        Assert.Equal(0.95, wipe.FromScale);
        Assert.Null(wipe.FromObject); // a custom start only belongs to an In
        Assert.False(AnimationEditing.ReplaceEffect(c, "s", AnimationKind.Morph, StepAnimation.Fade));
    }

    [Fact]
    public void SentencesAndTimingLabels()
    {
        var slideIn = AnimationEditing.FreshStep(AnimationKind.In, StepAnimation.Move, AnimationTrigger.OnClick);
        Assert.Equal("Slides in from left", AnimationEditing.Sentence(slideIn));
        slideIn.Edge = null;
        Assert.Equal("Slides in", AnimationEditing.Sentence(slideIn));
        Assert.Equal("Wipes out to bottom", AnimationEditing.Sentence(new AnimationStep { Kind = AnimationKind.Out, Animation = StepAnimation.Wipe, Edge = AnimationEdge.Bottom }));
        Assert.Equal("Un-types", AnimationEditing.Sentence(new AnimationStep { Kind = AnimationKind.Out, Animation = StepAnimation.Type }));
        Assert.Equal("Pulses", AnimationEditing.Sentence(new AnimationStep { Kind = AnimationKind.Emphasis, Animation = StepAnimation.Pulse }));
        Assert.Equal("Arrives from its custom start", AnimationEditing.Sentence(new AnimationStep
        {
            Kind = AnimationKind.In, Animation = StepAnimation.Fade, FromObject = new SlideObject { Id = "x", Name = "", Text = "" },
        }));
        Assert.Equal("Changes (blur)", AnimationEditing.Sentence(new AnimationStep { Kind = AnimationKind.Morph, Animation = StepAnimation.Blur }));

        Assert.Equal("0.5s", AnimationEditing.TimingLabel(new AnimationStep { DurationSeconds = 0.5 }));
        Assert.Equal("0.5s · after 0.2s", AnimationEditing.TimingLabel(new AnimationStep { DurationSeconds = 0.5, DelaySeconds = 0.2 }));
        Assert.Equal("1.2s · 0.3s early", AnimationEditing.TimingLabel(new AnimationStep { DurationSeconds = 1.2, DelaySeconds = -0.3 }));
    }

    [Fact]
    public void TargetLabelsNameTheObjectAndClipWords()
    {
        var step = Step("s", AnimationTrigger.OnClick, ranges: [Range(0, 0, 40)]);
        var text = Text("t", "The quick brown fox jumps over the lazy dog", name: "Verse");
        Assert.Equal("Verse · “The quick brown fox jump…”", AnimationEditing.TargetLabel(text, step));
        Assert.Equal("Verse", AnimationEditing.TargetLabel(text, Step("w", AnimationTrigger.OnClick)));
        Assert.Equal("Shape", AnimationEditing.TargetLabel(new SlideObject { Id = "s", ObjectKind = SlideObjectKind.Shape, Name = "", Text = "" }, Step("w", AnimationTrigger.OnClick)));
        Assert.Equal("—", AnimationEditing.TargetLabel(null, step));
    }

    [Fact]
    public void SpeedsAndRamps()
    {
        Assert.Equal(AnimationSpeed.Quick, AnimationNames.SpeedOf(0.3));
        Assert.Equal(AnimationSpeed.Normal, AnimationNames.SpeedOf(0.5));
        Assert.Equal(AnimationSpeed.Slow, AnimationNames.SpeedOf(1.0));
        Assert.Equal(AnimationSpeed.Custom, AnimationNames.SpeedOf(0.7));
        Assert.Null(AnimationSpeed.Custom.Seconds());
        Assert.Equal(AnimationRamp.Out, AnimationEditing.EffectiveRamp(new AnimationStep { Kind = AnimationKind.In }));
        Assert.Equal(AnimationRamp.In, AnimationEditing.EffectiveRamp(new AnimationStep { Kind = AnimationKind.Out }));
        Assert.Equal(AnimationRamp.Both, AnimationEditing.EffectiveRamp(new AnimationStep { Kind = AnimationKind.Emphasis }));
        Assert.Equal(AnimationRamp.None, AnimationEditing.EffectiveRamp(new AnimationStep { Kind = AnimationKind.In, Ramp = AnimationRamp.None }));
    }

    [Fact]
    public void OverlayWarningsFlagStepsWaitingForAClick()
    {
        // An In on a click never plays on an overlay; a clicked Out still plays at the take-down.
        var c = Composition(
            Text("a", "A", [Step("in", AnimationTrigger.OnClick), Step("out", AnimationTrigger.OnClick, AnimationKind.Out)]));
        var warnings = AnimationEditing.OverlayWarnings(c);
        Assert.Contains("never plays", warnings["in"]);
        Assert.False(warnings.ContainsKey("out"));
        Assert.Equal("Seconds after the Advance.", AnimationEditing.DelayHint(Step("x", AnimationTrigger.OnClick), false));
        Assert.StartsWith("Nothing before it", AnimationEditing.DelayHint(Step("x", AnimationTrigger.AfterPrevious), true));
    }

    [Fact]
    public void GalleryChoicesAndNames()
    {
        Assert.Equal(9, AnimationEditing.Choices(AnimationKind.In).Count);
        Assert.Equal(AnimationEditing.Choices(AnimationKind.In), AnimationEditing.Choices(AnimationKind.Out));
        Assert.Equal([StepAnimation.Pulse, StepAnimation.Color, StepAnimation.Scale, StepAnimation.Move, StepAnimation.Fade], AnimationEditing.Choices(AnimationKind.Emphasis));
        Assert.Equal("Slide", AnimationEditing.TileTitle(StepAnimation.Move, AnimationKind.In));
        Assert.Equal("Grow & settle", AnimationEditing.TileTitle(StepAnimation.Scale, AnimationKind.Emphasis));
        Assert.Equal("Dim", AnimationEditing.TileTitle(StepAnimation.Fade, AnimationKind.Emphasis));
        Assert.Equal("Fade", AnimationEditing.TileTitle(StepAnimation.Fade, AnimationKind.In));
        Assert.Equal("Final", AnimationTrigger.OnDismiss.ShortName());
        Assert.Equal("On Exit", AnimationTrigger.OnDismiss.DisplayName());
    }
}
