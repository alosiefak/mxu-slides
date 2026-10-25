using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using static MxU.Slides.Core.Tests.Animation.AnimationFixtures;

namespace MxU.Slides.Core.Tests.Animation;

// Kinetic text (Windows-only): presets that cut a text into lines or words and bring each in turn,
// built only from ordinary ranged steps, and theme builds that do the same over each slide's words.
public class KineticTextTests
{
    private static KineticPreset Preset(string id) => KineticPresets.All.Single(p => p.Id == id);

    private static Dictionary<string, double> Starts(IAnimatedComposition c) =>
        AnimationSequence.Schedule(c.Objects, c.AnimationOrder).ToDictionary(p => p.Key, p => p.Value.Start);

    [Fact]
    public void ThePresetsAreWellFormedAndBuiltFromSharedKinds()
    {
        var all = KineticPresets.All;
        Assert.True(all.Count >= 12);
        Assert.Equal(all.Count, all.Select(p => p.Id).Distinct().Count());
        foreach (var preset in all)
        {
            Assert.StartsWith("kinetic.", preset.Id);
            Assert.NotEmpty(preset.In);
            Assert.NotEmpty(preset.Out);
            Assert.All(preset.In, s => Assert.True(s.Kind is AnimationKind.In or AnimationKind.Emphasis && s.DurationSeconds > 0 && s.Ranges is null));
            Assert.All(preset.Out, s => Assert.True(s.Kind == AnimationKind.Out && s.Trigger == AnimationTrigger.OnDismiss));
            Assert.Equal(AnimationKind.In, preset.In[0].Kind);
            Assert.Equal(AnimationTrigger.OnClick, preset.In[0].Trigger); // like the Mac's presets: the editor files it
            // Text floats in place: moves are offsets, never "from the edge of the screen".
            Assert.All(preset.In.Concat(preset.Out), s => Assert.True(s.Animation != StepAnimation.Move || s.Edge is null, preset.Name));
            Assert.True(preset.Split == KineticSplit.Whole || preset.Stagger > 0, preset.Name);
        }
        foreach (var expected in new[] { "kinetic.wordRise", "kinetic.typewriter", "kinetic.softBlur", "kinetic.scalePop", "kinetic.wipeReveal",
                     "kinetic.glitch", "kinetic.burn", "kinetic.lightSweep" })
            Assert.Contains(expected, all.Select(p => p.Id));
    }

    [Fact]
    public void EveryPresetFiledOnAnOverlayIsWarningFree()
    {
        // What the overlay editor shows next to a step ("already shown", "waits for a click") must stay empty.
        foreach (var preset in KineticPresets.All)
        {
            var overlay = new Overlay { Id = "ov", Name = "O", Objects = [Text("t", "Grace upon grace\nand peace to you")] };
            var ids = KineticText.ApplyTo(overlay, ["t"], preset, AnimationPresetApplication.Mode.Replace);
            OverlaySteps.File(overlay, ids);
            var warnings = AnimationEditing.OverlayWarnings(overlay);
            Assert.True(warnings.Count == 0, $"{preset.Name}: {string.Join("; ", warnings.Values)}");
            var groups = AnimationSequence.Groups(overlay.Objects, overlay.AnimationOrder);
            Assert.Empty(groups.Click);
            Assert.Equal(preset.Out.Count, groups.Exit.Count);
        }
    }

    [Fact]
    public void WordByWordGivesEachWordItsOwnStepAStaggerApart()
    {
        var overlay = new Overlay { Id = "ov", Name = "O", Objects = [Text("t", "Grace upon grace")] };
        var preset = Preset("kinetic.wordRise");
        var counter = 0;
        var ids = KineticText.ApplyTo(overlay, ["t"], preset, AnimationPresetApplication.Mode.Replace, () => $"k{++counter}");
        var steps = overlay.Objects[0].AnimationSteps!;
        Assert.Equal(4, steps.Count); // three words in, the whole thing out
        Assert.Equal(ids, steps.Select(s => s.Id));
        Assert.Equal(["Grace", "upon", "grace"], steps.Take(3).Select(s => AnimationRanges.Text(s.Ranges!.Single(), "Grace upon grace")));
        Assert.Null(steps[3].Ranges);
        Assert.Equal(AnimationTrigger.OnClick, steps[0].Trigger);
        Assert.All(steps.Skip(1).Take(2), s => Assert.Equal(AnimationTrigger.WithPrevious, s.Trigger));

        var starts = Starts(overlay);
        Assert.Equal(0, starts["k1"], 6);
        Assert.Equal(0.08, starts["k2"], 6);
        Assert.Equal(0.16, starts["k3"], 6);
    }

