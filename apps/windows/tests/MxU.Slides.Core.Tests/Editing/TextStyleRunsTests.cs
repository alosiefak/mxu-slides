using MxU.Slides.Core.Editing;
using MxU.Slides.Core.Model;
using Range = MxU.Slides.Core.Editing.TextStyleRuns.Range;

namespace MxU.Slides.Core.Tests.Editing;

/// <summary>Ported from the Mac's TextStyleRunsTests.swift, plus ObjectText (typing keeps runs and chords on their words).</summary>
public sealed class TextStyleRunsTests
{
    private static TextStyleRun Run(int line, int column, int length, bool? underline = null, bool? strikethrough = null, string? colorHex = null, double? fontSize = null) =>
        new() { Line = line, Column = column, Length = length, Underline = underline, Strikethrough = strikethrough, ColorHex = colorHex, FontSize = fontSize };

    private static string Show(IEnumerable<TextStyleRun> runs) =>
        string.Join(" ", runs.Select(r => $"{r.Line}:{r.Column}+{r.Length}{(r.Underline == true ? "u" : "")}{(r.Strikethrough == true ? "s" : "")}{r.ColorHex}{r.FontSize}"));

    [Fact]
    public void Utf16SelectionConvertsToCharacters()
    {
        const string text = "🎸 Amazing";
        Assert.Equal(new Range(2, 9), TextStyleRuns.CharacterRange(3, 7, text));
        Assert.Equal(new Range(0, 1), TextStyleRuns.CharacterRange(0, 2, text));
    }

    [Fact]
    public void StylingASelectionEmitsOneRun() =>
        Assert.Equal("0:8+5#FF0000FF", Show(TextStyleRuns.Applying(r => r.ColorHex = "#FF0000FF", [], "Amazing grace", new Range(8, 13))));

    [Fact]
    public void OverlappingSelectionSplitsExistingRuns() =>
        Assert.Equal("0:0+2u 0:2+4u#00FF00FF 0:6+1u",
            Show(TextStyleRuns.Applying(r => r.ColorHex = "#00FF00FF", [Run(0, 0, 7, underline: true)], "Amazing grace", new Range(2, 6))));

    [Fact]
    public void ClearingAFieldSplitsTheRun() =>
        Assert.Equal("0:0+7u", Show(TextStyleRuns.Applying(r => r.Underline = null, [Run(0, 0, 13, underline: true)], "Amazing grace", new Range(7, 13))));

    [Fact]
    public void IdenticalNeighboursCoalesce() =>
        Assert.Equal("0:0+13u", Show(TextStyleRuns.Applying(r => r.Underline = true, [Run(0, 0, 7, underline: true)], "Amazing grace", new Range(7, 13))));

    [Fact]
    public void AMultiLineSelectionNeverSpansLines() =>
        Assert.Equal("0:5+290 1:0+290", Show(TextStyleRuns.Applying(r => r.FontSize = 90, [], "Amazing\ngrace", new Range(5, 10))));

    [Fact]
    public void UntouchedLinesPassThroughVerbatim()
    {
        var other = Run(1, 0, 5, strikethrough: true);
        Assert.Contains(other, TextStyleRuns.Applying(r => r.Underline = true, [other], "Amazing\ngrace", new Range(0, 3)));
    }

    [Fact]
    public void TheEffectiveRunFoldsOverlaps()
    {
        TextStyleRun[] runs = [Run(0, 0, 7, underline: true), Run(0, 2, 3, colorHex: "#FF0000FF")];
        var effective = TextStyleRuns.EffectiveRun(3, runs, "Amazing grace");
        Assert.True(effective?.Underline);
        Assert.Equal("#FF0000FF", effective?.ColorHex);
        Assert.Null(TextStyleRuns.EffectiveRun(10, runs, "Amazing grace"));
    }

    [Fact]
    public void InsertingBeforeARunShiftsIt() =>
        Assert.Equal("0:11+5u", Show(TextStyleRuns.Replacing([Run(0, 8, 5, underline: true)], new Range(0, 0), "O! ", "Amazing grace")));

    [Fact]
    public void TypingInsideARunGrowsIt() =>
        Assert.Equal("0:8+7u", Show(TextStyleRuns.Replacing([Run(0, 8, 5, underline: true)], new Range(10, 10), "aa", "Amazing grace")));

