using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Scene;

/// <summary>
/// Turns a slide, its theme and its presentation into a <see cref="RenderScene"/>.
/// Port of SlideScene/SlideSceneBuilder.swift; animation, paging and masks are
/// not rendered yet, but the composition rules (theme placeholders, design
/// decor, backgrounds carried through arrangements) are the same.
/// </summary>
public static class SlideSceneBuilder
{
    public static readonly SceneSize CanvasSize = SceneSize.Hd;

    public static SceneSize CanvasSizeFor(Presentation? presentation) =>
        presentation is { CanvasWidth: > 0 and var w, CanvasHeight: > 0 and var h }
            ? new SceneSize(w, h)
            : CanvasSize;

    public static readonly (string Label, int Width, int Height)[] CanvasPresets =
    [
        ("1080p (1920×1080)", 1920, 1080),
        ("4K UHD (3840×2160)", 3840, 2160),
        ("720p (1280×720)", 1280, 720),
        ("4:3 (1024×768)", 1024, 768),
        ("4:3 (1440×1080)", 1440, 1080),
        ("16:10 (1920×1200)", 1920, 1200),
        ("Vertical (1080×1920)", 1080, 1920),
        ("Square (1080×1080)", 1080, 1080),
    ];

    public static SceneRect DefaultFrame(SlideObjectKind kind, SceneSize? canvas = null)
    {
        var c = canvas ?? CanvasSize;
        return kind == SlideObjectKind.Text
            ? new SceneRect(c.Width * 0.1, c.Height * 0.1, c.Width * 0.8, c.Height * 0.8)
            : new SceneRect(c);
    }

    // MARK: Arrangements

    public static List<Slide> ArrangedSlides(Presentation presentation, string? arrangementId = null, bool emptyRepeatsOnce = false)
    {
        if (string.IsNullOrEmpty(arrangementId)
            || presentation.Arrangements?.FirstOrDefault(a => a.Id == arrangementId) is not { SectionIds.Count: > 0 } arrangement)
            return presentation.Slides;
        return PlayedBlocks(arrangement.SectionIds, presentation, emptyRepeatsOnce).SelectMany(block => block).ToList();
    }

    public static List<List<Slide>> PlayedBlocks(IReadOnlyList<string> sectionIds, Presentation presentation, bool emptyRepeatsOnce = false)
    {
        string? previous = null;
        var blocks = new List<List<Slide>>();
        foreach (var sectionId in sectionIds)
        {
            var slides = presentation.Slides.Where(s => s.SectionId == sectionId).ToList();
            var repeatsEmpty = emptyRepeatsOnce && sectionId == previous && slides.All(ShowsNothing);
            previous = sectionId;
            blocks.Add(repeatsEmpty ? [] : slides);
        }
        return blocks;
    }

    public static bool ShowsNothing(Slide slide) =>
        slide.Background is null && slide.BackgroundFill is null
        && (slide.Actions ?? []).Count == 0 && slide.AutoAdvance is null
        && slide.Objects.All(o => o.ObjectKind == SlideObjectKind.Text && string.IsNullOrWhiteSpace(o.Text));

    public static List<int> ArrangementBlockStarts(Presentation presentation, string? arrangementId = null, bool emptyRepeatsOnce = false)
    {
        var starts = new List<int>();
        if (!string.IsNullOrEmpty(arrangementId)
            && presentation.Arrangements?.FirstOrDefault(a => a.Id == arrangementId) is { SectionIds.Count: > 0 } arrangement)
        {
            var index = 0;
            foreach (var block in PlayedBlocks(arrangement.SectionIds, presentation, emptyRepeatsOnce))
            {
                starts.Add(index);
                index += block.Count;
            }
            return starts;
        }
        var sections = presentation.Sections ?? [];
        string? lastId = null;
        for (var i = 0; i < presentation.Slides.Count; i++)
        {
            var slide = presentation.Slides[i];
            if (slide.SectionId != lastId && slide.SectionId is { } id && sections.Any(s => s.Id == id))
                starts.Add(i);
            lastId = slide.SectionId;
        }
        return starts;
    }

