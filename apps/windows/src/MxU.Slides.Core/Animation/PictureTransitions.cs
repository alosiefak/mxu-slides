using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Animation;

/// <summary>Four numbers: one of a shader's constant registers (x, y, z, w).</summary>
public readonly record struct Vec4(double X, double Y, double Z, double W)
{
    public static readonly Vec4 Zero = new(0, 0, 0, 0);
}

/// <summary>
/// A picture transition in progress on one layer, as the engine hands it to the view: what is
/// playing, how far through it is (0–1, straight time), and how long it has run. The layer's
/// items are its two pictures: ids ending "::leaving" are the old picture, the rest the new one.
/// <see cref="Inner"/> is set when the old picture was itself caught mid-transition (the operator
/// pressed Next again before a push landed): that picture is drawn as it looked at that moment.
/// <see cref="LitBelow"/> is set on a Light Leak when a layer beneath has one too: that layer lights
/// the whole screen, so this one lights only its own pixels (or the light would be added twice).
/// </summary>
public sealed record LayerMix(WindowsTransition Transition, double Progress, double Elapsed, LayerMix? Inner = null, bool LitBelow = false);

/// <summary>Which of the graphics-card programs draws a kind (several kinds share one, with different numbers).</summary>
public enum PictureShader { Planes, Wipe, Radial, Whip, Light, Glitch, Ripple }

/// <summary>One frame of a picture transition: the shader to run and its eight constant registers (c0–c7).</summary>
public sealed record PictureMix(PictureShader Shader, IReadOnlyList<Vec4> Constants);

/// <summary>
/// A flat picture placed on the screen: where its four corners land (in screen units, 0–1 across
/// and down; top-left, top-right, bottom-left, bottom-right), how opaque it is, and how bright
/// (a cube's side darkens as it turns away).
/// </summary>
public sealed record PicturePlane(IReadOnlyList<(double X, double Y)> Corners, double Opacity = 1, double Brightness = 1)
{
    /// <summary>The screen, exactly.</summary>
    public static readonly IReadOnlyList<(double X, double Y)> Screen = [(0, 0), (1, 0), (0, 1), (1, 1)];

    public static readonly PicturePlane Hidden = new(Screen, Opacity: 0);
}

/// <summary>
/// The Windows-only transitions, worked out as numbers for the graphics card. Each frame, the
/// view's shader gets two pictures (the layer before and after) and the numbers from
/// <see cref="Mix"/>, and works out every pixel from them. All the geometry lives here, in plain
/// C#, so it can be tested; the shaders only follow instructions. Windows only: the Mac has no twin.
/// <para>
/// Moves, zooms and the 3-D kinds are each two flat pictures placed on the screen
/// (<see cref="Planes"/>): a push is two pictures side by side sliding along, a cube is two sides
/// of a box seen in perspective. Each placement becomes a homography, the same maths as a
/// screen's corner pin (<see cref="OutputWarp"/>), which tells the shader where in the picture each
/// pixel of the screen is.
/// </para>
/// </summary>
public static class PictureTransitions
{
    /// <summary>How far the camera is from the screen, in screen heights: about a 35° lens, enough perspective to read as 3-D.</summary>
    public const double CameraDistance = 1.6;

    public static PictureShader ShaderFor(WindowsTransitionKind kind) => kind switch
    {
        WindowsTransitionKind.Iris or WindowsTransitionKind.Diamond or WindowsTransitionKind.Clock
            or WindowsTransitionKind.SoftWipe or WindowsTransitionKind.LumaWipe => PictureShader.Wipe,
        WindowsTransitionKind.ZoomThrough or WindowsTransitionKind.CrossZoom => PictureShader.Radial,
        WindowsTransitionKind.WhipPan => PictureShader.Whip,
        WindowsTransitionKind.LightLeak => PictureShader.Light,
        WindowsTransitionKind.Glitch => PictureShader.Glitch,
        WindowsTransitionKind.Ripple => PictureShader.Ripple,
        _ => PictureShader.Planes,
    };

