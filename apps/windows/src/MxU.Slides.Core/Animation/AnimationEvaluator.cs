using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Animation;

/// <summary>The laid-out height of a text block at a width (TextRasterizer.blockHeight on the Mac).</summary>
public delegate double TextBlockHeight(StyledText text, double width);

/// <summary>
/// Evaluates every item's animation at one instant: what to draw (an item can
/// split into a base, range pieces and a morph partner, or vanish) and how to
/// move each piece. Pure; the renderer calls it once per frame with its host
/// time. Port of RenderEngine/AnimationEvaluator.swift.
/// </summary>
public static class AnimationEvaluator
{
    public static ResolvedScene Resolve(AnimatedScene scene, double hostTime, TextBlockHeight? blockHeight = null)
    {
        var motions = new Dictionary<string, AnimationMotion>();
        var pushState = VideoPushEvaluator.ActiveState(scene, hostTime);
        var pushTransform = pushState is null ? null : VideoPushEvaluator.TransformFor(scene.CanvasSize, pushState);
        var backdrop = pushState is { Push: { Mode: not SceneVideoPushMode.BlurBackground, Backdrop: true } }
            ? VideoPushEvaluator.BackdropTransform(scene.CanvasSize, pushState)
            : null;
        var layers = new List<ResolvedLayer>(scene.Layers.Count);
        foreach (var layer in scene.Layers)
        {
            var pushed = VideoPushEvaluator.PushedLayers.Contains(layer.Kind);
            var items = new List<ResolvedItem>(layer.Items.Count);
            foreach (var item in layer.Items)
            {
                foreach (var each in Resolve(item, scene.CanvasSize, hostTime, blockHeight))
                {
                    var resolved = each;
                    if (pushed && pushTransform is not null)
                    {
                        if (backdrop is not null && resolved.Item.Render.MatteGroup is null)
                        {
                            var clone = VideoPushEvaluator.Apply(backdrop, resolved);
                            clone = clone with
                            {
                                Item = clone.Item with
                                {
                                    Render = clone.Item.Render with
                                    {
                                        Id = $"{resolved.Item.Id}#pushbg",
                                        MaskedBy = null,
                                        Opacity = resolved.Item.Render.Opacity * Math.Min(pushState!.Amount * 2, 1),
                                    },
                                },
                            };
                            items.Add(clone);
                            if (!clone.Motion.IsIdentity) motions[clone.Item.Id] = clone.Motion;
                        }
                        resolved = VideoPushEvaluator.Apply(pushTransform, resolved);
                    }
                    items.Add(resolved);
                    if (!resolved.Motion.IsIdentity) motions[resolved.Item.Id] = resolved.Motion;
                }
            }
            layers.Add(new ResolvedLayer(layer.Kind, layer.IsHidden, items));
        }
        return new ResolvedScene(scene.CanvasSize, scene.Background, layers, motions);
    }

    /// <summary>
    /// One item at <paramref name="hostTime"/>: empty when hidden, otherwise the
    /// base first, then any running text-range pieces, then a morph cross-fade partner.
    /// </summary>
    public static List<ResolvedItem> Resolve(AnimatedItem item, SceneSize canvasSize, double hostTime, TextBlockHeight? blockHeight = null)
    {
        if (item.Render.Content is not ItemContent.Text { Styled: var text } || item.Scroll is not { Speed: > 0 } scroll)
            return ResolveBuilds(item, canvasSize, hostTime);

        if (item.AnimationContext?.FrozenHostTime == AnimationContext.Settled.FrozenHostTime)
            return ResolveBuilds(item with { Scroll = null }, canvasSize, hostTime);

        var anchor = item.TickerAnchorHostTime ?? item.AnimationContext?.AnchorHostTime;
        var now = item.AnimationContext?.FrozenHostTime ?? hostTime;
        var elapsed = anchor is { } start ? now - start : now;
        var height = blockHeight?.Invoke(text, item.Render.Frame.Width) ?? 0;
        if (BlockScrollRoll.Resolve(item, text, scroll, height, elapsed, anchor is not null) is not { } rolled) return [];
        return ResolveBuilds(rolled.Item, canvasSize, hostTime)
            .Select(resolved => resolved with
            {
                Motion = resolved.Motion with { Translate = resolved.Motion.Translate + rolled.Translate, Clip = rolled.Clip },
            })
            .ToList();
    }

