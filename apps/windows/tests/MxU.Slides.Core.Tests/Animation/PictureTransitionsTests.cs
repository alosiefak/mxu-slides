using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Outputs;

namespace MxU.Slides.Core.Tests.Animation;

// The Windows-only transitions' maths (Phase 6): the easing curves, where each picture is at a
// moment, and the numbers the shaders get. Screen units: 0–1 across and down.
public class PictureTransitionsTests
{
    private const double Aspect = 16.0 / 9;

    private static WindowsTransition T(WindowsTransitionKind kind, TransitionDirection direction = TransitionDirection.Left, double softness = 0.35) =>
        new() { Kind = kind, Direction = direction, Softness = softness, DurationSeconds = 1 };

    private static (double X, double Y) Centre(PicturePlane plane) => (plane.Corners.Average(c => c.X), plane.Corners.Average(c => c.Y));

    private static void Near((double X, double Y) expected, (double X, double Y) actual, double tolerance = 1e-6)
    {
        Assert.Equal(expected.X, actual.X, tolerance);
        Assert.Equal(expected.Y, actual.Y, tolerance);
    }

    private static bool IsScreen(PicturePlane plane) =>
        plane.Opacity > 0 && plane.Corners.Zip(PicturePlane.Screen).All(p => Math.Abs(p.First.X - p.Second.X) < 1e-6 && Math.Abs(p.First.Y - p.Second.Y) < 1e-6);

    // MARK: Curves

    [Fact]
    public void EveryCurveStartsAtZeroEndsAtOneAndNeverGoesBack()
    {
        foreach (var kind in WindowsTransitions.All)
        {
            Assert.Equal(0, TransitionCurves.Eased(kind, 0), 1e-9);
            Assert.Equal(1, TransitionCurves.Eased(kind, 1), 1e-9);
            var last = 0.0;
            for (var t = 0.0; t <= 1.0001; t += 0.01)
            {
                var e = TransitionCurves.Eased(kind, t);
                Assert.True(e >= last - 1e-9, $"{kind} went back at {t}");
                last = e;
            }
        }
        Assert.Equal(0, TransitionCurves.Eased(WindowsTransitionKind.Push, -3));
        Assert.Equal(1, TransitionCurves.Eased(WindowsTransitionKind.Push, 7));
    }

    [Fact]
    public void TheSCurvesAreSymmetricAndGentleAtTheEnds()
    {
        foreach (var curve in new Func<double, double>[] { TransitionCurves.InOutSine, TransitionCurves.InOutCubic, TransitionCurves.InOutQuart, TransitionCurves.InOutQuint })
        {
            for (var t = 0.0; t <= 1; t += 0.05) Assert.Equal(1 - curve(1 - t), curve(t), 1e-9);
            Assert.Equal(0.5, curve(0.5), 1e-9);
            Assert.True(curve(0.05) < 0.05); // slower than the clock at the start…
            Assert.True(curve(0.95) > 0.95); // …and at the end
        }
        Assert.True(TransitionCurves.InOutQuint(0.1) < TransitionCurves.InOutCubic(0.1)); // a whip waits longer before it goes
    }

    [Fact]
    public void ABellIsZeroAtTheEndsAndFullInTheMiddle()
    {
        Assert.Equal(0, TransitionCurves.Bell(0), 1e-9);
        Assert.Equal(1, TransitionCurves.Bell(0.5), 1e-9);
        Assert.Equal(0, TransitionCurves.Bell(1), 1e-9);
        Assert.Equal(0.5, TransitionCurves.Smoothstep(0.4, 0.6, 0.5), 1e-9);
    }

    [Fact]
    public void AWhipIsFastestInTheMiddle()
    {
        var middle = TransitionCurves.Speed(WindowsTransitionKind.WhipPan, 0.5);
        Assert.Equal(5, middle, 0.05); // InOutQuint's slope at the half-way point (measured over a thousandth either side)
        Assert.True(TransitionCurves.Speed(WindowsTransitionKind.WhipPan, 0.05) < 0.01);
        Assert.Equal(0.25, PictureTransitions.WhipBlur(0.5), 1e-3);
        Assert.True(PictureTransitions.WhipBlur(0.02) < 0.001); // no streak when barely moving
    }

    // MARK: Moves