    /// <summary>The way the pictures travel, in screen units (y grows downwards, as on screen).</summary>
    public static (double X, double Y) Vector(TransitionDirection direction) => direction switch
    {
        TransitionDirection.Right => (1, 0),
        TransitionDirection.Up => (0, -1),
        TransitionDirection.Down => (0, 1),
        _ => (-1, 0),
    };

    /// <summary>
    /// The numbers for one frame.
    /// </summary>
    /// <param name="progress">How much of the transition's time has gone, 0–1.</param>
    /// <param name="aspect">The canvas's width over its height (16:9 is 1.78), so circles stay round.</param>
    /// <param name="elapsed">Seconds since it started (a glitch jitters on the clock, not on progress).</param>
    /// <param name="pixels">The picture's size on the output, in pixels: a moving edge is smoothed over one pixel.</param>
    /// <param name="litBelow">A Light Leak on a layer above another one's (<see cref="LayerMix.LitBelow"/>).</param>
    public static PictureMix Mix(WindowsTransition transition, double progress, double aspect, double elapsed, (double Width, double Height) pixels,
        bool litBelow = false)
    {
        var t = TransitionCurves.Clamp01(progress);
        aspect = aspect > 0.01 ? aspect : 16.0 / 9;
        var c = new Vec4[8];
        var shader = ShaderFor(transition.Kind);
        switch (shader)
        {
            case PictureShader.Planes:
                PlanesConstants(c, Planes(transition, t, aspect), pixels);
                break;
            case PictureShader.Wipe:
                WipeConstants(c, transition, t, aspect);
                break;
            case PictureShader.Radial:
                RadialConstants(c, transition.Kind, t, pixels);
                break;
            case PictureShader.Whip:
                WhipConstants(c, transition, t, pixels);
                break;
            case PictureShader.Light:
                LightConstants(c, transition, t, aspect, litBelow);
                break;
            case PictureShader.Glitch:
                GlitchConstants(c, t, elapsed);
                break;
            case PictureShader.Ripple:
                RippleConstants(c, t, aspect, elapsed);
                break;
        }
        return new PictureMix(shader, c);
    }

    // MARK: Moves, zooms and 3-D: two flat pictures

    /// <summary>
    /// Where the old and new pictures are at eased progress, and how to put one over the other:
    /// <c>NewOnTop</c> draws the new picture over the old (Slide Over), <c>OldOnTop</c> the old over
    /// the new (Slide Reveal, the zooms); with neither, the two are added (they never overlap). The
    /// one on top hides what's under its whole area, by its opacity, even where it's transparent:
    /// a slide layer is mostly transparent round its words, and the old words must still go under
    /// the new slide as it slides over them.
    /// </summary>
    public static (PicturePlane Old, PicturePlane New, bool NewOnTop, bool OldOnTop) Planes(WindowsTransition transition, double t, double aspect)
    {
        var e = TransitionCurves.Eased(transition.Kind, t);
        var (dx, dy) = Vector(transition.Direction);
        switch (transition.Kind)
        {
            case WindowsTransitionKind.Push:
                // Side by side, sliding along together: the old one leaves as the new one arrives.
                return (Moved(dx * e, dy * e), Moved(dx * (e - 1), dy * (e - 1)), false, false);
            case WindowsTransitionKind.SlideOver:
                // The new picture slides over; the old one drifts a little the same way and dims, for depth.
                return (Moved(dx * e * 0.3, dy * e * 0.3) with { Brightness = 1 - 0.35 * e },
                    Moved(dx * (e - 1), dy * (e - 1)), true, false);
            case WindowsTransitionKind.SlideReveal:
                // The old picture slides off; the new one, beneath, drifts in from a little behind and brightens.
                return (Moved(dx * e, dy * e),
                    Moved(dx * (e - 1) * 0.3, dy * (e - 1) * 0.3) with { Brightness = 0.65 + 0.35 * e }, false, true);
            case WindowsTransitionKind.ZoomIn:
            {
                // Flying forwards: the old picture grows past us and fades, uncovering the new one, which
                // settles back into place. Neither is ever smaller than the screen, so no edge shows.
                var fade = TransitionCurves.InOutSine(t);
                return (Scaled(1 + 0.6 * e) with { Opacity = 1 - fade }, Scaled(1.3 - 0.3 * e), false, true);
            }
            case WindowsTransitionKind.ZoomOut:
            {
                // The old picture falls away from us, shrinking and fading over the new one, which settles in.
                var fade = TransitionCurves.InOutSine(t);
                return (Scaled(1 - 0.35 * e) with { Opacity = 1 - fade }, Scaled(1.2 - 0.2 * e), false, true);
            }
            case WindowsTransitionKind.Cube:
                return (CubeFace(transition.Direction, e, aspect, newFace: false), CubeFace(transition.Direction, e, aspect, newFace: true), false, false);
            case WindowsTransitionKind.Flip:
                return (FlipFace(transition.Direction, e, aspect, newFace: false), FlipFace(transition.Direction, e, aspect, newFace: true), false, false);
            default:
                // Not a planes kind: show the new picture.
                return (PicturePlane.Hidden, new PicturePlane(PicturePlane.Screen), false, false);
        }
    }

