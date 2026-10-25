using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Animation;

/// <summary>How a kinetic preset cuts a text into pieces that arrive in turn.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<KineticSplit>))]
public enum KineticSplit
{
    /// <summary>The whole object moves as one.</summary>
    Whole,

    /// <summary>Each line arrives on its own, one after another.</summary>
    Lines,

    /// <summary>Each word arrives on its own.</summary>
    Words,
}

/// <summary>
/// A kinetic text recipe: the In steps every piece plays (a rise and a fade, a blur…), how the text
/// is cut into pieces, and how far apart the pieces start. The Out is for the whole object at once:
/// words leaving one by one reads as fussy, so they go together.
/// </summary>
/// <param name="Stagger">Seconds between one piece starting and the next.</param>
public sealed record KineticPreset(string Id, string Name, string Description, KineticSplit Split, double Stagger,
    IReadOnlyList<AnimationStep> In, IReadOnlyList<AnimationStep> Out);

/// <summary>
/// What a kinetic build is, stored beside a theme for one of its text placeholders (a Windows
/// extra, see <see cref="Library.KineticExtras"/>). The shared theme keeps a whole-object build the
/// Mac plays as it is; on Windows, the placeholder's In steps are replaced by these, played piece
/// by piece over each slide's own words.
/// </summary>
public sealed record KineticBuild(KineticSplit Split, double Stagger, List<AnimationStep> In);

/// <summary>
/// Kinetic text: words or lines arriving one after another, each with its own rise, blur or wipe.
/// <para>
/// It is built only from what the shared format already has: one ordinary step per piece, each
/// with a <c>ranges</c> naming its line or word (the Mac's own "one step per word"), timed to
/// start a stagger after the one before. So an overlay or slide animated this way opens on the
/// Mac and plays the same there.
/// </para>
/// The presets themselves (<see cref="KineticPresets"/>) are Windows-only; the Mac has its own
/// recommended set (<see cref="AnimationPresetCatalog"/>), which Windows offers too.
/// </summary>
public static class KineticText
{
    /// <summary>
    /// Where <see cref="PlaceholderAnimations.Compose"/> finds a theme placeholder's kinetic build.
    /// The app points it at its library's Windows extras (<see cref="Library.KineticExtras.For"/>);
    /// left null (in tests, and in effect on the Mac) there are none, and every design plays the
    /// whole-text build its shared document describes.
    /// </summary>
    public static Func<SlideObject, KineticBuild?>? BuildFor { get; set; }

    /// <summary>The pieces of a text, in reading order: one range for the whole of it, or per line, or per word.</summary>
    public static List<AnimationRange?> Pieces(string text, KineticSplit split) => split switch
    {
        KineticSplit.Lines => [.. AnimationRanges.WholeText(text)],
        KineticSplit.Words => [.. AnimationRanges.Words(AnimationRanges.WholeText(text), text)],
        _ => [null],
    };

    /// <summary>
    /// The In steps of a build, played over the pieces of <paramref name="text"/>: every In
    /// template for the first piece, then for the second, and so on. The first piece's lead step
    /// keeps the template's trigger and delay (so the build starts when the design says); each
    /// later piece's lead starts <paramref name="stagger"/> after the one before (With Previous),
    /// and the steps that go with it keep their own timing relative to it. Even a single word
    /// gets its own ranged step, so typing more words later can split it (<see cref="Reflow"/>);
    /// an empty box plays the templates once, as a whole.
    /// </summary>
    public static List<AnimationStep> Expand(IReadOnlyList<AnimationStep> inTemplates, string text, KineticSplit split, double stagger, Func<string> makeId)
    {
        var pieces = Pieces(text, split);
        if (pieces.Count == 0) pieces = [null];
        // "With Previous" counts from the step just before in the play order, which for a later
        // piece is the previous piece's last step, not its lead. So each lead's delay takes off how
        // far that last step started after its own lead, and the leads stay a stagger apart.
        var lastOffset = LastStartOffset(inTemplates);
        var steps = new List<AnimationStep>();
        for (var p = 0; p < pieces.Count; p++)
        {
            for (var t = 0; t < inTemplates.Count; t++)
            {
                var step = MxuJson.Clone(inTemplates[t]);
                step.Id = makeId();
                step.Ranges = pieces[p] is { } range ? [new AnimationRange { Line = range.Line, Column = range.Column, Length = range.Length }] : null;
                if (p > 0 && t == 0)
                {
                    step.Trigger = AnimationTrigger.WithPrevious;
                    step.DelaySeconds = Math.Round(stagger - lastOffset, 4);
                }
                steps.Add(step);
            }
        }
        return steps;
    }

