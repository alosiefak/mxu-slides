using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;
using static MxU.Slides.Core.Animation.AnimationSequence;

namespace MxU.Slides.Core.Animation;

/// <summary>
/// What the animation timeline's gestures do to a composition's steps: drag a block along its row
/// (it starts later or sooner, the rest stay put), drag it into another column (it plays on another
/// click), stretch its right edge (longer), drag its left edge (it starts later and ends where it
/// did), link it to the step before (after rather than with), copy it, and delete or move a whole
/// column. And the two steps posed on the canvas: a morph ("Change") with its end state, and an In's
/// custom start ("where it enters from").
/// <para>
/// Port of the Mac's Sources/SlideEditorModel+Animations.swift (<c>placeTimelineStep</c>,
/// <c>resizeTimelineStep</c>, <c>moveTimelineSteps</c>, <c>trimTimelineStepStart</c>,
/// <c>moveTimelineStep</c>, <c>toggleStepLink</c>, <c>duplicateAnimationStep</c>,
/// <c>deleteTimelineColumn</c>, <c>moveTimelineColumn</c>, <c>morphBaseline</c>, <c>addMorph</c>,
/// <c>setInCustomStart</c>), on any slide, theme design or overlay. Times snap to a twentieth of a
/// second, as there. Every function changes the composition in place and returns whether it changed.
/// </para>
/// </summary>
public static class AnimationTimelineEditing
{
    /// <summary>A time on the timeline's grid: twentieths of a second.</summary>
    public static double Snap(double seconds) => Math.Round(seconds * 20, MidpointRounding.AwayFromZero) / 20;

    private static void ApplyDelta(IAnimatedComposition c, string id, AnimationTrigger trigger, double? delay) =>
        AnimationEditing.Update(c, id, s =>
        {
            s.Trigger = trigger;
            s.DelaySeconds = delay;
        });

    private static void Repin(IAnimatedComposition c, IReadOnlyDictionary<string, (double Start, double Duration)> before, IReadOnlySet<string> excluding)
    {
        foreach (var pin in Pins(c.Objects, c.AnimationOrder, before, excluding))
            ApplyDelta(c, pin.StepId, pin.Trigger, pin.DelaySeconds);
    }

    /// <summary>A block dragged along its row: the step starts at <paramref name="offset"/> into its column; the others keep their starts.</summary>
    public static bool PlaceStep(IAnimatedComposition c, string stepId, double offset)
    {
        if (Placement(stepId, Snap(offset), c.Objects, c.AnimationOrder) is not { } delta) return false;
        var before = Schedule(c.Objects, c.AnimationOrder);
        ApplyDelta(c, stepId, delta.Trigger, delta.DelaySeconds);
        Repin(c, before, new HashSet<string>(StringComparer.Ordinal) { stepId });
        return true;
    }

    /// <summary>A block's right edge dragged: the step lasts <paramref name="duration"/> (at least a twentieth); the others keep their starts.</summary>
    public static bool ResizeStep(IAnimatedComposition c, string stepId, double duration)
    {
        if (AnimationEditing.Find(c, stepId) is null) return false;
        var before = Schedule(c.Objects, c.AnimationOrder);
        var value = Math.Max(Snap(duration), 0.05);
        AnimationEditing.Update(c, stepId, s => s.DurationSeconds = value);
        Repin(c, before, new HashSet<string>(StringComparer.Ordinal) { stepId });
        return true;
    }

    /// <summary>Several blocks dragged together by <paramref name="delta"/> seconds (never before their column's start).</summary>
    public static bool MoveSteps(IAnimatedComposition c, IReadOnlySet<string> ids, double delta)
    {
        if (ids.Count == 0 || delta == 0) return false;
        var old = Schedule(c.Objects, c.AnimationOrder);
        var headroom = ids.Where(old.ContainsKey).Select(id => old[id].Start).DefaultIfEmpty(0).Min();
        var moved = Math.Max(Snap(delta), -headroom);
        if (moved == 0) return false;
        var targets = old.ToDictionary(kv => kv.Key, kv => ids.Contains(kv.Key) ? kv.Value.Start + moved : kv.Value.Start);
        foreach (var pin in Pins(c.Objects, c.AnimationOrder, targets)) ApplyDelta(c, pin.StepId, pin.Trigger, pin.DelaySeconds);
        return true;
    }

