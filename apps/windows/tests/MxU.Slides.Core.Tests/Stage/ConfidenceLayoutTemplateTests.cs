using MxU.Slides.Core.Model;
using MxU.Slides.Core.Stage;

namespace MxU.Slides.Core.Tests.Stage;

// ConfidenceLayoutSceneTests.swift and the template cases of ChordSceneTests.swift and
// PresenterCoreTests/LibraryTests.swift, checked on resolved objects instead of render items.
public class ConfidenceLayoutTemplateTests
{
    private static readonly DateTimeOffset FixedDate = DateTimeOffset.FromUnixTimeSeconds(1_784_800_000);

    private static ConfidenceInfo FixtureInfo(CueAlert? alert = null) => new()
    {
        Current = new SlideText("current line"),
        Next = new SlideText("next line"),
        Last = new SlideText("last line"),
        Alert = alert,
        Timers = [new TimerSnapshot("Sermon", TimerMode.Countdown, id: "t", banked: 754, durationSeconds: 1800)],
        VideoCountdown = new VideoCountdown("Bumper", 300, 180, FixedDate, IsPlaying: false),
    };

    private static SlideObject LinkedObject(string id, TextSourceKind source) => new()
    {
        Id = id, ObjectKind = SlideObjectKind.Text, Name = id, Text = "",
        X = 0, Y = 0, Width = 800, Height = 200,
        TextLink = new TextLink { Source = source },
    };

    private static ConfidenceLayout Layout(List<SlideObject> objects) => new() { Id = "layout-1", Name = "Band", Objects = objects };

    private static List<SlideObject> Resolved(ConfidenceLayout layout, ConfidenceInfo? info = null) =>
        LinkedText.ResolvedObjects(layout.Objects, info ?? FixtureInfo(), FixedDate);

    private static List<string> Reads(ConfidenceLayoutTemplate template) =>
        Resolved(template.Make(template.Title())).Where(o => o.ObjectKind == SlideObjectKind.Text).Select(o => o.Text).ToList();

    private static (double, double, double, double) Frame(SlideObject o) => (o.X ?? -1, o.Y ?? -1, o.Width ?? -1, o.Height ?? -1);

    private static int ShapeCount(ConfidenceLayoutTemplate template) =>
        template.Make(template.Title()).Objects.Count(o => o.ObjectKind == SlideObjectKind.Shape);

    [Fact]
    public void LayoutResolvesLinkedText()
    {
        var reads = Resolved(Layout([LinkedObject("a", TextSourceKind.CurrentSlide), LinkedObject("b", TextSourceKind.LastSlide)])).Select(o => o.Text);
        Assert.Contains("current line", reads);
        Assert.Contains("last line", reads);
    }

    [Fact]
    public void StageMessageObjectOwnsTheAlert()
    {
        var alert = new CueAlert("Mics hot", AlertBehavior.Persist, AlertTarget.Confidence);
        var plain = Layout([LinkedObject("a", TextSourceKind.CurrentSlide)]);
        Assert.False(plain.HasStageMessageObject()); // the banner still reaches the glass
        var owned = Layout([LinkedObject("m", TextSourceKind.StageMessage)]);
        Assert.True(owned.HasStageMessageObject()); // a layout with its own stage-message box owns the presentation
        Assert.Contains("Mics hot", Resolved(owned, FixtureInfo(alert)).Select(o => o.Text));
    }

    [Fact]
    public void DefaultTemplateIsTheClassicDashboardBoxed()
    {
        var layout = ConfidenceLayoutTemplates.DefaultTemplate("New Layout");
        Assert.Equal("New Layout", layout.Name);
        var reads = Resolved(layout).Select(o => o.Text).ToList();
        foreach (var expected in new[] { "17:26", "Bumper  2:00", "current line", "next line", "TIME REMAINING", "VIDEO COUNTDOWN", "CLOCK", "CURRENT SLIDE", "NEXT SLIDE" })
            Assert.Contains(expected, reads);
        Assert.Equal(5, layout.Objects.Count(o => o.ObjectKind == SlideObjectKind.Shape)); // timer, video, clock, current, next
    }

