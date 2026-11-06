using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Automation;

// The Mac's AutoAdvanceControls bindings have no tests of their own (they're SwiftUI); these pin
// them down, with the auto-advance cases of SlideBulkEditTests.swift.
public class AutoAdvanceEditingTests
{
    private static Presentation Deck(params Slide[] slides) => new() { Id = "p", Name = "Loop", PresentationKind = PresentationKind.Deck, ThemeId = "", Slides = [.. slides] };

    private static Slide Slide(string id, string? section = null, AutoAdvance? advance = null) =>
        new() { Id = id, Name = id, Objects = [], SectionId = section, AutoAdvance = advance };

    [Fact]
    public void TurningItOnStartsAtSevenSeconds()
    {
        var on = AutoAdvanceEditing.Enabled(null, true)!;
        Assert.Equal(7, on.DelaySeconds);
        Assert.Null(on.AfterPlayback);
        Assert.Null(on.LoopToStart);
        Assert.Null(AutoAdvanceEditing.Enabled(on, false));
        var kept = AutoAdvanceEditing.Enabled(new AutoAdvance { DelaySeconds = 3, LoopToStart = true }, true)!;
        Assert.Equal((3.0, true), (kept.DelaySeconds, kept.LoopToStart!.Value)); // already on: unchanged
    }

    [Fact]
    public void CountingFromTheVideoEndStartsAtZeroAndBackGoesToSeven()
    {
        var fromEnd = AutoAdvanceEditing.CountingFromVideoEnd(new AutoAdvance { DelaySeconds = 12 }, true)!;
        Assert.Equal((0.0, true), (fromEnd.DelaySeconds, fromEnd.AfterPlayback!.Value));
        var back = AutoAdvanceEditing.CountingFromVideoEnd(fromEnd, false)!;
        Assert.Equal(7, back.DelaySeconds); // "0 s after it fires" would skip the slide
        Assert.Null(back.AfterPlayback); // false is stored as absent
        var keeps = AutoAdvanceEditing.CountingFromVideoEnd(new AutoAdvance { DelaySeconds = 3, AfterPlayback = true }, false)!;
        Assert.Equal(3, keeps.DelaySeconds);
        Assert.Null(AutoAdvanceEditing.CountingFromVideoEnd(null, true));
    }

    [Fact]
    public void TheDelayMayStartEarlyOnlyFromAVideosEnd()
    {
        Assert.Equal(0, AutoAdvanceEditing.WithDelay(new AutoAdvance { DelaySeconds = 5 }, -4)!.DelaySeconds);
        Assert.Equal(-4, AutoAdvanceEditing.WithDelay(new AutoAdvance { DelaySeconds = 0, AfterPlayback = true }, -4)!.DelaySeconds);
        Assert.Equal(-30, AutoAdvanceEditing.WithDelay(new AutoAdvance { DelaySeconds = 0, AfterPlayback = true }, -99)!.DelaySeconds);
        Assert.Equal(3600, AutoAdvanceEditing.WithDelay(new AutoAdvance { DelaySeconds = 5 }, 99999)!.DelaySeconds);
        Assert.Equal(2.5, AutoAdvanceEditing.WithDelay(new AutoAdvance { DelaySeconds = 5 }, 2.54)!.DelaySeconds); // tenths
        Assert.Equal(5, AutoAdvanceEditing.WithDelay(new AutoAdvance { DelaySeconds = 5 }, double.NaN)!.DelaySeconds);
    }

    [Fact]
    public void LoopingIsStoredOnlyWhenOn()
    {
        var looping = AutoAdvanceEditing.Looping(new AutoAdvance { DelaySeconds = 8 }, true)!;
        Assert.True(looping.LoopToStart);
        Assert.Null(AutoAdvanceEditing.Looping(looping, false)!.LoopToStart);
    }

    [Fact]
    public void EditsNeverChangeTheValueTheyWereGiven()
    {
        var original = new AutoAdvance { DelaySeconds = 8 };
        AutoAdvanceEditing.WithDelay(original, 3);
        AutoAdvanceEditing.Looping(original, true);
        Assert.Equal(8, original.DelaySeconds);
        Assert.Null(original.LoopToStart);
    }