    /// <summary>
    /// A block's left edge dragged by <paramref name="delta"/>: it starts later (or sooner) and still ends where
    /// it did. A column's first step can't start before the column does.
    /// </summary>
    public static bool TrimStepStart(IAnimatedComposition c, string stepId, double delta)
    {
        if (AnimationEditing.Find(c, stepId) is not { } found) return false;
        var old = Schedule(c.Objects, c.AnimationOrder);
        if (!old.TryGetValue(stepId, out var current)) return false;
        var shift = Math.Min(Snap(delta), found.Step.DurationSeconds - 0.05);
        var groups = Groups(c.Objects, c.AnimationOrder);
        var isHead = groups.Click.Append(groups.Auto).Append(groups.Exit).Any(g => g.FirstOrDefault().Step?.Id == stepId);
        var target = current.Start + shift;
        if (isHead && target < 0)
        {
            target = 0;
            shift = -current.Start;
        }
        var duration = Math.Max(current.Start + found.Step.DurationSeconds - target, 0.05);
        if (Pin(stepId, target, c.Objects, c.AnimationOrder) is not { } pinned) return false;
        ApplyDelta(c, stepId, pinned.Trigger, pinned.DelaySeconds);
        AnimationEditing.Update(c, stepId, s => s.DurationSeconds = duration);
        Repin(c, old, new HashSet<string>(StringComparer.Ordinal) { stepId });
        return true;
    }

    /// <summary>
    /// A block dropped in another column at <paramref name="offset"/> seconds into it: the step joins that
    /// group (or a new click past the last) where its start falls among the others there.
    /// </summary>
    public static bool MoveStepInto(IAnimatedComposition c, string stepId, GroupSlot slot, double offset)
    {
        offset = Snap(offset);
        var groups = Groups(c.Objects, c.AnimationOrder);
        if (slot.Kind == GroupSlotKind.Click && (slot.Index < 0 || slot.Index >= groups.Click.Count))
            slot = GroupSlot.NewClick(groups.Click.Count);
        var starts = Schedule(c.Objects, c.AnimationOrder);
        var target = slot.Kind switch
        {
            GroupSlotKind.Auto => groups.Auto,
            GroupSlotKind.Click => groups.Click[slot.Index],
            GroupSlotKind.Exit => groups.Exit,
            _ => [],
        };
        var position = target.Count(e => e.Step.Id != stepId && (starts.TryGetValue(e.Step.Id, out var s) ? s.Start : 0) < offset);
        if (GroupedMove(c.Objects, c.AnimationOrder, stepId, slot, position) is not { } move) return false;
        var promotion = Promotion(stepId, c.Objects, c.AnimationOrder);
        c.AnimationOrder = move.Order;
        var triggers = new Dictionary<string, AnimationTrigger>(move.Triggers);
        if (promotion is { } p && !triggers.ContainsKey(p.StepId)) triggers[p.StepId] = p.Trigger;
        foreach (var (id, trigger) in triggers) AnimationEditing.Update(c, id, s => s.Trigger = trigger);
        if (Placement(stepId, offset, c.Objects, c.AnimationOrder) is { } delta) ApplyDelta(c, stepId, delta.Trigger, delta.DelaySeconds);
        Repin(c, starts, new HashSet<string>(StringComparer.Ordinal) { stepId });
        return true;
    }

    /// <summary>Flips a step between "with the previous" and "after the previous" without moving it (the link on its block).</summary>
    public static bool ToggleLink(IAnimatedComposition c, string stepId)
    {
        if (LinkToggle(stepId, c.Objects, c.AnimationOrder) is not { } delta) return false;
        ApplyDelta(c, stepId, delta.Trigger, delta.DelaySeconds);
        return true;
    }

