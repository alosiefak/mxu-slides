using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Outputs;

// Ports of OutputEngineTests/ScreenMapTests.swift and LayerPlacementRoutingTests.swift, and of
// PresenterCoreTests/ScreenMapSchemaTests.swift's preset half.
public class ScreenMapTests
{
    private static readonly SceneRect Wall = new(1920, 0, 1920, 1080);
    private static readonly SceneRect Ribbon = new(0, 1080, 5760, 192);

    [Fact]
    public void NoPlacementsOrAScreenOffTheMapFollowTheDot()
    {
        Assert.Null(ScreenMapResolver.Resolve([], Wall));
        Assert.Null(ScreenMapResolver.Resolve([new MapPlacement(Wall)], null));
    }

    [Fact]
    public void APlacementOnTheScreenFillsItAndASpanContinuesPastIt()
    {
        Assert.Equal([new ResolvedPlacement(new SceneRect(0, 0, 1, 1), true)], ScreenMapResolver.Resolve([new MapPlacement(Wall, Fill: true)], Wall));
        Assert.Equal([new ResolvedPlacement(new SceneRect(-1, 0, 2, 1), false)], ScreenMapResolver.Resolve([new MapPlacement(new SceneRect(0, 0, 3840, 1080))], Wall));
        var corner = new MapPlacement(new SceneRect(3200, 600, 640, 360));
        var hits = ScreenMapResolver.Resolve([new MapPlacement(Wall), corner], Wall)!;
        Assert.Equal(2, hits.Count);
        Assert.Equal((3200.0 - 1920) / 1920, hits[^1].Frame.X, 4);
        Assert.Empty(ScreenMapResolver.Resolve([new MapPlacement(Ribbon)], Wall)!); // placed elsewhere: nothing here
    }

    [Fact]
    public void StartingPoints()
    {
        var left = new SceneRect(0, 0, 1920, 1080);
        Assert.Equal([left, Wall], ScreenMapResolver.OnePerScreen([left, Wall]).Select(p => p.Rect));
        Assert.Equal([new SceneRect(0, 0, 3840, 1080)], ScreenMapResolver.AcrossScreens([left, Wall]).Select(p => p.Rect));
        Assert.Empty(ScreenMapResolver.AcrossScreens([]));
    }