    internal static List<ResolvedItem> ResolveBuilds(AnimatedItem item, SceneSize canvasSize, double hostTime)
    {
        var staticTilt = item.Tilt;
        if (item.AnimationSteps.Count == 0 || item.AnimationContext is not { } context)
        {
            var still = new AnimationMotion
            {
                Tilt = staticTilt, Swing = item.Swing, TiltPivot = item.TiltPivot,
                KeystoneTop = item.KeystoneTop, KeystoneBottom = item.KeystoneBottom, KeystoneStretch = item.KeystoneStretch,
                SkewX = item.Render.SkewX, SkewY = item.Render.SkewY,
            };
            return [new ResolvedItem(item, still)];
        }
        var visibility = AnimationTimeline.Visibility(item.AnimationSteps, context, hostTime);
        if (visibility.HidesWholeObject) return [];

        var morphed = MorphStateOf(item, context, hostTime);
        var baseItem = morphed.Base;
        var partner = morphed.Partner;
        var morphMotion = morphed.Motion;

        var wholeSteps = item.AnimationSteps.Where(s => s.TargetsWholeObject).ToList();
        var whole = Animate(wholeSteps, context, hostTime, baseItem.Render.Frame, canvasSize);
        var uniform = whole.Motion.Scale;
        var wholeMotion = whole.Motion with
        {
            Tilt = whole.Motion.Tilt + staticTilt + morphMotion.Tilt,
            Swing = whole.Motion.Swing + baseItem.Swing + morphMotion.Swing,
            TiltPivot = baseItem.TiltPivot,
            KeystoneTop = baseItem.KeystoneTop,
            KeystoneBottom = baseItem.KeystoneBottom,
            KeystoneStretch = baseItem.KeystoneStretch,
            SkewX = whole.Motion.SkewX + baseItem.Render.SkewX,
            SkewY = whole.Motion.SkewY + baseItem.Render.SkewY,
            Translate = whole.Motion.Translate + morphMotion.Translate,
            Scale = uniform * morphMotion.Scale,
            ScaleY = morphMotion.ScaleY * uniform,
        };
        baseItem = WithOpacity(baseItem, Math.Clamp(baseItem.Render.Opacity * whole.Opacity, 0, 1));
        if (partner is not null) partner = WithOpacity(partner, Math.Clamp(partner.Render.Opacity * whole.Opacity, 0, 1));
        if (whole.Effects.Count > 0)
        {
            baseItem = baseItem with { Effects = [.. whole.Effects, .. baseItem.Effects] };
            if (partner is not null) partner = partner with { Effects = [.. whole.Effects, .. partner.Effects] };
        }

        if (baseItem.Render.Content is ItemContent.Shape && RunningStep(SceneStepAnimation.Draw, wholeSteps, context, hostTime) is { } draw)
        {
            var r = draw.Step.Kind == SceneAnimationKind.Enter ? draw.Progress : 1 - draw.Progress;
            baseItem = baseItem with
            {
                StrokeTrim = new StrokeTrim(draw.Step.DrawStart, Quantize(r), draw.Step.Reverse),
                FillOpacity = Quantize(Math.Clamp((r - 0.6) / 0.4, 0, 1)),
            };
        }

        if (baseItem.Render.Content is not ItemContent.Text { Styled: var text })
            return WithPartner([new ResolvedItem(baseItem, wholeMotion)], partner, wholeMotion);

        var typeRuns = new List<HiddenTextRun>();
        var typingRanges = new HashSet<SceneAnimationRange>();
        foreach (var step in item.AnimationSteps.Where(s => s.Animation == SceneStepAnimation.Type))
        {
            if (!AnimationTimeline.Phase(step, context, hostTime).IsRunning(out var progress)) continue;
            var r = step.Kind == SceneAnimationKind.Enter ? progress : 1 - progress;
            var targets = step.Ranges ?? WholeRanges(text.String);
            typeRuns.AddRange(TypeRuns(r, targets, text.String, step.Cursor, step.Reverse));
            typingRanges.UnionWith(targets);
        }
        var rangeSteps = item.AnimationSteps.Where(s => !s.TargetsWholeObject).ToList();
        if (rangeSteps.Count == 0 && typeRuns.Count == 0)
            return WithPartner([new ResolvedItem(baseItem, wholeMotion)], partner, wholeMotion);

        var results = new List<ResolvedItem>();
        var hiddenOnBase = new List<SceneAnimationRange>(visibility.HiddenRanges);
        var placeholders = visibility.PlaceholderRanges.ToHashSet();
        var pieceIndex = 0;
        foreach (var step in rangeSteps.Where(s => s.Animation != SceneStepAnimation.Type))
        {
            if (!AnimationTimeline.Phase(step, context, hostTime).IsRunning(out _) || step.Ranges is not { } ranges) continue;
            var animated = Animate([step], context, hostTime, item.Render.Frame, canvasSize);
            var piece = baseItem with
            {
                Render = baseItem.Render with
                {
                    Id = $"{item.Id}#step{pieceIndex++}",
                    Content = new ItemContent.Text(text with { Chords = [] }),
                    MatteGroup = null,
                    Opacity = Math.Clamp(baseItem.Render.Opacity * animated.Opacity, 0, 1),
                },
                HiddenRuns = ComplementRuns(ranges, text.String),
                Effects = animated.Effects.Count > 0 ? [.. animated.Effects, .. baseItem.Effects] : baseItem.Effects,
            };
            var motion = wholeMotion with
            {
                Translate = wholeMotion.Translate + animated.Motion.Translate,
                Scale = wholeMotion.Scale * animated.Motion.Scale,
                Wipe = animated.Motion.Wipe ?? wholeMotion.Wipe,
            };
            results.Add(new ResolvedItem(piece, motion));
            hiddenOnBase.AddRange(ranges);
        }
        var runs = new List<HiddenTextRun>(baseItem.HiddenRuns);
        foreach (var range in hiddenOnBase.Where(r => !typingRanges.Contains(r)))
            runs.Add(new HiddenTextRun(range.Line, range.Column, range.Length, PlaceholderUnderline: placeholders.Contains(range)));
        runs.AddRange(typeRuns);
        results.Insert(0, new ResolvedItem(baseItem with { HiddenRuns = runs }, wholeMotion));
        return WithPartner(results, partner, wholeMotion);
    }

