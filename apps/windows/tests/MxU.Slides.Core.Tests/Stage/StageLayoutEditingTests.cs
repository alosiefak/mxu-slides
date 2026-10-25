using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Core.Stage;

namespace MxU.Slides.Core.Tests.Stage;

// The first three are ported from the Mac's PresenterCore/Tests/PresenterCoreTests/ConfidenceLayoutEditingTests.swift;
// the boxes, margins and notes are Windows' own.
public class StageLayoutEditingTests
{
    private static ConfidenceLayout Layout(int? width = null, int? height = null) => new()
    {
        Id = "layout-1", Name = "Booth",
        Objects = [new SlideObject { Id = "a", ObjectKind = SlideObjectKind.Shape, Name = "A", Text = "" }],
        CanvasWidth = width, CanvasHeight = height,
    };

    [Fact]
    public void EditorMirrorCarriesTheLayoutsCanvasAndComposition()
    {
        var (mirror, slide) = StageLayoutEditing.EditorMirror(Layout(1080, 1920));
        Assert.Equal(1080, mirror.CanvasWidth);
        Assert.Equal(1920, mirror.CanvasHeight);
        Assert.Equal(["layout-1"], mirror.Slides.Select(s => s.Id));
        Assert.Equal(["a"], slide.Objects.Select(o => o.Id));
    }

    [Fact]
    public void AdoptingAMirrorPersistsACanvasResize()
    {
        var edited = Layout();
        var (mirror, slide) = StageLayoutEditing.EditorMirror(edited);
        mirror.CanvasWidth = 1080;
        mirror.CanvasHeight = 1920;
        slide.Objects.Add(new SlideObject { Id = "b", ObjectKind = SlideObjectKind.Text, Name = "B", Text = "" });
        slide.AnimationOrder = ["b", "a"];

        StageLayoutEditing.Adopt(edited, mirror);
        Assert.Equal(1080, edited.CanvasWidth);
        Assert.Equal(1920, edited.CanvasHeight);
        Assert.Equal(["a", "b"], edited.Objects.Select(o => o.Id));
        Assert.Equal(["b", "a"], edited.AnimationOrder);
    }

    [Fact]
    public void AdoptingTheDefaultCanvasStoresAbsent()
    {
        var edited = Layout(1080, 1920);
        var (mirror, _) = StageLayoutEditing.EditorMirror(edited);
        mirror.CanvasWidth = null;
        mirror.CanvasHeight = null;
        StageLayoutEditing.Adopt(edited, mirror);
        Assert.Null(edited.CanvasWidth);
        Assert.Null(edited.CanvasHeight);
    }

    [Fact]
    public void TheMirrorsObjectsAreTheLayoutsOwn()
    {
        var layout = Layout();
        var (_, slide) = StageLayoutEditing.EditorMirror(layout);
        slide.Objects[0].X = 42; // what the canvas does when you drag the box
        Assert.Equal(42, layout.Objects[0].X);
    }

    [Fact]
    public void ANewCanvasScalesTheBoxesAndStoresTheDefaultAsAbsent()
    {
        var layout = Layout();
        layout.Objects[0].X = 960;
        layout.Objects[0].Width = 480;
        layout.Objects[0].Y = 540;
        Assert.True(StageLayoutEditing.SetCanvas(layout, 1080, 1920));
        Assert.True(StageLayoutEditing.IsTallCanvas(layout));
        Assert.Equal((540.0, 270.0, 960.0), (layout.Objects[0].X!.Value, layout.Objects[0].Width!.Value, layout.Objects[0].Y!.Value));
        Assert.False(StageLayoutEditing.SetCanvas(layout, 1080, 1920));
        Assert.True(StageLayoutEditing.SetCanvas(layout, 1920, 1080));
        Assert.Null(layout.CanvasWidth);
        Assert.Equal(960, layout.Objects[0].X);
    }

