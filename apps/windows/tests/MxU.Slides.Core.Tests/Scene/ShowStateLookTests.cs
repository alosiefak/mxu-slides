using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using static MxU.Slides.Core.Tests.Animation.AnimationFixtures;

namespace MxU.Slides.Core.Tests.Scene;

/// <summary>
/// Looks (theme overrides per output) on the live show: the ShowState cases of the Mac's
/// PlaceholderAnimationsTests.swift (clicks, clears, held looks, hand-offs and look switches)
/// and PerOutputLookTests.swift.
/// </summary>
public class ShowStateLookTests
{
    private static AnimationStep MakeStep(string id, AnimationTrigger trigger = AnimationTrigger.WithPrevious, AnimationKind kind = AnimationKind.In,
        double duration = 0.5) => Step(id, trigger, kind, StepAnimation.Move, duration: duration);

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

    private static Slide PlainSlide(string id = "s1") => new() { Id = id, Name = "Verse 1", Objects = [TextObject("lyrics")] };

    private static List<AnimatedItem> SlideLayer(AnimatedScene scene) => scene[LayerKind.Slide].Items;

    [Fact]
    public void OverrideOnlyClickBuildConsumesAdvancesAndArmsTheClock()
    {
        var show = new ShowState { SlideThemeOverrides = [SideThirdTheme([MakeStep("in", AnimationTrigger.OnClick)])] };
        show.Fire(PlainSlide(), atHostTime: 100);
        Assert.Equal(1, show.SlideClickCount);
        Assert.Equal(new AnimationContext(100), show.SlideAnimationContext);
        Assert.True(show.AdvanceStep(101));
        Assert.False(show.AdvanceStep(102)); // second advance = next slide

        show.SlideThemeOverrides = [];
        show.Fire(PlainSlide(), atHostTime: 200);
        Assert.Equal(0, show.SlideClickCount);
        Assert.Null(show.SlideAnimationContext);
    }

    [Fact]
    public void ClickOffPlaysTheThirdOutWhileTheSlideStaysLive()
    {
        Theme ClickOff() => SideThirdTheme([MakeStep("in"), MakeStep("out", AnimationTrigger.OnClick, AnimationKind.Out, duration: 0.4)]);
        var show = new ShowState { SlideThemeOverrides = [ClickOff()] };
        show.Fire(PlainSlide(), atHostTime: 10);
        Assert.Equal(1, show.SlideClickCount);
        Assert.True(show.AdvanceStep(15));
        Assert.NotNull(show.Live); // the click-off never removes the slide
        Assert.False(show.AdvanceStep(16)); // next advance = next slide
        show.Clear(LayerKind.Slide, 20);
        Assert.Null(show.Live); // clear after the click-off cuts: the leave already played

        show.SlideThemeOverrides = [ClickOff()];
        show.Fire(PlainSlide(), atHostTime: 30);
        show.Clear(LayerKind.Slide, 40);
        Assert.NotNull(show.Live);
        Assert.False(show.SweepFinishedExits(40.3));
        Assert.True(show.SweepFinishedExits(40.5));
        Assert.Null(show.Live);
    }

    [Fact]
    public void OverrideOnlyOutDefersTheClear()
    {
        var show = new ShowState
        {
            SlideThemeOverrides = [SideThirdTheme([MakeStep("in"), MakeStep("out", AnimationTrigger.OnDismiss, AnimationKind.Out, duration: 0.4)])],
        };
        show.Fire(PlainSlide(), atHostTime: 10);
        show.Clear(LayerKind.Slide, 20);
        Assert.NotNull(show.Live); // the stream-side Out plays before removal
        Assert.False(show.SweepFinishedExits(20.3));
        Assert.True(show.SweepFinishedExits(20.5));
        Assert.Null(show.Live);
    }

