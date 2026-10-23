using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Animation;

/// <summary>
/// The editor's animation timeline as geometry: a column per group, a row per
/// object with a block per step and a presence bar. Port of SlideScene/AnimationTimelineLayout.swift.
/// </summary>
public static class AnimationTimelineLayout
{
    /// <param name="DisplayDuration">Never narrower than <see cref="MinimumColumnSeconds"/>, so an empty click stays clickable.</param>
    public sealed record Column(SceneAnimationGroup Group, double Start, double Duration, double DisplayDuration);

    public sealed record Block(
        string StepId, string ObjectId, AnimationKind Kind, SceneAnimationGroup Group,
        double StartOffset, double Duration, bool RampIn, bool RampOut, bool Linked, bool Ranged);

    public sealed record Row(string ObjectId, IReadOnlyList<Block> Blocks, double? PresenceStart = null, double? PresenceEnd = null);

    public sealed record Timeline(
        IReadOnlyList<Column> Columns, IReadOnlyList<Row> Rows, IReadOnlyList<double> ClickTimes, double DismissAt, double Duration);

    public const double MinimumColumnSeconds = 0.6;

    public static Timeline TimelineOf(IReadOnlyList<SlideObject> objects, IReadOnlyList<string>? order)
    {
        var preview = AnimationSequence.PreviewTimeline(objects, order);
        var scheduled = AnimationSequence.SceneSteps(objects, order);
        var allSteps = scheduled.Values.SelectMany(s => s).ToList();

        var columns = new List<Column>();
        void AddColumn(SceneAnimationGroup group, double start)
        {
            var duration = AnimationTimeline.GroupDuration(group, allSteps);
            columns.Add(new Column(group, start, duration, duration > 0 ? duration : MinimumColumnSeconds));
        }
        AddColumn(SceneAnimationGroup.Auto, 0);
        for (var index = 0; index < preview.Clicks.Count; index++) AddColumn(SceneAnimationGroup.Click(index), preview.Clicks[index]);
        AddColumn(SceneAnimationGroup.Exit, preview.DismissAt);

        var blocksByObject = new Dictionary<string, List<Block>>();
        foreach (var entry in AnimationSequence.OrderedEntries(objects, order))
        {
            if (!scheduled.TryGetValue(entry.ObjectId, out var steps) || steps.FirstOrDefault(s => s.Id == entry.Step.Id) is not { } scene) continue;
            if (!blocksByObject.TryGetValue(entry.ObjectId, out var blocks)) blocksByObject[entry.ObjectId] = blocks = [];
            blocks.Add(new Block(
                entry.Step.Id, entry.ObjectId, entry.Step.Kind, scene.Group, scene.StartOffset, scene.Duration,
                RampIn: scene.Ramp is SceneAnimationRamp.EaseIn or SceneAnimationRamp.EaseInOut,
                RampOut: scene.Ramp is SceneAnimationRamp.EaseOut or SceneAnimationRamp.EaseInOut,
                Linked: entry.Step.Trigger == AnimationTrigger.AfterPrevious,
                Ranged: entry.Step.Ranges is { Count: > 0 }));
        }

        var rows = new List<Row>();
        foreach (var o in objects.Where(o => o.Hidden != true))
        {
            var blocks = blocksByObject.GetValueOrDefault(o.Id) ?? [];
            var whole = blocks.Where(b => !b.Ranged).ToList();
            double? presenceStart = whole.FirstOrDefault(b => b.Kind == AnimationKind.In) is { } firstIn ? Start(firstIn, preview) : null;
            double? presenceEnd = whole.LastOrDefault(b => b.Kind == AnimationKind.Out) is { } lastOut ? Start(lastOut, preview) + lastOut.Duration : null;
            rows.Add(new Row(o.Id, blocks, presenceStart, presenceEnd));
        }
        return new Timeline(columns, rows, preview.Clicks, preview.DismissAt, preview.Duration);
    }

    internal static double? Start(Block block, AnimationSequence.Preview preview) => block.Group.Kind switch
    {
        SceneAnimationGroupKind.Auto => block.StartOffset,
        SceneAnimationGroupKind.Click => block.Group.Index < preview.Clicks.Count ? preview.Clicks[block.Group.Index] + block.StartOffset : null,
        _ => preview.DismissAt + block.StartOffset,
    };

    /// <summary>Whether a morph's end state moves or reshapes the object (labelled Morph) rather than only restyling it (Change).</summary>
    public static bool MorphMoves(AnimationStep step, SlideObject? o)
    {
        if (step.Kind != AnimationKind.Morph || step.ToObject is not { } to || o is null) return false;
        static bool D(double? a, double? b) => (a ?? 0) != (b ?? 0);
        return D(to.X, o.X) || D(to.Y, o.Y) || D(to.Width, o.Width) || D(to.Height, o.Height)
            || D(to.RotationDegrees, o.RotationDegrees) || D(to.Tilt, o.Tilt) || D(to.Swing, o.Swing)
            || D(to.SkewX, o.SkewX) || D(to.SkewY, o.SkewY) || D(to.KeystoneTop, o.KeystoneTop) || D(to.KeystoneBottom, o.KeystoneBottom);
    }
}
