using System.Security.Cryptography;
using System.Text;

namespace MxU.Slides.Core.Library;

/// <summary>
/// The fonts the Windows app ships with: Poppins, Montserrat, Bebas Neue and DM Serif Display,
/// all under the SIL Open Font License 1.1 (which lets software bundle them, as long as the
/// licence and each font's copyright notice go with them).
/// <para>
/// Why copy them into every library's <c>fonts</c> folder rather than just using them from the
/// app's own folder? Because that folder is what everything else already reads:
/// <list type="bullet">
/// <item>the app loads the library's fonts for drawing without installing them into Windows
/// (Rendering/FontMap.ActivateStored);</item>
/// <item>a <c>.mxuslides</c> export carries the font files its slides name from there
/// (FontSharing), so a Mac that opens the deck gets the same fonts, and the starter looks that
/// use them look the same on both.</item>
/// </list>
/// The copies are named by their SHA-256, like every other font in the folder, so the same font
/// arriving again (in an import, or a later copy of the app) is never stored twice.
/// </para>
/// Windows-only: the Mac app ships no fonts of its own.
/// </summary>
public static class BundledFonts
{
    /// <summary>The folder beside the app (MxUSlides.exe) that holds the shipped fonts and their licence.</summary>
    public const string FolderName = "Fonts";

    /// <summary>The licence every bundled font is under (its full text ships as OFL.txt).</summary>
    public const string LicenceFileName = "OFL.txt";

    /// <summary>The credits and licence in one file, for people to read (written by <see cref="Notice"/>).</summary>
    public const string NoticeFileName = "FONT-NOTICES.txt";

    /// <summary>Where the running app's fonts are.</summary>
    public static string ShippedFolder => Path.Combine(AppContext.BaseDirectory, FolderName);

    /// <summary>One family's credit: who made it and holds its copyright, the styles shipped, and its licence.</summary>
    public sealed record Credit(string Family, IReadOnlyList<string> Styles, string Copyright, string Version, string Licence, string? LicenceUrl);

    /// <summary>The font files in a folder, in name order.</summary>
    public static IReadOnlyList<string> Files(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder)
                .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".ttf" or ".otf")
                .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
                .ToList()
            : [];

    /// <summary>
    /// Puts each shipped font in the library's fonts folder, as <c>&lt;sha256&gt;.ttf</c>, unless that
    /// file is already there. Returns how many were copied (0 once the library has them all).
    /// A font that can't be copied is skipped: its words fall back to a stand-in font, and the
    /// next launch tries again.
    /// </summary>
    public static int AddTo(string libraryRoot, string shippedFolder)
    {
        var target = Path.Combine(libraryRoot, FontFiles.FolderName);
        var copied = 0;
        foreach (var file in Files(shippedFolder))
        {
            try
            {
                var bytes = File.ReadAllBytes(file);
                var name = Convert.ToHexStringLower(SHA256.HashData(bytes)) + Path.GetExtension(file).ToLowerInvariant();
                var path = Path.Combine(target, name);
                if (File.Exists(path)) continue;
                Directory.CreateDirectory(target);
                // Written beside its final name, then renamed, so a half-copied font is never loaded.
                File.WriteAllBytes(path + ".tmp", bytes);
                File.Move(path + ".tmp", path, overwrite: true);
                copied++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return copied;
    }

    /// <summary>
    /// Each family's credit, read from the fonts' own <c>name</c> tables (so it can't drift from
    /// the files): the copyright notice (name 0), the version (5), the licence (13) and its address (14).
    /// </summary>
    public static IReadOnlyList<Credit> Credits(string shippedFolder)
    {
        var faces = new List<(string Family, string Style, IReadOnlyDictionary<int, string> Names)>();
        foreach (var file in Files(shippedFolder))
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            if (FontFiles.Faces(bytes) is not [var face, ..]) continue;
            faces.Add((face.Family, face.Subfamily, FontFiles.NameStrings(bytes)));
        }
        return faces
            .GroupBy(f => f.Family, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var first = g.First().Names;
                return new Credit(
                    g.Key,
                    [.. g.Select(f => f.Style).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(StyleRank)],
                    first.GetValueOrDefault(0) ?? "",
                    first.GetValueOrDefault(5) ?? "",
                    first.GetValueOrDefault(13) ?? "",
                    first.GetValueOrDefault(14));
            })
            .ToList();
    }

    /// <summary>
    /// The notice that ships beside the fonts: each family with its copyright line, then the
    /// licence's full text. The OFL asks for exactly this: "each copy contains the above copyright
    /// notice and this license".
    /// </summary>
    public static string Notice(IReadOnlyList<Credit> credits, string licenceText)
    {
        var text = new StringBuilder();
        text.Append("Fonts bundled with MxU Slides for Windows\n");
        text.Append("=========================================\n\n");
        text.Append("These fonts are used by the app's starter looks, lower thirds and countdowns. Each is\n");
        text.Append("licensed under the SIL Open Font License, Version 1.1, whose full text follows.\n");
        text.Append("They are copied into the library's fonts folder so that exported presentations carry them.\n\n");
        foreach (var credit in credits)
        {
            text.Append(credit.Family).Append(" (").Append(string.Join(", ", credit.Styles)).Append(")\n");
            text.Append("  ").Append(credit.Copyright).Append('\n');
            if (credit.Version.Length > 0)
                text.Append("  ").Append(credit.Version.StartsWith("Version", StringComparison.OrdinalIgnoreCase) ? "" : "Version ").Append(credit.Version).Append('\n');
            if (credit.LicenceUrl is { Length: > 0 } url) text.Append("  Licence: ").Append(url).Append('\n');
            text.Append('\n');
        }
        text.Append("\n").Append(licenceText.Replace("\r\n", "\n").TrimEnd()).Append('\n');
        return text.ToString();
    }

    /// <summary>Styles listed lightest first, the way font menus order them.</summary>
    private static int StyleRank(string style) => style.ToLowerInvariant() switch
    {
        "regular" => 0,
        "italic" => 1,
        "medium" => 2,
        "semibold" => 3,
        "bold" => 4,
        "extrabold" => 5,
        "black" => 6,
        _ => 7,
    };
}
