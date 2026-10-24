using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;
using S = MxU.Slides.Core.Automation.SongTransitions;
using T = MxU.Slides.Core.Automation.PresentationTimelines;

namespace MxU.Slides.Core.Tests.Automation;

// Ported from PresenterCoreTests/SongTransitionsTests.swift and MediaAutoAdvanceTests.swift.
public class SongTransitionsTests
{
    private static SongTransition Transition(string kind, double? seconds = null) => new() { Kind = kind, Seconds = seconds };

    [Fact]
    public void AbsentOrUnknownTransitionsAreStop()
    {
        Assert.Equal(S.Kind.Stop, S.KindOf(null));
        Assert.Equal(S.Kind.Stop, S.KindOf(Transition("segue")));
        Assert.Equal(S.Kind.Crossfade, S.KindOf(Transition("crossfade")));
        Assert.Null(S.PlanFor(S.Kind.Stop, 300, 5));
    }

    [Fact]
    public void LengthsArePlaybacksUnlessTyped()
    {
        Assert.Equal(5, S.Length(Transition("fade")));
        Assert.Equal(5, S.Length(Transition("crossfade")));
        Assert.Equal(12, S.Length(Transition("crossfade", 12)));
        Assert.Equal(3.5, S.Length(Transition("overlap"), 3.5, 8));
        Assert.Equal(4, S.Length(Transition("overlap"), 20, 4));
        Assert.Equal(5, S.Length(Transition("overlap"), 20, null));
        Assert.Equal(9, S.Length(Transition("overlap", 9), 3));
        Assert.Equal(5, S.Length(Transition("fade", 0)));
    }

    [Fact]
    public void EachTransitionHandsOverWhereTheTableSays()
    {
        Assert.Equal(new S.Plan(S.Kind.Fade, 300, 295, 5, 0, 300), S.PlanFor(S.Kind.Fade, 300, 5));
        Assert.Equal(new S.Plan(S.Kind.Overlap, 296, null, 0, 0, 300), S.PlanFor(S.Kind.Overlap, 300, 4));
        Assert.Equal(new S.Plan(S.Kind.Crossfade, 295, 295, 5, 5, 300), S.PlanFor(S.Kind.Crossfade, 300, 5));
        Assert.Equal(0, S.PlanFor(S.Kind.Crossfade, 3, 5)?.NextAt);
    }

    [Fact]
    public void NormalPlayCrossesTheHandoverOnce()
    {
        var plan = S.PlanFor(S.Kind.Overlap, 300, 4)!;
        Assert.False(S.Due(plan, 295.9, 295.99));
        Assert.True(S.Due(plan, 295.99, 296.02));
        Assert.False(S.Due(plan, 296.02, 296.05));
    }

    [Fact]
    public void AJumpPastTheHandoverStartsItAtOnceShortened()
    {
        var crossfade = S.PlanFor(S.Kind.Crossfade, 300, 5)!;
        Assert.Null(S.Landing(crossfade, 200));
        Assert.Equal(new S.Plan(S.Kind.Crossfade, 298, 298, 2, 2, 300), S.Landing(crossfade, 298));
        var fade = S.PlanFor(S.Kind.Fade, 300, 5)!;
        Assert.Equal(new S.Plan(S.Kind.Fade, 300, 297, 3, 0, 300), S.Landing(fade, 297));
        var overlap = S.PlanFor(S.Kind.Overlap, 300, 4)!;
        Assert.Equal(299, S.Landing(overlap, 299)?.NextAt);
        Assert.Null(S.Landing(overlap, 299)?.FadeFrom);
    }

    [Fact]
    public void AManualJumpAlwaysCrossfades()
    {
        Assert.Equal(5, S.ManualCrossfade(null));
        Assert.Equal(3, S.ManualCrossfade(Transition("stop", 3)));
        Assert.Equal(new S.Plan(S.Kind.Crossfade, 61, 61, 5, 5, 66), S.ManualPlan(61, 5));
    }

