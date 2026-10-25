using System.Globalization;

namespace MxU.Slides.Core.Scripture;

/// <summary>
/// Saying, gracefully, why a translation hasn't got a verse. Some verses are left out on purpose:
/// they're later additions that the oldest manuscripts don't have, so the WEB and ASV (made from
/// those manuscripts) skip the number, while the KJV (made from later ones) has the words. That's
/// a fact about the translation, not an error, and the operator should hear it that way.
/// Windows only.
/// </summary>
public static class AbsentVerses
{
    // The sixteen verses modern critical texts leave out (and the KJV keeps). The ASV leaves out all
    // of them; the WEB keeps most, but not Acts 8:37, 15:34, 24:7 or Luke 17:36.
    private static readonly HashSet<string> LaterAdditions =
    [
        "MAT 17:21", "MAT 18:11", "MAT 23:14", "MAR 7:16", "MAR 9:44", "MAR 9:46", "MAR 11:26", "MAR 15:28",
        "LUK 17:36", "LUK 23:17", "JOH 5:4", "ACT 8:37", "ACT 15:34", "ACT 24:7", "ACT 28:29", "ROM 16:24",
    ];

    // Romans' closing doxology ("Now to him that is of power to stablish you…") sits at 16:25–27 in
    // the KJV and ASV, and at 14:24–26 in the WEB, following other manuscripts.
    private static readonly (string Here, string There)[] Moved =
    [
        ("ROM 16:25", "ROM 14:24"), ("ROM 16:26", "ROM 14:25"), ("ROM 16:27", "ROM 14:26"),
    ];

    /// <summary>True for a verse the oldest manuscripts don't have.</summary>
    public static bool IsLaterAddition(VerseRef verse) => LaterAdditions.Contains(verse.ToString());

    /// <summary>Where a translation that numbers it differently has the same words, or null.</summary>
    public static VerseRef? MovedTo(VerseRef verse)
    {
        foreach (var (here, there) in Moved)
        {
            if (verse.ToString() == here) return Parse(there);
            if (verse.ToString() == there) return Parse(here);
        }
        return null;
    }

    /// <summary>
    /// One calm sentence or two about verses <paramref name="bible"/> hasn't got: "Acts 8:37 isn't in
    /// the WEB: the oldest manuscripts don't have it. The KJV has it." <paramref name="others"/> are
    /// the other translations to suggest (those already loaded); null names none.
    /// </summary>
    public static string Explain(IReadOnlyList<VerseRef> absent, Bible bible, IEnumerable<Bible>? others)
    {
        if (absent.Count == 0) return "";
        var abbreviation = bible.Translation.Abbreviation;
        var names = Names(absent);
        var one = absent.Count == 1;
        var sentence = $"{names} {(one ? "isn't" : "aren't")} in the {abbreviation}";
        if (absent.All(IsLaterAddition))
            sentence += $": the oldest manuscripts don't have {(one ? "it" : "them")}.";
        else if (absent.Select(MovedTo).ToList() is var moved && moved.All(m => m is not null && bible.Contains(m.Value)))
            sentence += $", which has {(one ? "these words" : "them")} at {Names(moved.Select(m => m!.Value).ToList())}.";
        else
            sentence += ".";
        var having = (others ?? []).Where(o => o.Translation.Id != bible.Translation.Id && absent.All(o.Contains))
            .Select(o => o.Translation.Abbreviation).ToList();
        if (having.Count > 0) sentence += $" The {string.Join(" and ", having)} {(having.Count == 1 ? "has" : "have")} {(one ? "it" : "them")}.";
        return sentence;
    }

    // "Acts 8:37", "Mark 9:44 and 46", "Mark 9:44, 46 and 11:26".
    private static string Names(IReadOnlyList<VerseRef> verses)
    {
        var parts = new List<string>();
        VerseRef? previous = null;
        foreach (var verse in verses)
        {
            parts.Add(previous is { } p && p.Book == verse.Book
                ? p.Chapter == verse.Chapter ? $"{verse.Verse}" : $"{verse.Chapter}:{verse.Verse}"
                : verse.Display);
            previous = verse;
        }
        return parts.Count == 1 ? parts[0] : $"{string.Join(", ", parts.SkipLast(1))} and {parts[^1]}";
    }

    private static VerseRef Parse(string code)
    {
        var space = code.IndexOf(' ');
        var colon = code.IndexOf(':');
        return new VerseRef(BibleBooks.WithCode(code[..space])!.Index,
            int.Parse(code[(space + 1)..colon], CultureInfo.InvariantCulture), int.Parse(code[(colon + 1)..], CultureInfo.InvariantCulture));
    }
}
