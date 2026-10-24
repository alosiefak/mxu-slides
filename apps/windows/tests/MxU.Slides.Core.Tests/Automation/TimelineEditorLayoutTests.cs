using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;
using L = MxU.Slides.Core.Automation.TimelineEditorLayout;
using Mode = MxU.Slides.Core.Automation.TimelineBlockEdit.Mode;

namespace MxU.Slides.Core.Tests.Automation;

// The editor's rows, cards and hit-testing (SwiftUI did these on the Mac, from the layout in
// PresentationTimelineLanes: 16-point ruler, 18-point sections bar, rows a point apart).
public class TimelineEditorLayoutTests
{
    private static TimelineCue Stem(string id, double at, double duration, string channel) => TimelineClipsTests.Stem(id, at, duration, channel: channel);

    private static TimelineEditorLayout Laid(List<TimelineCue> cues, bool expanded = false, IReadOnlyList<string>? channels = null, double pps = 10)
    {
        var lanes = TimelineEditing.VisibleLanes(cues, new HashSet<TimelineLane>());
        var showsTracks = TimelineTracks.Stems(cues).Count > 0;
        var rows = L.RowsFor(lanes, 40, showsTracks, expanded, channels ?? []);
        return L.Build(rows, cues, TimelineEditing.Map(60, pps), expanded);
    }

    [Fact]
    public void RowsStackUnderTheRulerAndSectionsAPointApart()
    {
        var rows = L.RowsFor([TimelineLane.Slide, TimelineLane.LoopingVideos, TimelineLane.Audio], 40, showsTracks: true, expanded: true,
            ["keys", "bass"], id => id == "bass" ? 60 : 40);
        Assert.Equal(36, L.LanesTop);
        Assert.Equal([36.0, 63, 90, 131, 172, 213], rows.Select(r => r.Top));
        Assert.Equal([26.0, 26, 40, 40, 40, 60], rows.Select(r => r.Height));
        Assert.Equal([TimelineRowKind.Lane, TimelineRowKind.Lane, TimelineRowKind.Lane, TimelineRowKind.Tracks, TimelineRowKind.Track, TimelineRowKind.Track],
            rows.Select(r => r.Kind));
        Assert.Equal("bass", rows[^1].ChannelId);
        Assert.True(rows[2].HoldsAudio && rows[3].HoldsAudio && !rows[0].HoldsAudio);
        // Folded, the tracks are one row.
        Assert.Equal(4, L.RowsFor([TimelineLane.Slide, TimelineLane.LoopingVideos, TimelineLane.Audio], 40, true, false, ["keys", "bass"]).Count);
        Assert.Equal(20, L.TrackHeight(26, 0.1));
        Assert.Equal(192, L.TrackHeight(26, 100));
        Assert.Equal(52, L.TrackHeight(26, 2));
    }

    [Fact]
    public void EachLaneHoldsItsCuesAndFoldedTracksAreOneBar()
    {
        List<TimelineCue> cues = [Make.Cue("s", "slide", 1, slideId: "a"), Stem("keys", 2, 10, "keys"), Stem("bass", 2, 6, "bass")];
        var folded = Laid(cues);
        var bar = Assert.Single(folded.Cards, c => c.Row.Kind == TimelineRowKind.Tracks);
        Assert.Equal("2 stems", bar.Title);
        Assert.True(TimelineEditing.IsFolded(bar.Cue));
        Assert.Equal((20.0, 100.0), (bar.X, bar.Width));
        Assert.True(folded.Cards.Single(c => c.Cue.Id == "s").IsPin);

        var open = Laid(cues, expanded: true, ["keys", "bass"]);
        Assert.Equal(["keys", "bass"], open.Cards.Where(c => c.Row.Kind == TimelineRowKind.Track).Select(c => c.Cue.Id));
        Assert.Equal("bass", open.CardFor("bass")!.Row.ChannelId);
        Assert.DoesNotContain(open.Cards, c => c.Row.Kind == TimelineRowKind.Tracks);
    }

    [Fact]
    public void TheRowUnderAPointCountsTheGapWithTheRowAbove()
    {
        var layout = Laid([]);
        Assert.Null(layout.RowAt(10));
        Assert.True(L.InRuler(10));
        Assert.False(L.InRuler(20));
        Assert.Equal(TimelineLane.Slide, layout.RowAt(36)?.Lane);
        Assert.Equal(TimelineLane.Slide, layout.RowAt(62.5)?.Lane);
        Assert.Equal(TimelineLane.LoopingVideos, layout.RowAt(63)?.Lane);
        Assert.Null(layout.RowAt(500));
        Assert.Equal(36 + 26 + 1 + 26 + L.BottomRoom, layout.Height);
    }

