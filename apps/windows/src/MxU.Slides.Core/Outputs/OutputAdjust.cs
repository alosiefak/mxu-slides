using System.Text.Json.Serialization;

namespace MxU.Slides.Core.Outputs;

// Per-output corrections a projectionist dials in at the venue: corner pin
// (keystone), colour trims, edge-blend ramps and a whole-picture rotation.
// They are applied by one last render pass after the scene is drawn (see
// OutputPass.cs). Port of RenderEngine/OutputAdjust.swift; the JSON matches
// the Mac's Codable output so a saved setup reads the same on both.

/// <summary>A point or pixel offset. Encodes as <c>[x, y]</c>, like CoreGraphics' CGPoint.</summary>
[JsonConverter(typeof(OutputPointJsonConverter))]
public readonly record struct OutputPoint(double X, double Y)
{
    public static readonly OutputPoint Zero = new(0, 0);

    public static implicit operator OutputPoint((double X, double Y) point) => new(point.X, point.Y);

    public static implicit operator (double X, double Y)(OutputPoint point) => (point.X, point.Y);
}

/// <summary>
/// A soft ramp along one edge, so two overlapping projectors fade into each
/// other instead of doubling the light where they overlap.
/// </summary>
public sealed record OutputEdgeBlend
{
    public OutputEdgeBlend() { }

    public OutputEdgeBlend(double width, double curve = 2.2, double? intensity = null, double? blackLift = null)
    {
        Width = width;
        Curve = curve;
        Intensity = intensity;
        BlackLift = blackLift;
    }

    /// <summary>How far in from the edge the ramp runs, as a fraction of the output (0.1 = 10%). 0 or less = off.</summary>
    [JsonPropertyName("width")]
    public double Width { get; init; }

    /// <summary>The ramp's power curve: 1 is a straight line; the 2.2 default compensates for projector gamma.</summary>
    [JsonPropertyName("curve")]
    public double Curve { get; init; } = 2.2;

