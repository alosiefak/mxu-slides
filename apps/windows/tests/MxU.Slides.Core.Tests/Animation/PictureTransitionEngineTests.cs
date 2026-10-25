using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Animation;

// The engine's half of the Windows-only transitions (Phase 6): a layer under one becomes its two
// whole pictures plus a LayerMix, which the view's shader mixes; the rules for choosing them.
public class PictureTransitionEngineTests
{
    private static AnimatedScene Scene(IEnumerable<string> ids, LayerKind layer = LayerKind.Slide)
    {
        var scene = new AnimatedScene(new SceneSize(192, 108));
        foreach (var id in ids)
            scene.AddItem(new RenderItem { Id = id, Frame = new SceneRect(0, 0, 10, 10), Content = new ItemContent.Media($"media-{id}", SceneMediaScaleMode.Fill) }, layer);
        return scene;
    }

    private static readonly WindowsTransition Push = new() { Kind = WindowsTransitionKind.Push, DurationSeconds = 1 };

    [Fact]
    public void APictureTransitionHandsTheViewBothWholePictures()
    {
        var composite = SceneTransitionEngine.Composite(Scene(["stay", "leave"]), Scene(["stay", "arrive"]), SceneTransition.Of(Push), 0.25, [LayerKind.Slide]);
        var layer = composite[LayerKind.Slide];
        // Everything the layer showed, staying items too, as the old picture; everything it shows now as the new.
        Assert.Equal(["stay::leaving", "leave::leaving", "stay", "arrive"], layer.Items.Select(i => i.Id));
        Assert.All(layer.Items, i => Assert.Equal(1, i.Render.Opacity)); // the shader mixes them, not their opacity
        Assert.Equal(new LayerMix(Push, 0.25, 0.25), layer.Mix);
        Assert.Null(composite[LayerKind.Videos].Mix); // only the layer that changes
    }

    [Fact]
    public void ItWorksOnAnyLayer()
    {
        foreach (var kind in new[] { LayerKind.LoopingVideos, LayerKind.Videos, LayerKind.Overlays })
        {
            var composite = SceneTransitionEngine.Composite(Scene(["a"], kind), Scene(["b"], kind), SceneTransition.Of(Push), 0.5, [kind]);
            Assert.Equal(["a::leaving", "b"], composite[kind].Items.Select(i => i.Id));
            Assert.NotNull(composite[kind].Mix);
        }
    }

    [Fact]
    public void TheMixRunsOnTheClockAndIsGoneOnceItLands()
    {
        var engine = new SceneTransitionEngine(Scene(["a"]));
        engine.Push(Scene(["b"]), SceneTransition.Of(Push with { DurationSeconds = 2 }), now: 10);
        Assert.Equal(0.25, engine.Scene(10.5)[LayerKind.Slide].Mix!.Progress, 1e-9);
        Assert.Equal(0.5, engine.Scene(10.5)[LayerKind.Slide].Mix!.Elapsed, 1e-9);
        Assert.True(engine.IsTransitioning(11.9));
        Assert.Equal(["media-a"], engine.HeldMediaIds(11)); // the old picture's video keeps its player meanwhile
        var landed = engine.Scene(12.1);
        Assert.Null(landed[LayerKind.Slide].Mix);
        Assert.Equal(["b"], landed[LayerKind.Slide].Items.Select(i => i.Id));
    }

    [Fact]
    public void ACaughtTransitionIsDrawnAsItWasThen()
    {
        // Next pressed again half-way through a push: the old picture is the half-pushed one.
        var engine = new SceneTransitionEngine(Scene(["a"]));
        engine.Push(Scene(["b"]), SceneTransition.Of(Push), now: 0);
        var cube = new WindowsTransition { Kind = WindowsTransitionKind.Cube, DurationSeconds = 1 };
        engine.Push(Scene(["c"]), SceneTransition.Of(cube), now: 0.5);
        var layer = engine.Scene(0.75)[LayerKind.Slide];
        Assert.Equal(["a::leaving::leaving", "b::leaving", "c"], layer.Items.Select(i => i.Id));
        Assert.Equal(WindowsTransitionKind.Cube, layer.Mix!.Transition.Kind);
        Assert.Equal(0.25, layer.Mix.Progress, 1e-9);
        Assert.Equal(new LayerMix(Push, 0.5, 0.5), layer.Mix.Inner); // frozen where it was caught
    }

    [Fact]
    public void AMacKindCatchingAPictureTransitionCrossFadesWhereItHadGot()
    {
        var engine = new SceneTransitionEngine(Scene(["a"]));
        engine.Push(Scene(["b"]), SceneTransition.Of(Push), now: 0);
        engine.Push(Scene(["c"]), new SceneTransition(SceneTransitionKind.Dissolve, 1), now: 0.5);
        var layer = engine.Scene(0.5)[LayerKind.Slide];
        Assert.Null(layer.Mix);
        // Half-way through the push (eased, 0.5), the two pictures were worth half each; now both leave.
        Assert.Equal(0.5, layer.Items.First(i => i.Id == "a::leaving::leaving").Render.Opacity, 1e-6);
        Assert.Equal(0.5, layer.Items.First(i => i.Id == "b::leaving").Render.Opacity, 1e-6);
        Assert.Equal(0, layer.Items.First(i => i.Id == "c").Render.Opacity, 1e-6);
    }