    private static PicturePlane Moved(double dx, double dy) => new([(dx, dy), (1 + dx, dy), (dx, 1 + dy), (1 + dx, 1 + dy)]);

    /// <summary>The screen scaled about its centre.</summary>
    private static PicturePlane Scaled(double scale) => new([.. PicturePlane.Screen.Select(p => (0.5 + (p.X - 0.5) * scale, 0.5 + (p.Y - 0.5) * scale))]);

    // The 3-D kinds are worked out in a frame where the pictures travel towards −along: "along" runs
    // the way they move, "across" at right angles, and z away from the camera, with the screen at
    // z = 0. Distances are in screen heights from the middle of the screen. For Left, along is the
    // screen's x and across its y; the other directions turn or mirror that (ToScreen), so one set
    // of sums does all four.

    private static (double X, double Y) ToScreen(TransitionDirection direction, double along, double across) => direction switch
    {
        TransitionDirection.Right => (-along, across),
        TransitionDirection.Up => (across, along),
        TransitionDirection.Down => (across, -along),
        _ => (along, across),
    };

    /// <summary>Where a picture's corner (u, v) is in the frame when the picture lies flat on the screen (the reverse of <see cref="ToScreen"/>).</summary>
    private static (double Along, double Across) FromScreen(TransitionDirection direction, (double U, double V) corner, double aspect)
    {
        var (x, y) = ((corner.U - 0.5) * aspect, corner.V - 0.5);
        return direction switch
        {
            TransitionDirection.Right => (-x, y),
            TransitionDirection.Up => (y, x),
            TransitionDirection.Down => (-y, x),
            _ => (x, y),
        };
    }

    /// <summary>How wide the picture is along the move, in screen heights.</summary>
    private static double Along(TransitionDirection direction, double aspect) =>
        direction is TransitionDirection.Up or TransitionDirection.Down ? 1 : aspect;

    /// <summary>A point of the frame seen by the camera, in screen units (0–1): further away is smaller, towards the middle.</summary>
    private static (double X, double Y) Project(TransitionDirection direction, double along, double across, double z, double aspect)
    {
        var scale = CameraDistance / Math.Max(CameraDistance + z, 1e-3);
        var (x, y) = ToScreen(direction, along * scale, across * scale);
        return (x / aspect + 0.5, y + 0.5);
    }

