using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Outputs;

/// <summary>
/// What the correction pass needs to do for one output, from
/// <see cref="OutputPass.Plan"/>: draw the scene into an intermediate image of
/// the given size, then warp the <see cref="SourceRect"/> part of it into the
/// target (into <see cref="Placement"/> when set) with these adjustments.
/// </summary>
public sealed record OutputPassPlan(
    OutputAdjustments Adjustments,
    SceneRect SourceRect,
    SceneRect? Placement,
    int IntermediateWidth,
    int IntermediateHeight);

/// <summary>One edge's blend as the shader reads it, already clamped. An edge that is off is (0, 1, 1, 0).</summary>
public readonly record struct OutputEdgeUniform(double Width, double Curve, double Intensity, double BlackLift)
{
    public static readonly OutputEdgeUniform Off = new(0, 1, 1, 0);
}

/// <summary>The correction pass's shader constants (the Mac's OutputAdjustUniformsData, in doubles).</summary>
public sealed record OutputPassUniforms(
    double Brightness, double Contrast, double Gamma, double BlackLevel,
    double RedLevel, double GreenLevel, double BlueLevel, bool HasMask,
    OutputEdgeUniform BlendLeft, OutputEdgeUniform BlendRight, OutputEdgeUniform BlendTop, OutputEdgeUniform BlendBottom,
    SceneRect SourceRect);

/// <summary>A premultiplied RGBA pixel, 0–1 per channel (what the correction pass reads and writes).</summary>
public readonly record struct OutputPixel(double R, double G, double B, double A)
{
    public static readonly OutputPixel Transparent = new(0, 0, 0, 0);
}

/// <summary>
/// The geometry of one correction pass, in the target's unit square (0–1, y
/// down) so a pixel shader can use it directly: the four corners the picture
/// lands on, and the homography that maps a target point back to the canvas uv
/// it shows. Per pixel: <c>q = h6·x + h7·y + h8</c>, <c>uv = (h0·x + h1·y + h2, h3·x + h4·y + h5) / q</c>.
/// The matrix is <see cref="OutputWarp.CenteredHomography"/>: q = 1 at the
/// picture's centre and positive all over it, so clamping q is safe; the pixel
/// is inside the picture exactly when uv falls inside the source rect.
/// <see cref="Homography"/> is null for a degenerate quad; then draw
/// the two triangles (TL, TR, BL) and (TR, BR, BL) with
/// <see cref="SourceCorners"/> as plain uvs, as the Mac does.
/// </summary>
public sealed record OutputPassGeometry(
    IReadOnlyList<(double X, double Y)> UnitCorners,
    IReadOnlyList<(double X, double Y)> SourceCorners,
    double[]? Homography)
{
    /// <summary>The canvas uv shown at a target point (unit coordinates); null only on the warp's horizon.</summary>
    public (double U, double V)? SourceUv(double x, double y) =>
        Homography is { } h ? OutputWarp.Map(h, x, y) : null;
}

/// <summary>
/// The platform-neutral half of the Mac's last render pass
/// (Compositor.encodeOutputPass and outputPassPlan in OutputAdjust.swift /
/// Compositor.swift, and output_adjust_fragment in ShaderSource.swift): when
/// the pass is needed, where the corners land after rotation and corner pin,
/// the clamped shader constants, and a CPU reference of the pixel maths that
/// the WPF shader must reproduce. Drawing stays in the app.
/// </summary>
public static class OutputPass
{
    /// <summary>The whole canvas, as a unit rect. A slice or placement equal to this means "no crop" / "fill".</summary>
    public static readonly SceneRect FullSourceRect = new(0, 0, 1, 1);

    /// <summary>
    /// Whether this output needs the correction pass at all, and at what size
    /// to draw the scene first. Null = draw the scene straight into the target.
    /// When the output shows a crop of the canvas or lands in part of the frame,
    /// the scene is drawn at the size that gives the visible part its full
    /// resolution, so a slice is never upscaled from a small render.
    /// </summary>
    public static OutputPassPlan? Plan(
        OutputAdjustments? adjustments, SceneRect? sourceRect, SceneRect? placement, bool hasMask,
        SceneSize canvasSize, int targetWidth, int targetHeight)
    {
        SceneRect? crop = sourceRect is { } s && s != FullSourceRect ? s : null;
        SceneRect? land = placement is { } p && p != FullSourceRect ? p : null;
        var corrections = adjustments is { IsNeutral: false } ? adjustments : null;
        if (crop is null && corrections is null && !hasMask && land is null) return null;
        var rect = crop ?? FullSourceRect;
        if (crop is null && land is null)
            return new OutputPassPlan(corrections ?? new OutputAdjustments(), rect, null, targetWidth, targetHeight);

        var canvasWidth = Math.Max(canvasSize.Width, 1);
        var canvasHeight = Math.Max(canvasSize.Height, 1);
        var contentWidth = targetWidth * (land?.Width ?? 1);
        var contentHeight = targetHeight * (land?.Height ?? 1);
        var scale = Math.Max(Math.Max(
            contentWidth / (rect.Width * canvasWidth),
            contentHeight / (rect.Height * canvasHeight)), 0.01);
        return new OutputPassPlan(
            corrections ?? new OutputAdjustments(), rect, land,
            Math.Max(1, (int)Math.Round(canvasWidth * scale, MidpointRounding.AwayFromZero)),
            Math.Max(1, (int)Math.Round(canvasHeight * scale, MidpointRounding.AwayFromZero)));
    }