    private static List<ResolvedItem> WithPartner(List<ResolvedItem> items, AnimatedItem? partner, AnimationMotion motion)
    {
        if (partner is not null) items.Add(new ResolvedItem(partner, motion));
        return items;
    }

    private static AnimatedItem WithOpacity(AnimatedItem item, double opacity) =>
        item with { Render = item.Render with { Opacity = opacity } };

    // MARK: Morphs

    internal sealed record MorphState(AnimatedItem Base, AnimatedItem? Partner, AnimationMotion Motion)
    {
        public MorphState(AnimatedItem baseItem) : this(baseItem, null, new AnimationMotion()) { }
    }

    /// <summary>Folds finished morphs into the item and tweens the running morph (or custom-start In).</summary>
    internal static MorphState MorphStateOf(AnimatedItem item, AnimationContext? context, double now)
    {
        var current = item;
        foreach (var step in item.AnimationSteps)
        {
            if (step.Kind == SceneAnimationKind.Morph && step.ToItem is { } target)
            {
                var phase = AnimationTimeline.Phase(step, context, now);
                if (phase.IsDone) current = Adopt(target, current);
                else if (phase.IsRunning(out var p)) return Blended(Tween(current, Adopt(target, current), p, item), step, p);
            }
            else if (step.Kind == SceneAnimationKind.Enter && step.TargetsWholeObject && step.FromItem is { } start)
            {
                if (AnimationTimeline.Phase(step, context, now).IsRunning(out var p))
                    return Blended(Tween(Adopt(start, current), current, p, item), step, p);
            }
        }
        return new MorphState(current);
    }

    internal static MorphState Blended(MorphState state, SceneAnimationStep step, double p)
    {
        var peak = Quantize(1 - Math.Abs(2 * p - 1));
        AnimationEffect? pass = step.Animation switch
        {
            SceneStepAnimation.Blur => new AnimationEffect.Blur(peak * step.Amount),
            SceneStepAnimation.Burn => new AnimationEffect.Burn(peak * step.Amount, Quantize(p)),
            SceneStepAnimation.Glitch => new AnimationEffect.Glitch(peak * step.Amount, Quantize(p)),
            _ => null,
        };
        if (pass is null || peak <= 0.001) return state;
        return state with
        {
            Base = state.Base with { Effects = [pass, .. state.Base.Effects] },
            Partner = state.Partner is { } partner ? partner with { Effects = [pass, .. partner.Effects] } : null,
        };
    }

