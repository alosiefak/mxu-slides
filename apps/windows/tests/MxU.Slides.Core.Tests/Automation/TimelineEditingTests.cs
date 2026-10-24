using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;
using E = MxU.Slides.Core.Automation.TimelineEditing;
using Mode = MxU.Slides.Core.Automation.TimelineBlockEdit.Mode;

namespace MxU.Slides.Core.Tests.Automation;

// The timeline editor's rules. On the Mac these live inside the SwiftUI view
// (PresentationTimelineLanes in Sources/PresentationTimelineView.swift), which has no
// tests of its own; the cases here pin down what that view does, so the Windows editor
// behaves the same. The clip maths underneath are covered by TimelineClipsTests (ported).
public class TimelineEditingTests
{
    private static readonly HashSet<string> None = [];

    private static TimelineCue Slide(string id, double at, string slideId = "s1", string? section = "verse") =>
        Make.Cue(id, "slide", at, sectionId: section, slideId: slideId);

    private static TimelineCue Music(string id, double at, double? duration, double? inPoint = null, string media = "song") =>
        Make.Cue(id, TimelineLane.Audio.Raw(), at, duration, media, inPoint);

    private static TimelineCue Stem(string id, double at, double duration, string channel) => TimelineClipsTests.Stem(id, at, duration, channel: channel);

    private static TimelineMap Map(double seconds = 60, double pps = 10) => E.Map(seconds, pps);

    [Fact]
    public void SlidesAndBackgroundsAlwaysShowAndOtherLanesOnceTheyHoldSomething()
    {
        Assert.Equal([TimelineLane.Slide, TimelineLane.LoopingVideos], E.VisibleLanes([], new HashSet<TimelineLane>()));
        var overlay = Make.Cue("o", "overlays", 1, 5, overlayId: "lower-third");
        Assert.Equal([TimelineLane.Overlays, TimelineLane.Slide, TimelineLane.LoopingVideos, TimelineLane.Audio],
            E.VisibleLanes([overlay, Music("m", 0, 10)], new HashSet<TimelineLane>()));
        Assert.Equal([TimelineLane.Slide, TimelineLane.LoopingVideos, TimelineLane.Actions],
            E.VisibleLanes([], new HashSet<TimelineLane> { TimelineLane.Actions }));
        Assert.Equal("Background Media", TimelineLane.LoopingVideos.Title());
        Assert.Equal("Music", TimelineLane.Audio.Title());
    }

    [Fact]
    public void TheRulerTicksLeaveRoomForTheirLabels()
    {
        Assert.Equal(1, E.Interval(60));
        Assert.Equal(2, E.Interval(30));
        Assert.Equal(5, E.Interval(10));
        Assert.Equal(60, E.Interval(1));
        Assert.Equal(120, E.Interval(0.2));
        Assert.Equal("2:05", E.Clock(125));
        Assert.Equal("0:03", E.Clock(2.5));
    }

    [Fact]
    public void TheEditorDrawsTheSongWithRoomAfterIt()
    {
        Assert.Equal(30, E.DisplayLength(null));
        Assert.Equal(30, E.DisplayLength(Make.Timeline("t")));
        Assert.Equal(12, E.DisplayLength(Make.Timeline("t", cues: [Music("m", 0, 10)])));
        Assert.Equal(108, E.DisplayLength(Make.Timeline("t", cues: [Music("m", 0, 100)])));
        Assert.Equal(40, E.DisplayLength(Make.Timeline("t", length: 40, cues: [Music("m", 0, 100)])));
        Assert.Equal(12, E.StartingZoom(30, 0));
        Assert.Equal(20, E.StartingZoom(60, 900));
    }

    [Fact]
    public void AClickPicksOneCueAndCtrlClickAddsOrTakesOut()
    {
        var picked = new HashSet<string> { "a", "b" };
        Assert.Equal(["c"], E.Released(picked, "c", null, toggle: false));
        Assert.Equal(["a", "b", "c"], E.Released(picked, "c", null, toggle: true).Order());
        Assert.Equal(["b"], E.Released(picked, "a", null, toggle: true));
        // A drag keeps the selection it moved; dragging an unselected card picks only it.
        Assert.Equal(["a", "b"], E.Released(picked, "a", Mode.Move, toggle: false).Order());
        Assert.Equal(["c"], E.Released(picked, "c", Mode.Move, toggle: false));
        Assert.Equal(["a", "b"], E.Engaged(picked, "b").Order());
        Assert.Equal(["c"], E.Engaged(picked, "c"));
    }

