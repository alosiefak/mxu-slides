using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;
using LayerTransition = MxU.Slides.Core.Animation.SceneTransition;

namespace MxU.Slides.Core.Tests.Outputs;

// A key feed: a screen that shows only words and overlays over black or green, so a video
// switcher can lay them over the camera (KeyFeeds, OutputConfiguration.KeyFeed.cs).
public class KeyFeedTests
{
    private static readonly SceneSize Canvas = SceneSize.Hd;

    private static RenderItem Shape(string id, SceneRect frame, SceneFill? fill = null) => new()
    {
        Id = id, Frame = frame,
        Content = new ItemContent.Shape(new ShapeStyle(SceneShapeKind.Rect, fill ?? new SceneFill.Solid(new SceneColor(0.1, 0.2, 0.6)))),
    };

    private static RenderItem Text(string id, SceneRect frame) => new()
    {
        Id = id, Frame = frame, Content = new ItemContent.Text(new StyledText { String = "Amazing grace" }),
    };

    private static RenderItem Media(string id, SceneRect frame) => new()
    {
        Id = id, Frame = frame, Content = new ItemContent.Media("media-1", SceneMediaScaleMode.Fill),
    };

    /// <summary>A lyric slide as the show builds it: every media layer in use, a theme colour, a backdrop, a design and words.</summary>
    private static RenderScene Program()
    {
        var full = new SceneRect(Canvas);
        var scene = new RenderScene(Canvas, new SceneColor(0.2, 0, 0.4)); // the theme's background colour
        scene.AddItem(Media("input", full), LayerKind.VideoInput);
        scene.AddItem(Media("s1-cue-background", full), LayerKind.LoopingVideos);
        scene.AddItem(Media("still", full), LayerKind.StillGraphics);
        scene.AddItem(Media("clip", full), LayerKind.Videos);
        scene.AddItem(Shape("s1-backdrop", full), LayerKind.Slide);                       // the song's own background fill
        scene.AddItem(Media("theme-picture", full), LayerKind.Slide);                     // a design's full-screen picture
        scene.AddItem(Shape("theme-bar", new SceneRect(0, 780, 1920, 300)), LayerKind.Slide); // a lower-third bar
        scene.AddItem(Text("s1-lyrics", new SceneRect(100, 800, 1720, 260)), LayerKind.Slide);
        scene.AddItem(Shape("overlay-name-bar", new SceneRect(80, 860, 800, 120)), LayerKind.Overlays);
        scene.AddItem(Text("alert-text", new SceneRect(0, 0, 1920, 120)), LayerKind.Alerts);
        return scene;
    }

    [Fact]
    public void TheModesSaveAsWordsAndUnknownWordsAreOff()
    {
        Assert.Null(KeyFeedMode.Off.Raw());
        foreach (var mode in Enum.GetValues<KeyFeedMode>()) Assert.Equal(mode, KeyFeeds.Parse(mode.Raw()));
        Assert.Equal(KeyFeedMode.Off, KeyFeeds.Parse("blue"));
        Assert.Equal(KeyFeedMode.Off, KeyFeeds.Parse(null));
    }

    [Fact]
    public void TheFillIsBlackOrPureGreen()
    {
        Assert.Null(KeyFeeds.Fill(KeyFeedMode.Off));
        Assert.Equal(new SceneColor(0, 0, 0), KeyFeeds.Fill(KeyFeedMode.Black));
        Assert.Equal(new SceneColor(0, 1, 0), KeyFeeds.Fill(KeyFeedMode.Green));
    }

    [Fact]
    public void ItKeepsTheSlideOverlayAndAlertLayersOnly()
    {
        Assert.Equal([LayerKind.Slide, LayerKind.Overlays, LayerKind.Alerts], LayerKinds.All.Where(l => KeyFeeds.Shows(l)));
        Assert.False(KeyFeeds.Shows(null)); // a custom layer holds fired media
        // The Mac's Broadcast Feed template is the same idea without alerts.
        Assert.All(OutputPresets.BroadcastFeedLayers, raw => Assert.True(KeyFeeds.Shows(LayerKinds.Parse(raw))));
    }