    // MARK: Backgrounds

    public enum BackgroundSourceKind { Slide, Section, Song }

    public readonly record struct BackgroundSource(CueMedia Media, BackgroundSourceKind Kind, string? DeclaredBy);

    public static BackgroundSource? BackgroundSourceFor(Slide slide, Presentation? presentation, string? arrangementId = null)
    {
        if (slide.Background is { MediaId.Length: > 0 } own) return new(own, BackgroundSourceKind.Slide, slide.Id);
        if (presentation is null) return null;
        var sequence = ArrangedSlides(presentation, arrangementId);
        var index = sequence.FindIndex(s => s.Id == slide.Id);
        if (index >= 0)
        {
            for (var i = index - 1; i >= 0; i--)
            {
                var prior = sequence[i];
                if (prior.Background is not { MediaId.Length: > 0 } declared) continue;
                if (LayerKindOf(declared.Layer) == LayerKind.Videos) continue;
                if (declared.Mode == CueMediaMode.UntilReplaced) return new(declared, BackgroundSourceKind.Slide, prior.Id);
                break;
            }
        }
        if (!string.IsNullOrEmpty(slide.SectionId)
            && presentation.Sections?.FirstOrDefault(s => s.Id == slide.SectionId) is { Background: { MediaId.Length: > 0 } sectionBackground })
            return new(sectionBackground, BackgroundSourceKind.Section, slide.SectionId);
        if (presentation.Background is { MediaId.Length: > 0 } song) return new(song, BackgroundSourceKind.Song, null);
        return null;
    }

    public static CueMedia? EffectiveBackground(Slide slide, Presentation? presentation, string? arrangementId = null) =>
        BackgroundSourceFor(slide, presentation, arrangementId)?.Media;

    public static LayerKind LayerKindOf(CueMediaLayer? layer) => layer switch
    {
        CueMediaLayer.StillGraphics => LayerKind.StillGraphics,
        CueMediaLayer.Videos => LayerKind.Videos,
        _ => LayerKind.LoopingVideos,
    };

    // MARK: Themes

    public static Slide? ThemeSlide(Slide slide, Theme? theme)
    {
        if (theme?.Slides is not { Count: > 0 } slides) return null;
        if (!string.IsNullOrEmpty(slide.ThemeSlideName)
            && slides.FirstOrDefault(s => string.Equals(s.Name, slide.ThemeSlideName, StringComparison.OrdinalIgnoreCase)) is { } match)
            return match;
        return slides[0];
    }

    public static SlideObject? Placeholder(Slide themeSlide) =>
        themeSlide.Objects.FirstOrDefault(o => o.ObjectKind == SlideObjectKind.Text);

    public static Dictionary<string, SlideObject> PlaceholderAssignments(IReadOnlyList<SlideObject> objects, Slide? template)
    {
        var assignments = new Dictionary<string, SlideObject>();
        if (template is null) return assignments;
        var placeholders = template.Objects.Where(o => o.ObjectKind == SlideObjectKind.Text).ToList();
        if (placeholders.Count == 0) return assignments;
        var textObjects = objects.Where(o => o.ObjectKind == SlideObjectKind.Text).ToList();
        var claimed = new HashSet<string>();
        foreach (var o in textObjects)
        {
            var match = placeholders.FirstOrDefault(p => string.Equals(p.Name, o.Name, StringComparison.OrdinalIgnoreCase));
            if (match is null) continue;
            assignments[o.Id] = match;
            claimed.Add(match.Id);
        }
        var remaining = new Queue<SlideObject>(placeholders.Where(p => !claimed.Contains(p.Id)));
        foreach (var o in textObjects.Where(o => !assignments.ContainsKey(o.Id)))
            assignments[o.Id] = remaining.Count == 0 ? placeholders[0] : remaining.Dequeue();
        return assignments;
    }

    public static bool HasThemeOverrides(Slide slide) =>
        slide.Objects.Any(o => o.ObjectKind == SlideObjectKind.Text
            && (o.TextStyle is not null || o.X is not null || o.Y is not null || o.Width is not null || o.Height is not null));

