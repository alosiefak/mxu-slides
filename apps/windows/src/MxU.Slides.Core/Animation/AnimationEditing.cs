using System.Globalization;
using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;
using static MxU.Slides.Core.Animation.AnimationSequence;

namespace MxU.Slides.Core.Model
{
    /// <summary>
    /// Something whose objects carry animation steps, played in one order: a slide,
    /// or an overlay (which the Mac edits as a one-slide composition).
    /// </summary>
    public interface IAnimatedComposition
    {
        List<SlideObject> Objects { get; }

        /// <summary>Step ids in play order; null (or empty) means document order.</summary>
        List<string>? AnimationOrder { get; set; }
    }

    public sealed partial class Slide : IAnimatedComposition;
    public sealed partial class Overlay : IAnimatedComposition;
}

namespace MxU.Slides.Core.Animation
{
    /// <summary>
    /// Editing a composition's animation steps: adding an effect to objects,
    /// changing, re-timing, reordering and removing steps, and the words the
    /// editor shows for each ("Slides in from left", "0.5s · after 0.2s").
    /// The non-UI half of the Mac's SlideEditorModel+Animations.swift and the
    /// display names in AnimationInspector.swift. Morph ("Change") steps can be
    /// re-timed and removed here but not created: their end state is drawn on
    /// the Mac's canvas, which Windows doesn't have yet.
    /// </summary>
    public static class AnimationEditing
    {
        // MARK: Finding steps

        /// <summary>A step and the object it belongs to, or null.</summary>
        public static (string ObjectId, AnimationStep Step)? Find(IAnimatedComposition composition, string stepId) =>
            OrderedEntries(composition.Objects, composition.AnimationOrder).FirstOrDefault(e => e.Step.Id == stepId) is { Step: not null } entry
                ? (entry.ObjectId, entry.Step)
                : null;

        // MARK: Adding

        /// <summary>
        /// A new step with the Mac's starting settings: half a second (a second for a
        /// morph), sliding and wiping from the left, growing from 95%, and an emphasis
        /// nudge 20 up.
        /// </summary>
        public static AnimationStep FreshStep(AnimationKind kind, StepAnimation animation, AnimationTrigger trigger, List<AnimationRange>? ranges = null)
        {
            var step = new AnimationStep
            {
                Id = Guid.NewGuid().ToString().ToUpperInvariant(),
                Kind = kind,
                Animation = animation,
                Trigger = trigger,
                DurationSeconds = kind == AnimationKind.Morph ? 1.0 : 0.5,
                Ranges = ranges,
            };
            switch (animation)
            {
                case StepAnimation.Move:
                    step.Edge = kind == AnimationKind.Emphasis ? null : AnimationEdge.Left;
                    break;
                case StepAnimation.Scale:
                    step.FromScale = kind == AnimationKind.Emphasis ? null : 0.95;
                    break;
                case StepAnimation.Wipe:
                    step.Edge = AnimationEdge.Left;
                    break;
            }
            if (animation == StepAnimation.Move && kind == AnimationKind.Emphasis)
            {
                step.OffsetX = 0;
                step.OffsetY = -20;
            }
            return step;
        }

        /// <summary>
        /// Gives each object (in document order) a fresh step. The first takes
        /// <paramref name="trigger"/>; the rest play with it unless the trigger
        /// already chains (With/After). Returns the new step ids.
        /// </summary>
        public static List<string> AddStep(IAnimatedComposition composition, IEnumerable<string> objectIds, AnimationKind kind, StepAnimation animation, AnimationTrigger trigger)
        {
            var wanted = objectIds.ToHashSet(StringComparer.Ordinal);
            var newIds = new List<string>();
            var position = 0;
            foreach (var o in composition.Objects.Where(o => wanted.Contains(o.Id)))
            {
                var stepTrigger = position == 0 || trigger is AnimationTrigger.WithPrevious or AnimationTrigger.AfterPrevious
                    ? trigger
                    : AnimationTrigger.WithPrevious;
                var step = FreshStep(kind, animation, stepTrigger);
                o.AnimationSteps = [.. o.AnimationSteps ?? [], step];
                newIds.Add(step.Id);
                AppendToOrder(composition, step.Id);
                position++;
            }
            return newIds;
        }

