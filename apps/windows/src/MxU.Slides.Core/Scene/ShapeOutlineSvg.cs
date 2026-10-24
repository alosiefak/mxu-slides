using System.Globalization;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Scene;

/// <summary>
/// The outline of a shape as unit-space SVG path data: the line text runs
/// along when it is set on the shape's edge, and the shape a ProPresenter
/// mask is turned into. Rounded corners and ellipses are drawn with the
/// usual cubic-Bézier circle approximation. Port of SlideScene/ShapeOutlineSVG.swift.
/// </summary>
public static class ShapeOutlineSvg
{
    /// <summary>How far along a quarter circle's tangent its Bézier handles sit (4/3·(√2 − 1)).</summary>
    public const double Kappa = 0.5522847498;

    /// <summary>
    /// The outline of a <paramref name="shapeKind"/> shape <paramref name="frame"/> big.
    /// It starts at the top middle and runs clockwise, or at the bottom middle and
    /// runs the other way when text sits inside the edge (so the text reads upright).
    /// A custom path returns its own data. Null for an empty frame or a path with no data.
    /// </summary>
    public static string? PathData(ShapeKind? shapeKind, SceneSize frame, double? cornerRadius, string? customPathData, ShapeTextPlacement placement)
    {
        if (frame.Width <= 0 || frame.Height <= 0) return null;
        var startAtBottom = placement == ShapeTextPlacement.EdgeInside;
        return (shapeKind ?? ShapeKind.Rectangle) switch
        {
            ShapeKind.Rectangle => Perimeter(frame, 0, startAtBottom),
            ShapeKind.RoundedRectangle => Perimeter(frame, cornerRadius ?? 0, startAtBottom),
            ShapeKind.Ellipse => Ellipse(startAtBottom),
            _ => string.IsNullOrEmpty(customPathData) ? null : customPathData,
        };
    }

    private static string F(double value) => value.ToString("F6", CultureInfo.InvariantCulture);

    private static string Perimeter(SceneSize frame, double radius, bool startAtBottom)
    {
        // The radius is clamped like the rasterizer's, then measured separately across and down
        // (a 30-unit corner on a 400×100 box is 0.075 of its width but 0.3 of its height).
        var clamped = Math.Min(Math.Max(radius, 0), Math.Min(frame.Width, frame.Height) / 2);
        var rx = clamped / frame.Width;
        var ry = clamped / frame.Height;
        var kx = Kappa * rx;
        var ky = Kappa * ry;

        static string Corner(double c1x, double c1y, double c2x, double c2y, double x, double y) =>
            $"C {F(c1x)} {F(c1y)} {F(c2x)} {F(c2y)} {F(x)} {F(y)}";

        var parts = new List<string>();
        if (startAtBottom)
        {
            parts.Add("M 0.5 1");
            parts.Add($"L {F(rx)} 1");
            parts.Add(Corner(rx - kx, 1, 0, 1 - ry + ky, 0, 1 - ry));
            parts.Add($"L 0 {F(ry)}");
            parts.Add(Corner(0, ry - ky, rx - kx, 0, rx, 0));
            parts.Add($"L {F(1 - rx)} 0");
            parts.Add(Corner(1 - rx + kx, 0, 1, ry - ky, 1, ry));
            parts.Add($"L 1 {F(1 - ry)}");
            parts.Add(Corner(1, 1 - ry + ky, 1 - rx + kx, 1, 1 - rx, 1));
        }
        else
        {
            parts.Add("M 0.5 0");
            parts.Add($"L {F(1 - rx)} 0");
            parts.Add(Corner(1 - rx + kx, 0, 1, ry - ky, 1, ry));
            parts.Add($"L 1 {F(1 - ry)}");
            parts.Add(Corner(1, 1 - ry + ky, 1 - rx + kx, 1, 1 - rx, 1));
            parts.Add($"L {F(rx)} 1");
            parts.Add(Corner(rx - kx, 1, 0, 1 - ry + ky, 0, 1 - ry));
            parts.Add($"L 0 {F(ry)}");
            parts.Add(Corner(0, ry - ky, rx - kx, 0, rx, 0));
        }
        parts.Add("Z");
        return string.Join(" ", parts);
    }

    private static string Ellipse(bool startAtBottom)
    {
        var k = F(0.5 - Kappa * 0.5);
        var kk = F(0.5 + Kappa * 0.5);
        if (startAtBottom)
            return $"M 0.5 1 C {k} 1 0 {kk} 0 0.5 "
                + $"C 0 {k} {k} 0 0.5 0 "
                + $"C {kk} 0 1 {k} 1 0.5 "
                + $"C 1 {kk} {kk} 1 0.5 1 Z";
        return $"M 0.5 0 C {kk} 0 1 {k} 1 0.5 "
            + $"C 1 {kk} {kk} 1 0.5 1 "
            + $"C {k} 1 0 {kk} 0 0.5 "
            + $"C 0 {k} {k} 0 0.5 0 Z";
    }
}