    [Fact]
    public void TheSelectionMovesTogetherAndStemsThatStartTogetherMoveAsOne()
    {
        var keys = Stem("keys", 4, 10, "keys");
        var bass = Stem("bass", 4, 10, "bass");
        var pad = Stem("pad", 6, 4, "pad");
        var slide = Slide("s", 2);
        List<TimelineCue> cues = [slide, keys, bass, pad];
        Assert.Equal(["bass", "keys"], E.Together(keys, cues, None).Order());
        Assert.Equal(["bass", "keys", "s"], E.Together(slide, cues, new HashSet<string> { "s", "keys" }).Order());
        Assert.Equal(["s"], E.Together(slide, cues, new HashSet<string> { "pad" }));

        var folded = TimelineTracks.Folded(TimelineTracks.Stems(cues), cues)!;
        Assert.True(E.IsFolded(folded));
        Assert.Equal(["bass", "keys", "pad"], E.Reach(folded, cues).Order());
        Assert.Equal(["bass", "keys", "pad"], E.PickedIds(cues, new HashSet<string> { folded.Id }).Order());
        Assert.Equal(folded.At, E.Picked(cues, new HashSet<string> { folded.Id })!.At);
    }

    [Fact]
    public void ADragMovesTheSelectionOnTheFrameGrid()
    {
        var a = Slide("a", 1, "s1");
        var b = Make.Cue("b", "overlays", 2, 3, overlayId: "o");
        var c = Slide("c", 9, "s2");
        var moved = E.Moved([a, b, c], a, Mode.Move, 0.51, new HashSet<string> { "a", "b" });
        Assert.Equal(PresentationTimelines.SnappedToFrame(1.51), moved.First(x => x.Id == "a").At);
        Assert.Equal(PresentationTimelines.SnappedToFrame(2.51), moved.First(x => x.Id == "b").At);
        Assert.Equal(9, moved.First(x => x.Id == "c").At);
        Assert.Equal(1, a.At);
        // Only the dragged cue moves when it isn't selected.
        Assert.Equal(2, E.Moved([a, b], a, Mode.Move, 1, new HashSet<string> { "b" }).First(x => x.Id == "b").At);
        // Never before zero.
        Assert.Equal(0, E.Moved([a], a, Mode.Move, -5, None)[0].At);
    }

    [Fact]
    public void ASlideCueDroppedOnAnotherStepsClearOfIt()
    {
        var a = Slide("a", 1, "s1");
        var b = Slide("b", 2, "s2");
        var landed = E.Moved([a, b], a, Mode.Move, 1, None);
        Assert.Equal(2 + PresentationTimelines.Frame, landed.First(x => x.Id == "a").At, 9);
        var back = E.Moved([a, b], b, Mode.Move, -1, None);
        Assert.Equal(1 - PresentationTimelines.Frame, back.First(x => x.Id == "b").At, 9);
    }

    [Fact]
    public void StretchingAndTrimmingABlockMoveOneEdge()
    {
        var overlay = Make.Cue("o", "overlays", 2, 4, overlayId: "lt");
        var stretched = E.Moved([overlay], overlay, Mode.Stretch, 1, None)[0];
        Assert.True(stretched.At == 2 && stretched.Duration == 5);
        var trimmed = E.Moved([overlay], overlay, Mode.Trim, 1, None)[0];
        Assert.True(trimmed.At == 3 && trimmed.Duration == 3);

        // A clip's trim keeps its audio where it was (it reads further into the file).
        var clip = Music("m", 0, 8);
        var cut = E.Moved([clip], clip, Mode.Trim, 0.5, None)[0];
        Assert.True(cut.At == 0.5 && cut.InPoint == 0.5 && cut.Duration == 7.5);

        // A background that runs until replaced stretches from where the view drew its end.
        var background = Make.Cue("bg", "loopingVideos", 0, mediaId: "loop");
        Assert.Equal(12, E.Moved([background], background, Mode.Stretch, 2, None, openEnd: 10)[0].Duration);
    }

