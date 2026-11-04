using MxU.Slides.Core.Editing;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Editing;

/// <summary>Ported from the Mac's SlideBulkEditTests.swift (testMergeSlidesJoinsLyricBoxesAndReanchorsChords).</summary>
public sealed class SlideMergeTests
{
    private static SlideObject Reference() => new()
    {
        Id = "ref", ObjectKind = SlideObjectKind.Text, Name = "Ref", Text = "John 3:16", TextLink = new TextLink { Source = TextSourceKind.CurrentSlide },
    };

    [Fact]
    public void MergeSlidesJoinsLyricBoxesAndReanchorsChords()
    {
        var deck = new Presentation
        {
            Id = "d", Name = "Deck", PresentationKind = PresentationKind.Deck, ThemeId = "",
            Slides =
            [
                new Slide
                {
                    Id = "s1", Name = "Intro", SectionId = "intro", Actions = [new SlideAction { Id = "a1", Kind = SlideActionKind.FireMedia }],
                    Objects = [Reference(), new SlideObject { Id = "a", ObjectKind = SlideObjectKind.Text, Name = "Text", Text = "", Chords = [new ChordPlacement { Line = 0, Column = 0, Symbol = "G" }] }],
                },
                new Slide { Id = "s2", Name = "s2", Objects = [] },
                new Slide
                {
                    Id = "s3", Name = "Intro", SectionId = "intro", Actions = [new SlideAction { Id = "a2", Kind = SlideActionKind.FireMedia }],
                    Objects =
                    [
                        Reference(),
                        new SlideObject
                        {
                            Id = "b", ObjectKind = SlideObjectKind.Text, Name = "Text", Text = "la la\nla",
                            Chords = [new ChordPlacement { Line = 1, Column = 0, Symbol = "D" }],
                            StyleRuns = [new TextStyleRun { Line = 0, Column = 0, Length = 2, Underline = true }],
                        },
                        new SlideObject { Id = "logo", ObjectKind = SlideObjectKind.Shape, Name = "Logo", Text = "" },
                    ],
                },
            ],
        };

        Assert.Equal("s1", SlideMerge.MergeSlides(["s3", "s1"], deck)); // the first in slide order survives
        Assert.Equal(["s1", "s2"], deck.Slides.Select(s => s.Id));
        var merged = deck.Slides[0];
        Assert.Equal(["ref", "a", "logo"], merged.Objects.Select(o => o.Id)); // the reference box once, the logo comes along
        Assert.Equal("\nla la\nla", merged.Objects[1].Text);
        Assert.Equal(["G@0:0", "D@2:0"], merged.Objects[1].Chords!.Select(c => $"{c.Symbol}@{c.Line}:{c.Column}"));
        Assert.Equal((1, 0, 2), (merged.Objects[1].StyleRuns!.Single().Line, merged.Objects[1].StyleRuns![0].Column, merged.Objects[1].StyleRuns![0].Length));
        Assert.Equal(["a1"], merged.Actions!.Select(a => a.Id)); // the survivor's slide settings stay

        Assert.Null(SlideMerge.MergeSlides(["s1", "gone"], deck)); // one slide: nothing to merge
        Assert.Equal(2, deck.Slides.Count);
    }
}
