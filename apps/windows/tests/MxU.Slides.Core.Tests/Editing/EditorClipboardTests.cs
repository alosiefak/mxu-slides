using MxU.Slides.Core.Editing;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Core.Tests.Library;

namespace MxU.Slides.Core.Tests.Editing;

public sealed class EditorClipboardTests
{
    private static Func<string> Ids()
    {
        var n = 0;
        return () => $"new-{++n}";
    }

    private static Slide Slide(string id, string? section = null, params SlideObject[] objects) => new()
    {
        Id = id, Name = "", SectionId = section,
        Objects = objects.Length > 0 ? [.. objects] : [Text(id + "-t", id)],
    };

    private static SlideObject Text(string id, string text) => new() { Id = id, ObjectKind = SlideObjectKind.Text, Name = "Lyrics", Text = text };

    private static SlideObject Box(string id, double x = 100, string? group = null, string? mask = null) => new()
    {
        Id = id, ObjectKind = SlideObjectKind.Shape, Name = "Box", Text = "", X = x, Y = 50, Width = 200, Height = 100,
        GroupId = group, MaskObjectId = mask,
    };

    private static Presentation Song(params Slide[] slides) => new()
    {
        Id = "song", Name = "Song", PresentationKind = PresentationKind.Song, ThemeId = "",
        Sections = [new PresentationSection { Id = "verse", Name = "Verse 1" }, new PresentationSection { Id = "chorus", Name = "Chorus" }],
        Slides = [.. slides],
    };

    // MARK: Fresh ids (the Mac's freshIDCopy)

    [Fact]
    public void APastedSlideHasFreshIdsAndKeepsItsMedia()
    {
        var slide = Slide("s1", "verse", Box("matte"), Box("masked", mask: "matte"));
        slide.Background = new CueMedia { MediaId = "media-1" };
        slide.Actions = [new SlideAction { Id = "a1", Kind = SlideActionKind.FireMedia, MediaId = "media-2" }];
        var copy = EditorClipboard.FreshCopy(slide, Ids());

        Assert.NotEqual(slide.Id, copy.Id);
        Assert.DoesNotContain(copy.Objects, o => o.Id is "matte" or "masked");
        Assert.Equal(copy.Objects[0].Id, copy.Objects[1].MaskObjectId);
        Assert.NotEqual("a1", copy.Actions![0].Id);
        Assert.Equal("media-1", copy.Background?.MediaId);
        Assert.Equal("media-2", copy.Actions[0].MediaId);
        // The original is untouched.
        Assert.Equal("matte", slide.Objects[1].MaskObjectId);
    }

    [Fact]
    public void PastedSlidesJoinTheGroupOfTheSlideTheyLandAfter()
    {
        var song = Song(Slide("v1", "verse"), Slide("c1", "chorus"), Slide("c2", "chorus"));
        var pasted = EditorClipboard.InsertSlides(song, [Slide("x", "verse"), Slide("y", "other-song")], afterSlideId: "c1", Ids());
        Assert.Equal(["v1", "c1", "new-1", "new-3", "c2"], song.Slides.Select(s => s.Id));
        Assert.All(pasted, p => Assert.Equal("chorus", p.Copy.SectionId));
        Assert.Equal(["x", "y"], pasted.Select(p => p.Source.Id));
    }

    [Fact]
    public void WithNoAnchorTheyGoAtTheEnd()
    {
        var song = Song(Slide("v1", "verse"), Slide("c1", "chorus"));
        EditorClipboard.InsertSlides(song, [Slide("x")], afterSlideId: null, Ids());
        Assert.Equal("new-1", song.Slides[^1].Id);
        Assert.Equal("chorus", song.Slides[^1].SectionId);
    }

    [Fact]
    public void IntoAnEmptyPresentationOnlyAGroupItHasIsKept()
    {
        var empty = Song();
        EditorClipboard.InsertSlides(empty, [Slide("a", "chorus"), Slide("b", "from-another-song")], null, Ids());
        Assert.Equal(["chorus", null], empty.Slides.Select(s => s.SectionId));
    }

    [Fact]
    public void DuplicatingPutsEachCopyAfterItsSlide()
    {
        var song = Song(Slide("a"), Slide("b"), Slide("c"));
        var made = EditorClipboard.DuplicateSlides(song, ["a", "c"], Ids());
        Assert.Equal(["a", "new-3", "b", "c", "new-1"], song.Slides.Select(s => s.Id));
        Assert.Equal(["a", "c"], made.Select(m => m.Source.Id));
    }

    [Fact]
    public void DeletingKeepsTheLastSlide()
    {
        var song = Song(Slide("a"), Slide("b"));
        Assert.False(EditorClipboard.DeleteSlides(song, ["a", "b"]));
        Assert.True(EditorClipboard.DeleteSlides(song, ["a"]));
        Assert.Equal(["b"], song.Slides.Select(s => s.Id));
        Assert.False(EditorClipboard.DeleteSlides(song, ["missing"]));
    }

    // MARK: Objects (the Mac's pasteObjects)