    /// <summary>
    /// Keeps word-by-word and line-by-line builds in step with their words. Such a build is one
    /// ranged step (or a few) per word or line, and a range names characters: when someone types a
    /// new name into a lower third's field, the ranges still point at the old words, and the new
    /// ones would appear all at once, or half of a word with the rest. So for each text object
    /// whose build no longer covers exactly its words (or lines), the build is made again over the
    /// words it has now: the same steps, the same stagger, the same start, the old steps' ids kept
    /// for the pieces that are still there. A build counts if it starts at the text's first word and
    /// every piece plays the same steps; anything else (steps on a few chosen words) is left alone.
    /// Returns true when something changed. The Mac re-anchors ranges as you edit in its editor, and
    /// drops them when the whole text is typed over; this is Windows' own, so fields stay animated.
    /// </summary>
    public static bool Reflow(IAnimatedComposition composition, Func<string>? makeId = null)
    {
        makeId ??= () => Guid.NewGuid().ToString().ToUpperInvariant();
        var changed = false;
        foreach (var o in composition.Objects.Where(o => o.ObjectKind == SlideObjectKind.Text && o.AnimationSteps is { Count: > 0 }))
        {
            var steps = o.AnimationSteps!;
            static bool Ranged(AnimationStep s) => s.Ranges is { Count: 1 } && s.Kind is AnimationKind.In or AnimationKind.Emphasis;
            var first = steps.FindIndex(Ranged);
            if (first < 0) continue;
            var end = first;
            while (end < steps.Count && Ranged(steps[end])) end++;
            var block = steps.GetRange(first, end - first);

            // The pieces: runs of steps on the same range.
            var pieces = new List<List<AnimationStep>>();
            foreach (var step in block)
            {
                if (pieces.Count > 0 && SameRange(pieces[^1][0].Ranges![0], step.Ranges![0])) pieces[^1].Add(step);
                else pieces.Add([step]);
            }
            var shape = pieces[0].Select(s => (s.Kind, s.Animation)).ToList();
            if (pieces.Any(p => !p.Select(s => (s.Kind, s.Animation)).SequenceEqual(shape))) continue;
            var lead = pieces[0][0].Ranges![0];
            if (lead.Line != 0 || lead.Column != 0) continue;

            var split = pieces.All(p => p[0].Ranges![0].Column == 0) && pieces.Select(p => p[0].Ranges![0].Line).Distinct().Count() == pieces.Count
                && pieces.Count > 1 ? KineticSplit.Lines : KineticSplit.Words;
            var now = Pieces(o.Text, split).OfType<AnimationRange>().ToList();
            if (now.Count == 0) continue;
            if (now.Count == pieces.Count && now.Zip(pieces).All(pair => SameRange(pair.First, pair.Second[0].Ranges![0]))) continue;

            var templates = pieces[0].Select(s =>
            {
                var t = MxuJson.Clone(s);
                t.Ranges = null;
                return t;
            }).ToList();
            var stagger = pieces.Count > 1
                ? (pieces[1][0].DelaySeconds ?? 0) + LastStartOffset(templates)
                : split == KineticSplit.Lines ? 0.2 : 0.08;
            // The old pieces' ids for the pieces still there, fresh ones after.
            var oldIds = pieces.SelectMany(p => p.Select(s => s.Id)).ToList();
            var index = 0;
            var rebuilt = Expand(templates, o.Text, split, Math.Round(stagger, 4), () => index < oldIds.Count ? oldIds[index++] : makeId());
            steps.RemoveRange(first, block.Count);
            steps.InsertRange(first, rebuilt);

            if (composition.AnimationOrder is { Count: > 0 } order)
            {
                var keep = rebuilt.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
                var dropped = oldIds.Where(id => !keep.Contains(id)).ToHashSet(StringComparer.Ordinal);
                var list = order.Where(id => !dropped.Contains(id)).ToList();
                var added = rebuilt.Select(s => s.Id).Where(id => !list.Contains(id)).ToList();
                var after = list.FindLastIndex(id => keep.Contains(id));
                list.InsertRange(after < 0 ? list.Count : after + 1, added);
                composition.AnimationOrder = list;
            }
            changed = true;
        }
        return changed;
    }

