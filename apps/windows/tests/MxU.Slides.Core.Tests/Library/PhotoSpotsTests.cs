using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Notes;
using MxU.Slides.Core.Tests.Notes;

namespace MxU.Slides.Core.Tests.Library;

// Port of the Mac's PresenterCoreTests/PhotoSpotsTests.swift, whole: the spots themselves, the
// starter designs and their repairs, and Make Slides filling spots from notes (SlidePlanner,
// NoteSlideBuilder, NoteSlideUpdate). Tests the Mac doesn't have are marked "Windows".
public class PhotoSpotsTests : ScratchFolderTest
{
    private static SlideObject Spot(string id, double x, double y) => new()
    {
        Id = id, ObjectKind = SlideObjectKind.Shape, Name = "Shape", Text = "", X = x, Y = y, Width = 400, Height = 300, PhotoSpot = true,
    };

    private static readonly Theme Theme = new()
    {
        Id = "t", Name = "T", FontFamily = "Helvetica", FontSize = 72, TextColorHex = "#FFFFFFFF", BackgroundColorHex = "#000000FF",
        Slides =
        [
            new Slide { Id = "body", Name = "Body", Objects = [new SlideObject { Id = "b", ObjectKind = SlideObjectKind.Text, Name = "Text", Text = "" }] },
            new Slide { Id = "p1", Name = "Photo + Text", Folder = "Full Slide", Objects = [Spot("s", 0, 0), new SlideObject { Id = "t", ObjectKind = SlideObjectKind.Text, Name = "Words", Text = "Sample" }] },
            new Slide { Id = "p2", Name = "2 Photos", Folder = "Full Slide", Objects = [Spot("b", 900, 0), Spot("a", 0, 10)] },
            new Slide { Id = "p3", Name = "3 Photos", Folder = "Full Slide", Objects = [Spot("a", 0, 0), Spot("b", 500, 0), Spot("c", 1000, 0)] },
        ],
    };

    private static Dictionary<string, SlideRowOverride> PhotoText() => new() { ["g"] = new SlideRowOverride(ThemeSlideName: "Photo + Text") };

    private static MarkedGroup Picture(string text, params string[] media) => new("g", "picture", NoteSlideMarks.Shape.Paragraph, text, [], media, []);

    private static MediaScaleMode? FirstScale(Slide slide) => slide.Objects.FirstOrDefault()?.Fill?.MediaScaleMode;

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
    public void APicturePassageTakesItsChosenDesignAndItsWords()
    {
        var row = new DeterministicSlidePlanner().Plan([Picture("Scan to give", "k1", "k2")], Theme, PhotoText()).Rows[0];
        Assert.True(row.ThemeSlideName == "Photo + Text" && row.PhotosPerSlide == 1);
        Assert.True(row.Parts is [[{ Name: "Words", Text: "Scan to give" }]] && row.Notes is null);
        var built = NoteSlideBuilder.Build(new SlidePlan([row]), new Dictionary<string, BlankPlacement>(), new NoteSlideBuilder.BlankStyle(), media: k => k == "k1" ? "m1" : null);
        Assert.Equal(2, built.Slides.Count); // one slide each by default
        Assert.All(built.Slides, s => Assert.True(s.ThemeSlideName == "Photo + Text" && s.Objects.Count == 2));
        Assert.True(built.Slides[0].Objects[0].Fill?.MediaId == "m1" && built.Slides[0].Objects[0].IsPhotoSpot());
        Assert.Equal(NoteMedia.PendingId("k2"), built.Slides[1].Objects[0].Fill?.MediaId);
        Assert.Null(FirstScale(built.Slides[0]));
    }

