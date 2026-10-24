using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Animation;

/// <summary>
/// A picture pass on an item: the object's own effects (colour adjust, pixelate,
/// stained glass…) and the ones animations add (blur, burn, glitch, tint).
/// <see cref="Opacity"/> blends the result with the untouched picture. Distances are
/// in canvas units. Port of the Mac's SceneEffect.
/// </summary>
public abstract record AnimationEffect
{
    private readonly double opacity = 1;

    private AnimationEffect() { }

    public double Opacity { get => opacity; init => opacity = Math.Clamp(value, 0, 1); }

    /// <summary>True for effects that change every frame (noise and shimmer), so the renderer keeps drawing.</summary>
    public virtual bool VariesWithTime => false;

    public sealed record Blur(double Radius) : AnimationEffect;
    public sealed record Burn(double Amount, double Phase) : AnimationEffect;
    public sealed record Glitch(double Amount, double Phase) : AnimationEffect;
    public sealed record Tint(SceneColor Color, double Amount) : AnimationEffect;

    /// <summary>Brightness and contrast (0 = unchanged), saturation (1 = unchanged), hue in degrees.</summary>
    public sealed record ColorAdjust(double Brightness, double Contrast, double Saturation, double Hue) : AnimationEffect;
    public sealed record HueRotate(double Degrees) : AnimationEffect;
    public sealed record Invert : AnimationEffect;
    public sealed record Posterize(double Levels) : AnimationEffect;
    public sealed record Pixelate(double Size) : AnimationEffect;
    public sealed record Vignette(double Strength) : AnimationEffect;

    public sealed record Warp(double Amount, double Scale, double Speed) : AnimationEffect
    {
        public override bool VariesWithTime => Speed > 0;
    }

    /// <summary>
    /// Anything that changes inside the item leaves a copy that fades to a tenth in <see cref="Fade"/>
    /// seconds (see <see cref="EffectTrail"/>). It needs every frame, so the view keeps drawing.
    /// </summary>
    public sealed record Echo(double Fade) : AnimationEffect
    {
        public override bool VariesWithTime => true;
    }

    public sealed record Scatter(double Amount, double Size, double Speed, double Smooth) : AnimationEffect
    {
        public override bool VariesWithTime => Speed > 0.001;
    }

    public sealed record StainedGlass(double CellSize, double Leading, double Jitter, double Speed) : AnimationEffect
    {
        public override bool VariesWithTime => Speed > 0;
    }

    public sealed record Grain(double Amount, double Size, double Speed) : AnimationEffect
    {
        public override bool VariesWithTime => Speed > 0.001;
    }

    /// <summary>
    /// The item smokes: each frame is mixed with the last one pushed about by slow noise
    /// (<see cref="Drift"/> canvas units, noise cells of <see cref="Scale"/>, moving at <see cref="Speed"/>).
    /// </summary>
    public sealed record GhostTrails(double Fade, double Drift, double Scale, double Speed) : AnimationEffect
    {
        public override bool VariesWithTime => true;
    }

    /// <summary>
    /// An object's effect list as passes, skipping disabled, invisible and do-nothing ones.
    /// Blur is left out: it travels as <see cref="RenderItem.BlurRadius"/>. Port of
    /// SlideSceneBuilder.sceneEffects (same defaults and limits).
    /// </summary>
    public static List<AnimationEffect> From(IReadOnlyList<Effect>? effects)
    {
        var passes = new List<AnimationEffect>();
        foreach (var effect in effects ?? [])
        {
            if (!(effect.Enabled ?? true)) continue;
            var opacity = Math.Clamp(effect.Opacity ?? 1, 0, 1);
            if (opacity <= 0.001) continue;
            AnimationEffect? pass = effect.EffectKind switch
            {
                EffectKind.ColorAdjust => new ColorAdjust(effect.Brightness ?? 0, effect.Contrast ?? 0, effect.Saturation ?? 1, effect.Hue ?? 0),
                EffectKind.HueRotate when Math.Abs(effect.Amount ?? 90) > 0.1 => new HueRotate(effect.Amount ?? 90),
                EffectKind.Invert => new Invert(),
                EffectKind.Posterize => new Posterize(Math.Clamp(effect.Amount ?? 6, 2, 32)),
                EffectKind.Pixelate when (effect.Amount ?? 16) >= 1 => new Pixelate(effect.Amount ?? 16),
                EffectKind.Vignette when Math.Clamp(effect.Amount ?? 0.8, 0, 1.5) > 0.001 => new Vignette(Math.Clamp(effect.Amount ?? 0.8, 0, 1.5)),
                EffectKind.Warp when Math.Max(effect.Amount ?? 40, 0) >= 0.5 =>
                    new Warp(Math.Max(effect.Amount ?? 40, 0), Math.Max(effect.Scale ?? 240, 1), Math.Max(effect.Speed ?? 0.5, 0)),
                EffectKind.Echo when Math.Clamp(effect.Amount ?? 1, 0, 20) > 0.01 => new Echo(Math.Clamp(effect.Amount ?? 1, 0, 20)),
                EffectKind.Scatter when Math.Max(effect.Amount ?? 8, 0) >= 0.5 =>
                    new Scatter(Math.Max(effect.Amount ?? 8, 0), Math.Max(effect.Scale ?? 1, 1), Math.Clamp(effect.Speed ?? 1, 0, 1), Math.Clamp(effect.Smooth ?? 0, 0, 1)),
                EffectKind.StainedGlass => new StainedGlass(Math.Clamp(effect.Scale ?? 80, 4, 2000), Math.Max(effect.Radius ?? 3, 0),
                    Math.Clamp(effect.Amount ?? 0.8, 0, 1), Math.Max(effect.Speed ?? 0, 0)),
                EffectKind.Grain when Math.Clamp(effect.Amount ?? 0.3, 0, 1) > 0.001 =>
                    new Grain(Math.Clamp(effect.Amount ?? 0.3, 0, 1), Math.Max(effect.Scale ?? 1, 1), Math.Clamp(effect.Speed ?? 1, 0, 1)),
                EffectKind.GhostTrails when Math.Clamp(effect.Fade ?? 1.5, 0, 20) > 0.01 =>
                    new GhostTrails(Math.Clamp(effect.Fade ?? 1.5, 0, 20), Math.Max(effect.Amount ?? 6, 0), Math.Max(effect.Scale ?? 160, 1), Math.Max(effect.Speed ?? 0.5, 0)),
                _ => null,
            };
            if (pass is not null) passes.Add(pass with { Opacity = opacity });
        }
        return passes;
    }
}

