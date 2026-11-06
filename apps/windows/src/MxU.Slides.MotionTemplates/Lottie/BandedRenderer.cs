using MxU.Slides.Core.Motion;
using MxU.Slides.Core.MotionTemplates;

namespace MxU.Slides.MotionTemplates.Lottie;

/// <summary>
/// Draws one animation's frames in horizontal bands on several threads at once, for the templates
/// one core can't draw fast enough: a full-screen background of big soft glows takes tens of
/// milliseconds a frame at 1080p, where a lower third takes one or two.
/// <para>
/// A Skottie animation can only be used by one thread at a time, so each band has an animation of
/// its own (built from the same file). Each moves to the frame and draws just its band of the
/// picture (<see cref="LottieRenderer.RenderBand"/>): Skia skips what falls outside, so four bands on
/// four threads take about a quarter of the time. The bands are shared out by a
/// <see cref="RenderCrew"/>, threads of the crew's own, never .NET's shared pool. Every pixel is
/// drawn by exactly one band, with the same maths as a whole frame, so the picture is the same.
/// </para>
/// </summary>
public sealed class BandedRenderer : IDisposable
{
    private readonly LottieRenderer[] renderers;
    private readonly RenderCrew crew;

    public BandedRenderer(LottieDocument document, ILottieFonts? fonts, RenderCrew crew)
    {
        this.crew = crew;
        renderers = new LottieRenderer[Math.Max(2, crew.Size)];
        for (var i = 0; i < renderers.Length; i++) renderers[i] = new LottieRenderer(document, fonts);
    }

    public int Bands => renderers.Length;

    /// <summary>Moves to a frame (the first band's animation; the others move as they draw), saying what changed.</summary>
    public bool Seek(double frame, int width, int height, out PixelRect changed) => renderers[0].Seek(frame, width, height, out changed);

    /// <summary>Draws <paramref name="frame"/> into <paramref name="target"/>, a band per animation, on the crew.</summary>
    public void Render(double frame, LottieFrame target)
    {
        var count = renderers.Length;
        var rows = (target.Height + count - 1) / count;
        crew.For(count, (band, _) =>
        {
            var top = band * rows;
            var height = Math.Min(rows, target.Height - top);
            if (height <= 0) return;
            if (band > 0) renderers[band].Seek(frame);
            renderers[band].RenderBand(target, top, height);
        });
    }

    public void Dispose()
    {
        foreach (var renderer in renderers) renderer.Dispose();
    }
}
