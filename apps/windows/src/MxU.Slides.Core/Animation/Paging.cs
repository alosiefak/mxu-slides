using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Animation;

/// <summary>
/// Splits styled text into the pages that fit a frame of the given size
/// (TextRasterizer.pages(for:sceneFrame:) on the Mac). Supplied by the app's text
/// layout; one page (or none) means it fits.
/// </summary>
public delegate IReadOnlyList<string> TextPager(StyledText text, SceneSize frameSize);

/// <summary>
/// Text that overflows a "page on click" placeholder becomes click pages: the
/// object shows page 1, and a morph step per further page turns it, placed
/// before the design's leave. Port of SlideScene/Paging.swift; text measuring is
/// the injected <see cref="TextPager"/>.
/// </summary>
public static class Paging
{
    public const string StepMarker = "::page::";

    /// <summary>How a page turns.</summary>
    public sealed record Turn(StepAnimation Animation, double Duration, double? Amount = null)
    {
        public static readonly Turn Cut = new(StepAnimation.Fade, 0);
        public static readonly Turn Fade = new(StepAnimation.Fade, 0.35);

        /// <summary>Pages turn the way the slide transition looks.</summary>
        public static Turn Following(TransitionKind? kind, double duration) => kind switch
        {
            TransitionKind.Dissolve or TransitionKind.FadeBlack or TransitionKind.FadeWhite or TransitionKind.FadeColor => new(StepAnimation.Fade, duration),
            TransitionKind.BlurDissolve => new(StepAnimation.Blur, duration, 24),
            TransitionKind.FilmBurn => new(StepAnimation.Burn, duration, 1),
            _ => Cut,
        };
    }

    public sealed record PagedObjects(List<SlideObject> Objects, IReadOnlyList<string>? Order, int PageCount);

    public static PagedObjects Paged(
        IReadOnlyList<SlideObject> objects, Slide? template, Theme? theme, IReadOnlyList<string>? order,
        SceneSize canvas, TextPager? pager, Turn? turn = null)
    {
        turn ??= Turn.Cut;
        var pagesByIndex = PagesOf(objects, template, theme, canvas, pager);
        if (pagesByIndex.Count == 0) return new PagedObjects(objects.ToList(), order, 1);
        var result = objects.ToList();
        var count = pagesByIndex.Values.Max(p => p.Count);
        foreach (var (index, pages) in pagesByIndex)
        {
            result[index] = MxuJson.Clone(objects[index]);
            result[index].Text = pages[0];
        }
        var stepIds = new List<string>();
        for (var page = 2; page <= count; page++)
        {
            var leads = true;
            foreach (var index in pagesByIndex.Keys.Order())
            {
                var pages = pagesByIndex[index];
                if (page > pages.Count) continue;
                var target = MxuJson.Clone(objects[index]);
                target.Text = pages[page - 1];
                target.AnimationSteps = null;
                var id = $"{objects[index].Id}{StepMarker}{page}";
                var step = new AnimationStep
                {
                    Id = id, Kind = AnimationKind.Morph, Animation = turn.Animation,
                    Trigger = leads ? AnimationTrigger.OnClick : AnimationTrigger.WithPrevious,
                    DurationSeconds = turn.Duration, Ramp = AnimationRamp.Both, Amount = turn.Amount, ToObject = target,
                };
                result[index].AnimationSteps = [.. result[index].AnimationSteps ?? [], step];
                stepIds.Add(id);
                leads = false;
            }
        }
        return new PagedObjects(result, PlacingPages(order, result, template, stepIds), count);
    }