    [Fact]
    public void LeadsStayAStaggerApartWhenAPieceHasSeveralSteps()
    {
        // Light sweep: each word fades in, and a colour flash follows it 0.25 s later. The next
        // word's fade still starts 0.06 s after this word's, not 0.06 s after its flash.
        var overlay = new Overlay { Id = "ov", Name = "O", Objects = [Text("t", "one two three")] };
        var counter = 0;
        KineticText.ApplyTo(overlay, ["t"], Preset("kinetic.lightSweep"), AnimationPresetApplication.Mode.Replace, () => $"k{++counter}");
        var starts = Starts(overlay);
        Assert.Equal([0, 0.25, 0.06, 0.31, 0.12, 0.37], new[] { "k1", "k2", "k3", "k4", "k5", "k6" }.Select(id => Math.Round(starts[id], 6)));
    }

    [Fact]
    public void LineByLineAndWholeAndNonText()
    {
        var plate = new SlideObject { Id = "plate", ObjectKind = SlideObjectKind.Shape, Name = "Plate", Text = "" };
        var overlay = new Overlay { Id = "ov", Name = "O", Objects = [plate, Text("t", "Line one\nLine two\n\nLine four")] };
        KineticText.ApplyTo(overlay, ["plate", "t"], Preset("kinetic.lineRise"), AnimationPresetApplication.Mode.Replace);
        Assert.Equal(2, plate.AnimationSteps!.Count); // a shape plays the build as a whole: one In, one Out
        Assert.Null(plate.AnimationSteps[0].Ranges);
        var text = overlay.Objects[1].AnimationSteps!;
        Assert.Equal([0, 1, 3], text.Where(s => s.Kind == AnimationKind.In).Select(s => s.Ranges!.Single().Line)); // the empty line is skipped
        // The text follows the plate, a beat later (the per-object stagger the Mac's presets use).
        Assert.Equal(AnimationTrigger.WithPrevious, text[0].Trigger);
        Assert.Equal(0.15, text[0].DelaySeconds!.Value, 6);

        var single = new Overlay { Id = "o2", Name = "O", Objects = [Text("t", "Hallelujah")] };
        KineticText.ApplyTo(single, ["t"], Preset("kinetic.wordRise"), AnimationPresetApplication.Mode.Replace);
        // One word still gets its own ranged step, so typing more words later splits it (Reflow).
        Assert.Equal(10, single.Objects[0].AnimationSteps![0].Ranges!.Single().Length);
        var empty = new Overlay { Id = "o3", Name = "O", Objects = [Text("t", "")] };
        KineticText.ApplyTo(empty, ["t"], Preset("kinetic.wordRise"), AnimationPresetApplication.Mode.Replace);
        Assert.Null(empty.Objects[0].AnimationSteps![0].Ranges); // nothing to split: the whole box
    }

