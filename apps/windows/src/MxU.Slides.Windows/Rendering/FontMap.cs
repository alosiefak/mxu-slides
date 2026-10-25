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

    // The library's own fonts (a deck's embedded fonts, saved by the PowerPoint importer), loaded
    // for drawing from their files without installing them into Windows. By PostScript name
    // ("Gotham-Bold": the exact face) and by family name ("Gotham": every face of it in the folder).
    private static readonly Dictionary<string, Face> LibraryFaces = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, FontFamily> LibraryFamilies = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> LoadedFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Makes the fonts in the library's <c>fonts</c> folder usable for drawing and in the font
    /// pickers, as the Mac's FontActivator.activateStored does at launch. Each file is read once;
    /// call again after an import to pick up new ones. WPF loads them from the file privately
    /// (a FontFamily with a file location), so nothing is installed into Windows. Returns how many
    /// faces were added.
    /// </summary>
    public static int ActivateStored(string libraryRoot)
    {
        var added = 0;
        var folder = Path.Combine(libraryRoot, Core.Library.FontFiles.FolderName);
        lock (Cache)
        {
            foreach (var file in Core.Library.FontFiles.Stored(libraryRoot))
            {
                if (!LoadedFiles.Add(file)) continue;
                try
                {
                    var faces = Core.Library.FontFiles.Faces(File.ReadAllBytes(file));
                    // WPF's own reading of the file, so its family names are the ones WPF will match.
                    var families = Fonts.GetFontFamilies(file).ToList();
                    if (faces.Count == 0 || families.Count == 0) continue;
                    foreach (var family in families)
                        foreach (var name in family.FamilyNames.Values.Append(FamilyName(family)).OfType<string>())
                            LibraryFamilies.TryAdd(name, new FontFamily(new Uri(folder + Path.DirectorySeparatorChar), "./#" + name));
                    foreach (var face in faces)
                    {
                        var family = families.FirstOrDefault(f => f.FamilyNames.Values.Contains(face.Family, StringComparer.OrdinalIgnoreCase)) ?? families[0];
                        LibraryFamilies.TryAdd(face.Family, new FontFamily(new Uri(folder + Path.DirectorySeparatorChar), "./#" + (FamilyName(family) ?? face.Family)));
                        var exact = new Face(family, FontWeight.FromOpenTypeWeight(Math.Clamp(face.Weight, 1, 999)),
                            face.Italic ? FontStyles.Italic : FontStyles.Normal, FontStretch.FromOpenTypeStretch(Math.Clamp(face.Width, 1, 9)));
                        if (LibraryFaces.TryAdd(face.PostScriptName, exact)) added++;
                        // Its full name too ("Montserrat Black", "Poppins SemiBold"): what PowerPoint and ProPresenter
                        // files often name, and what Core Text also resolves. Without it "Montserrat Black" read as the
                        // family "Montserrat " plus a Black style, missed, and fell back to Segoe UI.
                        if (face.FullName.Length > 0) LibraryFaces.TryAdd(face.FullName, exact);
                    }
                }
                catch (Exception ex)
                {
                    Infrastructure.Log.Warn($"Couldn't load the library font {Path.GetFileName(file)}: {ex.Message}");
                }
            }
            if (added > 0)
            {
                // Names that fell back to a stand-in before may now have their real font.
                Cache.Clear();
                installedFamilies = null;
            }
        }
        if (added > 0)
        {
            Infrastructure.Log.Info($"Loaded {added} font face{(added == 1 ? "" : "s")} from the library");
            TextLayout.ForgetMeasurements();
            PathTextRenderer.ForgetLayouts();
        }
        return added;
    }

    /// <summary>The family name in a family's location ("file:///…/x.ttf#Gotham" → "Gotham").</summary>
    private static string? FamilyName(FontFamily family) =>
        family.Source.LastIndexOf('#') is var hash and >= 0 ? family.Source[(hash + 1)..] : null;

    private static bool IsAvailable(string family) => Installed.Value.Contains(family) || LibraryFamilies.ContainsKey(family);

    private static FontFamily Family(string name) => LibraryFamilies.TryGetValue(name, out var own) ? own : new FontFamily(name);

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
        // A library font's exact face by its PostScript name ("Gotham-Bold"), as Core Text resolves it.
        if (LibraryFaces.TryGetValue(fontName, out var own)) return own;

        // A plain family name ("Open Sans", "Montserrat SemiBold") that is installed wins outright.
        if (fontName.Contains(' ') && IsAvailable(fontName))
            return new Face(Family(fontName), FontWeights.Normal, FontStyles.Normal, FontStretches.Normal);

        var dash = fontName.IndexOf('-');
        var familyPart = dash > 0 ? fontName[..dash] : fontName;
        var stylePart = dash > 0 ? fontName[(dash + 1)..] : "";

        // "HelveticaNeue" with no dash may still carry a style ("HelveticaNeueBold").
        if (dash < 0 && TrailingStyle().Match(fontName) is { Success: true } m && m.Index > 0)
        {
            // "Poppins SemiBold" → "Poppins" + SemiBold (trimmed, or the family's name wouldn't match).
            familyPart = fontName[..m.Index].TrimEnd();
            stylePart = m.Value;
        }

        var (weight, style, stretch) = ParseStyle(stylePart);
        var key = familyPart.Replace(" ", "");
        var candidates = Substitutes.TryGetValue(key, out var known)
            ? known
            : [familyPart, SpaceCamelCase(familyPart), "Segoe UI"];
        var family = candidates.FirstOrDefault(IsAvailable) ?? "Segoe UI";
        return new Face(Family(family), weight, style, stretch);
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

    private static IReadOnlyList<string>? installedFamilies;

    /// <summary>Families to offer in font pickers: the installed ones and the library's own, sorted.</summary>
    public static IReadOnlyList<string> InstalledFamilies()
    {
        lock (Cache)
        {
            return installedFamilies ??= Fonts.SystemFontFamilies.Select(f => f.Source)
                .Concat(LibraryFamilies.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }

    /// <summary>True for a font that comes from the library rather than Windows (for tests and the self-test).</summary>
    public static bool IsLibraryFont(string fontName)
    {
        lock (Cache) return LibraryFaces.ContainsKey(fontName) || LibraryFamilies.ContainsKey(fontName);
    }

    private static string SpaceCamelCase(string s) => CamelBoundary().Replace(s, " ");

    [GeneratedRegex("(?<=[a-z])(?=[A-Z])")]
    private static partial Regex CamelBoundary();

    [GeneratedRegex("(UltraLight|ExtraLight|Thin|Light|Medium|DemiBold|SemiBold|ExtraBold|Heavy|Black|Bold)(Italic)?$|Italic$")]
    private static partial Regex TrailingStyle();
}
