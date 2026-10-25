using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;
using static MxU.Slides.Core.Tests.Animation.AnimationFixtures;

namespace MxU.Slides.Core.Tests.Animation;

// Port of PresenterCoreTests/AnimationPresetTests.swift (the preset catalog and how a preset lands
// on an object), with the effect catalog alongside. The starter-pack and board cases of the same
// Mac file are in StarterPackTests and PresetBoardsTests.
public class AnimationPresetTests
{
    [Fact]
    public void RecommendedCatalogIsWellFormed()
    {
        var all = AnimationPresetCatalog.Recommended;
        Assert.NotEmpty(all);
        Assert.Equal(all.Count, all.Select(p => p.Id).Distinct().Count()); // ids unique
        foreach (var preset in all)
        {
            Assert.StartsWith("builtin.", preset.Id);
            Assert.True(preset.Steps.Count > 0 || preset.Scroll is not null, preset.Name);
            Assert.All(preset.Steps, s => Assert.True(s.Ranges is null && s.DurationSeconds > 0, preset.Name));
        }
        var names = AnimationPresetCatalog.RecommendedGroups.Select(g => g.Name).ToList();
        foreach (var expected in new[] { "Settle", "Float", "Slide", "Bar Wipe", "Line Draw", "Pill Grow", "Bracket Pop", "Blur Resolve", "Burn", "Glitch", "Type", "Roll", "Crawl" })
            Assert.Contains(expected, names);
    }

    [Fact]
    public void ApplyReplacesAddAppendsAndFollowersStagger()
    {
        var preset = AnimationPresetCatalog.Recommended.Single(p => p.Id == "builtin.settle.both");
        var existing = new List<AnimationStep> { Step("old", AnimationTrigger.OnClick, duration: 0.5) };
        var counter = 0;
        string Id() => $"s{++counter}";

        var leader = AnimationPresetApplication.Apply(preset, existing, AnimationPresetApplication.Mode.Replace, 0, Id);
        Assert.Equal(["s1", "s2"], leader.AnimationSteps!.Select(s => s.Id)); // replace drops the old step; fresh ids
        Assert.Equal(AnimationTrigger.OnClick, leader.AnimationSteps![0].Trigger);
        Assert.Equal(AnimationTrigger.OnDismiss, leader.AnimationSteps[1].Trigger);
        Assert.Null(leader.AnimationSteps[0].DelaySeconds);
        Assert.Equal(["s1", "s2"], leader.AddedIds);

        var follower = AnimationPresetApplication.Apply(preset, existing, AnimationPresetApplication.Mode.Add, 2, Id);
        Assert.Equal(["old", "s3", "s4"], follower.AnimationSteps!.Select(s => s.Id)); // add keeps what was there
        Assert.Equal(AnimationTrigger.WithPrevious, follower.AnimationSteps![1].Trigger); // a follower's lead step rides With Previous
        Assert.Equal(0.3, follower.AnimationSteps[1].DelaySeconds!.Value, 9); // stagger × position
        Assert.Equal(AnimationTrigger.OnDismiss, follower.AnimationSteps[2].Trigger);
        Assert.Equal(0.3, follower.AnimationSteps[2].DelaySeconds!.Value, 9); // the Out staggers too

        var roll = AnimationPresetCatalog.Recommended.Single(p => p.Id == "builtin.roll");
        var rolled = AnimationPresetApplication.Apply(roll, existing, AnimationPresetApplication.Mode.Replace, 0, Id);
        Assert.Null(rolled.AnimationSteps);
        Assert.Equal(BlockScrollAxis.Up, rolled.Scroll?.Axis);
        var crawl = AnimationPresetCatalog.Recommended.Single(p => p.Id == "builtin.crawl");
        Assert.True(crawl.Tilt == 35 && (crawl.Scroll?.FadeTowardTop ?? 0) > 0);

        var ranged = MxuJson.Clone(existing[0]);
        ranged.Ranges = [Range(0, 0, 3)];
        Assert.Null(AnimationPresetApplication.Recipe([ranged])[0].Ranges);
    }

    [Fact]
    public void ApplyingDoesNotShareStepsWithTheCatalog()
    {
        var preset = AnimationPresetCatalog.Recommended.Single(p => p.Id == "builtin.float.in");
        var result = AnimationPresetApplication.Apply(preset, null, AnimationPresetApplication.Mode.Replace, 0);
        result.AnimationSteps![0].DurationSeconds = 9;
        Assert.Equal(0.5, preset.Steps[0].DurationSeconds);
        Assert.Equal("preset", preset.Steps[0].Id);
    }

