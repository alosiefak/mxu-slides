using MxU.Slides.Broadcast.Media;
using Decision = MxU.Slides.Broadcast.Media.ReplayPacer.Decision;

namespace MxU.Slides.Broadcast.Tests;

/// <summary>The replay's picture-by-picture choices (Windows rules on top of the Mac's quarter-second drop).</summary>
public sealed class ReplayPacerTests
{
    [Fact]
    public void OnTimePicturesAreShown()
    {
        var pacer = new ReplayPacer();
        for (var i = 0; i < 60; i++)
        {
            var due = i / 30.0;
            Assert.Equal(Decision.Show, pacer.Decide(due + 0.01, due, due));
        }
        Assert.Equal(0, pacer.Skips);
    }

    [Fact]
    public void AQuarterSecondLatePictureIsDroppedAsOnTheMac()
    {
        var pacer = new ReplayPacer();
        Assert.Equal(Decision.Show, pacer.Decide(0, 0, 0));
        Assert.Equal(Decision.Drop, pacer.Decide(0.4, 0.1, 0.1));
        Assert.Equal(Decision.Show, pacer.Decide(0.45, 0.4, 0.4));
    }

    [Fact]
    public void ADecoderThatCantKeepUpStillShowsAPictureEveryHalfSecond()
    {
        // Decoding takes 50 ms a picture, but the file has 30 pictures a second (33 ms each).
        var pacer = new ReplayPacer();
        var shown = new List<double>();
        double now = 0;
        for (var i = 0; i < 30; i++)
        {
            var due = i / 30.0;
            now = Math.Max(now + 0.05, due);
            if (pacer.Decide(now, due, due) == Decision.Show) shown.Add(now);
        }
        Assert.True(shown.Count >= 3, $"only {shown.Count} pictures shown");
        for (var i = 1; i < shown.Count; i++)
            Assert.True(shown[i] - shown[i - 1] <= ReplayPacer.LongestWithoutAPicture + 0.05 + 1e-9, $"a gap of {shown[i] - shown[i - 1]:0.00} s");
    }

    [Fact]
    public void FarBehindItSkipsAheadToThePresent()
    {
        var pacer = new ReplayPacer();
        Assert.Equal(Decision.Show, pacer.Decide(0, 0, 0));
        // The picture at 2.0 s in the file was due at 2.0 but is only ready at 3.5.
        Assert.Equal(Decision.SkipAhead, pacer.Decide(3.5, 2.0, 2.0));
        Assert.Equal(1, pacer.Skips);
        Assert.Equal(2.0 + 1.5 + ReplayPacer.SkipLead, pacer.SkipTo, 6);
    }

    [Fact]
    public void AfterAJumpItCatchesUpFromTheKeyFrameInsteadOfJumpingAgain()
    {
        var pacer = new ReplayPacer();
        Assert.Equal(Decision.Show, pacer.Decide(0, 0, 0));
        Assert.Equal(Decision.SkipAhead, pacer.Decide(3.5, 2.0, 2.0));
        var target = pacer.SkipTo; // 3.75 s into the file
        // The reader lands on the key frame at 2.5 s: well over a second late, but it mustn't jump again.
        var decisions = new List<Decision>();
        var now = 3.6;
        for (var position = 2.5; position < target; position += 1 / 30.0)
        {
            decisions.Add(pacer.Decide(now, position, position));
            now += 0.005;
        }
        Assert.DoesNotContain(Decision.SkipAhead, decisions);
        Assert.Equal(1, pacer.Skips);
        // Once past where it jumped, an on-time picture is shown.
        Assert.Equal(Decision.Show, pacer.Decide(target + 0.1, target + 0.1, target + 0.1));
    }

    [Fact]
    public void ItCanSkipAgainIfItFallsBehindLater()
    {
        var pacer = new ReplayPacer();
        Assert.Equal(Decision.SkipAhead, pacer.Decide(5, 2, 2));
        var past = pacer.SkipTo + 1;
        Assert.Equal(Decision.SkipAhead, pacer.Decide(past + 3, past, past));
        Assert.Equal(2, pacer.Skips);
    }
}
