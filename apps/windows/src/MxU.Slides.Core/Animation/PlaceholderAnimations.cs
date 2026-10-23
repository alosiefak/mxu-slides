using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Animation;

/// <summary>
/// A theme placeholder's animation steps donated to the slide text it hosts (a
/// stream "side third" builds in even though the song's slides have no steps),
/// and the per-output compositions ShowState counts clicks across. Port of
/// SlideScene/PlaceholderAnimations.swift. Paging runs only when a
/// <see cref="TextPager"/> is supplied (the Mac's <c>paging: true</c>).
/// </summary>
public static class PlaceholderAnimations
{
    public sealed record Composition(List<SlideObject> Objects, IReadOnlyList<string>? Order);

    /// <summary>
    /// Text objects without steps take their placeholder's, namespaced
    /// <c>objectId::stepId</c> and range-free; the slide's own order wins, else the design's expands to the donated ids.
    /// </summary>
    public static Composition Compose(IReadOnlyList<SlideObject> objects, Slide? template, IReadOnlyList<string>? slideOrder)
    {
        if (template is null) return new Composition(objects.ToList(), slideOrder);
        var assignments = SlideSceneBuilder.PlaceholderAssignments(objects, template);
        var donatedIds = new Dictionary<string, List<string>>();
        var result = objects.ToList();
        for (var index = 0; index < objects.Count; index++)
        {
            var o = objects[index];
            if (o.ObjectKind != SlideObjectKind.Text || (o.AnimationSteps ?? []).Count > 0
                || !assignments.TryGetValue(o.Id, out var placeholder) || placeholder.AnimationSteps is not { Count: > 0 } steps)
                continue;
            var donated = MxuJson.Clone(o);
            donated.AnimationSteps = steps.Select(step =>
            {
                var copy = MxuJson.Clone(step);
                copy.Id = $"{o.Id}::{step.Id}";
                copy.Ranges = null;
                if (!donatedIds.TryGetValue(step.Id, out var ids)) donatedIds[step.Id] = ids = [];
                ids.Add(copy.Id);
                return copy;
            }).ToList();
            result[index] = donated;
        }
        if (slideOrder is not null) return new Composition(result, slideOrder);
        var order = template.AnimationOrder?.SelectMany(id => donatedIds.TryGetValue(id, out var ids) ? ids : [id]).ToList();
        return new Composition(result, order);
    }

    /// <summary>The slide's full stack through a theme (design decor included), donated and, with a pager, paged.</summary>
    public static Composition StackComposition(Slide slide, Theme? theme, TextPager? pager = null)
    {
        var template = SlideSceneBuilder.ThemeSlide(slide, theme);
        var donated = Compose(slide.Objects, template, slide.AnimationOrder);
        if (pager is null)
            return new Composition(SlideSceneBuilder.ComposedStack(donated.Objects, template).Select(e => e.Object).ToList(), donated.Order);
        var paged = Paging.Paged(donated.Objects, template, theme, donated.Order, SlideSceneBuilder.CanvasSize, pager);
        return new Composition(SlideSceneBuilder.ComposedStack(paged.Objects, template).Select(e => e.Object).ToList(), paged.Order);
    }

    public static Composition OverrideComposition(Slide slide, Theme? originalTheme, Theme overrideTheme, TextPager? pager = null) =>
        StackComposition(OverrideSlide(slide, originalTheme, overrideTheme), overrideTheme, pager);

    /// <summary>The slide as an override look renders it: its override design, and its text restyled by that look.</summary>
    public static Slide OverrideSlide(Slide slide, Theme? originalTheme, Theme overrideTheme)
    {
        var rendered = MxuJson.Clone(RenderedThroughOverride(slide, overrideTheme));
        rendered.Objects = OverrideObjects(rendered, rendered.Objects, originalTheme, overrideTheme);
        return rendered;
    }

    /// <summary>The slide with the design it chose for <paramref name="theme"/> (Slide.rendered(throughOverride:)).</summary>
    public static Slide RenderedThroughOverride(Slide slide, Theme theme)
    {
        if (slide.OverrideDesigns?.FirstOrDefault(d => d.ThemeId == theme.Id)?.ThemeSlideName is not { } design) return slide;
        var copy = MxuJson.Clone(slide);
        copy.ThemeSlideName = design;
        return copy;
    }

