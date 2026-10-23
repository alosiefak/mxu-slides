using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Model;
using static MxU.Slides.Core.Tests.Animation.AnimationFixtures;

namespace MxU.Slides.Core.Tests.Animation;

// Port of SlideSceneTests/PlaceholderAnimationsTests.swift. The cases that drive ShowState
// (advance/clear/hand-off bookkeeping) keep only their composition assertions; see the report.
public class PlaceholderAnimationsTests
{
    private static AnimationStep MakeStep(string id, AnimationTrigger trigger = AnimationTrigger.WithPrevious, AnimationKind kind = AnimationKind.In,
        double duration = 0.5, List<AnimationRange>? ranges = null) =>
        Step(id, trigger, kind, StepAnimation.Move, duration: duration, ranges: ranges);

    private static SlideObject TextObject(string id, string name = "Lyrics", List<AnimationStep>? steps = null) => Text(id, "Amazing grace", steps, name: name);

    private static Theme SideThirdTheme(List<AnimationStep> placeholderBuilds, List<AnimationStep>? decorBuilds = null, List<string>? animationOrder = null)
    {
        var placeholder = TextObject("ph", steps: placeholderBuilds.Count == 0 ? null : placeholderBuilds);
        placeholder.Text = "Sample";
        placeholder.X = 20;
        placeholder.Y = 200;
        placeholder.Width = 560;
        placeholder.Height = 500;
        var plate = new SlideObject
        {
            Id = "plate", ObjectKind = SlideObjectKind.Shape, Name = "Plate", Text = "",
            Fill = new ObjectFill { FillKind = FillKind.Solid, ColorHex = "#112233FF" },
            AnimationSteps = decorBuilds is { Count: > 0 } ? decorBuilds : null,
        };
        return new Theme
        {
            Id = "side", Name = "Stream Side Third", FontFamily = "Arial", FontSize = 40,
            TextColorHex = "#FFFFFFFF", BackgroundColorHex = "#00000000",
            Slides = [new Slide { Id = "side-1", Name = "Lyrics", Objects = [plate, placeholder], AnimationOrder = animationOrder }],
        };
    }

    private static Slide PlainSlide() => new() { Id = "s1", Name = "Verse 1", Objects = [TextObject("lyrics")] };

    [Fact]
    public void PlaceholderBuildsDonateNamespacedAndRangeStripped()
    {
        var theme = SideThirdTheme([
            MakeStep("in", AnimationTrigger.WithPrevious, ranges: [Range(0, 0, 5)]),
            MakeStep("out", AnimationTrigger.OnDismiss, AnimationKind.Out),
        ]);
        var donated = PlaceholderAnimations.Compose([TextObject("lyrics")], theme.Slides![0], null);
        var steps = donated.Objects[0].AnimationSteps!;
        Assert.Equal(["lyrics::in", "lyrics::out"], steps.Select(s => s.Id));
        Assert.Null(steps[0].Ranges); // ranges anchor the placeholder's sample text — stripped
        Assert.Equal(AnimationTrigger.WithPrevious, steps[0].Trigger);
        Assert.Equal(AnimationKind.Out, steps[1].Kind);
        Assert.Equal("in", theme.Slides[0].Objects[1].AnimationSteps![0].Id); // the design's own steps are untouched
    }

    [Fact]
    public void OwnBuildsWinOverPlaceholder()
    {
        var theme = SideThirdTheme([MakeStep("theme-in", AnimationTrigger.OnClick)]);
        List<AnimationStep> own = [MakeStep("own-in", AnimationTrigger.WithPrevious)];
        var donated = PlaceholderAnimations.Compose([TextObject("lyrics", steps: own)], theme.Slides![0], null);
        Assert.Same(own, donated.Objects[0].AnimationSteps); // never a merge — own steps take everything
    }

    [Fact]
    public void TemplateOrderExpandsDonatedIdsAndSlideOrderWinsVerbatim()
    {
        var theme = SideThirdTheme([MakeStep("in", AnimationTrigger.OnClick)], [MakeStep("plate-in", AnimationTrigger.WithPrevious)], ["plate-in", "in"]);
        List<SlideObject> objects = [TextObject("a"), TextObject("b", name: "Other")];
        var donated = PlaceholderAnimations.Compose(objects, theme.Slides![0], null);
        Assert.Equal(["plate-in", "a::in", "b::in"], donated.Order!);
        Assert.Equal(["a::in", "b::in"], donated.Objects.Select(o => o.AnimationSteps?.FirstOrDefault()?.Id));

        Assert.Equal(["mine"], PlaceholderAnimations.Compose(objects, theme.Slides[0], ["mine"]).Order!);

        Assert.Equal(objects, PlaceholderAnimations.Compose(objects, null, null).Objects);
    }

    [Fact]
    public void OverrideOnlyClickBuildCountsAClick()
    {
        // overrideOnlyClickBuildConsumesAdvancesAndArmsTheClock: the click ShowState consumes comes from the override look.
        var side = SideThirdTheme([MakeStep("in", AnimationTrigger.OnClick)]);
        Assert.Equal(1, PlaceholderAnimations.ClickCount(PlainSlide(), [null, side]));
        Assert.True(PlaceholderAnimations.HasAnimationSteps(PlainSlide(), [null, side]));
        Assert.Equal(0, PlaceholderAnimations.ClickCount(PlainSlide(), [null]));
        Assert.False(PlaceholderAnimations.HasAnimationSteps(PlainSlide(), [null]));
    }

