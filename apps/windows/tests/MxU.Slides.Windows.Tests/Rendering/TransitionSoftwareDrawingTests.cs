using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Rendering;
using MxU.Slides.Windows.Views.Transitions;
using LayerTransition = MxU.Slides.Core.Animation.SceneTransition;
using ViewTransition = MxU.Slides.Windows.Rendering.SceneTransition;

namespace MxU.Slides.Windows.Tests.Rendering;

// The Windows-only transitions drawn by WPF's software drawing (Phase 6): what thumbnails, the
// picker's previews on a PC without a graphics card, and the software copy would draw; and a view
// copied in software playing the nearest Mac kind instead, because that drawing is slow.
public class TransitionSoftwareDrawingTests(ITestOutputHelper output)
{
    /// <summary>WPF wants its own kind of thread (STA, with a dispatcher) for anything it draws.</summary>
    private static void OnWpfThread(Action work)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { work(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(2)), "drawing didn't finish in two minutes");
        // Rethrown as it was, so a failed assertion reads as itself.
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    /// <summary>A scene as WPF's software drawing draws it (RenderTargetBitmap always draws in software).</summary>
    private static byte[] Drawn(AnimatedScene scene, int width, int height, out double milliseconds)
    {
        var view = new SceneView { PlaysVideo = false, Width = width, Height = height };
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.Present(scene, ViewTransition.Cut);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var watch = Stopwatch.StartNew();
        bitmap.Render(view);
        milliseconds = watch.Elapsed.TotalMilliseconds;
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    private static double Difference(byte[] a, byte[] b)
    {
        double total = 0;
        for (var i = 0; i < a.Length; i++) total += Math.Abs(a[i] - b[i]);
        return total / a.Length;
    }

    [Fact]
    public void EveryWindowsKindDrawsHalfWayInSoftware()
    {
        var timings = new List<string>();
        OnWpfThread(() =>
        {
            var before = TransitionSamples.Before();
            var after = TransitionSamples.After();
            var beforePixels = Drawn(before, 320, 180, out _);
            var afterPixels = Drawn(after, 320, 180, out _);
            foreach (var kind in WindowsTransitions.WindowsOnly)
            {
                var engine = new SceneTransitionEngine(before);
                engine.Push(after, LayerTransition.Of(new WindowsTransition { Kind = kind, DurationSeconds = 1 }), now: 0);
                var half = Drawn(engine.Scene(0.5), 320, 180, out var milliseconds);
                timings.Add($"{kind} {milliseconds:0} ms");
                // Half-way is neither picture: the shader ran (an effect that isn't drawn shows the new one).
                Assert.True(Difference(half, beforePixels) > 2, $"{kind} looks like the old picture half-way");
                Assert.True(Difference(half, afterPixels) > 2, $"{kind} looks like the new picture half-way");
            }
        });
        output.WriteLine(string.Join(", ", timings));
    }

    [Fact]
    public void AViewCopiedInSoftwarePlaysAPlainDissolve()
    {
        OnWpfThread(() =>
        {
            // A Zoom Through: its nearest Mac kind is a Blur Dissolve, which software drawing can't afford.
            // Both are long, so they're still playing however late a busy PC wakes from the sleep below.
            var zoom = LayerTransition.Of(new WindowsTransition { Kind = WindowsTransitionKind.ZoomThrough, DurationSeconds = 30 });
            var macBlur = new LayerTransition(SceneTransitionKind.BlurDissolve, 30);
            AnimatedScene MidWay(Func<bool>? copiedInSoftware, LayerTransition transition)
            {
                // Offscreen, as a feed's view says it is: a view that isn't on a screen would just cut.
                var view = new SceneView { PlaysVideo = false, Offscreen = true, Width = 320, Height = 180, CopiedInSoftware = copiedInSoftware };
                view.Present(TransitionSamples.Before(), ViewTransition.Cut);
                view.Present(TransitionSamples.After(), ViewTransition.PerLayer(_ => transition));
                // A little way in (an update carries the transition on), where a blur would be showing.
                Thread.Sleep(400);
                view.Update(TransitionSamples.After());
                return view.Scene!;
            }
            static bool Blurred(AnimatedScene scene) => scene.Layers.SelectMany(l => l.Items).Any(item => item.Effects.Any(e => e is AnimationEffect.Blur));
            // Drawn by the graphics card (or not a capture at all): the zoom mixes both layers' pictures.
            Assert.NotNull(MidWay(null, zoom)[LayerKind.Slide].Mix);
            Assert.NotNull(MidWay(() => false, zoom)[LayerKind.StillGraphics].Mix);
            // Copied in software: no picture mix and no blur; the layers dissolve item by item (the old words leaving).
            var copied = MidWay(() => true, zoom);
            Assert.Null(copied[LayerKind.Slide].Mix);
            Assert.Null(copied[LayerKind.StillGraphics].Mix);
            Assert.Contains(copied[LayerKind.Slide].Items, item => item.Id.EndsWith("::leaving", StringComparison.Ordinal));
            Assert.False(Blurred(copied));
            // The Mac's own Blur Dissolve blurs on the graphics card, but dissolves when copied in software.
            Assert.True(Blurred(MidWay(() => false, macBlur)));
            Assert.False(Blurred(MidWay(() => true, macBlur)));
        });
    }
}
