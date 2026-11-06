using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;
using Mode = MxU.Slides.Core.Animation.AnimationPresetApplication.Mode;

namespace MxU.Slides.Core.Library;

/// <summary>
/// Kinetic text on a theme's text placeholder: the words of every slide that uses the design arrive
/// line by line or word by word. A placeholder's own words are only a sample, so the pieces can't be
/// steps aimed at its characters (as on an overlay). Instead:
/// <list type="bullet">
/// <item>the shared theme gets ordinary whole-text steps (the preset's Ins and Outs on the whole
/// placeholder), which a Mac plays as they are: the words simply rise in together;</item>
/// <item>the kinetic build (how to cut the words, the stagger, the In steps each piece plays) is kept
/// as a Windows extra beside the theme (<see cref="KineticExtras"/>), saved against the placeholder's
/// new steps, and played over each slide's own words (<see cref="KineticText.Donate"/>).</item>
/// </list>
/// The caller saves the theme first and then the build (its fingerprint is of the steps saved).
/// Windows-only: the worship looks are built this way (StarterPacks.Worship.cs).
/// </summary>
public static class ThemeKinetics
{
    /// <summary>The stagger one step per line or word plays with: a beat between lines, less between words.</summary>
    public static double StaggerFor(KineticSplit split) => split == KineticSplit.Words ? 0.12 : 0.3;

    /// <summary>
    /// Puts a kinetic preset on a placeholder of a design: whole-text steps in the theme, and the build
    /// to keep beside it. Replace takes the placeholder's old steps out (of the play order too); Add
    /// keeps them. Returns the new steps' ids and the build.
    /// </summary>
    public static (List<string> Added, KineticBuild Build) Apply(IAnimatedComposition design, SlideObject placeholder, KineticPreset preset, Mode mode,
        Func<string>? makeId = null)
    {
        makeId ??= () => Guid.NewGuid().ToString().ToUpperInvariant();
        var steps = new List<AnimationStep>();
        foreach (var template in preset.In.Concat(preset.Out))
        {
            var step = MxuJson.Clone(template);
            step.Id = makeId();
            step.Ranges = null;
            steps.Add(step);
        }
        var added = Put(design, placeholder, steps, mode);
        return (added, new KineticBuild(preset.Split, preset.Stagger, [.. preset.In.Select(MxuJson.Clone)]));
    }

    /// <summary>
    /// One step per line or word on a placeholder: one whole-text In in the theme (with the slide
    /// unless the placeholder's first In says otherwise), and a build that plays it on each piece in
    /// turn. Returns the new step's id and the build.
    /// </summary>
    public static (List<string> Added, KineticBuild Build) PerPiece(IAnimatedComposition design, SlideObject placeholder, KineticSplit split,
        StepAnimation animation = StepAnimation.Fade, Func<string>? makeId = null)
    {
        makeId ??= () => Guid.NewGuid().ToString().ToUpperInvariant();
        var trigger = placeholder.AnimationSteps?.FirstOrDefault(s => s.Kind == AnimationKind.In)?.Trigger ?? AnimationTrigger.WithPrevious;
        var template = AnimationEditing.FreshStep(AnimationKind.In, animation, trigger);
        template.Id = makeId();
        var lead = MxuJson.Clone(template);
        var kept = (placeholder.AnimationSteps ?? []).Where(s => s.Kind != AnimationKind.In).ToList();
        // The old Ins go (the build takes their place); Outs and emphasis stay.
        var dropped = (placeholder.AnimationSteps ?? []).Where(s => s.Kind == AnimationKind.In).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        placeholder.AnimationSteps = [lead, .. kept];
        if (design.AnimationOrder is { } order)
        {
            var next = order.Where(id => !dropped.Contains(id)).ToList();
            var at = next.FindIndex(id => kept.Any(k => k.Id == id));
            next.Insert(at < 0 ? next.Count : at, lead.Id);
            design.AnimationOrder = next;
        }
        // The build's first piece starts when the design's step does (KineticText.Donate), the rest a stagger apart.
        return ([lead.Id], new KineticBuild(split, StaggerFor(split), [template]));
    }

    private static List<string> Put(IAnimatedComposition design, SlideObject placeholder, List<AnimationStep> steps, Mode mode)
    {
        if (mode == Mode.Replace && design.AnimationOrder is { } order)
        {
            var dropped = (placeholder.AnimationSteps ?? []).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            design.AnimationOrder = [.. order.Where(s => !dropped.Contains(s))];
        }
        List<AnimationStep> all = [.. mode == Mode.Replace ? [] : placeholder.AnimationSteps ?? [], .. steps];
        placeholder.AnimationSteps = all.Count == 0 ? null : all;
        if (design.AnimationOrder is { Count: > 0 } existing) design.AnimationOrder = [.. existing, .. steps.Select(s => s.Id)];
        if (design.AnimationOrder is { Count: 0 }) design.AnimationOrder = null;
        return [.. steps.Select(s => s.Id)];
    }

    /// <summary>"Lines rise in one after another", for the editor's note about a placeholder's build.</summary>
    public static string Describe(KineticBuild build) =>
        build.In.Count == 0 ? "Its words appear as the shared steps say."
            : $"{(build.Split switch { KineticSplit.Words => "Each word", KineticSplit.Lines => "Each line", _ => "The text" })} "
              + $"{AnimationEditing.Sentence(build.In[0]).ToLowerInvariant()}{(build.Split == KineticSplit.Whole ? "" : $", {build.Stagger:0.##} s apart")}.";
}
