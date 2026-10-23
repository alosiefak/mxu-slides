using System.Globalization;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.PptxImport;

internal sealed record MediaWant(string PlaceholderId, string AbsolutePath);

internal sealed record PptxMappedDocument(
    Presentation Presentation, List<MediaWant> MediaWants, List<string> Warnings,
    List<string> MissingFonts, List<TextureBake> Bakes);

internal abstract record TextureBakeKind
{
    public sealed record Texture(string SourcePath, double Alpha, double ScaleX, double ScaleY, bool Tiled) : TextureBakeKind;

    public sealed record Pattern(string Preset, string ForegroundHex, string BackgroundHex) : TextureBakeKind;
}

internal sealed record TextureBake(string PlaceholderId, TextureBakeKind Kind, double K);

/// <summary>
/// Maps a parsed deck to an MxU <see cref="Presentation"/>: EMU become scene points
/// on a 1080-high canvas, fills, strokes, text styles and runs become their MxU
/// forms, and media is referenced through pptx-media:// placeholders until the
/// importer swaps in library ids.
/// </summary>
internal static class PptxDocumentMapper
{
    public const string PlaceholderPrefix = "pptx-media://";

    private sealed class MapState(Func<string, bool> isFontInstalled)
    {
        public List<MediaWant> Wants { get; } = [];
        public List<TextureBake> Bakes { get; } = [];
        public List<string> Warnings { get; } = [];
        public List<string> MissingFonts { get; } = [];
        public Func<string, bool> IsFontInstalled { get; } = isFontInstalled;
        public Dictionary<string, string> FontNameCache { get; } = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> wantByPath = new(StringComparer.Ordinal);
        private int nextWantIndex = 1;

        public string Bake(Func<string, TextureBake> bake)
        {
            var placeholder = PlaceholderPrefix + nextWantIndex++;
            Bakes.Add(bake(placeholder));
            return placeholder;
        }

        public void Warn(string message) => Warnings.AppendUnique(message);

        public string Want(string path)
        {
            if (wantByPath.TryGetValue(path, out var existing)) return existing;
            var placeholder = PlaceholderPrefix + nextWantIndex++;
            Wants.Add(new MediaWant(placeholder, path));
            wantByPath[path] = placeholder;
            return placeholder;
        }
    }

    public static PptxMappedDocument Map(PptxDeck deck, string fallbackName, string sourceId, Func<string, bool>? isFontInstalled = null)
    {
        var state = new MapState(isFontInstalled ?? FontActivator.IsInstalled);
        foreach (var warning in deck.Warnings) state.Warn(warning);

        var heightEmu = deck.SlideHeightEmu > 0 ? deck.SlideHeightEmu : 6_858_000;
        var k = 1080 / (heightEmu / 12700);
        double Scene(double emu) => emu * 1080 / heightEmu;
        var canvasWidth = (int)Math.Round(Scene(deck.SlideWidthEmu), MidpointRounding.AwayFromZero);
        const int canvasHeight = 1080;

        var slides = deck.Slides.Select(slide => MapSlide(slide, Scene, k, canvasWidth, canvasHeight, state)).ToList();
        if (slides.Count == 0) slides = [new Slide { Id = LibraryStore.NewId(), Name = "", Objects = [] }];

        var sections = new List<PresentationSection>();
        foreach (var section in deck.Sections)
        {
            var valid = section.SlideIndexes.Where(index => index >= 0 && index < slides.Count).ToList();
            if (valid.Count == 0) continue;
            sections.Add(new PresentationSection { Id = section.Id, Name = section.Name });
            foreach (var index in valid) slides[index].SectionId = section.Id;
        }

        ObjectFill? presentationBackgroundFill = null;
        if (slides[0].BackgroundFill is { } first && slides.All(s => MxuJson.StructurallyEqual(s.BackgroundFill, first)))
        {
            presentationBackgroundFill = first;
            foreach (var slide in slides) slide.BackgroundFill = null;
        }

        var standard = canvasWidth == 1920 && canvasHeight == 1080;
        var presentation = new Presentation
        {
            Id = sourceId,
            Name = fallbackName,
            PresentationKind = PresentationKind.Deck,
            ThemeId = "",
            Slides = slides,
            CanvasWidth = standard ? null : canvasWidth,
            CanvasHeight = standard ? null : canvasHeight,
            BackgroundFill = presentationBackgroundFill,
            Sections = sections.Count == 0 ? null : sections,
        };
        return new PptxMappedDocument(presentation, state.Wants, state.Warnings, state.MissingFonts, state.Bakes);
    }