    /// <summary>
    /// One side of a cube turning a quarter-turn. The old picture is its front; the new one the
    /// side the pictures travel from, which comes round to the front. The cube turns about its own
    /// middle (half its width behind the screen) and backs away a little at the half-way point, so
    /// its nearest edge never grows past the screen.
    /// </summary>
    private static PicturePlane CubeFace(TransitionDirection direction, double e, double aspect, bool newFace)
    {
        var w = Along(direction, aspect);
        var angle = -e * Math.PI / 2;
        var back = 0.25 * w * TransitionCurves.Bell(e);
        (double X, double Y) Corner(double along, double across, double z)
        {
            // Turn about the cube's middle (along = 0, z = w/2), then push the whole cube back.
            var rz = z - w / 2;
            var x = along * Math.Cos(angle) - rz * Math.Sin(angle);
            var depth = along * Math.Sin(angle) + rz * Math.Cos(angle) + w / 2 + back;
            return Project(direction, x, across, depth, aspect);
        }
        // Each corner of the picture, in its own order (top-left, top-right, bottom-left, bottom-right).
        // The old picture is the front (z = 0). The new one is on the side at along = w/2, which, once
        // it has turned to the front, shows what's at "along" there: the near edge (z = 0) is its
        // leading edge, the one that meets the old picture.
        var corners = PicturePlane.Screen.Select(c =>
        {
            var (along, across) = FromScreen(direction, c, aspect);
            return newFace ? Corner(w / 2, across, along + w / 2) : Corner(along, across, 0);
        }).ToArray();
        var turned = newFace ? Math.PI / 2 - e * Math.PI / 2 : e * Math.PI / 2;
        return Face(corners, 0.55 + 0.45 * Math.Cos(turned));
    }

    /// <summary>
    /// A card turning over about its middle: the old picture on its front for the first half, the
    /// new one on its back for the second. It lifts back from the screen as it turns edge-on, so the
    /// near edge doesn't loom.
    /// </summary>
    private static PicturePlane FlipFace(TransitionDirection direction, double e, double aspect, bool newFace)
    {
        if (newFace ? e < 0.5 : e > 0.5) return PicturePlane.Hidden;
        var w = Along(direction, aspect);
        // The back's picture turns the last half-turn into place (from edge-on to flat at the end).
        var angle = e * Math.PI - (newFace ? Math.PI : 0);
        var back = 0.55 * w * TransitionCurves.Bell(e);
        var corners = PicturePlane.Screen.Select(c =>
        {
            var (along, across) = FromScreen(direction, c, aspect);
            return Project(direction, along * Math.Cos(angle), across, along * Math.Sin(angle) + back, aspect);
        }).ToArray();
        return Face(corners, 0.6 + 0.4 * Math.Abs(Math.Cos(angle)));
    }

    /// <summary>
    /// A face, unless it's turned away from the camera: then its corners run the other way round
    /// from the screen's (the picture would be seen from behind, mirrored).
    /// </summary>
    private static PicturePlane Face((double X, double Y)[] corners, double brightness) =>
        SignedArea(corners) * SignedArea(PicturePlane.Screen) > 1e-9 ? new PicturePlane(corners, 1, brightness) : PicturePlane.Hidden;

    /// <summary>Twice the signed area of a quad given as top-left, top-right, bottom-left, bottom-right.</summary>
    private static double SignedArea(IReadOnlyList<(double X, double Y)> c)
    {
        (double X, double Y)[] ring = [c[0], c[1], c[3], c[2]];
        var area = 0.0;
        for (var i = 0; i < 4; i++)
        {
            var (x0, y0) = ring[i];
            var (x1, y1) = ring[(i + 1) % 4];
            area += x0 * y1 - x1 * y0;
        }
        return area;
    }

    /// <summary>
    /// The homography that takes a point of the screen (0–1) to the point of the picture drawn
    /// there, as three rows; null when the plane is hidden or edge-on.
    /// </summary>
    public static double[]? ScreenToPicture(PicturePlane plane) =>
        plane.Opacity <= 0 ? null : OutputWarp.CenteredHomography(plane.Corners, PicturePlane.Screen);

    // c0 = (old brightness, new brightness, pixels across, pixels down); c1–c3 the old picture's
    // homography rows; c4–c6 the new one's; c7 = (old opacity, new opacity, old on top, new on top).
    private static void PlanesConstants(Vec4[] c, (PicturePlane Old, PicturePlane New, bool NewOnTop, bool OldOnTop) planes, (double Width, double Height) pixels)
    {
        var (old, @new, newOnTop, oldOnTop) = planes;
        var oldRows = ScreenToPicture(old);
        var newRows = ScreenToPicture(@new);
        c[0] = new Vec4(old.Brightness, @new.Brightness, Math.Max(pixels.Width, 1), Math.Max(pixels.Height, 1));
        Rows(c, 1, oldRows);
        Rows(c, 4, newRows);
        c[7] = new Vec4(oldRows is null ? 0 : old.Opacity, newRows is null ? 0 : @new.Opacity, oldOnTop ? 1 : 0, newOnTop ? 1 : 0);
    }