    /// <summary>One composition per output look: the slide's own theme first, then each override.</summary>
    public static List<Composition> Compositions(Slide slide, IReadOnlyList<Theme?> themes, TextPager? pager = null)
    {
        var own = themes.Count > 0 ? themes[0] : null;
        return themes
            .Select((theme, index) => index > 0 && theme is not null
                ? OverrideComposition(slide, own, theme, pager)
                : StackComposition(slide, theme, pager))
            .ToList();
    }

    public static int ClickCount(Slide slide, IReadOnlyList<Theme?> themes, TextPager? pager = null) =>
        Compositions(slide, themes, pager).Select(c => AnimationSequence.ClickCount(c.Objects, c.Order)).DefaultIfEmpty(0).Max();

    public static int AdvanceCount(Slide slide, IReadOnlyList<Theme?> themes, TextPager? pager = null)
    {
        var clicks = ClickCount(slide, themes, pager);
        var hasExit = Compositions(slide, themes, pager).Any(c => AnimationSequence.HasExitGroup(c.Objects, c.Order));
        return clicks + (hasExit ? 1 : 0);
    }

    public static bool HasAnimationSteps(Slide slide, IReadOnlyList<Theme?> themes, TextPager? pager = null) =>
        Compositions(slide, themes, pager).Any(c => AnimationSequence.HasAnimationSteps(c.Objects));

    // MARK: Theme override (port of ThemeOverride.overrideObjects)

