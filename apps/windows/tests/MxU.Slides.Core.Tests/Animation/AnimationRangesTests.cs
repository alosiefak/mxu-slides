using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Model;
using static MxU.Slides.Core.Tests.Animation.AnimationFixtures;

namespace MxU.Slides.Core.Tests.Animation;

// The word and line splitting behind "One Step per Line / per Word" (the Mac's
// buildRangesByWordGeneratorSplitsInsideTheSelection in TextStyleRunsTests.swift), and generating
// the steps (SlideEditorModel.generateAnimationSteps).
public class AnimationRangesTests
{
    private static (int, int, int) Of(AnimationRange r) => (r.Line, r.Column, r.Length);

    [Fact]
    public void WordsSplitInsideTheRangeOnWhitespace()
    {
        const string text = "Amazing grace how sweet\nthe  sound";
        var words = AnimationRanges.Words([Range(0, 0, 13), Range(1, 0, 10)], text);
        Assert.Equal([(0, 0, 7), (0, 8, 5), (1, 0, 3), (1, 5, 5)], words.Select(Of));
        // A range that starts mid-word takes the part of the word inside it.
        Assert.Equal([(0, 3, 4)], AnimationRanges.Words([Range(0, 3, 5)], text).Select(Of));
        Assert.Empty(AnimationRanges.Words([Range(5, 0, 3)], text)); // no such line
    }

    [Fact]
    public void ColumnsCountCharactersAsTheMacDoes()
    {
        // "é" written as e + combining accent is one character, and so is an emoji.
        const string text = "Café 👋🏽 joy";
        var words = AnimationRanges.Words(AnimationRanges.WholeText(text), text);
        Assert.Equal([(0, 0, 4), (0, 5, 1), (0, 7, 3)], words.Select(Of));
        Assert.Equal("👋🏽", AnimationRanges.Text(words[1], text));
    }

    [Fact]
    public void LinesAreTheWholeTextLessItsEmptyLines()
    {
        Assert.Equal([(0, 0, 5), (2, 0, 3)], AnimationRanges.WholeText("Hello\n\nyou").Select(Of));
        Assert.Equal([(0, 0, 5), (2, 0, 3)], AnimationRanges.Split(AnimationRanges.WholeText("Hello\n\nyou"), AnimationRanges.Generator.Line, "Hello\n\nyou").Select(Of));
        Assert.Empty(AnimationRanges.WholeText(""));
    }

    [Fact]
    public void GeneratingMakesOneStepPerPieceInOrder()
    {
        var overlay = new Overlay
        {
            Id = "ov", Name = "O",
            Objects = [Text("t", "Grace and peace\nto you", [Step("old", AnimationTrigger.WithPrevious)])],
            AnimationOrder = ["old"],
        };
        var ids = AnimationRanges.Generate(overlay, "t", AnimationRanges.WholeText(overlay.Objects[0].Text), AnimationRanges.Generator.Word,
            AnimationKind.In, StepAnimation.Fade, AnimationTrigger.OnClick);
        Assert.Equal(5, ids.Count);
        var steps = overlay.Objects[0].AnimationSteps!;
        Assert.Equal(["old", .. ids], steps.Select(s => s.Id));
        Assert.Equal(["Grace", "and", "peace", "to", "you"], steps.Skip(1).Select(s => AnimationRanges.Text(s.Ranges!.Single(), overlay.Objects[0].Text)));
        Assert.All(steps.Skip(1), s => Assert.Equal((AnimationKind.In, StepAnimation.Fade, AnimationTrigger.OnClick), (s.Kind, s.Animation, s.Trigger)));
        Assert.Equal(["old", .. ids], overlay.AnimationOrder);
        Assert.Equal(5, AnimationSequence.Groups(overlay.Objects, overlay.AnimationOrder).Click.Count); // each word on its own click, as on the Mac

        var lines = AnimationRanges.Generate(overlay, "t", AnimationRanges.WholeText(overlay.Objects[0].Text), AnimationRanges.Generator.Line,
            AnimationKind.Emphasis, StepAnimation.Pulse, AnimationTrigger.OnClick);
        Assert.Equal(2, lines.Count);
        Assert.Empty(AnimationRanges.Generate(overlay, "missing", [Range(0, 0, 3)], AnimationRanges.Generator.Line, AnimationKind.In, StepAnimation.Fade, AnimationTrigger.OnClick));
    }

    [Fact]
    public void AppendingJoinsTheLastRangedStep()
    {
        var ranged = Step("w", AnimationTrigger.OnClick, ranges: [Range(0, 0, 3)]);
        var merged = AnimationRanges.AppendingToLastStep([Range(0, 4, 2)], [ranged]);
        Assert.Equal("w", merged!.Value.StepId);
        Assert.Equal(2, merged.Value.AnimationSteps[0].Ranges!.Count);
        Assert.Null(AnimationRanges.AppendingToLastStep([Range(0, 0, 1)], [Step("whole", AnimationTrigger.OnClick)]));
        Assert.Null(AnimationRanges.AppendingToLastStep([Range(0, 0, 1)], null));
    }
}