    [Theory]
    [InlineData(TransitionDirection.Left, -1, 0)]
    [InlineData(TransitionDirection.Right, 1, 0)]
    [InlineData(TransitionDirection.Up, 0, -1)]
    [InlineData(TransitionDirection.Down, 0, 1)]
    public void APushMovesBothPicturesTheWayItSays(TransitionDirection direction, double dx, double dy)
    {
        var start = PictureTransitions.Planes(T(WindowsTransitionKind.Push, direction), 0, Aspect);
        Assert.True(IsScreen(start.Old));
        Near((0.5 - dx, 0.5 - dy), Centre(start.New)); // waiting just off the screen, on the side it comes from

        var half = PictureTransitions.Planes(T(WindowsTransitionKind.Push, direction), 0.5, Aspect);
        Near((0.5 + dx * 0.5, 0.5 + dy * 0.5), Centre(half.Old)); // half-way off
        Near((0.5 - dx * 0.5, 0.5 - dy * 0.5), Centre(half.New)); // half-way on, touching it
        Assert.False(half.NewOnTop || half.OldOnTop); // they never overlap

        var end = PictureTransitions.Planes(T(WindowsTransitionKind.Push, direction), 1, Aspect);
        Assert.True(IsScreen(end.New));
    }

    [Fact]
    public void APushIsEasedNotLinear()
    {
        var early = PictureTransitions.Planes(T(WindowsTransitionKind.Push), 0.1, Aspect);
        Assert.True(Centre(early.Old).X > 0.49); // a tenth of the time in, it has barely moved (cubic: 0.4 %)
    }

    [Fact]
    public void SlideOverPutsTheNewPictureOnTopAndDimsTheOld()
    {
        var half = PictureTransitions.Planes(T(WindowsTransitionKind.SlideOver), 0.5, Aspect);
        Assert.True(half.NewOnTop);
        Assert.True(half.Old.Brightness < 1);
        Assert.Equal(0.5 - 0.15, Centre(half.Old).X, 1e-6); // the old one drifts only a little (parallax)
        Assert.Equal(1, Centre(half.New).X, 1e-6);
    }

    [Fact]
    public void SlideRevealPutsTheOldPictureOnTop()
    {
        var half = PictureTransitions.Planes(T(WindowsTransitionKind.SlideReveal, TransitionDirection.Up), 0.5, Aspect);
        Assert.True(half.OldOnTop);
        Assert.Equal(0, Centre(half.Old).Y, 1e-6); // half-way up and away
        Assert.True(half.New.Brightness is > 0.65 and < 1);
        Assert.True(IsScreen(PictureTransitions.Planes(T(WindowsTransitionKind.SlideReveal), 1, Aspect).New));
    }

    [Fact]
    public void ZoomsFadeTheOldPictureOffTheNewOneAndNeverShowAnEdge()
    {
        foreach (var kind in new[] { WindowsTransitionKind.ZoomIn, WindowsTransitionKind.ZoomOut })
        {
            var half = PictureTransitions.Planes(T(kind), 0.5, Aspect);
            Assert.True(half.OldOnTop);
            Assert.Equal(0.5, half.Old.Opacity, 1e-9);
            Near((0.5, 0.5), Centre(half.Old)); // about the middle
            for (var t = 0.0; t <= 1; t += 0.1)
            {
                // The new picture, underneath, always covers the whole screen.
                var corners = PictureTransitions.Planes(T(kind), t, Aspect).New.Corners;
                Assert.True(corners[0].X <= 1e-9 && corners[0].Y <= 1e-9 && corners[3].X >= 1 - 1e-9 && corners[3].Y >= 1 - 1e-9, $"{kind} at {t}");
            }
            var end = PictureTransitions.Planes(T(kind), 1, Aspect);
            Assert.True(IsScreen(end.New));
            Assert.Equal(0, end.Old.Opacity, 1e-9);
        }
        var zoomIn = PictureTransitions.Planes(T(WindowsTransitionKind.ZoomIn), 0.5, Aspect);
        Assert.True(zoomIn.Old.Corners[1].X - zoomIn.Old.Corners[0].X > 1); // flying in: the old picture is bigger than the screen
        var zoomOut = PictureTransitions.Planes(T(WindowsTransitionKind.ZoomOut), 0.5, Aspect);
        Assert.True(zoomOut.Old.Corners[1].X - zoomOut.Old.Corners[0].X < 1); // falling away
    }

    // MARK: 3-D

