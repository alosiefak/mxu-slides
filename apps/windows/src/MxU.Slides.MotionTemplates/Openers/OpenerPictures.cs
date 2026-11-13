using MxU.Slides.Broadcast.Media;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using SkiaSharp;

namespace MxU.Slides.MotionTemplates.Openers;

/// <summary>
/// The pictures and clips an opener shows, ready to draw at one output size. A phone photo is
/// far bigger than the screen, so each is decoded once, turned the right way up (phones store a
/// sideways picture plus a note saying which way to turn it) and shrunk to just bigger than it
/// will be drawn; a small blurred copy is kept too, to fill the screen behind a picture whose
/// shape doesn't (a portrait photo).
/// <para>
/// Only the last few are kept (a montage shows its pictures one after another, and a hundred
/// decoded photos would fill the memory); one that comes round again is simply decoded again.
/// A file that's missing or unreadable is remembered as such and shows as nothing. Use from one
/// thread.
/// </para>
/// </summary>
public sealed class OpenerPictures : IDisposable
{
    private const int Kept = 8;

    /// <summary>One picture, ready to draw.</summary>
    internal sealed class Picture(SKImage image, SKImage? blurred, bool hasAlpha) : IDisposable
    {
        public SKImage Image { get; } = image;

        /// <summary>A small, heavily blurred copy (null for a see-through picture, which needs no backdrop).</summary>
        public SKImage? Blurred { get; } = blurred;

        /// <summary>True for a picture with see-through parts (a logo): it's never cropped.</summary>
        public bool HasAlpha { get; } = hasAlpha;

        public void Dispose()
        {
            Image.Dispose();
            Blurred?.Dispose();
        }
    }

    private readonly int width;
    private readonly int height;
    private readonly Dictionary<string, (Picture? Picture, long Used)> pictures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VideoClipReader?> clips = new(StringComparer.OrdinalIgnoreCase);
    private long uses;

    /// <param name="width">The size pictures will be drawn into (the video's, or the preview's).</param>
    public OpenerPictures(int width, int height)
    {
        this.width = Math.Max(16, width);
        this.height = Math.Max(16, height);
    }

    public static bool IsVideo(string path) => LibraryStore.MediaKindOf(path) == MediaKind.Video;

    /// <summary>The picture at <paramref name="path"/>, or null when it can't be read.</summary>
    internal Picture? Get(string path)
    {
        if (pictures.TryGetValue(path, out var held))
        {
            pictures[path] = (held.Picture, ++uses);
            return held.Picture;
        }
        var picture = Load(path);
        if (pictures.Count >= Kept)
        {
            var oldest = pictures.MinBy(p => p.Value.Used);
            oldest.Value.Picture?.Dispose();
            pictures.Remove(oldest.Key);
        }
        pictures[path] = (picture, ++uses);
        return picture;
    }

    /// <summary>
    /// The picture a video clip shows <paramref name="seconds"/> in, as BGRA (the reader's own
    /// buffer, good until the next call for the same clip); null when the clip can't be read.
    /// </summary>
    internal byte[]? ClipFrame(string path, double seconds, out int frameWidth, out int frameHeight)
    {
        frameWidth = frameHeight = 0;
        if (!clips.TryGetValue(path, out var reader))
        {
            try { reader = new VideoClipReader(path); }
            catch (IOException) { reader = null; }
            clips[path] = reader;
        }
        return reader?.FrameAt(Math.Max(0, seconds), out frameWidth, out frameHeight);
    }

    /// <summary>Lets go of clips that are no longer on screen (a montage may have dozens; each open one holds a decoder).</summary>
    internal void KeepOnlyClips(IEnumerable<string> showing)
    {
        if (clips.Count == 0) return;
        var keep = new HashSet<string>(showing, StringComparer.OrdinalIgnoreCase);
        foreach (var path in clips.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            clips[path]?.Dispose();
            clips.Remove(path);
        }
    }

    private Picture? Load(string path)
    {
        try
        {
            using var codec = SKCodec.Create(path);
            if (codec is null) return null;
            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var decoded = new SKBitmap(info);
            if (codec.GetPixels(info, decoded.GetPixels()) is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return null;
            var hasAlpha = codec.Info.AlphaType != SKAlphaType.Opaque && !IsOpaque(decoded);

            var turned = codec.EncodedOrigin is SKEncodedOrigin.RightTop or SKEncodedOrigin.LeftBottom or SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightBottom;
            var (sourceWidth, sourceHeight) = turned ? (info.Height, info.Width) : (info.Width, info.Height);
            // Just bigger than it'll ever be drawn: filling the screen, plus the montage's 8 % of zoom.
            var scale = Math.Min(1, Math.Max((double)width / sourceWidth, (double)height / sourceHeight) * 1.1);
            var (w, h) = (Math.Max(1, (int)Math.Round(sourceWidth * scale)), Math.Max(1, (int)Math.Round(sourceHeight * scale)));
            using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
            if (surface is null) return null;
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            canvas.Scale((float)scale);
            Upright(canvas, codec.EncodedOrigin, info.Width, info.Height);
            using (var source = SKImage.FromBitmap(decoded))
                canvas.DrawImage(source, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            var image = surface.Snapshot();
            return new Picture(image, hasAlpha ? null : Blur(image), hasAlpha);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Turns the canvas so a picture stored sideways or upside down (by its EXIF note) is drawn upright.</summary>
    private static void Upright(SKCanvas canvas, SKEncodedOrigin origin, int w, int h)
    {
        switch (origin)
        {
            case SKEncodedOrigin.BottomRight: // upside down
                canvas.Translate(w, h);
                canvas.RotateDegrees(180);
                break;
            case SKEncodedOrigin.RightTop: // a quarter turn clockwise to put right
                canvas.Translate(h, 0);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.LeftBottom: // a quarter turn the other way
                canvas.Translate(0, w);
                canvas.RotateDegrees(270);
                break;
            case SKEncodedOrigin.TopRight: // mirrored
                canvas.Translate(w, 0);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.BottomLeft:
                canvas.Translate(0, h);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.LeftTop:
                canvas.RotateDegrees(90);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.RightBottom:
                canvas.Translate(h, w);
                canvas.RotateDegrees(90);
                canvas.Scale(-1, 1);
                break;
        }
    }

    /// <summary>A copy about 160 pixels tall, blurred until only its colours are left.</summary>
    private static SKImage? Blur(SKImage image)
    {
        var scale = 160.0 / image.Height;
        var (w, h) = (Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale)));
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (surface is null) return null;
        using var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(9, 9, SKShaderTileMode.Clamp) };
        surface.Canvas.DrawImage(image, new SKRect(0, 0, w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        return surface.Snapshot();
    }

    /// <summary>A file format that can be see-through (PNG) often isn't: look at the corners and middle rows.</summary>
    private static unsafe bool IsOpaque(SKBitmap bitmap)
    {
        var pixels = (byte*)bitmap.GetPixels();
        var step = Math.Max(1, bitmap.Height / 64);
        for (var y = 0; y < bitmap.Height; y += step)
        {
            var row = pixels + (long)y * bitmap.RowBytes;
            for (var x = 0; x < bitmap.Width; x++)
                if (row[x * 4 + 3] < 250) return false;
        }
        return true;
    }

    public void Dispose()
    {
        foreach (var held in pictures.Values) held.Picture?.Dispose();
        pictures.Clear();
        foreach (var clip in clips.Values) clip?.Dispose();
        clips.Clear();
    }
}