    private static void Rows(Vec4[] c, int at, double[]? h)
    {
        h ??= [1, 0, 0, 0, 1, 0, 0, 0, 1];
        c[at] = new Vec4(h[0], h[1], h[2], 0);
        c[at + 1] = new Vec4(h[3], h[4], h[5], 0);
        c[at + 2] = new Vec4(h[6], h[7], h[8], 0);
    }

    // MARK: Wipes

    /// <summary>
    /// How soft a wipe's edge is, as a share of the wipe: a little even at 0 so it never looks
    /// jagged; a luma wipe is softer, or it would look like a cut-out.
    /// </summary>
    public static double WipeEdge(WindowsTransition transition) =>
        transition.Kind == WindowsTransitionKind.LumaWipe
            ? 0.08 + 0.6 * Math.Clamp(transition.Softness, 0, 1)
            : 0.004 + 0.4 * Math.Clamp(transition.Softness, 0, 1);

    /// <summary>
    /// Where the wipe's edge is: each pixel has a "reveal time" (0 = first, 1 = last) and shows the
    /// new picture once the edge passes it. The edge starts a little before 0 and ends a little
    /// after 1, so the soft edge has fully cleared at both ends.
    /// </summary>
    public static double WipeThreshold(WindowsTransition transition, double t)
    {
        var edge = WipeEdge(transition);
        return TransitionCurves.Eased(transition.Kind, t) * (1 + edge) - edge;
    }

    // c0 = (threshold, edge width, aspect, 0); c1 = how much each reveal pattern counts
    // (circle, diamond, straight, luma); c2 = (direction x, y, 1 / farthest circle, 1 / farthest diamond);
    // c3 = (clock hand, its turn: 1 clockwise, −1 the other way).
    private static void WipeConstants(Vec4[] c, WindowsTransition transition, double t, double aspect)
    {
        var (dx, dy) = Vector(transition.Direction);
        var circle = Math.Sqrt(aspect * aspect / 4 + 0.25);
        var diamond = aspect / 2 + 0.5;
        c[0] = new Vec4(WipeThreshold(transition, t), WipeEdge(transition), aspect, 0);
        c[1] = transition.Kind switch
        {
            WindowsTransitionKind.Iris => new Vec4(1, 0, 0, 0),
            WindowsTransitionKind.Diamond => new Vec4(0, 1, 0, 0),
            WindowsTransitionKind.SoftWipe => new Vec4(0, 0, 1, 0),
            WindowsTransitionKind.LumaWipe => new Vec4(0, 0, 0, 1),
            _ => Vec4.Zero,
        };
        c[2] = new Vec4(dx, dy, 1 / circle, 1 / diamond);
        c[3] = new Vec4(transition.Kind == WindowsTransitionKind.Clock ? 1 : 0, 1, 0, 0);
    }

    // MARK: Radial blur (zoom through, cross zoom)

    /// <summary>The eight sample points along a streak, as shares of the way to the centre (1 = the pixel itself).</summary>
    public static double[] Streak(double blur)
    {
        blur = Math.Clamp(blur, 0, 0.9);
        return [.. Enumerable.Range(0, 8).Select(i => 1 - blur * i / 7.0)];
    }