    private static Dictionary<int, IReadOnlyList<string>> PagesOf(
        IReadOnlyList<SlideObject> objects, Slide? template, Theme? theme, SceneSize canvas, TextPager? pager)
    {
        var result = new Dictionary<int, IReadOnlyList<string>>();
        if (template is null || pager is null) return result;
        var assignments = SlideSceneBuilder.PlaceholderAssignments(objects, template);
        for (var index = 0; index < objects.Count; index++)
        {
            var o = objects[index];
            if (o.ObjectKind != SlideObjectKind.Text || o.Hidden == true || o.Text.Length == 0
                || !assignments.TryGetValue(o.Id, out var placeholder) || placeholder.TextStyle?.PageOnClick != true
                || MovesOnItsOwn(o, placeholder))
                continue;
            var styled = SlideSceneBuilder.StyledTextFor(o, theme, placeholder);
            var frame = SlideSceneBuilder.Frame(o, placeholder, canvas);
            var pages = pager(styled, new SceneSize(frame.Width, frame.Height));
            if (pages.Count > 1) result[index] = pages;
        }
        return result;
    }

    /// <summary>
    /// Text on a path, a ticker or a block scroll shows its whole text its own way, so it never pages
    /// (TextRasterizer.pages returns such text as one page on the Mac).
    /// </summary>
    private static bool MovesOnItsOwn(SlideObject o, SlideObject placeholder)
    {
        var own = o.TextStyle;
        var design = placeholder.Id == o.Id ? null : placeholder.TextStyle;
        var path = own?.PathData ?? design?.PathData;
        var ticker = own?.TickerSpeed ?? design?.TickerSpeed ?? 0;
        var scroll = own?.Scroll ?? design?.Scroll;
        return path is not null || ticker != 0 || scroll is { Speed: > 0 };
    }

    /// <summary>The page turns go after the build-in and before the leave (the first dismiss step, or a clicked Out).</summary>
    private static IReadOnlyList<string>? PlacingPages(IReadOnlyList<string>? order, IReadOnlyList<SlideObject> objects, Slide? template, List<string> pageStepIds)
    {
        if (pageStepIds.Count == 0) return order;
        var stack = SlideSceneBuilder.ComposedStack(objects, template).Select(e => e.Object).ToList();
        var entries = AnimationSequence.OrderedEntries(stack, order).Where(e => !e.Step.Id.Contains(StepMarker)).ToList();
        var ids = entries.Select(e => e.Step.Id).ToList();
        var leave = entries.FindIndex(e => e.Step.Trigger == AnimationTrigger.OnDismiss
            || (e.Step.Kind == AnimationKind.Out && e.Step.Trigger == AnimationTrigger.OnClick));
        ids.InsertRange(leave >= 0 ? leave : ids.Count, pageStepIds);
        return ids;
    }

    /// <summary>The slide as one slide per page (page 1 keeps the id; later pages are <c>id::page::n</c>).</summary>
    public static List<Slide> Pages(Slide slide, Theme? theme, TextPager? pager, SceneSize? canvas = null) =>
        PagesOfRead(slide, theme, pager, canvas ?? SlideSceneBuilder.CanvasSize);

    /// <summary>The pages a slide turns through an override look.</summary>
    public static List<Slide> PagesThrough(Slide slide, Theme? look, Theme? originalTheme, TextPager? pager, SceneSize? canvas = null)
    {
        var read = look is null ? slide : PlaceholderAnimations.OverrideSlide(slide, originalTheme, look);
        return PagesOfRead(read, look, pager, canvas ?? SlideSceneBuilder.CanvasSize);
    }

    private static List<Slide> PagesOfRead(Slide slide, Theme? theme, TextPager? pager, SceneSize canvas)
    {
        var template = SlideSceneBuilder.ThemeSlide(slide, theme);
        var pagesByIndex = PagesOf(slide.Objects, template, theme, canvas, pager);
        if (pagesByIndex.Count == 0) return [slide];
        var count = pagesByIndex.Values.Max(p => p.Count);
        return Enumerable.Range(1, count).Select(page =>
        {
            var copy = MxuJson.Clone(slide);
            if (page > 1) copy.Id = $"{slide.Id}{StepMarker}{page}";
            foreach (var (index, pages) in pagesByIndex) copy.Objects[index].Text = pages[Math.Min(page, pages.Count) - 1];
            return copy;
        }).ToList();
    }

    public static int PageCount(Slide slide, Theme? theme, TextPager? pager, SceneSize? canvas = null) =>
        Pages(slide, theme, pager, canvas).Count;
}
