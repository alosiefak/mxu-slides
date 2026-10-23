using MxU.Slides.Core.Json;
using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Animation;

/// <summary>
/// Turns the animation steps authored on a slide's (or overlay's) objects into
/// groups (auto, one per click, exit) and a schedule of <see cref="SceneAnimationStep"/>s,
/// plus the editor's reordering helpers. Port of SlideScene/AnimationSequence.swift.
/// </summary>
public static class AnimationSequence
{
    public readonly record struct Entry(string ObjectId, AnimationStep Step);

    /// <summary>Steps run from the fire (auto), one list per click, and on the exit press or dismiss.</summary>
    public sealed record StepGroups(List<Entry> Auto, List<List<Entry>> Click, List<Entry> Exit);

    /// <summary>A step's new trigger and delay; a zero delay is null (the absent default).</summary>
    public readonly record struct StepTiming(AnimationTrigger Trigger, double? DelaySeconds);

    public readonly record struct StepDelta(string StepId, AnimationTrigger Trigger, double? DelaySeconds);

    public sealed record GroupedMoveResult(List<string> Order, Dictionary<string, AnimationTrigger> Triggers);

    public sealed record Rehome(List<string> Order, List<StepDelta> Deltas);

    public sealed record Preview(IReadOnlyList<double> Clicks, double DismissAt, double Duration);

    public enum GroupSlotKind { Auto, Click, Exit, NewClick }

    /// <summary>Where the editor drops steps: an existing group, or a new click inserted at an index.</summary>
    public readonly record struct GroupSlot(GroupSlotKind Kind, int Index = 0)
    {
        public static readonly GroupSlot Auto = new(GroupSlotKind.Auto);
        public static readonly GroupSlot Exit = new(GroupSlotKind.Exit);
        public static GroupSlot Click(int index) => new(GroupSlotKind.Click, index);
        public static GroupSlot NewClick(int index) => new(GroupSlotKind.NewClick, index);
    }

    private static List<AnimationStep>? StepsOf(SlideObject o) => o.AnimationSteps ?? o.Builds;

    /// <summary>Visible objects' steps in document order; <paramref name="order"/> lists ids to put first (unknown and repeated ids ignored).</summary>
    public static List<Entry> OrderedEntries(IReadOnlyList<SlideObject> objects, IReadOnlyList<string>? order)
    {
        var all = new List<Entry>();
        foreach (var o in objects.Where(o => o.Hidden != true))
            foreach (var step in StepsOf(o) ?? [])
                all.Add(new Entry(o.Id, step));
        if (order is null || order.Count == 0) return all;
        var byId = new Dictionary<string, Entry>();
        foreach (var entry in all) byId[entry.Step.Id] = entry;
        var listed = new List<Entry>();
        var seen = new HashSet<string>();
        foreach (var id in order)
        {
            if (!byId.TryGetValue(id, out var entry) || !seen.Add(id)) continue;
            listed.Add(entry);
        }
        return [.. listed, .. all.Where(e => !seen.Contains(e.Step.Id))];
    }

    public static StepGroups Groups(IReadOnlyList<SlideObject> objects, IReadOnlyList<string>? order)
    {
        var groups = new StepGroups([], [], []);
        var current = SceneAnimationGroup.Auto;
        foreach (var entry in OrderedEntries(objects, order))
        {
            switch (entry.Step.Trigger)
            {
                case AnimationTrigger.OnClick:
                    groups.Click.Add([entry]);
                    current = SceneAnimationGroup.Click(groups.Click.Count - 1);
                    break;
                case AnimationTrigger.OnDismiss:
                    groups.Exit.Add(entry);
                    current = SceneAnimationGroup.Exit;
                    break;
                default:
                    (current.Kind switch
                    {
                        SceneAnimationGroupKind.Auto => groups.Auto,
                        SceneAnimationGroupKind.Click => groups.Click[current.Index],
                        _ => groups.Exit,
                    }).Add(entry);
                    break;
            }
        }
        return groups;
    }

    public static int ClickCount(IReadOnlyList<SlideObject> objects, IReadOnlyList<string>? order) => Groups(objects, order).Click.Count;

    public static bool HasExitGroup(IReadOnlyList<SlideObject> objects, IReadOnlyList<string>? order) => Groups(objects, order).Exit.Count > 0;