    /// <summary>A copy with placeholder media ids swapped for library ids; anything left unresolved is dropped.</summary>
    public static Presentation ReplacingMediaIds(Presentation presentation, IReadOnlyDictionary<string, string> map)
    {
        var result = MxuJson.Clone(presentation);

        string? Remap(string? id) =>
            id is null || !id.StartsWith(PlaceholderPrefix, StringComparison.Ordinal) ? id : map.GetValueOrDefault(id);
        CueMedia? RemapCue(CueMedia? media)
        {
            if (media is null || Remap(media.MediaId) is not { } mapped) return null;
            media.MediaId = mapped;
            return media;
        }
        ObjectFill? RemapFill(ObjectFill? fill)
        {
            if (fill is null) return null;
            if (fill.MediaId is not null && Remap(fill.MediaId) is null) return null;
            fill.MediaId = Remap(fill.MediaId);
            return fill;
        }
        bool Unresolved(string? id) => id is not null && id.StartsWith(PlaceholderPrefix, StringComparison.Ordinal) && !map.ContainsKey(id);

        foreach (var slide in result.Slides)
        {
            slide.Background = RemapCue(slide.Background);
            if (slide.Actions is { } actions)
            {
                actions.RemoveAll(action => Unresolved(action.AudioItemId) || Unresolved(action.MediaId));
                foreach (var action in actions)
                {
                    action.AudioItemId = Remap(action.AudioItemId);
                    action.MediaId = Remap(action.MediaId);
                }
                if (actions.Count == 0) slide.Actions = null;
            }
            slide.Objects.RemoveAll(o =>
            {
                var fillLostItsMedia = o.Fill?.FillKind == FillKind.Media && Unresolved(o.Fill.MediaId);
                o.Fill = RemapFill(o.Fill);
                return fillLostItsMedia && o.Stroke is null && string.IsNullOrWhiteSpace(o.Text);
            });
        }
        return result;
    }

