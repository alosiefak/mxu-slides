using System.Globalization;
using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Animation;

/// <summary>
/// Steps on part of a text: a line, or a word. A range is (line, column, length), counted in
/// characters as the Mac counts them (what you see as one letter, even when it's two UTF-16 units).
/// Port of the Mac's <c>AnimationRanges</c> (PresenterCore/Library/TextStyleRuns.swift), with the
/// editor's "Generate › One Step per Line / per Word" (SlideEditorModel.generateAnimationSteps).
/// <para>
/// The Mac generates from a text selection. Windows' overlay editor has no text selection yet, so
/// it generates from the whole text (<see cref="WholeText"/>).
/// </para>
/// </summary>
public static class AnimationRanges
{
    public enum Generator { Line, Word }

    /// <summary>Each line of the text as one range (empty lines are skipped: there is nothing to animate).</summary>
    public static List<AnimationRange> WholeText(string text) =>
        [.. text.Split('\n').Select((line, index) => new AnimationRange { Line = index, Column = 0, Length = line.CharacterCount() })
            .Where(r => r.Length > 0)];

    /// <summary>The words inside the ranges, each as its own range (runs of non-space characters).</summary>
    public static List<AnimationRange> Words(IEnumerable<AnimationRange> ranges, string text)
    {
        var lines = text.Split('\n');
        var output = new List<AnimationRange>();
        foreach (var range in ranges)
        {
            if (range.Line < 0 || range.Line >= lines.Length) continue;
            var characters = Characters(lines[range.Line]);
            var start = Math.Min(Math.Max(range.Column, 0), characters.Count);
            var end = Math.Min(start + Math.Max(range.Length, 0), characters.Count);
            var index = start;
            while (index < end)
            {
                while (index < end && IsWhitespace(characters[index])) index++;
                var wordStart = index;
                while (index < end && !IsWhitespace(characters[index])) index++;
                if (index > wordStart) output.Add(new AnimationRange { Line = range.Line, Column = wordStart, Length = index - wordStart });
            }
        }
        return output;
    }

    /// <summary>The pieces each generated step covers: the ranges themselves (one per line), or their words.</summary>
    public static List<AnimationRange> Split(IEnumerable<AnimationRange> ranges, Generator generator, string text) => generator switch
    {
        Generator.Line => [.. ranges.Where(r => r.Length > 0)],
        _ => Words(ranges, text),
    };

    /// <summary>The object's steps with these ranges added to its last step (the Mac's ⌥-click), or null when that step has no ranges.</summary>
    public static (List<AnimationStep> AnimationSteps, string StepId)? AppendingToLastStep(IReadOnlyList<AnimationRange> ranges, IReadOnlyList<AnimationStep>? animationSteps)
    {
        if (animationSteps is not { Count: > 0 } || animationSteps[^1].Ranges is not { Count: > 0 }) return null;
        var steps = animationSteps.ToList();
        steps[^1].Ranges = [.. steps[^1].Ranges!, .. ranges];
        return (steps, steps[^1].Id);
    }

    /// <summary>The characters a range covers.</summary>
    public static string Text(AnimationRange range, string text) => AnimationSequence.RangeText(range, text);

    /// <summary>
    /// One new step per line or per word of <paramref name="ranges"/> on an object, appended to its
    /// steps and the play order. Returns the new ids, in order; none when the object isn't there or
    /// there's nothing to split. The Mac gives each its own click; the caller may re-time them
    /// (an overlay never gets a click, so the overlay editor files them into "when it goes up").
    /// </summary>
    public static List<string> Generate(IAnimatedComposition composition, string objectId, IReadOnlyList<AnimationRange> ranges,
        Generator generator, AnimationKind kind, StepAnimation animation, AnimationTrigger trigger)
    {
        if (composition.Objects.FirstOrDefault(o => o.Id == objectId) is not { } o) return [];
        var pieces = Split(ranges, generator, o.Text);
        if (pieces.Count == 0) return [];
        var steps = o.AnimationSteps?.ToList() ?? [];
        var ids = new List<string>();
        foreach (var piece in pieces)
        {
            var step = AnimationEditing.FreshStep(kind, animation, trigger, [piece]);
            steps.Add(step);
            ids.Add(step.Id);
        }
        o.AnimationSteps = steps;
        if (composition.AnimationOrder is { Count: > 0 } order) composition.AnimationOrder = [.. order, .. ids];
        return ids;
    }

    private static List<string> Characters(string line)
    {
        var characters = new List<string>();
        var e = StringInfo.GetTextElementEnumerator(line);
        while (e.MoveNext()) characters.Add(e.GetTextElement());
        return characters;
    }

    private static bool IsWhitespace(string character) => character.Length > 0 && character.All(char.IsWhiteSpace);
}