    public static void ResetThemeOverrides(Slide slide)
    {
        foreach (var o in slide.Objects.Where(o => o.ObjectKind == SlideObjectKind.Text))
        {
            o.TextStyle = null;
            o.X = o.Y = o.Width = o.Height = null;
        }
    }

    public readonly record struct ComposedEntry(SlideObject Object, bool FromTheme)
    {
        public string Id => FromTheme ? $"theme-{Object.Id}" : Object.Id;
    }

    public static List<ComposedEntry> ComposedStack(IReadOnlyList<SlideObject> objects, Slide? template)
    {
        if (template is null) return objects.Select(o => new ComposedEntry(o, false)).ToList();
        var placeholders = PlaceholderAssignments(objects, template);
        var stack = new List<ComposedEntry>();
        var bound = new HashSet<string>();
        foreach (var templateObject in template.Objects)
        {
            if (templateObject.ObjectKind == SlideObjectKind.Text)
            {
                var boundAny = false;
                foreach (var o in objects.Where(o => placeholders.TryGetValue(o.Id, out var p) && p.Id == templateObject.Id))
                {
                    bound.Add(o.Id);
                    boundAny = true;
                    stack.Add(new ComposedEntry(o, false));
                }
                if (!boundAny && templateObject.TextLink is not null) stack.Add(new ComposedEntry(templateObject, true));
            }
            else
            {
                stack.Add(new ComposedEntry(templateObject, true));
            }
        }
        stack.AddRange(objects.Where(o => !bound.Contains(o.Id)).Select(o => new ComposedEntry(o, false)));
        return stack;
    }

    // MARK: Scenes

    public static RenderScene Scene(
        Slide slide,
        Theme? theme,
        Presentation? presentation = null,
        string? arrangementId = null,
        bool showThemeDecor = true,
        SceneSize? canvasSize = null,
        IReadOnlyDictionary<LayerKind, string>? carriedMedia = null,
        Animation.TextPager? pager = null)
    {
        var canvas = canvasSize ?? CanvasSizeFor(presentation);
        var scene = new RenderScene(canvas);
        if (theme is not null && ColorHex.Color(theme.BackgroundColorHex) is { } themeBackground)
            scene.Background = themeBackground;

        var background = EffectiveBackground(slide, presentation, arrangementId);
        if (background is not null)
            scene.AddItem(MediaItem($"{slide.Id}-cue-background", background, canvas), LayerKindOf(background.Layer));

        if (carriedMedia is not null)
        {
            foreach (var layer in LayerKinds.All)
            {
                if (!carriedMedia.TryGetValue(layer, out var mediaId)) continue;
                if (background is not null && LayerKindOf(background.Layer) == layer && background.MediaId == mediaId) continue;
                scene.AddItem(new RenderItem
                {
                    Id = $"{slide.Id}-carried-{layer.Raw()}",
                    Frame = new SceneRect(canvas),
                    Content = new ItemContent.Media(mediaId, SceneMediaScaleMode.Fill),
                }, layer);
            }
        }

        if ((slide.BackgroundFill ?? presentation?.BackgroundFill) is { } fill
            && BackdropItem(fill, canvas, $"{slide.Id}-backdrop") is { } backdrop)
            scene.AddItem(backdrop, LayerKind.Slide);

        var template = ThemeSlide(slide, theme);
        IReadOnlyList<SlideObject> objects = slide.Objects;
        // With a pager (thumbnails), text that pages on click shows its first page, as the output first does.
        if (pager is not null)
        {
            var donated = Animation.PlaceholderAnimations.Compose(objects, template, slide.AnimationOrder);
            objects = Animation.Paging.Paged(donated.Objects, template, theme, donated.Order, canvas, pager).Objects;
        }
        AddSlideObjects(scene, objects, theme, template, canvas, showThemeDecor);
        return scene;
    }