    private static Slide MapSlide(
        PptxSlide slide, Func<double, double> scene, double k, double canvasWidth, double canvasHeight, MapState state)
    {
        var objects = new List<SlideObject>();
        ObjectFill? backgroundFill = null;

        SlideObject BackdropObject(string mediaId) => new()
        {
            Id = LibraryStore.NewId(),
            ObjectKind = SlideObjectKind.Shape,
            Name = "Background",
            Text = "",
            X = 0, Y = 0, Width = canvasWidth, Height = canvasHeight,
            Fill = new ObjectFill { FillKind = FillKind.Media, MediaId = mediaId, MediaScaleMode = MediaScaleMode.Stretch },
        };
        switch (slide.BackgroundFill)
        {
            case PptxFill.Solid or PptxFill.Gradient:
                backgroundFill = ObjectFillFrom(slide.BackgroundFill, state);
                break;
            case PptxFill.Blip blip:
                objects.Add(BackdropObject(state.Want(blip.Path)));
                break;
            case PptxFill.BlipTiled tiled:
                objects.Add(BackdropObject(state.Bake(id => new TextureBake(
                    id, new TextureBakeKind.Texture(tiled.Path, tiled.Alpha, tiled.ScaleX, tiled.ScaleY, tiled.Tiled), k))));
                break;
            case PptxFill.Pattern pattern:
                if (TextureBaker.StructuralPatterns.Contains(pattern.Preset))
                {
                    objects.Add(BackdropObject(state.Bake(id => new TextureBake(
                        id, new TextureBakeKind.Pattern(pattern.Preset, pattern.ForegroundHex, pattern.BackgroundHex), k))));
                }
                else
                {
                    state.Warn("pattern fills are approximated as a blend of their two colors");
                    backgroundFill = new ObjectFill
                    {
                        FillKind = FillKind.Solid,
                        ColorHex = Rgba(PatternBlend(pattern.Preset, pattern.ForegroundHex, pattern.BackgroundHex)),
                    };
                }
                break;
        }

        var actions = new List<SlideAction>();
        var objectIdsByShape = new Dictionary<int, string>();
        foreach (var shape in slide.Shapes)
        {
            if (shape.Kind == PptxShapeKind.Picture && shape.MediaKind == PptxMediaKind.Audio)
            {
                if (shape.MediaPath is not { } path) continue;
                state.Warn("slide audio imported as a play action (the speaker icon is not drawn)");
                actions.Add(new SlideAction { Id = LibraryStore.NewId(), Kind = SlideActionKind.FireAudio, AudioItemId = state.Want(path) });
                continue;
            }
            if (MapShape(shape, scene, k, state) is { } mapped)
            {
                objects.Add(mapped);
                if (shape.ShapeId is { } shapeId) objectIdsByShape[shapeId] = mapped.Id;
            }
        }

        var animationSteps = PptxAnimationMapper.Apply(slide.AnimationSteps, objectIdsByShape, objects);

        AnimationImportSupport.RigidifyMoves(objects, canvasWidth, canvasHeight);
        var notes = slide.Notes;
        foreach (var warning in animationSteps.Warnings)
        {
            state.Warn(warning);
            notes = (notes is null ? "" : notes + "\n") + "PowerPoint " + warning;
        }

        Transition? transition = null;
        AutoAdvance? autoAdvance = null;
        if (slide.Transition is { } pptxTransition)
        {
            if (pptxTransition.Kind is { } kind)
            {
                if (kind == "fade")
                {
                    transition = new Transition
                    {
                        TransitionKind = pptxTransition.ThroughBlack ? TransitionKind.FadeBlack : TransitionKind.Dissolve,
                        DurationSeconds = pptxTransition.DurationSeconds,
                    };
                }
                else
                {
                    state.Warn($"transition \"{kind}\" has no MxU equivalent — shown as a dissolve");
                    transition = new Transition { TransitionKind = TransitionKind.Dissolve, DurationSeconds = pptxTransition.DurationSeconds };
                }
            }
            if (pptxTransition.AdvanceAfterMs is { } ms) autoAdvance = new AutoAdvance { DelaySeconds = Math.Max(ms / 1000, 0) };
        }

        return new Slide
        {
            Id = LibraryStore.NewId(),
            Name = slide.Name ?? "",
            Objects = objects,
            BackgroundFill = backgroundFill,
            Actions = actions.Count == 0 ? null : actions,
            Notes = notes,
            AutoAdvance = autoAdvance,
            Transition = transition,
            AnimationOrder = animationSteps.AnimationOrder,
        };
    }