    [Fact]
    public void TheFoldedStemsBarOnlyMovesAndTakesEveryStem()
    {
        List<TimelineCue> stems = [Stem("keys", 4, 10, "keys"), Stem("pad", 6, 4, "pad")];
        var folded = TimelineTracks.Folded(stems, stems)!;
        var moved = E.Moved(stems, folded, Mode.Stretch, 2, None);
        Assert.Equal([6.0, 8.0], moved.Select(c => c.At));
        Assert.Equal([10.0, 4.0], moved.Select(c => c.Duration!.Value));
        Assert.Empty(E.Deleted(stems, folded, None));
    }

    [Fact]
    public void DeleteTakesTheSelectionWhenTheCueIsInIt()
    {
        var a = Slide("a", 1);
        var b = Slide("b", 2, "s2");
        var c = Slide("c", 3, "s3");
        Assert.Equal(["c"], E.Deleted([a, b, c], a, new HashSet<string> { "a", "b" }).Select(x => x.Id));
        Assert.Equal(["b", "c"], E.Deleted([a, b, c], a, new HashSet<string> { "b" }).Select(x => x.Id));
        Assert.Equal("Remove Cues", E.DeleteLabel(a, new HashSet<string> { "a", "b" }));
        Assert.Equal("Remove Cue", E.DeleteLabel(a, new HashSet<string> { "b", "c" }));

        List<TimelineCue> stems = [Stem("keys", 4, 10, "keys"), Stem("bass", 4, 10, "bass"), Stem("pad", 6, 4, "pad")];
        Assert.Equal(["pad"], E.RemovingStems(stems, stems[0]).Select(x => x.Id));
    }

    [Fact]
    public void SplitCutsTheSelectionOrEveryClip()
    {
        var a = Music("a", 0, 10);
        var b = Music("b", 12, 10, media: "other");
        var slide = Slide("s", 3);
        List<TimelineCue> cues = [a, b, slide];
        Assert.Equal(["a", "b"], E.SplitTargets(cues, None).Order());
        Assert.Equal(["b"], E.SplitTargets(cues, new HashSet<string> { "b" }));
    }

    [Fact]
    public void CopyTakesTheRangeOrTheSelectionTimedFromTheFirst()
    {
        var a = Slide("a", 2);
        var b = Make.Cue("b", "overlays", 4, 4, overlayId: "lt");
        var c = Slide("c", 10, "s2");
        List<TimelineCue> cues = [a, b, c];
        var picked = E.Copied(cues, new HashSet<string> { "b", "c" }, null);
        Assert.Equal([0.0, 6.0], picked.Select(x => x.At));
        var ranged = E.Copied(cues, new HashSet<string> { "c" }, new ClosedRange(3, 6));
        Assert.Equal(["b"], ranged.Select(x => x.Id));
        Assert.True(ranged[0].At == 1 && ranged[0].Duration == 2);
        // Naming cues wins over the range (the clip menu's Copy).
        Assert.Equal(["c"], E.Copied(cues, None, new ClosedRange(3, 6), new HashSet<string> { "c" }).Select(x => x.Id));
    }

    [Fact]
    public void AFadeIsAddedReshapedAndRemovedFittedToTheClip()
    {
        var clip = Music("m", 0, 2);
        var faded = E.WithFade([clip], "m", @out: false, TimelineClips.Shape.SCurve)[0];
        Assert.True(faded.FadeIn is { Shape: "sCurve" } && Math.Abs(faded.FadeIn.Seconds - 2.0 / 3) < 1e-9);
        Assert.Null(clip.FadeIn);
        var reshaped = E.WithFade([faded], "m", @out: false, TimelineClips.Shape.EqualPower)[0];
        Assert.Equal(faded.FadeIn.Seconds, reshaped.FadeIn!.Seconds);
        Assert.Equal("equalPower", reshaped.FadeIn.Shape);
        Assert.Null(E.WithFade([faded], "m", @out: false, null)[0].FadeIn);
        var dragged = E.WithFadeSet([faded], "m", @out: true, Make.Fade(5))[0];
        Assert.Equal(2 - faded.FadeIn.Seconds, dragged.FadeOut!.Seconds, 9);
        Assert.False(E.WithEdgeFades([clip], "m", on: false)[0].EdgeFades);
        Assert.Null(E.WithEdgeFades([clip], "m", on: true)[0].EdgeFades);
    }