    /// <summary>How deep the ramp goes, 0–1 (null = 1, all the way to black at the edge).</summary>
    [JsonPropertyName("intensity"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Intensity { get; init; }

    /// <summary>
    /// Raises black outside the overlap, 0–1, to match the doubled "black" of two
    /// projectors overlapping (projectors can't show true black). Null = 0.
    /// </summary>
    [JsonPropertyName("blackLift"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? BlackLift { get; init; }
}

/// <summary>
/// Every correction one output (a screen or a slice of it) carries. Null means
/// "not set", which behaves like zero. Create with an object initializer and
/// change with <c>with</c>, e.g. <c>adjustments with { Brightness = -0.5 }</c>.
/// </summary>
public sealed record OutputAdjustments
{
    /// <summary>Corner-pin offsets, in target pixels (y down), moving each corner of the picture.</summary>
    [JsonPropertyName("topLeft"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OutputPoint? TopLeft { get; init; }

    [JsonPropertyName("topRight"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OutputPoint? TopRight { get; init; }

    [JsonPropertyName("bottomLeft"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OutputPoint? BottomLeft { get; init; }

    [JsonPropertyName("bottomRight"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OutputPoint? BottomRight { get; init; }

    /// <summary>Added to every channel: -1 turns white black, +1 turns black white.</summary>
    [JsonPropertyName("brightness"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Brightness { get; init; }

    /// <summary>Stretches the picture around mid-grey: the slope becomes 1 + contrast.</summary>
    [JsonPropertyName("contrast"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Contrast { get; init; }

    /// <summary>Bends the mid-tones: the picture is raised to the power 1 / (1 + gamma).</summary>
    [JsonPropertyName("gamma"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Gamma { get; init; }

    /// <summary>Added before brightness and floored at 0, so negative values crush the shadows.</summary>
    [JsonPropertyName("blackLevel"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? BlackLevel { get; init; }

    /// <summary>Per-channel gain: the channel is multiplied by 1 + level (-1 switches it off).</summary>
    [JsonPropertyName("redLevel"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? RedLevel { get; init; }

    [JsonPropertyName("greenLevel"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? GreenLevel { get; init; }

    [JsonPropertyName("blueLevel"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? BlueLevel { get; init; }

    [JsonPropertyName("blendLeft"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OutputEdgeBlend? BlendLeft { get; init; }

    [JsonPropertyName("blendRight"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OutputEdgeBlend? BlendRight { get; init; }

    [JsonPropertyName("blendTop"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OutputEdgeBlend? BlendTop { get; init; }

    [JsonPropertyName("blendBottom"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OutputEdgeBlend? BlendBottom { get; init; }

    /// <summary>Turns the whole picture, scaled down so it still fits the output (90 = a quarter turn clockwise).</summary>
    [JsonPropertyName("rotationDegrees"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? RotationDegrees { get; init; }

    /// <summary>True when nothing here would change the picture, so the correction pass can be skipped.</summary>
    [JsonIgnore]
    public bool IsNeutral
    {
        get
        {
            static bool Zero(OutputPoint? point) => (point ?? OutputPoint.Zero) == OutputPoint.Zero;
            static bool ZeroValue(double? value) => (value ?? 0) == 0;
            static bool Off(OutputEdgeBlend? blend) => (blend?.Width ?? 0) <= 0;
            // A full turn (360, 720…) is no turn. C#'s % on doubles truncates like Swift's truncatingRemainder.
            return Zero(TopLeft) && Zero(TopRight) && Zero(BottomLeft) && Zero(BottomRight)
                && ZeroValue(Brightness) && ZeroValue(Contrast) && ZeroValue(Gamma) && ZeroValue(BlackLevel)
                && ZeroValue(RedLevel) && ZeroValue(GreenLevel) && ZeroValue(BlueLevel)
                && Off(BlendLeft) && Off(BlendRight) && Off(BlendTop) && Off(BlendBottom)
                && ZeroValue(RotationDegrees % 360);
        }
    }

    /// <summary>True when any edge carries a blend (even a zero-width one), the test the layout picker uses.</summary>
    [JsonIgnore]
    public bool HasBlends => BlendLeft is not null || BlendRight is not null || BlendTop is not null || BlendBottom is not null;
}

/// <summary>
/// The corner-pin maths: a projective transform (homography) between the
/// warped output quad and the picture it shows. Port of OutputWarp in
/// OutputAdjust.swift.
/// </summary>
public static class OutputWarp
{
    /// <summary>
    /// The 3×3 homography that maps a destination point (x, y) to its source
    /// uv: row-major h0…h8 with h8 = 1, so
    /// <c>q = h6·x + h7·y + 1</c>, <c>u = (h0·x + h1·y + h2) / q</c>, <c>v = (h3·x + h4·y + h5) / q</c>.
    /// Corners are in the Mac's order: top-left, top-right, bottom-left, bottom-right.
    /// Returns null when the destination quad is degenerate (three corners on a line).
    /// A pixel shader can evaluate this per pixel (see <see cref="Map"/>: q may be
    /// negative, so don't clamp it); the Mac instead evaluates it at the corners
    /// (<see cref="ProjectiveUvs"/>) and lets the GPU interpolate.
    /// </summary>
    public static double[]? Homography(IReadOnlyList<(double X, double Y)> destinations, IReadOnlyList<(double X, double Y)> sources)
    {
        if (destinations.Count != 4 || sources.Count != 4)
            throw new ArgumentException("A homography needs exactly four destination and four source corners.");

        // Two equations per corner pair, unknowns h0…h7, right-hand side in column 8.
        var matrix = new double[8][];
        for (var i = 0; i < 4; i++)
        {
            var (dx, dy) = destinations[i];
            var (sx, sy) = sources[i];
            matrix[i * 2] = [dx, dy, 1, 0, 0, 0, -sx * dx, -sx * dy, sx];
            matrix[i * 2 + 1] = [0, 0, 0, dx, dy, 1, -sy * dx, -sy * dy, sy];
        }
        if (Solve(matrix) is not { } h) return null;
        return [h[0], h[1], h[2], h[3], h[4], h[5], h[6], h[7], 1];
    }

    /// <summary>
    /// The same transform as <see cref="Homography"/>, scaled so that q = 1 at
    /// the centre of the destination quad instead of at the coordinate origin.
    /// q is then positive everywhere inside the quad, so a shader may clamp it
    /// (<c>max(q, 1e-6)</c>) safely, and the solve stays well conditioned
    /// whatever corner the coordinates start from. h8 is generally not 1.
    /// </summary>
    public static double[]? CenteredHomography(IReadOnlyList<(double X, double Y)> destinations, IReadOnlyList<(double X, double Y)> sources)
    {
        if (destinations.Count != 4) throw new ArgumentException("A homography needs exactly four destination corners.");
        var cx = destinations.Average(d => d.X);
        var cy = destinations.Average(d => d.Y);
        if (Homography(destinations.Select(d => (d.X - cx, d.Y - cy)).ToArray(), sources) is not { } h) return null;
        // Solved for (x − cx, y − cy); fold that shift back in so it takes plain (x, y).
        return
        [
            h[0], h[1], h[2] - h[0] * cx - h[1] * cy,
            h[3], h[4], h[5] - h[3] * cx - h[4] * cy,
            h[6], h[7], h[8] - h[6] * cx - h[7] * cy,
        ];
    }

    /// <summary>
    /// The homography evaluated at each destination corner, as (u·q, v·q, q).
    /// The GPU interpolates these linearly and divides u·q and v·q by q per
    /// pixel, which is what makes the warp perspective-correct. Null when degenerate.
    /// </summary>
    public static (double Uq, double Vq, double Q)[]? ProjectiveUvs(IReadOnlyList<(double X, double Y)> destinations, IReadOnlyList<(double X, double Y)> sources)
    {
        if (Homography(destinations, sources) is not { } h) return null;
        return destinations.Select(d =>
        {
            var q = h[6] * d.X + h[7] * d.Y + 1;
            var uq = h[0] * d.X + h[1] * d.Y + h[2];
            var vq = h[3] * d.X + h[4] * d.Y + h[5];
            return (uq, vq, q);
        }).ToArray();
    }

    /// <summary>
    /// Applies a homography to one point. Divide by q whatever its sign: H and
    /// −H are the same transform, and with h8 fixed at 1 q comes out negative
    /// across the whole quad whenever the coordinate origin lies beyond the
    /// warp's horizon (in unit space the origin is the target's top-left
    /// corner, so pulling that corner in by more than about a third does it;
    /// the Mac's clip space puts the origin at the centre, and
    /// <see cref="CenteredHomography"/> avoids it). A point maps inside the
    /// source rect exactly when it is inside the quad. Null only on the horizon
    /// itself (q = 0).
    /// </summary>
    public static (double U, double V)? Map(double[] homography, double x, double y)
    {
        var h = homography;
        var q = h[6] * x + h[7] * y + h[8];
        if (Math.Abs(q) <= 1e-12) return null;
        return ((h[0] * x + h[1] * y + h[2]) / q, (h[3] * x + h[4] * y + h[5]) / q);
    }

    /// <summary>Gauss–Jordan elimination with partial pivoting on an 8×9 augmented matrix (changed in place).</summary>
    private static double[]? Solve(double[][] m)
    {
        for (var column = 0; column < 8; column++)
        {
            // The first row with the largest magnitude in this column (Swift's max(by:) keeps the first).
            var pivot = column;
            for (var row = column + 1; row < 8; row++)
                if (Math.Abs(m[row][column]) > Math.Abs(m[pivot][column])) pivot = row;
            if (Math.Abs(m[pivot][column]) <= 1e-9) return null;
            (m[column], m[pivot]) = (m[pivot], m[column]);
            var divisor = m[column][column];
            for (var k = column; k < 9; k++) m[column][k] /= divisor;
            for (var row = 0; row < 8; row++)
            {
                if (row == column) continue;
                var factor = m[row][column];
                if (factor == 0) continue;
                for (var k = column; k < 9; k++) m[row][k] -= factor * m[column][k];
            }
        }
        return m.Select(row => row[8]).ToArray();
    }
}