    private static SlideObject? MapShape(PptxShape shape, Func<double, double> scene, double k, MapState state)
    {
        var transform = shape.Transform;
        var frame = (X: scene(transform.OffXEmu), Y: scene(transform.OffYEmu), W: scene(transform.ExtXEmu), H: scene(transform.ExtYEmu));

        var rotation = transform.Rotation60k / 60000 % 360;
        double? rotationDegrees = rotation == 0 ? null : rotation;
        bool? flipHorizontal = transform.FlipH ? true : null;
        bool? flipVertical = transform.FlipV ? true : null;

        ObjectStroke? Stroke() => shape.Stroke is { } stroke
            ? new ObjectStroke { ColorHex = Rgba(stroke.ColorHex), Width = stroke.WidthEmu / 12700 * k, DashKind = stroke.Dash }
            : null;

        if (shape.Kind == PptxShapeKind.Picture)
        {
            if (shape.MediaPath is not { } path) return null;

            var geometry = ResolvedGeometry(shape, frame.W, frame.H, state);
            return new SlideObject
            {
                Id = LibraryStore.NewId(),
                ObjectKind = SlideObjectKind.Shape,
                Name = shape.Name ?? "Picture",
                Text = "",
                X = frame.X, Y = frame.Y, Width = frame.W, Height = frame.H,
                RotationDegrees = rotationDegrees,
                FlipHorizontal = flipHorizontal,
                FlipVertical = flipVertical,
                Opacity = shape.BlipAlpha,
                ShapeKind = geometry.Kind == ShapeKind.Rectangle ? null : geometry.Kind,
                CornerRadius = geometry.CornerRadius,
                PathData = geometry.PathData,
                Fill = new ObjectFill
                {
                    FillKind = FillKind.Media,
                    MediaId = state.Want(path),
                    MediaScaleMode = MediaScaleMode.Stretch,
                    MediaSourceRect = MediaSourceRectFrom(shape.SourceRect),
                },
                Stroke = Stroke(),
            };
        }

        var text = shape.TextBody?.PlainText ?? "";
        var fill = ObjectFillFrom(shape.Fill, state);
        var fillIsInk = fill is not null && fill.FillKind != FillKind.None;
        var objectStroke = Stroke();
        TextStyle? textStyle = null;
        List<TextStyleRun> styleRuns = [];
        if (text.Length > 0 && shape.TextBody is { } body)
            (textStyle, styleRuns) = TextStyleFor(body, k, state);

        if (text.Length > 0 && !fillIsInk && objectStroke is null)
        {
            return new SlideObject
            {
                Id = LibraryStore.NewId(),
                ObjectKind = SlideObjectKind.Text,
                Name = shape.Name ?? "Text",
                Text = text,
                X = frame.X, Y = frame.Y, Width = frame.W, Height = frame.H,
                RotationDegrees = rotationDegrees,
                FlipHorizontal = flipHorizontal,
                FlipVertical = flipVertical,
                TextStyle = textStyle,
                StyleRuns = styleRuns.Count == 0 ? null : styleRuns,
            };
        }

        var shapeGeometry = ResolvedGeometry(shape, frame.W, frame.H, state);

        if (text.Length == 0 && !fillIsInk && objectStroke is null) return null;

        return new SlideObject
        {
            Id = LibraryStore.NewId(),
            ObjectKind = SlideObjectKind.Shape,
            Name = shape.Name ?? (text.Length == 0 ? "Shape" : "Text"),
            Text = text,
            X = frame.X, Y = frame.Y, Width = frame.W, Height = frame.H,
            RotationDegrees = rotationDegrees,
            FlipHorizontal = flipHorizontal,
            FlipVertical = flipVertical,
            TextStyle = textStyle,
            ShapeKind = shapeGeometry.Kind,
            CornerRadius = shapeGeometry.CornerRadius,
            PathData = shapeGeometry.PathData,
            Fill = fill,
            Stroke = objectStroke,
            StyleRuns = styleRuns.Count == 0 ? null : styleRuns,
        };
    }

    private static (ShapeKind Kind, double? CornerRadius, string? PathData) ResolvedGeometry(
        PptxShape shape, double width, double height, MapState state)
    {
        if (shape.CustomPathData is { } custom) return (ShapeKind.Path, null, custom);
        switch (shape.PresetGeometry)
        {
            case null or "rect":
                return (ShapeKind.Rectangle, null, null);
            case "roundRect":
                return (ShapeKind.RoundedRectangle, (shape.RoundRectAdjustment ?? 16_667) / 100_000 * Math.Min(width, height), null);
            case "ellipse" or "flowChartConnector":
                return (ShapeKind.Ellipse, null, null);
            case var preset:
                if (PptxPresetGeometry.PathData(preset) is { } curated) return (ShapeKind.Path, null, curated);
                if (preset.StartsWith("bentConnector", StringComparison.Ordinal) || preset.StartsWith("curvedConnector", StringComparison.Ordinal))
                {
                    state.Warn($"connector \"{preset}\" imported as a straight line");
                    return (ShapeKind.Path, null, PptxPresetGeometry.LinePath);
                }
                state.Warn($"shape \"{preset}\" approximated as a rectangle");
                return (ShapeKind.Rectangle, null, null);
        }
    }