    /// <summary>The item wearing the target state's look (frame, content, pose, effects); id, clock and steps stay.</summary>
    internal static AnimatedItem Adopt(AnimatedItem target, AnimatedItem item) => item with
    {
        Render = item.Render with
        {
            Frame = target.Render.Frame,
            Content = target.Render.Content,
            RotationDegrees = target.Render.RotationDegrees,
            SkewX = target.Render.SkewX,
            SkewY = target.Render.SkewY,
            FlipHorizontal = target.Render.FlipHorizontal,
            FlipVertical = target.Render.FlipVertical,
            Opacity = target.Render.Opacity,
            BlendMode = target.Render.BlendMode,
            BlurRadius = target.Render.BlurRadius,
        },
        Tilt = target.Tilt,
        Swing = target.Swing,
        TiltPivot = target.TiltPivot,
        KeystoneTop = target.KeystoneTop,
        KeystoneBottom = target.KeystoneBottom,
        KeystoneStretch = target.KeystoneStretch,
        Effects = target.Effects,
        EffectsApplyBelow = target.EffectsApplyBelow,
        HiddenRuns = target.HiddenRuns,
        StrokeTrim = target.StrokeTrim,
        FillOpacity = target.FillOpacity,
        Scroll = target.Scroll,
    };

    internal static MorphState Tween(AnimatedItem start, AnimatedItem end, double p, AnimatedItem item)
    {
        double Lerp(double a, double b) => a + (b - a) * p;
        var q = Quantize(p);
        RenderItem s = start.Render, e = end.Render;
        var frameLerp = new SceneRect(Lerp(s.Frame.X, e.Frame.X), Lerp(s.Frame.Y, e.Frame.Y), Lerp(s.Frame.Width, e.Frame.Width), Lerp(s.Frame.Height, e.Frame.Height));
        var motion = new AnimationMotion();
        var frame = s.Frame;
        var isText = s.Content is ItemContent.Text;
        if (isText && (s.Frame.Width != e.Frame.Width || s.Frame.Height != e.Frame.Height))
        {
            var sx = s.Frame.Width > 0 ? frameLerp.Width / s.Frame.Width : 1;
            var sy = s.Frame.Height > 0 ? frameLerp.Height / s.Frame.Height : 1;
            motion = motion with
            {
                Scale = sx,
                ScaleY = sy,
                Translate = new SceneVector(MidX(frameLerp) - MidX(s.Frame), MidY(frameLerp) - MidY(s.Frame)),
            };
        }
        else if (isText)
        {
            motion = motion with { Translate = new SceneVector(frameLerp.X - s.Frame.X, frameLerp.Y - s.Frame.Y) };
        }
        else
        {
            frame = new SceneRect(
                s.Frame.X + (e.Frame.X - s.Frame.X) * q, s.Frame.Y + (e.Frame.Y - s.Frame.Y) * q,
                s.Frame.Width + (e.Frame.Width - s.Frame.Width) * q, s.Frame.Height + (e.Frame.Height - s.Frame.Height) * q);
        }
        var swapsLate = (s.FlipHorizontal != e.FlipHorizontal || s.FlipVertical != e.FlipVertical || s.BlendMode != e.BlendMode) && p >= 0.5;
        var baseItem = start with
        {
            Render = s with
            {
                Frame = frame,
                RotationDegrees = Lerp(s.RotationDegrees, e.RotationDegrees),
                SkewX = Lerp(s.SkewX, e.SkewX),
                SkewY = Lerp(s.SkewY, e.SkewY),
                Opacity = Lerp(s.Opacity, e.Opacity),
                BlurRadius = TweenBlur(s.BlurRadius, e.BlurRadius, q),
                FlipHorizontal = swapsLate ? e.FlipHorizontal : s.FlipHorizontal,
                FlipVertical = swapsLate ? e.FlipVertical : s.FlipVertical,
                BlendMode = swapsLate ? e.BlendMode : s.BlendMode,
            },
            Tilt = Lerp(start.Tilt, end.Tilt),
            Swing = Lerp(start.Swing, end.Swing),
            TiltPivot = p >= 0.5 ? end.TiltPivot : start.TiltPivot,
            KeystoneTop = Lerp(start.KeystoneTop, end.KeystoneTop),
            KeystoneBottom = Lerp(start.KeystoneBottom, end.KeystoneBottom),
            KeystoneStretch = p >= 0.5 ? end.KeystoneStretch : start.KeystoneStretch,
            Effects = TweenEffects(start.Effects, end.Effects, q),
        };

        if (TweenContent(baseItem, end, q) is { } tweened) return new MorphState(tweened, null, motion);

        var partner = baseItem with
        {
            Render = baseItem.Render with { Id = $"{item.Id}#morph", Content = e.Content, MatteGroup = null, Opacity = baseItem.Render.Opacity * p },
            HiddenRuns = end.HiddenRuns,
            StrokeTrim = end.StrokeTrim,
            FillOpacity = end.FillOpacity,
            Scroll = end.Scroll,
        };
        return new MorphState(WithOpacity(baseItem, baseItem.Render.Opacity * (1 - p)), partner, motion);
    }

