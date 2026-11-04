using System.Globalization;
using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Editing;

/// <summary>What to look for: the words, and how strictly.</summary>
public sealed record FindQuery(string Text, bool MatchCase = false, bool WholeWords = false)
{
    public bool IsEmpty => Text.Length == 0;
}

/// <summary>One place the words were found: which presentation, slide and text box, and where in its text (characters).</summary>
public sealed record FindMatch(string PresentationId, string PresentationName, string SlideId, int SlideNumber, string ObjectId, TextStyleRuns.Range Range, string Context);

/// <summary>
/// Find and replace across a presentation, or across every song in the library: a typo in a
/// hymn's third verse, or "Thee" made "You" through a whole song. It looks in the slides' own
/// text boxes (not in text linked to a timer or a stage field), and a replacement keeps the
/// words' styling, chords and animation on the right words (<see cref="ObjectText"/>).
/// <para>
/// Matching counts characters as the Mac does, ignores case unless asked, and "whole words"
/// means the match isn't part of a longer word. Windows only: the Mac has no find and replace.
/// </para>
/// </summary>
public static class FindReplace
{
    /// <summary>Every match in a presentation, in slide order.</summary>
    public static List<FindMatch> Find(Presentation presentation, FindQuery query)
    {
        var matches = new List<FindMatch>();
        if (query.IsEmpty) return matches;
        for (var s = 0; s < presentation.Slides.Count; s++)
        {
            var slide = presentation.Slides[s];
            foreach (var o in slide.Objects.Where(Searchable))
            {
                foreach (var range in Ranges(o.Text, query))
                    matches.Add(new FindMatch(presentation.Id, presentation.Name, slide.Id, s + 1, o.Id, range, Context(o.Text, range)));
            }
        }
        return matches;
    }

    /// <summary>Every match in many presentations (the library's songs), presentation by presentation.</summary>
    public static List<FindMatch> Find(IEnumerable<Presentation> presentations, FindQuery query) =>
        [.. presentations.SelectMany(p => Find(p, query))];

    /// <summary>
    /// Replaces every match in a presentation. Returns how many were replaced. Matches are found
    /// afresh and replaced from the end of each text box backwards, so earlier positions hold.
    /// </summary>
    public static int ReplaceAll(Presentation presentation, FindQuery query, string replacement)
    {
        if (query.IsEmpty) return 0;
        var count = 0;
        foreach (var o in presentation.Slides.SelectMany(s => s.Objects).Where(Searchable))
        {
            foreach (var range in Ranges(o.Text, query).AsEnumerable().Reverse())
            {
                ObjectText.ReplaceText(o, range, Cased(replacement, o.Text, range, query));
                count++;
            }
        }
        return count;
    }

    /// <summary>Replaces one match, if it's still where it was found (the text may have changed since).</summary>
    public static bool Replace(Presentation presentation, FindMatch match, FindQuery query, string replacement)
    {
        var o = presentation.Slides.FirstOrDefault(s => s.Id == match.SlideId)?.Objects.FirstOrDefault(x => x.Id == match.ObjectId);
        if (o is null || !Ranges(o.Text, query).Contains(match.Range)) return false;
        ObjectText.ReplaceText(o, match.Range, Cased(replacement, o.Text, match.Range, query));
        return true;
    }

    /// <summary>A text box worth searching: words typed on the slide, not a timer or a linked field.</summary>
    private static bool Searchable(SlideObject o) => o.ObjectKind == SlideObjectKind.Text && o.TextLink is null && o.Text.Length > 0;

    /// <summary>The ranges (in characters) where the query matches the text, left to right, not overlapping.</summary>
    public static List<TextStyleRuns.Range> Ranges(string text, FindQuery query)
    {
        var ranges = new List<TextStyleRuns.Range>();
        if (query.IsEmpty) return ranges;
        var characters = text.Characters();
        var wanted = query.Text.Characters();
        var compare = query.MatchCase ? StringComparison.Ordinal : StringComparison.CurrentCultureIgnoreCase;
        for (var i = 0; i + wanted.Count <= characters.Count;)
        {
            var match = true;
            for (var j = 0; j < wanted.Count && match; j++) match = string.Equals(characters[i + j], wanted[j], compare);
            if (match && query.WholeWords && (IsWordCharacter(characters, i - 1) || IsWordCharacter(characters, i + wanted.Count))) match = false;
            if (match)
            {
                ranges.Add(new TextStyleRuns.Range(i, i + wanted.Count));
                i += Math.Max(1, wanted.Count);
            }
            else
            {
                i++;
            }
        }
        return ranges;
    }

    private static bool IsWordCharacter(IReadOnlyList<string> characters, int index) =>
        index >= 0 && index < characters.Count && characters[index].Length > 0
        && (char.IsLetterOrDigit(characters[index], 0) || characters[index][0] is '\'' or '’');

    /// <summary>
    /// Without Match Case, a replacement follows the match's capital: "Thee" at the start of a line
    /// becomes "You", and "thee" mid-line becomes "you".
    /// </summary>
    private static string Cased(string replacement, string text, TextStyleRuns.Range range, FindQuery query)
    {
        if (query.MatchCase || replacement.Length == 0) return replacement;
        var found = string.Concat(text.Characters().Skip(range.Start).Take(range.Length));
        if (found.Length == 0 || !char.IsLetter(found[0])) return replacement;
        var culture = CultureInfo.CurrentCulture;
        if (found.Length > 1 && found.All(c => !char.IsLetter(c) || char.IsUpper(c))) return replacement.ToUpper(culture);
        return char.IsUpper(found[0])
            ? char.ToUpper(replacement[0], culture) + replacement[1..]
            : char.ToLower(replacement[0], culture) + replacement[1..];
    }

    /// <summary>The words round a match, for the results list: "…how sweet [the] sound…".</summary>
    private static string Context(string text, TextStyleRuns.Range range)
    {
        var characters = text.Characters();
        var start = Math.Max(0, range.Start - 24);
        var end = Math.Min(characters.Count, range.End + 24);
        string Piece(int from, int to) => string.Concat(characters.Skip(from).Take(to - from)).Replace('\n', ' ');
        return (start > 0 ? "…" : "") + Piece(start, range.Start) + "[" + Piece(range.Start, range.End) + "]" + Piece(range.End, end) + (end < characters.Count ? "…" : "");
    }
}