    private static ObjectFill? ObjectFillFrom(PptxFill? fill, MapState state)
    {
        switch (fill)
        {
            case null:
                return null;
            case PptxFill.NoFill:
                return new ObjectFill { FillKind = FillKind.None };
            case PptxFill.Solid solid:
                return new ObjectFill { FillKind = FillKind.Solid, ColorHex = Rgba(solid.ColorHex) };
            case PptxFill.Gradient { Value: var gradient }:
                return new ObjectFill
                {
                    FillKind = FillKind.LinearGradient,
                    GradientAngleDegrees = gradient.Angle60k / 60000,
                    GradientStops = gradient.Stops
                        .Select(s => new GradientStop { ColorHex = Rgba(s.ColorHex), Position = s.Position / 100_000 })
                        .ToList(),
                };
            case PptxFill.Blip blip:
                return new ObjectFill { FillKind = FillKind.Media, MediaId = state.Want(blip.Path), MediaScaleMode = MediaScaleMode.Stretch };
            case PptxFill.BlipTiled tiled:
                state.Warn("a tiled texture fill on a shape is approximated stretched");
                return new ObjectFill { FillKind = FillKind.Media, MediaId = state.Want(tiled.Path), MediaScaleMode = MediaScaleMode.Stretch };
            case PptxFill.Pattern pattern:
                state.Warn("pattern fills are approximated as a blend of their two colors");
                return new ObjectFill
                {
                    FillKind = FillKind.Solid,
                    ColorHex = Rgba(PatternBlend(pattern.Preset, pattern.ForegroundHex, pattern.BackgroundHex)),
                };
            default:
                return null;
        }
    }

    public static string PatternBlend(string preset, string foreground, string background)
    {
        double coverage;
        if (preset.StartsWith("pct", StringComparison.Ordinal) && OoxmlQuery.ParseDouble(preset[3..]) is { } percent)
            coverage = percent / 100;
        else if (TextureBaker.StructuralPatterns.Contains(preset))
            coverage = 0.15;
        else
            coverage = 0.5;
        return PptxSlideParser.Blend(foreground, background, coverage);
    }

    private static MediaSourceRect? MediaSourceRectFrom(PptxSourceRect? rect)
    {
        if (rect is null || rect.IsFullFrame) return null;
        return new MediaSourceRect
        {
            X = rect.L / 100_000,
            Y = rect.T / 100_000,
            Width = 1 - (rect.L + rect.R) / 100_000,
            Height = 1 - (rect.T + rect.B) / 100_000,
        };
    }