    /// <summary>The content of <paramref name="a"/> tweened toward <paramref name="b"/>'s, or null when they can't blend.</summary>
    internal static AnimatedItem? TweenContent(AnimatedItem a, AnimatedItem b, double p)
    {
        if (SameContent(a, b)) return a;
        switch (a.Render.Content, b.Render.Content)
        {
            case (ItemContent.Shape sa, ItemContent.Shape sb):
                if (TweenKind(sa.Style.Kind, sb.Style.Kind, p) is not { } kind
                    || TweenFill(sa.Style.Fill, sb.Style.Fill, p) is not { } fill
                    || !TweenStroke(sa.Style.Stroke, sb.Style.Stroke, p, out var stroke))
                    return null;
                return a with
                {
                    Render = a.Render with
                    {
                        Content = new ItemContent.Shape(sa.Style with { Kind = kind, Fill = fill, Stroke = stroke, Shadow = TweenShadow(sa.Style.Shadow, sb.Style.Shadow, p) }),
                    },
                    FillOpacity = a.FillOpacity + (b.FillOpacity - a.FillOpacity) * p,
                    StrokeTrim = p < 0.5 ? a.StrokeTrim : b.StrokeTrim,
                };
            case (ItemContent.Text ta, ItemContent.Text tb):
                var probe = ta.Styled with { Color = tb.Styled.Color, FontSize = tb.Styled.FontSize, Tracking = tb.Styled.Tracking };
                if (!TextEquals(probe, tb.Styled) || !a.HiddenRuns.SequenceEqual(b.HiddenRuns) || a.Scroll != b.Scroll) return null;
                return a with
                {
                    Render = a.Render with
                    {
                        Content = new ItemContent.Text(ta.Styled with
                        {
                            Color = LerpColor(ta.Styled.Color, tb.Styled.Color, p),
                            FontSize = ta.Styled.FontSize + (tb.Styled.FontSize - ta.Styled.FontSize) * p,
                            Tracking = ta.Styled.Tracking + (tb.Styled.Tracking - ta.Styled.Tracking) * p,
                        }),
                    },
                };
            default:
                return null;
        }
    }

    internal static SceneShapeKind? TweenKind(SceneShapeKind a, SceneShapeKind b, double p) => (a, b) switch
    {
        (SceneShapeKind.Rectangle, SceneShapeKind.Rectangle) or (SceneShapeKind.Ellipse, SceneShapeKind.Ellipse) => a,
        (SceneShapeKind.Path x, SceneShapeKind.Path y) when x.Data == y.Data => a,
        (SceneShapeKind.Rectangle, SceneShapeKind.RoundedRectangle r) => new SceneShapeKind.RoundedRectangle(r.CornerRadius * p),
        (SceneShapeKind.RoundedRectangle r, SceneShapeKind.Rectangle) => new SceneShapeKind.RoundedRectangle(r.CornerRadius * (1 - p)),
        (SceneShapeKind.RoundedRectangle r1, SceneShapeKind.RoundedRectangle r2) =>
            new SceneShapeKind.RoundedRectangle(r1.CornerRadius + (r2.CornerRadius - r1.CornerRadius) * p),
        _ => null,
    };

