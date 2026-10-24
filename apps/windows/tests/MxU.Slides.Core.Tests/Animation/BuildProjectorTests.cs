using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Animation;

// Ported from RenderEngine/Tests/AnimationEvaluatorTests.swift (testQuadCornersSwingAndPivot,
// testStretchKeystoneStripMesh), plus the keystone warp the Windows renderer draws with.
public class BuildProjectorTests
{
    private static readonly SceneRect Rect = new(100, 100, 200, 100);
    private static readonly SceneSize Target = new(1920, 1080);

    private static EditorGeometry.Point[] Corners(AnimationMotion motion) => BuildProjector.QuadCorners(Rect, motion, 1, Target);

    [Fact]
    public void QuadCornersSwingAndPivot()
    {
        var flat = Corners(AnimationMotion.Identity);
        Assert.Equal(new EditorGeometry.Point(100, 100), flat[0]);
        Assert.Equal(new EditorGeometry.Point(300, 200), flat[3]);

        var swung = Corners(new AnimationMotion { Swing = 30 });
        Assert.True(swung[3].Y - swung[1].Y < swung[2].Y - swung[0].Y); // the far side shrinks
        Assert.True(swung[1].X < 300);

        var bottomPivot = Corners(new AnimationMotion { Tilt = 40, TiltPivot = TiltPivot.Bottom });
        Assert.Equal(100, bottomPivot[2].X, 0.001);
        Assert.Equal(200, bottomPivot[2].Y, 0.001);
        Assert.Equal(300, bottomPivot[3].X, 0.001);
        Assert.True(bottomPivot[0].X > 100 && bottomPivot[0].Y > 100);

        var keyed = Corners(new AnimationMotion { KeystoneTop = 0.5 });
        Assert.Equal(new EditorGeometry.Point(150, 100), keyed[0]);
        Assert.Equal(new EditorGeometry.Point(250, 100), keyed[1]);
        Assert.Equal(new EditorGeometry.Point(100, 200), keyed[2]);
        Assert.Equal(new EditorGeometry.Point(300, 200), keyed[3]);
        Assert.False(new AnimationMotion { KeystoneBottom = 1.2 }.IsIdentity);

        var sheared = Corners(new AnimationMotion { SkewX = 45 });
        Assert.Equal(150, sheared[0].X, 0.001);
        Assert.Equal(350, sheared[1].X, 0.001);
        Assert.Equal(50, sheared[2].X, 0.001);
        Assert.Equal(100, sheared[0].Y, 0.001);
        Assert.False(new AnimationMotion { SkewY = 5 }.IsIdentity);
    }

    [Fact]
    public void StretchKeystoneStripMesh()
    {
        var motion = new AnimationMotion { KeystoneTop = 0.5, KeystoneBottom = 1, KeystoneStretch = true };
        Assert.True(motion.UsesStretchKeystone);
        Assert.False(new AnimationMotion { KeystoneTop = 0.5, KeystoneStretch = false }.UsesStretchKeystone);
        Assert.False(new AnimationMotion { KeystoneStretch = true }.UsesStretchKeystone); // a flat trapezoid is a plain quad
        var vertices = BuildProjector.StretchKeystoneVertices(Rect, motion, 1, Target);
        Assert.Equal(BuildProjector.StretchKeystoneStrips * 6, vertices.Count);
        Assert.Equal(150, vertices[0].Position.X, 0.01);
        Assert.Equal(250, vertices[5].Position.X, 0.01);
        Assert.Equal(100, vertices[0].Position.Y, 0.01);

        var mid = BuildProjector.StretchKeystoneStrips / 2;
        var midTopLeft = vertices[mid * 6];
        Assert.Equal(150, midTopLeft.Position.Y, 0.01);
        Assert.Equal(125, midTopLeft.Position.X, 0.01);
        Assert.Equal(0.5, midTopLeft.V, 0.001); // v rides linearly
    }

    [Fact]
    public void KeystoneCornersAreTheTrapezoidInTheItemsOwnFrame()
    {
        var corners = BuildProjector.KeystoneCorners(Rect, 0.5, 1.5);
        Assert.Equal(new EditorGeometry.Point(150, 100), corners[0]);
        Assert.Equal(new EditorGeometry.Point(250, 100), corners[1]);
        Assert.Equal(new EditorGeometry.Point(50, 200), corners[2]);
        Assert.Equal(new EditorGeometry.Point(350, 200), corners[3]);
        Assert.True(BuildProjector.HasKeystone(new AnimationMotion { KeystoneBottom = 1.5 }));
        Assert.False(BuildProjector.HasKeystone(AnimationMotion.Identity));
    }

    [Fact]
    public void APerspectiveKeystoneMapsTheCornersAndForeshortensTheNarrowEnd()
    {
        // The warp the renderer draws: a homography from the trapezoid back to the frame.
        var corners = BuildProjector.KeystoneCorners(Rect, 0.5, 1).Select(p => (p.X, p.Y)).ToArray();
        var frame = new[] { (100.0, 100.0), (300.0, 100.0), (100.0, 200.0), (300.0, 200.0) };
        var h = OutputWarp.CenteredHomography(corners, frame)!;
        for (var i = 0; i < 4; i++)
        {
            var (u, v) = OutputWarp.Map(h, corners[i].X, corners[i].Y)!.Value;
            Assert.Equal(frame[i].Item1, u, 1e-6);
            Assert.Equal(frame[i].Item2, v, 1e-6);
        }
        // Halfway down the trapezoid shows more than half of the picture's height: the narrow
        // (far) end is foreshortened, which is what makes it look tipped back rather than squeezed.
        var (_, midV) = OutputWarp.Map(h, 200, 150)!.Value;
        Assert.True(midV > 150);
    }
}