        /// <summary>
        /// The effect gallery's one-click add: an In or emphasis starts on a click, an
        /// Out on the exit. Given a <paramref name="slot"/>, the new steps then move into
        /// that group (the end of it), taking its trigger. Morphs aren't made here.
        /// </summary>
        public static List<string> QuickAdd(IAnimatedComposition composition, IEnumerable<string> objectIds, AnimationKind kind, StepAnimation animation, GroupSlot? slot = null)
        {
            if (kind == AnimationKind.Morph) return [];
            var trigger = kind == AnimationKind.Out ? AnimationTrigger.OnDismiss : AnimationTrigger.OnClick;
            var ids = AddStep(composition, objectIds, kind, animation, trigger);
            if (slot is { } into)
                foreach (var id in ids) MoveIntoGroup(composition, id, into, int.MaxValue);
            return ids;
        }

        private static void AppendToOrder(IAnimatedComposition composition, string id)
        {
            if (composition.AnimationOrder is not { Count: > 0 } order) return;
            composition.AnimationOrder = [.. order, id];
        }

        // MARK: Changing

        /// <summary>Changes one step in place; false when there is no such step.</summary>
        public static bool Update(IAnimatedComposition composition, string stepId, Action<AnimationStep> change)
        {
            foreach (var o in composition.Objects)
            {
                if (o.AnimationSteps?.FirstOrDefault(s => s.Id == stepId) is not { } step) continue;
                change(step);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Swaps a step's effect (Slide for Fade, In for Out…), keeping its timing and
        /// resetting the settings that belong to the old effect. Not for morphs.
        /// </summary>
        public static bool ReplaceEffect(IAnimatedComposition composition, string stepId, AnimationKind kind, StepAnimation animation)
        {
            if (kind == AnimationKind.Morph) return false;
            return Update(composition, stepId, step =>
            {
                var fresh = FreshStep(kind, animation, step.Trigger, step.Ranges);
                step.Kind = kind;
                step.Animation = animation;
                step.Edge = fresh.Edge;
                step.OffsetX = fresh.OffsetX;
                step.OffsetY = fresh.OffsetY;
                step.FromScale = fresh.FromScale;
                step.Amount = null;
                step.SoftEdge = null;
                step.DrawStart = null;
                step.Reverse = null;
                step.ColorHex = null;
                step.ToObject = null;
                if (kind != AnimationKind.In) step.FromObject = null;
            });
        }

        /// <summary>
        /// Removes a step. If it led its group (a click or the exit), the step that
        /// chained off it takes over its trigger so the group keeps starting the same way.
        /// </summary>
        public static bool Remove(IAnimatedComposition composition, string stepId)
        {
            if (Find(composition, stepId) is not { } found) return false;
            if (Promotion(stepId, composition.Objects, composition.AnimationOrder) is { } promotion)
                Update(composition, promotion.StepId, s => s.Trigger = promotion.Trigger);
            if (composition.Objects.FirstOrDefault(o => o.Id == found.ObjectId) is { } owner && owner.AnimationSteps is { } steps)
            {
                steps.RemoveAll(s => s.Id == stepId);
                if (steps.Count == 0) owner.AnimationSteps = null;
            }
            if (composition.AnimationOrder is { } order)
            {
                var kept = order.Where(id => id != stepId).ToList();
                composition.AnimationOrder = kept.Count == 0 ? null : kept;
            }
            return true;
        }

        /// <summary>Moves a step to just before another in the play order (or to the end); its trigger stays.</summary>
        public static bool MoveBefore(IAnimatedComposition composition, string stepId, string? beforeId)
        {
            var before = OrderedEntries(composition.Objects, composition.AnimationOrder).Select(e => e.Step.Id).ToList();
            var ids = before.ToList();
            var source = ids.IndexOf(stepId);
            if (source < 0) return false;
            ids.RemoveAt(source);
            var target = beforeId is null ? -1 : ids.IndexOf(beforeId);
            ids.Insert(target < 0 ? ids.Count : target, stepId);
            if (ids.SequenceEqual(before)) return false;
            composition.AnimationOrder = ids;
            return true;
        }

        /// <summary>
        /// Moves a step into a group (when it goes up, a click, the exit) at a position,
        /// taking that group's trigger; the step it leaves behind is promoted as in <see cref="Remove"/>.
        /// </summary>
        public static bool MoveIntoGroup(IAnimatedComposition composition, string stepId, GroupSlot slot, int position)
        {
            if (GroupedMove(composition.Objects, composition.AnimationOrder, stepId, slot, position) is not { } move) return false;
            var promotion = Promotion(stepId, composition.Objects, composition.AnimationOrder);
            composition.AnimationOrder = move.Order;
            var triggers = new Dictionary<string, AnimationTrigger>(move.Triggers);
            if (promotion is { } p && !triggers.ContainsKey(p.StepId)) triggers[p.StepId] = p.Trigger;
            foreach (var (id, trigger) in triggers) Update(composition, id, s => s.Trigger = trigger);
            return true;
        }

        // MARK: Words for the editor

        /// <summary>"Name", or "Name · “the words”" for a step on part of the text.</summary>
        public static string TargetLabel(SlideObject? o, AnimationStep step)
        {
            if (o is null) return "—";
            var name = string.IsNullOrEmpty(o.Name) ? KindName(o.ObjectKind) : o.Name;
            if (step.Ranges is not { Count: > 0 } ranges) return name;
            var text = string.Join(" … ", ranges.Select(r => RangeText(r, o.Text)));
            var clipped = text.CharacterCount() > 24 ? text[..text.Utf16Offset(24)] + "…" : text;
            return $"{name} · “{clipped}”";
        }

        private static string KindName(SlideObjectKind kind) => kind switch
        {
            SlideObjectKind.Text => "Text",
            SlideObjectKind.Media => "Media",
            SlideObjectKind.LiveInput => "Live Input",
            _ => "Shape",
        };

        /// <summary>What a step does, in a few words: "Slides in from left", "Fades out", "Pulses".</summary>
        public static string Sentence(AnimationStep step, SlideObject? o = null)
        {
            var edge = step.Edge?.DisplayName().ToLowerInvariant();
            switch (step.Kind)
            {
                case AnimationKind.In:
                    if (step.FromObject is not null) return "Arrives from its custom start";
                    return step.Animation switch
                    {
                        StepAnimation.Fade => "Fades in",
                        StepAnimation.Move => "Slides in" + (edge is null ? "" : $" from {edge}"),
                        StepAnimation.Scale => "Grows in",
                        StepAnimation.Wipe => "Wipes in" + (edge is null ? "" : $" from {edge}"),
                        StepAnimation.Blur => "Blurs in",
                        StepAnimation.Burn => "Burns in",
                        StepAnimation.Glitch => "Glitches in",
                        StepAnimation.Draw => "Draws in",
                        StepAnimation.Type => "Types in",
                        _ => "Appears",
                    };
                case AnimationKind.Out:
                    return step.Animation switch
                    {
                        StepAnimation.Fade => "Fades out",
                        StepAnimation.Move => "Slides out" + (edge is null ? "" : $" to {edge}"),
                        StepAnimation.Scale => "Shrinks out",
                        StepAnimation.Wipe => "Wipes out" + (edge is null ? "" : $" to {edge}"),
                        StepAnimation.Blur => "Blurs out",
                        StepAnimation.Burn => "Burns out",
                        StepAnimation.Glitch => "Glitches out",
                        StepAnimation.Draw => "Erases",
                        StepAnimation.Type => "Un-types",
                        _ => "Disappears",
                    };
                case AnimationKind.Emphasis:
                    return step.Animation switch
                    {
                        StepAnimation.Pulse => "Pulses",
                        StepAnimation.Color => "Flashes color",
                        StepAnimation.Scale => "Grows and settles",
                        StepAnimation.Move => "Nudges",
                        StepAnimation.Fade => "Dims and returns",
                        _ => "Emphasizes",
                    };
                default:
                    var verb = AnimationTimelineLayout.MorphMoves(step, o) ? "Morphs" : "Changes";
                    return step.Animation switch
                    {
                        StepAnimation.Blur => $"{verb} (blur)",
                        StepAnimation.Burn => $"{verb} (burn)",
                        StepAnimation.Glitch => $"{verb} (glitch)",
                        _ => verb,
                    };
            }
        }

        /// <summary>"0.5s", "0.5s · after 0.2s" or "0.5s · 0.2s early".</summary>
        public static string TimingLabel(AnimationStep step)
        {
            var parts = new List<string> { Seconds(step.DurationSeconds) };
            var delay = step.DelaySeconds ?? 0;
            if (delay < 0) parts.Add($"{Seconds(-delay)} early");
            else if (delay > 0) parts.Add($"after {Seconds(delay)}");
            return string.Join(" · ", parts);
        }

        private static string Seconds(double value) => value.ToString("0.0", CultureInfo.InvariantCulture) + "s";

        /// <summary>What the delay box means for this step's trigger.</summary>
        public static string DelayHint(AnimationStep step, bool startsOnItsOwn) => step.Trigger switch
        {
            AnimationTrigger.OnClick => "Seconds after the Advance.",
            AnimationTrigger.OnDismiss => "Seconds after the final click or clear.",
            AnimationTrigger.WithPrevious => startsOnItsOwn
                ? "Nothing before it, so it starts this long after the slide fires. No click needed."
                : "Seconds after the previous step starts. Negative starts sooner.",
            _ => startsOnItsOwn
                ? "Nothing before it, so it starts this long after the slide fires. No click needed."
                : "Seconds after the previous step ends. Negative overlaps it.",
        };

        /// <summary>The ramp a step uses when it doesn't name one: Ins ease out, Outs ease in, the rest both.</summary>
        public static AnimationRamp EffectiveRamp(AnimationStep step) => step.Ramp ?? step.Kind switch
        {
            AnimationKind.In => AnimationRamp.Out,
            AnimationKind.Out => AnimationRamp.In,
            _ => AnimationRamp.Both,
        };

        /// <summary>
        /// Problems worth flagging on an overlay's steps: the sequence's own warnings,
        /// plus anything but an Out waiting for a click, since an overlay never gets
        /// one (a clicked Out still plays when it is taken down).
        /// </summary>
        public static Dictionary<string, string> OverlayWarnings(IAnimatedComposition composition)
        {
            var warnings = Warnings(composition.Objects, composition.AnimationOrder);
            foreach (var entry in Groups(composition.Objects, composition.AnimationOrder).Click.SelectMany(c => c))
                if (entry.Step.Kind != AnimationKind.Out && !warnings.ContainsKey(entry.Step.Id))
                    warnings[entry.Step.Id] = "Waits for a click, which an overlay never gets, so it never plays. Choose With, After or Final.";
            return warnings;
        }

        /// <summary>The effects offered for a kind of step (the Mac's gallery tiles).</summary>
        public static IReadOnlyList<StepAnimation> Choices(AnimationKind kind) => kind switch
        {
            AnimationKind.Emphasis => [StepAnimation.Pulse, StepAnimation.Color, StepAnimation.Scale, StepAnimation.Move, StepAnimation.Fade],
            AnimationKind.Morph => [StepAnimation.Fade, StepAnimation.Blur, StepAnimation.Burn, StepAnimation.Glitch],
            _ => [StepAnimation.Fade, StepAnimation.Move, StepAnimation.Scale, StepAnimation.Wipe, StepAnimation.Blur, StepAnimation.Burn, StepAnimation.Glitch, StepAnimation.Draw, StepAnimation.Type],
        };

        /// <summary>A gallery tile's title: "Slide" for a move, "Grow", "Dim"…</summary>
        public static string TileTitle(StepAnimation animation, AnimationKind kind) => (kind, animation) switch
        {
            (AnimationKind.Morph, StepAnimation.Fade) => "Move & restyle",
            (AnimationKind.Morph, StepAnimation.Blur) => "Change with blur",
            (AnimationKind.Morph, StepAnimation.Burn) => "Change with burn",
            (AnimationKind.Morph, StepAnimation.Glitch) => "Change with glitch",
            (_, StepAnimation.Move) => "Slide",
            (AnimationKind.Emphasis, StepAnimation.Scale) => "Grow & settle",
            (_, StepAnimation.Scale) => "Grow",
            (AnimationKind.Emphasis, StepAnimation.Fade) => "Dim",
            _ => animation.DisplayName(),
        };
    }

    /// <summary>The editor's speed presets for a step's duration.</summary>
    public enum AnimationSpeed { Quick, Normal, Slow, Custom }

    /// <summary>The names the editor shows for animation settings (AnimationInspector.swift).</summary>
    public static class AnimationNames
    {
        public static double? Seconds(this AnimationSpeed speed) => speed switch
        {
            AnimationSpeed.Quick => 0.3,
            AnimationSpeed.Normal => 0.5,
            AnimationSpeed.Slow => 1.0,
            _ => null,
        };

        /// <summary>The preset a duration matches, or Custom.</summary>
        public static AnimationSpeed SpeedOf(double seconds) =>
            Enum.GetValues<AnimationSpeed>().FirstOrDefault(s => s.Seconds() is { } v && Math.Abs(v - seconds) < 0.001, AnimationSpeed.Custom);

        public static string DisplayName(this AnimationKind kind) => kind switch
        {
            AnimationKind.In => "In",
            AnimationKind.Out => "Out",
            AnimationKind.Emphasis => "Emphasis",
            _ => "Morph",
        };

        public static string DisplayName(this StepAnimation animation) => animation switch
        {
            StepAnimation.Fade => "Fade",
            StepAnimation.Move => "Move",
            StepAnimation.Scale => "Scale",
            StepAnimation.Wipe => "Wipe",
            StepAnimation.Blur => "Blur",
            StepAnimation.Burn => "Burn",
            StepAnimation.Glitch => "Glitch",
            StepAnimation.Draw => "Draw",
            StepAnimation.Type => "Type",
            StepAnimation.Pulse => "Pulse",
            _ => "Color",
        };

        public static string DisplayName(this AnimationTrigger trigger) => trigger switch
        {
            AnimationTrigger.OnClick => "On Click",
            AnimationTrigger.WithPrevious => "With Previous",
            AnimationTrigger.AfterPrevious => "After Previous",
            _ => "On Exit",
        };

        public static string ShortName(this AnimationTrigger trigger) => trigger switch
        {
            AnimationTrigger.OnClick => "Click",
            AnimationTrigger.WithPrevious => "With",
            AnimationTrigger.AfterPrevious => "After",
            _ => "Final",
        };

        public static string DisplayName(this AnimationRamp ramp) => ramp switch
        {
            AnimationRamp.None => "None",
            AnimationRamp.In => "In",
            AnimationRamp.Out => "Out",
            _ => "Both",
        };

        public static string DisplayName(this AnimationEdge edge) => edge switch
        {
            AnimationEdge.Left => "Left",
            AnimationEdge.Right => "Right",
            AnimationEdge.Top => "Top",
            _ => "Bottom",
        };
    }
}
