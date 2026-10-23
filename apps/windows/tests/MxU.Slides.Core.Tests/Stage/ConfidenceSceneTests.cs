using MxU.Slides.Core.Model;
using MxU.Slides.Core.Stage;

namespace MxU.Slides.Core.Tests.Stage;

// Port of the non-rendering cases of SlideSceneTests/ConfidenceSceneTests.swift.
public class ConfidenceSceneTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeSeconds(1_000);

    private static SlideObject Obj(string id, SlideObjectKind kind, string name, string text) =>
        new() { Id = id, ObjectKind = kind, Name = name, Text = text };

    private static Slide LyricSlide(string id, string text) =>
        new() { Id = id, Name = id, Objects = [Obj($"{id}-text", SlideObjectKind.Text, "Lyrics", text)] };

    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute)
    {
        var wall = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(wall, TimeZoneInfo.Local.GetUtcOffset(wall));
    }

    private static Theme AlertsTheme(string slideName) => new()
    {
        Id = "t", Name = "Theme", FontFamily = "Helvetica", FontSize = 60,
        TextColorHex = "#FFFFFFFF", BackgroundColorHex = "#000000FF",
        Slides =
        [
            new Slide
            {
                Id = "t-alerts", Name = slideName,
                Objects =
                [
                    new SlideObject { Id = "t-decor", ObjectKind = SlideObjectKind.Shape, Name = "Band", Text = "", ShapeKind = ShapeKind.Rectangle },
                    Obj("t-msg", SlideObjectKind.Text, "Message", "Sample"),
                ],
            },
        ],
    };

    [Fact]
    public void CueAlertTargetsSplitStageAndAudience()
    {
        Assert.True(new CueAlert("x", AlertBehavior.Persist).ShowsOnConfidence);
        Assert.False(new CueAlert("x", AlertBehavior.Persist).ShowsOnAudience);
        Assert.False(new CueAlert("x", AlertBehavior.Persist, AlertTarget.Audience).ShowsOnConfidence);
        Assert.True(new CueAlert("x", AlertBehavior.Persist, AlertTarget.Both).ShowsOnConfidence);
        Assert.True(new CueAlert("x", AlertBehavior.Persist, AlertTarget.Both).ShowsOnAudience);
    }

    [Fact]
    public void AlertsThemeSlideIsTheSlideNamedAlerts()
    {
        Assert.Equal("t-alerts", AlertSceneBuilder.AlertsThemeSlide(AlertsTheme("Alerts"))?.Id);
        Assert.Equal("t-alerts", AlertSceneBuilder.AlertsThemeSlide(AlertsTheme("ALERTS"))?.Id);
        Assert.Null(AlertSceneBuilder.AlertsThemeSlide(AlertsTheme("Lyrics"))); // no alerts slide: falls back to the banner
        Assert.Null(AlertSceneBuilder.AlertsThemeSlide(null));
    }

    [Fact]
    public void AlertMessageTimerTokensResolveLiveOthersPassThrough()
    {
        var info = new ConfidenceInfo
        {
            Timers = [new TimerSnapshot("Grace Timer", TimerMode.Countdown, id: "grace", isRunning: true, runningSince: Start, durationSeconds: 450)],
        };
        var resolved = LinkedText.ResolvedAlertMessage("{Grace Timer} until we start, {Child}", info, Start.AddSeconds(30));
        Assert.Equal("7:00 until we start, {Child}", resolved); // timer ticks, fill slot survives
        Assert.True(LinkedText.AlertMessageHasTimerToken("{Grace Timer}", info.Timers));
        Assert.False(LinkedText.AlertMessageHasTimerToken("{Child}", info.Timers));
        Assert.Equal("Plain words", LinkedText.ResolvedAlertMessage("Plain words", info, Start));
    }

    [Fact]
    public void ClockStringReadsTwelveHour()
    {
        Assert.Equal("2:05", ConfidenceSceneBuilder.ClockString(Local(2026, 7, 5, 14, 5)));
        Assert.Equal("12:30", ConfidenceSceneBuilder.ClockString(Local(2026, 7, 5, 0, 30)));
    }

    [Fact]
    public void BuiltInLayoutAlwaysShowsTheClock()
    {
        // ConfidenceSceneTests.confidenceSceneAlwaysShowsTheClock, at the resolved-object level.
        var layout = ConfidenceSceneBuilder.BuiltInLayout;
        Assert.Equal("built-in", layout.Id);
        var texts = LinkedText.ResolvedObjects(layout.Objects, new ConfidenceInfo(), Local(2026, 7, 5, 14, 5)).Select(o => o.Text);
        Assert.Contains("2:05 PM", texts);
        Assert.Equal(layout.Objects.Select(o => o.Id), ConfidenceSceneBuilder.BuiltInLayout.Objects.Select(o => o.Id)); // stable ids, fresh copies
        Assert.NotSame(layout, ConfidenceSceneBuilder.BuiltInLayout);
    }

    [Fact]
    public void LyricTextStripsEverythingButText()
    {
        var slide = new Slide
        {
            Id = "s", Name = "s",
            Objects =
            [
                new SlideObject { Id = "bg", ObjectKind = SlideObjectKind.Media, Name = "BG", Text = "", MediaId = "m" },
                Obj("l1", SlideObjectKind.Text, "Line 1", "Amazing grace"),
                Obj("sh", SlideObjectKind.Shape, "Box", ""),
                Obj("l2", SlideObjectKind.Text, "Line 2", "how sweet"),
            ],
        };
        Assert.Equal("Amazing grace\nhow sweet", ConfidenceSceneBuilder.LyricText(slide));
    }

    [Fact]
    public void NextSkipsBlankSlidesToTheFirstWithText()
    {
        var blank = new Slide { Id = "blank", Name = "blank", Objects = [] };
        var shapeOnly = new Slide { Id = "shape", Name = "shape", Objects = [Obj("s", SlideObjectKind.Shape, "Box", "")] };
        var verse = LyricSlide("v2", "Through many dangers");
        Assert.Equal("v2", ConfidenceSceneBuilder.NextSlide([blank, shapeOnly, verse], skippingBlanks: true)?.Id);
        Assert.Equal("blank", ConfidenceSceneBuilder.NextSlide([blank, verse], skippingBlanks: false)?.Id); // off = the literal next
        Assert.Null(ConfidenceSceneBuilder.NextSlide([blank, shapeOnly], skippingBlanks: true)); // nothing upcoming carries text
        Assert.Null(ConfidenceSceneBuilder.NextSlide([], skippingBlanks: true));
    }

    [Fact]
    public void PrimaryTimerPrefersTheRunningOne()
    {
        var idle = new TimerSnapshot("Idle", TimerMode.Countdown, durationSeconds: 300);
        var running = new TimerSnapshot("Sermon", TimerMode.CountUp, isRunning: true, runningSince: DateTimeOffset.UtcNow);
        Assert.Equal("Sermon", ConfidenceSceneBuilder.PrimaryTimer([idle, running])?.Name);
        Assert.Equal("Idle", ConfidenceSceneBuilder.PrimaryTimer([idle])?.Name);
        Assert.Null(ConfidenceSceneBuilder.PrimaryTimer([]));
    }

    [Fact]
    public void CountdownCountsDownAndGoesNegativeInOvertime()
    {
        var timer = new TimerSnapshot("t", TimerMode.Countdown, isRunning: true, runningSince: Start, durationSeconds: 90);
        Assert.Equal(60, timer.Value(Start.AddSeconds(30)));
        Assert.Equal(-10, timer.Value(Start.AddSeconds(100))); // overtime keeps counting
    }

    [Fact]
    public void PauseBanksElapsedAndResumeContinues()
    {
        var timer = new TimerSnapshot("t", TimerMode.CountUp, isRunning: true, runningSince: Start);
        timer = timer with { Banked = timer.Elapsed(Start.AddSeconds(40)), IsRunning = false, RunningSince = null };
        Assert.Equal(40, timer.Value(Start.AddSeconds(500))); // paused timers hold still

        timer = timer with { IsRunning = true, RunningSince = Start.AddSeconds(500) };
        Assert.Equal(50, timer.Value(Start.AddSeconds(510)));
    }

    [Fact]
    public void CountdownToTimeRunsAgainstTheWallRegardlessOfTransport()
    {
        var target = DateTimeOffset.FromUnixTimeSeconds(10_000);
        var timer = new TimerSnapshot("t", TimerMode.CountdownToTime, isRunning: false, targetTime: target);
        Assert.Equal(120, timer.Value(target.AddSeconds(-120)));
        Assert.Equal(-45, timer.Value(target.AddSeconds(45)));
    }

    [Fact]
    public void ProgressFillsTowardTheEnd()
    {
        var countdown = new TimerSnapshot("t", TimerMode.Countdown, isRunning: true, runningSince: Start, durationSeconds: 100);
        Assert.Equal(0.25, countdown.Progress(Start.AddSeconds(25)));
        Assert.Equal(1, countdown.Progress(Start.AddSeconds(500))); // overtime clamps full
        countdown = countdown with { DurationSeconds = 0 };
        Assert.Null(countdown.Progress(Start)); // zero-length countdown has no bar

        var armed = DateTimeOffset.FromUnixTimeSeconds(0);
        var toTime = new TimerSnapshot("t", TimerMode.CountdownToTime, targetTime: armed.AddSeconds(200), armedAt: armed);
        Assert.Equal(0.25, toTime.Progress(armed.AddSeconds(50)));
        var unarmed = toTime with { ArmedAt = null };
        Assert.Null(unarmed.Progress(armed)); // no baseline, no bar

        var unlimited = new TimerSnapshot("t", TimerMode.CountUp);
        Assert.Null(unlimited.Progress(Start)); // an unlimited Count Up is unbounded

        var limited = new TimerSnapshot("t", TimerMode.CountUp, isRunning: true, runningSince: Start, durationSeconds: 100);
        Assert.Equal(0.25, limited.Progress(Start.AddSeconds(25)));
    }

    [Fact]
    public void CountUpLimitDrivesWarningAndOverrun()
    {
        var limited = new TimerSnapshot("t", TimerMode.CountUp, isRunning: true, runningSince: Start, durationSeconds: 100);
        Assert.Equal(TimerUrgency.Normal, limited.Urgency(Start.AddSeconds(10)));
        Assert.Equal(TimerUrgency.Warning, limited.Urgency(Start.AddSeconds(80))); // inside the final 30s
        Assert.Equal(TimerUrgency.Overrun, limited.Urgency(Start.AddSeconds(130))); // past the limit
        Assert.Equal(130, limited.Value(Start.AddSeconds(130)));

        var unlimited = new TimerSnapshot("t", TimerMode.CountUp, isRunning: true, runningSince: Start);
        Assert.Equal(TimerUrgency.Normal, unlimited.Urgency(Start.AddSeconds(9_999))); // no limit, no urgency
    }

    [Fact]
    public void TimerTextFormatsAbbreviationsAndWords()
    {
        const double seconds = 3 * 86400 + 11 * 3600 + 18 * 60 + 23;
        Assert.Equal("83:18:23", TimerSnapshot.Text(seconds, TimerTextFormat.Digits));
        Assert.Equal("3d 11h 18m 23s", TimerSnapshot.Text(seconds, TimerTextFormat.Abbreviated));
        Assert.Equal("3 days 11 hours 18 minutes 23 seconds", TimerSnapshot.Text(seconds, TimerTextFormat.Words));
        Assert.Equal("1 minute 1 second", TimerSnapshot.Text(61, TimerTextFormat.Words));
        Assert.Equal("45s", TimerSnapshot.Text(45, TimerTextFormat.Abbreviated));
        Assert.Equal("0s", TimerSnapshot.Text(0, TimerTextFormat.Abbreviated));
        Assert.Equal("-1m 15s", TimerSnapshot.Text(-75, TimerTextFormat.Abbreviated));
    }

    [Fact]
    public void TimecodeFormatsBoothStyle()
    {
        Assert.Equal("0:00", TimerSnapshot.Timecode(0));
        Assert.Equal("0:59", TimerSnapshot.Timecode(59));
        Assert.Equal("10:00", TimerSnapshot.Timecode(600));
        Assert.Equal("1:02:03", TimerSnapshot.Timecode(3_723));
        Assert.Equal("-1:23", TimerSnapshot.Timecode(-83));
        Assert.Equal("0:00", TimerSnapshot.Timecode(-0.4)); // no -0
    }

    [Fact]
    public void DisplayStringTruncatesTowardZero()
    {
        var timer = new TimerSnapshot("t", TimerMode.Countdown, durationSeconds: 90.9);
        Assert.Equal(90, timer.DisplayValue(Start));
        Assert.Equal("1:30", timer.DisplayString(Start));
        Assert.Equal("1m 30s", timer.DisplayString(Start, TimerTextFormat.Abbreviated));
        Assert.Equal("90.9", timer.DisplayString(Start, TimerTextFormat.Words, "S.f"));
    }

    [Fact]
    public void IdleTimersCarryNoWarning()
    {
        var idle = new TimerSnapshot("Idle", TimerMode.Countdown, id: "idle");
        Assert.True(idle.IsIdle);
        Assert.Null(idle.ActiveWarning(Start));
        var armedToTime = new TimerSnapshot("t", TimerMode.CountdownToTime, targetTime: Start);
        Assert.False(armedToTime.IsIdle);
        Assert.Equal(TimerWarning.RedHex, armedToTime.ActiveWarning(Start.AddSeconds(1))?.ColorHex);
        Assert.Equal("30.0-#FFB826FF", TimerWarning.Defaults[0].Id);
    }

    [Fact]
    public void SnapshotsCompareByValue()
    {
        var a = new TimerSnapshot("t", TimerMode.Countdown, id: "x", durationSeconds: 60, warnings: [new TimerWarning(10, "#FF0000FF")]);
        var b = new TimerSnapshot("t", TimerMode.Countdown, id: "x", durationSeconds: 60, warnings: [new TimerWarning(10, "#FF0000FF")]);
        Assert.Equal(a, b);
        Assert.NotEqual(a, b with { Banked = 1 });
    }

    [Fact]
    public void VideoCountdownAdvancesOnlyWhilePlaying()
    {
        var playing = new VideoCountdown("Bumper", 120, 30, Start, IsPlaying: true);
        Assert.Equal(80, playing.Remaining(Start.AddSeconds(10)));
        Assert.Equal(0, playing.Remaining(Start.AddSeconds(500)));
        Assert.Equal(90, (playing with { IsPlaying = false }).Remaining(Start.AddSeconds(10)));
        var info = new ConfidenceInfo
        {
            VideoCountdown = playing,
            VideoCountdowns = new Dictionary<string, VideoCountdown> { ["slide"] = playing with { Name = "Slide" } },
        };
        Assert.Same(playing, info.VideoCountdownFor(null));
        Assert.Same(playing, info.VideoCountdownFor(""));
        Assert.Equal("Slide", info.VideoCountdownFor("slide")?.Name);
        Assert.Null(info.VideoCountdownFor("overlays"));
    }
}
