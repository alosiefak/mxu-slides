using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Animation;

/// <summary>A named set of presets in the gallery ("Settle": In, Out, In + Out).</summary>
public sealed record AnimationPresetGroup(string Name, IReadOnlyList<AnimationPreset> Presets);

/// <summary>
/// The animation recipes the app ships, grouped as the Mac's presets menu shows them: Settle,
/// Float, Slide, Bar Wipe and the rest. A preset is a list of step templates (an In, an Out, or
/// both), plus for Roll and Crawl a block scroll. Line-for-line port of the Mac's
/// PresenterCore/Library/AnimationPresetCatalog.swift (<c>AnimationPreset.recommendedGroups</c>).
/// The Windows-only kinetic text presets are in <see cref="KineticPresets"/>.
/// </summary>
public static class AnimationPresetCatalog
{
    private static AnimationStep Step(
        AnimationKind kind, StepAnimation animation, AnimationTrigger trigger,
        double duration = 0.5, double? delay = null, AnimationRamp? ramp = null, bool? withFade = null,
        AnimationEdge? edge = null, double? offsetX = null, double? offsetY = null, double? fromScale = null,
        double? amount = null, double? softEdge = null, bool? cursor = null, double? drawStart = null) => new()
    {
        Id = "preset", Kind = kind, Animation = animation, Trigger = trigger,
        DelaySeconds = delay, DurationSeconds = duration, Ramp = ramp, WithFade = withFade,
        Edge = edge, OffsetX = offsetX, OffsetY = offsetY, FromScale = fromScale,
        Amount = amount, SoftEdge = softEdge, Cursor = cursor, DrawStart = drawStart,
    };

    private static AnimationPreset Preset(string id, string name, List<AnimationStep> steps, double? stagger = null,
        BlockScroll? scroll = null, double? tilt = null) => new()
    {
        Id = id, Name = name, Steps = steps, StaggerSeconds = stagger, Scroll = scroll, Tilt = tilt,
    };

    /// <summary>An In, an Out and both together, as one group (the Mac's <c>pair</c>).</summary>
    private static AnimationPresetGroup Pair(string id, string name, AnimationStep enter, AnimationStep exit, double stagger = 0.15) =>
        new(name,
        [
            Preset($"builtin.{id}.in", "In", [enter], stagger),
            Preset($"builtin.{id}.out", "Out", [exit], stagger),
            Preset($"builtin.{id}.both", "In + Out", [MxuJson.Clone(enter), MxuJson.Clone(exit)], stagger),
        ]);

