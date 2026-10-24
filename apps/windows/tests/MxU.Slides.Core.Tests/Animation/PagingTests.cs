using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using static MxU.Slides.Core.Tests.Animation.AnimationFixtures;

namespace MxU.Slides.Core.Tests.Animation;

// Port of SlideSceneTests/PagingTests.swift. The Mac runs these against the Digital
// starter pack and Core Text; here a hand-built look with the same shape (a projector
// "Scripture" design, and a "Side Thirds" verse design whose verse placeholder pages on
// click and whose Out is a click-off) and a fake pager (a word budget per frame area).
public class PagingTests
{
    private static readonly string Verse = string.Join(" ", Enumerable.Range(0, 120).Select(i => new[] { "grace", "how", "sweet", "the", "sound", "that", "saved", "a", "wretch" }[i % 9]));

    /// <summary>Pages of whole words: one word per (4 × size²) of frame area.</summary>
    private static readonly TextPager Pager = (text, frame) =>
    {
        var budget = Math.Max(1, (int)(frame.Width * frame.Height / (text.FontSize * text.FontSize * 4)));
        return text.String.Split(' ').Chunk(budget).Select(words => string.Join(" ", words)).ToList();
    };

    private static Slide ScriptureSlide() => new()
    {
        Id = "s", Name = "Scripture", ThemeSlideName = "Scripture",
        Objects = [Text("ref", "Psalm 1:1-6", name: "Reference"), Text("verse", Verse, name: "Verse")],
    };

    private static SlideObject Placeholder(string id, string name, double x, double y, double w, double h, double size, bool page = false, List<AnimationStep>? steps = null)
    {
        var o = Text(id, $"Sample {name}", steps, name: name);
        o.X = x;
        o.Y = y;
        o.Width = w;
        o.Height = h;
        o.TextStyle = new TextStyle { FontSize = size, PageOnClick = page ? true : null };
        return o;
    }

    private static Theme Digital()
    {
        var scripture = new Slide
        {
            Id = "digital.scripture", Name = "Scripture",
            Objects = [Placeholder("digital.scripture.ref", "Reference", 160, 120, 1600, 100, 40), Placeholder("digital.scripture.verse", "Verse", 160, 240, 1600, 760, 40)],
        };
        var panel = new SlideObject
        {
            Id = "digital.verseSide.panel", ObjectKind = SlideObjectKind.Shape, Name = "Panel", Text = "",
            X = 1300, Y = 0, Width = 620, Height = 1080,
            AnimationSteps = [Step("panel-in", AnimationTrigger.WithPrevious, animation: StepAnimation.Move), Step("panel-out", AnimationTrigger.WithPrevious, AnimationKind.Out, StepAnimation.Move)],
        };
        var side = new Slide
        {
            Id = "digital.verseSide", Name = "Verse + Reference (Side Third)", Folder = "Side Thirds",
            Objects =
            [
                panel,
                Placeholder("digital.verseSide.verse", "Verse", 1340, 240, 520, 520, 40, page: true,
                    steps: [Step("verse-in", AnimationTrigger.WithPrevious), Step("verse-out", AnimationTrigger.OnClick, AnimationKind.Out, duration: 0.4)]),
                Placeholder("digital.verseSide.ref", "Reference", 1340, 796, 520, 50, 26,
                    steps: [Step("ref-in", AnimationTrigger.WithPrevious), Step("ref-out", AnimationTrigger.WithPrevious, AnimationKind.Out)]),
            ],
            AnimationOrder = ["panel-in", "verse-in", "ref-in", "verse-out", "ref-out", "panel-out"],
        };
        return new Theme
        {
            Id = "digital", Name = "Digital", FontFamily = "Helvetica", FontSize = 72,
            TextColorHex = "#FFFFFFFF", BackgroundColorHex = "#000000FF", Slides = [scripture, side],
        };
    }

    /// <summary><c>digital.scoped(toSlideFolder: "Side Thirds")</c>: the same theme id, just the side-third designs.</summary>
    private static Theme SideThirds()
    {
        var digital = Digital();
        digital.Slides = digital.Slides!.Where(s => s.Folder == "Side Thirds").ToList();
        return digital;
    }

    private static Slide WithSideThirdDesign(Slide slide)
    {
        slide.OverrideDesigns = [new OverrideDesign { ThemeId = "digital", ThemeSlideName = "Verse + Reference (Side Third)" }];
        return slide;
    }

