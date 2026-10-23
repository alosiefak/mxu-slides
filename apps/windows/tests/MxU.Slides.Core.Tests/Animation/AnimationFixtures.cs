using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Animation;

/// <summary>Builders shared by the animation tests (the Mac tests' private step/object helpers).</summary>
internal static class AnimationFixtures
{
    public static AnimationStep Step(
        string id, AnimationTrigger trigger, AnimationKind kind = AnimationKind.In, StepAnimation animation = StepAnimation.Fade,
        double? delay = null, double duration = 1, List<AnimationRange>? ranges = null, AnimationRamp? ramp = null) => new()
    {
        Id = id, Kind = kind, Animation = animation, Trigger = trigger,
        DelaySeconds = delay, DurationSeconds = duration, Ramp = ramp, Ranges = ranges,
    };

    public static AnimationRange Range(int line, int column, int length) => new() { Line = line, Column = column, Length = length };

    public static SlideObject Text(string id, string text, List<AnimationStep>? steps = null, string? name = null, bool? hidden = null) => new()
    {
        Id = id, ObjectKind = SlideObjectKind.Text, Name = name ?? id, Text = text, Hidden = hidden, AnimationSteps = steps,
    };

    public static List<SlideObject> Objects(params SlideObject[] objects) => [.. objects];

    /// <summary>Every scheduled step of a composition, flattened.</summary>
    public static List<SceneAnimationStep> AllSteps(IReadOnlyList<SlideObject> objects, IReadOnlyList<string>? order = null) =>
        AnimationSequence.SceneSteps(objects, order).Values.SelectMany(s => s).ToList();

    /// <summary>
    /// The slide layer of a scene with animation attached, built the way the Mac's
    /// SlideSceneBuilder.scene(for:…animationContext:) builds it.
    /// </summary>
    public static List<AnimatedItem> SlideItems(
        Slide slide, Theme? theme, AnimationContext? context = null, TextPager? pager = null, SceneSize? canvas = null)
    {
        var size = canvas ?? SlideSceneBuilder.CanvasSize;
        var template = SlideSceneBuilder.ThemeSlide(slide, theme);
        var composition = AnimatedItems.Compose(slide.Objects, template, theme, slide.AnimationOrder, size, pager);
        var items = new List<AnimatedItem>();
        foreach (var entry in composition.Stack)
        {
            var placeholder = entry.FromTheme ? null : composition.Placeholders.GetValueOrDefault(entry.Object.Id);
            var rendered = SlideSceneBuilder.RenderItems(entry.Object, theme, placeholder, size, entry.FromTheme ? entry.Id : null);
            items.AddRange(AnimatedItems.For(entry.Object, rendered, composition.StepsFor(entry.Object.Id), theme, placeholder, size, context));
        }
        return items;
    }
}
