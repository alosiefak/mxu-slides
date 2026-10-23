using MxU.Slides.Core.Model;

namespace MxU.Slides.ProImport;

/// <summary>The slides one pass of a section plays, in order (PresenterCore's TimelinePlayedBlock).</summary>
internal sealed class TimelinePlayedBlock(string? sectionId, List<string> slideIds)
{
    public string? SectionId { get; } = sectionId;
    public List<string> SlideIds { get; } = slideIds;
}

/// <summary>The parts of PresenterCore's PresentationTimelines the importer needs.</summary>
internal static class PresentationTimelines
{
    public const int FramesPerSecond = 30;
    public const string SlideLane = "slide";
    public const string ActionsLane = "actions";
    public const string AudioLane = "audio";

    public static double SnappedToFrame(double seconds) => SwiftCompat.Rounded(seconds * FramesPerSecond) / FramesPerSecond;

    private static (string? SectionId, int SectionOccurrence, string SlideId)? Anchor(int occurrence, List<TimelinePlayedBlock> blocks)
    {
        var index = 0;
        var seen = new Dictionary<string, int>();
        foreach (var block in blocks)
        {
            var key = block.SectionId ?? "\0";
            var sectionOccurrence = seen.GetValueOrDefault(key);
            seen[key] = sectionOccurrence + 1;
            if (occurrence < index + block.SlideIds.Count)
            {
                if (occurrence < index) return null;
                return (block.SectionId, sectionOccurrence, block.SlideIds[occurrence - index]);
            }
            index += block.SlideIds.Count;
        }
        return null;
    }

    public static (List<TimelineCue> Cues, int Missing) SlideCues(
        List<(double At, string SlideId)> fires, List<TimelinePlayedBlock> blocks, string idPrefix)
    {
        var order = blocks.SelectMany(b => b.SlideIds).ToList();
        var next = 0;
        var cues = new List<TimelineCue>();
        var missing = 0;
        var index = 0;
        foreach (var fire in fires.OrderBy(f => f.At))
        {
            var from = Math.Min(next, order.Count);
            var found = order.IndexOf(fire.SlideId, from);
            if (found < 0) found = order.LastIndexOf(fire.SlideId);
            if (found < 0 || Anchor(found, blocks) is not { } anchor)
            {
                missing++;
                index++;
                continue;
            }
            next = Math.Max(next, found + 1);
            cues.Add(new TimelineCue
            {
                Id = idPrefix + index, Lane = SlideLane, At = SnappedToFrame(Math.Max(fire.At, 0)),
                SectionId = anchor.SectionId, SectionOccurrence = anchor.SectionOccurrence, SlideId = anchor.SlideId,
            });
            index++;
        }
        return (cues, missing);
    }
}

internal static class AnimationImportSupport
{
    /// <summary>
    /// Moves that fly several objects off the same edge together become one shared offset, so
    /// the group travels as a rigid block instead of each object stopping at its own edge.
    /// </summary>
    public static void RigidifyMoves(List<SlideObject> objects, double canvasWidth, double canvasHeight)
    {
        var clusters = new Dictionary<(AnimationKind, AnimationEdge, int), List<(int Object, int Step)>>();
        for (var o = 0; o < objects.Count; o++)
        {
            var steps = objects[o].AnimationSteps ?? [];
            for (var s = 0; s < steps.Count; s++)
            {
                var step = steps[s];
                if (step.Animation != StepAnimation.Move || step.Ranges is { Count: > 0 } || step.Edge is not { } edge) continue;
                var key = (step.Kind, edge, (int)SwiftCompat.Rounded(step.DurationSeconds * 1000));
                if (!clusters.TryGetValue(key, out var members)) clusters[key] = members = [];
                members.Add((o, s));
            }
        }
        foreach (var ((_, edge, _), members) in clusters)
        {
            if (members.Count <= 1) continue;
            double dx = 0, dy = 0;
            foreach (var (o, _) in members)
            {
                var obj = objects[o];
                var x = obj.X ?? 0;
                var y = obj.Y ?? 0;
                var maxX = x + (obj.Width ?? canvasWidth);
                var maxY = y + (obj.Height ?? canvasHeight);
                switch (edge)
                {
                    case AnimationEdge.Left: dx = Math.Min(dx, -maxX); break;
                    case AnimationEdge.Right: dx = Math.Max(dx, canvasWidth - x); break;
                    case AnimationEdge.Top: dy = Math.Min(dy, -maxY); break;
                    case AnimationEdge.Bottom: dy = Math.Max(dy, canvasHeight - y); break;
                }
            }
            dx = SwiftCompat.Rounded(dx * 1000) / 1000;
            dy = SwiftCompat.Rounded(dy * 1000) / 1000;
            foreach (var (o, s) in members)
            {
                var step = objects[o].AnimationSteps![s];
                step.Edge = null;
                step.OffsetX = dx == 0 ? null : dx;
                step.OffsetY = dy == 0 ? null : dy;
            }
        }
    }
}