    [Fact]
    public void ALightLeakLightsTheScreenOnceHoweverManyLayersLeak()
    {
        var leak = SceneTransition.Of(new WindowsTransition { Kind = WindowsTransitionKind.LightLeak, DurationSeconds = 1 });
        var before = Scene(["bg-a"], LayerKind.StillGraphics);
        before.AddItem(new RenderItem { Id = "words-a", Frame = new SceneRect(0, 0, 10, 10), Content = new ItemContent.Media("m", SceneMediaScaleMode.Fill) }, LayerKind.Slide);
        var after = Scene(["bg-b"], LayerKind.StillGraphics);
        after.AddItem(new RenderItem { Id = "words-b", Frame = new SceneRect(0, 0, 10, 10), Content = new ItemContent.Media("m", SceneMediaScaleMode.Fill) }, LayerKind.Slide);
        var both = SceneTransitionEngine.Composite(before, after, leak, 0.5, [LayerKind.StillGraphics, LayerKind.Slide]);
        Assert.False(both[LayerKind.StillGraphics].Mix!.LitBelow); // the background lights the whole screen…
        Assert.True(both[LayerKind.Slide].Mix!.LitBelow); // …so the words light only themselves
        var wordsOnly = SceneTransitionEngine.Composite(before, after, leak, 0.5, [LayerKind.Slide]);
        Assert.False(wordsOnly[LayerKind.Slide].Mix!.LitBelow); // a new verse over the same background: the words' layer lights the screen
    }

    [Fact]
    public void TheMixIsCopiedWithTheScene()
    {
        var composite = SceneTransitionEngine.Composite(Scene(["a"]), Scene(["b"]), SceneTransition.Of(Push), 0.5, [LayerKind.Slide]);
        Assert.Equal(composite[LayerKind.Slide].Mix, composite.Copy()[LayerKind.Slide].Mix);
    }

    // MARK: Choosing them

    private static Transition T(TransitionKind kind, double? seconds = null) => new() { TransitionKind = kind, DurationSeconds = seconds };

    private static LiveCues Cues(string? slide, Transition? own = null, Transition? deck = null, params (LayerKind Layer, string Media)[] media) =>
        new(slide, own, deck, media.ToDictionary(m => m.Layer, m => m.Media));

    [Fact]
    public void AWindowsKindPlaysAsAPictureTransitionAndAMacKindAsTheMacDrawsIt()
    {
        var push = LiveTransitions.FromWindows(Push)!;
        Assert.Equal((SceneTransitionKind.Picture, 1.0, Push), (push.Kind, push.Duration, push.Picture));
        Assert.Equal(LiveTransitions.From(T(TransitionKind.FadeWhite, 2)), LiveTransitions.FromWindows(new WindowsTransition { Kind = WindowsTransitionKind.FadeWhite, DurationSeconds = 2 }));
        Assert.Equal(SceneTransitionKind.Cut, LiveTransitions.FromWindows(Push with { DurationSeconds = 0.01 })!.Kind);
        Assert.Null(LiveTransitions.FromWindows(null));
    }

    [Fact]
    public void WithoutShadersAPictureTransitionPlaysTheNearestMacKind()
    {
        var fallback = LiveTransitions.Fallback(LiveTransitions.FromWindows(Push with { DurationSeconds = 1.5 }))!;
        Assert.Equal((SceneTransitionKind.Dissolve, 1.5), (fallback.Kind, fallback.Duration));
        Assert.Equal(SceneTransitionKind.FilmBurn, LiveTransitions.Fallback(LiveTransitions.FromWindows(new WindowsTransition { Kind = WindowsTransitionKind.LightLeak }))!.Kind);
        var dissolve = new SceneTransition(SceneTransitionKind.Dissolve, 1);
        Assert.Same(dissolve, LiveTransitions.Fallback(dissolve)); // the Mac's kinds play as they are
    }

    [Fact]
    public void DrawnInSoftwareEveryPictureTransitionIsAPlainDissolve()
    {
        // Even the blurred ones, whose nearest Mac kind (a Blur Dissolve) costs software drawing far more.
        foreach (var kind in WindowsTransitions.WindowsOnly)
        {
            var plain = LiveTransitions.InSoftware(LiveTransitions.FromWindows(new WindowsTransition { Kind = kind, DurationSeconds = 1.25 }))!;
            Assert.Equal((SceneTransitionKind.Dissolve, 1.25, (WindowsTransition?)null), (plain.Kind, plain.Duration, plain.Picture));
        }
        // The Mac's Blur Dissolve too: its blur would hold the UI thread for seconds a frame.
        var blur = LiveTransitions.InSoftware(new SceneTransition(SceneTransitionKind.BlurDissolve, 1))!;
        Assert.Equal((SceneTransitionKind.Dissolve, 1.0), (blur.Kind, blur.Duration));
        // The Mac's other kinds play exactly as they always have.
        foreach (var kind in new[] { SceneTransitionKind.Cut, SceneTransitionKind.Dissolve, SceneTransitionKind.Fade, SceneTransitionKind.FilmBurn })
        {
            var mac = new SceneTransition(kind, 1);
            Assert.Same(mac, LiveTransitions.InSoftware(mac));
        }
        Assert.Null(LiveTransitions.InSoftware(null));
    }

