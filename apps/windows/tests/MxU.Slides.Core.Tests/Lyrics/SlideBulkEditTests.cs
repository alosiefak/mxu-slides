using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Lyrics;

// Port of SlideSceneTests/SlideBulkEditTests.swift's testMergeSlidesJoinsLyricBoxesAndReanchorsChords.
public class SlideBulkEditTests
{
    [Fact]
    public void MergeSlidesJoinsLyricBoxesAndReanchorsChords()
    {
        SlideObject Ref() => new() { Id = "ref", ObjectKind = SlideObjectKind.Text, Name = "Ref", Text = "John 3:16", TextLink = new TextLink { Source = TextSourceKind.CurrentSlide } };
        var doc = new Presentation
        {
            Id = "p", Name = "P",
            Slides =
            [
                new Slide
                {
                    Id = "s1", Name = "Intro", SectionId = "intro", Actions = [new SlideAction { Id = "a1", Kind = SlideActionKind.FireMedia }],
                    Objects = [Ref(), new SlideObject { Id = "a", ObjectKind = SlideObjectKind.Text, Name = "Text", Text = "", Chords = [new ChordPlacement { Line = 0, Column = 0, Symbol = "G" }] }],
                },
                new Slide { Id = "s2", Name = "", Objects = [] },
                new Slide
                {
                    Id = "s3", Name = "Intro", SectionId = "intro", Actions = [new SlideAction { Id = "a2", Kind = SlideActionKind.FireMedia }],
                    Objects =
                    [
                        Ref(),
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
        Assert.Equal("s1", SlideBulkEdit.MergeSlides(["s3", "s1"], doc)); // the first in slide order survives
        Assert.Equal(["s1", "s2"], doc.Slides.Select(s => s.Id));
        var merged = doc.Slides[0];
        Assert.Equal(["ref", "a", "logo"], merged.Objects.Select(o => o.Id)); // the reference box once, the logo comes along
        Assert.Equal("\nla la\nla", merged.Objects[1].Text);
        Assert.Equal([(0, "G"), (2, "D")], merged.Objects[1].Chords!.Select(c => (c.Line, c.Symbol)));
        Assert.Equal(1, Assert.Single(merged.Objects[1].StyleRuns!).Line);
        Assert.Equal(["a1"], merged.Actions!.Select(a => a.Id)); // the survivor's slide settings stay
        Assert.Null(SlideBulkEdit.MergeSlides(["s1", "gone"], doc)); // one slide: nothing to merge
        Assert.Equal(2, doc.Slides.Count);
    }
}