    public static IReadOnlyList<AnimationPresetGroup> RecommendedGroups { get; } =
    [
        Pair("settle", "Settle",
            Step(AnimationKind.In, StepAnimation.Scale, AnimationTrigger.OnClick, withFade: true, fromScale: 0.95),
            Step(AnimationKind.Out, StepAnimation.Scale, AnimationTrigger.OnDismiss, duration: 0.4, withFade: true, fromScale: 0.95)),

        Pair("float", "Float",
            Step(AnimationKind.In, StepAnimation.Move, AnimationTrigger.OnClick, withFade: true, offsetX: 0, offsetY: 40),
            Step(AnimationKind.Out, StepAnimation.Move, AnimationTrigger.OnDismiss, duration: 0.4, withFade: true, offsetX: 0, offsetY: -40)),

        new("Slide",
        [
            Preset("builtin.slide.left", "From Left", [Step(AnimationKind.In, StepAnimation.Move, AnimationTrigger.OnClick, edge: AnimationEdge.Left)]),
            Preset("builtin.slide.right", "From Right", [Step(AnimationKind.In, StepAnimation.Move, AnimationTrigger.OnClick, edge: AnimationEdge.Right)]),
            Preset("builtin.slide.bottom", "From Bottom", [Step(AnimationKind.In, StepAnimation.Move, AnimationTrigger.OnClick, edge: AnimationEdge.Bottom)]),
            Preset("builtin.slide.top", "From Top", [Step(AnimationKind.In, StepAnimation.Move, AnimationTrigger.OnClick, edge: AnimationEdge.Top)]),
            Preset("builtin.slide.leftBoth", "From Left, Out Left",
            [
                Step(AnimationKind.In, StepAnimation.Move, AnimationTrigger.OnClick, edge: AnimationEdge.Left),
                Step(AnimationKind.Out, StepAnimation.Move, AnimationTrigger.OnDismiss, duration: 0.4, edge: AnimationEdge.Left),
            ]),
        ]),

        Pair("barWipe", "Bar Wipe",
            Step(AnimationKind.In, StepAnimation.Wipe, AnimationTrigger.OnClick, duration: 0.6, edge: AnimationEdge.Left, softEdge: 40),
            Step(AnimationKind.Out, StepAnimation.Wipe, AnimationTrigger.OnDismiss, duration: 0.5, edge: AnimationEdge.Right, softEdge: 40)),

        new("Line Draw", [Preset("builtin.lineDraw", "Line Draw", [Step(AnimationKind.In, StepAnimation.Draw, AnimationTrigger.OnClick, duration: 1.0, drawStart: 0)])]),

        new("Pill Grow",
        [
            Preset("builtin.pillGrow", "Pill Grow",
            [
                Step(AnimationKind.In, StepAnimation.Scale, AnimationTrigger.OnClick, duration: 0.4, ramp: AnimationRamp.Out, withFade: true, fromScale: 0.6),
                Step(AnimationKind.Emphasis, StepAnimation.Pulse, AnimationTrigger.AfterPrevious, duration: 0.3, amount: 1.06),
            ]),
        ]),

        new("Bracket Pop",
        [
            Preset("builtin.bracketPop", "Bracket Pop",
                [Step(AnimationKind.In, StepAnimation.Scale, AnimationTrigger.OnClick, duration: 0.3, ramp: AnimationRamp.Out, withFade: true, fromScale: 1.2)]),
        ]),

        Pair("blurResolve", "Blur Resolve",
            Step(AnimationKind.In, StepAnimation.Blur, AnimationTrigger.OnClick, duration: 0.9, withFade: true, amount: 32),
            Step(AnimationKind.Out, StepAnimation.Blur, AnimationTrigger.OnDismiss, duration: 0.7, withFade: true, amount: 32)),

        Pair("burn", "Burn",
            Step(AnimationKind.In, StepAnimation.Burn, AnimationTrigger.OnClick, duration: 1.2, amount: 0.7),
            Step(AnimationKind.Out, StepAnimation.Burn, AnimationTrigger.OnDismiss, duration: 1.0, amount: 0.7)),

        Pair("glitch", "Glitch",
            Step(AnimationKind.In, StepAnimation.Glitch, AnimationTrigger.OnClick, duration: 0.9, amount: 0.9),
            Step(AnimationKind.Out, StepAnimation.Glitch, AnimationTrigger.OnDismiss, duration: 0.8, amount: 0.9)),

        new("Type",
        [
            Preset("builtin.type", "Type", [Step(AnimationKind.In, StepAnimation.Type, AnimationTrigger.OnClick, duration: 1.2, cursor: true)]),
            Preset("builtin.type.frame", "Frame, then Type",
            [
                Step(AnimationKind.In, StepAnimation.Draw, AnimationTrigger.OnClick, duration: 0.8, drawStart: 0),
                Step(AnimationKind.In, StepAnimation.Type, AnimationTrigger.AfterPrevious, duration: 1.2, delay: -0.3, cursor: true),
            ]),
        ]),

        new("Roll",
        [
            Preset("builtin.roll", "Roll (credits)", [],
                scroll: new BlockScroll { Axis = BlockScrollAxis.Up, Speed = 60, Passes = 1, Ramp = AnimationRamp.Both, RestAtEnd = false }),
            Preset("builtin.roll.rest", "Roll, rest on last lines", [],
                scroll: new BlockScroll { Axis = BlockScrollAxis.Up, Speed = 60, Passes = 1, Ramp = AnimationRamp.Both, RestAtEnd = true }),
        ]),
        new("Crawl",
        [
            Preset("builtin.crawl", "Crawl", [],
                scroll: new BlockScroll { Axis = BlockScrollAxis.Up, Speed = 50, Passes = 1, Ramp = AnimationRamp.None, FadeTowardTop = 0.35 },
                tilt: 35),
        ]),
    ];

    /// <summary>Every recommended preset, in menu order.</summary>
    public static IReadOnlyList<AnimationPreset> Recommended { get; } = [.. RecommendedGroups.SelectMany(g => g.Presets)];
}