    /// <summary>
    /// The zoom kinds: (centre x, y; old scale, new scale; old opacity, new opacity; blur; flash).
    /// Zoom Through rushes into the old picture (it grows three times over), swaps where it's
    /// fastest, and pulls back out of the new one, both streaked by their speed: neither is ever
    /// smaller than the screen. Cross Zoom keeps both at their size and streaks them towards a point
    /// that drifts across the screen.
    /// </summary>
    public static (double X, double Y, double OldScale, double NewScale, double OldOpacity, double NewOpacity, double Blur, double Flash) Zoom(WindowsTransitionKind kind, double t)
    {
        if (kind == WindowsTransitionKind.CrossZoom)
        {
            var cross = TransitionCurves.Smoothstep(0.35, 0.65, t);
            return (TransitionCurves.Lerp(0.3, 0.7, t), 0.5, 1, 1, 1 - cross, cross, 0.3 * TransitionCurves.Bell(t), 0.12 * TransitionCurves.Bell(t));
        }
        var e = TransitionCurves.Eased(kind, t);
        var swap = TransitionCurves.Smoothstep(0.4, 0.6, e);
        return (0.5, 0.5, TransitionCurves.Lerp(1, 3, e), TransitionCurves.Lerp(3, 1, e), 1 - swap, swap,
            0.25 * TransitionCurves.Speed(kind, t) / TransitionCurves.Speed(kind, 0.5), 0);
    }

    /// <summary>
    /// The step of the shaders' noise pattern for a pixel of the screen (interleaved gradient noise:
    /// a fixed, even scatter of values with no visible pattern, cheap enough for ps_2_0). The shader
    /// takes the screen position (0–1), so the pixel size is folded in here.
    /// </summary>
    private static (double X, double Y) NoiseStep((double Width, double Height) pixels) =>
        (Math.Max(pixels.Width, 1) * 0.06711056, Math.Max(pixels.Height, 1) * 0.00583715);

    // c0 = (centre x, y, old scale, new scale); c1 = (old opacity, new opacity, flash, 0);
    // c2, c3 = the old picture's eight streak samples; c4, c5 = the new one's; c6 = (the noise
    // step, the gap between samples). Each pixel shifts its samples by a different share of that gap
    // (dither), so eight samples read as a smooth streak rather than eight copies.
    private static void RadialConstants(Vec4[] c, WindowsTransitionKind kind, double t, (double Width, double Height) pixels)
    {
        var z = Zoom(kind, t);
        var noise = NoiseStep(pixels);
        c[0] = new Vec4(z.X, z.Y, z.OldScale, z.NewScale);
        c[1] = new Vec4(z.OldOpacity, z.NewOpacity, z.Flash, 0);
        c[6] = new Vec4(noise.X, noise.Y, Math.Clamp(z.Blur, 0, 0.9) / 7, 0);
        var steps = Streak(z.Blur);
        c[2] = new Vec4(steps[0], steps[1], steps[2], steps[3]);
        c[3] = new Vec4(steps[4], steps[5], steps[6], steps[7]);
        c[4] = c[2];
        c[5] = c[3];
    }

    // MARK: Whip pan

    /// <summary>How long a whip pan's streak is (a share of the screen), from how fast it's moving: a quarter of the screen at full speed.</summary>
    public static double WhipBlur(double t) =>
        0.25 * TransitionCurves.Speed(WindowsTransitionKind.WhipPan, t) / TransitionCurves.Speed(WindowsTransitionKind.WhipPan, 0.5);

    // c0 = (the move's axis x, y, the noise step); c1, c2 = where the old picture's eight samples
    // are read, as distances along the axis from the pixel; c3, c4 the new picture's; c5 = (the gap
    // between samples), for the dither (see RadialConstants). A push, streaked along its direction by its speed.
    private static void WhipConstants(Vec4[] c, WindowsTransition transition, double t, (double Width, double Height) pixels)
    {
        var e = TransitionCurves.Eased(WindowsTransitionKind.WhipPan, t);
        var (dx, dy) = Vector(transition.Direction);
        // Sample along the axis the move runs on; the sign of the direction says which way the pictures sit.
        var (ax, ay) = (Math.Abs(dx), Math.Abs(dy));
        var sign = dx + dy;
        var blur = WhipBlur(t);
        double[] Samples(double shift) => [.. Enumerable.Range(0, 8).Select(i => -shift + blur * (i / 7.0 - 0.5))];
        // The old picture is pushed by e along the direction, the new one by e − 1 (as in Push).
        var old = Samples(sign * e);
        var @new = Samples(sign * (e - 1));
        var noise = NoiseStep(pixels);
        c[0] = new Vec4(ax, ay, noise.X, noise.Y);
        c[5] = new Vec4(blur / 7, 0, 0, 0);
        c[1] = new Vec4(old[0], old[1], old[2], old[3]);
        c[2] = new Vec4(old[4], old[5], old[6], old[7]);
        c[3] = new Vec4(@new[0], @new[1], @new[2], @new[3]);
        c[4] = new Vec4(@new[4], @new[5], @new[6], @new[7]);
    }