    [Fact]
    public void APressPicksTheEdgeOfABlockAndAPinOnlyMoves()
    {
        var overlay = Make.Cue("o", "overlays", 2, 6, overlayId: "lt");
        var slide = Make.Cue("s", "slide", 1, slideId: "a");
        var layout = Laid([overlay, slide]);
        var block = layout.CardFor("o")!;
        var y = block.Top + block.Height / 2;
        Assert.Equal(Mode.Trim, layout.CardAt(21, y)?.Mode);
        Assert.Equal(Mode.Move, layout.CardAt(50, y)?.Mode);
        Assert.Equal(Mode.Stretch, layout.CardAt(78, y)?.Mode);
        // A press just outside still grabs it; further off grabs nothing.
        Assert.Equal("o", layout.CardAt(17, y)?.Card.Cue.Id);
        Assert.Null(layout.CardAt(14, y));

        var pin = layout.CardFor("s")!;
        Assert.Equal(2, pin.Left);
        var py = pin.Top + pin.Height / 2;
        Assert.Equal((Mode.Move, "s"), (layout.CardAt(pin.X, py)!.Mode, layout.CardAt(pin.X, py)!.Card.Cue.Id));
        Assert.Equal(Mode.Move, layout.CardAt(pin.Right - 1, py)?.Mode);
    }

    [Fact]
    public void ANarrowBlockHasNoTrimAndTheLastDrawnWinsWhereCardsOverlap()
    {
        var narrow = Make.Cue("n", "overlays", 2, 3, overlayId: "a");
        var under = Make.Cue("u", "overlays", 2.5, 4, overlayId: "b");
        var layout = Laid([narrow, under]);
        var card = layout.CardFor("n")!;
        var y = card.Top + card.Height / 2;
        Assert.Equal(30, card.Width);
        Assert.Equal((Mode.Move, "n"), (layout.CardAt(20.5, y)!.Mode, layout.CardAt(20.5, y)!.Card.Cue.Id));
        Assert.Equal("u", layout.CardAt(40, y)?.Card.Cue.Id);
    }

    [Fact]
    public void APressBecomesADragAfterThreePointsAndSnaps()
    {
        var overlay = Make.Cue("o", "overlays", 2, 6, overlayId: "lt");
        var layout = Laid([overlay]);
        var hit = layout.CardAt(50, layout.CardFor("o")!.Top + 5)!;
        var drag = new TimelineCardDrag(hit, 50);
        Assert.False(drag.MovedTo(52, null));
        Assert.False(drag.IsDragging);
        Assert.Equal((null, 0.0), drag.Released(52, null));
        Assert.True(drag.MovedTo(60, (_, dx) => Math.Round(dx / 5) * 5));
        Assert.False(drag.MovedTo(62, null));
        Assert.Equal(Mode.Move, drag.Mode);
        Assert.Equal(12, drag.Delta);
        Assert.Equal((Mode.Move, 15.0), drag.Released(64, (_, dx) => Math.Round(dx / 5) * 5));
        Assert.Equal((32.0, 60.0), drag.LiveSpan(drag.Card, movesWithIt: false));
    }

    [Fact]
    public void AStretchOrTrimDrawsOneEdgeMovingAndCardsMovingAlongShift()
    {
        var a = Make.Cue("a", "overlays", 2, 6, overlayId: "x");
        var b = Make.Cue("b", "videos", 1, 2, mediaId: "v");
        var layout = Laid([a, b]);
        var card = layout.CardFor("a")!;
        var stretch = new TimelineCardDrag(new TimelineCardHit(card, Mode.Stretch), 78);
        stretch.MovedTo(88, null);
        Assert.Equal((20.0, 70.0), stretch.LiveSpan(card, movesWithIt: false));
        var other = layout.CardFor("b")!;
        Assert.Equal((10.0, 26.0), stretch.LiveSpan(other, movesWithIt: true));
        var move = new TimelineCardDrag(new TimelineCardHit(card, Mode.Move), 50);
        move.MovedTo(60, null);
        Assert.Equal((20.0, 26.0), move.LiveSpan(other, movesWithIt: true));
        Assert.Equal((10.0, 26.0), move.LiveSpan(other, movesWithIt: false));
    }
}