    /// <summary>Advances the slide absorbs: one per click, plus the exit press when it has an exit group.</summary>
    public static int AdvanceCount(IReadOnlyList<SlideObject> objects, IReadOnlyList<string>? order)
    {
        var groups = Groups(objects, order);
        return groups.Click.Count + (groups.Exit.Count == 0 ? 0 : 1);
    }

    /// <summary>How long the leave takes: the exit group, and any clicked Out not yet consumed (it plays at the dismiss).</summary>
    public static double ExitDuration(IReadOnlyList<SlideObject> objects, IReadOnlyList<string>? order, int consumedClicks = 0)
    {
        var steps = SceneSteps(objects, order).Values.SelectMany(s => s).ToList();
        var duration = AnimationTimeline.GroupDuration(SceneAnimationGroup.Exit, steps);
        foreach (var step in steps.Where(s => s.Kind == SceneAnimationKind.Exit))
            if (step.Group.IsClick && step.Group.Index >= consumedClicks)
                duration = Math.Max(duration, step.StartOffset + step.Duration);
        return duration;
    }

    public const double PreviewClickGap = 0.4;
    public const double PreviewExitHold = 0.8;

    /// <summary>The editor's play-through: each click right after the previous group, then a hold before the exit.</summary>
    public static Preview PreviewTimeline(IReadOnlyList<SlideObject> objects, IReadOnlyList<string>? order)
    {
        var steps = SceneSteps(objects, order).Values.SelectMany(s => s).ToList();
        var groups = Groups(objects, order);
        var cursor = AnimationTimeline.GroupDuration(SceneAnimationGroup.Auto, steps);
        var clicks = new List<double>();
        for (var index = 0; index < groups.Click.Count; index++)
        {
            var start = cursor + (cursor > 0 || index > 0 ? PreviewClickGap : 0);
            clicks.Add(start);
            cursor = start + AnimationTimeline.GroupDuration(SceneAnimationGroup.Click(index), steps);
        }
        var dismissAt = cursor + PreviewExitHold;
        return new Preview(clicks, dismissAt, dismissAt + AnimationTimeline.GroupDuration(SceneAnimationGroup.Exit, steps));
    }

    /// <summary>A context playing the preview from <paramref name="startedAt"/>, frozen at <paramref name="time"/> when scrubbing.</summary>
    public static AnimationContext PreviewContext(IReadOnlyList<SlideObject> objects, IReadOnlyList<string>? order, double? time, double startedAt = 0)
    {
        var timeline = PreviewTimeline(objects, order);
        return new AnimationContext(
            startedAt,
            timeline.Clicks.Select(c => startedAt + c).ToList(),
            dismissHostTime: startedAt + timeline.DismissAt,
            frozenHostTime: time is { } t ? startedAt + t : null);
    }

    /// <summary>Authoring problems by step id: an In while already shown, an Out/emphasis/morph before the In.</summary>
    public static Dictionary<string, string> Warnings(IReadOnlyList<SlideObject> objects, IReadOnlyList<string>? order)
    {
        var warnings = new Dictionary<string, string>();
        var entries = OrderedEntries(objects, order);
        var hasIn = entries.Where(e => e.Step.Kind == AnimationKind.In).Select(Key).ToHashSet();
        var shown = new Dictionary<string, bool>();
        bool IsShown(Entry entry) => shown.TryGetValue(Key(entry), out var state) ? state : !hasIn.Contains(Key(entry));
        foreach (var entry in entries)
        {
            var k = Key(entry);
            switch (entry.Step.Kind)
            {
                case AnimationKind.In:
                    if (IsShown(entry)) warnings[entry.Step.Id] = "Already shown — nothing left to bring in";
                    shown[k] = true;
                    break;
                case AnimationKind.Out:
                    if (!IsShown(entry)) warnings[entry.Step.Id] = "Plays before it's been brought in";
                    shown[k] = false;
                    break;
                case AnimationKind.Emphasis:
                    if (!IsShown(entry)) warnings[entry.Step.Id] = "Plays before it's been brought in";
                    break;
                case AnimationKind.Morph:
                    if (!IsShown(entry)) warnings[entry.Step.Id] = "Plays before it's been brought in";
                    else if (entry.Step.ToObject is null) warnings[entry.Step.Id] = "No end state yet";
                    break;
            }
        }
        return warnings;
    }

    private static string Key(Entry entry)
    {
        if (entry.Step.Ranges is not { Count: > 0 } ranges) return entry.ObjectId;
        return entry.ObjectId + "#" + string.Join(",", ranges.Select(r => $"{r.Line}:{r.Column}:{r.Length}").OrderBy(s => s, StringComparer.Ordinal));
    }