    private static bool SameRange(AnimationRange a, AnimationRange b) => a.Line == b.Line && a.Column == b.Column && a.Length == b.Length;

    /// <summary>When the last template starts, in seconds after the first (as AnimationSequence times them).</summary>
    private static double LastStartOffset(IReadOnlyList<AnimationStep> templates)
    {
        if (templates.Count == 0) return 0;
        double start = 0, end = Math.Max(templates[0].DurationSeconds, 0);
        for (var t = 1; t < templates.Count; t++)
        {
            var delay = templates[t].DelaySeconds ?? 0;
            start = templates[t].Trigger == AnimationTrigger.AfterPrevious ? end + delay : start + delay;
            end = start + Math.Max(templates[t].DurationSeconds, 0);
        }
        return start;
    }

    /// <summary>
    /// A kinetic preset on objects of a slide or an overlay, in the order given. Text objects get
    /// one In per piece; other objects (a plate, a shape) play the In as a whole. Each object after
    /// the first starts a little later (a stagger per object, as the Mac's presets do), and Outs are
    /// for the whole object. Replace drops the object's old steps (and their places in the play
    /// order); Add keeps them. Returns the new steps' ids, in play order.
    /// </summary>
    public static List<string> ApplyTo(IAnimatedComposition composition, IEnumerable<string> objectIds, KineticPreset preset,
        AnimationPresetApplication.Mode mode, Func<string>? makeId = null)
    {
        makeId ??= () => Guid.NewGuid().ToString().ToUpperInvariant();
        var added = new List<string>();
        var position = 0;
        foreach (var id in objectIds)
        {
            if (composition.Objects.FirstOrDefault(o => o.Id == id) is not { } o) continue;
            var split = o.ObjectKind == SlideObjectKind.Text ? preset.Split : KineticSplit.Whole;
            var steps = Expand(preset.In, o.Text, split, preset.Stagger, makeId);
            if (position > 0 && steps.Count > 0)
            {
                // Followers ride with the first object, a beat later each (AnimationPresetApplication's stagger).
                if (steps[0].Trigger == AnimationTrigger.OnClick) steps[0].Trigger = AnimationTrigger.WithPrevious;
                steps[0].DelaySeconds = (steps[0].DelaySeconds ?? 0) + 0.15 * position;
            }
            foreach (var template in preset.Out)
            {
                var step = MxuJson.Clone(template);
                step.Id = makeId();
                step.Ranges = null;
                if (position > 0 && template.Trigger == AnimationTrigger.OnDismiss) step.DelaySeconds = (template.DelaySeconds ?? 0) + 0.15 * position;
                steps.Add(step);
            }
            if (mode == AnimationPresetApplication.Mode.Replace && composition.AnimationOrder is { } order)
            {
                var dropped = (o.AnimationSteps ?? []).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
                composition.AnimationOrder = [.. order.Where(s => !dropped.Contains(s))];
            }
            List<AnimationStep> all = [.. mode == AnimationPresetApplication.Mode.Replace ? [] : o.AnimationSteps ?? [], .. steps];
            o.AnimationSteps = all.Count == 0 ? null : all;
            if (composition.AnimationOrder is { Count: > 0 } existing) composition.AnimationOrder = [.. existing, .. steps.Select(s => s.Id)];
            added.AddRange(steps.Select(s => s.Id));
            position++;
        }
        if (composition.AnimationOrder is { Count: 0 }) composition.AnimationOrder = null;
        return added;
    }