    [Fact]
    public void SceneDonatesOnlyThroughTheThemeThatOwnsTheBuilds()
    {
        var @override = SideThirdTheme([MakeStep("in")]);
        var show = new ShowState { SlideThemeOverrides = [@override] };
        show.Fire(PlainSlide(), atHostTime: 5);
        var baseItems = SlideLayer(show.AnimatedScene());
        Assert.Contains(baseItems, i => i.Id.StartsWith("lyrics", StringComparison.Ordinal));
        Assert.All(baseItems, i => Assert.Empty(i.AnimationSteps)); // the projector theme renders static
        var built = SlideLayer(show.AnimatedScene(slideThemeOverride: @override)).Where(i => i.AnimationSteps.Count > 0).ToList();
        Assert.Contains(built, i => i.Id.StartsWith("lyrics", StringComparison.Ordinal)); // the override output builds the side third
        Assert.All(built, i => Assert.Equal(["lyrics::in"], i.AnimationSteps.Select(s => s.Id)));
        Assert.All(built, i => Assert.Equal(new AnimationContext(5), i.AnimationContext)); // override-only steps still ride the shared clock
    }

    [Fact]
    public void UpcomingRevealReadsTheOverrideComposition()
    {
        var show = new ShowState { SlideThemeOverrides = [SideThirdTheme([MakeStep("in", AnimationTrigger.OnClick)])] };
        show.Fire(PlainSlide(), atHostTime: 1);
        Assert.Equal("Amazing grace", show.UpcomingRevealText());
        Assert.True(show.AdvanceStep(2));
        Assert.Null(show.UpcomingRevealText());
    }

    [Fact]
    public void ASameLookSlideHoldsTheStreamsBuild()
    {
        var @override = SideThirdTheme(
            [MakeStep("in"), MakeStep("out", AnimationTrigger.OnDismiss, AnimationKind.Out, duration: 0.4)],
            [MakeStep("plate-in")]);
        var show = new ShowState { SlideThemeOverrides = [@override] };
        show.Fire(PlainSlide(), atHostTime: 5);
        Assert.False(show.HoldsLook(@override)); // the first slide builds in
        var second = PlainSlide("s2");
        show.Fire(second, atHostTime: 9);
        Assert.True(show.HoldsLook(@override));
        var built = SlideLayer(show.AnimatedScene(slideThemeOverride: @override)).Where(i => i.AnimationSteps.Count > 0).ToList();
        Assert.Equal(2, built.Count); // the words and the plate
        var plate = built.First(i => !i.Id.StartsWith("lyrics", StringComparison.Ordinal));
        var words = built.First(i => i.Id.StartsWith("lyrics", StringComparison.Ordinal));
        Assert.True(plate.AnimationContext?.AutoSettled); // the plate stays put
        Assert.False(words.AnimationContext?.AutoSettled);
        Assert.Equal(9, words.AnimationContext?.AnchorHostTime);
        // The words fade in over the plate; the Out stays the design's.
        Assert.Equal(["Fade 0.3", "Move 0.4"], words.AnimationSteps.Select(s => $"{s.Animation} {s.Duration}"));
        Assert.All(SlideLayer(show.AnimatedScene()), i => Assert.NotEqual(true, i.AnimationContext?.AutoSettled)); // the projector restarts as before

        show.Fire(second, atHostTime: 10);
        Assert.False(show.HoldsLook(@override));
        show.Fire(PlainSlide(), atHostTime: 11);
        show.Fire(new Slide { Id = "blank", Name = "", Objects = [] }, atHostTime: 12);
        Assert.False(show.HoldsLook(@override));
        show.Fire(PlainSlide(), atHostTime: 13);
        show.Clear(LayerKind.Slide, 14);
        show.Fire(second, atHostTime: 14.1);
        Assert.False(show.HoldsLook(@override)); // a slide already leaving hands nothing over
    }

    [Fact]
    public void TheHandOffWaitsForTheOldLooksOutOnlyWhenTheLookChanges()
    {
        var side = SideThirdTheme([MakeStep("in"), MakeStep("out", AnimationTrigger.OnDismiss, AnimationKind.Out, duration: 0.4)]);
        var show = new ShowState { SlideThemeOverrides = [side] };
        var second = PlainSlide("s2");
        Assert.Equal(0, show.HandoffWait(second, [side])); // nothing live
        show.Fire(PlainSlide(), atHostTime: 5);
        Assert.Equal(0, show.HandoffWait(second, [side])); // the same look holds
        Assert.Equal(0.4, show.HandoffWait(second, [])); // the look leaves with the preset switch
        Assert.Equal(0.4, show.HandoffWait(new Slide { Id = "b", Name = "", Objects = [] }, [side])); // a blank takes the look down
        Assert.Equal(0, show.HandoffWait(PlainSlide(), [])); // a re-fire never waits

        var twoDesigns = SideThirdTheme([MakeStep("in"), MakeStep("out", AnimationTrigger.OnDismiss, AnimationKind.Out, duration: 0.4)]);
        twoDesigns.Slides!.Add(new Slide { Id = "side-2", Name = "Points", Objects = [TextObject("ph2", name: "Points")] });
        show.SlideThemeOverrides = [twoDesigns];
        var points = PlainSlide("s2");
        points.ThemeSlideName = "Points";
        Assert.Equal(0.4, show.HandoffWait(points, [twoDesigns]));
        Assert.Equal(0, show.HandoffWait(second, [twoDesigns]));
        show.Clear(LayerKind.Slide, 6);
        Assert.Equal(0, show.HandoffWait(second, [])); // already leaving
    }

