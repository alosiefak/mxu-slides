using System.Text.RegularExpressions;
using MxU.Slides.Core.Lyrics;

namespace MxU.Slides.Core.Scripture;

/// <summary>
/// Spots Bible references inside a line of text ("see Matthew 5:3-12 for the list"), tells a
/// line that is only references from one that just mentions one, and splits pasted scripture
/// into the verse and its reference ("For God so loved the world (John 3:16)"). It never looks
/// the verse up: <see cref="PassageReference"/> reads a reference into book, chapter and verses.
/// Line-for-line port of the Mac's PresenterCore/Library/ScriptureReference.swift.
/// </summary>
public static class ScriptureReference
{
    /// <param name="Start">Where the reference starts in the line (a UTF-16 index, like .NET's own).</param>
    /// <param name="HasVerse">True for "John 3:16", false for a whole chapter ("Psalm 23").</param>
    public sealed record Match(int Start, int Length, string Text, bool HasVerse)
    {
        public int End => Start + Length;
    }

    public sealed record Split(string Verse, string Reference);

    internal static readonly string[] Books =
    [
        "Genesis|Gen", "Exodus|Exod|Ex", "Leviticus|Lev", "Numbers|Num", "Deuteronomy|Deut|Dt",
        "Joshua|Josh", "Judges|Judg", "Ruth", "Samuel|Sam", "Kings|Kgs", "Chronicles|Chron|Chr",
        "Ezra", "Nehemiah|Neh", "Esther|Esth", "Job", "Psalms|Psalm|Ps|Pss", "Proverbs|Prov",
        "Ecclesiastes|Eccl|Eccles", "Song of Songs|Song of Solomon|Song", "Isaiah|Isa", "Jeremiah|Jer",
        "Lamentations|Lam", "Ezekiel|Ezek", "Daniel|Dan", "Hosea|Hos", "Joel", "Amos", "Obadiah|Obad",
        "Jonah|Jon", "Micah|Mic", "Nahum|Nah", "Habakkuk|Hab", "Zephaniah|Zeph", "Haggai|Hag",
        "Zechariah|Zech", "Malachi|Mal",
        "Matthew|Matt|Mt", "Mark|Mk", "Luke|Lk", "John|Jn", "Acts", "Romans|Rom", "Corinthians|Cor",
        "Galatians|Gal", "Ephesians|Eph", "Philippians|Phil", "Colossians|Col", "Thessalonians|Thess",
        "Timothy|Tim", "Titus", "Philemon|Phlm", "Hebrews|Heb", "James|Jas", "Peter|Pet", "Jude",
        "Revelation|Rev",
    ];

    internal const string Translations = "NIV|ESV|KJV|NKJV|NLT|NASB|CSB|HCSB|MSG|AMP|NRSV|NET|TPT|CEV|NCV|WEB|ASV|RSV|BSB|LSB|NIRV|GNT|NABRE";

    // Longest names first, so "Song of Songs" wins over "Song" and "Philemon" over "Phil".
    private static readonly Regex Pattern = BuildPattern();

