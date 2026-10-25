namespace MxU.Slides.Core.Scripture;

/// <summary>
/// One book of the Bible: its place (0 = Genesis … 65 = Revelation), its eBible.org code (the
/// first column of the data files: "GEN", "JOH", "1CO"), its name, how many chapters it has, and
/// the short forms people type ("Jn", "1 Cor", "Ps").
/// </summary>
public sealed record BibleBook(int Index, string Code, string Name, int Chapters, IReadOnlyList<string> Aliases)
{
    public bool IsNewTestament => Index >= 39;

    /// <summary>Obadiah, Philemon, 2 and 3 John and Jude: "Jude 3" means verse 3.</summary>
    public bool HasOneChapter => Chapters == 1;

    /// <summary>"Psalm 23" but "Psalms 23–24": the one book whose name changes with the count.</summary>
    public string DisplayName(bool severalChapters) => Code == "PSA" && !severalChapters ? "Psalm" : Name;

    public override string ToString() => Name;
}

/// <summary>
/// The 66 books, in order, and finding one from what someone typed. Windows only: the Mac's
/// <c>ScriptureReference</c> only spots references in text, it doesn't read them (see
/// <see cref="ScriptureReference"/>).
/// <para>
/// Typed names are compared as <em>keys</em>: lower case with spaces, dots and apostrophes
/// removed, and a leading "First", "1st", "I" or "1" all written "1". So "1 Cor", "I Cor.",
/// "1st Corinthians" and "1corinthians" are all the key "1cor…".
/// </para>
/// </summary>
public static class BibleBooks
{
    // Code, name, chapters, then the short forms people type (as keys). The chapter counts are the
    // same in the KJV, WEB and ASV (a test checks them against the data).
    private static readonly (string Code, string Name, int Chapters, string Aliases)[] Table =
    [
        ("GEN", "Genesis", 50, "gen ge gn"),
        ("EXO", "Exodus", 40, "exod exo ex"),
        ("LEV", "Leviticus", 27, "lev le lv"),
        ("NUM", "Numbers", 36, "num nu nm nb"),
        ("DEU", "Deuteronomy", 34, "deut deu dt de"),
        ("JOS", "Joshua", 24, "josh jos jsh"),
        ("JDG", "Judges", 21, "judg jdg jg jdgs"),
        ("RUT", "Ruth", 4, "rut ru rth"),
        ("1SA", "1 Samuel", 31, "1sam 1sa 1sm 1s"),
        ("2SA", "2 Samuel", 24, "2sam 2sa 2sm 2s"),
        ("1KI", "1 Kings", 22, "1kgs 1ki 1kin 1k"),
        ("2KI", "2 Kings", 25, "2kgs 2ki 2kin 2k"),
        ("1CH", "1 Chronicles", 29, "1chron 1chr 1ch"),
        ("2CH", "2 Chronicles", 36, "2chron 2chr 2ch"),
        ("EZR", "Ezra", 10, "ezr"),
        ("NEH", "Nehemiah", 13, "neh ne"),
        ("EST", "Esther", 10, "esth est es"),
        ("JOB", "Job", 42, "jb"),
        ("PSA", "Psalms", 150, "psalm ps pss psa psm pslm"),
        ("PRO", "Proverbs", 31, "prov pro prv pr"),
        ("ECC", "Ecclesiastes", 12, "eccl eccles ecc ec qoh"),
        ("SOL", "Song of Solomon", 8, "songofsongs song sos sng songofsol canticles"),
        ("ISA", "Isaiah", 66, "isa is"),
        ("JER", "Jeremiah", 52, "jer je jr"),
        ("LAM", "Lamentations", 5, "lam la"),
        ("EZE", "Ezekiel", 48, "ezek eze ezk"),
        ("DAN", "Daniel", 12, "dan da dn"),
        ("HOS", "Hosea", 14, "hos ho"),
        ("JOE", "Joel", 3, "jl"),
        ("AMO", "Amos", 9, "amo am"),
        ("OBA", "Obadiah", 1, "obad oba ob"),
        ("JON", "Jonah", 4, "jon jnh"),
        ("MIC", "Micah", 7, "mic mc"),
        ("NAH", "Nahum", 3, "nah na"),
        ("HAB", "Habakkuk", 3, "hab hb"),
        ("ZEP", "Zephaniah", 3, "zeph zep zp"),
        ("HAG", "Haggai", 2, "hag hg"),
        ("ZEC", "Zechariah", 14, "zech zec zc"),
        ("MAL", "Malachi", 4, "mal ml"),
        ("MAT", "Matthew", 28, "matt mat mt"),
        ("MAR", "Mark", 16, "mrk mar mk mr"),
        ("LUK", "Luke", 24, "luk lk"),
        ("JOH", "John", 21, "jn jhn joh"),
        ("ACT", "Acts", 28, "act ac"),
        ("ROM", "Romans", 16, "rom ro rm"),
        ("1CO", "1 Corinthians", 16, "1cor 1co"),
        ("2CO", "2 Corinthians", 13, "2cor 2co"),
        ("GAL", "Galatians", 6, "gal ga"),
        ("EPH", "Ephesians", 6, "eph ephes"),
        ("PHI", "Philippians", 4, "phil php pp"),
        ("COL", "Colossians", 4, "col"),
        ("1TH", "1 Thessalonians", 5, "1thess 1thes 1th"),
        ("2TH", "2 Thessalonians", 3, "2thess 2thes 2th"),
        ("1TI", "1 Timothy", 6, "1tim 1ti 1tm"),
        ("2TI", "2 Timothy", 4, "2tim 2ti 2tm"),
        ("TIT", "Titus", 3, "tit ti"),
        ("PHM", "Philemon", 1, "phlm phm philem phile"),
        ("HEB", "Hebrews", 13, "heb"),
        ("JAM", "James", 5, "jas jam jm"),
        ("1PE", "1 Peter", 5, "1pet 1pe 1pt 1p"),
        ("2PE", "2 Peter", 3, "2pet 2pe 2pt 2p"),
        ("1JO", "1 John", 5, "1jn 1jhn 1joh 1jo 1j"),
        ("2JO", "2 John", 1, "2jn 2jhn 2joh 2jo 2j"),
        ("3JO", "3 John", 1, "3jn 3jhn 3joh 3jo 3j"),
        ("JUD", "Jude", 1, "jud jd"),
        ("REV", "Revelation", 22, "rev re rv revelations therevelation"),
    ];