    [Fact]
    public void ACutBecomesACrossfadeIntoTheSpareAudioAndBackAgain()
    {
        // The file is 20 s long; the cut at 10 leaves audio on both sides of it.
        var left = Music("l", 0, 10);
        var right = Music("r", 10, 10, inPoint: 10);
        List<TimelineCue> cues = [left, right];
        double? Window(string id) => 20;
        Assert.Equal((10, 10), E.Handles(left, right, cues, Window));
        var preview = E.Placed(left, right, null, cues, Window);
        Assert.Equal(TimelineClips.Placement.Centered, preview?.Placement);
        var made = E.CrossfadingAtCut(cues, left, right, TimelineClips.Shape.EqualPower, Window)!;
        var (l, r) = (made.First(c => c.Id == "l"), made.First(c => c.Id == "r"));
        Assert.True(l.Duration == 10.5 && r.At == 9.5 && r.InPoint == 9.5 && l.FadeOut?.Seconds == 1 && r.FadeIn?.Placement == "centered");
        var after = E.Replacing(made, l, r, TimelineClips.Placement.Post, Window)!;
        Assert.Equal(10, after.First(c => c.Id == "r").At);
        Assert.Equal("post", after.First(c => c.Id == "r").FadeIn?.Placement);
        var undone = E.Uncrossfading(made, l, r)!;
        Assert.True(undone.First(c => c.Id == "l").Duration == 10 && undone.First(c => c.Id == "r").At == 10);
        // No spare audio either side (the left clip plays to its file's end, the right from its start): no crossfade.
        var fresh = Music("f", 10, 10, media: "other");
        Assert.Null(E.Placed(left, fresh, null, [left, fresh], _ => 10));
    }

    [Fact]
    public void OverlappingClipsCrossfadeAcrossTheOverlap()
    {
        var a = Music("a", 0, 10);
        var b = Music("b", 8, 10, media: "other");
        var made = E.Crossfading([a, b], a, b, TimelineClips.Shape.Linear)!;
        Assert.Equal(2, made.First(c => c.Id == "a").FadeOut!.Seconds);
        Assert.Equal(2, made.First(c => c.Id == "b").FadeIn!.Seconds);
        Assert.Null(E.Crossfading([a, Music("c", 20, 2)], a, Music("c", 20, 2), TimelineClips.Shape.Linear));
    }

    [Fact]
    public void MovingStemsChangesTheirTrack()
    {
        List<TimelineCue> cues = [Stem("a", 0, 4, "eg"), Stem("b", 0, 4, "keys")];
        var moved = E.MovingStems(cues, "eg", "eg2");
        Assert.Equal(["track:eg2", "track:keys"], moved.Select(c => c.Lane));
        Assert.Equal("track:eg", cues[0].Lane);
    }

    [Fact]
    public void ADraggedBlockSnapsEitherEdgeOntoTheOtherCues()
    {
        var map = Map();
        var moving = Make.Cue("o", "overlays", 2, 3, overlayId: "a");
        var other = Make.Cue("p", "overlays", 10, 5, overlayId: "b");
        List<TimelineCue> cues = [moving, other];
        // Its end (50) comes within 6 points of the other's start (100) after a 46-point drag.
        Assert.Equal(50, E.SnappedDelta(46, moving, cues, Mode.Move, None, map, 20, 30, null));
        // Its start lands on the other's end (150).
        Assert.Equal(130, E.SnappedDelta(127, moving, cues, Mode.Move, None, map, 20, 30, null));
        Assert.Equal(30, E.SnappedDelta(30, moving, cues, Mode.Move, None, map, 20, 30, null));
        // The playhead is a target too; a stretch snaps by the end only.
        Assert.Equal(9, E.SnappedDelta(7, moving, cues, Mode.Stretch, None, map, 20, 30, playhead: 5.9));
        // Cues moving with it are not targets.
        Assert.Equal(46, E.SnappedDelta(46, moving, cues, Mode.Move, new HashSet<string> { "o", "p" }, map, 20, 30, null));
    }