    [Fact]
    public void ABuildFollowsNewWordsTypedIntoItsField()
    {
        var overlay = new Overlay
        {
            Id = "ov", Name = "O",
            Objects = [new SlideObject { Id = "plate", ObjectKind = SlideObjectKind.Shape, Name = "Plate", Text = "", AnimationSteps = [Step("plate.in", AnimationTrigger.WithPrevious)] }, Text("t", "Full Name")],
        };
        var ids = KineticText.ApplyTo(overlay, ["t"], Preset("kinetic.wordRise"), AnimationPresetApplication.Mode.Add);
        overlay.AnimationOrder = ["plate.in", .. ids];
        OverlaySteps.File(overlay, ids);
        var before = AnimationSequence.Schedule(overlay.Objects, overlay.AnimationOrder);
        var text = overlay.Objects[1];
        Assert.False(KineticText.Reflow(overlay)); // nothing to do while the words match

        text.Text = "Pastor Grace Okafor";
        var counter = 0;
        Assert.True(KineticText.Reflow(overlay, () => $"new{++counter}"));
        var ins = text.AnimationSteps!.Where(s => s.Kind == AnimationKind.In).ToList();
        Assert.Equal(["Pastor", "Grace", "Okafor"], ins.Select(s => AnimationRanges.Text(s.Ranges!.Single(), text.Text)));
        Assert.Equal([ids[0], ids[1], "new1"], ins.Select(s => s.Id)); // the old pieces keep their ids
        var after = AnimationSequence.Schedule(overlay.Objects, overlay.AnimationOrder);
        Assert.Equal(before[ids[0]].Start, after[ids[0]].Start, 6); // same start
        Assert.Equal(after[ids[1]].Start + 0.08, after["new1"].Start, 6); // same stagger
        Assert.Empty(AnimationEditing.OverlayWarnings(overlay));
        Assert.Contains("new1", overlay.AnimationOrder!);
        Assert.Equal(overlay.AnimationOrder!.Count, overlay.AnimationOrder.Distinct().Count());
        Assert.Single(text.AnimationSteps!, s => s.Kind == AnimationKind.Out); // the Out stays

        text.Text = "Grace";
        Assert.True(KineticText.Reflow(overlay));
        Assert.Equal("Grace", AnimationRanges.Text(text.AnimationSteps!.First(s => s.Kind == AnimationKind.In).Ranges!.Single(), "Grace"));
        Assert.DoesNotContain(ids[1], overlay.AnimationOrder!);
        Assert.Equal(1, text.AnimationSteps!.Count(s => s.Kind == AnimationKind.In));
    }

    [Fact]
    public void ALineBuildWithTwoStepsAPieceReflowsWhole()
    {
        var overlay = new Overlay { Id = "ov", Name = "O", Objects = [Text("t", "one\ntwo")] };
        KineticText.ApplyTo(overlay, ["t"], Preset("kinetic.lineRise"), AnimationPresetApplication.Mode.Replace);
        overlay.Objects[0].Text = "one\ntwo\nthree";
        Assert.True(KineticText.Reflow(overlay));
        Assert.Equal([0, 1, 2], overlay.Objects[0].AnimationSteps!.Where(s => s.Kind == AnimationKind.In).Select(s => s.Ranges!.Single().Line));

        var sweep = new Overlay { Id = "o2", Name = "O", Objects = [Text("t", "a b")] };
        KineticText.ApplyTo(sweep, ["t"], Preset("kinetic.lightSweep"), AnimationPresetApplication.Mode.Replace);
        sweep.Objects[0].Text = "a b c";
        Assert.True(KineticText.Reflow(sweep));
        var steps = sweep.Objects[0].AnimationSteps!;
        Assert.Equal(7, steps.Count); // three words × (fade + flash), and the Out
        var starts = AnimationSequence.Schedule(sweep.Objects, sweep.AnimationOrder);
        var fades = steps.Where(s => s.Kind == AnimationKind.In).Select(s => Math.Round(starts[s.Id].Start, 6)).ToList();
        Assert.Equal([0, 0.06, 0.12], fades);
    }

    [Fact]
    public void StepsOnAFewChosenWordsAreLeftAlone()
    {
        // The Mac's "one step per word" on a selection that starts mid-text: not a whole-text build.
        var o = Text("t", "Grace upon grace", [Step("w", AnimationTrigger.OnClick, ranges: [Range(0, 6, 4)])]);
        var overlay = new Overlay { Id = "ov", Name = "O", Objects = [o] };
        o.Text = "Something else entirely";
        Assert.False(KineticText.Reflow(overlay));
        Assert.Equal(6, o.AnimationSteps![0].Ranges![0].Column);
    }

    [Fact]
    public void AddKeepsTheOldStepsAndTheOrderGrows()
    {
        var overlay = new Overlay { Id = "ov", Name = "O", Objects = [Text("t", "a b", [Step("old", AnimationTrigger.WithPrevious)])], AnimationOrder = ["old"] };
        var ids = KineticText.ApplyTo(overlay, ["t"], Preset("kinetic.wordBlur"), AnimationPresetApplication.Mode.Add);
        Assert.Equal(["old", .. ids], overlay.AnimationOrder);
        Assert.Equal("old", overlay.Objects[0].AnimationSteps![0].Id);
    }