    // MARK: Light leak

    /// <summary>(cross-fade, glow strength, where the band of light is along its diagonal, the overall bloom.)</summary>
    public static (double Cross, double Glow, double Band, double Bloom) Light(double t, double aspect)
    {
        var bell = TransitionCurves.Bell(t);
        // The band crosses the whole diagonal (0.8 x + 0.6 y in screen heights) and a little more either side.
        var far = 0.8 * aspect + 0.6;
        return (TransitionCurves.Smoothstep(0.3, 0.7, t), 0.75 * Math.Pow(bell, 0.8), TransitionCurves.Lerp(-0.5, far + 0.5, t), 0.1 * bell * bell);
    }

    // c0 = (cross-fade, glow, aspect, band position); c1 = (the light's colour, 1 when a layer beneath
    // lights the screen and this one lights only its own pixels); c2 = (band axis x, y, 1 / band
    // width², bloom); c3 = a softer glow (centre x, y in screen heights, 1 / radius², strength).
    private static void LightConstants(Vec4[] c, WindowsTransition transition, double t, double aspect, bool litBelow)
    {
        var light = Light(t, aspect);
        var color = ColorHex.Color(WindowsTransitions.Color(transition)) ?? new SceneColor(1, 0.7, 0.35);
        c[0] = new Vec4(light.Cross, light.Glow, aspect, light.Band);
        c[1] = new Vec4(color.Red, color.Green, color.Blue, litBelow ? 1 : 0);
        c[2] = new Vec4(0.8, 0.6, 3, light.Bloom);
        c[3] = new Vec4(TransitionCurves.Lerp(0.15, 0.95, t) * aspect, TransitionCurves.Lerp(0.25, 0.75, t), 2.5, 0.7);
    }

    // MARK: Glitch

    /// <summary>How hard it glitches: strongest in the middle, but already rough at the start.</summary>
    public static double GlitchStrength(double t) => Math.Pow(TransitionCurves.Bell(t), 0.6);

    // c0 = (blocks switched so far, strength, 0, jitter seed); c1 = (rows, columns, colour split, row shift).
    // Blocks switch to the new picture in a scattered order, finishing a little before the end so the
    // last frames are the new picture settling down. The seed steps 24 times a second.
    private static void GlitchConstants(Vec4[] c, double t, double elapsed)
    {
        var strength = GlitchStrength(t);
        var seed = Math.Floor(Math.Max(elapsed, 0) * 24) * 1.37 + 3.1;
        c[0] = new Vec4(t * 1.2 - 0.05, strength, 0, seed);
        c[1] = new Vec4(22, 9, 0.012 * strength, 0.14 * strength);
    }

    // MARK: Ripple

    /// <summary>(how far the ring has spread, in screen heights; its soft width; the waves' height.)</summary>
    public static (double Ring, double Width, double Amplitude) Ripple(double t, double aspect)
    {
        const double width = 0.3;
        var farthest = Math.Sqrt(aspect * aspect / 4 + 0.25);
        return (TransitionCurves.Eased(WindowsTransitionKind.Ripple, t) * (farthest + width), width, 0.014 * TransitionCurves.Bell(t));
    }

    // c0 = (ring, ring width, aspect, wave phase); c1 = (wave height, waves per screen height).
    private static void RippleConstants(Vec4[] c, double t, double aspect, double elapsed)
    {
        var ripple = Ripple(t, aspect);
        c[0] = new Vec4(ripple.Ring, ripple.Width, aspect, Math.Max(elapsed, 0) * 14);
        c[1] = new Vec4(ripple.Amplitude, 46, 0, 0);
    }
}
