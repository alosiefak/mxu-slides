using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Library;

// Port of the parts of the Mac's PresenterCoreTests/PhotoSpotsTests.swift about the spots and
// the starter designs. The rest drive Make Slides (SlidePlanner, NoteSlideBuilder), the Notes port.
public class PhotoSpotsTests : ScratchFolderTest
{
    private static SlideObject Spot(string id, double x, double y) => new()
    {
        Id = id, ObjectKind = SlideObjectKind.Shape, Name = "Shape", Text = "", X = x, Y = y, Width = 400, Height = 300, PhotoSpot = true,
    };

    [Fact]
    public void SpotsReadLeftToRightThenTopToBottom()
    {
        var slide = new Slide { Id = "x", Name = "", Objects = [Spot("low", 0, 600), Spot("right", 900, 20), Spot("left", 0, 0)] };
        Assert.Equal(["left", "right", "low"], slide.PhotoSpotsOf().Select(o => o.Id));
        Assert.Null(PhotoSpots.Photo("m", MediaScaleMode.Fill).Fill?.MediaScaleMode);
        Assert.True(PhotoSpots.Photo("m", MediaScaleMode.Fit).IsPhotoSpot());
        var text = new SlideObject { Id = "t", ObjectKind = SlideObjectKind.Text, Name = "T", Text = "", PhotoSpot = true };
        Assert.False(text.IsPhotoSpot(), "only a shape can be a spot");
    }

    [Fact]
    public void APictureFarFromItsSpotsShapeTakesScaleAndBlur()
    {
        Assert.Equal(PictureShape.Tall, PictureShapes.Of(0.5, 4.0 / 3));
        Assert.Equal(PictureShape.Wide, PictureShapes.Of(3, 4.0 / 3));
        Assert.Null(PictureShapes.Of(1.5, 4.0 / 3));
        Assert.Null(PictureShapes.Of(null, 1));
        Assert.Equal("Scale and Blur (tall picture)", PictureShape.Tall.Caption());
    }

    [Fact]
    public void ASpotFillsUnlessTheScaleSaysOtherwise()
    {
        Assert.Equal(MediaScaleMode.Fill, MediaScaleMode.ScaleAndBlur.InPhotoSpot());
        Assert.Equal(MediaScaleMode.Fit, MediaScaleMode.Fit.InPhotoSpot());
        Assert.Equal(MediaScaleMode.Stretch, MediaScaleMode.Stretch.InPhotoSpot());
    }

    [Fact]
    public void AFilledSpotKeepsTheDesignsLookAndTakesThePhoto()
    {
        var spot = Spot("design.spot", 100, 100);
        spot.CornerRadius = 12;
        spot.Stroke = new ObjectStroke { ColorHex = "#FFFFFFFF", Width = 6 };
        spot.AnimationSteps = [new AnimationStep { Id = "in", Kind = AnimationKind.In, Animation = StepAnimation.Fade, Trigger = AnimationTrigger.WithPrevious, DurationSeconds = 0.5 }];
        var photo = PhotoSpots.Photo("m1", MediaScaleMode.Fit);

        var filled = spot.FilledWith(photo);
        Assert.Equal(photo.Id, filled.Id);
        Assert.Equal("m1", filled.Fill?.MediaId);
        Assert.Equal((100.0, 100.0, 400.0, 300.0), (filled.X, filled.Y, filled.Width, filled.Height));
        Assert.Equal(12, filled.CornerRadius);
        Assert.Equal(6, filled.Stroke?.Width);
        Assert.Equal("in", Assert.Single(filled.AnimationSteps!).Id);

        photo.X = 10; photo.Y = 20; photo.Width = 30; photo.Height = 40;
        Assert.Equal((10.0, 20.0, 30.0, 40.0), spot.FilledWith(photo) is var moved ? (moved.X, moved.Y, moved.Width, moved.Height) : default);
        Assert.Equal(100, spot.X); // the design itself is left as it was
    }

    public static TheoryData<StarterPack> Packs() => [.. StarterPacks.All];

