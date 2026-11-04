using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Windows.Rendering;

/// <summary>
/// A slide drawn to a picture for export (slide images, PDFs), full size, exactly as an output
/// draws it: a <see cref="SceneView"/> that's never shown, captured with RenderTargetBitmap, as
/// <see cref="ScenePictures"/> does for the browser remote's thumbnails. The drawing has to happen
/// on the UI thread (WPF's drawing lives there), so it yields between slides to keep the window
/// answering; the encoding (<see cref="Jpeg"/>, <see cref="Png"/>) works on the frozen picture on
/// any thread. The Mac's twin is SlideImageExport.swift (its Metal compositor's renderFrame).
/// </summary>
internal static class SlidePictures
{
    /// <param name="transparent">No backdrop: what isn't drawn stays see-through (a PNG of just the words, for a video editor).</param>
    public static async Task<BitmapSource> RenderAsync(RenderScene scene, int width, MediaHub media, bool transparent)
    {
        var canvas = scene.CanvasSize;
        var height = Math.Max(1, (int)Math.Round(width * canvas.Height / Math.Max(1, canvas.Width)));
        if (transparent) scene.Background = SceneColor.Clear;
        var view = new SceneView { Media = media, PlaysVideo = false, Width = width, Height = height, Letterbox = Brushes.Transparent };
        try
        {
            view.Measure(new Size(width, height));
            view.Arrange(new Rect(0, 0, width, height));
            view.Present(scene, SceneTransition.Cut);
            await ScenePictures.WaitForMediaAsync(scene, width, media);
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view);
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            view.Present((RenderScene?)null, SceneTransition.Cut);
            view.Media = null;
        }
    }

    /// <summary>A JPEG of the picture over <paramref name="backdrop"/> (JPEG has no see-through), at 90% quality.</summary>
    public static byte[] Jpeg(BitmapSource picture, Color backdrop, int quality = 90)
    {
        var flat = new DrawingVisual();
        using (var dc = flat.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(backdrop), null, new Rect(0, 0, picture.PixelWidth, picture.PixelHeight));
            dc.DrawImage(picture, new Rect(0, 0, picture.PixelWidth, picture.PixelHeight));
        }
        var opaque = new RenderTargetBitmap(picture.PixelWidth, picture.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        opaque.Render(flat);
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(opaque, PixelFormats.Bgr24, null, 0)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public static byte[] Png(BitmapSource picture)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(picture));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
