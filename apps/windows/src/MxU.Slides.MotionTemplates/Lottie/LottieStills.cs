using MxU.Slides.Core.MotionTemplates;
using SkiaSharp;

namespace MxU.Slides.MotionTemplates.Lottie;

/// <summary>
/// Still pictures of a template: the PNG (with its transparency) a Mac shows in place of a
/// lower third, the gallery's thumbnails, and a look at how much of the frame a design covers.
/// </summary>
public static class LottieStills
{
    /// <summary>Frame <paramref name="frame"/> as a PNG with alpha, at <paramref name="width"/> × <paramref name="height"/>.</summary>
    public static byte[] Png(LottieDocument document, double frame, int width, int height, ILottieFonts? fonts = null)
    {
        using var renderer = new LottieRenderer(document, fonts);
        using var target = new LottieFrame(width, height);
        renderer.Render(frame, target);
        return Encode(target);
    }

    /// <summary>A drawn frame as a PNG with alpha.</summary>
    public static byte[] Encode(LottieFrame frame)
    {
        var info = new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var pixmap = new SKPixmap(info, System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(frame.Pixels, 0), frame.Stride);
        using var data = pixmap.Encode(SKEncodedImageFormat.Png, 100) ?? throw new IOException("Skia couldn't encode the picture");
        return data.ToArray();
    }

    /// <summary>
    /// How much of the picture the design covers at <paramref name="frame"/>, from 0 (nothing) to 1
    /// (all of it, opaque). A full-screen design covers nearly all of it: it's a background.
    /// </summary>
    public static double Coverage(LottieDocument document, double frame, ILottieFonts? fonts = null)
    {
        var (w, h) = LottieRenderer.FittedSize(document, 192, 192);
        using var renderer = new LottieRenderer(document, fonts);
        using var target = new LottieFrame(w, h);
        renderer.Render(frame, target);
        long alpha = 0;
        for (var i = 3; i < target.Pixels.Length; i += 4) alpha += target.Pixels[i];
        return alpha / (255.0 * w * h);
    }

    /// <summary>What a template is for, from how it looks: a background if its settled design covers almost all of the frame.</summary>
    public static MotionUse SuggestUse(LottieDocument document, ILottieFonts? fonts = null)
    {
        if (MotionTemplateStore.SuggestUse(document) == MotionUse.Background) return MotionUse.Background;
        var timeline = MotionTimeline.For(document, MotionPlayback.Loop);
        return Coverage(document, timeline.SettledFrame, fonts) >= 0.97 ? MotionUse.Background : MotionUse.Overlay;
    }
}
