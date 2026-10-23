using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Animation;

/// <summary>Where a rolling text block sits after some seconds. Port of RenderEngine/BlockScrollRoll.swift.</summary>
public static class BlockScrollRoll
{
    /// <summary>The block's travel along its pass, or null once a finite roll has gone.</summary>
    public static double? Pose(SceneBlockScroll scroll, double elapsed, double passLength, double restOffset, bool anchored)
    {
        if (scroll.Speed <= 0 || passLength <= 0) return 0;
        var period = passLength / scroll.Speed;
        var t = Math.Max(elapsed, 0);
        if (anchored && scroll.Passes > 0 && t >= period * scroll.Passes)
            return scroll.RestAtEnd ? restOffset : null;
        var phase = t / period;
        var inPass = phase - Math.Floor(phase);
        return scroll.Ramp.Apply(inPass) * passLength;
    }

    public sealed record Resolved(AnimatedItem Item, SceneVector Translate, MotionClip Clip);

    /// <summary>
    /// The block expanded to its full height and the offset/clip that roll it through
    /// the frame; null when it has rolled away. <paramref name="blockHeight"/> is the laid-out text height.
    /// </summary>
    public static Resolved? Resolve(AnimatedItem item, StyledText text, SceneBlockScroll scroll, double blockHeight, double elapsed, bool anchored)
    {
        var frame = item.Render.Frame;
        if (scroll.Speed <= 0 || frame.Width <= 0 || frame.Height <= 0) return null;
        var content = text;
        var expandedFrame = frame;
        var vertical = scroll.Axis is SceneScrollAxis.Up or SceneScrollAxis.Down;

        var blockH = vertical ? Math.Max(frame.Height, Math.Ceiling(blockHeight)) : frame.Height;
        if (vertical && blockH > frame.Height)
        {
            content = content with { VerticalAlignment = SceneTextVerticalAlignment.Top };
            expandedFrame = frame with { Height = blockH };
        }
        var expanded = item with
        {
            Render = item.Render with { Frame = expandedFrame, Content = new ItemContent.Text(content) },
            Scroll = null,
        };

        var passLength = vertical ? frame.Height + blockH : frame.Width * 2;
        var restOffset = vertical ? blockH : frame.Width;
        if (Pose(scroll, elapsed, passLength, restOffset, anchored) is not { } d) return null;

        var fade = Math.Clamp(scroll.FadeTowardTop, 0, 1);
        var visible = frame.Height / blockH;
        switch (scroll.Axis)
        {
            case SceneScrollAxis.Up:
            {
                var dy = frame.Height - d;
                var minV = -dy / blockH;
                return new(expanded, new SceneVector(0, dy), new MotionClip { MinV = minV, MaxV = minV + visible, FeatherMinV = fade * visible });
            }
            case SceneScrollAxis.Down:
            {
                var dy = -blockH + d;
                var minV = -dy / blockH;
                return new(expanded, new SceneVector(0, dy), new MotionClip { MinV = minV, MaxV = minV + visible, FeatherMaxV = fade * visible });
            }
            case SceneScrollAxis.Left:
            {
                var dx = frame.Width - d;
                var minU = -dx / frame.Width;
                return new(expanded, new SceneVector(dx, 0), new MotionClip { MinU = minU, MaxU = minU + 1, FeatherMinU = fade });
            }
            default:
            {
                var dx = -frame.Width + d;
                var minU = -dx / frame.Width;
                return new(expanded, new SceneVector(dx, 0), new MotionClip { MinU = minU, MaxU = minU + 1, FeatherMaxU = fade });
            }
        }
    }
}