    internal static SceneFill? TweenFill(SceneFill a, SceneFill b, double p)
    {
        if (a == b) return a;
        return (a, b) is (SceneFill.Solid ca, SceneFill.Solid cb) ? new SceneFill.Solid(LerpColor(ca.Color, cb.Color, p)) : null;
    }

    /// <summary>False when the strokes can't blend (different dashes).</summary>
    internal static bool TweenStroke(SceneStroke? a, SceneStroke? b, double p, out SceneStroke? stroke)
    {
        stroke = null;
        switch (a, b)
        {
            case (null, null):
                return true;
            case ({ } sa, { } sb):
                if (sa.Dash != sb.Dash) return false;
                stroke = sa with { Color = LerpColor(sa.Color, sb.Color, p), Width = sa.Width + (sb.Width - sa.Width) * p };
                return true;
            case ({ } sa, null):
                var thinning = sa with { Width = sa.Width * (1 - p) };
                stroke = thinning.Width > 0.01 ? thinning : null;
                return true;
            default:
                var growing = b! with { Width = b.Width * p };
                stroke = growing.Width > 0.01 ? growing : null;
                return true;
        }
    }

    internal static TextShadow? TweenShadow(TextShadow? a, TextShadow? b, double p) => (a, b) switch
    {
        (null, null) => null,
        ({ } sa, { } sb) => sa with
        {
            Color = LerpColor(sa.Color, sb.Color, p),
            BlurRadius = sa.BlurRadius + (sb.BlurRadius - sa.BlurRadius) * p,
            OffsetX = sa.OffsetX + (sb.OffsetX - sa.OffsetX) * p,
            OffsetY = sa.OffsetY + (sb.OffsetY - sa.OffsetY) * p,
        },
        ({ } sa, null) => sa with { Color = sa.Color.WithAlpha(sa.Color.Alpha * (1 - p)) },
        (null, { } sb) => sb with { Color = sb.Color.WithAlpha(sb.Color.Alpha * p) },
    };

    internal static IReadOnlyList<AnimationEffect> TweenEffects(IReadOnlyList<AnimationEffect> a, IReadOnlyList<AnimationEffect> b, double p)
    {
        if (a.Count != b.Count) return p < 0.5 ? a : b;
        return a.Zip(b, (ea, eb) => ea is AnimationEffect.Blur ba && eb is AnimationEffect.Blur bb
            ? new AnimationEffect.Blur(ba.Radius + (bb.Radius - ba.Radius) * p) { Opacity = ea.Opacity + (eb.Opacity - ea.Opacity) * p }
            : p < 0.5 ? ea : eb).ToList();
    }

    /// <summary>The item's own blur, tweened like the Mac's one-pass blur effect: blends when both sides blur, else swaps halfway.</summary>
    private static double TweenBlur(double a, double b, double p) =>
        (a > 0) == (b > 0) ? a + (b - a) * p : p < 0.5 ? a : b;

    internal static SceneColor LerpColor(SceneColor a, SceneColor b, double p) => new(
        a.Red + (b.Red - a.Red) * p, a.Green + (b.Green - a.Green) * p,
        a.Blue + (b.Blue - a.Blue) * p, a.Alpha + (b.Alpha - a.Alpha) * p);

    private static bool SameContent(AnimatedItem a, AnimatedItem b) =>
        ContentEquals(a.Render.Content, b.Render.Content) && a.HiddenRuns.SequenceEqual(b.HiddenRuns)
        && a.StrokeTrim == b.StrokeTrim && a.FillOpacity == b.FillOpacity && a.Scroll == b.Scroll;

    /// <summary>Value equality for content (StyledText's lists compare by reference otherwise).</summary>
    internal static bool ContentEquals(ItemContent a, ItemContent b) =>
        a is ItemContent.Text ta && b is ItemContent.Text tb ? TextEquals(ta.Styled, tb.Styled) : Equals(a, b);

    internal static bool TextEquals(StyledText a, StyledText b) =>
        a.LineOverrides.SequenceEqual(b.LineOverrides) && a.StyleRuns.SequenceEqual(b.StyleRuns) && a.Chords.SequenceEqual(b.Chords)
        && a with { LineOverrides = b.LineOverrides, StyleRuns = b.StyleRuns, Chords = b.Chords } == b;