    /// <summary>Adds a slide's own objects, composed with its theme design, to the slide layer.</summary>
    public static void AddSlideObjects(
        RenderScene scene, IReadOnlyList<SlideObject> objects, Theme? theme, Slide? template,
        SceneSize canvas, bool showThemeDecor = true, Func<SlideObject, SlideObject>? resolveTemplateObject = null)
    {
        var placeholders = PlaceholderAssignments(objects, template);
        var stack = ComposedStack(objects, template);
        var wiring = MaskWiring(stack.Select(e => e.Object).ToList());
        foreach (var entry in stack)
        {
            var masked = wiring.MaskedBy.GetValueOrDefault(entry.Object.Id);
            var matte = wiring.Mattes.Contains(entry.Object.Id) ? entry.Object.Id : null;
            if (entry.FromTheme)
            {
                if (!showThemeDecor) continue;
                var templateObject = resolveTemplateObject?.Invoke(entry.Object) ?? entry.Object;
                foreach (var item in RenderItems(templateObject, theme, null, canvas, entry.Id, masked, matte))
                    scene.AddItem(item, LayerKind.Slide);
            }
            else
            {
                foreach (var item in RenderItems(entry.Object, theme, placeholders.GetValueOrDefault(entry.Object.Id), canvas, null, masked, matte))
                    scene.AddItem(item, LayerKind.Slide);
            }
        }
    }

    /// <summary>Adds free-standing objects (an overlay, a stage layout) to a layer, with masks wired up.</summary>
    public static void AddObjects(RenderScene scene, IReadOnlyList<SlideObject> objects, LayerKind layer, SceneSize canvas, string idPrefix)
    {
        var wiring = MaskWiring(objects);
        foreach (var o in objects)
        {
            var masked = wiring.MaskedBy.GetValueOrDefault(o.Id);
            var matte = wiring.Mattes.Contains(o.Id) ? o.Id : null;
            foreach (var item in RenderItems(o, null, null, canvas, $"{idPrefix}{o.Id}", masked, matte))
                scene.AddItem(item, layer);
        }
    }

    public readonly record struct Mask(string Matte, bool Out);

    /// <summary>Which objects are clipped by which matte, and which objects are mattes. Port of maskWiring(for:).</summary>
    public static (Dictionary<string, Mask> MaskedBy, HashSet<string> Mattes) MaskWiring(IReadOnlyList<SlideObject> objects)
    {
        var ids = objects.Select(o => o.Id).ToHashSet();
        var maskedBy = new Dictionary<string, Mask>();
        var mattes = new HashSet<string>();
        foreach (var o in objects)
        {
            if (string.IsNullOrEmpty(o.MaskObjectId) || o.MaskObjectId == o.Id || !ids.Contains(o.MaskObjectId)) continue;
            maskedBy[o.Id] = new Mask(o.MaskObjectId, o.MaskMode == MaskMode.Out);
            mattes.Add(o.MaskObjectId);
        }
        return (maskedBy, mattes);
    }

    public static RenderItem MediaItem(string id, CueMedia media, SceneSize canvas) => new()
    {
        Id = id,
        Frame = new SceneRect(canvas),
        Content = new ItemContent.Media(media.MediaId, SceneMediaScaleMode.Fill),
        Loops = media.Loops ?? true,
    };

    public static RenderItem? BackdropItem(ObjectFill fill, SceneSize canvas, string baseId)
    {
        var o = new SlideObject
        {
            Id = baseId, ObjectKind = SlideObjectKind.Shape, Name = "Backdrop", Text = "",
            X = 0, Y = 0, Width = canvas.Width, Height = canvas.Height, Fill = fill,
        };
        return RenderItems(o, null, null, canvas).FirstOrDefault();
    }

    public static SceneRect Frame(SlideObject o, SlideObject? placeholder = null, SceneSize? canvas = null)
    {
        var c = canvas ?? CanvasSize;
        var fallback = o.ObjectKind == SlideObjectKind.Text && placeholder is not null && placeholder.Id != o.Id
            ? Frame(placeholder, null, c)
            : DefaultFrame(o.ObjectKind, c);
        return new SceneRect(o.X ?? fallback.X, o.Y ?? fallback.Y, o.Width ?? fallback.Width, o.Height ?? fallback.Height);
    }