    /// <summary>Removing a group's first step hands its trigger to the follower that chained off it.</summary>
    public static (string StepId, AnimationTrigger Trigger)? Promotion(string afterRemoving, IReadOnlyList<SlideObject> objects, IReadOnlyList<string>? order)
    {
        var entries = OrderedEntries(objects, order);
        var index = entries.FindIndex(e => e.Step.Id == afterRemoving);
        if (index < 0) return null;
        var leaving = entries[index].Step;
        if (leaving.Trigger is not (AnimationTrigger.OnClick or AnimationTrigger.OnDismiss) || index + 1 >= entries.Count) return null;
        var next = entries[index + 1].Step;
        if (next.Trigger is not (AnimationTrigger.WithPrevious or AnimationTrigger.AfterPrevious)) return null;
        return (next.Id, leaving.Trigger);
    }

    /// <summary>Moves one step into a group at a position, taking the trigger of its new neighbourhood.</summary>
    public static GroupedMoveResult? GroupedMove(IReadOnlyList<SlideObject> objects, IReadOnlyList<string>? order, string moving, GroupSlot into, int position)
    {
        var groups = Groups(objects, order);
        if (Find(OrderedEntries(objects, order), moving) is not { } entry) return null;
        var auto = groups.Auto.Where(e => e.Step.Id != moving).ToList();
        var clicks = groups.Click.Select(c => c.Where(e => e.Step.Id != moving).ToList()).ToList();
        var exit = groups.Exit.Where(e => e.Step.Id != moving).ToList();
        var triggers = new Dictionary<string, AnimationTrigger>();
        void Insert(List<Entry> list, AnimationTrigger firstTrigger)
        {
            var index = Math.Clamp(position, 0, list.Count);
            list.Insert(index, entry);
            if (index == 0)
            {
                triggers[moving] = firstTrigger;
                if (list.Count > 1 && list[1].Step.Trigger == firstTrigger && firstTrigger != AnimationTrigger.WithPrevious)
                    triggers[list[1].Step.Id] = AnimationTrigger.WithPrevious;
            }
            else
            {
                triggers[moving] = entry.Step.Trigger == AnimationTrigger.AfterPrevious ? AnimationTrigger.AfterPrevious : AnimationTrigger.WithPrevious;
            }
        }
        switch (into.Kind)
        {
            case GroupSlotKind.Auto:
                Insert(auto, AnimationTrigger.WithPrevious);
                break;
            case GroupSlotKind.Click:
                if (into.Index < 0 || into.Index >= clicks.Count) return null;
                Insert(clicks[into.Index], AnimationTrigger.OnClick);
                break;
            case GroupSlotKind.NewClick:
                clicks.Insert(Math.Clamp(into.Index, 0, clicks.Count), [entry]);
                triggers[moving] = AnimationTrigger.OnClick;
                break;
            case GroupSlotKind.Exit:
                Insert(exit, AnimationTrigger.OnDismiss);
                break;
        }
        return new GroupedMoveResult(Flatten(auto, clicks, exit), triggers);
    }