    [Fact]
    public void ARangedRiseHidesEachWordUntilItsTurn()
    {
        // What the outputs draw: early on, only the first word shows; once all have started, all of it.
        var overlay = new Overlay { Id = "ov", Name = "O", Objects = [Text("t", "Grace upon grace")] };
        var ids = KineticText.ApplyTo(overlay, ["t"], Preset("kinetic.wordRise"), AnimationPresetApplication.Mode.Replace);
        OverlaySteps.File(overlay, ids); // as the overlay editor does: it plays when the overlay goes up
        var groups = AnimationSequence.Groups(overlay.Objects, overlay.AnimationOrder);
        Assert.Equal(3, groups.Auto.Count);
        Assert.Empty(groups.Click);
        Assert.Single(groups.Exit);
        Assert.Equal(0.16, AnimationSequence.Schedule(overlay.Objects, overlay.AnimationOrder)[ids[2]].Start, 6); // the stagger survives the filing
        var visibility = AnimationTimeline.Visibility(
            AnimationSequence.SceneSteps(overlay.Objects, overlay.AnimationOrder)["t"], new AnimationContext(0), 0.04);
        Assert.Equal(2, visibility.HiddenRanges.Count); // "upon" and the second "grace" wait their turn
        var later = AnimationTimeline.Visibility(
            AnimationSequence.SceneSteps(overlay.Objects, overlay.AnimationOrder)["t"], new AnimationContext(0), 2);
        Assert.Empty(later.HiddenRanges);
    }

    [Fact]
    public void DonatingABuildReplacesTheDesignsInsAndKeepsTheOut()
    {
        var build = new KineticBuild(KineticSplit.Lines, 0.2, [Step("tpl", AnimationTrigger.OnClick, animation: StepAnimation.Move, duration: 0.7)]);
        List<AnimationStep> donated =
        [
            Step("lyrics::in", AnimationTrigger.WithPrevious, delay: 0.1, duration: 0.6),
            Step("lyrics::blur", AnimationTrigger.WithPrevious, animation: StepAnimation.Blur),
            Step("lyrics::out", AnimationTrigger.OnClick, AnimationKind.Out),
        ];
        var (steps, ids) = KineticText.Donate(build, "Amazing grace\nhow sweet the sound\nthat saved", donated, ["in", "blur", "out"]);
        Assert.Equal(4, steps.Count); // three lines in, the design's own Out
        Assert.Equal([0, 1, 2], steps.Take(3).Select(s => s.Ranges!.Single().Line));
        Assert.Equal((AnimationTrigger.WithPrevious, 0.1), (steps[0].Trigger, steps[0].DelaySeconds)); // starts when the design's In did
        Assert.Equal((AnimationTrigger.WithPrevious, 0.2), (steps[1].Trigger, steps[1].DelaySeconds));
        Assert.Equal("lyrics::out", steps[3].Id);
        Assert.Equal(["lyrics::in::kinetic0", "lyrics::in::kinetic1", "lyrics::in::kinetic2"], ids["in"]);
        Assert.Empty(ids["blur"]); // the other Ins give way to the build
        Assert.Equal(["lyrics::out"], ids["out"]);
    }

