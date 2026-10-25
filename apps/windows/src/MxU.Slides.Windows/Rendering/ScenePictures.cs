using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Windows.Rendering;

/// <summary>
/// A scene drawn to a PNG off screen, exactly as an output would draw it: a <see cref="SceneView"/>
/// that's never shown, captured with <c>RenderTargetBitmap</c>. The browser remote's slide
/// thumbnails come from here. Call on the UI thread (WPF drawing lives there); keep the pictures
/// small, as a 320-pixel one takes a few milliseconds. Windows only.
/// </summary>
internal static class ScenePictures
{
    /// <summary>How long to wait for a slide's background picture to finish decoding before drawing without it.</summary>
    private static readonly TimeSpan MediaWait = TimeSpan.FromSeconds(1.5);

    public static async Task<byte[]> PngAsync(RenderScene scene, int width, MediaHub media)
    {
        var canvas = scene.CanvasSize;
        var height = Math.Max(1, (int)Math.Round(width * canvas.Height / Math.Max(1, canvas.Width)));
        var view = new SceneView { Media = media, PlaysVideo = false, Width = width, Height = height };
        try
        {
            view.Measure(new Size(width, height));
            view.Arrange(new Rect(0, 0, width, height));
            view.Present(scene, SceneTransition.Cut);
            // Images decode on another thread the first time they're used: give them a moment.
            await WaitForMediaAsync(scene, width, media);
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            var backdrop = new DrawingVisual();
            using (var dc = backdrop.RenderOpen()) dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
            bitmap.Render(backdrop);
            bitmap.Render(view);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var png = new MemoryStream();
            encoder.Save(png);
            return png.ToArray();
        }
        finally
        {
            // Let go of the media hub's event and any players, so the view can be collected.
            view.Present((RenderScene?)null, SceneTransition.Cut);
            view.Media = null;
        }
    }

    private static async Task WaitForMediaAsync(RenderScene scene, int width, MediaHub media)
    {
        bool Ready(string id) => media.KindOf(id) switch
        {
            MediaKind.Image => media.Image(id, width) is not null,
            MediaKind.Video => media.Poster(id) is not null,
            _ => true,
        };
        var waiting = scene.MediaIds.Distinct().Where(id => !Ready(id)).ToHashSet();
        if (waiting.Count == 0) return;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnReady(string id)
        {
            if (waiting.Remove(id) && waiting.Count == 0) done.TrySetResult();
        }
        media.MediaReady += OnReady;
        try
        {
            await Task.WhenAny(done.Task, Task.Delay(MediaWait));
        }
        finally
        {
            media.MediaReady -= OnReady;
        }
    }
}
