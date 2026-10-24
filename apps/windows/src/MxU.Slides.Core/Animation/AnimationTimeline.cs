using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Animation;

/// <summary>
/// Where each step is against an <see cref="AnimationContext"/> at an instant,
/// and what that hides. Port of RenderEngine/AnimationTimeline.swift.
/// </summary>
public static class AnimationTimeline
{
    public static AnimationStepPhase Phase(SceneAnimationStep step, AnimationContext? context, double now)
    {
        if (context is null) return AnimationStepPhase.Done;
        if (context.AutoSettled && step.Group == SceneAnimationGroup.Auto) return AnimationStepPhase.Done;
        if (EffectiveStart(step, context) is not { } groupStart) return AnimationStepPhase.Pending;
        var elapsed = (context.FrozenHostTime ?? now) - groupStart - step.StartOffset;
        if (elapsed < 0) return AnimationStepPhase.Pending;
        if (step.Duration <= 0 || elapsed >= step.Duration) return AnimationStepPhase.Done;
        return AnimationStepPhase.Running(step.Ramp.Apply(elapsed / step.Duration));
    }

    /// <summary>A clicked Out that was never clicked plays at the dismiss instead.</summary>
    internal static double? EffectiveStart(SceneAnimationStep step, AnimationContext context)
    {
        if (context.Start(step.Group) is { } start) return start;
        if (step.Kind == SceneAnimationKind.Exit && step.Group.IsClick && context.DismissHostTime is { } dismiss) return dismiss;
        return null;
    }

    public static AnimationVisibility Visibility(IReadOnlyList<SceneAnimationStep> steps, AnimationContext? context, double now)
    {
        if (context is null) return AnimationVisibility.Shown;
        var hidesWhole = IsHidden(steps.Where(s => s.TargetsWholeObject).ToList(), context, now);

        var byRange = new Dictionary<SceneAnimationRange, List<SceneAnimationStep>>();
        var order = new List<SceneAnimationRange>();
        foreach (var step in steps.Where(s => !s.TargetsWholeObject))
        {
            foreach (var range in step.Ranges ?? [])
            {
                if (!byRange.TryGetValue(range, out var list))
                {
                    byRange[range] = list = [];
                    order.Add(range);
                }
                list.Add(step);
            }
        }
        var hidden = new List<SceneAnimationRange>();
        var placeholders = new List<SceneAnimationRange>();
        foreach (var range in order)
        {
            var rangeSteps = byRange[range];
            if (!IsHidden(rangeSteps, context, now)) continue;
            hidden.Add(range);
            if (rangeSteps.Any(s => s.PlaceholderUnderline)) placeholders.Add(range);
        }
        return new AnimationVisibility(hidesWhole, hidden, placeholders);
    }

    private static bool IsHidden(IReadOnlyList<SceneAnimationStep> steps, AnimationContext? context, double now)
    {
        var gates = steps.Where(s => s.Kind is not (SceneAnimationKind.Emphasis or SceneAnimationKind.Morph)).ToList();
        if (gates.Count == 0) return false;
        (SceneAnimationStep Step, AnimationStepPhase Phase)? latestStarted = null;
        foreach (var step in gates)
        {
            var p = Phase(step, context, now);
            if (p.HasStarted) latestStarted = (step, p);
        }
        if (latestStarted is not { } latest) return gates.Any(s => s.Kind == SceneAnimationKind.Enter);

        var enters = gates.Where(s => s.Kind == SceneAnimationKind.Enter).ToList();
        if (enters.Count > 0 && !enters.Any(s => Phase(s, context, now).HasStarted)) return true;
        return latest.Step.Kind == SceneAnimationKind.Exit && latest.Phase.IsDone;
    }

    public static bool GroupCompleted(SceneAnimationGroup group, IEnumerable<SceneAnimationStep> steps, AnimationContext? context, double now)
    {
        var members = steps.Where(s => s.Group == group).ToList();
        return members.Count == 0 || members.All(s => Phase(s, context, now).IsDone);
    }

    public static double GroupDuration(SceneAnimationGroup group, IEnumerable<SceneAnimationStep> steps) =>
        steps.Where(s => s.Group == group).Select(s => s.StartOffset + s.Duration).DefaultIfEmpty(0).Max();

    /// <summary>
    /// True while the scene changes with time: a step is running, a step's group has
    /// started but it is still waiting out its delay, or something scrolls. A renderer
    /// draws every frame while this holds and can stop once it doesn't (a click re-renders).
    /// </summary>
    /// <param name="tickers">False for a view that shows tickers at rest (a thumbnail): they then don't keep it drawing.</param>
    public static bool IsAnimating(AnimatedScene scene, double now, bool tickers = true) =>
        scene.Layers.Where(l => !l.IsHidden).SelectMany(l => l.Items).Any(item => IsAnimating(item, now, tickers));

    private static bool IsAnimating(AnimatedItem item, double now, bool tickers)
    {
        if (item.Render.Content is ItemContent.Text && item.Scroll is { Speed: > 0 }) return true;
        // A ticker is drawn afresh every frame while it's up, as the Mac does (even once a repeat count runs out).
        if (tickers && item.Render.Content is ItemContent.Text { Styled: var styled } && PathTextLayout.IsMoving(styled)) return true;
        if (item.Effects.Any(e => e.VariesWithTime)) return true;
        if (item.AnimationContext is not { } context) return false;
        foreach (var step in item.AnimationSteps)
        {
            var phase = Phase(step, context, now);
            if (phase.Kind == AnimationPhaseKind.Running) return true;
            if (phase.Kind == AnimationPhaseKind.Pending && context.FrozenHostTime is null && EffectiveStart(step, context) is not null) return true;
        }
        return false;
    }
}
