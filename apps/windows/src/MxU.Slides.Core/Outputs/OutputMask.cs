using System.Globalization;
using System.Text.Json.Serialization;

namespace MxU.Slides.Core.Outputs;

// Masks black out parts of an output: the pillar in front of the screen, the
// wings of a stage set, a window cut into an LED wall. Each screen keeps a
// library of masks; "always on" ones describe the room and apply all the time,
// the others are switched on by output presets. Port of
// RenderEngine/OutputMask.swift. Turning the SVG path into pixels stays in the
// app (WPF geometry); this file does the maths on the resulting coverage.

public enum OutputMaskMode
{
    /// <summary>Black out what is inside the path.</summary>
    Out,

    /// <summary>Show only what is inside the path.</summary>
    In,
}

/// <summary>One mask in a screen's library.</summary>
public sealed record OutputMask
{
    public OutputMask() { }

    public OutputMask(
        string name, string pathData, OutputMaskMode mode = OutputMaskMode.Out, double feather = 0,
        bool alwaysOn = true, IReadOnlyList<Guid>? sliceIds = null, Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        Name = name;
        PathData = pathData;
        Mode = mode;
        Feather = feather;
        AlwaysOn = alwaysOn;
        SliceIds = sliceIds;
    }

    [JsonPropertyName("id"), JsonConverter(typeof(SwiftUuidJsonConverter))]
    public Guid Id { get; init; } = Guid.NewGuid();

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    /// <summary>An SVG path in the canvas's unit square (0–1 across and down), e.g. "M 0.25 0 L 0.75 0 L 0.75 1 L 0.25 1 Z".</summary>
    [JsonPropertyName("pathData")]
    public string PathData { get; init; } = "";

    [JsonPropertyName("mode"), JsonConverter(typeof(OutputMaskModeJsonConverter))]
    public OutputMaskMode Mode { get; init; }

    /// <summary>Softens the edge by a blur this fraction of the canvas's shorter side wide (0 = hard edge).</summary>
    [JsonPropertyName("feather")]
    public double Feather { get; init; }

    /// <summary>True for room geometry that applies all the time; false for masks output presets switch on.</summary>
    [JsonPropertyName("alwaysOn")]
    public bool AlwaysOn { get; init; } = true;

    /// <summary>The slices (outputs) of the screen this mask covers; null = all of them.</summary>
    [JsonPropertyName("sliceIds"), JsonConverter(typeof(SwiftUuidListJsonConverter)), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<Guid>? SliceIds { get; init; }

    /// <summary>Whether this mask applies to the given slice (the screen's own id is its default slice).</summary>
    public bool Covers(Guid sliceId) => SliceIds is null || SliceIds.Contains(sliceId);

    // Swift compares arrays by value; a record would compare the list by reference.
    public bool Equals(OutputMask? other) =>
        other is not null && Id == other.Id && Name == other.Name && PathData == other.PathData && Mode == other.Mode
        && Feather.Equals(other.Feather) && AlwaysOn == other.AlwaysOn
        && (SliceIds is null ? other.SliceIds is null : other.SliceIds is not null && SliceIds.SequenceEqual(other.SliceIds));

    public override int GetHashCode() => HashCode.Combine(Id, Name, PathData, Mode, Feather, AlwaysOn, SliceIds?.Count);
}

/// <summary>
/// What one output's mask raster is made of: the active masks that cover it,
/// drawn at the screen's canvas size. Two outputs with the same
/// <see cref="Fingerprint"/> can share one raster (a mirror's slices do).
/// </summary>
public sealed record OutputMaskPlan(IReadOnlyList<OutputMask> Masks, int Width, int Height)
{
    /// <summary>The Mac's raster cache key: every mask's id, path, mode and feather, plus the canvas size.</summary>
    public string Fingerprint => string.Join("·", Masks.Select(mask => string.Join("|",
            SwiftUuid.Format(mask.Id), mask.PathData, mask.Mode == OutputMaskMode.In ? "in" : "out",
            mask.Feather.ToString("R", CultureInfo.InvariantCulture))))
        + $"@{Width}x{Height}";

    public bool Equals(OutputMaskPlan? other) => other is not null && Fingerprint == other.Fingerprint;

    public override int GetHashCode() => Fingerprint.GetHashCode(StringComparison.Ordinal);
}

/// <summary>
/// Turns masks into a coverage raster: one byte per canvas pixel, row by row
/// from the top, 255 = show, 0 = black. The renderer multiplies the output's
/// alpha by it. Port of OutputMaskRaster.texture, minus the Core Graphics fill
/// and the Metal texture upload.
/// </summary>
public static class OutputMaskRaster
{
    /// <summary>
    /// Combines <paramref name="masks"/> into one coverage raster. The app
    /// supplies <paramref name="fillPath"/>: fill a mask's unit-space SVG path,
    /// antialiased, into a width×height alpha buffer (0–255, top row first),
    /// or return null when the path can't be read (that mask is skipped).
    /// Feathering and combining happen here. Null when there is nothing to mask.
    /// </summary>
    public static byte[]? Coverage(IReadOnlyList<OutputMask> masks, int width, int height, Func<string, int, int, byte[]?> fillPath)
    {
        if (masks.Count == 0 || width <= 0 || height <= 0) return null;
        var coverage = new byte[width * height];
        Array.Fill(coverage, (byte)255);
        var rasterizedAny = false;
        foreach (var mask in masks)
        {
            if (fillPath(mask.PathData, width, height) is not { } alpha) continue;
            if (alpha.Length != coverage.Length)
                throw new ArgumentException($"The path fill for mask {mask.Name} returned {alpha.Length} bytes, expected {coverage.Length}.");
            if (mask.Feather > 0)
                alpha = TentBlur(alpha, width, height, BlurKernelSize(mask.Feather, width, height));
            Apply(coverage, alpha, mask.Mode);
            rasterizedAny = true;
        }
        return rasterizedAny ? coverage : null;
    }