    [Fact]
    public void BackgroundsAreTheBackdropAndAnythingCoveringTheCanvas()
    {
        var full = new SceneRect(Canvas);
        Assert.True(KeyFeeds.IsBackground(Shape("s1-backdrop", full), Canvas));
        Assert.True(KeyFeeds.IsBackground(Shape("s1-backdrop", new SceneRect(240, 0, 1440, 1080)), Canvas)); // a 4:3 backdrop moved onto a 16:9 screen
        Assert.True(KeyFeeds.IsBackground(Shape("design-fill", full), Canvas));
        Assert.True(KeyFeeds.IsBackground(Media("design-picture", full), Canvas));
        Assert.True(KeyFeeds.IsBackground(Shape("bleeds", new SceneRect(-20, -20, 1960, 1120)), Canvas));
        Assert.True(KeyFeeds.IsBackground(Shape("a-pixel-short", new SceneRect(1, 1, 1918, 1078)), Canvas));

        Assert.False(KeyFeeds.IsBackground(Text("words", full), Canvas)); // text never is
        Assert.False(KeyFeeds.IsBackground(Shape("bar", new SceneRect(0, 780, 1920, 300)), Canvas));
        Assert.False(KeyFeeds.IsBackground(Shape("half", new SceneRect(0, 0, 960, 1080)), Canvas));
        Assert.False(KeyFeeds.IsBackground(Shape("border", full, SceneFill.None), Canvas));
        Assert.False(KeyFeeds.IsBackground(Shape("matte", full) with { MatteGroup = "matte" }, Canvas));
        Assert.False(KeyFeeds.IsBackground(Media("clipped", full) with { MaskedBy = "matte" }, Canvas));
    }

    [Theory]
    [InlineData(KeyFeedMode.Black)]
    [InlineData(KeyFeedMode.Green)]
    public void AKeyedSceneIsWordsAndOverlaysOverTheKeyColour(KeyFeedMode mode)
    {
        var program = AnimatedScene.From(Program());
        var keyed = KeyFeeds.Apply(program, mode);

        Assert.Equal(KeyFeeds.Fill(mode), keyed.Background); // not the theme's colour
        Assert.Equal(["theme-bar", "s1-lyrics", "overlay-name-bar", "alert-text"],
            keyed.Layers.Where(l => !l.IsHidden).SelectMany(l => l.Items).Select(i => i.Id));
        Assert.All(keyed.Layers.Where(l => l.IsMediaLayer || l.Kind == LayerKind.VideoInput), l => Assert.True(l.IsHidden));

        // The program itself (what the other screens show) is untouched.
        Assert.Equal(new SceneColor(0.2, 0, 0.4), program.Background);
        Assert.Equal(10, program.AllItems.Count());
        Assert.DoesNotContain(program.Layers, l => l.IsHidden);

        // Applying it again changes nothing more.
        Assert.Equal(keyed.AllItems.Select(i => i.Id), KeyFeeds.Apply(keyed, mode).AllItems.Select(i => i.Id));
    }

    [Fact]
    public void AKeyedStillSceneLeavesTheOtherLayersEmpty()
    {
        var keyed = KeyFeeds.Apply(Program(), KeyFeedMode.Green);
        Assert.Equal(KeyFeeds.Green, keyed.Background);
        Assert.Equal(["theme-bar", "s1-lyrics", "overlay-name-bar", "alert-text"], keyed.AllItems.Select(i => i.Id));
    }

    [Fact]
    public void OffReturnsTheSceneItself()
    {
        var still = Program();
        var program = AnimatedScene.From(still);
        Assert.Same(program, KeyFeeds.Apply(program, KeyFeedMode.Off));
        Assert.Same(still, KeyFeeds.Apply(still, KeyFeedMode.Off));
    }

    [Fact]
    public void ACustomLayerIsHidden()
    {
        var program = AnimatedScene.From(Program());
        program.InsertCustomLayer("side-wall", "Side wall", LayerKind.Videos.Raw()).Items.Add(Media("wall", new SceneRect(Canvas)));
        var keyed = KeyFeeds.Apply(program, KeyFeedMode.Black);
        Assert.True(keyed.Layer("side-wall")!.IsHidden);
    }