    [Theory]
    [InlineData(TransitionDirection.Left)]
    [InlineData(TransitionDirection.Right)]
    [InlineData(TransitionDirection.Up)]
    [InlineData(TransitionDirection.Down)]
    public void ACubeTurnsTheNewSideToTheFront(TransitionDirection direction)
    {
        var start = PictureTransitions.Planes(T(WindowsTransitionKind.Cube, direction), 0, Aspect);
        Assert.True(IsScreen(start.Old));
        Assert.Equal(0, start.New.Opacity); // round the side, out of sight

        var half = PictureTransitions.Planes(T(WindowsTransitionKind.Cube, direction), 0.5, Aspect);
        Assert.True(half.Old.Opacity > 0 && half.New.Opacity > 0); // two sides at once
        // The edge nearest the camera never looms past the screen (the cube backs away).
        foreach (var corner in half.Old.Corners.Concat(half.New.Corners))
        {
            Assert.InRange(corner.X, -0.05, 1.05);
            Assert.InRange(corner.Y, -0.05, 1.05);
        }
        // The old side has turned the way the pictures travel.
        var (dx, dy) = PictureTransitions.Vector(direction);
        var (cx, cy) = Centre(half.Old);
        Assert.True((cx - 0.5) * dx + (cy - 0.5) * dy > 0.1);
        Assert.True(half.Old.Brightness < 1); // turned away from the light

        var end = PictureTransitions.Planes(T(WindowsTransitionKind.Cube, direction), 1, Aspect);
        Assert.True(end.New.Corners.Zip(PicturePlane.Screen).All(p => Math.Abs(p.First.X - p.Second.X) < 1e-6 && Math.Abs(p.First.Y - p.Second.Y) < 1e-6));
    }

    [Fact]
    public void AFlipShowsTheOldFaceThenTheNewOne()
    {
        var early = PictureTransitions.Planes(T(WindowsTransitionKind.Flip), 0.3, Aspect);
        Assert.True(early.Old.Opacity > 0);
        Assert.Equal(0, early.New.Opacity);
        var late = PictureTransitions.Planes(T(WindowsTransitionKind.Flip), 0.7, Aspect);
        Assert.Equal(0, late.Old.Opacity);
        Assert.True(late.New.Opacity > 0);
        // Turning, it's narrower than the screen; it lifts away so it never grows past it.
        var width = early.Old.Corners.Max(c => c.X) - early.Old.Corners.Min(c => c.X);
        Assert.True(width < 0.9);
        Assert.All(early.Old.Corners, c => Assert.InRange(c.Y, -0.05, 1.05));
        Assert.True(IsScreen(PictureTransitions.Planes(T(WindowsTransitionKind.Flip), 1, Aspect).New));
    }

    [Fact]
    public void TheHomographyFindsThePictureUnderEachPixel()
    {
        var half = PictureTransitions.Planes(T(WindowsTransitionKind.Push), 0.5, Aspect);
        var rows = PictureTransitions.ScreenToPicture(half.New)!;
        // The screen's middle is the new picture's left edge, half-way down.
        var (u, v) = OutputWarp.Map(rows, 0.5, 0.5)!.Value;
        Assert.Equal(0, u, 1e-6);
        Assert.Equal(0.5, v, 1e-6);
        Assert.Null(PictureTransitions.ScreenToPicture(PicturePlane.Hidden));

        var cube = PictureTransitions.Planes(T(WindowsTransitionKind.Cube), 0.5, Aspect);
        var cubeRows = PictureTransitions.ScreenToPicture(cube.New)!;
        for (var i = 0; i < 4; i++)
        {
            var mapped = OutputWarp.Map(cubeRows, cube.New.Corners[i].X, cube.New.Corners[i].Y)!.Value;
            Near(PicturePlane.Screen[i], mapped, 1e-6);
        }
    }

    // MARK: Wipes and the rest

    [Fact]
    public void AWipeShowsNothingNewAtTheStartAndAllOfItAtTheEnd()
    {
        foreach (var kind in new[] { WindowsTransitionKind.Iris, WindowsTransitionKind.Diamond, WindowsTransitionKind.Clock, WindowsTransitionKind.SoftWipe, WindowsTransitionKind.LumaWipe })
            foreach (var softness in new[] { 0.0, 0.35, 1.0 })
            {
                var t = T(kind, softness: softness);
                var edge = PictureTransitions.WipeEdge(t);
                Assert.True(PictureTransitions.WipeThreshold(t, 0) + edge <= 1e-9); // every pixel (reveal time ≥ 0) still old
                Assert.True(PictureTransitions.WipeThreshold(t, 1) >= 1 - 1e-9); // every pixel (reveal time ≤ 1) new
            }
        Assert.True(PictureTransitions.WipeEdge(T(WindowsTransitionKind.Iris, softness: 1)) > PictureTransitions.WipeEdge(T(WindowsTransitionKind.Iris, softness: 0)));
    }

