using System.Globalization;

namespace MxU.Slides.PptxImport;

/// <summary>Exact unit-square outlines for the preset shapes MxU can draw as paths.</summary>
internal static class PptxPresetGeometry
{
    public const string LinePath = "M 0.0000 0.0000 C 0.0000 0.0000 1.0000 1.0000 1.0000 1.0000";

    public static string? PathData(string preset) => preset switch
    {
        "triangle" => PolygonPath([(0.5, 0), (1, 1), (0, 1)]),
        "rtTriangle" => PolygonPath([(0, 0), (0, 1), (1, 1)]),
        "diamond" => PolygonPath([(0.5, 0), (1, 0.5), (0.5, 1), (0, 0.5)]),
        "parallelogram" => PolygonPath([(0.25, 0), (1, 0), (0.75, 1), (0, 1)]),
        "trapezoid" => PolygonPath([(0.25, 0), (0.75, 0), (1, 1), (0, 1)]),
        "pentagon" => PolygonPath([(0.5, 0), (1, 0.38197), (0.80902, 1), (0.19098, 1), (0, 0.38197)]),
        "hexagon" => PolygonPath([(0.25, 0), (0.75, 0), (1, 0.5), (0.75, 1), (0.25, 1), (0, 0.5)]),
        "octagon" => PolygonPath([
            (0.29289, 0), (0.70711, 0), (1, 0.29289), (1, 0.70711),
            (0.70711, 1), (0.29289, 1), (0, 0.70711), (0, 0.29289),
        ]),
        "star5" => PolygonPath([
            (0.5, 0), (0.38196, 0.38196), (0, 0.38196), (0.30902, 0.61803),
            (0.19098, 1), (0.5, 0.76393), (0.80902, 1), (0.69098, 0.61803),
            (1, 0.38196), (0.61803, 0.38196),
        ]),
        "chevron" => PolygonPath([(0, 0), (0.75, 0), (1, 0.5), (0.75, 1), (0, 1), (0.25, 0.5)]),
        "rightArrow" => PolygonPath([(0, 0.25), (0.6, 0.25), (0.6, 0), (1, 0.5), (0.6, 1), (0.6, 0.75), (0, 0.75)]),
        "leftArrow" => PolygonPath([(1, 0.25), (0.4, 0.25), (0.4, 0), (0, 0.5), (0.4, 1), (0.4, 0.75), (1, 0.75)]),
        "upArrow" => PolygonPath([(0.25, 1), (0.25, 0.4), (0, 0.4), (0.5, 0), (1, 0.4), (0.75, 0.4), (0.75, 1)]),
        "downArrow" => PolygonPath([(0.25, 0), (0.25, 0.6), (0, 0.6), (0.5, 1), (1, 0.6), (0.75, 0.6), (0.75, 0)]),
        "line" or "straightConnector1" => LinePath,
        _ => null,
    };

    public static string Format(double value) => Math.Clamp(value, -4, 4).ToString("F4", CultureInfo.InvariantCulture);

    public static string Coordinate((double X, double Y) point) => $"{Format(point.X)} {Format(point.Y)}";

    public static string PolygonPath(IReadOnlyList<(double X, double Y)> points, bool closed = true)
    {
        if (points.Count < 2) return "";
        var commands = new List<string> { $"M {Coordinate(points[0])}" };
        for (var index = 1; index < points.Count; index++)
            commands.Add($"C {Coordinate(points[index - 1])} {Coordinate(points[index])} {Coordinate(points[index])}");
        if (closed)
        {
            commands.Add($"C {Coordinate(points[^1])} {Coordinate(points[0])} {Coordinate(points[0])}");
            commands.Add("Z");
        }
        return string.Join(" ", commands);
    }
}