    /// <summary>Moves a whole group to another slot, keeping its internal timing; the vacated click disappears.</summary>
    public static Rehome? RehomeGroup(GroupSlot from, GroupSlot to, IReadOnlyList<SlideObject> objects, IReadOnlyList<string>? order)
    {
        if (from == to) return null;
        var groups = Groups(objects, order);
        var starts = Schedule(objects, order);
        List<Entry>? Members(GroupSlot slot) => slot.Kind switch
        {
            GroupSlotKind.Auto => groups.Auto,
            GroupSlotKind.Click => slot.Index >= 0 && slot.Index < groups.Click.Count ? groups.Click[slot.Index] : null,
            GroupSlotKind.NewClick => [],
            _ => groups.Exit,
        };
        if (Members(from) is not { Count: > 0 } moving) return null;
        var movingIds = moving.Select(e => e.Step.Id).ToHashSet();
        var target = Members(to) ?? [];

        var auto = groups.Auto.Where(e => !movingIds.Contains(e.Step.Id)).ToList();
        var clicks = groups.Click.Select(c => c.Where(e => !movingIds.Contains(e.Step.Id)).ToList()).ToList();
        var exit = groups.Exit.Where(e => !movingIds.Contains(e.Step.Id)).ToList();
        switch (to.Kind)
        {
            case GroupSlotKind.Auto: auto.AddRange(moving); break;
            case GroupSlotKind.Click when to.Index >= 0 && to.Index < clicks.Count: clicks[to.Index].AddRange(moving); break;
            case GroupSlotKind.Click: clicks.Add(moving); break;
            case GroupSlotKind.NewClick: clicks.Insert(Math.Clamp(to.Index, 0, clicks.Count), moving); break;
            case GroupSlotKind.Exit: exit.AddRange(moving); break;
        }
        var flat = Flatten(auto, clicks, exit);

        var deltas = new List<StepDelta>();
        var head = moving[0];
        if (target.Count > 0)
        {
            var targetEnd = target.Select(e => Start(starts, e.Step.Id) + Duration(starts, e.Step.Id)).Max();
            var delay = targetEnd - Start(starts, target[^1].Step.Id);
            deltas.Add(new StepDelta(head.Step.Id, AnimationTrigger.WithPrevious, delay == 0 ? null : delay));
        }
        else
        {
            var trigger = to.Kind switch
            {
                GroupSlotKind.Auto => AnimationTrigger.WithPrevious,
                GroupSlotKind.Exit => AnimationTrigger.OnDismiss,
                _ => AnimationTrigger.OnClick,
            };
            deltas.Add(new StepDelta(head.Step.Id, trigger, null));
        }
        for (var index = 1; index < moving.Count; index++)
        {
            var entry = moving[index];
            if (entry.Step.Trigger is not (AnimationTrigger.OnClick or AnimationTrigger.OnDismiss)) continue;
            var delay = Start(starts, entry.Step.Id) - Start(starts, moving[index - 1].Step.Id);
            deltas.Add(new StepDelta(entry.Step.Id, AnimationTrigger.WithPrevious, delay == 0 ? null : delay));
        }
        return new Rehome(flat, deltas);
    }

    /// <summary>Moves a set of steps into a slot with its first landing at <paramref name="firstOffset"/>, keeping the set's spacing and planting everything else.</summary>
    public static Rehome? RehomeSteps(IReadOnlySet<string> ids, GroupSlot to, double firstOffset, IReadOnlyList<SlideObject> objects, IReadOnlyList<string>? order)
    {
        var entries = OrderedEntries(objects, order);
        var moving = entries.Where(e => ids.Contains(e.Step.Id)).ToList();
        if (moving.Count == 0) return null;
        var movingIds = moving.Select(e => e.Step.Id).ToHashSet();
        var starts = Schedule(objects, order);

        var previews = movingIds.ToDictionary(id => id, id => PreviewStart(id, objects, order) ?? 0);
        var baseStart = previews[moving[0].Step.Id];
        var landed = Math.Max(firstOffset, 0);
        var targets = new Dictionary<string, double>();
        foreach (var entry in entries)
        {
            var id = entry.Step.Id;
            targets[id] = movingIds.Contains(id) ? landed + (previews[id] - baseStart) : Start(starts, id);
        }

        var groups = Groups(objects, order);
        var auto = groups.Auto.Where(e => !movingIds.Contains(e.Step.Id)).ToList();
        var clicks = groups.Click.Select(c => c.Where(e => !movingIds.Contains(e.Step.Id)).ToList()).ToList();
        var exit = groups.Exit.Where(e => !movingIds.Contains(e.Step.Id)).ToList();
        Dictionary<string, AnimationTrigger> Insert(List<Entry> list, AnimationTrigger headTrigger)
        {
            var position = list.Count(e => Start(starts, e.Step.Id) < landed);
            var conversions = new Dictionary<string, AnimationTrigger>();
            if (position == 0)
            {
                conversions[moving[0].Step.Id] = headTrigger;
                if (list.Count > 0 && list[0].Step.Trigger == headTrigger && headTrigger != AnimationTrigger.WithPrevious)
                    conversions[list[0].Step.Id] = AnimationTrigger.WithPrevious;
            }
            else
            {
                conversions[moving[0].Step.Id] = AnimationTrigger.WithPrevious;
            }
            list.InsertRange(position, moving);
            return conversions;
        }
        Dictionary<string, AnimationTrigger> conversions;
        switch (to.Kind)
        {
            case GroupSlotKind.Auto:
                conversions = Insert(auto, AnimationTrigger.WithPrevious);
                break;
            case GroupSlotKind.Click when to.Index >= 0 && to.Index < clicks.Count:
                conversions = Insert(clicks[to.Index], AnimationTrigger.OnClick);
                break;
            case GroupSlotKind.Click:
                clicks.Add(moving);
                conversions = new() { [moving[0].Step.Id] = AnimationTrigger.OnClick };
                break;
            case GroupSlotKind.NewClick:
                clicks.Insert(Math.Clamp(to.Index, 0, clicks.Count), moving);
                conversions = new() { [moving[0].Step.Id] = AnimationTrigger.OnClick };
                break;
            default:
                conversions = Insert(exit, AnimationTrigger.OnDismiss);
                break;
        }

        foreach (var entry in moving.Skip(1).Where(e => e.Step.Trigger is AnimationTrigger.OnClick or AnimationTrigger.OnDismiss))
            conversions[entry.Step.Id] = AnimationTrigger.WithPrevious;
        foreach (var list in clicks)
        {
            if (list.Count == 0) continue;
            var first = list[0].Step;
            if ((conversions.TryGetValue(first.Id, out var effective) ? effective : first.Trigger) != AnimationTrigger.OnClick)
                conversions[first.Id] = AnimationTrigger.OnClick;
        }
        if (exit.Count > 0)
        {
            var first = exit[0].Step;
            if ((conversions.TryGetValue(first.Id, out var effective) ? effective : first.Trigger) != AnimationTrigger.OnDismiss)
                conversions[first.Id] = AnimationTrigger.OnDismiss;
        }
        var flat = Flatten(auto, clicks, exit);

        var working = objects.Select(MxuJson.Clone).ToList();
        foreach (var step in working.SelectMany(o => o.AnimationSteps ?? []))
            if (conversions.TryGetValue(step.Id, out var trigger)) step.Trigger = trigger;
        var forced = movingIds.Union(conversions.Keys).ToHashSet();
        var deltas = new List<StepDelta>();
        foreach (var entry in OrderedEntries(working, flat))
        {
            var id = entry.Step.Id;
            if (!targets.TryGetValue(id, out var target)) continue;
            if (!Schedule(working, flat).TryGetValue(id, out var current)) continue;
            if (!forced.Contains(id) && Math.Abs(current.Start - target) <= 0.0001) continue;
            if (Pin(id, target, working, flat) is not { } delta) continue;
            deltas.Add(new StepDelta(id, delta.Trigger, delta.DelaySeconds));
            Apply(working, id, delta);
        }
        return new Rehome(flat, deltas);
    }