    [Fact]
    public void AWipeGetsItsPatternAndDirection()
    {
        var iris = PictureTransitions.Mix(T(WindowsTransitionKind.Iris), 0.5, Aspect, 0.5, (1920, 1080));
        Assert.Equal(PictureShader.Wipe, iris.Shader);
        Assert.Equal(new Vec4(1, 0, 0, 0), iris.Constants[1]);
        var wipe = PictureTransitions.Mix(T(WindowsTransitionKind.SoftWipe, TransitionDirection.Up), 0.5, Aspect, 0.5, (1920, 1080));
        Assert.Equal((0.0, -1.0), (wipe.Constants[2].X, wipe.Constants[2].Y));
        // The farthest point of a circle and a diamond (the corners) is revealed exactly at the end.
        Assert.Equal(1 / Math.Sqrt(Aspect * Aspect / 4 + 0.25), iris.Constants[2].Z, 1e-9);
        Assert.Equal(1 / (Aspect / 2 + 0.5), iris.Constants[2].W, 1e-9);
        var clock = PictureTransitions.Mix(T(WindowsTransitionKind.Clock), 0.5, Aspect, 0.5, (1920, 1080));
        Assert.Equal(1, clock.Constants[3].X);
    }

    [Fact]
    public void EveryKindHasItsShaderAndEightRegisters()
    {
        foreach (var kind in WindowsTransitions.WindowsOnly)
            foreach (var t in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
            {
                var mix = PictureTransitions.Mix(T(kind), t, Aspect, t, (1920, 1080));
                Assert.Equal(PictureTransitions.ShaderFor(kind), mix.Shader);
                Assert.Equal(8, mix.Constants.Count);
                Assert.All(mix.Constants, c => Assert.True(double.IsFinite(c.X) && double.IsFinite(c.Y) && double.IsFinite(c.Z) && double.IsFinite(c.W), $"{kind} at {t}"));
            }
        Assert.Equal(PictureShader.Planes, PictureTransitions.ShaderFor(WindowsTransitionKind.Cube));
        Assert.Equal(PictureShader.Radial, PictureTransitions.ShaderFor(WindowsTransitionKind.CrossZoom));
    }

    [Fact]
    public void PlanesConstantsCarryOpacitiesAndTheOutputsPixels()
    {
        var mix = PictureTransitions.Mix(T(WindowsTransitionKind.SlideOver), 0.5, Aspect, 0.5, (1280, 720));
        Assert.Equal((1280.0, 720.0), (mix.Constants[0].Z, mix.Constants[0].W)); // edges are smoothed over one output pixel
        Assert.Equal(new Vec4(1, 1, 0, 1), mix.Constants[7]); // both shown, the new one on top
        var flip = PictureTransitions.Mix(T(WindowsTransitionKind.Flip), 0.25, Aspect, 0.25, (1920, 1080));
        Assert.Equal(0, flip.Constants[7].Y); // the back of the card isn't drawn yet
    }

    [Fact]
    public void ZoomThroughRushesInAndCrossZoomDrifts()
    {
        var through = PictureTransitions.Zoom(WindowsTransitionKind.ZoomThrough, 0.5);
        Assert.Equal(2, through.OldScale, 1e-9); // rushing into the old picture…
        Assert.Equal(2, through.NewScale, 1e-9); // …and out of the new one, never smaller than the screen
        Assert.Equal(1, through.OldOpacity + through.NewOpacity, 1e-9);
        Assert.True(through.Blur > 0.2);
        Assert.True(PictureTransitions.Zoom(WindowsTransitionKind.ZoomThrough, 0).Blur < 1e-3);
        Assert.Equal(1, PictureTransitions.Zoom(WindowsTransitionKind.ZoomThrough, 1).NewScale, 1e-9);

        var early = PictureTransitions.Zoom(WindowsTransitionKind.CrossZoom, 0.1);
        var late = PictureTransitions.Zoom(WindowsTransitionKind.CrossZoom, 0.9);
        Assert.True(late.X > early.X); // the point it streaks to drifts across
        Assert.Equal(0, PictureTransitions.Zoom(WindowsTransitionKind.CrossZoom, 0).Blur, 1e-9);
        Assert.Equal(1, PictureTransitions.Zoom(WindowsTransitionKind.CrossZoom, 1).NewOpacity, 1e-9);
        var streak = PictureTransitions.Streak(0.4);
        Assert.Equal(8, streak.Length);
        Assert.Equal(1, streak[0], 1e-9); // the pixel itself…
        Assert.Equal(1 - 0.4 / 7, streak[1], 1e-9);
        Assert.Equal(0.6, streak[7], 1e-9); // …to 40 % of the way to the centre
    }

    [Fact]
    public void TheWhipSamplesSitWhereThePushPutsThePictures()
    {
        var mix = PictureTransitions.Mix(T(WindowsTransitionKind.WhipPan, TransitionDirection.Right), 0.5, Aspect, 0.5, (1920, 1080));
        Assert.Equal((1.0, 0.0), (mix.Constants[0].X, mix.Constants[0].Y)); // across the screen
        // Half-way, the old picture is pushed half right: a pixel reads it half a screen to its left, streaked either side.
        var old = new[] { mix.Constants[1].X, mix.Constants[1].Y, mix.Constants[1].Z, mix.Constants[1].W, mix.Constants[2].X, mix.Constants[2].Y, mix.Constants[2].Z, mix.Constants[2].W };
        Assert.Equal(-0.5, old.Average(), 1e-9);
        Assert.Equal(0.25, old.Max() - old.Min(), 1e-3);
    }

    [Fact]
    public void LightLeakGlitchAndRippleAreStrongestInTheMiddle()
    {
        Assert.Equal(0, PictureTransitions.Light(0, Aspect).Glow, 1e-9);
        Assert.True(PictureTransitions.Light(0.5, Aspect).Glow > 0.5);
        Assert.True(PictureTransitions.Light(0.8, Aspect).Band > PictureTransitions.Light(0.2, Aspect).Band); // the light sweeps across
        Assert.Equal(0, PictureTransitions.GlitchStrength(0), 1e-9);
        Assert.Equal(1, PictureTransitions.GlitchStrength(0.5), 1e-9);
        Assert.Equal(0, PictureTransitions.Ripple(0, Aspect).Amplitude, 1e-9);
        Assert.True(PictureTransitions.Ripple(1, Aspect).Ring > Math.Sqrt(Aspect * Aspect / 4 + 0.25)); // the ring has passed the corners
        // The glitch's blocks have all switched before the end, and none at the start.
        Assert.True(PictureTransitions.Mix(T(WindowsTransitionKind.Glitch), 0, Aspect, 0, (1920, 1080)).Constants[0].X <= 0);
        Assert.True(PictureTransitions.Mix(T(WindowsTransitionKind.Glitch), 0.9, Aspect, 0.9, (1920, 1080)).Constants[0].X >= 1);
    }

    [Fact]
    public void TheGlitchJittersOnTheClock()
    {
        double Seed(double elapsed) => PictureTransitions.Mix(T(WindowsTransitionKind.Glitch), 0.5, Aspect, elapsed, (1920, 1080)).Constants[0].W;
        Assert.Equal(Seed(0.50), Seed(0.51)); // steps 24 times a second, not every frame
        Assert.NotEqual(Seed(0.50), Seed(0.56));
    }

    [Fact]
    public void LightLeakTakesItsColour()
    {
        var amber = PictureTransitions.Mix(T(WindowsTransitionKind.LightLeak), 0.5, Aspect, 0.5, (1920, 1080)).Constants[1];
        Assert.True(amber.X > amber.Z); // warm by default
        var blue = PictureTransitions.Mix(T(WindowsTransitionKind.LightLeak) with { ColorHex = "#0000FF" }, 0.5, Aspect, 0.5, (1920, 1080)).Constants[1];
        Assert.Equal(new Vec4(0, 0, 1, 0), blue);
        // Over a layer that's already leaking, it lights only its own pixels.
        Assert.Equal(1, PictureTransitions.Mix(T(WindowsTransitionKind.LightLeak), 0.5, Aspect, 0.5, (1920, 1080), litBelow: true).Constants[1].W);
    }
}