    /// <summary>The widest a theme's build spreads (first piece to last) on a long slide, in seconds, unless four pieces already spread wider.</summary>
    public const double LongTextSpread = 0.8;

    /// <summary>
    /// The stagger a theme's build plays with over a slide's words. A design is made for a slide of
    /// a few lines; a long one (a whole hymn verse pasted in) would take seconds to arrive at that
    /// pace while the room is already singing. So after the fourth piece the pieces squeeze closer:
    /// the build never spreads wider than four pieces would, or <see cref="LongTextSpread"/>.
    /// </summary>
    public static double StaggerFor(KineticBuild build, string text)
    {
        var count = Pieces(text, build.Split).Count;
        if (count <= 4 || build.Stagger <= 0) return build.Stagger;
        var widest = Math.Max(build.Stagger * 3, LongTextSpread);
        return Math.Round(Math.Min(build.Stagger, widest / (count - 1)), 4);
    }

    /// <summary>
    /// A theme placeholder's donated steps with a kinetic build in place of its Ins, over the slide's
    /// own words (<paramref name="text"/>). <paramref name="donated"/> are the steps already copied
    /// for the slide's text (ids <c>objectId::stepId</c>, no ranges); <paramref name="templateIds"/>
    /// their ids in the design. The build's steps take the place of the first In; the other Ins go.
    /// Returns the new steps and, for each design step id, the ids now standing for it (what
    /// <see cref="PlaceholderAnimations.Compose"/> expands the design's play order with).
    /// </summary>
    public static (List<AnimationStep> Steps, Dictionary<string, List<string>> Ids) Donate(
        KineticBuild build, string text, IReadOnlyList<AnimationStep> donated, IReadOnlyList<string> templateIds)
    {
        var firstIn = donated.ToList().FindIndex(s => s.Kind == AnimationKind.In);
        var ids = new Dictionary<string, List<string>>();
        if (firstIn < 0 || build.In.Count == 0)
        {
            for (var i = 0; i < donated.Count; i++) ids[templateIds[i]] = [donated[i].Id];
            return (donated.ToList(), ids);
        }
        var lead = donated[firstIn];
        var counter = 0;
        var expanded = Expand(build.In, text, build.Split, StaggerFor(build, text), () => $"{lead.Id}::kinetic{counter++}");
        // The build starts when the design's own In did.
        expanded[0].Trigger = lead.Trigger;
        expanded[0].DelaySeconds = lead.DelaySeconds;
        var steps = new List<AnimationStep>();
        for (var i = 0; i < donated.Count; i++)
        {
            if (i == firstIn)
            {
                steps.AddRange(expanded);
                ids[templateIds[i]] = [.. expanded.Select(s => s.Id)];
            }
            else if (donated[i].Kind == AnimationKind.In)
            {
                ids[templateIds[i]] = [];
            }
            else
            {
                steps.Add(donated[i]);
                ids[templateIds[i]] = [donated[i].Id];
            }
        }
        return (steps, ids);
    }
}