    /// <summary>A step's start on the preview timeline.</summary>
    public static double? PreviewStart(string ofStep, IReadOnlyList<SlideObject> objects, IReadOnlyList<string>? order)
    {
        var timeline = PreviewTimeline(objects, order);
        foreach (var steps in SceneSteps(objects, order).Values)
        {
            if (steps.FirstOrDefault(s => s.Id == ofStep) is not { } step) continue;
            return step.Group.Kind switch
            {
                SceneAnimationGroupKind.Auto => step.StartOffset,
                SceneAnimationGroupKind.Click => step.Group.Index < timeline.Clicks.Count ? timeline.Clicks[step.Group.Index] + step.StartOffset : null,
                _ => timeline.DismissAt + step.StartOffset,
            };
        }
        return null;
    }

    /// <summary>What the next click brings in, for the stage display's "next step".</summary>
    public static string? UpcomingRevealText(IReadOnlyList<SlideObject> objects, IReadOnlyList<string>? order, int consumed)
    {
        var groups = Groups(objects, order);
        if (consumed < 0 || consumed >= groups.Click.Count) return null;
        var lines = new List<string>();
        foreach (var entry in groups.Click[consumed])
        {
            if (entry.Step.Kind is not (AnimationKind.In or AnimationKind.Morph)) continue;
            if (objects.FirstOrDefault(o => o.Id == entry.ObjectId) is not { } o) continue;
            if (entry.Step.Ranges is { Count: > 0 } ranges)
            {
                var text = string.Join(" ", ranges.Select(r => RangeText(r, o.Text)));
                if (text.Length > 0) lines.Add(text);
                continue;
            }
            lines.Add(o.Text.Length == 0 ? o.Name : o.Text);
        }
        var body = string.Join("\n", lines.Where(l => l.Length > 0));
        return body.Length == 0 ? null : body;
    }

