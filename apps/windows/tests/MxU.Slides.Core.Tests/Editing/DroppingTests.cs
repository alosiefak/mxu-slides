using MxU.Slides.Core.Editing;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using Point = MxU.Slides.Core.Scene.EditorGeometry.Point;

namespace MxU.Slides.Core.Tests.Editing;

/// <summary>Ported from the Mac's SlideGridDropTests.swift, plus the Windows drop rules.</summary>
public sealed class DroppingTests
{
    // Six tiles, three to a row: 100 × 60, 12 apart.
    private static readonly Dictionary<int, SceneRect> Frames =
        Enumerable.Range(0, 6).ToDictionary(i => i, i => new SceneRect(i % 3 * 112, i / 3 * 72, 100, 60));

    private static SlideGridDrop.Spot Spot(double x, double y, int count = 6) =>
        SlideGridDrop.SpotAt(new Point(x, y), Frames, count, rowGap: 12);

    [Fact]
    public void TheGapBetweenTwoTilesInsertsBetweenThem()
    {
        Assert.Equal(SlideGridDrop.Spot.Before(1), Spot(106, 30));
        Assert.Equal(1, Spot(106, 30).InsertionIndex);
        Assert.Equal(SlideGridDrop.Spot.Before(5), Spot(218, 100)); // second row, between slides 4 and 5
    }

    [Fact]
    public void PastARowsLastTileLandsAfterIt()
    {
        Assert.Equal(SlideGridDrop.Spot.AfterTile(2), Spot(400, 30));
        Assert.Equal(3, Spot(400, 30).InsertionIndex);
    }

    [Fact]
    public void TheGapBelowARowBelongsToItsNearerHalf()
    {
        Assert.Equal(SlideGridDrop.Spot.Before(0), Spot(10, 64));
        Assert.Equal(SlideGridDrop.Spot.Before(3), Spot(10, 70));
    }

    [Fact]
    public void BelowEveryRowLandsAfterTheLastSlide()
    {
        Assert.Equal(SlideGridDrop.Spot.AfterTile(5), Spot(10, 500));
        Assert.Equal(SlideGridDrop.Spot.Before(0), SlideGridDrop.SpotAt(new Point(0, 0), new Dictionary<int, SceneRect>(), 0, 12));
    }

    [Fact]
    public void ATilesEdgesInsertMediaBesideItAndItsMiddleIsTheBackground()
    {
        Assert.Equal(SlideDropZone.Before, SlideDropZones.Tile(new Point(10, 30), 160, reorder: false, insertOnly: false));
        Assert.Equal(SlideDropZone.On, SlideDropZones.Tile(new Point(80, 30), 160, reorder: false, insertOnly: false));
        Assert.Equal(SlideDropZone.After, SlideDropZones.Tile(new Point(150, 30), 160, reorder: false, insertOnly: false));
    }

    [Fact]
    public void SlideReordersPickTheirSideByHalf()
    {
        Assert.Equal(SlideDropZone.Before, SlideDropZones.Tile(new Point(70, 30), 160, reorder: true, insertOnly: false));
        Assert.Equal(SlideDropZone.After, SlideDropZones.Tile(new Point(90, 30), 160, reorder: true, insertOnly: false));
    }

    [Fact]
    public void ARowsBandsInsertAroundIt()
    {
        var size = new SceneSize(240, 28);
        Assert.Equal(SlideDropZone.Before, SlideDropZones.Row(new Point(120, 3), size, insertOnly: false));
        Assert.Equal(SlideDropZone.On, SlideDropZones.Row(new Point(120, 14), size, insertOnly: false));
        Assert.Equal(SlideDropZone.After, SlideDropZones.Row(new Point(120, 25), size, insertOnly: false));
        Assert.Equal(SlideDropZone.Before, SlideDropZones.Row(new Point(10, 14), size, insertOnly: false));
        Assert.Equal(SlideDropZone.Before, SlideDropZones.Row(new Point(120, 14), size, insertOnly: true));
    }

    // MARK: Payloads

    [Fact]
    public void PayloadsRoundTripAndStrangersAreIgnored()
    {
        var media = new LibraryDrag(typeof(MediaItem), "m1");
        Assert.Equal(media, LibraryDrag.Decode(media.Encode()));
        Assert.Null(LibraryDrag.Decode("mxuitem::Theme::t1"));
        Assert.Null(LibraryDrag.Decode("hello"));
        var slides = new SlideDrag("deck", ["a", "b"]);
        Assert.Equal(["a", "b"], SlideDrag.Decode(slides.Encode())!.SlideIds);
        Assert.Equal("deck", SlideDrag.Decode("mxuslide::deck::s1")?.PresentationId); // the Mac's one-slide payload
        Assert.Null(SlideDrag.Decode("mxuslide::deck::"));
    }