    private static Regex BuildPattern()
    {
        var names = string.Join("|", Books.SelectMany(b => b.Split('|'))
            .OrderByDescending(n => n.Length)
            .Select(Regex.Escape));
        const string ordinal = @"(?:(?:[123]|I{1,3}|First|Second|Third)\s+)?";
        const string verse = @"(?:\s*(?::|\.|\s+v\.?|\s+vs\.?|\s+verses?)\s*\d+[a-c]?)";
        const string range = @"(?:\s*[-–—]\s*\d+(?::\d+)?[a-c]?)?(?:\s*,\s*\d+(?:\s*[-–—]\s*\d+)?)*";
        var translation = $@"(?:\s*\(?(?:{Translations})\)?)?";
        return new Regex($@"\b{ordinal}(?:{names})\.?\s+(\d+)({verse})?{range}{translation}",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static List<Match> Matches(string line) =>
        Pattern.Matches(line)
            .Select(m => new Match(m.Index, m.Length, m.Value, HasVerse: m.Groups[2].Success))
            .ToList();

    /// <summary>True when the line holds references and nothing else but brackets and separators.</summary>
    public static bool IsReferenceLine(string line)
    {
        var trimmed = line.TrimmingWhitespacesAndNewlines();
        if (trimmed.Length == 0) return false;
        var rest = trimmed;
        var found = Matches(trimmed);
        if (found.Count == 0) return false;
        for (var i = found.Count - 1; i >= 0; i--) rest = rest.Remove(found[i].Start, found[i].Length);
        var leftovers = rest.Where(c => !char.IsWhiteSpace(c) && !";,()[]".Contains(c));
        return !leftovers.Any();
    }

    /// <summary>
    /// Pasted scripture as its verse and its reference: the reference on its own line (first or
    /// last), or at the end of a line in brackets or after a dash, or leading with a colon
    /// ("John 3:16: For God…"). Null when there is no reference to take off.
    /// </summary>
    public static Split? SplitText(string text)
    {
        var lines = text.ComponentsSeparatedByNewlines().Select(l => l.TrimmingWhitespaces()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0) return null;
        var first = lines[0];
        var last = lines[^1];
        if (lines.Count == 1)
        {
            if (IsReferenceLine(first)) return new Split("", first);
            return SplitInline(first);
        }
        if (IsReferenceLine(last)) return new Split(string.Join("\n", lines.SkipLast(1)), last);
        if (IsReferenceLine(first)) return new Split(string.Join("\n", lines.Skip(1)), first);
        if (SplitInline(last) is { } inline)
            return new Split(string.Join("\n", lines.SkipLast(1).Append(inline.Verse).Where(l => l.Length > 0)), inline.Reference);
        return null;
    }

    private static Split? SplitInline(string line)
    {
        var found = Matches(line);
        if (found.Count == 0) return null;
        var match = found[^1];
        var before = line[..match.Start].TrimmingWhitespaces();
        var after = line[match.End..].TrimmingWhitespaces();
        var trailing = after.Length == 0 || after == ")";
        var opensParen = before.EndsWith('(');
        var afterDash = before.Length > 0 && "-–—".Contains(before[^1]);
        if (trailing && (opensParen || afterDash))
            return new Split(before[..^1].TrimmingWhitespaces(), match.Text);
        if (trailing && match.HasVerse && before.Length > 0)
            return new Split(before, match.Text);
        if (found[0] is { Start: 0 } lead)
        {
            var rest = line[lead.End..].TrimmingWhitespaces();
            var separator = rest.Length > 0 && ":-–—".Contains(rest[0]);
            if (separator)
            {
                var verse = rest[1..].TrimmingWhitespaces();
                if (verse.Length > 0) return new Split(verse, lead.Text);
            }
        }
        return null;
    }

    /// <summary>
    /// The chapter and verses a reference names: "Hebrews 12:27–28 (WEB)" is Hebrews 12, 27 to 28.
    /// Null for a whole chapter, or a passage over two chapters.
    /// </summary>
    public static (string Chapter, int First, int Last)? VersesOf(string reference)
    {
        var match = VersePattern.Match(reference);
        if (!match.Success || !int.TryParse(match.Groups[2].Value, out var first)) return null;
        var last = match.Groups[3].Success && int.TryParse(match.Groups[3].Value, out var l) ? l : first;
        if (last < first) return null;
        return (match.Groups[1].Value, first, last);
    }

    private static readonly Regex VersePattern = new(
        @"^\s*([^:]*?\d+)\s*:\s*(\d+)[a-c]?(?:\s*[-–—]\s*(\d+)[a-c]?)?\s*(?:\(?[A-Z]{2,6}\)?)?\s*$", RegexOptions.CultureInvariant);

    /// <summary>One verse of a passage, with the number it carried in the text (null for words before the first number).</summary>
    public sealed record Verse(int? Number, string Text)
    {
        /// <summary>"4 Rejoice": the verse with its number in front.</summary>
        public string Numbered => Number is { } n ? $"{n} {Text}" : Text;
    }

    /// <summary>
    /// The passage cut at the verse numbers its reference names ("27 This phrase… 28 Therefore…"),
    /// counting up from the first, so a number in the words ("after 3 days") is no cut.
    /// </summary>
    public static List<Verse> VersesIn(string text, int first, int last)
    {
        var cuts = VerseNumbers(text, first, last);
        if (cuts.Count == 0) return [new Verse(null, text.TrimmingWhitespacesAndNewlines())];
        var result = new List<Verse>();
        var head = text[..cuts[0].Start].TrimmingWhitespacesAndNewlines();
        if (head.Length > 0) result.Add(new Verse(null, head));
        for (var index = 0; index < cuts.Count; index++)
        {
            var end = index + 1 < cuts.Count ? cuts[index + 1].Start : text.Length;
            var from = cuts[index].Start + cuts[index].Length;
            result.Add(new Verse(cuts[index].Number, text[from..end].TrimmingWhitespacesAndNewlines()));
        }
        return result;
    }

    /// <summary>The passage with its verse numbers taken out.</summary>
    public static string RemovingVerseNumbers(string text, int first, int last)
    {
        foreach (var cut in Enumerable.Reverse(VerseNumbers(text, first, last))) text = text.Remove(cut.Start, cut.Length);
        return text;
    }

    private static List<(int Number, int Start, int Length)> VerseNumbers(string text, int first, int last)
    {
        var cuts = new List<(int, int, int)>();
        var expected = first;
        foreach (System.Text.RegularExpressions.Match match in NumberPattern.Matches(text))
        {
            if (expected > last || !int.TryParse(match.Groups[1].Value, out var number)) continue;
            var leading = text[..match.Index].TrimmingWhitespacesAndNewlines().Length > 0;
            // The first number may be one past the reference's first verse when words come before it.
            if (number == expected || (cuts.Count == 0 && leading && number == expected + 1 && number <= last))
            {
                cuts.Add((number, match.Index, match.Length));
                expected = number + 1;
            }
        }
        return cuts;
    }

    private static readonly Regex NumberPattern = new(@"(?:^|(?<=\s))(\d{1,3})\s+(?=[^\d\s])", RegexOptions.Multiline | RegexOptions.CultureInvariant);
}
