using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using Point = MxU.Slides.Core.Scene.EditorGeometry.Point;

namespace MxU.Slides.Core.Animation;

/// <summary>
/// Where an item's corners land once its 3-D pose is applied: keystone (the top and bottom
/// edges drawn narrower or wider), skew, tilt (about the horizontal axis, around a pivot), swing
/// (about the vertical axis), then perspective, then the move and scale. Port of the Mac's
/// Compositor.BuildProjector, buildQuadCorners and stretchKeystoneVertices.
/// <para>
/// A point is given in the item's own unit square (u across, v down). Keystone scales each row's
/// width by <c>top + (bottom − top)·v</c>, so a keystone of 50% at the top makes a trapezoid.
/// </para>
/// <para>
/// The Mac draws the four projected corners with a projective (perspective-correct) mapping, or, in
/// "stretch" keystone mode, as 32 horizontal strips that each map straight across, so rows stay
/// evenly spaced. The Windows renderer does the same on the graphics card (Rendering/KeystoneEffect.cs).
/// </para>
/// </summary>
public readonly struct BuildProjector
{
    private readonly double centerX, centerY, dx, dy, halfW, halfH, cosT, sinT, cosS, sinS, pivotY, keyTop, keyBottom, shearX, shearY, focal;

    /// <param name="sceneScale">Canvas units to target units (pixels on the Mac; 1 in canvas units).</param>
    /// <param name="targetSize">The target's size: the perspective's focal distance is 1.5× its larger side.</param>
    public BuildProjector(SceneRect contentRect, AnimationMotion motion, double sceneScale, SceneSize targetSize)
    {
        centerX = contentRect.X + contentRect.Width / 2;
        centerY = contentRect.Y + contentRect.Height / 2;
        dx = motion.Translate.Dx * sceneScale;
        dy = motion.Translate.Dy * sceneScale;
        halfW = contentRect.Width / 2 * motion.Scale;
        halfH = contentRect.Height / 2 * (motion.ScaleY ?? motion.Scale);
        var theta = motion.Tilt * Math.PI / 180;
        cosT = Math.Cos(theta);
        sinT = Math.Sin(theta);
        var phi = motion.Swing * Math.PI / 180;
        cosS = Math.Cos(phi);
        sinS = Math.Sin(phi);
        pivotY = motion.TiltPivot switch
        {
            TiltPivot.Top => -halfH,
            TiltPivot.Bottom => halfH,
            _ => 0,
        };
        keyTop = motion.KeystoneTop;
        keyBottom = motion.KeystoneBottom;
        shearX = Math.Tan(Math.Clamp(motion.SkewX, -85, 85) * Math.PI / 180);
        shearY = Math.Tan(Math.Clamp(motion.SkewY, -85, 85) * Math.PI / 180);
        focal = FocalDistance(targetSize);
    }

    public static double FocalDistance(SceneSize targetSize) => Math.Max(targetSize.Width, targetSize.Height) * 1.5;

    /// <summary>Where the item's unit-square point (u, v) lands on the target.</summary>
    public Point Project(double u, double v)
    {
        var edgeScale = keyTop + (keyBottom - keyTop) * v;
        var kx = (u * 2 - 1) * halfW * edgeScale;
        var ky = (v * 2 - 1) * halfH;

        var rx = kx - ky * shearX;
        var ry = ky + kx * shearY;

        var ay = ry - pivotY;
        var z = -ay * sinT;
        var py = pivotY + ay * cosT;

        var px = rx * cosS - z * sinS;
        z = rx * sinS + z * cosS;
        var depth = Math.Max(focal + z, 1);
        var k = focal / depth;
        return new Point(centerX + dx + px * k, centerY + dy + py * k);
    }

    /// <summary>The four corners in the Mac's order: top-left, top-right, bottom-left, bottom-right.</summary>
    public static Point[] QuadCorners(SceneRect contentRect, AnimationMotion motion, double sceneScale, SceneSize targetSize)
    {
        var projector = new BuildProjector(contentRect, motion, sceneScale, targetSize);
        return [projector.Project(0, 0), projector.Project(1, 0), projector.Project(0, 1), projector.Project(1, 1)];
    }

    /// <summary>How many strips a stretch keystone is drawn in.</summary>
    public const int StretchKeystoneStrips = 32;

    /// <summary>
    /// A stretch keystone as triangles: each strip's corners projected, two triangles a strip
    /// (top-left, bottom-left, bottom-right, top-left, bottom-right, top-right), with their
    /// unit-square (u, v). v rides linearly down the strips, so rows stay evenly spaced.
    /// </summary>
    public static List<(Point Position, double U, double V)> StretchKeystoneVertices(SceneRect contentRect, AnimationMotion motion, double sceneScale, SceneSize targetSize)
    {
        var projector = new BuildProjector(contentRect, motion, sceneScale, targetSize);
        (Point, double, double) Vertex(double u, double v) => (projector.Project(u, v), u, v);
        var vertices = new List<(Point, double, double)>(StretchKeystoneStrips * 6);
        for (var i = 0; i < StretchKeystoneStrips; i++)
        {
            var v0 = (double)i / StretchKeystoneStrips;
            var v1 = (double)(i + 1) / StretchKeystoneStrips;
            var tl = Vertex(0, v0);
            var tr = Vertex(1, v0);
            var bl = Vertex(0, v1);
            var br = Vertex(1, v1);
            vertices.AddRange([tl, bl, br, tl, br, tr]);
        }
        return vertices;
    }

    /// <summary>
    /// Just the keystone, in the item's own frame (no move, skew, tilt or swing): the trapezoid the
    /// Windows renderer warps the item into before its ordinary 2-D transform is applied.
    /// </summary>
    public static Point[] KeystoneCorners(SceneRect frame, double keystoneTop, double keystoneBottom) =>
        QuadCorners(frame, new AnimationMotion { KeystoneTop = keystoneTop, KeystoneBottom = keystoneBottom }, 1, new SceneSize(1, 1));

    /// <summary>True when the item is keystoned (either edge not 100%).</summary>
    public static bool HasKeystone(AnimationMotion motion) => motion.KeystoneTop != 1 || motion.KeystoneBottom != 1;
}