/// <summary>
/// Putting a preset onto an object, and turning an object's steps back into a recipe to save.
/// Port of the Mac's <c>AnimationPresetApplication</c> (AnimationPresetCatalog.swift).
/// </summary>
public static class AnimationPresetApplication
{
    /// <summary>Replace the object's steps, or add the preset's after them.</summary>
    public enum Mode { Replace, Add }

    /// <param name="AnimationSteps">The object's steps afterwards (null for none).</param>
    /// <param name="AddedIds">The new steps' ids, in order (to append to the play order).</param>
    /// <param name="Scroll">A block scroll the preset brings (Roll, Crawl), for a text object.</param>
    /// <param name="Tilt">The tilt that goes with it (Crawl).</param>
    public sealed record Result(List<AnimationStep>? AnimationSteps, List<string> AddedIds, BlockScroll? Scroll, double? Tilt);

    /// <summary>
    /// The object's steps with the preset applied. <paramref name="position"/> is the object's place
    /// among those it's applied to at once: the first (0) plays as the preset says; each one after
    /// it follows the first (its lead step rides With Previous) and starts a stagger later, and its
    /// Outs stagger the same way, so a row of objects ripples in and out.
    /// </summary>
    public static Result Apply(AnimationPreset preset, IReadOnlyList<AnimationStep>? existing, Mode mode, int position, Func<string>? makeId = null)
    {
        makeId ??= () => Guid.NewGuid().ToString().ToUpperInvariant();
        var stagger = preset.StaggerSeconds ?? 0.15;
        var steps = new List<AnimationStep>();
        for (var index = 0; index < preset.Steps.Count; index++)
        {
            var template = preset.Steps[index];
            var step = MxuJson.Clone(template);
            step.Id = makeId();
            step.Ranges = null;
            if (position > 0)
            {
                if (index == 0 && template.Trigger == AnimationTrigger.OnClick) step.Trigger = AnimationTrigger.WithPrevious;
                if (index == 0 || template.Trigger == AnimationTrigger.OnDismiss)
                    step.DelaySeconds = (template.DelaySeconds ?? 0) + stagger * position;
            }
            steps.Add(step);
        }
        var kept = mode == Mode.Replace ? [] : existing ?? [];
        List<AnimationStep> all = [.. kept, .. steps];
        return new Result(all.Count == 0 ? null : all, [.. steps.Select(s => s.Id)], preset.Scroll is null ? null : MxuJson.Clone(preset.Scroll), preset.Tilt);
    }

    /// <summary>An object's steps as a recipe to save: no ranges (they belong to that text) and no morph poses.</summary>
    public static List<AnimationStep> Recipe(IReadOnlyList<AnimationStep>? animationSteps) =>
        [.. (animationSteps ?? []).Select(step =>
        {
            var s = MxuJson.Clone(step);
            s.Ranges = null;
            s.ToObject = null;
            s.FromObject = null;
            return s;
        })];

    /// <summary>
    /// Applies a preset to objects of a composition (a slide or an overlay), in the order given:
    /// the first plays as the preset says and the rest stagger after it. Replacing takes the
    /// object's old steps out of the play order; the new ones join its end. A Roll or Crawl sets a
    /// text object's block scroll (and Crawl its tilt). Port of the Mac's
    /// SlideEditorModel.applyAnimationPreset. Returns the new steps' ids.
    /// </summary>
    public static List<string> ApplyTo(IAnimatedComposition composition, IEnumerable<string> objectIds, AnimationPreset preset, Mode mode,
        Func<string>? makeId = null)
    {
        var added = new List<string>();
        var position = 0;
        foreach (var id in objectIds)
        {
            if (composition.Objects.FirstOrDefault(o => o.Id == id) is not { } o) continue;
            var result = Apply(preset, o.AnimationSteps, mode, position, makeId);
            if (mode == Mode.Replace && composition.AnimationOrder is { } order)
            {
                var dropped = (o.AnimationSteps ?? []).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
                composition.AnimationOrder = [.. order.Where(s => !dropped.Contains(s))];
            }
            o.AnimationSteps = result.AnimationSteps;
            if (composition.AnimationOrder is { Count: > 0 } existingOrder) composition.AnimationOrder = [.. existingOrder, .. result.AddedIds];
            added.AddRange(result.AddedIds);
            if (result.Scroll is { } scroll && o.ObjectKind == SlideObjectKind.Text)
            {
                o.TextStyle ??= new TextStyle();
                o.TextStyle.Scroll = scroll;
            }
            if (result.Tilt is { } tilt) o.Tilt = tilt;
            position++;
        }
        if (composition.AnimationOrder is { Count: 0 }) composition.AnimationOrder = null;
        return added;
    }
}