    [Fact]
    public void TheExitPressBreaksTheHoldAndOnlyTheChangingLookGoesDown()
    {
        var side = SideThirdTheme([MakeStep("in"), MakeStep("out", AnimationTrigger.OnDismiss, AnimationKind.Out, duration: 0.4)]);
        var lower = SideThirdTheme([MakeStep("in"), MakeStep("out", AnimationTrigger.OnDismiss, AnimationKind.Out, duration: 0.2)]);
        lower.Id = "lower";
        var show = new ShowState { SlideThemeOverrides = [side, lower] };
        var second = PlainSlide("s2");
        show.Fire(PlainSlide(), atHostTime: 1);

        Assert.True(show.AdvanceStep(2));
        Assert.Equal(0, show.HandoffWait(second, [])); // the Out already played
        show.Fire(second, atHostTime: 3);
        Assert.False(show.HoldsLook(side));
        Assert.False(show.HoldsLook(lower));

        var change = show.Handoff(PlainSlide(), [lower]);
        Assert.Equal(0.4, change.Wait);
        Assert.Equal(["side"], change.Leaving.Select(t => t.Id));
        show.DismissLooks(change.Leaving, 5);
        AnimationContext? Context(Theme? theme) =>
            SlideLayer(show.AnimatedScene(slideThemeOverride: theme)).FirstOrDefault(i => i.AnimationSteps.Count > 0)?.AnimationContext;
        Assert.Equal(5, Context(side)?.DismissHostTime); // the side third plays its Out
        Assert.Null(Context(lower)?.DismissHostTime); // the lower third runs on
        Assert.Null(Context(null)?.DismissHostTime); // and so does the projector
        Assert.NotNull(show.Live);
        Assert.Null(show.SlideDismissAt);
        Assert.Equal(0, show.Handoff(PlainSlide(), [lower]).Wait); // taken down already
        show.Fire(PlainSlide(), atHostTime: 5.4);
        Assert.Empty(show.LookDismissAt);
        Assert.True(show.HoldsLook(lower));

        show.SlideThemeOverrides = [side, lower];
        var mid = show.LookSwitch([lower]);
        Assert.Equal(0.4, mid.Wait);
        Assert.Equal(["side"], mid.Leaving.Select(t => t.Id));
        Assert.Equal(0, show.LookSwitch([side, lower]).Wait);
        show.DismissLooks(mid.Leaving, 6);
        Assert.Equal(6, Context(side)?.DismissHostTime);
        Assert.Null(Context(lower)?.DismissHostTime);
    }