    [Fact]
    public void APinDoesNotSnapOntoItsOwnKind()
    {
        var map = Map();
        var a = Slide("a", 2);
        var b = Slide("b", 5, "s2");
        var overlay = Make.Cue("o", "overlays", 5, 2, overlayId: "x");
        Assert.Equal(28, E.SnappedDelta(28, a, [a, b], Mode.Move, None, map, 20, 26, null));
        Assert.Equal(30, E.SnappedDelta(28, a, [a, b, overlay], Mode.Move, None, map, 20, 26, null));
    }

    [Fact]
    public void ARangeIsOnTheFrameGridAndNeverEmpty()
    {
        Assert.Equal(new ClosedRange(1, PresentationTimelines.SnappedToFrame(2.02)), E.Range(2.02, 1));
        Assert.Null(E.Range(1, 1.01));
    }

    [Fact]
    public void ABlockSpansItsLengthAndAPinReachesTheNextCue()
    {
        var map = Map(60, 10);
        var block = Make.Cue("o", "overlays", 2, 3, overlayId: "a");
        Assert.Equal((20.0, 30.0), E.Card(block, [block], map));
        var open = Make.Cue("bg", "loopingVideos", 50, mediaId: "loop");
        Assert.Equal((500.0, 100.0), E.Card(open, [open], map));
        Assert.Equal((590.0, 26.0), E.Card(Make.Cue("late", "loopingVideos", 59, mediaId: "x"), [Make.Cue("late", "loopingVideos", 59, mediaId: "x")], map));
        var a = Slide("a", 1);
        var b = Slide("b", 3, "s2");
        var c = Slide("c", 3.5, "s3");
        // A pin's label runs to 4 points short of the next cue on its lane: 16 to 140 points.
        Assert.Equal((10.0, 16.0), E.Card(a, [a, Slide("near", 1.1, "s4")], map));
        Assert.Equal((10.0, 136.0), E.Card(a, [a], map));
        Assert.Equal((10.0, 140.0), E.Card(a, [a, Slide("far", 30, "s4")], map));
        Assert.Equal((10.0, 16.0), E.Card(a, [a, b, c], map));
        Assert.Equal((30.0, 16.0), E.Card(b, [a, b, c], map));
        // Only its own lane counts.
        Assert.Equal((10.0, 136.0), E.Card(a, [a, Make.Cue("o", "overlays", 1.2, 2, overlayId: "x")], map));
    }

    [Fact]
    public void NewCuesGoWhereTheirMediaFiresAndOnTheFrameGrid()
    {
        var ids = Make.Ids();
        Assert.Equal(TimelineLane.LoopingVideos, E.MediaLane(Make.Media("bg", MediaKind.Video, MediaClassification.Background)));
        Assert.Equal(TimelineLane.Videos, E.MediaLane(Make.Media("v", MediaKind.Video)));
        Assert.Equal(TimelineLane.StillGraphics, E.MediaLane(Make.Media("still", MediaKind.Image)));
        var background = E.MediaCue(Make.Media("bg", MediaKind.Video, MediaClassification.Background), 1.01, ids);
        Assert.True(background is { Lane: "loopingVideos", Duration: null, MediaId: "bg" });
        Assert.Equal(PresentationTimelines.SnappedToFrame(1.01), background.At);
        var video = Make.Media("v");
        video.DurationSeconds = 42;
        Assert.Equal(42, E.MediaCue(video, 0, ids).Duration);
        Assert.Equal(10, E.MediaCue(Make.Media("v2"), 0, ids).Duration);

        var song = new AudioItem { Id = "song", Name = "Song", FileHash = "", FileName = "song.wav", Tags = [], DurationSeconds = 200, InPoint = 5, OutPoint = 180 };
        var music = E.MusicCue(song, 3, ids);
        Assert.True(music is { Lane: "audio", At: 3, Duration: 175, MediaId: "song" });
        Assert.True(E.OverlayCue("lt", 2, ids) is { Lane: "overlays", Duration: 10, OverlayId: "lt" });
        Assert.True(E.AlertCue("nursery", 2, ids) is { Lane: "alerts", AlertId: "nursery" });
        Assert.True(E.LiveInputCue("camera", 2, ids) is { Lane: "videoInput", LiveInputId: "camera" });
        var combo = E.ComboCue("welcome", 4, ids);
        Assert.True(combo is { Lane: "actions", Duration: null } && combo.Actions is [{ Kind: SlideActionKind.FireCombo, ComboId: "welcome" }]);
    }