    [Theory]
    [MemberData(nameof(Packs))]
    public void EveryPackShipsPhotoDesignsInEachFolder(StarterPack pack)
    {
        var slides = pack.MakeTheme().Slides ?? [];
        (string Name, string Folder, int Spots, bool Text)[] expected =
        [
            ("Photo", "Full Slide", 1, false), ("Photo + Text", "Full Slide", 1, true),
            ("2 Photos", "Full Slide", 2, false), ("3 Photos", "Full Slide", 3, false),
            ("Photo + Text (Lower Third)", "Lower Thirds", 1, true), ("Photo (Lower Third)", "Lower Thirds", 1, false),
            ("Photo (Side Third)", "Side Thirds", 1, false), ("Photo + Text (Side Third)", "Side Thirds", 1, true),
        ];
        foreach (var (name, folder, spots, text) in expected)
        {
            var design = slides.FirstOrDefault(s => s.Name == name);
            Assert.True(design?.Folder == folder, $"{pack.Title()}/{name} folder");
            Assert.True(design!.PhotoSpotsOf().Count == spots, $"{pack.Title()}/{name} spots");
            Assert.True(design.Objects.Any(o => o.ObjectKind == SlideObjectKind.Text) == text, $"{pack.Title()}/{name} words");
            Assert.True(design.Objects.All(o => !o.IsPhotoSpot() || (o.AnimationSteps ?? []).Count > 0), $"{pack.Title()}/{name} spots arrive");
            Assert.True(design.Objects.All(o => (o.AnimationSteps ?? []).All(s => s.Kind != AnimationKind.Out)), $"{pack.Title()}/{name} never leaves on a click");
        }
    }

    [Fact]
    public void ThePointsDesignShrinksToItsFloor()
    {
        var points = StarterPack.Digital.MakeTheme().Slides!.Single(s => s.Name == "Points").Objects
            .Single(o => o.Id == $"{StarterPack.Digital.ThemeId()}.points.text");
        Assert.True(points.TextStyle!.AutoShrink);
        Assert.Equal(StarterPacks.PointsFloor, points.TextStyle.MinFontSize);
        Assert.Equal(28, points.TextStyle.ParagraphSpacing);

        // Port of MakeSlidesFixturesTests.swift's shrinkingPoints check: only a box that never chose.
        var pack = StarterPack.CleanGeometric;
        var fresh = pack.MakeTheme().Slides!;
        var stale = MxuJson.Clone(fresh);
        var box = stale.Single(s => s.Name == "Points").Objects.Single(o => o.Id == $"{pack.ThemeId()}.points.text");
        box.TextStyle!.AutoShrink = null;
        box.TextStyle.MinFontSize = null;
        Assert.True(MxuJson.StructurallyEqual(fresh, pack.ShrinkingPoints(stale)));
        var chosen = MxuJson.Clone(stale);
        chosen.Single(s => s.Name == "Points").Objects.Single(o => o.Id == box.Id).TextStyle!.AutoShrink = false;
        Assert.True(MxuJson.StructurallyEqual(chosen, pack.ShrinkingPoints(chosen)));
    }

    [Fact]
    public void TheRepairTakesThePhotoLeavesOut()
    {
        var pack = StarterPack.Digital;
        var fresh = pack.MakeTheme().Slides!;
        var leave = new AnimationStep { Id = "leave", Kind = AnimationKind.Out, Animation = StepAnimation.Fade, Trigger = AnimationTrigger.OnClick, DurationSeconds = 0.4 };
        Slide Leaving(Slide slide)
        {
            var s = MxuJson.Clone(slide);
            s.Objects[0].AnimationSteps = [.. s.Objects[0].AnimationSteps ?? [], MxuJson.Clone(leave)];
            s.AnimationOrder = [.. s.AnimationOrder ?? [], leave.Id];
            return s;
        }
        var stale = fresh.Select(s => s.Id.StartsWith($"{pack.ThemeId()}.photo", StringComparison.Ordinal) ? Leaving(s) : s).ToList();
        Assert.False(MxuJson.StructurallyEqual(stale, fresh));
        Assert.True(MxuJson.StructurallyEqual(fresh, pack.StayingPhotos(stale)));
        var mine = Leaving(new Slide { Id = "mine", Name = "My Photo", Objects = [new SlideObject { Id = "p", ObjectKind = SlideObjectKind.Shape, Name = "Photo", Text = "" }] });
        List<Slide> withMine = [.. fresh, mine];
        Assert.True(MxuJson.StructurallyEqual(withMine, pack.StayingPhotos(withMine)));
    }