    internal static (SceneAnimationStep Step, double Progress)? RunningStep(
        SceneStepAnimation animation, IEnumerable<SceneAnimationStep> steps, AnimationContext? context, double now)
    {
        foreach (var step in steps.Where(s => s.Animation == animation))
            if (AnimationTimeline.Phase(step, context, now).IsRunning(out var p)) return (step, p);
        return null;
    }

    /// <summary>Snaps a 0…1 progress to 1/240ths, so per-frame passes cache well.</summary>
    internal static double Quantize(double value) => Math.Round(value * 240, MidpointRounding.AwayFromZero) / 240;

    private static double MidX(SceneRect r) => r.X + r.Width / 2;
    private static double MidY(SceneRect r) => r.Y + r.Height / 2;

    // MARK: Text ranges

    internal static List<SceneAnimationRange> WholeRanges(string text) =>
        text.Split('\n')
            .Select((line, index) => (line, index))
            .Where(l => l.line.Length > 0)
            .Select(l => new SceneAnimationRange(l.index, 0, l.line.CharacterCount()))
            .ToList();

    /// <summary>The runs a Type step leaves hidden at <paramref name="reveal"/> (0…1), with the caret on the first.</summary>
    internal static List<HiddenTextRun> TypeRuns(double reveal, IReadOnlyList<SceneAnimationRange> ranges, string text, bool caret, bool reverse)
    {
        var lines = text.Split('\n');
        var clamped = new List<SceneAnimationRange>();
        foreach (var range in ranges)
        {
            if (range.Line < 0 || range.Line >= lines.Length) continue;
            var count = lines[range.Line].CharacterCount();
            var start = Math.Clamp(range.Column, 0, count);
            var end = Math.Min(start + Math.Max(range.Length, 0), count);
            if (end > start) clamped.Add(new SceneAnimationRange(range.Line, start, end - start));
        }
        var total = clamped.Sum(r => r.Length);
        if (total <= 0) return [];
        var remaining = (int)Math.Floor(Math.Clamp(reveal, 0, 1) * total);
        var runs = new List<HiddenTextRun>();
        var caretPlaced = false;
        foreach (var range in reverse ? Enumerable.Reverse(clamped) : clamped)
        {
            if (remaining >= range.Length)
            {
                remaining -= range.Length;
                continue;
            }
            var hidden = reverse
                ? new SceneAnimationRange(range.Line, range.Column, range.Length - remaining)
                : new SceneAnimationRange(range.Line, range.Column + remaining, range.Length - remaining);
            remaining = 0;
            runs.Add(new HiddenTextRun(hidden.Line, hidden.Column, hidden.Length, Caret: caret && !caretPlaced && !reverse));
            caretPlaced = true;
        }
        return runs;
    }

    /// <summary>Hides everything on each line except <paramref name="ranges"/> (merged and clamped).</summary>
    internal static List<HiddenTextRun> ComplementRuns(IReadOnlyList<SceneAnimationRange> ranges, string text)
    {
        var lines = text.Split('\n');
        var runs = new List<HiddenTextRun>();
        for (var index = 0; index < lines.Length; index++)
        {
            var count = lines[index].CharacterCount();
            if (count == 0) continue;
            var keep = ranges
                .Where(r => r.Line == index && r.Length > 0)
                .Select(r =>
                {
                    var start = Math.Clamp(r.Column, 0, count);
                    return (Lower: start, Upper: Math.Min(start + r.Length, count));
                })
                .Where(r => r.Upper > r.Lower)
                .OrderBy(r => r.Lower)
                .ToList();
            var merged = new List<(int Lower, int Upper)>();
            foreach (var r in keep)
            {
                if (merged.Count > 0 && r.Lower <= merged[^1].Upper)
                    merged[^1] = (merged[^1].Lower, Math.Max(merged[^1].Upper, r.Upper));
                else
                    merged.Add(r);
            }
            var cursor = 0;
            foreach (var r in merged)
            {
                if (r.Lower > cursor) runs.Add(new HiddenTextRun(index, cursor, r.Lower - cursor));
                cursor = r.Upper;
            }
            if (cursor < count) runs.Add(new HiddenTextRun(index, cursor, count - cursor));
        }
        return runs;
    }