    /// <summary>Bold/italic traits of a font name (Core Text on the Mac). Replace with real font data when available.</summary>
    public static Func<string, (bool Bold, bool Italic)> FontTraits { get; set; } = name => (
        name.Contains("Bold", StringComparison.OrdinalIgnoreCase) || name.Contains("Black", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Heavy", StringComparison.OrdinalIgnoreCase),
        name.Contains("Italic", StringComparison.OrdinalIgnoreCase) || name.Contains("Oblique", StringComparison.OrdinalIgnoreCase));

    /// <summary>The face of a font's family with the given traits (Core Text's trait copy on the Mac); null when unknown.</summary>
    public static Func<string, bool, bool, string?> FontNamed { get; set; } = (name, bold, italic) =>
    {
        var family = name.Split('-')[0];
        var suffix = (bold, italic) switch { (true, true) => "BoldItalic", (true, false) => "Bold", (false, true) => "Italic", _ => "" };
        return suffix.Length == 0 ? family : $"{family}-{suffix}";
    };

    private const string DefaultFontName = "HelveticaNeue-Bold";
    private const double DefaultFontSize = 96;

    private readonly record struct Keep(
        bool Bold, bool Italic, bool Underline, bool Strikethrough, bool SizeEmphasis, bool WordLetterSpacing, bool Highlight, bool TextColor)
    {
        public Keep(KeepFromSlideOptions? options) : this(
            options?.Bold ?? true, options?.Italic ?? true, options?.Underline ?? true, options?.Strikethrough ?? true,
            options?.SizeEmphasis ?? true, options?.WordLetterSpacing ?? true, options?.Highlight ?? false, options?.TextColor ?? false) { }
    }

    private readonly record struct BaseStyle(string FontName, double FontSize, double Tracking)
    {
        public static BaseStyle Of(SlideObject o, SlideObject? placeholder, Theme? theme)
        {
            var own = o.TextStyle;
            var b = placeholder?.Id == o.Id ? null : placeholder?.TextStyle;
            return new BaseStyle(
                own?.FontName ?? b?.FontName ?? theme?.FontFamily ?? DefaultFontName,
                own?.FontSize ?? b?.FontSize ?? theme?.FontSize ?? DefaultFontSize,
                own?.Tracking ?? b?.Tracking ?? 0);
        }
    }

    /// <summary>
    /// Slide text restyled by an override look: the look's placeholder takes over
    /// font, size and frame, keeping only the emphasis its keep-from-slide options allow.
    /// </summary>
    public static List<SlideObject> OverrideObjects(Slide slide, IReadOnlyList<SlideObject> objects, Theme? originalTheme, Theme overrideTheme)
    {
        var originalAssignments = SlideSceneBuilder.PlaceholderAssignments(objects, SlideSceneBuilder.ThemeSlide(slide, originalTheme));
        var overrideAssignments = SlideSceneBuilder.PlaceholderAssignments(objects, SlideSceneBuilder.ThemeSlide(slide, overrideTheme));
        return objects.Select(o =>
        {
            if (o.ObjectKind != SlideObjectKind.Text) return o;
            var overridePlaceholder = overrideAssignments.GetValueOrDefault(o.Id);
            var from = BaseStyle.Of(o, originalAssignments.GetValueOrDefault(o.Id), originalTheme);
            var result = MxuJson.Clone(o);
            result.TextStyle = null;
            var to = BaseStyle.Of(result, overridePlaceholder, overrideTheme);
            var keep = new Keep(overridePlaceholder?.KeepFromSlide);
            result.X = result.Y = result.Width = result.Height = null;
            var lineStyles = (o.TextStyle?.LineStyles ?? []).Select(l => CarriedLineStyle(l, from, to, keep)).OfType<LineStyleOverride>().ToList();
            if (lineStyles.Count > 0) result.TextStyle = new TextStyle { LineStyles = lineStyles };
            var runs = (o.StyleRuns ?? []).Select(r => CarriedRun(r, from, to, keep)).OfType<TextStyleRun>().ToList();
            result.StyleRuns = runs.Count > 0 ? runs : null;
            return result;
        }).ToList();
    }

    private static TextStyleRun? CarriedRun(TextStyleRun run, BaseStyle from, BaseStyle to, Keep keep)
    {
        var carried = new TextStyleRun { Line = run.Line, Column = run.Column, Length = run.Length };
        if (keep.Underline && run.Underline == true) carried.Underline = true;
        if (keep.Strikethrough && run.Strikethrough == true) carried.Strikethrough = true;
        if (keep.Highlight) carried.HighlightColorHex = run.HighlightColorHex;
        if (keep.TextColor) carried.ColorHex = run.ColorHex;
        carried.FontName = CarriedFontName(run.FontName, from, to, keep);
        if (keep.SizeEmphasis && run.FontSize is { } size && from.FontSize > 0 && Math.Abs(size / from.FontSize - 1) > 0.001)
            carried.FontSize = size / from.FontSize * to.FontSize;
        if (keep.WordLetterSpacing && run.Tracking is { } tracking)
        {
            var delta = tracking - from.Tracking;
            if (Math.Abs(delta) > 0.001 && from.FontSize > 0) carried.Tracking = to.Tracking + delta * (to.FontSize / from.FontSize);
        }
        var stylesAnything = carried.Underline is not null || carried.Strikethrough is not null || carried.FontName is not null
            || carried.FontSize is not null || carried.ColorHex is not null || carried.HighlightColorHex is not null || carried.Tracking is not null;
        return stylesAnything ? carried : null;
    }

    private static LineStyleOverride? CarriedLineStyle(LineStyleOverride line, BaseStyle from, BaseStyle to, Keep keep)
    {
        var carried = new LineStyleOverride { LineIndex = line.LineIndex, FontName = CarriedFontName(line.FontName, from, to, keep) };
        if (keep.SizeEmphasis && line.FontSize is { } size && from.FontSize > 0 && Math.Abs(size / from.FontSize - 1) > 0.001)
            carried.FontSize = size / from.FontSize * to.FontSize;
        if (keep.TextColor) carried.ColorHex = line.ColorHex;
        return carried.FontName is null && carried.FontSize is null && carried.ColorHex is null ? null : carried;
    }

    private static string? CarriedFontName(string? runFontName, BaseStyle from, BaseStyle to, Keep keep)
    {
        if (!(keep.Bold || keep.Italic) || string.IsNullOrEmpty(runFontName)) return null;
        var baseTraits = FontTraits(from.FontName);
        var run = FontTraits(runFontName);
        var addBold = keep.Bold && run.Bold && !baseTraits.Bold;
        var addItalic = keep.Italic && run.Italic && !baseTraits.Italic;
        if (!addBold && !addItalic) return null;
        var target = FontTraits(to.FontName);
        return FontNamed(to.FontName, target.Bold || addBold, target.Italic || addItalic);
    }
}
