using MxU.Slides.Core.Library;
using MxU.Slides.Core.MotionTemplates;
using SkiaSharp;

namespace MxU.Slides.MotionTemplates.Lottie;

/// <summary>Finds a font file for a font a Lottie animation asks for, when Windows hasn't got it installed.</summary>
public interface ILottieFonts
{
    /// <summary>The font file's bytes, or null when there's no such font here.</summary>
    byte[]? Find(LottieFont font);
}

/// <summary>
/// The fonts in a folder (the library's <c>fonts</c> folder, which holds the fonts the app ships
/// and any a deck brought in): a Lottie font is matched by its PostScript name first
/// ("Montserrat-SemiBold", which is what Bodymovin writes), then by family and style. Each
/// file is read once.
/// </summary>
public sealed class FolderFonts : ILottieFonts
{
    private readonly Lock gate = new();
    private readonly string folder;
    private List<(FontFiles.Face Face, string Path)>? faces;

    public FolderFonts(string folder) => this.folder = folder;

    /// <summary>The fonts of a library (its <c>fonts</c> folder).</summary>
    public static FolderFonts OfLibrary(string libraryRoot) => new(Path.Combine(libraryRoot, FontFiles.FolderName));

    public byte[]? Find(LottieFont font)
    {
        List<(FontFiles.Face Face, string Path)> all;
        lock (gate) all = faces ??= Scan();
        var match = all.FirstOrDefault(f => f.Face.PostScriptName.Equals(font.Name, StringComparison.OrdinalIgnoreCase));
        if (match.Path is null)
            match = all.FirstOrDefault(f => f.Face.Family.Equals(font.Family, StringComparison.OrdinalIgnoreCase)
                                            && SameStyle(f.Face.Subfamily, font.Style));
        if (match.Path is null) return null;
        try { return File.ReadAllBytes(match.Path); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Forgets what was found, so fonts added since are seen.</summary>
    public void Refresh()
    {
        lock (gate) faces = null;
    }

    private List<(FontFiles.Face, string)> Scan()
    {
        var found = new List<(FontFiles.Face, string)>();
        if (!Directory.Exists(folder)) return found;
        foreach (var file in Directory.EnumerateFiles(folder).Where(f => Path.GetExtension(f).ToLowerInvariant() is ".ttf" or ".otf").Order(StringComparer.Ordinal))
        {
            try
            {
                foreach (var face in FontFiles.Faces(File.ReadAllBytes(file))) found.Add((face, file));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return found;
    }

    private static bool SameStyle(string a, string b) =>
        string.Equals(a.Replace(" ", ""), b.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Gives an animation the fonts it needs. Skia finds installed fonts by family and style on its
/// own; for any font Windows hasn't got, a matching file from <see cref="ILottieFonts"/> is put
/// into the animation as a <c>data:</c> URI (<c>fPath</c>), which Skottie loads directly. So a
/// template made with Montserrat draws in Montserrat on a PC that never installed it.
/// </summary>
internal static class FontEmbedding
{
    private static readonly Lazy<HashSet<string>> Installed = new(() =>
        new HashSet<string>(SKFontManager.Default.GetFontFamilies(), StringComparer.OrdinalIgnoreCase));

    public static bool IsInstalled(string family) => Installed.Value.Contains(family);

    /// <summary>The animation with fonts embedded where needed (the same object when nothing needed it).</summary>
    public static LottieDocument WithFonts(LottieDocument document, ILottieFonts? fonts)
    {
        if (fonts is null || document.Fonts.Count == 0) return document;
        var missing = document.Fonts.Where(f => f.Path is null && !IsInstalled(f.Family)).ToList();
        if (missing.Count == 0) return document;
        var copy = document.Clone();
        var changed = false;
        foreach (var entry in (copy.Root["fonts"]?["list"] as System.Text.Json.Nodes.JsonArray)?.OfType<System.Text.Json.Nodes.JsonObject>() ?? [])
        {
            var name = entry["fName"]?.GetValue<string>() ?? "";
            if (missing.FirstOrDefault(f => f.Name == name) is not { } font || fonts.Find(font) is not { } bytes) continue;
            entry["fPath"] = LottieFiles.DataUri(bytes, "font.ttf");
            entry["origin"] = 3;
            changed = true;
        }
        return changed ? copy : document;
    }
}