    [Fact]
    public void TheMapIsAsBigAsItsPiecesAndRoundTrips()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var map = new ScreenMap().Placing(a, Wall).Placing(b, Ribbon);
        Assert.Equal(new SceneRect(0, 0, 5760, 1272), map.Bounds);
        Assert.Equal(Wall, map.RectFor(a));
        Assert.Null(map.Placing(b, null).RectFor(b));
        Assert.Equal(map, MxuJson.Deserialize<ScreenMap>(MxuJson.Serialize(map)));
    }

    [Fact]
    public void AFirstMapLaysScreensSideBySideAndSeatsArrivalsToTheRight()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var map = ScreenMap.SideBySide([(a, 1920, 1080), (b, 1920, 192)]);
        Assert.Equal(new SceneRect(0, 0, 1920, 1080), map.RectFor(a));
        Assert.Equal(new SceneRect(1920, 0, 1920, 192), map.RectFor(b));
        Assert.Equal(new SceneRect(3840, 0, 1920, 1080), map.NextSeat(1920, 1080));
        Assert.Equal("Main Room", map.Name);
    }

    [Fact]
    public void SettingTheMapFiresTheSaveHookOnce()
    {
        var outputs = new OutputConfiguration();
        var saves = 0;
        outputs.ScreenMapChanged += () => saves++;
        var map = new ScreenMap().Placing(Guid.NewGuid(), Wall);
        outputs.SetScreenMap(map);
        outputs.SetScreenMap(MxuJson.Deserialize<ScreenMap>(MxuJson.Serialize(map)));
        Assert.Equal(map, outputs.ScreenMap);
        Assert.Equal(1, saves);
    }

    [Fact]
    public void APresetsPlacementsGroupByLayer()
    {
        var preset = new OutputPreset
        {
            Id = "p", Name = "Wall", Assignments = [],
            Placements =
            [
                new LayerPlacement { LayerId = "slide", X = 0, Y = 0, Width = 100, Height = 50, Scale = MediaScaleMode.Fill },
                new LayerPlacement { LayerId = "slide", X = 200, Y = 0, Width = 100, Height = 50 },
            ],
            Layers = [new CustomLayer { Id = "lower", Name = "Lower", Above = "slide" }],
        };
        var placements = OutputConfiguration.PlacementsOf(preset);
        Assert.Equal([true, false], placements["slide"].Select(p => p.Fill));
        Assert.Empty(OutputConfiguration.PlacementsOf(null));
        // The new fields round-trip, and an older preset reads with none.
        var read = MxuJson.Deserialize<OutputPreset>(MxuJson.Serialize(preset));
        Assert.Equal("slide", read.Layers![0].Above);
        Assert.Null(MxuJson.Deserialize<OutputPreset>("""{"id":"x","name":"X","assignments":[]}""").Placements);
    }

    // MARK: Routing (LayerPlacementRoutingTests.swift)

    private static RenderScene Program(double side = 1920)
    {
        var scene = new RenderScene(new SceneSize(side, side * 9 / 16));
        scene.AddItem(OutputFixtures.Solid("bg", default, SceneColor.White), LayerKind.LoopingVideos);
        scene.AddItem(OutputFixtures.Solid("lyric", default, SceneColor.White), LayerKind.Slide);
        return scene;
    }

    private static (Guid Left, Guid Right) WallOf(OutputConfiguration outputs)
    {
        var left = outputs.AddScreen("Left", 1920, 1080)!.Id;
        var right = outputs.AddScreen("Right", 1920, 1080)!.Id;
        outputs.SetScreenMap(new ScreenMap().Placing(left, new SceneRect(0, 0, 1920, 1080)).Placing(right, new SceneRect(1920, 0, 1920, 1080)));
        return (left, right);
    }

    private static RenderScene Preview(OutputConfiguration outputs, Guid screen) => outputs.PreviewProvider(SwiftUuid.Format(screen))();

    [Fact]
    public void NoPlacementsRendersExactlyAsBefore()
    {
        var outputs = new OutputConfiguration { SceneProvider = () => Program() };
        var (left, _) = WallOf(outputs);
        outputs.SetLayerRouting(new Dictionary<string, IReadOnlySet<string>> { [SwiftUuid.Format(left)] = new HashSet<string> { "slide" } });
        var before = Preview(outputs, left);
        outputs.SetLayerPlacements(new Dictionary<string, IReadOnlyList<MapPlacement>>());
        OutputFixtures.SameScene(before, Preview(outputs, left));
        outputs.SetLayerPlacements(new Dictionary<string, IReadOnlyList<MapPlacement>> { ["alerts"] = [new MapPlacement(new SceneRect(0, 0, 100, 100))] });
        var after = Preview(outputs, left);
        Assert.Equal(before[LayerKind.Slide].Items, after[LayerKind.Slide].Items);
        Assert.Equal(before[LayerKind.LoopingVideos].Items, after[LayerKind.LoopingVideos].Items);
    }

    [Fact]
    public void ACustomLayerRoutesAndPlacesByItsId()
    {
        var outputs = new OutputConfiguration
        {
            SceneProvider = () =>
            {
                var scene = Program();
                scene.InsertCustomLayer("lower", "Lower", "slide");
                scene.AddItem(OutputFixtures.Solid("bug", default, SceneColor.White), "lower");
                return scene;
            },
        };
        var (left, right) = WallOf(outputs);
        outputs.SetLayerRouting(new Dictionary<string, IReadOnlySet<string>> { [SwiftUuid.Format(left)] = new HashSet<string> { "slide" } });
        RenderLayer Lower(Guid screen) => Preview(outputs, screen).Layer("lower")!;
        Assert.Empty(Lower(left).Items); // no dot for it on the left
        Assert.Single(Lower(right).Items); // no assignment = everything
        outputs.SetLayerPlacements(new Dictionary<string, IReadOnlyList<MapPlacement>> { ["lower"] = [new MapPlacement(new SceneRect(0, 900, 1920, 180))] });
        var placed = Lower(left);
        Assert.Equal(new SceneRect(0, 900, 1920, 180), placed.Placed!.Frames[0].Rect);
        Assert.Equal(["bug"], placed.Items.Select(i => i.Id));
        Assert.Empty(Lower(right).Items); // placed elsewhere = nothing here
    }

    [Fact]
    public void ASpannedLayerLandsHalfOnEachScreenAndIgnoresTheDot()
    {
        var outputs = new OutputConfiguration { SceneProvider = () => Program() };
        var (left, right) = WallOf(outputs);
        outputs.SetLayerRouting(new Dictionary<string, IReadOnlySet<string>> { [SwiftUuid.Format(left)] = new HashSet<string> { "slide" } });
        outputs.SetLayerPlacements(new Dictionary<string, IReadOnlyList<MapPlacement>>
        {
            ["loopingVideos"] = [new MapPlacement(new SceneRect(0, 0, 3840, 1080), Fill: true)],
        });
        var onLeft = Preview(outputs, left)[LayerKind.LoopingVideos];
        Assert.Equal(new SceneSize(1920, 1080), onLeft.Placed!.CanvasSize);
        Assert.Equal(new PlacedLayer.Frame(new SceneRect(0, 0, 3840, 1080), true), onLeft.Placed.Frames.Single());
        Assert.Equal(new SceneRect(-1920, 0, 3840, 1080), Preview(outputs, right)[LayerKind.LoopingVideos].Placed!.Frames[0].Rect); // the right piece shows the right half
        Assert.Equal(["bg"], onLeft.Items.Select(i => i.Id)); // the picture is the layer itself
    }

    [Fact]
    public void PlacedElsewhereDrawsNothingHereAndOffTheMapFollowsTheDot()
    {
        var outputs = new OutputConfiguration { SceneProvider = () => Program() };
        var (left, right) = WallOf(outputs);
        var lobby = outputs.AddScreen("Lobby", 1920, 1080)!.Id;
        outputs.SetLayerPlacements(new Dictionary<string, IReadOnlyList<MapPlacement>>
        {
            ["loopingVideos"] = [new MapPlacement(new SceneRect(1920, 0, 1920, 1080))],
        });
        Assert.Empty(Preview(outputs, left)[LayerKind.LoopingVideos].Items);
        Assert.NotEmpty(Preview(outputs, right)[LayerKind.LoopingVideos].Items);
        var offMap = Preview(outputs, lobby)[LayerKind.LoopingVideos];
        Assert.NotEmpty(offMap.Items);
        Assert.Null(offMap.Placed); // a screen off the map keeps its dot
    }

    [Fact]
    public void APlacedSlideLayerTakesTheLookOfTheScreenItMostlyLandsOn()
    {
        var themed = Program();
        themed.AddItem(OutputFixtures.Solid("themed", default, SceneColor.White), LayerKind.Slide);
        var outputs = new OutputConfiguration { SceneProvider = () => Program(), VariantSceneProvider = key => key == "wall-look" ? themed : null };
        var (left, right) = WallOf(outputs);
        outputs.SetSlideThemeRouting(new Dictionary<string, string> { [SwiftUuid.Format(right)] = "wall-look" });
        outputs.SetLayerPlacements(new Dictionary<string, IReadOnlyList<MapPlacement>> { ["slide"] = [new MapPlacement(new SceneRect(1440, 0, 1920, 1080))] });
        var onLeft = Preview(outputs, left)[LayerKind.Slide];
        Assert.Equal(["lyric", "themed"], onLeft.Items.Select(i => i.Id)); // the left piece draws the right piece's look
        Assert.Equal(1440, onLeft.Placed!.Frames[0].Rect.X);
    }

    [Fact]
    public void TheAnimatedProgramPlacesTheSameWay()
    {
        var outputs = new OutputConfiguration();
        var (left, _) = WallOf(outputs);
        outputs.SetLayerPlacements(new Dictionary<string, IReadOnlyList<MapPlacement>> { ["loopingVideos"] = [new MapPlacement(new SceneRect(960, 0, 1920, 1080))] });
        var scene = AnimatedScene.From(Program());
        var placed = outputs.Program(outputs.Feed(SwiftUuid.Format(left)), scene);
        Assert.Equal(new SceneRect(960, 0, 1920, 1080), placed[LayerKind.LoopingVideos].Placed!.Frames[0].Rect);
        Assert.Null(scene[LayerKind.LoopingVideos].Placed); // the program itself is untouched
    }

    // The app's windows draw the animated program, so the dominant screen's look must reach it too.
    [Fact]
    public void TheAnimatedPlacedSlideLayerTakesTheDominantScreensLookToo()
    {
        var themedScene = Program();
        themedScene.AddItem(OutputFixtures.Solid("themed", default, SceneColor.White), LayerKind.Slide);
        var themed = AnimatedScene.From(themedScene);
        var outputs = new OutputConfiguration();
        var (left, right) = WallOf(outputs);
        outputs.SetSlideThemeRouting(new Dictionary<string, string> { [SwiftUuid.Format(right)] = "wall-look" });
        outputs.SetLayerPlacements(new Dictionary<string, IReadOnlyList<MapPlacement>> { ["slide"] = [new MapPlacement(new SceneRect(1440, 0, 1920, 1080))] });
        AnimatedScene? Looks(string key) => key == "wall-look" ? themed : null;
        var plain = AnimatedScene.From(Program());
        var onLeft = outputs.Program(outputs.Feed(SwiftUuid.Format(left)), plain, Looks)[LayerKind.Slide];
        Assert.Equal(["lyric", "themed"], onLeft.Items.Select(i => i.Id)); // the left piece draws the right piece's look
        // The right screen already shows that look: its picture is its own layer, and it keeps it.
        var onRight = outputs.Program(outputs.Feed(SwiftUuid.Format(right)), themed, Looks)[LayerKind.Slide];
        Assert.Equal(["lyric", "themed"], onRight.Items.Select(i => i.Id));
        Assert.Equal(["lyric"], outputs.Program(outputs.Feed(SwiftUuid.Format(left)), plain)[LayerKind.Slide].Items.Select(i => i.Id)); // no looks given: as before
    }
}
