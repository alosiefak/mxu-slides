using System.Globalization;
using MxU.Slides.Core.Stage;

namespace MxU.Slides.Core.Tests.Stage;

public class DateFormatPatternTests
{
    // Thursday 23 July 2026, 09:46:40 UTC.
    private static readonly DateTimeOffset FixedDate = DateTimeOffset.FromUnixTimeSeconds(1_784_800_000);

    private static string Format(string pattern, CultureInfo? culture = null) =>
        DateFormatPattern.Format(FixedDate, pattern, TimeZoneInfo.Utc, culture ?? CultureInfo.InvariantCulture);

    [Fact]
    public void TranslatesTheCommonFields()
    {
        Assert.Equal(@"h\:mm\ tt", DateFormatPattern.ToDotNet("h:mm a"));
        Assert.Equal("%h", DateFormatPattern.ToDotNet("h"));
        Assert.Equal(@"dddd\,\ MMMM\ d\,\ yyyy", DateFormatPattern.ToDotNet("EEEE, MMMM d, yyyy"));
    }

    [Theory]
    [InlineData("h:mm a", "9:46 AM")]
    [InlineData("hh:mm:ss", "09:46:40")]
    [InlineData("HH:mm", "09:46")]
    [InlineData("H", "9")]
    [InlineData("h", "9")]
    [InlineData("m", "46")]
    [InlineData("s", "40")]
    [InlineData("EEE d MMM", "Thu 23 Jul")]
    [InlineData("EEEE, MMMM d, yyyy", "Thursday, July 23, 2026")]
    [InlineData("dd/MM/yy", "23/07/26")]
    [InlineData("'Doors at' h a", "Doors at 9 AM")]
    [InlineData("'it''s' h", "it's 9")]
    [InlineData("h.mm", "9.46")]
    [InlineData("''", "'")]
    [InlineData("'", "")]
    public void FormatsLikeDateFormatter(string pattern, string expected) => Assert.Equal(expected, Format(pattern));

    [Fact]
    public void AmPmIsAlwaysUpperCase()
    {
        Assert.Equal("9:46 AM", Format("h:mm a", CultureInfo.GetCultureInfo("en-GB")));
        Assert.Equal("9:46 PM", DateFormatPattern.Format(FixedDate.AddHours(12), "h:mm a", TimeZoneInfo.Utc, CultureInfo.GetCultureInfo("en-GB")));
    }

    [Fact]
    public void SeparatorsAreLiteralWhateverTheCulture() =>
        Assert.Equal("09:46", Format("HH:mm", CultureInfo.GetCultureInfo("fi-FI")));

    [Fact]
    public void ReadsTheGivenTimeZone()
    {
        var plusTwo = TimeZoneInfo.CreateCustomTimeZone("Test+2", TimeSpan.FromHours(2), "Test+2", "Test+2");
        Assert.Equal("11:46", DateFormatPattern.Format(FixedDate, "HH:mm", plusTwo, CultureInfo.InvariantCulture));
    }
}
