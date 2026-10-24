using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;
using C = MxU.Slides.Core.Automation.TimelineClips;
using E = MxU.Slides.Core.Automation.TimelineEffect;

namespace MxU.Slides.Core.Tests.Automation;

// Ported from PresenterCoreTests/TimelineClipsTests.swift.
public class TimelineClipsTests
{
    internal static TimelineCue Stem(string id, double at, double? duration, double? inPoint = null, string channel = "keys") =>
        Make.Cue(id, TimelineTracks.Lane(channel), at, duration, "audio-" + channel, inPoint);

    private static TimelineCue Music(string id, double at, double? duration, double? inPoint = null, string media = "song") =>
        Make.Cue(id, TimelineLane.Audio.Raw(), at, duration, media, inPoint);

    /// <summary>Which second of the file a clip plays at time t (null outside it).</summary>
    internal static double? Read(TimelineCue cue, double t)
    {
        if (t < cue.At || t >= cue.At + (cue.Duration ?? double.PositiveInfinity)) return null;
        return (cue.InPoint ?? 0) + t - cue.At;
    }

    private static readonly List<TimelinePlayedBlock> NoBlocks = [];

    [Fact]
    public void ASplitKeepsEveryFrameWhereItWas()
    {
        var cue = Stem("a", 2, 10, 1);
        cue.FadeIn = Make.Fade(1, "sCurve");
        cue.FadeOut = Make.Fade(2);
        var (left, right) = C.Split(cue, 5, [cue], newId: Make.Ids())!.Value;
        Assert.Equal("a", left.Id);
        Assert.Equal("n1", right.Id);
        Assert.True(left.At == 2 && left.Duration == 3);
        Assert.True(right.At == 5 && right.Duration == 7 && right.InPoint == 4);

        foreach (var t in new[] { 2.5, 4.9, 5, 8, 11.9 })
            Assert.Equal(Read(cue, t), Read(t < 5 ? left : right, t));
        Assert.True(Make.Same(cue.FadeIn, left.FadeIn) && left.FadeOut is null);
        Assert.True(right.FadeIn is null && Make.Same(cue.FadeOut, right.FadeOut));

        Assert.Null(C.Split(cue, 2, [cue]));
        Assert.Null(C.Split(cue, 13, [cue]));
        Assert.Null(C.Split(Make.Cue("s", "slide", 1), 3, []));
    }

    [Fact]
    public void SplittingAtThePlayheadSplitsOnlyWhatItCrosses()
    {
        var a = Stem("a", 0, 10);
        var b = Stem("b", 0, 10, channel: "bass");
        var c = Stem("c", 6, 4, channel: "pad");
        Assert.Equal(["a", "n1", "b", "c"], C.Splitting(new HashSet<string> { "a", "c" }, 4, [a, b, c], Make.Ids()).Select(x => x.Id));
    }

    [Fact]
    public void TrimmingAStartKeepsTheAudioInPlace()
    {
        var cue = Stem("a", 4, 10, 1);
        var later = C.TrimmedStart(cue, 2, 14);
        Assert.True(later.At == 6 && later.Duration == 8 && later.InPoint == 3);
        Assert.Equal(Read(cue, 7), Read(later, 7));

        var earlier = C.TrimmedStart(cue, -3, 14);
        Assert.True(earlier.At == 3 && earlier.InPoint is null && earlier.Duration == 11);

        Assert.Equal(PresentationTimelines.SnappedToFrame(14 - PresentationTimelines.Frame), C.TrimmedStart(cue, 20, 14).At);

        var faded = cue.Copy();
        faded.FadeIn = Make.Fade(3);
        faded.FadeOut = Make.Fade(3);
        var shortened = C.TrimmedStart(faded, 6, 14);
        Assert.Equal(4, shortened.Duration);
        Assert.True(shortened.FadeIn?.Seconds == 3 && shortened.FadeOut?.Seconds == 1);
        Assert.Equal(3, faded.FadeOut.Seconds);
    }

    [Fact]
    public void TheMusicLayersClipsTrimTheSameWay()
    {
        var music = Make.Cue("m", TimelineLane.Audio.Raw(), 0, 30, "song");
        var trimmed = PresentationTimelines.Edited(music, [music], TimelineBlockEdit.Mode.Trim, 5);
        Assert.True(trimmed.At == 5 && trimmed.InPoint == 5 && trimmed.Duration == 25);

        var overlay = Make.Cue("o", TimelineLane.Overlays.Raw(), 0, 30, overlayId: "x");
        Assert.Null(PresentationTimelines.Edited(overlay, [overlay], TimelineBlockEdit.Mode.Trim, 5).InPoint);
    }