    [Fact]
    public void TheRepairAddsPhotoDesignsAndKeepsTheUsersOwn()
    {
        var pack = StarterPack.CleanGeometric;
        var mine = new Slide { Id = "mine", Name = "Photo", Objects = [], Folder = "Full Slide" };
        var seeded = pack.MakeTheme().Slides!.Where(s => !s.Id.StartsWith($"{pack.ThemeId()}.photo", StringComparison.Ordinal)).ToList();
        var repaired = pack.AddingPhotoDesigns([.. seeded, mine]);
        Assert.Equal(["mine"], repaired.Where(s => s.Name == "Photo").Select(s => s.Id)); // a design the user calls Photo keeps its place
        Assert.Contains(repaired, s => s.Name == "3 Photos");
        Assert.Contains(repaired, s => s.Name == "Photo (Side Third)");
        var lower = repaired.FindIndex(s => s.Name == "Photo + Text (Lower Third)");
        Assert.Equal("Lower Thirds", repaired[lower - 1].Folder); // joins the end of its folder
        Assert.True(MxuJson.StructurallyEqual(repaired, pack.AddingPhotoDesigns(repaired)));
    }

    [Fact]
    public void AddingLyricsDesignsFillsOnlyTheGaps()
    {
        // Port of AnimationPresetTests.swift's addingLyricsDesignsFillsOnlyTheGaps.
        var pack = StarterPack.EditorialSerif;
        var full = pack.MakeTheme().Slides!;
        Assert.True(MxuJson.StructurallyEqual(full, pack.AddingLyricsDesigns(full)), "a complete theme is untouched");
        var missing = full.Where(s => !s.Id.StartsWith($"{pack.ThemeId()}.lyrics", StringComparison.Ordinal)).ToList();
        var repaired = pack.AddingLyricsDesigns(missing);
        Assert.Equal("Lyrics", repaired[0].Name); // the Full Slide design leads
        Assert.Equal(full.Count, repaired.Count);
    }

    [Fact]
    public void ALibraryFromTheLastRevisionGainsThePhotoDesignsAndKeepsItsEdits()
    {
        var library = new LibraryStore(Path.Combine(Scratch, "Library"));
        library.Load();
        LibrarySeeder.RefreshStarterPacks(library);

        // A library seeded by the build before the photo designs, with an edit to a starter theme.
        foreach (var pack in StarterPacks.All)
        {
            var theme = MxuJson.Clone(library.Get<Theme>(pack.ThemeId())!);
            theme.Slides = [.. theme.Slides!.Where(s => !s.Id.StartsWith($"{pack.ThemeId()}.photo", StringComparison.Ordinal))];
            if (pack == StarterPack.BoldBlocks) theme.Slides[0].Name = "My Lyrics";
            library.Save(theme);
        }
        File.WriteAllText(Path.Combine(library.Root, "starter-revision.txt"), LibrarySeeder.GentleRepairFrom.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var reopened = new LibraryStore(library.Root);
        reopened.Load();
        LibrarySeeder.RefreshStarterPacks(reopened);
        foreach (var pack in StarterPacks.All)
            Assert.Contains(reopened.Get<Theme>(pack.ThemeId())!.Slides!, s => s.Name == "3 Photos");
        Assert.Equal("My Lyrics", reopened.Get<Theme>(StarterPack.BoldBlocks.ThemeId())!.Slides![0].Name); // the edit stayed
        Assert.Equal(StarterPacks.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            File.ReadAllText(Path.Combine(reopened.Root, "starter-revision.txt")).Trim());
    }
}
