using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace MxU.Slides.Windows.Rendering;

/// <summary>
/// Maps the Mac app's font names (PostScript names such as "HelveticaNeue-Bold",
/// or family names) to an installed Windows typeface. Documents keep the Mac
/// names so they stay portable; only drawing is translated.
/// </summary>
public static partial class FontMap
{
    public sealed record Face(FontFamily Family, FontWeight Weight, FontStyle Style, FontStretch Stretch)
    {
        public Typeface Typeface { get; } = new(Family, Style, Weight, Stretch);
    }

    private static readonly Lazy<HashSet<string>> Installed = new(() =>
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var family in Fonts.SystemFontFamilies)
        {
            names.Add(family.Source);
            foreach (var name in family.FamilyNames.Values) names.Add(name);
        }
        return names;
    });

    private static readonly Dictionary<string, string[]> Substitutes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["helveticaneue"] = ["Helvetica Neue", "Helvetica", "Arial"],
        ["helvetica"] = ["Helvetica", "Arial"],
        ["avenirnext"] = ["Avenir Next", "Avenir", "Segoe UI"],
        ["avenirnextcondensed"] = ["Avenir Next Condensed", "Segoe UI"],
        ["avenir"] = ["Avenir", "Segoe UI"],
        ["menlo"] = ["Menlo", "Cascadia Mono", "Consolas"],
        ["sfmono"] = ["SF Mono", "Cascadia Mono", "Consolas"],
        ["sfpro"] = ["SF Pro", "Segoe UI Variable Display", "Segoe UI"],
        ["sfprodisplay"] = ["SF Pro Display", "Segoe UI Variable Display", "Segoe UI"],
        ["sfprotext"] = ["SF Pro Text", "Segoe UI Variable Text", "Segoe UI"],
        [".sfns"] = ["Segoe UI Variable Display", "Segoe UI"],
        ["systemfont"] = ["Segoe UI Variable Display", "Segoe UI"],
        ["timesnewromanps"] = ["Times New Roman"],
        ["times"] = ["Times New Roman"],
        ["georgia"] = ["Georgia"],
        ["futura"] = ["Futura", "Century Gothic", "Segoe UI"],
        ["gillsans"] = ["Gill Sans", "Gill Sans MT", "Segoe UI"],
        ["couriernewps"] = ["Courier New"],
        ["courier"] = ["Courier New"],
        ["arialmt"] = ["Arial"],
        ["trebuchetms"] = ["Trebuchet MS"],
        ["palatino"] = ["Palatino", "Palatino Linotype"],
        ["baskerville"] = ["Baskerville", "Baskerville Old Face", "Georgia"],
        ["didot"] = ["Didot", "Bodoni MT", "Georgia"],
        ["optima"] = ["Optima", "Candara", "Segoe UI"],
        ["markerfelt"] = ["Marker Felt", "Segoe Print"],
        ["americantypewriter"] = ["American Typewriter", "Courier New"],
        ["hoeflertext"] = ["Hoefler Text", "Georgia"],
        ["bodoni72"] = ["Bodoni 72", "Bodoni MT", "Georgia"],
        ["chalkboard"] = ["Chalkboard", "Comic Sans MS"],
        ["impact"] = ["Impact"],
    };

    private static readonly Dictionary<string, Face> Cache = new(StringComparer.Ordinal);

    public static Face Resolve(string? fontName)
    {
        fontName = string.IsNullOrWhiteSpace(fontName) ? "HelveticaNeue-Bold" : fontName.Trim();
        lock (Cache)
        {
            if (Cache.TryGetValue(fontName, out var cached)) return cached;
            var face = Compute(fontName);
            Cache[fontName] = face;
            return face;
        }
    }

    private static Face Compute(string fontName)
    {
        // A plain family name ("Open Sans", "Montserrat SemiBold") that is installed wins outright.
        if (fontName.Contains(' ') && Installed.Value.Contains(fontName))
            return new Face(new FontFamily(fontName), FontWeights.Normal, FontStyles.Normal, FontStretches.Normal);

        var dash = fontName.IndexOf('-');
        var familyPart = dash > 0 ? fontName[..dash] : fontName;
        var stylePart = dash > 0 ? fontName[(dash + 1)..] : "";

        // "HelveticaNeue" with no dash may still carry a style ("HelveticaNeueBold").
        if (dash < 0 && TrailingStyle().Match(fontName) is { Success: true } m && m.Index > 0)
        {
            familyPart = fontName[..m.Index];
            stylePart = m.Value;
        }

        var (weight, style, stretch) = ParseStyle(stylePart);
        var key = familyPart.Replace(" ", "");
        var candidates = Substitutes.TryGetValue(key, out var known)
            ? known
            : [familyPart, SpaceCamelCase(familyPart), "Segoe UI"];
        var family = candidates.FirstOrDefault(Installed.Value.Contains) ?? "Segoe UI";
        return new Face(new FontFamily(family), weight, style, stretch);
    }

    private static (FontWeight, FontStyle, FontStretch) ParseStyle(string style)
    {
        var s = style.ToLowerInvariant();
        var weight = FontWeights.Normal;
        if (s.Contains("ultralight") || s.Contains("extralight")) weight = FontWeights.ExtraLight;
        else if (s.Contains("thin") || s.Contains("hairline")) weight = FontWeights.Thin;
        else if (s.Contains("light")) weight = FontWeights.Light;
        else if (s.Contains("medium")) weight = FontWeights.Medium;
        else if (s.Contains("demibold") || s.Contains("semibold")) weight = FontWeights.SemiBold;
        else if (s.Contains("extrabold") || s.Contains("ultrabold")) weight = FontWeights.ExtraBold;
        else if (s.Contains("heavy")) weight = FontWeights.ExtraBold;
        else if (s.Contains("black")) weight = FontWeights.Black;
        else if (s.Contains("bold")) weight = FontWeights.Bold;
        var italic = s.Contains("italic") || s.Contains("oblique") || s.EndsWith("it", StringComparison.Ordinal);
        var stretch = s.Contains("condensed") ? FontStretches.Condensed
            : s.Contains("compressed") ? FontStretches.ExtraCondensed
            : s.Contains("expanded") ? FontStretches.Expanded
            : FontStretches.Normal;
        return (weight, italic ? FontStyles.Italic : FontStyles.Normal, stretch);
    }

    /// <summary>The PostScript-style name for a family plus bold/italic, used when the editor toggles styles.</summary>
    public static string WithTraits(string? fontName, bool bold, bool italic)
    {
        var name = string.IsNullOrWhiteSpace(fontName) ? "HelveticaNeue" : fontName;
        var family = name.Contains('-') ? name[..name.IndexOf('-')] : name;
        var suffix = (bold, italic) switch
        {
            (true, true) => "-BoldItalic",
            (true, false) => "-Bold",
            (false, true) => "-Italic",
            _ => "",
        };
        return family + suffix;
    }

    public static bool IsBold(string? fontName) => Resolve(fontName).Weight.ToOpenTypeWeight() >= 600;

    public static bool IsItalic(string? fontName) => Resolve(fontName).Style != FontStyles.Normal;

    /// <summary>Families to offer in font pickers: the installed ones, sorted.</summary>
    public static IReadOnlyList<string> InstalledFamilies() =>
        Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();

    private static string SpaceCamelCase(string s) => CamelBoundary().Replace(s, " ");

    [GeneratedRegex("(?<=[a-z])(?=[A-Z])")]
    private static partial Regex CamelBoundary();

    [GeneratedRegex("(UltraLight|ExtraLight|Thin|Light|Medium|DemiBold|SemiBold|ExtraBold|Heavy|Black|Bold)(Italic)?$|Italic$")]
    private static partial Regex TrailingStyle();
}