    public static readonly IReadOnlyList<BibleBook> All = Table
        .Select((row, index) => new BibleBook(index, row.Code, row.Name, row.Chapters,
            [Key(row.Name), .. row.Aliases.Split(' ', StringSplitOptions.RemoveEmptyEntries)]))
        .ToArray();

    private static readonly Dictionary<string, BibleBook> ByCode = All.ToDictionary(b => b.Code, StringComparer.OrdinalIgnoreCase);

    // Every key (names and short forms) to its book. A key belongs to one book only.
    private static readonly Dictionary<string, BibleBook> ByKey = All
        .SelectMany(b => b.Aliases.Select(a => (Key: a, Book: b)))
        .ToDictionary(p => p.Key, p => p.Book, StringComparer.Ordinal);

    /// <summary>The book with this data code ("JOH"), or null.</summary>
    public static BibleBook? WithCode(string code) => ByCode.GetValueOrDefault(code);

    /// <summary>
    /// The book someone means by <paramref name="typed"/> ("John", "jn", "1 Cor", "I Corinthians",
    /// "Song of Songs"), or null. A name cut short counts when only one book starts that way
    /// ("Philipp", "Gene", "1 Thes"), so a fast typist needn't finish it.
    /// </summary>
    public static BibleBook? Find(string typed)
    {
        if (FindExact(typed) is { } exact) return exact;
        foreach (var key in Keys(typed))
        {
            if (key.Length < 2) continue;
            var starting = All.Where(b => b.Aliases.Any(a => a.StartsWith(key, StringComparison.Ordinal))).ToList();
            if (starting.Count == 1) return starting[0];
        }
        return null;
    }

    /// <summary>The book whose name or a short form is exactly <paramref name="typed"/> (any case, spacing or numbering), or null.</summary>
    public static BibleBook? FindExact(string typed)
    {
        foreach (var key in Keys(typed))
        {
            if (ByKey.TryGetValue(key, out var book)) return book;
        }
        return null;
    }

    /// <summary>
    /// Books to offer while a name is being typed: those whose name or a short form starts with
    /// it, best first (an exact short form, then names in Bible order, then the numbered books
    /// whose name without its number starts with it: "jo" also offers 1, 2 and 3 John).
    /// </summary>
    public static IReadOnlyList<BibleBook> Complete(string typed, int max = 8)
    {
        var keys = Keys(typed).ToList();
        if (keys.Count == 0 || keys[0].Length == 0) return [];
        var exact = keys.Select(k => ByKey.GetValueOrDefault(k)).OfType<BibleBook>();
        var starting = All.Where(b => keys.Any(k => b.Aliases.Any(a => a.StartsWith(k, StringComparison.Ordinal))));
        var numbered = All.Where(b => char.IsDigit(b.Code[0]) && keys.Any(k => !char.IsDigit(k[0]) && Key(b.Name[2..]).StartsWith(k, StringComparison.Ordinal)));
        return exact.Concat(starting).Concat(numbered).Distinct().Take(max).ToList();
    }

    /// <summary>Lower case, without spaces, dots or apostrophes ("Song of Songs" → "songofsongs").</summary>
    public static string Key(string text) =>
        string.Concat(text.Where(c => !char.IsWhiteSpace(c) && c is not ('.' or '\'' or '’'))).ToLowerInvariant();

    // The ways a typed name can be read: as it is, then with a leading ordinal written as a digit.
    // "Isaiah" must stay Isaiah, so the Roman "I" is only one of the readings, never the only one.
    private static IEnumerable<string> Keys(string typed)
    {
        var key = Key(typed);
        yield return key;
        foreach (var (word, digit) in Ordinals)
        {
            if (key.Length > word.Length && key.StartsWith(word, StringComparison.Ordinal) && !char.IsDigit(key[word.Length]))
                yield return digit + key[word.Length..];
        }
    }

    private static readonly (string Word, string Digit)[] Ordinals =
    [
        ("first", "1"), ("second", "2"), ("third", "3"), ("1st", "1"), ("2nd", "2"), ("3rd", "3"),
        ("iii", "3"), ("ii", "2"), ("i", "1"),
    ];
}
