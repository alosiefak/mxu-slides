using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Animation;

// The live header's transition chips (TransitionChip in Sources/PerformanceHUD.swift).
public class TransitionChipsTests
{
    [Fact]
    public void TheChipsOfferTheMacsFiveKinds()
    {
        Assert.Equal([TransitionKind.Cut, TransitionKind.Dissolve, TransitionKind.FadeBlack, TransitionKind.FadeWhite, TransitionKind.BlurDissolve],
            TransitionChips.Choices);
        Assert.Equal((0.7, 0.5), (TransitionChips.MediaFallbackSeconds, TransitionChips.SlideFallbackSeconds));
    }

    [Fact]
    public void TheChipReadsShortAndACutHasNoLength()
    {
        Assert.Equal("Cut", TransitionChips.Caption(TransitionKind.Cut, 0.5));
        Assert.Equal("Dissolve 0.7 s", TransitionChips.Caption(TransitionKind.Dissolve, 0.7));
        Assert.Equal("Blur Dslv 1 s", TransitionChips.Caption(TransitionKind.BlurDissolve, 1));
        Assert.Equal("Fade White 0.6 s", TransitionChips.Caption(TransitionKind.FadeWhite, 0.6));
        Assert.False(TransitionChips.ShowsDuration(TransitionKind.Cut));
        Assert.True(TransitionChips.ShowsDuration(TransitionKind.FadeBlack));
        Assert.EndsWith("items with their own always win", TransitionChips.Help(TransitionKind.Dissolve, 0.4));
    }

    [Fact]
    public void TheSliderSnapsToTenths()
    {
        Assert.Equal(0.7, TransitionChips.Snapped(0.66), 9);
        Assert.Equal(1.2, TransitionChips.Snapped(1.24), 9);
        Assert.Equal(1.3, TransitionChips.Snapped(1.26), 9);
        Assert.Equal(0.1, TransitionChips.Snapped(0.1), 9);
    }
}