    /// <summary>The characters a range covers (AnimationRanges.text on the Mac).</summary>
    public static string RangeText(AnimationRange range, string text)
    {
        var lines = text.Split('\n');
        if (range.Line < 0 || range.Line >= lines.Length) return "";
        var line = lines[range.Line];
        var count = line.CharacterCount();
        var start = Math.Clamp(range.Column, 0, count);
        var end = Math.Min(start + Math.Max(range.Length, 0), count);
        if (end <= start) return "";
        var from = line.Utf16Offset(start);
        return line[from..line.Utf16Offset(end)];
    }

    public static bool HasAnimationSteps(IReadOnlyList<SlideObject> objects) =>
        objects.Any(o => o.Hidden != true && (StepsOf(o) ?? []).Count > 0);

    /// <summary>Each step's start offset (within its group) and duration.</summary>
    public static Dictionary<string, (double Start, double Duration)> Schedule(IReadOnlyList<SlideObject> objects, IReadOnlyList<string>? order)
    {
        var result = new Dictionary<string, (double Start, double Duration)>();
        foreach (var step in SceneSteps(objects, order).Values.SelectMany(s => s))
            result[step.Id] = (step.StartOffset, step.Duration);
        return result;
    }

    /// <summary>The trigger and delay that put a step at <paramref name="targetOffset"/>, relative to the step before it.</summary>
    public static StepTiming? Placement(string of, double targetOffset, IReadOnlyList<SlideObject> objects, IReadOnlyList<string>? order)
    {
        var groups = Groups(objects, order);
        var starts = Schedule(objects, order);
        StepTiming? Within(List<Entry> entries, AnimationTrigger head)
        {
            var index = entries.FindIndex(e => e.Step.Id == of);
            if (index < 0) return null;
            if (index == 0)
            {
                var delay = Math.Max(targetOffset, 0);
                return new StepTiming(head, delay == 0 ? null : delay);
            }
            var offset = targetOffset - Start(starts, entries[index - 1].Step.Id);
            return new StepTiming(AnimationTrigger.WithPrevious, offset == 0 ? null : offset);
        }
        if (Within(groups.Auto, AnimationTrigger.WithPrevious) is { } auto) return auto;
        foreach (var entries in groups.Click)
            if (Within(entries, AnimationTrigger.OnClick) is { } click) return click;
        return Within(groups.Exit, AnimationTrigger.OnDismiss);
    }

    /// <summary>The delay that keeps a step's own trigger but lands it at <paramref name="targetOffset"/>.</summary>
    public static StepTiming? Pin(string of, double targetOffset, IReadOnlyList<SlideObject> objects, IReadOnlyList<string>? order)
    {
        var groups = Groups(objects, order);
        var starts = Schedule(objects, order);
        StepTiming? Within(List<Entry> entries)
        {
            var index = entries.FindIndex(e => e.Step.Id == of);
            if (index < 0) return null;
            var entry = entries[index];
            double delay;
            if (entry.Step.Trigger == AnimationTrigger.WithPrevious && index > 0)
            {
                delay = targetOffset - Start(starts, entries[index - 1].Step.Id);
            }
            else if (entry.Step.Trigger == AnimationTrigger.AfterPrevious && index > 0)
            {
                var previous = entries[index - 1].Step.Id;
                delay = targetOffset - (Start(starts, previous) + Duration(starts, previous));
            }
            else
            {
                delay = Math.Max(targetOffset, 0);
            }
            return new StepTiming(entry.Step.Trigger, delay == 0 ? null : delay);
        }
        if (Within(groups.Auto) is { } auto) return auto;
        foreach (var entries in groups.Click)
            if (Within(entries) is { } click) return click;
        return Within(groups.Exit);
    }

    /// <summary>Re-pins every step (but the excluded) to where it started before an edit, so moving one card never shoves the rest.</summary>
    public static List<StepDelta> Pins(
        IReadOnlyList<SlideObject> objects, IReadOnlyList<string>? order,
        IReadOnlyDictionary<string, (double Start, double Duration)> toPrevious, IReadOnlySet<string> excluding) =>
        Pins(objects, order, toPrevious.Where(kv => !excluding.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value.Start));

    public static List<StepDelta> Pins(IReadOnlyList<SlideObject> objects, IReadOnlyList<string>? order, IReadOnlyDictionary<string, double> targets)
    {
        var working = objects.Select(MxuJson.Clone).ToList();
        var result = new List<StepDelta>();
        foreach (var entry in OrderedEntries(working, order))
        {
            var id = entry.Step.Id;
            if (!targets.TryGetValue(id, out var target)) continue;
            if (!Schedule(working, order).TryGetValue(id, out var current) || Math.Abs(current.Start - target) <= 0.0001) continue;
            if (Pin(id, target, working, order) is not { } delta) continue;
            result.Add(new StepDelta(id, delta.Trigger, delta.DelaySeconds));
            Apply(working, id, delta);
        }
        return result;
    }

