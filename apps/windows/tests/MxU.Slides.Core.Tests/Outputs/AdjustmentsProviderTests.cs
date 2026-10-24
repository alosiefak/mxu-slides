using MxU.Slides.Core.Outputs;

namespace MxU.Slides.Core.Tests.Outputs;

// Ported from OutputEngineTests/AdjustmentsProviderTests.swift.
public class AdjustmentsProviderTests
{
    [Fact]
    public void ProviderSeesLiveEdits()
    {
        var outputs = new OutputConfiguration();
        var screen = Guid.NewGuid();
        var provider = outputs.AdjustmentsProvider(SwiftUuid.Format(screen));
        Assert.Null(provider()); // untouched screen reads null, so mirrors skip the pass

        outputs.SetAdjustments(screen, new OutputAdjustments { Brightness = -0.5 });
        Assert.Equal(-0.5, provider()?.Brightness); // a mid-service tweak reaches running mirrors

        outputs.SetAdjustments(screen, new OutputAdjustments());
        Assert.Null(provider()); // reset to neutral reads null again
        Assert.Null(outputs.AdjustmentsProvider("not-a-uuid")());
    }

    [Fact]
    public void RemovalClearsAdjustments()
    {
        var outputs = new OutputConfiguration();
        outputs.AddScreen("Doomed", 192, 108);
        var screen = outputs.Screens.First(s => s.Name == "Doomed");
        outputs.SetAdjustments(screen.Id, new OutputAdjustments { Gamma = 0.2 });
        var provider = outputs.AdjustmentsProvider(SwiftUuid.Format(screen.Id));
        outputs.RemoveScreen(screen.Id);
        Assert.False(outputs.StoredAdjustments.ContainsKey(screen.Id)); // removal drops the stored correction
        Assert.Null(provider()); // running feeds read the cleared value
    }

    [Fact]
    public void NeutralButSetValuesAreStoredYetNotApplied()
    {
        // Only a completely empty value is dropped (as on the Mac); a value that
        // changes nothing is kept but providers and the renderer skip it.
        var outputs = new OutputConfiguration();
        var screen = Guid.NewGuid();
        outputs.SetAdjustments(screen, new OutputAdjustments { Brightness = 0, RotationDegrees = 360 });
        Assert.True(outputs.StoredAdjustments.ContainsKey(screen));
        Assert.Null(outputs.EffectiveAdjustments(screen));
        Assert.Null(outputs.AdjustmentsProvider(SwiftUuid.Format(screen).ToLowerInvariant())());
    }
}