    // MARK: Moving slides

    private static Presentation Deck() => new()
    {
        Id = "deck", Name = "Deck", PresentationKind = PresentationKind.Song, ThemeId = "",
        Slides =
        [
            new Slide { Id = "v1", Name = "", Objects = [], SectionId = "verse" },
            new Slide { Id = "v2", Name = "", Objects = [], SectionId = "verse" },
            new Slide { Id = "c1", Name = "", Objects = [], SectionId = "chorus" },
            new Slide { Id = "c2", Name = "", Objects = [], SectionId = "chorus" },
        ],
    };

    [Fact]
    public void DraggedSlidesLandBeforeTheTargetAndJoinItsGroup()
    {
        var deck = Deck();
        Assert.True(DropRules.MoveSlides(deck, ["v1"], beforeSlideId: "c2"));
        Assert.Equal(["v2", "c1", "v1", "c2"], deck.Slides.Select(s => s.Id));
        Assert.Equal("chorus", deck.Slides[2].SectionId);
    }

    [Fact]
    public void SeveralKeepTheirOrderAndTheEndJoinsTheLastGroup()
    {
        var deck = Deck();
        DropRules.MoveSlides(deck, ["c1", "v1"], beforeSlideId: null);
        Assert.Equal(["v2", "c2", "v1", "c1"], deck.Slides.Select(s => s.Id));
        Assert.All(deck.Slides.Skip(2), s => Assert.Equal("chorus", s.SectionId));
    }

    [Fact]
    public void DroppingOnItselfChangesNothing()
    {
        var deck = Deck();
        Assert.False(DropRules.MoveSlides(deck, ["v2"], beforeSlideId: "v2"));
        Assert.False(DropRules.MoveSlides(deck, ["v2"], beforeSlideId: "c1"));
        Assert.Equal(["v1", "v2", "c1", "c2"], deck.Slides.Select(s => s.Id));
    }

    // MARK: Dropping library items

    [Fact]
    public void ALibraryItemMakesTheRightServiceRow()
    {
        Assert.Equal(ServiceItemKind.Media, DropRules.ServiceItemFor(new LibraryDrag(typeof(MediaItem), "m"), "Clip")?.ItemKind);
        Assert.Equal(ServiceItemKind.Playlist, DropRules.ServiceItemFor(new LibraryDrag(typeof(Playlist), "p"), "Walk-in")?.ItemKind);
        Assert.Equal("m", DropRules.ServiceItemFor(new LibraryDrag(typeof(MediaItem), "m"), "Clip")?.RefId);
        Assert.Null(DropRules.ServiceItemFor(new LibraryDrag(typeof(Overlay), "o"), "Lower third"));
    }

    [Fact]
    public void MediaOnAShapeFillsItAndElsewhereMakesAPictureBox()
    {
        var shape = new SlideObject { Id = "frame", ObjectKind = SlideObjectKind.Shape, Name = "Frame", Text = "", X = 100, Y = 100, Width = 400, Height = 300 };
        var slide = new Slide { Id = "s", Name = "", Objects = [shape] };
        var item = new MediaItem { Id = "m1", Name = "Sunrise", MediaKind = MediaKind.Image, Classification = MediaClassification.Background, FileHash = "h", FileName = "a.png", FileStatus = MediaFileStatus.Ready, StatusDetail = "", PixelWidth = 1600, PixelHeight = 900 };
        var (filled, madeOne) = DropRules.DropMediaOnCanvas(slide, null, item, new Point(200, 200), new SceneSize(1920, 1080));
        Assert.False(madeOne);
        Assert.Same(shape, filled);
        Assert.Equal("m1", shape.Fill?.MediaId);

        var (box, made) = DropRules.DropMediaOnCanvas(slide, null, item, new Point(1500, 800), new SceneSize(1920, 1080));
        Assert.True(made);
        Assert.Equal(2, slide.Objects.Count);
        Assert.Equal(960, box.Width);
        Assert.Equal(540, box.Height);
        Assert.Equal(1920 - 960, box.X); // centred on the drop, kept on the slide
        Assert.Equal(800 - 270, box.Y);
    }

    [Fact]
    public void MediaOnATileIsItsBackground()
    {
        var slide = new Slide { Id = "s", Name = "", Objects = [] };
        var item = new MediaItem { Id = "m1", Name = "Loop", MediaKind = MediaKind.Video, Classification = MediaClassification.Background, FileHash = "h", FileName = "a.mp4", FileStatus = MediaFileStatus.Ready, StatusDetail = "" };
        DropRules.DropMediaAsBackground(slide, item);
        Assert.Equal("m1", slide.Background?.MediaId);
    }
}