    [Fact]
    public void CrossfadeOnlyOnAnOverlap()
    {
        var a = Stem("a", 0, 10);
        var b = Stem("b", 8, 10, 30);
        var (left, right) = C.Crossfaded(b, a, [a, b])!.Value;
        Assert.True(left.Id == "a" && right.Id == "b");
        Assert.True(Make.Same(Make.Fade(2, "equalPower"), left.FadeOut));
        Assert.True(Make.Same(Make.Fade(2, "equalPower"), right.FadeIn));
        Assert.True(left.At == 0 && left.Duration == 10 && right.At == 8 && right.InPoint == 30);

        var butted = Stem("c", 10, 5);
        Assert.Null(C.Crossfaded(a, butted, [a, butted]));
        var bass = Stem("d", 5, 10, channel: "bass");
        Assert.Null(C.Crossfaded(a, bass, [a, bass]));
        Assert.Equal(["b"], C.Overlapping(a, [a, b, butted, bass]).Select(x => x.Id));
    }

    [Fact]
    public void EdgeFadesAreOnExceptWhereTheAudioRunsOn()
    {
        var whole = Stem("a", 0, 10);
        Assert.Equal((true, true), C.Edges(whole, [whole]));

        var (left, right) = C.Split(whole, 4, [whole], newId: Make.Ids())!.Value;
        Assert.Equal((true, false), C.Edges(left, [left, right]));
        Assert.Equal((false, true), C.Edges(right, [left, right]));

        var moved = PresentationTimelines.Moved(right, 1);
        Assert.Equal((true, true), C.Edges(left, [left, moved]));
        Assert.Equal((true, true), C.Edges(moved, [left, moved]));

        var off = whole.Copy();
        off.EdgeFades = false;
        Assert.Equal((false, false), C.Edges(off, [off]));
    }

    [Fact]
    public void CopyAndPasteKeepTheirSpacing()
    {
        var a = Stem("a", 4, 6);
        var b = Stem("b", 7, 6, channel: "bass");
        var clipboard = C.Copied([a, b], [a, b]);
        Assert.Equal([0.0, 3], clipboard.Select(c => c.At));
        var pasted = C.Pasted(clipboard, 20, Make.Ids());
        Assert.Equal(["n1", "n2"], pasted.Select(c => c.Id));
        Assert.Equal([20.0, 23], pasted.Select(c => c.At));
        Assert.Equal([a.Lane, b.Lane], pasted.Select(c => c.Lane));
        Assert.Equal(4, a.At);
    }

    [Fact]
    public void CopyingARangeCutsClipsToIt()
    {
        var a = Stem("a", 0, 10);
        a.FadeIn = Make.Fade(1);
        a.FadeOut = Make.Fade(1);
        var slide = Make.Cue("s", TimelineLane.Slide.Raw(), 5, sectionId: "v", slideId: "x");
        var early = Make.Cue("s0", TimelineLane.Slide.Raw(), 1);
        List<TimelineCue> all = [a, slide, early];
        var copied = C.Copied(all, all, new ClosedRange(4, 8));
        Assert.Equal(["a", "s"], copied.Select(c => c.Id));
        var piece = copied[0];
        Assert.True(piece.At == 0 && piece.Duration == 4 && piece.InPoint == 4);
        Assert.True(piece.FadeIn is null && piece.FadeOut is null);
        Assert.Equal(1, copied[1].At);
    }

    [Fact]
    public void RippleDeleteMovesEverythingAfterUpAndButtsTheCut()
    {
        var keys = Stem("k", 0, 60);
        var late = Stem("p", 45, 10, channel: "pad");
        var inside = Stem("i", 25, 5, channel: "bass");
        var tail = Stem("t", 30, 20, 2, channel: "gtr");
        var slides = new[] { 10.0, 25, 41 }.Select((at, i) => Make.Cue($"s{i}", TimelineLane.Slide.Raw(), at)).ToList();
        var overlay = Make.Cue("o", TimelineLane.Overlays.Raw(), 15, 30, overlayId: "lower");
        var timeline = Make.Timeline("t", length: 60, cues: [keys, late, inside, tail, overlay, .. slides]);
        var result = C.RippleDeleted(timeline, new ClosedRange(20, 40), Make.Ids());
        var byId = result.Cues.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());

        var head = byId["k"];
        var bridge = byId["n1"];
        Assert.True(head.At == 0 && head.Duration == 20);
        Assert.True(bridge.At == 20 && bridge.Duration == 20 && bridge.InPoint == 40);
        Assert.True(bridge.FadeIn is null && head.FadeOut is null);

        Assert.Equal(25, byId["p"].At);
        Assert.False(byId.ContainsKey("i"));
        Assert.True(byId["t"].At == 20 && byId["t"].Duration == 10 && byId["t"].InPoint == 12);