    [Fact]
    public void OnlySlidesOwnClockArmsAGo()
    {
        Assert.True(S.Arms(S.Kind.Crossfade, true, false, false));
        Assert.False(S.Arms(S.Kind.Crossfade, false, false, false));
        Assert.False(S.Arms(S.Kind.Fade, true, true, false));
        Assert.False(S.Arms(S.Kind.Overlap, true, false, true));
        Assert.False(S.Arms(S.Kind.Stop, true, false, false));
    }

    [Fact]
    public void AServiceItemCarriesItsTransition()
    {
        var item = new ServiceItem { Id = "a", ItemKind = ServiceItemKind.Presentation, Name = "A", RefId = "deck", Transition = Transition("overlap", 3) };
        var decoded = MxuJson.Deserialize<ServiceItem>(MxuJson.Serialize(item));
        Assert.True(Make.Same(Transition("overlap", 3), decoded.Transition));
        Assert.Null(MxuJson.Deserialize<ServiceItem>("""{"id":"b","itemKind":"presentation","name":"B","refId":"d"}""").Transition);
    }

    [Fact]
    public void AServicesPlanReadsTheTimelinesEndAndSections()
    {
        List<TimelinePlayedBlock> aBlocks = [new("v", ["v1"]), new("c", ["c1"])];
        var a = Make.Timeline("a", length: 300, cues: [T.SlideCue("v1", 0, T.Anchor(0, aBlocks)!), T.SlideCue("c1", 290, T.Anchor(1, aBlocks)!)]);
        List<TimelinePlayedBlock> bBlocks = [new("i", ["i1"]), new("v", ["v1"])];
        var b = Make.Timeline("b", cues: [T.SlideCue("i1", 0, T.Anchor(0, bBlocks)!), T.SlideCue("v1", 3, T.Anchor(1, bBlocks)!)]);

        var overlap = S.PlanFor(Transition("overlap"), a, aBlocks, (b, bBlocks));
        Assert.Equal(297, overlap?.NextAt);
        Assert.Equal(300, overlap?.End);
        Assert.Equal(295, S.PlanFor(Transition("overlap"), a, aBlocks)?.NextAt);
        Assert.Null(S.PlanFor(null, a, aBlocks));
    }

    [Fact]
    public void AGoHandsOverOnTheTickThatReachesIt()
    {
        var plan = S.PlanFor(S.Kind.Crossfade, 300, 5)!;
        Assert.Equal(new S.Step.Play(290), S.StepFor(plan, 290, false, false));
        Assert.Equal(new S.Step.HandOver(295), S.StepFor(plan, 295.02, false, false));
        Assert.Equal(new S.Step.Play(295), S.StepFor(plan, 295.2, true, false));
        Assert.Equal(new S.Step.HandOver(295), S.StepFor(plan, 295.1, true, true));
        Assert.Equal(new S.Step.HandOver(295), S.StepFor(plan, 295.5, true, false));
    }

    [Fact]
    public void TheNextThingIsTheRunOrdersNextFireableItem()
    {
        var song = new ServiceItem { Id = "s", ItemKind = ServiceItemKind.Presentation, Name = "Song", RefId = "d" };
        var video = new ServiceItem { Id = "v", ItemKind = ServiceItemKind.Media, Name = "Video", RefId = "m" };
        Assert.Equal(S.NextStart.PlayTimeline, S.NextStartFor(song, true));
        Assert.Equal(S.NextStart.Fire, S.NextStartFor(song, false));
        Assert.Equal(S.NextStart.Fire, S.NextStartFor(video, false));
        Assert.Equal(S.NextStart.Nothing, S.NextStartFor(null, false));
    }

    [Fact]
    public void OnlyAManualPlayOnAnotherSongCrossfades()
    {
        Assert.True(S.ManualJumpCrossfades(true, true, true, false));
        Assert.False(S.ManualJumpCrossfades(true, true, true, true));
        Assert.False(S.ManualJumpCrossfades(true, true, false, false));
        Assert.False(S.ManualJumpCrossfades(false, true, true, false));
        Assert.False(S.ManualJumpCrossfades(true, false, true, false));
    }