    public static RenderItem RenderItemFor(SlideObject source, Theme? theme, SlideObject? placeholder = null, SceneSize? canvas = null)
    {
        var o = SlideObjectNormalization.Normalized(source);
        var c = canvas ?? CanvasSize;
        return new RenderItem
        {
            Id = o.Id,
            Frame = Frame(o, placeholder, c),
            Content = o.ObjectKind == SlideObjectKind.Text
                ? new ItemContent.Text(StyledTextFor(o, theme, placeholder))
                : new ItemContent.Shape(ShapeStyleFor(o)),
            RotationDegrees = o.RotationDegrees ?? 0,
            SkewX = o.SkewX ?? 0,
            SkewY = o.SkewY ?? 0,
            FlipHorizontal = o.FlipHorizontal ?? false,
            FlipVertical = o.FlipVertical ?? false,
            Opacity = o.Opacity ?? 1,
            BlendMode = o.BlendMode switch
            {
                Model.BlendMode.Multiply => SceneBlendMode.Multiply,
                Model.BlendMode.Screen => SceneBlendMode.Screen,
                Model.BlendMode.Add => SceneBlendMode.Add,
                _ => SceneBlendMode.Normal,
            },
            BlurRadius = (o.Effects ?? []).Where(e => e.EffectKind == EffectKind.Blur && (e.Enabled ?? true)).Sum(e => e.Radius ?? 0),
            Loops = o.Fill?.Loops ?? true,
        };
    }

    public static List<RenderItem> RenderItems(SlideObject source, Theme? theme, SlideObject? placeholder = null, SceneSize? canvas = null,
        string? baseId = null, Mask? maskedBy = null, string? matteGroup = null)
    {
        if (source.Hidden == true) return [];
        var o = SlideObjectNormalization.Normalized(source);
        var primary = RenderItemFor(o, theme, placeholder, canvas);
        var id = baseId ?? o.Id;
        primary = primary with { Id = id, MaskedBy = maskedBy?.Matte, MaskOut = maskedBy?.Out ?? false, MatteGroup = matteGroup };
        if (o.ObjectKind != SlideObjectKind.Shape || string.IsNullOrWhiteSpace(o.Text)) return [primary];

        // Text inside a shape renders as a second item over it.
        var text = StyledTextFor(o, theme, null);
        return
        [
            primary,
            primary with { Id = $"{id}::text", Content = new ItemContent.Text(text), BlurRadius = 0 },
        ];
    }

    /// <summary>Chord display (transposition and notation).</summary>
    public static Func<List<ChordPlacement>, string?, string?, ChordNotation, List<ChordPlacement>> DisplayChords { get; set; } =
        Lyrics.ChordMath.DisplayPlacements;