    [Fact]
    public void APagingLookTurnsOverflowIntoClickPagesBeforeTheLeave()
    {
        var sideThirds = SideThirds();
        var r = PlaceholderAnimations.RenderedThroughOverride(WithSideThirdDesign(ScriptureSlide()), sideThirds);
        var pages = Paging.Pages(r, sideThirds, Pager);
        Assert.True(pages.Count > 1);
        Assert.True(pages[0].Objects[1].Text.Length < Verse.Length); // page 1 is the first cut
        Assert.All(pages, p => Assert.Equal("Psalm 1:1-6", p.Objects[0].Text)); // the reference fits, so it never pages
        var composition = PlaceholderAnimations.StackComposition(r, sideThirds, Pager);
        var groups = AnimationSequence.Groups(composition.Objects, composition.Order);
        Assert.Equal(pages.Count, groups.Click.Count); // pages minus one, plus the design's click-off Out
        foreach (var turn in groups.Click.SkipLast(1))
            Assert.True(turn[0].Step.Kind == AnimationKind.Morph && turn[0].Step.Id.Contains(Paging.StepMarker));
        Assert.Contains(groups.Click[^1], e => e.Step.Kind == AnimationKind.Out); // the Out is the last click
        var composed = composition.Objects.First(o => o.Id == "verse");
        Assert.Equal(pages[0].Objects[1].Text, composed.Text); // the composed object shows page 1
        Assert.Equal(pages[^1].Objects[1].Text, composed.AnimationSteps![^1].ToObject?.Text);
    }

    [Fact]
    public void TheProjectorsDesignHoldsTheWholeTextAndCountsNoPages()
    {
        var digital = Digital();
        var projector = PlaceholderAnimations.StackComposition(ScriptureSlide(), digital, Pager);
        Assert.Equal(Verse, projector.Objects.First(o => o.Id == "verse").Text);
        Assert.Equal(1, Paging.PageCount(ScriptureSlide(), digital, Pager));

        // What ShowState.slideClickCount sees with the side-third look as an override output.
        var s = WithSideThirdDesign(ScriptureSlide());
        var pages = Paging.PageCount(PlaceholderAnimations.RenderedThroughOverride(s, SideThirds()), SideThirds(), Pager);
        Assert.Equal(pages, PlaceholderAnimations.ClickCount(s, [null, SideThirds()], Pager)); // pages minus one, plus the Out

        var show = new ShowState { SlideThemeOverrides = [SideThirds()], Pager = Pager };
        show.Fire(s, theme: null, atHostTime: 1);
        Assert.Equal(pages, show.SlideClickCount); // pages minus one, plus the Out
    }

    [Fact]
    public void TheLiveLookTurnsItsPagesOnClick()
    {
        // End to end through ShowState: the look's scene shows page 1, a click turns to page 2 (the morph
        // step starts at the click), and the projector's own scene is untouched.
        var s = WithSideThirdDesign(ScriptureSlide());
        var look = SideThirds();
        var show = new ShowState { SlideThemeOverrides = [look], Pager = Pager, PageTurn = Paging.Turn.Fade };
        show.Fire(s, theme: Digital(), atHostTime: 1);
        var verse = show.AnimatedScene(slideThemeOverride: look)[LayerKind.Slide].Items.First(i => i.Id == "verse");
        var pageOne = Assert.IsType<ItemContent.Text>(verse.Render.Content).Styled.String;
        Assert.True(pageOne.Length < Verse.Length);
        var turn = verse.AnimationSteps.First(step => step.Id.Contains(Paging.StepMarker));
        Assert.Equal(SceneAnimationKind.Morph, turn.Kind);
        Assert.Equal(SceneStepAnimation.Fade, turn.Animation);
        Assert.Equal(0.35, turn.Duration, 3);
        Assert.Equal(SceneAnimationGroupKind.Click, turn.Group.Kind);
        Assert.True(show.AdvanceStep(2));
        Assert.Equal([2.0], show.SlideAnimationContext!.ClickHostTimes);
        Assert.Equal(Verse, Assert.IsType<ItemContent.Text>(show.AnimatedScene()[LayerKind.Slide].Items.First(i => i.Id == "verse").Render.Content).Styled.String);

        show.Pager = null; // no measurer: nothing pages, as before
        Assert.Equal(1, show.SlideClickCount);
    }

