using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Animation;

/// <summary>
/// Video push: an animation step on a slide/overlay object that squeezes the
/// video layers into the space the object leaves free. Port of RenderEngine/VideoPushEvaluator.swift.
/// </summary>
public static class VideoPushEvaluator
{
    public static readonly IReadOnlySet<LayerKind> PushedLayers =
        new HashSet<LayerKind> { LayerKind.VideoInput, LayerKind.LoopingVideos, LayerKind.StillGraphics, LayerKind.Videos };

    /// <param name="Amount">0 = the full-frame picture, 1 = fully pushed.</param>
    /// <param name="Target">Where the picture lands when fully pushed.</param>
    public sealed record State(SceneVideoPush Push, double Amount, SceneRect Target);

    /// <param name="Clip">The window the picture shows through, in canvas units.</param>
    public sealed record Transform(double Scale, SceneVector Offset, SceneRect? Clip, double BlurRadius);

    /// <summary>The push in force: the most recently fired item whose push step has started.</summary>
    public static State? ActiveState(AnimatedScene scene, double now)
    {
        (double Anchor, State State)? best = null;
        foreach (var layer in scene.Layers)
        {
            if (layer.IsHidden || PushedLayers.Contains(layer.Kind)) continue;
            foreach (var item in layer.Items)
            {
                if (item.AnimationContext is not { } context || item.AnimationSteps.Count == 0) continue;
                double? amount = null;
                SceneVideoPush? push = null;
                foreach (var step in item.AnimationSteps)
                {
                    if (step.VideoPush is not { } stepPush) continue;
                    var phase = AnimationTimeline.Phase(step, context, now);
                    if (phase.IsRunning(out var p))
                        amount = step.Kind == SceneAnimationKind.Exit ? 1 - p : p;
                    else if (phase.IsDone)
                        amount = step.Kind == SceneAnimationKind.Exit ? 0 : 1;
                    else
                        continue;
                    push = stepPush;
                }
                if (amount is not { } a || a <= 0.0001 || push is null) continue;
                var state = new State(push, Math.Min(a, 1), FreeRect(item.Render.Frame, scene.CanvasSize, push.Margin));
                if (best is null || context.AnchorHostTime > best.Value.Anchor) best = (context.AnchorHostTime, state);
            }
        }
        return best?.State;
    }

    /// <summary>The largest band of the canvas beside the object, inset by the margin.</summary>
    public static SceneRect FreeRect(SceneRect frame, SceneSize canvas, double margin)
    {
        var full = new SceneRect(canvas);
        var clipped = Intersection(frame, full);
        if (IsEmpty(clipped)) return full.Inset(margin, margin);
        SceneRect[] candidates =
        [
            new(0, 0, clipped.X, canvas.Height),
            new(clipped.Right, 0, canvas.Width - clipped.Right, canvas.Height),
            new(0, 0, canvas.Width, clipped.Y),
            new(0, clipped.Bottom, canvas.Width, canvas.Height - clipped.Bottom),
        ];
        var bestRect = candidates.MaxBy(r => r.Width * r.Height);
        var inset = bestRect.Inset(margin, margin);
        return IsEmpty(inset) || bestRect.Width * bestRect.Height < 1 ? full : inset;
    }

    public static Transform TransformFor(SceneSize canvas, State state)
    {
        var push = state.Push;
        var amount = Math.Clamp(state.Amount, 0, 1);
        double Lerp(double a, double b) => a + (b - a) * amount;
        if (push.Mode == SceneVideoPushMode.BlurBackground)
        {
            var zoomed = Lerp(1, Math.Max(push.Zoom, 1));
            var centred = new SceneVector(canvas.Width / 2 * (1 - zoomed), canvas.Height / 2 * (1 - zoomed));
            return new Transform(zoomed, centred, null, Lerp(0, Math.Max(push.BlurRadius, 0)));
        }

        var rect = new SceneRect(
            Lerp(0, state.Target.X), Lerp(0, state.Target.Y),
            Lerp(canvas.Width, state.Target.Width), Lerp(canvas.Height, state.Target.Height));
        double ratioW = rect.Width / canvas.Width, ratioH = rect.Height / canvas.Height;
        var fit = push.Mode == SceneVideoPushMode.Fit ? Math.Min(ratioW, ratioH) : Math.Max(ratioW, ratioH);
        var scale = fit * Lerp(1, Math.Max(push.Zoom, 0.05));
        double drawnW = canvas.Width * scale, drawnH = canvas.Height * scale;
        var dx = push.Alignment switch
        {
            SceneVideoPushAlignment.Left => rect.X,
            SceneVideoPushAlignment.Right => rect.Right - drawnW,
            _ => rect.X + rect.Width / 2 - drawnW / 2,
        };
        var dy = push.Alignment switch
        {
            SceneVideoPushAlignment.Top => rect.Y,
            SceneVideoPushAlignment.Bottom => rect.Bottom - drawnH,
            _ => rect.Y + rect.Height / 2 - drawnH / 2,
        };
        return new Transform(scale, new SceneVector(dx, dy), rect, 0);
    }

    /// <summary>The blurred full-frame copy that fills the space the pushed picture vacates.</summary>
    public static Transform BackdropTransform(SceneSize canvas, State state)
    {
        var amount = Math.Clamp(state.Amount, 0, 1);
        var scale = 1 + (Math.Max(state.Push.Zoom, 1.12) - 1) * amount;
        return new Transform(
            scale,
            new SceneVector(canvas.Width / 2 * (1 - scale), canvas.Height / 2 * (1 - scale)),
            null,
            Math.Max(state.Push.BlurRadius, 0) * amount);
    }

    public static ResolvedItem Apply(Transform transform, ResolvedItem resolved)
    {
        var frame = resolved.Item.Render.Frame;
        double centerX = frame.X + frame.Width / 2, centerY = frame.Y + frame.Height / 2;
        var motion = resolved.Motion with
        {
            Scale = resolved.Motion.Scale * transform.Scale,
            Translate = resolved.Motion.Translate + new SceneVector(
                transform.Offset.Dx + (transform.Scale - 1) * centerX,
                transform.Offset.Dy + (transform.Scale - 1) * centerY),
        };
        if (transform.Clip is { } clip && frame.Width > 0 && frame.Height > 0)
        {
            var s = transform.Scale;
            var originX = transform.Offset.Dx + s * frame.X;
            var originY = transform.Offset.Dy + s * frame.Y;
            motion = motion with
            {
                Clip = new MotionClip
                {
                    MinU = (clip.X - originX) / (frame.Width * s),
                    MaxU = (clip.Right - originX) / (frame.Width * s),
                    MinV = (clip.Y - originY) / (frame.Height * s),
                    MaxV = (clip.Bottom - originY) / (frame.Height * s),
                },
            };
        }
        var item = transform.BlurRadius > 0.5
            ? resolved.Item with { Effects = [new AnimationEffect.Blur(transform.BlurRadius), .. resolved.Item.Effects] }
            : resolved.Item;
        return new ResolvedItem(item, motion);
    }

    internal static SceneRect Intersection(SceneRect a, SceneRect b)
    {
        double x = Math.Max(a.X, b.X), y = Math.Max(a.Y, b.Y);
        double right = Math.Min(a.Right, b.Right), bottom = Math.Min(a.Bottom, b.Bottom);
        return right < x || bottom < y ? default : new SceneRect(x, y, right - x, bottom - y);
    }

    internal static bool IsEmpty(SceneRect rect) => rect.Width <= 0 || rect.Height <= 0;
}