    [Fact]
    public void ObjectsPastedBackOntoTheirSlideStepAsideEachTime()
    {
        var slide = Slide("s", null, Box("b1", x: 100));
        var clip = EditorClipboard.CopyObjects(slide, ["b1"]);
        var ids = Ids();
        var first = EditorClipboard.PasteObjects(slide, clip, cascade: 1, o => new SceneRect(o.X ?? 0, o.Y ?? 0, o.Width ?? 0, o.Height ?? 0), ids);
        var second = EditorClipboard.PasteObjects(slide, clip, cascade: 2, o => new SceneRect(o.X ?? 0, o.Y ?? 0, o.Width ?? 0, o.Height ?? 0), ids);
        Assert.Equal(124, first[0].X);
        Assert.Equal(74, first[0].Y);
        Assert.Equal(148, second[0].X);
        Assert.Equal(3, slide.Objects.Count);
        Assert.Equal(3, slide.Objects.Select(o => o.Id).Distinct().Count());
    }

    [Fact]
    public void ObjectsPastedOntoAnotherSlideKeepTheirPlace()
    {
        var clip = EditorClipboard.CopyObjects(Slide("from", null, Box("b1", x: 100)), ["b1"]);
        var other = Slide("to", null, Text("t", "words"));
        var pasted = EditorClipboard.PasteObjects(other, clip, cascade: 1, frameOf: null, Ids());
        Assert.Equal(100, pasted[0].X);
        Assert.Equal(["t", "new-1"], other.Objects.Select(o => o.Id));
    }

    [Fact]
    public void GroupedObjectsStayGroupedInANewGroupAndMasksFollow()
    {
        var source = Slide("s", null, Box("a", group: "g1"), Box("b", group: "g1"), Box("c", mask: "a"));
        var clip = EditorClipboard.CopyObjects(source, ["a", "b", "c"]);
        var pasted = EditorClipboard.PasteObjects(Slide("t", null, Text("x", "")), clip, 0, null, Ids());
        Assert.Equal(pasted[0].GroupId, pasted[1].GroupId);
        Assert.NotEqual("g1", pasted[0].GroupId);
        Assert.Equal(pasted[0].Id, pasted[2].MaskObjectId);
    }

    [Fact]
    public void CopiedObjectsKeepTheirStackingOrder()
    {
        var slide = Slide("s", null, Box("bottom"), Box("middle"), Box("top"));
        Assert.Equal(["bottom", "top"], EditorClipboard.CopyObjects(slide, ["top", "bottom"]).Objects.Select(o => o.Id));
    }

    // MARK: The clipboard's own format

    [Fact]
    public void ClipsRoundTripAndAnythingElseIsIgnored()
    {
        var clip = new SlideClip("song", [Slide("a", "verse")]);
        var back = EditorClipboard.DecodeSlides(EditorClipboard.Encode(clip));
        Assert.Equal("a", back?.Slides[0].Id);
        Assert.Equal("song", back?.SourcePresentationId);
        Assert.Null(EditorClipboard.DecodeSlides("just some words"));
        Assert.Null(EditorClipboard.DecodeSlides(null));
        Assert.Null(EditorClipboard.DecodeObjects(EditorClipboard.Encode(new ObjectClip("s", []))));
    }

    [Fact]
    public void OtherAppsGetTheWords()
    {
        Assert.Equal("one\n\ntwo", EditorClipboard.PlainText([Slide("a", null, Text("1", "one")), Slide("b", null, Box("x")), Slide("c", null, Text("2", "two"))]));
    }

    // MARK: Text from another app

    [Fact]
    public void PlainTextOnASlideIsACentredTextBox()
    {
        var box = EditorClipboard.TextBox("  Welcome\r\nto church  ", new SceneSize(1920, 1080), Ids());
        Assert.Equal(SlideObjectKind.Text, box.ObjectKind);
        Assert.Equal("Welcome\nto church", box.Text);
        Assert.Equal(1920.0, box.X * 2 + box.Width);
        Assert.Equal(1080.0, (box.Y * 2 + box.Height)!.Value, 1);
    }

    [Fact]
    public void LyricsPastedIntoASongBecomeSlidesInTheNeighboursGroup()
    {
        var song = Song(Slide("v1", "verse", Text("t1", "Amazing grace\nhow sweet")), Slide("c1", "chorus"));
        var made = EditorClipboard.InsertLyrics(song, "Line one\nLine two\nLine three", afterSlideId: "v1", Ids());
        Assert.Equal(2, made.Count); // two lines a slide, as the song's slides have
        Assert.Equal(["v1", made[0].Id, made[1].Id, "c1"], song.Slides.Select(s => s.Id));
        Assert.All(made, s => Assert.Equal("verse", s.SectionId));
        Assert.Equal("Line one\nLine two", made[0].Objects[0].Text);
        Assert.Equal(2, song.Sections!.Count);
    }

    [Fact]
    public void LabelledLyricsJoinTheSongsGroupOfThatNameOrStartOne()
    {
        var song = Song(Slide("v1", "verse", Text("t1", "a\nb")), Slide("c1", "chorus"));
        var made = EditorClipboard.InsertLyrics(song, "Chorus\nSing it\nagain\n\nBridge\nHigher\nstill", afterSlideId: "c1", Ids());
        Assert.Equal("chorus", made[0].SectionId);
        var bridge = song.Sections!.Single(s => s.Name == "Bridge");
        Assert.Equal(bridge.Id, made[^1].SectionId);
        Assert.Empty(EditorClipboard.InsertLyrics(song, "   \n  ", null, Ids()));
    }
}
