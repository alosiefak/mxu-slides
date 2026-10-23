using System.Windows;
using System.Windows.Media;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Windows.Rendering;

/// <summary>Scene colours and fills as frozen WPF brushes.</summary>
public static class Painting
{
    private static readonly Dictionary<SceneColor, SolidColorBrush> Solids = [];

    public static Color ToColor(SceneColor c) => Color.FromArgb(
        (byte)Math.Round(Math.Clamp(c.Alpha, 0, 1) * 255),
        (byte)Math.Round(Math.Clamp(c.Red, 0, 1) * 255),
        (byte)Math.Round(Math.Clamp(c.Green, 0, 1) * 255),
        (byte)Math.Round(Math.Clamp(c.Blue, 0, 1) * 255));

    public static SolidColorBrush Brush(SceneColor color)
    {
        lock (Solids)
        {
            if (Solids.TryGetValue(color, out var brush)) return brush;
            brush = new SolidColorBrush(ToColor(color));
            brush.Freeze();
            if (Solids.Count > 512) Solids.Clear();
            Solids[color] = brush;
            return brush;
        }
    }

    public static SolidColorBrush Brush(string? hex, SceneColor fallback) => Brush(ColorHex.Color(hex) ?? fallback);

    /// <summary>
    /// A gradient across <paramref name="rect"/>, angle 0 = left→right and 90 = top→bottom,
    /// spanning the rect's projection on the direction, as ShapeRasterizer.swift does.
    /// </summary>
    public static Brush Gradient(SceneFill.LinearGradient gradient, Rect rect)
    {
        var radians = gradient.AngleDegrees * Math.PI / 180;
        var dx = Math.Cos(radians);
        var dy = Math.Sin(radians);
        var center = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        var halfSpan = (Math.Abs(dx) * rect.Width + Math.Abs(dy) * rect.Height) / 2;
        var stops = new GradientStopCollection(gradient.Stops
            .OrderBy(s => s.Position)
            .Select(s => new GradientStop(ToColor(s.Color), s.Position)));
        var brush = new LinearGradientBrush(stops,
            new Point(center.X - dx * halfSpan, center.Y - dy * halfSpan),
            new Point(center.X + dx * halfSpan, center.Y + dy * halfSpan))
        {
            MappingMode = BrushMappingMode.Absolute,
            ColorInterpolationMode = ColorInterpolationMode.SRgbLinearInterpolation,
        };
        brush.Freeze();
        return brush;
    }

    public static Brush? FillBrush(SceneFill? fill, Rect rect) => fill switch
    {
        SceneFill.Solid solid => Brush(solid.Color),
        SceneFill.LinearGradient gradient => Gradient(gradient, rect),
        _ => null,
    };
}