    [Fact]
    public void ASlideCuePointsAtItsPlaceInTheArrangementAndStepsClearOfAnother()
    {
        List<TimelinePlayedBlock> blocks = [new("verse", ["v1", "v2"]), new("chorus", ["c1"]), new("verse", ["v1", "v2"])];
        var cue = E.SlideCue(3, blocks, 4, Make.Ids())!;
        Assert.True(cue is { SectionId: "verse", SectionOccurrence: 1, SlideId: "v1", At: 4 });
        Assert.Equal(3, PresentationTimelines.Occurrence(cue, blocks));
        Assert.Null(E.SlideCue(9, blocks, 4));
        var added = E.Adding([Slide("x", 4, "c1", "chorus")], cue);
        Assert.Equal(4 + PresentationTimelines.Frame, added.First(c => c.Id == cue.Id).At, 9);
    }

    [Fact]
    public void TheTransitionHandleKeepsToTheHalfSecond()
    {
        Assert.Equal(2.5, E.ClampedTransition(2.4, 60));
        Assert.Equal(0.5, E.ClampedTransition(0.1, 60));
        Assert.Equal(30, E.ClampedTransition(45, 60));
        Assert.Equal(10, E.ClampedTransition(45, 10));
        Assert.Equal("5 s", E.Seconds(5));
        Assert.Equal("2.5 s", E.Seconds(2.5));
    }

    [Fact]
    public void PickingATransitionKeepsItsTypedLength()
    {
        Assert.Null(E.Choosing(SongTransitions.Kind.Stop, null));
        Assert.True(E.Choosing(SongTransitions.Kind.Stop, new SongTransition { Kind = "fade", Seconds = 3 }) is { Kind: "stop", Seconds: 3 });
        Assert.True(E.Choosing(SongTransitions.Kind.Crossfade, new SongTransition { Kind = "fade", Seconds = 8 }) is { Kind: "crossfade", Seconds: 8 });
        Assert.Equal("Stop", E.Summary(null));
        Assert.Equal("Crossfade 8 s", E.Summary(new SongTransition { Kind = "crossfade", Seconds = 8 }));
        Assert.Equal("Overlap · auto", E.Summary(new SongTransition { Kind = "overlap" }));
        Assert.Equal("Fade 5 s", E.Summary(new SongTransition { Kind = "fade" }));
    }

    [Fact]
    public void TheLanesKeysMatchTheMac()
    {
        Assert.IsType<E.KeyVerb.Split>(E.VerbFor("B", control: true, shift: false));
        Assert.IsType<E.KeyVerb.Copy>(E.VerbFor("C", control: true, shift: false));
        Assert.IsType<E.KeyVerb.Paste>(E.VerbFor("V", control: true, shift: false));
        Assert.IsType<E.KeyVerb.Delete>(E.VerbFor("Backspace", control: false, shift: false));
        Assert.IsType<E.KeyVerb.Delete>(E.VerbFor("Delete", control: false, shift: false));
        Assert.IsType<E.KeyVerb.RippleDelete>(E.VerbFor("Delete", control: false, shift: true));
        Assert.Equal(new E.KeyVerb.Nudge(-PresentationTimelines.Frame), E.VerbFor("Left", control: false, shift: false));
        Assert.Equal(new E.KeyVerb.Nudge(1), E.VerbFor("Right", control: false, shift: true));
        Assert.IsType<E.KeyVerb.Discard>(E.VerbFor("Escape", control: false, shift: false));
        Assert.Null(E.VerbFor("B", control: false, shift: false));
        Assert.Null(E.VerbFor("C", control: true, shift: false, alt: true));
    }
}