/// <summary>How much of a shape's outline is stroked (the Draw animation). Start and length are fractions of the perimeter.</summary>
public readonly record struct StrokeTrim(double Start, double Length, bool Reversed = false);

/// <summary>
/// Characters of a text item not to draw this frame (a pending build, the untyped
/// tail of a Type). Columns are Swift Characters, as in <see cref="StyleRun"/>.
/// <see cref="PlaceholderUnderline"/> draws a blank underline in its place;
/// <see cref="Caret"/> draws the typing caret at its start.
/// </summary>
public readonly record struct HiddenTextRun(int Line, int Column, int Length, bool PlaceholderUnderline = false, bool Caret = false);

/// <summary>
/// A render item as the animation engine sees it: the <see cref="RenderItem"/>
/// plus its scheduled steps and clock, and the Mac RenderItem state the Windows
/// scene model doesn't carry yet (3-D pose, effect passes, hidden text runs,
/// stroke trim, block scroll). Plays the part of the Mac's RenderItem in the
/// ported engine.
/// </summary>
public sealed record AnimatedItem
{
    public required RenderItem Render { get; init; }
    public IReadOnlyList<SceneAnimationStep> AnimationSteps { get; init; } = [];
    public AnimationContext? AnimationContext { get; init; }

    /// <summary>Anchors tickers and block scrolls; falls back to the context's anchor.</summary>
    public double? TickerAnchorHostTime { get; init; }

    public double Tilt { get; init; }
    public double Swing { get; init; }
    public TiltPivot TiltPivot { get; init; } = TiltPivot.Center;
    public double KeystoneTop { get; init; } = 1;
    public double KeystoneBottom { get; init; } = 1;
    public bool KeystoneStretch { get; init; }

    /// <summary>Passes on top of <see cref="RenderItem.BlurRadius"/>, outermost first.</summary>
    public IReadOnlyList<AnimationEffect> Effects { get; init; } = [];
    public bool EffectsApplyBelow { get; init; }

    public IReadOnlyList<HiddenTextRun> HiddenRuns { get; init; } = [];
    public StrokeTrim? StrokeTrim { get; init; }
    public double FillOpacity { get; init; } = 1;
    public SceneBlockScroll? Scroll { get; init; }

    public string Id => Render.Id;

    public static implicit operator AnimatedItem(RenderItem item) => new() { Render = item };
}

public sealed class AnimatedLayer(LayerKind kind)
{
    public LayerKind Kind { get; } = kind;
    public bool IsHidden { get; set; }
    public List<AnimatedItem> Items { get; } = [];
}

/// <summary>A <see cref="RenderScene"/> whose items carry their animation.</summary>
public sealed class AnimatedScene
{
    public AnimatedScene(SceneSize canvasSize, SceneColor? background = null)
    {
        CanvasSize = canvasSize;
        Background = background ?? SceneColor.Black;
        Layers = LayerKinds.All.Select(kind => new AnimatedLayer(kind)).ToArray();
    }

    public SceneSize CanvasSize { get; }
    public SceneColor Background { get; set; }
    public IReadOnlyList<AnimatedLayer> Layers { get; }