    /// <summary>
    /// Where the picture's four corners land in the target, in pixels (y down),
    /// ordered top-left, top-right, bottom-left, bottom-right. The picture first
    /// fills <paramref name="placement"/> (a unit rect of the target; null = all
    /// of it), is then turned by the rotation and shrunk so the turned picture
    /// still fits that rect, and finally each corner moves by its corner-pin offset.
    /// </summary>
    public static (double X, double Y)[] PixelCorners(OutputAdjustments adjustments, SceneRect? placement, int targetWidth, int targetHeight)
    {
        double width = targetWidth, height = targetHeight;
        var placed = placement ?? FullSourceRect;
        var dest = new SceneRect(placed.X * width, placed.Y * height, placed.Width * width, placed.Height * height);
        var midX = dest.X + dest.Width / 2;
        var midY = dest.Y + dest.Height / 2;
        var theta = (adjustments.RotationDegrees ?? 0) * Math.PI / 180;
        var cos = Math.Cos(theta);
        var sin = Math.Sin(theta);
        // The turned picture's bounding box, and the per-axis scale that fits it back into the rect.
        var boundsW = dest.Width * Math.Abs(cos) + dest.Height * Math.Abs(sin);
        var boundsH = dest.Width * Math.Abs(sin) + dest.Height * Math.Abs(cos);
        var scaleX = boundsW > 0 ? dest.Width / boundsW : 1;
        var scaleY = boundsH > 0 ? dest.Height / boundsH : 1;

        (double X, double Y) Corner(double restX, double restY, OutputPoint? offset)
        {
            var vx = restX - midX;
            var vy = restY - midY;
            var rx = (vx * cos - vy * sin) * scaleX;
            var ry = (vx * sin + vy * cos) * scaleY;
            return (midX + rx + (offset?.X ?? 0), midY + ry + (offset?.Y ?? 0));
        }

        return
        [
            Corner(dest.X, dest.Y, adjustments.TopLeft),
            Corner(dest.Right, dest.Y, adjustments.TopRight),
            Corner(dest.X, dest.Bottom, adjustments.BottomLeft),
            Corner(dest.Right, dest.Bottom, adjustments.BottomRight),
        ];
    }

    /// <summary>The same corners in the target's unit square (0–1, y down): what a WPF pixel shader works in.</summary>
    public static (double X, double Y)[] UnitCorners(OutputAdjustments adjustments, SceneRect? placement, int targetWidth, int targetHeight) =>
        PixelCorners(adjustments, placement, targetWidth, targetHeight)
            .Select(p => (p.X / targetWidth, p.Y / targetHeight))
            .ToArray();

    /// <summary>The same corners in Metal/Direct3D clip space (−1…1, y up), exactly the Mac's vertex positions.</summary>
    public static (double X, double Y)[] NdcCorners(OutputAdjustments adjustments, SceneRect? placement, int targetWidth, int targetHeight) =>
        PixelCorners(adjustments, placement, targetWidth, targetHeight)
            .Select(p => (p.X / targetWidth * 2 - 1, 1 - p.Y / targetHeight * 2))
            .ToArray();

    /// <summary>The canvas uvs the four corners show: the slice's corners, in the same order.</summary>
    public static (double X, double Y)[] SourceCorners(SceneRect sourceRect) =>
    [
        (sourceRect.X, sourceRect.Y),
        (sourceRect.Right, sourceRect.Y),
        (sourceRect.X, sourceRect.Bottom),
        (sourceRect.Right, sourceRect.Bottom),
    ];

    /// <summary>Everything the pixel shader needs to place the picture, in unit space.</summary>
    public static OutputPassGeometry Geometry(OutputAdjustments adjustments, SceneRect sourceRect, SceneRect? placement, int targetWidth, int targetHeight)
    {
        var corners = UnitCorners(adjustments, placement, targetWidth, targetHeight);
        var sources = SourceCorners(sourceRect);
        return new OutputPassGeometry(corners, sources, OutputWarp.CenteredHomography(corners, sources));
    }