    [Fact]
    public void ABoxIsACardWithItsLinkedTextAndCaption()
    {
        var canvas = SceneSize.Hd;
        var clock = StageLayoutEditing.MakeBox(StageBoxKind.Clock, canvas, []);
        Assert.Equal(3, clock.Count);
        Assert.Equal(SlideObjectKind.Shape, clock[0].ObjectKind);
        Assert.Equal(TextSourceKind.Clock, clock[1].TextLink?.Source);
        Assert.Equal("h:mm a", clock[1].TextLink?.ClockFormat);
        Assert.Equal("CLOCK", clock[2].Text);
        Assert.True(clock[1].X > clock[0].X && clock[1].Y > clock[0].Y); // the words sit inside the card

        var timer = StageLayoutEditing.MakeBox(StageBoxKind.ServiceTimer, canvas, [], timerId: "service_tracking.item.remaining");
        Assert.Equal(("service_tracking.item.remaining", TextSourceKind.Timer), (timer[1].TextLink?.TimerId, timer[1].TextLink!.Source));
        Assert.True(StageLayoutEditing.MakeBox(StageBoxKind.Chords, canvas, [])[1].TextStyle?.ShowChords);
        var notes = StageLayoutEditing.MakeBox(StageBoxKind.Notes, canvas, [])[1];
        Assert.Equal((TextSourceKind.CurrentSlide, StageLayoutEditing.NotesObjectName), (notes.TextLink!.Source, notes.TextLink.SourceObjectName));
        Assert.Equal("Slide notes", StageLayoutEditing.Describe(notes));
        var label = Assert.Single(StageLayoutEditing.MakeBox(StageBoxKind.Label, canvas, []));
        Assert.Null(label.TextLink);
        Assert.Equal("Static text", StageLayoutEditing.Describe(label));
    }

    [Fact]
    public void NewBoxesFindAFreeSpotInsideTheMargin()
    {
        var canvas = SceneSize.Hd;
        var first = StageLayoutEditing.MakeBox(StageBoxKind.Clock, canvas, []);
        Assert.Equal((96.0, 54.0), (first[0].X!.Value, first[0].Y!.Value)); // the 5 % margin's corner
        var second = StageLayoutEditing.MakeBox(StageBoxKind.Timer, canvas, first);
        var a = new SceneRect(first[0].X!.Value, first[0].Y!.Value, first[0].Width!.Value, first[0].Height!.Value);
        var b = new SceneRect(second[0].X!.Value, second[0].Y!.Value, second[0].Width!.Value, second[0].Height!.Value);
        Assert.False(a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom); // they don't overlap
    }

    [Fact]
    public void SafeAreasAndWhatPokesOutOfThem()
    {
        var safe = StageLayoutEditing.SafeArea(SceneSize.Hd, 0.1);
        Assert.Equal(new SceneRect(192, 108, 1536, 864), safe);
        var inside = new SlideObject { Id = "in", ObjectKind = SlideObjectKind.Shape, Name = "", Text = "", X = 200, Y = 200, Width = 100, Height = 100 };
        var outside = new SlideObject { Id = "out", ObjectKind = SlideObjectKind.Shape, Name = "", Text = "", X = 10, Y = 200, Width = 100, Height = 100 };
        Assert.Equal(["out"], StageLayoutEditing.OutsideSafeArea([inside, outside], safe).Select(o => o.Id));
    }

    [Fact]
    public void CopiesAndNames()
    {
        var copy = StageLayoutEditing.Duplicate(Layout());
        Assert.Equal("Booth Copy", copy.Name);
        Assert.NotEqual("layout-1", copy.Id);
        Assert.Equal(["a"], copy.Objects.Select(o => o.Id)); // as the Mac copies: everything else the same
        Assert.Equal("Current + Next", StageLayoutEditing.Create(ConfidenceLayoutTemplate.CurrentAndNext).Name);
        Assert.Equal("Untitled Layout", StageLayoutEditing.Create(ConfidenceLayoutTemplate.Blank).Name);
        var layout = Layout();
        Assert.False(StageLayoutEditing.Rename(layout, " "));
        Assert.True(StageLayoutEditing.Rename(layout, " Band "));
        Assert.Equal("Band", layout.Name);
    }

    [Fact]
    public void ANotesBoxReadsTheLiveSlidesNotes()
    {
        var slide = new Slide
        {
            Id = "s", Name = "", Notes = "Key change after this",
            Objects = [new SlideObject { Id = "t", ObjectKind = SlideObjectKind.Text, Name = "Lyrics", Text = "Amazing grace" }],
        };
        var info = new ConfidenceInfo { Current = ConfidenceSceneBuilder.SlideText(slide, null) };
        var link = new TextLink { Source = TextSourceKind.CurrentSlide, SourceObjectName = StageLayoutEditing.NotesObjectName };
        Assert.Equal("Key change after this", LinkedText.Resolve(link, info, DateTimeOffset.Now));
        Assert.Equal("Amazing grace", LinkedText.Resolve(new TextLink { Source = TextSourceKind.CurrentSlide }, info, DateTimeOffset.Now));

        // A slide with an object of its own called Notes shows that, as a Mac does.
        slide.Objects.Add(new SlideObject { Id = "n", ObjectKind = SlideObjectKind.Text, Name = "Notes", Text = "On the slide" });
        Assert.Equal("On the slide", LinkedText.Resolve(link, new ConfidenceInfo { Current = ConfidenceSceneBuilder.SlideText(slide, null) }, DateTimeOffset.Now));
        Assert.False(string.IsNullOrEmpty(LinkedText.SampleText(link))); // the editor's preview reads something
    }
}