    [Fact]
    public void TransitionsThatCrossThePictureBecomeADissolve()
    {
        Assert.Null(KeyFeeds.Transition(null));
        Assert.Same(LayerTransition.Cut, KeyFeeds.Transition(LayerTransition.Cut));
        var dissolve = new LayerTransition(SceneTransitionKind.Dissolve, 0.8);
        Assert.Same(dissolve, KeyFeeds.Transition(dissolve));
        foreach (var crossing in new[]
                 {
                     LayerTransition.Fade(SceneColor.White, 1.6),
                     new LayerTransition(SceneTransitionKind.FilmBurn, 1.6),
                     new LayerTransition(SceneTransitionKind.BlurDissolve, 1.6),
                     new LayerTransition(SceneTransitionKind.Picture, 1.6),
                 })
        {
            var played = KeyFeeds.Transition(crossing)!;
            Assert.Equal(SceneTransitionKind.Dissolve, played.Kind);
            Assert.Equal(1.6, played.Duration);
        }
    }

    [Fact]
    public void TheSettingIsPerScreenSavedAndOffByDefault()
    {
        var outputs = new OutputConfiguration();
        var projector = outputs.AddScreen("Projector", 1920, 1080)!;
        var stream = outputs.AddScreen("ATEM", 1920, 1080)!;
        var slice = outputs.AddSlice(stream.Id, "Right", new SceneRect(0.5, 0, 0.5, 1))!;
        var saves = 0;
        outputs.ConfigurationChanged += () => saves++;

        Assert.Equal(KeyFeedMode.Off, outputs.KeyFeed(stream.Id));
        outputs.SetKeyFeed(stream.Id, KeyFeedMode.Green);
        outputs.SetKeyFeed(stream.Id, KeyFeedMode.Green); // no change, no save
        Assert.Equal(1, saves);
        Assert.Equal(KeyFeedMode.Green, outputs.KeyFeed(stream.Id));
        Assert.Equal(KeyFeedMode.Green, outputs.KeyFeed(slice.Id)); // a slice answers for its screen
        Assert.Equal(KeyFeedMode.Off, outputs.KeyFeed(projector.Id)); // other screens aren't changed
        outputs.SetKeyFeed(Guid.NewGuid(), KeyFeedMode.Black); // not a screen: ignored
        Assert.Equal(1, saves);

        // Saved beside the look in the screen's settings, and read back.
        outputs.SetScreenLook(stream.Id, new SlideThemeLook("digital", "Lower Thirds", null));
        var saved = ScreenSetup.Capture(outputs);
        Assert.Null(saved[0].KeyFeed);
        Assert.Equal("green", saved[1].KeyFeed);
        var json = ScreenSetup.ToJson(saved);
        Assert.Contains("\"keyFeed\"", json);
        var restored = ScreenSetup.Load(ScreenSetup.FromJson(json));
        Assert.Equal(KeyFeedMode.Green, restored.KeyFeed(stream.Id));
        Assert.Equal(KeyFeedMode.Off, restored.KeyFeed(projector.Id));
        Assert.Equal("digital#Lower Thirds", restored.ScreenLook(stream.Id)!.Key); // the look is kept alongside

        outputs.SetKeyFeed(stream.Id, KeyFeedMode.Off);
        Assert.Null(ScreenSetup.Capture(outputs)[1].KeyFeed);
        outputs.SetKeyFeed(stream.Id, KeyFeedMode.Black);
        outputs.RemoveScreen(stream.Id);
        Assert.Equal(KeyFeedMode.Off, outputs.KeyFeed(stream.Id));
    }