    /// <summary>One edge's blend, clamped the way the Mac clamps it before handing it to the shader.</summary>
    public static OutputEdgeUniform Edge(OutputEdgeBlend? blend)
    {
        if (blend is null || blend.Width <= 0) return OutputEdgeUniform.Off;
        return new OutputEdgeUniform(
            Math.Clamp(blend.Width, 0, 1),
            blend.Curve > 0 ? blend.Curve : 1,
            Math.Clamp(blend.Intensity ?? 1, 0, 1),
            Math.Clamp(blend.BlackLift ?? 0, 0, 1));
    }

    public static OutputPassUniforms Uniforms(OutputAdjustments adjustments, SceneRect sourceRect, bool hasMask) => new(
        adjustments.Brightness ?? 0, adjustments.Contrast ?? 0, adjustments.Gamma ?? 0, adjustments.BlackLevel ?? 0,
        adjustments.RedLevel ?? 0, adjustments.GreenLevel ?? 0, adjustments.BlueLevel ?? 0, hasMask,
        Edge(adjustments.BlendLeft), Edge(adjustments.BlendRight), Edge(adjustments.BlendTop), Edge(adjustments.BlendBottom),
        new SceneRect(sourceRect.X, sourceRect.Y, Math.Max(sourceRect.Width, 1e-6), Math.Max(sourceRect.Height, 1e-6)));

    /// <summary>
    /// A CPU reference of the correction shader for one pixel: <paramref name="sample"/>
    /// is the scene's premultiplied colour at canvas <paramref name="uv"/>, and
    /// <paramref name="maskCoverage"/> the mask raster there (0–1; ignored unless
    /// the uniforms say a mask is bound). Outside the slice the pass draws nothing.
    /// The order is: black level, brightness, contrast, channel gains, gamma,
    /// then the edge ramps and black lift, then the mask.
    /// </summary>
    public static OutputPixel Shade(OutputPixel sample, (double U, double V) uv, OutputPassUniforms u, double maskCoverage = 1)
    {
        var localX = (uv.U - u.SourceRect.X) / u.SourceRect.Width;
        var localY = (uv.V - u.SourceRect.Y) / u.SourceRect.Height;
        if (localX < 0 || localY < 0 || localX > 1 || localY > 1) return OutputPixel.Transparent;

        var alpha = sample.A;
        double Channel(double premultiplied, double gain)
        {
            var c = alpha > 0 ? premultiplied / alpha : premultiplied;
            c = Math.Max(c + u.BlackLevel, 0);
            c += u.Brightness;
            c = (c - 0.5) * (1 + u.Contrast) + 0.5;
            c *= 1 + gain;
            return Math.Pow(Math.Max(c, 0), 1 / Math.Clamp(1 + u.Gamma, 0.05, 20));
        }
        var r = Channel(sample.R, u.RedLevel);
        var g = Channel(sample.G, u.GreenLevel);
        var b = Channel(sample.B, u.BlueLevel);

        var left = EdgeRamp(localX, u.BlendLeft);
        var right = EdgeRamp(1 - localX, u.BlendRight);
        var top = EdgeRamp(localY, u.BlendTop);
        var bottom = EdgeRamp(1 - localY, u.BlendBottom);
        var falloff = left.Falloff * right.Falloff * top.Falloff * bottom.Falloff;
        var lift = Math.Max(Math.Max(left.Lift, right.Lift), Math.Max(top.Lift, bottom.Lift));
        var coverage = u.HasMask ? maskCoverage : 1;
        var outAlpha = alpha * falloff * coverage;
        var lifted = lift * alpha * coverage;
        return new OutputPixel(r * outAlpha + lifted, g * outAlpha + lifted, b * outAlpha + lifted, outAlpha);
    }

    /// <summary>
    /// One edge's effect at <paramref name="distance"/> from it (0 at the edge, 1
    /// at the far side): how much of the picture survives, and how much black lift
    /// to add. The lift is full away from the seam and tapers to 0 at it, where
    /// the neighbouring projector supplies the light.
    /// </summary>
    public static (double Falloff, double Lift) EdgeRamp(double distance, OutputEdgeUniform edge)
    {
        if (edge.Width <= 0) return (1, 0);
        var t = Math.Clamp(distance / Math.Max(edge.Width, 1e-4), 0, 1);
        var ramp = Math.Pow(t, Math.Max(edge.Curve, 0.01));
        return (1 - edge.Intensity * (1 - ramp), edge.BlackLift * ramp);
    }
}
