using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Outputs;

/// <summary>What a screen does when the presentation is a different shape from it.</summary>
public enum OutputScaleMode
{
    /// <summary>Show the whole slide; backgrounds grow to fill the bars at the sides (or top and bottom).</summary>
    Fit,

    /// <summary>Cover the whole screen and crop the slide's edges.</summary>
    Fill,
}

public static class OutputScaleModes
{
    public static string Raw(this OutputScaleMode mode) => mode == OutputScaleMode.Fill ? "fill" : "fit";

    public static OutputScaleMode? Parse(string? raw) => raw switch
    {
        "fit" => OutputScaleMode.Fit,
        "fill" => OutputScaleMode.Fill,
        _ => null,
    };
}

/// <summary>
/// Reshapes a scene's canvas to a screen's aspect ratio before it is drawn, so
/// a 4:3 deck on a 16:9 screen neither stretches nor leaves the renderer to
/// guess. Fit grows the canvas and centres the slide in it, and full-canvas
/// backgrounds (background media, and a slide's "-backdrop" item) grow with it
/// so the extra space isn't black: the bleed rule. Fill shrinks the canvas,
/// which crops the slide's edges. Port of RenderEngine/OutputScaling.swift,
/// for both the plain and the animated scene.
/// </summary>
public static class OutputScaling
{
    /// <summary>The scene reshaped for a screen of this size; the same instance when the shapes already match.</summary>
    public static RenderScene Scaled(this RenderScene scene, SceneSize screen, OutputScaleMode mode)
    {
        if (Reshape(scene.CanvasSize, screen, mode) is not { } shape) return scene;
        var full = new SceneRect(scene.CanvasSize);
        var scaled = new RenderScene(shape.Canvas, scene.Background);
        foreach (var layer in scene.Layers)
            foreach (var item in layer.Items)
                scaled.AddItem(item with { Frame = Moved(item.Frame, item.Id, layer.Kind, full, shape) }, layer.Kind);
        return scaled;
    }

    /// <summary>The animated scene reshaped for a screen of this size; the same instance when the shapes already match.</summary>
    public static AnimatedScene Scaled(this AnimatedScene scene, SceneSize screen, OutputScaleMode mode)
    {
        if (Reshape(scene.CanvasSize, screen, mode) is not { } shape) return scene;
        var full = new SceneRect(scene.CanvasSize);
        var scaled = new AnimatedScene(shape.Canvas, scene.Background);
        foreach (var layer in scene.Layers)
        {
            scaled[layer.Kind].IsHidden = layer.IsHidden;
            foreach (var item in layer.Items)
            {
                var frame = Moved(item.Render.Frame, item.Id, layer.Kind, full, shape);
                scaled.AddItem(item with { Render = item.Render with { Frame = frame } }, layer.Kind);
            }
        }
        return scaled;
    }

    /// <summary>Whether a full-canvas item stretches to the grown canvas under Fit, instead of moving with the slide.</summary>
    public static bool Bleeds(string itemId, LayerKind layer) =>
        layer == LayerKind.LoopingVideos || (layer == LayerKind.Slide && itemId.EndsWith("-backdrop", StringComparison.Ordinal));

    private readonly record struct Shape(SceneSize Canvas, double Dx, double Dy, bool GrowsBackgrounds);

    private static Shape? Reshape(SceneSize canvas, SceneSize screen, OutputScaleMode mode)
    {
        if (canvas.Width <= 0 || canvas.Height <= 0 || screen.Width <= 0 || screen.Height <= 0) return null;
        var screenAspect = screen.Width / screen.Height;
        var canvasAspect = canvas.Width / canvas.Height;
        if (Math.Abs(canvasAspect - screenAspect) <= 0.001) return null;
        var wider = screenAspect > canvasAspect;
        var size = mode == OutputScaleMode.Fit
            // Grow the short side so the whole slide fits…
            ? wider ? new SceneSize(canvas.Height * screenAspect, canvas.Height) : new SceneSize(canvas.Width, canvas.Width / screenAspect)
            // …or shrink the long side so the screen is covered.
            : wider ? new SceneSize(canvas.Width, canvas.Width / screenAspect) : new SceneSize(canvas.Height * screenAspect, canvas.Height);
        return new Shape(size, (size.Width - canvas.Width) / 2, (size.Height - canvas.Height) / 2, mode == OutputScaleMode.Fit);
    }

    private static SceneRect Moved(SceneRect frame, string id, LayerKind layer, SceneRect full, Shape shape) =>
        shape.GrowsBackgrounds && frame == full && Bleeds(id, layer)
            ? new SceneRect(shape.Canvas)
            : frame with { X = frame.X + shape.Dx, Y = frame.Y + shape.Dy };
}
