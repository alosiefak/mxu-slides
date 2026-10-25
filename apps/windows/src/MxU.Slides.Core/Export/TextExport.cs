using MxU.Slides.Core.Library;
using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Export;

/// <summary>
/// A song as plain text: its title and credits, then each section's name and words in the order
/// they're sung, then the copyright lines. No chords and no ChordPro tags, so it pastes cleanly
/// into a bulletin or an email. (The Mac's "Plain Text (.txt)" holds the same ChordPro text as its
/// .cho; on Windows ChordPro is the .cho, and .txt is just the words.)
/// </summary>
public static class TextExport
{
    public static string PlainText(Presentation song)
    {
        var lines = new List<string> { song.Name.Length == 0 ? "Untitled" : song.Name };
        if (LyricSheet.Credits(song) is { Length: > 0 } credits) lines.Add(credits);
        foreach (var block in ChordProExport.Blocks(song))
        {
            lines.Add("");
            if (block.Name is { } name) lines.Add(name.ToUpperInvariant());
            lines.Add(string.Join("\n\n", block.Stanzas.Select(stanza =>
                string.Join("\n", stanza.Split('\n').Select(line => ChordMath.ExtractLine(line).Text.TrimEnd())))));
        }
        if (LyricSheet.Footer(song) is { Count: > 0 } footer)
        {
            lines.Add("");
            lines.AddRange(footer);
        }
        // Windows line endings, so Notepad and Outlook show the lines as lines.
        return string.Join("\r\n", string.Join("\n", lines).Split('\n')) + "\r\n";
    }

    /// <summary>The text a format writes: ChordPro's own, or the plain words.</summary>
    public static string For(Presentation song, PresentationExport.Format format) =>
        format == PresentationExport.Format.ChordPro ? ChordProExport.Text(song) : PlainText(song);
}

/// <summary>
/// The fonts a PDF is written in: Segoe UI (Windows' own), else Arial, else Montserrat (which ships
/// with the app), each with its bold. Characters the main font hasn't got are borrowed from the
/// others (Arial has music notes, Segoe UI Symbol arrows and crosses), so Yoruba's ẹ, ọ and ṣ,
/// accents, Greek and Cyrillic all draw; a character none of them has shows as a box.
/// </summary>
public static class PdfFonts
{
    public sealed record Set(TrueTypeFont Regular, TrueTypeFont Bold, IReadOnlyList<TrueTypeFont> Fallbacks);

    private static readonly (string Regular, string Bold)[] Choices =
    [
        ("segoeui.ttf", "segoeuib.ttf"),
        ("arial.ttf", "arialbd.ttf"),
    ];

    private static readonly string[] Borrowed = ["arial.ttf", "seguisym.ttf"];

    /// <summary>The fonts, or null when none can be read and embedded.</summary>
    public static Set? Find(string? windowsFonts = null, string? shippedFonts = null)
    {
        windowsFonts ??= Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        shippedFonts ??= BundledFonts.ShippedFolder;
        var shipped = (Load(Path.Combine(shippedFonts, "Montserrat-Regular.ttf")), Load(Path.Combine(shippedFonts, "Montserrat-SemiBold.ttf")));
        foreach (var (regular, bold) in Choices)
            if (Load(Path.Combine(windowsFonts, regular)) is { } r && Load(Path.Combine(windowsFonts, bold)) is { } b)
            {
                var fallbacks = Borrowed.Where(f => f != regular).Select(f => Load(Path.Combine(windowsFonts, f))).OfType<TrueTypeFont>().ToList();
                if (shipped.Item1 is { } extra) fallbacks.Add(extra);
                return new Set(r, b, fallbacks);
            }
        return shipped is ({ } sr, { } sb) ? new Set(sr, sb, []) : null;
    }

    /// <summary>The regular and bold fonts added to <paramref name="pdf"/>, each borrowing from the fallbacks.</summary>
    public static (PdfFont Regular, PdfFont Bold) AddTo(PdfDocument pdf, Set set) =>
        (pdf.AddFont(set.Regular, [.. set.Fallbacks]), pdf.AddFont(set.Bold, [.. set.Fallbacks]));

    private static TrueTypeFont? Load(string path) => File.Exists(path) && TrueTypeFont.TryLoad(path) is { CanEmbed: true } font ? font : null;
}
