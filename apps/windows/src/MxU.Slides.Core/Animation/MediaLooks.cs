using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Animation;

/// <summary>
/// Two looks for picture and video slides, made out of ordinary items just before a frame is
/// drawn, so nothing else in the renderer needs to know about them. Port of the Mac's
/// RenderEngine/MediaLooks.swift.
/// <list type="bullet">
/// <item><b>Scale and Blur</b>: the picture fitted whole, over a blurred copy of itself zoomed to
/// fill the frame, so a tall phone photo on a wide screen has no black bars. In a photo spot the
/// blurred copy is clipped to the spot's shape.</item>
/// <item><b>End fade</b>: a video that fades over its last second, to black (a black cover rising
/// over it) or to clear (the video itself fading, so what's under it shows).</item>
/// </list>
/// </summary>
public static class MediaLooks
{
    /// <summary>How much the blurred copy is enlarged, so its blurred edges fall outside the frame.</summary>
    public const double BackdropZoom = 1.12;

    /// <summary>The blur's radius as a share of the canvas width.</summary>
    public const double BackdropBlurShare = 0.025;

    public const double EndFadeSeconds = 1.0;

    /// <summary>
    /// The scene with every Scale and Blur and end fade made into plain items. <paramref name="remaining"/>
    /// says how many seconds a playing video has left (null when unknown, so nothing fades).
    /// The same instance comes back when there's nothing to do.
    /// </summary>
    public static AnimatedScene Expanding(AnimatedScene scene, Func<string, double?> remaining)
    {
        if (!scene.Layers.Any(layer => layer.Items.Any(NeedsExpanding))) return scene;
        var copy = scene.Copy();
        foreach (var layer in copy.Layers.Where(layer => layer.Items.Any(NeedsExpanding)))
        {
            var items = layer.Items.SelectMany(item => Expanded(item, scene.CanvasSize, remaining)).ToList();
            layer.Items.Clear();
            layer.Items.AddRange(items);
        }
        return copy;
    }

    /// <summary>How far through its end fade a video is: 0 until its last second, 1 at its end.</summary>
    public static double EndFadeAmount(double? remaining) =>
        remaining is { } left ? Math.Clamp(1 - left / EndFadeSeconds, 0, 1) : 0;

    internal static SceneSourceRect Zoomed(SceneSourceRect? crop)
    {
        var b = crop ?? new SceneSourceRect(0, 0, 1, 1);
        var width = b.Width / BackdropZoom;
        var height = b.Height / BackdropZoom;
        return new SceneSourceRect(b.X + (b.Width - width) / 2, b.Y + (b.Height - height) / 2, width, height);
    }

    private static bool NeedsExpanding(AnimatedItem item) =>
        item.Render.EndFade is not null
        || item.Render.Content is ItemContent.Media { ScaleMode: SceneMediaScaleMode.ScaleAndBlur }
        || item.Render.Content is ItemContent.Shape { Style.Fill: SceneFill.Media { ScaleMode: SceneMediaScaleMode.ScaleAndBlur } };

    private static List<AnimatedItem> Expanded(AnimatedItem item, SceneSize canvas, Func<string, double?> remaining)
    {
        if (!NeedsExpanding(item)) return [item];
        var blur = canvas.Width * BackdropBlurShare;
        List<AnimatedItem> items = [item];
        var render = item.Render;
        if (render.Content is ItemContent.Media { ScaleMode: SceneMediaScaleMode.ScaleAndBlur } media)
        {
            var backdrop = item with
            {
                Render = render with
                {
                    Id = render.Id + "#scalebg",
                    Content = new ItemContent.Media(media.Id, SceneMediaScaleMode.Fill, Zoomed(media.SourceRect)),
                    BlurRadius = render.BlurRadius + blur,
                    MaskedBy = null,
                },
            };
            var picture = item with { Render = render with { Content = media with { ScaleMode = SceneMediaScaleMode.Fit } } };
            items = [backdrop, picture];
        }
        if (render.Content is ItemContent.Shape { Style: var style } && style.Fill is SceneFill.Media { ScaleMode: SceneMediaScaleMode.ScaleAndBlur } fill)
        {
            // The spot's outline as a matte, so the blurred copy takes the spot's shape (rounded corners and all).
            var group = render.Id + "#spotmatte";
            var outline = style with { Fill = new SceneFill.Solid(SceneColor.White), Stroke = null, Shadow = null };
            var matte = item with { Render = render with { Id = group, MatteGroup = group, MaskedBy = null, Content = new ItemContent.Shape(outline) }, Effects = [] };
            var backdrop = item with
            {
                Render = render with
                {
                    Id = render.Id + "#scalebg",
                    MaskedBy = group,
                    BlurRadius = render.BlurRadius + blur,
                    Content = new ItemContent.Shape(outline with { Fill = new SceneFill.Media(fill.Id, SceneMediaScaleMode.Fill, Zoomed(fill.SourceRect)) }),
                },
            };
            var picture = item with
            {
                Render = render with { Content = new ItemContent.Shape(style with { Fill = fill with { ScaleMode = SceneMediaScaleMode.Fit } }) },
            };
            items = [matte, backdrop, picture];
        }
        if (render.EndFade is not { } fade || MediaId(render.Content) is not { } id) return items;
        var amount = EndFadeAmount(remaining(id));
        if (amount <= 0) return items;
        if (fade == MediaEndFade.Clear)
            return [.. items.Select(piece => piece with { Render = piece.Render with { Opacity = piece.Render.Opacity * (1 - amount) } })];
        AnimatedItem cover = new RenderItem
        {
            Id = render.Id + "#endfade",
            Frame = render.Frame,
            Content = new ItemContent.Shape(new ShapeStyle(SceneShapeKind.Rect, new SceneFill.Solid(SceneColor.Black))),
            Opacity = amount,
        };
        return [.. items, cover];
    }

    private static string? MediaId(ItemContent content) => content switch
    {
        ItemContent.Media media => media.Id,
        ItemContent.Shape { Style.Fill: SceneFill.Media fill } => fill.Id,
        _ => null,
    };
}
