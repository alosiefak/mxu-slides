using System.Runtime.InteropServices;
using MxU.Slides.Core.MotionTemplates;
using SkiaSharp;
using SkiaSharp.Resources;
using SkiaSharp.SceneGraph;
using SkiaSharp.Skottie;

namespace MxU.Slides.MotionTemplates.Lottie;

/// <summary>
/// A picture a <see cref="LottieRenderer"/> draws into: premultiplied BGRA, top row first, in
/// memory that never moves (it's allocated pinned), so Skia can draw straight into it and the
/// app can copy it out without an extra step. WPF's <c>Pbgra32</c> is the same layout.
/// </summary>
public sealed class LottieFrame : IDisposable
{
    private readonly SKSurface surface;

    public LottieFrame(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        Stride = Width * 4;
        Pixels = GC.AllocateUninitializedArray<byte>(Stride * Height, pinned: true);
        var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        surface = SKSurface.Create(info, Marshal.UnsafeAddrOfPinnedArrayElement(Pixels, 0), Stride)
                  ?? throw new InvalidOperationException($"Skia can't make a {Width} × {Height} picture");
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    /// <summary>The picture: <see cref="Stride"/> × <see cref="Height"/> bytes.</summary>
    public byte[] Pixels { get; }

    internal SKCanvas Canvas => surface.Canvas;

    public void Dispose() => surface.Dispose();
}

/// <summary>
/// Draws the frames of one Lottie animation with Skia's Skottie, the Lottie player Chrome and
/// Android use. It's built once from the animation (fields already filled in), then asked for
/// any frame in any order: Skottie works out each frame from the keyframes, so the same frame
/// always gives the same picture, wherever it falls in a play-through and on any PC.
/// <para>
/// The animation is drawn as large as fits the picture, centred, onto a transparent background,
/// so whatever isn't the design stays see-through. Fonts Windows hasn't got come from
/// <see cref="ILottieFonts"/> (<see cref="FontEmbedding"/>); pictures are inside the animation already
/// (<see cref="LottieFiles"/>). Use from one thread at a time.
/// </para>
/// </summary>
public sealed class LottieRenderer : IDisposable
{
    private readonly Animation animation;
    private readonly SKRect destination;
    private readonly InvalidationController changes = new();
    private bool moved;

    public LottieRenderer(LottieDocument document, ILottieFonts? fonts = null)
    {
        Document = document;
        var prepared = FontEmbedding.WithFonts(document, fonts);
        using var builder = Animation.CreateBuilder()
            .SetResourceProvider(new CachingResourceProvider(new DataUriResourceProvider(preDecode: true)))
            .SetFontManager(SKFontManager.Default);
        using var stream = new MemoryStream(prepared.ToUtf8());
        animation = builder.Build(stream) ?? throw new LottieFormatException("Skia couldn't read this animation.");
        destination = new SKRect(0, 0, (float)document.Width, (float)document.Height);
    }

    public LottieDocument Document { get; }

    /// <summary>
    /// Moves to frame <paramref name="frame"/> (in the animation's own numbering, from its in point)
    /// without drawing. True when the picture there differs from the frame moved to before: Skottie
    /// keeps track of what each move changes, so a lower third holding still reports no change and
    /// needn't be drawn again.
    /// </summary>
    public bool Seek(double frame)
    {
        changes.Reset();
        changes.Begin();
        // Skottie counts frames from the animation's in point.
        animation.SeekFrame(Math.Clamp(frame, Document.InPoint, Document.OutPoint) - Document.InPoint, changes);
        changes.End();
        var changed = !moved || !changes.Bounds.IsEmpty;
        moved = true;
        return changed;
    }

    /// <summary>Draws frame <paramref name="frame"/> into <paramref name="target"/>, fitted and centred.</summary>
    public void Render(double frame, LottieFrame target)
    {
        Seek(frame);
        RenderCurrent(target);
    }

    /// <summary>Draws the frame last moved to (<see cref="Seek"/>) into <paramref name="target"/>.</summary>
    public void RenderCurrent(LottieFrame target)
    {
        var canvas = target.Canvas;
        canvas.Clear(SKColors.Transparent);
        DrawCurrent(canvas, new SKRect(0, 0, target.Width, target.Height));
        canvas.Flush();
    }

    /// <summary>Draws frame <paramref name="frame"/> onto <paramref name="canvas"/>, fitted and centred in <paramref name="area"/>.</summary>
    public void Draw(double frame, SKCanvas canvas, SKRect area)
    {
        Seek(frame);
        DrawCurrent(canvas, area);
    }

    private void DrawCurrent(SKCanvas canvas, SKRect area)
    {
        var scale = Math.Min(area.Width / destination.Width, area.Height / destination.Height);
        var w = destination.Width * scale;
        var h = destination.Height * scale;
        var x = area.Left + (area.Width - w) / 2;
        var y = area.Top + (area.Height - h) / 2;
        animation.Render(canvas, new SKRect(x, y, x + w, y + h));
    }

    /// <summary>A size for drawing this animation fitted into <paramref name="maxWidth"/> × <paramref name="maxHeight"/>, keeping its shape.</summary>
    public static (int Width, int Height) FittedSize(LottieDocument document, int maxWidth, int maxHeight)
    {
        var scale = Math.Min(maxWidth / document.Width, maxHeight / document.Height);
        return (Math.Max(2, (int)Math.Round(document.Width * scale)), Math.Max(2, (int)Math.Round(document.Height * scale)));
    }

    public void Dispose()
    {
        animation.Dispose();
        changes.Dispose();
    }
}
