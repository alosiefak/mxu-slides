using System.Globalization;
using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Outputs;

/// <summary>Helpers shared by the output tests.</summary>
internal static class OutputFixtures
{
    public const string CenterBand = "M 0.25 0 L 0.75 0 L 0.75 1 L 0.25 1 Z";
    public const string LeftHalf = "M 0 0 L 0.5 0 L 0.5 1 L 0 1 Z";

    /// <summary>
    /// A stand-in for the app's path filler: unit-space "M x y L x y … Z"
    /// polygons only, sampled at pixel centres with no antialiasing.
    /// </summary>
    public static byte[]? FillPolygon(string pathData, int width, int height)
    {
        var tokens = pathData.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        var points = new List<(double X, double Y)>();
        for (var i = 0; i < tokens.Length; i++)
        {
            switch (tokens[i])
            {
                case "M" or "L":
                    if (i + 2 >= tokens.Length
                        || !double.TryParse(tokens[i + 1], CultureInfo.InvariantCulture, out var x)
                        || !double.TryParse(tokens[i + 2], CultureInfo.InvariantCulture, out var y))
                        return null;
                    points.Add((x * width, y * height));
                    i += 2;
                    break;
                case "Z":
                    break;
                default:
                    return null;
            }
        }
        if (points.Count < 3) return null;
        var alpha = new byte[width * height];
        for (var py = 0; py < height; py++)
            for (var px = 0; px < width; px++)
                if (Inside(points, px + 0.5, py + 0.5)) alpha[py * width + px] = 255;
        return alpha;
    }

    private static bool Inside(List<(double X, double Y)> polygon, double x, double y)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var (xi, yi) = polygon[i];
            var (xj, yj) = polygon[j];
            if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside;
    }

    public static byte At(byte[] coverage, int width, int x, int y) => coverage[y * width + x];

    /// <summary>The scene's layers and items as comparable values (RenderScene itself compares by reference).</summary>
    public static (SceneSize Canvas, SceneColor Background, (LayerKind Layer, RenderItem Item)[] Items) Snapshot(RenderScene scene) =>
        (scene.CanvasSize, scene.Background, scene.Layers.SelectMany(layer => layer.Items.Select(item => (layer.Kind, item))).ToArray());

    public static void SameScene(RenderScene expected, RenderScene actual)
    {
        var a = Snapshot(expected);
        var b = Snapshot(actual);
        Assert.Equal(a.Canvas, b.Canvas);
        Assert.Equal(a.Background, b.Background);
        Assert.Equal(a.Items, b.Items);
    }

    public static RenderItem Solid(string id, SceneRect frame, SceneColor color) => new()
    {
        Id = id,
        Frame = frame,
        Content = new ItemContent.Shape(new ShapeStyle(SceneShapeKind.Rect, new SceneFill.Solid(color))),
    };

    public static readonly OutputPixel White = new(1, 1, 1, 1);
    public static readonly OutputPixel Red = new(1, 0, 0, 1);
    public static readonly OutputPixel Blue = new(0, 0, 1, 1);
    public static readonly OutputPixel OpaqueBlack = new(0, 0, 0, 1);

    /// <summary>
    /// What the correction pass draws at one target pixel: the pixel's centre
    /// mapped back through the warp to a canvas uv, the canvas sampled there,
    /// and the shader maths applied. The maths of the Mac's pixel tests, without a GPU.
    /// </summary>
    public static OutputPixel RenderPixel(
        Func<double, double, OutputPixel> canvas, OutputAdjustments adjustments, int targetWidth, int targetHeight, int x, int y,
        SceneRect? sourceRect = null, SceneRect? placement = null, Func<double, double, double>? mask = null)
    {
        var source = sourceRect ?? OutputPass.FullSourceRect;
        var geometry = OutputPass.Geometry(adjustments, source, placement, targetWidth, targetHeight);
        if (geometry.SourceUv((x + 0.5) / targetWidth, (y + 0.5) / targetHeight) is not { } uv) return OutputPixel.Transparent;
        var uniforms = OutputPass.Uniforms(adjustments, source, hasMask: mask is not null);
        var sample = canvas(Math.Clamp(uv.U, 0, 1), Math.Clamp(uv.V, 0, 1));
        return OutputPass.Shade(sample, uv, uniforms, mask?.Invoke(uv.U, uv.V) ?? 1);
    }

    /// <summary>Red on the canvas's left half, blue on its right (the Mac's halvesScene).</summary>
    public static OutputPixel Halves(double u, double v) => u < 0.5 ? Red : Blue;
}