    [Fact]
    public void APictureFarFromItsSpotsShapeTakesScaleAndBlur()
    {
        Assert.True(PictureShapes.Of(0.5, 4.0 / 3) == PictureShape.Tall && PictureShapes.Of(3, 4.0 / 3) == PictureShape.Wide);
        Assert.True(PictureShapes.Of(1.5, 4.0 / 3) is null && PictureShapes.Of(null, 1) is null);
        Assert.Equal("Scale and Blur (tall picture)", PictureShape.Tall.Caption());
        var group = Picture("", "tall", "wide", "unknown");
        var row = new DeterministicSlidePlanner(PictureAspects: new Dictionary<string, double> { ["tall"] = 0.5, ["wide"] = 1.5 }).Plan([group], Theme, PhotoText()).Rows[0];
        Assert.Equal(new Dictionary<string, PictureShape> { ["tall"] = PictureShape.Tall }, row.PictureShapes);
        var built = NoteSlideBuilder.Build(new SlidePlan([row]));
        Assert.Equal([MediaScaleMode.ScaleAndBlur, null, null], built.Slides.Select(FirstScale));
        Assert.Equal(new Dictionary<string, PictureShape> { [built.Slides[0].Id] = PictureShape.Tall }, built.PictureShapes);
        Assert.Equal(MediaScaleMode.ScaleAndBlur, FirstScale(NoteSlideUpdate.Updating(built.Slides, row).Updated[0]));
        var fitRow = new DeterministicSlidePlanner(new SlideOptions { PictureScale = MediaScaleMode.Fit }, new Dictionary<string, double> { ["tall"] = 0.5 })
            .Plan([group], Theme, PhotoText()).Rows[0];
        Assert.Empty(fitRow.PictureShapes);
        Assert.All(NoteSlideUpdate.Updating(built.Slides, fitRow).Updated, s => Assert.Equal(MediaScaleMode.Fit, FirstScale(s)));
    }

    [Fact]
    public void AScalePickedForThePassageWinsInPhotoSpots()
    {
        var group = Picture("", "tall", "wide");
        var tall = new Dictionary<string, double> { ["tall"] = 0.5 };
        var auto = new DeterministicSlidePlanner(PictureAspects: tall).Plan([group], Theme, PhotoText()).Rows[0];
        Assert.True(!auto.ScaleChosen && auto.ShownScale == MediaScaleMode.ScaleAndBlur); // the first picture took Scale and Blur for its shape

        var fill = new SlideOptions { PictureScale = MediaScaleMode.Fill };
        var picked = new DeterministicSlidePlanner(fill, tall, true).Plan([group], Theme, PhotoText()).Rows[0];
        Assert.True(picked.ScaleChosen && picked.PictureShapes.Count == 0 && picked.ShownScale == MediaScaleMode.Fill);
        Assert.All(NoteSlideBuilder.Build(new SlidePlan([picked])).Slides, s => Assert.Null(FirstScale(s)));
        Assert.All(NoteSlideUpdate.Updating(NoteSlideBuilder.Build(new SlidePlan([auto])).Slides, picked).Updated, s => Assert.Null(FirstScale(s)));

        var blurPick = new DeterministicSlidePlanner(new SlideOptions { PictureScale = MediaScaleMode.ScaleAndBlur }, PictureScaleChosen: true).Plan([group], Theme, PhotoText()).Rows[0];
        Assert.Equal(MediaScaleMode.ScaleAndBlur, blurPick.ShownScale);
        Assert.All(NoteSlideBuilder.Build(new SlidePlan([blurPick])).Slides, s => Assert.Equal(MediaScaleMode.ScaleAndBlur, FirstScale(s)));
        Assert.Equal(MediaScaleMode.Fill, new DeterministicSlidePlanner(fill).Plan([group], null, N.NoOverrides).Rows[0].ShownScale);
    }

    [Fact]
    public void PicturesTogetherFindTheDesignWithThatManySpots()
    {
        var planner = new DeterministicSlidePlanner(new SlideOptions { PicturesTogether = true });
        var three = Picture("", "a", "b", "c", "d");
        var row = planner.Plan([three], Theme, N.NoOverrides).Rows[0];
        Assert.True(row.ThemeSlideName == "3 Photos" && row.PhotosPerSlide == 3);
        Assert.Equal([3, 1], NoteSlideBuilder.Build(new SlidePlan([row])).Slides.Select(s => s.Objects.Count)); // four pictures: three together, then one
        Assert.Equal("2 Photos", planner.Plan([Picture("", "a", "b")], Theme, N.NoOverrides).Rows[0].ThemeSlideName);
        Assert.Null(planner.Plan([Picture("", "a")], Theme, N.NoOverrides).Rows[0].ThemeSlideName);
        Assert.Null(planner.Plan([three], null, N.NoOverrides).Rows[0].ThemeSlideName);
        Assert.Null(new DeterministicSlidePlanner().Plan([Picture("", "a")], Theme, new Dictionary<string, SlideRowOverride> { ["g"] = new(ThemeSlideName: "Body") }).Rows[0].ThemeSlideName);
    }