    public AnimatedLayer this[LayerKind kind] => Layers[(int)kind];

    public void AddItem(AnimatedItem item, LayerKind layer) => this[layer].Items.Add(item);

    public IEnumerable<AnimatedItem> AllItems => Layers.SelectMany(layer => layer.Items);

    /// <summary>Whether frames differ over time, so the view must redraw on a display link.</summary>
    public bool AnimatesOnHostTime => Layers.Any(layer => !layer.IsHidden && layer.Items.Any(item =>
        (item.AnimationContext is not null && item.AnimationSteps.Count > 0)
        || (item.Render.Content is ItemContent.Text && item.Scroll is { Speed: > 0 })
        || (item.Render.Content is ItemContent.Text { Styled: var text } && PathTextLayout.IsMoving(text))
        || item.Effects.Any(e => e.VariesWithTime)));

    public AnimatedScene Copy()
    {
        var copy = new AnimatedScene(CanvasSize, Background);
        foreach (var layer in Layers)
        {
            copy[layer.Kind].IsHidden = layer.IsHidden;
            copy[layer.Kind].Items.AddRange(layer.Items);
        }
        return copy;
    }

    /// <summary>Wraps a built scene; <paramref name="animate"/> attaches each item's animation (default: none).</summary>
    public static AnimatedScene From(RenderScene scene, Func<RenderItem, LayerKind, AnimatedItem>? animate = null)
    {
        var animated = new AnimatedScene(scene.CanvasSize, scene.Background);
        foreach (var layer in scene.Layers)
            foreach (var item in layer.Items)
                animated.AddItem(animate?.Invoke(item, layer.Kind) ?? item, layer.Kind);
        return animated;
    }
}

/// <summary>A soft-edged reveal from one edge; <see cref="Progress"/> 0 = hidden, 1 = whole.</summary>
public readonly record struct MotionWipe(SceneAnimationEdge Edge, double Progress, double Feather);

/// <summary>The visible window in the item's own unit square (u across, v down), with feathered edges.</summary>
public sealed record MotionClip
{
    public double MinU { get; init; }
    public double MaxU { get; init; } = 1;
    public double MinV { get; init; }
    public double MaxV { get; init; } = 1;
    public double FeatherMinU { get; init; }
    public double FeatherMaxU { get; init; }
    public double FeatherMinV { get; init; }
    public double FeatherMaxV { get; init; }
}

/// <summary>
/// The transform the renderer applies to an item's drawn quad this frame. Port of AnimationMotion.
/// Translate is in canvas units; Scale (and ScaleY when set) scale about the frame's centre;
/// Tilt/Swing/Keystone/Skew are the totals (the item's own pose included), so draw with these
/// rather than <see cref="RenderItem.SkewX"/>/<see cref="RenderItem.SkewY"/>.
/// </summary>
public sealed record AnimationMotion
{
    public SceneVector Translate { get; init; }
    public double Scale { get; init; } = 1;

    /// <summary>A separate vertical scale (text morphs between frame sizes); null = <see cref="Scale"/>.</summary>
    public double? ScaleY { get; init; }
    public double Tilt { get; init; }
    public double Swing { get; init; }
    public TiltPivot TiltPivot { get; init; } = TiltPivot.Center;
    public double KeystoneTop { get; init; } = 1;
    public double KeystoneBottom { get; init; } = 1;
    public bool KeystoneStretch { get; init; }
    public double SkewX { get; init; }
    public double SkewY { get; init; }
    public MotionWipe? Wipe { get; init; }
    public MotionClip? Clip { get; init; }

    public bool UsesStretchKeystone => KeystoneStretch && (KeystoneTop != 1 || KeystoneBottom != 1);

    public static readonly AnimationMotion Identity = new();

    public bool IsIdentity =>
        Translate == SceneVector.Zero && Scale == 1 && (ScaleY ?? 1) == 1 && Tilt == 0 && Swing == 0
        && KeystoneTop == 1 && KeystoneBottom == 1 && SkewX == 0 && SkewY == 0 && Wipe is null && Clip is null;
}

/// <summary>One piece to draw this frame: the item as it now looks, and how to move it.</summary>
public sealed record ResolvedItem(AnimatedItem Item, AnimationMotion Motion);

public sealed record ResolvedLayer(LayerKind Kind, bool IsHidden, IReadOnlyList<ResolvedItem> Items);

/// <summary>A scene evaluated at one instant. Port of ResolvedBuildScene.</summary>
public sealed record ResolvedScene(
    SceneSize CanvasSize,
    SceneColor Background,
    IReadOnlyList<ResolvedLayer> Layers,
    IReadOnlyDictionary<string, AnimationMotion> Motions)
{
    public ResolvedLayer this[LayerKind kind] => Layers.First(layer => layer.Kind == kind);
}