    [Fact]
    public void ASlidesWindowsTransitionWinsOverItsFallbackAndItsPresentations()
    {
        var room = RoomTransitions.MacDefault;
        var fired = Cues("s1", own: T(TransitionKind.Dissolve, 1), deck: T(TransitionKind.FadeBlack, 1)) with { SlideWindows = Push };
        Assert.Equal(SceneTransitionKind.Picture, LiveTransitions.For(LiveCues.Nothing, fired, _ => null, room)(LayerKind.Slide)!.Kind);
        // Without its extra (none, or a Mac changed it), the shared transition plays.
        var plain = Cues("s1", own: T(TransitionKind.Dissolve, 1));
        Assert.Equal(SceneTransitionKind.Dissolve, LiveTransitions.For(LiveCues.Nothing, plain, _ => null, room)(LayerKind.Slide)!.Kind);
    }

    [Fact]
    public void APresentationsWindowsTransitionIsItsSlidesDefault()
    {
        var cube = new WindowsTransition { Kind = WindowsTransitionKind.Cube, DurationSeconds = 1 };
        var fired = Cues("s1", own: null, deck: T(TransitionKind.Dissolve, 1)) with { PresentationWindows = cube };
        var transition = LiveTransitions.For(LiveCues.Nothing, fired, _ => null, RoomTransitions.MacDefault)(LayerKind.Slide)!;
        Assert.Equal(cube, transition.Picture);
        // A slide's own cut still beats it.
        var cut = Cues("s1", own: T(TransitionKind.Cut), deck: T(TransitionKind.Dissolve, 1)) with { PresentationWindows = cube };
        Assert.Equal(SceneTransitionKind.Cut, LiveTransitions.For(LiveCues.Nothing, cut, _ => null, RoomTransitions.MacDefault)(LayerKind.Slide)!.Kind);
    }

    [Fact]
    public void AMediaItemsWindowsTransitionPlaysWhenItsFiredAndWhenItsCleared()
    {
        var iris = new WindowsTransition { Kind = WindowsTransitionKind.Iris, DurationSeconds = 1 };
        WindowsTransition? Windows(string id) => id == "loop" ? iris : null;
        Transition? Shared(string id) => id == "loop" ? T(TransitionKind.Dissolve, 1) : null;
        var on = LiveTransitions.For(LiveCues.Nothing, Cues(null, media: (LayerKind.LoopingVideos, "loop")), Shared, RoomTransitions.MacDefault, mediaWindows: Windows);
        Assert.Equal(iris, on(LayerKind.LoopingVideos)!.Picture);
        var off = LiveTransitions.For(Cues(null, media: (LayerKind.LoopingVideos, "loop")), LiveCues.Nothing, Shared, RoomTransitions.MacDefault, mediaWindows: Windows);
        Assert.Equal(iris, off(LayerKind.LoopingVideos)!.Picture);
    }

    [Fact]
    public void RoomDefaultsCanBeWindowsKinds()
    {
        var flip = new WindowsTransition { Kind = WindowsTransitionKind.Flip, DurationSeconds = 0.9 };
        var room = RoomTransitions.Of(flip, new WindowsTransition { Kind = WindowsTransitionKind.Dissolve, DurationSeconds = 0.7 });
        Assert.Equal(TransitionKind.Dissolve, room.Slide!.TransitionKind); // the Mac-readable half
        Assert.Equal(flip, LiveTransitions.RoomDefault(LayerKind.Slide, room)!.Picture);
        Assert.Equal(SceneTransitionKind.Dissolve, LiveTransitions.RoomDefault(LayerKind.Videos, room)!.Kind);
        var wipes = RoomTransitions.Of(null, new WindowsTransition { Kind = WindowsTransitionKind.SoftWipe });
        Assert.Null(LiveTransitions.RoomDefault(LayerKind.Slide, wipes)); // no slide default: slides cut
        Assert.Equal(WindowsTransitionKind.SoftWipe, LiveTransitions.RoomDefault(LayerKind.StillGraphics, wipes)!.Picture!.Kind);
        Assert.Null(LiveTransitions.RoomDefault(LayerKind.Overlays, wipes)); // overlays always cut
        Assert.Equal(SceneTransitionKind.Cut, LiveTransitions.RoomDefault(LayerKind.Slide, RoomTransitions.Of(new WindowsTransition { Kind = WindowsTransitionKind.Cut }, null))!.Kind);
    }
}