/// <summary>
/// The kinetic text presets Windows adds to the overlay editor's presets: words rising in turn,
/// a typewriter, a soft blur, a light sweep… All are ordinary steps (moves, fades, blurs, wipes,
/// types, pulses and colour flashes) with ranges, so the Mac plays what they make.
/// Windows-only.
/// </summary>
public static class KineticPresets
{
    private static AnimationStep In(StepAnimation animation, double duration, AnimationTrigger trigger = AnimationTrigger.OnClick,
        double? delay = null, AnimationRamp? ramp = AnimationRamp.Out, bool? fade = null, AnimationEdge? edge = null, double? dx = null,
        double? dy = null, double? fromScale = null, double? amount = null, double? softEdge = null, bool? cursor = null,
        AnimationKind kind = AnimationKind.In, string? color = null) => new()
    {
        Id = "kinetic", Kind = kind, Animation = animation, Trigger = trigger, DelaySeconds = delay, DurationSeconds = duration,
        Ramp = ramp, WithFade = fade, Edge = edge, OffsetX = dx ?? (dy is null ? null : 0), OffsetY = dy ?? (dx is null ? null : 0),
        FromScale = fromScale, Amount = amount, SoftEdge = softEdge, Cursor = cursor, ColorHex = color,
    };

    private static AnimationStep Out(StepAnimation animation, double duration, bool? fade = null, AnimationEdge? edge = null,
        double? dx = null, double? dy = null, double? fromScale = null, double? amount = null, double? softEdge = null) => new()
    {
        Id = "kinetic", Kind = AnimationKind.Out, Animation = animation, Trigger = AnimationTrigger.OnDismiss, DurationSeconds = duration,
        Ramp = AnimationRamp.In, WithFade = fade, Edge = edge, OffsetX = dx ?? (dy is null ? null : 0), OffsetY = dy ?? (dx is null ? null : 0),
        FromScale = fromScale, Amount = amount, SoftEdge = softEdge,
    };

    private static readonly AnimationStep FadeOut = Out(StepAnimation.Fade, 0.45);

