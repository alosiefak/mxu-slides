using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Animation;

// How each output layer changes, decided as the Mac's RenderContext and ServiceControls decide it
// (the Mac has no unit tests for these; the rules are read from RenderContext.swift and ServiceControls.swift).
public class LiveTransitionsTests
{
    private static Transition T(TransitionKind kind, double? seconds = null, string? color = null) =>
        new() { TransitionKind = kind, DurationSeconds = seconds, ColorHex = color };

    private static LiveCues Cues(string? slide, Transition? own = null, Transition? deck = null, params (LayerKind Layer, string Media)[] media) =>
        new(slide, own, deck, media.ToDictionary(m => m.Layer, m => m.Media));

    private static readonly RoomTransitions Room = new(T(TransitionKind.Dissolve, 0.4), RoomTransitions.MacDefault.Media);

    [Fact]
    public void StoredTransitionsPlayAsTheRendererDrawsThem()
    {
        Assert.Null(LiveTransitions.From(null));
        Assert.Equal(SceneTransitionKind.Cut, LiveTransitions.From(T(TransitionKind.Cut))!.Kind);
        Assert.Equal(SceneTransitionKind.Cut, LiveTransitions.From(T(TransitionKind.Dissolve, 0.01))!.Kind); // too short to see: a cut
        var dissolve = LiveTransitions.From(T(TransitionKind.Dissolve))!;
        Assert.Equal((SceneTransitionKind.Dissolve, 0.5), (dissolve.Kind, dissolve.Duration)); // half a second by default
        Assert.Equal(SceneColor.White, LiveTransitions.From(T(TransitionKind.FadeWhite, 1))!.Plate);
        Assert.Equal(new SceneColor(1, 0, 0), LiveTransitions.From(T(TransitionKind.FadeColor, 1, "#FF0000FF"))!.Plate);
        Assert.Equal(SceneColor.Black, LiveTransitions.From(T(TransitionKind.FadeColor, 1, "nonsense"))!.Plate);
        Assert.Equal(SceneTransitionKind.BlurDissolve, LiveTransitions.From(T(TransitionKind.BlurDissolve, 2))!.Kind);
        Assert.Equal(SceneTransitionKind.FilmBurn, LiveTransitions.From(T(TransitionKind.FilmBurn, 2))!.Kind);
        Assert.Equal(12, LiveTransitions.From(T(TransitionKind.Dissolve, 12))!.Duration); // a typed length past the slider plays
    }

    [Fact]
    public void RoomDefaultsCoverSlidesAndMediaOnly()
    {
        Assert.Null(LiveTransitions.RoomDefault(LayerKind.Slide, RoomTransitions.MacDefault)); // the Mac's slides cut out of the box
        Assert.Equal(0.7, LiveTransitions.RoomDefault(LayerKind.LoopingVideos, RoomTransitions.MacDefault)!.Duration);
        Assert.Equal(SceneTransitionKind.Dissolve, LiveTransitions.RoomDefault(LayerKind.Videos, RoomTransitions.MacDefault)!.Kind);
        Assert.Null(LiveTransitions.RoomDefault(LayerKind.Overlays, Room)); // overlays play their own In and Out
        Assert.Null(LiveTransitions.RoomDefault(LayerKind.Alerts, Room));
        Assert.Null(LiveTransitions.RoomDefault(LayerKind.VideoInput, Room)); // cameras cut
        Assert.Null(LiveTransitions.RoomDefault(LayerKind.StillGraphics, new RoomTransitions(null, T(TransitionKind.Cut))));
    }

    [Fact]
    public void ASlideComesInWithItsOwnTransitionElseItsPresentations()
    {
        var own = T(TransitionKind.FadeBlack, 1);
        var deck = T(TransitionKind.BlurDissolve, 2);
        var none = (Func<string, Transition?>)(_ => null);
        Assert.Equal(SceneTransitionKind.Fade, LiveTransitions.For(Cues("a"), Cues("b", own, deck), none, Room)(LayerKind.Slide)!.Kind);
        Assert.Equal(SceneTransitionKind.BlurDissolve, LiveTransitions.For(Cues("a"), Cues("b", null, deck), none, Room)(LayerKind.Slide)!.Kind);
        Assert.Equal(SceneTransitionKind.Cut, LiveTransitions.For(Cues("a"), Cues("b", T(TransitionKind.Cut), deck), none, Room)(LayerKind.Slide)!.Kind); // own wins, even a cut
        Assert.Equal(0.4, LiveTransitions.For(Cues("a"), Cues("b"), none, Room)(LayerKind.Slide)!.Duration); // the room default
        // Nothing fired (a build click, a clock tick): the room default, which only plays if the slide's items changed.
        Assert.Equal(0.4, LiveTransitions.For(Cues("b", own), Cues("b", own), none, Room)(LayerKind.Slide)!.Duration);
    }

    [Fact]
    public void MediaUsesItsOwnTransitionAndASlidesBackgroundFallsBackToThePresentation()
    {
        var wipe = T(TransitionKind.FilmBurn, 1.5);
        var deck = T(TransitionKind.FadeWhite, 2);
        Transition? Lookup(string id) => id == "motion" ? wipe : null;

        // Fired on its own: its own transition, else the room default.
        Assert.Equal(SceneTransitionKind.FilmBurn, LiveTransitions.For(Cues(null), Cues(null, media: (LayerKind.LoopingVideos, "motion")), Lookup, Room)(LayerKind.LoopingVideos)!.Kind);
        Assert.Equal(0.7, LiveTransitions.For(Cues(null), Cues(null, media: (LayerKind.LoopingVideos, "plain")), Lookup, Room)(LayerKind.LoopingVideos)!.Duration);
        // Brought by a slide: its own, else the presentation's.
        Assert.Equal(SceneTransitionKind.Fade, LiveTransitions.For(Cues("a"), Cues("b", null, deck, (LayerKind.LoopingVideos, "plain")), Lookup, Room)(LayerKind.LoopingVideos)!.Kind);
        Assert.Equal(SceneTransitionKind.FilmBurn, LiveTransitions.For(Cues("a"), Cues("b", null, deck, (LayerKind.LoopingVideos, "motion")), Lookup, Room)(LayerKind.LoopingVideos)!.Kind);
    }

    [Fact]
    public void ClearingMediaPlaysItsOwnExitButClearAllAndASweepUseTheRoomDefault()
    {
        var burn = T(TransitionKind.FilmBurn, 1);
        Transition? Lookup(string id) => id == "intro" ? burn : null;
        var live = Cues("a", media: (LayerKind.Videos, "intro"));
        Assert.Equal(SceneTransitionKind.FilmBurn, LiveTransitions.For(live, Cues("a"), Lookup, Room)(LayerKind.Videos)!.Kind); // F3: the video's own exit
        Assert.Equal(0.7, LiveTransitions.For(live, Cues(null), Lookup, Room, clearAll: true)(LayerKind.Videos)!.Duration); // F1: room default
        Assert.Equal(0.7, LiveTransitions.For(live, Cues("b"), Lookup, Room)(LayerKind.Videos)!.Duration); // swept off by the next slide
    }

    [Fact]
    public void AllCutCutsEveryLayer() =>
        Assert.All(LayerKinds.All, layer => Assert.Equal(SceneTransitionKind.Cut, LiveTransitions.AllCut(layer)!.Kind));
}
