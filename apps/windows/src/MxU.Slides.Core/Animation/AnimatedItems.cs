using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Animation;

/// <summary>
/// The scene-building half of animation: composing a slide's objects with its
/// design (donation, paging, stack, schedule) and attaching the scheduled steps,
/// clock and morph/enter states to the render items SlideSceneBuilder made.
/// Port of the animation parts of SlideSceneBuilder.scene(for:) and renderItems(for:…animationSteps:).
/// </summary>
public static class AnimatedItems
{
    /// <param name="Stack">Render these in order, as SlideSceneBuilder.AddSlideObjects does (theme decor uses <c>entry.Id</c>).</param>
    /// <param name="Placeholders">Each slide text object's placeholder.</param>
    /// <param name="Steps">Scheduled steps by object id.</param>
    public sealed record SlideComposition(
        IReadOnlyList<SlideSceneBuilder.ComposedEntry> Stack,
        IReadOnlyDictionary<string, SlideObject> Placeholders,
        IReadOnlyDictionary<string, List<SceneAnimationStep>> Steps,
        IReadOnlyList<string>? Order,
        int PageCount)
    {
        public IReadOnlyList<SceneAnimationStep> StepsFor(string objectId) => Steps.TryGetValue(objectId, out var steps) ? steps : [];
    }

    /// <summary>Donates placeholder steps, pages overflowing text (with a pager), composes the stack and schedules it.</summary>
    public static SlideComposition Compose(
        IReadOnlyList<SlideObject> objects, Slide? template, Theme? theme, IReadOnlyList<string>? slideOrder,
        SceneSize canvas, TextPager? pager = null, Paging.Turn? pageTurn = null)
    {
        var donated = PlaceholderAnimations.Compose(objects, template, slideOrder);
        var paged = Paging.Paged(donated.Objects, template, theme, donated.Order, canvas, pager, pageTurn);
        var stack = SlideSceneBuilder.ComposedStack(paged.Objects, template);
        return new SlideComposition(
            stack,
            SlideSceneBuilder.PlaceholderAssignments(paged.Objects, template),
            AnimationSequence.SceneSteps(stack.Select(e => e.Object).ToList(), paged.Order),
            paged.Order,
            paged.PageCount);
    }

    /// <summary>
    /// The render items one object produced (<see cref="SlideSceneBuilder.RenderItems"/>), each carrying the
    /// object's pose, the scheduled <paramref name="steps"/> and the clock. Morph steps get the end state
    /// (<see cref="SceneAnimationStep.ToItem"/>) and custom-start Ins the start pose (<see cref="SceneAnimationStep.FromItem"/>),
    /// matched shape-to-shape and text-to-text by the <c>::text</c> suffix.
    /// </summary>
    public static List<AnimatedItem> For(
        SlideObject source, IReadOnlyList<RenderItem> items, IReadOnlyList<SceneAnimationStep> steps,
        Theme? theme = null, SlideObject? placeholder = null, SceneSize? canvas = null,
        AnimationContext? context = null, double? tickerAnchorHostTime = null)
    {
        if (items.Count == 0) return [];
        var o = SlideObjectNormalization.Normalized(source);
        var baseId = items[0].Id;
        var states = new Dictionary<string, List<AnimatedItem>>();
        foreach (var step in steps)
        {
            if (StateObject(o, step) is not { } stateObject) continue;
            var state = MxuJson.Clone(SlideObjectNormalization.Normalized(stateObject));
            state.Id = o.Id;
            state.AnimationSteps = null;
            states[step.Id] = SlideSceneBuilder.RenderItems(state, theme, placeholder, canvas, baseId)
                .Select(item => Posed(item, state, placeholder))
                .ToList();
        }
        return items
            .Select(item => Stamped(
                Posed(item, o, placeholder) with { AnimationSteps = steps, AnimationContext = context, TickerAnchorHostTime = tickerAnchorHostTime },
                states))
            .ToList();
    }