    public static IReadOnlyList<KineticPreset> All { get; } =
    [
        new("kinetic.wordRise", "Word by word: rise", "Each word floats up into place, a beat after the one before.",
            KineticSplit.Words, 0.08, [In(StepAnimation.Move, 0.6, fade: true, dy: 34)], [FadeOut]),
        new("kinetic.lineRise", "Line by line: rise", "Each line rises in on its own: the classic worship build.",
            KineticSplit.Lines, 0.22, [In(StepAnimation.Move, 0.75, fade: true, dy: 48)], [Out(StepAnimation.Move, 0.45, fade: true, dy: -24)]),
        // One In per piece: two Ins on the same words would play together, but the Mac's editor
        // flags the second as "already shown". An emphasis after the In is fine (light sweep, wave).
        new("kinetic.softBlur", "Soft blur in", "The words come gently into focus: for prayer and reflective songs.",
            KineticSplit.Whole, 0, [In(StepAnimation.Blur, 1.1, fade: true, amount: 26)], [Out(StepAnimation.Blur, 0.6, fade: true, amount: 18)]),
        new("kinetic.wordBlur", "Word by word: blur in", "Each word resolves out of a blur in turn.",
            KineticSplit.Words, 0.09, [In(StepAnimation.Blur, 0.65, fade: true, amount: 20)], [Out(StepAnimation.Blur, 0.5, fade: true, amount: 16)]),
        new("kinetic.lineBlur", "Line by line: blur in", "Each line comes into focus, one after another.",
            KineticSplit.Lines, 0.24, [In(StepAnimation.Blur, 0.8, fade: true, amount: 22)], [Out(StepAnimation.Blur, 0.5, fade: true, amount: 16)]),
        new("kinetic.typewriter", "Typewriter", "Typed out letter by letter, with a cursor.",
            KineticSplit.Whole, 0, [In(StepAnimation.Type, 1.4, ramp: null, cursor: true)], [FadeOut]),
        new("kinetic.scalePop", "Scale pop", "Pops up from small and settles with a little bounce.",
            KineticSplit.Whole, 0,
            [In(StepAnimation.Scale, 0.42, fade: true, fromScale: 0.7), In(StepAnimation.Pulse, 0.3, AnimationTrigger.AfterPrevious, -0.05, AnimationRamp.Both, amount: 1.05, kind: AnimationKind.Emphasis)],
            [Out(StepAnimation.Scale, 0.3, fade: true, fromScale: 0.85)]),
        new("kinetic.wipeReveal", "Wipe reveal", "Revealed left to right behind a soft-edged wipe.",
            KineticSplit.Whole, 0, [In(StepAnimation.Wipe, 0.9, edge: AnimationEdge.Left, softEdge: 140)],
            [Out(StepAnimation.Wipe, 0.6, edge: AnimationEdge.Right, softEdge: 140)]),
        new("kinetic.lineWipe", "Line by line: wipe", "Each line wipes on from the left, in turn.",
            KineticSplit.Lines, 0.18, [In(StepAnimation.Wipe, 0.65, edge: AnimationEdge.Left, softEdge: 80)],
            [Out(StepAnimation.Wipe, 0.5, edge: AnimationEdge.Right, softEdge: 80)]),
        new("kinetic.glitch", "Glitch in", "Arrives with a burst of digital glitch.",
            KineticSplit.Whole, 0, [In(StepAnimation.Glitch, 0.8, ramp: null, amount: 0.85)], [Out(StepAnimation.Glitch, 0.7, amount: 0.85)]),
        new("kinetic.burn", "Burn in", "Burns in from the edges, like film catching light.",
            KineticSplit.Whole, 0, [In(StepAnimation.Burn, 1.1, amount: 0.7)], [Out(StepAnimation.Burn, 0.9, amount: 0.7)]),
        new("kinetic.lightSweep", "Light sweep", "Fades in, then a warm light runs across the words.",
            KineticSplit.Words, 0.06,
            [In(StepAnimation.Fade, 0.4), In(StepAnimation.Color, 0.5, AnimationTrigger.WithPrevious, 0.25, AnimationRamp.Both, color: "#FFE9B0FF", kind: AnimationKind.Emphasis)],
            [FadeOut]),
        new("kinetic.wave", "Wave", "Words arrive, then swell in a wave across the line.",
            KineticSplit.Words, 0.07,
            [In(StepAnimation.Fade, 0.35), In(StepAnimation.Pulse, 0.45, AnimationTrigger.WithPrevious, 0.2, AnimationRamp.Both, amount: 1.12, kind: AnimationKind.Emphasis)],
            [FadeOut]),
        new("kinetic.wordDrop", "Word by word: drop", "Each word drops softly into place from above.",
            KineticSplit.Words, 0.07, [In(StepAnimation.Move, 0.55, fade: true, dy: -30)], [FadeOut]),
        new("kinetic.lineSlide", "Line by line: slide", "Lines slide in from the left, one after another.",
            KineticSplit.Lines, 0.16, [In(StepAnimation.Move, 0.6, fade: true, dx: -60)], [Out(StepAnimation.Move, 0.4, fade: true, dx: 40)]),
        new("kinetic.focusPull", "Cinematic focus", "A slow, deep focus pull, for a title that deserves a moment.",
            KineticSplit.Whole, 0, [In(StepAnimation.Blur, 1.8, fade: true, amount: 44)], [Out(StepAnimation.Blur, 0.9, fade: true, amount: 30)]),
        new("kinetic.riseSettle", "Rise and settle", "Rises from below, then settles with the slightest swell.",
            KineticSplit.Whole, 0,
            [In(StepAnimation.Move, 0.75, fade: true, dy: 60), In(StepAnimation.Pulse, 0.4, AnimationTrigger.AfterPrevious, -0.1, AnimationRamp.Both, amount: 1.03, kind: AnimationKind.Emphasis)],
            [FadeOut]),
    ];
}