        Assert.True(byId["s0"].At == 10 && !byId.ContainsKey("s1") && byId["s2"].At == 21);
        Assert.True(byId["o"].At == 15 && byId["o"].Duration == 10);
        Assert.Equal(40, result.Length);
        Assert.Equal(Read(keys, 45), Read(bridge, 25));
        Assert.Equal(60, timeline.Length);
    }

    [Fact]
    public void ShapeNamesMatchTheEngines()
    {
        Assert.Equal(["linear", "sCurve", "equalPower", "fast", "slow"], C.Shapes.Select(s => s.Raw()));
        Assert.True(C.ShapeNamed("unknown") == C.Shape.Linear && C.ShapeNamed(null) == C.Shape.Linear);
    }

    [Fact]
    public void ASplitMusicClipPlaysAsOneRunUntilItsPiecesMove()
    {
        var whole = Music("m", 2, 30);
        var (left, right) = C.Split(whole, 10, [whole], newId: Make.Ids())!.Value;
        var runs = C.WithMusicRuns([left, right]);
        Assert.True(Make.Same(new List<TimelineCue> { whole }, runs));
        Assert.Empty(PresentationTimelines.Crossing([left, right], NoBlocks, 9.9, 10.1, new HashSet<string> { "song" }));

        var moved = PresentationTimelines.Moved(right, 2);
        Assert.Equal(2, C.WithMusicRuns([left, moved]).Count);
        var faded = right.Copy();
        faded.FadeIn = Make.Fade(1);
        Assert.Equal(2, C.WithMusicRuns([left, faded]).Count);
        Assert.Equal(2, C.WithMusicRuns([left, Music("o", 10, 5, 8, "other")]).Count);
        Assert.Equal(8, left.Duration);
    }

    [Fact]
    public void MusicClipsEndAndLandByClip()
    {
        var a = Music("a", 0, 10);
        var b = Music("b", 8, 10, 30);
        Assert.Equal([new E.PlayAudio("song", 8.1 - 8, "b")], PresentationTimelines.Crossing([a, b], NoBlocks, 7.9, 8.1, new HashSet<string> { "song" }));
        Assert.Equal([new E.EndAudio("song", "a")], PresentationTimelines.Crossing([a, b], NoBlocks, 9.9, 10.1, new HashSet<string> { "song" }));
        Assert.Equal([new E.PlayAudio("song", 9, "a"), new E.PlayAudio("song", 1, "b")], PresentationTimelines.Landing([a, b], NoBlocks, 9, new HashSet<string>()));
    }

    [Fact]
    public void ACrossfadeAtAButtCutUsesTheAudioPastTheEdges()
    {
        var verse = Stem("v", 0, 20);
        var bridge = Stem("b", 20, 20, 40);
        List<TimelineCue> cues = [verse, bridge];
        Assert.Equal(20, C.Cut(bridge, verse, cues));
        var handlesV = C.Handles(verse, 60, cues);
        var handlesB = C.Handles(bridge, 60, cues);
        Assert.True(handlesV.After == 40 && handlesB.Before == 40);
        var made = C.CrossfadedAtCut(verse, bridge, cues, handlesV.After, handlesB.Before, seconds: 2)!.Value;
        Assert.Equal(C.Placement.Centered, made.Placement);
        Assert.True(made.Left.Duration == 21 && made.Right.At == 19 && made.Right.InPoint == 39 && made.Right.Duration == 21);
        Assert.True(made.Left.FadeOut?.Seconds == 2 && made.Right.FadeIn?.Seconds == 2);
        Assert.Equal(Read(verse, 19.5), Read(made.Left, 19.5));
        Assert.Equal(Read(bridge, 20.5), Read(made.Right, 20.5));

        var undone = C.Uncrossfaded(made.Left, made.Right, [made.Left, made.Right])!.Value;
        Assert.True(Make.Same(verse, undone.Left) && Make.Same(bridge, undone.Right));
    }

    [Fact]
    public void TheCrossfadeSitsWhereThereIsAudio()
    {
        Assert.Equal((C.Placement.Centered, 2.0), C.PlacementFor(2, 5, 5));
        Assert.Equal((C.Placement.Post, 2.0), C.PlacementFor(2, 5, 0));
        Assert.Equal((C.Placement.Pre, 2.0), C.PlacementFor(2, 0, 5));
        Assert.Equal((C.Placement.Post, 0.5), C.PlacementFor(2, 0.5, 0.2));
        Assert.Null(C.PlacementFor(2, 0, 0));
        Assert.Equal((C.Placement.Centered, 0.8), C.PlacementFor(2, 5, 0.4, C.Placement.Centered));
        Assert.Null(C.PlacementFor(2, 5, 0, C.Placement.Pre));
    }

    [Fact]
    public void FadeHandlesDragLengthAndMidpoint()
    {
        var clip = Stem("a", 0, 10);
        var fade = Make.Fade(2, "sCurve");
        Assert.Equal(3, C.Dragged(fade, false, false, 1, clip, 10).Seconds);
        Assert.Equal(1, C.Dragged(fade, false, true, 1, clip, 10).Seconds);
        var both = clip.Copy();
        both.FadeOut = Make.Fade(4);
        Assert.Equal(6, C.Dragged(fade, false, false, 20, both, 10).Seconds);
        Assert.Equal(0.75, C.Dragged(fade, true, false, 0.5, clip, 10).Midpoint);
        Assert.Equal(0.95, C.Dragged(fade, true, false, 5, clip, 10).Midpoint);
        Assert.Equal(2, fade.Seconds);
    }
}
