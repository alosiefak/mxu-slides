using System.Numerics;

namespace MxU.Slides.Core.Motion;

/// <summary>
/// A named set of colours for a motion background, darkest first. Every preset has four:
/// the deep colour the background sits in, two middle colours, and a highlight. They're
/// chosen so white lyrics stay readable on top: the darkest colour is always very dark.
/// The church-season presets follow the usual liturgical colours.
/// </summary>
public sealed record MotionPalette(string Name, IReadOnlyList<string> Colors)
{
    public static readonly IReadOnlyList<MotionPalette> Presets =
    [
        new("Midnight", ["#040814", "#0B1E4B", "#2160C4", "#9BD6FF"]),
        new("Dawn", ["#0B0A1F", "#3A2A6B", "#E0708A", "#FFD39A"]),
        new("Golden Hour", ["#0D0703", "#4A2208", "#E08A2E", "#FFE3A3"]),
        new("Ember", ["#0A0403", "#3D0F05", "#C2410C", "#FFC46B"]),
        new("Royal", ["#08040F", "#331060", "#8E44C9", "#F3C96B"]),
        new("Ocean", ["#020B11", "#053B4A", "#0E8A8A", "#9FF2E0"]),
        new("Evergreen", ["#020A07", "#0B3326", "#2E8B57", "#C9F2A7"]),
        new("Northern Lights", ["#02040A", "#0B2A3A", "#2BD98A", "#B07CFF"]),
        new("Rose Gold", ["#0F0608", "#4A1D2A", "#C97B84", "#F7D6C4"]),
        new("Silver", ["#050506", "#1E2126", "#6B7280", "#E5E7EB"]),
        new("Advent", ["#05030F", "#1B1450", "#4B3BA8", "#A7B4FF"]),
        new("Christmas", ["#070302", "#5A0A12", "#B81E2B", "#F2D27A"]),
        new("Lent", ["#08030C", "#2B0F3A", "#5E2A7E", "#C9A7E0"]),
        new("Easter", ["#0C0A08", "#3B2F1E", "#D9BF85", "#FFF6E0"]),
        new("Pentecost", ["#0A0201", "#6B0F05", "#E2401B", "#FFB347"]),
    ];

    /// <summary>The preset called <paramref name="name"/> (any case), or null.</summary>
    public static MotionPalette? Find(string? name) =>
        Presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The most colours a custom palette has.</summary>
    public const int MaxColors = 4;

    /// <summary>The fewest.</summary>
    public const int MinColors = 2;

    /// <summary>
    /// Turns two to four colours into the four the styles draw with, as linear light:
    /// <list type="bullet">
    /// <item>A preset's colours are used in the order they were designed in: deep, two middles,
    /// highlight (in "Northern Lights" the green comes before the lighter-looking violet, so the
    /// aurora is green at its foot).</item>
    /// <item>Colours picked by hand (<paramref name="inDesignedOrder"/> false) are sorted by how
    /// light they look, so the background colour is always the darkest.</item>
    /// <item>Two or three colours are spread into four by mixing between them.</item>
    /// <item>The darkest is made dark enough to sit behind white text (a custom palette of
    /// pastels still gets a deep version of its darkest colour, same hue).</item>
    /// </list>
    /// </summary>
    public static Vector3[] Stops(IReadOnlyList<string> colors, bool inDesignedOrder = false)
    {
        var labs = colors.Select(MotionColor.FromHex).OfType<Vector3>().Select(MotionColor.ToOklab).ToList();
        if (!inDesignedOrder) labs = [.. labs.OrderBy(l => l.X)];
        if (labs.Count == 0) labs = [.. Presets[0].Colors.Select(c => MotionColor.ToOklab(MotionColor.FromHex(c)!.Value))];
        if (labs.Count == 1) labs.Insert(0, labs[0] * new Vector3(0.25f, 0.6f, 0.6f));
        var four = labs.Count switch
        {
            2 => new[] { labs[0], Vector3.Lerp(labs[0], labs[1], 1 / 3f), Vector3.Lerp(labs[0], labs[1], 2 / 3f), labs[1] },
            3 => [labs[0], labs[1], Vector3.Lerp(labs[1], labs[2], 0.5f), labs[2]],
            _ => [labs[0], labs[1], labs[^2], labs[^1]],
        };
        // OKLab lightness 0.2 is about the darkest a projector shows as more than black; keep the
        // base below it, so lyrics read. Hue and colourfulness are kept, just darker.
        const float darkest = 0.2f;
        if (four[0].X > darkest)
        {
            var scale = darkest / four[0].X;
            four[0] = new Vector3(darkest, four[0].Y * scale, four[0].Z * scale);
        }
        return [.. four.Select(MotionColor.FromOklab)];
    }
}