    [Fact]
    public void TheDrawnLevelsAreThePlayersCurves()
    {
        var crossfade = S.PlanFor(S.Kind.Crossfade, 300, 4)!;
        Assert.Equal((1.0, 0.0), S.Levels(crossfade, 290));
        var middle = S.Levels(crossfade, 298);
        Assert.True(Math.Abs(middle.Outgoing - Math.Cos(Math.PI / 4)) < 1e-9 && Math.Abs(middle.Incoming - Math.Sin(Math.PI / 4)) < 1e-9);
        Assert.Equal((0.0, 1.0), S.Levels(crossfade, 300));
        var fade = S.PlanFor(S.Kind.Fade, 300, 4)!;
        Assert.True(Math.Abs(S.Levels(fade, 298).Outgoing - 0.5) < 1e-9);
        Assert.Equal(0, S.Levels(fade, 299).Incoming);
        Assert.Equal((1.0, 1.0), S.Levels(S.PlanFor(S.Kind.Overlap, 300, 4)!, 299));
    }

    // C#: ServiceRunOrder.nextFireable, which a handover uses.
    [Fact]
    public void TheNextFireableSkipsHeadersAndPlaylists()
    {
        ServiceItem Item(string id, ServiceItemKind kind) => new() { Id = id, ItemKind = kind, Name = id, RefId = id };
        List<ServiceItem> show = [Item("deck", ServiceItemKind.Presentation), Item("head", ServiceItemKind.Header), Item("walkin", ServiceItemKind.Playlist), Item("bumper", ServiceItemKind.Media)];
        Assert.Equal("bumper", TimelinePlayer.NextFireable(show, "deck")?.Id);
        Assert.Null(TimelinePlayer.NextFireable(show, "bumper"));
        Assert.Null(TimelinePlayer.NextFireable(show, "ghost"));
    }

    [Fact]
    public void NegativeDelayLeadsTheEnding()
    {
        Assert.Equal(0.7, MediaAutoAdvance.LeadSeconds(-0.7));
        Assert.Equal(0, MediaAutoAdvance.LeadSeconds(0));
        Assert.Equal(0, MediaAutoAdvance.LeadSeconds(5));
        Assert.True(MediaAutoAdvance.ShouldPreFire(0.6, -0.7));
        Assert.False(MediaAutoAdvance.ShouldPreFire(0.9, -0.7));
        Assert.False(MediaAutoAdvance.ShouldPreFire(0.1, 0));
    }

    [Fact]
    public void SlideAutoAdvanceRoundTripsAndOldDocumentsDecode()
    {
        var slide = new Slide { Id = "s", Name = "Last", Objects = [], AutoAdvance = new AutoAdvance { DelaySeconds = 7, LoopToStart = true } };
        var decoded = MxuJson.Deserialize<Slide>(MxuJson.Serialize(slide));
        Assert.True(Make.Same(slide.AutoAdvance, decoded.AutoAdvance));
        Assert.Null(MxuJson.Deserialize<Slide>("""{"id":"s","name":"Verse","objects":[]}""").AutoAdvance);
        var playlist = MxuJson.Deserialize<Playlist>(
            """{"id":"p","name":"Loop","playlistKind":"media","autoAdvance":true,"autoAdvanceDelaySeconds":7,"entries":[{"id":"e","refKind":"media","refId":"m","autoAdvanceDelaySeconds":2}],"playbackMode":"playAll","crossfadeSeconds":0}""");
        Assert.Equal(PlaylistKind.Media, playlist.PlaylistKind);
        Assert.Equal(["m"], playlist.Entries.Select(e => e.RefId));
        Assert.Equal("Automatic (Running Timer)", AutomaticTimer.Title);
    }
}