    /// <summary>
    /// Folds one mask's (feathered) path alpha into the coverage: "out" keeps
    /// what's outside the path, "in" what's inside. Masks multiply, so two
    /// overlapping "out" masks black out both regions. Integer maths, as the Mac's.
    /// </summary>
    public static void Apply(Span<byte> coverage, ReadOnlySpan<byte> pathAlpha, OutputMaskMode mode)
    {
        for (var i = 0; i < coverage.Length; i++)
        {
            int alpha = pathAlpha[i];
            var factor = mode == OutputMaskMode.Out ? 255 - alpha : alpha;
            coverage[i] = (byte)(coverage[i] * factor / 255);
        }
    }

    /// <summary>The feather blur's kernel size in pixels: odd, at least 3, about feather × the shorter side.</summary>
    public static int BlurKernelSize(double feather, int width, int height)
    {
        var radius = feather * Math.Min(width, height);
        return Math.Max(3, (int)Math.Round(radius, MidpointRounding.AwayFromZero) | 1);
    }

    /// <summary>
    /// A tent (triangle) blur with edge pixels extended outwards, the filter
    /// the Mac gets from vImageTentConvolve. Weights fall off linearly from the
    /// centre in each direction; it is done as two running-sum box passes per
    /// axis so wide feathers stay fast.
    /// </summary>
    public static byte[] TentBlur(byte[] source, int width, int height, int kernelSize)
    {
        var half = Math.Max(0, kernelSize / 2);
        if (half == 0) return (byte[])source.Clone();
        var box = half + 1;

        // Horizontal pass into unnormalised sums (each at most 255 · box², fits an int).
        var rows = new int[width * height];
        var padded = new long[width + 2 * half];
        var once = new long[width + half];
        for (var y = 0; y < height; y++)
        {
            for (var i = 0; i < padded.Length; i++) padded[i] = source[y * width + Math.Clamp(i - half, 0, width - 1)];
            TentLine(padded, once, width, box, (x, sum) => rows[y * width + x] = (int)sum);
        }

        // Vertical pass over those sums, then divide by the whole kernel's weight with rounding.
        var result = new byte[width * height];
        var divisor = (long)box * box * box * box;
        padded = new long[height + 2 * half];
        once = new long[height + half];
        for (var x = 0; x < width; x++)
        {
            for (var i = 0; i < padded.Length; i++) padded[i] = rows[Math.Clamp(i - half, 0, height - 1) * width + x];
            TentLine(padded, once, height, box, (y, sum) => result[y * width + x] = (byte)Math.Min(255, (sum + divisor / 2) / divisor));
        }
        return result;
    }

    // A tent of half-width h is a box of h + 1 forwards followed by a box of h + 1 backwards.
    private static void TentLine(long[] padded, long[] once, int length, int box, Action<int, long> write)
    {
        long sum = 0;
        for (var i = 0; i < box; i++) sum += padded[i];
        for (var i = 0; i < once.Length; i++)
        {
            once[i] = sum;
            if (i + box < padded.Length) sum += padded[i + box] - padded[i];
        }
        sum = 0;
        for (var i = 0; i < box; i++) sum += once[i];
        for (var i = 0; i < length; i++)
        {
            write(i, sum);
            if (i + box < once.Length) sum += once[i + box] - once[i];
        }
    }
}

/// <summary>
/// Keeps mask rasters by fingerprint so outputs showing the same masks share
/// one, and a preset switch that leaves an output's masks unchanged reuses its
/// raster. Safe to use from a render thread.
/// </summary>
public sealed class OutputMaskRasterCache
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, byte[]?> rasters = new(StringComparer.Ordinal);

    /// <summary>The raster for a plan, made with <paramref name="fillPath"/> the first time it is asked for.</summary>
    public byte[]? Get(OutputMaskPlan plan, Func<string, int, int, byte[]?> fillPath)
    {
        lock (gate)
        {
            if (rasters.TryGetValue(plan.Fingerprint, out var cached)) return cached;
        }
        var made = OutputMaskRaster.Coverage(plan.Masks, plan.Width, plan.Height, fillPath);
        lock (gate)
        {
            // Another thread may have made it meanwhile; keep the first so everyone shares one array.
            if (rasters.TryGetValue(plan.Fingerprint, out var raced)) return raced;
            rasters[plan.Fingerprint] = made;
            return made;
        }
    }

    /// <summary>Drops rasters no output uses any more.</summary>
    public void Retain(IEnumerable<string> fingerprints)
    {
        var keep = fingerprints.ToHashSet(StringComparer.Ordinal);
        lock (gate)
        {
            foreach (var key in rasters.Keys.Where(key => !keep.Contains(key)).ToList()) rasters.Remove(key);
        }
    }

    public int Count
    {
        get { lock (gate) return rasters.Count; }
    }
}
