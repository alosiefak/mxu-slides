using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Media;

/// <summary>What a media item brings to the picture wherever it's shown full screen: its effects, and its Windows look.</summary>
public sealed record MediaDress(IReadOnlyList<Effect>? Effects, MediaLook Look)
{
    public bool IsPlain => Effects is not { Count: > 0 } && Look.IsDefault;
}

/// <summary>
/// Dresses the full-screen media in a scene (a background, a video on the media layer) the way
/// its media item says: the item's picture effects (a blurred or tinted background), and on
/// Windows its fit, zoom and crop. Slide objects filled with media aren't touched: they carry
/// their own fit and crop in the shared document.
/// <para>
/// The effects half is a port of the Mac's <c>RenderScene.applyingMediaEffects</c>
/// (RenderEngine/SceneModel.swift), which the Mac applies to every scene it shows; the look half is
/// Windows-only (Library/MediaLookExtras.cs).
/// </para>
/// </summary>
public static class MediaDressing
{
    /// <summary>
    /// A copy of <paramref name="scene"/> with each media item dressed by <paramref name="lookup"/>
    /// (null or plain: left as it is). The scene itself isn't changed; when nothing needs dressing
    /// it's returned as it is.
    /// </summary>
    public static AnimatedScene Dress(AnimatedScene scene, Func<string, MediaDress?> lookup)
    {
        AnimatedScene? dressed = null;
        foreach (var layer in scene.Layers)
        {
            for (var i = 0; i < layer.Items.Count; i++)
            {
                var item = layer.Items[i];
                if (item.Render.Content is not ItemContent.Media media || lookup(media.Id) is not { IsPlain: false } dress) continue;
                dressed ??= scene.Copy();
                dressed[layer.Kind].Items[i] = Dressed(item, media, dress, scene.CanvasSize);
            }
        }
        return dressed ?? scene;
    }

    /// <summary>One media item dressed: its effects in front of any the animation adds, then its fit, zoom and crop.</summary>
    public static AnimatedItem Dressed(AnimatedItem item, ItemContent.Media media, MediaDress dress, SceneSize canvas)
    {
        var render = item.Render;
        var effects = item.Effects;
        if (dress.Effects is { Count: > 0 } own)
        {
            // Blur travels as the item's blur radius, summed as a slide object's is (SlideSceneBuilder); the rest as passes.
            var blur = own.Where(e => e.EffectKind == EffectKind.Blur && (e.Enabled ?? true)).Sum(e => Math.Max(e.Radius ?? 0, 0));
            if (blur > 0) render = render with { BlurRadius = render.BlurRadius + blur };
            effects = [.. AnimationEffect.From(own), .. item.Effects];
        }
        var look = dress.Look;
        if (!look.IsDefault)
        {
            var mode = look.Fit switch
            {
                MediaLookFit.Fit => SceneMediaScaleMode.Fit,
                MediaLookFit.Stretch => SceneMediaScaleMode.Stretch,
                _ => SceneMediaScaleMode.Fill,
            };
            SceneSourceRect? crop = look.Crop is { IsNone: false } c ? Source(c.Kept()) : media.SourceRect;
            render = render with { Content = new ItemContent.Media(media.Id, mode, crop), Frame = Zoomed(render.Frame, look.Scale) };
        }
        return item with { Render = render, Effects = effects };
    }

    private static SceneSourceRect Source((double X, double Y, double Width, double Height) kept) => new(kept.X, kept.Y, kept.Width, kept.Height);

    /// <summary>The frame grown (or shrunk) about its centre: a zoom of 1.2 makes a picture a fifth bigger.</summary>
    public static SceneRect Zoomed(SceneRect frame, double scale)
    {
        if (Math.Abs(scale - 1) < 0.0005 || !double.IsFinite(scale)) return frame;
        var width = frame.Width * scale;
        var height = frame.Height * scale;
        return new SceneRect(frame.X + (frame.Width - width) / 2, frame.Y + (frame.Height - height) / 2, width, height);
    }
}