    [Fact]
    public void OverrideOnlyOutHasALeaveToPlay()
    {
        // overrideOnlyOutDefersTheClear / clickOffPlaysTheThirdOutWhileTheSlideStaysLive: the leave the clear waits for.
        var dismiss = PlaceholderAnimations.OverrideComposition(PlainSlide(), null,
            SideThirdTheme([MakeStep("in"), MakeStep("out", AnimationTrigger.OnDismiss, AnimationKind.Out, duration: 0.4)]));
        Assert.Equal(0.4, AnimationSequence.ExitDuration(dismiss.Objects, dismiss.Order));

        var clickOff = PlaceholderAnimations.OverrideComposition(PlainSlide(), null,
            SideThirdTheme([MakeStep("in"), MakeStep("out", AnimationTrigger.OnClick, AnimationKind.Out, duration: 0.4)]));
        Assert.Equal(1, AnimationSequence.ClickCount(clickOff.Objects, clickOff.Order));
        Assert.Equal(0.4, AnimationSequence.ExitDuration(clickOff.Objects, clickOff.Order, consumedClicks: 0)); // not clicked: plays at the clear
        Assert.Equal(0, AnimationSequence.ExitDuration(clickOff.Objects, clickOff.Order, consumedClicks: 1)); // clicked off: the clear cuts
    }

    [Fact]
    public void SceneDonatesOnlyThroughTheThemeThatOwnsTheBuilds()
    {
        var @override = SideThirdTheme([MakeStep("in")]);
        var context = new AnimationContext(5);
        var baseItems = SlideItems(PlainSlide(), null, context);
        Assert.Contains(baseItems, i => i.Id.StartsWith("lyrics"));
        Assert.All(baseItems, i => Assert.Empty(i.AnimationSteps)); // the projector theme renders static

        var variant = SlideItems(PlaceholderAnimations.OverrideSlide(PlainSlide(), null, @override), @override, context);
        var built = variant.Where(i => i.AnimationSteps.Count > 0).ToList();
        Assert.Contains(built, i => i.Id.StartsWith("lyrics")); // the override output builds the side third
        Assert.All(built, i => Assert.Equal(["lyrics::in"], i.AnimationSteps.Select(s => s.Id)));
        Assert.All(built, i => Assert.Equal(context, i.AnimationContext)); // override-only steps still ride the shared clock
    }

    [Fact]
    public void ChipClickCountPredictsAcrossThemes()
    {
        var side = SideThirdTheme([MakeStep("in", AnimationTrigger.OnClick)]);
        var bare = PlainSlide();
        Assert.Equal(0, PlaceholderAnimations.ClickCount(bare, [null]));
        Assert.Equal(1, PlaceholderAnimations.ClickCount(bare, [null, side]));

        var own = new Slide { Id = "s2", Name = "Verse 2", Objects = [TextObject("t", steps: [MakeStep("a", AnimationTrigger.OnClick), MakeStep("b", AnimationTrigger.OnClick)])] };
        Assert.Equal(2, PlaceholderAnimations.ClickCount(own, [null, side]));

        var auto = SideThirdTheme([MakeStep("in", AnimationTrigger.WithPrevious)]);
        Assert.Equal(0, PlaceholderAnimations.ClickCount(bare, [null, auto]));
        Assert.True(PlaceholderAnimations.HasAnimationSteps(bare, [null, auto]));
        Assert.False(PlaceholderAnimations.HasAnimationSteps(bare, [null]));
    }

    [Fact]
    public void UpcomingRevealReadsTheOverrideComposition()
    {
        var composition = PlaceholderAnimations.OverrideComposition(PlainSlide(), null, SideThirdTheme([MakeStep("in", AnimationTrigger.OnClick)]));
        Assert.Equal("Amazing grace", AnimationSequence.UpcomingRevealText(composition.Objects, composition.Order, 0));
        Assert.Null(AnimationSequence.UpcomingRevealText(composition.Objects, composition.Order, 1));
    }

    [Fact]
    public void OverrideObjectsTakeTheLooksStyleKeepingEmphasis()
    {
        // ThemeOverride.overrideObjects, which the override compositions run through.
        var lyrics = TextObject("lyrics");
        lyrics.X = 100;
        lyrics.TextStyle = new TextStyle { FontSize = 48, LineStyles = [new LineStyleOverride { LineIndex = 0, FontSize = 96 }] };
        lyrics.StyleRuns = [new TextStyleRun { Line = 0, Column = 0, Length = 7, Underline = true, FontSize = 48 }];
        var side = SideThirdTheme([]);
        var result = PlaceholderAnimations.OverrideObjects(PlainSlide(), [lyrics], null, side)[0];
        Assert.Null(result.X); // the look's placeholder frame takes over
        Assert.Equal(80, result.TextStyle?.LineStyles?[0].FontSize); // double-size emphasis carried at the look's size (40)
        Assert.Null(result.TextStyle?.FontSize);
        var run = Assert.Single(result.StyleRuns!);
        Assert.True(run.Underline);
        Assert.Null(run.FontSize); // the run matched its base size: no emphasis to carry
        Assert.Equal(100, lyrics.X); // the slide's own object is untouched
    }
}