    [Fact]
    public void TheImportFillsPicturesInSpots()
    {
        var slide = new Slide { Id = "s", Name = "", Objects = [PhotoSpots.Photo(NoteMedia.PendingId("k1"), null), PhotoSpots.Photo(NoteMedia.PendingId("k2"), null)] };
        var filled = NoteMedia.Filling([slide], [new NoteMediaImport { Key = "k1", MediaId = "m1" }])!;
        Assert.Equal(["m1"], filled[0].Objects.Select(o => o.Fill?.MediaId));
        Assert.StartsWith(SlideMediaFacts.NeedsMediaNote, filled[0].Notes);
    }

    [Fact]
    public void UpdatingAPictureInASpotTakesTheScale()
    {
        var row = new DeterministicSlidePlanner(new SlideOptions { PictureScale = MediaScaleMode.Stretch }).Plan([Picture("", "k")], Theme, PhotoText()).Rows[0];
        var made = new Slide { Id = "s", Name = "", Objects = [PhotoSpots.Photo("m", MediaScaleMode.Fit)] };
        Assert.Equal(MediaScaleMode.Stretch, FirstScale(NoteSlideUpdate.Updating([made], row).Updated[0]));
    }

    [Fact]
    public void PicturesTogetherDecodesOffByDefault()
    {
        Assert.False(SlideDesignMap.Decode("""{"options":{}}""").Options.PicturesTogether);
        Assert.True(SlideDesignMap.Decode("""{"options":{"picturesTogether":true}}""").Options.PicturesTogether);
    }

    [Fact]
    public void TwoPicturesWithOneFileNameDownloadApart()
    {
        var first = NoteMedia.DownloadPath(new NoteBlock { Type = NoteBlockType.Image, Url = "/rails/active_storage/blobs/a/image.png" }, 0, "run");
        var second = NoteMedia.DownloadPath(new NoteBlock { Type = NoteBlockType.Image, Url = "/rails/active_storage/blobs/b/image.png" }, 1, "run");
        Assert.True(first != second && Path.GetFileName(first) == "image.png" && Path.GetFileName(second) == "image.png");
    }

    [Fact]
    public void WordsNeverLandOnAPhotoDesignByName()
    {
        foreach (var pack in Enum.GetValues<StarterPack>())
        {
            var theme = pack.MakeTheme();
            foreach (var kind in SlideKinds.All.Where(k => k != SlideKind.Picture))
            {
                var name = NoteSlideBuilder.DesignName(kind, theme);
                var design = theme.Slides?.FirstOrDefault(s => s.Name == name);
                Assert.True(design is null || design.PhotoSpotsOf().Count == 0, $"{pack} {kind} landed on {name}");
            }
        }
    }

    [Fact]
    public void ASpotFillsUnlessTheScaleSaysOtherwise()
    {
        Assert.Equal(MediaScaleMode.Fill, MediaScaleMode.ScaleAndBlur.InPhotoSpot());
        Assert.True(MediaScaleMode.Fit.InPhotoSpot() == MediaScaleMode.Fit && MediaScaleMode.Stretch.InPhotoSpot() == MediaScaleMode.Stretch);
        var group = Picture("", "k");
        var row = new DeterministicSlidePlanner(new SlideOptions { PictureScale = MediaScaleMode.Fit }).Plan([group], Theme, PhotoText()).Rows[0];
        Assert.Equal(MediaScaleMode.Fit, FirstScale(NoteSlideBuilder.Build(new SlidePlan([row])).Slides[0]));
        var made = new Slide { Id = "s", Name = "", Objects = [PhotoSpots.Photo("m", MediaScaleMode.Fit)] };
        var blurRow = new DeterministicSlidePlanner().Plan([group], Theme, PhotoText()).Rows[0];
        Assert.Null(FirstScale(NoteSlideUpdate.Updating([made], blurRow).Updated[0]));
    }

    // Windows: the engine's filled(with:), which the Mac covers through PhotoSpotSceneTests.
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