    private static (TextStyle Style, List<TextStyleRun> Runs) TextStyleFor(PptxTextBody body, double k, MapState state)
    {
        var lines = body.Lines;
        var allRuns = lines.SelectMany(l => l.Runs).Where(r => r.Text.Length > 0).ToList();
        var baseRun = DominantRun(allRuns);
        var fontScale = body.HasNormAutofit ? (body.AutofitFontScale ?? 100_000) / 100_000 : 1;

        double ScaledSize(PptxRun? run) => (run?.SizeHundredthsPt ?? 1800) / 100 * k * fontScale;
        double? ScaledTracking(PptxRun? run) => run?.TrackingHundredthsPt / 100 * k;

        var style = new TextStyle();
        var baseFontName = ResolvedFontName(baseRun, null, state);
        var baseFontSize = ScaledSize(baseRun);
        var baseColor = baseRun?.ColorHex is { } baseHex ? Rgba(baseHex) : null;
        var baseTracking = ScaledTracking(baseRun);
        style.FontName = baseFontName;
        style.FontSize = baseFontSize;
        style.ColorHex = baseColor;
        if (baseTracking is { } tracking && tracking != 0) style.Tracking = tracking;

        style.VerticalAlignment = body.Anchor switch
        {
            "ctr" => TextVerticalAlignment.Middle,
            "b" => TextVerticalAlignment.Bottom,
            _ => TextVerticalAlignment.Top,
        };
        style.HorizontalAlignment = HorizontalAlignment(lines, state);

        if (body.HasNormAutofit) style.AutoShrink = true;

        var insetTop = (body.TopInsetEmu ?? 45_720) / 12700 * k;
        var insetLeft = (body.LeftInsetEmu ?? 91_440) / 12700 * k;
        var insetBottom = (body.BottomInsetEmu ?? 45_720) / 12700 * k;
        var insetRight = (body.RightInsetEmu ?? 91_440) / 12700 * k;
        if (insetTop != 0) style.InsetTop = insetTop;
        if (insetLeft != 0) style.InsetLeft = insetLeft;
        if (insetBottom != 0) style.InsetBottom = insetBottom;
        if (insetRight != 0) style.InsetRight = insetRight;

        var uniformUnderline = allRuns.Count > 0 && allRuns.All(r => r.Underline);
        var uniformStrike = allRuns.Count > 0 && allRuns.All(r => r.Strike);
        if (uniformUnderline) style.Underline = true;
        if (uniformStrike) style.Strikethrough = true;

        var overrides = new List<LineStyleOverride>();
        var styleRuns = new List<TextStyleRun>();
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var lineDominant = DominantRun(line.Runs.Where(r => r.Text.Length > 0).ToList());
            var lineFontName = ResolvedFontName(lineDominant, baseRun?.FontFamily, state) ?? baseFontName;
            var lineFontSize = lineDominant is null ? baseFontSize : ScaledSize(lineDominant);
            var lineColor = lineDominant?.ColorHex is { } lineHex ? Rgba(lineHex) : baseColor;
            var lineTracking = (lineDominant is null ? null : ScaledTracking(lineDominant)) ?? baseTracking;

            if (lineDominant is not null)
            {
                var lineOverride = new LineStyleOverride { LineIndex = index };
                if (lineFontName != baseFontName) lineOverride.FontName = lineFontName;
                if (lineFontSize != baseFontSize) lineOverride.FontSize = lineFontSize;
                if (lineColor != baseColor) lineOverride.ColorHex = lineColor;
                if (lineTracking != baseTracking) lineOverride.Tracking = lineTracking;
                if (!MxuJson.StructurallyEqual(lineOverride, new LineStyleOverride { LineIndex = index })) overrides.Add(lineOverride);
            }

            var column = 0;
            foreach (var run in line.Runs)
            {
                var length = CharacterCount(run.Text);
                if (length > 0)
                {
                    var styleRun = new TextStyleRun { Line = index, Column = column, Length = length };
                    var inherited = lineDominant?.FontFamily ?? baseRun?.FontFamily;
                    if (ResolvedFontName(run, inherited, state) is { } runFontName && runFontName != lineFontName)
                        styleRun.FontName = runFontName;
                    if (ScaledSize(run) != lineFontSize && run.SizeHundredthsPt is not null)
                        styleRun.FontSize = ScaledSize(run);
                    if (run.ColorHex is { } runHex && Rgba(runHex) is var color && color != lineColor)
                        styleRun.ColorHex = color;
                    if (ScaledTracking(run) is { } runTracking && runTracking != lineTracking)
                        styleRun.Tracking = runTracking;
                    if (!uniformUnderline && run.Underline) styleRun.Underline = true;
                    if (!uniformStrike && run.Strike) styleRun.Strikethrough = true;
                    if (!MxuJson.StructurallyEqual(styleRun, new TextStyleRun { Line = index, Column = column, Length = length }))
                        styleRuns.Add(styleRun);
                }
                column += length;
            }
        }
        if (overrides.Count > 0) style.LineStyles = overrides;

