using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace MxU.Slides.OpenLpImport;

/// <summary>
/// One verse as OpenLP stores it. <see cref="Type"/> is OpenLP's one-letter kind (v verse, c chorus,
/// b bridge, p pre-chorus, i intro, e ending, o other), <see cref="Label"/> is usually a number
/// ("1"), and <see cref="Slides"/> is the verse's words already cut where OpenLP's split marks
/// were, with the formatting tags taken out. Chords are still in the words as <c>[C]</c>: the
/// app's own lyric importer lifts them out later.
/// </summary>
public sealed record OpenLpVerse(char Type, string Label, IReadOnlyList<string> Slides);

/// <summary>What <see cref="OpenLpLyrics.Parse"/> made of one song's (or custom slide's) text.</summary>
public sealed record OpenLpParsedLyrics(IReadOnlyList<OpenLpVerse> Verses, IReadOnlyList<string> Warnings, bool Unreadable);

/// <summary>
/// Reads the text OpenLP keeps in <c>songs.lyrics</c> and <c>custom_slide.text</c>. It is a small
/// XML document:
/// <code>&lt;song version="1.0"&gt;&lt;lyrics&gt;&lt;verse type="v" label="1"&gt;&lt;![CDATA[line\nline]]&gt;&lt;/verse&gt;…</code>
/// Very old rows hold plain words instead. Nothing here touches a file or the library, so every
/// rule can be checked by a unit test with a string.
/// </summary>
public static class OpenLpLyrics
{
    /// <summary>OpenLP's "start a new slide here if the words don't fit" mark.</summary>
    public const string OptionalSplit = "[---]";

    /// <summary>OpenLP 3's "always start a new slide here" mark.</summary>
    public const string ForcedSplit = "[--}{--]";

    /// <summary>
    /// The formatting tags every OpenLP has built in (colours, bold, italics…), without their braces.
    /// From OpenLP's formattingtags.py. A church can add its own; those are spotted another way
    /// (see <see cref="PairedTags"/>).
    /// </summary>
    private static readonly HashSet<string> BuiltInTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "r", "b", "bl", "y", "g", "pk", "o", "pp", "w", "su", "sb", "p", "st", "it", "u", "uc", "preview", "br",
    };

    /// <summary>A tag: an opening brace, an optional slash, a short name with no spaces, a closing brace.</summary>
    private static readonly Regex TagPattern = new(@"\{(/?)([^{}\s/]{1,30})\}", RegexOptions.CultureInvariant);

    /// <summary>A break kept apart from the words while they are cleaned (a character no lyric contains).</summary>
    private const string Break = "\u001F";

    public static OpenLpParsedLyrics Parse(string? raw)
    {
        var text = (raw ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        var warnings = new List<string>();
        if (text.Length == 0) return new OpenLpParsedLyrics([], warnings, Unreadable: false);

        // Plain words (very old OpenLP): each block between blank lines is a verse.
        if (!text.StartsWith('<'))
        {
            var paired = PairedTags(text);
            var plain = new List<OpenLpVerse>();
            foreach (var block in Regex.Split(text, @"\n[ \t]*\n"))
            {
                var slides = CleanVerse(block, paired, warnings);
                if (slides.Count > 0) plain.Add(new OpenLpVerse('v', (plain.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), slides));
            }
            return new OpenLpParsedLyrics(plain, warnings, Unreadable: false);
        }

        XDocument document;
        try
        {
            // DTDs are refused: a database from somewhere else must not make the reader fetch anything.
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException error)
        {
            warnings.Add($"the words could not be read (damaged XML: {error.Message})");
            return new OpenLpParsedLyrics([], warnings, Unreadable: true);
        }

        var elements = document.Descendants().Where(element => element.Name.LocalName == "verse").ToList();
        var pairedTags = PairedTags(string.Join("\n", elements.Select(element => element.Value)));
        var verses = new List<OpenLpVerse>();
        foreach (var element in elements)
        {
            var type = VerseType(Attribute(element, "type"));
            var label = (Attribute(element, "label") ?? "").Trim();
            if (label.Length == 0) label = "1";
            verses.Add(new OpenLpVerse(type, label, CleanVerse(element.Value, pairedTags, warnings)));
        }
        return new OpenLpParsedLyrics(verses, warnings, Unreadable: false);
    }

    private static string? Attribute(XElement element, string name) =>
        element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == name)?.Value;

    /// <summary>
    /// OpenLP's own rule (VerseType.from_tag): only the first letter counts, so "v", "V" and "Verse"
    /// are all a verse, and anything it doesn't know is "other".
    /// </summary>
    internal static char VerseType(string? raw)
    {
        var first = char.ToLowerInvariant((raw ?? "").Trim().FirstOrDefault());
        return "vcbpieo".Contains(first) ? first : 'o';
    }

    /// <summary>
    /// The names of tags that appear both opened and closed in the text, such as <c>{gold}…{/gold}</c>.
    /// That pairing is how a church's own formatting tag is told apart from words that merely sit
    /// in braces: we can't see OpenLP's settings, where the church's tags are listed.
    /// </summary>
    internal static HashSet<string> PairedTags(string text)
    {
        var opened = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var closed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in TagPattern.Matches(text))
            (match.Groups[1].Length > 0 ? closed : opened).Add(match.Groups[2].Value);
        opened.IntersectWith(closed);
        return opened;
    }

    /// <summary>
    /// One verse's words → its slides. Split marks become slide breaks, <c>{br}</c> becomes a new
    /// line, formatting tags are dropped (their words stay), and a brace word that is not a tag is
    /// kept, with a warning so someone looks at it.
    /// </summary>
    internal static List<string> CleanVerse(string verse, HashSet<string> pairedTags, List<string> warnings)
    {
        var text = verse.Replace("\r\n", "\n").Replace('\r', '\n')
            .Replace(ForcedSplit, "\n" + Break + "\n")
            .Replace(OptionalSplit, "\n" + Break + "\n");

        text = TagPattern.Replace(text, match =>
        {
            var name = match.Groups[2].Value;
            if (name.Equals("br", StringComparison.OrdinalIgnoreCase)) return "\n";
            if (BuiltInTags.Contains(name) || pairedTags.Contains(name)) return "";
            var warning = $"\"{match.Value}\" is not a formatting tag this importer knows, so it was kept as words";
            if (!warnings.Contains(warning)) warnings.Add(warning);
            return match.Value;
        });

        var slides = new List<string>();
        var lines = new List<string>();
        void Flush()
        {
            // Blank lines at the top and bottom of a slide are dropped; ones in the middle stay.
            while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
            while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            if (lines.Count > 0) slides.Add(string.Join("\n", lines));
            lines = [];
        }
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line == Break) Flush();
            else lines.Add(line);
        }
        Flush();
        return slides;
    }
}