    public static StyledText StyledTextFor(SlideObject o, Theme? theme, SlideObject? placeholder = null, Presentation? music = null)
    {
        var own = o.TextStyle;
        var b = placeholder?.Id == o.Id ? null : placeholder?.TextStyle;
        T? R<T>(Func<TextStyle, T?> key) where T : class => (own is null ? null : key(own)) ?? (b is null ? null : key(b));
        T? V<T>(Func<TextStyle, T?> key) where T : struct => (own is null ? null : key(own)) ?? (b is null ? null : key(b));

        var showChords = V(s => s.ShowChords) ?? false;
        var chords = new List<ChordRun>();
        if (showChords && o.Chords is { Count: > 0 } placements && R(s => s.PathData) is null)
        {
            var display = o.TextLink is not null
                ? placements
                : DisplayChords(placements, music?.MusicKey, music?.DisplayKey, V(s => s.ChordNotation) ?? ChordNotation.Chords);
            chords = display.Select(c => new ChordRun(c.Line, c.Column, c.Symbol)).ToList();
        }

        return new StyledText
        {
            String = o.Text,
            FontName = R(s => s.FontName) ?? theme?.FontFamily ?? "HelveticaNeue-Bold",
            FontSize = V(s => s.FontSize) ?? theme?.FontSize ?? 96,
            Color = ColorHex.Color(R(s => s.ColorHex)) ?? (theme is null ? null : ColorHex.Color(theme.TextColorHex)) ?? SceneColor.White,
            Alignment = V(s => s.HorizontalAlignment) switch
            {
                TextHorizontalAlignment.Left => SceneTextAlignment.Left,
                TextHorizontalAlignment.Right => SceneTextAlignment.Right,
                _ => SceneTextAlignment.Center,
            },
            VerticalAlignment = V(s => s.VerticalAlignment) switch
            {
                TextVerticalAlignment.Top => SceneTextVerticalAlignment.Top,
                TextVerticalAlignment.Bottom => SceneTextVerticalAlignment.Bottom,
                _ => SceneTextVerticalAlignment.Middle,
            },
            Shadow = R(s => s.Shadow) is { } shadow ? SceneShadow(shadow) : null,
            Tracking = V(s => s.Tracking) ?? 0,
            LineHeightMultiple = V(s => s.LineHeightMultiple) ?? 1,
            Transform = V(s => s.TextTransform) == Model.TextTransform.Uppercase ? SceneTextTransform.Uppercase : SceneTextTransform.None,
            TabularFigures = V(s => s.TabularFigures) ?? false,
            AutoShrink = V(s => s.AutoShrink) ?? false,
            MinFontSize = V(s => s.MinFontSize) ?? StyledText.DefaultMinFontSize,
            KeepLinesWhole = V(s => s.KeepLinesWhole) ?? false,
            BalancedWrap = V(s => s.BalancedWrap) ?? false,
            Outline = R(s => s.Outline) is { } outline && ColorHex.Color(outline.ColorHex) is { } outlineColor
                ? new TextOutline(outlineColor, outline.Width)
                : null,
            LineOverrides = (own?.LineStyles ?? [])
                .Select(l => new TextLineOverride(l.LineIndex, l.FontName, l.FontSize, ColorHex.Color(l.ColorHex), l.Tracking))
                .ToList(),
            Fill = R(s => s.Fill) is { } fill ? TextFill(fill) : null,
            LineFill = R(s => s.LineFill) is { } lineFill ? LineFillStyle(lineFill) : null,
            Chords = chords,
            ChordColor = ColorHex.Color(R(s => s.ChordColorHex)),
            InsetTop = V(s => s.InsetTop) ?? 0,
            InsetLeft = V(s => s.InsetLeft) ?? 0,
            InsetBottom = V(s => s.InsetBottom) ?? 0,
            InsetRight = V(s => s.InsetRight) ?? 0,
            FirstLineIndent = V(s => s.FirstLineIndent) ?? 0,
            LeftIndent = V(s => s.LeftIndent) ?? 0,
            RightIndent = V(s => s.RightIndent) ?? 0,
            ParagraphSpacing = V(s => s.ParagraphSpacing) ?? 0,
            Underline = V(s => s.Underline) ?? false,
            Strikethrough = V(s => s.Strikethrough) ?? false,
            StyleRuns = (o.StyleRuns ?? [])
                .Select(r => new StyleRun(r.Line, r.Column, r.Length, r.Underline, r.Strikethrough, r.FontName, r.FontSize,
                    ColorHex.Color(r.ColorHex), ColorHex.Color(r.HighlightColorHex)))
                .ToList(),
        };
    }

    public static TextLineFillStyle? LineFillStyle(TextLineFill lineFill)
    {
        if (TextFill(lineFill.Fill) is not { } fill || fill is SceneFill.NoFill) return null;
        return new TextLineFillStyle(
            fill,
            lineFill.WidthMode switch
            {
                TextLineFillWidthMode.LineWidth => LineFillWidthMode.LineWidth,
                TextLineFillWidthMode.MaxLineWidth => LineFillWidthMode.MaxLineWidth,
                _ => LineFillWidthMode.FullWidth,
            },
            lineFill.VerticalPadding ?? 0,
            lineFill.HorizontalPadding ?? 0,
            lineFill.VerticalOffset ?? 0,
            lineFill.HorizontalOffset ?? 0,
            lineFill.CornerRadius ?? 0);
    }