    /// <summary>The look at its peak, for thumbnails: no leaves and no page turns. Port of SlideSceneBuilder.peakLook.</summary>
    public static AnimatedScene PeakLook(AnimatedScene scene)
    {
        var peak = scene.Copy();
        foreach (var layer in peak.Layers)
        {
            for (var index = 0; index < layer.Items.Count; index++)
            {
                var item = layer.Items[index];
                layer.Items[index] = item with
                {
                    AnimationSteps = item.AnimationSteps.Where(s => s.Kind != SceneAnimationKind.Exit && !s.Id.Contains(Paging.StepMarker)).ToList(),
                };
            }
        }
        return peak;
    }

    /// <summary>A render item with the object's 3-D pose and block scroll, which <see cref="RenderItem"/> doesn't carry.</summary>
    public static AnimatedItem Posed(RenderItem item, SlideObject o, SlideObject? placeholder = null)
    {
        var shapeText = item.Id.EndsWith("::text", StringComparison.Ordinal);
        var scroll = shapeText
            ? o.TextStyle?.Scroll
            : o.TextStyle?.Scroll ?? (placeholder?.Id == o.Id ? null : placeholder?.TextStyle?.Scroll);
        return new AnimatedItem
        {
            Render = item,
            Tilt = o.Tilt ?? 0,
            Swing = o.Swing ?? 0,
            TiltPivot = o.TiltPivot ?? TiltPivot.Center,
            KeystoneTop = (o.KeystoneTop ?? 100) / 100,
            KeystoneBottom = (o.KeystoneBottom ?? 100) / 100,
            KeystoneStretch = o.KeystoneMode == KeystoneMode.Stretch,
            EffectsApplyBelow = !shapeText && (o.EffectsApplyBelow ?? false),
            // A shape's text box draws plain: the effects belong to the shape around it.
            Effects = shapeText ? [] : AnimationEffect.From(o.Effects),
            Scroll = item.Content is ItemContent.Text && scroll is not null ? BlockScroll(scroll) : null,
        };
    }

    public static SceneBlockScroll? BlockScroll(BlockScroll scroll)
    {
        if (scroll.Speed <= 0) return null;
        return new SceneBlockScroll(
            scroll.Speed,
            scroll.Axis switch
            {
                BlockScrollAxis.Down => SceneScrollAxis.Down,
                BlockScrollAxis.Left => SceneScrollAxis.Left,
                BlockScrollAxis.Right => SceneScrollAxis.Right,
                _ => SceneScrollAxis.Up,
            },
            Math.Max(scroll.Passes ?? 0, 0),
            scroll.Ramp switch
            {
                AnimationRamp.In => SceneAnimationRamp.EaseIn,
                AnimationRamp.Out => SceneAnimationRamp.EaseOut,
                AnimationRamp.Both => SceneAnimationRamp.EaseInOut,
                _ => SceneAnimationRamp.None,
            },
            scroll.RestAtEnd ?? false,
            Math.Clamp(scroll.FadeTowardTop ?? 0, 0, 1));
    }

    private static SlideObject? StateObject(SlideObject o, SceneAnimationStep step)
    {
        if (o.AnimationSteps?.FirstOrDefault(s => s.Id == step.Id) is not { } source) return null;
        return step.Kind switch
        {
            SceneAnimationKind.Morph => source.ToObject,
            SceneAnimationKind.Enter => source.FromObject,
            _ => null,
        };
    }

    private static AnimatedItem Stamped(AnimatedItem item, Dictionary<string, List<AnimatedItem>> states)
    {
        if (states.Count == 0) return item;
        var wantsText = item.Id.EndsWith("::text", StringComparison.Ordinal);
        return item with
        {
            AnimationSteps = item.AnimationSteps.Select(step =>
            {
                if (!states.TryGetValue(step.Id, out var candidates)
                    || candidates.FirstOrDefault(c => c.Id.EndsWith("::text", StringComparison.Ordinal) == wantsText) is not { } match)
                    return step;
                return step.Kind == SceneAnimationKind.Morph ? step with { ToItem = match } : step with { FromItem = match };
            }).ToList(),
        };
    }
}
