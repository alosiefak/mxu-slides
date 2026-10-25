using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Animation;

// The overlay editor's preset gallery (Windows-only): each tile plays a preset on a small sample
// lower third, filed as the editor files presets on an overlay.
public class PresetSamplesTests
{
    private static void PlaysWithoutAClick(Overlay sample, string label)
    {
        var groups = AnimationSequence.Groups(sample.Objects, sample.AnimationOrder);
        Assert.True(groups.Click.Count == 0, $"{label}: nothing waits for a click");
        Assert.True(OverlayPreview.Duration(sample) > 0.5, $"{label}: something to watch");
    }

    [Fact]
    public void EveryKineticPresetPlaysOnTheSampleWordByWordOrLineByLine()
    {
        foreach (var preset in KineticPresets.All)
        {
            var sample = PresetSamples.Make((c, ids) => KineticText.ApplyTo(c, ids, preset, AnimationPresetApplication.Mode.Replace));
            PlaysWithoutAClick(sample, preset.Name);
            Assert.Empty(AnimationEditing.OverlayWarnings(sample));
            var words = sample.Objects.Single(o => o.Id == PresetSamples.TextId);
            var ins = words.AnimationSteps!.Where(s => s.Kind == AnimationKind.In).ToList();
            var expected = preset.Split switch
            {
                KineticSplit.Lines => 2,
                KineticSplit.Words => 6, // "Amazing grace" / "how sweet the sound"
                _ => 1,
            };
            Assert.True(ins.Count == expected, $"{preset.Name}: {ins.Count} Ins");
            // The plate is no text: it plays the preset's In as a whole.
            Assert.All(sample.Objects.Single(o => o.Id == PresetSamples.PlateId).AnimationSteps!, s => Assert.Null(s.Ranges));
        }
    }

    [Fact]
    public void EveryMacPresetPlaysOnTheSampleAndLeavesTheCatalogAlone()
    {
        var before = MxuJson.Serialize(AnimationPresetCatalog.Recommended);
        foreach (var preset in AnimationPresetCatalog.Recommended)
        {
            var sample = PresetSamples.Make((c, ids) => AnimationPresetApplication.ApplyTo(c, ids, preset, AnimationPresetApplication.Mode.Replace));
            if (preset.Steps.Count == 0)
            {
                // Roll and Crawl scroll the words instead of adding steps.
                Assert.NotNull(sample.Objects.Single(o => o.Id == PresetSamples.TextId).TextStyle?.Scroll);
                continue;
            }
            PlaysWithoutAClick(sample, preset.Name);
        }
        Assert.Equal(before, MxuJson.Serialize(AnimationPresetCatalog.Recommended));
    }

    [Fact]
    public void EachSampleIsNew()
    {
        var preset = KineticPresets.All[0];
        var a = PresetSamples.Make((c, ids) => KineticText.ApplyTo(c, ids, preset, AnimationPresetApplication.Mode.Replace));
        var b = PresetSamples.Make((c, ids) => KineticText.ApplyTo(c, ids, preset, AnimationPresetApplication.Mode.Replace));
        Assert.NotSame(a.Objects[1], b.Objects[1]);
        a.Objects[1].Text = "changed";
        Assert.Equal("Amazing grace\nhow sweet the sound", b.Objects[1].Text);
    }
}