    public static SceneFill? TextFill(ObjectFill fill) => fill.FillKind switch
    {
        FillKind.None => SceneFill.None,
        FillKind.Solid => ColorHex.Color(fill.ColorHex) is { } color ? new SceneFill.Solid(color) : null,
        FillKind.LinearGradient => Gradient(fill),
        _ => null,
    };

    private static SceneFill.LinearGradient? Gradient(ObjectFill fill)
    {
        var stops = (fill.GradientStops ?? [])
            .Select(s => ColorHex.Color(s.ColorHex) is { } c ? new SceneGradientStop(c, s.Position) : (SceneGradientStop?)null)
            .OfType<SceneGradientStop>()
            .ToList();
        return stops.Count >= 2 ? new SceneFill.LinearGradient(fill.GradientAngleDegrees ?? 0, stops) : null;
    }

    public static ShapeStyle ShapeStyleFor(SlideObject o)
    {
        SceneShapeKind kind = (o.ShapeKind ?? Model.ShapeKind.Rectangle) switch
        {
            Model.ShapeKind.RoundedRectangle => new SceneShapeKind.RoundedRectangle(o.CornerRadius ?? 0),
            Model.ShapeKind.Ellipse => new SceneShapeKind.Ellipse(),
            Model.ShapeKind.Path => new SceneShapeKind.Path(o.PathData ?? ""),
            _ => SceneShapeKind.Rect,
        };
        return new ShapeStyle(
            kind,
            Fill(o.Fill),
            o.Stroke is { } stroke && ColorHex.Color(stroke.ColorHex) is { } strokeColor
                ? new SceneStroke(strokeColor, stroke.Width, stroke.DashKind switch
                {
                    StrokeDashKind.Dashed => StrokeDash.Dashed,
                    StrokeDashKind.Dotted => StrokeDash.Dotted,
                    _ => StrokeDash.Solid,
                })
                : null,
            o.Shadow is { } shadow ? SceneShadow(shadow) : null);
    }

    public static SceneFill Fill(ObjectFill? fill)
    {
        if (fill is null) return new SceneFill.Solid(SceneColor.White);
        switch (fill.FillKind)
        {
            case FillKind.None:
                return SceneFill.None;
            case FillKind.Solid:
                return new SceneFill.Solid(ColorHex.Color(fill.ColorHex) ?? SceneColor.White);
            case FillKind.LinearGradient:
                return (SceneFill?)Gradient(fill) ?? new SceneFill.Solid(SceneColor.White);
            default:
                if (!string.IsNullOrEmpty(fill.ScreenSourceId) || !string.IsNullOrEmpty(fill.LiveInputId)
                    || !string.IsNullOrEmpty(fill.CaptureSourceId) || fill.CaptureSourceKind is not null)
                    return SceneFill.None; // Live inputs and screen mirrors arrive in a later phase.
                if (string.IsNullOrEmpty(fill.MediaId)) return SceneFill.None;
                return new SceneFill.Media(fill.MediaId, ScaleMode(fill.MediaScaleMode), SourceRect(fill.MediaSourceRect));
        }
    }

    public static SceneMediaScaleMode ScaleMode(MediaScaleMode? mode) => mode switch
    {
        MediaScaleMode.Fit => SceneMediaScaleMode.Fit,
        MediaScaleMode.Stretch => SceneMediaScaleMode.Stretch,
        _ => SceneMediaScaleMode.Fill,
    };

    private static SceneSourceRect? SourceRect(MediaSourceRect? rect)
    {
        if (rect is null || rect.Width <= 0 || rect.Height <= 0) return null;
        if (rect is { X: 0, Y: 0, Width: 1, Height: 1 }) return null;
        return new SceneSourceRect(rect.X, rect.Y, rect.Width, rect.Height);
    }

    private static TextShadow SceneShadow(ObjectShadow shadow) =>
        new(ColorHex.Color(shadow.ColorHex) ?? new SceneColor(0, 0, 0, 0.5), shadow.BlurRadius, shadow.OffsetX, shadow.OffsetY);
}