    // MARK: Step effects

    internal sealed record Animated(AnimationMotion Motion, double Opacity, IReadOnlyList<AnimationEffect> Effects);

    internal static Animated Animate(IEnumerable<SceneAnimationStep> steps, AnimationContext? context, double now, SceneRect frame, SceneSize canvasSize)
    {
        var translate = SceneVector.Zero;
        var scale = 1.0;
        MotionWipe? wipe = null;
        var opacity = 1.0;
        var effects = new List<AnimationEffect>();
        foreach (var step in steps)
        {
            if (!AnimationTimeline.Phase(step, context, now).IsRunning(out var progress)) continue;
            switch (step.Kind)
            {
                case SceneAnimationKind.Morph:
                    break;
                case SceneAnimationKind.Enter when step.FromItem is not null:
                    if (step.WithFade) opacity *= progress;
                    break;
                case SceneAnimationKind.Enter or SceneAnimationKind.Exit:
                    var r = step.Kind == SceneAnimationKind.Enter ? progress : 1 - progress;
                    switch (step.Animation)
                    {
                        case SceneStepAnimation.Fade:
                            opacity *= r;
                            break;
                        case SceneStepAnimation.Move:
                            translate += MoveOrigin(step, frame, canvasSize) * (1 - r);
                            if (step.WithFade) opacity *= r;
                            break;
                        case SceneStepAnimation.Scale:
                            scale *= step.FromScale + (1 - step.FromScale) * r;
                            if (step.WithFade) opacity *= r;
                            break;
                        case SceneStepAnimation.Wipe:
                            wipe = new MotionWipe(step.Edge ?? SceneAnimationEdge.Left, r, step.SoftEdge);
                            if (step.WithFade) opacity *= r;
                            break;
                        case SceneStepAnimation.Draw or SceneStepAnimation.Type:
                            if (step.WithFade) opacity *= r;
                            break;
                        case SceneStepAnimation.Blur:
                            effects.Add(new AnimationEffect.Blur(Quantize(1 - r) * step.Amount));
                            if (step.WithFade) opacity *= r;
                            break;
                        case SceneStepAnimation.Burn:
                            effects.Add(new AnimationEffect.Burn(Quantize(1 - r) * step.Amount, Quantize(r)));
                            opacity *= Math.Min(1, r * 2.5);
                            break;
                        case SceneStepAnimation.Glitch:
                            effects.Add(new AnimationEffect.Glitch(Quantize(1 - r) * step.Amount, Quantize(r)));
                            opacity *= r < 0.08 ? 0 : 1;
                            break;
                        case SceneStepAnimation.Pulse or SceneStepAnimation.Color:
                            opacity *= r;
                            break;
                    }
                    break;
                case SceneAnimationKind.Emphasis:
                    var envelope = Math.Sin(progress * Math.PI);
                    switch (step.Animation)
                    {
                        case SceneStepAnimation.Pulse or SceneStepAnimation.Scale:
                            scale *= 1 + (step.Amount - 1) * envelope;
                            break;
                        case SceneStepAnimation.Fade:
                            opacity *= 1 - 0.5 * envelope;
                            break;
                        case SceneStepAnimation.Move:
                            translate += MoveOrigin(step, frame, canvasSize) * envelope;
                            break;
                        case SceneStepAnimation.Color when step.Color is { } color:
                            effects.Add(new AnimationEffect.Tint(color, Math.Clamp(step.Amount, 0, 1) * color.Alpha * Quantize(envelope)));
                            break;
                    }
                    break;
            }
        }
        return new Animated(new AnimationMotion { Translate = translate, Scale = scale, Wipe = wipe }, opacity, effects);
    }

    internal static SceneVector MoveOrigin(SceneAnimationStep step, SceneRect frame, SceneSize canvasSize) => step.Edge switch
    {
        SceneAnimationEdge.Left => new SceneVector(-frame.Right, 0),
        SceneAnimationEdge.Right => new SceneVector(canvasSize.Width - frame.X, 0),
        SceneAnimationEdge.Top => new SceneVector(0, -frame.Bottom),
        SceneAnimationEdge.Bottom => new SceneVector(0, canvasSize.Height - frame.Y),
        _ => step.Offset,
    };
}
