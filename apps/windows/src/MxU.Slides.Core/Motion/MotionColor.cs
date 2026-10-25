using System.Globalization;
using System.Numerics;

namespace MxU.Slides.Core.Motion;

/// <summary>
/// Colour maths for the motion backgrounds. Light is added up in <b>linear</b> light (twice
/// the light is twice the number), which is how glows and lens blur really mix. Colours are
/// blended in <b>OKLab</b> (Björn Ottosson, 2020), a space where equal steps look equally
/// different, so a gradient from blue to gold passes through colours that look in-between
/// rather than a muddy grey. Only the final picture is turned into sRGB, the numbers a screen
/// and a video file expect.
/// </summary>
public static class MotionColor
{
    /// <summary>"#RRGGBB" (or "#RRGGBBAA", alpha ignored) as linear light; null when it isn't a colour.</summary>
    public static Vector3? FromHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var text = hex.Trim().TrimStart('#');
        if (text.Length is not (6 or 8) || !uint.TryParse(text[..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return null;
        return new Vector3(ToLinear((rgb >> 16 & 0xFF) / 255f), ToLinear((rgb >> 8 & 0xFF) / 255f), ToLinear((rgb & 0xFF) / 255f));
    }

    /// <summary>Linear light as "#RRGGBB".</summary>
    public static string ToHex(Vector3 linear)
    {
        static int Byte(float c) => (int)MathF.Round(Math.Clamp(ToSrgb(c), 0, 1) * 255);
        return $"#{Byte(linear.X):X2}{Byte(linear.Y):X2}{Byte(linear.Z):X2}";
    }

    public static float ToLinear(float srgb) => srgb <= 0.04045f ? srgb / 12.92f : MathF.Pow((srgb + 0.055f) / 1.055f, 2.4f);

    public static float ToSrgb(float linear) =>
        linear <= 0.0031308f ? linear * 12.92f : 1.055f * MathF.Pow(Math.Max(linear, 0), 1 / 2.4f) - 0.055f;

    /// <summary>How bright a linear colour looks (its luminance, as in BT.709).</summary>
    public static float Luminance(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;

    /// <summary>Linear sRGB to OKLab: X is lightness (0 black … 1 white), Y and Z the two colour axes.</summary>
    public static Vector3 ToOklab(Vector3 c)
    {
        var l = MathF.Cbrt(0.4122214708f * c.X + 0.5363325363f * c.Y + 0.0514459929f * c.Z);
        var m = MathF.Cbrt(0.2119034982f * c.X + 0.6806995451f * c.Y + 0.1073969566f * c.Z);
        var s = MathF.Cbrt(0.0883024619f * c.X + 0.2817188376f * c.Y + 0.6299787005f * c.Z);
        return new Vector3(
            0.2104542553f * l + 0.7936177850f * m - 0.0040720468f * s,
            1.9779984951f * l - 2.4285922050f * m + 0.4505937099f * s,
            0.0259040371f * l + 0.7827717662f * m - 0.8086757660f * s);
    }

    /// <summary>OKLab back to linear sRGB (out-of-range results are left for the caller to clamp).</summary>
    public static Vector3 FromOklab(Vector3 lab)
    {
        var l = lab.X + 0.3963377774f * lab.Y + 0.2158037573f * lab.Z;
        var m = lab.X - 0.1055613458f * lab.Y - 0.0638541728f * lab.Z;
        var s = lab.X - 0.0894841775f * lab.Y - 1.2914855480f * lab.Z;
        l = l * l * l;
        m = m * m * m;
        s = s * s * s;
        return Vector3.Max(Vector3.Zero, new Vector3(
            4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s,
            -1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s,
            -0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s));
    }

    /// <summary>The colour <paramref name="t"/> of the way from <paramref name="a"/> to <paramref name="b"/>, mixed in OKLab (<see cref="LerpLab"/>).</summary>
    public static Vector3 Mix(Vector3 a, Vector3 b, float t) => FromOklab(LerpLab(ToOklab(a), ToOklab(b), t));

    /// <summary>How colourful an OKLab colour is: its distance from grey.</summary>
    public static float Chroma(Vector3 lab) => MathF.Sqrt(lab.Y * lab.Y + lab.Z * lab.Z);

    // How far LerpLab may turn up the colour: enough to bring back what a straight line loses
    // between hues far apart, without inventing a strong colour between two that are opposite.
    private const float MostBoost = 2.5f;

    /// <summary>
    /// Two OKLab colours blended, keeping their colourfulness. A straight line between two quite
    /// different hues (purple and gold, say) passes close to grey half-way, which reads as mud.
    /// So lightness and hue follow the straight line, but the chroma (how colourful) is set to the
    /// blend of the two colours' own chromas: purple to gold goes through a clear rose instead.
    /// Colours of similar hue barely change, since their straight line loses almost nothing.
    /// </summary>
    public static Vector3 LerpLab(Vector3 a, Vector3 b, float t) => KeepChroma(Vector3.Lerp(a, b, t), Chroma(a) + (Chroma(b) - Chroma(a)) * t);

    /// <summary>
    /// <paramref name="lab"/> with its chroma turned up to <paramref name="chroma"/> (never down,
    /// and at most <see cref="MostBoost"/> times), its lightness and hue unchanged: for blends,
    /// which lose colour on the way (<see cref="LerpLab"/>, and weighted averages of several colours).
    /// </summary>
    public static Vector3 KeepChroma(Vector3 lab, float chroma)
    {
        var have = Chroma(lab);
        if (have < 1e-6f || chroma <= have) return lab;
        var scale = Math.Min(chroma / have, MostBoost);
        return new Vector3(lab.X, lab.Y * scale, lab.Z * scale);
    }

    public static float Smoothstep(float edge0, float edge1, float x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);
        return t * t * (3 - 2 * t);
    }
}

/// <summary>
/// A palette laid out as a smooth gradient from its darkest colour (0) to its lightest (1),
/// precomputed into a table so the styles can look a colour up for every pixel cheaply.
/// </summary>
public sealed class PaletteRamp
{
    private const int Size = 256;
    private readonly Vector3[] table = new Vector3[Size + 1];

    /// <param name="stops">Linear colours, darkest first, spread evenly along the ramp.</param>
    public PaletteRamp(IReadOnlyList<Vector3> stops)
    {
        var labs = stops.Select(MotionColor.ToOklab).ToArray();
        for (var i = 0; i <= Size; i++)
        {
            var at = (float)i / Size * (labs.Length - 1);
            var from = Math.Min((int)at, labs.Length - 2);
            table[i] = MotionColor.FromOklab(MotionColor.LerpLab(labs[from], labs[from + 1], at - from));
        }
    }

    /// <summary>The colour at <paramref name="t"/> (0–1, clamped) along the ramp.</summary>
    public Vector3 this[float t]
    {
        get
        {
            var at = Math.Clamp(t, 0, 1) * Size;
            var i = Math.Min((int)at, Size - 1);
            return Vector3.Lerp(table[i], table[i + 1], at - i);
        }
    }
}