    [Fact]
    public void OnlyTheKeyFeedScreensProgramIsKeyed()
    {
        var outputs = new OutputConfiguration();
        var projector = outputs.AddScreen("Projector", 1920, 1080)!;
        var stream = outputs.AddScreen("ATEM", 1920, 1080)!;
        outputs.SetKeyFeed(stream.Id, KeyFeedMode.Green);
        var program = AnimatedScene.From(Program());

        var onProjector = outputs.KeyedProgram(outputs.Feed(SwiftUuid.Format(projector.Id)), program);
        Assert.Same(program, onProjector); // exactly what it showed before

        var onStream = outputs.KeyedProgram(outputs.Feed(SwiftUuid.Format(stream.Id)), program);
        Assert.Equal(KeyFeeds.Green, onStream.Background);
        Assert.Equal(["theme-bar", "s1-lyrics", "overlay-name-bar", "alert-text"],
            onStream.Layers.Where(l => !l.IsHidden).SelectMany(l => l.Items).Select(i => i.Id));

        // The previews show the same thing.
        outputs.SceneProvider = Program;
        var preview = outputs.PreviewProvider(SwiftUuid.Format(stream.Id))();
        Assert.Equal(KeyFeeds.Green, preview.Background);
        Assert.DoesNotContain(preview.AllItems, i => i.Id is "s1-backdrop" or "s1-cue-background" or "theme-picture");
        Assert.Equal(10, outputs.PreviewProvider(SwiftUuid.Format(projector.Id))().AllItems.Count());
    }

    [Fact]
    public void A43SlideOnAWideKeyFeedLosesItsBackdropAndTheBarsAreTheKeyColour()
    {
        var outputs = new OutputConfiguration();
        var stream = outputs.AddScreen("ATEM", 1920, 1080)!;
        outputs.SetKeyFeed(stream.Id, KeyFeedMode.Green);
        var canvas = new SceneSize(1440, 1080);
        var slide = new RenderScene(canvas, new SceneColor(0.3, 0, 0));
        slide.AddItem(Shape("s1-backdrop", new SceneRect(canvas)), LayerKind.Slide);
        slide.AddItem(Shape("design-fill", new SceneRect(canvas)), LayerKind.Slide);
        slide.AddItem(Text("s1-lyrics", new SceneRect(100, 400, 1240, 300)), LayerKind.Slide);

        var keyed = outputs.KeyedProgram(outputs.Feed(SwiftUuid.Format(stream.Id)), AnimatedScene.From(slide));
        Assert.Equal(new SceneSize(1920, 1080), keyed.CanvasSize); // reshaped to the screen
        Assert.Equal(KeyFeeds.Green, keyed.Background);            // so the side bars are green too
        var words = Assert.Single(keyed.AllItems);
        Assert.Equal("s1-lyrics", words.Id);
        Assert.Equal(340, words.Render.Frame.X); // centred as on any other screen
    }

    [Fact]
    public void AStageScreenAndTheTestPicturesAreNeverKeyed()
    {
        var outputs = new OutputConfiguration();
        var stream = outputs.AddScreen("ATEM", 1920, 1080)!;
        outputs.SetKeyFeed(stream.Id, KeyFeedMode.Black);
        var id = SwiftUuid.Format(stream.Id);
        Assert.Equal(KeyFeedMode.Black, outputs.KeyFeed(outputs.Feed(id)));
        outputs.SetTestPattern(stream.Id, true);
        Assert.Equal(KeyFeedMode.Off, outputs.KeyFeed(outputs.Feed(id))); // the rigging grid shows as it is
        outputs.SetTestPattern(stream.Id, false);
        outputs.SetRole(stream.Id, ScreenRole.Confidence);
        Assert.Equal(KeyFeedMode.Off, outputs.KeyFeed(outputs.Feed(id)));
    }

    [Fact]
    public void ItWorksWithTheScreensLook()
    {
        // The stream shows the lyric through a lower-third look and as a key feed; the projector neither.
        var outputs = new OutputConfiguration();
        var stream = outputs.AddScreen("ATEM", 1920, 1080)!;
        outputs.SetScreenLook(stream.Id, new SlideThemeLook("digital", "Lower Thirds", null));
        outputs.SetKeyFeed(stream.Id, KeyFeedMode.Black);
        var feed = outputs.Feed(SwiftUuid.Format(stream.Id));
        Assert.Equal("digital#Lower Thirds", feed.SlideThemeKey); // the look still applies
        Assert.Equal(KeyFeedMode.Black, outputs.KeyFeed(feed));
        Assert.Equal(["digital#Lower Thirds"], outputs.ActiveLooks.Keys);
    }
}