/// <summary>A named set of effect presets ("Frost": Light, Heavy).</summary>
public sealed record EffectPresetGroup(string Name, IReadOnlyList<EffectPreset> Presets);

/// <summary>
/// The picture-effect chains the app ships: Frost, Static, Heat Haze, Long Trails, Amoeba and
/// Ghosty. Port of the Mac's PresenterCore/Library/EffectPresetCatalog.swift.
/// </summary>
public static class EffectPresetCatalog
{
    private static EffectPreset Preset(string id, string name, params Effect[] effects) => new() { Id = id, Name = name, Effects = [.. effects] };

    public static IReadOnlyList<EffectPresetGroup> RecommendedGroups { get; } =
    [
        new("Frost",
        [
            Preset("builtin.frost.light", "Light", new Effect { EffectKind = EffectKind.Scatter, Amount = 12, Scale = 1, Speed = 0, Smooth = 1 }),
            Preset("builtin.frost.heavy", "Heavy", new Effect { EffectKind = EffectKind.Scatter, Amount = 100, Scale = 1, Speed = 0, Smooth = 1 }),
        ]),
        new("Static", [Preset("builtin.static", "Static", new Effect { EffectKind = EffectKind.Scatter, Amount = 8, Scale = 1, Speed = 1, Smooth = 0 })]),
        new("Heat Haze", [Preset("builtin.heatHaze", "Heat Haze", new Effect { EffectKind = EffectKind.Warp, Amount = 40, Scale = 240, Speed = 0.5 })]),
        new("Long Trails", [Preset("builtin.longTrails", "Long Trails", new Effect { EffectKind = EffectKind.GhostTrails, Amount = 3, Scale = 240, Speed = 0, Fade = 2 })]),
        new("Amoeba",
        [
            Preset("builtin.amoeba", "Amoeba",
                new Effect { EffectKind = EffectKind.GhostTrails, Amount = 6, Scale = 240, Speed = 0, Fade = 1.5 },
                new Effect { EffectKind = EffectKind.Warp, Amount = 300, Scale = 240, Speed = 0 },
                new Effect { EffectKind = EffectKind.Echo, Amount = 20 },
                new Effect { EffectKind = EffectKind.Scatter, Amount = 5.5, Scale = 1, Speed = 0, Smooth = 1 }),
        ]),
        new("Ghosty",
        [
            Preset("builtin.ghosty", "Ghosty",
                new Effect { EffectKind = EffectKind.GhostTrails, Amount = 6, Scale = 160, Speed = 0, Fade = 1.5 },
                new Effect { EffectKind = EffectKind.Echo, Amount = 6.9 },
                new Effect { EffectKind = EffectKind.Scatter, Amount = 5.5, Scale = 1, Speed = 0, Smooth = 1 }),
        ]),
    ];

    public static IReadOnlyList<EffectPreset> Recommended { get; } = [.. RecommendedGroups.SelectMany(g => g.Presets)];

    /// <summary>
    /// An object's effect chain with a preset applied: Apply replaces the chain, Add puts the
    /// preset's effects after it (the Mac's EffectPresetsMenu apply/add). An empty chain is none
    /// at all, and then "apply to content below" goes too.
    /// </summary>
    public static void ApplyTo(SlideObject o, EffectPreset preset, AnimationPresetApplication.Mode mode)
    {
        var chain = preset.Effects.Select(MxuJson.Clone).ToList();
        o.Effects = mode == AnimationPresetApplication.Mode.Replace ? (chain.Count == 0 ? null : chain) : [.. o.Effects ?? [], .. chain];
        if (o.Effects is null or { Count: 0 })
        {
            o.Effects = null;
            o.EffectsApplyBelow = null;
        }
    }
}