    /// <summary>Flips a follower between with- and after-previous without moving it.</summary>
    public static StepTiming? LinkToggle(string of, IReadOnlyList<SlideObject> objects, IReadOnlyList<string>? order)
    {
        var groups = Groups(objects, order);
        var starts = Schedule(objects, order);
        StepTiming? Within(List<Entry> entries)
        {
            var index = entries.FindIndex(e => e.Step.Id == of);
            if (index <= 0 || !starts.TryGetValue(of, out var current) || !starts.TryGetValue(entries[index - 1].Step.Id, out var previous))
                return null;
            if (entries[index].Step.Trigger == AnimationTrigger.AfterPrevious)
            {
                var with = current.Start - previous.Start;
                return new StepTiming(AnimationTrigger.WithPrevious, with == 0 ? null : with);
            }
            var after = current.Start - (previous.Start + previous.Duration);
            return new StepTiming(AnimationTrigger.AfterPrevious, after == 0 ? null : after);
        }
        if (Within(groups.Auto) is { } auto) return auto;
        foreach (var entries in groups.Click)
            if (Within(entries) is { } click) return click;
        return Within(groups.Exit);
    }

    /// <summary>The engine's steps for each object id: grouped, with chaining and delays folded into start offsets.</summary>
    public static Dictionary<string, List<SceneAnimationStep>> SceneSteps(IReadOnlyList<SlideObject> objects, IReadOnlyList<string>? order)
    {
        var groups = Groups(objects, order);
        var result = new Dictionary<string, List<SceneAnimationStep>>();
        void ScheduleGroup(List<Entry> entries, SceneAnimationGroup group)
        {
            (double Start, double End)? previous = null;
            foreach (var entry in entries)
            {
                var delay = entry.Step.DelaySeconds ?? 0;
                var start = entry.Step.Trigger switch
                {
                    AnimationTrigger.WithPrevious => Math.Max((previous?.Start ?? 0) + delay, 0),
                    AnimationTrigger.AfterPrevious => Math.Max((previous?.End ?? 0) + delay, 0),
                    _ => Math.Max(delay, 0),
                };
                previous = (start, start + Math.Max(entry.Step.DurationSeconds, 0));
                if (!result.TryGetValue(entry.ObjectId, out var list)) result[entry.ObjectId] = list = [];
                list.Add(SceneStep(entry.Step, group, start));
            }
        }
        ScheduleGroup(groups.Auto, SceneAnimationGroup.Auto);
        for (var index = 0; index < groups.Click.Count; index++) ScheduleGroup(groups.Click[index], SceneAnimationGroup.Click(index));
        ScheduleGroup(groups.Exit, SceneAnimationGroup.Exit);
        return result;
    }