    [Fact]
    public void TheCaptionSaysWhatHappens()
    {
        Assert.StartsWith("Fires the next slide this long after this one fires", AutoAdvanceEditing.Caption(new AutoAdvance { DelaySeconds = 7 }, false), StringComparison.Ordinal);
        Assert.Contains("foreground video", AutoAdvanceEditing.Caption(new AutoAdvance { AfterPlayback = true }, false), StringComparison.Ordinal);
        Assert.Equal("Fires the next run-order item this long after this media fires.", AutoAdvanceEditing.Caption(new AutoAdvance { DelaySeconds = 7 }, true));
    }

    [Fact]
    public void ManySlidesShareOneValueOrReadMixed()
    {
        // SlideBulkEditTests.swift: autoAdvanceValue.
        Assert.Equal("Off", AutoAdvanceEditing.Value([null, null]));
        Assert.Equal("8 s", AutoAdvanceEditing.Value([new AutoAdvance { DelaySeconds = 8 }, new AutoAdvance { DelaySeconds = 8 }]));
        Assert.Equal("Mixed", AutoAdvanceEditing.Value([new AutoAdvance { DelaySeconds = 8 }, null]));
        Assert.Equal("After Video", AutoAdvanceEditing.Value([new AutoAdvance { DelaySeconds = 0, AfterPlayback = true }]));
        Assert.Equal("After Video + 2 s", AutoAdvanceEditing.Value([new AutoAdvance { DelaySeconds = 2, AfterPlayback = true }]));
        Assert.Equal("Mixed", AutoAdvanceEditing.Value([new AutoAdvance { DelaySeconds = 8 }, new AutoAdvance { DelaySeconds = 8, LoopToStart = true }]));
    }

    [Fact]
    public void SettingSeveralSlidesGivesEachItsOwnCopy()
    {
        var deck = Deck(Slide("a"), Slide("b"), Slide("c"));
        Assert.Equal(2, AutoAdvanceEditing.SetOnSlides(deck, ["a", "c"], new AutoAdvance { DelaySeconds = 5 }));
        Assert.Equal(5, deck.Slides[0].AutoAdvance!.DelaySeconds);
        Assert.Null(deck.Slides[1].AutoAdvance);
        Assert.NotSame(deck.Slides[0].AutoAdvance, deck.Slides[2].AutoAdvance);
        Assert.Equal("5 s", AutoAdvanceEditing.Value(AutoAdvanceEditing.OnSlides(deck, ["c", "a"])));
        AutoAdvanceEditing.SetOnSlides(deck, ["a"], null);
        Assert.Null(deck.Slides[0].AutoAdvance);
    }

    [Fact]
    public void AnEditReachesTheChosenSlidesTheirGroupsOrEverySlide()
    {
        var deck = Deck(Slide("v1a", "verse"), Slide("v1b", "verse"), Slide("c1", "chorus"), Slide("x"));
        Assert.Equal(["v1a"], AutoAdvanceEditing.Reached(deck, ["v1a"], AutoAdvanceEditing.Reach.Chosen));
        Assert.Equal(["v1a", "v1b"], AutoAdvanceEditing.Reached(deck, ["v1a"], AutoAdvanceEditing.Reach.Groups));
        Assert.Equal(["x"], AutoAdvanceEditing.Reached(deck, ["x"], AutoAdvanceEditing.Reach.Groups)); // no group: just itself
        Assert.Equal(4, AutoAdvanceEditing.Reached(deck, ["x"], AutoAdvanceEditing.Reach.Every).Count);
    }

    [Fact]
    public void ButtonsAndTilesDescribeIt()
    {
        Assert.Equal("8s · Loop", AutoAdvanceEditing.SlideShowText(new AutoAdvance { DelaySeconds = 8, LoopToStart = true }));
        Assert.Equal("2.5s", AutoAdvanceEditing.SlideShowText(new AutoAdvance { DelaySeconds = 2.5 }));
        Assert.Equal("Auto Advance: after 8s, loops to the first slide", AutoAdvanceEditing.Summary(new AutoAdvance { DelaySeconds = 8, LoopToStart = true }));
        Assert.Equal("Auto Advance: when the video ends", AutoAdvanceEditing.Summary(new AutoAdvance { DelaySeconds = 0, AfterPlayback = true }));
        Assert.Equal("Auto Advance: 2s before the video ends", AutoAdvanceEditing.Summary(new AutoAdvance { DelaySeconds = -2, AfterPlayback = true }));
    }
}
