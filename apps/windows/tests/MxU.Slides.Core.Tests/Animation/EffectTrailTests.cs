using MxU.Slides.Core.Animation;

namespace MxU.Slides.Core.Tests.Animation;

// The memory behind Echo and Ghost Trails (the Mac's Compositor effect history and its
// compositor_echo / compositor_ghost_trails shaders).
public class EffectTrailTests
{
    private const uint White = 0xFFFFFFFF;
    private const uint Clear = 0;

    private static uint[] Picture(int width, int height, Func<int, int, uint> pixel)
    {
        var pixels = new uint[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                pixels[y * width + x] = pixel(x, y);
        return pixels;
    }

    private static byte Alpha(uint pixel) => (byte)(pixel >> 24);

    [Fact]
    public void KeepFallsToATenthAfterTheFadeTime()
    {
        Assert.Equal(0.1, EffectTrail.Keep(1.5, 1.5), 6);
        Assert.Equal(Math.Sqrt(0.1), EffectTrail.Keep(0.75, 1.5), 6);
        Assert.Equal(0.999, EffectTrail.Keep(0, 1)); // never quite all of it, so trails always fade
        // The same fade however fast the frames come: two half steps keep what one whole step does.
        Assert.Equal(EffectTrail.Keep(1.0 / 30, 2), EffectTrail.Keep(1.0 / 60, 2) * EffectTrail.Keep(1.0 / 60, 2), 6);
    }

    [Fact]
    public void TheFirstFrameShowsTheItemAsItIs()
    {
        var trail = new EffectTrail();
        var output = new uint[16];
        Assert.Null(trail.Advance(Picture(4, 4, (_, _) => White), 4, 4, 10, new AnimationEffect.Echo(1), 1, output));
        Assert.All(output, p => Assert.Equal(Clear, p)); // nothing behind it yet
    }

    [Fact]
    public void AnEchoLeavesAFadingCopyOfWhatMoved()
    {
        // A white dot that jumps from the left column to the right column.
        var trail = new EffectTrail();
        var output = new uint[16];
        var echo = new AnimationEffect.Echo(1);
        trail.Advance(Picture(4, 4, (x, _) => x == 0 ? White : Clear), 4, 4, 10, echo, 1, output);
        var keep = trail.Advance(Picture(4, 4, (x, _) => x == 3 ? White : Clear), 4, 4, 10.1, echo, 1, output);
        Assert.NotNull(keep);
        Assert.Equal(EffectTrail.Keep(0.1, 1), keep!.Value, 6);
        Assert.InRange(Alpha(output[0]), 190, 210); // the old dot, at about 0.1^0.1 ≈ 79%
        Assert.Equal(0, Alpha(output[3])); // the new dot isn't in the trail: the renderer draws it fresh

        // A second later the copy has faded to about a tenth of that.
        trail.Advance(Picture(4, 4, (x, _) => x == 3 ? White : Clear), 4, 4, 10.4, echo, 1, output);
        trail.Advance(Picture(4, 4, (x, _) => x == 3 ? White : Clear), 4, 4, 10.7, echo, 1, output);
        trail.Advance(Picture(4, 4, (x, _) => x == 3 ? White : Clear), 4, 4, 11.0, echo, 1, output);
        Assert.InRange(Alpha(output[0]), 15, 30);
        Assert.InRange(Alpha(output[3]), 120, 255); // the dot that stayed is in the trail too (max(fresh, trail) shows it as it is)
    }

    [Fact]
    public void AStaleOrResizedHistoryStartsAgain()
    {
        var trail = new EffectTrail();
        var output = new uint[16];
        var echo = new AnimationEffect.Echo(1);
        trail.Advance(Picture(4, 4, (_, _) => White), 4, 4, 1, echo, 1, output);
        Assert.Null(trail.Advance(Picture(4, 4, (_, _) => White), 4, 4, 1 + EffectTrail.HistoryGap + 0.1, echo, 1, output)); // off screen too long
        Assert.NotNull(trail.Advance(Picture(4, 4, (_, _) => White), 4, 4, 2, echo, 1, output));
        Assert.Null(trail.Advance(Picture(2, 8, (_, _) => White), 2, 8, 2.05, echo, 1, output)); // the item changed size
        Assert.Null(trail.Advance(Picture(2, 8, (_, _) => White), 2, 8, 1.9, echo, 1, output)); // the clock went backwards
    }

    [Fact]
    public void GhostTrailsMixInADriftedCopy()
    {
        var trail = new EffectTrail();
        const int size = 32;
        var output = new uint[size * size];
        var ghost = new AnimationEffect.GhostTrails(1.5, 3, 16, 0.5);
        var square = Picture(size, size, (x, y) => x is >= 8 and < 24 && y is >= 8 and < 24 ? White : Clear);
        trail.Advance(square, size, size, 5, ghost, 1, output);
        var keep = trail.Advance(Picture(size, size, (_, _) => Clear), size, size, 5 + 1.0 / 60, ghost, 1, output);
        Assert.NotNull(keep);
        // The trail is the old square pushed about: still mostly there, its edges moved.
        var covered = output.Count(p => Alpha(p) > 128);
        Assert.InRange(covered, 16 * 16 / 2, 16 * 16 * 2);
        Assert.Contains(output, p => Alpha(p) is > 0 and < 255); // soft, drifted edges
        // Deterministic: the same frames give the same trail.
        var again = new EffectTrail();
        var second = new uint[size * size];
        again.Advance(square, size, size, 5, ghost, 1, second);
        again.Advance(Picture(size, size, (_, _) => Clear), size, size, 5 + 1.0 / 60, ghost, 1, second);
        Assert.Equal(output, second);
    }

    [Fact]
    public void TheNoisePushStaysInRange()
    {
        for (var i = 0; i < 200; i++)
        {
            var (x, y) = EffectTrail.NoiseOffset(i * 0.37, i * 0.91, i * 0.05);
            Assert.InRange(x, -0.5, 0.5);
            Assert.InRange(y, -0.5, 0.5);
        }
    }

    [Fact]
    public void OnlyEchoAndGhostTrailsKeepATrail() =>
        Assert.Throws<ArgumentException>(() => new EffectTrail().Advance(new uint[1], 1, 1, 0, new AnimationEffect.Invert(), 1, new uint[1]));

    [Fact]
    public void TrailEffectsKeepTheViewDrawing()
    {
        Assert.True(new AnimationEffect.Echo(1).VariesWithTime);
        Assert.True(new AnimationEffect.GhostTrails(1, 1, 1, 0).VariesWithTime);
    }
}