    [Fact]
    public void CurrentAndNextTemplateHasItsModules()
    {
        var reads = Reads(ConfidenceLayoutTemplate.CurrentAndNext);
        foreach (var expected in new[] { "current line", "next line", "17:26", "CURRENT SLIDE", "NEXT SLIDE", "TIME REMAINING", "CLOCK" })
            Assert.Contains(expected, reads);
        Assert.Equal(4, ShapeCount(ConfidenceLayoutTemplate.CurrentAndNext)); // current, next, timer, clock
    }

    [Fact]
    public void TimersTemplateIsTimingOnly()
    {
        var reads = Reads(ConfidenceLayoutTemplate.Timers);
        foreach (var expected in new[] { "17:26", "Bumper  2:00", "TIME REMAINING", "VIDEO COUNTDOWN", "CLOCK" })
            Assert.Contains(expected, reads);
        Assert.DoesNotContain("current line", reads); // a timers room never shows lyrics
        Assert.Equal(3, ShapeCount(ConfidenceLayoutTemplate.Timers));
    }

    [Fact]
    public void CurrentOverNextTemplateStacksWithClockOnTop()
    {
        var objects = Resolved(ConfidenceLayoutTemplate.CurrentOverNext.Make("Stack"));
        var reads = objects.Select(o => o.Text).ToList();
        foreach (var expected in new[] { "current line", "next line", "CURRENT SLIDE", "NEXT SLIDE" })
            Assert.Contains(expected, reads);
        double Top(Func<SlideObject, bool> match) => objects.First(match).Y!.Value;
        var clockTop = Top(o => o.TextLink?.Source == TextSourceKind.Clock);
        var currentTop = Top(o => o.Text == "current line");
        var nextTop = Top(o => o.Text == "next line");
        Assert.True(clockTop < currentTop && currentTop < nextTop);
    }

    [Fact]
    public void ChordsTemplateShowsCurrentWithChordsAndNextFirstLineOnly()
    {
        Assert.Contains(ConfidenceLayoutTemplate.CurrentOverNextChords, ConfidenceLayoutTemplates.WorkspaceStarters);
        var info = new ConfidenceInfo
        {
            Current = new SlideText("Amazing grace how sweet the sound\nThat saved a wretch like me")
            {
                Chords = [new ChordPlacement { Line = 0, Column = 0, Symbol = "G" }, new ChordPlacement { Line = 1, Column = 5, Symbol = "C" }],
            },
            Next = new SlideText("next one\nnext two\nnext three")
            {
                Chords = [new ChordPlacement { Line = 0, Column = 0, Symbol = "G" }, new ChordPlacement { Line = 2, Column = 0, Symbol = "D" }],
            },
        };
        var objects = Resolved(ConfidenceLayoutTemplate.CurrentOverNextChords.Make("Chords"), info);

        var current = objects.First(o => o.Text.StartsWith("Amazing grace"));
        Assert.Equal(2, current.Chords?.Count);
        Assert.True(current.TextStyle?.ShowChords);
        Assert.True(current.TextStyle?.AutoShrink);

        var next = objects.First(o => o.Text.StartsWith("next"));
        Assert.Equal("next one", next.Text);
        Assert.Equal(["G"], next.Chords!.Select(c => c.Symbol));
        Assert.Null(next.TextStyle?.AutoShrink);
    }

    [Fact]
    public void BlankAndWorkspaceStartersNameThemselves()
    {
        Assert.Empty(ConfidenceLayoutTemplate.Blank.Make("x").Objects);
        Assert.Equal("Untitled Layout", ConfidenceLayoutTemplate.Blank.StarterName());
        Assert.Equal(
            new[] { "Current + Next", "Timers", "Current Over Next", "Current Over Next + Chords" },
            ConfidenceLayoutTemplates.WorkspaceStarters.Select(t => t.Make(t.StarterName()).Name));
    }