    [Fact]
    public void OwnFramedSlidesPageThroughTheLookNotTheirOwnBox()
    {
        var sideThirds = SideThirds();
        var own = ScriptureSlide();
        own.Objects[1].X = 172;
        own.Objects[1].Y = 150;
        own.Objects[1].Width = 1575;
        own.Objects[1].Height = 810;
        own.Objects[1].TextStyle = new TextStyle { FontName = "HelveticaNeue", FontSize = 42 };
        Assert.Single(Paging.Pages(own, sideThirds, Pager)); // raw: its own box holds it
        var pages = Paging.PagesThrough(own, sideThirds, Digital(), Pager);
        Assert.True(pages.Count > 1); // through the look: the column pages it
        Assert.All(pages, p => Assert.True(p.Objects[1].X is null && p.Objects[1].TextStyle is null)); // page slides render as the output does
        Assert.Equal(pages.Count, Paging.PagesThrough(own, sideThirds, null, Pager).Count); // a deck whose theme is missing still pages through the look

        var @short = MxuJson.Clone(own);
        @short.Objects[1].Text = "Blessed is the one";
        var leave = PlaceholderAnimations.ClickCount(@short, [null, sideThirds], Pager);
        Assert.Equal(leave + pages.Count - 1, PlaceholderAnimations.ClickCount(own, [null, sideThirds], Pager)); // the page turns, then the design's own leave
        Assert.Equal(leave, PlaceholderAnimations.ClickCount(own, [null, sideThirds])); // the body's cheap count skips the page measuring

        var show = new ShowState { SlideThemeOverrides = [sideThirds], Pager = Pager };
        show.Fire(own, theme: null, atHostTime: 1);
        Assert.Equal(leave + pages.Count - 1, show.SlideClickCount); // the advance bookkeeping sees the pages the output turns
    }

    [Fact]
    public void TextThatMovesOnItsOwnNeverPages()
    {
        // TextRasterizer.pages returns a ticker, a block scroll or text on a path whole.
        var sideThirds = SideThirds();
        var r = PlaceholderAnimations.RenderedThroughOverride(WithSideThirdDesign(ScriptureSlide()), sideThirds);
        r.Objects[1].TextStyle = new TextStyle { TickerSpeed = 60 };
        Assert.Single(Paging.Pages(r, sideThirds, Pager));
        r.Objects[1].TextStyle = new TextStyle { Scroll = new BlockScroll { Speed = 40 } };
        Assert.Single(Paging.Pages(r, sideThirds, Pager));
    }

    [Fact]
    public void ThumbnailScenesShowPageOne()
    {
        var sideThirds = SideThirds();
        var r = PlaceholderAnimations.RenderedThroughOverride(WithSideThirdDesign(ScriptureSlide()), sideThirds);
        string VerseText(RenderScene scene) => scene.AllItems.Select(i => i.Content).OfType<ItemContent.Text>().Select(t => t.Styled.String).First(t => t.StartsWith("grace", StringComparison.Ordinal));
        Assert.Equal(Verse, VerseText(SlideSceneBuilder.Scene(r, sideThirds)));
        Assert.Equal(Paging.Pages(r, sideThirds, Pager)[0].Objects[1].Text, VerseText(SlideSceneBuilder.Scene(r, sideThirds, pager: Pager)));
    }

    [Fact]
    public void ThumbnailsShowPageOneAndAFittingTextNeverPages()
    {
        var sideThirds = SideThirds();
        var r = PlaceholderAnimations.RenderedThroughOverride(WithSideThirdDesign(ScriptureSlide()), sideThirds);
        var scene = new AnimatedScene(SceneSize.Hd);
        foreach (var item in SlideItems(r, sideThirds, AnimationContext.Settled, Pager)) scene.AddItem(item, LayerKind.Slide);
        Assert.Contains(scene.AllItems, i => i.AnimationSteps.Any(s => s.Id.Contains(Paging.StepMarker)));
        var peak = AnimatedItems.PeakLook(scene);
        Assert.All(peak.AllItems, i => Assert.DoesNotContain(i.AnimationSteps, s => s.Id.Contains(Paging.StepMarker) || s.Kind == SceneAnimationKind.Exit));

        var @short = MxuJson.Clone(r);
        @short.Objects[1].Text = "Blessed is the one";
        Assert.Single(Paging.Pages(@short, sideThirds, Pager));
        var composition = PlaceholderAnimations.StackComposition(@short, sideThirds, Pager);
        Assert.Equal(1, AnimationSequence.ClickCount(composition.Objects, composition.Order)); // just the click-off Out
    }

    [Fact]
    public void PageTurnsFollowTheSlideTransition()
    {
        Assert.Equal(Paging.Turn.Cut, Paging.Turn.Following(null, 1));
        Assert.Equal(Paging.Turn.Cut, Paging.Turn.Following(TransitionKind.Cut, 1));
        Assert.Equal(new Paging.Turn(StepAnimation.Fade, 0.5), Paging.Turn.Following(TransitionKind.FadeBlack, 0.5));
        Assert.Equal(new Paging.Turn(StepAnimation.Blur, 0.5, 24), Paging.Turn.Following(TransitionKind.BlurDissolve, 0.5));
        Assert.Equal(new Paging.Turn(StepAnimation.Burn, 0.5, 1), Paging.Turn.Following(TransitionKind.FilmBurn, 0.5));
    }
}
