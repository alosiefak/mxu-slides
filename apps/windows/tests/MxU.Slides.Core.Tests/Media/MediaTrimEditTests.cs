using MxU.Slides.Core.Media;

namespace MxU.Slides.Core.Tests.Media;

// Ported from the Mac's SlideScene/Tests/SlideSceneTests/MediaTrimEditTests.swift, plus the
// play-rate and "Plays" rules from MediaEditorView.swift.
public class MediaTrimEditTests
{
    [Fact]
    public void WindowIsNilUntrimmed()
    {
        Assert.Null(MediaTrimEdit.Window(null, null, 40));
        Assert.Null(MediaTrimEdit.Window(0.02, 39.97, 40)); // edges within slack read as ends
        Assert.Null(MediaTrimEdit.Window(30, 10, 40)); // an inverted pair is no window
    }

    [Fact]
    public void WindowAndFractionsFollowThePoints()
    {
        Assert.Equal((5.0, 30.0), MediaTrimEdit.Window(5, 30, 40));
        Assert.Equal((5.0, 40.0), MediaTrimEdit.Window(5, 90, 40)); // out clamps to the file
        Assert.Equal((0.25, 0.75), MediaTrimEdit.Fractions(10, 30, 40));
        Assert.Null(MediaTrimEdit.Fractions(10, 30, 0)); // no duration yet, no shrouds
    }

    [Fact]
    public void PointsFromABarDragTidyAndClearAtTheEnds()
    {
        var dragged = MediaTrimEdit.Points((0.25, 0.756), 40);
        Assert.Equal(10, dragged.InPoint);
        Assert.Equal(30.24, dragged.OutPoint);
        var parked = MediaTrimEdit.Points((0, 1), 40);
        Assert.Null(parked.InPoint);
        Assert.Null(parked.OutPoint);
    }

    [Fact]
    public void TypedInClampsClearsAndSwallowsALaterOut()
    {
        Assert.Equal(((double?)12.35, (double?)30), MediaTrimEdit.TypedIn(12.345, 30, 40));
        Assert.Equal(((double?)null, (double?)30), MediaTrimEdit.TypedIn(0, 30, 40)); // the start clears it
        Assert.Equal(((double?)null, (double?)30), MediaTrimEdit.TypedIn(null, 30, 40)); // an emptied field clears it
        Assert.Equal(((double?)35, (double?)null), MediaTrimEdit.TypedIn(35, 30, 40)); // past the out: a fresh window to the end
        Assert.Equal(((double?)40, (double?)null), MediaTrimEdit.TypedIn(99, null, 40)); // clamped into the file
    }

    [Fact]
    public void TypedOutClampsClearsAndRefusesBeforeTheIn()
    {
        Assert.Equal(((double?)10, (double?)30), MediaTrimEdit.TypedOut(30, 10, 40));
        Assert.Equal(((double?)10, (double?)null), MediaTrimEdit.TypedOut(99, 10, 40)); // the end clears it
        Assert.Equal(((double?)10, (double?)null), MediaTrimEdit.TypedOut(null, 10, 40));
        Assert.Null(MediaTrimEdit.TypedOut(8, 10, 40)); // before the in is refused
    }

    [Fact]
    public void TimecodesParseAndFormat()
    {
        Assert.Equal(65.5, MediaTrimEdit.Seconds("1:05.5"));
        Assert.Equal(65.5, MediaTrimEdit.Seconds("65.5"));
        Assert.Equal(15, MediaTrimEdit.Seconds(" 0:15 "));
        Assert.Equal(3723, MediaTrimEdit.Seconds("1:02:03"));
        Assert.Null(MediaTrimEdit.Seconds("abc"));
        Assert.Null(MediaTrimEdit.Seconds("1:"));
        Assert.Null(MediaTrimEdit.Seconds("-3"));
        Assert.Equal("1:05.5", MediaTrimEdit.Timecode(65.5));
        Assert.Equal("0:15.4", MediaTrimEdit.Timecode(15.44));
    }

    [Fact]
    public void PlayRatesAreStoredAbsentAtNormalSpeed()
    {
        Assert.Equal("Normal", MediaTrimEdit.RateLabel(1));
        Assert.Equal("0.25×", MediaTrimEdit.RateLabel(0.25));
        Assert.Equal("2×", MediaTrimEdit.RateLabel(2));
        Assert.Null(MediaTrimEdit.StoredRate(1));
        Assert.Equal(1.5, MediaTrimEdit.StoredRate(1.5));
        Assert.Contains(1.0, MediaTrimEdit.RateChoices);
    }

    [Fact]
    public void ATrimmedItemPlaysForItsWindowAtItsRate()
    {
        Assert.Null(MediaTrimEdit.PlaysFor(null, null, 40, 2)); // untrimmed: no "Plays" line
        Assert.Equal(20, MediaTrimEdit.PlaysFor(10, 30, 40, null));
        Assert.Equal(10, MediaTrimEdit.PlaysFor(10, 30, 40, 2));
        Assert.Equal(30, MediaTrimEdit.PlaysFor(10, null, 40, 1));
        Assert.Equal("1:05", MediaTrimEdit.Length(65.4));
    }
}