    [Fact]
    public void DeletingAcrossARunsEdgeTrimsIt() =>
        Assert.Equal("0:5+3u", Show(TextStyleRuns.Replacing([Run(0, 8, 5, underline: true)], new Range(5, 10), "", "Amazing grace")));

    [Fact]
    public void ReplacingARunEntirelyDropsIt() =>
        Assert.Empty(TextStyleRuns.Replacing([Run(0, 8, 5, underline: true)], new Range(8, 13), "mercy", "Amazing grace"));

    [Fact]
    public void APastedNewlineSplitsARun() =>
        Assert.Equal("0:0+7u 1:0+5u", Show(TextStyleRuns.Replacing([Run(0, 0, 13, underline: true)], new Range(7, 8), "\n", "Amazing grace")));

    [Fact]
    public void EditsOnOtherLinesLeaveRunsAlone() =>
        Assert.Equal("1:0+5#FF0000FF", Show(TextStyleRuns.Replacing([Run(1, 0, 5, colorHex: "#FF0000FF")], new Range(0, 7), "Boundless", "Amazing\ngrace")));

    [Fact]
    public void ChordAnchorsShiftAndDrop()
    {
        ChordPlacement[] chords = [new() { Line = 0, Column = 0, Symbol = "G" }, new() { Line = 0, Column = 8, Symbol = "C" }];
        var afterDelete = TextStyleRuns.Replacing(chords, new Range(7, 13), "", "Amazing grace");
        Assert.Equal(["G@0:0"], afterDelete.Select(c => $"{c.Symbol}@{c.Line}:{c.Column}"));
        var afterInsert = TextStyleRuns.Replacing(chords, new Range(8, 8), "a", "Amazing grace");
        Assert.Equal(["G@0:0", "C@0:9"], afterInsert.Select(c => $"{c.Symbol}@{c.Line}:{c.Column}"));
    }

    [Fact]
    public void AnimationRangesFollowTheirWordsAndGoWithThem()
    {
        const string text = "one two\nthree";
        var step = new AnimationStep { Id = "s", Kind = AnimationKind.In, Animation = StepAnimation.Fade, Trigger = AnimationTrigger.OnClick, DurationSeconds = 0.5, Ranges = [new AnimationRange { Line = 0, Column = 4, Length = 3 }] };
        var whole = new AnimationStep { Id = "w", Kind = AnimationKind.In, Animation = StepAnimation.Fade, Trigger = AnimationTrigger.OnClick, DurationSeconds = 0.5 };
        var shifted = TextStyleRuns.Replacing(step.Ranges!, new Range(4, 4), "very ", text);
        Assert.Equal(9, shifted.Single().Column);
        var after = TextStyleRuns.Reanchor([Core.Json.MxuJson.Clone(step), whole], new Range(4, 7), "", text);
        Assert.Equal(["w"], after.Select(s => s.Id)); // the ranged step lost its words; the whole-object step stays
    }

    // MARK: Typing in a text box

    [Fact]
    public void TypingKeepsStylingAndChordsOnTheirWords()
    {
        var o = new SlideObject
        {
            Id = "t", ObjectKind = SlideObjectKind.Text, Name = "Lyrics", Text = "Amazing grace",
            StyleRuns = [Run(0, 8, 5, underline: true)],
            Chords = [new() { Line = 0, Column = 8, Symbol = "C" }],
        };
        ObjectText.SetText(o, "Oh amazing grace");
        Assert.Equal("0:11+5u", Show(o.StyleRuns!));
        Assert.Equal(11, o.Chords![0].Column);
        ObjectText.SetText(o, "Oh amazing grace!");
        Assert.Equal("0:11+5u", Show(o.StyleRuns!));
        Assert.Equal("Oh amazing grace!", o.Text);
    }

    [Fact]
    public void TheDifferenceIsTheOneStretchThatChanged()
    {
        Assert.Equal((new Range(8, 8), "sweet "), ObjectText.Difference("Amazing grace", "Amazing sweet grace"));
        Assert.Equal((new Range(0, 2), ""), ObjectText.Difference("O amazing", "amazing"));
        Assert.Equal((new Range(2, 3), "é"), ObjectText.Difference("caf", "caé"));
    }
}
