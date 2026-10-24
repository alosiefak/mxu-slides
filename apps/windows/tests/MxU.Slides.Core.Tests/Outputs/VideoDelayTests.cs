using MxU.Slides.Core.Outputs;

namespace MxU.Slides.Core.Tests.Outputs;

// Ported from OutputEngineTests/VideoDelayTests.swift.
public class VideoDelayTests
{
    [Fact]
    public void DelayRoundTripsAndClamps()
    {
        var outputs = new OutputConfiguration();
        var screen = Guid.NewGuid();
        outputs.SetVideoDelay(screen, 5);
        Assert.Equal(5, outputs.VideoDelay(screen));
        outputs.SetVideoDelay(screen, 999);
        Assert.Equal(OutputConfiguration.MaxVideoDelayFrames, outputs.VideoDelay(screen));
        outputs.SetVideoDelay(screen, 0);
        Assert.Equal(0, outputs.VideoDelay(screen));
        Assert.False(outputs.VideoDelays.ContainsKey(screen)); // zero is absence, not a stored zero
    }

    [Fact]
    public void ProviderSeesLiveEdits()
    {
        var outputs = new OutputConfiguration();
        var screen = Guid.NewGuid();
        var provider = outputs.VideoDelayProvider(SwiftUuid.Format(screen));
        Assert.Equal(0, provider());
        outputs.SetVideoDelay(screen, 3);
        Assert.Equal(3, provider()); // a mid-service nudge reaches running mirrors
        Assert.Equal(0, outputs.VideoDelayProvider("not-a-uuid")());
    }

    [Fact]
    public void DelayChangeNotifiesPersistence()
    {
        var outputs = new OutputConfiguration();
        var saves = 0;
        outputs.ConfigurationChanged += () => saves++;
        var screen = Guid.NewGuid();
        outputs.SetVideoDelay(screen, 4);
        Assert.Equal(1, saves);
        outputs.SetVideoDelay(screen, 4);
        Assert.Equal(1, saves); // no-op sets don't churn the save path
    }

    [Fact]
    public void RemovingAScreenDropsItsDelay()
    {
        var outputs = new OutputConfiguration();
        var screen = outputs.AddScreen("Test", 64, 36)!;
        outputs.SetVideoDelay(screen.Id, 6);
        var provider = outputs.VideoDelayProvider(SwiftUuid.Format(screen.Id));
        outputs.RemoveScreen(screen.Id);
        Assert.Equal(0, outputs.VideoDelay(screen.Id));
        Assert.Equal(0, provider()); // running feeds read the cleared value
    }
}
