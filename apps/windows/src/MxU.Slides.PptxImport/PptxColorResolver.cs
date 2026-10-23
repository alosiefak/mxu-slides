using System.Globalization;
using System.Xml.Linq;

namespace MxU.Slides.PptxImport;

/// <summary>Resolves a DrawingML colour (sRGB, scheme slot, system, preset) and its transforms to RRGGBBAA hex.</summary>
internal sealed class PptxColorResolver(IReadOnlyDictionary<string, string> scheme, IReadOnlyDictionary<string, string> slotMap)
{
    public IReadOnlyDictionary<string, string> Scheme { get; } = scheme;
    public IReadOnlyDictionary<string, string> SlotMap { get; } = slotMap;

    public string? Color(XElement? parent, List<string> warnings, string? phClr = null)
    {
        if (parent is null) return null;
        foreach (var child in parent.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "srgbClr":
                    return child.Attr("val") is { } hex ? ApplyingTransforms(child, hex, warnings) : null;
                case "schemeClr":
                {
                    if (child.Attr("val") is not { } slot) return null;
                    var baseHex = slot == "phClr" ? phClr : Scheme.GetValueOrDefault(SlotMap.GetValueOrDefault(slot) ?? slot);
                    if (baseHex is null)
                    {
                        warnings.AppendUnique($"theme color \"{slot}\" could not be resolved — that ink was skipped");
                        return null;
                    }
                    return ApplyingTransforms(child, baseHex, warnings);
                }
                case "sysClr":
                    return child.Attr("lastClr") is { } last ? ApplyingTransforms(child, last, warnings) : null;
                case "prstClr":
                    if (child.Attr("val") is not { } name || PresetColor(name) is not { } preset)
                    {
                        warnings.AppendUnique($"preset color \"{child.Attr("val") ?? "?"}\" is not recognized — that ink was skipped");
                        return null;
                    }
                    return ApplyingTransforms(child, preset, warnings);
                case "hslClr" or "scrgbClr":
                    warnings.AppendUnique($"{child.Name.LocalName} color values are not supported yet");
                    return null;
            }
        }
        return null;
    }

    private static string? ApplyingTransforms(XElement colorElement, string hex, List<string> warnings)
    {
        if (Channels(hex) is not { } rgb) return null;
        var alpha = 255.0;
        foreach (var transform in colorElement.Elements())
        {
            var value = transform.DoubleAttr("val") / 100_000;
            switch (transform.Name.LocalName)
            {
                case "alpha":
                    if (value is { } a) alpha = 255 * a;
                    break;
                case "tint":
                    if (value is { } tint) rgb = InLinear(rgb, c => c * tint + (1 - tint));
                    break;
                case "shade":
                    if (value is { } shade) rgb = InLinear(rgb, c => c * shade);
                    break;
                case "lumMod":
                    if (value is { } lumMod) rgb = AdjustingHsl(rgb, hsl => hsl with { L = hsl.L * lumMod });
                    break;
                case "lumOff":
                    if (value is { } lumOff) rgb = AdjustingHsl(rgb, hsl => hsl with { L = hsl.L + lumOff });
                    break;
                case "satMod":
                    if (value is { } satMod) rgb = AdjustingHsl(rgb, hsl => hsl with { S = hsl.S * satMod });
                    break;
                case var name:
                    warnings.AppendUnique($"color transform \"{name}\" is not applied");
                    break;
            }
        }
        var bytes = rgb.Append(alpha).Select(v => (byte)Math.Clamp(Math.Round(v, MidpointRounding.AwayFromZero), 0, 255)).ToArray();
        return $"{bytes[0]:X2}{bytes[1]:X2}{bytes[2]:X2}{bytes[3]:X2}";
    }

    private static double[] InLinear(double[] rgb, Func<double, double> change) =>
        rgb.Select(channel =>
        {
            var c = channel / 255;
            var linear = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            var changed = Math.Clamp(change(linear), 0, 1);
            var encoded = changed <= 0.0031308 ? changed * 12.92 : 1.055 * Math.Pow(changed, 1 / 2.4) - 0.055;
            return encoded * 255;
        }).ToArray();

    private readonly record struct Hsl(double H, double S, double L);

    private static double[] AdjustingHsl(double[] rgb, Func<Hsl, Hsl> change)
    {
        var hsl = change(ToHsl(rgb));
        return ToRgb(hsl with { L = Math.Clamp(hsl.L, 0, 1), S = Math.Clamp(hsl.S, 0, 1) });
    }

    private static Hsl ToHsl(double[] rgb)
    {
        double r = rgb[0] / 255, g = rgb[1] / 255, b = rgb[2] / 255;
        double maxC = Math.Max(r, Math.Max(g, b)), minC = Math.Min(r, Math.Min(g, b));
        var l = (maxC + minC) / 2;
        if (maxC == minC) return new Hsl(0, 0, l);
        var d = maxC - minC;
        var s = l > 0.5 ? d / (2 - maxC - minC) : d / (maxC + minC);
        double h;
        if (maxC == r) h = (g - b) / d + (g < b ? 6 : 0);
        else if (maxC == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        return new Hsl(h / 6, s, l);
    }

    private static double[] ToRgb(Hsl hsl)
    {
        if (hsl.S == 0) return [hsl.L * 255, hsl.L * 255, hsl.L * 255];
        var q = hsl.L < 0.5 ? hsl.L * (1 + hsl.S) : hsl.L + hsl.S - hsl.L * hsl.S;
        var p = 2 * hsl.L - q;
        double Channel(double offset)
        {
            var t = hsl.H + offset;
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
        return [Channel(1.0 / 3) * 255, Channel(0) * 255, Channel(-1.0 / 3) * 255];
    }

    private static double[]? Channels(string hex)
    {
        var cleaned = hex.Trim(' ', '\t');
        if (cleaned.Length != 6 || !uint.TryParse(cleaned, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
            return null;
        return [(value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF];
    }

    public static string? PresetColor(string name)
    {
        if (PresetColors.TryGetValue(name, out var hex)) return hex;
        var normalized = name;
        foreach (var (longForm, shortForm) in new[] { ("dark", "dk"), ("light", "lt"), ("medium", "med") })
        {
            if (normalized.StartsWith(longForm, StringComparison.Ordinal))
                normalized = shortForm + normalized[longForm.Length..];
        }
        normalized = normalized.Replace("Grey", "Gray", StringComparison.Ordinal);
        if (normalized.StartsWith("grey", StringComparison.Ordinal)) normalized = "gray" + normalized[4..];
        return PresetColors.GetValueOrDefault(normalized);
    }

    private static readonly Dictionary<string, string> PresetColors = new(StringComparer.Ordinal)
    {
        ["aliceBlue"] = "F0F8FF", ["antiqueWhite"] = "FAEBD7", ["aqua"] = "00FFFF",
        ["aquamarine"] = "7FFFD4", ["azure"] = "F0FFFF", ["beige"] = "F5F5DC",
        ["bisque"] = "FFE4C4", ["black"] = "000000", ["blanchedAlmond"] = "FFEBCD",
        ["blue"] = "0000FF", ["blueViolet"] = "8A2BE2", ["brown"] = "A52A2A",
        ["burlyWood"] = "DEB887", ["cadetBlue"] = "5F9EA0", ["chartreuse"] = "7FFF00",
        ["chocolate"] = "D2691E", ["coral"] = "FF7F50", ["cornflowerBlue"] = "6495ED",
        ["cornsilk"] = "FFF8DC", ["crimson"] = "DC143C", ["cyan"] = "00FFFF",
        ["deepPink"] = "FF1493", ["deepSkyBlue"] = "00BFFF", ["dimGray"] = "696969",
        ["dkBlue"] = "00008B", ["dkCyan"] = "008B8B", ["dkGoldenrod"] = "B8860B",
        ["dkGray"] = "A9A9A9", ["dkGreen"] = "006400", ["dkKhaki"] = "BDB76B",
        ["dkMagenta"] = "8B008B", ["dkOliveGreen"] = "556B2F", ["dkOrange"] = "FF8C00",
        ["dkOrchid"] = "9932CC", ["dkRed"] = "8B0000", ["dkSalmon"] = "E9967A",
        ["dkSeaGreen"] = "8FBC8F", ["dkSlateBlue"] = "483D8B", ["dkSlateGray"] = "2F4F4F",
        ["dkTurquoise"] = "00CED1", ["dkViolet"] = "9400D3", ["dodgerBlue"] = "1E90FF",
        ["firebrick"] = "B22222", ["floralWhite"] = "FFFAF0", ["forestGreen"] = "228B22",
        ["fuchsia"] = "FF00FF", ["gainsboro"] = "DCDCDC", ["ghostWhite"] = "F8F8FF",
        ["gold"] = "FFD700", ["goldenrod"] = "DAA520", ["gray"] = "808080",
        ["green"] = "008000", ["greenYellow"] = "ADFF2F", ["honeydew"] = "F0FFF0",
        ["hotPink"] = "FF69B4", ["indianRed"] = "CD5C5C", ["indigo"] = "4B0082",
        ["ivory"] = "FFFFF0", ["khaki"] = "F0E68C", ["lavender"] = "E6E6FA",
        ["lavenderBlush"] = "FFF0F5", ["lawnGreen"] = "7CFC00", ["lemonChiffon"] = "FFFACD",
        ["lime"] = "00FF00", ["limeGreen"] = "32CD32", ["linen"] = "FAF0E6",
        ["ltBlue"] = "ADD8E6", ["ltCoral"] = "F08080", ["ltCyan"] = "E0FFFF",
        ["ltGoldenrodYellow"] = "FAFAD2", ["ltGray"] = "D3D3D3", ["ltGreen"] = "90EE90",
        ["ltPink"] = "FFB6C1", ["ltSalmon"] = "FFA07A", ["ltSeaGreen"] = "20B2AA",
        ["ltSkyBlue"] = "87CEFA", ["ltSlateGray"] = "778899", ["ltSteelBlue"] = "B0C4DE",
        ["ltYellow"] = "FFFFE0", ["magenta"] = "FF00FF", ["maroon"] = "800000",
        ["medAquamarine"] = "66CDAA", ["medBlue"] = "0000CD", ["medOrchid"] = "BA55D3",
        ["medPurple"] = "9370DB", ["medSeaGreen"] = "3CB371", ["medSlateBlue"] = "7B68EE",
        ["medSpringGreen"] = "00FA9A", ["medTurquoise"] = "48D1CC", ["medVioletRed"] = "C71585",
        ["midnightBlue"] = "191970", ["mintCream"] = "F5FFFA", ["mistyRose"] = "FFE4E1",
        ["moccasin"] = "FFE4B5", ["navajoWhite"] = "FFDEAD", ["navy"] = "000080",
        ["oldLace"] = "FDF5E6", ["olive"] = "808000", ["oliveDrab"] = "6B8E23",
        ["orange"] = "FFA500", ["orangeRed"] = "FF4500", ["orchid"] = "DA70D6",
        ["paleGoldenrod"] = "EEE8AA", ["paleGreen"] = "98FB98", ["paleTurquoise"] = "AFEEEE",
        ["paleVioletRed"] = "DB7093", ["papayaWhip"] = "FFEFD5", ["peachPuff"] = "FFDAB9",
        ["peru"] = "CD853F", ["pink"] = "FFC0CB", ["plum"] = "DDA0DD",
        ["powderBlue"] = "B0E0E6", ["purple"] = "800080", ["red"] = "FF0000",
        ["rosyBrown"] = "BC8F8F", ["royalBlue"] = "4169E1", ["saddleBrown"] = "8B4513",
        ["salmon"] = "FA8072", ["sandyBrown"] = "F4A460", ["seaGreen"] = "2E8B57",
        ["seaShell"] = "FFF5EE", ["sienna"] = "A0522D", ["silver"] = "C0C0C0",
        ["skyBlue"] = "87CEEB", ["slateBlue"] = "6A5ACD", ["slateGray"] = "708090",
        ["snow"] = "FFFAFA", ["springGreen"] = "00FF7F", ["steelBlue"] = "4682B4",
        ["tan"] = "D2B48C", ["teal"] = "008080", ["thistle"] = "D8BFD8",
        ["tomato"] = "FF6347", ["turquoise"] = "40E0D0", ["violet"] = "EE82EE",
        ["wheat"] = "F5DEB3", ["white"] = "FFFFFF", ["whiteSmoke"] = "F5F5F5",
        ["yellow"] = "FFFF00", ["yellowGreen"] = "9ACD32",
    };
}