        return (style, styleRuns);
    }

    private static PptxRun? DominantRun(IReadOnlyList<PptxRun> runs)
    {
        static bool SameStyle(PptxRun a, PptxRun b) =>
            a.FontFamily == b.FontFamily && a.SizeHundredthsPt == b.SizeHundredthsPt
            && a.Bold == b.Bold && a.Italic == b.Italic
            && a.ColorHex == b.ColorHex && a.TrackingHundredthsPt == b.TrackingHundredthsPt;
        var representatives = new List<PptxRun>();
        var counts = new List<int>();
        foreach (var run in runs.Where(r => r.Text.Length > 0))
        {
            var index = representatives.FindIndex(r => SameStyle(r, run));
            if (index >= 0)
            {
                counts[index] += CharacterCount(run.Text);
            }
            else
            {
                representatives.Add(run);
                counts.Add(CharacterCount(run.Text));
            }
        }
        int? best = null;
        for (var index = 0; index < representatives.Count; index++)
        {
            if (best is not { } current || counts[index] > counts[current]) best = index;
        }
        return best is { } winner ? representatives[winner] : null;
    }

    private static TextHorizontalAlignment HorizontalAlignment(List<PptxTextLine> lines, MapState state)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var line in lines)
        {
            var alignment = line.Alignment ?? "l";
            if (!counts.ContainsKey(alignment)) order.Add(alignment);
            counts[alignment] = counts.GetValueOrDefault(alignment) + CharacterCount(line.Text);
        }
        var best = "l";
        if (order.Count > 0)
        {
            best = order[0];
            foreach (var candidate in order.Skip(1))
            {
                if (counts[best] < counts[candidate]) best = candidate;
            }
        }
        switch (best)
        {
            case "ctr":
                return TextHorizontalAlignment.Center;
            case "r":
                return TextHorizontalAlignment.Right;
            case "just":
                state.Warn("justified text is not supported — aligned left");
                return TextHorizontalAlignment.Left;
            default:
                return TextHorizontalAlignment.Left;
        }
    }

    /// <summary>
    /// The Mac resolves an installed face to its PostScript name through Core Text.
    /// Windows has no such lookup, so faces are named the way the Windows editor
    /// names them (<c>Family</c>, <c>Family-Bold</c>, <c>Family-Italic</c>,
    /// <c>Family-BoldItalic</c>), which is also the Mac's fallback for missing fonts.
    /// </summary>
    private static string? ResolvedFontName(PptxRun? run, string? inheritedFamily, MapState state)
    {
        if (run is null || (run.FontFamily ?? inheritedFamily) is not { } family) return null;
        var cacheKey = $"{family}|{run.Bold}|{run.Italic}";
        if (state.FontNameCache.TryGetValue(cacheKey, out var cached)) return cached;

        if (!state.IsFontInstalled(family))
        {
            state.Warn($"font \"{family}\" is not installed — imported by name");
            if (!state.MissingFonts.Contains(family)) state.MissingFonts.Add(family);
        }
        var resolved = (run.Bold, run.Italic) switch
        {
            (false, false) => family,
            (true, false) => family + "-Bold",
            (false, true) => family + "-Italic",
            (true, true) => family + "-BoldItalic",
        };
        state.FontNameCache[cacheKey] = resolved;
        return resolved;
    }

    private static string Rgba(string hex)
    {
        var upper = hex.ToUpperInvariant();
        return upper.Length == 8 ? $"#{upper}" : $"#{upper}FF";
    }

    /// <summary>Swift's <c>String.count</c>: user-perceived characters, which is what MxU text columns count.</summary>
    public static int CharacterCount(string text) => new StringInfo(text).LengthInTextElements;
}