    /// <summary>A copy of a step just after it (on the same object, next in the play order). Returns the copy's id.</summary>
    public static string? DuplicateStep(IAnimatedComposition c, string stepId)
    {
        if (AnimationEditing.Find(c, stepId) is not { } found || c.Objects.FirstOrDefault(o => o.Id == found.ObjectId) is not { } owner) return null;
        var copy = MxuJson.Clone(found.Step);
        copy.Id = Guid.NewGuid().ToString().ToUpperInvariant();
        var ids = OrderedEntries(c.Objects, c.AnimationOrder).Select(e => e.Step.Id).ToList();
        var at = ids.IndexOf(stepId);
        ids.Insert(at < 0 ? ids.Count : at + 1, copy.Id);
        var steps = owner.AnimationSteps ?? [];
        var own = steps.FindIndex(s => s.Id == stepId);
        steps.Insert(own < 0 ? steps.Count : own + 1, copy);
        owner.AnimationSteps = steps;
        c.AnimationOrder = ids;
        return copy.Id;
    }

    /// <summary>Removes every step in a column (a click, the auto group or the exit).</summary>
    public static bool DeleteColumn(IAnimatedComposition c, GroupSlot slot)
    {
        var groups = Groups(c.Objects, c.AnimationOrder);
        var members = slot.Kind switch
        {
            GroupSlotKind.Auto => groups.Auto,
            GroupSlotKind.Click when slot.Index >= 0 && slot.Index < groups.Click.Count => groups.Click[slot.Index],
            GroupSlotKind.Exit => groups.Exit,
            _ => [],
        };
        var ids = members.Select(e => e.Step.Id).ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0) return false;
        foreach (var o in c.Objects)
        {
            var kept = o.AnimationSteps?.Where(s => !ids.Contains(s.Id)).ToList();
            o.AnimationSteps = kept is { Count: 0 } ? null : kept;
        }
        if (c.AnimationOrder is { } order)
        {
            var kept = order.Where(id => !ids.Contains(id)).ToList();
            c.AnimationOrder = kept.Count == 0 ? null : kept;
        }
        return true;
    }

    /// <summary>Moves a whole column to another slot (say click 2 before click 1), keeping its own timing.</summary>
    public static bool MoveColumn(IAnimatedComposition c, GroupSlot from, GroupSlot to)
    {
        if (RehomeGroup(from, to, c.Objects, c.AnimationOrder) is not { } move) return false;
        c.AnimationOrder = move.Order;
        foreach (var delta in move.Deltas) ApplyDelta(c, delta.StepId, delta.Trigger, delta.DelaySeconds);
        return true;
    }

    // MARK: Steps posed on the canvas

    /// <summary>
    /// How an object looks just before <paramref name="beforeStepId"/> (or after all its morphs, for
    /// null): its document look, changed by each earlier morph's end state. No steps of its own.
    /// </summary>
    public static SlideObject? MorphBaseline(IAnimatedComposition c, string objectId, string? beforeStepId)
    {
        if (c.Objects.FirstOrDefault(o => o.Id == objectId) is not { } o) return null;
        var state = o;
        foreach (var entry in OrderedEntries(c.Objects, c.AnimationOrder).Where(e => e.ObjectId == objectId))
        {
            if (entry.Step.Id == beforeStepId) break;
            if (entry.Step.Kind == AnimationKind.Morph && entry.Step.ToObject is { } to) state = to;
        }
        var baseline = MxuJson.Clone(state);
        baseline.Id = objectId;
        baseline.AnimationSteps = null;
        return baseline;
    }

    /// <summary>
    /// A morph ("Change") step on each object, starting from how it looks after its earlier morphs:
    /// the first on a click, the rest with it, lasting a second. Its end state starts as that look,
    /// so nothing changes until you pose it. Returns the new steps' ids.
    /// </summary>
    public static List<string> AddMorph(IAnimatedComposition c, IReadOnlyList<string> objectIds)
    {
        var added = new List<string>();
        var baselines = objectIds.Select(id => (id, MorphBaseline(c, id, null))).Where(p => p.Item2 is not null).ToList();
        var position = 0;
        foreach (var (id, baseline) in baselines)
        {
            if (c.Objects.FirstOrDefault(o => o.Id == id) is not { } o) continue;
            var step = AnimationEditing.FreshStep(AnimationKind.Morph, StepAnimation.Fade, position == 0 ? AnimationTrigger.OnClick : AnimationTrigger.WithPrevious);
            step.DurationSeconds = 1.0;
            step.ToObject = baseline;
            o.AnimationSteps = [.. o.AnimationSteps ?? [], step];
            if (c.AnimationOrder is { Count: > 0 } order) c.AnimationOrder = [.. order, step.Id];
            added.Add(step.Id);
            position++;
        }
        return added;
    }

    /// <summary>
    /// Turns an In's custom start ("where it enters from") on, starting where the object rests, or
    /// off again. While on, the object travels from that pose to its place (the canvas poses it).
    /// </summary>
    public static bool SetCustomStart(IAnimatedComposition c, string stepId, bool enabled)
    {
        if (AnimationEditing.Find(c, stepId) is not { } found || found.Step.Kind != AnimationKind.In) return false;
        if (!enabled) return AnimationEditing.Update(c, stepId, s => s.FromObject = null);
        if (c.Objects.FirstOrDefault(o => o.Id == found.ObjectId) is not { } o) return false;
        var pose = MxuJson.Clone(o);
        pose.AnimationSteps = null;
        return AnimationEditing.Update(c, stepId, s => s.FromObject = pose);
    }

    /// <summary>"Starts from" for a custom start: off an edge (a slide in), or where you placed it.</summary>
    public static bool StartFromEdge(IAnimatedComposition c, string stepId, AnimationEdge edge) =>
        AnimationEditing.Update(c, stepId, s =>
        {
            if (s.Animation is not (StepAnimation.Move or StepAnimation.Wipe)) s.Animation = StepAnimation.Move;
            s.Edge = edge;
            s.FromObject = null;
        });

    /// <summary>What a posed step's canvas edits: its end state (a morph) or start (an In's custom start), or null for other steps.</summary>
    public static SlideObject? Pose(IAnimatedComposition c, string stepId) =>
        AnimationEditing.Find(c, stepId)?.Step is { } step
            ? step.Kind == AnimationKind.Morph ? step.ToObject : step.Kind == AnimationKind.In ? step.FromObject : null
            : null;

    /// <summary>
    /// The objects the canvas shows while a step is posed: everything at rest, except the posed object,
    /// which is its pose (the same instance, so moving it on the canvas moves the pose). Its id is the
    /// object's, so the canvas can pick and move it. Null when the step has no pose.
    /// </summary>
    public static List<SlideObject>? PosedObjects(IAnimatedComposition c, string stepId)
    {
        if (AnimationEditing.Find(c, stepId) is not { } found || Pose(c, stepId) is not { } pose) return null;
        pose.Id = found.ObjectId;
        pose.AnimationSteps = null;
        return [.. c.Objects.Select(o => o.Id == found.ObjectId ? pose : o)];
    }

    /// <summary>Puts a posed object back as it rests: the morph's end state becomes its look before the step (nothing changes).</summary>
    public static bool ResetPose(IAnimatedComposition c, string stepId)
    {
        if (AnimationEditing.Find(c, stepId) is not { } found) return false;
        if (found.Step.Kind == AnimationKind.Morph && MorphBaseline(c, found.ObjectId, stepId) is { } baseline)
            return AnimationEditing.Update(c, stepId, s => s.ToObject = baseline);
        if (found.Step.Kind == AnimationKind.In && found.Step.FromObject is not null) return SetCustomStart(c, stepId, true);
        return false;
    }
}
