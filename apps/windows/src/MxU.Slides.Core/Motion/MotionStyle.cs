using System.Numerics;

namespace MxU.Slides.Core.Motion;

/// <summary>
/// One kind of motion background (aurora, bokeh, smoke…). A style is pure maths: given the
/// recipe and a moment in the loop it says what colour every point of the picture is, and
/// which glowing dots (sprites) float over it. <see cref="MotionRenderer"/> does the rest:
/// sharing the work across threads, scaling, film grain and turning it into video pixels.
/// <para>
/// Every style draws in <b>picture units</b>: y runs from 0 at the top to 1 at the bottom and
/// x from 0 to the aspect ratio (1.78 for 16:9). So a recipe looks the same at 1080p and 4K,
/// just sharper, and the tiny previews in the gallery look like the real thing.
/// </para>
/// </summary>
public abstract class MotionStyle
{
    /// <summary>A short stable id, saved in recipes ("aurora"). Never rename one.</summary>
    public abstract string Id { get; }

    public abstract string Name { get; }

    /// <summary>One line for the gallery's tooltip.</summary>
    public abstract string Description { get; }

    /// <summary>The <see cref="MotionPalette"/> it looks best in, picked when you choose the style.</summary>
    public abstract string DefaultPalette { get; }

    /// <summary>
    /// How many picture pixels each shaded point covers, across and down. Smooth styles (smoke,
    /// gradients) are worked out on a grid 4× coarser than the picture and smoothly enlarged:
    /// 16 times less work, and no visible difference because nothing in them is sharp. Styles
    /// with fine lines say 1 and are worked out at every pixel.
    /// </summary>
    internal virtual int FieldDivisor => 4;

    /// <summary>How strong the film grain is (0–1). A little on everything stops smooth gradients from banding.</summary>
    internal virtual float Grain => 0.25f;

    /// <summary>How much the corners are darkened (0–1), which keeps the eye in the middle and the text readable.</summary>
    internal virtual float Vignette => 0.35f;

    /// <summary>Works out one moment of the loop: anything that's the same for every pixel (where the sprites are, per-column curves).</summary>
    internal abstract MotionFrame Begin(MotionScene scene, LoopClock clock);
}

/// <summary>One moment of a style, ready to be asked about every pixel (from many threads at once, so it must not change once made).</summary>
internal abstract class MotionFrame
{
    /// <summary>Glowing dots drawn over the smooth part, at full sharpness.</summary>
    public List<Sprite> Sprites { get; } = [];

    /// <summary>
    /// The light at picture point (x, y), in linear light. <paramref name="column"/> is the
    /// point's index in <see cref="MotionScene.ColumnX"/>, for styles that worked something out per column in <see cref="MotionStyle.Begin"/>.
    /// </summary>
    public abstract Vector3 Shade(int column, float x, float y);

    /// <summary>A whole row at once (y fixed, x from <see cref="MotionScene.ColumnX"/>).</summary>
    public virtual void ShadeRow(float y, ReadOnlySpan<float> xs, Span<Vector3> row)
    {
        for (var i = 0; i < xs.Length; i++) row[i] = Shade(i, xs[i], y);
    }
}

internal enum SpriteShape
{
    /// <summary>A soft round glow that fades to nothing at its radius (dust, distant lights).</summary>
    Glow,

    /// <summary>An out-of-focus light: an even disc with a soft edge and a slightly brighter rim, as a real lens draws it.</summary>
    Disc,

    /// <summary>A star: a tight bright core in a faint halo.</summary>
    Star,
}

/// <summary>
/// A dot of light: where (picture units), how big, how bright and what colour (linear light,
/// added to what's under it, the way light adds up), and its shape.
/// </summary>
internal readonly record struct Sprite(Vector2 Center, float Radius, Vector3 Light, SpriteShape Shape, float Softness = 0.25f, float Rim = 0);

/// <summary>
/// What every frame of one recipe at one picture size shares: the recipe's numbers in the form
/// the styles want, its palette as linear colours and a ramp, and where the shaded columns are.
/// </summary>
public sealed class MotionScene
{
    internal MotionScene(MotionRecipe recipe, MotionStyle style, int width, int height, float[] columnX)
    {
        Recipe = recipe;
        Style = style;
        Width = width;
        Height = height;
        Aspect = (float)width / height;
        Speed = (float)recipe.Speed;
        Intensity = (float)recipe.Intensity;
        Seconds = recipe.Seconds;
        Seed = LoopNoise.Hash((uint)recipe.Variation, StableHash(style.Id));
        // A preset's colours keep their designed order; hand-picked ones are sorted dark to light.
        var preset = MotionPalette.Find(recipe.Palette);
        Stops = MotionPalette.Stops(recipe.Colors, inDesignedOrder: preset is not null && preset.Colors.SequenceEqual(recipe.Colors, StringComparer.OrdinalIgnoreCase));
        Ramp = new PaletteRamp(Stops);
        ColumnX = columnX;
        // Where this variation sits in the noise: far apart for different variations.
        Offset = new Vector2(LoopNoise.Unit(Seed, 0, 0) * 400 - 200, LoopNoise.Unit(Seed, 0, 1) * 400 - 200);
    }

    public MotionRecipe Recipe { get; }

    public MotionStyle Style { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Width ÷ height: the right edge's x in picture units.</summary>
    public float Aspect { get; }

    public float Speed { get; }

    public float Intensity { get; }

    public int Seconds { get; }

    /// <summary>The recipe's variation, mixed with the style, as the seed for every random choice.</summary>
    public uint Seed { get; }

    /// <summary>The palette's four colours, darkest first, in linear light.</summary>
    public Vector3[] Stops { get; }

    /// <summary>The palette as a gradient from dark (0) to light (1).</summary>
    public PaletteRamp Ramp { get; }

    /// <summary>The x (picture units) of each column the style is asked to shade.</summary>
    public float[] ColumnX { get; }

    /// <summary>Added to noise coordinates so each variation gets its own part of the noise.</summary>
    public Vector2 Offset { get; }

    /// <summary>One picture pixel, in picture units.</summary>
    public float Pixel => 1f / Height;

    /// <summary>
    /// The radius of the circle of time (<see cref="LoopNoise.Loop"/>) that changes noise by about
    /// <paramref name="perSecond"/> noise units a second at this recipe's speed. The circle's
    /// length is the distance travelled in one loop, so a longer loop gets a bigger circle and
    /// the same speed looks the same whatever the length.
    /// </summary>
    public float Turn(float perSecond) => Speed * Seconds * perSecond / (2 * MathF.PI);

    /// <summary>How many times something that should take about <paramref name="seconds"/> repeats in the loop (at least once).</summary>
    public int RoundsOf(float seconds) => Math.Max(1, (int)MathF.Round(Seconds * Speed / seconds));

    /// <summary>A repeatable random number 0–1: property <paramref name="channel"/> of thing <paramref name="index"/>.</summary>
    public float Random(int index, int channel) => LoopNoise.Unit(Seed, index + 1, channel);

    /// <summary>A string hash that's the same in every run (.NET's own string hash changes each run).</summary>
    private static uint StableHash(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text) hash = (hash ^ c) * 16777619u;
        return hash;
    }
}