    /// <summary>One authored step in engine terms, with the schema's defaults filled in.</summary>
    public static SceneAnimationStep SceneStep(AnimationStep step, SceneAnimationGroup group, double startOffset)
    {
        var kind = step.Kind switch
        {
            AnimationKind.In => SceneAnimationKind.Enter,
            AnimationKind.Out => SceneAnimationKind.Exit,
            AnimationKind.Emphasis => SceneAnimationKind.Emphasis,
            _ => SceneAnimationKind.Morph,
        };
        var ramp = step.Ramp switch
        {
            AnimationRamp.None => SceneAnimationRamp.None,
            AnimationRamp.In => SceneAnimationRamp.EaseIn,
            AnimationRamp.Out => SceneAnimationRamp.EaseOut,
            AnimationRamp.Both => SceneAnimationRamp.EaseInOut,
            _ => kind switch
            {
                SceneAnimationKind.Enter => SceneAnimationRamp.EaseOut,
                SceneAnimationKind.Exit => SceneAnimationRamp.EaseIn,
                _ => SceneAnimationRamp.EaseInOut,
            },
        };
        var animation = step.Animation switch
        {
            StepAnimation.Move => SceneStepAnimation.Move,
            StepAnimation.Scale => SceneStepAnimation.Scale,
            StepAnimation.Wipe => SceneStepAnimation.Wipe,
            StepAnimation.Blur => SceneStepAnimation.Blur,
            StepAnimation.Burn => SceneStepAnimation.Burn,
            StepAnimation.Glitch => SceneStepAnimation.Glitch,
            StepAnimation.Draw => SceneStepAnimation.Draw,
            StepAnimation.Type => SceneStepAnimation.Type,
            StepAnimation.Pulse => SceneStepAnimation.Pulse,
            StepAnimation.Color => SceneStepAnimation.Color,
            _ => SceneStepAnimation.Fade,
        };
        SceneAnimationEdge? edge = step.Edge switch
        {
            AnimationEdge.Left => SceneAnimationEdge.Left,
            AnimationEdge.Right => SceneAnimationEdge.Right,
            AnimationEdge.Top => SceneAnimationEdge.Top,
            AnimationEdge.Bottom => SceneAnimationEdge.Bottom,
            _ => null,
        };
        var ranges = step.Ranges?.Select(r => new SceneAnimationRange(r.Line, r.Column, r.Length)).ToList();
        var defaultAmount = animation switch
        {
            SceneStepAnimation.Blur => 24,
            SceneStepAnimation.Pulse => 1.1,
            _ => 0.6,
        };
        return new SceneAnimationStep
        {
            Id = step.Id,
            Kind = kind,
            Animation = animation,
            Group = group,
            StartOffset = startOffset,
            Duration = step.DurationSeconds,
            Ramp = ramp,
            WithFade = step.WithFade ?? false,
            Ranges = ranges is { Count: > 0 } ? ranges : null,
            Edge = edge,
            Offset = new SceneVector(step.OffsetX ?? 0, step.OffsetY ?? 0),
            FromScale = step.FromScale ?? 0.95,
            Amount = step.Amount ?? defaultAmount,
            SoftEdge = step.SoftEdge ?? 0,
            Cursor = step.Cursor ?? true,
            Color = ColorHex.Color(step.ColorHex) ?? (animation == SceneStepAnimation.Color ? ColorHex.Color("#FFD54FFF") : null),
            PlaceholderUnderline = step.PlaceholderUnderline ?? false,
            DrawStart = step.DrawStart ?? 0,
            Reverse = step.Reverse ?? false,
            VideoPush = step.VideoPush is { } push ? SceneVideoPushFor(push) : null,
        };
    }

    internal static SceneVideoPush SceneVideoPushFor(VideoPush push) => new(
        push.Alignment switch
        {
            VideoPushAlignment.Left => SceneVideoPushAlignment.Left,
            VideoPushAlignment.Right => SceneVideoPushAlignment.Right,
            VideoPushAlignment.Top => SceneVideoPushAlignment.Top,
            VideoPushAlignment.Bottom => SceneVideoPushAlignment.Bottom,
            _ => SceneVideoPushAlignment.Center,
        },
        push.Mode switch
        {
            VideoPushMode.Fit => SceneVideoPushMode.Fit,
            VideoPushMode.BlurBackground => SceneVideoPushMode.BlurBackground,
            _ => SceneVideoPushMode.Fill,
        },
        Math.Max(push.Zoom ?? 1, 0.05),
        Math.Max(push.Margin ?? 0, 0),
        push.Backdrop ?? false,
        Math.Max(push.BlurRadius ?? 36, 0));

    // MARK: Helpers

    private static Entry? Find(List<Entry> entries, string id)
    {
        foreach (var entry in entries)
            if (entry.Step.Id == id) return entry;
        return null;
    }

    private static List<string> Flatten(List<Entry> auto, List<List<Entry>> clicks, List<Entry> exit) =>
        [.. auto.Select(e => e.Step.Id), .. clicks.Where(c => c.Count > 0).SelectMany(c => c).Select(e => e.Step.Id), .. exit.Select(e => e.Step.Id)];

    private static double Start(IReadOnlyDictionary<string, (double Start, double Duration)> starts, string id) =>
        starts.TryGetValue(id, out var slot) ? slot.Start : 0;

    private static double Duration(IReadOnlyDictionary<string, (double Start, double Duration)> starts, string id) =>
        starts.TryGetValue(id, out var slot) ? slot.Duration : 0;

    private static void Apply(List<SlideObject> working, string id, StepTiming timing)
    {
        foreach (var o in working)
        {
            if (o.AnimationSteps?.FirstOrDefault(s => s.Id == id) is not { } step) continue;
            step.Trigger = timing.Trigger;
            step.DelaySeconds = timing.DelaySeconds;
        }
    }
}
