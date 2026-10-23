using MxU.Slides.Core.Stage;

namespace MxU.Slides.Core.Tests.Stage;

// Port of SlideSceneTests/TimerPatternTests.swift.
public class TimerPatternTests
{
    private const double ThreeDays = 3 * 86400 + 11 * 3600 + 18 * 60 + 23;

    [Fact]
    public void UnitsRollOverAndTotalsDoNot()
    {
        Assert.Equal("3 11 18 23", TimerPattern.Text(ThreeDays, "d h m s"));
        Assert.Equal("83:18:23", TimerPattern.Text(ThreeDays, "H:mm:ss"));
        Assert.Equal("4998", TimerPattern.Text(ThreeDays, "M"));
        Assert.Equal("299903", TimerPattern.Text(ThreeDays, "S"));
        Assert.Equal("17:26", TimerPattern.Text(1046, "m:ss")); // minutes without the hour
        Assert.Equal("26", TimerPattern.Text(1046, "ss")); // just the seconds in the minute
    }

    [Fact]
    public void DoubledTokensZeroPad()
    {
        Assert.Equal("00:01:05", TimerPattern.Text(65, "hh:mm:ss"));
        Assert.Equal("0:1:5", TimerPattern.Text(65, "h:m:s"));
        Assert.Equal("005", TimerPattern.Text(5, "SSS"));
    }

    [Fact]
    public void FractionsTruncateLikeEveryOtherUnit()
    {
        Assert.Equal("26.2", TimerPattern.Text(26.25, "s.f"));
        Assert.Equal("26.25", TimerPattern.Text(26.25, "s.ff"));
        Assert.Equal("26.250", TimerPattern.Text(26.25, "s.fff"));
        Assert.Equal("17:26.420", TimerPattern.Text(1046.42, "m:ss.fff")); // binary slack never reads 419
        Assert.Equal("26.999", TimerPattern.Text(1046.9996, "s.fff")); // never rounds into the next second
    }

    [Fact]
    public void QuotesFenceLiteralsAndOtherCharactersPassThrough()
    {
        Assert.Equal("3d 83:18", TimerPattern.Text(ThreeDays, "d'd' H:mm"));
        Assert.Equal("45 seconds", TimerPattern.Text(45, "S 'seconds'"));
        Assert.Equal("it's 45", TimerPattern.Text(45, "'it''s' S")); // '' is one apostrophe
        Assert.Equal("[45]", TimerPattern.Text(45, "[S]"));
    }

    [Fact]
    public void OvertimeCarriesAMinusOnlyWhereDigitsShow()
    {
        Assert.Equal("-1:15", TimerPattern.Text(-75, "m:ss"));
        Assert.Equal("0", TimerPattern.Text(-0.5, "s")); // no -0
        Assert.Equal("-0.5", TimerPattern.Text(-0.5, "s.f"));
    }

    [Fact]
    public void FractionDetectionSkipsQuotedText()
    {
        Assert.True(TimerPattern.HasFraction("m:ss.fff"));
        Assert.False(TimerPattern.HasFraction("m:ss"));
        Assert.False(TimerPattern.HasFraction("S 'left'")); // the f in 'left' is text, not a token
    }
}