    [Fact]
    public void ApplyingToObjectsKeepsThePlayOrderWhole()
    {
        var overlay = new Overlay
        {
            Id = "ov", Name = "Overlay",
            Objects = [Text("a", "A", [Step("a.in", AnimationTrigger.WithPrevious)]), Text("b", "B", [Step("b.in", AnimationTrigger.WithPrevious)])],
            AnimationOrder = ["b.in", "a.in"],
        };
        var preset = AnimationPresetCatalog.Recommended.Single(p => p.Id == "builtin.blurResolve.both");
        var counter = 0;
        var added = AnimationPresetApplication.ApplyTo(overlay, ["a", "b"], preset, AnimationPresetApplication.Mode.Replace, () => $"n{++counter}");
        Assert.Equal(["n1", "n2", "n3", "n4"], added);
        Assert.Equal(["n1", "n2", "n3", "n4"], overlay.AnimationOrder); // the old steps left the order; the new ones joined its end
        Assert.Equal(AnimationTrigger.WithPrevious, overlay.Objects[1].AnimationSteps![0].Trigger); // b follows a
        Assert.Equal(0.15, overlay.Objects[1].AnimationSteps![0].DelaySeconds!.Value, 9);

        var roll = AnimationPresetCatalog.Recommended.Single(p => p.Id == "builtin.crawl");
        AnimationPresetApplication.ApplyTo(overlay, ["a"], roll, AnimationPresetApplication.Mode.Replace);
        Assert.Equal(BlockScrollAxis.Up, overlay.Objects[0].TextStyle?.Scroll?.Axis);
        Assert.Equal(35, overlay.Objects[0].Tilt);
        Assert.Null(overlay.Objects[0].AnimationSteps);
        Assert.Equal(["n3", "n4"], overlay.AnimationOrder); // a's steps are gone from the order too

        // Emptied by a replace, the order goes back to document order (the Mac's appendToOrder only adds to a list it has).
        var single = new Overlay { Id = "o2", Name = "O", Objects = [Text("a", "A", [Step("a.in", AnimationTrigger.WithPrevious)])], AnimationOrder = ["a.in"] };
        AnimationPresetApplication.ApplyTo(single, ["a"], preset, AnimationPresetApplication.Mode.Replace);
        Assert.Null(single.AnimationOrder);
        Assert.Equal(2, single.Objects[0].AnimationSteps!.Count);
    }

    [Fact]
    public void EffectCatalogIsWellFormedAndAppliesOrAdds()
    {
        var all = EffectPresetCatalog.Recommended;
        Assert.Equal(all.Count, all.Select(p => p.Id).Distinct().Count());
        Assert.All(all, p => Assert.StartsWith("builtin.", p.Id));
        Assert.All(all, p => Assert.NotEmpty(p.Effects));
        Assert.Equal(["Frost", "Static", "Heat Haze", "Long Trails", "Amoeba", "Ghosty"], EffectPresetCatalog.RecommendedGroups.Select(g => g.Name));
        Assert.Equal(4, all.Single(p => p.Id == "builtin.amoeba").Effects.Count);

        var plate = new SlideObject { Id = "p", ObjectKind = SlideObjectKind.Shape, Name = "Plate", Text = "" };
        EffectPresetCatalog.ApplyTo(plate, all.Single(p => p.Id == "builtin.frost.light"), AnimationPresetApplication.Mode.Replace);
        EffectPresetCatalog.ApplyTo(plate, all.Single(p => p.Id == "builtin.heatHaze"), AnimationPresetApplication.Mode.Add);
        Assert.Equal([EffectKind.Scatter, EffectKind.Warp], plate.Effects!.Select(e => e.EffectKind));
        plate.Effects![0].Amount = 1;
        Assert.Equal(12, all.Single(p => p.Id == "builtin.frost.light").Effects[0].Amount); // the catalog isn't shared
        plate.EffectsApplyBelow = true;
        EffectPresetCatalog.ApplyTo(plate, new EffectPreset { Id = "none", Name = "None", Effects = [] }, AnimationPresetApplication.Mode.Replace);
        Assert.Null(plate.Effects);
        Assert.Null(plate.EffectsApplyBelow);
    }
}