    [Fact]
    public void TemplatesRoundTripTheirRawNames()
    {
        foreach (var template in ConfidenceLayoutTemplates.All)
            Assert.Equal(template, ConfidenceLayoutTemplates.Parse(template.Raw()));
        Assert.Equal("currentOverNextChords", ConfidenceLayoutTemplate.CurrentOverNextChords.Raw());
        Assert.Null(ConfidenceLayoutTemplates.Parse("nope"));
    }

    [Fact]
    public void ClassicGeometryAndStylingMatchTheMac()
    {
        var objects = ConfidenceLayoutTemplate.Classic.Make("Classic").Objects;
        Assert.Equal(15, objects.Count);
        Assert.Equal(objects.Count, objects.Select(o => o.Id).Distinct().Count());

        var timer = objects.Single(o => o.Name == "Timer");
        Assert.Equal((80.0, 56.0, 440.0, 108.0), Frame(timer));
        Assert.Equal(TextSourceKind.Timer, timer.TextLink?.Source);
        Assert.Equal(84, timer.TextStyle?.FontSize);
        Assert.Equal("#7ED957FF", timer.TextStyle?.ColorHex);
        Assert.True(timer.TextStyle?.TabularFigures);
        Assert.True(timer.TextStyle?.AutoShrink);
        Assert.Equal(80, timer.TextStyle?.MinFontSize);
        Assert.Equal(TextVerticalAlignment.Middle, timer.TextStyle?.VerticalAlignment);
        Assert.Equal(TextHorizontalAlignment.Center, timer.TextStyle?.HorizontalAlignment);

        var clock = objects.Single(o => o.Name == "Clock");
        Assert.Equal("h:mm a", clock.TextLink?.ClockFormat);
        Assert.Equal("#B8B8B8FF", clock.TextStyle?.ColorHex);

        var video = objects.Single(o => o.Name == "Video Remaining");
        Assert.Equal(20, video.TextStyle?.MinFontSize); // the default floor

        var nextBox = objects.Single(o => o.Name == "Next Box");
        Assert.Equal(ShapeKind.RoundedRectangle, nextBox.ShapeKind);
        Assert.Equal(24, nextBox.CornerRadius);
        Assert.Equal(FillKind.None, nextBox.Fill?.FillKind);
        Assert.Equal("#FFB826FF", nextBox.Stroke?.ColorHex);
        Assert.Equal(3, nextBox.Stroke?.Width);

        var caption = objects.Single(o => o.Text == "NEXT SLIDE");
        Assert.Equal("Caption", caption.Name);
        Assert.Equal((60.0, 1002.0, 1800.0, 36.0), Frame(caption));
        Assert.Equal("HelveticaNeue-Bold", caption.TextStyle?.FontName);
        Assert.Equal(26, caption.TextStyle?.FontSize);
        Assert.Equal(3, caption.TextStyle?.Tracking);
        Assert.Equal("#FFB826FF", caption.TextStyle?.ColorHex);
        Assert.Equal(FillKind.Solid, caption.TextStyle?.LineFill?.Fill.FillKind);
        Assert.Equal("#000000FF", caption.TextStyle?.LineFill?.Fill.ColorHex);
        Assert.Equal(TextLineFillWidthMode.LineWidth, caption.TextStyle?.LineFill?.WidthMode);
        Assert.Equal(22, caption.TextStyle?.LineFill?.HorizontalPadding);
    }

    [Fact]
    public void ChordsTemplateNextBoxKeepsOneLineAndNoShrink()
    {
        var next = ConfidenceLayoutTemplate.CurrentOverNextChords.Make("c").Objects.Single(o => o.Name == "Next Slide");
        Assert.Equal(1, next.TextLink?.MaxLines);
        Assert.True(next.TextStyle?.ShowChords);
        Assert.Null(next.TextStyle?.MinFontSize);
        Assert.Equal(TextHorizontalAlignment.Left, next.TextStyle?.HorizontalAlignment);
        Assert.Equal((100.0, 796.0, 1720.0, 194.0), Frame(next));
    }
}