    [Fact]
    public void AClickedOffLookBuildsInAgainOnTheNextSlide()
    {
        var side = SideThirdTheme(
            [MakeStep("in"), MakeStep("out", AnimationTrigger.OnClick, AnimationKind.Out, duration: 0.4)],
            [MakeStep("plate-in"), MakeStep("plate-out", AnimationTrigger.WithPrevious, AnimationKind.Out, duration: 0.4)],
            ["plate-in", "in", "out", "plate-out"]);
        var show = new ShowState { SlideThemeOverrides = [side] };
        var second = PlainSlide("s2");
        show.Fire(PlainSlide(), atHostTime: 1);
        Assert.Equal((0, 1), show.SlideAnimationStep);

        show.Fire(second, atHostTime: 2);
        Assert.True(show.HoldsLook(side));

        show.Fire(PlainSlide(), atHostTime: 3);
        Assert.True(show.AdvanceStep(4));
        Assert.Null(show.SlideExitAt); // a click-off is a click, not the exit press
        Assert.Equal(0, show.HandoffWait(second, [side])); // nothing left to wait for
        show.Fire(second, atHostTime: 5);
        Assert.False(show.HoldsLook(side));
        var built = SlideLayer(show.AnimatedScene(slideThemeOverride: side)).Where(i => i.AnimationSteps.Count > 0).ToList();
        // The plate and the words build in from the fire.
        Assert.All(built, i => Assert.True(i.AnimationContext?.AutoSettled != true && i.AnimationContext?.AnchorHostTime == 5));
        var words = built.First(i => i.Id.StartsWith("lyrics", StringComparison.Ordinal));
        Assert.Equal(["Move", "Move"], words.AnimationSteps.Select(s => s.Animation.ToString())); // the design's In, not the hold's fade

        var points = PlainSlide("points");
        points.Objects = [TextObject("lyrics", steps: [
            MakeStep("p1-in"), MakeStep("p1-out", AnimationTrigger.OnClick, AnimationKind.Out),
            MakeStep("all-out", AnimationTrigger.OnClick, AnimationKind.Out, duration: 0.4)])];
        show.Fire(points, atHostTime: 6);
        Assert.True(show.AdvanceStep(7));
        show.Fire(second, atHostTime: 8);
        Assert.True(show.HoldsLook(side)); // one Out clicked, the leave still has the rest to play

        show.Fire(PlainSlide(), atHostTime: 9);
        show.DismissLooks([side], 10);
        show.Fire(second, atHostTime: 10.5);
        Assert.False(show.HoldsLook(side));
    }

    // PerOutputLookTests.swift

    private static Theme StreamTheme()
    {
        static Slide Design(string id, string name, double x, double width)
        {
            var placeholder = Text($"{id}-ph", "Sample", name: "Lyrics");
            placeholder.X = x;
            placeholder.Y = 200;
            placeholder.Width = width;
            placeholder.Height = 400;
            return new Slide { Id = id, Name = name, Objects = [placeholder] };
        }
        return new Theme
        {
            Id = "stream", Name = "Stream", FontFamily = "Arial", FontSize = 40,
            TextColorHex = "#FFFFFFFF", BackgroundColorHex = "#00000000",
            Slides = [Design("narrow", "Lyrics", 20, 500), Design("wide", "Lyrics Wide", 100, 1700)],
        };
    }

    [Fact]
    public void AnOverrideOutputRendersTheSlideThroughItsChosenLook()
    {
        var slide = new Slide { Id = "s1", Name = "", Objects = [Text("lyrics", "Amazing grace", name: "Lyrics")], ThemeSlideName = "Lyrics" };
        var theme = StreamTheme();
        static SceneRect? LyricsFrame(AnimatedScene scene) => scene[LayerKind.Slide].Items.FirstOrDefault(i => i.Id.StartsWith("lyrics", StringComparison.Ordinal))?.Render.Frame;

        var show = new ShowState { SlideThemeOverrides = [theme] };
        show.Fire(slide, atHostTime: 1);
        Assert.Equal(20, LyricsFrame(show.AnimatedScene(slideThemeOverride: theme))?.X); // no choice: the same-named design

        slide.OverrideDesigns = [new OverrideDesign { ThemeId = "stream", ThemeSlideName = "Lyrics Wide" }];
        show.Fire(slide, atHostTime: 2);
        Assert.Equal(100, LyricsFrame(show.AnimatedScene(slideThemeOverride: theme))?.X); // the chosen look on the override output
        Assert.Contains(show.AnimatedScene()[LayerKind.Slide].Items, i => i.Id.StartsWith("lyrics", StringComparison.Ordinal)); // the projector still renders the slide as its own
    }

    [Fact]
    public void LookIdentityIsTheThemeAndItsDesigns()
    {
        var whole = StreamTheme();
        var narrow = whole.ScopedToSlideId("narrow");
        Assert.True(ShowState.SameLook(whole, StreamTheme()));
        Assert.False(ShowState.SameLook(whole, narrow));
        Assert.Equal("stream|narrow,wide", ShowState.LookKey(whole));
        Assert.Equal("stream|narrow", ShowState.LookKey(narrow));
    }
}
