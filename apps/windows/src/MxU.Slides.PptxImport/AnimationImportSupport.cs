using MxU.Slides.Core.Model;

namespace MxU.Slides.PptxImport;

/// <summary>
/// Port of the Mac's PresenterCore AnimationImportSupport, kept here until Core
/// gets its Animation port. Edge moves that fly in together get one shared
/// offset so the group travels as a rigid block instead of each piece from its own edge.
/// </summary>
internal static class AnimationImportSupport
{
    private readonly record struct Key(AnimationKind Kind, AnimationEdge Edge, int DurationMilli);

    public static void RigidifyMoves(List<SlideObject> objects, double canvasWidth, double canvasHeight)
    {
        var clusters = new Dictionary<Key, List<(int Object, int Step)>>();
        for (var o = 0; o < objects.Count; o++)
        {
            var steps = objects[o].AnimationSteps ?? [];
            for (var s = 0; s < steps.Count; s++)
            {
                var step = steps[s];
                if (step.Animation != StepAnimation.Move || step.Ranges is { Count: > 0 } || step.Edge is not { } edge) continue;
                var key = new Key(step.Kind, edge, (int)Math.Round(step.DurationSeconds * 1000, MidpointRounding.AwayFromZero));
                if (!clusters.TryGetValue(key, out var members)) clusters[key] = members = [];
                members.Add((o, s));
            }
        }
        foreach (var (key, members) in clusters)
        {
            if (members.Count <= 1) continue;
            double dx = 0, dy = 0;
            foreach (var (o, _) in members)
            {
                var item = objects[o];
                var x = item.X ?? 0;
                var y = item.Y ?? 0;
                var width = item.Width ?? canvasWidth;
                var height = item.Height ?? canvasHeight;
                switch (key.Edge)
                {
                    case AnimationEdge.Left: dx = Math.Min(dx, -(x + width)); break;
                    case AnimationEdge.Right: dx = Math.Max(dx, canvasWidth - x); break;
                    case AnimationEdge.Top: dy = Math.Min(dy, -(y + height)); break;
                    case AnimationEdge.Bottom: dy = Math.Max(dy, canvasHeight - y); break;
                }
            }

            dx = Math.Round(dx * 1000, MidpointRounding.AwayFromZero) / 1000;
            dy = Math.Round(dy * 1000, MidpointRounding.AwayFromZero) / 1000;
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