    [Fact]
    public void ALongSlidesLinesSqueezeCloserSoTheBuildStillArrivesQuickly()
    {
        var build = new KineticBuild(KineticSplit.Lines, 0.2, [Step("tpl", AnimationTrigger.OnClick, animation: StepAnimation.Move, duration: 0.85)]);
        var four = string.Join("\n", Enumerable.Range(1, 4).Select(i => $"Line {i}"));
        var sixteen = string.Join("\n", Enumerable.Range(1, 16).Select(i => $"Line {i}"));
        Assert.Equal(0.2, KineticText.StaggerFor(build, four)); // a slide of the size the design was made for keeps its pace
        Assert.Equal(KineticText.LongTextSpread / 15, KineticText.StaggerFor(build, sixteen), 3);

        var (steps, _) = KineticText.Donate(build, sixteen, [Step("lyrics::in", AnimationTrigger.WithPrevious, delay: 0)], ["in"]);
        var slide = new Slide { Id = "s", Name = "S", Objects = [Text("lyrics", sixteen, steps)] };
        var starts = Starts(slide);
        Assert.Equal(16, starts.Count);
        Assert.InRange(starts.Values.Max() - starts.Values.Min(), KineticText.LongTextSpread - 0.01, KineticText.LongTextSpread + 0.01);

        // A slow design (four lines already spread wider than that) is never squeezed below its four-line width.
        var slow = build with { Stagger = 0.4 };
        Assert.Equal(1.2 / 15, KineticText.StaggerFor(slow, sixteen), 3);
    }

    [Fact]
    public void ABuildWithNothingToSplitPlaysOnce()
    {
        var build = new KineticBuild(KineticSplit.Lines, 0.2, [Step("tpl", AnimationTrigger.OnClick, animation: StepAnimation.Move)]);
        var (steps, _) = KineticText.Donate(build, "One line only", [Step("x::in", AnimationTrigger.WithPrevious)], ["in"]);
        Assert.Equal(13, steps.Single().Ranges!.Single().Length); // one line, all of it
        var (blank, _) = KineticText.Donate(build, "", [Step("x::in", AnimationTrigger.WithPrevious)], ["in"]);
        Assert.Null(blank.Single().Ranges);
        var (outs, outIds) = KineticText.Donate(build, "a\nb", [Step("x::out", AnimationTrigger.OnDismiss, AnimationKind.Out)], ["out"]);
        Assert.Equal("x::out", outs.Single().Id); // no In to replace: the steps are as they were
        Assert.Equal(["x::out"], outIds["out"]);
    }
}

// The static hook Compose reads is shared, so these run on their own, after the parallel tests.
[CollectionDefinition("Kinetic hook", DisableParallelization = true)]
public sealed class KineticHookCollection;

[Collection("Kinetic hook")]
public class KineticComposeTests
{
    [Fact]
    public void ComposeExpandsTheDesignsOrderWithTheBuild()
    {
        var placeholder = Text("ph", "Sample", [Step("in", AnimationTrigger.WithPrevious, animation: StepAnimation.Move), Step("out", AnimationTrigger.OnClick, AnimationKind.Out)]);
        var rule = new SlideObject { Id = "rule", ObjectKind = SlideObjectKind.Shape, Name = "Rule", Text = "", AnimationSteps = [Step("rule.in", AnimationTrigger.AfterPrevious)] };
        var design = new Slide { Id = "d", Name = "Lyrics", Objects = [placeholder, rule], AnimationOrder = ["in", "rule.in", "out"] };
        var build = new KineticBuild(KineticSplit.Lines, 0.25, [Step("tpl", AnimationTrigger.OnClick, animation: StepAnimation.Move, duration: 0.6)]);
        try
        {
            KineticText.BuildFor = p => p.Id == "ph" ? build : null;
            var lyric = Text("lyrics", "Amazing grace\nhow sweet the sound");
            var composed = PlaceholderAnimations.Compose([lyric], design, null);
            Assert.Equal(["lyrics::in::kinetic0", "lyrics::in::kinetic1", "rule.in", "lyrics::out"], composed.Order);
            Assert.Equal(3, composed.Objects[0].AnimationSteps!.Count);

            var stack = SlideSceneBuilder.ComposedStack(composed.Objects, design).Select(e => e.Object).ToList();
            var schedule = AnimationSequence.Schedule(stack, composed.Order);
            Assert.Equal(0.25, schedule["lyrics::in::kinetic1"].Start, 6);
            Assert.Equal(0.85, schedule["rule.in"].Start, 6); // the rule still follows the words, now the last line
        }
        finally
        {
            KineticText.BuildFor = null;
        }
        // Without the hook (as on the Mac) the design's whole-text In is donated as it was.
        var plain = PlaceholderAnimations.Compose([Text("lyrics", "a\nb")], design, null);
        Assert.Equal(["lyrics::in", "rule.in", "lyrics::out"], plain.Order);
    }
}
